using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 어긋남 검사. 재생이 기록과 같은 플레이였는지 본다.
    // "무엇이 일어났나"(누가 입력을 받았나, 난수 흐름, 서버 응답, 한쪽에만 있는 UI)는 정확히 보고,
    // "언제 보였나"(UI·화면이 켜지고 꺼진 프레임, 서버 요청이 나간 프레임)는 Slack 프레임까지 넘겨 참고로 남긴다.
    //  · 터치: 누른 순간 입력을 받은 대상
    //  · UI: 입력을 받을 수 있는 UI(버튼·카드 등)가 켜지고 꺼진 프레임 — 흐름이 밀리거나 당겨진 첫 프레임이 여기서 드러난다
    //        (스크롤 목록 안은 줄 크기가 녹화와 같을 때만 — 다른 언어에서 줄 높이가 달라진 목록은 근거를 확인하고 뺀다, HitTrace.LayoutShifted)
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
        public const string ServerHold = "서버:";   // 세션이 서버 응답을 기다릴 때 대기 이름 앞에 붙이는 말
        // "언제 보였나" 를 보는 검사(UI 켜짐·꺼짐, 화면 열림·닫힘, 서버 요청이 나간 프레임)가 넘겨 주는 프레임 차이.
        // 같은 일이 이 안에 일어났으면 다름으로 치지 않고 참고(Slacks)로 남긴다 — 0.07초 안이라 영상에서 구분되지 않고 플레이 결과도 바뀌지 않는다.
        // "무엇이 일어났나"(누가 입력을 받았나, 난수 흐름, 서버 응답, 한쪽에만 있는 팝업)에는 쓰지 않는다.
        public const int Slack = 2;

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
            public int ServerPiped;                  // 재생한 응답 중 통신 코드를 그대로 돌려 답한 수(전송 단계의 원본 응답으로). 0 이면 통신 코드가 건너뛰어졌다
            public int InputQueued, InputDelivered;   // 재생한 입력 수 / 게임 입력 시스템이 처리한 수
            public string InputDevices;
            public List<string> TapMismatches = new List<string>();   // 녹화와 다른 대상을 누른 곳
            // 끌기가 된 누름이라 "클릭을 받을 대상" 을 견주지 않은 곳 "프레임: 내용"(TapProbe.ClickNeverSent). 실패가 아니다 — 결과에 참고로 나온다.
            public List<string> TapNotes = new List<string>();
            public List<string> UiMismatches = new List<string>();    // 녹화와 다른 프레임에 열리고 닫힌 화면
            public List<int> FrameMs = new List<int>();               // 재생 프레임마다 실제로 걸린 ms
            public int HeldFrames;                         // 로딩을 기다리느라 더 돈 프레임
            public int ServerHeldFrames;                   // 그중 서버 응답을 기다린 프레임(실제 서버에서 받는 응답이 녹화 프레임까지 안 온 경우 등)
            public List<string> HeldOn = new List<string>();
            public List<string> HoldLog = new List<string>();   // 로딩 대기가 걸린 곳 "프레임 신호 ×대기 프레임 수"
            public List<ReplayManifest.Marker> Loads = new List<ReplayManifest.Marker>();   // 재생에서 끝난 Addressables 로딩(프레임·이름, 진단용)
            public List<string> Shifts = new List<string>();   // 기록과 다른 프레임에 끝난 로딩 "신호 기록→재생"
            // 이 패스에서 UI 배치 계산으로 자리·크기가 바뀐 프레임. 녹화와 비교하지 않는다(언어마다 다른 게 정상) — 영상 비교가
            // 언어 영상끼리 다르게 바뀐 프레임이 배치 차이인지 가리는 데 쓴다.
            public List<LayoutTrace.Move> Layouts = new List<LayoutTrace.Move>();
            public bool LayoutTraced;   // 배치를 끝까지 쟀나(이전 버전으로 뽑았거나 도중에 꺼졌으면 false — 영상 비교가 배치 차이를 가리지 않는다)
            // 줄 크기가 녹화 언어와 달라 UI 켜짐·꺼짐 비교에서 뺀 스크롤 목록 구간(구간마다 한 줄). 실패가 아니다 — 결과에 참고로 나온다.
            public List<string> LayoutSkips = new List<string>();
            // 다른 언어 패스에서 스크롤 목록 안이 녹화와 달랐는데 줄·목록 크기는 녹화와 같았던 곳. 실패로 치지 않고 주의로 알린다.
            public List<string> ListWarnings = new List<string>();
            // 녹화와 1~2프레임(Slack) 차이로 보인 곳의 수와 처음 몇 건 "프레임 [분류] 내용". 실패가 아니다 — 결과에 참고로 나온다.
            public int SlackCount;
            public List<string> Slacks = new List<string>();
            // 난수 상태가 짧게(연달아 Slack 프레임 안) 어긋났다가 다시 맞았고, 난수를 쓴 코드의 순서는 녹화와 같았다.
            public bool RandomShifted;
            // 이 패스에서 스크롤 목록의 크기가 바뀐 프레임 — 영상 비교가 한쪽만 화면이 바뀐 프레임에 줄 높이가 다른 목록이 떠 있었는지 볼 때 쓴다.
            public List<HitTrace.ListState> Lists = new List<HitTrace.ListState>();
            // 입력 받는 UI 추적이 도중에 꺼져(기록이든 재생이든) 이 프레임부터는 UI 켜짐·꺼짐을 비교하지 못했다. 끝까지 봤으면 -1.
            public bool UiCheckStopped;
            public int UiComparedUntil;
            // 재생 중 게임 언어가 이 패스의 언어가 아니어서 도구가 바꾼 횟수, 그 첫 프레임, 그때 게임이 쓰던 언어.
            // 그 프레임 앞의 영상은 다른 언어로 찍혔을 수 있다.
            public int LocaleSwitches;
            public int LocaleSwitchFrame = -1;
            public string LocaleSwitchedFrom;
            // GPU 에서 읽지 못해 직전 프레임으로 채운 영상 프레임 수와 그 첫 프레임. 플레이는 같아도 그 언어 영상만 그 순간 한 프레임 멈춰 보인다.
            public int RepeatedFrames;
            public int RepeatedFirstFrame = -1;
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
        private static List<HitTrace.ListState> _replayedLists;
        private static readonly List<Diff> _diffs = new List<Diff>();
        private static int _firstMismatch = -1, _firstMismatchAfterStart = -1;
        private static bool _lastMatched = true;
        private static ulong _recordedHash;          // 같은 프레임들의 녹화 쪽 난수 상태 요약
        private static int _run, _longestRun;        // 난수 상태가 연달아 어긋난 프레임 수(지금 / 가장 긴 것)
        private const int MaxSlackNotes = 6;
        private const int MaxLoadHints = 6;   // 달라진 곳 목록에 넣는 로딩 시점 참고 줄 수(실제로 달라진 곳을 밀어내지 않게)

        internal static void Begin(string pass, string locale, ReplayManifest manifest, List<TapProbe.Tap> replayedTaps,
            List<UiTrace.Event> replayedUi, List<HitTrace.Event> replayedHits, List<HitTrace.ListState> replayedLists)
        {
            _manifest = manifest;
            _replayedTaps = replayedTaps;
            _replayedUi = replayedUi;
            _replayedHits = replayedHits;
            _replayedLists = replayedLists;
            _report = new Report { Pass = pass, Locale = locale, VideoStartFrame = manifest.VideoStartFrame, TraceVersion = manifest.TraceVersion };
            _hash = 14695981039346656037UL;
            _recordedHash = _hash;
            _run = _longestRun = 0;
            _diffs.Clear();
            _firstMismatch = -1;
            _firstMismatchAfterStart = -1;
            _lastMatched = true;
        }

        // 녹화와 Slack 프레임 안의 차이로 보인 곳 하나. 다름으로 치지 않고 개수와 처음 몇 건을 남긴다.
        private static void Tolerated(int frame, string kind, string detail)
        {
            Report report = _report;
            if (report == null) return;
            lock (_diffs)
            {
                report.SlackCount++;
                if (report.Slacks.Count < MaxSlackNotes) report.Slacks.Add($"{frame} [{kind}] {detail}");
            }
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
            int[] current = RandomStates.Current();
            foreach (int v in current) _hash = (_hash ^ (uint) v) * 1099511628211UL;
            // 녹화 쪽 요약도 같은 프레임들로 쌓는다(기록에 그 프레임이 없으면 지금 값으로 — 아래 Matches 가 그럴 때 맞은 것으로 본다).
            int at = frame * 4;
            bool recorded = recordedStates != null && frame >= 0 && at + 3 < recordedStates.Count;
            for (int i = 0; i < 4; i++) _recordedHash = (_recordedHash ^ (uint) (recorded ? recordedStates[at + i] : current[i])) * 1099511628211UL;
            _lastMatched = RandomStates.Matches(recordedStates, frame);
            if (_lastMatched) { _run = 0; return; }
            if (++_run > _longestRun) _longestRun = _run;
            _report.StateMismatchFrames++;
            if (_firstMismatch < 0) _firstMismatch = frame;
            if (_firstMismatchAfterStart < 0 && frame >= _report.VideoStartFrame) _firstMismatchAfterStart = frame;
        }

        internal static void Held(int frame, string marker)
        {
            if (_report == null) return;
            _report.HeldFrames++;
            if (marker != null && marker.StartsWith(ServerHold, StringComparison.Ordinal)) _report.ServerHeldFrames++;
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

        // 게임 언어가 이 패스의 언어가 아니어서 도구가 바꿨다(from = 그때 게임이 쓰던 언어).
        internal static void LocaleSwitched(int frame, string from)
        {
            if (_report == null) return;
            if (_report.LocaleSwitches++ > 0) return;
            _report.LocaleSwitchFrame = frame;
            _report.LocaleSwitchedFrom = from;
        }

        // 영상 프레임 하나를 읽지 못해 직전 프레임으로 채웠다(세션 프레임 번호).
        internal static void FrameRepeated(int frame)
        {
            if (_report == null) return;
            if (_report.RepeatedFrames++ == 0) _report.RepeatedFirstFrame = frame;
        }

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
            string detail = $"{request} 요청: 녹화 {recordedFrame}, 재생 {frame} ({frame - recordedFrame:+0;-0}프레임)";
            // 요청이 나간 프레임만 조금 다른 것은 넘긴다 — 응답은 녹화 때 도착한 프레임에 넘기므로 그 뒤 흐름은 같다.
            if (Math.Abs(frame - recordedFrame) <= Slack) { Tolerated(Math.Min(recordedFrame, frame), "서버", detail); return; }
            Differ(Math.Min(recordedFrame, frame), "서버", detail);
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
            report.TapMismatches = TapProbe.Compare(_manifest.Taps, _replayedTaps, tapFrames, traced, report.TapNotes);
            if (report.TapMismatches.Count > 0) Add(0, "터치", report.TapMismatches[0]);
            if (traced)
                for (int i = 0; i < tapFrames.Count; i++) Differ(tapFrames[i], "터치", "녹화와 다른 대상이 눌림 — " + report.TapMismatches[i]);

            // 화면: 최상위 Canvas 열림·닫힘
            int before = _diffs.Count;
            CompareTimeline("화면", Timeline(_manifest.UiEvents, e => (e.Name + (e.Open ? " 열림" : " 닫힘"), e.Frame, 1)),
                Timeline(_replayedUi, e => (e.Name + (e.Open ? " 열림" : " 닫힘"), e.Frame, 1)), Slack);
            for (int i = before; i < _diffs.Count; i++) report.UiMismatches.Add(_diffs[i].Detail);
            if (report.UiMismatches.Count > 0) Add(0, "화면", report.UiMismatches[0]);

            if (traced)
            {
                // UI: 입력을 받을 수 있는 대상이 켜지고 꺼진 프레임
                CompareHits(report, _manifest, _replayedHits, _replayedLists);
                // 난수 생성: new Random() 이 만들어진 프레임
                // 난수 생성: new Random() 이 만들어진 프레임 — 난수 흐름이라 유예를 두지 않는다
                CompareTimeline("난수 생성", Timeline(_manifest.RandomCreations, c => ("new Random() — " + c.Creator, c.Frame, 1)),
                    Timeline(CaptureClock.Creations, c => ("new Random() — " + c.Creator, c.Frame, 1)), 0);
            }

            // 난수: 프레임 끝의 상태가 녹화와 같아야 한다. 다만 연달아 Slack 프레임 안으로 어긋났다가 다시 맞았고, 난수를 쓴 코드의 순서가
            // 녹화와 같으면 같은 값이 같은 곳에 쓰인 것이다(쓰인 프레임만 조금 달랐다) — 다름으로 치지 않고 참고로 남긴다.
            if (_firstMismatch >= 0 && RandomTolerated(_lastMatched, _longestRun, _manifest.RandomCalls, replayedCalls))
            {
                report.RandomShifted = true;
                Tolerated(_firstMismatch, "난수", $"게임 난수 상태가 {report.StateMismatchFrames}프레임(연달아 길게는 {_longestRun}프레임) 동안 기록과 달랐다가 다시 맞았다 — 난수를 쓴 코드의 순서는 녹화와 같다");
            }
            else if (_firstMismatch >= 0)
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
            // 난수가 짧게 어긋났다 다시 맞은 패스는 프레임별 상태가 녹화와 조금 다르다 — 같은 난수 흐름으로 확인됐으므로 녹화 쪽 요약을 쓴다
            // (프레임마다 꼭 맞은 패스의 요약과 같은 값이라, 패스끼리 견줄 때 다른 플레이로 잡히지 않는다).
            report.StateHash = (report.RandomShifted ? _recordedHash : _hash).ToString("x16");
            if (_replayedLists != null) report.Lists = new List<HitTrace.ListState>(_replayedLists);
            report.ServerReplayed = NetworkTape.Replayed;
            report.ServerLive = NetworkTape.Live;
            report.ServerMissed = NetworkTape.Missed;
            report.ServerFetched = NetworkTape.Fetched;
            report.ServerPiped = NetworkTape.Piped;
            report.LayoutTraced = LayoutTrace.Active;
            report.Layouts = LayoutTrace.End();
            try
            {
                string dir = Path.Combine(directory, Folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, report.Pass + ".json"), JsonUtility.ToJson(report, true));
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 검사 결과 저장 실패: " + e.Message); }
            return report;
        }

        // 입력을 받을 수 있는 UI 가 켜지고 꺼진 프레임을 녹화와 맞춰 본다. 같은 일이 Slack 프레임 안에 일어났으면 넘긴다.
        // 스크롤 목록 안은 줄 높이가 글자 길이로 정해져, 같은 플레이라도 줄이 화면에 들어오는 프레임·보이는 줄 수가 언어마다 다르다.
        // 그래서 녹화 언어가 아닌 패스에서는 목록 안의 대상을 다름으로 치지 않는다:
        //  · 줄·목록 크기가 녹화와 다르다는 근거가 있는 구간(HitTrace.LayoutShifted)은 참고(LayoutSkips)로 남긴다.
        //  · 근거 없이 달랐던 곳(크기는 녹화와 같은데 프레임이 다르다)은 주의(ListWarnings)로 알린다 — 실패는 아니지만 확인할 곳이다.
        // 녹화 언어로 뽑는 패스는 배치가 녹화와 같으므로 목록 안도 그대로 비교한다.
        //  · 기록 형식 6 이하의 기록은 목록 안인지를 남기지 않았고 이름별로만 셌다 — 재생에서 늘 목록 안에서만 켜진 이름을 목록 안의 대상으로 본다.
        //  · 추적이 도중에 꺼졌으면(기록이든 재생이든) 그 프레임부터는 한쪽 타임라인이 비어 있다 — 견주지 않고 UiComparedUntil 로 남긴다.
        //  · 엔진이 이름에 화면 문구를 넣는 대상(Dropdown 항목)은 문구를 떼고 견준다(TapProbe.Neutral).
        private static void CompareHits(Report report, ReplayManifest manifest, List<HitTrace.Event> replayed, List<HitTrace.ListState> replayedLists)
        {
            List<HitTrace.Event> recorded = manifest.HitEvents;
            if (recorded != null && replayed != null)
            {
                int limit = manifest.Frames > 0 ? manifest.Frames : int.MaxValue;
                int stop = manifest.HitFailed ? manifest.HitFailedFrame : int.MaxValue;
                if (HitTrace.FailedFrame >= 0) stop = Math.Min(stop, HitTrace.FailedFrame);
                if (stop < limit)
                {
                    limit = stop;
                    report.UiCheckStopped = true;
                    report.UiComparedUntil = stop;
                    recorded = recorded.FindAll(e => e.Frame < stop);
                    replayed = replayed.FindAll(e => e.Frame < stop);
                }
                bool otherLocale = !string.IsNullOrEmpty(report.Locale) && !string.Equals(report.Locale, manifest.RecordedLocale, StringComparison.OrdinalIgnoreCase);
                if (manifest.TraceVersion >= 7 && otherLocale)
                {
                    List<HitTrace.Shift> shifts = HitTrace.LayoutShifted(recorded, replayed, manifest.ListEvents, replayedLists, limit);
                    foreach (HitTrace.Shift shift in shifts) report.LayoutSkips.Add(shift.Note);
                    // 근거가 있는 구간을 뺀 나머지 목록 안 — 달랐으면 주의로만 남긴다
                    List<(int frame, string detail)> rest = TimelineDiffs(
                        HitTimeline(recorded.FindAll(e => !string.IsNullOrEmpty(e.Scroll) && !shifts.Exists(s => s.Covers(e)))),
                        HitTimeline(replayed.FindAll(e => !string.IsNullOrEmpty(e.Scroll) && !shifts.Exists(s => s.Covers(e)))), Slack, null);
                    if (rest.Count > 0)
                        report.ListWarnings.Add($"스크롤 목록 안의 대상이 녹화와 다른 프레임에 켜지고 꺼진 곳이 {rest.Count}곳 있는데 줄·목록 크기는 녹화와 같다 — 처음 프레임 {rest[0].frame} {rest[0].detail}");
                    recorded = recorded.FindAll(e => string.IsNullOrEmpty(e.Scroll));
                    replayed = replayed.FindAll(e => string.IsNullOrEmpty(e.Scroll));
                }
                else if (manifest.TraceVersion < 7 && otherLocale)
                {
                    // 재생에서 늘 목록 안에서만 켜진 이름
                    var inside = new HashSet<string>();
                    var outside = new HashSet<string>();
                    foreach (HitTrace.Event e in replayed) (string.IsNullOrEmpty(e.Scroll) ? outside : inside).Add(e.Name);
                    inside.ExceptWith(outside);
                    replayed = HitTrace.ByName(replayed);
                    List<(int frame, string detail)> rest = TimelineDiffs(HitTimeline(recorded.FindAll(e => inside.Contains(e.Name))), HitTimeline(replayed.FindAll(e => inside.Contains(e.Name))), Slack, null);
                    if (rest.Count > 0)
                        report.LayoutSkips.Add($"스크롤 목록 안의 대상이 녹화와 다른 프레임에 켜지고 꺼진 곳이 {rest.Count}곳 있다(이전 형식 기록이라 줄 크기는 견주지 못했다) — 처음 프레임 {rest[0].frame} {rest[0].detail}");
                    recorded = recorded.FindAll(e => !inside.Contains(e.Name));
                    replayed = replayed.FindAll(e => !inside.Contains(e.Name));
                }
                else if (manifest.TraceVersion < 7) replayed = HitTrace.ByName(replayed);
            }
            CompareTimeline("UI", HitTimeline(recorded), HitTimeline(replayed), Slack);
        }

        private static List<(string key, int frame)> HitTimeline(List<HitTrace.Event> events) =>
            Timeline(events, e => (TapProbe.Neutral(e.Name) + (e.Delta > 0 ? " 켜짐" : " 꺼짐"), e.Frame, Math.Abs(e.Delta)));

        // 난수 상태가 녹화와 어긋난 적이 있는데도 같은 난수 흐름으로 볼 수 있나: 마지막엔 다시 맞았고, 연달아 어긋난 프레임이 Slack 안이고,
        // 난수를 쓴 코드의 순서가 녹화와 같다(호출 기록이 한도까지 차 끝을 모르면 같다고 보지 않는다).
        internal static bool RandomTolerated(bool lastMatched, int longestRun, List<RandomTrace.Call> recorded, List<RandomTrace.Call> replayed)
        {
            if (!lastMatched || longestRun > Slack || recorded == null || replayed == null) return false;
            if (recorded.Count != replayed.Count || recorded.Count >= RandomTrace.MaxCalls) return false;
            for (int i = 0; i < recorded.Count; i++)
                if (recorded[i].Caller != replayed[i].Caller) return false;
            return true;
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

        // 같은 이름의 같은 순번끼리 맞춰, 프레임이 다르거나 한쪽에만 있는 것을 다름으로 남긴다.
        // slack: 같은 일이 이 프레임 수 안에 일어났으면 다름으로 치지 않고 참고(Slacks)로 남긴다. 한쪽에만 있는 것은 늘 다름이다.
        private static void CompareTimeline(string kind, List<(string key, int frame)> recorded, List<(string key, int frame)> replayed, int slack)
        {
            var near = new List<(int frame, string detail)>();
            foreach ((int frame, string detail) in TimelineDiffs(recorded, replayed, slack, near)) Differ(frame, kind, detail);
            foreach ((int frame, string detail) in near) Tolerated(frame, kind, detail);
        }

        // 두 타임라인에서 다른 곳(이른 쪽 프레임, 내용). slack 안의 프레임 차이는 near 에 따로 담는다(near 가 null 이면 버린다).
        private static List<(int frame, string detail)> TimelineDiffs(List<(string key, int frame)> recorded, List<(string key, int frame)> replayed, int slack, List<(int frame, string detail)> near)
        {
            var diffs = new List<(int frame, string detail)>();
            var queues = new Dictionary<string, Queue<int>>();
            foreach ((string key, int frame) in replayed)
            {
                if (!queues.TryGetValue(key, out Queue<int> q)) queues[key] = q = new Queue<int>();
                q.Enqueue(frame);
            }
            foreach ((string key, int frame) in recorded)
            {
                if (!queues.TryGetValue(key, out Queue<int> q) || q.Count == 0) { diffs.Add((frame, $"{key}: 녹화 {frame}, 재생에는 없음")); continue; }
                int got = q.Dequeue();
                if (got == frame) continue;
                (int frame, string detail) diff = (Math.Min(got, frame), $"{key}: 녹화 {frame}, 재생 {got} ({got - frame:+0;-0}프레임)");
                if (Math.Abs(got - frame) <= slack) near?.Add(diff);
                else diffs.Add(diff);
            }
            foreach (KeyValuePair<string, Queue<int>> pair in queues)
                foreach (int frame in pair.Value) diffs.Add((frame, $"{pair.Key}: 녹화에는 없음, 재생 {frame}"));
            return diffs;
        }

        public static Report Load(string directory, string pass)
        {
            string path = Path.Combine(directory, Folder, pass + ".json");
            return File.Exists(path) ? JsonUtility.FromJson<Report>(File.ReadAllText(path)) : null;
        }
    }
}
