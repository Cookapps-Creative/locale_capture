# CookApps Locale Capture

한 번 플레이를 기록하면, 지원 언어마다 같은 플레이를 다시 재생하며 영상으로 녹화한다. 로비·전투·상점·가챠가 언어마다 같은 장면으로 나온다.
게임 코드를 하나도 참조하거나 고치지 않는다. 에디터 전용(빌드에 들어가지 않는다).

## 설치

1. `LocaleCapture_*.unitypackage` 를 가져온다(Assets → Import Package → Custom Package). `Assets/CookApps/LocaleCapture` 에 들어간다.
2. 컴파일 뒤 `Tools > Locale Capture` 를 열어 맨 위가 "점검 통과" 인지 본다.

게임 쪽에 넣을 코드나 패치는 없다.

## 사용

1. `Tools > Locale Capture` → 언어 체크, 해상도·품질, Output File 의 File Name·Path → **녹화 대기**.
2. Unity **Play** 를 직접 누른다. 대기를 누르면 플레이 화면이 Device Simulator 로 바뀌고, 언어별 재생은 자동으로 Game 뷰에서(기록 때 화면 크기·안전 영역 그대로), 끝나면 다시 Simulator 로 돌아온다. Resolution 이 Custom 이면 그 크기 그대로의 Simulator 기기(`LocaleCapture 1080x1920` 등, 노치 없음)를 `Devices/` 에 만들어 자동으로 고른다 — 영상이 출력 크기로 바로 나와 블러·늘림이 없다. Custom 이 아니면 Simulator 에서 고른 기기 그대로이고, 기기 비율이 출력 비율과 다르면 편집 Export 의 비율·Blur 로 맞춘다. 빌드 설정의 첫 씬(켜진 씬이 없으면 목록 첫 씬)부터 열리고, 부팅부터 기록된다.
3. 원하는 장면에서 **START** → 플레이 → **STOP**. START 부터 STOP 까지만 녹화된다. START 전에는 STOP 이 없고, START 전에 Play 를 끄면 취소된다. 녹화 중 인스펙터에서 오브젝트·컴포넌트 체크박스(켜기/끄기)를 바꾸면 그 순간이 기록돼 언어별 영상에도 같은 프레임에 반영된다(UI 숨기기 등). Input 의 **바로 녹화** 를 켜 두면 START 없이 Play 부터 녹화가 시작되고 STOP 만 누른다.
4. 언어마다 플레이 모드가 자동으로 다시 열리며 부팅부터 같은 플레이를 재생하고, START 이후만 녹화한다. BGM·SFX 스템은 앞 언어 패스들이 함께 뽑아(첫 언어 = SFX, 둘째 = BGM) 따로 패스를 돌지 않고, 끝나면 그 영상들의 소리를 다시 섞어 원래대로 둔다. 편집은 창 맨 아래 **Edit Videos** 로 연다.
   첫 입력 전 부팅 구간은 30fps 로 묶지 않고 빨리 돌린다. 패스는 언어 수만큼이고, 하나에 걸리는 시간은 대략 "녹화한 플레이 길이 + 몇 초" 다.
   런타임 패치 대상은 `Library/LocaleCapture/patch-targets.json` 에 캐시해, 코드가 다시 빌드되기 전까지 녹화 대기가 바로 끝난다.
5. 창 오른쪽 위 **⋮ → Re-render…** 는 이미 있는 기록을 지금 설정으로 다시 뽑는다. 다시 플레이하지 않고, 서버도 필요 없다.

결과: `<Path>/<File Name>_<시각>/<File Name>_<언어>.mp4`, 스템 `audio_bgm.wav`·`audio_sfx.wav`, 진단 `verify/*.json`.

## 언어마다 같은 플레이가 되는 방법

| 맞추는 것 | 방법 |
|---|---|
| 시작 상태 | 기록 시작 때 persistentDataPath·temporaryCachePath·PlayerPrefs 를 통째로 백업하고, 재생 직전마다 복원. |
| 서버 결과(가챠·보상·인벤토리) | 부팅부터 서버 응답(HttpClient·UnityWebRequest)을 지연 프레임까지 기록했다가 재생 때 그대로 돌려줌 |
| 기록보다 많은 요청 | 재생에서 같은 요청이 기록보다 많이 나가면 실제 서버로 보내지 않고 그 요청의 마지막 기록 응답으로 대신(연결 대기 화면·실제 계정 변경 방지). 기록에 아예 없는 요청만 실제 서버로 |
| 언어별 서버 데이터 | 요청에 언어 코드가 든 요청은 그 언어로 처음 뽑을 때만 실서버, 이후 언어별 테이프로 재생 |
| 시간 | 프로젝트 코드의 DateTime·TickCount, 모든 스크립트와 Assets 의 DLL(DOTween 등)의 realtimeSinceStartup·Stopwatch·unscaled 시간·소리 시계(dspTime)·Task.Delay(메인 스레드)를 "기록 시작 시각 + 프레임/fps" 로 바꿔치기. 기록·재생 모두 실제 1/fps 초 간격으로 돈다 |
| 난수 | 프로젝트 코드의 new Random()·Guid, BCL Random 생성자, UnityEngine.Random 시드 고정. new Random() 시드는 "프레임 번호 + 그 프레임의 몇 번째" 로 정하고, 에디터·다른 스레드가 만든 것은 세지 않는다. 소리 코드(호출 경로에 Audio·Sound 가 든 타입)가 쓰는 UnityEngine.Random 은 따로 둔 상태로 돌려, 소리 재생 개수 차이가 게임 판정 난수를 흔들지 않게 한다 |
| 화면 | 기록 때의 안전 영역·DPI·노치를 재생에 그대로 줌 — Device Simulator 로 기록하고 게임뷰에서 재생해도 UI 배치가 같다 |
| 입력 | Input System 은 이벤트 트레이스, 예전 Input 은 게임이 물은 값을 프레임별 기록. 기록 때도 실제 장치 입력을 가로채 가상 장치로 프레임 시작에 넣는다(재생과 같은 시점) — 손을 뗀 순간이 게임 Update 사이에 처리돼 "뗐다"를 놓치는 일이 녹화에만 생기지 않게 |
| 로딩 시간 차이 | 씬·Addressables·비동기 작업 완료를 기록해, 재생 때 같은 로딩이 끝날 때까지 게임 시간을 멈춤(씬은 실제로 읽는 중일 때만. 기다린 프레임의 난수 소비는 되돌림). 씬을 일찍 다 읽으면 기록 때 열린 프레임까지 활성화를 미룸 |
| 언어 전환 | 저장 값 안의 녹화 언어 표기(ko-KR·Korean 등)를 재생 언어로 바꾸고, 기기 언어도 그 언어로. 그래도 다르면 부팅이 끝난 뒤(START 이후) Unity Localization·I2 선택 언어를 바꿈 |
| BGM·SFX 분리 | 반복 재생하는 10초 이상 소리를 BGM 으로 보고 패스마다 끔 |

바꿔치기는 녹화·재생 동안에만 Harmony(동봉, MIT)로 건다. 시간을 프로젝트 코드에서만 바꾸는 건 패키지의 네트워크 서명처럼 실제 시각이 필요한 곳을 건드리지 않기 위해서다.

## 일치 검사

작업이 끝나면 창 맨 위(점검 줄 자리)에 "최근 녹화 성공/실패" 가 나오고(실패 = 중단됐거나 녹화와 다르게 재생된 곳이 있음), 자세한 결과는 콘솔에 남는다. 판정 기준은 — **모든 언어 영상과 BGM·SFX 가 같은 플레이였는지**(패스마다 재생 내내의 난수 상태를 통째로 비교).
"다름" 이면 같은 플레이끼리 묶어 보여 준다.
녹화 때 누른 곳과 재생 때 누른 곳(그 자리에서 UI 가 맞춘 대상)이 다르면, 언어끼리 같아도 "다름 — 녹화와 다른 곳이 눌림" 으로 따로 알린다.
난수를 안 쓰는 화면에서 버튼이 안 눌린 경우도 여기서 드러난다.

직접 한 녹화와의 비교(난수를 쓴 코드, 서버 요청 순서·프레임, 로딩 프레임)는 `verify/<패스>.json` 에 진단용으로만 남는다.

## 편집

편집 창은 창 맨 아래 **Edit Videos** 로 연다(다른 녹화는 창 오른쪽 위 **⋮ → 다른 녹화 편집…**). 한 언어 영상을 보면서 지정한 편집은 모든 언어 영상에 똑같이 적용된다.

- Set In / Set Out(I/O 키)으로 구간 → **Delete**, **Mute BGM**·**Mute SFX**·**Mute All**, **Speed**(슬라이더 0.1x–10x, 0.1 단위), **Fade In-Out**(F 키, 구간 앞 절반은 검은 화면에서 밝아지고 뒤 절반은 다시 검게 어두워짐)
- **Insert Clip**: 재생 위치 앞에 다른 영상(언어별로 다르게도). Locale Capture 녹화 클립은 BGM·SFX 를 따로 끈다
- **Frame**: 비율별 Blur(안에 맞추고 빈 곳 블러)·Crop(자르기, 위치 조절) 미리보기
- **Target** 목표 길이, 실행 취소 Ctrl+Z·Ctrl+Y
- **Thumbnail**: 플레이헤드 장면을 언어별·비율별 PNG
- **Export**: 비율 토글(9:16·1:1·16:9·4:5)마다 언어별 파일. 포맷·품질·해상도는 Locale Capture 창 설정을 따른다
- 편집·내보내기는 동봉 ffmpeg(`Tools/ffmpeg.exe`, GPL 빌드 — `FFMPEG-LICENSE.txt`)

## 전제와 한계

- 언어 목록은 Unity Localization·I2 Localization 에서 읽는다. 둘 다 없으면 창의 Language Codes 에 직접 적는다.
  언어를 저장 값에 독자 형식으로 두는 게임은 창의 **Language Pref Key** 에 그 PlayerPrefs 키를 적는다.
- HTTP 가 아닌 통신(소켓·WebSocket), 네이티브 플러그인 내부 상태는 기록하지 못한다.
- 제네릭 메서드 안의 시간 호출처럼 바꿔치기 못 한 곳은 콘솔에 "런타임 패치 실패" 로 남는다.
- 로딩을 기다리는 동안 게임 Update 가 더 돈다. 프레임 수를 세는 로직은 어긋날 수 있고, 일치 검사가 알려 준다.
- 기록과 재생은 같은 게임뷰 크기여야 한다(창이 자동으로 맞춘다).
- Windows 에디터 기준(PlayerPrefs 백업에 reg.exe 를 쓴다).
- 프로젝트에 다른 Harmony(0Harmony.dll)가 있으면 하나만 남긴다.
