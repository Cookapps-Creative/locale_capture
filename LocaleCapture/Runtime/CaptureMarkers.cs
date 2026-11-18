using System.Collections.Generic;
using System.Text;

namespace CookApps.LocaleCapture
{
    // 동기 지점. 로딩이 끝날 때마다(씬·에셋·비동기 작업) 이름을 남긴다. 기록 때는 몇 번째 프레임에 끝났는지 저장하고,
    // 재생 때는 같은 프레임까지 같은 로딩이 끝나지 않았으면 게임 시간을 멈추고 기다린다 — 기기 부하에 따라 로딩이
    // 빨리·늦게 끝나도 입력이 같은 화면에 들어간다. 에디터 패치(AsyncOperation 완료, Addressables 진단)와 세션이 부른다.
    public static class CaptureMarkers
    {
        private static readonly List<string> _hits = new List<string>();

        public static void Hit(string name)
        {
            if (!CaptureClock.Active || string.IsNullOrEmpty(name)) return;
            lock (_hits) _hits.Add(name);
        }

        // 끝난 로딩의 이름(진단용). 맞추는 데는 쓰지 않는다 — 언어마다 대상이 다르다.
        private static readonly List<string> _loaded = new List<string>();

        public static void Loaded(string name)
        {
            if (!CaptureClock.Active || string.IsNullOrEmpty(name)) return;
            lock (_loaded) _loaded.Add(name);
        }

        internal static void DrainLoaded(int frame, List<ReplayManifest.Marker> into, int max)
        {
            lock (_loaded)
            {
                if (into != null)
                    foreach (string name in _loaded)
                        if (into.Count < max) into.Add(new ReplayManifest.Marker { Frame = frame, Name = name });
                _loaded.Clear();
            }
        }

        internal static void Drain(List<string> into)
        {
            lock (_hits)
            {
                into.AddRange(_hits);
                _hits.Clear();
            }
        }

        internal static void Clear()
        {
            lock (_hits) _hits.Clear();
            lock (_loaded) _loaded.Clear();
            ReleaseActivation();
            GateActivation = false;
            _sceneLoads.Clear();
        }

        // 진행 중인 씬 로드. 로딩 대기는 실제로 씬을 읽는 중일 때만 건다 — 게임이 아직 로드를 시작 안 했거나
        // (게임 시간이 흘러야 시작), 다 읽고 게임의 활성화 허락만 기다리면 대기가 오히려 서로 기다리게 만든다.
        private static readonly List<UnityEngine.AsyncOperation> _sceneLoads = new List<UnityEngine.AsyncOperation>();

        public static void SceneLoadStarted(UnityEngine.AsyncOperation op)
        {
            if (!CaptureClock.Active || op == null) return;
            _sceneLoads.Add(op);
            if (GateActivation && op.allowSceneActivation)
            {
                SetNative(op, false);
                _gated[op] = true;
            }
        }

        // 재생에서 씬을 기록보다 일찍 다 읽으면 기록 때 열린 프레임까지 활성화를 미룬다(늦으면 로딩 대기가 맞춘다).
        // 미루는 동안 게임·Addressables 가 보는 allowSceneActivation 은 게임이 바란 값 그대로다(에디터 패치가 가로챈다) —
        // false 로 보이면 Addressables 가 "활성화 보류"로 알고 로드를 끝난 것으로 처리한다.
        internal static bool GateActivation;
        private static readonly Dictionary<UnityEngine.AsyncOperation, bool> _gated = new Dictionary<UnityEngine.AsyncOperation, bool>();
        private static bool _bypass;

        private static void SetNative(UnityEngine.AsyncOperation op, bool value)
        {
            _bypass = true;
            try { op.allowSceneActivation = value; }
            finally { _bypass = false; }
        }

        // 에디터 패치(allowSceneActivation set/get prefix)가 부른다. true 면 원래 동작을 건너뛴다.
        public static bool InterceptSet(UnityEngine.AsyncOperation op, bool value)
        {
            if (_bypass || op == null || !_gated.ContainsKey(op)) return false;
            _gated[op] = value;
            return true;
        }

        public static bool InterceptGet(UnityEngine.AsyncOperation op, out bool value)
        {
            value = false;
            return !_bypass && op != null && _gated.TryGetValue(op, out value);
        }

        // 미뤄 둔 활성화를 게임이 바란 값으로 돌려놓는다.
        internal static void ReleaseActivation()
        {
            foreach (KeyValuePair<UnityEngine.AsyncOperation, bool> g in _gated)
                if (g.Key != null && !g.Key.isDone) SetNative(g.Key, g.Value);
            _gated.Clear();
        }

        internal static bool SceneLoading()
        {
            _sceneLoads.RemoveAll(op => op == null || op.isDone);
            foreach (UnityEngine.AsyncOperation op in _sceneLoads)
                if (op.allowSceneActivation || op.progress < 0.9f) return true;
            return false;
        }

        // 실행마다 달라지는 부분(숫자·16진 해시)을 지워 같은 로딩이면 같은 이름이 되게 한다.
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (!char.IsDigit(c)) sb.Append(c);
            return sb.Length > 120 ? sb.ToString(0, 120) : sb.ToString();
        }
    }
}
