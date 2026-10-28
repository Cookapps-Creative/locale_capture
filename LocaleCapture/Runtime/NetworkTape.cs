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
        }

        [Serializable]
        private sealed class TapeFile { public List<Entry> Entries = new List<Entry>(); }

        public static Mode Current { get; private set; }
        public static int Replayed { get; private set; }
        public static int Missed { get; private set; }
        public static int Live { get; private set; }

        private static string _directory, _locale;
        private static int _fps = 30;
        private static string[] _liveMarkers = Array.Empty<string>();
        private static volatile int _frame;
        private static readonly List<Entry> _recorded = new List<Entry>();
        private static readonly List<Entry> _liveRecorded = new List<Entry>();
        private static readonly Dictionary<string, Queue<Entry>> _queues = new Dictionary<string, Queue<Entry>>();
        private static readonly Dictionary<string, Queue<Entry>> _liveQueues = new Dictionary<string, Queue<Entry>>();
        private static readonly List<(int due, TaskCompletionSource<HttpResponseMessage> tcs, HttpResponseMessage response)> _waiting =
            new List<(int, TaskCompletionSource<HttpResponseMessage>, HttpResponseMessage)>();
        private static readonly ConditionalWeakTable<HttpRequestMessage, object> _seen = new ConditionalWeakTable<HttpRequestMessage, object>();
        private static readonly ConditionalWeakTable<HttpRequestMessage, object> _handled = new ConditionalWeakTable<HttpRequestMessage, object>();
        private static readonly Dictionary<string, int> _inFlight = new Dictionary<string, int>();   // HttpClient 요청(안쪽 UnityWebRequest 를 이중 기록하지 않게)
        private static readonly HashSet<string> _warned = new HashSet<string>();

        // ── 세션 ───────────────────────────────────────────────────────────────

        internal static void BeginRecord(string directory, string[] liveMarkers)
        {
            Reset(directory, null, liveMarkers);
            Current = Mode.Record;
        }

        internal static void BeginReplay(string directory, string locale, string[] liveMarkers, int fps)
        {
            Reset(directory, locale, liveMarkers);
            _fps = Math.Max(1, fps);
            Load(Path.Combine(directory, FileName), _queues);
            Load(Path.Combine(directory, LiveFileName(locale)), _liveQueues);
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
            _lastByKey.Clear();
            _waiting.Clear();
            _warned.Clear();
            lock (_inFlight) _inFlight.Clear();
            Replayed = Missed = Live = 0;
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
            _heldFrames = 0;
        }

        // 로딩 대기로 게임 시간이 멈춘 프레임. 응답은 계속 흐르게 한다 — 로딩이 응답을 기다리면 서로 기다리다 멈춘다.
        internal static void Hold() => _heldFrames++;

        private static volatile int _heldFrames;
        private static int Now => _frame + _heldFrames;

        // 때가 된 응답을 넘긴다. 세션의 Update 에서 부른다 — 게임 쪽 await 이어받기가 같은 프레임의 정해진 단계
        // (Update 뒤 지연 작업)에서 돌아, 재생마다 같은 프레임에 응답이 도착한다.
        internal static void Release()
        {
            int frame = Now;
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                if (_waiting[i].due > frame) continue;
                (int _, TaskCompletionSource<HttpResponseMessage> tcs, HttpResponseMessage response) = _waiting[i];
                _waiting.RemoveAt(i);
                tcs.TrySetResult(response);
            }
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
                    if (save && _liveRecorded.Count > 0) Append(Path.Combine(_directory, LiveFileName(_locale)), _liveRecorded);
                    foreach (KeyValuePair<string, Queue<Entry>> pair in _queues)
                        if (pair.Value.Count > 0)
                            CaptureVerify.Add(_frame, "서버", $"기록된 응답 {pair.Value.Count}개가 쓰이지 않았다: {Short(pair.Key)}");
                    Debug.Log($"[LocaleCapture] 서버 응답 재생 {Replayed}개, 실서버 {Live}개, 테이프에 없음 {Missed}개");
                }
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 테이프 저장 실패: " + e.Message); }
            foreach ((int _, TaskCompletionSource<HttpResponseMessage> tcs, HttpResponseMessage response) in _waiting) tcs.TrySetResult(response);
            _waiting.Clear();
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
                if (!live && _frame - e.RequestFrame > 1)
                    CaptureVerify.Add(_frame, "서버", $"{Short(key)} 요청이 기록(프레임 {e.RequestFrame})보다 늦게 나갔다");
                Replayed++;
                return e;
            }
            if (live) { Live++; return null; }   // 이 언어로는 처음 — 실제 서버로 가고 언어별 테이프에 남긴다
            Missed++;
            // 같은 요청이 기록보다 한 번 더 나간 것이면 실제 서버로 보내지 않는다 — 연결 대기 화면이 끼고, 실제 계정이 바뀔 수 있다.
            if (_lastByKey.TryGetValue(key, out Entry last))
            {
                if (_warned.Add(key)) Debug.LogWarning($"[LocaleCapture] 기록보다 많이 나간 서버 요청이라 마지막 기록 응답으로 대신한다: {key}");
                CaptureVerify.Add(_frame, "서버", "기록보다 많이 나간 요청(마지막 기록 응답으로 대신): " + Short(key));
                return last;
            }
            if (_warned.Add(key)) Debug.LogWarning($"[LocaleCapture] 기록에 없는 서버 요청이라 실제 서버로 보낸다: {key}");
            CaptureVerify.Add(_frame, "서버", "기록에 없는 요청: " + Short(key));
            return null;
        }

        // 응답이 도착할 프레임. 요청이 기록보다 일찍 나갔으면 기록 때 도착한 프레임까지 붙잡는다 —
        // 부팅 중 실시간 대기 등으로 요청 시점이 앞당겨져도 응답 뒤 화면은 기록과 같은 프레임에 이어진다.
        private static int DueFrame(Entry e) => Math.Max(Now, e.RequestFrame) + e.LatencyFrames;

        private static Entry NewEntry(string method, string url, bool live)
        {
            var entry = new Entry { Method = method, Url = url, RequestFrame = _frame };
            if (Current == Mode.Record && !live) _recorded.Add(entry);
            else _liveRecorded.Add(entry);
            return entry;
        }

        // 기록 대상인가: 기록 중이면 전부, 재생 중이면 언어별 테이프에 없어 실제 서버로 간 언어 요청만.
        private static bool ShouldRecord(bool live, bool replayedFromTape) =>
            Current == Mode.Record || (Current == Mode.Replay && live && !replayedFromTape);

        private static string Key(string method, string url) => method + " " + url;

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

        // Harmony prefix: 테이프에 있으면 서버 대신 응답(true).
        // 서버로 보낼 땐 여기서 진행 중 표시를 건다 — 안쪽 UnityWebRequest 는 원본 호출 안에서 바로 나가서 postfix 로는 늦다.
        public static bool TryReplay(HttpRequestMessage request, out Task<HttpResponseMessage> result)
        {
            result = null;
            if (Current == Mode.Off || request == null || _seen.TryGetValue(request, out _)) return false;
            _seen.Add(request, null);
            string url = request.RequestUri?.ToString();
            Entry e = Current == Mode.Replay ? Take(request.Method.Method, url, IsLive(url, BodyOf(request))) : null;
            if (e == null)
            {
                string key = Key(request.Method.Method, url);
                lock (_inFlight) _inFlight[key] = _inFlight.TryGetValue(key, out int n) ? n + 1 : 1;
                return false;
            }
            _fromTape.Add(request, e);
            HttpResponseMessage response = ToResponse(e, request);
            int due = DueFrame(e);
            if (due <= Now) { result = Task.FromResult(response); return true; }
            var tcs = new TaskCompletionSource<HttpResponseMessage>();
            _waiting.Add((due, tcs, response));
            result = tcs.Task;
            return true;
        }

        // Harmony postfix: 기록 대상이면 응답을 받아 저장하고 그대로 넘긴다.
        public static Task<HttpResponseMessage> Record(HttpRequestMessage request, Task<HttpResponseMessage> original)
        {
            if (Current == Mode.Off || request == null || original == null) return original;
            if (_fromTape.TryGetValue(request, out _)) return original;
            if (_handled.TryGetValue(request, out _)) return original;   // 겹친 SendAsync — 안쪽 postfix 가 먼저 처리했다
            _handled.Add(request, null);
            string url = request.RequestUri?.ToString();
            string key = Key(request.Method.Method, url);
            original.ContinueWith(_ =>
            {
                lock (_inFlight)
                    if (_inFlight.TryGetValue(key, out int n)) { if (n <= 1) _inFlight.Remove(key); else _inFlight[key] = n - 1; }
            }, TaskContinuationOptions.ExecuteSynchronously);
            bool live = IsLive(url, BodyOf(request));
            if (Current == Mode.Record && live) Live++;
            if (!ShouldRecord(live, false)) return original;
            return RecordAsync(NewEntry(request.Method.Method, url, live), original);
        }

        private static async Task<HttpResponseMessage> RecordAsync(Entry entry, Task<HttpResponseMessage> original)
        {
            HttpResponseMessage response = await original;
            try
            {
                entry.LatencyFrames = Math.Max(0, _frame - entry.RequestFrame);
                entry.Status = (int) response.StatusCode;
                entry.Reason = response.ReasonPhrase;
                foreach (KeyValuePair<string, IEnumerable<string>> h in response.Headers)
                    foreach (string v in h.Value) entry.Headers.Add(new Header { Name = h.Key, Value = v });
                if (response.Content != null)
                {
                    await response.Content.LoadIntoBufferAsync();
                    entry.Body = Convert.ToBase64String(await response.Content.ReadAsByteArrayAsync());
                    foreach (KeyValuePair<string, IEnumerable<string>> h in response.Content.Headers)
                        foreach (string v in h.Value) entry.ContentHeaders.Add(new Header { Name = h.Key, Value = v });
                }
            }
            catch (Exception e) { Debug.LogWarning($"[LocaleCapture] 응답 기록 실패({entry.Url}): {e.Message}"); }
            return response;
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
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || url.StartsWith(TapeServer.Prefix, StringComparison.OrdinalIgnoreCase)) return false;
            lock (_inFlight) return !_inFlight.ContainsKey(Key(request.method, url));
        }

        // Harmony prefix(SendWebRequest): 재생 중 테이프에 있으면 주소를 테이프 서버로 바꾼다.
        public static void BeforeWebRequest(UnityWebRequest request)
        {
            if (Current != Mode.Replay || !Tapeable(request)) return;
            bool live = IsLive(request.url, null);
            Entry e = Take(request.method, request.url, live);
            if (e == null) { if (live) _pendingLive.Add(request); return; }
            request.url = TapeServer.Serve(e, DueFrame(e) - 2);   // 보내고 완료 이벤트까지 두 프레임쯤 걸린다
        }

        private static readonly HashSet<UnityWebRequest> _pendingLive = new HashSet<UnityWebRequest>();

        // Harmony postfix(SendWebRequest): 기록 대상이면 끝났을 때 응답을 저장한다.
        public static void AfterWebRequest(UnityWebRequest request, UnityWebRequestAsyncOperation operation)
        {
            if (Current == Mode.Off || request == null || operation == null) return;
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
        private static class TapeServer
        {
            public static string Prefix => $"http://127.0.0.1:{_port}/lc/";
            private static HttpListener _listener;
            private static int _port;
            private static int _next;
            private static readonly Dictionary<int, (Entry entry, int due)> _entries = new Dictionary<int, (Entry, int)>();

            // dueFrame: 이 프레임이 되면 응답을 보낸다(완료 이벤트가 기록 때 응답이 온 프레임에 오도록).
            public static string Serve(Entry entry, int dueFrame)
            {
                if (_listener == null) Start();
                int id;
                lock (_entries) { id = ++_next; _entries[id] = (entry, dueFrame); }
                return Prefix + id;
            }

            public static void Start()
            {
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

            // 기록 때 응답까지 걸린 시간(프레임 / fps)만큼 기다렸다 보낸다 — 재생도 실제 속도로 돌므로 기록과 비슷한 프레임에 도착한다.
            private static void Respond(HttpListenerContext context)
            {
                    try
                    {
                        string idText = context.Request.Url.AbsolutePath.Substring("/lc/".Length);
                        (Entry e, int due) item;
                        bool found;
                        lock (_entries) found = _entries.TryGetValue(int.TryParse(idText, out int id) ? id : -1, out item);
                        if (!found) { context.Response.StatusCode = 404; context.Response.Close(); return; }
                        Entry e = item.e;
                        var waited = System.Diagnostics.Stopwatch.StartNew();
                        while (Now < item.due && Current == Mode.Replay && waited.ElapsedMilliseconds < 30000) Thread.Sleep(1);
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
                lock (_entries) _entries.Clear();
            }
        }
    }
}
