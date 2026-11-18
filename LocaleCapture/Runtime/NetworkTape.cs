using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace CookApps.LocaleCapture
{
    // 서버 응답 테이프. 기록 때는 나간 요청의 응답을 순서·지연(프레임)과 함께 저장하고, 재생 때는 같은 요청에 서버 대신
    // 저장한 응답을 같은 지연으로 돌려준다. 가챠·보상처럼 서버가 정하는 결과도 언어마다 같아지고 서버 상태와 무관해진다.
    //  · HttpClient(HttpMessageInvoker.SendAsync): 응답 객체를 바로 돌려준다
    //  · UnityWebRequest(텍스트 응답만): 요청 주소를 이 프로세스 안의 작은 서버(127.0.0.1)로 바꿔 저장한 응답을 받게 한다
    // 요청 주소·본문에 언어 코드가 든 요청(언어 데이터 등)은 언어마다 달라야 하므로 언어별 테이프에 따로 둔다 —
    // 그 언어로 처음 뽑을 때만 실제 서버로 가고, 다음부터는 언어별 테이프로 재생한다(나중에 다시 뽑아도 서버가 필요 없다).
    // 진입점은 에디터가 Harmony 로 연결한다.
    public static class NetworkTape
    {
        public const string FileName = "network.tape.json";
        public static string LiveFileName(string locale) => $"network.live.{(string.IsNullOrEmpty(locale) ? "unknown" : locale)}.json";

        public enum Mode { Off, Record, Replay }

        [Serializable]
        public struct Header { public string Name, Value; }

        [Serializable]
        public sealed class Entry
        {
            public string Method;
            public string Url;
            public int RequestFrame;
            public int LatencyFrames;
            public int Status;
            public string Reason;
            public List<Header> Headers = new List<Header>();
            public List<Header> ContentHeaders = new List<Header>();
            public string Body;   // base64
            // 요청이 어떻게 끝났나. 실패(연결 오류 등 예외)·취소(타임아웃, 화면을 떠나며 취소)·기록이 끝날 때까지 안 끝남도 그대로 남겨,
            // 재생에서 같은 프레임에 같은 식으로 끝낸다. 이전 기록에는 없다(0 = 응답 받음).
            public int Outcome;
            public const int Completed = 0, Faulted = 1, Canceled = 2, Unfinished = 3;

            // 재생에서만: 녹화에 언어 표기만 다른 같은 요청이 있어 타이밍은 그쪽을 따르고, 내용은 실제 서버에서 받는다.
            [NonSerialized] public bool Fetch;
            [NonSerialized] public Entry Fallback;   // 실제 서버가 안 되면 대신 쓸 녹화 응답

            internal Entry WithTiming(Entry timing) => new Entry
            {
                Method = Method, Url = Url, RequestFrame = timing.RequestFrame, LatencyFrames = timing.LatencyFrames,
                Status = Status, Reason = Reason, Headers = Headers, ContentHeaders = ContentHeaders, Body = Body, Outcome = Outcome,
            };
        }

        // 실제 서버에서 받는 중인 응답. 녹화 때 도착한 프레임(Due)이 됐는데 아직 안 왔으면 세션이 게임 시간을 멈추고 기다린다.
        private sealed class Pending
        {
            public int Due;
            public string Name;
            public Func<bool> Ready;
            public Action Deliver;
            public bool GaveUp;
        }

        private static readonly List<Pending> _pending = new List<Pending>();

        [Serializable]
        private sealed class TapeFile { public List<Entry> Entries = new List<Entry>(); }

        public static Mode Current { get; private set; }
        public static int Replayed { get; private set; }
        public static int Missed { get; private set; }
        public static int Live { get; private set; }
        public static int Fetched { get; private set; }   // 재생: 실제 서버에서 받아 온 응답 수(언어 요청·기록에 없던 요청) — 받는 동안 게임 시간을 멈추고 기다렸다

        private static string _directory, _locale;
        private static int _fps = 30;
        private static string[] _liveMarkers = Array.Empty<string>();
        private static volatile int _frame;
        private static readonly List<Entry> _recorded = new List<Entry>();
        private static readonly List<Entry> _liveRecorded = new List<Entry>();
        private static readonly Dictionary<string, Queue<Entry>> _queues = new Dictionary<string, Queue<Entry>>();
        private static readonly Dictionary<string, Queue<Entry>> _liveQueues = new Dictionary<string, Queue<Entry>>();
        private static readonly Dictionary<string, Queue<Entry>> _recordedLiveQueues = new Dictionary<string, Queue<Entry>>();   // 녹화 언어의 언어별 테이프(타이밍만 쓴다)
        private static string[] _recordedForms = Array.Empty<string>();
        private static readonly Dictionary<UnityWebRequest, int> _tapedDue = new Dictionary<UnityWebRequest, int>();
        // 재생: 테이프로 답할 HttpClient 요청. 녹화 프레임(Due)에 넘긴다.
        private sealed class Waiting
        {
            public int Due;
            public TaskCompletionSource<HttpResponseMessage> Tcs;
            public HttpResponseMessage Response;   // 응답을 받은 요청이면
            public Entry Entry;
            public bool OffMain;                   // 작업 스레드에서 보낸 요청 — 응답도 작업 스레드에서 넘긴다
            public int Frame, Stall;               // 요청한 세션 프레임과 그때까지 따로 돌린 프레임 수(Stall 참고)
        }

        private static readonly List<Waiting> _waiting = new List<Waiting>();
        private static readonly List<TaskCompletionSource<HttpResponseMessage>> _unfinished = new List<TaskCompletionSource<HttpResponseMessage>>();
        // 요청은 작업 스레드에서도 나간다 — 테이프 목록(_recorded·_queues·_waiting 등)은 이 잠금 안에서만 만진다.
        private static readonly object _gate = new object();
        private static readonly ConditionalWeakTable<HttpRequestMessage, object> _handled = new ConditionalWeakTable<HttpRequestMessage, object>();
        private static readonly Dictionary<string, int> _inFlight = new Dictionary<string, int>();   // HttpClient 요청(안쪽 UnityWebRequest 를 이중 기록하지 않게)
        private static readonly HashSet<string> _warned = new HashSet<string>();

        // ── 세션 ───────────────────────────────────────────────────────────────

        internal static void BeginRecord(string directory, string[] liveMarkers)
        {
            Reset(directory, null, liveMarkers);
            Current = Mode.Record;
        }

        // recordedLocale·recordedForms: 녹화 언어와 그 표기. 주소의 언어 표기만 다른 요청을 녹화의 같은 요청으로 맞춘다.
        // deliveredOnUpdate: 기록이 응답을 세션 Update 에서 넘긴 방식인가(ReplayManifest.TraceVersion 2 부터). 그러면 지연 프레임이
        // "게임이 응답을 이어받은 프레임" 이라 재생도 늘 Update 에서 넘긴다. 이전 기록은 작업 스레드 도착 프레임이라 예전처럼 다룬다.
        internal static void BeginReplay(string directory, string locale, string[] liveMarkers, int fps, string recordedLocale, string[] recordedForms, bool deliveredOnUpdate)
        {
            Reset(directory, locale, liveMarkers);
            _deliveredOnUpdate = deliveredOnUpdate;
            _fps = Math.Max(1, fps);
            _recordedForms = recordedForms ?? Array.Empty<string>();
            Load(Path.Combine(directory, FileName), _queues);
            Load(Path.Combine(directory, LiveFileName(locale)), _liveQueues);
            if (!string.IsNullOrEmpty(recordedLocale) && !string.Equals(recordedLocale, locale, StringComparison.OrdinalIgnoreCase))
                Load(Path.Combine(directory, LiveFileName(recordedLocale)), _recordedLiveQueues);
            try { TapeServer.Start(); }   // 첫 요청 때 띄우면 그만큼 늦게 응답한다
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 테이프 서버를 열지 못했다: " + e.Message); }
            Current = Mode.Replay;
        }

        private static void Reset(string directory, string locale, string[] liveMarkers)
        {
            _directory = directory;
            _locale = locale;
            _liveMarkers = liveMarkers ?? Array.Empty<string>();
            _frame = 0;
            _recorded.Clear();
            _liveRecorded.Clear();
            _queues.Clear();
            _liveQueues.Clear();
            _recordedLiveQueues.Clear();
            _recordedForms = Array.Empty<string>();
            _lastByKey.Clear();
            lock (_gate) { _waiting.Clear(); _unfinished.Clear(); }
            lock (_pending) _pending.Clear();
            _tapedDue.Clear();
            _warned.Clear();
            lock (_inFlight) _inFlight.Clear();
            Replayed = Missed = Live = Fetched = 0;
        }

        private static void Load(string path, Dictionary<string, Queue<Entry>> into)
        {
            if (!File.Exists(path)) return;
            TapeFile file = JsonUtility.FromJson<TapeFile>(File.ReadAllText(path));
            foreach (Entry e in file.Entries)
            {
                string key = Key(e.Method, e.Url);
                if (!into.TryGetValue(key, out Queue<Entry> q)) into[key] = q = new Queue<Entry>();
                q.Enqueue(e);
                _lastByKey[key] = e;
            }
        }

        // 요청마다 기록된 마지막 응답. 재생에서 같은 요청이 기록보다 많이 나가면 이걸로 대신 답한다.
        private static readonly Dictionary<string, Entry> _lastByKey = new Dictionary<string, Entry>();

        internal static void Tick(int frame)
        {
            _frame = frame;
            _stallFrames = 0;
        }

        // 응답을 넘기는 시계는 세션 프레임이다 — 재생에서 로딩이 늦어 게임 시간을 멈추고 기다리는 동안에는 같이 멈춘다(그동안 흐르면
        // 걸려 있던 응답이 기다린 프레임만큼 일찍 간다). 다만 그 로딩이 바로 응답을 기다리는 것이면(흐름이 이미 녹화와 달라졌을 때)
        // 서로 기다리다 멈추므로, 대기가 오래 안 풀릴 때만 세션이 Stall 로 이 시계를 따로 돌려 풀어 준다.
        internal static void Stall() => _stallFrames++;

        private static volatile int _stallFrames;
        private static int Now => _frame + _stallFrames;
        private static bool _deliveredOnUpdate;

        // 때가 된 응답을 넘긴다. 세션의 Update 에서 부른다 — 게임 쪽 await 이어받기가 같은 프레임의 정해진 단계
        // (Update 뒤 지연 작업)에서 돌아, 재생마다 같은 프레임에 응답이 도착한다.
        internal static void Release()
        {
            int frame = Now;
            // 같은 프레임에 넘길 응답이 여럿이면 요청한 순서대로 넘긴다 — 기록 때 넘긴 순서(아래 _pending)와 같게.
            // 넘기는 도중 게임이 다음 요청을 보내 목록이 늘 수 있어, 먼저 추려 낸 뒤 넘긴다.
            List<Waiting> due = TakeDue(false);
            if (due != null)
                foreach (Waiting w in due) { Waiting item = w; Complete(() => Settle(item)); }
            List<Pending> ready = null;
            lock (_pending)
                for (int i = 0; i < _pending.Count; i++)
                {
                    Pending p = _pending[i];
                    if ((p.Due > frame && !p.GaveUp) || !IsReady(p)) continue;
                    (ready ??= new List<Pending>()).Add(p);
                    _pending.RemoveAt(i--);
                }
            if (ready != null) foreach (Pending p in ready) if (p.Deliver != null) Complete(p.Deliver);
        }

        // 작업 스레드에서 보낸 요청의 응답. 기록에서는 실제 응답이 작업 스레드에서 끝나 그 이어받기가 작업 스레드에서 돌고, 메인 스레드로
        // 돌아오는 것만 ThreadGate 가 프레임을 맞춘다. 재생에서 메인 스레드가 넘기면 그 이어받기가 메인 스레드에서 돌아 버려(돌아올 일이
        // 없어져) ThreadGate 가 기록된 것을 기다리다 놓친다 — 그래서 스레드 풀에서 넘긴다. 세션 Update 의 맨 앞(ThreadGate 보다 먼저)에서 부른다.
        internal static void ReleaseOffMain()
        {
            List<Waiting> due = TakeDue(true);
            if (due == null) return;
            foreach (Waiting w in due)
            {
                Waiting item = w;
                ThreadPool.QueueUserWorkItem(_ => Settle(item));
            }
        }

        // 때가 된 것을 요청한 순서대로 꺼낸다.
        private static List<Waiting> TakeDue(bool offMain)
        {
            List<Waiting> due = null;
            int frame = _frame, stall = _stallFrames;
            lock (_gate)
                for (int i = 0; i < _waiting.Count; i++)
                {
                    Waiting w = _waiting[i];
                    // 시계를 따로 돌리는 중에 나간 요청은 그 뒤로 돌린 만큼만 앞당긴다
                    int turned = w.Frame == frame ? stall - w.Stall : stall;
                    if (w.OffMain != offMain || w.Due > frame + turned) continue;
                    (due ??= new List<Waiting>()).Add(w);
                    _waiting.RemoveAt(i--);
                }
            return due;
        }

        // 기록 때 끝난 대로 끝낸다: 응답, 예외, 취소.
        private static void Settle(Waiting w)
        {
            try
            {
                int outcome = w.Entry == null ? Entry.Completed : w.Entry.Outcome;
                if (outcome == Entry.Canceled) w.Tcs.TrySetCanceled();
                else if (outcome == Entry.Faulted) w.Tcs.TrySetException(new HttpRequestException(string.IsNullOrEmpty(w.Entry.Reason) ? "기록 때 실패한 요청" : w.Entry.Reason));
                else w.Tcs.TrySetResult(w.Response);
            }
            catch (Exception e) { Debug.LogException(e); }
        }

        // 응답을 넘기는 동안 동기화 컨텍스트를 비운다. Unity 메인 스레드처럼 컨텍스트가 있는 곳에서 작업을 끝내면, .NET 은
        // ConfigureAwait(false) 로 기다리던 이어받기(HttpClient 내부, 통신 라이브러리)를 그 자리에서 돌리지 않고 스레드 풀에 맡긴다 —
        // 거기서 다시 메인 스레드로 돌아오는 프레임이 실행마다 한두 프레임씩 달라진다. 컨텍스트를 비우면 그런 이어받기가 이 자리에서
        // 바로 돌고, 게임 코드처럼 Unity 컨텍스트를 잡아 둔 이어받기는 그 컨텍스트로 넘겨져 같은 프레임의 지연 작업 단계에서 돈다.
        private static void Complete(Action complete)
        {
            SynchronizationContext saved = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try { complete(); }
            catch (Exception e) { Debug.LogException(e); }
            finally { SynchronizationContext.SetSynchronizationContext(saved); }
        }

        private static bool IsReady(Pending p)
        {
            try { return p.Ready(); }
            catch { return true; }   // 요청이 이미 해제됨
        }

        // 녹화 때 이미 도착했어야 하는데 아직 안 온 응답(없으면 null). 세션이 매 프레임 끝에 본다.
        internal static string Overdue()
        {
            int now = Now;
            lock (_pending)
                foreach (Pending p in _pending)
                    if (!p.GaveUp && p.Due <= now && !IsReady(p)) return p.Name;
            return null;
        }

        // 너무 오래 기다렸다 — 지금 늦은 응답은 더 붙잡지 않고 올 때 넘긴다.
        internal static void GiveUpOverdue()
        {
            int now = Now;
            lock (_pending)
                foreach (Pending p in _pending)
                    if (p.Due <= now) p.GaveUp = true;
        }

        private static void AddPending(Pending p)
        {
            lock (_pending) _pending.Add(p);
        }

        // recordedLocale: 기록이면 녹화 언어(언어별 테이프 이름). 재생이면 무시.
        internal static void End(string recordedLocale, bool save)
        {
            try
            {
                if (save && Current == Mode.Record)
                {
                    Save(Path.Combine(_directory, FileName), _recorded);
                    if (_liveRecorded.Count > 0) Save(Path.Combine(_directory, LiveFileName(recordedLocale)), _liveRecorded);
                    Debug.Log($"[LocaleCapture] 서버 응답 {_recorded.Count}개 기록, 언어별 {_liveRecorded.Count}개");
                }
                else if (Current == Mode.Replay)
                {
                    List<Entry> live;
                    // 실패했거나 끝나지 않은 것은 남기지 않는다 — 다음에 이 언어로 뽑을 때 실제 서버에서 다시 받게.
                    lock (_liveRecorded) live = _liveRecorded.FindAll(e => e.Outcome == Entry.Completed);
                    if (save && live.Count > 0) Append(Path.Combine(_directory, LiveFileName(_locale)), live);
                    foreach (KeyValuePair<string, Queue<Entry>> pair in _queues)
                    {
                        if (pair.Value.Count == 0) continue;
                        // 기록이 끝나던 마지막 프레임 즈음에 나간 요청은 재생이 그 프레임을 다시 돌지 않아 안 나갈 수 있다 — 그건 차이로 치지 않는다.
                        int requested = pair.Value.Peek().RequestFrame;
                        string unused = $"녹화에서 나간 요청 {pair.Value.Count}개가 재생에서는 나가지 않았다: {Short(pair.Key)}";
                        if (save && requested < _frame - 1) CaptureVerify.ServerDiffered(requested, unused);
                        else CaptureVerify.Add(requested, "서버", unused);
                    }
                    Debug.Log($"[LocaleCapture] 서버 응답 재생 {Replayed}개, 실서버 {Live}개, 테이프에 없음 {Missed}개");
                }
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 테이프 저장 실패: " + e.Message); }
            List<Waiting> rest;
            List<TaskCompletionSource<HttpResponseMessage>> unfinished;
            lock (_gate)
            {
                rest = new List<Waiting>(_waiting);
                _waiting.Clear();
                unfinished = new List<TaskCompletionSource<HttpResponseMessage>>(_unfinished);
                _unfinished.Clear();
            }
            foreach (Waiting w in rest) Settle(w);
            foreach (TaskCompletionSource<HttpResponseMessage> tcs in unfinished) tcs.TrySetCanceled();   // 기록에서 끝나지 않은 요청 — 게임이 멈춰 있지 않게 취소로 푼다
            // 받는 중인 응답은 기다리지 않고, 온 것만 넘긴다(게임이 멈춰 있지 않게).
            List<Pending> pending;
            lock (_pending) { pending = new List<Pending>(_pending); _pending.Clear(); }
            foreach (Pending p in pending) if (IsReady(p)) p.Deliver?.Invoke();
            _tapedDue.Clear();
            TapeServer.Stop();
            Current = Mode.Off;
        }

        private static void Save(string path, List<Entry> entries)
        {
            var file = new TapeFile();
            file.Entries.AddRange(entries);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(file));
        }

        private static void Append(string path, List<Entry> entries)
        {
            var file = File.Exists(path) ? JsonUtility.FromJson<TapeFile>(File.ReadAllText(path)) : new TapeFile();
            file.Entries.AddRange(entries);
            File.WriteAllText(path, JsonUtility.ToJson(file));
        }

        // ── 공통 ───────────────────────────────────────────────────────────────

        // 재생에서 꺼낼 응답. 없으면 null(실제 서버로). live 요청이면 언어별 테이프에서.
        private static Entry Take(string method, string url, bool live)
        {
            string key = Key(method, url);
            Dictionary<string, Queue<Entry>> queues = live ? _liveQueues : _queues;
            if (queues.TryGetValue(key, out Queue<Entry> q) && q.Count > 0)
            {
                Entry e = q.Dequeue();
                if (!live) CaptureVerify.Requested(Short(key), e.RequestFrame, _frame);
                Replayed++;
                // 언어별 테이프의 시점은 처음 실제 서버로 받았을 때라 들쭉날쭉하다 — 녹화에 같은 요청이 있으면 그 시점을 따른다.
                Entry timing = live ? TakeSibling(method, url) : null;
                return timing != null ? e.WithTiming(timing) : e;
            }
            // 주소의 언어 표기만 다른 녹화 요청(예: locale=ko → locale=en): 녹화 때와 같은 프레임에 응답이 오게 한다.
            // 그 언어로 받아 둔 응답이 있으면 그걸, 없으면 실제 서버에서 받아 언어별 테이프에 남긴다.
            Entry sibling = TakeSibling(method, url);
            if (sibling != null)
            {
                CaptureVerify.Requested(Short(key), sibling.RequestFrame, _frame);
                if (_warned.Add("~" + key)) Debug.Log($"[LocaleCapture] 언어만 다른 녹화 요청과 같은 프레임에 응답한다: {Short(key)} ≈ {Short(Key(sibling.Method, sibling.Url))}");
                if (_liveQueues.TryGetValue(key, out Queue<Entry> stored) && stored.Count > 0) { Replayed++; return stored.Dequeue().WithTiming(sibling); }
                Live++;
                return new Entry { Method = method, Url = url, RequestFrame = sibling.RequestFrame, LatencyFrames = sibling.LatencyFrames, Fetch = true, Fallback = sibling };
            }
            // 언어 요청인데 이 언어로는 처음이고 녹화에 맞출 요청도 없다 — 실제 서버에서 받고(받는 동안 게임 시간을 멈춘다) 언어별 테이프에 남긴다.
            if (live) { Live++; return new Entry { Method = method, Url = url, RequestFrame = _frame, LatencyFrames = 0, Fetch = true }; }
            Missed++;
            // 같은 요청이 기록보다 한 번 더 나간 것이면 실제 서버로 보내지 않는다 — 연결 대기 화면이 끼고, 실제 계정이 바뀔 수 있다.
            if (_lastByKey.TryGetValue(key, out Entry last))
            {
                if (_warned.Add(key)) Debug.LogWarning($"[LocaleCapture] 기록보다 많이 나간 서버 요청이라 마지막 기록 응답으로 대신한다: {key}");
                CaptureVerify.ServerDiffered(_frame, "기록보다 많이 나간 요청(마지막 기록 응답으로 대신): " + Short(key));
                return last;
            }
            // 기록에 없는 요청(예: 녹화 땐 저장된 캐시를 써서 안 나갔는데, 다른 언어라 캐시 주소가 달라 나간 요청).
            // 녹화에선 기다림 없이 지나간 자리라, 응답이 올 때까지 게임 시간을 멈춰 그 뒤 흐름이 밀리지 않게 한다.
            // 이 언어로 받아 둔 응답이 있으면 서버 없이 그걸 쓰고, 없으면 실제 서버에서 받아 언어별 테이프에 남긴다.
            CaptureVerify.Add(_frame, "서버", "기록에 없는 요청(응답까지 게임 시간을 멈춤): " + Short(key));
            var now = new Entry { RequestFrame = _frame, LatencyFrames = 0 };
            if (_liveQueues.TryGetValue(key, out Queue<Entry> kept) && kept.Count > 0) return kept.Dequeue().WithTiming(now);
            if (_warned.Add(key)) Debug.LogWarning($"[LocaleCapture] 기록에 없는 서버 요청이라 실제 서버에서 받아 온다(받는 동안 게임 시간을 멈춘다): {key}");
            return new Entry { Method = method, Url = url, RequestFrame = _frame, LatencyFrames = 0, Fetch = true };
        }

        // 녹화(공통 테이프·녹화 언어의 언어별 테이프)에서 주소가 언어 표기만 다른 같은 요청을 꺼낸다. 없으면 null.
        private static Entry TakeSibling(string method, string url)
        {
            if (_recordedForms.Length == 0 || string.IsNullOrEmpty(url)) return null;
            List<string> tokens = Tokens(url);
            foreach (Dictionary<string, Queue<Entry>> source in new[] { _queues, _recordedLiveQueues })
                foreach (KeyValuePair<string, Queue<Entry>> pair in source)
                    if (pair.Value.Count > 0 && SameExceptLocale(pair.Value.Peek(), method, tokens))
                        return pair.Value.Dequeue();
            return null;
        }

        private static readonly System.Text.RegularExpressions.Regex TokenPattern =
            new System.Text.RegularExpressions.Regex(@"[A-Za-z0-9\-_.%]+|[^A-Za-z0-9\-_.%]+");
        private static readonly System.Text.RegularExpressions.Regex LocaleToken =
            new System.Text.RegularExpressions.Regex(@"^[A-Za-z]{2,20}([-_][A-Za-z0-9]{2,8})*$");

        private static List<string> Tokens(string url)
        {
            var list = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in TokenPattern.Matches(url)) list.Add(m.Value);
            return list;
        }

        // 같은 메서드·같은 모양의 주소이고, 다른 칸은 모두 녹화 쪽이 녹화 언어 표기·재생 쪽이 언어 표기 모양인 곳뿐인가.
        private static bool SameExceptLocale(Entry recorded, string method, List<string> tokens)
        {
            if (!string.Equals(recorded.Method, method, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(recorded.Url)) return false;
            List<string> other = Tokens(recorded.Url);
            if (other.Count != tokens.Count) return false;
            bool differs = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] == other[i]) continue;
                if (!IsRecordedForm(other[i]) || !LocaleToken.IsMatch(tokens[i])) return false;
                differs = true;
            }
            return differs;
        }

        private static bool IsRecordedForm(string token)
        {
            foreach (string form in _recordedForms)
                if (string.Equals(form, token, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // 응답이 도착할 프레임. 요청이 기록보다 일찍 나갔으면 기록 때 도착한 프레임까지 붙잡는다 —
        // 부팅 중 실시간 대기 등으로 요청 시점이 앞당겨져도 응답 뒤 화면은 기록과 같은 프레임에 이어진다.
        // 기준은 세션 프레임이다(Now 가 아니라) — 로딩 대기가 오래 안 풀려 시계를 따로 돌리는 중에 나간 요청도 그만큼 늦어지지 않는다.
        private static int DueFrame(Entry e) => Math.Max(_frame, e.RequestFrame) + e.LatencyFrames;

        private static Entry NewEntry(string method, string url, bool live)
        {
            var entry = new Entry { Method = method, Url = url, RequestFrame = _frame, Outcome = Entry.Unfinished };   // 끝나면 결과를 적는다
            if (Current == Mode.Record && !live) lock (_gate) _recorded.Add(entry);
            else lock (_liveRecorded) _liveRecorded.Add(entry);
            return entry;
        }

        // 기록 대상인가: 기록 중이면 전부, 재생 중이면 언어별 테이프에 없어 실제 서버로 간 언어 요청만.
        private static bool ShouldRecord(bool live, bool replayedFromTape) =>
            Current == Mode.Record || (Current == Mode.Replay && live && !replayedFromTape);

        private static string Key(string method, string url) => method + " " + url;

        // 진행 중인 HttpClient 요청과 그 안쪽 UnityWebRequest 를 맞춰 보는 키. 같은 주소라도 두 쪽의 표기가 다르다
        // (Uri.ToString() 은 %3A 를 ':' 로 풀고 UnityWebRequest.url 은 그대로 둔다) — 이스케이프를 모두 풀어서 비교한다.
        private static string FlightKey(string method, string url)
        {
            if (string.IsNullOrEmpty(url)) return method + " ";
            string plain = url;
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) plain = uri.AbsoluteUri;
                plain = Uri.UnescapeDataString(plain);
            }
            catch { /* 풀 수 없는 주소 — 그대로 비교 */ }
            return method + " " + plain;
        }

        private static string Short(string key) => key.Length > 120 ? key.Substring(0, 120) + "…" : key;

        private static bool IsLive(string url, string body)
        {
            foreach (string marker in _liveMarkers)
            {
                if (string.IsNullOrEmpty(marker)) continue;
                if ((url != null && url.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) || (body != null && body.IndexOf(marker, StringComparison.Ordinal) >= 0))
                    return true;
            }
            return false;
        }

        // ── HttpClient ─────────────────────────────────────────────────────────

        private static readonly ConditionalWeakTable<HttpRequestMessage, Entry> _fromTape = new ConditionalWeakTable<HttpRequestMessage, Entry>();

        // 요청 하나에 SendAsync 가 겹쳐 불린다(HttpClient.SendAsync 안에서 HttpMessageInvoker.SendAsync). 게임이 실제로 기다리는 것은
        // 가장 바깥 호출이 돌려준 작업이라, 응답을 Update 에서 넘기는 일은 가장 바깥 postfix 에서 한다 — 안쪽에서 넘기면 바깥 호출이
        // 다시 스레드 풀을 거쳐 끝난다.
        private sealed class Call
        {
            public int Depth;
            public Entry Entry;       // 이 요청을 기록 중인 항목(없으면 null)
            public Entry Timing;      // 재생: 실제 서버로 보냈고, 이 녹화 요청의 프레임에 맞춰 넘긴다
            public bool MainThread;   // 메인 스레드에서 보낸 요청인가
        }

        private static readonly ConditionalWeakTable<HttpRequestMessage, Call> _calls = new ConditionalWeakTable<HttpRequestMessage, Call>();

        // Harmony prefix: 테이프에 있으면 서버 대신 응답(true).
        // 서버로 보낼 땐 여기서 진행 중 표시를 건다 — 안쪽 UnityWebRequest 는 원본 호출 안에서 바로 나가서 postfix 로는 늦다.
        public static bool TryReplay(HttpRequestMessage request, out Task<HttpResponseMessage> result)
        {
            result = null;
            if (Current == Mode.Off || request == null) return false;
            Call call = _calls.GetOrCreateValue(request);
            if (call.Depth++ > 0) return false;   // 겹친 안쪽 호출 — 바깥 prefix 가 이미 정했다
            call.MainThread = CaptureClock.OnMainThread;
            string url = request.RequestUri?.ToString();
            Entry e = null;
            if (Current == Mode.Replay)
            {
                bool live = IsLive(url, BodyOf(request));
                lock (_gate) e = Take(request.Method.Method, url, live);
            }
            if (e != null && e.Fetch) { call.Timing = e; Fetched++; }   // 실제 서버로 보내고, postfix 가 녹화 프레임에 맞춰 넘긴다
            if (e == null || e.Fetch)
            {
                string key = FlightKey(request.Method.Method, url);
                lock (_inFlight) _inFlight[key] = _inFlight.TryGetValue(key, out int n) ? n + 1 : 1;
                return false;
            }
            _fromTape.Add(request, e);
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            result = tcs.Task;
            if (e.Outcome == Entry.Unfinished)
            {
                // 기록이 끝날 때까지 응답이 안 온 요청 — 재생에서도 끝나지 않는다(세션이 끝날 때 취소로 푼다).
                lock (_gate) _unfinished.Add(tcs);
                return true;
            }
            HttpResponseMessage response = e.Outcome == Entry.Completed ? ToResponse(e, request) : null;
            int due = DueFrame(e);
            // 기록이 Update 에서 넘긴 방식이면 지연 0 인 응답도 요청 호출 안에서 바로 주지 않고 다음 Update 에서 넘긴다(기록과 같은 자리).
            if (response != null && call.MainThread && due <= Now && !_deliveredOnUpdate) { result = Task.FromResult(response); return true; }
            lock (_gate) _waiting.Add(new Waiting { Due = due, Tcs = tcs, Response = response, Entry = e, OffMain = !call.MainThread, Frame = _frame, Stall = _stallFrames });
            return true;
        }

        // Harmony postfix: 기록 대상이면 응답을 받아 저장한다(가장 안쪽 호출에서). 가장 바깥 호출에서는 게임에 넘길 시점을 맞춘다.
        public static Task<HttpResponseMessage> Record(HttpRequestMessage request, Task<HttpResponseMessage> original)
        {
            if (Current == Mode.Off || request == null || original == null) return original;
            if (!_calls.TryGetValue(request, out Call call)) return original;   // prefix 를 안 탄 호출(세션이 켜지기 전에 시작)
            bool outermost = --call.Depth <= 0;
            if (_fromTape.TryGetValue(request, out _)) return original;
            Task<HttpResponseMessage> task = original;
            if (!_handled.TryGetValue(request, out _))
            {
                _handled.Add(request, null);
                string url = request.RequestUri?.ToString();
                string key = FlightKey(request.Method.Method, url);
                original.ContinueWith(_ =>
                {
                    lock (_inFlight)
                        if (_inFlight.TryGetValue(key, out int n)) { if (n <= 1) _inFlight.Remove(key); else _inFlight[key] = n - 1; }
                }, TaskContinuationOptions.ExecuteSynchronously);
                if (call.Timing != null)
                {
                    call.Entry = NewEntry(request.Method.Method, url, true);
                    task = RecordAsync(call.Entry, original);
                }
                else
                {
                    bool live = IsLive(url, BodyOf(request));
                    if (Current == Mode.Record && live) Live++;
                    if (ShouldRecord(live, false))
                    {
                        call.Entry = NewEntry(request.Method.Method, url, live);
                        task = RecordAsync(call.Entry, original);
                    }
                }
            }
            if (!outermost || call.Entry == null) return task;
            if (call.Timing != null) return call.MainThread ? DeliverAt(task, call.Timing) : task;
            // 다른 스레드에서 보낸 요청은 그대로 둔다 — 그 스레드가 응답을 막고 기다리면 메인 스레드 Update 를 못 기다린다.
            return Current == Mode.Record && call.MainThread ? DeliverRecorded(call.Entry, task) : task;
        }

        // 기록 때도 응답을 세션 Update 에서 넘긴다 — 재생이 테이프 응답을 넘기는 자리와 같다.
        // 실제 응답은 작업 스레드에서 끝나고, 게임 쪽 이어받기는 메인 스레드로 넘어오느라 그 프레임 뒤쪽이나 한두 프레임 뒤에 돈다(실행마다 다르다).
        // 재생은 메인 스레드에서 바로 이어받으므로 그대로 두면 응답 뒤 흐름이 녹화보다 한두 프레임 일찍 간다.
        // 지연 프레임도 여기서 다시 적는다 — "작업 스레드에 도착한 프레임" 이 아니라 "게임이 이어받은 프레임" 이 재생이 맞출 값이다.
        private static Task<HttpResponseMessage> DeliverRecorded(Entry entry, Task<HttpResponseMessage> fetch)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            AddPending(new Pending
            {
                Due = 0, Name = Short(Key(entry.Method, entry.Url)), Ready = () => fetch.IsCompleted,
                Deliver = () =>
                {
                    entry.LatencyFrames = Math.Max(0, Now - entry.RequestFrame);
                    if (fetch.IsFaulted) tcs.TrySetException(fetch.Exception.InnerExceptions);
                    else if (fetch.IsCanceled) tcs.TrySetCanceled();
                    else tcs.TrySetResult(fetch.Result);
                },
            });
            return tcs.Task;
        }

        // 실제 서버 응답을 녹화 때 도착한 프레임에 넘긴다(일찍 오면 붙잡고, 늦으면 세션이 게임 시간을 멈추고 기다린다).
        private static Task<HttpResponseMessage> DeliverAt(Task<HttpResponseMessage> fetch, Entry timing)
        {
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            AddPending(new Pending
            {
                Due = DueFrame(timing), Name = Short(Key(timing.Method, timing.Url)), Ready = () => fetch.IsCompleted,
                Deliver = () =>
                {
                    if (fetch.IsFaulted) tcs.TrySetException(fetch.Exception.InnerExceptions);
                    else if (fetch.IsCanceled) tcs.TrySetCanceled();
                    else tcs.TrySetResult(fetch.Result);
                },
            });
            return tcs.Task;
        }

        private static async Task<HttpResponseMessage> RecordAsync(Entry entry, Task<HttpResponseMessage> original)
        {
            // 기다림은 모두 ConfigureAwait(false) — 메인 스레드에서 불려도 응답 처리(본문 읽기)는 스레드 풀에서 끝낸다.
            HttpResponseMessage response;
            try { response = await original.ConfigureAwait(false); }
            catch (OperationCanceledException) { Fail(entry, Entry.Canceled, null); throw; }
            catch (Exception e) { Fail(entry, Entry.Faulted, e); throw; }
            try
            {
                entry.Outcome = Entry.Completed;
                entry.LatencyFrames = Math.Max(0, _frame - entry.RequestFrame);
                entry.Status = (int) response.StatusCode;
                entry.Reason = response.ReasonPhrase;
                foreach (KeyValuePair<string, IEnumerable<string>> h in response.Headers)
                    foreach (string v in h.Value) entry.Headers.Add(new Header { Name = h.Key, Value = v });
                if (response.Content != null)
                {
                    await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    entry.Body = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                    foreach (KeyValuePair<string, IEnumerable<string>> h in response.Content.Headers)
                        foreach (string v in h.Value) entry.ContentHeaders.Add(new Header { Name = h.Key, Value = v });
                }
            }
            catch (Exception e) { Debug.LogWarning($"[LocaleCapture] 응답 기록 실패({entry.Url}): {e.Message}"); }
            return response;
        }

        // 응답 없이 끝난 요청(예외·취소)을 그대로 남긴다. 끝난 프레임은 게임에 넘길 때(DeliverRecorded) 다시 적는다.
        private static void Fail(Entry entry, int outcome, Exception e)
        {
            entry.LatencyFrames = Math.Max(0, _frame - entry.RequestFrame);
            entry.Reason = e == null ? null : e.GetType().Name + ": " + e.Message;
            entry.Outcome = outcome;
        }

        private static string BodyOf(HttpRequestMessage request)
        {
            if (!(request.Content is ByteArrayContent content)) return null;
            try { return Encoding.UTF8.GetString(content.ReadAsByteArrayAsync().Result); }
            catch { return null; }
        }

        private static HttpResponseMessage ToResponse(Entry e, HttpRequestMessage request)
        {
            var response = new HttpResponseMessage((HttpStatusCode) e.Status) { ReasonPhrase = e.Reason, RequestMessage = request };
            response.Content = new ByteArrayContent(string.IsNullOrEmpty(e.Body) ? Array.Empty<byte>() : Convert.FromBase64String(e.Body));
            foreach (Header h in e.ContentHeaders) response.Content.Headers.TryAddWithoutValidation(h.Name, h.Value);
            foreach (Header h in e.Headers) response.Headers.TryAddWithoutValidation(h.Name, h.Value);
            return response;
        }

        // ── UnityWebRequest ────────────────────────────────────────────────────

        private static bool Tapeable(UnityWebRequest request)
        {
            if (request == null || request.downloadHandler == null || request.downloadHandler.GetType() != typeof(DownloadHandlerBuffer)) return false;
            string url = request.url ?? "";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith(TapeServer.Prefix, StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith(TapeServer.RefusePrefix, StringComparison.OrdinalIgnoreCase)) return false;
            lock (_inFlight) return !_inFlight.ContainsKey(FlightKey(request.method, url));
        }

        // Harmony prefix(SendWebRequest): 재생 중 테이프에 있으면 주소를 테이프 서버로 바꾼다.
        public static void BeforeWebRequest(UnityWebRequest request)
        {
            if (Current != Mode.Replay || !Tapeable(request)) return;
            bool live = IsLive(request.url, null);
            Entry e;
            lock (_gate) e = Take(request.method, request.url, live);
            if (e == null) { if (live) _pendingLive.Add(request); return; }
            if (e.Outcome == Entry.Unfinished)
            {
                // 기록이 끝날 때까지 안 끝난 요청 — 테이프 서버가 세션이 끝날 때까지 답하지 않는다(끝나기를 기다리지도 않는다).
                request.url = TapeServer.Refuse(e, int.MaxValue);
                return;
            }
            int due = DueFrame(e);
            if (e.Fetch)
            {
                Fetched++;
                // 테이프 서버가 실제 서버에서 받아 와 녹화 프레임에 내준다. 그때까지 못 받으면 세션이 기다린다.
                TapeServer.Slot slot = TapeServer.Fetch(e, RequestHeaders(request), request.uploadHandler?.data, due - 2);
                request.url = slot.Url;
                AddPending(new Pending { Due = due - 2, Name = Short(Key(e.Method, e.Url)), Ready = () => slot.Fetched });
            }
            // 보내고 완료 이벤트까지 두 프레임쯤 걸린다. 기록 때 응답 없이 끝난 요청(연결 실패·중단)은 응답 없이 연결을 끊는 쪽으로 보낸다.
            else request.url = e.Outcome == Entry.Faulted ? TapeServer.Refuse(e, due - 2) : TapeServer.Serve(e, due - 2);
            // 이 프레임이 지나도 안 끝났으면 기다린다. 녹화에서 기다림 없이 지나간 요청(지연 0)은 바로, 아니면 한 프레임 여유를 둔다.
            _tapedDue[request] = e.LatencyFrames > 0 ? due + 1 : due;
        }

        // 실제 서버로 다시 보낼 때 옮길 요청 헤더(UnityWebRequest 는 헤더 목록을 주지 않아 흔한 이름만 본다).
        private static readonly string[] ForwardHeaders =
            { "Accept", "Accept-Language", "Authorization", "Content-Type", "User-Agent", "If-None-Match", "If-Modified-Since", "Cookie", "X-Requested-With" };

        private static List<Header> RequestHeaders(UnityWebRequest request)
        {
            var list = new List<Header>();
            foreach (string name in ForwardHeaders)
            {
                string value = request.GetRequestHeader(name);
                if (!string.IsNullOrEmpty(value)) list.Add(new Header { Name = name, Value = value });
            }
            return list;
        }

        private static readonly HashSet<UnityWebRequest> _pendingLive = new HashSet<UnityWebRequest>();

        // Harmony postfix(SendWebRequest): 기록 대상이면 끝났을 때 응답을 저장한다.
        public static void AfterWebRequest(UnityWebRequest request, UnityWebRequestAsyncOperation operation)
        {
            if (Current == Mode.Off || request == null || operation == null) return;
            // 테이프로 답하는 요청: 녹화 때 끝난 프레임이 지나도 안 끝났으면(재생이 빨라 로컬 왕복이 여러 프레임에 걸치면) 세션이 기다린다.
            if (_tapedDue.TryGetValue(request, out int tapedDue))
            {
                _tapedDue.Remove(request);
                AddPending(new Pending { Due = tapedDue, Name = Short(Key(request.method, request.url)), Ready = () => operation.isDone });
            }
            bool wasLive = _pendingLive.Remove(request);
            if (Current == Mode.Replay && !wasLive) return;
            if (Current == Mode.Record && !Tapeable(request)) return;
            bool live = IsLive(request.url, null);
            if (!ShouldRecord(live, false)) return;
            Entry entry = NewEntry(request.method, request.url, live);
            operation.completed += _ =>
            {
                try
                {
                    entry.LatencyFrames = Math.Max(0, _frame - entry.RequestFrame);
                    entry.Status = (int) request.responseCode;
                    // 응답 없이 끝남(연결 실패·중단): 재생에서 테이프 서버가 답하지 않고 연결을 끊어 같은 식으로 끝나게 한다
                    bool failed = request.responseCode == 0 && !string.IsNullOrEmpty(request.error);
                    entry.Outcome = failed ? Entry.Faulted : Entry.Completed;
                    if (failed) entry.Reason = request.error;
                    Dictionary<string, string> headers = request.GetResponseHeaders();
                    if (headers != null)
                        foreach (KeyValuePair<string, string> h in headers) entry.Headers.Add(new Header { Name = h.Key, Value = h.Value });
                    byte[] data = request.downloadHandler?.data;
                    entry.Body = data == null ? "" : Convert.ToBase64String(data);
                }
                catch (Exception e) { Debug.LogWarning($"[LocaleCapture] 응답 기록 실패({entry.Url}): {e.Message}"); }
            };
        }

        // 저장한 응답을 내주는 작은 HTTP 서버(127.0.0.1). UnityWebRequest 재생용.
        // 언어 표기만 다른 요청(Fetch)은 실제 서버에서 받아 와 녹화 프레임에 내준다.
        private static class TapeServer
        {
            public sealed class Slot
            {
                public string Url;
                public Entry Entry;
                public int Due;
                public volatile bool Fetched = true;
            }

            public static string Prefix => $"http://127.0.0.1:{_port}/lc/";
            private static HttpListener _listener;
            private static int _port;
            private static int _next;
            private static readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();

            // dueFrame: 이 프레임이 되면 응답을 보낸다(완료 이벤트가 기록 때 응답이 온 프레임에 오도록).
            public static string Serve(Entry entry, int dueFrame) => Add(new Slot { Entry = entry, Due = dueFrame }).Url;

            // 실제 서버에서 바로 받기 시작한다(게임 요청이 이 서버에 닿기 전부터). 못 받으면 녹화 응답(Fallback)으로 답한다.
            public static Slot Fetch(Entry timing, List<Header> headers, byte[] body, int dueFrame)
            {
                var entry = new Entry { Method = timing.Method, Url = timing.Url, RequestFrame = _frame };
                Slot slot = Add(new Slot { Entry = entry, Due = dueFrame, Fetched = false });
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Download(entry, headers, body);
                        lock (_liveRecorded) _liveRecorded.Add(entry);   // 다음에 이 언어로 다시 뽑을 땐 서버 없이
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[LocaleCapture] 실제 서버에서 받지 못해 녹화 응답으로 대신한다({Short(Key(entry.Method, entry.Url))}): {e.Message}");
                        Entry f = timing.Fallback;
                        if (f != null) { entry.Status = f.Status; entry.Reason = f.Reason; entry.Headers = f.Headers; entry.ContentHeaders = f.ContentHeaders; entry.Body = f.Body; }
                        else entry.Status = 504;
                    }
                    finally { slot.Fetched = true; }
                });
                return slot;
            }

            private static Slot Add(Slot slot)
            {
                if (_listener == null) Start();
                int id;
                lock (_slots) { id = ++_next; _slots[id] = slot; }
                slot.Url = Prefix + id;
                return slot;
            }

            // 패치하지 않은 길(HttpWebRequest)로 받는다 — HttpClient 는 테이프 패치를 다시 탄다.
            private static void Download(Entry entry, List<Header> headers, byte[] body)
            {
                var web = (HttpWebRequest) WebRequest.Create(entry.Url);
                web.Method = entry.Method;
                web.Timeout = 20000;
                web.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                foreach (Header h in headers)
                {
                    try
                    {
                        switch (h.Name.ToLowerInvariant())
                        {
                            case "accept": web.Accept = h.Value; break;
                            case "user-agent": web.UserAgent = h.Value; break;
                            case "content-type": web.ContentType = h.Value; break;
                            case "if-modified-since": if (DateTime.TryParse(h.Value, out DateTime since)) web.IfModifiedSince = since; break;
                            default: web.Headers[h.Name] = h.Value; break;
                        }
                    }
                    catch { /* 제한 헤더 */ }
                }
                if (body != null && body.Length > 0 && !string.Equals(entry.Method, "GET", StringComparison.OrdinalIgnoreCase))
                    using (Stream stream = web.GetRequestStream()) stream.Write(body, 0, body.Length);
                HttpWebResponse response;
                try { response = (HttpWebResponse) web.GetResponse(); }
                catch (WebException e) when (e.Response is HttpWebResponse error) { response = error; }   // 304·4xx 도 응답 그대로
                using (response)
                {
                    entry.Status = (int) response.StatusCode;
                    entry.Reason = response.StatusDescription;
                    foreach (string name in response.Headers.AllKeys)
                        entry.Headers.Add(new Header { Name = name, Value = response.Headers[name] });
                    using (var buffer = new MemoryStream())
                    {
                        response.GetResponseStream()?.CopyTo(buffer);
                        entry.Body = Convert.ToBase64String(buffer.ToArray());
                    }
                }
            }

            public static void Start()
            {
                if (_refuser == null) StartRefuser();
                if (_listener != null) return;
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                _port = ((IPEndPoint) probe.LocalEndpoint).Port;
                probe.Stop();
                _listener = new HttpListener();
                _listener.Prefixes.Add(Prefix);
                _listener.Start();
                var thread = new Thread(Loop) { IsBackground = true, Name = "LocaleCapture.TapeServer" };
                thread.Start(_listener);
            }

            private static void Loop(object state)
            {
                var listener = (HttpListener) state;
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = listener.GetContext(); }
                    catch { return; }
                    ThreadPool.QueueUserWorkItem(_ => Respond(context));
                }
            }

            // 기록 때 응답이 온 프레임(세션 프레임)까지 기다렸다 보낸다 — 재생 속도와 상관없이 같은 프레임에 도착한다.
            private static void Respond(HttpListenerContext context)
            {
                    try
                    {
                        string idText = context.Request.Url.AbsolutePath.Substring("/lc/".Length);
                        Slot slot;
                        bool found;
                        lock (_slots) found = _slots.TryGetValue(int.TryParse(idText, out int id) ? id : -1, out slot);
                        if (!found) { context.Response.StatusCode = 404; context.Response.Close(); return; }
                        var waited = System.Diagnostics.Stopwatch.StartNew();
                        while ((Now < slot.Due || !slot.Fetched) && Current == Mode.Replay && waited.ElapsedMilliseconds < 30000) Thread.Sleep(1);
                        Entry e = slot.Entry;
                        context.Response.StatusCode = e.Status <= 0 ? 200 : e.Status;
                        foreach (Header h in e.Headers)
                        {
                            string n = h.Name.ToLowerInvariant();
                            if (n == "content-length" || n == "transfer-encoding" || n == "connection" || n == "content-encoding") continue;
                            try { context.Response.Headers[h.Name] = h.Value; } catch { /* 제한 헤더 */ }
                        }
                        byte[] body = string.IsNullOrEmpty(e.Body) ? Array.Empty<byte>() : Convert.FromBase64String(e.Body);
                        context.Response.ContentLength64 = body.Length;
                        context.Response.OutputStream.Write(body, 0, body.Length);
                        context.Response.Close();
                    }
                    catch { try { context.Response.Abort(); } catch { /* 끊김 */ } }
            }

            public static void Stop()
            {
                try { _listener?.Stop(); _listener?.Close(); } catch { /* 이미 닫힘 */ }
                _listener = null;
                try { _refuser?.Stop(); } catch { /* 이미 닫힘 */ }
                _refuser = null;
                lock (_slots) _slots.Clear();
            }

            // ── 응답 없이 끝난 요청 ────────────────────────────────────────────
            // 기록 때 연결 실패·중단으로 끝났거나 끝나지 않은 UnityWebRequest 는 여기로 보낸다: 연결만 받고, 기록 때 끝난 프레임까지 붙잡았다가
            // 응답 없이 끊는다(RST) — 클라이언트는 응답 코드 0 의 연결 오류로 끝난다. HttpListener 로는 못 한다(끊어도 200 머리가 먼저 나간다).

            public static string RefusePrefix => $"http://127.0.0.1:{_refusePort}/lc/";
            private static TcpListener _refuser;
            private static int _refusePort;

            // dueFrame: 이 프레임이 되면 끊는다. 끝나지 않은 요청은 세션이 끝날 때까지 붙잡는다.
            public static string Refuse(Entry entry, int dueFrame)
            {
                if (_refuser == null) StartRefuser();
                var slot = new Slot { Entry = entry, Due = dueFrame };
                int id;
                lock (_slots) { id = ++_next; _slots[id] = slot; }
                slot.Url = RefusePrefix + id;
                return slot.Url;
            }

            private static void StartRefuser()
            {
                _refuser = new TcpListener(IPAddress.Loopback, 0);
                _refuser.Start();
                _refusePort = ((IPEndPoint) _refuser.LocalEndpoint).Port;
                var thread = new Thread(RefuseLoop) { IsBackground = true, Name = "LocaleCapture.TapeServer.Refuse" };
                thread.Start(_refuser);
            }

            private static void RefuseLoop(object state)
            {
                var listener = (TcpListener) state;
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch { return; }
                    ThreadPool.QueueUserWorkItem(_ => Drop(client));
                }
            }

            private static void Drop(TcpClient client)
            {
                try
                {
                    // 요청 줄 "GET /lc/<번호> HTTP/1.1" 에서 번호를 읽는다
                    client.ReceiveTimeout = 5000;
                    var buffer = new byte[1024];
                    int read = client.GetStream().Read(buffer, 0, buffer.Length);
                    string head = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, read));
                    int at = head.IndexOf("/lc/", StringComparison.Ordinal);
                    int id = 0;
                    for (int i = at + 4; at >= 0 && i < head.Length && char.IsDigit(head[i]); i++) id = id * 10 + (head[i] - '0');
                    Slot slot;
                    lock (_slots) _slots.TryGetValue(id, out slot);
                    if (slot != null)
                    {
                        bool unfinished = slot.Entry.Outcome == Entry.Unfinished;
                        var waited = System.Diagnostics.Stopwatch.StartNew();
                        while ((unfinished || Now < slot.Due) && Current == Mode.Replay && (unfinished || waited.ElapsedMilliseconds < 30000)) Thread.Sleep(unfinished ? 20 : 1);
                    }
                }
                catch { /* 끊김 */ }
                finally
                {
                    try { client.LingerState = new LingerOption(true, 0); } catch { /* 닫힘 */ }   // 남은 것을 보내지 않고 바로 끊는다
                    try { client.Close(); } catch { /* 닫힘 */ }
                }
            }
        }
    }
}
