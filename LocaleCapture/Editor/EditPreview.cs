using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 편집 창 미리보기 재료. 에디트 모드에서는 VideoPlayer 가 준비되지 않으므로 동봉 ffmpeg 로
    // 프레임(JPEG)과 소리(WAV)를 뽑아 Library/LocaleCapturePreview 에 캐시하고, 소리는 에디터 미리듣기로 튼다.
    internal static class EditPreview
    {
        public const float PreviewFps = 15f;
        private const int PreviewWidth = 360;

        private static string CacheRoot => Path.GetFullPath("Library/LocaleCapturePreview");

        private static string Key(string file, string extra = "")
        {
            var fi = new FileInfo(file);
            string raw = fi.FullName + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "missing") + "|" + extra;
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(raw))).Replace("-", "").Substring(0, 16);
        }

        // ── 프레임 ─────────────────────────────────────────────────────────────

        public sealed class Frames : IDisposable
        {
            public string Dir;
            public int Count;
            public float Duration;
            public int Width, Height;
            private Texture2D _texture, _blurred;
            private int _loaded = -1, _blurLoaded = -1;

            public Texture Get(float time)
            {
                if (Count == 0) return null;
                int index = Mathf.Clamp(Mathf.FloorToInt(time * PreviewFps), 0, Count - 1);
                if (_texture == null) _texture = new Texture2D(2, 2, TextureFormat.RGB24, false) { hideFlags = HideFlags.HideAndDontSave };
                if (index != _loaded)
                {
                    string path = Path.Combine(Dir, $"{index + 1:00000}.jpg");
                    if (File.Exists(path)) _texture.LoadImage(File.ReadAllBytes(path));
                    _loaded = index;
                }
                return _texture;
            }

            // 마지막 Get 장면의 흐린 사본(블러 여백 미리보기용). 1/16 로 줄이고 한 번 더 뭉갠 뒤 쌍선형으로 키워 그린다.
            public Texture Blurred()
            {
                if (_texture == null || _loaded < 0) return null;
                if (_blurLoaded == _loaded && _blurred != null) return _blurred;
                int w = Mathf.Max(2, _texture.width / 16), h = Mathf.Max(2, _texture.height / 16);
                Color32[] src = _texture.GetPixels32();
                var small = new Color32[w * h];
                int bx = _texture.width / w, by = _texture.height / h;
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int yy = y * by; yy < (y + 1) * by && yy < _texture.height; yy++)
                    for (int xx = x * bx; xx < (x + 1) * bx && xx < _texture.width; xx++)
                    {
                        Color32 c = src[yy * _texture.width + xx];
                        r += c.r; g += c.g; b += c.b; n++;
                    }
                    n = Mathf.Max(1, n);
                    small[y * w + x] = new Color32((byte) (r / n), (byte) (g / n), (byte) (b / n), 255);
                }
                var soft = new Color32[w * h];
                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int sx = Mathf.Clamp(x + dx, 0, w - 1), sy = Mathf.Clamp(y + dy, 0, h - 1);
                        Color32 c = small[sy * w + sx];
                        r += c.r; g += c.g; b += c.b; n++;
                    }
                    soft[y * w + x] = new Color32((byte) (r / n * 0.92f), (byte) (g / n * 0.92f), (byte) (b / n * 0.92f), 255);
                }
                if (_blurred == null || _blurred.width != w || _blurred.height != h)
                {
                    if (_blurred != null) UnityEngine.Object.DestroyImmediate(_blurred);
                    _blurred = new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                }
                _blurred.SetPixels32(soft);
                _blurred.Apply(false);
                _blurLoaded = _loaded;
                return _blurred;
            }

            public void Dispose()
            {
                if (_texture != null) UnityEngine.Object.DestroyImmediate(_texture);
                if (_blurred != null) UnityEngine.Object.DestroyImmediate(_blurred);
                _texture = _blurred = null;
            }
        }

        // 영상의 미리보기 프레임을 만든다(캐시가 있으면 그대로). 실패하면 null 과 이유.
        public static Frames BuildFrames(string video, out string error)
        {
            error = null;
            if (!BundledFfmpeg.Available) { error = "Tools~/ffmpeg.exe 가 없다"; return null; }
            if (!File.Exists(video)) { error = "영상이 없다: " + video; return null; }
            BundledFfmpeg.MediaInfo info = BundledFfmpeg.Probe(video);
            if (!info.HasVideo) { error = "영상을 읽지 못했다: " + video; return null; }

            string dir = Path.Combine(CacheRoot, Key(video, "frames"));
            int count = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jpg").Length : 0;
            if (count == 0)
            {
                Directory.CreateDirectory(dir);
                string fps = PreviewFps.ToString(CultureInfo.InvariantCulture);
                string err = BundledFfmpeg.Run($"-y -loglevel error -i \"{video}\" -an -vf \"fps={fps},scale={PreviewWidth}:-2\" -q:v 5 \"{Path.Combine(dir, "%05d.jpg")}\"");
                if (err.Length > 0) { error = err; return null; }
                count = Directory.GetFiles(dir, "*.jpg").Length;
            }
            return new Frames { Dir = dir, Count = count, Duration = info.Duration, Width = info.Width, Height = info.Height };
        }

        // ── 소리 ───────────────────────────────────────────────────────────────

        // 원본 소리 + 음소거 구간. 스템이 있으면 스템을 섞는다. 소리가 없으면 null.
        public static string BuildSourceAudio(string video, string bgmStem, string sfxStem, IList<EditList.Mute> mutes, float duration)
        {
            if (!BundledFfmpeg.Available) return null;
            var ci = CultureInfo.InvariantCulture;
            string muteKey = string.Join(";", mutes.Select(m => $"{m.Start.ToString(ci)},{m.End.ToString(ci)},{(int) m.Target}"));
            string stemKey = (bgmStem != null ? "b" : "") + (sfxStem != null ? "s" : "");
            string output = Path.Combine(CacheRoot, Key(video, "audio|" + stemKey + "|" + muteKey) + ".wav");
            if (File.Exists(output)) return output;

            var inputs = new List<string>();
            var f = new StringBuilder();
            var pads = new List<string>();
            if (bgmStem != null || sfxStem != null)
            {
                if (bgmStem != null) { inputs.Add($"-i \"{bgmStem}\""); f.Append($"[{inputs.Count - 1}:a]aformat=sample_rates=48000:channel_layouts=stereo{Mute(mutes, MuteTarget.Bgm, false)}[b];"); pads.Add("[b]"); }
                if (sfxStem != null) { inputs.Add($"-i \"{sfxStem}\""); f.Append($"[{inputs.Count - 1}:a]aformat=sample_rates=48000:channel_layouts=stereo{Mute(mutes, MuteTarget.Sfx, false)}[s];"); pads.Add("[s]"); }
            }
            else
            {
                if (!BundledFfmpeg.Probe(video).HasAudio) return null;
                inputs.Add($"-i \"{video}\"");
                f.Append($"[0:a]aformat=sample_rates=48000:channel_layouts=stereo{Mute(mutes, MuteTarget.All, true)}[v];");
                pads.Add("[v]");
            }
            f.Append(pads.Count == 2 ? "[b][s]amix=inputs=2:duration=longest:normalize=0[out]" : $"{pads[0]}anull[out]");
            Directory.CreateDirectory(CacheRoot);
            string err = BundledFfmpeg.Run($"-y -loglevel error {string.Join(" ", inputs)} -filter_complex \"{f}\" -map \"[out]\" -t {duration.ToString(ci)} -c:a pcm_s16le \"{output}\"");
            return err.Length == 0 && File.Exists(output) ? output : null;
        }

        // 삽입 클립 소리. Locale Capture 녹화를 음소거했으면 남긴 스템만, 아니면 클립 소리. 클립 안 음소거 구간은
        // 소리를 0 으로(BGM/SFX 만 끄는 구간은 스템이 있을 때만 따로, 없으면 전체). 소리가 없으면 null.
        public static string BuildClipAudio(string clip, bool muteBgm, bool muteSfx, IList<EditList.Mute> mutes = null)
        {
            if (!BundledFfmpeg.Available || !File.Exists(clip)) return null;
            mutes ??= new List<EditList.Mute>();
            var ci = CultureInfo.InvariantCulture;
            string muteKey = string.Join(";", mutes.Select(m => $"{m.Start.ToString(ci)},{m.End.ToString(ci)},{(int) m.Target}"));
            string output = Path.Combine(CacheRoot, Key(clip, $"clip|{muteBgm}|{muteSfx}|{muteKey}") + ".wav");
            if (File.Exists(output)) return output;
            BundledFfmpeg.MediaInfo info = BundledFfmpeg.Probe(clip);
            string dur = info.Duration.ToString(ci);
            const string Fmt48 = "aformat=sample_rates=48000:channel_layouts=stereo";
            bool partMute = mutes.Any(m => m.Target != MuteTarget.All);
            string args;
            if ((muteBgm || muteSfx || partMute) && EditList.ClipStems(clip, out string bgm, out string sfx))
            {
                var inputs = new List<string>();
                var f = new StringBuilder();
                if (bgm != null && !muteBgm) { inputs.Add($"-i \"{bgm}\""); f.Append($"[{inputs.Count - 1}:a]{Fmt48}{Mute(mutes, MuteTarget.Bgm, false)}[k{inputs.Count - 1}];"); }
                if (sfx != null && !muteSfx) { inputs.Add($"-i \"{sfx}\""); f.Append($"[{inputs.Count - 1}:a]{Fmt48}{Mute(mutes, MuteTarget.Sfx, false)}[k{inputs.Count - 1}];"); }
                if (inputs.Count == 0) return null;
                f.Append(inputs.Count == 1 ? "[k0]anull[out]" : "[k0][k1]amix=inputs=2:duration=longest:normalize=0[out]");
                args = $"-y -loglevel error {string.Join(" ", inputs)} -filter_complex \"{f}\" -map \"[out]\" -t {dur} -ac 2 -ar 48000 -c:a pcm_s16le \"{output}\"";
            }
            else
            {
                if (!info.HasAudio) return null;
                args = $"-y -loglevel error -i \"{clip}\" -vn -af \"{Fmt48}{Mute(mutes, MuteTarget.All, true)}\" -ac 2 -ar 48000 -c:a pcm_s16le \"{output}\"";
            }
            Directory.CreateDirectory(CacheRoot);
            string err = BundledFfmpeg.Run(args);
            return err.Length == 0 && File.Exists(output) ? output : null;
        }

        // 배속 구간 소리: 원본 소리 WAV 의 [start, end] 를 잘라 rate 배로(음높이 유지). 미리듣기는 WAV 를 원래 속도로만
        // 틀 수 있어서 구간마다 따로 만든다. 같은 구간·배속이면 캐시를 쓴다.
        public static string BuildSpeedAudio(string sourceWav, float start, float end, float rate)
        {
            if (!BundledFfmpeg.Available || string.IsNullOrEmpty(sourceWav) || !File.Exists(sourceWav) || end - start < 0.01f || rate <= 0) return null;
            var ci = CultureInfo.InvariantCulture;
            string output = Path.Combine(CacheRoot, Key(sourceWav, $"speed|{start.ToString(ci)}|{end.ToString(ci)}|{rate.ToString(ci)}") + ".wav");
            if (File.Exists(output)) return output;
            Directory.CreateDirectory(CacheRoot);
            string err = BundledFfmpeg.Run($"-y -loglevel error -i \"{sourceWav}\" -af \"atrim=start={start.ToString(ci)}:end={end.ToString(ci)},asetpts=PTS-STARTPTS{VideoEditExporter.Atempo(rate)}\" -c:a pcm_s16le \"{output}\"");
            return err.Length == 0 && File.Exists(output) ? output : null;
        }

        private static string Mute(IList<EditList.Mute> mutes, MuteTarget target, bool all)
        {
            var ci = CultureInfo.InvariantCulture;
            string enable = string.Join("+", mutes.Where(m => all || m.Target == MuteTarget.All || m.Target == target)
                .Select(m => $"between(t,{Mathf.Min(m.Start, m.End).ToString(ci)},{Mathf.Max(m.Start, m.End).ToString(ci)})"));
            return enable.Length == 0 ? "" : $",volume=0:enable='{enable}'";
        }

        // ── 미리듣기 ───────────────────────────────────────────────────────────
        // Unity 오디오(출력 장치·게임뷰 음소거·엔진 상태)에 휘둘리지 않게 Windows MCI 로 WAV 를 직접 튼다.
        // 여러 개를 동시에 틀 수 있다(본 소리 + BGM 교체 음원).

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

        private static readonly List<string> _playing = new List<string>();
        private static int _next;

        private static int Mci(string command) => mciSendString(command, null, 0, IntPtr.Zero);

        public static void PlaySound(string wav, float time)
        {
            StopSound();
            if (string.IsNullOrEmpty(wav) || !File.Exists(wav) || !SoundAvailable) return;
            string alias = "lcpreview" + ++_next;
            if (Mci($"open \"{wav}\" type waveaudio alias {alias}") != 0) return;
            Mci($"set {alias} time format milliseconds");
            var length = new StringBuilder(32);
            mciSendString($"status {alias} length", length, length.Capacity, IntPtr.Zero);
            int.TryParse(length.ToString(), out int lengthMs);
            int from = Mathf.Max(0, Mathf.RoundToInt(time * 1000));
            if (lengthMs > 0 && from >= lengthMs) { Mci($"close {alias}"); return; }
            Mci($"play {alias} from {from}");
            _playing.Add(alias);
        }

        public static void StopSound()
        {
            foreach (string alias in _playing) Mci($"close {alias}");
            _playing.Clear();
        }

        // 미리듣기는 Windows 에디터에서만.
        public static bool SoundAvailable => Application.platform == RuntimePlatform.WindowsEditor;
    }
}
