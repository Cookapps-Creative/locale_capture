using System.Collections.Generic;
using Unity.Collections;

namespace CookApps.LocaleCapture
{
    public struct FrameFormat
    {
        public int Width;
        public int Height;
        public int FrameRate;
        public int SampleRate;
        // 0 이면 오디오 없음.
        public int Channels;
        // 출력 경로 앞부분. 싱크가 "_<로케일>.<확장자>" 를 붙인다.
        public string OutputBase;
        // 픽셀 채널 순서. false 면 RGBA.
        public bool Bgra;
    }

    // 프레임을 받아 파일로 쓰는 쪽. 세션은 캡처만 하고 인코딩은 모른다.
    public interface IFrameSink
    {
        void Begin(FrameFormat format, IReadOnlyList<string> locales);

        // 세션이 GPU 읽기 대상으로 쓸 배열을 빌린다. AddVideoFrame 또는 Return 으로 돌려받는다.
        NativeArray<byte> Rent(int length);
        void Return(NativeArray<byte> buffer);

        // 8비트 4채널(순서는 FrameFormat.Bgra), 행 순서는 GPU 원본. Rent 로 빌려준 배열이며 싱크가 다 쓰면 풀로 되돌린다.
        void AddVideoFrame(int localeIndex, NativeArray<byte> rgba);

        // 인터리브 float 샘플. 모든 로케일 영상에 같은 오디오가 들어간다.
        void AddAudio(NativeArray<float> interleaved);

        void End();
    }
}
