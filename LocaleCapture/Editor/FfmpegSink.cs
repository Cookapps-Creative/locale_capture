using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Debug = UnityEngine.Debug;

namespace CookApps.LocaleCapture.Editor
{
    // 동봉 ffmpeg 로 인코딩한다. 프레임은 큐에 넣고 쓰기 스레드가 ffmpeg 표준 입력으로 흘려 보내,
    // 메인 스레드(게임)는 인코딩을 기다리지 않는다. GPU 인코더(NVENC·AMF·QSV)가 되면 그걸, 안 되면 x264 를 쓴다.
    // ffmpeg 표준 입력은 하나라서 소리는 녹화 동안 WAV 로 따로 쓰고, 끝날 때 영상과 합친다(영상은 다시 인코딩하지 않는다).
    public sealed class FfmpegSink : IFrameSink
    {
        private const int QueueDepth = 8;   // 이만큼 쌓이면 메인 스레드가 기다린다(1080x1920 이면 약 64MB)

        private sealed class Target
        {
            public string Locale, Video, Audio, Final;
            public Process Process;
            public Stream Stdin;
            public BlockingCollection<NativeArray<byte>> Queue;
            public Thread Writer;
            public readonly StringBuilder Stderr = new StringBuilder();
            public WavSink Wav;
            public byte[] Scratch;
            public volatile string Error;
        }

        private readonly CaptureSettings _settings;
        private readonly FramePool _pool = new FramePool();
        private FrameFormat _format;
        private Target[] _targets;
        private string _encoder;

        public FfmpegSink(CaptureSettings settings) => _settings = settings;

        public void Begin(FrameFormat format, IReadOnlyList<string> locales)
        {
            _format = format;
            if (format.Width % 2 != 0 || format.Height % 2 != 0)
                Debug.LogWarning($"[LocaleCapture] 홀수 해상도 {format.Width}x{format.Height}, 인코더가 거부할 수 있다");
            _encoder = _settings.Format == VideoFormat.WebM ? "libvpx-vp9" : H264Encoder();
            string ext = _settings.Extension;
            _targets = new Target[locales.Count];
            for (int i = 0; i < locales.Count; i++)
            {
                var t = new Target
                {
                    Locale = locales[i],
                    Final = $"{format.OutputBase}_{locales[i]}.{ext}",
                    Video = $"{format.OutputBase}_{locales[i]}.video.{ext}",
                    Audio = $"{format.OutputBase}_{locales[i]}.audio.wav",
                    Queue = new BlockingCollection<NativeArray<byte>>(QueueDepth),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(t.Final));
                if (format.Channels > 0) { t.Wav = new WavSink(t.Audio); t.Wav.Begin(format, locales); }
                Start(t);
                _targets[i] = t;
            }
        }

        private void Start(Target t)
        {
            // 입력: 8비트 4채널 원시 프레임. GPU 원본(아래→위)이라 뒤집는다. Flip 설정은 반대로.
            string pix = _format.Bgra ? "bgra" : "rgba";
            string flip = _settings.Flip ? "" : "-vf vflip ";
            string args = $"-hide_banner -loglevel error -y -f rawvideo -pix_fmt {pix} -s {_format.Width}x{_format.Height} -r {_format.FrameRate} -i pipe:0 " +
                          $"{flip}-an {EncoderArgs(_encoder, _settings)} -pix_fmt yuv420p \"{t.Video}\"";
            t.Process = Process.Start(new ProcessStartInfo(BundledFfmpeg.Path, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true,
            });
            t.Process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (t.Stderr) t.Stderr.AppendLine(e.Data); };
            t.Process.BeginErrorReadLine();
            t.Stdin = t.Process.StandardInput.BaseStream;
            t.Writer = new Thread(() => WriteLoop(t)) { IsBackground = true, Name = "LocaleCapture.Ffmpeg." + t.Locale };
            t.Writer.Start();
        }

        private unsafe void WriteLoop(Target t)
        {
            foreach (NativeArray<byte> frame in t.Queue.GetConsumingEnumerable())
            {
                try
                {
                    if (t.Error == null)
                    {
                        int length = frame.Length;
                        if (t.Scratch == null || t.Scratch.Length < length) t.Scratch = new byte[length];
                        fixed (byte* dst = t.Scratch)
                            UnsafeUtility.MemCpy(dst, NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(frame), length);
                        t.Stdin.Write(t.Scratch, 0, length);
                    }
                }
                catch (Exception e) { t.Error ??= e.Message; }   // 파이프가 끊겼다(ffmpeg 가 죽음) — 나머지 프레임은 버린다
                finally { _pool.Return(frame); }
            }
        }

        public NativeArray<byte> Rent(int length) => _pool.Rent(length);
        public void Return(NativeArray<byte> buffer) => _pool.Return(buffer);

        public void AddVideoFrame(int localeIndex, NativeArray<byte> rgba)
        {
            Target t = _targets[localeIndex];
            if (t.Error != null || t.Queue.IsAddingCompleted) { _pool.Return(rgba); return; }
            t.Queue.Add(rgba);   // 큐가 가득 차면 여기서 기다린다 — 인코더가 못 따라오는 만큼만 게임이 느려진다
        }

        public void AddAudio(NativeArray<float> interleaved)
        {
            foreach (Target t in _targets) t.Wav?.AddAudio(interleaved);
        }

        public void End()
        {
            foreach (Target t in _targets)
            {
                t.Queue.CompleteAdding();
                t.Writer.Join();
                try { t.Stdin.Close(); } catch { /* 이미 끊김 */ }
                if (!t.Process.WaitForExit(120000)) { try { t.Process.Kill(); } catch { /* 이미 끝남 */ } t.Error ??= "인코더가 끝나지 않아 멈췄다"; }
                t.Process.WaitForExit();   // stderr 를 다 읽는다
                if (t.Process.ExitCode != 0) t.Error ??= $"ffmpeg 종료 코드 {t.Process.ExitCode}";
                t.Process.Dispose();
                t.Wav?.End();
                Finish(t);
            }
            _pool.DisposeAll();
            Debug.Log($"[LocaleCapture] 출력 {_format.OutputBase}_<locale>.{_settings.Extension} ({_encoder})");
        }

        // 영상과 소리를 하나로 합친다(영상은 복사). 소리가 없으면 이름만 바꾼다.
        private void Finish(Target t)
        {
            string stderr;
            lock (t.Stderr) stderr = t.Stderr.ToString().Trim();
            if (t.Error != null || !File.Exists(t.Video))
            {
                Debug.LogError($"[LocaleCapture] {t.Locale} 영상 인코딩 실패: {t.Error}\n{Tail(stderr)}");
                return;
            }
            if (!string.IsNullOrEmpty(stderr)) Debug.LogWarning($"[LocaleCapture] ffmpeg({t.Locale}): {Tail(stderr)}");
            if (File.Exists(t.Final)) File.Delete(t.Final);
            if (t.Wav == null || !File.Exists(t.Audio))
            {
                File.Move(t.Video, t.Final);
                return;
            }
            string audio = _settings.Format == VideoFormat.WebM ? "-c:a libopus -b:a 160k" : "-c:a aac -b:a 192k -movflags +faststart";
            string error = BundledFfmpeg.Run($"-hide_banner -loglevel error -y -i \"{t.Video}\" -i \"{t.Audio}\" -map 0:v -map 1:a -c:v copy {audio} -shortest \"{t.Final}\"");
            if (!string.IsNullOrEmpty(error) || !File.Exists(t.Final))
            {
                Debug.LogError($"[LocaleCapture] {t.Locale} 영상·소리 합치기 실패(영상만 남긴다): {error}");
                if (File.Exists(t.Final)) File.Delete(t.Final);
                File.Move(t.Video, t.Final);
                return;
            }
            File.Delete(t.Video);
            File.Delete(t.Audio);
        }

        private static string Tail(string text)
        {
            string[] lines = text.Split('\n');
            return lines.Length <= 5 ? text : string.Join("\n", lines, lines.Length - 5, 5);
        }

        // ── 인코더 ─────────────────────────────────────────────────────────────

        private static string _h264;   // 이 에디터 세션에서 쓸 H.264 인코더(한 번 고른다)

        // GPU 인코더가 실제로 도는지 작은 입력으로 시험한다(목록에 있어도 드라이버·장치가 없으면 실패한다).
        public static string H264Encoder()
        {
            if (_h264 != null) return _h264;
            foreach (string encoder in new[] { "h264_nvenc", "h264_amf", "h264_qsv" })
                if (Works(encoder)) { Debug.Log($"[LocaleCapture] GPU 인코더 {encoder} 사용"); return _h264 = encoder; }
            return _h264 = "libx264";
        }

        private static bool Works(string encoder) =>
            BundledFfmpeg.Available &&
            BundledFfmpeg.Run($"-hide_banner -loglevel error -f lavfi -i color=black:s=256x256:d=0.2:r=30 -frames:v 3 -c:v {encoder} -f null -") == "";

        // 녹화 설정의 품질을 인코더별 인자로. Low·Medium·High 는 화질 고정(CRF/CQ), Custom 은 비트레이트.
        private static string EncoderArgs(string encoder, CaptureSettings s)
        {
            int q = s.Quality == VideoQuality.Low ? 28 : s.Quality == VideoQuality.Medium ? 23 : 18;
            int mbps = Math.Max(1, s.CustomBitrateMbps);
            bool custom = s.Quality == VideoQuality.Custom;
            string rate = $"-b:v {mbps}M -maxrate {mbps}M -bufsize {mbps * 2}M";
            switch (encoder)
            {
                case "h264_nvenc": return $"-c:v h264_nvenc -preset p4 -tune hq -profile:v high " + (custom ? $"-rc vbr {rate}" : $"-rc vbr -cq {q} -b:v 0");
                case "h264_amf": return $"-c:v h264_amf -quality balanced -profile:v high " + (custom ? $"-rc vbr_peak {rate}" : $"-rc cqp -qp_i {q} -qp_p {q}");
                case "h264_qsv": return $"-c:v h264_qsv -preset medium -profile:v high " + (custom ? rate : $"-global_quality {q}");
                case "libvpx-vp9": return $"-c:v libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1 " + (custom ? $"-b:v {mbps}M" : $"-crf {q + 10} -b:v 0");
                default: return $"-c:v libx264 -preset veryfast -profile:v high " + (custom ? rate : $"-crf {q}");
            }
        }
    }
}
