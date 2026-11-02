using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화가 실패하거나 녹화와 다르게 재생된 테이크의 보고서를 Library/LocaleCapture/reports/ 에 남긴다.
    // 창의 "GitHub 이슈로 보고" 가 그 보고서를 클립보드에 복사하고 저장소의 새 이슈 페이지를 연다 — 토큰 없이, 보고하는 사람의
    // 계정으로 올라가며, 누르기 전에는 아무것도 밖으로 나가지 않는다. 콘솔은 이 도구 태그가 붙은 줄만 모은다(게임 로그·계정 정보 제외).
    [InitializeOnLoad]
    internal static class IssueReport
    {
        public const string RepoUrl = "https://github.com/Cookapps-Creative/locale_capture";
        private const string Tag = "LocaleCapture";
        private const string LastKey = "LocaleCapture.LastIssueReport";
        private const int MaxLogs = 40, MaxIssuesPerPass = 15, MaxMismatches = 5, MaxUrlBody = 6000;
        private static readonly List<string> _logs = new List<string>();
        private static string ReportsDir => Path.GetFullPath("Library/LocaleCapture/reports");

        public static string LastPath
        {
            get { string p = EditorPrefs.GetString(LastKey, ""); return File.Exists(p) ? p : null; }
            private set => EditorPrefs.SetString(LastKey, value ?? "");
        }

        static IssueReport() => Application.logMessageReceived += OnLog;

        private static void OnLog(string message, string stack, LogType type)
        {
            if (type == LogType.Log) return;
            bool ours = message.Contains(Tag) || (stack != null && stack.Contains(Tag));
            if (!ours || (type == LogType.Warning && !message.Contains("[" + Tag + "]"))) return;
            string first = stack?.Split('\n').FirstOrDefault(l => l.Contains(Tag))?.Trim();
            _logs.Add($"[{DateTime.Now:HH:mm:ss}] {type}: {message.Split('\n')[0]}" + (first != null ? $"\n    at {first}" : ""));
            if (_logs.Count > MaxLogs) _logs.RemoveAt(0);
        }

        // 테이크가 끝난 뒤 CaptureDirector 가 부른다. 보고서 파일 경로를 돌려준다.
        public static string Write(string directory, string message, IReadOnlyList<string> reports, CaptureSettings settings, IReadOnlyList<string> locales)
        {
            var sb = new StringBuilder();
            string name = string.IsNullOrEmpty(directory) ? "(폴더 없음)" : Path.GetFileName(directory);
            sb.AppendLine($"## 결과");
            sb.AppendLine($"- 녹화: `{name}`");
            sb.AppendLine($"- 메시지: {message}");
            foreach (string r in reports) sb.AppendLine($"- {r}");
            sb.AppendLine();
            sb.AppendLine("## 환경");
            sb.AppendLine($"- Unity {Application.unityVersion} / {SystemInfo.operatingSystem} / {SystemInfo.graphicsDeviceName}");
            sb.AppendLine($"- 프로젝트: {PlayerSettings.productName} / 언어: {string.Join(", ", locales)}");
            sb.AppendLine($"- 설정: {JsonUtility.ToJson(settings)}");
            sb.AppendLine();
            AppendVerify(sb, directory);
            sb.AppendLine("## 콘솔 (이 도구 관련 줄만)");
            if (_logs.Count == 0) sb.AppendLine("- 없음");
            else { sb.AppendLine("```"); foreach (string l in _logs) sb.AppendLine(l); sb.AppendLine("```"); }

            Directory.CreateDirectory(ReportsDir);
            string path = Path.Combine(ReportsDir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{name}.md");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            LastPath = path;
            Debug.Log($"[LocaleCapture] 보고서 저장 {path} — 창의 'GitHub 이슈로 보고' 로 올릴 수 있다");
            return path;
        }

        private static void AppendVerify(StringBuilder sb, string directory)
        {
            string dir = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, CaptureVerify.Folder);
            if (dir == null || !Directory.Exists(dir)) return;
            sb.AppendLine("## 패스별 일치 검사");
            foreach (string file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
            {
                CaptureVerify.Report r;
                try { r = JsonUtility.FromJson<CaptureVerify.Report>(File.ReadAllText(file)); } catch { continue; }
                if (r == null) continue;
                sb.AppendLine($"### {r.Pass} — {(r.Ok ? "일치" : "다름")}, {r.Frames}프레임, 서버 재생 {r.ServerReplayed}·실서버 {r.ServerLive}·없음 {r.ServerMissed}, " +
                              $"로딩 대기 {r.HeldFrames}프레임, 난수 어긋남 {r.StateMismatchFrames}프레임");
                if (r.Shifts.Count > 0) sb.AppendLine($"- 로딩 시점 차이: {string.Join(", ", r.Shifts.Take(MaxMismatches))}");
                foreach (string m in r.UiMismatches.Take(MaxMismatches)) sb.AppendLine($"- 화면: {m}");
                foreach (string m in r.TapMismatches.Take(MaxMismatches)) sb.AppendLine($"- 터치: {Shorten(m, 220)}");
                if (r.TapMismatches.Count > MaxMismatches) sb.AppendLine($"- 터치 불일치 {r.TapMismatches.Count}건 중 {MaxMismatches}건만 표시");
                foreach (CaptureVerify.Issue i in r.Issues.Take(MaxIssuesPerPass)) sb.AppendLine($"- {i.Frame}: [{i.Kind}] {Shorten(i.Detail, 200)}");
                if (r.Issues.Count > MaxIssuesPerPass) sb.AppendLine($"- 이슈 {r.Issues.Count}건 중 {MaxIssuesPerPass}건만 표시");
            }
            sb.AppendLine();
        }

        private static string Shorten(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

        // 보고서 전체를 클립보드에 넣고, 제목·본문이 채워진 새 이슈 페이지를 연다. 본문은 주소 길이에 맞춰 자르고 나머지는 붙여넣기로 안내한다.
        public static void Open(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string text = File.ReadAllText(path);
            EditorGUIUtility.systemCopyBuffer = text;
            string title = "녹화 실패: " + Path.GetFileNameWithoutExtension(path).Substring(16);
            string body = text.Length <= MaxUrlBody ? text : text.Substring(0, MaxUrlBody) + "\n\n(본문이 길어 잘렸다 — 클립보드의 보고서 전체를 여기에 붙여 넣는다)";
            Application.OpenURL($"{RepoUrl}/issues/new?labels=recording-failure&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}");
        }
    }
}
