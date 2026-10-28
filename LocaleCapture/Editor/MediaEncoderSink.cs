using System.Collections.Generic;
using Unity.Collections;
using UnityEditor.Media;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // Unity 내장 MediaEncoder. 외부 의존이 없지만 메인 스레드에서 동기 인코딩한다. MP4(H.264)·WebM(VP8)만 된다.
    public sealed class MediaEncoderSink : IFrameSink
    {
        private readonly CaptureSettings _settings;
        private FrameFormat _format;
        private MediaEncoder[] _encoders;
        private NativeArray<byte> _flipped;
        private byte[] _scratch;
        private readonly FramePool _pool = new FramePool();

        public static bool Supports(VideoFormat format) => format == VideoFormat.MP4 || format == VideoFormat.WebM;

        public MediaEncoderSink(CaptureSettings settings) => _settings = settings;

        public void Begin(FrameFormat format, IReadOnlyList<string> locales)
        {
            _format = format;
            if (format.Width % 2 != 0 || format.Height % 2 != 0)
                Debug.LogWarning($"[LocaleCapture] 홀수 해상도 {format.Width}x{format.Height}, 인코더가 거부할 수 있다");

            VideoTrackEncoderAttributes video = _settings.Format == VideoFormat.WebM
                ? new VideoTrackEncoderAttributes(new VP8EncoderAttributes { keyframeDistance = 25 })
                : new VideoTrackEncoderAttributes(new H264EncoderAttributes
                {
                    gopSize = 25, numConsecutiveBFrames = 2, profile = UnityEditor.VideoEncodingProfile.H264High,
                });
            video.frameRate = new MediaRational(format.FrameRate);
            video.width = (uint) format.Width;
            video.height = (uint) format.Height;
            video.includeAlpha = false;
            video.bitRateMode = BitrateMode(_settings.Quality);
            if (_settings.Quality == VideoQuality.Custom) video.targetBitRate = (uint) Mathf.Max(1, _settings.CustomBitrateMbps) * 1000000u;

            var audio = new AudioTrackAttributes
            {
                sampleRate = new MediaRational(format.SampleRate),
                channelCount = (ushort) format.Channels,
                language = "",
            };
            string ext = _settings.Extension;
            _encoders = new MediaEncoder[locales.Count];
            for (int i = 0; i < locales.Count; i++)
            {
                string path = $"{format.OutputBase}_{locales[i]}.{ext}";
                _encoders[i] = format.Channels > 0 ? new MediaEncoder(path, video, audio) : new MediaEncoder(path, video);
            }
        }

        public NativeArray<byte> Rent(int length) => _pool.Rent(length);
        public void Return(NativeArray<byte> buffer) => _pool.Return(buffer);

        public void AddVideoFrame(int localeIndex, NativeArray<byte> rgba)
        {
            try { Encode(localeIndex, rgba); }
            finally { _pool.Return(rgba); }
        }

        private void Encode(int localeIndex, NativeArray<byte> rgba)
        {
            NativeArray<byte> data = rgba;
            if (_settings.Flip)
            {
                if (!_flipped.IsCreated) _flipped = new NativeArray<byte>(rgba.Length, Allocator.Persistent);
                if (_scratch == null) _scratch = new byte[rgba.Length];
                FrameFlip.CopyTo(rgba, _scratch, _format.Width, _format.Height, true);
                _flipped.CopyFrom(_scratch);
                data = _flipped;
            }
            TextureFormat fmt = _format.Bgra ? TextureFormat.BGRA32 : TextureFormat.RGBA32;
            _encoders[localeIndex].AddFrame(_format.Width, _format.Height, _format.Width * 4, fmt, data);
        }

        public void AddAudio(NativeArray<float> interleaved)
        {
            foreach (MediaEncoder e in _encoders) e.AddSamples(interleaved);
        }

        public void End()
        {
            foreach (MediaEncoder e in _encoders) e.Dispose();
            if (_flipped.IsCreated) _flipped.Dispose();
            _pool.DisposeAll();
            Debug.Log($"[LocaleCapture] 출력 {_format.OutputBase}_<locale>.{_settings.Extension}");
        }

        private static UnityEditor.VideoBitrateMode BitrateMode(VideoQuality q)
        {
            switch (q)
            {
                case VideoQuality.Low: return UnityEditor.VideoBitrateMode.Low;
                case VideoQuality.Medium: return UnityEditor.VideoBitrateMode.Medium;
                default: return UnityEditor.VideoBitrateMode.High;
            }
        }
    }
}
