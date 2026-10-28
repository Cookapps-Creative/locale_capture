using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CookApps.LocaleCapture.Editor
{
    // 게임의 로컬 저장을 통째로 백업·복원한다 — persistentDataPath, temporaryCachePath, PlayerPrefs(에디터는 레지스트리).
    // 기록 시작 때 백업해 두고 언어별 재생 직전마다 복원해서, 모든 재생이 기록과 같은 시작 상태에서 출발한다.
    // 복원할 때 저장 값 안의 녹화 언어 표기(ko-KR·Korean 등)를 재생 언어 표기로 바꿔 게임이 그 언어로 켜지게 한다.
    internal static class LocalState
    {
        private const string Persistent = "persistent", Cache = "cache", Prefs = "prefs.reg";
        private const long MaxTextFile = 4 * 1024 * 1024;

        private static string PrefsKey => $@"HKCU\Software\Unity\UnityEditor\{PlayerSettings.companyName}\{PlayerSettings.productName}";

        public static bool Exists(string stateDir) => Directory.Exists(stateDir) && Directory.Exists(Path.Combine(stateDir, Persistent));

        public static void Save(string stateDir)
        {
            if (Directory.Exists(stateDir)) Directory.Delete(stateDir, true);
            Directory.CreateDirectory(stateDir);
            CopyDir(Application.persistentDataPath, Path.Combine(stateDir, Persistent));
            CopyDir(Application.temporaryCachePath, Path.Combine(stateDir, Cache));
            string prefs = Path.Combine(stateDir, Prefs);
            if (Reg($"query \"{PrefsKey}\"", out _) == 0) Reg($"export \"{PrefsKey}\" \"{prefs}\" /y", out _);
        }

        // fromCode → toCode: 저장 값 안의 언어 표기를 바꾼다(null 이면 그대로). customKey: 언어를 담는 PlayerPrefs 키(선택).
        public static void Restore(string stateDir, string fromCode, string toCode, string customKey)
        {
            if (!Exists(stateDir)) throw new DirectoryNotFoundException("저장 백업이 없다: " + stateDir);
            ReplaceDir(Path.Combine(stateDir, Persistent), Application.persistentDataPath);
            ReplaceDir(Path.Combine(stateDir, Cache), Application.temporaryCachePath);

            Reg($"delete \"{PrefsKey}\" /f", out _);
            string prefs = Path.Combine(stateDir, Prefs);
            if (File.Exists(prefs))
            {
                string import = prefs;
                if (Rewriting(fromCode, toCode, customKey))
                {
                    import = Path.Combine(Path.GetTempPath(), "LocaleCapture_prefs.reg");
                    File.WriteAllText(import, RewriteReg(File.ReadAllText(prefs, Encoding.Unicode), fromCode, toCode, customKey), Encoding.Unicode);
                }
                if (Reg($"import \"{import}\"", out string error) != 0) Debug.LogWarning("[LocaleCapture] PlayerPrefs 복원 실패: " + error);
            }
            if (Rewriting(fromCode, toCode, null)) RewriteFiles(Application.persistentDataPath, fromCode, toCode);
        }

        // 녹화 언어를 언어 시스템에서 못 읽었을 때: 백업의 PlayerPrefs·텍스트 파일에 어느 언어 표기가 들어 있는지로 짐작한다.
        public static string GuessLocale(string stateDir, IEnumerable<string> codes)
        {
            var texts = new List<string>();
            string prefs = Path.Combine(stateDir, Prefs);
            if (File.Exists(prefs))
                foreach (string line in File.ReadAllText(prefs, Encoding.Unicode).Replace("\\\r\n", "").Split('\n'))
                {
                    int eq = line.IndexOf("\"=hex:", StringComparison.Ordinal);
                    if (eq < 0) continue;
                    try
                    {
                        byte[] bytes = line.Substring(eq + 6).Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Select(s => Convert.ToByte(s, 16)).ToArray();
                        texts.Add(Encoding.UTF8.GetString(bytes).TrimEnd('\0'));
                    }
                    catch { /* 문자열이 아니다 */ }
                }
            foreach (string code in codes)
            {
                string[] forms = LocaleCatalog.Forms(code);
                if (texts.Any(t => forms.Any(f => !string.IsNullOrEmpty(f) && string.Equals(t, f, StringComparison.OrdinalIgnoreCase)))) return code;
            }
            return null;
        }

        private static bool Rewriting(string from, string to, string customKey) =>
            !string.IsNullOrEmpty(to) && (!string.IsNullOrEmpty(customKey) || (!string.IsNullOrEmpty(from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)));

        // ── 폴더 ───────────────────────────────────────────────────────────────

        private static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from)) return;
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));
            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                try { File.Copy(file, file.Replace(from, to), true); }
                catch (IOException e) { Debug.LogWarning($"[LocaleCapture] 복사 건너뜀({file}): {e.Message}"); }
            }
        }

        // 대상 폴더 내용을 지우고 백업으로 채운다. 엉뚱한 곳을 지우지 않게 회사·제품 이름이 든 경로만 허용한다.
        private static void ReplaceDir(string from, string to)
        {
            string full = Path.GetFullPath(to);
            if (!full.Contains(PlayerSettings.productName) || full.Length < 12) throw new InvalidOperationException("복원 대상 경로가 이상하다: " + full);
            if (Directory.Exists(full))
            {
                foreach (string file in Directory.GetFiles(full)) TryDelete(() => File.Delete(file));
                foreach (string dir in Directory.GetDirectories(full)) TryDelete(() => Directory.Delete(dir, true));
            }
            CopyDir(from, full);
        }

        private static void TryDelete(Action action)
        {
            try { action(); } catch (Exception e) { Debug.LogWarning("[LocaleCapture] 지우기 실패: " + e.Message); }
        }

        // ── 언어 표기 바꾸기 ───────────────────────────────────────────────────

        private static string RewriteReg(string text, string fromCode, string toCode, string customKey)
        {
            string[] from = LocaleCatalog.Forms(fromCode), to = LocaleCatalog.Forms(toCode);
            var lines = new List<string>();
            var logical = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                if (raw.EndsWith("\\")) { logical.Append(raw.Substring(0, raw.Length - 1).Trim()); continue; }
                logical.Append(logical.Length > 0 ? raw.Trim() : raw);
                lines.Add(RewriteRegLine(logical.ToString(), from, to, customKey));
                logical.Clear();
            }
            return string.Join("\r\n", lines);
        }

        private static string RewriteRegLine(string line, string[] from, string[] to, string customKey)
        {
            int eq = line.IndexOf("\"=hex:", StringComparison.Ordinal);
            if (!line.StartsWith("\"") || eq < 0) return line;
            string name = line.Substring(1, eq - 1);
            byte[] bytes;
            try { bytes = line.Substring(eq + 6).Split(',').Where(s => s.Length > 0).Select(s => Convert.ToByte(s.Trim(), 16)).ToArray(); }
            catch { return line; }
            int length = bytes.Length;
            while (length > 0 && bytes[length - 1] == 0) length--;
            string value;
            try { value = new UTF8Encoding(false, true).GetString(bytes, 0, length); }
            catch { return line; }

            string replacement = null;
            for (int i = 0; i < from.Length && i < to.Length; i++)
                if (!string.IsNullOrEmpty(from[i]) && !string.IsNullOrEmpty(to[i]) && string.Equals(value, from[i], StringComparison.OrdinalIgnoreCase)) { replacement = to[i]; break; }
            bool custom = !string.IsNullOrEmpty(customKey) && (name == customKey || name.StartsWith(customKey + "_h", StringComparison.Ordinal));
            if (replacement == null && custom) replacement = to.Length > 0 ? to[0] : null;
            if (replacement == null) return line;
            byte[] encoded = Encoding.UTF8.GetBytes(replacement).Concat(new byte[] { 0 }).ToArray();
            return $"\"{name}\"=hex:" + string.Join(",", encoded.Select(b => b.ToString("x2")));
        }

        // persistentDataPath 의 텍스트 파일(JSON 등)에서 따옴표로 둘러싼 언어 표기를 바꾼다.
        private static void RewriteFiles(string root, string fromCode, string toCode)
        {
            string[] from = LocaleCatalog.Forms(fromCode), to = LocaleCatalog.Forms(toCode);
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0 || info.Length > MaxTextFile) continue;
                    byte[] bytes = File.ReadAllBytes(file);
                    if (Array.IndexOf(bytes, (byte) 0) >= 0) continue;   // 바이너리
                    string text = new UTF8Encoding(false, true).GetString(bytes);
                    string changed = text;
                    for (int i = 0; i < from.Length && i < to.Length; i++)
                        if (!string.IsNullOrEmpty(from[i]) && !string.IsNullOrEmpty(to[i]))
                            changed = changed.Replace("\"" + from[i] + "\"", "\"" + to[i] + "\"");
                    if (!ReferenceEquals(changed, text) && changed != text) File.WriteAllText(file, changed, new UTF8Encoding(false));
                }
                catch { /* 텍스트가 아니거나 잠김 */ }
            }
        }

        // ── reg.exe ────────────────────────────────────────────────────────────

        private static int Reg(string args, out string error)
        {
            var info = new ProcessStartInfo("reg.exe", args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using (Process process = Process.Start(info))
            {
                process.StandardOutput.ReadToEnd();
                error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
    }
}
