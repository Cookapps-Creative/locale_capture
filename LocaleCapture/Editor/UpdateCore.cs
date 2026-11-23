using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CookApps.LocaleCapture.Editor
{
    // 설치본을 저장소의 배포 브랜치와 맞춘다. 받아 오는 일은 git 이 한다 — 비공개 저장소의 로그인도 이 PC 의 git 이 가진 것을 그대로 쓴다
    // (여기서는 토큰을 읽지도 저장하지도 않는다). 받은 것과 설치본을 파일마다 비교해 다른 것만 바꾼다.
    //  · 받는 곳은 작업 폴더가 없는 저장소(bare)다. 명령마다 GIT_DIR 로 그곳을 못 박아, 어떤 경우에도 게임 프로젝트의 저장소를 건드리지 않는다.
    //  · 큰 파일(LFS — ffmpeg·Harmony)은 내용이 달라졌을 때만 받는다.
    // Unity 를 부르지 않는다 — 백그라운드 스레드에서 돌고, 에디터 밖에서도 그대로 돌려 볼 수 있다.
    internal static class UpdateCore
    {
        public const string Repository = "https://github.com/Cookapps-Creative/locale_capture.git";
        public const string Branch = "release";       // 배포 = 이 브랜치를 옮기는 것(git push origin main:release)
        public const string Folder = "LocaleCapture";  // 저장소 안에서 도구가 든 폴더

        internal static string GitExe = "git";

        public enum Problem { None, NoGit, NoAccess, NoNetwork, NoRelease, NoLfs, Other }

        public sealed class Outcome
        {
            public Problem Problem;
            public string Detail = "";
            public bool Ok => Problem == Problem.None;
        }

        public sealed class Release
        {
            public string Sha = "", Date = "", Subject = "";

            // "2026-11-20 12:03 (d8d255d)"
            public string Label
            {
                get
                {
                    string day = DateTimeOffset.TryParse(Date, out DateTimeOffset at) ? at.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : Date;
                    return Sha.Length >= 7 ? $"{day} ({Sha.Substring(0, 7)})" : day;
                }
            }
        }

        // 설치본에서 바꿀 파일 하나. Path 는 설치 폴더 기준('/' 구분).
        public sealed class Change
        {
            public string Path;
            public bool Delete;
            public string Oid;   // LFS 파일이면 내용의 sha256
            public long Size;
            public bool Lfs => Oid != null;
        }

        private static readonly Outcome Fine = new Outcome();

        // ── git 실행 ────────────────────────────────────────────────────────────

        private sealed class Run
        {
            public int Code = -1;
            public string Out = "", Error = "";
            public bool Missing, TimedOut;
            public bool Ok => Code == 0 && !Missing && !TimedOut;
        }

        // interactive 가 꺼져 있으면 로그인 창을 띄우지 않고 실패한다(창을 열 때의 자동 확인). 터미널 입력은 어느 쪽이든 기다리지 않는다.
        private static Run Git(string gitDir, string arguments, bool interactive, int seconds)
        {
            var run = new Run();
            var info = new ProcessStartInfo(GitExe, arguments)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            if (gitDir != null)
            {
                info.EnvironmentVariables["GIT_DIR"] = gitDir;
                info.WorkingDirectory = Path.GetDirectoryName(gitDir);
            }
            else info.EnvironmentVariables.Remove("GIT_DIR");
            info.EnvironmentVariables.Remove("GIT_WORK_TREE");
            info.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            info.EnvironmentVariables["GIT_LFS_SKIP_SMUDGE"] = "1";   // 큰 파일은 포인터만 — 필요한 것만 따로 받는다
            info.EnvironmentVariables["LC_ALL"] = "C";                // 실패 까닭을 문구로 가린다
            if (!interactive) info.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            try
            {
                using (var process = new Process { StartInfo = info })
                {
                    process.Start();
                    process.StandardInput.Close();
                    Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(seconds * 1000))
                    {
                        run.TimedOut = true;
                        try { process.Kill(); } catch { }
                    }
                    // 죽인 뒤에도 자식 프로세스가 파이프를 쥐고 있을 수 있어 무한정 기다리지 않는다.
                    if (Task.WaitAll(new Task[] { output, error }, 5000))
                    {
                        run.Out = output.Result ?? "";
                        run.Error = error.Result ?? "";
                    }
                    if (!run.TimedOut) run.Code = process.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception) { run.Missing = true; }
            catch (Exception e) { run.Error = e.Message; }
            return run;
        }

        private static string Quote(string value) => "\"" + value.TrimEnd('\\', '/').Replace("\"", "\\\"") + "\"";

        private static bool Has(string text, params string[] any) => any.Any(s => text.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

        private static Outcome Fail(Run run)
        {
            string text = (run.Error + "\n" + run.Out).Trim();
            Problem problem =
                run.Missing ? Problem.NoGit
                : Has(text, "couldn't find remote ref") ? Problem.NoRelease
                : Has(text, "is not a git command") ? Problem.NoLfs
                : Has(text, "Authentication failed", "could not read Username", "could not read Password", "Repository not found", "terminal prompts disabled",
                    "interactivity has been disabled", "Permission denied", "error: 403", "error: 401", "status 403", "status 401", "Authorization error") ? Problem.NoAccess
                : run.TimedOut || Has(text, "Could not resolve host", "Failed to connect", "Connection timed out", "Connection was reset", "unable to access", "network is unreachable") ? Problem.NoNetwork
                : Problem.Other;
            string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string detail = string.Join(" / ", lines.Skip(Math.Max(0, lines.Length - 3)));
            if (run.TimedOut) detail = ("시간 초과 " + detail).Trim();
            return new Outcome { Problem = problem, Detail = detail.Length <= 400 ? detail : detail.Substring(0, 400) };
        }

        private static Outcome Other(string detail) => new Outcome { Problem = Problem.Other, Detail = detail };

        // ── 받기 ────────────────────────────────────────────────────────────────

        private static string GitDir(string cache) => Path.Combine(cache, "repo.git");

        private static string TarFile(string cache) => Path.Combine(cache, "release.tar");

        private static string ObjectPath(string cache, string oid) => Path.Combine(GitDir(cache), "lfs", "objects", oid.Substring(0, 2), oid.Substring(2, 2), oid);

        // 배포 브랜치의 지금 내용을 cache 로 받는다(큰 파일은 포인터만).
        public static Outcome Fetch(string cache, string branch, bool interactive, out Release release)
        {
            Outcome outcome = FetchOnce(cache, branch, interactive, out release);
            if (outcome.Ok || outcome.Problem != Problem.Other) return outcome;
            // 받아 둔 것이 깨졌을 수 있다(받는 중에 에디터가 꺼졌을 때의 잠금 파일 등) — 지우고 한 번만 다시 받는다.
            try { DeleteDirectory(cache); }
            catch (Exception e) { return Other($"{outcome.Detail} / 받아 둔 폴더를 지우지 못했다: {e.Message}"); }
            return FetchOnce(cache, branch, interactive, out release);
        }

        private static Outcome FetchOnce(string cache, string branch, bool interactive, out Release release)
        {
            release = null;
            string git = GitDir(cache);
            try { Directory.CreateDirectory(cache); }
            catch (Exception e) { return Other($"{cache} 를 만들지 못했다: {e.Message}"); }
            if (!File.Exists(Path.Combine(git, "HEAD")))
            {
                Run init = Git(null, "init --quiet --bare " + Quote(git), false, 60);
                if (!init.Ok) return Fail(init);
                if (!File.Exists(Path.Combine(git, "HEAD"))) return Other("받을 곳을 만들지 못했다");
            }
            Run url = Git(git, "config remote.origin.url " + Quote(Repository), false, 60);
            if (!url.Ok) return Fail(url);
            string local = "refs/remotes/origin/" + branch;
            Run fetch = Git(git, $"fetch --quiet --force --no-tags origin +refs/heads/{branch}:{local}", interactive, interactive ? 300 : 60);
            if (!fetch.Ok) return Fail(fetch);
            Run log = Git(git, $"log -1 --format=%H%n%cI%n%s {local}", false, 60);
            string[] lines = log.Out.Split('\n');
            if (!log.Ok || lines.Length < 3 || !IsSha(lines[0].Trim())) return log.Ok ? Other("배포본의 정보를 읽지 못했다") : Fail(log);
            release = new Release { Sha = lines[0].Trim(), Date = lines[1].Trim(), Subject = lines[2].Trim() };
            string tar = TarFile(cache);
            Run archive = Git(git, $"-c core.autocrlf=false archive --format=tar --output={Quote(tar)} {release.Sha} {Folder}", false, 120);
            if (!archive.Ok) return Fail(archive);
            return Fine;
        }

        private static bool IsSha(string text) => text != null && text.Length == 40 && text.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));

        // 설치본의 버전(from)부터 배포본(to)까지 바뀐 내용 — 커밋 제목. 이어지지 않는 버전이면 빈 목록.
        public static List<string> Log(string cache, string from, string to)
        {
            var lines = new List<string>();
            if (!IsSha(from) || !IsSha(to) || from == to) return lines;
            string git = GitDir(cache);
            if (!Git(git, $"merge-base --is-ancestor {from} {to}", false, 60).Ok) return lines;
            Run run = Git(git, $"log --no-merges --format=%s -n 12 {from}..{to} -- {Folder}", false, 60);
            if (run.Ok) lines.AddRange(run.Out.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            return lines;
        }

        // 바뀐 큰 파일을 받는다(이미 받아 둔 것은 건너뛴다).
        public static Outcome FetchLfs(string cache, string sha, List<Change> changes, bool interactive)
        {
            List<Change> need = changes.Where(c => c.Lfs && !Stored(cache, c)).ToList();
            if (need.Count == 0) return Fine;
            if (!IsSha(sha)) return Other("배포본의 버전을 모른다");
            string include = string.Join(",", need.Select(c => Folder + "/" + c.Path));
            Run run = Git(GitDir(cache), $"lfs fetch origin {sha} --include={Quote(include)}", interactive, 3600);
            if (!run.Ok) return Fail(run);
            Change missing = need.FirstOrDefault(c => !Stored(cache, c));
            return missing == null ? Fine : Other($"{missing.Path} 를 받지 못했다 / {Fail(run).Detail}");
        }

        private static bool Stored(string cache, Change change)
        {
            var file = new FileInfo(ObjectPath(cache, change.Oid));
            return file.Exists && file.Length == change.Size;
        }

        // 받아 둔 큰 파일을 지운다(설치가 끝나면 두 벌 둘 까닭이 없다).
        public static void Trim(string cache)
        {
            try { DeleteDirectory(Path.Combine(GitDir(cache), "lfs", "objects")); } catch { }
        }

        private static void DeleteDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);   // git 의 pack 은 읽기 전용이다
            Directory.Delete(directory, true);
        }

        // ── 비교 ────────────────────────────────────────────────────────────────

        private const string EditorMark = "Editor/CookApps.LocaleCapture.Editor.asmdef", RuntimeMark = "Runtime/CookApps.LocaleCapture.asmdef";

        // 배포본에서 없어진 파일을 지울 때 건드리는 종류. 그 밖의 파일(녹화 때 만든 장치 파일 등)은 그대로 둔다.
        private static readonly HashSet<string> Owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".asmdef", ".asmref", ".dll", ".exe", ".md", ".txt" };

        public static bool LooksInstalled(string install) =>
            File.Exists(Path.Combine(install, EditorMark)) && File.Exists(Path.Combine(install, RuntimeMark));

        // 받아 둔 배포본과 설치본을 비교해 바꿀 파일을 돌려준다. 같으면 빈 목록, 비교할 수 없으면 null(error 에 까닭).
        public static List<Change> Diff(string cache, string install, out string error)
        {
            error = null;
            Dictionary<string, byte[]> source;
            try { source = ReadTar(TarFile(cache), Folder + "/"); }
            catch (Exception e) { error = "받은 배포본을 읽지 못했다: " + e.Message; return null; }
            if (!source.ContainsKey(EditorMark) || !source.ContainsKey(RuntimeMark)) { error = "배포본에 도구가 들어 있지 않다"; return null; }
            if (!LooksInstalled(install)) { error = $"설치 폴더가 아니다: {install}"; return null; }

            var changes = new List<Change>();
            try
            {
                foreach (KeyValuePair<string, byte[]> entry in source.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    string target = Path.Combine(install, entry.Key);
                    if (Pointer(entry.Value, out string oid, out long size))
                    {
                        var file = new FileInfo(target);
                        if (!file.Exists || file.Length != size || Sha256(target) != oid) changes.Add(new Change { Path = entry.Key, Oid = oid, Size = size });
                    }
                    else if (!File.Exists(target) || !SameContent(entry.Value, File.ReadAllBytes(target)))
                        changes.Add(new Change { Path = entry.Key, Size = entry.Value.Length });
                }

                // 배포본에서 없어진 파일. 배포본에 있는 폴더(Editor·Runtime·Tools …)와 맨 위에서만, 도구가 가진 종류만 지운다.
                var tops = new HashSet<string>(source.Keys.Where(k => k.Contains("/")).Select(k => k.Substring(0, k.IndexOf('/'))), StringComparer.OrdinalIgnoreCase);
                string root = Path.GetFullPath(install).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var known = new HashSet<string>(source.Keys, StringComparer.OrdinalIgnoreCase);
                foreach (string file in Directory.GetFiles(install, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetFullPath(file).Substring(root.Length).Replace('\\', '/');
                    if (known.Contains(relative)) continue;
                    int slash = relative.IndexOf('/');
                    if (slash >= 0 && !tops.Contains(relative.Substring(0, slash))) continue;
                    string asset = relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? relative.Substring(0, relative.Length - 5) : relative;
                    if (!Owned.Contains(Path.GetExtension(asset))) continue;
                    changes.Add(new Change { Path = relative, Delete = true });
                }
            }
            catch (Exception e) { error = "설치본을 읽지 못했다: " + e.Message; return null; }
            return changes;
        }

        // git archive 가 내는 tar 를 읽는다(512바이트 머리 + 512 로 올림한 본문). 긴 이름은 prefix 칸이나 pax 머리('x')로 온다.
        private static Dictionary<string, byte[]> ReadTar(string file, string strip)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[] data = File.ReadAllBytes(file);
            string longName = null;
            for (int at = 0; at + 512 <= data.Length && data[at] != 0;)
            {
                string name = Text(data, at, 100), prefix = Text(data, at + 345, 155), octal = Text(data, at + 124, 12).Trim();
                long size = octal.Length == 0 ? 0 : Convert.ToInt64(octal, 8);
                char type = (char) data[at + 156];
                int body = at + 512;
                if (body + size > data.Length) throw new InvalidDataException("tar 가 중간에 끊겼다");
                if (type == 'x')
                {
                    // "길이 키=값\n" 줄들. path 만 본다.
                    foreach (string record in Encoding.UTF8.GetString(data, body, (int) size).Split('\n'))
                    {
                        int space = record.IndexOf(' '), equal = record.IndexOf('=');
                        if (space >= 0 && equal > space && record.Substring(space + 1, equal - space - 1) == "path") longName = record.Substring(equal + 1);
                    }
                }
                else if (type == '0' || type == '\0')
                {
                    string path = longName ?? (prefix.Length > 0 ? prefix + "/" + name : name);
                    longName = null;
                    if (path.StartsWith(strip, StringComparison.Ordinal) && path.Length > strip.Length)
                    {
                        byte[] content = new byte[size];
                        Buffer.BlockCopy(data, body, content, 0, (int) size);
                        entries[path.Substring(strip.Length)] = content;
                    }
                }
                else if (type != 'g') longName = null;
                at = body + (int) ((size + 511) / 512 * 512);
            }
            return entries;
        }

        private static string Text(byte[] data, int start, int length)
        {
            int end = start;
            while (end < start + length && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, start, end - start);
        }

        // LFS 포인터: "version https://git-lfs.github.com/spec/v1 / oid sha256:<64자> / size <바이트>"
        private static bool Pointer(byte[] data, out string oid, out long size)
        {
            oid = null;
            size = 0;
            if (data.Length > 400 || data.Length < 60) return false;
            string text = Encoding.ASCII.GetString(data);
            if (!text.StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal)) return false;
            foreach (string line in text.Split('\n'))
            {
                if (line.StartsWith("oid sha256:", StringComparison.Ordinal)) oid = line.Substring(11).Trim();
                else if (line.StartsWith("size ", StringComparison.Ordinal)) long.TryParse(line.Substring(5).Trim(), out size);
            }
            return oid != null && oid.Length == 64;
        }

        private static string Sha256(string file)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20))
                return Hex(sha.ComputeHash(stream));
        }

        private static string Hex(byte[] bytes)
        {
            var text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        // 글 파일은 줄바꿈(CRLF·LF)만 다르면 같은 것으로 본다 — 패키지로 깐 것은 CRLF, 저장소의 원본은 LF 다.
        private static bool SameContent(byte[] a, byte[] b)
        {
            if (a.Length == b.Length)
            {
                int k = 0;
                while (k < a.Length && a[k] == b[k]) k++;
                if (k == a.Length) return true;
            }
            if (Binary(a) || Binary(b)) return false;
            int i = 0, j = 0;
            while (true)
            {
                if (i + 1 < a.Length && a[i] == '\r' && a[i + 1] == '\n') i++;
                if (j + 1 < b.Length && b[j] == '\r' && b[j + 1] == '\n') j++;
                if (i >= a.Length || j >= b.Length) return i >= a.Length && j >= b.Length;
                if (a[i] != b[j]) return false;
                i++;
                j++;
            }
        }

        private static bool Binary(byte[] data)
        {
            for (int i = 0, n = Math.Min(data.Length, 8000); i < n; i++) if (data[i] == 0) return true;
            return false;
        }

        // ── 바꾸기 ──────────────────────────────────────────────────────────────

        // changes 대로 설치본을 바꾼다. 큰 파일은 FetchLfs 로 받아 둔 뒤에 부른다. 실패하면 어느 파일인지 적어 던진다.
        public static void Apply(string cache, string install, List<Change> changes)
        {
            Dictionary<string, byte[]> source = ReadTar(TarFile(cache), Folder + "/");
            List<Change> writes = changes.Where(c => !c.Delete).OrderBy(c => c.Lfs ? 0 : 1).ToList();   // 큰 파일부터 — 못 바꾸면 스크립트를 건드리기 전에 멈춘다
            foreach (Change change in writes)
            {
                if (change.Lfs ? !Stored(cache, change) : !source.ContainsKey(change.Path)) throw new IOException($"{change.Path} 의 새 내용이 없다");
            }
            foreach (Change change in writes)
            {
                string target = Path.Combine(install, change.Path), temp = target + ".new~";   // '~' 로 끝나는 파일은 Unity 가 보지 않는다
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (change.Lfs) File.Copy(ObjectPath(cache, change.Oid), temp, true);
                    else File.WriteAllBytes(temp, source[change.Path]);
                    File.SetAttributes(temp, FileAttributes.Normal);
                    if (File.Exists(target))
                    {
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Delete(target);
                    }
                    File.Move(temp, target);
                }
                catch (Exception e)
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    throw new IOException($"{change.Path} 를 바꾸지 못했다 — {e.Message}", e);
                }
            }
            var emptied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Change change in changes.Where(c => c.Delete))
            {
                string target = Path.Combine(install, change.Path);
                try
                {
                    if (!File.Exists(target)) continue;
                    File.SetAttributes(target, FileAttributes.Normal);
                    File.Delete(target);
                    emptied.Add(Path.GetDirectoryName(target));
                }
                catch (Exception e) { throw new IOException($"{change.Path} 를 지우지 못했다 — {e.Message}", e); }
            }
            // 파일을 지워서 비게 된 폴더는 그 meta 와 함께 치운다(설치 폴더 자체는 두고).
            string root = Path.GetFullPath(install).TrimEnd('\\', '/');
            foreach (string directory in emptied.OrderByDescending(d => d.Length))
            {
                try
                {
                    if (string.Equals(Path.GetFullPath(directory).TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any()) continue;
                    Directory.Delete(directory);
                    if (File.Exists(directory + ".meta")) File.Delete(directory + ".meta");
                }
                catch { }
            }
        }

        // ── 설치된 버전 표시 ────────────────────────────────────────────────────

        // 설치본이 어느 배포본과 같았는지 적어 둔다. 그 뒤 파일이 하나라도 바뀌면(지문이 달라지면) 모르는 것으로 친다.
        public static void WriteStamp(string file, Release release, string install)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[] { release.Sha, release.Date, release.Subject.Replace('\n', ' '), Fingerprint(install) }, new UTF8Encoding(false));
            }
            catch { }
        }

        public static Release ReadStamp(string file, string install)
        {
            try
            {
                if (!File.Exists(file)) return null;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                if (lines.Length < 4 || !IsSha(lines[0]) || lines[3] != Fingerprint(install)) return null;
                return new Release { Sha = lines[0], Date = lines[1], Subject = lines[2] };
            }
            catch { return null; }
        }

        // 설치 폴더의 파일 이름·크기·고친 시각을 묶은 값(내용은 읽지 않는다). 도구가 가진 종류의 파일만 본다.
        public static string Fingerprint(string install)
        {
            string root = Path.GetFullPath(install).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var text = new StringBuilder();
            foreach (FileInfo file in new DirectoryInfo(install).GetFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName, StringComparer.OrdinalIgnoreCase))
            {
                string relative = file.FullName.Substring(root.Length).Replace('\\', '/');
                string asset = relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? relative.Substring(0, relative.Length - 5) : relative;
                if (!Owned.Contains(Path.GetExtension(asset))) continue;
                text.Append(relative.ToLowerInvariant()).Append('|').Append(file.Length).Append('|').Append(file.LastWriteTimeUtc.Ticks).Append('\n');
            }
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString())));
        }
    }
}
