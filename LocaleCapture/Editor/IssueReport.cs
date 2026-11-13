using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화가 실패하거나 녹화와 다르게 재생된 테이크의 보고서를 창의 Output File > Path 폴더에 남긴다.
    // 창의 "GitHub 이슈 보내기" 가 그 보고서를 저장소 이슈로 올린다(IssueToken 의 토큰, GitHub 계정 없이). 토큰이 없거나 전송이
    // 실패하면 보고서를 클립보드에 복사하고 새 이슈 페이지를 연다. 누르기 전에는 아무것도 밖으로 나가지 않는다.
    // 콘솔은 이 도구 태그가 붙은 줄만 모은다(게임 로그·계정 정보 제외).
    [InitializeOnLoad]
    internal static class IssueReport
    {
        public const string Repo = IssueToken.Repo;
        public const string RepoUrl = "https://github.com/" + Repo;
        private const string Label = "recording-failure";
        private const string Tag = "LocaleCapture";
        private const string LastKey = "LocaleCapture.LastIssueReport";
        private const int MaxLogs = 40, MaxIssuesPerPass = 15, MaxMismatches = 5, MaxUrlBody = 6000;
        private static readonly List<string> _logs = new List<string>();
        private static UnityWebRequest _sending;
        private static string ReportsDir => LocaleCaptureWindow.SavedOutputDir;

        public static string LastPath
        {
            get { string p = EditorPrefs.GetString(LastKey, ""); return File.Exists(p) ? p : null; }
            private set => EditorPrefs.SetString(LastKey, value ?? "");
        }

        public static bool Sending => _sending != null;
        public static string Status { get; private set; } = "";     // 창에 보이는 전송 결과 한 줄
        public static string SentUrl { get; private set; }          // 올라간 이슈 주소

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
            sb.AppendLine("## 결과");
            sb.AppendLine($"- 녹화: `{name}`");
            sb.AppendLine($"- 메시지: {message}");
            foreach (string r in reports) sb.AppendLine($"- {r}");
            sb.AppendLine();
            sb.AppendLine("## 환경");
            sb.AppendLine($"- 보고: {Environment.UserName}@{Environment.MachineName}, {DateTime.Now:yyyy-MM-dd HH:mm}");
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
            Status = ""; SentUrl = null;
            Debug.Log($"[LocaleCapture] 보고서 저장 {path} — 창의 'GitHub 이슈 보내기' 로 올릴 수 있다");
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
        private static string TitleOf(string path) => "녹화 실패: " + Path.GetFileNameWithoutExtension(path).Substring(16);

        // 보고서를 저장소 이슈로 올린다. 토큰이 없으면 바로 브라우저로, 전송이 실패해도 브라우저로 넘긴다.
        public static void Send(string path)
        {
            if (Sending || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string token = IssueToken.Value;
            if (string.IsNullOrEmpty(token)) { Status = "토큰이 없어 브라우저로 연다"; Open(path); return; }
            string text = File.ReadAllText(path);
            var payload = new IssuePayload { title = TitleOf(path), body = text, labels = new[] { Label } };
            byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
            var request = new UnityWebRequest($"https://api.github.com/repos/{Repo}/issues", "POST")
            {
                uploadHandler = new UploadHandlerRaw(bytes) { contentType = "application/json" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = 30,
            };
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.SetRequestHeader("Accept", "application/vnd.github+json");
            request.SetRequestHeader("X-GitHub-Api-Version", "2022-11-28");
            request.SetRequestHeader("User-Agent", "LocaleCapture");
            _sending = request;
            Status = "보내는 중…";
            request.SendWebRequest().completed += _ => OnSent(path, request);
        }

        private static void OnSent(string path, UnityWebRequest request)
        {
            _sending = null;
            try
            {
                string body = request.downloadHandler?.text ?? "";
                if (request.result == UnityWebRequest.Result.Success && request.responseCode == 201)
                {
                    var created = JsonUtility.FromJson<IssueCreated>(body);
                    SentUrl = created?.html_url;
                    Status = $"이슈 #{created?.number} 보냄";
                    File.AppendAllText(path, $"\n보냄: {SentUrl}\n", new UTF8Encoding(false));
                    Debug.Log($"[LocaleCapture] 이슈 보냄 {SentUrl}");
                }
                else
                {
                    string why = request.responseCode == 401 ? "토큰이 만료됐거나 잘못됐다" : request.responseCode == 403 || request.responseCode == 404 ? "토큰에 이 저장소 Issues 쓰기 권한이 없다" : request.error;
                    Status = $"전송 실패({request.responseCode}): {why} — 브라우저로 연다";
                    Debug.LogWarning($"[LocaleCapture] 이슈 전송 실패 {request.responseCode}: {request.error}\n{Shorten(body, 400)}");
                    Open(path);
                }
            }
            finally { request.Dispose(); }
        }

        // 보고서 전체를 클립보드에 넣고, 제목·본문이 채워진 새 이슈 페이지를 연다. 본문은 주소 길이에 맞춰 자르고 나머지는 붙여넣기로 안내한다.
        public static void Open(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string text = File.ReadAllText(path);
            EditorGUIUtility.systemCopyBuffer = text;
            string body = text.Length <= MaxUrlBody ? text : text.Substring(0, MaxUrlBody) + "\n\n(본문이 길어 잘렸다 — 클립보드의 보고서 전체를 여기에 붙여 넣는다)";
            Application.OpenURL($"{RepoUrl}/issues/new?labels={Label}&title={Uri.EscapeDataString(TitleOf(path))}&body={Uri.EscapeDataString(body)}");
        }

        [Serializable] private class IssuePayload { public string title; public string body; public string[] labels; }
        [Serializable] private class IssueCreated { public int number; public string html_url; }
    }
}
