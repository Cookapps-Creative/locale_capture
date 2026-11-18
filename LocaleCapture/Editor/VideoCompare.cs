using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CookApps.LocaleCapture.Editor
{
    // 언어별 영상을 프레임마다 서로 비교한다. 재생 중 검사(CaptureVerify)와 따로, 나온 영상 자체로 하는 마지막 확인이다.
    // 두 가지를 본다(화면을 가로 64픽셀 회색으로 줄여서):
    //  · 화면이 바뀌는 시점 — 프레임마다 "앞 프레임에서 얼마나 바뀌었나"를 영상별로 재서 맞춰 본다. 같은 플레이면 언어가 달라도
    //    같은 프레임에 같은 만큼 바뀐다(글자는 가만히 있는 동안 값에 안 잡힌다). 한쪽만 바뀌는 프레임이 있으면 그때 갈라진 것이다.
    //    재본 값(같은 플레이 30쌍·갈라진 플레이 5쌍): 같은 플레이 0.1 이하(가장 큰 것 0.85), 갈라진 플레이 2.9~76.
    //  · 화면 모양 — 닮은 정도(PSNR)의 1초 평균. 처음부터 다른 화면이라 "바뀌는 시점" 으로는 안 잡히는 경우를 본다.
    //    글자가 큰 화면(챕터 제목·상점)은 같은 플레이도 22dB 까지 내려가 갈라진 플레이(21dB)와 구분이 안 되므로, 팝업 유무(약 11dB)처럼 확실한 차이만 본다.
    internal static class VideoCompare
    {
        private const int Window = 30;                // 닮은 정도 평균을 내는 프레임 수
        private const double Differ = 16.0;           // 닮은 정도가 이보다 낮으면 다른 화면(실패)
        private const double Cap = 60.0;              // 똑같은 프레임(inf)은 이 값으로
        private const double MotionRelative = 0.25;   // 두 영상의 바뀐 정도 중 큰 쪽의 이 비율까지는 글자 차이로 본다
        private const double MotionSuspect = 1.2;     // 바뀐 정도의 차이가 이 이상이면 주의
        private const double MotionDiffer = 2.5;      // 이 이상이면 다르게 흘렀다(실패)
        private const string StatsFile = "verify_video.tmp", MotionFileA = "verify_motion_a.tmp", MotionFileB = "verify_motion_b.tmp";
        private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov" };

        private sealed class Measured
        {
            public List<double> Psnr, MotionA, MotionB;
        }

        // 첫 언어(가능하면 녹화 언어) 영상과 나머지 언어 영상을 비교해 결과 줄을 돌려준다.
        // extension: 이번에 뽑은 형식("mp4" 등). 주면 그 형식 파일만 본다 — 형식을 바꿔 다시 뽑았을 때 이전 형식 파일을 집지 않게.
        public static List<string> Run(string directory, List<string> locales, string extension = null)
        {
            var lines = new List<string>();
            ReplayManifest manifest = ReplayManifest.Load(directory);
            if (manifest == null || locales == null || !BundledFfmpeg.Available) return lines;
            locales = locales.Distinct().ToList();
            if (locales.Contains(manifest.RecordedLocale)) { locales.Remove(manifest.RecordedLocale); locales.Insert(0, manifest.RecordedLocale); }
            var videos = new List<(string locale, string path)>();
            foreach (string locale in locales)
            {
                string exact = string.IsNullOrEmpty(extension) ? null : Path.Combine(directory, $"{manifest.Name}_{locale}.{extension}");
                string path = exact != null
                    ? (File.Exists(exact) ? exact : null)
                    : Directory.GetFiles(directory, $"{manifest.Name}_{locale}.*")
                        .FirstOrDefault(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !f.Contains(".remix"));
                if (path != null) videos.Add((locale, path));
            }
            if (videos.Count < 2) return lines;

            int fps = Math.Max(1, manifest.Fps);
            double lowest = double.MaxValue, widest = 0;
            bool flagged = false, compared = false;
            for (int i = 1; i < videos.Count; i++)
            {
                string pair = $"{videos[i].locale} 영상이 {videos[0].locale} 영상과";
                Measured m = Measure(directory, videos[0].path, videos[i].path);
                if (m == null || m.Psnr.Count == 0) { lines.Add($"참고 — 영상 비교: {videos[i].locale} 영상을 읽지 못해 비교하지 못했다"); continue; }
                compared = true;
                lowest = Math.Min(lowest, LowestWindow(m.Psnr));

                // 길이: 같은 플레이를 같은 프레임 수로 뽑으므로 프레임 수가 같아야 한다
                if (m.MotionA.Count != m.MotionB.Count)
                {
                    flagged = true;
                    lines.Add($"주의 — 영상 비교: {pair} 길이가 다르다 ({m.MotionB.Count + 1} 대 {m.MotionA.Count + 1} 프레임)");
                }

                // 화면이 바뀌는 시점
                (int suspect, int differ, double top) = MotionMismatch(m.MotionA, m.MotionB);
                widest = Math.Max(widest, top);
                if (differ >= 0)
                {
                    flagged = true;
                    // 시계열의 i 번째 값은 영상 프레임 i → i+1 사이의 변화
                    lines.Add($"다름 — 영상 비교: {pair} {(differ + 1) / (double) fps:F1}초(영상 프레임 {differ + 1})에서 화면이 다르게 바뀐다 " +
                              $"(바뀐 정도 {m.MotionB[differ]:F1} 대 {m.MotionA[differ]:F1}) — 그때부터 다른 플레이");
                    continue;
                }

                // 화면 모양
                (int hard, double hardValue) = FirstBelow(m.Psnr, Differ);
                if (hard >= 0)
                {
                    flagged = true;
                    lines.Add($"다름 — 영상 비교: {pair} {hard / (double) fps:F1}초(영상 프레임 {hard})부터 다른 화면이다 (닮은 정도 {hardValue:F1}dB)");
                    continue;
                }
                if (suspect >= 0)
                {
                    flagged = true;
                    lines.Add($"주의 — 영상 비교: {pair} {(suspect + 1) / (double) fps:F1}초(영상 프레임 {suspect + 1}) 즈음 화면이 바뀌는 정도가 다르다 " +
                              $"(바뀐 정도 {m.MotionB[suspect]:F1} 대 {m.MotionA[suspect]:F1}) — 그 구간을 직접 확인한다");
                }
            }
            if (!flagged && compared)
                lines.Add($"일치 — 영상 비교: 언어 영상끼리 화면이 바뀌는 시점이 끝까지 같다 (바뀐 정도 차이 최대 {widest:F2}, 닮은 정도 최저 {lowest:F1}dB)");
            return lines;
        }

        // 한 번의 실행으로 두 영상의 닮은 정도(PSNR)와 영상별 "앞 프레임에서 바뀐 정도" 를 잰다. 실패하면 null.
        private static Measured Measure(string directory, string a, string b)
        {
            string[] files = { Path.Combine(directory, StatsFile), Path.Combine(directory, MotionFileA), Path.Combine(directory, MotionFileB) };
            try
            {
                foreach (string f in files) if (File.Exists(f)) File.Delete(f);
                // 결과 파일은 작업 폴더 기준 이름으로 준다 — 필터 인자 안에서는 드라이브 문자의 ':' 이 구분자로 읽힌다.
                const string small = "scale=64:-2:flags=area,format=gray";
                const string change = "tblend=all_mode=difference,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=";
                string error = BundledFfmpeg.Run(
                    $"-hide_banner -loglevel error -i \"{a}\" -i \"{b}\" -lavfi \"[0:v]{small},split[x][xm];[1:v]{small},split[y][ym];" +
                    $"[x][y]psnr=stats_file={StatsFile};[xm]{change}{MotionFileA},nullsink;[ym]{change}{MotionFileB},nullsink\" -f null -",
                    null, directory);
                if (!string.IsNullOrEmpty(error) || files.Any(f => !File.Exists(f))) return null;
                return new Measured
                {
                    Psnr = Values(files[0], @"psnr_avg:(\S+)", Cap),
                    MotionA = Values(files[1], @"YAVG=(\S+)", 255),
                    MotionB = Values(files[2], @"YAVG=(\S+)", 255),
                };
            }
            finally
            {
                foreach (string f in files)
                    try { if (File.Exists(f)) File.Delete(f); } catch { /* 잠김 */ }
            }
        }

        private static List<double> Values(string file, string pattern, double cap)
        {
            var values = new List<double>();
            foreach (Match m in Regex.Matches(File.ReadAllText(file), pattern))
                values.Add(double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsInfinity(v) && !double.IsNaN(v) ? Math.Min(v, cap) : cap);
            return values;
        }

        // 프레임마다 두 영상의 바뀐 정도 차이(큰 쪽의 일정 비율은 글자 차이로 빼고)를 보고, 주의·다름 기준을 처음 넘는 프레임과 가장 큰 값을 돌려준다.
        internal static (int suspect, int differ, double top) MotionMismatch(List<double> a, List<double> b)
        {
            int suspect = -1, differ = -1;
            double top = 0;
            int n = Math.Min(a.Count, b.Count);
            for (int i = 0; i < n; i++)
            {
                double score = Math.Abs(a[i] - b[i]) - MotionRelative * Math.Max(a[i], b[i]);
                if (score > top) top = score;
                if (suspect < 0 && score >= MotionSuspect) suspect = i;
                if (differ < 0 && score >= MotionDiffer) differ = i;
            }
            return (suspect, differ, top);
        }

        // Window 프레임 평균이 threshold 아래로 내려간 첫 구간의 시작 프레임과 그 평균. 없으면 (-1, 0).
        private static (int frame, double value) FirstBelow(List<double> psnr, double threshold)
        {
            int n = Math.Min(Window, psnr.Count);
            double sum = 0;
            for (int i = 0; i < psnr.Count; i++)
            {
                sum += psnr[i];
                if (i >= n) sum -= psnr[i - n];
                if (i >= n - 1 && sum / n < threshold) return (i - n + 1, sum / n);
            }
            return (-1, 0);
        }

        private static double LowestWindow(List<double> psnr)
        {
            int n = Math.Min(Window, psnr.Count);
            double sum = 0, lowest = double.MaxValue;
            for (int i = 0; i < psnr.Count; i++)
            {
                sum += psnr[i];
                if (i >= n) sum -= psnr[i - n];
                if (i >= n - 1) lowest = Math.Min(lowest, sum / n);
            }
            return lowest;
        }
    }
}
