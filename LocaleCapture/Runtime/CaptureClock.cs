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
        private static int _stallFrames;
        private static int _mainThread;
        private static double _dspBase;
        private static int _seed;
        private static System.Random _guid;
        private static SystemLanguage? _language;

        // seedScheme: new Random() 시드를 정하는 방식(ReplayManifest.SeedScheme). 재생은 기록 때 방식을 그대로 쓴다.
        internal static void Begin(long baseUtcTicks, int baseTickCount, int fps, int seed, SystemLanguage? language, int seedScheme)
        {
            _seedScheme = seedScheme;
            _seedFrame = -1;
            _randomCount = 0;
            _baseUtcTicks = baseUtcTicks;
            _baseTickCount = baseTickCount;
            _fps = Mathf.Max(1, fps);
            _frame = 0;
            _stallFrames = 0;
            _heldTotal = 0;
            _seed = seed;
            _seedCounts.Clear();
            Creations = new List<RandomCreation>();
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
            _stallFrames = 0;
        }

        // Task.Delay 가 끝나는 시계는 세션 프레임이다 — 재생에서 로딩이 늦어 게임 시간을 멈추고 기다리는 동안에는 같이 멈춘다
        // (그동안 흐르면 걸려 있던 대기가 기다린 프레임만큼 일찍 끝난다). 다만 로딩으로 가는 길에 그 대기가 있으면(흐름이 이미 녹화와
        // 달라졌을 때) 서로 기다리다 멈추므로, 대기가 오래 안 풀릴 때만 세션이 Stall 로 이 시계를 따로 돌려 풀어 준다.
        internal static void Stall() => _stallFrames++;

        internal static int Frame => _frame;
        internal static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThread;

        // 로딩 코드(Addressables·ResourceManager)가 보는 시계. 평소에는 게임 시계와 똑같다 — 에셋 데이터베이스 모드의 Addressables 는
        // "지금 + 지연" 시각에 로드를 끝내므로, 이 시계가 프레임과 같이 가야 로드가 녹화와 같은 프레임에 끝난다(대기 중에 따로 흘리면
        // 그동안 걸려 있던 로드가 그만큼 일찍 끝난다).
        // 다만 로딩 대기가 바로 그 지연 로드를 기다리는 것이면 시계가 서 있는 한 영영 안 끝난다(흐름이 이미 녹화와 달라졌을 때 생긴다).
        // 그런 대기가 오래 안 풀릴 때만 세션이 TickLoadClock 으로 이 시계를 따로 돌려 풀어 준다.
        private static int _heldTotal;
        internal static void TickLoadClock() => _heldTotal++;
        private static double LoadElapsed => (_frame + _heldTotal) / (double) _fps;
        public static float LoadUnscaledTime => Active ? (float) (1.0 + LoadElapsed) : Time.unscaledTime;
        public static double LoadUnscaledTimeAsDouble => Active ? 1.0 + LoadElapsed : Time.unscaledTimeAsDouble;
        public static float LoadUnscaledDeltaTime => Active ? Time.captureDeltaTime : Time.unscaledDeltaTime;
        public static float LoadRealtimeSinceStartup => Active ? (float) (1.0 + LoadElapsed) : Time.realtimeSinceStartup;
        public static double LoadRealtimeSinceStartupAsDouble => Active ? 1.0 + LoadElapsed : Time.realtimeSinceStartupAsDouble;

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
        // 세션 동안 게임은 늘 포커스가 있다고 본다(에디터 패치가 OnApplicationFocus·OnApplicationPause 알림도 막는다).
        public static bool IsFocused => Active || Application.isFocused;

        // Task.Delay 대신. 실제 시계가 아니라 세션 프레임으로 끝난다 — 녹화(인코딩)로 프레임이 느려져도 같은 프레임에 깬다.
        // 세션 Update 가 Release 로 때가 된 것을 푼다.
        // frame·stall: 대기를 건 세션 프레임과 그때까지 따로 돌린 프레임 수 — 시계를 따로 돌리는 중에 건 대기는 그 뒤로 돌린 만큼만 앞당겨진다.
        private static readonly List<(int due, int frame, int stall, TaskCompletionSource<bool> tcs)> _delays =
            new List<(int, int, int, TaskCompletionSource<bool>)>();

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
            // 프레임 수는 long 으로 — Task.Delay(int.MaxValue)("취소될 때까지 대기") 같은 긴 대기가 int 곱셈에서 넘쳐 한 프레임 만에 끝나지 않게.
            long frames = Math.Max(1L, (long) Math.Ceiling(millisecondsDelay * (double) _fps / 1000.0));
            int due = (int) Math.Min(int.MaxValue, _frame + frames);   // 세션 프레임 기준 — 시계를 따로 돌리는 중에 만든 대기도 그만큼 늦어지지 않는다
            lock (_delays) _delays.Add((due, _frame, _stallFrames, tcs));
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
                    int turned = _delays[i].frame == _frame ? _stallFrames - _delays[i].stall : _stallFrames;
                    if (!all && _delays[i].due > _frame + turned) continue;
                    (ready ??= new List<TaskCompletionSource<bool>>()).Add(_delays[i].tcs);
                    _delays.RemoveAt(i);
                }
            if (ready == null) return;
            for (int i = ready.Count - 1; i >= 0; i--) ready[i].TrySetResult(true);   // 등록 순서대로
        }

        // new Random() 대신. 시드는 "누가 만들었나 + 그 코드가 만든 몇 번째인가" 로 정한다.
        //  · 프레임 번호로 정하면 흐름이 한 프레임만 밀려도 시드가 달라져 덱 셔플·뽑기가 통째로 바뀐다(작은 어긋남이 다른 플레이가 된다).
        //  · 전역 순번이면 에디터 쪽(창·스크립트 컴파일)이 기록 중에 만든 Random 하나에 그 뒤 게임 시드가 전부 밀린다.
        // 다른 스레드는 시점이 매번 달라 손대지 않는다.
        public static System.Random NewRandom() => TryNextSeed(CreatorOf(1), out int seed) ? new System.Random(seed) : new System.Random();

        [Serializable]
        public struct RandomCreation
        {
            public int Frame;
            public string Creator;   // new Random() 을 부른 게임 코드 "타입.메서드"
        }

        // 이번 세션에서 new Random() 이 만들어진 프레임과 만든 코드. 기록과 재생을 비교해 흐름이 밀린 첫 지점을 찾는다.
        public static List<RandomCreation> Creations { get; private set; } = new List<RandomCreation>();
        private const int MaxCreations = 4000;
        private static readonly Dictionary<string, int> _seedCounts = new Dictionary<string, int>();

        public static bool TryNextSeed(string creator, out int seed)
        {
            seed = 0;
            if (!Active || Thread.CurrentThread.ManagedThreadId != _mainThread) return false;
            creator ??= "?";
            if (Creations.Count < MaxCreations) Creations.Add(new RandomCreation { Frame = _frame, Creator = creator });
            if (_seedScheme == 0)
            {
                // 이전 기록: 같은 프레임의 몇 번째인지로 정했다. 그 기록을 다시 뽑을 때만 쓴다.
                if (_seedFrame != _frame) { _seedFrame = _frame; _randomCount = 0; }
                seed = unchecked((_seed * 31 + _frame) * 31 + 17 * _randomCount++);
                return true;
            }
            _seedCounts.TryGetValue(creator, out int count);
            _seedCounts[creator] = count + 1;
            uint hash = 2166136261;   // FNV-1a — string.GetHashCode 는 실행마다 다를 수 있다
            foreach (char c in creator) hash = (hash ^ c) * 16777619;
            seed = unchecked((int) ((uint) _seed * 31 + hash) * 31 + 17 * count);
            return true;
        }

        public const int CurrentSeedScheme = 1;
        private static int _seedScheme = CurrentSeedScheme;
        private static int _seedFrame = -1;
        private static int _randomCount;

        // 호출 스택에서 이 도구·시스템·Unity 를 뺀 첫 코드 "타입.메서드". skip: 건너뛸 이쪽 프레임 수.
        // 한 단계만 본다 — 위쪽 호출자는 await 이 바로 끝났는지 다음 프레임에 이어졌는지에 따라 달라진다.
        public static string CreatorOf(int skip)
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(skip + 1, false);
                for (int i = 0; i < trace.FrameCount; i++)
                {
                    System.Reflection.MethodBase m = trace.GetFrame(i).GetMethod();
                    Type t = m?.DeclaringType;
                    if (t == null) continue;
                    string asm = t.Assembly.GetName().Name;
                    if (asm == "mscorlib" || asm.StartsWith("System") || asm == "0Harmony" || asm.StartsWith("CookApps.LocaleCapture") || asm.StartsWith("UnityEngine")) continue;
                    string type = t.FullName ?? t.Name;
                    string method = m.Name;
                    int nested = type.IndexOf('+');   // async·람다·반복자는 바깥 타입.메서드로
                    if (nested > 0 && type.IndexOf('<', nested) > 0)
                    {
                        int open = type.IndexOf('<', nested), close = type.IndexOf('>', open);
                        if (close > open + 1) method = type.Substring(open + 1, close - open - 1);
                        type = type.Substring(0, nested);
                    }
                    int generic = type.IndexOf('[');   // 닫힌 제네릭 인자는 뗀다
                    if (generic > 0) type = type.Substring(0, generic);
                    int patch = method.LastIndexOf("_Patch", StringComparison.Ordinal);   // Harmony 가 바꾼 메서드 이름
                    if (patch > 0) method = method.Substring(0, patch);
                    int dot = method.LastIndexOf('.');
                    if (dot >= 0 && dot < method.Length - 1) method = method.Substring(dot + 1);
                    return type + "." + method;
                }
            }
            catch { /* 스택을 못 읽으면 이름 없이 */ }
            return "?";
        }

        public static Guid NewGuid()
        {
            // 메인 스레드만 — 작업 스레드가 섞여 뽑으면 메인 스레드가 받는 순서가 실행마다 달라지고, 같은 Random 을 동시에 쓰면 상태가 깨진다.
            if (!Active || !OnMainThread) return Guid.NewGuid();
            var bytes = new byte[16];
            _guid.NextBytes(bytes);
            bytes[7] = (byte) ((bytes[7] & 0x0F) | 0x40);   // 버전 4 모양
            return new Guid(bytes);
        }
    }
}
