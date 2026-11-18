using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace CookApps.LocaleCapture.Editor
{
    // 패키지에 동봉한 ffmpeg(Tools~/ffmpeg.exe). 편집 내보내기에서만 쓰고 UA 제작자에게는 보이지 않는다.
    internal static class BundledFfmpeg
    {
        public struct MediaInfo
        {
            public float Duration;
            public bool HasVideo, HasAudio;
            public int Width, Height;
            public float Fps;
        }

        private static string _cached;

        public static string Path
        {
            get
            {
                if (_cached != null && File.Exists(_cached)) return _cached;
                // 임베디드(Packages/)든 tgz·git 설치(Library/PackageCache)든 패키지가 실제로 놓인 곳에서 찾는다.
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(BundledFfmpeg).Assembly);
                string root = package != null ? package.resolvedPath : System.IO.Path.GetFullPath("Packages/com.cookapps.locale-capture");
                string bundled = System.IO.Path.Combine(root, "Tools~", "ffmpeg.exe");
                if (File.Exists(bundled)) return _cached = bundled;
                // .unitypackage 로 Assets 아래에 넣은 경우: Tools/ffmpeg.exe 를 에셋으로 찾는다.
                foreach (string guid in UnityEditor.AssetDatabase.FindAssets("ffmpeg"))
                {
                    string asset = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    if (asset.EndsWith("/Tools/ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(asset))
                        return _cached = System.IO.Path.GetFullPath(asset);
                }
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (!string.IsNullOrEmpty(local))
                {
                    string winget = System.IO.Path.Combine(local, "Microsoft", "WinGet", "Packages");
                    if (Directory.Exists(winget))
                        foreach (string f in Directory.GetFiles(winget, "ffmpeg.exe", SearchOption.AllDirectories)) return f;
                }
                return null;
            }
        }

        public static bool Available => Path != null;

        // 동기 실행. 실패하면 stderr 를 돌려준다(성공은 빈 문자열).
        public static string Run(string args, Action<string> onStderrLine = null, string workingDirectory = null)
        {
            using (var p = Process.Start(new ProcessStartInfo(Path, args)
                   {
                       UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
                       WorkingDirectory = workingDirectory ?? "",
                   }))
            {
                var err = new System.Text.StringBuilder();
                string line;
                while ((line = p.StandardError.ReadLine()) != null)
                {
                    err.AppendLine(line);
                    onStderrLine?.Invoke(line);
                }
                p.WaitForExit();
                return p.ExitCode == 0 ? "" : err.ToString().Trim();
            }
        }

        // ffmpeg -i 의 출력에서 길이·스트림·크기를 읽는다(ffprobe 없이).
        public static MediaInfo Probe(string file)
        {
            var info = new MediaInfo();
            string text = Run($"-hide_banner -i \"{file}\"") ?? "";
            Match d = Regex.Match(text, @"Duration: (\d+):(\d+):(\d+\.?\d*)");
            if (d.Success)
                info.Duration = int.Parse(d.Groups[1].Value) * 3600 + int.Parse(d.Groups[2].Value) * 60 + float.Parse(d.Groups[3].Value, CultureInfo.InvariantCulture);
            Match v = Regex.Match(text, @"Video: .*?(\d{2,5})x(\d{2,5})");
            if (v.Success) { info.HasVideo = true; info.Width = int.Parse(v.Groups[1].Value); info.Height = int.Parse(v.Groups[2].Value); }
            info.HasAudio = Regex.IsMatch(text, @"Stream #\d+:\d+.*Audio:");
            Match f = Regex.Match(text, @"(\d+(?:\.\d+)?) fps");
            if (f.Success) info.Fps = float.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
            return info;
        }

        // 진행 로그("time=00:00:12.34")에서 초를 뽑는다.
        public static float ParseProgress(string line)
        {
            Match m = Regex.Match(line, @"time=(\d+):(\d+):(\d+\.?\d*)");
            if (!m.Success) return -1f;
            return int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }
    }
}
