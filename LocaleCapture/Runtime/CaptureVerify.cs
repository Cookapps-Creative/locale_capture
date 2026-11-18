using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 어긋남 검사. 재생이 기록과 프레임 하나 어긋남 없이 같은 플레이였는지 본다. 허용 오차는 없다.
    //  · 터치: 누른 순간 입력을 받은 대상
    //  · UI: 입력을 받을 수 있는 UI(버튼·카드 등)가 켜지고 꺼진 프레임 — 흐름이 밀리거나 당겨진 첫 프레임이 여기서 드러난다
    //  · 화면: 최상위 Canvas 가 열리고 닫힌 프레임
    //  · 난수: 매 프레임 끝의 UnityEngine.Random 상태, new Random() 이 만들어진 프레임(덱 셔플 등)
    //  · 서버: 요청이 나간 프레임, 기록에 없는 요청이나 안 쓰인 응답
    //  · 입력: 재생한 입력이 모두 게임에 들어갔는지
    //  · 스레드: 작업 스레드에서 메인 스레드로 돌아오는 이어받기가 녹화와 같은 프레임에 왔는지
    //  · 로딩: 기록 때 끝났던 로딩이 재생에서도 같은 프레임에 끝났는지(원인을 짚는 참고 — 이것만으로는 실패로 보지 않는다)
    // 결과는 기록 폴더의 verify/<패스>.json 에 남고, 녹화 시작(START) 이후 처음 달라진 프레임이 창과 보고서 맨 위에 나온다.
    public static class CaptureVerify
    {
        public const string Folder = "verify";
        private const int MaxIssues = 40, MaxDifferences = 30;
        private const string LoadKind = "로딩";

        [Serializable]
        public struct Issue
        {
            public int Frame;
            public string Kind;
            public string Detail;
        }

        [Serializable]
        public sealed class Report
        {
            public string Pass;
            public string Locale;
            public bool Ok = true;
            public int Frames;
            public int VideoStartFrame;
            public int TraceVersion;                 // 기록 형식 판(0 = 이전 기록: 입력 받는 UI·난수 생성 비교가 없고, 누른 대상은 맨 위 그래픽으로만 가린다)
            // 녹화 시작(START) 이후 녹화와 처음 달라진 곳. 없으면 DivergedFrame = -1.
            public int DivergedFrame = -1;
            public string DivergedKind, DivergedDetail;
            // START 전에 처음 달라진 곳(영상에는 안 남는 구간이지만 그 뒤 흐름을 밀 수 있다). 없으면 -1.
            public int EarlyFrame = -1;
            public string EarlyKind, EarlyDetail;
            public int DifferenceCount;              // 달라진 곳 수(로딩 시점 차이 제외)
            public List<string> Differences = new List<string>();   // 프레임 순으로 처음 몇 건 "프레임 [분류] 내용"
            public int ServerReplayed, ServerLive, ServerMissed;
            public int ServerFetched;                // 실제 서버에서 받아 온 응답 수 — 있으면 그 패스를 받아 둔 응답으로 다시 뽑는다
            public int InputQueued, InputDelivered;   // 재생한 입력 수 / 게임 입력 시스템이 처리한 수
            public string InputDevices;
            public List<string> TapMismatches = new List<string>();   // 녹화와 다른 대상을 누른 곳
            public List<string> UiMismatches = new List<string>();    // 녹화와 다른 프레임에 열리고 닫힌 화면
            public List<int> FrameMs = new List<int>();               // 재생 프레임마다 실제로 걸린 ms
            public int HeldFrames;                         // 로딩을 기다리느라 더 돈 프레임
            public List<string> HeldOn = new List<string>();
            public List<string> HoldLog = new List<string>();   // 로딩 대기가 걸린 곳 "프레임 신호 ×대기 프레임 수"
            public List<ReplayManifest.Marker> Loads = new List<ReplayManifest.Marker>();   // 재생에서 끝난 Addressables 로딩(프레임·이름, 진단용)
            public List<string> Shifts = new List<string>();   // 기록과 다른 프레임에 끝난 로딩 "신호 기록→재생"
            public int StateMismatchFrames;
            public string StateHash;   // 재생 내내의 난수 상태 요약 — 패스끼리 같으면 같은 플레이
            public List<Issue> Issues = new List<Issue>();
        }

        private struct Diff
        {
            public int Frame;       // 녹화·재생 중 이른 쪽 프레임
            public string Kind;
            public string Detail;
            public bool Shift;      // 로딩이 끝난 프레임 차이(참고) — 목록에 몇 줄만 넣는다
        }

        private static Report _report;
        private static ulong _hash;
        private static ReplayManifest _manifest;
        private static List<TapProbe.Tap> _replayedTaps;
        private static List<UiTrace.Event> _replayedUi;
        private static List<HitTrace.Event> _replayedHits;
        private static readonly List<Diff> _diffs = new List<Diff>();
        private static int _firstMismatch = -1, _firstMismatchAfterStart = -1;
        private static bool _lastMatched = true;
        private const int MaxLoadHints = 6;   // 달라진 곳 목록에 넣는 로딩 시점 참고 줄 수(실제로 달라진 곳을 밀어내지 않게)

        internal static void Begin(string pass, string locale, ReplayManifest manifest, List<TapProbe.Tap> replayedTaps,
            List<UiTrace.Event> replayedUi, List<HitTrace.Event> replayedHits)
        {
            _manifest = manifest;
            _replayedTaps = replayedTaps;
            _replayedUi = replayedUi;
            _replayedHits = replayedHits;
            _report = new Report { Pass = pass, Locale = locale, VideoStartFrame = manifest.VideoStartFrame, TraceVersion = manifest.TraceVersion };
            _hash = 14695981039346656037UL;
            _diffs.Clear();
            _firstMismatch = -1;
            _firstMismatchAfterStart = -1;
            _lastMatched = true;
        }

        public static void Add(int frame, string kind, string detail)
        {
            Report report = _report;
            if (report == null) return;
            lock (_diffs)   // 서버 요청은 작업 스레드에서도 나간다
            {
                report.Ok = false;
                if (report.Issues.Count < MaxIssues) report.Issues.Add(new Issue { Frame = frame, Kind = kind, Detail = detail });
            }
        }

        // 녹화와 달라진 곳 하나. 끝날 때 프레임 순으로 추려 처음 달라진 곳을 정한다.
        private static void Differ(int frame, string kind, string detail)
        {
            if (_report == null) return;
            lock (_diffs) _diffs.Add(new Diff { Frame = frame, Kind = kind, Detail = detail });
        }

        internal static void CheckState(int frame, List<int> recordedStates)
        {
            if (_report == null) return;
            foreach (int v in RandomStates.Current()) _hash = (_hash ^ (uint) v) * 1099511628211UL;
            _lastMatched = RandomStates.Matches(recordedStates, frame);
            if (_lastMatched) return;
            _report.StateMismatchFrames++;
            if (_firstMismatch < 0) _firstMismatch = frame;
            if (_firstMismatchAfterStart < 0 && frame >= _report.VideoStartFrame) _firstMismatchAfterStart = frame;
        }

        internal static void Held(int frame, string marker)
        {
            if (_report == null) return;
            _report.HeldFrames++;
            if (_report.HeldOn.Count < 10 && !_report.HeldOn.Contains(marker)) _report.HeldOn.Add(marker);
            if (frame == _holdFrame && marker == _holdMarker) { _holdCount++; return; }
            FlushHold();
            _holdFrame = frame; _holdMarker = marker; _holdCount = 1;
        }

        private static int _holdFrame = -1, _holdCount;
        private static string _holdMarker;

        private static void FlushHold()
        {
            if (_holdCount > 0 && _report != null && _report.HoldLog.Count < 60) _report.HoldLog.Add($"{_holdFrame} {_holdMarker} ×{_holdCount}");
            _holdCount = 0;
        }

        internal static void FrameTime(int ms) => _report?.FrameMs.Add(ms);

        internal static List<ReplayManifest.Marker> Loads => _report?.Loads;

        internal static void Input(int queued, int delivered, string devices)
        {
            if (_report == null) return;
            _report.InputQueued = queued;
            _report.InputDelivered = delivered;
            _report.InputDevices = devices;
            if (delivered >= queued) return;
            Add(0, "입력", $"재생한 입력 {queued}개 중 {delivered}개만 게임에 들어갔다 ({devices})");
            Differ(_report.VideoStartFrame, "입력", $"재생한 입력 {queued}개 중 {delivered}개만 게임에 들어갔다 ({devices})");
        }

        // 로딩 신호가 도착한 프레임. 기록과 다르면 참고로 남긴다.
        internal static void Arrived(string marker, int recordedFrame, int frame)
        {
            if (_report == null || frame == recordedFrame) return;
            if (_report.Shifts.Count < 10) _report.Shifts.Add($"{marker} {recordedFrame}→{frame}");
            lock (_diffs) _diffs.Add(new Diff { Frame = Math.Min(recordedFrame, frame), Kind = LoadKind, Detail = $"{marker} 완료: 녹화 {recordedFrame}, 재생 {frame}", Shift = true });
        }

        // 로딩 대기가 오래 안 풀려 로딩 코드의 시계를 따로 돌렸다 — 그 로드는 녹화보다 늦게 시작됐다(흐름이 이미 달라졌다).
        internal static void LoadClockTicked(int frame, string marker)
        {
            Add(frame, LoadKind, $"'{marker}' 대기가 풀리지 않아 로딩 시계를 따로 돌렸다 — 이 로드는 녹화보다 늦게 시작됐다");
            Differ(frame, LoadKind, $"'{marker}' 대기가 풀리지 않아 로딩 시계를 따로 돌렸다(이 로드는 녹화보다 늦게 시작됐다)");
        }

        // 기록 때 끝났던 로딩이 제한 시간 안에 오지 않았다.
        internal static void LoadTimedOut(int frame, string marker, float seconds)
        {
            Add(frame, LoadKind, $"'{marker}' 이 {seconds}초 안에 끝나지 않았다 — 기록 때와 다른 로딩");
            Differ(frame, LoadKind, $"'{marker}' 이 {seconds}초를 기다려도 끝나지 않았다(기록에는 이 프레임까지 끝남)");
        }

        // 서버 요청이 나간 프레임(테이프가 부른다).
        internal static void Requested(string request, int recordedFrame, int frame)
        {
            if (_report == null || frame == recordedFrame) return;
            Differ(Math.Min(recordedFrame, frame), "서버", $"{request} 요청: 녹화 {recordedFrame}, 재생 {frame} ({frame - recordedFrame:+0;-0}프레임)");
            if (frame - recordedFrame > 1) Add(frame, "서버", $"{request} 요청이 기록(프레임 {recordedFrame})보다 늦게 나갔다");
        }

        // 녹화에서는 이 프레임에 작업 스레드에서 돌아온 이어받기가 재생에서는 기다려도 오지 않았다.
        internal static void ThreadMissed(int frame, string key)
        {
            Add(frame, "스레드", $"작업 스레드에서 돌아오는 '{key}' 가 기다려도 오지 않았다");
            Differ(frame, "스레드", $"작업 스레드에서 돌아오는 '{key}' 가 기다려도 오지 않았다(녹화에는 이 프레임에 돌아옴)");
        }

        // 녹화에 없던 작업 스레드 이어받기(참고로만 남긴다 — 바로 넘겼다).
        internal static void ThreadExtra(int frame, string key)
        {
            if (_report == null) return;
            if (_report.Issues.Count < MaxIssues) _report.Issues.Add(new Issue { Frame = frame, Kind = "스레드", Detail = $"녹화에 없던 작업 스레드 이어받기: {key}" });
        }

        // 기록에 없는 요청·기록보다 많이 나간 요청 등(테이프가 부른다).
        internal static void ServerDiffered(int frame, string detail)
        {
            Add(frame, "서버", detail);
            Differ(frame, "서버", detail);
        }

        internal static Report End(string directory, int frames)
        {
            Report report = _report;
            if (report == null) return null;
            List<RandomTrace.Call> replayedCalls = RandomTrace.End();
            bool traced = _manifest.TraceVersion >= 1;

            // 터치: 이전 기록은 맨 위 그래픽으로만 가려 언어별 문구에 걸릴 수 있어, 실패 판정에는 넣지 않는다(TapMismatches 로만 남는다).
            var tapFrames = new List<int>();
            report.TapMismatches = TapProbe.Compare(_manifest.Taps, _replayedTaps, tapFrames, traced);
            if (report.TapMismatches.Count > 0) Add(0, "터치", report.TapMismatches[0]);
            if (traced)
                for (int i = 0; i < tapFrames.Count; i++) Differ(tapFrames[i], "터치", "녹화와 다른 대상이 눌림 — " + report.TapMismatches[i]);

            // 화면: 최상위 Canvas 열림·닫힘
            int before = _diffs.Count;
            CompareTimeline("화면", Timeline(_manifest.UiEvents, e => (e.Name + (e.Open ? " 열림" : " 닫힘"), e.Frame, 1)),
                Timeline(_replayedUi, e => (e.Name + (e.Open ? " 열림" : " 닫힘"), e.Frame, 1)));
            for (int i = before; i < _diffs.Count; i++) report.UiMismatches.Add(_diffs[i].Detail);
            if (report.UiMismatches.Count > 0) Add(0, "화면", report.UiMismatches[0]);

            if (traced)
            {
                // UI: 입력을 받을 수 있는 대상이 켜지고 꺼진 프레임
                CompareTimeline("UI", Timeline(_manifest.HitEvents, e => (e.Name + (e.Delta > 0 ? " 켜짐" : " 꺼짐"), e.Frame, Math.Abs(e.Delta))),
                    Timeline(_replayedHits, e => (e.Name + (e.Delta > 0 ? " 켜짐" : " 꺼짐"), e.Frame, Math.Abs(e.Delta))));
                // 난수 생성: new Random() 이 만들어진 프레임
                CompareTimeline("난수 생성", Timeline(_manifest.RandomCreations, c => ("new Random() — " + c.Creator, c.Frame, 1)),
                    Timeline(CaptureClock.Creations, c => ("new Random() — " + c.Creator, c.Frame, 1)));
            }

            if (_firstMismatch >= 0)
            {
                string detail = (_lastMatched
                                    ? $"게임 난수 상태가 {report.StateMismatchFrames}프레임 동안 기록과 달랐다가 다시 맞았다 — 그 즈음 게임이 프레임 하나 밀렸다(로딩 대기 {report.HeldFrames}프레임: {string.Join(", ", report.HeldOn)})"
                                    : "게임 난수 상태가 이 프레임부터 끝까지 기록과 다르다 — 플레이가 갈라졌다")
                                + $" | 난수 호출 기록 [{RandomTrace.Around(_manifest.RandomCalls, _firstMismatch)}] 재생 [{RandomTrace.Around(replayedCalls, _firstMismatch)}]"
                                + (report.Shifts.Count > 0 ? $" | 로딩 프레임 차이 [{string.Join(", ", report.Shifts)}]" : "");
                Add(_firstMismatch, "상태", detail);
                Differ(_firstMismatch, "난수", detail);
                // 처음 갈라진 곳이 녹화 시작 전이어도, 시작 뒤까지 다른 채면 영상 구간의 플레이가 다른 것이다(시작 전 것만 남기면 참고로만 보인다).
                if (_firstMismatch < report.VideoStartFrame && _firstMismatchAfterStart >= 0)
                    Differ(_firstMismatchAfterStart, "난수", $"게임 난수 상태가 녹화 시작 전(프레임 {_firstMismatch})부터 기록과 달랐고 시작 뒤에도 다르다 — 영상 구간의 플레이가 기록과 다르다");
            }

            FlushHold();
            _holdFrame = -1;
            Summarize(report);
            _report = null;
            _manifest = null;
            report.Frames = frames;
            report.StateHash = _hash.ToString("x16");
            report.ServerReplayed = NetworkTape.Replayed;
            report.ServerLive = NetworkTape.Live;
            report.ServerMissed = NetworkTape.Missed;
            report.ServerFetched = NetworkTape.Fetched;
            try
            {
                string dir = Path.Combine(directory, Folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, report.Pass + ".json"), JsonUtility.ToJson(report, true));
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 검사 결과 저장 실패: " + e.Message); }
            return report;
        }

        // 달라진 곳을 프레임 순으로 추려 처음 달라진 곳(START 전·후)을 정한다. 로딩 시점 차이는 목록에만 넣는다.
        private static void Summarize(Report report)
        {
            lock (_diffs) SummarizeLocked(report);
        }

        private static void SummarizeLocked(Report report)
        {
            _diffs.Sort((a, b) => a.Frame != b.Frame ? a.Frame.CompareTo(b.Frame) : string.CompareOrdinal(a.Kind, b.Kind));
            int loadHints = 0;
            foreach (Diff d in _diffs)
            {
                if (report.Differences.Count < MaxDifferences && (!d.Shift || loadHints++ < MaxLoadHints)) report.Differences.Add($"{d.Frame} [{d.Kind}] {d.Detail}");
                if (d.Kind == LoadKind) continue;
                report.DifferenceCount++;
                if (d.Frame >= report.VideoStartFrame)
                {
                    if (report.DivergedFrame < 0) { report.DivergedFrame = d.Frame; report.DivergedKind = d.Kind; report.DivergedDetail = d.Detail; }
                }
                else if (report.EarlyFrame < 0) { report.EarlyFrame = d.Frame; report.EarlyKind = d.Kind; report.EarlyDetail = d.Detail; }
            }
            if (report.DifferenceCount > 0) report.Ok = false;
            _diffs.Clear();
        }

        // 기록의 프레임 수를 넘는 것은 뺀다 — 기록은 STOP 을 누른 마지막 프레임 도중에 끝나, 그 프레임의 일은 재생이 다시 돌지 않는다.
        private static List<(string key, int frame)> Timeline<T>(List<T> events, Func<T, (string key, int frame, int count)> read)
        {
            var list = new List<(string, int)>();
            if (events == null) return list;
            int limit = _manifest != null && _manifest.Frames > 0 ? _manifest.Frames : int.MaxValue;
            foreach (T e in events)
            {
                (string key, int frame, int count) = read(e);
                if (frame >= limit) continue;
                for (int i = 0; i < count; i++) list.Add((key, frame));
            }
            return list;
        }

        // 같은 이름의 같은 순번끼리 맞춰, 프레임이 다르거나 한쪽에만 있는 것을 남긴다.
        private static void CompareTimeline(string kind, List<(string key, int frame)> recorded, List<(string key, int frame)> replayed)
        {
            var queues = new Dictionary<string, Queue<int>>();
            foreach ((string key, int frame) in replayed)
            {
                if (!queues.TryGetValue(key, out Queue<int> q)) queues[key] = q = new Queue<int>();
                q.Enqueue(frame);
            }
            foreach ((string key, int frame) in recorded)
            {
                if (!queues.TryGetValue(key, out Queue<int> q) || q.Count == 0) { Differ(frame, kind, $"{key}: 녹화 {frame}, 재생에는 없음"); continue; }
                int got = q.Dequeue();
                if (got != frame) Differ(Math.Min(got, frame), kind, $"{key}: 녹화 {frame}, 재생 {got} ({got - frame:+0;-0}프레임)");
            }
            foreach (KeyValuePair<string, Queue<int>> pair in queues)
                foreach (int frame in pair.Value) Differ(frame, kind, $"{pair.Key}: 녹화에는 없음, 재생 {frame}");
        }

        public static Report Load(string directory, string pass)
        {
            string path = Path.Combine(directory, Folder, pass + ".json");
            return File.Exists(path) ? JsonUtility.FromJson<Report>(File.ReadAllText(path)) : null;
        }
    }
}
