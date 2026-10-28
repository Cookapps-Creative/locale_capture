using System;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 에디터가 플레이 모드에 들어가기 전에 무엇을 할지(기록·언어별 재생) 적어 두면, 첫 씬이 뜨기 전에 세션을 시작한다.
    // 그래서 기록과 재생이 모두 게임 부팅 첫 프레임부터 같은 조건으로 돈다.
    [Serializable]
    public sealed class SessionRequest
    {
        public enum Kind { Record, Video, BgmStem, SfxStem }

        public Kind Type;
        public string Directory;
        public string Locale;                 // 재생 언어 코드(예: ko-KR). 기록이면 비어 있다
        public string Stem;                   // 영상 패스가 함께 뽑을 스템(bgm·sfx). 그 패스 영상의 소리는 이 스템만 남는다(끝나고 다시 섞는다)
        public bool OverrideSystemLanguage;
        public SystemLanguage SystemLanguage;  // 재생 때 프로젝트 코드가 보는 기기 언어
        public string[] LiveMarkers;           // 이 문자열이 든 서버 요청은 테이프 대신 실제 서버로(언어 데이터 등)
        public CaptureSettings Settings;
        public int Fps;
        public int Seed;
        public long ClockBaseUtcTicks;
        public int BaseTickCount;
    }

    public struct SessionResult
    {
        public SessionRequest Request;
        public bool Completed;
        public string Error;
    }

    public static class CaptureBoot
    {
        public static SessionRequest Pending;
        public static Func<SessionRequest, IFrameSink> VideoSinkFactory;
        public static Func<string, IFrameSink> WavSinkFactory;
        public static event Action<SessionResult> Ended;

        internal static void RaiseEnded(SessionResult result) => Ended?.Invoke(result);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Start()
        {
            SessionRequest request = Pending;
            Pending = null;
            if (request != null) ReplaySession.Create(request);
        }
    }
}
