using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 예전 입력 방식(UnityEngine.Input) 기록·재생. 에디터가 게임·UI 코드의 Input 호출을 이쪽으로 바꿔 끼운다.
    // 기록: 프레임마다 게임이 물어본 값(마우스·터치·키·축)을 그대로 저장. 재생: 실제 입력은 무시하고 저장한 값을 돌려준다.
    // Input System 을 쓰는 게임은 InputEventTrace 가 맡고, 이쪽은 비어 있게 된다.
    public static class CaptureInput
    {
        public const string FileName = "input.legacy.json";

        private enum Mode { Off, Record, Replay }

        [Serializable]
        private sealed class FrameValues
        {
            public int Frame;
            public List<string> Keys = new List<string>();
            public List<string> Values = new List<string>();
        }

        [Serializable]
        private sealed class InputFile { public List<FrameValues> Frames = new List<FrameValues>(); }

        private static Mode _mode;
        private static string _path;
        private static int _frame;
        private static InputFile _file;
        private static FrameValues _current;
        private static Dictionary<string, string> _currentKeys;
        private static readonly Dictionary<int, Dictionary<string, string>> _replay = new Dictionary<int, Dictionary<string, string>>();
        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        internal static void BeginRecord(string path)
        {
            _mode = Mode.Record;
            _path = path;
            _frame = 0;
            _file = new InputFile();
            _current = null;
        }

        internal static void BeginReplay(string path)
        {
            _mode = Mode.Replay;
            _path = path;
            _frame = 0;
            _replay.Clear();
            if (!File.Exists(path)) return;
            InputFile file = JsonUtility.FromJson<InputFile>(File.ReadAllText(path));
            foreach (FrameValues f in file.Frames)
            {
                var map = new Dictionary<string, string>();
                for (int i = 0; i < f.Keys.Count && i < f.Values.Count; i++) map[f.Keys[i]] = f.Values[i];
                _replay[f.Frame] = map;
            }
        }

        internal static void SetFrame(int frame) => _frame = frame;

        internal static void End()
        {
            if (_mode == Mode.Record && _file != null && _file.Frames.Count > 0)
            {
                try { File.WriteAllText(_path, JsonUtility.ToJson(_file)); }
                catch (Exception e) { Debug.LogWarning("[LocaleCapture] 예전 입력 저장 실패: " + e.Message); }
            }
            _mode = Mode.Off;
            _file = null;
            _current = null;
            _replay.Clear();
        }

        private static string Get(string key, Func<string> real)
        {
            if (_mode == Mode.Off) return real();
            if (_mode == Mode.Replay)
                return _replay.TryGetValue(_frame, out Dictionary<string, string> map) && map.TryGetValue(key, out string v) ? v : null;

            if (_current == null || _current.Frame != _frame)
            {
                _current = new FrameValues { Frame = _frame };
                _currentKeys = new Dictionary<string, string>();
                _file.Frames.Add(_current);
            }
            if (_currentKeys.TryGetValue(key, out string seen)) return seen;
            string value;
            try { value = real(); }
            catch (InvalidOperationException) { value = null; }   // 예전 입력이 꺼진 프로젝트
            _currentKeys[key] = value;
            _current.Keys.Add(key);
            _current.Values.Add(value ?? "");
            return value;
        }

        // ── 변환 ───────────────────────────────────────────────────────────────

        private static string F(float v) => v.ToString("R", Ci);
        private static float PF(string s, int i, float d = 0) => s != null && TryPart(s, i, out string p) && float.TryParse(p, NumberStyles.Float, Ci, out float v) ? v : d;
        private static int PI(string s, int i, int d = 0) => s != null && TryPart(s, i, out string p) && int.TryParse(p, NumberStyles.Integer, Ci, out int v) ? v : d;

        private static bool TryPart(string s, int index, out string part)
        {
            string[] parts = s.Split('|');
            part = index < parts.Length ? parts[index] : null;
            return part != null;
        }

        private static bool B(string key, Func<bool> real) => Get(key, () => real() ? "1" : "0") == "1";
        private static float Fl(string key, Func<float> real) => PF(Get(key, () => F(real())), 0);

        private static string TouchToString(Touch t) => string.Join("|", t.fingerId.ToString(Ci), F(t.position.x), F(t.position.y), F(t.deltaPosition.x), F(t.deltaPosition.y),
            F(t.deltaTime), t.tapCount.ToString(Ci), ((int) t.phase).ToString(Ci), ((int) t.type).ToString(Ci), F(t.pressure), F(t.maximumPossiblePressure),
            F(t.radius), F(t.radiusVariance), F(t.rawPosition.x), F(t.rawPosition.y));

        private static Touch StringToTouch(string s)
        {
            var t = new Touch
            {
                fingerId = PI(s, 0), position = new Vector2(PF(s, 1), PF(s, 2)), deltaPosition = new Vector2(PF(s, 3), PF(s, 4)), deltaTime = PF(s, 5),
                tapCount = PI(s, 6), phase = (TouchPhase) PI(s, 7), type = (TouchType) PI(s, 8), pressure = PF(s, 9), maximumPossiblePressure = PF(s, 10, 1),
                radius = PF(s, 11), radiusVariance = PF(s, 12), rawPosition = new Vector2(PF(s, 13), PF(s, 14)),
            };
            return t;
        }

        // ── Input 대체(이름·시그니처는 바꿔 끼우는 원래 멤버와 1:1) ─────────────────

        public static Vector3 MousePosition()
        {
            string s = Get("mp", () => F(Input.mousePosition.x) + "|" + F(Input.mousePosition.y));
            return new Vector3(PF(s, 0), PF(s, 1), 0);
        }

        public static Vector2 MouseScrollDelta()
        {
            string s = Get("msd", () => F(Input.mouseScrollDelta.x) + "|" + F(Input.mouseScrollDelta.y));
            return new Vector2(PF(s, 0), PF(s, 1));
        }

        public static bool GetMouseButton(int button) => B("mb:" + button, () => Input.GetMouseButton(button));
        public static bool GetMouseButtonDown(int button) => B("mbd:" + button, () => Input.GetMouseButtonDown(button));
        public static bool GetMouseButtonUp(int button) => B("mbu:" + button, () => Input.GetMouseButtonUp(button));

        public static int TouchCount() => PI(Get("tc", () => Input.touchCount.ToString(Ci)), 0);
        public static Touch GetTouch(int index) => StringToTouch(Get("t:" + index, () => TouchToString(Input.GetTouch(index))));

        public static Touch[] Touches()
        {
            int count = TouchCount();
            var touches = new Touch[count];
            for (int i = 0; i < count; i++) touches[i] = GetTouch(i);
            return touches;
        }

        public static bool AnyKey() => B("ak", () => Input.anyKey);
        public static bool AnyKeyDown() => B("akd", () => Input.anyKeyDown);
        public static bool GetKey(KeyCode key) => B("k:" + (int) key, () => Input.GetKey(key));
        public static bool GetKeyDown(KeyCode key) => B("kd:" + (int) key, () => Input.GetKeyDown(key));
        public static bool GetKeyUp(KeyCode key) => B("ku:" + (int) key, () => Input.GetKeyUp(key));
        public static bool GetKey(string name) => B("ks:" + name, () => Input.GetKey(name));
        public static bool GetKeyDown(string name) => B("ksd:" + name, () => Input.GetKeyDown(name));
        public static bool GetKeyUp(string name) => B("ksu:" + name, () => Input.GetKeyUp(name));
        public static float GetAxis(string name) => Fl("ax:" + name, () => Input.GetAxis(name));
        public static float GetAxisRaw(string name) => Fl("axr:" + name, () => Input.GetAxisRaw(name));
        public static bool GetButton(string name) => B("b:" + name, () => Input.GetButton(name));
        public static bool GetButtonDown(string name) => B("bd:" + name, () => Input.GetButtonDown(name));
        public static bool GetButtonUp(string name) => B("bu:" + name, () => Input.GetButtonUp(name));
        public static string InputString() => Get("is", () => Input.inputString) ?? "";
    }
}
