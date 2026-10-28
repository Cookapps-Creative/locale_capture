using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 어긋남 검사. 재생이 기록과 같은 플레이였는지 세 가지로 본다.
    //  · 상태: 매 프레임 끝의 UnityEngine.Random 상태 — 게임 로직이 다른 길로 가면 난수 소비가 달라져 여기서 먼저 드러난다
    //  · 서버: 요청 순서·프레임이 기록과 같은지, 기록에 없는 요청이나 안 쓰인 응답이 있는지
    //  · 로딩: 기록 때 끝났던 로딩이 재생에서도 끝났는지
    // 결과는 기록 폴더의 verify/<패스>.json 에 진단용으로 남는다. 창에는 패스끼리 같은 플레이였는지(StateHash 비교)만 보인다.
    public static class CaptureVerify
    {
        public const string Folder = "verify";
        private const int MaxIssues = 40;

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
            public int ServerReplayed, ServerLive, ServerMissed;
            public int InputQueued, InputDelivered;   // 재생한 입력 수 / 게임 입력 시스템이 처리한 수
            public string InputDevices;
            public List<string> TapMismatches = new List<string>();   // 녹화와 다른 대상을 누른 곳
            public List<string> UiMismatches = new List<string>();    // 녹화와 다른 프레임에 열리고 닫힌 화면
            public List<int> FrameMs = new List<int>();               // 재생 프레임마다 실제로 걸린 ms
            public int HeldFrames;                         // 로딩을 기다리느라 더 돈 프레임
            public List<string> HeldOn = new List<string>();
            public List<string> Shifts = new List<string>();   // 기록과 다른 프레임에 끝난 로딩 "신호 기록→재생"
            public int StateMismatchFrames;
            public string StateHash;   // 재생 내내의 난수 상태 요약 — 패스끼리 같으면 같은 플레이
            public List<Issue> Issues = new List<Issue>();
        }

        private static Report _report;
        private static ulong _hash;
        private static List<RandomTrace.Call> _recordedCalls;
        private static List<TapProbe.Tap> _recordedTaps, _replayedTaps;
        private static List<UiTrace.Event> _recordedUi, _replayedUi;
        private static int _firstMismatch = -1;
        private static bool _lastMatched = true;

        internal static void Begin(string pass, string locale, List<RandomTrace.Call> recordedCalls, List<TapProbe.Tap> recordedTaps, List<TapProbe.Tap> replayedTaps,
            List<UiTrace.Event> recordedUi, List<UiTrace.Event> replayedUi)
        {
            _recordedUi = recordedUi;
            _replayedUi = replayedUi;
            _recordedTaps = recordedTaps;
            _replayedTaps = replayedTaps;
            _report = new Report { Pass = pass, Locale = locale };
            _hash = 14695981039346656037UL;
            _recordedCalls = recordedCalls;
            _firstMismatch = -1;
            _lastMatched = true;
        }

        public static void Add(int frame, string kind, string detail)
        {
            if (_report == null) return;
            _report.Ok = false;
            if (_report.Issues.Count < MaxIssues) _report.Issues.Add(new Issue { Frame = frame, Kind = kind, Detail = detail });
        }

        internal static void CheckState(int frame, List<int> recordedStates)
        {
            if (_report == null) return;
            foreach (int v in RandomStates.Current()) _hash = (_hash ^ (uint) v) * 1099511628211UL;
            _lastMatched = RandomStates.Matches(recordedStates, frame);
            if (_lastMatched) return;
            _report.StateMismatchFrames++;
            if (_firstMismatch < 0) _firstMismatch = frame;
        }

        internal static void Held(string marker)
        {
            if (_report == null) return;
            _report.HeldFrames++;
            if (_report.HeldOn.Count < 10 && !_report.HeldOn.Contains(marker)) _report.HeldOn.Add(marker);
        }

        internal static void FrameTime(int ms) => _report?.FrameMs.Add(ms);

        internal static void Input(int queued, int delivered, string devices)
        {
            if (_report == null) return;
            _report.InputQueued = queued;
            _report.InputDelivered = delivered;
            _report.InputDevices = devices;
            if (delivered < queued) Add(0, "입력", $"재생한 입력 {queued}개 중 {delivered}개만 게임에 들어갔다 ({devices})");
        }

        internal static void Arrived(string marker, int recordedFrame, int frame)
        {
            if (_report == null || frame == recordedFrame || _report.Shifts.Count >= 10) return;
            _report.Shifts.Add($"{marker} {recordedFrame}→{frame}");
        }

        internal static Report End(string directory, int frames)
        {
            Report report = _report;
            if (report == null) return null;
            List<RandomTrace.Call> replayedCalls = RandomTrace.End();
            report.TapMismatches = TapProbe.Compare(_recordedTaps, _replayedTaps);
            if (report.TapMismatches.Count > 0) Add(0, "터치", report.TapMismatches[0]);
            report.UiMismatches = UiTrace.Compare(_recordedUi, _replayedUi);
            if (report.UiMismatches.Count > 0) Add(0, "화면", report.UiMismatches[0]);
            if (_firstMismatch >= 0)
                Add(_firstMismatch, "상태", (_lastMatched
                    ? $"게임 난수 상태가 {report.StateMismatchFrames}프레임 동안 기록과 달랐다가 다시 맞았다 — 그 즈음 게임이 프레임 하나 밀렸다(로딩 대기 {report.HeldFrames}프레임: {string.Join(", ", report.HeldOn)})"
                    : $"게임 난수 상태가 이 프레임부터 끝까지 기록과 다르다 — 플레이가 갈라졌다")
                    + $" | 난수 호출 기록 [{RandomTrace.Around(_recordedCalls, _firstMismatch)}] 재생 [{RandomTrace.Around(replayedCalls, _firstMismatch)}]"
                    + (report.Shifts.Count > 0 ? $" | 로딩 프레임 차이 [{string.Join(", ", report.Shifts)}]" : ""));
            _report = null;
            report.Frames = frames;
            report.StateHash = _hash.ToString("x16");
            report.ServerReplayed = NetworkTape.Replayed;
            report.ServerLive = NetworkTape.Live;
            report.ServerMissed = NetworkTape.Missed;
            try
            {
                string dir = Path.Combine(directory, Folder);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, report.Pass + ".json"), JsonUtility.ToJson(report, true));
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 검사 결과 저장 실패: " + e.Message); }
            return report;
        }

        public static Report Load(string directory, string pass)
        {
            string path = Path.Combine(directory, Folder, pass + ".json");
            return File.Exists(path) ? JsonUtility.FromJson<Report>(File.ReadAllText(path)) : null;
        }
    }
}
