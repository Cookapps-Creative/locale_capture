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
        private InputRelay _relay;

        // 재생
        private InputTracePlayer _player;
        private readonly List<TapProbe.Tap> _replayTaps = new List<TapProbe.Tap>();
        private readonly List<UiTrace.Event> _replayUi = new List<UiTrace.Event>();
        private IFrameSink _sink;
        private IFrameSink _stemSink;   // 영상 패스가 함께 뽑는 스템 WAV
        private RenderTexture _rt, _copy;
        private NativeArray<float> _audio;
        private int _channels;
        private bool _audioStarted;
        private readonly List<(AsyncGPUReadbackRequest request, NativeArray<byte> buffer)> _pending = new List<(AsyncGPUReadbackRequest, NativeArray<byte>)>();

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
        public void Stop() => Finish(null, IsRecording);

        public bool VideoStartMarked { get; private set; }
        public int VideoStartFrame => _manifest?.VideoStartFrame ?? 0;

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
            string dir = Request.Directory;
            int fps = Mathf.Max(1, Request.Fps);

            if (IsRecording)
            {
                _manifest = new ReplayManifest
                {
                    Name = Path.GetFileName(dir), Fps = fps, CreatedAt = DateTime.Now.ToString("s"),
                    Seed = Request.Seed, ClockBaseUtcTicks = Request.ClockBaseUtcTicks, BaseTickCount = Request.BaseTickCount,
                };
                CaptureClock.Begin(Request.ClockBaseUtcTicks, Request.BaseTickCount, fps, Request.Seed, null);
                CaptureScreen.Begin(_manifest, true);
                RandomTrace.Begin(_manifest.RandomCalls);
                NetworkTape.BeginRecord(dir, Request.LiveMarkers);
                CaptureInput.BeginRecord(Path.Combine(dir, CaptureInput.FileName));
                _relay = new InputRelay();   // 실제 입력을 재생과 같은 시점에 넣는다(트레이스보다 먼저)
                _trace = new InputEventTrace { recordFrameMarkers = false };   // 프레임 경계는 EventCounts 로 직접 잰다
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
                    Request.OverrideSystemLanguage ? Request.SystemLanguage : (SystemLanguage?) null);
                NetworkTape.BeginReplay(dir, Request.Locale, Request.LiveMarkers, fps);
                CaptureScreen.Begin(_manifest, false);
                CaptureInput.BeginReplay(Path.Combine(dir, CaptureInput.FileName));
                CaptureVerify.Begin(_passName, Request.Locale, _manifest.RandomCalls, _manifest.Taps, _replayTaps, _manifest.UiEvents, _replayUi);
                RandomTrace.Begin(new List<RandomTrace.Call>());
                _player = new InputTracePlayer(Path.Combine(dir, _manifest.TraceFile), _manifest.EventCounts);
                UncapFramePacing();
                // 재생도 기록과 같은 실제 속도로 돈다 — 실시간으로 끝나는 로딩·통신이 기록과 비슷한 프레임에 끝나게.
                Application.targetFrameRate = fps;
                Status = Request.Type == SessionRequest.Kind.Video ? $"{Request.Locale} 녹화" : Request.Type == SessionRequest.Kind.BgmStem ? "BGM 스템" : "SFX 스템";
            }
            Time.captureDeltaTime = 1f / fps;
            Frame = 0;
            StartCoroutine(IsRecording ? RecordLoop() : ReplayLoop());
        }

        // ── 기록 ───────────────────────────────────────────────────────────────

        private IEnumerator RecordLoop()
        {
            while (!_finished)
            {
                yield return FrameEnd;
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
                RandomStates.Append(_manifest.RandomStates);
                TapProbe.Check(Frame, _manifest.Taps);
                UiTrace.Check(Frame, _manifest.UiEvents);
                _manifest.FrameMs.Add(FrameMs());
                if (string.IsNullOrEmpty(_manifest.RecordedLocale) && Frame >= 60 && Frame % 30 == 0)
                    _manifest.RecordedLocale = LocalizationBridge.SelectedCode();
                double toolMs = _tool.Elapsed.TotalMilliseconds;
                _toolMsSum += toolMs;
                _toolMsMax = Math.Max(_toolMsMax, toolMs);
                Advance();
                _relay?.Flush();
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
            _manifest.Frames = Frame;
            if (string.IsNullOrEmpty(_manifest.RecordedLocale)) _manifest.RecordedLocale = LocalizationBridge.SelectedCode();
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
                OutputBase = outputBase, Bgra = _copy != null && _copy.graphicsFormat.ToString().StartsWith("B8G8R8A8"),
            };
            _sink.Begin(format, new[] { Request.Locale });
            if (video && !string.IsNullOrEmpty(Request.Stem))
            {
                _stemSink = CaptureBoot.WavSinkFactory?.Invoke(Path.Combine(Request.Directory, Request.Stem == "bgm" ? EditList.BgmStem : EditList.SfxStem));
                _stemSink?.Begin(format, new[] { Request.Locale });
            }
            if (audio) { AudioRenderer.Start(); _audioStarted = true; CaptureClock.SyncDsp(); }

            if (Screen.width != _manifest.Width || Screen.height != _manifest.Height)
                CaptureVerify.Add(0, "화면", $"게임뷰 {Screen.width}x{Screen.height} 가 기록 {_manifest.Width}x{_manifest.Height} 와 다르다 — 입력 위치가 어긋난다");

            int nextMarker = 0;
            float holdStarted = -1f;
            int firstInput = _manifest.EventCounts.FindIndex(c => c > 0);
            _fastUntil = Math.Max(0, Math.Min(firstInput < 0 ? _manifest.Frames : firstInput, _manifest.VideoStartFrame > 0 ? _manifest.VideoStartFrame : int.MaxValue) - 30);
            _player.QueueFrame(0);
            while (Frame < TotalFrames && !_finished)
            {
                yield return FrameEnd;
                if (Frame >= _fastUntil) Pace();
                Application.targetFrameRate = Frame + 1 < _fastUntil ? -1 : _manifest.Fps;
                if (_finished) break;
                if (Frame == 1) { _player.DisableRealDevices(); AddressablesProbe.Register(); }

                _drain.Clear();
                CaptureMarkers.Drain(_drain);
                foreach (string hit in _drain)
                {
                    if (!_arrived.TryGetValue(hit, out Queue<int> q)) _arrived[hit] = q = new Queue<int>();
                    q.Enqueue(Frame);
                }

                // 기록 때 이 프레임까지 끝났던 로딩이 아직이면 게임 시간을 멈추고 기다린다(로딩 시간 차이 흡수).
                bool hold = false;
                while (nextMarker < _manifest.Markers.Count && _manifest.Markers[nextMarker].Frame <= Frame)
                {
                    ReplayManifest.Marker marker = _manifest.Markers[nextMarker];
                    if (_arrived.TryGetValue(marker.Name, out Queue<int> q) && q.Count > 0)
                    {
                        CaptureVerify.Arrived(marker.Name, marker.Frame, Math.Min(q.Dequeue(), Frame));
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
                if (hold)
                {
                    string expected = _manifest.Markers[nextMarker].Name;
                    float limit = expected.StartsWith("scene:") ? SceneHoldSeconds : LoadHoldSeconds;
                    if (holdStarted < 0) holdStarted = Time.realtimeSinceStartup;
                    if (Time.realtimeSinceStartup - holdStarted < limit)
                    {
                        // 기다리는 동안 도는 프레임이 쓴 난수를 되돌려, 대기가 풀리는 프레임이 기록과 같은 상태에서 시작하게 한다.
                        RandomStates.Restore(_manifest.RandomStates, Frame - 1);
                        CaptureVerify.Held(expected);
                        CaptureClock.Hold();
                        NetworkTape.Hold();
                        Time.captureDeltaTime = 1e-5f;
                        continue;
                    }
                    CaptureVerify.Add(Frame, "로딩", $"'{expected}' 이 {limit}초 안에 끝나지 않았다 — 기록 때와 다른 로딩");
                    nextMarker++;
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
                CaptureVerify.FrameTime(FrameMs());
                Advance();
                if (Frame < TotalFrames) _player.QueueFrame(Frame);
                Time.captureDeltaTime = 1f / _manifest.Fps;
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
                Debug.Log($"[LocaleCapture] 선택 언어가 {current} 라서 {Request.Locale} 로 바꿨다 (프레임 {Frame})");
        }

        private void CaptureFrame(bool audio)
        {
            ScreenCapture.CaptureScreenshotIntoRenderTexture(_rt);
            // 백버퍼 캡처는 위→아래 순서인데 MediaEncoder 는 Texture2D 처럼 아래→위를 기대한다 — GPU 에서 뒤집어 복사.
            Graphics.Blit(_rt, _copy, new Vector2(1, -1), new Vector2(0, 1));
            NativeArray<byte> buffer = _sink.Rent(_copy.width * _copy.height * 4);
            _pending.Add((AsyncGPUReadback.RequestIntoNativeArray(ref buffer, _copy, 0), buffer));
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
                (AsyncGPUReadbackRequest request, NativeArray<byte> buffer) = _pending[0];
                if (!request.done)
                {
                    if (!all) return;
                    request.WaitForCompletion();
                }
                _pending.RemoveAt(0);
                if (request.hasError) { Debug.LogWarning("[LocaleCapture] 프레임 읽기 실패, 건너뜀"); _sink.Return(buffer); continue; }
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
            CaptureClock.ReleaseDelays(false);
            if (!IsRecording) NetworkTape.Release();
        }

        private int EnforceLocaleFrom
        {
            get
            {
                int lastScene = 0;
                foreach (ReplayManifest.Marker m in _manifest.Markers)
                    if (m.Name.StartsWith("scene:")) lastScene = Math.Max(lastScene, m.Frame);
                return Math.Max(_manifest.VideoStartFrame, lastScene + 60);
            }
        }

        private int NextSceneFrame(int from)
        {
            for (int i = from; i < _manifest.Markers.Count; i++)
                if (_manifest.Markers[i].Name.StartsWith("scene:")) return _manifest.Markers[i].Frame;
            return 0;   // 남은 씬 전환이 없으면 미루지 않는다
        }

        private static bool IsSceneSignal(string marker) => marker.StartsWith("scene:") || marker == "async:AsyncOperation";

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
                }
                CaptureInput.End();
            }
            catch (Exception e) { Debug.LogException(e); error ??= e.Message; completed = false; }
            AddressablesProbe.Unregister();
            CaptureMarkers.Clear();
            CaptureClock.End();
            CaptureScreen.End();
            RandomTrace.End();
            RestoreFramePacing();
            RestoreBackgroundInput();

            if (error != null) { Status = "실패: " + error; Debug.LogError("[LocaleCapture] " + error); }
            else Status = IsRecording ? "기록 완료" : (completed ? Status + " 완료" : "취소");
            LastStatus = Status;
            if (this != null) Destroy(gameObject);
            CaptureBoot.RaiseEnded(new SessionResult { Request = Request, Completed = completed && error == null, Error = error });
        }

#if UNITY_EDITOR
        // 기록 중에 플레이 모드를 끄면 거기까지를 기록으로 저장한다. 재생 중이면 취소.
        private void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) Finish(null, IsRecording);
        }
#endif

        private void OnDestroy()
        {
            if (!_finished) Finish(null, IsRecording);
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
