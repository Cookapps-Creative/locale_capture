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

    // 기록의 Addressables 로딩 완료("addr" 동기 지점)를 재생의 완료와 짝짓는다.
    // 개수만 세면 재생에만 있는 로딩이 한 번 끼는 순간부터 그 뒤가 모두 한 칸씩 앞서 짝지어져, 늦게 끝난 로딩을 기다리지 못한다
    // (이 도구가 재생 때 언어 시스템을 일찍 건드려 부팅에 Locale 로딩이 몇 개 더 생기는 것만으로도 그렇게 된다).
    // 그래서 이름으로 먼저 짝짓고, 이름이 다른 것(언어마다 다른 글꼴·테이블)만 차례로 맞춘다:
    //   1. 같은 이름의 재생 완료
    //   2. 기록에 없는 이름의 재생 완료 중 같은 종류(작업·에셋 형식)인 것 — 언어만 다른 같은 로딩
    //   3. 기록에 없는 이름의 재생 완료 중 최근(Window 프레임 안) 것 — 구조가 다른 로딩(초기화 방식 차이 등)
    // 어느 것도 없으면 아직 안 끝난 것이다(기다린다). 기록에 이름이 없으면(이전 기록) 예전처럼 개수로만 맞춘다.
    internal sealed class LoadMatcher
    {
        private struct Spare { public int Frame; public string Kind; }

        private readonly bool _named;
        private readonly int _window;
        private readonly Dictionary<string, int> _expected = new Dictionary<string, int>();             // 아직 안 지난 기록 로딩(이름별 개수)
        private readonly Dictionary<string, Queue<int>> _byName = new Dictionary<string, Queue<int>>();  // 기록에 같은 이름이 남아 있는 재생 완료
        private readonly List<Spare> _spare = new List<Spare>();                                         // 기록에 없는 이름의 재생 완료(온 순서)

        // recordedNames: 기록의 로딩 이름(순서대로). null 이면 이름 없이 개수로만 맞춘다.
        internal LoadMatcher(IEnumerable<string> recordedNames, int window)
        {
            _window = window;
            _named = recordedNames != null;
            if (!_named) return;
            foreach (string name in recordedNames)
            {
                _expected.TryGetValue(name ?? "", out int count);
                _expected[name ?? ""] = count + 1;
            }
        }

        // 재생에서 로딩 하나가 끝났다.
        internal void Arrive(string name, int frame)
        {
            if (_named && name != null && _expected.TryGetValue(name, out int expected))
            {
                if (!_byName.TryGetValue(name, out Queue<int> q)) _byName[name] = q = new Queue<int>();
                if (q.Count < expected) { q.Enqueue(frame); return; }
            }
            _spare.Add(new Spare { Frame = frame, Kind = KindOf(name) });
        }

        // 기록의 로딩(recordedName, recordedFrame 에 끝남)에 맞는 재생 완료가 있으면 짝짓고 그 도착 프레임을 돌려준다.
        internal bool TryMatch(string recordedName, int recordedFrame, out int arrivedFrame)
        {
            arrivedFrame = 0;
            if (!_named) return Take(0, out arrivedFrame);
            string name = recordedName ?? "";
            if (_byName.TryGetValue(name, out Queue<int> q) && q.Count > 0)
            {
                arrivedFrame = q.Dequeue();
                Passed(name);
                return true;
            }
            string kind = KindOf(recordedName);
            int found = -1;
            if (kind != null) found = _spare.FindIndex(s => s.Kind == kind);
            if (found < 0) found = _spare.FindLastIndex(s => s.Frame >= recordedFrame - _window);
            if (found < 0) return false;
            Take(found, out arrivedFrame);
            Passed(name);
            return true;
        }

        // 기다리다 넘어간 기록 로딩 — 뒤늦게 같은 이름이 오면 기록에 없는 로딩으로 친다.
        internal void Skip(string recordedName)
        {
            if (_named) Passed(recordedName ?? "");
        }

        private bool Take(int index, out int frame)
        {
            frame = 0;
            if (index >= _spare.Count) return false;
            frame = _spare[index].Frame;
            _spare.RemoveAt(index);
            return true;
        }

        private void Passed(string name)
        {
            if (!_expected.TryGetValue(name, out int count)) return;
            if (count <= 1) _expected.Remove(name); else _expected[name] = count - 1;
            // 이 이름으로 더 올 기록이 없는데 남아 있는 재생 완료는 기록에 없는 로딩이다
            if (!_byName.TryGetValue(name, out Queue<int> q)) return;
            while (q.Count > count - 1) _spare.Add(new Spare { Frame = q.Dequeue(), Kind = KindOf(name) });
        }

        // 로딩의 종류: "작업 형식<에셋 형식>" — 대상 이름("(…)" 안)이나 주소는 뺀다. 예) "ProviderOperation Resource<Material>"
        internal static string KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int paren = name.IndexOf('(');
            if (paren > 0) return name.Substring(0, paren);
            int space = name.IndexOf(' ');
            return space > 0 ? name.Substring(0, space) : name;
        }
    }
}
