using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 전체 진행을 에디터에서 끌고 간다. 플레이 모드 한 번 = 세션 하나.
    //  1) 기록: 로컬 저장 백업 → 첫 빌드 씬으로 플레이 모드 시작 → 사용자가 플레이 → STOP
    //  2) 패스마다(BGM·SFX 스템, 언어별 영상): 로컬 저장 복원(+언어 표기 교체) → 플레이 모드 → 재생·녹화 → 종료
    //  3) 끝: 기록 직후의 로컬 저장으로 되돌리고 편집 창을 연다
    // 스크립트 리로드를 넘나들므로 진행 상태는 Library/LocaleCapture/director.json 에 둔다.
    [InitializeOnLoad]
    internal static class CaptureDirector
    {
        [Serializable]
        private sealed class State
        {
            public string Phase = Idle;
            public string Directory;
            public CaptureSettings Settings;
            public List<string> Locales = new List<string>();
            public List<string> Passes = new List<string>();
            public int PassIndex;
            public string CustomLanguageKey;
            public string AfterState;
            public long StartedUtcTicks;
            public long PassStartedUtcTicks;   // 지금 패스를 시작한 때(영상 파일이 이번에 쓰였는지 확인용)
            public int PatchProblems = -1;     // 바꿔 끼우지 못한 게임 메서드 수(-1 = 아직 모름)
            public List<string> PatchRisky = new List<string>();   // 그중 재현에 영향을 줄 수 있는 것
            public string PendingRequestJson;
            public bool LastCompleted;
            public string LastError;
            public string Message;
            public List<string> Reports = new List<string>();
            public List<string> PassHashes = new List<string>();   // "패스=난수 상태 요약"
            public int SavedInsecureHttp = -1;
            public int SavedAudioMute = -1;   // 게임뷰 Mute Audio(마스터 음소거) — 재생 녹화 동안 풀었다가 되돌린다
            public bool StartSceneOverridden;
            public string SavedStartScene;
            public bool RetryingRealtime;   // 지금 패스는 빠르게 뽑은 게 녹화와 달라 실제 속도로 다시 뽑는 중
            public bool RetriedLive;        // 지금 패스는 실제 서버에서 받은 응답이 있어 한 번 더 뽑는 중
            public int SavedGameViewMaximized = -1;   // 재생 동안 Game 뷰를 최대화(다른 창이 매 프레임 그리지 않게) — 끝나면 되돌린다
            public int SavedEnterPlayMode = -1;       // Enter Play Mode Settings(도메인·씬 리로드 끄기) 원래 값 — 작업 동안 꺼 둔다
            public int SavedEnterPlayModeOptions;
            // 다시 뽑기 — 디자이너가 다시 플레이하지 않아도 되게, 어긋나거나 끊긴 패스는 같은 기록으로 다시 뽑는다
            public int Retries;                 // 지금 패스를 다시 뽑은 횟수(어긋남·영상 없음·멈춤)
            public string LastDivergence;       // 지금 패스가 실제 속도 재생에서 마지막으로 어긋난 곳 — 두 번 연달아 같으면 더 뽑지 않는다
            public bool Stalled;                // 멈춤 감시가 지금 패스를 끊었다
            public string StallNote;
            public bool Partial;                // 실패한 것만 다시 뽑는 작업(OnlyPasses 만 돈다)
            public List<string> AllPasses = new List<string>();    // 이 기록의 전체 패스(일부만 다시 뽑을 때도 전체)
            public List<string> OnlyPasses = new List<string>();   // 이번에 뽑을 패스
            public List<string> DonePasses = new List<string>();   // 녹화와 같게 끝난 패스
            public List<string> DoneHashes = new List<string>();   // 그 패스들의 "패스=난수 상태 요약"
            public List<string> Unfinished = new List<string>();   // 작업이 끝났을 때 남은 패스(어긋났거나 뽑지 못한 것)
            public bool RemixFailed;            // 소리 섞기가 실패했다 — 다시 뽑기로 섞기만 다시 한다
        }

        // 한 패스를 다시 뽑는 횟수 한도(빠른 재생이 어긋나 실제 속도로 다시 뽑는 것 포함). 간헐적인 어긋남은 다시 뽑으면 대개 맞는다.
        private const int MaxRetries = 3;

        private const string Idle = "Idle", Recording = "Recording", Rendering = "Rendering", Cancelling = "Cancelling";
        private const string StateFolder = ReplayManifest.StateFolder;

        private static State _state;
        private static string StatePath => Path.GetFullPath("Library/LocaleCapture/director.json");
        private static string AfterStateDir => Path.GetFullPath("Library/LocaleCapture/after");

        public static event Action<string> Completed;

        public static bool Busy => _state.Phase != Idle;
        public static bool IsRecording => _state.Phase == Recording;
        public static bool IsRendering => _state.Phase == Rendering;
        public static string Message => _state.Message ?? "";
        public static IReadOnlyList<string> Reports => _state.Reports;
        public static string Directory => _state.Directory;
        public static int PassIndex => _state.PassIndex;
        public static int PassCount => _state.Passes.Count;
        public static string CurrentPass => _state.PassIndex < _state.Passes.Count ? _state.Passes[_state.PassIndex] : "";

        public static float Progress
        {
            get
            {
                if (_state.Phase != Rendering || _state.Passes.Count == 0) return 0f;
                float inPass = ReplaySession.Current != null && !ReplaySession.Current.IsRecording ? ReplaySession.Current.Progress : 0f;
                return Mathf.Clamp01((_state.PassIndex + inPass) / _state.Passes.Count);
            }
        }

        public static float RemainingSeconds
        {
            get
            {
                float p = Progress;
                if (p < 0.02f || _state.StartedUtcTicks == 0) return -1f;
                double elapsed = (DateTime.UtcNow - new DateTime(_state.StartedUtcTicks, DateTimeKind.Utc)).TotalSeconds;
                return (float) (elapsed * (1 - p) / p);
            }
        }

        static CaptureDirector()
        {
            _state = Load();
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorApplication.update += Watch;
            CaptureBoot.Ended += OnSessionEnded;
            SetFactories();
            // 플레이 모드로 들어가며 스크립트가 다시 로드된 직후 — 첫 씬 전에 세션을 걸어 둔다.
            if ((_state.Phase == Recording || _state.Phase == Rendering) && EditorApplication.isPlayingOrWillChangePlaymode && !string.IsNullOrEmpty(_state.PendingRequestJson))
            {
                // 여기서 예외가 새면 이 클래스가 통째로 못 쓰게 된다(정적 생성자 실패) — 작업을 실패로 끝내고 창은 살려 둔다.
                try { Arm(); }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    string reason = "세션 준비 실패: " + e.Message;
                    EditorApplication.delayCall += () =>
                    {
                        if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
                        if (Busy) Finish(reason, false);
                    };
                }
            }
            // 에디터가 중간에 꺼졌다 켜진 경우: 진행 중 표시만 남아 있으면 정리하고 로컬 저장을 되돌린다.
            EditorApplication.delayCall += () =>
            {
                if (Busy && !WaitingForPlay && !EditorApplication.isPlayingOrWillChangePlaymode) Finish("이전 작업이 중단됐다", false);
            };
            // 작업 도중 에디터를 닫으면: 작업 동안 바꿔 둔 프로젝트 설정(Enter Play Mode 옵션·HTTP 허용·시작 씬)이 그대로 저장돼
            // 커밋될 수 있다 — 닫기 전에 되돌린다. 녹화 대기만 걸어 둔 상태면 작업도 끝낸다(다음에 켜서 누른 Play 가 녹화가 되지 않게).
            // 재생 중이었으면 로컬 저장 되돌리기는 다음에 켰을 때 한다(위 delayCall) — 지금 되돌리면 게임이 꺼지며 다시 덮어쓴다.
            EditorApplication.wantsToQuit += () =>
            {
                try
                {
                    if (!Busy) return true;
                    if (WaitingForPlay) Finish(Cancelled, false);
                    else { RestoreProjectSettings(); Save(); }
                }
                catch (Exception e) { Debug.LogException(e); }
                return true;
            };
        }

        // ── 시작 ───────────────────────────────────────────────────────────────

        // 녹화 대기. 시작 상태를 백업하고 첫 씬·패치를 준비해 두면, 사용자가 Play 를 눌렀을 때 부팅부터 기록된다.
        public static void StartRecording(string directory, CaptureSettings settings, List<string> locales, string customLanguageKey)
        {
            if (Busy || EditorApplication.isPlaying) return;
            _state = new State { Directory = directory, Settings = settings, Locales = locales.ToList(), CustomLanguageKey = customLanguageKey };
            BeginRecording();
        }

        public static bool WaitingForPlay => _state.Phase == Recording && !EditorApplication.isPlayingOrWillChangePlaymode;

        // 영상 시작 지점. 부팅부터 기록은 계속하고, 언어별 영상은 이 프레임부터 남긴다.
        public static void MarkStart() => ReplaySession.Current?.MarkVideoStart();

        // 이미 있는 기록을 지금 설정으로 다시 뽑는다(다시 플레이하지 않는다).
        public static void StartRender(string directory, CaptureSettings settings, List<string> locales, string customLanguageKey)
        {
            if (Busy || EditorApplication.isPlaying) return;
            ReplayManifest manifest = ReplayManifest.Load(directory);
            if (manifest == null) throw new InvalidOperationException("기록이 없다: " + directory);
            if (!LocalState.Exists(Path.Combine(directory, StateFolder))) throw new InvalidOperationException("이 기록에는 시작 상태 백업이 없다(이전 방식 기록) — 새로 기록해야 한다");
            _state = new State { Directory = directory, Settings = settings, Locales = locales.ToList(), CustomLanguageKey = customLanguageKey };
            LocalState.Save(AfterStateDir);
            _state.AfterState = AfterStateDir;
            BeginRendering();
        }

        // 최근 작업에서 녹화와 같게 끝나지 못한 것(언어 코드·스템 이름). 비어 있으면 다시 뽑을 게 없다.
        public static List<string> RetryTargets
        {
            get
            {
                var targets = new List<string>();
                if (Busy) return targets;
                foreach (string pass in _state.Unfinished)
                {
                    string label = pass.StartsWith("video:") ? LocaleOf(pass) : pass.ToUpperInvariant();
                    if (!targets.Contains(label)) targets.Add(label);
                }
                if (targets.Count == 0 && _state.RemixFailed) targets.Add("소리 섞기");
                return targets;
            }
        }

        // 최근 작업에서 어긋났거나 뽑지 못한 패스만 같은 기록·같은 설정으로 다시 뽑는다(다시 플레이하지 않는다). 맞게 끝난 언어의 영상은 그대로 둔다.
        public static void RetryFailed()
        {
            if (Busy || EditorApplication.isPlaying) return;
            State last = _state;
            if (last.Unfinished.Count == 0 && !last.RemixFailed) return;
            if (ReplayManifest.Load(last.Directory) == null) throw new InvalidOperationException("기록이 없다: " + last.Directory);
            if (!LocalState.Exists(Path.Combine(last.Directory, StateFolder))) throw new InvalidOperationException("이 기록에는 시작 상태 백업이 없다(이전 방식 기록) — 새로 기록해야 한다");
            // 다시 뽑을 패스는 "맞게 끝난 패스" 에서 뺀다(영상 비교에서만 달랐던 패스는 플레이 검사를 통과해 그 목록에 들어 있다).
            var done = new List<string>();
            var hashes = new List<string>();
            for (int i = 0; i < last.DonePasses.Count && i < last.DoneHashes.Count; i++)
            {
                if (last.Unfinished.Contains(last.DonePasses[i])) continue;
                done.Add(last.DonePasses[i]);
                hashes.Add(last.DoneHashes[i]);
            }
            _state = new State
            {
                Directory = last.Directory, Settings = last.Settings, Locales = last.Locales.ToList(), CustomLanguageKey = last.CustomLanguageKey,
                Partial = true, OnlyPasses = last.Unfinished.ToList(), DonePasses = done, DoneHashes = hashes,
            };
            LocalState.Save(AfterStateDir);
            _state.AfterState = AfterStateDir;
            BeginRendering();
        }

        public static void StopRecording() => ReplaySession.Current?.Stop();

        public static void Cancel()
        {
            if (!Busy) return;
            if (EditorApplication.isPlaying) { _state.Phase = Cancelling; Save(); EditorApplication.ExitPlaymode(); }
            else Finish(Cancelled, false);
        }

        private static void BeginRecording()
        {
            try
            {
                string dir = _state.Directory;
                System.IO.Directory.CreateDirectory(dir);
                _state.Message = "로컬 저장 백업";
                LocalState.Save(Path.Combine(dir, StateFolder));
                var request = new SessionRequest
                {
                    Type = SessionRequest.Kind.Record, Directory = dir, Fps = _state.Settings.FrameRate, Settings = _state.Settings,
                    Seed = UnityEngine.Random.Range(1, int.MaxValue), ClockBaseUtcTicks = DateTime.UtcNow.Ticks, BaseTickCount = Environment.TickCount,
                    LiveMarkers = LocaleCatalog.LiveMarkers(_state.Locales),
                    NoLoadDelay = true, CollapseLoads = true,
                };
                if (_state.Settings.Resolution == OutputResolution.Custom && !GameViewResolution.Matches(_state.Settings.CustomWidth, _state.Settings.CustomHeight))
                    GameViewResolution.Set(_state.Settings.CustomWidth, _state.Settings.CustomHeight);
                PlayViewSwitch.ShowSimulator();   // 사람이 하는 기록은 Simulator 에서
                if (_state.Settings.Resolution == OutputResolution.Custom)   // 출력 크기 그대로의 기기로 — 영상에 블러·늘림이 생기지 않게
                    SimulatorDevice.Use(_state.Settings.CustomWidth, _state.Settings.CustomHeight);
                _state.Phase = Recording;
                _state.Message = "Play 를 누르면 기록 시작";
                Enter(request, false);
            }
            catch (Exception e) { Finish("기록 시작 실패: " + e.Message, false); }
        }

        private static void BeginRendering()
        {
            ReplayManifest manifest = ReplayManifest.Load(_state.Directory);
            if (manifest != null && string.IsNullOrEmpty(manifest.RecordedLocale))
            {
                manifest.RecordedLocale = LocalState.GuessLocale(Path.Combine(_state.Directory, StateFolder), _state.Locales);
                if (!string.IsNullOrEmpty(manifest.RecordedLocale)) manifest.Save(_state.Directory);
                else Debug.LogWarning("[LocaleCapture] 녹화 언어를 알 수 없어 저장 값의 언어 표기를 바꾸지 못한다 — Language Pref Key 를 적거나 언어 시스템을 쓴다");
            }
            // 스템은 앞 언어 패스들이 함께 뽑는다("video:ko-KR|sfx") — 스템만 뽑는 패스를 따로 돌지 않게.
            // 언어가 스템보다 적을 때만 남은 스템을 따로 돈다.
            _state.Passes.Clear();
            if (_state.Settings.PerLocaleSfx && _state.Settings.Sfx)
            {
                // 언어별 SFX(음성): 모든 언어 패스가 자기 SFX 스템을 뽑고(BGM 음소거), BGM 은 영상 없는 가벼운 패스로 하나 뽑는다.
                foreach (string locale in _state.Locales) _state.Passes.Add("video:" + locale + "|sfx");
                if (_state.Settings.Bgm) _state.Passes.Add("bgm");
            }
            else
            {
                // 첫 언어 패스가 SFX 를, 둘째가 BGM 을 함께 뽑는다 — 모든 언어에 쓰는 SFX(음성 포함)는 첫 언어 것이어야 한다.
                var stems = new List<string>();
                if (_state.Settings.Sfx) stems.Add("sfx");
                if (_state.Settings.Bgm) stems.Add("bgm");
                for (int i = 0; i < _state.Locales.Count; i++)
                    _state.Passes.Add("video:" + _state.Locales[i] + (i < stems.Count ? "|" + stems[i] : ""));
                for (int i = _state.Locales.Count; i < stems.Count; i++) _state.Passes.Add(stems[i]);
            }
            // 실패한 것만 다시 뽑는 작업이면 그 패스만 돈다. 패스 이름(어느 언어가 어느 스템을 함께 뽑는지)은 전체를 뽑을 때와 같다 —
            // 스템을 함께 뽑던 언어를 다시 뽑으면 그 스템도 다시 뽑히고, 마무리에서 그 스템을 쓰는 영상 모두에 다시 섞인다.
            _state.AllPasses = _state.Passes.ToList();
            if (_state.Partial) _state.Passes = _state.AllPasses.Where(p => _state.OnlyPasses.Contains(p)).ToList();
            else { _state.DonePasses.Clear(); _state.DoneHashes.Clear(); }
            _state.Unfinished.Clear();
            _state.RemixFailed = false;
            _state.PassIndex = 0;
            _state.RetryingRealtime = false;
            _state.RetriedLive = false;
            _state.Retries = 0;
            _state.LastDivergence = null;
            _state.Stalled = false;
            try   // 지난번 뽑을 때 남긴 중간 검사 결과(빠른 재생·다시 뽑기 전 것)는 지운다
            {
                string verify = Path.Combine(_state.Directory, CaptureVerify.Folder);
                if (System.IO.Directory.Exists(verify))
                    foreach (string old in System.IO.Directory.GetFiles(verify, "*.fast.json").Concat(System.IO.Directory.GetFiles(verify, "*.try*.json")))
                        if (!_state.Partial || _state.Passes.Any(p => Path.GetFileName(old).StartsWith(ReportName(p) + ".", StringComparison.Ordinal))) File.Delete(old);
            }
            catch { /* 잠김 */ }
            _state.Reports.Clear();
            _state.PassHashes.Clear();
            _state.PassHashes.AddRange(_state.DoneHashes);   // 앞서 맞게 끝난 패스들과도 같은 플레이인지 본다
            _state.Phase = Rendering;
            _state.StartedUtcTicks = DateTime.UtcNow.Ticks;
            if (manifest == null || _state.AllPasses.Count == 0) { Finish("녹화할 언어가 없다", false); return; }
            if (_state.Passes.Count == 0) { Finish(null, true); return; }   // 뽑을 패스는 없고 마무리(소리 섞기)만 다시 한다
            StartPass();
        }

        private static void StartPass()
        {
            try
            {
                string dir = _state.Directory;
                ReplayManifest manifest = ReplayManifest.Load(dir);
                string pass = _state.Passes[_state.PassIndex];
                bool video = pass.StartsWith("video:");
                string stem = StemOf(pass);
                string locale = video ? LocaleOf(pass) : (string.IsNullOrEmpty(manifest.RecordedLocale) ? _state.Locales.FirstOrDefault() : manifest.RecordedLocale);
                bool realtime = _state.Settings.RealtimeReplay || _state.RetryingRealtime;
                _state.PassStartedUtcTicks = DateTime.UtcNow.Ticks;
                _state.Message =$"{_state.PassIndex + 1}/{_state.Passes.Count} {(video ? locale + " 녹화" + (stem != null ? " + " + stem.ToUpperInvariant() : "") : pass.ToUpperInvariant() + " 스템")}" +
                                 (_state.RetryingRealtime ? " 실제 속도로 다시" : _state.Retries > 0 ? " 다시" : "") + (_state.Retries > 1 ? $" ({_state.Retries}번째)" : "") + " 준비";
                LocalState.Restore(Path.Combine(dir, StateFolder), manifest.RecordedLocale, locale, _state.CustomLanguageKey);
                var request = new SessionRequest
                {
                    Type = video ? SessionRequest.Kind.Video : pass == "bgm" ? SessionRequest.Kind.BgmStem : SessionRequest.Kind.SfxStem,
                    Directory = dir, Locale = locale, Stem = video ? stem : null, Fps = manifest.Fps, Settings = _state.Settings,
                    OverrideSystemLanguage = LocaleCatalog.SystemLanguageOf(locale) != SystemLanguage.Unknown, SystemLanguage = LocaleCatalog.SystemLanguageOf(locale),
                    LiveMarkers = LocaleCatalog.LiveMarkers(_state.Locales.Concat(new[] { manifest.RecordedLocale })),
                    RecordedLocale = manifest.RecordedLocale, RecordedLocaleForms = LocaleCatalog.MatchForms(manifest.RecordedLocale),
                    Realtime = realtime,
                    NoLoadDelay = manifest.TraceVersion >= 4,   // 이전 형식 기록은 지연이 있는 채로 기록됐다
                    CollapseLoads = manifest.TraceVersion >= 5,
                };
                PlayViewSwitch.ShowGame();   // 언어별 재생은 Game 뷰에서 기록 때 화면 크기로
                if (_state.SavedGameViewMaximized < 0) _state.SavedGameViewMaximized = PlayViewSwitch.SetGameMaximized(true) ? 1 : 0;
                if (manifest.Width > 0 && !GameViewResolution.Matches(manifest.Width, manifest.Height)) GameViewResolution.Set(manifest.Width, manifest.Height);
                AllowLocalHttp();
                UnmuteAudio();
                Enter(request);
            }
            catch (Exception e) { Finish("패스 시작 실패: " + e.Message, false); }
        }

        // 첫 빌드 씬에서 플레이 모드를 연다(게임이 부팅부터 돈다).
        // play: 바로 플레이 모드로 들어갈지(언어별 패스). 녹화 대기는 사용자가 Play 를 누른다.
        private static void Enter(SessionRequest request, bool play = true)
        {
            _state.PendingRequestJson = JsonUtility.ToJson(request);
            _state.LastCompleted = false;
            _state.LastError = null;
            if (!_state.StartSceneOverridden)
            {
                _state.SavedStartScene = EditorSceneManager.playModeStartScene != null ? AssetDatabase.GetAssetPath(EditorSceneManager.playModeStartScene) : "";
                _state.StartSceneOverridden = true;
            }
            ForceCleanStart();
            // 씬을 Addressables 로 싣는 게임은 빌드 씬을 다 꺼 둔다 — 그땐 목록 첫 씬
            EditorBuildSettingsScene first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled) ?? EditorBuildSettings.scenes.FirstOrDefault();
            if (first != null) EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(first.path);
            Save();
            // 패치는 작업 전체에서 한 번만 건다(이미 걸려 있으면 그대로). 플레이 때 도메인을 다시 올리는 프로젝트면 정적 생성자의 Arm 이 다시 건다.
            Arm();
            if (play) EditorApplication.EnterPlaymode();
        }

        // 기록과 모든 재생 패스가 같은 상태에서 시작하게, 플레이 모드에 들어갈 때마다 도메인과 씬을 다시 올린다.
        // "Enter Play Mode Settings" 로 리로드를 꺼 둔 프로젝트는 정적 필드(캐시·싱글턴·초기화 여부·static Random)가 앞 플레이에서
        // 그대로 넘어온다 — 기록은 그 전 플레이의 상태에서, 재생은 기록·앞 언어 패스의 상태에서 시작해 로딩 개수와 타이밍이 달라진다.
        // 로컬 저장을 복원해도 이 상태는 맞춰지지 않는다. 끝나면 원래 설정으로 되돌린다(Finish).
        private static void ForceCleanStart()
        {
            if (_state.SavedEnterPlayMode >= 0) return;
            _state.SavedEnterPlayMode = EditorSettings.enterPlayModeOptionsEnabled ? 1 : 0;
            _state.SavedEnterPlayModeOptions = (int) EditorSettings.enterPlayModeOptions;
            if (EditorSettings.enterPlayModeOptionsEnabled) EditorSettings.enterPlayModeOptionsEnabled = false;
        }

        private static void RestoreEnterPlayMode()
        {
            if (_state.SavedEnterPlayMode < 0) return;
            if (EditorSettings.enterPlayModeOptionsEnabled != (_state.SavedEnterPlayMode == 1)) EditorSettings.enterPlayModeOptionsEnabled = _state.SavedEnterPlayMode == 1;
            if ((int) EditorSettings.enterPlayModeOptions != _state.SavedEnterPlayModeOptions) EditorSettings.enterPlayModeOptions = (EnterPlayModeOptions) _state.SavedEnterPlayModeOptions;
            _state.SavedEnterPlayMode = -1;
        }

        // 작업 동안 바꿔 둔 프로젝트·에디터 설정을 원래대로. 여러 번 불러도 된다.
        private static void RestoreProjectSettings()
        {
            if (_state.SavedInsecureHttp >= 0) { PlayerSettings.insecureHttpOption = (InsecureHttpOption) _state.SavedInsecureHttp; _state.SavedInsecureHttp = -1; }
            if (_state.SavedAudioMute >= 0) { EditorUtility.audioMasterMute = _state.SavedAudioMute == 1; _state.SavedAudioMute = -1; }
            RestoreEnterPlayMode();
            if (_state.StartSceneOverridden)
            {
                EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(_state.SavedStartScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(_state.SavedStartScene);
                _state.StartSceneOverridden = false;
            }
        }

        private static void Arm()
        {
            SetFactories();
            CaptureBoot.Pending = JsonUtility.FromJson<SessionRequest>(_state.PendingRequestJson);
            CaptureBoot.NoLoadDelay = CaptureBoot.Pending != null && CaptureBoot.Pending.NoLoadDelay;
            CaptureBoot.CollapseLoads = CaptureBoot.Pending != null && CaptureBoot.Pending.CollapseLoads;
            CapturePatches.Install();
            NotePatchProblems();
        }

        public const string PatchProblemsFile = "patch-problems.txt";

        // 바꿔 끼우지 못한 곳을 테이크의 verify 폴더에 남기고(콘솔에는 앞의 몇 건만 나온다), 결과 줄에 쓸 수를 기억해 둔다.
        private static void NotePatchProblems()
        {
            try
            {
                _state.PatchProblems = CapturePatches.Problems.Count;
                _state.PatchRisky = CapturePatches.CoreProblems.Select(p => "핵심 패치 — " + p).Concat(CapturePatches.RiskyProblems).ToList();
                if (string.IsNullOrEmpty(_state.Directory)) return;
                string dir = Path.Combine(_state.Directory, CaptureVerify.Folder);
                string file = Path.Combine(dir, PatchProblemsFile);
                if (CapturePatches.Problems.Count == 0) { if (File.Exists(file)) File.Delete(file); return; }
                System.IO.Directory.CreateDirectory(dir);
                var lines = new List<string>
                {
                    $"바꿔 끼우지 못한 곳 {CapturePatches.Problems.Count}건 — 이 메서드들 안의 시계·난수·입력 호출은 원래 것 그대로 돈다.",
                    $"재현에 영향을 줄 수 있는 것 {_state.PatchRisky.Count}건:",
                };
                lines.AddRange(_state.PatchRisky.Select(p => "  ! " + p));
                lines.Add("전체:");
                lines.AddRange(CapturePatches.Problems.Select(p => "  - " + p));
                File.WriteAllLines(file, lines);
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 패치 문제 목록 저장 실패: " + e.Message); }
        }

        private static void SetFactories()
        {
            // 동봉 ffmpeg 가 있으면 별도 프로세스에서 인코딩한다(게임이 인코딩을 기다리지 않는다). 없으면 Unity 내장 인코더.
            CaptureBoot.VideoSinkFactory = r => BundledFfmpeg.Available ? new FfmpegSink(r.Settings) : (IFrameSink) new MediaEncoderSink(r.Settings);
            CaptureBoot.WavSinkFactory = path => new WavSink(path);
        }

        // UnityWebRequest 재생용 테이프 서버(http://127.0.0.1)를 막지 않게 한다. 끝나면 되돌린다.
        // 게임뷰가 음소거면 렌더링한 소리도 비어 BGM·SFX 가 안 남을 수 있어 재생 동안 푼다(재생 소리는 파일로만 가고 스피커로 안 나간다).
        private static void UnmuteAudio()
        {
            if (_state.SavedAudioMute >= 0) return;
            _state.SavedAudioMute = EditorUtility.audioMasterMute ? 1 : 0;
            EditorUtility.audioMasterMute = false;
        }

        private static void AllowLocalHttp()
        {
            if (_state.SavedInsecureHttp >= 0) return;
            _state.SavedInsecureHttp = (int) PlayerSettings.insecureHttpOption;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        }

        // ── 진행 ───────────────────────────────────────────────────────────────

        private static void OnSessionEnded(SessionResult result)
        {
            _state.LastCompleted = result.Completed;
            _state.LastError = result.Error;
            _state.PendingRequestJson = null;
            Save();
            // 사용자가 Play 를 꺼서 끝난 경우엔 이 delayCall 이 다음 패스가 Play 를 연 뒤에 돈다 — 그 패스(대기 중 요청)는 끄지 않는다.
            EditorApplication.delayCall += () => { if (EditorApplication.isPlaying && string.IsNullOrEmpty(_state.PendingRequestJson)) EditorApplication.ExitPlaymode(); };
        }

        // ── 멈춤 감시 ───────────────────────────────────────────────────────────
        // 언어별 재생이 더 나아가지 않으면(세션이 안 뜸·프레임이 안 넘어감) 그 패스를 끊고 다시 뽑는다 — 그대로 두면 에디터를 강제로 끄고
        // 처음부터 다시 플레이해야 한다. 에디터 메인 스레드가 통째로 멈춘 경우는 여기서도 알 수 없다.

        private const double StallSeconds = 90;       // 세션 프레임이 이만큼 안 넘어가면(로딩 대기 한도 20초보다 넉넉히) 멈춘 것으로 본다
        private const double NoSessionSeconds = 120;  // 플레이는 도는데 세션이 이만큼 안 뜨면
        private const double NoPlaySeconds = 60;      // 플레이 모드로 못 들어간 채 이만큼 지나면(컴파일 오류 등)
        private static double _watchTick, _watchIdle;
        private static long _watchKey = long.MinValue;

        private static void Watch()
        {
            if (_state.Phase != Rendering) { _watchIdle = 0; _watchKey = long.MinValue; return; }
            double now = EditorApplication.timeSinceStartup;
            // 한 번에 1초까지만 센다 — 에디터가 잠깐 통째로 멈췄다 풀린 것(임포트·절전)을 재생이 멈춘 것으로 보지 않게.
            double step = Math.Min(1.0, Math.Max(0.0, now - _watchTick));
            _watchTick = now;

            bool playing = EditorApplication.isPlaying;
            // 일시정지(Console 의 Error Pause 등)는 풀고 이어 간다 — 재생은 프레임 단위라 멈췄다 가도 결과가 같다.
            if (playing && EditorApplication.isPaused)
            {
                EditorApplication.isPaused = false;
                const string note = "참고 — 재생 중 에디터가 일시정지돼(Console 의 Error Pause 등) 풀고 이어 갔다";
                if (!_state.Reports.Contains(note)) { _state.Reports.Add(note); Save(); }
            }
            ReplaySession session = playing ? ReplaySession.Current : null;
            bool entering = !playing && EditorApplication.isPlayingOrWillChangePlaymode;
            // 나아가고 있는지: 패스·플레이 여부·세션 프레임 가운데 하나라도 바뀌면 나아간 것.
            long key = ((long) _state.PassIndex << 40) ^ ((long) _state.Retries << 34) ^ (playing ? 1L << 33 : 0) ^ (entering ? 1L << 32 : 0) ^ (session != null ? (uint) session.Frame : uint.MaxValue);
            if (key != _watchKey || EditorApplication.isCompiling || EditorApplication.isUpdating) { _watchKey = key; _watchIdle = 0; return; }
            _watchIdle += step;
            double limit = session != null ? StallSeconds : playing || entering ? NoSessionSeconds : NoPlaySeconds;
            if (_watchIdle < limit) return;
            _watchIdle = 0;

            if (!playing && !entering)
            {
                // 플레이 모드에 들어가지 못했다 — 다시 시도해도 같다.
                Finish($"플레이 모드에 들어가지 못했다({(int) limit}초) — 콘솔에 컴파일 오류가 있는지 확인한다", false);
                return;
            }
            _state.Stalled = true;
            _state.StallNote = session != null ? $"재생이 프레임 {session.Frame} 에서 {(int) limit}초 동안 나아가지 않았다" : $"플레이 모드에서 {(int) limit}초 동안 재생이 시작되지 않았다";
            Save();
            Debug.LogWarning($"[LocaleCapture] {ReportName(CurrentPass)}: {_state.StallNote} — 이 패스를 끊고 다시 뽑는다");
            if (playing) EditorApplication.ExitPlaymode();   // 편집 모드로 돌아오면 OnPlayMode 가 이어받는다
            else { _state.Stalled = false; Finish($"{ReportName(CurrentPass)}: {_state.StallNote}", false); }   // 플레이로 넘어가다 선 경우 — 끊을 방법이 없다
        }

        private static void OnPlayMode(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode) return;
            switch (_state.Phase)
            {
                case Recording:
                    if (!_state.LastCompleted || ReplayManifest.Load(_state.Directory) == null) { Finish(_state.LastError ?? "START 전에 끝나 녹화하지 않았다", false); break; }
                    LocalState.Save(AfterStateDir);   // 기록 직후 상태 — 다 끝나면 여기로 되돌린다
                    _state.AfterState = AfterStateDir;
                    BeginRendering();
                    break;
                case Rendering:
                    string pass = CurrentPass;
                    // 멈춤 감시가 끊은 패스: 검사 결과가 없다(있어도 앞 시도의 것) — 보지 않고 다시 뽑는다.
                    if (_state.Stalled)
                    {
                        _state.Stalled = false;
                        _state.RetriedLive = false;
                        if (_state.Retries < MaxRetries)
                        {
                            _state.Retries++;
                            _state.Reports.Add($"다시 — {ReportName(pass)}: {_state.StallNote} — 끊고 다시 뽑았다");
                            _state.RetryingRealtime = true;
                            StartPass();
                        }
                        else Finish($"{ReportName(pass)}: {_state.StallNote} — {MaxRetries}번 다시 뽑아도 끝까지 가지 못했다", false);
                        break;
                    }
                    CaptureVerify.Report report = CaptureVerify.Load(_state.Directory, ReportName(pass));
                    // 실제 서버에서 받은 응답이 있었으면(주소에 언어가 든 요청, 녹화 땐 캐시를 써서 안 나갔던 요청) 그 패스를 한 번 더 뽑는다.
                    // 받는 동안 게임 시간을 멈추고 기다리는데, 그 사이에도 게임의 프레임 단위 로직은 돌아 흐름이 녹화보다 앞선다.
                    // 받은 응답은 언어별 테이프에 남으므로 다시 뽑을 땐 기다림 없이 녹화 프레임에 그대로 넘긴다.
                    if (_state.LastCompleted && !_state.RetriedLive && report != null && report.ServerFetched > 0)
                    {
                        _state.Reports.Add($"다시 — {report.Pass}: 실제 서버에서 받은 응답 {report.ServerFetched}개가 있어(이 언어로 처음) 받아 둔 응답으로 다시 뽑았다");
                        _state.RetriedLive = true;
                        StartPass();
                        break;
                    }
                    _state.RetriedLive = false;
                    // 재생이 녹화와 달라졌으면 그 패스만 실제 속도로 다시 뽑는다(파일은 덮어쓴다). 간헐적인 어긋남은 다시 뽑으면 대개 맞는다 —
                    // 한도(MaxRetries)까지 뽑되, 실제 속도 재생이 두 번 연달아 같은 곳에서 어긋나면 그 기록에서는 늘 그런 것으로 보고 그만둔다.
                    string diverged = _state.LastCompleted ? Divergence(report) : null;
                    if (diverged != null)
                    {
                        bool wasRealtime = _state.Settings.RealtimeReplay || _state.RetryingRealtime;
                        bool repeated = wasRealtime && diverged == _state.LastDivergence;
                        if (wasRealtime) _state.LastDivergence = diverged;
                        // 이전 형식 기록의 "누른 곳이 다름" 은 언어별 문구에 걸린 것일 수 있어 실패로 치지 않는다 — 실제 속도로 한 번만 다시 뽑아 본다.
                        bool soft = report.DivergedFrame < 0 && report.TraceVersion == 0 && report.TapMismatches.Count > 0;
                        bool again = soft ? !wasRealtime : !repeated && _state.Retries < MaxRetries;
                        if (again)
                        {
                            _state.Retries++;
                            _state.Reports.Add(wasRealtime
                                ? $"다시 — {report.Pass}: 실제 속도로 뽑은 플레이도 녹화와 달라({diverged}) 한 번 더 뽑았다 (다시 뽑기 {_state.Retries}번째)"
                                : $"다시 — {report.Pass}: 빠르게 뽑은 플레이가 녹화와 달라({diverged}) 실제 속도로 다시 뽑았다");
                            Debug.LogWarning($"[LocaleCapture] {report.Pass}: 재생이 녹화와 달라 실제 속도로 다시 뽑는다({_state.Retries}/{MaxRetries}) — {diverged}");
                            // 어긋난 시도의 검사 결과를 따로 남긴다(다시 뽑으면 같은 이름으로 덮어쓴다) — 어디서 어긋났었는지 볼 수 있게.
                            try
                            {
                                string verify = Path.Combine(_state.Directory, CaptureVerify.Folder, report.Pass + ".json");
                                string kept = wasRealtime ? $"{report.Pass}.try{_state.Retries}.json" : report.Pass + ".fast.json";
                                if (File.Exists(verify)) File.Copy(verify, Path.Combine(_state.Directory, CaptureVerify.Folder, kept), true);
                            }
                            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 어긋난 재생의 검사 결과 보관 실패: " + e.Message); }
                            _state.RetryingRealtime = true;
                            StartPass();
                            break;
                        }
                        if (repeated && !soft)
                            _state.Reports.Add($"참고 — {report.Pass}: 실제 속도로 두 번 연달아 같은 곳({diverged})에서 어긋나 더 다시 뽑지 않았다 — 이 기록에 재현되지 않는 구간이 있다(다시 플레이해도 같은 장면이면 같을 수 있다)");
                    }
                    // 패스는 끝까지 돌았어도 영상 파일이 안 나왔을 수 있다(인코더가 죽음·디스크 가득·이전 파일이 다른 프로그램에 열려 있음).
                    // 인코더가 죽은 경우는 다음 시도에서 다른 인코더로 넘어가므로 다시 뽑아 본다.
                    string missing = _state.LastCompleted && pass.StartsWith("video:") ? MissingVideo(LocaleOf(pass)) : null;
                    if (missing != null && _state.Retries < MaxRetries)
                    {
                        _state.Retries++;
                        _state.Reports.Add($"다시 — {LocaleOf(pass)}: {missing} — 다시 뽑았다");
                        StartPass();
                        break;
                    }
                    // 여기부터는 이 패스의 마지막 결과다.
                    _state.RetryingRealtime = false;
                    _state.Retries = 0;
                    _state.LastDivergence = null;
                    bool same = _state.LastCompleted && report != null && report.DivergedFrame < 0 &&
                                (_state.DoneHashes.Count == 0 || report.StateHash == HashOf(_state.DoneHashes[0]));
                    if (report != null) _state.PassHashes.Add(report.Pass + "=" + report.StateHash);
                    Judge(report);
                    if (!_state.LastCompleted) { Finish(_state.LastError ?? "녹화가 중단됐다", false); break; }
                    if (missing != null) { Finish(missing, false); break; }
                    if (same && !_state.DonePasses.Contains(pass))
                    {
                        _state.DonePasses.Add(pass);
                        _state.DoneHashes.Add(report.Pass + "=" + report.StateHash);
                    }
                    _state.PassIndex++;
                    if (_state.PassIndex < _state.Passes.Count) StartPass();
                    else Finish(null, true);
                    break;
                case Cancelling:
                    Finish(Cancelled, false);
                    break;
            }
        }

        public const string Cancelled = "취소";

        // 영상 패스가 끝났는데 그 언어 영상이 없거나 이번 패스에서 새로 쓰이지 않았으면 그 까닭을 돌려준다. 정상이면 null.
        private static string MissingVideo(string locale)
        {
            string name = ReplayManifest.Load(_state.Directory)?.Name;
            string path = Path.Combine(_state.Directory, $"{name}_{locale}.{_state.Settings.Extension}");
            if (!File.Exists(path)) return $"{locale} 영상 파일이 만들어지지 않았다 — 콘솔의 인코딩 오류를 확인한다(디스크 공간, 인코더)";
            if (_state.PassStartedUtcTicks > 0 && File.GetLastWriteTimeUtc(path).Ticks < _state.PassStartedUtcTicks)
                return $"{locale} 영상 파일이 새로 쓰이지 않았다(이전 파일이 그대로 남아 있다) — 그 파일이 다른 프로그램에 열려 있는지 확인한다";
            return null;
        }

        private static void Finish(string error, bool success)
        {
            try
            {
                if (!string.IsNullOrEmpty(_state.AfterState) && LocalState.Exists(_state.AfterState)) LocalState.Restore(_state.AfterState, null, null, null);
            }
            catch (Exception e) { Debug.LogError("[LocaleCapture] 로컬 저장 되돌리기 실패: " + e.Message); }
            RestoreProjectSettings();
            CapturePatches.Uninstall();
            CaptureBoot.Pending = null;
            CaptureBoot.NoLoadDelay = false;
            CaptureBoot.CollapseLoads = false;
            CaptureRender.EndLowRes();   // 세션이 못 되돌렸어도 렌더 스케일은 꼭 원래대로
            if (_state.SavedGameViewMaximized >= 0) { PlayViewSwitch.SetGameMaximized(_state.SavedGameViewMaximized == 1); _state.SavedGameViewMaximized = -1; }
            PlayViewSwitch.ShowSimulator();   // 다 끝나면 다시 Simulator 로
            string dir = _state.Directory;
            _state.Phase = Idle;
            _state.PendingRequestJson = null;
            _state.Message = success ? "완료: " + dir : "실패: " + error;
            // 녹화와 같게 끝나지 못한 패스(어긋났거나, 중단·취소로 뽑지 못한 것) — 창의 "다시 뽑기" 가 이것만 같은 기록으로 다시 뽑는다.
            _state.Unfinished = _state.AllPasses.Where(p => !_state.DonePasses.Contains(p)).ToList();
            _state.Stalled = false;
            if (_state.PatchRisky.Count > 0)
                _state.Reports.Add($"주의 — 패치: 게임 코드 {_state.PatchRisky.Count}곳의 시계·난수·입력 호출을 바꿔 끼우지 못했다 — 그 코드가 도는 장면은 재생마다 다르게 나올 수 있다 " +
                                   $"({Shorten(string.Join(" / ", _state.PatchRisky.Take(3)), 200)}). 목록: {CaptureVerify.Folder}/{PatchProblemsFile}");
            else if (_state.PatchProblems > 0)
                _state.Reports.Add($"참고 — 패치: {_state.PatchProblems}곳은 바꿔 끼우지 못했지만 프로젝트 코드의 시계·난수 생성·입력 호출은 남지 않았다(게임 난수, 패키지·플러그인 코드뿐). 목록: {CaptureVerify.Folder}/{PatchProblemsFile}");
            // 끝난 상태를 먼저 저장한다 — 아래 마무리(소리 섞기·영상 비교)는 오래 걸리고 파일 잠김으로 실패할 수 있는데,
            // 그 사이 에디터가 꺼지거나 예외가 나도 다음에 "작업 중" 으로 남아 있지 않게.
            Save();
            // 소리 섞기와 영상 비교는 영상을 통째로 읽는다(언어 수만큼) — 그동안 에디터가 멈춘 것처럼 보이지 않게 진행 막대를 띄운다.
            if (success) EditorUtility.DisplayProgressBar("Locale Capture", "마무리 — 소리를 섞고 언어 영상끼리 비교하는 중", 0.95f);
            try { FinishOutputs(success, dir); }
            finally { if (success) EditorUtility.ClearProgressBar(); }
            MarkVideoDifferences();
            Save();
            if (success) Debug.Log("[LocaleCapture] 완료 — " + string.Join(" · ", _state.Reports));
            else Debug.LogWarning("[LocaleCapture] " + _state.Message);
            // 실패(중단)했거나 녹화와 다르게 재생된 테이크는 보고서로 남긴다 — 창의 'GitHub 이슈로 보고' 가 쓴다.
            // 사용자가 직접 취소한 것은 문제가 아니므로 보고서를 만들지 않는다.
            if ((!success && error != Cancelled) || _state.Reports.Any(r => r.StartsWith("다름")))
            {
                try { IssueReport.Write(dir, _state.Message, _state.Reports, _state.Settings, _state.Locales); }
                catch (Exception e) { Debug.LogWarning("[LocaleCapture] 보고서 저장 실패: " + e.Message); }
            }
            if (success) EditorApplication.delayCall += () => Completed?.Invoke(dir);   // 편집 창은 열지 않는다(창 맨 아래 Edit Videos 로)
        }

        // 플레이 검사는 모두 통과했는데 영상 비교에서만 다른 영상이 나온 경우: 어느 언어 영상이 틀렸는지는 알 수 없다 —
        // 영상 패스를 모두 "다시 뽑기" 대상으로 둔다(다시 플레이할 필요는 없다).
        private static void MarkVideoDifferences()
        {
            if (_state.Unfinished.Count > 0 || !_state.Reports.Any(r => r.StartsWith("다름 — 영상 비교"))) return;
            _state.Unfinished = EveryPass().Where(p => p.StartsWith("video:")).ToList();
        }

        // 성공한 작업의 마무리: 스템 섞기, 패스끼리 비교, 영상 비교.
        private static void FinishOutputs(bool success, string dir)
        {
            if (success)
            {
                try { RemixStemPasses(); }
                catch (Exception e)
                {
                    Debug.LogError("[LocaleCapture] 소리 다시 섞기 실패: " + e.Message);
                    _state.RemixFailed = true;
                    _state.Reports.Add("다름 — 소리: 영상에 BGM·SFX 를 다시 섞지 못했다(" + Shorten(e.Message, 120) + ") — 영상 파일이 다른 프로그램에 열려 있는지 확인하고 창의 '다시 뽑기' 를 누른다");
                }
            }
            if (success && _state.PassHashes.Count > 0) _state.Reports.Add(CompareHashes(_state.PassHashes));
            if (success)
            {
                // 일부만 다시 뽑았어도 영상 비교는 이 기록의 모든 언어를 본다(앞서 뽑아 둔 영상 포함).
                try { _state.Reports.AddRange(VideoCompare.Run(dir, EveryPass().Where(p => p.StartsWith("video:")).Select(LocaleOf).ToList(), _state.Settings.Extension)); }
                catch (Exception e) { Debug.LogWarning("[LocaleCapture] 영상 비교 실패: " + e.Message); }
            }
        }

        // 한 패스의 검사 결과를 결과 줄로. "다름" 은 실패, "주의"·"참고" 는 성공으로 치고 따로 보여 준다.
        // 녹화 시작(START) 이후에 녹화와 프레임 하나라도 다르게 흘렀으면 실패다 — 덱 셔플·뽑기처럼 한 프레임 차이가 다른 플레이로 번진다.
        private static void Judge(CaptureVerify.Report report)
        {
            if (report == null) return;
            int fps = Mathf.Max(1, ReplayManifest.Load(_state.Directory)?.Fps ?? 30);
            if (report.DivergedFrame >= 0)
                _state.Reports.Add($"다름 — {report.Pass}: 프레임 {report.DivergedFrame}(영상 {Mathf.Max(0, report.DivergedFrame - report.VideoStartFrame) / (float) fps:F1}초)부터 " +
                                   $"녹화와 다르게 흘렀다 — [{report.DivergedKind}] {Shorten(report.DivergedDetail, 260)} (달라진 곳 모두 {report.DifferenceCount}건)");
            else if (report.TraceVersion == 0 && report.TapMismatches.Count > 0)   // 이전 기록: 맨 위 그래픽으로만 가려 언어별 문구에 걸렸을 수 있다
                _state.Reports.Add($"주의 — {report.Pass}: 녹화와 다른 곳이 눌림 {report.TapMismatches.Count}건, 처음 {Shorten(report.TapMismatches[0], 260)}");
            if (report.EarlyFrame >= 0)
                _state.Reports.Add($"참고 — {report.Pass}: 녹화 시작 전 프레임 {report.EarlyFrame} 에서 이미 녹화와 달랐다 — [{report.EarlyKind}] {Shorten(report.EarlyDetail, 200)}");
            // 로딩이 끝난 프레임 차이는 원인을 짚는 단서다 — 실제로 달라진 곳이 있을 때만 보여 준다(개수로 맞춰 보는 값이라 그것만으로는 뜻이 없다).
            if (report.Shifts.Count > 0 && (report.DivergedFrame >= 0 || report.EarlyFrame >= 0))
                _state.Reports.Add($"참고 — {report.Pass}: 녹화와 다른 프레임에 끝난 로딩 {string.Join(", ", report.Shifts.Take(4))}" + (report.HeldFrames > 0 ? $", 로딩 대기 {report.HeldFrames}프레임" : ""));
        }

        private static string Shorten(string s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";

        private static string LocaleOf(string pass)
        {
            string body = pass.StartsWith("video:") ? pass.Substring(6) : pass;
            int bar = body.IndexOf('|');
            return bar < 0 ? body : body.Substring(0, bar);
        }

        private static string StemOf(string pass)
        {
            int bar = pass.IndexOf('|');
            return bar < 0 ? null : pass.Substring(bar + 1);
        }

        // 스템을 함께 뽑은 영상은 소리가 그 스템뿐이라, 다 끝나면 BGM·SFX 스템을 섞어 소리를 원래대로 되돌린다.
        // SFX 스템은 언어별 파일이 있으면 그 언어 것(언어별 SFX), 없으면 공용.
        // 일부만 다시 뽑은 작업에서도 스템을 쓰는 영상은 모두 다시 섞는다 — 다시 뽑힌 스템이 앞서 뽑아 둔 영상에도 들어가야 한다(영상 줄기는 그대로 두고 소리만 바꾼다).
        private static void RemixStemPasses()
        {
            string bgm = Path.Combine(_state.Directory, EditList.BgmStem);
            if (!File.Exists(bgm) || !BundledFfmpeg.Available) return;
            string name = ReplayManifest.Load(_state.Directory)?.Name;
            foreach (string pass in EveryPass().Where(p => p.StartsWith("video:") && StemOf(p) != null))
            {
                string sfx = EditList.SfxStemFor(_state.Directory, LocaleOf(pass));
                if (sfx == null) continue;
                // 이번에 뽑은 형식의 파일만 — 형식을 바꿔 다시 뽑으면 같은 이름의 이전 형식 파일이 남아 있을 수 있다.
                string video = Path.Combine(_state.Directory, $"{name}_{LocaleOf(pass)}.{_state.Settings.Extension}");
                if (!File.Exists(video)) continue;
                string ext = Path.GetExtension(video);
                string temp = Path.Combine(_state.Directory, $"{name}_{LocaleOf(pass)}.remix{ext}");
                string codec = ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus -b:a 192k" : "aac -b:a 192k";
                string error = BundledFfmpeg.Run($"-y -i \"{video}\" -i \"{bgm}\" -i \"{sfx}\" -filter_complex \"[1:a][2:a]amix=inputs=2:duration=longest:normalize=0,apad[a]\" " +   // 소리를 무음으로 늘려 두고 영상 끝에서 끊는다 — 영상 프레임이 잘리지 않게
                                                 $"-map 0:v -map \"[a]\" -c:v copy -c:a {codec} -shortest \"{temp}\"");
                if (!string.IsNullOrEmpty(error) || !File.Exists(temp)) { Debug.LogWarning($"[LocaleCapture] {Path.GetFileName(video)} 소리 다시 섞기 실패: {error}"); continue; }
                File.Delete(video);
                File.Move(temp, video);
            }
        }

        // 재생이 녹화와 다른 길로 갔는지(다시 뽑을 이유). 같으면 null.
        // 누른 대상이 다르거나, 앞서 끝낸 패스들과 난수 상태 흐름이 다르면 다른 플레이다.
        private static string Divergence(CaptureVerify.Report report)
        {
            if (report == null) return null;
            if (report.DivergedFrame >= 0) return $"프레임 {report.DivergedFrame} [{report.DivergedKind}]";
            if (report.TapMismatches.Count > 0) return $"다른 곳이 눌림 {report.TapMismatches.Count}건";
            // 녹화와 같게 끝난 앞선 패스와 견준다(어긋난 패스와 견주면 맞게 뽑힌 뒤 언어들이 모두 "다른 플레이" 가 된다).
            if (_state.DoneHashes.Count > 0 && report.StateHash != HashOf(_state.DoneHashes[0])) return "앞선 언어와 다른 플레이";
            return null;
        }

        private static string HashOf(string passHash) => passHash.Substring(passHash.IndexOf('=') + 1);

        // 이 기록의 전체 패스(일부만 다시 뽑는 작업에서도). 이전 판이 남긴 진행 상태에는 없으므로 그땐 이번에 도는 패스.
        private static List<string> EveryPass() => _state.AllPasses.Count > 0 ? _state.AllPasses : _state.Passes;

        // 패스의 검사 결과 이름: 언어 영상은 언어 코드, 스템만 뽑는 패스는 스템 이름.
        private static string ReportName(string pass) => pass.StartsWith("video:") ? LocaleOf(pass) : pass;

        // 패스(언어별 영상·BGM·SFX)끼리 같은 플레이였는지. 다르면 같은 플레이끼리 묶어 보여 준다.
        private static string CompareHashes(List<string> passHashes)
        {
            var groups = passHashes.Select(p => p.Split('=')).GroupBy(p => p[1], p => p[0]).ToList();
            return groups.Count == 1
                ? (passHashes.Count == 1 ? "일치 — 한 패스" : "일치 — 모든 언어 영상과 BGM·SFX 가 같은 플레이")
                : "다름 — 같은 플레이끼리: " + string.Join(" / ", groups.Select(g => "[" + string.Join(", ", g) + "]"));
        }

        // ── 저장 ───────────────────────────────────────────────────────────────

        private static State Load()
        {
            try { if (File.Exists(StatePath)) return JsonUtility.FromJson<State>(File.ReadAllText(StatePath)) ?? new State(); }
            catch { /* 깨진 파일 */ }
            return new State();
        }

        private static void Save()
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
            File.WriteAllText(StatePath, JsonUtility.ToJson(_state, true));
        }
    }
}
