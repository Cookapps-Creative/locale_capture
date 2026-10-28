using System;

namespace CookApps.LocaleCapture
{
    public enum VideoFormat { MP4, WebM, MOV }
    public enum VideoQuality { Low, Medium, High, Custom }
    public enum OutputResolution { MatchGameView, P720, P1080, P1440, P2160, Custom }

    // Unity Recorder 의 Movie 옵션에 맞춘 녹화 설정. 창이 채우고 세션·싱크가 읽는다.
    [Serializable]
    public struct CaptureSettings
    {
        public VideoFormat Format;
        public VideoQuality Quality;
        public int CustomBitrateMbps;
        public OutputResolution Resolution;
        public int CustomWidth, CustomHeight;
        public int FrameRate;
        public bool Audio;        // Bgm || Sfx
        public bool Bgm, Sfx;
        public bool Flip;
        public bool StartOnPlay;   // Play 하자마자 영상 시작(START 를 누르지 않는다)
        // ffmpeg 에 GPU 인코더(NVENC/AMF/QSV)가 있으면 쓴다. MP4·MOV 만.
        public bool HardwareEncoder;
        // 출력 경로 앞부분. 싱크가 "_<로케일>.<확장자>" 를 붙인다.
        public string OutputBase;
        // 어댑터 Locales 중 녹화할 인덱스. 비면 전부.
        public int[] SelectedLocales;

        public string Extension
        {
            get
            {
                switch (Format)
                {
                    case VideoFormat.WebM: return "webm";
                    case VideoFormat.MOV: return "mov";
                    default: return "mp4";
                }
            }
        }

        public static CaptureSettings Default => new CaptureSettings
        {
            Format = VideoFormat.MP4, Quality = VideoQuality.High, CustomBitrateMbps = 8,
            Resolution = OutputResolution.MatchGameView, CustomWidth = 1080, CustomHeight = 1920,
            FrameRate = 30, Audio = true, Bgm = true, Sfx = true, HardwareEncoder = true,
        };

        // 게임뷰 크기에서 출력 크기를 정한다. 프리셋은 세로 길이를 맞추고 가로는 비율대로, 짝수로.
        public void ResolveSize(int gameWidth, int gameHeight, out int width, out int height)
        {
            int shortSide = 0;
            switch (Resolution)
            {
                case OutputResolution.P720: shortSide = 720; break;
                case OutputResolution.P1080: shortSide = 1080; break;
                case OutputResolution.P1440: shortSide = 1440; break;
                case OutputResolution.P2160: shortSide = 2160; break;
            }

            if (Resolution == OutputResolution.Custom)
            {
                width = CustomWidth;
                height = CustomHeight;
                // 기록 화면(예: Device Simulator 기기) 비율이 다르면 늘이지 않는다 — 가로를 맞추고 세로는 기록 비율대로(비율 맞춤은 편집 Export 의 Blur)
                if (gameWidth > 0 && gameHeight > 0 && Math.Abs((float) width / height - (float) gameWidth / gameHeight) > 0.01f)
                    height = (int) Math.Round(width * (double) gameHeight / gameWidth);
            }
            else if (shortSide == 0) { width = gameWidth; height = gameHeight; }
            else
            {
                // 짧은 변을 프리셋 길이로, 긴 변은 게임뷰 비율대로.
                float aspect = (float) gameWidth / gameHeight;
                if (gameHeight >= gameWidth) { width = shortSide; height = (int) Math.Round(shortSide / aspect); }
                else { height = shortSide; width = (int) Math.Round(shortSide * aspect); }
            }
            width &= ~1;
            height &= ~1;
        }
    }
}
