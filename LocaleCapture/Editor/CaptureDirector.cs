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
            public int SavedGameViewMaximized = -1;   // 재생 동안 Game 뷰를 최대화(다른 창이 매 프레임 그리지 않게) — 끝나면 되돌린다
        }

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
            CaptureBoot.Ended += OnSessionEnded;
            SetFactories();
            // 플레이 모드로 들어가며 스크립트가 다시 로드된 직후 — 첫 씬 전에 세션을 걸어 둔다.
            if ((_state.Phase == Recording || _state.Phase == Rendering) && EditorApplication.isPlayingOrWillChangePlaymode && !string.IsNullOrEmpty(_state.PendingRequestJson))
                Arm();
            // 에디터가 중간에 꺼졌다 켜진 경우: 진행 중 표시만 남아 있으면 정리하고 로컬 저장을 되돌린다.
            EditorApplication.delayCall += () =>
            {
                if (Busy && !WaitingForPlay && !EditorApplication.isPlayingOrWillChangePlaymode) Finish("이전 작업이 중단됐다", false);
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

        public static void StopRecording() => ReplaySession.Current?.Stop();

        public static void Cancel()
        {
            if (!Busy) return;
            if (EditorApplication.isPlaying) { _state.Phase = Cancelling; Save(); EditorApplication.ExitPlaymode(); }
            else Finish("취소", false);
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
                var stems = new List<string>();
                if (_state.Settings.Bgm) stems.Add("bgm");
                if (_state.Settings.Sfx) stems.Add("sfx");
                for (int i = 0; i < _state.Locales.Count; i++)
                    _state.Passes.Add("video:" + _state.Locales[i] + (i < stems.Count ? "|" + stems[i] : ""));
                for (int i = _state.Locales.Count; i < stems.Count; i++) _state.Passes.Add(stems[i]);
            }
            _state.PassIndex = 0;
            _state.RetryingRealtime = false;
            _state.Reports.Clear();
            _state.PassHashes.Clear();
            _state.Phase = Rendering;
            _state.StartedUtcTicks = DateTime.UtcNow.Ticks;
            if (manifest == null || _state.Passes.Count == 0) { Finish("녹화할 언어가 없다", false); return; }
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
                _state.Message = $"{_state.PassIndex + 1}/{_state.Passes.Count} {(video ? locale + " 녹화" + (stem != null ? " + " + stem.ToUpperInvariant() : "") : pass.ToUpperInvariant() + " 스템")}" +
                                 (_state.RetryingRealtime ? " 실제 속도로 다시" : "") + " 준비";
                LocalState.Restore(Path.Combine(dir, StateFolder), manifest.RecordedLocale, locale, _state.CustomLanguageKey);
                var request = new SessionRequest
                {
                    Type = video ? SessionRequest.Kind.Video : pass == "bgm" ? SessionRequest.Kind.BgmStem : SessionRequest.Kind.SfxStem,
                    Directory = dir, Locale = locale, Stem = video ? stem : null, Fps = manifest.Fps, Settings = _state.Settings,
                    OverrideSystemLanguage = LocaleCatalog.SystemLanguageOf(locale) != SystemLanguage.Unknown, SystemLanguage = LocaleCatalog.SystemLanguageOf(locale),
                    LiveMarkers = LocaleCatalog.LiveMarkers(_state.Locales.Concat(new[] { manifest.RecordedLocale })),
                    RecordedLocale = manifest.RecordedLocale, RecordedLocaleForms = LocaleCatalog.MatchForms(manifest.RecordedLocale),
                    Realtime = realtime,
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
            // 씬을 Addressables 로 싣는 게임은 빌드 씬을 다 꺼 둔다 — 그땐 목록 첫 씬
            EditorBuildSettingsScene first = EditorBuildSettings.scenes.FirstOrDefault(s => s.enabled) ?? EditorBuildSettings.scenes.FirstOrDefault();
            if (first != null) EditorSceneManager.playModeStartScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(first.path);
            Save();
            // 패치는 작업 전체에서 한 번만 건다(이미 걸려 있으면 그대로). 플레이 때 도메인을 다시 올리는 프로젝트면 정적 생성자의 Arm 이 다시 건다.
            Arm();
            if (play) EditorApplication.EnterPlaymode();
        }

        private static void Arm()
        {
            SetFactories();
            CaptureBoot.Pending = JsonUtility.FromJson<SessionRequest>(_state.PendingRequestJson);
            CapturePatches.Install();
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
                    CaptureVerify.Report report = CaptureVerify.Load(_state.Directory, pass.StartsWith("video:") ? LocaleOf(pass) : pass);
                    // 빠르게 뽑은 플레이가 녹화와 달라졌으면 그 패스만 실제 속도로 한 번 더 뽑는다(파일은 덮어쓴다).
                    string diverged = _state.LastCompleted && !_state.Settings.RealtimeReplay && !_state.RetryingRealtime ? Divergence(report) : null;
                    if (diverged != null)
                    {
                        _state.Reports.Add($"다시 — {report.Pass}: 빠르게 뽑은 플레이가 녹화와 달라({diverged}) 실제 속도로 다시 뽑았다");
                        Debug.LogWarning($"[LocaleCapture] {report.Pass}: 빠르게 뽑은 플레이가 녹화와 달라 실제 속도로 다시 뽑는다 — {diverged}");
                        _state.RetryingRealtime = true;
                        StartPass();
                        break;
                    }
                    _state.RetryingRealtime = false;
                    if (report != null) _state.PassHashes.Add(report.Pass + "=" + report.StateHash);
                    // 화면이 몇 프레임 늦고 빨라도 누른 곳이 모두 같으면 결과는 같다 — 그땐 참고로만.
                    if (report != null && report.UiMismatches.Count > 0)
                        _state.Reports.Add($"{(report.TapMismatches.Count > 0 ? "다름" : "참고")} — {report.Pass}: 녹화와 다른 때 열리고 닫힌 화면 {report.UiMismatches.Count}건, 처음 {report.UiMismatches[0]}");
                    if (report != null && report.TapMismatches.Count > 0)
                        _state.Reports.Add($"다름 — {report.Pass}: 녹화와 다른 곳이 눌림 {report.TapMismatches.Count}건, 처음 {report.TapMismatches[0]}");
                    if (report != null && report.InputDelivered < report.InputQueued)
                        _state.Reports.Add($"다름 — {report.Pass}: 녹화한 입력 {report.InputQueued}개 중 {report.InputDelivered}개만 들어감 ({report.InputDevices})");
                    if (!_state.LastCompleted) { Finish(_state.LastError ?? "녹화가 중단됐다", false); break; }
                    _state.PassIndex++;
                    if (_state.PassIndex < _state.Passes.Count) StartPass();
                    else Finish(null, true);
                    break;
                case Cancelling:
                    Finish("취소", false);
                    break;
            }
        }

        private static void Finish(string error, bool success)
        {
            try
            {
                if (!string.IsNullOrEmpty(_state.AfterState) && LocalState.Exists(_state.AfterState)) LocalState.Restore(_state.AfterState, null, null, null);
            }
            catch (Exception e) { Debug.LogError("[LocaleCapture] 로컬 저장 되돌리기 실패: " + e.Message); }
            if (_state.SavedInsecureHttp >= 0) { PlayerSettings.insecureHttpOption = (InsecureHttpOption) _state.SavedInsecureHttp; _state.SavedInsecureHttp = -1; }
            if (_state.SavedAudioMute >= 0) { EditorUtility.audioMasterMute = _state.SavedAudioMute == 1; _state.SavedAudioMute = -1; }
            if (_state.StartSceneOverridden)
            {
                EditorSceneManager.playModeStartScene = string.IsNullOrEmpty(_state.SavedStartScene) ? null : AssetDatabase.LoadAssetAtPath<SceneAsset>(_state.SavedStartScene);
                _state.StartSceneOverridden = false;
            }
            CapturePatches.Uninstall();
            CaptureBoot.Pending = null;
            CaptureRender.EndLowRes();   // 세션이 못 되돌렸어도 렌더 스케일은 꼭 원래대로
            if (_state.SavedGameViewMaximized >= 0) { PlayViewSwitch.SetGameMaximized(_state.SavedGameViewMaximized == 1); _state.SavedGameViewMaximized = -1; }
            PlayViewSwitch.ShowSimulator();   // 다 끝나면 다시 Simulator 로
            string dir = _state.Directory;
            _state.Phase = Idle;
            _state.PendingRequestJson = null;
            _state.Message = success ? "완료: " + dir : "실패: " + error;
            if (success) RemixStemPasses();
            if (success && _state.PassHashes.Count > 0) _state.Reports.Add(CompareHashes(_state.PassHashes));
            Save();
            if (success) Debug.Log("[LocaleCapture] 완료 — " + string.Join(" · ", _state.Reports));
            else Debug.LogWarning("[LocaleCapture] " + _state.Message);
            if (success) EditorApplication.delayCall += () => Completed?.Invoke(dir);   // 편집 창은 열지 않는다(창 맨 아래 Edit Videos 로)
        }

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
        private static void RemixStemPasses()
        {
            string bgm = Path.Combine(_state.Directory, EditList.BgmStem);
            if (!File.Exists(bgm) || !BundledFfmpeg.Available) return;
            string name = ReplayManifest.Load(_state.Directory)?.Name;
            foreach (string pass in _state.Passes.Where(p => p.StartsWith("video:") && StemOf(p) != null))
            {
                string sfx = EditList.SfxStemFor(_state.Directory, LocaleOf(pass));
                if (sfx == null) continue;
                string video = System.IO.Directory.GetFiles(_state.Directory, $"{name}_{LocaleOf(pass)}.*")
                    .FirstOrDefault(f => !f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));
                if (video == null) continue;
                string ext = Path.GetExtension(video);
                string temp = Path.Combine(_state.Directory, $"{name}_{LocaleOf(pass)}.remix{ext}");
                string codec = ext.Equals(".webm", StringComparison.OrdinalIgnoreCase) ? "libopus -b:a 192k" : "aac -b:a 192k";
                string error = BundledFfmpeg.Run($"-y -i \"{video}\" -i \"{bgm}\" -i \"{sfx}\" -filter_complex \"[1:a][2:a]amix=inputs=2:duration=first:normalize=0[a]\" " +
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
            if (report.TapMismatches.Count > 0) return $"다른 곳이 눌림 {report.TapMismatches.Count}건";
            if (_state.PassHashes.Count > 0)
            {
                string first = _state.PassHashes[0].Split('=')[1];
                if (report.StateHash != first) return "앞선 언어와 다른 플레이";
            }
            return null;
        }

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
