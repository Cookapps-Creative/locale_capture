using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화 대기 뒤 Play 를 누르면 게임을 첫 씬부터 기록하고(영상은 START 부터), STOP 하면 체크한 언어마다 같은 플레이를
    // 재생하며 녹화한다(CaptureDirector). 게임 코드는 참조하지 않는다. 출력 설정은 창에 그대로 보인다.
    public sealed class LocaleCaptureWindow : EditorWindow, IHasCustomMenu
    {
        private const string Prefix = "LocaleCapture.";

        private readonly HashSet<string> _selected = new HashSet<string>();
        private List<string> _locales = new List<string>();
        private string _manualLocales = "";
        private string _languageKey = "";
        private string _recordingsDir = "Recordings/LocaleCapture";
        private string _name = "capture";
        private string _lastDirectory = "";
        private CaptureSettings _settings = CaptureSettings.Default;
        private List<string> _health;
        private Vector2 _scroll;

        [MenuItem("Tools/Locale Capture")]
        private static void Open()
        {
            var w = GetWindow<LocaleCaptureWindow>("Locale Capture");
            w.minSize = new Vector2(440, 560);
            if (w.position.width < 500 || w.position.height < 720)
                w.position = new Rect(w.position.x, w.position.y, Mathf.Max(w.position.width, 500), Mathf.Max(w.position.height, 720));
        }

        // 편집 창 내보내기가 따르는 출력 설정(창에서 바꾸는 즉시 저장된다).
        internal static CaptureSettings SavedSettings()
        {
            string json = EditorPrefs.GetString(Prefix + "Settings", "");
            CaptureSettings s = string.IsNullOrEmpty(json) ? CaptureSettings.Default : JsonUtility.FromJson<CaptureSettings>(json);
            return s.FrameRate <= 0 ? CaptureSettings.Default : s;
        }

        private void OnEnable()
        {
            _recordingsDir = EditorPrefs.GetString(Prefix + "RecordingsDir", _recordingsDir);
            _name = EditorPrefs.GetString(Prefix + "Name", _name);
            _lastDirectory = EditorPrefs.GetString(Prefix + "LastRecording", "");
            _manualLocales = EditorPrefs.GetString(Prefix + "ManualLocales", "");
            _languageKey = EditorPrefs.GetString(Prefix + "LanguageKey", "");
            _settings = SavedSettings();
            if (_settings.Format == VideoFormat.MOV) _settings.Format = VideoFormat.MP4; // 내장 인코더는 MP4·WebM 만
            LoadLocales();
            RunHealthCheck();
            EditorApplication.update += Tick;
        }

        private void OnDisable() => EditorApplication.update -= Tick;

        // 진행 표시용 갱신. 매 업데이트마다 다시 그리면 플레이 중 프레임마다 창 전체를 그려 게임이 버벅인다.
        private double _nextRepaint;

        private void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextRepaint) return;
            _nextRepaint = now + 0.25;
            Repaint();
        }

        // 언어 목록: 언어 시스템에서 읽고, 없으면 직접 적은 코드. 처음 보는 언어는 체크해 둔다.
        private void LoadLocales()
        {
            _locales = LocaleCatalog.Codes();
            if (_locales.Count == 0) _locales = ManualCodes();
            string saved = EditorPrefs.GetString(Prefix + "Locales", null);
            _selected.Clear();
            if (saved == null) foreach (string c in _locales) _selected.Add(c);
            else foreach (string c in saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) _selected.Add(c);
            string known = EditorPrefs.GetString(Prefix + "KnownLocales", null);
            if (known != null)
            {
                var seen = new HashSet<string>(known.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (string c in _locales) if (!seen.Contains(c)) _selected.Add(c);
            }
            EditorPrefs.SetString(Prefix + "KnownLocales", string.Join(",", _locales));
        }

        private List<string> ManualCodes() =>
            _manualLocales.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Distinct().ToList();

        private void RunHealthCheck()
        {
            if (CaptureDirector.Busy) return;
            _health = CaptureHealth.Check(ManualCodes().Count);
            if (_health.Count > 0) Debug.LogWarning("[LocaleCapture] 점검\n- " + string.Join("\n- ", _health));
        }

        // 맨 위 상태 줄: 점검에 걸린 게 있으면 그것을, 통과면 최근 녹화 결과를 보여 준다.
        private void DrawStatus()
        {
            if (_health != null && _health.Count > 0)
            {
                Box("확인 필요 — 녹화가 실패하거나 언어마다 다른 영상이 나올 수 있다\n- " + string.Join("\n- ", _health), MessageType.Warning);
                if (GUI.Button(Row(" "), "다시 점검", EditorStyles.miniButton)) RunHealthCheck();
                return;
            }
            string message = CaptureDirector.Message;
            if (string.IsNullOrEmpty(message)) { Box("점검 통과", MessageType.Info); return; }
            // 성공 = 끝까지 녹화됐고 녹화와 다르게 재생된 곳이 없음. 자세한 내용은 콘솔·verify 폴더.
            bool failed = !message.StartsWith("완료") || CaptureDirector.Reports.Any(r => r.StartsWith("다름"));
            Box(failed ? "최근 녹화 실패" : "최근 녹화 성공", failed ? MessageType.Warning : MessageType.Info);
        }

        // Unity Recorder 처럼: 위에 녹화 버튼·상태 막대, 아래에 Description / Input / Output Format / Output File 구획.
        // 줄은 모두 Row() 한 곳에서 좌표를 정해(라벨 칸 고정, 같은 좌우 여백) EditorGUI 로 그린다 — 자동 배치 필드는 스타일마다 여백이 달라 들쭉날쭉하다.
        private const float LabelWidth = 150, Pad = 10, LabelIndent = 10, BottomBarHeight = 40;
        private float _topBarHeight = 46;

        private void OnGUI()
        {
            bool busy = CaptureDirector.Busy;
            DrawControlBar();
            bool bottom = HasBottomBar(busy);
            // 창 position 에는 탭 머리가 들어 있어, 그릴 수 있는 높이는 GUI 좌표로 잰다.
            float guiHeight = GUIUtility.ScreenToGUIPoint(new Vector2(position.x, position.yMax)).y;
            float height = Mathf.Max(60, guiHeight - _topBarHeight - (bottom ? BottomBarHeight : 0));
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.Height(height));
            if (!busy)
            {
                GUILayout.Space(8);
                DrawStatus();
                DrawDescription();
            }

            using (new EditorGUI.DisabledScope(busy))
            {
                EditorGUI.BeginChangeCheck();

                Section("Input");
                _settings.StartOnPlay = EditorGUI.Toggle(Row("바로 녹화"), _settings.StartOnPlay);
                _settings.RealtimeReplay = !EditorGUI.Toggle(Row(new GUIContent("빠른 녹화",
                    "언어별 영상을 실제 속도보다 빠르게 뽑는다. 결과물 속도는 같다. 녹화와 다르게 흘러간 언어는 실제 속도로 한 번 더 뽑는다.")), !_settings.RealtimeReplay);
                DrawLocales();
                DrawResolution();
                _settings.FrameRate = EditorGUI.IntSlider(Row("Frame Rate"), _settings.FrameRate, 15, 60);
                Rect audio = Row("Audio");
                _settings.Bgm = EditorGUI.ToggleLeft(new Rect(audio.x, audio.y, 70, audio.height), "BGM", _settings.Bgm);
                _settings.Sfx = EditorGUI.ToggleLeft(new Rect(audio.x + 76, audio.y, 70, audio.height), "SFX", _settings.Sfx);
                _settings.Audio = _settings.Bgm || _settings.Sfx;

                Section("Output Format");
                VideoFormat format = (VideoFormat) EditorGUI.EnumPopup(Row("Media File Format"), _settings.Format);
                _settings.Format = format == VideoFormat.MOV ? VideoFormat.MP4 : format;
                _settings.Quality = (VideoQuality) EditorGUI.EnumPopup(Row("Encoding Quality"), _settings.Quality);
                if (_settings.Quality == VideoQuality.Custom)
                    _settings.CustomBitrateMbps = Mathf.Clamp(EditorGUI.IntField(Row("Bitrate (Mbps)"), _settings.CustomBitrateMbps), 1, 200);

                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(Prefix + "Settings", JsonUtility.ToJson(_settings));

                Section("Output File");
                _name = EditorGUI.TextField(Row("File Name"), _name);
                Rect path = Row("Path");
                const float pick = 28, open = 48, gap = 4;
                _recordingsDir = EditorGUI.TextField(new Rect(path.x, path.y, path.width - pick - open - gap * 2, path.height), _recordingsDir);
                if (GUI.Button(new Rect(path.xMax - open - pick - gap, path.y, pick, path.height), "..."))
                {
                    string picked = EditorUtility.OpenFolderPanel("Path", ResolveDir(), "");
                    if (!string.IsNullOrEmpty(picked)) _recordingsDir = picked;
                }
                if (GUI.Button(new Rect(path.xMax - open, path.y, open, path.height), "Open"))
                {
                    Directory.CreateDirectory(ResolveDir());
                    EditorUtility.RevealInFinder(ResolveDir());
                }
                EditorGUI.LabelField(Row(" "), $"{_name}_<시각>/{_name}_<언어>.{_settings.Extension}", EditorStyles.miniLabel);
            }

            GUILayout.Space(10);
            EditorGUILayout.EndScrollView();
            if (bottom) DrawBottomBar();
        }

        // 한 줄: 왼쪽 라벨 칸(고정 폭) + 오른쪽 값 칸. 값 칸 Rect 를 돌려준다.
        private static Rect Row(string label) => Row(new GUIContent(label));

        private static Rect Row(GUIContent label)
        {
            Rect r = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            r.xMin += Pad;
            r.xMax -= Pad;
            if (!string.IsNullOrWhiteSpace(label.text)) EditorGUI.LabelField(new Rect(r.x + LabelIndent, r.y, LabelWidth - LabelIndent, r.height), label);
            return new Rect(r.x + LabelWidth, r.y, r.width - LabelWidth, r.height);
        }

        // 구획 제목과 같은 좌우 여백의 안내 상자.
        private void Box(string text, MessageType type)
        {
            float width = position.width - Pad * 2 - 20;
            float height = Mathf.Max(30, EditorStyles.helpBox.CalcHeight(new GUIContent(text), width - 40) + 6);
            Rect r = EditorGUILayout.GetControlRect(false, height);
            r.xMin += Pad;
            r.xMax -= Pad;
            EditorGUI.HelpBox(r, text, type);
        }

        // 녹화 순서 안내.
        private void DrawDescription()
        {
            Section("Description");
            EditorGUI.LabelField(Row("1. 녹화 대기"), "위 버튼을 누른다");
            EditorGUI.LabelField(Row("2. Play"), _settings.StartOnPlay ? "게임이 처음부터 기록되고, 바로 녹화가 시작된다" : "게임이 처음부터 기록된다");
            EditorGUI.LabelField(Row("3. START"), _settings.StartOnPlay ? "바로 녹화가 켜져 있어 누르지 않는다" : "녹화를 시작할 장면에서 누른다");
            EditorGUI.LabelField(Row("4. STOP"), "언어마다 자동으로 다시 재생·녹화");
            GUILayout.Space(4);
            EditorGUI.LabelField(Row("바로 녹화"), "Play 하는 순간부터 바로 녹화된다 (START 를 누르지 않는다)");
        }

        // 맨 아래 가운데: 최근 녹화 폴더 열기·편집.
        private static bool HasBottomBar(bool busy) =>
            !busy && !string.IsNullOrEmpty(CaptureDirector.Directory) && Directory.Exists(CaptureDirector.Directory);

        private static void DrawBottomBar()
        {
            string dir = CaptureDirector.Directory;
            using (new EditorGUILayout.VerticalScope(GUILayout.Height(BottomBarHeight)))
            {
                GUILayout.FlexibleSpace();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("폴더 열기", GUILayout.Width(110), GUILayout.Height(26))) EditorUtility.RevealInFinder(dir);
                    GUILayout.Space(8);
                    if (GUILayout.Button("Edit Videos", GUILayout.Width(110), GUILayout.Height(26))) VideoEditWindow.Open(dir);
                    GUILayout.FlexibleSpace();
                }
                GUILayout.FlexibleSpace();
            }
        }

        // 맨 위 막대: 녹화 시작·멈춤·진행 상태. Re-render·다른 녹화 편집은 창의 ⋮ 메뉴에.
        private void DrawControlBar()
        {
            Rect bar = EditorGUILayout.BeginVertical();
            EditorGUI.DrawRect(new Rect(bar.x, bar.y, position.width, bar.height), EditorGUIUtility.isProSkin ? new Color(0.16f, 0.16f, 0.16f) : new Color(0.78f, 0.78f, 0.78f));
            GUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                bool canRecord = SelectedLocales().Count > 0;
                if (!CaptureDirector.Busy)
                {
                    using (new EditorGUI.DisabledScope(!canRecord || EditorApplication.isPlaying))
                    {
                        GUI.backgroundColor = new Color(1f, 0.45f, 0.4f);
                        var record = new GUIContent("●  녹화 대기", EditorApplication.isPlaying ? "Play 를 멈춘 뒤" : "");
                        if (GUILayout.Button(record, BigButton, GUILayout.Width(190), GUILayout.Height(30))) StartRecording();
                        GUI.backgroundColor = Color.white;
                    }
                    GUILayout.FlexibleSpace();
                }
                else if (CaptureDirector.WaitingForPlay)
                {
                    GUILayout.Label("Play 를 누르세요", EditorStyles.boldLabel, GUILayout.Height(30));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Cancel", GUILayout.Width(70), GUILayout.Height(30))) CaptureDirector.Cancel();
                }
                else if (CaptureDirector.IsRecording)
                {
                    ReplaySession session = ReplaySession.Current;
                    bool marked = session != null && session.VideoStartMarked;
                    GUI.backgroundColor = new Color(1f, 0.45f, 0.4f);
                    using (new EditorGUI.DisabledScope(session == null))
                    {
                        if (!marked && GUILayout.Button("▶  START", BigButton, GUILayout.Width(190), GUILayout.Height(30))) CaptureDirector.MarkStart();
                        if (marked && GUILayout.Button("■  STOP", BigButton, GUILayout.Width(190), GUILayout.Height(30))) CaptureDirector.StopRecording();
                    }
                    GUI.backgroundColor = Color.white;
                    GUILayout.Space(8);
                    string time = session == null ? "게임 시작 중"
                        : marked ? $"녹화 중  {(session.Frame - session.VideoStartFrame) / (float) _settings.FrameRate:F1}s"
                        : "START 전 — 아직 녹화 안 됨";
                    GUILayout.Label(time, EditorStyles.boldLabel, GUILayout.Height(30));
                    GUILayout.FlexibleSpace();
                    if (!marked && GUILayout.Button("Cancel", GUILayout.Width(70), GUILayout.Height(30))) CaptureDirector.Cancel();
                }
                else
                {
                    using (new EditorGUILayout.VerticalScope()) DrawProgress();
                    if (GUILayout.Button("Cancel", GUILayout.Width(70), GUILayout.Height(30))) CaptureDirector.Cancel();
                }
                GUILayout.Space(8);
            }
            GUILayout.Space(8);
            EditorGUILayout.EndVertical();
            if (Event.current.type == EventType.Repaint) _topBarHeight = GUILayoutUtility.GetLastRect().height;   // 아래 스크롤 영역 높이 계산용
        }

        // 창 오른쪽 위 ⋮ 메뉴(Unity 창 메뉴)에 자주 안 쓰는 동작.
        public void AddItemsToMenu(GenericMenu menu)
        {
            if (SelectedLocales().Count > 0 && !EditorApplication.isPlaying && !CaptureDirector.Busy) menu.AddItem(new GUIContent("Re-render…"), false, ReRender);
            else menu.AddDisabledItem(new GUIContent("Re-render…"));
            menu.AddItem(new GUIContent("다른 녹화 편집…"), false, () =>
            {
                string dir = EditorUtility.OpenFolderPanel("녹화 폴더", Directory.Exists(_lastDirectory) ? _lastDirectory : ResolveDir(), "");
                if (!string.IsNullOrEmpty(dir)) VideoEditWindow.Open(dir);
            });
        }

        private static void DrawProgress()
        {
            float p = CaptureDirector.Progress;
            float remain = CaptureDirector.RemainingSeconds;
            string eta = remain < 0 ? "계산 중" : remain >= 3600 ? $"{(int) (remain / 3600)}시간 {(int) (remain % 3600 / 60)}분" : $"{(int) (remain / 60):00}:{(int) (remain % 60):00}";
            Rect bar = GUILayoutUtility.GetRect(10, 20, GUILayout.ExpandWidth(true));
            EditorGUI.ProgressBar(bar, p, $"{p * 100f:F0}%  ·  남은 시간 {eta}");
            ReplaySession session = ReplaySession.Current;
            string frames = session != null && !session.IsRecording ? $" · {session.Frame}/{session.TotalFrames}" : "";
            EditorGUILayout.LabelField(CaptureDirector.Message + frames, EditorStyles.miniLabel);
        }

        // 구획 제목(굵은 글씨 + 아래 얇은 줄). 줄들과 같은 좌우 여백.
        private static void Section(string title)
        {
            GUILayout.Space(14);
            Rect r = EditorGUILayout.GetControlRect(false, 20);
            r.xMin += Pad;
            r.xMax -= Pad;
            EditorGUI.LabelField(r, title, EditorStyles.boldLabel);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 1, r.width, 1), new Color(0.5f, 0.5f, 0.5f, 0.35f));
            GUILayout.Space(4);
        }

        private static GUIStyle _bigButton;
        private static GUIStyle BigButton => _bigButton ??= new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 12 };

        private void DrawResolution()
        {
            EditorGUI.BeginChangeCheck();
            _settings.Resolution = (OutputResolution) EditorGUI.EnumPopup(Row("Output Resolution"), _settings.Resolution);
            if (_settings.Resolution == OutputResolution.Custom)
            {
                Rect size = Row(" ");
                float half = (size.width - 8) / 2, labelWidth = EditorGUIUtility.labelWidth;
                EditorGUIUtility.labelWidth = 16;
                // 입력을 마칠 때만 반영한다(타자 중간 값으로 게임뷰가 바뀌지 않게).
                _settings.CustomWidth = Mathf.Clamp(EditorGUI.DelayedIntField(new Rect(size.x, size.y, half, size.height), "W", _settings.CustomWidth), 64, 8192) & ~1;
                _settings.CustomHeight = Mathf.Clamp(EditorGUI.DelayedIntField(new Rect(size.x + half + 8, size.y, half, size.height), "H", _settings.CustomHeight), 64, 8192) & ~1;
                EditorGUIUtility.labelWidth = labelWidth;
            }
            if (EditorGUI.EndChangeCheck() && _settings.Resolution == OutputResolution.Custom && !GameViewResolution.Matches(_settings.CustomWidth, _settings.CustomHeight))
                GameViewResolution.Set(_settings.CustomWidth, _settings.CustomHeight);
            if (_settings.Resolution == OutputResolution.Custom && !GameViewResolution.Matches(_settings.CustomWidth, _settings.CustomHeight))
            {
                Vector2 game = GameViewResolution.Current;
                EditorGUI.LabelField(Row(" "), $"Record 시 게임뷰 {(int) game.x}x{(int) game.y} → {_settings.CustomWidth}x{_settings.CustomHeight}", EditorStyles.miniLabel);
            }
        }

        // 언어 체크박스를 창 폭에 맞춰 여러 줄로 깐다. 언어 시스템이 없으면 코드를 직접 적는다.
        private void DrawLocales()
        {
            if (string.IsNullOrEmpty(LocaleCatalog.Source))
            {
                EditorGUI.BeginChangeCheck();
                _manualLocales = EditorGUI.DelayedTextField(Row("Language Codes"), _manualLocales);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(Prefix + "ManualLocales", _manualLocales);
                    LoadLocales();
                }
            }
            Rect head = Row("Languages");
            if (GUI.Button(new Rect(head.x, head.y, 44, head.height), "All", EditorStyles.miniButtonLeft)) foreach (string c in _locales) _selected.Add(c);
            if (GUI.Button(new Rect(head.x + 44, head.y, 48, head.height), "None", EditorStyles.miniButtonRight)) _selected.Clear();
            // 한 줄 개수는 창 폭으로 정한다 — 줄 Rect 폭은 배치 단계(Layout)에서 0 이라, 그걸로 세면 배치와 그리기의 줄 수가 달라져 아래 줄이 전부 밀린다.
            const float cell = 76, scrollbar = 16;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((position.width - Pad * 2 - LabelWidth - scrollbar - 6) / cell));
            for (int i = 0; i < _locales.Count; i += perRow)
            {
                Rect row = Row(" ");
                for (int k = i; k < Mathf.Min(i + perRow, _locales.Count); k++)
                {
                    string code = _locales[k];
                    bool on = EditorGUI.ToggleLeft(new Rect(row.x + (k - i) * cell, row.y, cell, row.height), code, _selected.Contains(code));
                    if (on) _selected.Add(code); else _selected.Remove(code);
                }
            }
            _languageKey = EditorGUI.TextField(Row("Language Pref Key"), _languageKey);
        }

        private List<string> SelectedLocales() => _locales.Where(_selected.Contains).ToList();

        private string ResolveDir()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            return Path.IsPathRooted(_recordingsDir) ? _recordingsDir : Path.Combine(root, _recordingsDir);
        }

        private void StartRecording()
        {
            SavePrefs();
            string dir = Path.Combine(ResolveDir(), $"{_name}_{DateTime.Now:yyyyMMdd_HHmmss}");
            _lastDirectory = dir;
            EditorPrefs.SetString(Prefix + "LastRecording", dir);
            CaptureDirector.StartRecording(dir, _settings, SelectedLocales(), _languageKey);
        }

        // 기존 기록을 다시 플레이하지 않고 지금 설정(해상도·품질·언어)으로 다시 녹화한다. 같은 폴더의 영상을 덮어쓴다.
        private void ReRender()
        {
            string start = Directory.Exists(_lastDirectory) ? _lastDirectory : ResolveDir();
            string dir = EditorUtility.OpenFolderPanel("다시 렌더할 기록 폴더", start, "");
            if (string.IsNullOrEmpty(dir)) return;
            SavePrefs();
            try
            {
                CaptureDirector.StartRender(dir, _settings, SelectedLocales(), _languageKey);
                _lastDirectory = dir;
                EditorPrefs.SetString(Prefix + "LastRecording", dir);
            }
            catch (Exception e) { EditorUtility.DisplayDialog("Re-render", e.Message, "확인"); }
        }

        private void SavePrefs()
        {
            EditorPrefs.SetString(Prefix + "RecordingsDir", _recordingsDir);
            EditorPrefs.SetString(Prefix + "Name", _name);
            EditorPrefs.SetString(Prefix + "Locales", string.Join(",", _selected));
            EditorPrefs.SetString(Prefix + "LanguageKey", _languageKey);
            EditorPrefs.SetString(Prefix + "Settings", JsonUtility.ToJson(_settings));
        }
    }
}
