using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 실시간 통신(Photon) 테이프. HTTP(NetworkTape)와 달리 연결을 붙든 채 서버·다른 기기가 보내는 것을 받는 통신이라,
    // 요청-응답 짝이 아니라 "통신 라이브러리가 게임 쪽에 넘긴 것" 을 일어난 순서대로 남긴다.
    //
    // 경계: PhotonPeer. 그 위의 코드(Realtime·PUN·게임)는 이 피어에 호출(Connect·SendOperation·Disconnect…)을 하고, 피어는
    //       리스너(IPhotonPeerListener)에게 상태 변화·요청의 응답·이벤트를 넘긴다. 방·플레이어·방장 같은 상태는 전부 이 콜백으로 만들어진다.
    //  · 기록: 호출과 콜백을 순서대로, 세션 프레임과 함께 적는다. 콜백의 내용은 라이브러리의 직렬화기로 바이트로 만든다.
    //  · 재생: 실제 서버에 접속하지 않는다. 호출은 기록의 결과만 돌려주고, 콜백은 기록 프레임에 "받기"(DispatchIncomingCommands) 안에서 넘긴다.
    //          상대 기기가 보낸 메시지·매칭 결과가 녹화와 같은 프레임에 같은 내용으로 온다. 지역 고르기(핑)도 재지 않고 기록한 결과를 넘긴다.
    //  · 순서: 콜백은 그 앞에 기록된 호출을 게임이 한 뒤에만 넘긴다(요청보다 응답이 먼저 가지 않게).
    //
    // 타입은 이름으로 찾는다 — 이 도구는 Photon 을 참조하지 않고, Photon 이 없는 프로젝트에서는 아무 일도 하지 않는다.
    // 진입점은 에디터가 Harmony 로 연결한다(CapturePatches). 테이프 파일에는 접속 토큰이 그대로 들어 있다 — 테이크 폴더 밖으로 내보내지 않는다.
    public static class RealtimeTape
    {
        public const string FileName = "realtime.tape.json";

        public enum Mode { Off, Record, Replay, Watch }

        public const int KindCall = 1, KindCallback = 2, KindState = 3, KindRegion = 4;
        public const string RegionCall = "RegionPing";
        public const string SentDiffers = "보낸 내용이 녹화와 다르다";   // 결과에서 이 종류의 확인할 곳을 가려낼 때 쓴다
        private const int Global = -1;          // 피어에 딸리지 않은 항목(지역 고르기)
        private const int CallGraceFrames = 90; // 재생: 기록에 있던 호출을 게임이 이만큼 지나도 하지 않으면 건너뛰고 알린다(뒤의 콜백이 막히지 않게)
        private const int MaxNotes = 8;

        [Serializable]
        public sealed class Entry
        {
            public int Frame;
            public int Kind;
            public int Peer;         // 피어 순번(이 세션에서 처음 쓰인 순서). 지역 고르기는 -1
            public string Name;      // 호출: 메서드 이름 / 콜백: status·response·event·disconnect·message
            public int Code;         // 연산 코드·이벤트 코드·상태 코드·피어 상태 값
            public int Result = -1;  // 호출 결과(1 = true, 0 = false, -1 = 없음)
            public int Nested;       // 콜백: 1 = 게임의 호출 안에서 곧바로 불렸다(받기를 거치지 않았다)
            public int Keys;         // 이벤트: 보낸 사람·내용을 꺼내는 키(SenderKey << 8 | CustomDataKey)
            public int Sender = -1;  // 이벤트: 보낸 사람 번호. 라이브러리는 이것을 읽을 때 값 묶음에서 빼 따로 두고, 쓸 때는 값 묶음만 쓴다 — 따로 적는다
            public string Data;      // 직렬화한 내용(base64)
            public string Hash;      // 호출: 보낸 내용의 지문(녹화와 다른 내용을 보냈는지 견준다)
            [NonSerialized] public bool Used;
        }

        [Serializable]
        private sealed class Tape
        {
            public int Version = 1;
            public string Library;
            public List<Entry> Entries = new List<Entry>();
        }

        private sealed class PeerInfo
        {
            public int Index;
            public object Peer;
            public object Protocol;
            public int LastState = int.MinValue;   // 기록: 마지막으로 적은 피어 상태
            public int State;                       // 재생: 지금 흉내 내는 피어 상태(0 = 끊김)
        }

        public static Mode Current { get; private set; }
        public static int Delivered { get; private set; }   // 재생: 넘긴 콜백 수
        public static int Calls { get; private set; }       // 재생: 기록과 맞춘 호출 수
        public static int Live { get; private set; }        // 실시간 통신 기록이 없는 녹화를 재생하며 실제 서버에 접속한 횟수
        public static int Recorded => _entries?.Count ?? 0;

        // 기록에서 리스너 타입을 처음 봤을 때 그 콜백 메서드에 패치를 건다(에디터가 넣어 준다).
        public static Action<Type> ListenerPatcher;
        // 기록에서 지역 고르기가 끝났을 때 불릴 메서드를 처음 봤을 때 거기에 패치를 건다(에디터가 넣어 준다).
        public static Action<MethodInfo> RegionPatcher;

        private static readonly object _lock = new object();
        private static readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private static readonly List<string> _notes = new List<string>();
        private static List<Entry> _entries;
        private static string _directory;
        private static int _frame, _next, _generation, _unexpected;
        private static bool _delivering, _warned;
        private static object _regionHandler;
        private static Delegate _regionCallback;

        // 이 스레드가 지금 들어가 있는 곳(기록에서만 쓴다): 게임의 호출 안·받기 안·콜백 안
        private const int InCall = 1, InDispatch = 2, InCallback = 3;
        [ThreadStatic] private static List<int> t_stack;
        [ThreadStatic] private static List<Entry> t_calls;
        [ThreadStatic] private static int t_generation;

        private static List<int> Stack
        {
            get
            {
                if (t_stack == null) { t_stack = new List<int>(8); t_calls = new List<Entry>(4); }
                if (t_generation != _generation) { t_stack.Clear(); t_calls.Clear(); t_generation = _generation; }
                return t_stack;
            }
        }

        // ── 세션 ───────────────────────────────────────────────────────────────

        internal static void BeginRecord(string directory)
        {
            Reset(directory);
            if (!Bind()) return;
            _entries = new List<Entry>();
            Current = Mode.Record;
        }

        // hasTape: 이 녹화가 실시간 통신을 기록하는 판으로 만들어졌는가. 아니면(이전 형식) 실제 통신을 그대로 두고 접속한 횟수만 센다.
        internal static void BeginReplay(string directory, bool hasTape)
        {
            Reset(directory);
            if (!Bind()) return;
            if (!hasTape) { Current = Mode.Watch; return; }
            _entries = new List<Entry>();
            string path = Path.Combine(directory, FileName);
            if (File.Exists(path))
            {
                try { _entries = JsonUtility.FromJson<Tape>(File.ReadAllText(path))?.Entries ?? _entries; }
                catch (Exception e) { Debug.LogWarning("[LocaleCapture] 실시간 통신 기록을 읽지 못했다 — " + e.Message); }
            }
            Current = Mode.Replay;
        }

        private static void Reset(string directory)
        {
            lock (_lock)
            {
                Current = Mode.Off;
                _directory = directory;
                _entries = null;
                _peers.Clear();
                _notes.Clear();
                _frame = 0;
                _next = 0;
                _unexpected = 0;
                _generation++;
                _delivering = false;
                _warned = false;
                _regionHandler = null;
                _regionCallback = null;
                Delivered = 0;
                Calls = 0;
                Live = 0;
            }
        }

        internal static void Tick(int frame) => _frame = frame;

        // frames: 이 세션이 돈 프레임 수(재생: 그 안에 기록됐는데 재생되지 않은 것을 알린다).
        internal static void End(bool save, int frames)
        {
            Mode mode = Current;
            Current = Mode.Off;
            if (mode == Mode.Record)
            {
                List<Entry> entries;
                lock (_lock) entries = _entries == null ? new List<Entry>() : new List<Entry>(_entries);
                if (save && _directory != null)
                {
                    try
                    {
                        File.WriteAllText(Path.Combine(_directory, FileName),
                            JsonUtility.ToJson(new Tape { Library = _peerType?.Assembly.GetName().ToString(), Entries = entries }));
                        if (entries.Count > 0) Debug.Log($"[LocaleCapture] 실시간 통신 {entries.Count}건 기록(연결 {_peers.Count}개)");
                    }
                    catch (Exception e) { Debug.LogWarning("[LocaleCapture] 실시간 통신 기록을 저장하지 못했다 — " + e.Message); }
                }
            }
            else if (mode == Mode.Replay)
            {
                int left = 0;
                Entry first = null;
                lock (_lock)
                {
                    foreach (Entry e in _entries)
                    {
                        if (e.Used || e.Kind == KindState || e.Frame >= frames) continue;
                        left++;
                        first ??= e;
                    }
                }
                if (left > 0)
                    CaptureVerify.RealtimeDiffered(first.Frame, $"기록한 실시간 통신 {left}건이 재생되지 않았다 — 처음: 프레임 {first.Frame} {Describe(first)}");
                CaptureVerify.RealtimeSummary(_entries.Count, Delivered, Calls, 0, _notes);
                if (_entries.Count > 0) Debug.Log($"[LocaleCapture] 실시간 통신 재생: 넘긴 콜백 {Delivered}건, 맞춘 호출 {Calls}건, 남은 것 {left}건(기록 {_entries.Count}건)");
            }
            else if (mode == Mode.Watch)
            {
                CaptureVerify.RealtimeSummary(0, 0, 0, Live, _notes);
            }
            lock (_lock) { _peers.Clear(); _regionHandler = null; _regionCallback = null; }
        }

        // ── 라이브러리 찾기 ─────────────────────────────────────────────────────

        private static readonly string[] Namespaces = { "Photon.Client", "ExitGames.Client.Photon" };
        private static bool _bindTried;
        private static Type _peerType, _listenerType, _eventType, _responseType, _disconnectType, _protocolType, _streamType, _stateEnum, _statusEnum;
        private static Type _regionType, _fallbackType;
        private static string _namespace;
        private static PropertyInfo _listenerProperty;
        private static Func<object, int> _stateOf;
        private static FieldInfo _peerBaseField;
        private static Type _connectionStateType;
        private static int _stateConnected, _stateInitializing;
        private static MethodInfo _serEvent, _serResponse, _serRequest, _desEvent, _desResponse, _serObject, _desObject, _toArray;
        private static ConstructorInfo _streamNew, _streamFrom;
        private static Action<object, object> _onStatus, _onResponse, _onEvent, _onDisconnect;
        private static Action<object, bool, object> _onMessage;

        public static bool Bound { get; private set; }
        public static string BindProblem { get; private set; }   // 라이브러리는 있는데 붙지 못한 까닭(없으면 null)
        public static string LibraryNamespace => _namespace;
        public static Type PeerType => _peerType;
        public static Type ListenerType => _listenerType;
        public static Type StateEnum => _stateEnum;
        public static Type RegionHandlerType => _regionType;
        public static Type FallbackType => _fallbackType;

        // Photon 이 이 프로젝트에 있고 필요한 멤버를 모두 찾았으면 true. 없으면 false(BindProblem 도 null).
        public static bool Bind()
        {
            if (_bindTried) return Bound;
            _bindTried = true;
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    foreach (string ns in Namespaces)
                    {
                        Type peer = assembly.GetType(ns + ".PhotonPeer", false);
                        if (peer == null) continue;
                        _peerType = peer;
                        _namespace = ns;
                        break;
                    }
                    if (_peerType != null) break;
                }
                if (_peerType == null) return false;

                Assembly lib = _peerType.Assembly;
                Type Need(string name) => lib.GetType(_namespace + "." + name, false) ?? throw new MissingMemberException(_namespace + "." + name);
                _listenerType = Need("IPhotonPeerListener");
                _eventType = Need("EventData");
                _responseType = Need("OperationResponse");
                _disconnectType = lib.GetType(_namespace + ".DisconnectMessage", false);
                _protocolType = lib.GetType(_namespace + ".Protocol", false) ?? Need("IProtocol");
                _streamType = Need("StreamBuffer");
                _stateEnum = Need("PeerStateValue");
                _statusEnum = Need("StatusCode");

                _listenerProperty = _peerType.GetProperty("Listener") ?? throw new MissingMemberException("PhotonPeer.Listener");
                PropertyInfo state = _peerType.GetProperty("PeerState") ?? throw new MissingMemberException("PhotonPeer.PeerState");
                ParameterExpression p = Expression.Parameter(typeof(object), "peer");
                _stateOf = Expression.Lambda<Func<object, int>>(
                    Expression.Convert(Expression.Property(Expression.Convert(p, _peerType), state), typeof(int)), p).Compile();

                const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                // 재생에서 피어 상태를 흉내 낼 자리: 피어 안쪽 객체의 연결 상태와 "앱 초기화 끝남" 표시.
                // 상태를 읽는 getter 를 가로채지 않고 이 값을 기록대로 맞춰 둔다 — getter 는 원래 것이 그대로 돈다.
                _peerBaseField = _peerType.GetField("peerBase", instance) ?? throw new MissingMemberException("PhotonPeer.peerBase");
                Type peerBase = _peerBaseField.FieldType;
                _connectionStateType = (peerBase.GetProperty("peerConnectionState", instance)?.PropertyType ?? peerBase.GetField("peerConnectionState", instance)?.FieldType)
                                       ?? throw new MissingMemberException(peerBase.Name + ".peerConnectionState");
                if (!_connectionStateType.IsEnum) throw new MissingMemberException(peerBase.Name + ".peerConnectionState(enum)");
                if (peerBase.GetField("ApplicationIsInitialized", instance) == null) throw new MissingMemberException(peerBase.Name + ".ApplicationIsInitialized");
                _stateConnected = Convert.ToInt32(Enum.Parse(_stateEnum, "Connected"));
                _stateInitializing = Convert.ToInt32(Enum.Parse(_stateEnum, "InitializingApplication"));
                MethodInfo Find(string name, Type first, int minParameters)
                {
                    foreach (MethodInfo m in _protocolType.GetMethods(instance))
                    {
                        ParameterInfo[] ps = m.GetParameters();
                        if (m.Name == name && ps.Length >= minParameters && ps.Length > 0 && ps[0].ParameterType == first) return m;
                    }
                    throw new MissingMemberException(_protocolType.Name + "." + name);
                }
                _serEvent = Find("SerializeEventData", _streamType, 3);
                _serResponse = Find("SerializeOperationResponse", _streamType, 3);
                _serRequest = Find("SerializeOperationRequest", _streamType, 4);
                _desEvent = Find("DeserializeEventData", _streamType, 1);
                _desResponse = Find("DeserializeOperationResponse", _streamType, 1);
                _serObject = _protocolType.GetMethod("Serialize", new[] { typeof(object) });
                _desObject = _protocolType.GetMethod("Deserialize", new[] { typeof(byte[]) });
                _streamNew = _streamType.GetConstructor(new[] { typeof(int) }) ?? throw new MissingMemberException("StreamBuffer(int)");
                _streamFrom = _streamType.GetConstructor(new[] { typeof(byte[]) }) ?? throw new MissingMemberException("StreamBuffer(byte[])");
                _toArray = _streamType.GetMethod("ToArray", Type.EmptyTypes) ?? throw new MissingMemberException("StreamBuffer.ToArray");

                _onStatus = Invoker("OnStatusChanged", _statusEnum);
                _onResponse = Invoker("OnOperationResponse", _responseType);
                _onEvent = Invoker("OnEvent", _eventType);
                if (_disconnectType != null && _listenerType.GetMethod("OnDisconnectMessage") != null) _onDisconnect = Invoker("OnDisconnectMessage", _disconnectType);
                MethodInfo message = _listenerType.GetMethod("OnMessage");
                if (message != null && message.GetParameters().Length == 2)
                {
                    ParameterExpression l = Expression.Parameter(typeof(object)), raw = Expression.Parameter(typeof(bool)), msg = Expression.Parameter(typeof(object));
                    _onMessage = Expression.Lambda<Action<object, bool, object>>(Expression.Call(Expression.Convert(l, _listenerType), message, raw, msg), l, raw, msg).Compile();
                }

                // 그 위의 층(지역 고르기·연결 유지 스레드). 없어도 된다(다른 층을 쓰는 프로젝트).
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _regionType ??= assembly.GetType("Photon.Realtime.RegionHandler", false);
                    _fallbackType ??= assembly.GetType("Photon.Realtime.ConnectionHandler", false);
                }
                Bound = true;
            }
            catch (Exception e)
            {
                BindProblem = e.GetBaseException().Message;
                Bound = false;
            }
            return Bound;
        }

        private static Action<object, object> Invoker(string name, Type argument)
        {
            MethodInfo method = _listenerType.GetMethod(name) ?? throw new MissingMemberException("IPhotonPeerListener." + name);
            ParameterExpression l = Expression.Parameter(typeof(object)), a = Expression.Parameter(typeof(object));
            return Expression.Lambda<Action<object, object>>(
                Expression.Call(Expression.Convert(l, _listenerType), method, Expression.Convert(a, argument)), l, a).Compile();
        }

        // 이 코드(타입.메서드)가 통신 라이브러리 안쪽인가. 재생에서는 라이브러리 안쪽(소켓·암호화·정적 초기화)이 돌지 않으므로,
        // 거기서 만든 new Random() 은 녹화와 견줄 것이 아니다.
        public static bool IsLibraryCode(string creator) =>
            _namespace != null && creator != null && creator.StartsWith(_namespace + ".", StringComparison.Ordinal);

        // 리스너의 콜백 이름 → 테이프의 종류 이름. 명시적으로 구현한 메서드는 이름 앞에 인터페이스가 붙는다.
        public static string CallbackKind(string methodName)
        {
            if (methodName == null) return null;
            if (methodName.EndsWith("OnStatusChanged", StringComparison.Ordinal)) return "status";
            if (methodName.EndsWith("OnOperationResponse", StringComparison.Ordinal)) return "response";
            if (methodName.EndsWith("OnEvent", StringComparison.Ordinal)) return "event";
            if (methodName.EndsWith("OnDisconnectMessage", StringComparison.Ordinal)) return "disconnect";
            if (methodName.EndsWith("OnMessage", StringComparison.Ordinal)) return "message";
            return null;
        }

        // ── 피어 ───────────────────────────────────────────────────────────────

        // _lock 을 쥔 채 부른다.
        private static PeerInfo InfoOf(object peer, out bool created)
        {
            created = false;
            foreach (PeerInfo known in _peers)
                if (ReferenceEquals(known.Peer, peer)) return known;
            var info = new PeerInfo { Index = _peers.Count, Peer = peer };
            _peers.Add(info);
            created = true;
            return info;
        }

        private static PeerInfo Register(object peer)
        {
            PeerInfo info;
            bool created;
            lock (_lock) info = InfoOf(peer, out created);
            if (created && Current == Mode.Record)
            {
                object listener = null;
                try { listener = _listenerProperty.GetValue(peer); } catch { }
                if (listener != null) ListenerPatcher?.Invoke(listener.GetType());
            }
            return info;
        }

        private static PeerInfo OfListener(object listener)
        {
            lock (_lock)
            {
                foreach (PeerInfo known in _peers)
                {
                    try { if (ReferenceEquals(_listenerProperty.GetValue(known.Peer), listener)) return known; }
                    catch { }
                }
            }
            return null;
        }

        private static object ProtocolOf(PeerInfo info)
        {
            if (info.Protocol != null) return info.Protocol;
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object protocol = null;
            try
            {
                // 피어가 지금 쓰는 직렬화기(등록된 사용자 타입·바이트 배열 풀이 같다)
                object peerBase = _peerType.GetField("peerBase", any)?.GetValue(info.Peer);
                if (peerBase != null)
                    protocol = Member(peerBase, "SerializationProtocol") ?? Member(peerBase, "serializationProtocol") ?? Member(peerBase, "protocol");
                if (protocol != null && !_protocolType.IsInstanceOfType(protocol)) protocol = null;
            }
            catch { protocol = null; }
            if (protocol == null)
            {
                Type latest = _peerType.Assembly.GetType(_namespace + ".Protocol18", false) ?? throw new MissingMemberException(_namespace + ".Protocol18");
                protocol = Activator.CreateInstance(latest, true);
                try
                {
                    FieldInfo pool = _protocolType.GetField("ByteArraySlicePool", any);
                    object peerPool = _peerType.GetProperty("ByteArraySlicePool", any)?.GetValue(info.Peer);
                    if (pool != null && peerPool != null) pool.SetValue(protocol, peerPool);
                }
                catch { }
            }
            return info.Protocol = protocol;
        }

        private static object Member(object target, string name)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, any | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(target);
                PropertyInfo property = type.GetProperty(name, any | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(true) != null) return property.GetValue(target);
            }
            return null;
        }

        private static bool SetMember(object target, string name, object value)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(name, any | BindingFlags.DeclaredOnly);
                if (field != null) { field.SetValue(target, value); return true; }
                PropertyInfo property = type.GetProperty(name, any | BindingFlags.DeclaredOnly);
                MethodInfo setter = property?.GetSetMethod(true);
                if (setter != null) { setter.Invoke(target, new[] { value }); return true; }
            }
            return false;
        }

        // ── 직렬화 ─────────────────────────────────────────────────────────────

        private static string Serialize(PeerInfo info, MethodInfo method, params object[] tail)
        {
            object stream = _streamNew.Invoke(new object[] { 256 });
            var arguments = new object[tail.Length + 1];
            arguments[0] = stream;
            Array.Copy(tail, 0, arguments, 1, tail.Length);
            method.Invoke(ProtocolOf(info), arguments);
            return Convert.ToBase64String((byte[]) _toArray.Invoke(stream, null));
        }

        // 읽을 때의 선택(바이트 배열 풀·구조체 감싸기)은 피어의 설정을 따른다 — 게임이 받는 값의 타입이 실제 통신 때와 같아야 한다.
        private static object Deserialize(PeerInfo info, MethodInfo method, string data)
        {
            object stream = _streamFrom.Invoke(new object[] { Convert.FromBase64String(data ?? "") });
            ParameterInfo[] parameters = method.GetParameters();
            var arguments = new object[parameters.Length];
            arguments[0] = stream;
            for (int i = 1; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (!type.IsEnum) { arguments[i] = null; continue; }
                int flags = 0;
                if (Flag(info.Peer, "UseByteArraySlicePoolForEvents")) flags |= 1;
                if (Flag(info.Peer, "WrapIncomingStructs")) flags |= 2;
                arguments[i] = Enum.ToObject(type, flags);
            }
            return method.Invoke(ProtocolOf(info), arguments);
        }

        private static bool Flag(object peer, string property)
        {
            try { return _peerType.GetProperty(property)?.GetValue(peer) is bool on && on; }
            catch { return false; }
        }

        private static string HashOf(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return "";
            ulong hash = 14695981039346656037UL;
            foreach (char c in base64) hash = (hash ^ c) * 1099511628211UL;
            return hash.ToString("x16");
        }

        private static int Field(object target, string name)
        {
            object value = target?.GetType().GetField(name)?.GetValue(target);
            return value == null ? 0 : Convert.ToInt32(value);
        }

        // ── 호출(게임 → 피어) ───────────────────────────────────────────────────

        // 호출 prefix. true 면 원래 호출을 건너뛰고 result 를 돌려준다(재생). peer 가 null 이면 피어에 딸리지 않은 호출(지역 고르기).
        public static bool BeforeCall(object peer, string name, object[] args, out bool result)
        {
            result = false;
            switch (Current)
            {
                case Mode.Watch:
                    if (name == "Connect") Live++;
                    return false;
                case Mode.Record:
                    RecordCall(peer, name, args);
                    return false;
                case Mode.Replay:
                    return ReplayCall(peer, name, args, out result);
                default:
                    return false;
            }
        }

        private static void RecordCall(object peer, string name, object[] args)
        {
            List<int> stack = Stack;
            bool forwarded = stack.Count > 0 && stack[stack.Count - 1] == InCall;   // 같은 이름의 다른 꼴이 넘겨 부른 것 — 바깥 것만 적는다
            stack.Add(InCall);
            if (forwarded) { t_calls.Add(null); return; }
            PeerInfo info = peer == null ? null : Register(peer);
            var entry = new Entry { Frame = _frame, Kind = KindCall, Peer = info?.Index ?? Global, Name = name };
            Fill(info, entry, name, args);
            lock (_lock)
            {
                if (info != null) Sample(info);
                _entries?.Add(entry);
            }
            t_calls.Add(entry);
        }

        // 호출의 연산 코드와 보낸 내용의 지문.
        private static void Fill(PeerInfo info, Entry entry, string name, object[] args)
        {
            try
            {
                if (name == "SendOperation" && args != null && args.Length >= 2)
                {
                    entry.Code = Convert.ToInt32(args[0]);
                    entry.Hash = HashOf(Serialize(info, _serRequest, args[0], args[1], false));
                }
                else if (name == "Connect" && args != null && args.Length > 0) entry.Hash = HashOf(args[0] as string);
            }
            catch (Exception e) { Warn("보낸 내용을 읽지 못했다 — " + e.GetBaseException().Message); }
        }

        // 호출 postfix: 결과를 적는다.
        public static void CallReturned(bool result)
        {
            if (Current != Mode.Record || t_calls == null || t_calls.Count == 0) return;
            Entry entry = t_calls[t_calls.Count - 1];
            if (entry != null) entry.Result = result ? 1 : 0;
        }

        // 호출 finalizer: 예외로 나가도 들어간 자리를 걷는다.
        public static void AfterCall(object peer)
        {
            if (Current != Mode.Record || t_stack == null || t_stack.Count == 0) return;
            t_stack.RemoveAt(t_stack.Count - 1);
            if (t_calls.Count > 0) t_calls.RemoveAt(t_calls.Count - 1);
            if (peer == null) return;
            lock (_lock) Sample(InfoOf(peer, out _));
        }

        private static bool ReplayCall(object peer, string name, object[] args, out bool result)
        {
            result = false;
            List<Entry> nested = null;
            PeerInfo info = null;
            lock (_lock)
            {
                int index = peer == null ? Global : (info = InfoOf(peer, out _)).Index;
                if (info != null) ApplyStates(info);
                Entry entry = null;
                int at = -1;
                for (int i = _next; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    if (e.Used || e.Peer != index || e.Kind != KindCall) continue;
                    entry = e;
                    at = i;
                    break;
                }
                int code = name == "SendOperation" && args != null && args.Length > 0 ? Convert.ToInt32(args[0]) : 0;
                if (entry == null || entry.Name != name || entry.Code != code)
                {
                    if (_unexpected++ < MaxNotes)
                        CaptureVerify.RealtimeDiffered(_frame, entry == null
                            ? $"녹화에 없던 실시간 통신 호출 {Call(name, code)} — 실제 서버로는 보내지 않았다"
                            : $"녹화와 다른 실시간 통신 호출: 녹화 {Call(entry.Name, entry.Code)}(프레임 {entry.Frame}), 재생 {Call(name, code)} — 실제 서버로는 보내지 않았다");
                    return true;
                }
                entry.Used = true;
                Calls++;
                result = entry.Result != 0;
                CaptureVerify.RealtimeRequested(Call(name, code), entry.Frame, _frame);
                if (!string.IsNullOrEmpty(entry.Hash))
                {
                    var sent = new Entry();
                    Fill(info, sent, name, args);
                    if (sent.Hash != entry.Hash && _notes.Count < MaxNotes)
                        _notes.Add($"프레임 {_frame} {Call(name, code)}: {SentDiffers}(녹화 프레임 {entry.Frame})");
                }
                // 이 호출 안에서 곧바로 불렸던 콜백(연결되지 않은 채 보내 바로 오류가 온 경우 등)
                for (int i = at + 1; i < _entries.Count; i++)
                {
                    Entry e = _entries[i];
                    if (e.Used || e.Peer != index) continue;
                    if (e.Kind != KindCallback || e.Nested != 1) break;
                    e.Used = true;
                    (nested ??= new List<Entry>()).Add(e);
                }
                if (name == "Connect" && info != null) Prepare(info, args);
                if (info != null) ApplyStates(info);
            }
            if (nested != null)
                foreach (Entry e in nested) Deliver(info, e);
            return true;
        }

        // 접속 호출을 건너뛰면 피어 안쪽이 준비되지 않는다 — 통신 없이 하는 준비만 따라 한다: 전송 방식에 맞는 안쪽 객체, 소켓 종류, 서버 주소.
        // (그 위 층이 접속 로그를 만들며 소켓 종류의 이름을 읽는다 — 비어 있으면 거기서 예외가 나 접속 흐름이 깨진다.)
        private static void Prepare(PeerInfo info, object[] args)
        {
            const BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                _peerType.GetMethod("CreatePeerBase", any, null, Type.EmptyTypes, null)?.Invoke(info.Peer, null);
                info.Protocol = null;
                object protocol = _peerType.GetProperty("TransportProtocol", any)?.GetValue(info.Peer);
                if (protocol != null && Member(info.Peer, "SocketImplementationConfig") is IDictionary sockets && sockets.Contains(protocol))
                    SetMember(info.Peer, "SocketImplementation", sockets[protocol]);
                object peerBase = _peerBaseField.GetValue(info.Peer);
                if (peerBase != null && args != null && args.Length > 0 && args[0] is string address) SetMember(peerBase, "ServerAddress", address);
            }
            catch (Exception e) { Warn("접속 준비를 따라 하지 못했다 — " + e.GetBaseException().Message); }
            SetState(info, info.State);   // 안쪽 객체가 새로 만들어졌으면 상태가 처음 값이다
        }

        // 재생: 피어 상태를 기록대로 — 피어 안쪽 값에 적어, 게임 쪽이 읽는 PeerState 가 녹화 때와 같게 나온다.
        private static void SetState(PeerInfo info, int state)
        {
            info.State = state;
            try
            {
                object peerBase = _peerBaseField.GetValue(info.Peer);
                if (peerBase == null) return;
                // "앱 초기화 중" 은 따로 있는 상태가 아니라 "연결됨 + 초기화 안 끝남" 이다
                int raw = state == _stateInitializing ? _stateConnected : state;
                SetMember(peerBase, "peerConnectionState", Enum.ToObject(_connectionStateType, raw));
                SetMember(peerBase, "ApplicationIsInitialized", state == _stateConnected);
            }
            catch (Exception e) { Warn("피어 상태를 맞추지 못했다 — " + e.GetBaseException().Message); }
        }

        private static string Call(string name, int code) => name == "SendOperation" ? $"SendOperation(연산 {code})" : name;

        private static string Describe(Entry e)
        {
            switch (e.Kind)
            {
                case KindCall: return "호출 " + Call(e.Name, e.Code);
                case KindRegion: return "지역 고르기 결과";
                case KindCallback:
                    return e.Name == "status" ? $"상태 변화({e.Code})" : e.Name == "response" ? $"응답(연산 {e.Code})" : e.Name == "event" ? $"이벤트(코드 {e.Code})" : e.Name;
                default: return "피어 상태";
            }
        }

        // ── 받기(피어 → 리스너) ─────────────────────────────────────────────────

        // DispatchIncomingCommands prefix. true 면 원래 호출을 건너뛴다(재생 — result 는 "넘긴 것이 있다").
        public static bool BeforeDispatch(object peer, out bool result)
        {
            result = false;
            if (Current == Mode.Record)
            {
                Stack.Add(InDispatch);
                PeerInfo info = Register(peer);
                lock (_lock) Sample(info);
                return false;
            }
            if (Current != Mode.Replay) return false;
            if (_delivering || !CaptureClock.OnMainThread) return true;   // 콜백 안에서 다시 받기를 부른 것·다른 스레드 — 넘기지 않는다

            PeerInfo target;
            Entry deliver = null;
            lock (_lock)
            {
                target = InfoOf(peer, out _);
                while (true)
                {
                    while (_next < _entries.Count && _entries[_next].Used) _next++;
                    Entry e = null;
                    for (int i = _next; i < _entries.Count; i++)
                    {
                        Entry candidate = _entries[i];
                        if (candidate.Used || (candidate.Peer != target.Index && candidate.Peer != Global)) continue;
                        e = candidate;
                        break;
                    }
                    if (e == null) break;
                    if (e.Kind == KindState)
                    {
                        if (e.Frame > _frame) break;
                        SetState(target, e.Code);
                        e.Used = true;
                        continue;
                    }
                    if (e.Frame > _frame) break;
                    if (e.Kind == KindCall)
                    {
                        // 게임이 이 호출을 먼저 해야 한다(요청보다 응답이 먼저 가지 않게). 한참 지나도 하지 않으면 건너뛴다.
                        if (_frame - e.Frame <= CallGraceFrames) break;
                        CaptureVerify.RealtimeDiffered(e.Frame, $"녹화에 있던 실시간 통신 호출 {Call(e.Name, e.Code)}(프레임 {e.Frame})이 재생에서는 나가지 않았다");
                        e.Used = true;
                        continue;
                    }
                    if (e.Kind == KindRegion && _regionCallback == null) break;   // 게임이 아직 지역 고르기를 시작하지 않았다
                    e.Used = true;
                    deliver = e;
                    // 이 알림 바로 뒤에 적힌 상태 변화는 알림보다 먼저 일어난 것이다(위 BeforeCallback 의 설명) — 넘기기 전에 맞춰 둔다
                    if (e.Kind == KindCallback) ApplyStates(target);
                    break;
                }
            }
            if (deliver == null) return true;
            _delivering = true;
            try { Deliver(target, deliver); }
            finally { _delivering = false; }
            result = true;
            return true;
        }

        public static void AfterDispatch(object peer)
        {
            if (Current != Mode.Record || t_stack == null || t_stack.Count == 0) return;
            t_stack.RemoveAt(t_stack.Count - 1);
            if (peer == null) return;
            lock (_lock) Sample(InfoOf(peer, out _));   // 알림 없이 받기 안에서 바뀐 상태(연결 중 → 초기화 중 등)
        }

        // 보내기(SendOutgoingCommands·SendAcksOnly) prefix. 재생에서는 보낼 것이 없다.
        public static bool BeforePump(out bool result)
        {
            result = false;
            return Current == Mode.Replay;
        }

        // 연결 유지 스레드: 재생에서는 "오래 못 보냈다" 며 스스로 연결을 끊지 않게 돌리지 않는다.
        public static bool SkipFallback => Current == Mode.Replay;

        // 기록: 피어 상태가 바뀌었으면 적는다. _lock 을 쥔 채 부른다.
        private static void Sample(PeerInfo info)
        {
            if (_entries == null) return;
            int state;
            try { state = _stateOf(info.Peer); }
            catch { return; }
            if (state == info.LastState) return;
            bool first = info.LastState == int.MinValue;
            info.LastState = state;
            if (first && state == 0) return;   // 처음부터 끊긴 상태 — 재생도 0 에서 시작한다
            _entries.Add(new Entry { Frame = _frame, Kind = KindState, Peer = info.Index, Code = state });
        }

        // 재생: 이 피어의 다음 항목들이 상태 변화면 적용한다. _lock 을 쥔 채 부른다.
        private static void ApplyStates(PeerInfo info)
        {
            for (int i = _next; i < _entries.Count; i++)
            {
                Entry e = _entries[i];
                if (e.Used || e.Peer != info.Index) continue;
                if (e.Kind != KindState || e.Frame > _frame) return;
                SetState(info, e.Code);
                e.Used = true;
            }
        }

        // 리스너 콜백 prefix(기록에서만 건다).
        public static void BeforeCallback(object listener, string kind, object[] args)
        {
            if (Current != Mode.Record) return;
            List<int> stack = Stack;
            int top = stack.Count > 0 ? stack[stack.Count - 1] : 0;
            stack.Add(InCallback);
            if (top == InCallback || kind == null || args == null) return;   // 물려받은 쪽이 base 를 부른 것 — 바깥에서 이미 적었다
            PeerInfo info = OfListener(listener);
            if (info == null) { Warn("어느 연결의 콜백인지 알 수 없어 적지 못했다(" + kind + ")"); return; }
            var entry = new Entry { Frame = _frame, Kind = KindCallback, Peer = info.Index, Name = kind, Nested = top == InCall ? 1 : 0 };
            try
            {
                object value = args.Length > 0 ? args[0] : null;
                switch (kind)
                {
                    case "status":
                        entry.Code = Convert.ToInt32(value);
                        break;
                    case "response":
                        entry.Code = Field(value, "OperationCode");
                        entry.Data = Serialize(info, _serResponse, value, false);
                        break;
                    case "event":
                        entry.Code = Field(value, "Code");
                        entry.Keys = Field(value, "SenderKey") << 8 | Field(value, "CustomDataKey");
                        entry.Sender = Convert.ToInt32(_eventType.GetProperty("Sender")?.GetValue(value) ?? -1);
                        entry.Data = Serialize(info, _serEvent, value, false);
                        break;
                    case "disconnect":
                    {
                        // 끊김 알림은 직렬화 메서드가 없다 — 같은 모양(코드·문구·값 묶음)인 응답에 실어 적는다
                        object carrier = Activator.CreateInstance(_responseType);
                        entry.Code = Field(value, "Code");
                        _responseType.GetField("ReturnCode").SetValue(carrier, Convert.ToInt16(entry.Code));
                        _responseType.GetField("DebugMessage").SetValue(carrier, value?.GetType().GetField("DebugMessage")?.GetValue(value));
                        _responseType.GetField("Parameters").SetValue(carrier, value?.GetType().GetField("Parameters")?.GetValue(value));
                        entry.Data = Serialize(info, _serResponse, carrier, false);
                        break;
                    }
                    case "message":
                        entry.Result = args[0] is bool raw && raw ? 1 : 0;
                        if (_serObject != null && args.Length > 1) entry.Data = Convert.ToBase64String((byte[]) _serObject.Invoke(ProtocolOf(info), new[] { args[1] }));
                        break;
                }
            }
            catch (Exception e) { Warn($"받은 내용({kind})을 적지 못했다 — {e.GetBaseException().Message}"); return; }
            lock (_lock)
            {
                // 피어는 받기 안에서 상태를 바꾼 다음에 알린다 — 알림을 받은 쪽이 그 자리에서 읽는 상태가 알림보다 먼저 적혀 있어야 한다
                Sample(info);
                _entries?.Add(entry);
            }
        }

        public static void AfterCallback()
        {
            if (Current != Mode.Record || t_stack == null || t_stack.Count == 0) return;
            t_stack.RemoveAt(t_stack.Count - 1);
        }

        // 재생: 기록한 콜백 하나를 리스너에 넘긴다. 게임 코드가 그 안에서 다시 호출을 하므로 _lock 밖에서 부른다.
        private static void Deliver(PeerInfo info, Entry e)
        {
            try
            {
                if (e.Kind == KindRegion) { DeliverRegion(e); Delivered++; return; }
                object listener = _listenerProperty.GetValue(info.Peer);
                if (listener == null) return;
                switch (e.Name)
                {
                    case "status":
                        _onStatus(listener, Enum.ToObject(_statusEnum, e.Code));
                        break;
                    case "response":
                        _onResponse(listener, Deserialize(info, _desResponse, e.Data));
                        break;
                    case "event":
                    {
                        object data = Deserialize(info, _desEvent, e.Data);
                        if (e.Keys != 0)
                        {
                            _eventType.GetField("SenderKey")?.SetValue(data, (byte) (e.Keys >> 8));
                            _eventType.GetField("CustomDataKey")?.SetValue(data, (byte) (e.Keys & 0xFF));
                        }
                        if (e.Sender != -1 && !SetMember(data, "Sender", e.Sender)) SetMember(data, "sender", e.Sender);
                        _onEvent(listener, data);
                        break;
                    }
                    case "disconnect":
                    {
                        if (_onDisconnect == null) break;
                        object carrier = Deserialize(info, _desResponse, e.Data);
                        object message = Activator.CreateInstance(_disconnectType);
                        _disconnectType.GetField("Code")?.SetValue(message, Convert.ToInt16(e.Code));
                        _disconnectType.GetField("DebugMessage")?.SetValue(message, _responseType.GetField("DebugMessage").GetValue(carrier));
                        _disconnectType.GetField("Parameters")?.SetValue(message, _responseType.GetField("Parameters").GetValue(carrier));
                        _onDisconnect(listener, message);
                        break;
                    }
                    case "message":
                        if (_onMessage != null && _desObject != null && e.Data != null)
                            _onMessage(listener, e.Result == 1, _desObject.Invoke(ProtocolOf(info), new object[] { Convert.FromBase64String(e.Data) }));
                        break;
                }
                Delivered++;
            }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
            {
                // 직렬화기가 던진 것 — 게임 콜백의 예외는 감싸지지 않고 그대로 올라간다(실제 통신 때와 같다)
                CaptureVerify.RealtimeDiffered(e.Frame, $"기록한 {Describe(e)}(프레임 {e.Frame})을 되살리지 못했다 — {wrapped.InnerException.Message}");
            }
        }

        // ── 지역 고르기(핑) ─────────────────────────────────────────────────────

        // PingMinimumOfRegions prefix. skip 이면 원래 호출을 건너뛰고 result 를 돌려준다(재생).
        // 기록에서는 끝났을 때 불릴 메서드에 패치를 걸어 두어(RegionPatcher), 그 메서드가 불리는 순간 결과를 적는다(RegionCompleted).
        public static void BeforeRegionPing(object handler, Delegate callback, out bool skip, out bool result)
        {
            skip = BeforeCall(null, RegionCall, null, out result);
            if (Current == Mode.Record)
            {
                if (callback?.Method != null) RegionPatcher?.Invoke(callback.Method);
                return;
            }
            if (Current != Mode.Replay) return;
            lock (_lock)
            {
                _regionHandler = handler;
                _regionCallback = result ? callback : null;
            }
            if (!result) return;
            try { SetMember(handler, "Aborted", false); SetMember(handler, "IsPinging", true); }
            catch { }
        }

        // 지역 고르기가 끝나 콜백이 불리는 순간(기록). 지역별 핑을 적는다 — 이 뒤에 콜백이 하는 호출(선택된 지역으로 접속)이 이어 적힌다.
        public static void RegionCompleted(object handler)
        {
            if (Current != Mode.Record || handler == null || !(Member(handler, "EnabledRegions") is IEnumerable regions)) return;
            string pings = "";
            try
            {
                var parts = new List<string>();
                foreach (object region in regions) parts.Add(Member(region, "Code") + ":" + Convert.ToInt32(Member(region, "Ping")));
                pings = string.Join(",", parts);
            }
            catch (Exception e) { Warn("지역 고르기 결과를 읽지 못했다 — " + e.GetBaseException().Message); }
            lock (_lock) _entries?.Add(new Entry { Frame = _frame, Kind = KindRegion, Peer = Global, Name = "region", Data = pings });
        }

        private static void DeliverRegion(Entry e)
        {
            object handler;
            Delegate callback;
            lock (_lock)
            {
                handler = _regionHandler;
                callback = _regionCallback;
                _regionHandler = null;
                _regionCallback = null;
            }
            if (handler == null || callback == null) return;
            if (Member(handler, "Aborted") is bool aborted && aborted) return;   // 게임이 그사이 그만뒀다 — 실제로도 콜백이 오지 않는다
            var pings = new Dictionary<string, int>();
            foreach (string part in (e.Data ?? "").Split(','))
            {
                int colon = part.LastIndexOf(':');
                if (colon > 0 && int.TryParse(part.Substring(colon + 1), out int ping)) pings[part.Substring(0, colon)] = ping;
            }
            if (Member(handler, "EnabledRegions") is IEnumerable regions)
                foreach (object region in regions)
                    if (Member(region, "Code") is string code && pings.TryGetValue(code, out int ping)) SetMember(region, "Ping", ping);
            SetMember(handler, "bestRegionCache", null);
            SetMember(handler, "IsPinging", false);
            Invoke(callback, handler);
        }

        // 콜백 안의 예외는 감싸지 않고 올린다.
        private static void Invoke(Delegate callback, object argument)
        {
            try { callback.DynamicInvoke(argument); }
            catch (TargetInvocationException wrapped) when (wrapped.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(wrapped.InnerException).Throw();
            }
        }

        private static void Warn(string message)
        {
            if (_warned) return;
            _warned = true;
            Debug.LogWarning("[LocaleCapture] 실시간 통신: " + message + " — 이 녹화의 실시간 통신은 재생에서 어긋날 수 있다");
            if (_notes.Count < MaxNotes) lock (_lock) _notes.Add(message);
        }
    }
}
