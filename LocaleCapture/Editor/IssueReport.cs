using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화가 실패하거나 녹화와 다르게 재생된 테이크의 보고서를 창의 Output File > Path 폴더에 남긴다(파일로만 — 밖으로 보내지 않는다).
    // 콘솔은 이 도구 태그가 붙은 줄만 모은다(게임 로그·계정 정보 제외).
    [InitializeOnLoad]
    internal static class IssueReport
    {
        private const string Tag = "LocaleCapture";
        private const int MaxLogs = 40, MaxIssuesPerPass = 15, MaxMismatches = 5, MaxDifferences = 15;
        private static readonly List<string> _logs = new List<string>();
        private static string ReportsDir => LocaleCaptureWindow.SavedOutputDir;

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
            Debug.Log($"[LocaleCapture] 보고서 저장 {path}");
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
                string verdict = r.DivergedFrame >= 0 ? "다름" : r.EarlyFrame >= 0 || r.TapMismatches.Count > 0 ? "녹화 시작 이후 일치(참고 있음)" : "일치";
                string fileName = Path.GetFileName(file);
                int attempt = fileName.LastIndexOf(".try", StringComparison.OrdinalIgnoreCase);
                string pass = fileName.EndsWith(".fast.json", StringComparison.OrdinalIgnoreCase) ? r.Pass + " (빠른 재생 — 이 뒤 실제 속도로 다시 뽑음)"
                    : attempt >= 0 ? r.Pass + $" (실제 속도 재생 — 이 뒤 다시 뽑음, {fileName.Substring(attempt + 1, fileName.Length - attempt - 6)})"
                    : r.Pass;
                sb.AppendLine($"### {pass} — {verdict}, {r.Frames}프레임(녹화 시작 {r.VideoStartFrame}), 서버 재생 {r.ServerReplayed}(통신 코드 경유 {r.ServerPiped})·실서버 {r.ServerLive}·없음 {r.ServerMissed}, " +
                              $"로딩 대기 {r.HeldFrames}프레임, 난수 어긋남 {r.StateMismatchFrames}프레임");
                if (r.DivergedFrame >= 0) sb.AppendLine($"- **처음 달라진 곳**: 프레임 {r.DivergedFrame} [{r.DivergedKind}] {Shorten(r.DivergedDetail, 300)}");
                if (r.EarlyFrame >= 0) sb.AppendLine($"- 녹화 시작 전 처음 달라진 곳: 프레임 {r.EarlyFrame} [{r.EarlyKind}] {Shorten(r.EarlyDetail, 300)}");
                if (r.Differences.Count > 0)
                {
                    sb.AppendLine($"- 달라진 곳(프레임 순, 로딩 시점 차이 포함) — 로딩 제외 모두 {r.DifferenceCount}건:");
                    foreach (string d in r.Differences.Take(MaxDifferences)) sb.AppendLine($"  - {Shorten(d, 240)}");
                }
                if (r.TraceVersion == 0)
                {
                    sb.AppendLine("- 이전 형식 기록이라 입력 받는 UI·난수 생성 프레임은 비교하지 못했다");
                    foreach (string m in r.TapMismatches.Take(MaxMismatches)) sb.AppendLine($"- 터치(맨 위 그래픽 기준): {Shorten(m, 220)}");
                }
                // 달라진 곳 목록에 이미 든 분류는 빼고, 그 밖의 기록(기록에 없는 서버 요청 등)만
                var others = r.Issues.Where(i => i.Kind != "터치" && i.Kind != "화면" && i.Kind != "상태" && i.Kind != "로딩").ToList();
                foreach (CaptureVerify.Issue i in others.Take(MaxIssuesPerPass)) sb.AppendLine($"- {i.Frame}: [{i.Kind}] {Shorten(i.Detail, 200)}");
                if (others.Count > MaxIssuesPerPass) sb.AppendLine($"- 그 밖의 기록 {others.Count}건 중 {MaxIssuesPerPass}건만 표시");
            }
            sb.AppendLine();
        }

        private static string Shorten(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
