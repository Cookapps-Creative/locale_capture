using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CookApps.LocaleCapture
{
    // 플레이 모드 한 번 = 세션 하나. 에디터(CaptureDirector)가 기록 한 번, 그다음 언어·스템마다 재생 한 번씩 플레이 모드를 새로 연다.
    // 기록: 게임 부팅 첫 프레임부터 입력·프레임 경계·로딩 완료·서버 응답·난수 상태를 저장한다. 화면은 건드리지 않는다.
    // 재생: 같은 로컬 저장(에디터가 복원)·같은 시계·같은 난수·같은 서버 응답으로 같은 입력을 같은 프레임에 넣으며 녹화하고,
    //       기록과 달라진 곳을 어긋남 검사로 남긴다. 게임 코드는 하나도 참조하지 않는다.
    public sealed class ReplaySession : MonoBehaviour
    {
        private const float SceneHoldSeconds = 20f;   // 씬 로드를 이만큼 기다려도 안 오면 그냥 간다
        private const float LoadHoldSeconds = 5f;     // 그 밖의 로딩
        private const int MaxLoadNames = 3000;        // 진단용으로 남기는 로딩 이름 수
        private const float LoadClockAfterSeconds = 2f;   // 로딩 대기가 이만큼 안 풀리면 로딩 코드의 시계를 따로 돌린다(지연 로드를 기다리다 선 경우)
        private const float ServerHoldSeconds = 20f;  // 실제 서버에서 받는 응답
        private const float BgmMinLength = 10f;       // 반복 재생하는 이 길이 이상의 소리를 BGM 으로 본다

        public static ReplaySession Current { get; private set; }
        public static string LastStatus { get; private set; } = "";
        public static CaptureVerify.Report LastReport { get; private set; }

        public SessionRequest Request { get; private set; }
        public bool IsRecording => Request.Type == SessionRequest.Kind.Record;
        public string Status { get; private set; } = "";
        public int Frame { get; private set; }
        public int TotalFrames { get; private set; }
        public float Progress => TotalFrames <= 0 ? 0f : Mathf.Clamp01(Frame / (float) TotalFrames);

        // 기기 언어 흉내는 Application.systemLanguage 만으로는 모자란다 — Unity Localization 의 SystemLocaleSelector 는
        // CultureInfo.CurrentUICulture 를 먼저 보므로, 한국어 Windows 에서 en-US 패스를 돌리면 언어 시스템은 ko-KR 을 고르고
        // 게임(복원된 저장 값)은 영어로 부팅해 초기화가 엇갈린다. UI 문화권만 바꾼다(숫자·날짜 파싱이 쓰는 CurrentCulture 는 그대로).
        private System.Globalization.CultureInfo _savedUiCulture;

        private void ApplyUiCulture(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.GetCultureInfo(code.Replace('_', '-'));
                _savedUiCulture = System.Globalization.CultureInfo.CurrentUICulture;
                System.Globalization.CultureInfo.CurrentUICulture = culture;
            }
            catch (System.Globalization.CultureNotFoundException) { }
        }

        private void RestoreUiCulture()
        {
            if (_savedUiCulture == null) return;
            System.Globalization.CultureInfo.CurrentUICulture = _savedUiCulture;
            _savedUiCulture = null;
        }

        // 프레임 끝. 배치 모드는 화면을 그리지 않아 WaitForEndOfFrame 이 오지 않으므로 다음 프레임으로 대신한다.
        private static readonly WaitForEndOfFrame EndOfFrame = new WaitForEndOfFrame();
        private static object FrameEnd => Application.isBatchMode ? null : EndOfFrame;

        // 프레임을 실제 1/fps 초 간격 이상으로 묶는다. targetFrameRate 는 에디터·포커스 상태에 따라 안 먹을 때가 있어,
        // 기록과 재생의 실제 속도가 달라지면 아직 못 바꾼 실시간 대기가 다른 프레임에 끝난다.
        private readonly System.Diagnostics.Stopwatch _pace = System.Diagnostics.Stopwatch.StartNew();
        private double _nextFrameAt;

        private double _lastFrameAt;

        // 이번 프레임이 실제로 걸린 ms(Pace 직후 = 프레임 끝 기준).
        private int FrameMs()
        {
            double now = _pace.Elapsed.TotalSeconds;
            int ms = _lastFrameAt > 0 ? (int) Math.Round((now - _lastFrameAt) * 1000) : 0;
            _lastFrameAt = now;
            return ms;
        }

        private readonly System.Diagnostics.Stopwatch _tool = new System.Diagnostics.Stopwatch();
        private double _toolMsSum, _toolMsMax;

        // 재생에서 이 프레임 전까지는 30fps 로 묶지 않고 빨리 돈다(부팅 — 첫 입력·START 전, 영상에 안 남는 구간).
        // 로딩은 동기 지점이 붙잡고, 서버 응답·시계·Task.Delay 는 프레임 기준이라 빨리 돌아도 같은 프레임에 끝난다.
        private int _fastUntil;
        private readonly System.Diagnostics.Stopwatch _replayClock = new System.Diagnostics.Stopwatch();   // 재생에 걸린 실제 시간(로그)
        private double _startReachedSeconds = -1;
        private int _nextToggle;   // 재생에서 다음에 적용할 인스펙터 켜기/끄기
        private bool _loadClockTicked;
        // 로딩 대기 프레임 하나에 흐르는 게임 시간. 0 으로는 둘 수 없어(프레임 고정이 풀린다) 아주 작게 준다. 2 의 거듭제곱이라
        // 프레임 간격(1/fps)에서 빼고 더해도 반올림 오차가 없다 — 대기가 풀린 뒤 그만큼을 덜어 내면 엔진 시간이 녹화와 비트까지 같아진다.
        private const float HoldDeltaTime = 1f / 131072f;   // 2^-17 초 ≈ 7.6μs
        private float _heldTime;   // 대기 프레임들에서 흐른 시간 중 아직 덜어 내지 않은 것

        private void Pace()
        {
            double interval = 1.0 / Mathf.Max(1, Request.Fps);
            double now = _pace.Elapsed.TotalSeconds;
            if (now < _nextFrameAt) System.Threading.Thread.Sleep((int) ((_nextFrameAt - now) * 1000));
            _nextFrameAt = Math.Max(_nextFrameAt + interval, _pace.Elapsed.TotalSeconds);   // 밀렸으면 몰아서 따라잡지 않는다
        }

        private ReplayManifest _manifest;
        private bool _finished;
        private string _passName;

        // 기록
        private InputEventTrace _trace;
        private const long TraceMaxBytes = 512L << 20;   // 입력 기록 버퍼의 한도(필요한 만큼만 늘어난다)
        private long _traceSeen;                         // 기록기가 받은 입력 수 — 버퍼에 남은 수보다 많으면 덮어써진 것
        private InputRelay _relay;

        // 재생
        private InputTracePlayer _player;
        private readonly List<TapProbe.Tap> _replayTaps = new List<TapProbe.Tap>();
        private readonly List<UiTrace.Event> _replayUi = new List<UiTrace.Event>();
        private readonly List<HitTrace.Event> _replayHits = new List<HitTrace.Event>();
        private readonly List<HitTrace.ListState> _replayLists = new List<HitTrace.ListState>();
        private IFrameSink _sink;
        private IFrameSink _stemSink;   // 영상 패스가 함께 뽑는 스템 WAV
        private RenderTexture _rt, _copy;
        private NativeArray<float> _audio;
        private int _channels;
        private bool _audioStarted;
        private readonly List<(AsyncGPUReadbackRequest request, NativeArray<byte> buffer, int frame)> _pending = new List<(AsyncGPUReadbackRequest, NativeArray<byte>, int)>();
        private int _readbackErrors;

        // 동기 지점
        private readonly List<string> _drain = new List<string>();
        private readonly Dictionary<string, Queue<int>> _arrived = new Dictionary<string, Queue<int>>();   // 동기 신호별 도착 프레임

        // 복원값
        private int _savedVSync, _savedTargetFps;
        private readonly List<(object view, bool vsync)> _gameViews = new List<(object, bool)>();

        internal static void Create(SessionRequest request)
        {
            if (Current != null) return;
            var go = new GameObject("LocaleCapture.Session") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            var s = go.AddComponent<ReplaySession>();
            Current = s;
            s.Request = request;
            s.Begin();
        }

        // 기록을 멈춘다(창의 STOP). 재생 중이면 취소.
        public void Stop() => Finish(null, Keep);

        // 끝낼 때 기록을 남길지. START(바로 녹화면 Play 순간) 전에 끝나면 영상으로 쓸 구간이 없어 버린다.
        private bool Keep => IsRecording && VideoStartMarked;

        public bool VideoStartMarked { get; private set; }
        public int VideoStartFrame => _manifest?.VideoStartFrame ?? 0;

        internal void RecordToggle(InspectorToggles.Toggle t)
        {
            if (_manifest == null) return;
            t.Frame = Frame;
            _manifest.Toggles.Add(t);
        }

        public void MarkVideoStart()
        {
            if (!IsRecording || VideoStartMarked || _manifest == null) return;
            _manifest.VideoStartFrame = Frame;
            VideoStartMarked = true;
        }

        // ── 시작 ───────────────────────────────────────────────────────────────

        private void Begin()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
            ForceBackgroundInput();
            CaptureMarkers.Clear();
            UiTrace.Begin();
            HitTrace.Begin();
            // 영상이 나오는 재생 패스에서만 잰다(영상 비교가 본다). 앞 세션이 중간에 끊겨 남은 것은 여기서 치운다.
            if (IsRecording) LayoutTrace.End();
            else LayoutTrace.Begin();
            TapProbe.Reset();
            string dir = Request.Directory;
            int fps = Mathf.Max(1, Request.Fps);

            if (IsRecording)
            {
                _manifest = new ReplayManifest
                {
                    Name = Path.GetFileName(dir), Fps = fps, CreatedAt = DateTime.Now.ToString("s"),
                    Seed = Request.Seed, ClockBaseUtcTicks = Request.ClockBaseUtcTicks, BaseTickCount = Request.BaseTickCount,
                    TraceVersion = ReplayManifest.CurrentTraceVersion, SeedScheme = CaptureClock.CurrentSeedScheme,
                };
                CaptureClock.Begin(Request.ClockBaseUtcTicks, Request.BaseTickCount, fps, Request.Seed, null, _manifest.SeedScheme);
                ThreadGate.BeginRecord();
                CaptureScreen.Begin(_manifest, true);
                RandomTrace.Begin(_manifest.RandomCalls);
                NetworkTape.BeginRecord(dir, Request.LiveMarkers);
                CaptureInput.BeginRecord(Path.Combine(dir, CaptureInput.FileName));
                _relay = new InputRelay();   // 실제 입력을 재생과 같은 시점에 넣는다(트레이스보다 먼저)
                // 버퍼는 필요한 만큼 늘린다 — 기본값(1MB 고정)은 가득 차면 가장 오래된 입력부터 덮어써서, 마우스를 많이 움직이는
                // 긴 녹화는 앞부분 입력이 조용히 사라지고 프레임별 개수(EventCounts)와도 어긋난다. 프레임 경계는 EventCounts 로 직접 잰다.
                _trace = new InputEventTrace(1 << 20, true, TraceMaxBytes, 4 << 20) { recordFrameMarkers = false };
                _traceSeen = 0;
                _trace.onEvent += _ => _traceSeen++;
                _trace.Enable();
                UncapFramePacing();
                Application.targetFrameRate = fps;   // 기록은 실시간에 맞춘다
                Status = "기록 중";
            }
            else
            {
                _manifest = ReplayManifest.Load(dir);
                if (_manifest == null || _manifest.Frames == 0) { Finish("기록이 없다: " + dir, false); return; }
                TotalFrames = _manifest.Frames;
                _passName = Request.Type == SessionRequest.Kind.Video ? Request.Locale : Request.Type == SessionRequest.Kind.BgmStem ? "bgm" : "sfx";
                CaptureClock.Begin(_manifest.ClockBaseUtcTicks, _manifest.BaseTickCount, fps, _manifest.Seed,
                    Request.OverrideSystemLanguage ? Request.SystemLanguage : (SystemLanguage?) null, _manifest.SeedScheme);
                ThreadGate.BeginReplay(_manifest.TraceVersion >= 3 ? _manifest.ThreadPosts : null);
                if (Request.OverrideSystemLanguage) ApplyUiCulture(Request.Locale);
                NetworkTape.BeginReplay(dir, Request.Locale, Request.LiveMarkers, fps,
                    string.IsNullOrEmpty(Request.RecordedLocale) ? _manifest.RecordedLocale : Request.RecordedLocale, Request.RecordedLocaleForms,
                    _manifest.TraceVersion >= 2);
                CaptureScreen.Begin(_manifest, false);
                CaptureInput.BeginReplay(Path.Combine(dir, CaptureInput.FileName));
                CaptureVerify.Begin(_passName, Request.Locale, _manifest, _replayTaps, _replayUi, _replayHits, _replayLists);
                RandomTrace.Begin(new List<RandomTrace.Call>());
                _player = new InputTracePlayer(Path.Combine(dir, _manifest.TraceFile), _manifest.EventCounts, _manifest.Devices);
                UncapFramePacing();
                // 실제 속도 재생은 기록과 같은 속도로 돈다 — 실시간으로 끝나는 로딩·통신이 기록과 비슷한 프레임에 끝나게.
                // 빠른 재생은 묶지 않는다. 로딩·서버 응답은 동기 지점과 응답 대기가 기록 프레임에 맞춘다.
                Application.targetFrameRate = Request.Realtime ? fps : -1;
                Status = Request.Type == SessionRequest.Kind.Video ? $"{Request.Locale} 녹화" : Request.Type == SessionRequest.Kind.BgmStem ? "BGM 스템" : "SFX 스템";
            }
            Time.captureDeltaTime = 1f / fps;
            Frame = 0;
            StartCoroutine(IsRecording ? RecordLoop() : ReplayLoop());
        }

        // 세션 프레임은 엔진 프레임 StartEngineFrame 이 끝날 때부터 센다(그 전의 엔진 프레임은 모두 세션 프레임 0 에 든다).
        // 플레이 모드에 들어간 직후에는 화면이 아직 그려지지 않아 "프레임 끝" 신호가 한 번 빠지는 실행이 있다(Game 뷰를 최대화해 띄우는
        // 재생에서 잦다). 첫 신호부터 세면 그런 실행은 세션 프레임 0 에 엔진 프레임이 하나 더 들어가, 게임이 보는 Time.time·Time.frameCount 가
        // 녹화와 한 프레임 어긋난다 — "Time.time 이 0.5초 지나면" 식의 게임 타이머가 프레임 경계에서 한 프레임 일찍·늦게 끝난다.
        // 첫 신호가 언제 오든 같은 엔진 프레임에서 세기 시작하면 실행마다 같다. 재생은 녹화가 세기 시작한 엔진 프레임을 따른다.
        private const int StartEngineFrame = 4;
        private bool _engineNoted;

        private bool BeforeStartFrame => Time.frameCount < (IsRecording ? StartEngineFrame : _manifest.EngineFrame);

        // 세션 프레임 0 이 엔진의 몇 번째 프레임·몇 초인지 남긴다(첫 프레임 끝에 한 번).
        private void NoteEngineStart()
        {
            if (_engineNoted) return;
            _engineNoted = true;
            if (IsRecording)
            {
                _manifest.EngineFrame = Time.frameCount;
                _manifest.EngineTime = Time.timeAsDouble;
                if (Time.frameCount != StartEngineFrame)
                    Debug.LogWarning($"[LocaleCapture] 세션이 엔진 프레임 {Time.frameCount} 에서 시작했다({StartEngineFrame} 이어야 한다) — 첫 화면이 늦게 그려졌다");
                return;
            }
            // 이전 형식 기록(EngineFrame 0)은 세기 시작한 자리가 남아 있지 않다 — 비교하지 않는다.
            if (_manifest.EngineFrame > 0 && (Time.frameCount != _manifest.EngineFrame || Time.timeAsDouble != _manifest.EngineTime))
                CaptureVerify.Add(0, "시간", $"세션이 엔진 프레임 {Time.frameCount}(시간 {Time.timeAsDouble:R})에서 시작했다 — 녹화는 {_manifest.EngineFrame}({_manifest.EngineTime:R}). " +
                                             "게임이 보는 엔진 시간이 녹화와 달라, 프레임 경계에 걸린 게임 타이머가 한 프레임 어긋날 수 있다");
        }

        // ── 기록 ───────────────────────────────────────────────────────────────

        private IEnumerator RecordLoop()
        {
            while (!_finished)
            {
                yield return FrameEnd;
                if (BeforeStartFrame) continue;
                NoteEngineStart();
                Pace();
                if (_finished) break;
                _tool.Restart();
                if (Frame == 0)
                {
                    _manifest.Width = Screen.width;
                    _manifest.Height = Screen.height;
                    if (Request.Settings.StartOnPlay) MarkVideoStart();   // Play 하자마자 영상 시작
                }
                if (Frame == 1) AddressablesProbe.Register();
                _manifest.EventCounts.Add((int) _trace.eventCount);
                _drain.Clear();
                CaptureMarkers.Drain(_drain);
                foreach (string hit in _drain) _manifest.Markers.Add(new ReplayManifest.Marker { Frame = Frame, Name = hit });
                CaptureMarkers.DrainLoaded(Frame, _manifest.Loads, MaxLoadNames);
                RandomStates.Append(_manifest.RandomStates);
                TapProbe.Check(Frame, _manifest.Taps);
                UiTrace.Check(Frame, _manifest.UiEvents);
                HitTrace.Check(Frame, _manifest.HitEvents, _manifest.ListEvents);
                _manifest.FrameMs.Add(FrameMs());
                // 녹화 언어는 게임이 언어 시스템 초기화를 끝낸 뒤에만 읽는다(SelectedCode 가 초기화를 건드리지 않는다)
                if (string.IsNullOrEmpty(_manifest.RecordedLocale) && Frame >= 60 && Frame % 30 == 0)
                    _manifest.RecordedLocale = LocalizationBridge.SelectedCode();
                double toolMs = _tool.Elapsed.TotalMilliseconds;
                _toolMsSum += toolMs;
                _toolMsMax = Math.Max(_toolMsMax, toolMs);
                Advance();
                if (_relay != null) _relay.Frame = Frame;
                _relay?.Flush();
                // 실제 장치 입력이 게임에 그대로 들어간 기록은 재생과 어긋난다 — 더 플레이하기 전에 바로 알린다.
                if (_relay?.Leak != null)
                {
                    Finish($"실제 장치({_relay.Leak}) 입력을 가로채지 못해 게임에 그대로 들어갔다 — 이 기록은 재생과 어긋난다. " +
                           $"Input System {InputSystem.version} 에 맞게 Locale Capture 를 고쳐야 한다", false);
                    break;
                }
            }
        }

        private void Advance()
        {
            Frame++;
            CaptureClock.SetFrame(Frame);
            CaptureInput.SetFrame(Frame);
            NetworkTape.Tick(Frame);
            RandomTrace.SetFrame(Frame);
        }

        private void SaveRecording()
        {
            // 입력 기록이 한도까지 차서 앞쪽 입력이 덮어써졌으면 이 기록은 재생할 수 없다 — 저장하지 않고 실패로 알린다.
            long dropped = _traceSeen - _trace.eventCount;
            if (dropped > 0)
                throw new InvalidOperationException($"입력 기록이 한도({TraceMaxBytes >> 20}MB)를 넘어 앞쪽 입력 {dropped}개가 지워졌다 — 이 기록은 재생할 수 없다. 더 짧게 나눠 녹화한다");
            _manifest.Frames = Frame;
            _manifest.HitFailed = HitTrace.FailedFrame >= 0;
            _manifest.HitFailedFrame = Math.Max(0, HitTrace.FailedFrame);
            if (_relay != null) _manifest.Devices = new List<ReplayManifest.Device>(_relay.Devices);
            _manifest.RandomCreations = new List<CaptureClock.RandomCreation>(CaptureClock.Creations);
            _manifest.ThreadPosts = new List<ThreadGate.Post>(ThreadGate.Log);
            if (string.IsNullOrEmpty(_manifest.RecordedLocale)) _manifest.RecordedLocale = LocalizationBridge.SelectedCode(true);
            Directory.CreateDirectory(Request.Directory);
            _trace.WriteTo(Path.Combine(Request.Directory, _manifest.TraceFile));
            _manifest.Save(Request.Directory);
            Debug.Log($"[LocaleCapture] 기록 저장 {Frame} 프레임, 입력 {_trace.eventCount}, 동기 지점 {_manifest.Markers.Count}, 언어 {_manifest.RecordedLocale} → {Request.Directory}");
            Debug.Log($"[LocaleCapture] 기록 중 도구 부담 프레임당 평균 {_toolMsSum / Math.Max(1, Frame):F2}ms, 최대 {_toolMsMax:F1}ms (프레임 평균 {(_manifest.FrameMs.Count > 1 ? _manifest.FrameMs.Skip(1).Average() : 0):F1}ms)");
        }

        // ── 재생 ───────────────────────────────────────────────────────────────

        private IEnumerator ReplayLoop()
        {
            bool video = Request.Type == SessionRequest.Kind.Video;
            CaptureSettings settings = Request.Settings;
            bool audio = settings.Audio || !video || !string.IsNullOrEmpty(Request.Stem);

            settings.ResolveSize(_manifest.Width, _manifest.Height, out int outWidth, out int outHeight);
            float recordedAspect = _manifest.Width / (float) Mathf.Max(1, _manifest.Height);
            if (Mathf.Abs(outWidth / (float) outHeight - recordedAspect) > 0.01f)
                outHeight = Mathf.RoundToInt(outWidth / recordedAspect) & ~1;   // 늘리거나 자르지 않는다
            if (video)
            {
                _rt = new RenderTexture(_manifest.Width, _manifest.Height, 0, RenderTextureFormat.ARGB32) { name = "LocaleCapture" };
                _copy = new RenderTexture(outWidth, outHeight, 0, RenderTextureFormat.ARGB32) { name = "LocaleCapture.Copy" };
            }
            _channels = ChannelCount(AudioSettings.speakerMode);
            string outputBase = Path.Combine(Request.Directory, _manifest.Name);
            settings.OutputBase = outputBase;
            Request.Settings = settings;
            _sink = video ? CaptureBoot.VideoSinkFactory?.Invoke(Request)
                : CaptureBoot.WavSinkFactory?.Invoke(Path.Combine(Request.Directory, Request.Type == SessionRequest.Kind.BgmStem ? EditList.BgmStem : EditList.SfxStem));
            if (_sink == null) { Finish("녹화 출력(싱크)을 만들지 못했다", false); yield break; }
            var format = new FrameFormat
            {
                Width = outWidth, Height = outHeight, FrameRate = _manifest.Fps,
                SampleRate = AudioSettings.outputSampleRate, Channels = audio ? _channels : 0,
                OutputBase = outputBase,
                Bgra = _copy != null && CaptureRender.ReadbackIsBgra(_copy),   // 실제로 읽어 본 순서(ffmpeg)
                BgraHint = _copy != null && _copy.graphicsFormat.ToString().StartsWith("B8G8R8A8"),   // 포맷 이름(Unity 내장 인코더가 맞춰져 있던 값)
            };
            _sink.Begin(format, new[] { Request.Locale });
            if (video && !string.IsNullOrEmpty(Request.Stem))
            {
                string stemFile = Request.Stem == "bgm" ? EditList.BgmStem : settings.PerLocaleSfx ? EditList.SfxStemFile(Request.Locale) : EditList.SfxStem;
                _stemSink = CaptureBoot.WavSinkFactory?.Invoke(Path.Combine(Request.Directory, stemFile));
                _stemSink?.Begin(format, new[] { Request.Locale });
            }
            if (audio) { AudioRenderer.Start(); _audioStarted = true; CaptureClock.SyncDsp(); }

            if (Screen.width != _manifest.Width || Screen.height != _manifest.Height)
                CaptureVerify.Add(0, "화면", $"게임뷰 {Screen.width}x{Screen.height} 가 기록 {_manifest.Width}x{_manifest.Height} 와 다르다 — 입력 위치가 어긋난다");

            int nextMarker = 0;
            // Addressables 로딩은 이름으로 짝짓는다(LoadMatcher). 기록의 신호와 이름이 안 맞으면(이전 기록·이름 한도 초과) 개수로만.
            string[] markerLoad = LoadNamesByMarker(_manifest);
            var loads = new LoadMatcher(markerLoad != null ? _manifest.Loads.Select(l => l.Name) : null, _manifest.Fps);
            int loadsSeen = 0;
            float holdStarted = -1f;
            int heldKey = -1;   // 지금 기다리는 대상(동기 지점 번호, 서버 응답은 -2) — 대상이 바뀌면 기다린 시간을 다시 잰다
            int firstInput = _manifest.EventCounts.FindIndex(c => c > 0);
            _fastUntil = Request.Realtime
                ? Math.Max(0, Math.Min(firstInput < 0 ? _manifest.Frames : firstInput, _manifest.VideoStartFrame > 0 ? _manifest.VideoStartFrame : int.MaxValue) - 30)
                : int.MaxValue;
            _player.QueueFrame(0);
            _replayClock.Restart();
            // 영상에 남지 않는 구간은 작게 그린다 — 영상 패스는 START 전, 스템 패스는 끝까지(RestoreFramePacing 이 되돌린다).
            if (!video || _manifest.VideoStartFrame > 1) CaptureRender.BeginLowRes();
            while (Frame < TotalFrames && !_finished)
            {
                yield return FrameEnd;
                if (BeforeStartFrame) continue;
                NoteEngineStart();
                if (Frame >= _fastUntil) Pace();
                Application.targetFrameRate = Frame + 1 < _fastUntil ? -1 : _manifest.Fps;
                if (_finished) break;
                if (Frame == _manifest.VideoStartFrame) _startReachedSeconds = _replayClock.Elapsed.TotalSeconds;
                if (Frame == 1) { _player.DisableRealDevices(); AddressablesProbe.Register(); }
                TapProbe.Latch(Frame);   // 아래에서 로딩 대기로 넘어가도 이 Unity 프레임의 눌림을 놓치지 않게

                _drain.Clear();
                CaptureMarkers.Drain(_drain);
                int addrHits = 0;
                foreach (string hit in _drain)
                {
                    if (hit == AddrSignal) { addrHits++; continue; }
                    if (!_arrived.TryGetValue(hit, out Queue<int> q)) _arrived[hit] = q = new Queue<int>();
                    q.Enqueue(Frame);
                }
                CaptureMarkers.DrainLoaded(Frame, CaptureVerify.Loads, MaxLoadNames);
                // 끝난 Addressables 로딩은 이름과 함께 넘긴다(신호와 이름은 같은 순서로 쌓인다). 이름 수가 다르면 이름 없이.
                List<ReplayManifest.Marker> loaded = CaptureVerify.Loads;
                int named = loaded != null ? loaded.Count - loadsSeen : 0;
                for (int i = 0; i < addrHits; i++) loads.Arrive(named == addrHits ? loaded[loadsSeen + i].Name : null, Frame);
                if (loaded != null) loadsSeen = loaded.Count;

                // 기록 때 이 프레임까지 끝났던 로딩이 아직이면 게임 시간을 멈추고 기다린다(로딩 시간 차이 흡수).
                bool hold = false;
                while (nextMarker < _manifest.Markers.Count && _manifest.Markers[nextMarker].Frame <= Frame)
                {
                    ReplayManifest.Marker marker = _manifest.Markers[nextMarker];
                    bool arrived;
                    int arrivedFrame = 0;
                    if (marker.Name == AddrSignal) arrived = loads.TryMatch(markerLoad?[nextMarker], marker.Frame, out arrivedFrame);
                    else
                    {
                        arrived = _arrived.TryGetValue(marker.Name, out Queue<int> q) && q.Count > 0;
                        if (arrived) arrivedFrame = q.Dequeue();
                    }
                    if (arrived)
                    {
                        CaptureVerify.Arrived(marker.Name, marker.Frame, Math.Min(arrivedFrame, Frame));
                        nextMarker++;
                        continue;
                    }
                    // 씬 로드 신호인데 읽는 중인 씬이 없으면 기다리지 않는다(로드가 늦게 시작됐거나 활성화 대기). 신호는 올 때 받는다.
                    hold = !IsSceneSignal(marker.Name) || CaptureMarkers.SceneLoading();
                    break;
                }
                // 다음 씬이 기록 때 다음 프레임보다 뒤에 열렸으면 그때까지 활성화를 미루고, 아니면 푼다
                // (이번 프레임 끝에 풀면 다음 프레임에 열린다).
                CaptureMarkers.GateActivation = NextSceneFrame(nextMarker) > Frame + 1;
                if (!CaptureMarkers.GateActivation) CaptureMarkers.ReleaseActivation();
                // 로딩이 아니면 서버 응답: 기록 때 이 프레임까지 왔던 응답(언어별로 실제 서버에서 받는 것 등)이 아직이면 기다린다.
                string waitingFor = hold ? _manifest.Markers[nextMarker].Name : null;
                string server = waitingFor == null ? NetworkTape.Overdue() : null;
                if (server != null) waitingFor = CaptureVerify.ServerHold + server;
                if (waitingFor != null)
                {
                    float limit = server != null ? ServerHoldSeconds : waitingFor.StartsWith("scene:") ? SceneHoldSeconds : LoadHoldSeconds;
                    // 기다린 시간은 대상마다 따로 잰다 — 앞 대상(씬 20초 한도)을 오래 기다린 뒤 이어지는 대상(로딩 5초 한도)이 곧바로 시간 초과가 되지 않게.
                    int holdKey = server != null ? -2 : nextMarker;
                    if (holdStarted < 0 || holdKey != heldKey) { holdStarted = Time.realtimeSinceStartup; heldKey = holdKey; }
                    if (Time.realtimeSinceStartup - holdStarted < limit)
                    {
                        // 기다리는 동안 도는 프레임이 쓴 난수를 되돌려, 대기가 풀리는 프레임이 기록과 같은 상태에서 시작하게 한다.
                        RandomStates.Restore(_manifest.RandomStates, Frame - 1);
                        CaptureVerify.Held(Frame, waitingFor);
                        // 기다리는 동안에는 세션 시계로 끝나는 것(Task.Delay, 테이프 응답)도 멈춰 있다 — 로딩만 늦은 것이면 풀린 뒤 녹화와 같은 프레임에 이어진다.
                        // 로딩(씬·서버 응답 말고)을 기다린 지 오래면 그 시계들과 로딩 코드의 시계를 돌려, 서로 기다리다 멈춘 흐름이 풀리게 한다.
                        if (server == null && !waitingFor.StartsWith("scene:") && Time.realtimeSinceStartup - holdStarted > LoadClockAfterSeconds)
                        {
                            if (!_loadClockTicked)
                            {
                                _loadClockTicked = true;
                                CaptureVerify.LoadClockTicked(Frame, waitingFor);
                            }
                            CaptureClock.TickLoadClock();
                            CaptureClock.Stall();
                            NetworkTape.Stall();
                        }
                        Time.captureDeltaTime = HoldDeltaTime;
                        _heldTime += HoldDeltaTime;
                        // 기다리는 Unity 프레임에도 읽어 온 영상 프레임을 받아 간다. 안 그러면 대기 직전에 찍은 프레임의 읽기 요청이
                        // 기다리는 사이 끝났다가 치워져, 멀쩡히 읽힌 프레임을 실패로 보고 직전 프레임으로 바꿔 넣게 된다.
                        Flush(false);
                        continue;
                    }
                    if (server != null)
                    {
                        CaptureVerify.Add(Frame, "서버", $"'{server}' 응답이 {limit}초 안에 오지 않았다 — 올 때 넘긴다");
                        NetworkTape.GiveUpOverdue();
                    }
                    else
                    {
                        CaptureVerify.LoadTimedOut(Frame, waitingFor, limit);
                        if (waitingFor == AddrSignal) loads.Skip(markerLoad?[nextMarker]);
                        nextMarker++;
                        // 녹화 언어가 아닌 패스에서 게임이 언어 시스템 초기화를 기다리다 서면 로딩 신호가 더 안 온다 — 초기화를 끝내 준다.
                        if (!string.IsNullOrEmpty(Request.Locale) && LocalizationBridge.FinishPendingInitialization())
                            Debug.Log($"[LocaleCapture] 로딩 대기가 끝나지 않아 언어 시스템 초기화를 동기로 끝냈다 (프레임 {Frame})");
                    }
                }
                holdStarted = -1f;

                ApplyAudioMix();
                EnforceLocale();
                if (Frame < _manifest.VideoStartFrame) CaptureAudio(false);   // START 전 — 소리는 렌더링만 하고 버린다
                else if (video) CaptureFrame(audio);
                else CaptureAudio(true);
                CaptureVerify.CheckState(Frame, _manifest.RandomStates);
                TapProbe.Check(Frame, _replayTaps);
                UiTrace.Check(Frame, _replayUi);
                HitTrace.Check(Frame, _replayHits, _replayLists);
                CaptureVerify.FrameTime(FrameMs());
                Advance();
                // 다음에 그릴 프레임(Frame)이 START 직전이면 본래 크기로 돌려, START 프레임부터 온전히 찍힌다.
                if (video && CaptureRender.LowRes && Frame + 1 >= _manifest.VideoStartFrame) CaptureRender.EndLowRes();
                while (_nextToggle < _manifest.Toggles.Count && _manifest.Toggles[_nextToggle].Frame <= Frame)
                    InspectorToggles.Apply(_manifest.Toggles[_nextToggle++]);
                if (Frame < TotalFrames) _player.QueueFrame(Frame);
                // 로딩 대기 프레임에서 흐른 시간만큼 다음 프레임을 짧게 해 되돌린다. 그대로 두면 엔진 시간(Time.time)이 녹화보다 그만큼 앞서,
                // "Time.time + 1초" 처럼 프레임 경계에 딱 걸리는 게임 타이머가 반올림 차이로 한 프레임 일찍·늦게 끝날 수 있다.
                float step = 1f / _manifest.Fps;
                if (_heldTime > 0f)
                {
                    float back = Mathf.Min(_heldTime, step * 0.5f);
                    step -= back;
                    _heldTime -= back;
                }
                Time.captureDeltaTime = step;
                Flush(false);
            }
            if (!_finished) Finish(null, true);
        }

        // 스템 패스는 BGM 만 / SFX 만 남긴다. 영상 패스는 창에서 끈 쪽을 끈다. 반복 재생하는 긴 소리를 BGM 으로 본다.
        private void ApplyAudioMix()
        {
            bool keepBgm, keepSfx;
            switch (Request.Type)
            {
                case SessionRequest.Kind.BgmStem: keepBgm = true; keepSfx = false; break;
                case SessionRequest.Kind.SfxStem: keepBgm = false; keepSfx = true; break;
                default:
                    if (!string.IsNullOrEmpty(Request.Stem)) { keepBgm = Request.Stem == "bgm"; keepSfx = Request.Stem == "sfx"; }
                    else { keepBgm = Request.Settings.Bgm; keepSfx = Request.Settings.Sfx; }
                    break;
            }
            if (keepBgm && keepSfx) return;
            foreach (AudioSource source in FindObjectsByType<AudioSource>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                bool bgm = source.loop && source.clip != null && source.clip.length >= BgmMinLength;
                source.mute = bgm ? !keepBgm : !keepSfx;
            }
        }

        // 저장 복원·기기 언어로 대부분 맞춰지지만, 그래도 다른 언어면 언어 시스템의 선택 언어를 바꾼다.
        // 마지막 수단이라 부팅이 끝난 뒤(기록의 마지막 씬 전환 60프레임 뒤, START 이후)에만 — 부팅 중에 바꾸면 게임이 자기 언어 적용과
        // 엇갈려 로그인 흐름이 멈출 수 있다.
        private void EnforceLocale()
        {
            if (string.IsNullOrEmpty(Request.Locale) || Frame < EnforceLocaleFrom || Frame % 30 != 0 || !LocalizationBridge.Available) return;
            string current = LocalizationBridge.SelectedCode();
            if (current != null && !string.Equals(current, Request.Locale, StringComparison.OrdinalIgnoreCase) && LocalizationBridge.Select(Request.Locale))
            {
                Debug.Log($"[LocaleCapture] 선택 언어가 {current} 라서 {Request.Locale} 로 바꿨다 (프레임 {Frame})");
                CaptureVerify.LocaleSwitched(Frame, current);   // 이 프레임 앞의 영상은 다른 언어로 찍혔을 수 있다 — 결과에 남긴다
            }
        }

        private void CaptureFrame(bool audio)
        {
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_rt);
            // 백버퍼 캡처는 위→아래 순서인데 MediaEncoder 는 Texture2D 처럼 아래→위를 기대한다 — GPU 에서 뒤집어 복사.
            Graphics.Blit(_rt, _copy, new Vector2(1, -1), new Vector2(0, 1));
            NativeArray<byte> buffer = _sink.Rent(_copy.width * _copy.height * 4);
            _pending.Add((AsyncGPUReadback.RequestIntoNativeArray(ref buffer, _copy, 0), buffer, Frame));
            if (audio) CaptureAudio(true);
        }

        private void CaptureAudio(bool keep)
        {
            if (!_audioStarted) return;
            int samples = AudioRenderer.GetSampleCountForCaptureFrame();
            if (samples <= 0) return;
            int length = samples * _channels;
            if (!_audio.IsCreated || _audio.Length < length)
            {
                if (_audio.IsCreated) _audio.Dispose();
                _audio = new NativeArray<float>(length, Allocator.Persistent);
            }
            NativeArray<float> slice = _audio.GetSubArray(0, length);
            AudioRenderer.Render(slice);
            if (!keep) return;
            _sink.AddAudio(slice);
            _stemSink?.AddAudio(slice);
        }

        private void Flush(bool all)
        {
            while (_pending.Count > 0)
            {
                (AsyncGPUReadbackRequest request, NativeArray<byte> buffer, int frame) = _pending[0];
                if (!request.done)
                {
                    if (!all) return;
                    request.WaitForCompletion();
                }
                _pending.RemoveAt(0);
                if (request.hasError)
                {
                    // 렌더 스케일·창 크기가 바뀐 직후 등에 GPU 읽기가 실패할 수 있다. 직전 프레임을 한 번 더 넣어 프레임 수를 맞춘다.
                    // 끝난 읽기 요청은 엔진이 다음 프레임에 치우고, 치워진 요청도 hasError 로 보인다 — 그래서 Unity 프레임마다 받아 가야 한다(로딩 대기 중에도).
                    if (++_readbackErrors <= 3) Debug.LogWarning($"[LocaleCapture] 프레임 {frame} 읽기 실패 — 직전 프레임으로 대신한다");
                    CaptureVerify.FrameRepeated(frame);
                    _sink.Return(buffer);
                    _sink.RepeatVideoFrame(0);
                    continue;
                }
                _sink.AddVideoFrame(0, buffer);
            }
        }

        private void EndReplay()
        {
            Flush(true);
            if (_audioStarted) { AudioRenderer.Stop(); _audioStarted = false; }
            try { _sink?.End(); } catch (Exception e) { Debug.LogException(e); }
            try { _stemSink?.End(); } catch (Exception e) { Debug.LogException(e); }
            _sink = null;
            _stemSink = null;
            _player?.Dispose(); _player = null;
            if (_audio.IsCreated) _audio.Dispose();
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            if (_copy != null) { _copy.Release(); Destroy(_copy); _copy = null; }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => CaptureMarkers.Hit("scene:" + scene.name);

        private void Update()
        {
            if (_finished) return;
            NetworkTape.ReleaseOffMain();   // 작업 스레드에서 보낸 요청의 응답 — 그 이어받기가 메인 스레드로 돌아오기 전에(아래) 풀어 둔다
            ThreadGate.Release(Frame);   // 작업 스레드에서 돌아온 이어받기
            CaptureClock.ReleaseDelays(false);
            NetworkTape.Release();   // 기록 때도 응답을 여기서 넘긴다(재생과 같은 자리)
        }

        // 아래 둘은 재생 프레임마다 묻는 값이다 — 동기 지점(끝난 로딩마다 하나)이 수천 개일 수 있어 한 번만 계산해 둔다.
        private int _enforceLocaleFrom = -1;
        private int[] _nextSceneFrame;   // [i] = i 번째 동기 지점부터 처음 나오는 씬 전환의 프레임(없으면 0)

        private int EnforceLocaleFrom
        {
            get
            {
                if (_enforceLocaleFrom >= 0) return _enforceLocaleFrom;
                int lastScene = 0;
                foreach (ReplayManifest.Marker m in _manifest.Markers)
                    if (m.Name.StartsWith("scene:", StringComparison.Ordinal)) lastScene = Math.Max(lastScene, m.Frame);
                return _enforceLocaleFrom = Math.Max(_manifest.VideoStartFrame, lastScene + 60);
            }
        }

        private int NextSceneFrame(int from)
        {
            if (_nextSceneFrame == null)
            {
                List<ReplayManifest.Marker> markers = _manifest.Markers;
                _nextSceneFrame = new int[markers.Count + 1];   // 맨 끝 = 0: 남은 씬 전환이 없으면 미루지 않는다
                for (int i = markers.Count - 1; i >= 0; i--)
                    _nextSceneFrame[i] = markers[i].Name.StartsWith("scene:", StringComparison.Ordinal) ? markers[i].Frame : _nextSceneFrame[i + 1];
            }
            return _nextSceneFrame[Math.Min(Math.Max(from, 0), _nextSceneFrame.Length - 1)];
        }

        private static bool IsSceneSignal(string marker) => marker.StartsWith("scene:", StringComparison.Ordinal) || marker == "async:AsyncOperation";

        private const string AddrSignal = "addr";   // Addressables 로딩 완료 동기 지점

        // 기록의 "addr" 동기 지점마다 그 로딩의 이름(Loads 와 같은 순서). 개수나 프레임이 안 맞으면 null.
        private static string[] LoadNamesByMarker(ReplayManifest manifest)
        {
            var names = new string[manifest.Markers.Count];
            int next = 0;
            for (int i = 0; i < manifest.Markers.Count; i++)
            {
                if (manifest.Markers[i].Name != AddrSignal) continue;
                if (next >= manifest.Loads.Count || manifest.Loads[next].Frame != manifest.Markers[i].Frame) return null;
                names[i] = manifest.Loads[next++].Name;
            }
            return next == manifest.Loads.Count ? names : null;
        }

        // ── 프레임 페이싱·입력 ─────────────────────────────────────────────────

        private void UncapFramePacing()
        {
            _savedVSync = QualitySettings.vSyncCount;
            _savedTargetFps = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
#if UNITY_EDITOR
            Type gameView = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView");
            System.Reflection.PropertyInfo prop = gameView?.GetProperty("vSyncEnabled",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (prop != null)
                foreach (UnityEngine.Object view in Resources.FindObjectsOfTypeAll(gameView))
                {
                    try { bool on = (bool) prop.GetValue(view); _gameViews.Add((view, on)); if (on) prop.SetValue(view, false); }
                    catch (Exception e) { Debug.LogException(e); }
                }
#endif
        }

        private void RestoreFramePacing()
        {
            CaptureRender.EndLowRes();
            Time.captureDeltaTime = 0f;
            QualitySettings.vSyncCount = _savedVSync;
            Application.targetFrameRate = _savedTargetFps;
#if UNITY_EDITOR
            Type gameView = typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView");
            System.Reflection.PropertyInfo prop = gameView?.GetProperty("vSyncEnabled",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            foreach ((object view, bool vsync) in _gameViews)
                if (view is UnityEngine.Object o && o != null && prop != null) { try { prop.SetValue(view, vsync); } catch { /* 창이 닫힘 */ } }
            _gameViews.Clear();
#endif
        }

#if UNITY_EDITOR
        private InputSettings.EditorInputBehaviorInPlayMode _savedEditorInput;
        private InputSettings.BackgroundBehavior _savedBackground;
        private bool _inputForced;
#endif

        // 에디터가 뒤에 있어도(다른 창을 보는 동안에도) 기록·재생 입력이 버려지지 않게 한다. 끝나면 되돌린다.
        private void ForceBackgroundInput()
        {
#if UNITY_EDITOR
            InputSettings settings = InputSystem.settings;
            _savedEditorInput = settings.editorInputBehaviorInPlayMode;
            _savedBackground = settings.backgroundBehavior;
            _inputForced = true;
            settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#endif
        }

        private void RestoreBackgroundInput()
        {
#if UNITY_EDITOR
            if (!_inputForced) return;
            _inputForced = false;
            InputSettings settings = InputSystem.settings;
            settings.editorInputBehaviorInPlayMode = _savedEditorInput;
            settings.backgroundBehavior = _savedBackground;
#endif
        }

        // ── 종료 ───────────────────────────────────────────────────────────────

        private void Finish(string error, bool completed)
        {
            if (_finished) return;
            _finished = true;
            if (Current == this) Current = null;
            SceneManager.sceneLoaded -= OnSceneLoaded;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif
            try
            {
                if (IsRecording)
                {
                    if (_trace != null)
                    {
                        _trace.Disable();
                        if (completed) SaveRecording();
                        _trace.Dispose(); _trace = null;
                    }
                    _relay?.Dispose(); _relay = null;
                    NetworkTape.End(_manifest?.RecordedLocale, completed);
                }
                else
                {
                    EndReplay();
                    NetworkTape.End(null, completed);
                    LastReport = CaptureVerify.End(Request.Directory, Frame);
                    double total = _replayClock.Elapsed.TotalSeconds;
                    Debug.Log($"[LocaleCapture] {_passName} 재생 {Frame}프레임({Frame / (double) Mathf.Max(1, _manifest?.Fps ?? 30):F1}초 분량)에 {total:F1}초 — " +
                              (Request.Realtime ? "실제 속도" : "빠르게") +
                              (_startReachedSeconds >= 0 ? $", START 까지 {_startReachedSeconds:F1}초·이후 {total - _startReachedSeconds:F1}초" : ""));
                }
                CaptureInput.End();
            }
            catch (Exception e) { Debug.LogException(e); error ??= e.Message; completed = false; }
            AddressablesProbe.Unregister();
            CaptureMarkers.Clear();
            ThreadGate.End();
            CaptureClock.End();
            RestoreUiCulture();
            CaptureScreen.End();
            RandomTrace.End();
            RestoreFramePacing();
            RestoreBackgroundInput();

            if (error != null) { Status = "실패: " + error; Debug.LogError("[LocaleCapture] " + error); }
            else Status = IsRecording ? (completed ? "기록 완료" : "취소") : (completed ? Status + " 완료" : "취소");
            LastStatus = Status;
            if (this != null) Destroy(gameObject);
            CaptureBoot.RaiseEnded(new SessionResult { Request = Request, Completed = completed && error == null, Error = error });
        }

#if UNITY_EDITOR
        // 기록 중에 플레이 모드를 끄면 거기까지를 기록으로 저장한다. 재생 중이면 취소.
        private void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Finish(null, Keep);
        }
#endif

        private void OnDestroy()
        {
            if (!_finished) Finish(null, Keep);
        }

        private static int ChannelCount(AudioSpeakerMode mode)
        {
            switch (mode)
            {
                case AudioSpeakerMode.Mono: return 1;
                case AudioSpeakerMode.Quad: return 4;
                case AudioSpeakerMode.Surround: return 5;
                case AudioSpeakerMode.Mode5point1: return 6;
                case AudioSpeakerMode.Mode7point1: return 8;
                default: return 2;
            }
        }
    }

    // 프레임 끝의 UnityEngine.Random 상태를 4개 정수로 저장·비교·복원한다.
    internal static class RandomStates
    {
        // Random.State 는 int 4개(s0~s3)라 그대로 겹쳐 읽는다 — 매 프레임 JSON 변환을 하지 않게.
        private struct Raw { public int s0, s1, s2, s3; }

        private static Raw Read()
        {
            UnityEngine.Random.State state = UnityEngine.Random.state;
            return UnsafeUtility.As<UnityEngine.Random.State, Raw>(ref state);
        }

        internal static void Append(List<int> into)
        {
            Raw raw = Read();
            into.Add(raw.s0); into.Add(raw.s1); into.Add(raw.s2); into.Add(raw.s3);
        }

        internal static int[] Current()
        {
            Raw raw = Read();
            return new[] { raw.s0, raw.s1, raw.s2, raw.s3 };
        }

        internal static bool Matches(List<int> states, int frame)
        {
            int i = frame * 4;
            if (states == null || frame < 0 || i + 3 >= states.Count) return true;
            Raw raw = Read();
            return raw.s0 == states[i] && raw.s1 == states[i + 1] && raw.s2 == states[i + 2] && raw.s3 == states[i + 3];
        }

        internal static void Restore(List<int> states, int frame)
        {
            int i = frame * 4;
            if (states == null || frame < 0 || i + 3 >= states.Count) return;
            var raw = new Raw { s0 = states[i], s1 = states[i + 1], s2 = states[i + 2], s3 = states[i + 3] };
            UnityEngine.Random.state = UnsafeUtility.As<Raw, UnityEngine.Random.State>(ref raw);
        }
    }
}
