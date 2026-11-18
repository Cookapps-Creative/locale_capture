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
        public string RecordedLocale;          // 재생: 녹화 언어. 언어 표기만 다른 요청을 녹화의 같은 요청으로 맞춘다
        public string[] RecordedLocaleForms;   // 재생: 녹화 언어의 여러 표기(ko-KR·ko_KR·Korean·ko 등)
        public bool Realtime;                  // 재생: 실제 1/fps 초 간격으로 묶는다(끄면 기기가 낼 수 있는 만큼 빨리)
        public bool NoLoadDelay;               // Addressables 에셋 데이터베이스 모드의 가짜 로딩 지연 없이 돈다(기록 형식 4 부터)
        public bool CollapseLoads;             // Addressables 의 연쇄 로딩을 한 프레임에 끝낸다(기록 형식 5 부터)
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
        // 에디터의 Addressables(Use Asset Database) 는 로드마다 가짜 지연(Simulated Load Delay, 기본 0.1초)을 넣고 "지금 + 지연" 시각에 끝낸다.
        // 실제 빌드에는 없는 지연이고, 시계에 묶여 있어 로딩 대기와 얽히면 로드가 안 끝나거나 일찍 끝난다. 세션 동안에는 지연 없이 바로 끝낸다.
        // 에디터가 패스를 걸 때 켜고(Arm), 작업이 끝나면 끈다. 에셋 제공자가 만들어질 때 에디터 패치가 이 값을 본다.
        public static bool NoLoadDelay;
        // Addressables 는 완료 콜백 안에서 끝난 작업의 콜백을 다음 프레임으로 미룬다. 그래서 "로드 → 콜백에서 다음 로드" 로 이어지는 연쇄는
        // 단계마다 한 프레임씩 걸리고, 단계 수는 언어마다(언어 테이블·글꼴 수) 다르다 — 그 뒤 흐름이 언어에 따라 한두 프레임 달라진다.
        // 세션 동안에는 미뤄진 콜백을 같은 프레임에 이어서 불러, 바로 끝나는 로드의 연쇄가 단계 수와 상관없이 한 프레임에 끝나게 한다.
        public static bool CollapseLoads;
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
