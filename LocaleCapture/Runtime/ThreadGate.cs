using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace CookApps.LocaleCapture
{
    // 작업 스레드에서 메인 스레드로 넘어오는 이어받기(await Task.Run, UniTask.RunOnThreadPool·SwitchToMainThread 등)를 붙잡아,
    // 기록 때 메인 스레드에 넘겨진 프레임에 재생에서도 넘긴다. 작업 스레드가 끝나는 때는 실제 시간이라 그대로 두면 이어받는 프레임이
    // 실행마다 한두 프레임씩 달라진다 — 그 뒤의 서버 요청·UI 가 녹화와 어긋난다.
    //  · 기록: 도착한 것을 세션 Update 에서 원래 큐(Unity 동기화 컨텍스트·UniTask 루프)로 넘기고, 넘긴 프레임과 누구 것인지 남긴다.
    //  · 재생: 같은 코드의 같은 순번을 녹화 프레임에 넘긴다. 일찍 왔으면 붙잡고, 아직이면 메인 스레드를 잠깐 세워 기다린다
    //    (작업 스레드는 메인 스레드와 따로 돌므로 프레임을 더 돌리지 않고 기다릴 수 있다).
    // 에디터 쪽 코드의 것은 손대지 않는다. 진입점은 에디터가 Harmony 로 연결한다.
    public static class ThreadGate
    {
        [Serializable]
        public struct Post
        {
            public int Frame;
            public string Key;   // 이어받는 코드(비동기 메서드의 상태 기계 타입 등)
        }

        private sealed class Item
        {
            public string Key;
            public Action Run;   // 원래 큐에 넣는다
        }

        private const int WaitMs = 2000;     // 재생에서 한 건을 기다리는 최대 시간
        private const int MaxPosts = 200000;
        private static bool _capWarned;
        private static readonly List<Item> _arrived = new List<Item>();
        private static readonly Dictionary<string, int> _remaining = new Dictionary<string, int>();   // 재생: 아직 안 넘긴 녹화 건수(코드별)
        private static List<Post> _recorded;
        private static int _next;
        private static bool _active, _recording;
        private static int _frame;

        // 이번 세션에서 메인 스레드로 넘긴 것(프레임·코드). 기록이면 manifest 에 저장된다.
        public static List<Post> Log { get; private set; } = new List<Post>();
        public static int Missed { get; private set; }   // 재생: 녹화에는 있는데 제때 안 온 건수

        internal static void BeginRecord()
        {
            Reset();
            _recording = true;
            _active = true;
        }

        // recorded: 기록의 목록. null 이면(이전 형식 기록) 붙잡지 않는다.
        internal static void BeginReplay(List<Post> recorded)
        {
            Reset();
            _recorded = recorded;
            if (recorded == null) return;
            foreach (Post p in recorded) _remaining[p.Key] = _remaining.TryGetValue(p.Key, out int n) ? n + 1 : 1;
            _active = true;
        }

        private static void Reset()
        {
            lock (_editor) _editor.Clear();
            lock (_arrived) _arrived.Clear();
            _remaining.Clear();
            _recorded = null;
            _next = 0;
            _frame = 0;
            _recording = false;
            _active = false;
            _capWarned = false;
            Missed = 0;
            Log = new List<Post>();
        }

        internal static void End()
        {
            _active = false;
            List<Item> rest;
            lock (_arrived) { rest = new List<Item>(_arrived); _arrived.Clear(); }
            foreach (Item item in rest) Run(item);   // 남은 것은 모두 넘긴다(게임이 멈춰 있지 않게)
        }

        // Harmony prefix 가 부른다. 다른 스레드에서 메인 스레드로 넘기는 게임 코드의 이어받기면 붙잡고 true(원래 호출을 건너뛴다).
        public static bool Capture(Delegate callback, object state, Action enqueueOriginal)
        {
            if (!_active || CaptureClock.OnMainThread) return false;
            string key = KeyOf(callback, state);
            if (key == null) return false;   // 에디터 쪽·알 수 없는 코드
            lock (_arrived)
            {
                _arrived.Add(new Item { Key = key, Run = enqueueOriginal });
                Monitor.PulseAll(_arrived);
            }
            return true;
        }

        // 세션 Update 에서 부른다.
        internal static void Release(int frame)
        {
            if (!_active) return;
            _frame = frame;
            if (_recording)
            {
                List<Item> all;
                lock (_arrived)
                {
                    if (_arrived.Count == 0) return;
                    all = new List<Item>(_arrived);
                    _arrived.Clear();
                }
                foreach (Item item in all) { Note(item.Key); Run(item); }
                return;
            }

            // 이 프레임까지 녹화된 것을 순서대로 넘긴다
            while (_next < _recorded.Count && _recorded[_next].Frame <= frame)
            {
                string key = _recorded[_next++].Key;
                _remaining[key] = _remaining.TryGetValue(key, out int n) ? n - 1 : 0;
                Item item = Take(key, WaitMs);
                if (item == null)
                {
                    Missed++;
                    CaptureVerify.ThreadMissed(frame, key);
                    continue;
                }
                Note(key);
                Run(item);
            }
            // 녹화에 없는 것(뒤쪽 프레임에도 같은 코드가 남아 있지 않은 것)은 붙잡지 않고 바로 넘긴다
            List<Item> extra = null;
            lock (_arrived)
            {
                var held = new Dictionary<string, int>();
                for (int i = 0; i < _arrived.Count; i++)
                {
                    Item item = _arrived[i];
                    held.TryGetValue(item.Key, out int h);
                    _remaining.TryGetValue(item.Key, out int left);
                    if (h < left) { held[item.Key] = h + 1; continue; }   // 뒤에 넘길 녹화 건이 남아 있다 — 그 프레임까지 붙잡는다
                    (extra ??= new List<Item>()).Add(item);
                    _arrived.RemoveAt(i--);
                }
            }
            if (extra == null) return;
            foreach (Item item in extra)
            {
                Note(item.Key);
                CaptureVerify.ThreadExtra(frame, item.Key);
                Run(item);
            }
        }

        // 도착한 것 중 key 의 첫 건. 없으면 올 때까지 메인 스레드를 세워 기다린다(최대 waitMs).
        private static Item Take(string key, int waitMs)
        {
            lock (_arrived)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    for (int i = 0; i < _arrived.Count; i++)
                    {
                        if (_arrived[i].Key != key) continue;
                        Item item = _arrived[i];
                        _arrived.RemoveAt(i);
                        return item;
                    }
                    int left = waitMs - (int) watch.ElapsedMilliseconds;
                    if (left <= 0 || !Monitor.Wait(_arrived, left)) return null;
                }
            }
        }

        private static void Note(string key)
        {
            if (Log.Count < MaxPosts) { Log.Add(new Post { Frame = _frame, Key = key }); return; }
            // 한도를 넘으면 그 뒤는 기록에 안 남아 재생에서 프레임을 맞추지 못한다 — 조용히 넘어가지 않게 알린다.
            if (_capWarned) return;
            _capWarned = true;
            UnityEngine.Debug.LogError($"[LocaleCapture] 작업 스레드 이어받기가 {MaxPosts}건을 넘었다(프레임 {_frame}) — 이 뒤로는 재생에서 프레임을 맞추지 못한다. 더 짧게 나눠 녹화한다");
        }

        private static void Run(Item item)
        {
            try { item.Run(); }
            catch (Exception e) { UnityEngine.Debug.LogException(e); }
        }

        // ── 누구의 이어받기인가 ────────────────────────────────────────────────

        private static readonly Dictionary<Type, string> _names = new Dictionary<Type, string>();
        private static readonly Dictionary<Assembly, bool> _editor = new Dictionary<Assembly, bool>();

        // 게임(플레이어) 쪽 어셈블리 이름. 에디터가 패치를 걸 때 넘긴다 — 에디터에서는 게임 어셈블리도 UnityEditor 를 참조할 수 있어
        // (#if UNITY_EDITOR 코드) 참조만으로는 에디터 코드인지 가릴 수 없다. 없으면 이름으로 짐작한다.
        public static HashSet<string> PlayerAssemblies;
        private static readonly string[] Inner = { "m_action", "m_stateMachine", "StateMachine", "stateMachine", "m_continuation", "continuation" };

        // 이어받기를 가진 코드의 이름. 대리자·러너를 몇 겹 벗겨 비동기 메서드의 상태 기계 타입까지 찾는다.
        // 에디터 어셈블리의 것이거나 게임 코드를 못 찾으면 null(붙잡지 않는다).
        private static string KeyOf(Delegate callback, object state)
        {
            try
            {
                object subject = state ?? callback;
                Type found = null;
                for (int depth = 0; depth < 6 && subject != null; depth++)
                {
                    if (subject is Delegate d)
                    {
                        if (d.Target == null) { found = Game(d.Method.DeclaringType) ?? found; break; }
                        subject = d.Target;
                        continue;
                    }
                    Type type = subject.GetType();
                    Type game = Game(type);
                    if (game != null) found = game;
                    object next = null;
                    foreach (string name in Inner)
                    {
                        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field == null) continue;
                        next = field.GetValue(subject);
                        if (next != null) break;
                    }
                    if (next == null) break;
                    subject = next;
                }
                if (found == null && callback != null && state == null) found = Game(callback.Method.DeclaringType);
                if (found == null) return null;
                lock (_names)
                {
                    if (_names.TryGetValue(found, out string cached)) return cached;
                    string full = found.FullName ?? found.Name;
                    int bracket = full.IndexOf('[');   // 닫힌 제네릭의 어셈블리 표기는 뗀다
                    return _names[found] = bracket > 0 ? full.Substring(0, bracket) : full;
                }
            }
            catch { return null; }
        }

        // 게임(플레이어) 쪽 타입이면 그 타입, 제네릭 러너면 인자 중 게임 타입. 시스템·Unity 엔진·에디터 것이면 null.
        private static Type Game(Type type)
        {
            if (type == null) return null;
            if (type.IsGenericType)
                foreach (Type arg in type.GetGenericArguments())
                {
                    Type inner = Game(arg);
                    if (inner != null) return inner;
                }
            Assembly asm = type.Assembly;
            string name = asm.GetName().Name;
            if (name == "mscorlib" || name == "netstandard" || name.StartsWith("System") || name.StartsWith("UnityEngine") || name.StartsWith("UnityEditor") ||
                name == "UniTask" || name.StartsWith("UniTask.") || name.StartsWith("CookApps.LocaleCapture")) return null;
            bool editor;
            lock (_editor)
                if (!_editor.TryGetValue(asm, out editor))
                {
                    HashSet<string> player = PlayerAssemblies;
                    if (player != null) editor = !player.Contains(name);
                    else
                    {
                        string location = "";
                        try { location = asm.IsDynamic ? "" : asm.Location; } catch { /* 위치 없음 */ }
                        editor = name.Contains("Editor") || name.StartsWith("Microsoft.CodeAnalysis") || string.IsNullOrEmpty(location);
                    }
                    _editor[asm] = editor;
                }
            return editor ? null : type;
        }
    }
}
