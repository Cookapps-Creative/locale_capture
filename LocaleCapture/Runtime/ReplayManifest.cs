using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 한 번의 플레이 기록. 입력 트레이스 파일과 나란히 JSON 으로 저장된다.
    [Serializable]
    public sealed class ReplayManifest
    {
        [Serializable]
        public struct Marker
        {
            public int Frame;
            public string Name;
        }

        // 기록 형식 판(0 = 이전 기록).
        //  1 부터: 누른 대상의 입력 처리기, 입력 받는 UI 타임라인, new Random() 생성 기록이 있다.
        //  2 부터: HttpClient 응답을 세션 Update 에서 넘기고 그 프레임을 지연으로 적는다(재생도 같은 자리에서 넘긴다).
        //  3 부터: 작업 스레드에서 메인 스레드로 넘어오는 이어받기를 붙잡아 넘긴 프레임을 남긴다(ThreadPosts).
        //  4 부터: Addressables 에셋 데이터베이스 모드의 가짜 로딩 지연(Simulated Load Delay) 없이 기록한다.
        //  5 부터: Addressables 의 연쇄 로딩(콜백에서 다음 로드)을 한 프레임에 끝내며 기록한다.
        //  6 부터: 기록 시작 때 실제 장치마다 가상 장치를 미리 만들고 그 목록을 남긴다(Devices). 재생도 같은 장치를 같은 순서로 만든다.
        //  7 부터: 스크롤 목록 안의 입력 받는 UI 를 목록별로 따로 세고 줄 크기를 남긴다(HitTrace.Event 의 Scroll·Shape·Size).
        //          목록의 내용·보이는 영역 크기가 바뀐 프레임도 남긴다(ListEvents).
        //  8 부터: 실시간 통신(Photon)을 기록한다(realtime.tape.json — 쓰지 않은 녹화면 빈 목록). 재생은 실제 서버에 접속하지 않는다.
        //          이전 판의 녹화는 그 기록이 없어 실제 통신을 그대로 두고, 접속했으면 결과에 알린다.
        public const int CurrentTraceVersion = 8;
        public int TraceVersion;
        public int SeedScheme;   // new Random() 시드 방식(CaptureClock). 재생은 기록 때 방식을 따른다

        public string Name;
        public string CreatedAt;
        public int Fps;
        public int Seed;
        public long ClockBaseUtcTicks;   // 기록 시작 시각 — 재생 때 프로젝트 코드가 보는 시계의 기준
        public int BaseTickCount;
        public string RecordedLocale;
        public int Width, Height;
        public Rect SafeArea;                            // 기록 때 화면 안전 영역·DPI·노치 — 재생에 그대로 준다(CaptureScreen)
        public float Dpi;
        public List<Rect> Cutouts = new List<Rect>();
        public int Frames;
        public int VideoStartFrame;   // 사용자가 START 를 누른 프레임 — 언어별 영상은 여기부터 남긴다(재생은 부팅부터)
        // 프레임 i 가 끝난 시점의 누적 입력 이벤트 수. 프레임 i 의 이벤트 = [EventCounts[i-1], EventCounts[i]).
        public List<int> EventCounts = new List<int>();
        public List<Marker> Markers = new List<Marker>();
        // 끝난 Addressables 로딩의 프레임과 이름(진단용 — 재생과 맞추는 데는 Markers 의 개수를 쓴다).
        public List<Marker> Loads = new List<Marker>();
        // 프레임 끝의 UnityEngine.Random 상태(프레임마다 정수 4개). 어긋남 검사와 로딩 대기 뒤 복원에 쓴다.
        public List<int> RandomStates = new List<int>();
        // 게임 스크립트의 UnityEngine.Random 호출(프레임·호출자). 어긋남이 났을 때 누가 난수를 썼는지 비교한다.
        public List<RandomTrace.Call> RandomCalls = new List<RandomTrace.Call>();
        // 누를 때마다 UI 가 맞춘 대상. 재생과 비교해 같은 입력이 다른 곳에 들어간 지점을 찾는다.
        public List<TapProbe.Tap> Taps = new List<TapProbe.Tap>();
        // UI 화면(최상위 Canvas)이 열리고 닫힌 프레임. 재생과 비교해 팝업이 늦게·일찍 뜬 곳을 찾는다.
        public List<UiTrace.Event> UiEvents = new List<UiTrace.Event>();
        // 입력을 받을 수 있는 UI(버튼·카드 등 처리기)가 켜지고 꺼진 프레임. 재생과 비교해 흐름이 밀린 첫 프레임을 찾는다.
        public List<HitTrace.Event> HitEvents = new List<HitTrace.Event>();
        // 스크롤 목록의 내용·보이는 영역 크기가 바뀐 프레임. 다른 언어 패스에서 목록 안이 녹화와 달라졌을 때 줄 크기가 다른 화면인지 가리는 근거다.
        public List<HitTrace.ListState> ListEvents = new List<HitTrace.ListState>();
        public bool HitFailed;
        public int HitFailedFrame;   // (HitFailed) 기록 중 입력 받는 UI 추적이 꺼진 프레임(안 꺼졌으면 -1) — 그 뒤는 HitEvents 가 비어 있어 비교하지 않는다
        // new Random() 이 만들어진 프레임과 만든 코드. 덱 셔플처럼 난수를 새로 만드는 지점이 같은 프레임이었는지 본다.
        public List<CaptureClock.RandomCreation> RandomCreations = new List<CaptureClock.RandomCreation>();
        // 작업 스레드에서 메인 스레드로 넘어온 이어받기(await Task.Run 등)를 넘긴 프레임과 그 코드. 재생은 같은 프레임에 넘긴다.
        public List<ThreadGate.Post> ThreadPosts = new List<ThreadGate.Post>();
        public List<int> FrameMs = new List<int>();   // 프레임마다 실제로 걸린 ms(끊김 진단)
        // 기록 중 인스펙터에서 바꾼 켜기/끄기. 재생 때 같은 프레임에 같은 값으로 둔다.
        public List<InspectorToggles.Toggle> Toggles = new List<InspectorToggles.Toggle>();
        [Serializable]
        public struct Device
        {
            public int Frame;       // 만든 세션 프레임(0 = 세션 시작)
            public int Id;          // 입력 기록 안의 장치 번호
            public string Layout;
        }

        // 기록 때 게임이 본 가상 입력 장치(만든 순서). 재생은 같은 장치를 같은 순서·같은 때에 만든다.
        // 장치가 추가되면 Input System 이 액션 바인딩을 다시 풀고, 그때 이미 움직였거나 눌려 있는 컨트롤이 한 번 더 발사된다 —
        // 그 프레임에 들어온 다른 입력과 섞여 UI 가 엉뚱한 쪽을 고른다(마우스를 움직인 뒤의 첫 터치가 버려졌다).
        public List<Device> Devices = new List<Device>();
        // 세션 프레임 0 이 끝난 때의 엔진 프레임 번호와 엔진 시간. 실행마다 같아야 게임이 보는 Time.time·Time.frameCount 가 같다.
        public int EngineFrame;
        public double EngineTime;
        public string TraceFile = "input.trace";
        public const string StateFolder = "state";   // 기록 시작 때의 로컬 저장(persistentDataPath·PlayerPrefs)

        public static string ManifestFile => "manifest.json";

        public void Save(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, ManifestFile), JsonUtility.ToJson(this, true));
        }

        public static ReplayManifest Load(string directory)
        {
            string path = Path.Combine(directory, ManifestFile);
            if (!File.Exists(path)) return null;
            return JsonUtility.FromJson<ReplayManifest>(File.ReadAllText(path));
        }
    }
}
