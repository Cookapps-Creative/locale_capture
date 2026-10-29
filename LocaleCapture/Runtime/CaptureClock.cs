using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 녹화·재생 중에 프로젝트 코드가 보는 시간·난수·기기 언어. 에디터가 프로젝트 코드의 호출을 이쪽으로 바꿔 끼운다
    // (DateTime.Now → Now 등). 시간은 "기록 시작 시각 + 세션 프레임 / fps" 라서 기록과 모든 재생에서 같은 값이 나온다.
    // 세션이 없으면 원래 값을 그대로 돌려준다.
    public static class CaptureClock
    {
        public static bool Active { get; private set; }

        private static long _baseUtcTicks;
        private static int _baseTickCount;
        private static int _fps = 30;
        private static int _frame;
        private static int _heldFrames;
        private static int _mainThread;
        private static double _dspBase;
        private static int _seed;
        private static int _randomCount;
        private static System.Random _guid;
        private static SystemLanguage? _language;

        internal static void Begin(long baseUtcTicks, int baseTickCount, int fps, int seed, SystemLanguage? language)
        {
            _baseUtcTicks = baseUtcTicks;
            _baseTickCount = baseTickCount;
            _fps = Mathf.Max(1, fps);
            _frame = 0;
            _seed = seed;
            _randomCount = 0;
            _seedFrame = -1;
            _guid =new System.Random(unchecked(seed * 7 + 3));
            _language = language;
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _dspBase = AudioSettings.dspTime;
            UnityEngine.Random.InitState(seed);
            Active = true;
        }

        internal static void SetFrame(int frame)
        {
            _frame = frame;
            _heldFrames = 0;
        }

        // 로딩 대기로 게임 시간이 멈춘 프레임. 이때도 Task.Delay 는 흐르게 한다 — 로딩으로 가는 길에 대기가 있으면 서로 기다리다 멈춘다.
        internal static void Hold() => _heldFrames++;

        internal static void End()
        {
            Active = false;
            ReleaseDelays(true);
            _language = null;
        }

        private static double Elapsed => _frame / (double) _fps;

        public static DateTime UtcNow => Active ? new DateTime(_baseUtcTicks, DateTimeKind.Utc).AddSeconds(Elapsed) : DateTime.UtcNow;
        public static DateTime Now => Active ? UtcNow.ToLocalTime() : DateTime.Now;
        public static DateTime Today => Now.Date;
        public static DateTimeOffset OffsetUtcNow => Active ? new DateTimeOffset(UtcNow) : DateTimeOffset.UtcNow;
        public static DateTimeOffset OffsetNow => Active ? new DateTimeOffset(Now) : DateTimeOffset.Now;

        // Stopwatch.GetTimestamp 대신(실시간 대기·타이머가 게임 시간과 같이 가게).
        public static long GetTimestamp() => Active
            ? (long) ((1.0 + Elapsed) * System.Diagnostics.Stopwatch.Frequency)
            : System.Diagnostics.Stopwatch.GetTimestamp();
        public static int TickCount => Active ? unchecked(_baseTickCount + (int) (Elapsed * 1000)) : Environment.TickCount;
        public static float RealtimeSinceStartup => Active ? (float) (1.0 + Elapsed) : Time.realtimeSinceStartup;
        public static double RealtimeSinceStartupAsDouble => Active ? 1.0 + Elapsed : Time.realtimeSinceStartupAsDouble;
        // UI 연출·일시정지 무시 대기가 쓰는 unscaled 시간도 세션 프레임으로.
        public static float UnscaledDeltaTime => Active ? Time.captureDeltaTime : Time.unscaledDeltaTime;
        public static float UnscaledTime => Active ? (float) (1.0 + Elapsed) : Time.unscaledTime;
        public static double UnscaledTimeAsDouble => Active ? 1.0 + Elapsed : Time.unscaledTimeAsDouble;
        // 소리 시계. 재생은 오디오를 프레임마다 렌더링해 엔진 dspTime 도 프레임을 따라가지만, 기록은 실제 시간으로 흘러
        // 소리에 맞춰 움직이는 연출(로고 등)이 느린 프레임에서 앞서 나간다. 세션 시작 값 + 프레임 시간으로 맞춘다.
        public static double DspTime => Active ? _dspBase + Elapsed : AudioSettings.dspTime;

        // 오디오를 프레임마다 렌더링하기 시작한 직후 부른다 — 이후 엔진 dspTime 과 같은 값을 내 PlayScheduled 가 어긋나지 않게.
        internal static void SyncDsp() => _dspBase = AudioSettings.dspTime - Elapsed;
        public static SystemLanguage SystemLanguage => Active && _language.HasValue ? _language.Value : Application.systemLanguage;

        // Task.Delay 대신. 실제 시계가 아니라 세션 프레임으로 끝난다 — 녹화(인코딩)로 프레임이 느려져도 같은 프레임에 깬다.
        // 세션 Update 가 Release 로 때가 된 것을 푼다.
        private static readonly List<(int due, TaskCompletionSource<bool> tcs)> _delays = new List<(int, TaskCompletionSource<bool>)>();

        public static Task Delay(int millisecondsDelay) => DelayToken(millisecondsDelay, CancellationToken.None);
        public static Task DelaySpan(TimeSpan delay) => DelayToken((int) delay.TotalMilliseconds, CancellationToken.None);
        public static Task DelaySpanToken(TimeSpan delay, CancellationToken token) => DelayToken((int) delay.TotalMilliseconds, token);

        public static Task DelayToken(int millisecondsDelay, CancellationToken token)
        {
            // 다른 스레드의 대기는 실제 시간 그대로 — 메인 스레드가 그 대기를 막고 기다리면 프레임이 안 넘어가 멈춘다.
            if (!Active || millisecondsDelay < 0 || Thread.CurrentThread.ManagedThreadId != _mainThread) return Task.Delay(millisecondsDelay, token);
            if (token.IsCancellationRequested) return Task.FromCanceled(token);
            if (millisecondsDelay == 0) return Task.CompletedTask;
            var tcs = new TaskCompletionSource<bool>();
            int due = _frame + _heldFrames + Math.Max(1, (int) Math.Ceiling(millisecondsDelay * _fps / 1000.0));
            lock (_delays) _delays.Add((due, tcs));
            if (token.CanBeCanceled) token.Register(() => tcs.TrySetCanceled(token));
            return tcs.Task;
        }

        // all: 세션이 끝날 때 남은 대기를 모두 푼다(게임이 멈춰 있지 않게).
        internal static void ReleaseDelays(bool all)
        {
            List<TaskCompletionSource<bool>> ready = null;
            lock (_delays)
                for (int i = _delays.Count - 1; i >= 0; i--)
                {
                    if (!all && _delays[i].due > _frame + _heldFrames) continue;
                    (ready ??= new List<TaskCompletionSource<bool>>()).Add(_delays[i].tcs);
                    _delays.RemoveAt(i);
                }
            if (ready == null) return;
            for (int i = ready.Count - 1; i >= 0; i--) ready[i].TrySetResult(true);   // 등록 순서대로
        }

        // new Random() 대신. 같은 프레임의 몇 번째인지로 시드를 정한다 — 전역 순번이면 에디터 쪽(창·스크립트 컴파일)이
        // 기록 중에 만든 Random 하나에 그 뒤 게임 시드가 전부 밀린다. 다른 스레드는 시점이 매번 달라 손대지 않는다.
        public static System.Random NewRandom() => TryNextSeed(out int seed) ? new System.Random(seed) : new System.Random();

        public static bool TryNextSeed(out int seed)
        {
            seed = 0;
            if (!Active || Thread.CurrentThread.ManagedThreadId != _mainThread) return false;
            if (_seedFrame != _frame) { _seedFrame = _frame; _randomCount = 0; }
            seed = unchecked((_seed * 31 + _frame) * 31 + 17 * _randomCount++);
            return true;
        }

        private static int _seedFrame = -1;

        public static Guid NewGuid()
        {
            if (!Active) return Guid.NewGuid();
            var bytes = new byte[16];
            _guid.NextBytes(bytes);
            bytes[7] = (byte) ((bytes[7] & 0x0F) | 0x40);   // 버전 4 모양
            return new Guid(bytes);
        }
    }
}
