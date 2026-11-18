using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace CookApps.LocaleCapture
{
    // 프로젝트 코드가 쓰는 Stopwatch 를 세션 시계(프레임)로 돌린다. 에디터가 Stopwatch 호출을 이쪽으로 바꿔 끼운다.
    // Stopwatch 는 실제 시간을 재므로, "이번 프레임에 몇 ms 썼으면 다음 프레임으로 넘긴다" 같은 분할 처리나 경과 시간 대기는
    // 기기 부하에 따라 끝나는 프레임이 달라진다 — 기록과 재생, 빠른 재생과 실제 속도 재생이 서로 다른 프레임에 끝난다.
    // 세션 시계는 한 프레임 안에서는 흐르지 않으므로 그런 코드는 늘 같은 프레임에 같은 만큼 일한다.
    //  · 세션 중 메인 스레드에서 시작한 Stopwatch 만 바꾼다. 그 전에 시작했거나 다른 스레드의 것은 실제 시간 그대로.
    //  · 실제 Stopwatch 도 함께 돌려, 세션이 끝난 뒤 읽으면 실제 값이 나온다.
    public static class CaptureStopwatch
    {
        private sealed class State
        {
            public long Start;      // 시작한 때의 세션 타임스탬프
            public long Elapsed;    // 멈춘 구간까지 쌓인 값
            public bool Running;
            public bool Real;       // 한 프레임 안에서 값이 바뀌길 기다리며 도는 코드 — 실제 시간으로 돌려준다
            public int Frame = -1, Reads;
        }

        // 한 프레임 안에서 이만큼 읽으면 경과 시간을 기다리며 도는 코드로 본다(세션 시계는 프레임 안에서 안 흘러 영영 못 빠져나온다).
        private const int SpinReads = 200000;
        private static readonly ConditionalWeakTable<Stopwatch, State> _states = new ConditionalWeakTable<Stopwatch, State>();

        private static bool Session => CaptureClock.Active && CaptureClock.OnMainThread;

        public static Stopwatch StartNew()
        {
            var sw = new Stopwatch();
            Start(sw);
            return sw;
        }

        public static void Start(Stopwatch sw)
        {
            sw.Start();
            if (!Session) { _states.Remove(sw); return; }   // 세션 밖·다른 스레드에서 만진 것은 실제 시간으로만
            State s = _states.GetOrCreateValue(sw);
            if (s.Running) return;
            s.Start = CaptureClock.GetTimestamp();
            s.Running = true;
        }

        public static void Stop(Stopwatch sw)
        {
            sw.Stop();
            if (!Session) { _states.Remove(sw); return; }
            if (!_states.TryGetValue(sw, out State s) || !s.Running) return;
            s.Elapsed += CaptureClock.GetTimestamp() - s.Start;
            s.Running = false;
        }

        public static void Reset(Stopwatch sw)
        {
            sw.Reset();
            if (!Session) { _states.Remove(sw); return; }
            if (!_states.TryGetValue(sw, out State s)) return;
            s.Elapsed = 0;
            s.Running = false;
        }

        public static void Restart(Stopwatch sw)
        {
            sw.Restart();
            if (!Session) { _states.Remove(sw); return; }
            State s = _states.GetOrCreateValue(sw);
            s.Elapsed = 0;
            s.Start = CaptureClock.GetTimestamp();
            s.Running = true;
        }

        public static bool IsRunning(Stopwatch sw) => sw.IsRunning;

        public static long ElapsedTicks(Stopwatch sw) => TryTicks(sw, out long ticks) ? ticks : sw.ElapsedTicks;

        public static TimeSpan Elapsed(Stopwatch sw) =>
            TryTicks(sw, out long ticks) ? TimeSpan.FromTicks((long) (ticks * (double) TimeSpan.TicksPerSecond / Stopwatch.Frequency)) : sw.Elapsed;

        public static long ElapsedMilliseconds(Stopwatch sw) =>
            TryTicks(sw, out long ticks) ? (long) (ticks * 1000.0 / Stopwatch.Frequency) : sw.ElapsedMilliseconds;

        // 세션 시계로 잰 경과(Stopwatch 틱). 세션 밖이거나 이 Stopwatch 를 세션에서 시작하지 않았으면 false.
        private static bool TryTicks(Stopwatch sw, out long ticks)
        {
            ticks = 0;
            if (!Session || !_states.TryGetValue(sw, out State s) || s.Real) return false;
            int frame = CaptureClock.Frame;
            if (s.Frame != frame) { s.Frame = frame; s.Reads = 0; }
            if (++s.Reads > SpinReads)
            {
                s.Real = true;
                UnityEngine.Debug.LogWarning("[LocaleCapture] 한 프레임 안에서 Stopwatch 경과를 기다리며 도는 코드가 있어 그 Stopwatch 는 실제 시간으로 돌려준다 — 이 구간은 재생마다 다른 프레임에 끝날 수 있다: "
                                             + CaptureClock.CreatorOf(2));
                return false;
            }
            ticks = s.Elapsed + (s.Running ? CaptureClock.GetTimestamp() - s.Start : 0);
            return true;
        }
    }
}
