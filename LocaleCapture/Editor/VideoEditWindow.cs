using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화된 언어 영상을 보면서 구간 삭제·클립 삽입·BGM/SFX 구간 음소거를 지정하고, 모든 언어에 같은 편집을 적용해 내보낸다.
    // 미리보기는 편집을 반영해 돈다: 삭제 구간은 건너뛰고, 삽입 지점에서는 클립을 틀고, 음소거 구간은 해당 소리를 끈다.
    // 에디트 모드에서는 VideoPlayer 가 돌지 않아 EditPreview(ffmpeg 로 뽑은 프레임·소리)로 보여 준다.
    // 구성: 툴바 → 미리보기 → 트랜스포트(+Export) → 편집 버튼 → 눈금자(In/Out 핸들) + Video/BGM/SFX 트랙 → 편집 목록.
    // 단축키: Space 재생/정지, ←/→ 프레임(Shift ×10), Home/End, I/O 구간, Delete 구간 삭제, B/S 구간 BGM/SFX 음소거, M 전체 음소거, Esc.
    public sealed class VideoEditWindow : EditorWindow
    {
        private const float RulerHeight = 22f, TrackHeight = 30f, TrackGap = 3f, HandleWidth = 8f;
        private static readonly Color CutColor = new Color(0.85f, 0.25f, 0.25f, 0.9f);
        private static readonly Color MuteColor = new Color(0.95f, 0.75f, 0.2f, 0.9f);
        private static readonly Color InsertColor = new Color(0.3f, 0.6f, 1f, 0.95f);
        private static readonly Color SelectionColor = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color SelectedTint = new Color(0.55f, 0.75f, 1f);

        private enum Drag { None, Scrub, In, Out }
        private enum Kind { Cut, Mute, Insert, Speed, Fade }
        private static readonly Color SpeedColor = new Color(0.75f, 0.5f, 1f, 0.95f);
        private static readonly Color FadeColor = new Color(0.12f, 0.12f, 0.12f, 0.95f);
        private const float MinSpeed = 0.1f, MaxSpeed = 10f;
        private float _speedRate = 2f;   // Speed 버튼이 넣을 배속

        // 0.1 단위 배속 슬라이더.
        private static float SpeedSlider(float rate, float width) =>
            Mathf.Round(Mathf.Clamp(EditorGUILayout.Slider(rate, MinSpeed, MaxSpeed, GUILayout.Width(width)), MinSpeed, MaxSpeed) * 10f) / 10f;

        private string _directory;
        private ReplayManifest _manifest;
        private EditList _edits = new EditList();
        private string[] _locales = Array.Empty<string>();
        private int _previewLocale;

        // 미리보기
        private EditPreview.Frames _frames;
        private string _previewError;
        private readonly Dictionary<string, EditPreview.Frames> _clipFrames = new Dictionary<string, EditPreview.Frames>();
        private string _sourceAudio;   // 미리듣기 소리 WAV 경로(편집이 바뀌면 다시 만든다)
        private float _time;                 // 원본 기준 플레이헤드
        private bool _playing;
        private int _activeInsert = -1;      // 지금 재생 중인 삽입 클립
        private float _clipTime;
        private readonly HashSet<int> _playedInserts = new HashSet<int>();
        private readonly Dictionary<string, float> _clipDurations = new Dictionary<string, float>();
        private double _lastTick, _lastRepaint;
        private float _curRate = 1f;         // 재생 중인 원본 구간의 배속
        private int _previewRatio = -1;      // 미리보기 화면 비율(Ratios 인덱스). -1 = 원본
        private EditHistory _history;        // 실행 취소 대상

        // 편집 상태
        private float _in = -1, _out = -1;
        private Drag _drag;
        private (Kind kind, int index)? _selected;
        private Vector2 _listScroll;

        // 내보낼 화면 비율. 고른 비율마다 언어별 파일이 나온다. 크기는 녹화 설정 해상도의 짧은 변 기준.
        private static readonly (string label, int aw, int ah)[] Ratios =
        {
            ("9:16", 9, 16), ("1:1", 1, 1), ("16:9", 16, 9), ("4:5", 4, 5),
        };
        private const string RatioPrefKey = "CookApps.LocaleCapture.ExportRatios";
        private int _ratioMask = -1;

        private int RatioMask
        {
            get { if (_ratioMask < 0) _ratioMask = EditorPrefs.GetInt(RatioPrefKey, 1); return _ratioMask; }
            set { _ratioMask = value; EditorPrefs.SetInt(RatioPrefKey, value); }
        }

        public static void Open(string directory)
        {
            var w = GetWindow<VideoEditWindow>("Edit Videos");
            w.minSize = new Vector2(820, 760);
            w.Load(directory);
        }

        // ── 폴더 ──────────────────────────────────────────────────────────────

        private void Load(string directory)
        {
            _directory = FindVideoFolder(directory) ?? directory;
            _manifest = ReplayManifest.Load(_directory) ?? Synthesize(_directory);
            _edits = EditList.Load(_directory);
            InitHistory();
            _locales = _manifest == null ? Array.Empty<string>() : VideoFiles(_directory)
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => n.StartsWith(_manifest.Name + "_"))
                .Select(n => n.Substring(_manifest.Name.Length + 1))
                .Distinct().OrderBy(n => n).ToArray();
            _previewLocale = Mathf.Max(0, Array.IndexOf(_locales, _manifest?.RecordedLocale));
            _in = _out = -1;
            _selected = null;
            _clipDurations.Clear();
            titleContent = new GUIContent("Edit Videos");
            OpenPreview();
        }

        // 녹화 결과 영상(언어별 mp4/webm). 인코딩 중간 파일(*.video.*)은 뺀다.
        private static IEnumerable<string> VideoFiles(string directory)
            => Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.mp4").Concat(Directory.GetFiles(directory, "*.webm"))
                    .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith(".video"))
                : Enumerable.Empty<string>();

        // 고른 폴더에 영상이 없으면 하위 폴더에서 가장 최근 녹화 폴더를 찾는다(녹화 폴더 안에 또 녹화한 경우 등).
        private static string FindVideoFolder(string directory)
        {
            if (!Directory.Exists(directory)) return null;
            if (VideoFiles(directory).Any()) return directory;
            return Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d) != "edited" && VideoFiles(d).Any())
                .OrderByDescending(Directory.GetLastWriteTime)
                .FirstOrDefault();
        }

        // manifest.json 이 없는 폴더: 파일명 "<이름>_<언어>.mp4" 에서 이름·언어를, 영상에서 크기·fps 를 읽는다.
        private static ReplayManifest Synthesize(string directory)
        {
            string first = VideoFiles(directory).OrderBy(f => f).FirstOrDefault();
            if (first == null) return null;
            string stem = Path.GetFileNameWithoutExtension(first);
            int cut = stem.LastIndexOf('_');
            var manifest = new ReplayManifest { Name = cut > 0 ? stem.Substring(0, cut) : stem, Fps = 30 };
            if (BundledFfmpeg.Available)
            {
                BundledFfmpeg.MediaInfo info = BundledFfmpeg.Probe(first);
                manifest.Width = info.Width;
                manifest.Height = info.Height;
                if (info.Fps > 0) manifest.Fps = Mathf.RoundToInt(info.Fps);
            }
            if (manifest.Width <= 0 || manifest.Height <= 0) { manifest.Width = 1080; manifest.Height = 1920; }
            return manifest;
        }

        private string VideoFile(string locale)
        {
            string mp4 = Path.Combine(_directory, $"{_manifest.Name}_{locale}.mp4");
            return File.Exists(mp4) ? mp4 : Path.Combine(_directory, $"{_manifest.Name}_{locale}.webm");
        }

        private string BgmStem => File.Exists(Path.Combine(_directory, EditList.BgmStem)) ? Path.Combine(_directory, EditList.BgmStem) : null;
        private string SfxStem => File.Exists(Path.Combine(_directory, EditList.SfxStem)) ? Path.Combine(_directory, EditList.SfxStem) : null;
        private bool HasStems => BgmStem != null || SfxStem != null;

        // ── 미리보기 ───────────────────────────────────────────────────────────

        private void OpenPreview()
        {
            ClosePreview();
            if (_manifest == null || _locales.Length == 0) return;
            try
            {
                EditorUtility.DisplayProgressBar("Edit Videos", "미리보기 준비 중", 0.5f);
                _frames = EditPreview.BuildFrames(VideoFile(_locales[_previewLocale]), out _previewError);
            }
            finally { EditorUtility.ClearProgressBar(); }
            _time = 0;
        }

        private void ClosePreview()
        {
            StopPlayback();
            _activeInsert = -1;
            _frames?.Dispose(); _frames = null;
            foreach (EditPreview.Frames f in _clipFrames.Values) f?.Dispose();
            _clipFrames.Clear();
            _sourceAudio = null;
        }

        private void OnEnable()
        {
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += OnUndoRedo;
            wantsMouseMove = true;
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= OnUndoRedo;
            ClosePreview();
        }

        private void OnDestroy()
        {
            if (_history == null) return;
            Undo.ClearUndo(_history);
            DestroyImmediate(_history);
        }

        private void InitHistory()
        {
            if (_history == null) _history = CreateInstance<EditHistory>();
            _history.hideFlags = HideFlags.DontSave;
            _history.Json = JsonUtility.ToJson(_edits);
            Undo.ClearUndo(_history);
        }

        // Ctrl+Z·Ctrl+Y 로 되돌린 편집을 창과 edit.json 에 반영한다.
        private void OnUndoRedo()
        {
            if (_history == null || _directory == null || _history.Json == JsonUtility.ToJson(_edits)) return;
            StopPlayback();
            _activeInsert = -1;
            _edits = JsonUtility.FromJson<EditList>(_history.Json).Normalized();
            _selected = null;
            _edits.Save(_directory);
            Repaint();
        }

        private float Duration => _frames?.Duration ?? 0f;
        private float FrameStep => 1f / Mathf.Max(1, _manifest?.Fps ?? 30);

        private EditPreview.Frames ClipFrames(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (_clipFrames.TryGetValue(file, out EditPreview.Frames f)) return f;
            f = EditPreview.BuildFrames(file, out string error);
            if (f == null) Debug.LogWarning("[LocaleCapture] 삽입 클립 미리보기 실패: " + error);
            _clipFrames[file] = f;
            return f;
        }

        // 음소거 구간이 바뀌면 원본 소리를 다시 만든다(캐시되므로 같은 편집이면 즉시).
        private string SourceAudio()
        {
            _sourceAudio = EditPreview.BuildSourceAudio(VideoFile(_locales[_previewLocale]), BgmStem, SfxStem, _edits.Mutes, Duration);
            return _sourceAudio;
        }

        private string ClipFile(int insert) => _edits.Inserts[insert].FileFor(_locales[_previewLocale]);

        private void PlayAudio(string main, float time) => EditPreview.PlaySound(main, time);

        // 원본 소리. 배속 구간에서는 미리보기 소리를 끈다(음원만 남는다).
        private void PlaySourceAudio() => PlayAudio(Mathf.Abs(_curRate - 1f) < 0.001f ? SourceAudio() : null, _time);

        private string ClipAudio(int index)
        {
            EditList.Insert ins = _edits.Inserts[index];
            return EditPreview.BuildClipAudio(ClipFile(index), ins.MuteBgm, ins.MuteSfx);
        }

        private void Tick()
        {
            if (!_playing || _frames == null) return;
            double now = EditorApplication.timeSinceStartup;
            float dt = Mathf.Min((float) (now - _lastTick), 0.25f);
            _lastTick = now;

            if (_activeInsert >= 0)
            {
                _clipTime += dt;
                EditPreview.Frames cf = ClipFrames(ClipFile(_activeInsert));
                if (cf == null || _clipTime >= cf.Duration) EndClip();
            }
            else
            {
                float prev = _time, next = _time + dt * _edits.RateAt(_time);
                // 삽입 지점을 지나면 클립을 튼다
                for (int i = 0; i < _edits.Inserts.Count; i++)
                {
                    float at = _edits.Inserts[i].At;
                    if (_playedInserts.Contains(i) || at < prev || at > next || string.IsNullOrEmpty(ClipFile(i))) continue;
                    _time = at;
                    StartClip(i);
                    RepaintThrottled(now);
                    return;
                }
                // 삭제 구간은 건너뛴다
                bool jumped = false;
                foreach (EditList.Range c in _edits.Cuts)
                {
                    float s = Mathf.Min(c.Start, c.End), e = Mathf.Max(c.Start, c.End);
                    if (next >= s && next < e) { next = e; jumped = true; }
                }
                _time = Mathf.Min(next, Duration);
                float rate = _edits.RateAt(_time);
                if (jumped || Mathf.Abs(rate - _curRate) > 0.001f) { _curRate = rate; PlaySourceAudio(); }
                if (_time >= Duration) StopPlayback();
            }
            RepaintThrottled(now);
        }

        private void RepaintThrottled(double now)
        {
            if (now - _lastRepaint < 1.0 / 30) return;
            _lastRepaint = now;
            Repaint();
        }

        private void StartClip(int index)
        {
            _activeInsert = index;
            _clipTime = 0;
            _playedInserts.Add(index);
            PlayAudio(ClipAudio(index), 0);
        }

        private void EndClip()
        {
            _activeInsert = -1;
            _curRate = _edits.RateAt(_time);
            PlaySourceAudio();
        }

        private void TogglePlay()
        {
            if (_frames == null) return;
            if (_playing) { StopPlayback(); return; }
            if (_activeInsert < 0 && _time >= Duration - FrameStep) _time = 0;
            // 플레이헤드 앞의 삽입은 이미 지난 것으로 본다
            _playedInserts.Clear();
            for (int i = 0; i < _edits.Inserts.Count; i++) if (_edits.Inserts[i].At < _time) _playedInserts.Add(i);
            if (_activeInsert >= 0) _playedInserts.Add(_activeInsert);
            // 삭제 구간 안에서 시작하면 끝으로
            foreach (EditList.Range c in _edits.Cuts)
                if (_time >= Mathf.Min(c.Start, c.End) && _time < Mathf.Max(c.Start, c.End)) _time = Mathf.Max(c.Start, c.End);

            _curRate = _edits.RateAt(_time);
            if (_activeInsert >= 0) PlayAudio(ClipAudio(_activeInsert), _clipTime);
            else PlaySourceAudio();
            _lastTick = EditorApplication.timeSinceStartup;
            _playing = true;
        }

        private void StopPlayback()
        {
            _playing = false;
            EditPreview.StopSound();
        }

        private void Seek(float t)
        {
            StopPlayback();
            _activeInsert = -1;
            _time = Mathf.Clamp(t, 0, Duration);
            Repaint();
        }

        // 결과 영상에 남는 조각(표시 시각 기준)과 배속. 삭제 구간은 빠지고, 원본은 배속 경계에서 나뉜다.
        private struct Seg { public float D0, D1, Rate; public bool Clip; }

        private List<Seg> ResultSegments()
        {
            var segs = new List<Seg>();
            if (Duration <= 0) return segs;
            var cuts = _edits.Cuts.Select(c => (a: Mathf.Clamp(Mathf.Min(c.Start, c.End), 0, Duration), b: Mathf.Clamp(Mathf.Max(c.Start, c.End), 0, Duration)))
                .Where(c => c.b > c.a).OrderBy(c => c.a).ToList();
            var keep = new List<(float a, float b)>();
            float cursor = 0;
            foreach ((float a, float b) in cuts)
            {
                if (a > cursor) keep.Add((cursor, a));
                cursor = Mathf.Max(cursor, b);
            }
            if (cursor < Duration) keep.Add((cursor, Duration));
            foreach ((float a, float b) in keep)
            {
                var bounds = new SortedSet<float> { a, b };
                foreach (EditList.Speed sp in _edits.Speeds)
                {
                    float s0 = Mathf.Min(sp.Start, sp.End), s1 = Mathf.Max(sp.Start, sp.End);
                    if (s0 > a && s0 < b) bounds.Add(s0);
                    if (s1 > a && s1 < b) bounds.Add(s1);
                }
                float prev = float.NaN;
                foreach (float t in bounds)
                {
                    if (!float.IsNaN(prev) && t - prev > 0.0001f)
                    {
                        float rate = _edits.RateAt((prev + t) / 2);
                        foreach ((float d0, float d1) in Pieces(prev, t))
                            if (d1 - d0 > 0.0001f) segs.Add(new Seg { D0 = d0, D1 = d1, Rate = rate });
                    }
                    prev = t;
                }
            }
            foreach (Slot x in Slots()) segs.Add(new Seg { D0 = x.Start, D1 = x.Start + x.Length, Rate = 1, Clip = true });
            return segs.OrderBy(g => g.D0).ToList();
        }

        private float ResultDuration => ResultSegments().Sum(g => (g.D1 - g.D0) / g.Rate);

        // 표시 위치 → 결과 영상 시각
        private float ResultAt(float display) => ResultSegments().Sum(g => Mathf.Clamp(display - g.D0, 0, g.D1 - g.D0) / g.Rate);

        // 결과 영상 시각 → 표시 위치
        private float DisplayAtResult(float result)
        {
            float acc = 0;
            foreach (Seg g in ResultSegments())
            {
                float len = (g.D1 - g.D0) / g.Rate;
                if (acc + len >= result) return g.D0 + (result - acc) * g.Rate;
                acc += len;
            }
            return DisplayTotal;
        }

        // 미리보기 화면을 덮을 검정 비율(페이드).
        private float FadeAlpha()
        {
            float a = 0;
            if (_activeInsert < 0)
                foreach (EditList.Range fd in _edits.Fades)
                {
                    float s = Mathf.Min(fd.Start, fd.End), e = Mathf.Max(fd.Start, fd.End), h = (e - s) / 2;
                    if (h > 0 && _time >= s && _time <= e) a = Mathf.Max(a, Mathf.Abs(_time - (s + h)) / h);
                }
            return Mathf.Clamp01(a);
        }

        private float ClipDuration(string file)
        {
            if (string.IsNullOrEmpty(file)) return 0;
            if (!_clipDurations.TryGetValue(file, out float d))
            {
                d = BundledFfmpeg.Available && File.Exists(file) ? BundledFfmpeg.Probe(file).Duration : 0;
                _clipDurations[file] = d;
            }
            return d;
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_manifest == null || _locales.Length == 0)
            {
                EditorGUILayout.HelpBox($"이 폴더와 하위 폴더에 녹화 영상이 없다\n{_directory}", MessageType.Info);
                if (GUILayout.Button("Choose folder…"))
                {
                    string dir = EditorUtility.OpenFolderPanel("녹화 폴더", _directory ?? "", "");
                    if (!string.IsNullOrEmpty(dir)) Load(dir);
                }
                return;
            }
            HandleKeys();
            DrawToolbar();
            DrawPreview();
            DrawTransport();
            DrawFinishing();
            DrawTimeline();
            DrawEditList();
        }

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label(_manifest.Name, EditorStyles.toolbarButton);
                GUILayout.Space(8);
                GUILayout.Label("Preview", GUILayout.Width(48));
                int picked = EditorGUILayout.Popup(_previewLocale, _locales, EditorStyles.toolbarPopup, GUILayout.Width(80));
                if (picked != _previewLocale) { _previewLocale = picked; OpenPreview(); }
                if (GUILayout.Button("Open folder", EditorStyles.toolbarButton)) EditorUtility.RevealInFinder(_directory);
                GUILayout.Space(8);
                if (GUILayout.Button(new GUIContent("↶", "Undo (Ctrl+Z)"), EditorStyles.toolbarButton, GUILayout.Width(26))) Undo.PerformUndo();
                if (GUILayout.Button(new GUIContent("↷", "Redo (Ctrl+Y)"), EditorStyles.toolbarButton, GUILayout.Width(26))) Undo.PerformRedo();
                GUILayout.FlexibleSpace();
                GUILayout.Label(OutputLabel(), EditorStyles.miniLabel);
                GUILayout.Space(8);
                GUILayout.Label(HasStems ? "BGM/SFX 스템 있음" : "스템 없음 — BGM/SFX 음소거는 전체 음소거로 적용", EditorStyles.miniLabel);
            }
            if (!BundledFfmpeg.Available) EditorGUILayout.HelpBox("미리보기·내보내기 도구(Tools~/ffmpeg.exe)가 없다", MessageType.Error);
        }

        private void DrawPreview()
        {
            float aspect = _previewRatio >= 0 ? Ratios[_previewRatio].ah / (float) Ratios[_previewRatio].aw : _manifest.Height / (float) Mathf.Max(1, _manifest.Width);
            float maxHeight = Mathf.Max(200, position.height * 0.46f);
            float width = Mathf.Min(position.width - 16, maxHeight / aspect);
            Rect area = GUILayoutUtility.GetRect(position.width, width * aspect + 8);
            Rect view = new Rect(area.x + (area.width - width) / 2, area.y + 4, width, width * aspect);
            EditorGUI.DrawRect(area, new Color(0.1f, 0.1f, 0.1f));
            EditorGUI.DrawRect(view, Color.black);

            EditPreview.Frames source = _activeInsert >= 0 ? ClipFrames(ClipFile(_activeInsert)) : _frames;
            Texture shown = source?.Get(_activeInsert >= 0 ? _clipTime : _time);
            if (shown != null) DrawFramed(view, shown, source);
            else GUI.Label(view, _previewError ?? "미리보기 없음", CenteredLabel);
            float fade = FadeAlpha();
            if (fade > 0) EditorGUI.DrawRect(view, new Color(0, 0, 0, fade));
            if (Event.current.type == EventType.MouseDown && view.Contains(Event.current.mousePosition)) { TogglePlay(); Event.current.Use(); }
        }

        // 고른 비율의 화면 맞춤(블러 여백 또는 자르기)대로 그린다.
        private void DrawFramed(Rect view, Texture tex, EditPreview.Frames frames)
        {
            float texAspect = tex.width / (float) Mathf.Max(1, tex.height), viewAspect = view.width / Mathf.Max(1, view.height);
            if (_previewRatio < 0 || Mathf.Abs(texAspect - viewAspect) < 0.01f) { GUI.DrawTexture(view, tex, ScaleMode.ScaleToFit); return; }
            EditList.Framing fr = _edits.FramingFor(Ratios[_previewRatio].label);
            if (fr.Crop)
            {
                Rect uv;
                if (texAspect > viewAspect) { float w = viewAspect / texAspect; uv = new Rect((1 - w) * fr.Focus, 0, w, 1); }
                else { float h = texAspect / viewAspect; uv = new Rect(0, (1 - h) * (1 - fr.Focus), 1, h); }
                GUI.DrawTextureWithTexCoords(view, tex, uv);
                return;
            }
            Texture blur = frames.Blurred();
            if (blur != null) GUI.DrawTexture(view, blur, ScaleMode.ScaleAndCrop);
            GUI.DrawTexture(view, tex, ScaleMode.ScaleToFit);
        }

        private string OutputLabel()
        {
            CaptureSettings s = LocaleCaptureWindow.SavedSettings();
            VideoEditExporter.RatioSize(1, 1, s, out int side, out _);
            string q = s.Quality == VideoQuality.Custom ? $"{s.CustomBitrateMbps}Mbps" : s.Quality.ToString();
            return $"{VideoEditExporter.Extension(s).ToUpperInvariant()} · {q} · {side}p";
        }

        private void DrawTransport()
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.Height(30)))
            {
                GUILayout.Space(8);
                using (new EditorGUI.DisabledScope(_frames == null))
                {
                    if (GUILayout.Button("⏮", GUILayout.Width(34), GUILayout.Height(26))) SeekDisplay(0);
                    if (GUILayout.Button("◀", GUILayout.Width(34), GUILayout.Height(26))) SeekDisplay(PlayheadDisplay - FrameStep);
                    if (GUILayout.Button(_playing ? "❚❚" : "▶", GUILayout.Width(48), GUILayout.Height(26))) TogglePlay();
                    if (GUILayout.Button("▶|", GUILayout.Width(34), GUILayout.Height(26))) SeekDisplay(PlayheadDisplay + FrameStep);
                    if (GUILayout.Button("⏭", GUILayout.Width(34), GUILayout.Height(26))) SeekDisplay(DisplayTotal);
                }
                GUILayout.Space(10);
                string time = $"{Fmt(PlayheadDisplay)} / {Fmt(DisplayTotal)}" + (_activeInsert >= 0 ? "  (삽입 클립)" : "");
                float result = ResultDuration;
                bool over = _edits.TargetLength > 0 && result > _edits.TargetLength + 0.01f;
                if (over) GUI.contentColor = new Color(1f, 0.45f, 0.4f);
                GUILayout.Label($"{time}   →  결과 {Fmt(result)}" + (_edits.TargetLength > 0 ? $" / 목표 {_edits.TargetLength:0}s" : ""), TimecodeStyle, GUILayout.Height(26));
                GUI.contentColor = Color.white;
                GUILayout.FlexibleSpace();
                int mask = RatioMask;
                for (int i = 0; i < Ratios.Length; i++)
                {
                    bool on = (mask & (1 << i)) != 0;
                    bool next = GUILayout.Toggle(on, Ratios[i].label, EditorStyles.miniButton, GUILayout.Width(44), GUILayout.Height(26));
                    if (next != on) mask ^= 1 << i;
                }
                if (mask != RatioMask) RatioMask = mask;
                GUILayout.Space(6);
                GUI.backgroundColor = new Color(0.45f, 0.8f, 0.45f);
                using (new EditorGUI.DisabledScope(!BundledFfmpeg.Available || mask == 0))
                    if (GUILayout.Button("Export", GUILayout.Width(110), GUILayout.Height(28))) ExportAll();
                GUI.backgroundColor = Color.white;
                GUILayout.Space(8);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                bool hasRange = _in >= 0 && _out > _in;
                if (GUILayout.Button("Set In  [I]", GUILayout.Width(84))) SetIn();
                if (GUILayout.Button("Set Out  [O]", GUILayout.Width(90))) SetOut();
                using (new EditorGUI.DisabledScope(!hasRange))
                {
                    GUI.backgroundColor = CutColor;
                    if (GUILayout.Button("Delete  [Del]", GUILayout.Width(96))) AddCut();
                    GUI.backgroundColor = MuteColor;
                    if (GUILayout.Button("Mute BGM  [B]", GUILayout.Width(104))) AddMute(MuteTarget.Bgm);
                    if (GUILayout.Button("Mute SFX  [S]", GUILayout.Width(100))) AddMute(MuteTarget.Sfx);
                    if (GUILayout.Button("Mute All  [M]", GUILayout.Width(96))) AddMute(MuteTarget.All);
                    GUI.backgroundColor = SpeedColor;
                    if (GUILayout.Button($"Speed {_speedRate:0.0}x", GUILayout.Width(84))) AddSpeed(_speedRate);
                    GUI.backgroundColor = Color.white;
                }
                _speedRate = SpeedSlider(_speedRate, 150);
                using (new EditorGUI.DisabledScope(!hasRange))
                {
                    if (GUILayout.Button("Clear", GUILayout.Width(52))) { _in = _out = -1; }
                }
                GUI.backgroundColor = InsertColor;
                if (GUILayout.Button("Insert Clip", GUILayout.Width(96))) AddInsert();
                GUI.backgroundColor = Color.white;
                GUILayout.FlexibleSpace();
            }
        }

        private static readonly string[] RatioOptions = new[] { "원본" }.Concat(Ratios.Select(r => r.label)).ToArray();
        private static readonly float[] TargetOptions = { 0, 6, 15, 30, 60 };
        private static readonly string[] TargetLabels = { "—", "6s", "15s", "30s", "60s" };

        private void DrawFinishing()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                GUILayout.Label("Frame", GUILayout.Width(42));
                _previewRatio = EditorGUILayout.Popup(_previewRatio + 1, RatioOptions, GUILayout.Width(64)) - 1;
                if (_previewRatio >= 0)
                {
                    EditList.Framing fr = _edits.FramingFor(Ratios[_previewRatio].label);
                    bool blur = GUILayout.Toggle(!fr.Crop, "Blur", EditorStyles.miniButtonLeft, GUILayout.Width(44));
                    bool crop = GUILayout.Toggle(fr.Crop, "Crop", EditorStyles.miniButtonRight, GUILayout.Width(44));
                    bool nextCrop = fr.Crop ? !blur : crop;
                    float focus = fr.Focus;
                    if (nextCrop) focus = GUILayout.HorizontalSlider(fr.Focus, 0, 1, GUILayout.Width(120));
                    if (nextCrop != fr.Crop || Mathf.Abs(focus - fr.Focus) > 0.0001f)
                    {
                        fr.Crop = nextCrop;
                        fr.Focus = focus;
                        _edits.SetFraming(fr);
                        SaveEdits();
                    }
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!BundledFfmpeg.Available))
                    if (GUILayout.Button("Thumbnail", GUILayout.Width(84))) ExportThumbnails();
                GUILayout.Space(8);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(8);
                GUI.backgroundColor = FadeColor;
                using (new EditorGUI.DisabledScope(!(_in >= 0 && _out > _in)))
                    if (GUILayout.Button(new GUIContent("Fade In-Out  [F]", "Set In–Set Out 구간 앞 절반은 검은 화면에서 밝아지고, 뒤 절반은 다시 검게 어두워진다"), GUILayout.Width(118))) AddFade();
                GUI.backgroundColor = Color.white;
                GUILayout.Space(12);
                GUILayout.Label("Target", GUILayout.Width(44));
                int ti = Mathf.Max(0, Array.IndexOf(TargetOptions, _edits.TargetLength));
                int picked = EditorGUILayout.Popup(ti, TargetLabels, GUILayout.Width(52));
                if (picked != ti) { _edits.TargetLength = TargetOptions[picked]; SaveEdits(); }
                GUILayout.FlexibleSpace();
            }
        }

        private void AddSpeed(float rate)
        {
            if (!(_in >= 0 && _out > _in)) return;
            _edits.Speeds.Add(new EditList.Speed { Start = _in, End = _out, Rate = rate });
            _selected = (Kind.Speed, _edits.Speeds.Count - 1);
            _in = _out = -1;
            SaveEdits();
        }

        // ── 표시 타임라인: 원본 + 삽입 클립 길이 ──────────────────────────────────
        // 원본 시각 t 앞에 끼운 클립은 t 자리에서 자기 길이만큼 자리를 차지한다. 삭제 구간은 그대로 보인다(편집 대상이라).

        private struct Slot { public int Index; public float At, Start, Length; }

        private List<Slot> Slots()
        {
            var list = new List<Slot>();
            float acc = 0;
            foreach (int i in Enumerable.Range(0, _edits.Inserts.Count).OrderBy(i => Mathf.Clamp(_edits.Inserts[i].At, 0, Duration)).ThenBy(i => i))
            {
                float at = Mathf.Clamp(_edits.Inserts[i].At, 0, Duration);
                float len = ClipDuration(ClipFile(i));
                list.Add(new Slot { Index = i, At = at, Start = at + acc, Length = len });
                acc += len;
            }
            return list;
        }

        private float DisplayTotal => Duration + Slots().Sum(x => x.Length);

        // 원본 t 의 표시 위치. inclusive 면 t 자리의 클립을 앞에 둔다(구간 시작·재생 위치), 아니면 뒤에 둔다(구간 끝).
        private float ToDisplay(float t, bool inclusive = true)
        {
            float d = t;
            foreach (Slot x in Slots()) if (inclusive ? x.At <= t : x.At < t) d += x.Length;
            return d;
        }

        // 표시 위치 → 원본 시각, 또는 (클립, 클립 안 시각).
        private (int insert, float time) FromDisplay(float d)
        {
            float before = 0;
            foreach (Slot x in Slots())
            {
                if (d < x.Start) return (-1, Mathf.Clamp(d - before, 0, Duration));
                if (d < x.Start + x.Length) return (x.Index, d - x.Start);
                before += x.Length;
            }
            return (-1, Mathf.Clamp(d - before, 0, Duration));
        }

        private float PlayheadDisplay
        {
            get
            {
                if (_activeInsert >= 0) foreach (Slot x in Slots()) if (x.Index == _activeInsert) return x.Start + _clipTime;
                return ToDisplay(_time);
            }
        }

        // 표시 위치로 이동. 클립 블록 안이면 그 클립의 해당 지점에 멈춘다.
        private void SeekDisplay(float d)
        {
            StopPlayback();
            (int insert, float time) = FromDisplay(Mathf.Clamp(d, 0, DisplayTotal));
            if (insert >= 0)
            {
                _activeInsert = insert;
                _clipTime = time;
                _time = Mathf.Clamp(_edits.Inserts[insert].At, 0, Duration);
            }
            else
            {
                _activeInsert = -1;
                _time = time;
            }
            Repaint();
        }

        // 원본 구간 [s, e] 를 표시 조각들로 — 사이에 낀 클립 자리는 건너뛴다.
        private List<(float a, float b)> Pieces(float s, float e)
        {
            float lo = Mathf.Min(s, e), hi = Mathf.Max(s, e);
            var pieces = new List<(float, float)>();
            float cur = ToDisplay(lo);
            foreach (Slot x in Slots())
            {
                if (x.At <= lo || x.At >= hi) continue;
                pieces.Add((cur, x.Start));
                cur = x.Start + x.Length;
            }
            pieces.Add((cur, ToDisplay(hi, false)));
            return pieces;
        }

        private void DrawTimeline()
        {
            float totalHeight = RulerHeight + TrackHeight * 3 + TrackGap * 4 + 6;
            Rect area = GUILayoutUtility.GetRect(position.width, totalHeight);
            float left = area.x + 52, right = area.xMax - 12;
            Rect ruler = new Rect(left, area.y + 4, right - left, RulerHeight);
            Rect video = new Rect(left, ruler.yMax + TrackGap, ruler.width, TrackHeight);
            Rect bgm = new Rect(left, video.yMax + TrackGap, ruler.width, TrackHeight);
            Rect sfx = new Rect(left, bgm.yMax + TrackGap, ruler.width, TrackHeight);

            EditorGUI.DrawRect(area, new Color(0.16f, 0.16f, 0.16f));
            GUI.Label(new Rect(area.x + 6, video.y, 44, TrackHeight), "Video", TrackLabel);
            GUI.Label(new Rect(area.x + 6, bgm.y, 44, TrackHeight), "BGM", TrackLabel);
            GUI.Label(new Rect(area.x + 6, sfx.y, 44, TrackHeight), "SFX", TrackLabel);

            DrawRuler(ruler);
            DrawVideoTrack(video);
            DrawAudioTrack(bgm, MuteTarget.Bgm);
            DrawAudioTrack(sfx, MuteTarget.Sfx);

            if (Duration > 0)
            {
                if (_in >= 0 && _out > _in)
                {
                    float a = X(ruler, ToDisplay(_in)), b = X(ruler, ToDisplay(_out, false));
                    EditorGUI.DrawRect(new Rect(a, ruler.y, b - a, sfx.yMax - ruler.y), SelectionColor);
                }
                if (_edits.TargetLength > 0 && _edits.TargetLength < ResultDuration)
                {
                    float tx = X(ruler, DisplayAtResult(_edits.TargetLength));
                    var targetColor = new Color(1f, 0.35f, 0.3f);
                    EditorGUI.DrawRect(new Rect(tx - 1, ruler.y, 2, sfx.yMax - ruler.y), targetColor);
                    GUI.contentColor = targetColor;
                    GUI.Label(new Rect(tx + 3, ruler.yMax - 14, 40, 14), $"{_edits.TargetLength:0}s", RulerLabel);
                    GUI.contentColor = Color.white;
                }
                float p = X(ruler, PlayheadDisplay);
                EditorGUI.DrawRect(new Rect(p - 1, ruler.y, 2, sfx.yMax - ruler.y), Color.white);
                EditorGUI.DrawRect(new Rect(p - 5, ruler.y, 10, 6), Color.white);
                DrawHandles(ruler, sfx.yMax);
            }
            HandleTimelineMouse(ruler, new Rect(left, ruler.y, ruler.width, sfx.yMax - ruler.y), video, bgm, sfx);
        }

        private void DrawRuler(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.22f, 0.22f, 0.22f));
            float total = DisplayTotal;
            if (total <= 0) return;
            float step = total > 60 ? 10 : total > 20 ? 5 : total > 8 ? 2 : 1;
            for (float t = 0; t <= total + 0.001f; t += step)
            {
                float x = X(r, t);
                EditorGUI.DrawRect(new Rect(x, r.yMax - 8, 1, 8), new Color(0.6f, 0.6f, 0.6f));
                GUI.Label(new Rect(x + 2, r.y, 60, 14), Fmt(t), RulerLabel);
            }
            for (float t = step / 2; t <= total; t += step)
                EditorGUI.DrawRect(new Rect(X(r, t), r.yMax - 4, 1, 4), new Color(0.45f, 0.45f, 0.45f));
        }

        private void DrawVideoTrack(Rect r)
        {
            EditorGUI.DrawRect(r, new Color(0.27f, 0.33f, 0.4f));
            if (Duration <= 0) return;
            for (int i = 0; i < _edits.Cuts.Count; i++)
            {
                bool first = true;
                foreach ((float a, float b) in Pieces(_edits.Cuts[i].Start, _edits.Cuts[i].End))
                {
                    Rect block = Block(r, a, b);
                    EditorGUI.DrawRect(block, CutColor);
                    if (first) GUI.Label(block, "삭제", BlockLabel);
                    if (IsSelected(Kind.Cut, i)) DrawOutline(block);
                    first = false;
                }
            }
            for (int i = 0; i < _edits.Speeds.Count; i++)
            {
                bool first = true;
                foreach ((float a, float b) in Pieces(_edits.Speeds[i].Start, _edits.Speeds[i].End))
                {
                    Rect band = SpeedBand(r, a, b);
                    EditorGUI.DrawRect(band, SpeedColor);
                    if (first) GUI.Label(band, $"{_edits.Speeds[i].Rate:0.##}x", BlockLabel);
                    if (IsSelected(Kind.Speed, i)) DrawOutline(band);
                    first = false;
                }
            }
            for (int i = 0; i < _edits.Fades.Count; i++)
            {
                bool first = true;
                foreach ((float a, float b) in Pieces(_edits.Fades[i].Start, _edits.Fades[i].End))
                {
                    Rect band = FadeBand(r, a, b);
                    EditorGUI.DrawRect(band, FadeColor);
                    if (first) GUI.Label(band, "페이드", BlockLabel);
                    if (IsSelected(Kind.Fade, i)) DrawOutline(band);
                    first = false;
                }
            }
            foreach (Slot x in Slots())
            {
                EditList.Insert ins = _edits.Inserts[x.Index];
                Rect block = Block(r, x.Start, x.Start + Mathf.Max(x.Length, 0.05f));
                EditorGUI.DrawRect(block, InsertColor);
                string flags = (ins.MuteBgm ? " BGM✕" : "") + (ins.MuteSfx ? " SFX✕" : "");
                GUI.Label(block, Path.GetFileNameWithoutExtension(ClipFile(x.Index)) + flags, ClipBlockLabel);
                if (IsSelected(Kind.Insert, x.Index)) DrawOutline(block);
            }
        }

        private void DrawAudioTrack(Rect r, MuteTarget track)
        {
            EditorGUI.DrawRect(r, track == MuteTarget.Bgm ? new Color(0.3f, 0.38f, 0.3f) : new Color(0.36f, 0.34f, 0.28f));
            if (Duration <= 0) return;
            foreach (EditList.Range c in _edits.Cuts)
                foreach ((float a, float b) in Pieces(c.Start, c.End))
                    EditorGUI.DrawRect(Block(r, a, b), new Color(CutColor.r, CutColor.g, CutColor.b, 0.3f));
            // 삽입 클립 자리: 클립 소리. 끈 쪽은 음소거 색
            foreach (Slot x in Slots())
            {
                EditList.Insert ins = _edits.Inserts[x.Index];
                bool muted = track == MuteTarget.Bgm ? ins.MuteBgm : ins.MuteSfx;
                Rect block = Block(r, x.Start, x.Start + Mathf.Max(x.Length, 0.05f));
                EditorGUI.DrawRect(block, muted ? MuteColor : new Color(InsertColor.r, InsertColor.g, InsertColor.b, 0.35f));
                if (muted) GUI.Label(block, "음소거", BlockLabel);
            }
            for (int i = 0; i < _edits.Mutes.Count; i++)
            {
                EditList.Mute m = _edits.Mutes[i];
                if (m.Target != MuteTarget.All && m.Target != track) continue;
                bool first = true;
                foreach ((float a, float b) in Pieces(m.Start, m.End))
                {
                    Rect block = Block(r, a, b);
                    EditorGUI.DrawRect(block, MuteColor);
                    if (first) GUI.Label(block, "음소거", BlockLabel);
                    if (IsSelected(Kind.Mute, i)) DrawOutline(block);
                    first = false;
                }
            }
        }

        private void DrawHandles(Rect ruler, float bottom)
        {
            if (_in >= 0)
            {
                float x = X(ruler, ToDisplay(_in));
                EditorGUI.DrawRect(new Rect(x - HandleWidth, ruler.y, HandleWidth, bottom - ruler.y), new Color(1, 1, 1, 0.5f));
                GUI.Label(new Rect(x - HandleWidth, ruler.y, HandleWidth, 14), "I", HandleLabel);
            }
            if (_out >= 0)
            {
                float x = X(ruler, ToDisplay(_out, false));
                EditorGUI.DrawRect(new Rect(x, ruler.y, HandleWidth, bottom - ruler.y), new Color(1, 1, 1, 0.5f));
                GUI.Label(new Rect(x, ruler.y, HandleWidth, 14), "O", HandleLabel);
            }
        }

        // 표시 위치 → 원본 시각. 클립 블록 안이면 그 클립의 삽입 지점.
        private float SourceAt(float display)
        {
            (int insert, float time) = FromDisplay(display);
            return insert >= 0 ? Mathf.Clamp(_edits.Inserts[insert].At, 0, Duration) : time;
        }

        private void HandleTimelineMouse(Rect ruler, Rect whole, Rect video, Rect bgm, Rect sfx)
        {
            if (Duration <= 0) return;
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when whole.Contains(e.mousePosition):
                {
                    float xIn = _in >= 0 ? X(ruler, ToDisplay(_in)) : float.NaN, xOut = _out >= 0 ? X(ruler, ToDisplay(_out, false)) : float.NaN;
                    if (!float.IsNaN(xIn) && Mathf.Abs(e.mousePosition.x - xIn + HandleWidth / 2) <= HandleWidth) _drag = Drag.In;
                    else if (!float.IsNaN(xOut) && Mathf.Abs(e.mousePosition.x - xOut - HandleWidth / 2) <= HandleWidth) _drag = Drag.Out;
                    else
                    {
                        _selected = HitEdit(e.mousePosition, video, bgm, sfx);
                        _drag = Drag.Scrub;
                        SeekDisplay(T(ruler, e.mousePosition.x));
                    }
                    GUI.FocusControl(null);
                    e.Use();
                    break;
                }
                case EventType.MouseDrag when _drag != Drag.None:
                {
                    float d = T(ruler, e.mousePosition.x);
                    if (_drag == Drag.Scrub) SeekDisplay(d);
                    else if (_drag == Drag.In) _in = Mathf.Min(SourceAt(d), _out < 0 ? Duration : _out);
                    else _out = Mathf.Max(SourceAt(d), _in < 0 ? 0 : _in);
                    Repaint();
                    e.Use();
                    break;
                }
                case EventType.MouseUp when _drag != Drag.None:
                    _drag = Drag.None;
                    e.Use();
                    break;
            }
        }

        private (Kind, int)? HitEdit(Vector2 mouse, Rect video, Rect bgm, Rect sfx)
        {
            Rect track = video.Contains(mouse) ? video : bgm.Contains(mouse) ? bgm : sfx.Contains(mouse) ? sfx : default;
            if (track == default) return null;
            foreach (Slot x in Slots())
                if (Block(track, x.Start, x.Start + Mathf.Max(x.Length, 0.05f)).Contains(mouse)) return (Kind.Insert, x.Index);
            if (track == video)
            {
                for (int i = 0; i < _edits.Speeds.Count; i++)
                    if (Pieces(_edits.Speeds[i].Start, _edits.Speeds[i].End).Any(p => SpeedBand(video, p.a, p.b).Contains(mouse))) return (Kind.Speed, i);
                for (int i = 0; i < _edits.Fades.Count; i++)
                    if (Pieces(_edits.Fades[i].Start, _edits.Fades[i].End).Any(p => FadeBand(video, p.a, p.b).Contains(mouse))) return (Kind.Fade, i);
                for (int i = 0; i < _edits.Cuts.Count; i++)
                    if (Pieces(_edits.Cuts[i].Start, _edits.Cuts[i].End).Any(p => Block(video, p.a, p.b).Contains(mouse))) return (Kind.Cut, i);
                return null;
            }
            MuteTarget kind = track == bgm ? MuteTarget.Bgm : MuteTarget.Sfx;
            for (int i = 0; i < _edits.Mutes.Count; i++)
            {
                EditList.Mute m = _edits.Mutes[i];
                if ((m.Target == MuteTarget.All || m.Target == kind) && Pieces(m.Start, m.End).Any(p => Block(track, p.a, p.b).Contains(mouse))) return (Kind.Mute, i);
            }
            return null;
        }

        // ── 편집 목록 ──────────────────────────────────────────────────────────

        private void DrawEditList()
        {
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Edits", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label($"삭제 {_edits.Cuts.Count} · 음소거 {_edits.Mutes.Count} · 배속 {_edits.Speeds.Count} · 페이드 {_edits.Fades.Count} · 삽입 {_edits.Inserts.Count}", EditorStyles.miniLabel);
            }
            _listScroll = EditorGUILayout.BeginScrollView(_listScroll);
            bool changed = false;

            int removeCut = -1;
            for (int i = 0; i < _edits.Cuts.Count; i++)
            {
                EditList.Range r = _edits.Cuts[i];
                GUI.backgroundColor = IsSelected(Kind.Cut, i) ? SelectedTint : Color.white;
                using (new EditorGUILayout.HorizontalScope(Row))
                {
                    GUI.backgroundColor = Color.white;
                    Swatch(CutColor); GUILayout.Label("삭제", GUILayout.Width(60));
                    float s = EditorGUILayout.FloatField(r.Start, GUILayout.Width(64));
                    GUILayout.Label("–", GUILayout.Width(10));
                    float en = EditorGUILayout.FloatField(r.End, GUILayout.Width(64));
                    GUILayout.Label($"({Fmt(r.Start)} – {Fmt(r.End)})", EditorStyles.miniLabel);
                    if (Math.Abs(s - r.Start) > 0.0001f || Math.Abs(en - r.End) > 0.0001f) { _edits.Cuts[i] = new EditList.Range { Start = s, End = en }; changed = true; }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("이동", EditorStyles.miniButton, GUILayout.Width(40))) Seek(r.Start);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeCut = i;
                }
            }
            if (removeCut >= 0) { _edits.Cuts.RemoveAt(removeCut); _selected = null; changed = true; }

            int removeMute = -1;
            for (int i = 0; i < _edits.Mutes.Count; i++)
            {
                EditList.Mute m = _edits.Mutes[i];
                GUI.backgroundColor = IsSelected(Kind.Mute, i) ? SelectedTint : Color.white;
                using (new EditorGUILayout.HorizontalScope(Row))
                {
                    GUI.backgroundColor = Color.white;
                    Swatch(MuteColor);
                    var target = (MuteTarget) EditorGUILayout.EnumPopup(m.Target, GUILayout.Width(60));
                    float s = EditorGUILayout.FloatField(m.Start, GUILayout.Width(64));
                    GUILayout.Label("–", GUILayout.Width(10));
                    float en = EditorGUILayout.FloatField(m.End, GUILayout.Width(64));
                    GUILayout.Label($"({Fmt(m.Start)} – {Fmt(m.End)})", EditorStyles.miniLabel);
                    if (target != m.Target || Math.Abs(s - m.Start) > 0.0001f || Math.Abs(en - m.End) > 0.0001f) { _edits.Mutes[i] = new EditList.Mute { Start = s, End = en, Target = target }; changed = true; }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("이동", EditorStyles.miniButton, GUILayout.Width(40))) Seek(m.Start);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeMute = i;
                }
            }
            if (removeMute >= 0) { _edits.Mutes.RemoveAt(removeMute); _selected = null; changed = true; }

            int removeSpeed = -1;
            for (int i = 0; i < _edits.Speeds.Count; i++)
            {
                EditList.Speed sp = _edits.Speeds[i];
                GUI.backgroundColor = IsSelected(Kind.Speed, i) ? SelectedTint : Color.white;
                using (new EditorGUILayout.HorizontalScope(Row))
                {
                    GUI.backgroundColor = Color.white;
                    Swatch(SpeedColor);
                    float rate = SpeedSlider(sp.Rate, 160);
                    GUILayout.Label("x", GUILayout.Width(12));
                    float s = EditorGUILayout.FloatField(sp.Start, GUILayout.Width(64));
                    GUILayout.Label("–", GUILayout.Width(10));
                    float en = EditorGUILayout.FloatField(sp.End, GUILayout.Width(64));
                    GUILayout.Label($"({Fmt(sp.Start)} – {Fmt(sp.End)})", EditorStyles.miniLabel);
                    if (Math.Abs(rate - sp.Rate) > 0.0001f || Math.Abs(s - sp.Start) > 0.0001f || Math.Abs(en - sp.End) > 0.0001f)
                    { _edits.Speeds[i] = new EditList.Speed { Start = s, End = en, Rate = rate }; changed = true; }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("이동", EditorStyles.miniButton, GUILayout.Width(40))) Seek(sp.Start);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeSpeed = i;
                }
            }
            if (removeSpeed >= 0) { _edits.Speeds.RemoveAt(removeSpeed); _selected = null; changed = true; }

            int removeFade = -1;
            for (int i = 0; i < _edits.Fades.Count; i++)
            {
                EditList.Range fd = _edits.Fades[i];
                GUI.backgroundColor = IsSelected(Kind.Fade, i) ? SelectedTint : Color.white;
                using (new EditorGUILayout.HorizontalScope(Row))
                {
                    GUI.backgroundColor = Color.white;
                    Swatch(FadeColor); GUILayout.Label("페이드", GUILayout.Width(60));
                    float s = EditorGUILayout.FloatField(fd.Start, GUILayout.Width(64));
                    GUILayout.Label("–", GUILayout.Width(10));
                    float en = EditorGUILayout.FloatField(fd.End, GUILayout.Width(64));
                    GUILayout.Label($"({Fmt(fd.Start)} – {Fmt(fd.End)})", EditorStyles.miniLabel);
                    if (Math.Abs(s - fd.Start) > 0.0001f || Math.Abs(en - fd.End) > 0.0001f) { _edits.Fades[i] = new EditList.Range { Start = s, End = en }; changed = true; }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("이동", EditorStyles.miniButton, GUILayout.Width(40))) Seek(fd.Start);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeFade = i;
                }
            }
            if (removeFade >= 0) { _edits.Fades.RemoveAt(removeFade); _selected = null; changed = true; }

            int removeInsert = -1;
            for (int i = 0; i < _edits.Inserts.Count; i++)
            {
                EditList.Insert ins = _edits.Inserts[i];
                GUI.backgroundColor = IsSelected(Kind.Insert, i) ? SelectedTint : Color.white;
                using (new EditorGUILayout.HorizontalScope(Row))
                {
                    GUI.backgroundColor = Color.white;
                    Swatch(InsertColor); GUILayout.Label("삽입", GUILayout.Width(60));
                    float at = EditorGUILayout.FloatField(ins.At, GUILayout.Width(64));
                    if (Math.Abs(at - ins.At) > 0.0001f) { ins.At = at; changed = true; }
                    GUILayout.Label($"{Path.GetFileName(ins.File)}  ({Fmt(ClipDuration(ins.File))})", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    bool lc = EditList.ClipStems(ins.File, out _, out _);
                    GUILayout.Label(lc ? "LC 녹화" : "외부 영상", EditorStyles.miniLabel, GUILayout.Width(52));
                    bool mb = GUILayout.Toggle(ins.MuteBgm, "BGM 끄기", EditorStyles.miniButton, GUILayout.Width(62));
                    if (mb != ins.MuteBgm) SetClipMute(ins, MuteTarget.Bgm, mb);
                    bool ms = GUILayout.Toggle(ins.MuteSfx, "SFX 끄기", EditorStyles.miniButton, GUILayout.Width(62));
                    if (ms != ins.MuteSfx) SetClipMute(ins, MuteTarget.Sfx, ms);
                    if (GUILayout.Button("언어별…", EditorStyles.miniButton, GUILayout.Width(60))) PickLocaleClips(ins);
                    if (GUILayout.Button("이동", EditorStyles.miniButton, GUILayout.Width(40))) { int idx = i; SeekDisplay(Slots().First(x => x.Index == idx).Start); }
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22))) removeInsert = i;
                }
                for (int k = 0; k < ins.LocaleKeys.Count; k++)
                    if (!string.IsNullOrEmpty(ins.LocaleFiles[k]))
                        GUILayout.Label($"        {ins.LocaleKeys[k]} → {Path.GetFileName(ins.LocaleFiles[k])}", EditorStyles.miniLabel);
            }
            if (removeInsert >= 0) { _edits.Inserts.RemoveAt(removeInsert); _selected = null; changed = true; }

            if (_edits.IsEmpty) GUILayout.Label("구간을 잡고 Delete/Mute/Fade 를 누르거나, Insert Clip 으로 영상을 끼운다.", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
            if (changed) SaveEdits();
        }

        private static void Swatch(Color c) { GUI.color = c; GUILayout.Label("■", GUILayout.Width(14)); GUI.color = Color.white; }

        // ── 편집 조작 ──────────────────────────────────────────────────────────

        private void SetIn() { _in = _time; if (_out >= 0 && _out < _in) _out = -1; }
        private void SetOut() { _out = _time; if (_in < 0 || _in > _out) _in = 0; }

        private void AddCut()
        {
            if (!(_in >= 0 && _out > _in)) return;
            _edits.Cuts.Add(new EditList.Range { Start = _in, End = _out });
            _selected = (Kind.Cut, _edits.Cuts.Count - 1);
            _in = _out = -1;
            SaveEdits();
        }

        private void AddFade()
        {
            if (!(_in >= 0 && _out > _in)) return;
            _edits.Fades.Add(new EditList.Range { Start = _in, End = _out });
            _selected = (Kind.Fade, _edits.Fades.Count - 1);
            _in = _out = -1;
            SaveEdits();
        }

        private void AddMute(MuteTarget target)
        {
            if (!(_in >= 0 && _out > _in)) return;
            // 구간 안에 삽입 클립이 있으면 클립 소리도 같이 끈다. 외부 영상이면 경고는 한 번만.
            for (int i = 0; i < _edits.Inserts.Count; i++)
            {
                EditList.Insert ins = _edits.Inserts[i];
                if (ins.At < _in || ins.At > _out) continue;
                bool ok = target == MuteTarget.Sfx || SetClipMute(ins, MuteTarget.Bgm, true);
                if (ok && target != MuteTarget.Bgm) SetClipMute(ins, MuteTarget.Sfx, true);
            }
            _edits.Mutes.Add(new EditList.Mute { Start = _in, End = _out, Target = target });
            _selected = (Kind.Mute, _edits.Mutes.Count - 1);
            _in = _out = -1;
            SaveEdits();
        }

        // 삽입 클립의 BGM/SFX 끄기. 클립이 쓰는 모든 영상(공통·언어별)이 Locale Capture 녹화여야 한다.
        private bool SetClipMute(EditList.Insert ins, MuteTarget target, bool on)
        {
            if (on)
            {
                var files = new List<string> { ins.File };
                files.AddRange(ins.LocaleFiles.Where(f => !string.IsNullOrEmpty(f)));
                string external = files.FirstOrDefault(f => !EditList.ClipStems(f, out string b, out string x)
                                                            || (target == MuteTarget.Bgm ? b == null : x == null));
                if (external != null)
                {
                    EditorUtility.DisplayDialog("음소거할 수 없음",
                        $"외부 영상은 BGM과 SFX가 한 트랙으로 섞여 있어 따로 끌 수 없습니다.\n\n{Path.GetFileName(external)}\n\n" +
                        "Locale Capture로 녹화한 영상(같은 폴더에 audio_bgm.wav·audio_sfx.wav가 있는 영상)만 클립 소리를 끌 수 있습니다.", "확인");
                    return false;
                }
            }
            if (target == MuteTarget.Bgm) ins.MuteBgm = on; else ins.MuteSfx = on;
            SaveEdits();
            return true;
        }

        private void AddInsert()
        {
            string file = EditorUtility.OpenFilePanel("Insert Clip", "", "mp4,mov,webm,mkv");
            if (string.IsNullOrEmpty(file)) return;
            _edits.Inserts.Add(new EditList.Insert { At = _time, File = file });
            _selected = (Kind.Insert, _edits.Inserts.Count - 1);
            SaveEdits();
        }

        private void DeleteSelected()
        {
            if (_selected == null) return;
            (Kind kind, int index) = _selected.Value;
            if (kind == Kind.Cut && index < _edits.Cuts.Count) _edits.Cuts.RemoveAt(index);
            else if (kind == Kind.Mute && index < _edits.Mutes.Count) _edits.Mutes.RemoveAt(index);
            else if (kind == Kind.Insert && index < _edits.Inserts.Count) _edits.Inserts.RemoveAt(index);
            else if (kind == Kind.Speed && index < _edits.Speeds.Count) _edits.Speeds.RemoveAt(index);
            else if (kind == Kind.Fade && index < _edits.Fades.Count) _edits.Fades.RemoveAt(index);
            _selected = null;
            SaveEdits();
        }

        private void HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || EditorGUIUtility.editingTextField || e.control || e.command || e.alt) return;
            switch (e.keyCode)
            {
                case KeyCode.Space: TogglePlay(); break;
                case KeyCode.LeftArrow: SeekDisplay(PlayheadDisplay - FrameStep * (e.shift ? 10 : 1)); break;
                case KeyCode.RightArrow: SeekDisplay(PlayheadDisplay + FrameStep * (e.shift ? 10 : 1)); break;
                case KeyCode.Home: SeekDisplay(0); break;
                case KeyCode.End: SeekDisplay(DisplayTotal); break;
                case KeyCode.I: SetIn(); break;
                case KeyCode.O: SetOut(); break;
                case KeyCode.B: AddMute(MuteTarget.Bgm); break;
                case KeyCode.S: AddMute(MuteTarget.Sfx); break;
                case KeyCode.M: AddMute(MuteTarget.All); break;
                case KeyCode.F: AddFade(); break;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    if (_in >= 0 && _out > _in) AddCut(); else DeleteSelected();
                    break;
                case KeyCode.Escape: _in = _out = -1; _selected = null; break;
                default: return;
            }
            e.Use();
            Repaint();
        }

        private void PickLocaleClips(EditList.Insert ins)
        {
            var menu = new GenericMenu();
            foreach (string locale in _locales)
            {
                string l = locale;
                int idx = ins.LocaleKeys.IndexOf(l);
                string current = idx >= 0 ? Path.GetFileName(ins.LocaleFiles[idx]) : "(공통)";
                menu.AddItem(new GUIContent($"{l}: {current}"), false, () =>
                {
                    string file = EditorUtility.OpenFilePanel($"{l} clip", "", "mp4,mov,webm,mkv");
                    if (string.IsNullOrEmpty(file)) return;
                    int i = ins.LocaleKeys.IndexOf(l);
                    if (i < 0) { ins.LocaleKeys.Add(l); ins.LocaleFiles.Add(file); }
                    else ins.LocaleFiles[i] = file;
                    SaveEdits();
                });
            }
            menu.ShowAsContext();
        }

        private void SaveEdits()
        {
            string json = JsonUtility.ToJson(_edits);
            if (_history != null && json != _history.Json)
            {
                Undo.RecordObject(_history, "Edit Videos");
                _history.Json = json;
            }
            _edits.Save(_directory);
            Repaint();
        }

        private void ExportAll()
        {
            StopPlayback();
            _activeInsert = -1;
            string defaultDir = Path.Combine(_directory, "edited");
            string outDir = EditorUtility.OpenFolderPanel("내보낼 폴더", Directory.Exists(defaultDir) ? defaultDir : _directory, "edited");
            if (string.IsNullOrEmpty(outDir)) return;

            CaptureSettings settings = LocaleCaptureWindow.SavedSettings();
            string ext = VideoEditExporter.Extension(settings);
            var ratios = Ratios.Where((r, i) => (RatioMask & (1 << i)) != 0).ToList();
            int total = ratios.Count * _locales.Length;
            var failed = new List<string>();
            try
            {
                int index = 0;
                foreach ((string label, int aw, int ah) in ratios)
                {
                    VideoEditExporter.RatioSize(aw, ah, settings, out int w, out int h);
                    EditList.Framing framing = _edits.FramingFor(label);
                    foreach (string locale in _locales)
                    {
                        string output = Path.Combine(outDir, $"{_manifest.Name}_{locale}_{w}x{h}.{ext}");
                        int done = index;
                        string error = VideoEditExporter.Export(VideoFile(locale), locale, _edits, output, BgmStem, SfxStem, settings, w, h, framing,
                            p => EditorUtility.DisplayProgressBar("Export", $"{label} {locale} ({done + 1}/{total})", (done + p) / total));
                        if (error.Length > 0) { failed.Add($"{label} {locale}"); Debug.LogError($"[LocaleCapture] {label} {locale} 내보내기 실패: {error}"); }
                        index++;
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (failed.Count == 0) EditorUtility.RevealInFinder(outDir);
            else EditorUtility.DisplayDialog("Export", "실패: " + string.Join(", ", failed) + "\n콘솔을 확인", "OK");
        }

        // 플레이헤드 장면을 언어별·비율별 PNG 로. 비율을 하나도 안 골랐으면 원본 크기.
        private void ExportThumbnails()
        {
            StopPlayback();
            string defaultDir = Path.Combine(_directory, "thumbnails");
            string outDir = EditorUtility.OpenFolderPanel("썸네일 폴더", Directory.Exists(defaultDir) ? defaultDir : _directory, "thumbnails");
            if (string.IsNullOrEmpty(outDir)) return;

            CaptureSettings settings = LocaleCaptureWindow.SavedSettings();
            var sizes = Ratios.Where((r, i) => (RatioMask & (1 << i)) != 0)
                .Select(r => { VideoEditExporter.RatioSize(r.aw, r.ah, settings, out int w, out int h); return (r.label, w, h); }).ToList();
            if (sizes.Count == 0) sizes.Add(("원본", 0, 0));
            int insert = _activeInsert;
            float time = insert >= 0 ? _clipTime : _time;
            string stamp = ResultAt(PlayheadDisplay).ToString("000.00", System.Globalization.CultureInfo.InvariantCulture);
            var failed = new List<string>();
            try
            {
                int index = 0, total = sizes.Count * _locales.Length;
                foreach ((string label, int w, int h) in sizes)
                foreach (string locale in _locales)
                {
                    EditorUtility.DisplayProgressBar("Thumbnail", $"{label} {locale}", index++ / (float) total);
                    string video = insert >= 0 ? _edits.Inserts[insert].FileFor(locale) : VideoFile(locale);
                    string size = w > 0 ? $"_{w}x{h}" : "";
                    string output = Path.Combine(outDir, $"{_manifest.Name}_{locale}{size}_{stamp}s.png");
                    string error = VideoEditExporter.Thumbnail(video, time, output, w, h, _edits.FramingFor(label));
                    if (error.Length > 0) { failed.Add($"{label} {locale}"); Debug.LogError($"[LocaleCapture] {label} {locale} 썸네일 실패: {error}"); }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            if (failed.Count == 0) EditorUtility.RevealInFinder(outDir);
            else EditorUtility.DisplayDialog("Thumbnail", "실패: " + string.Join(", ", failed) + "\n콘솔을 확인", "OK");
        }

        // ── 보조 ───────────────────────────────────────────────────────────────

        // 표시 시각 ↔ 화면 x
        private float X(Rect r, float display) => r.x + r.width * Mathf.Clamp01(display / Mathf.Max(0.001f, DisplayTotal));
        private float T(Rect r, float x) => Mathf.Clamp01((x - r.x) / Mathf.Max(1, r.width)) * DisplayTotal;

        private Rect Block(Rect track, float a, float b)
        {
            float x0 = X(track, Mathf.Min(a, b)), x1 = X(track, Mathf.Max(a, b));
            return new Rect(x0, track.y + 3, Mathf.Max(2, x1 - x0), track.height - 6);
        }

        // 비디오 트랙 아래쪽 띠(배속 구간)
        private Rect FadeBand(Rect track, float a, float b)
        {
            Rect block = Block(track, a, b);
            return new Rect(block.x, block.y, block.width, 12);
        }

        private Rect SpeedBand(Rect track, float a, float b)
        {
            Rect block = Block(track, a, b);
            return new Rect(block.x, block.yMax - 12, block.width, 12);
        }

        private bool IsSelected(Kind kind, int index) => _selected.HasValue && _selected.Value.kind == kind && _selected.Value.index == index;

        private static void DrawOutline(Rect r)
        {
            EditorGUI.DrawRect(new Rect(r.x - 1, r.y - 1, r.width + 2, 1), Color.white);
            EditorGUI.DrawRect(new Rect(r.x - 1, r.yMax, r.width + 2, 1), Color.white);
            EditorGUI.DrawRect(new Rect(r.x - 1, r.y, 1, r.height), Color.white);
            EditorGUI.DrawRect(new Rect(r.xMax, r.y, 1, r.height), Color.white);
        }

        private static string Fmt(float seconds)
        {
            if (seconds < 0) seconds = 0;
            int m = (int) (seconds / 60);
            float s = seconds - m * 60;
            return $"{m:00}:{s:00.00}";
        }

        private static GUIStyle _centered, _timecode, _rulerLabel, _blockLabel, _handleLabel, _trackLabel, _row, _clipBlockLabel;
        private static GUIStyle ClipBlockLabel => _clipBlockLabel ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft, normal = { textColor = Color.white }, clipping = TextClipping.Clip, padding = new RectOffset(4, 2, 0, 0) };
        private static GUIStyle CenteredLabel => _centered ??= new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true, normal = { textColor = Color.gray } };
        private static GUIStyle TimecodeStyle => _timecode ??= new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleLeft, fontSize = 12 };
        private static GUIStyle RulerLabel => _rulerLabel ??= new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.75f, 0.75f, 0.75f) } };
        private static GUIStyle BlockLabel => _blockLabel ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.black }, clipping = TextClipping.Clip };
        private static GUIStyle HandleLabel => _handleLabel ??= new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.black } };
        private static GUIStyle TrackLabel => _trackLabel ??= new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft, normal = { textColor = new Color(0.8f, 0.8f, 0.8f) } };
        private static GUIStyle Row => _row ??= new GUIStyle("box") { padding = new RectOffset(6, 6, 2, 2), margin = new RectOffset(4, 4, 1, 1) };
    }
}
