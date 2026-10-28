using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;

namespace CookApps.LocaleCapture.Editor
{
    // 소리만 PCM16 WAV 로 쓴다. 영상 프레임은 버린다 — BGM·SFX 스템 패스용.
    public sealed class WavSink : IFrameSink
    {
        private readonly string _path;
        private readonly FramePool _pool = new FramePool();
        private FileStream _stream;
        private BinaryWriter _writer;
        private int _sampleRate, _channels;
        private long _dataBytes;
        private byte[] _buffer = new byte[0];

        public WavSink(string path) => _path = path;

        public void Begin(FrameFormat format, IReadOnlyList<string> locales)
        {
            _sampleRate = format.SampleRate;
            _channels = Math.Max(1, format.Channels);
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _stream = new FileStream(_path, FileMode.Create, FileAccess.Write);
            _writer = new BinaryWriter(_stream);
            _writer.Write(new byte[44]);
        }

        public NativeArray<byte> Rent(int length) => _pool.Rent(length);
        public void Return(NativeArray<byte> buffer) => _pool.Return(buffer);
        public void AddVideoFrame(int localeIndex, NativeArray<byte> rgba) => _pool.Return(rgba);

        public void AddAudio(NativeArray<float> interleaved)
        {
            int bytes = interleaved.Length * 2;
            if (_buffer.Length < bytes) _buffer = new byte[bytes];
            for (int i = 0; i < interleaved.Length; i++)
            {
                short s = (short) Math.Clamp(interleaved[i] * 32767f, short.MinValue, short.MaxValue);
                _buffer[i * 2] = (byte) s;
                _buffer[i * 2 + 1] = (byte) (s >> 8);
            }
            _stream.Write(_buffer, 0, bytes);
            _dataBytes += bytes;
        }

        public void End()
        {
            _stream.Seek(0, SeekOrigin.Begin);
            _writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            _writer.Write((int) (36 + _dataBytes));
            _writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            _writer.Write(Encoding.ASCII.GetBytes("fmt "));
            _writer.Write(16);
            _writer.Write((short) 1);
            _writer.Write((short) _channels);
            _writer.Write(_sampleRate);
            _writer.Write(_sampleRate * _channels * 2);
            _writer.Write((short) (_channels * 2));
            _writer.Write((short) 16);
            _writer.Write(Encoding.ASCII.GetBytes("data"));
            _writer.Write((int) _dataBytes);
            _writer.Dispose();
            _pool.DisposeAll();
            UnityEngine.Debug.Log($"[LocaleCapture] 스템 저장 {_path} ({_dataBytes / 2 / _channels / (float) _sampleRate:F1}s)");
        }
    }
}
