using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEditor.Compilation;
using UnityEngine;
using UnityEngine.Networking;

namespace CookApps.LocaleCapture.Editor
{
    // 녹화·재생 동안만 거는 런타임 패치(Harmony). 게임 소스는 고치지 않는다.
    //  · 프로젝트 코드(Assets 아래 소스로 된 어셈블리): 시계(DateTime·TickCount)·Guid·new Random() → CaptureClock
    //  · 모든 스크립트 어셈블리(패키지·Assets 의 DLL 포함, Input System·이 도구 제외): 실시간(realtimeSinceStartup·Stopwatch·unscaled 시간·Task.Delay·dspTime)·기기 언어·예전 입력 → CaptureClock·CaptureInput, 화면 안전 영역·DPI·노치 → CaptureScreen(기록 값)
    //    (시계를 패키지까지 바꾸지 않는 건 네트워크 서명 같은 실제 시간이 필요한 곳을 건드리지 않기 위해서)
    //  · 모든 스크립트 어셈블리: UnityEngine.Random 호출 → RandomTrace(누가 난수를 썼는지, 어긋남 검사용)
    //  · System.Random() 생성자: 제네릭 메서드 안처럼 호출 위치를 못 바꾸는 곳까지 같은 시드로
    //  · 프로젝트 코드의 Stopwatch(Start·Elapsed 등) → CaptureStopwatch(세션 시계). 프레임당 ms 예산으로 일을 나누는 코드가 재생마다 같은 프레임에 끝나게
    //  · 로딩 코드(Unity.ResourceManager·Unity.Addressables)의 실시간·unscaled 시간 → 로딩 대기 중에도 흐르는 시계(CaptureClock.Load*)
    //  · HttpClient·UnityWebRequest → NetworkTape, AsyncOperation 완료 → CaptureMarkers
    //  · Unity 동기화 컨텍스트 Post·UniTask AddContinuation(다른 스레드에서 부른 것만) → ThreadGate
    //  · Addressables AssetDatabaseProvider 생성자 → 에디터 전용 가짜 로딩 지연을 0 으로
    //  · Addressables ResourceManager.ExecuteDeferredCallbacks → 미뤄진 완료 콜백을 같은 프레임에 이어서 불러 연쇄 로딩을 한 프레임에
    internal static class CapturePatches
    {
        private const string Id = "com.cookapps.localecapture";
        private static Harmony _harmony;
        private static Dictionary<MethodBase, MethodInfo> _full, _shared, _loader;

        public static bool Installed => _harmony != null;
        public static int PatchedMethods { get; private set; }
        public static readonly List<string> Problems = new List<string>();
        // 그중 핵심 패치(시계·난수 생성자·통신·로딩 등) 실패. 개별 메서드(제네릭·예외 블록 등 못 바꾸는 곳)는 Problems 에만 남는다.
        public static readonly List<string> CoreProblems = new List<string>();
        // 못 바꾼 개별 메서드 중 재현에 영향을 줄 수 있는 것: 프로젝트가 직접 쓴 코드인데 시계·난수 생성·입력 같은 호출이 그대로 남은 곳,
        // 소리 코드의 난수(게임 난수와 갈라 두지 못한다). 게임 코드의 UnityEngine.Random 만 남은 곳은 결과가 같아 넣지 않고,
        // 패키지·Assets/Plugins 의 코드는 목록 파일에만 남긴다.
        public static readonly List<string> RiskyProblems = new List<string>();
        public static double InstallMs { get; private set; }

        public static void Install()
        {
            if (_harmony != null) return;
            Problems.Clear();
            CoreProblems.Clear();
            RiskyProblems.Clear();
            PatchedMethods = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _harmony = new Harmony(Id);
            // 패치 코드를 DynamicMethod 로 만든다. 기본(MethodBuilder)은 미완성 타입이 든 동적 어셈블리를 남겨,
            // 모든 어셈블리의 GetTypes() 를 도는 게임 코드(디버그 패널 등)가 ReflectionTypeLoadException 으로 죽는다.
            string dmdType = Environment.GetEnvironmentVariable("MONOMOD_DMD_TYPE");
            Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", "dynamicmethod");
            try { InstallAll(); }
            finally { Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", dmdType); }
            InstallMs = watch.Elapsed.TotalMilliseconds;
            Debug.Log($"[LocaleCapture] 런타임 패치 {PatchedMethods}곳 ({InstallMs:F0}ms)" +
                      (Problems.Count > 0
                          ? $", 못 바꾼 곳 {Problems.Count}건(재현에 영향 줄 수 있는 것 {RiskyProblems.Count + CoreProblems.Count}건): {string.Join(" / ", CoreProblems.Concat(RiskyProblems).Concat(Problems).Take(5))}"
                          : ""));
        }

        private static void InstallAll()
        {
            BuildMaps();
            TargetCache.Load();
            PatchCallSites();
            TargetCache.Save();
            // 코루틴 실시간 대기는 엔진 어셈블리 안에서 실제 시계를 읽는다(호출 위치 패치가 닿지 않는다) — 그 안쪽도 세션 시계로.
            Try("WaitForSecondsRealtime", () => _harmony.Patch(AccessTools.PropertyGetter(typeof(WaitForSecondsRealtime), nameof(WaitForSecondsRealtime.keepWaiting))
                    ?? throw new MissingMethodException("WaitForSecondsRealtime.keepWaiting"),
                transpiler: new HarmonyMethod(typeof(CapturePatches), nameof(TranspileShared))));
            Try("System.Random 생성자", () => _harmony.Patch(AccessTools.Constructor(typeof(System.Random), Type.EmptyTypes),
                postfix: new HarmonyMethod(typeof(CapturePatches), nameof(RandomPostfix))));
            Try("HttpMessageInvoker.SendAsync", () => PatchHttp(typeof(HttpMessageInvoker).GetMethod("SendAsync", new[] { typeof(HttpRequestMessage), typeof(CancellationToken) })));
            Try("HttpClient.SendAsync", () => PatchHttp(typeof(HttpClient).GetMethod("SendAsync", new[] { typeof(HttpRequestMessage), typeof(HttpCompletionOption), typeof(CancellationToken) })));
            Try("UnityWebRequest.SendWebRequest", () => _harmony.Patch(typeof(UnityWebRequest).GetMethod("SendWebRequest", Type.EmptyTypes),
                prefix: new HarmonyMethod(typeof(CapturePatches), nameof(WebPrefix)), postfix: new HarmonyMethod(typeof(CapturePatches), nameof(WebPostfix))));
            Try("AsyncOperation 완료", () => _harmony.Patch(AccessTools.Method(typeof(AsyncOperation), "InvokeCompletionEvent"),
                prefix: new HarmonyMethod(typeof(CapturePatches), nameof(AsyncPrefix))));
            Try("씬 로드 시작", () => _harmony.Patch(AccessTools.Method(typeof(UnityEngine.SceneManagement.SceneManager), "LoadSceneAsyncNameIndexInternal")
                    ?? throw new MissingMethodException("SceneManager.LoadSceneAsyncNameIndexInternal"),
                postfix: new HarmonyMethod(typeof(CapturePatches), nameof(SceneLoadPostfix))));
            // 에디터에서 Addressables "Use Asset Database" 모드는 씬을 이쪽으로 싣는다
            Try("에디터 씬 로드 시작", () => _harmony.Patch(AccessTools.Method(typeof(UnityEditor.SceneManagement.EditorSceneManager), "LoadSceneInPlayModeInternal")
                    ?? throw new MissingMethodException("EditorSceneManager.LoadSceneInPlayModeInternal"),
                postfix: new HarmonyMethod(typeof(CapturePatches), nameof(SceneLoadPostfix))));
            // Addressables 2.x 는 진단 콜백 API 가 없다(AddressablesProbe 가 아무것도 못 건다). 완료 콜백을 모아서 부르는 자리에서 개수를 센다 —
            // 언어 테이블·에셋 로딩이 끝나는 프레임을 동기 지점("addr")으로 남겨, 재생에서 로딩이 느려도 게임 시간을 멈추고 기다린다.
            Type resourceManager = AccessTools.TypeByName("UnityEngine.ResourceManagement.ResourceManager");
            if (resourceManager != null)
                Try("Addressables 완료", () => _harmony.Patch(AccessTools.Method(resourceManager, "ExecuteDeferredCallbacks")
                        ?? throw new MissingMethodException("ResourceManager.ExecuteDeferredCallbacks"),
                    prefix: new HarmonyMethod(typeof(CapturePatches), nameof(DeferredCallbacksPrefix)),
                    postfix: new HarmonyMethod(typeof(CapturePatches), nameof(DeferredCallbacksPostfix))));
            // Addressables 에셋 데이터베이스 모드의 가짜 로딩 지연 → 없앤다(CaptureBoot.NoLoadDelay). 제공자가 만들어질 때마다 지연을 0 으로.
            Type assetDatabaseProvider = AccessTools.TypeByName("UnityEngine.ResourceManagement.ResourceProviders.AssetDatabaseProvider");
            if (assetDatabaseProvider != null)
                Try("Addressables 가짜 로딩 지연", () =>
                {
                    _loadDelay = AccessTools.Field(assetDatabaseProvider, "m_LoadDelay") ?? throw new MissingFieldException("AssetDatabaseProvider.m_LoadDelay");
                    foreach (ConstructorInfo ctor in assetDatabaseProvider.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                        _harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(CapturePatches), nameof(AssetProviderPostfix)));
                });
            // 작업 스레드에서 메인 스레드로 넘어오는 이어받기(await Task.Run, UniTask.RunOnThreadPool 등) → ThreadGate 가 녹화 프레임에 넘긴다
            Type unityContext = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine.UnitySynchronizationContext");
            if (unityContext != null)
                Try("동기화 컨텍스트 Post", () => _harmony.Patch(AccessTools.Method(unityContext, "Post", new[] { typeof(SendOrPostCallback), typeof(object) })
                        ?? throw new MissingMethodException("UnitySynchronizationContext.Post"),
                    prefix: new HarmonyMethod(typeof(CapturePatches), nameof(ContextPostPrefix))));
            Type uniTaskLoop = AccessTools.TypeByName("Cysharp.Threading.Tasks.PlayerLoopHelper");
            MethodInfo addContinuation = uniTaskLoop == null ? null : AccessTools.Method(uniTaskLoop, "AddContinuation");
            if (addContinuation != null)
                Try("UniTask AddContinuation", () => _harmony.Patch(addContinuation, prefix: new HarmonyMethod(typeof(CapturePatches), nameof(UniTaskContinuationPrefix))));
            PropertyInfo activation = typeof(AsyncOperation).GetProperty(nameof(AsyncOperation.allowSceneActivation));
            Try("씬 활성화 지연", () =>
            {
                _harmony.Patch(activation.GetSetMethod(), prefix: new HarmonyMethod(typeof(CapturePatches), nameof(ActivationSetPrefix)));
                _harmony.Patch(activation.GetGetMethod(), prefix: new HarmonyMethod(typeof(CapturePatches), nameof(ActivationGetPrefix)));
            });
        }

        public static void Uninstall()
        {
            if (_harmony == null) return;
            try { _harmony.UnpatchAll(Id); } catch (Exception e) { Debug.LogWarning("[LocaleCapture] 패치 해제 실패: " + e.Message); }
            _harmony = null;
        }

        private static void Try(string what, Action action)
        {
            try { action(); PatchedMethods++; }
            catch (Exception e) { Problems.Add($"{what}: {e.GetBaseException().Message}"); CoreProblems.Add(what); }
        }

        // ── 바꿔 끼울 호출 ──────────────────────────────────────────────────────

        private static void BuildMaps()
        {
            _shared = new Dictionary<MethodBase, MethodInfo>();
            void S(MethodBase from, string to, Type owner = null) { if (from != null) _shared[from] = Rep(owner ?? typeof(CaptureClock), to); }
            void I(MethodBase from, string to) { if (from != null) _shared[from] = Rep(typeof(CaptureInput), to); }

            S(typeof(Time).GetProperty(nameof(Time.realtimeSinceStartup))?.GetGetMethod(), "get_" + nameof(CaptureClock.RealtimeSinceStartup));
            S(typeof(Time).GetProperty(nameof(Time.realtimeSinceStartupAsDouble))?.GetGetMethod(), "get_" + nameof(CaptureClock.RealtimeSinceStartupAsDouble));
            S(typeof(System.Diagnostics.Stopwatch).GetMethod(nameof(System.Diagnostics.Stopwatch.GetTimestamp)), nameof(CaptureClock.GetTimestamp));
            S(typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(int) }), nameof(CaptureClock.Delay));
            S(typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(int), typeof(CancellationToken) }), nameof(CaptureClock.DelayToken));
            S(typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(TimeSpan) }), nameof(CaptureClock.DelaySpan));
            S(typeof(Task).GetMethod(nameof(Task.Delay), new[] { typeof(TimeSpan), typeof(CancellationToken) }), nameof(CaptureClock.DelaySpanToken));
            S(typeof(AudioSettings).GetProperty(nameof(AudioSettings.dspTime))?.GetGetMethod(), "get_" + nameof(CaptureClock.DspTime));
            S(typeof(Screen).GetProperty(nameof(Screen.safeArea))?.GetGetMethod(), "get_" + nameof(CaptureScreen.SafeArea), typeof(CaptureScreen));
            S(typeof(Screen).GetProperty(nameof(Screen.dpi))?.GetGetMethod(), "get_" + nameof(CaptureScreen.Dpi), typeof(CaptureScreen));
            S(typeof(Screen).GetProperty(nameof(Screen.cutouts))?.GetGetMethod(), "get_" + nameof(CaptureScreen.Cutouts), typeof(CaptureScreen));
            // 기기 언어는 패키지(Unity Localization 의 시스템 언어 선택 등)까지 — 게임이 처음부터 그 언어로 뜨면 강제 전환이 필요 없다
            S(typeof(Application).GetProperty(nameof(Application.systemLanguage))?.GetGetMethod(), "get_" + nameof(CaptureClock.SystemLanguage));
            S(typeof(Time).GetProperty(nameof(Time.unscaledDeltaTime))?.GetGetMethod(), "get_" + nameof(CaptureClock.UnscaledDeltaTime));
            S(typeof(Time).GetProperty(nameof(Time.unscaledTime))?.GetGetMethod(), "get_" + nameof(CaptureClock.UnscaledTime));
            S(typeof(Time).GetProperty(nameof(Time.unscaledTimeAsDouble))?.GetGetMethod(), "get_" + nameof(CaptureClock.UnscaledTimeAsDouble));

            Type input = typeof(Input);
            I(input.GetProperty(nameof(Input.mousePosition))?.GetGetMethod(), nameof(CaptureInput.MousePosition));
            I(input.GetProperty(nameof(Input.mouseScrollDelta))?.GetGetMethod(), nameof(CaptureInput.MouseScrollDelta));
            I(input.GetMethod(nameof(Input.GetMouseButton), new[] { typeof(int) }), nameof(CaptureInput.GetMouseButton));
            I(input.GetMethod(nameof(Input.GetMouseButtonDown), new[] { typeof(int) }), nameof(CaptureInput.GetMouseButtonDown));
            I(input.GetMethod(nameof(Input.GetMouseButtonUp), new[] { typeof(int) }), nameof(CaptureInput.GetMouseButtonUp));
            I(input.GetProperty(nameof(Input.touchCount))?.GetGetMethod(), nameof(CaptureInput.TouchCount));
            I(input.GetMethod(nameof(Input.GetTouch), new[] { typeof(int) }), nameof(CaptureInput.GetTouch));
            I(input.GetProperty(nameof(Input.touches))?.GetGetMethod(), nameof(CaptureInput.Touches));
            I(input.GetProperty(nameof(Input.anyKey))?.GetGetMethod(), nameof(CaptureInput.AnyKey));
            I(input.GetProperty(nameof(Input.anyKeyDown))?.GetGetMethod(), nameof(CaptureInput.AnyKeyDown));
            I(input.GetProperty(nameof(Input.inputString))?.GetGetMethod(), nameof(CaptureInput.InputString));
            foreach (string name in new[] { nameof(Input.GetKey), nameof(Input.GetKeyDown), nameof(Input.GetKeyUp) })
            {
                _shared[input.GetMethod(name, new[] { typeof(KeyCode) })] = typeof(CaptureInput).GetMethod(name, new[] { typeof(KeyCode) });
                _shared[input.GetMethod(name, new[] { typeof(string) })] = typeof(CaptureInput).GetMethod(name, new[] { typeof(string) });
            }
            foreach (string name in new[] { nameof(Input.GetAxis), nameof(Input.GetAxisRaw), nameof(Input.GetButton), nameof(Input.GetButtonDown), nameof(Input.GetButtonUp) })
                _shared[input.GetMethod(name, new[] { typeof(string) })] = typeof(CaptureInput).GetMethod(name, new[] { typeof(string) });

            // 난수 호출자 기록(어긋남 검사)
            Type random = typeof(UnityEngine.Random);
            void R(MethodBase from, string to) { if (from != null) _shared[from] = Rep(typeof(RandomTrace), to); }
            R(random.GetMethod(nameof(UnityEngine.Random.Range), new[] { typeof(int), typeof(int) }), nameof(RandomTrace.RangeInt));
            R(random.GetMethod(nameof(UnityEngine.Random.Range), new[] { typeof(float), typeof(float) }), nameof(RandomTrace.RangeFloat));
            R(random.GetProperty(nameof(UnityEngine.Random.value))?.GetGetMethod(), "get_" + nameof(RandomTrace.Value));
            R(random.GetProperty(nameof(UnityEngine.Random.insideUnitCircle))?.GetGetMethod(), "get_" + nameof(RandomTrace.InsideUnitCircle));
            R(random.GetProperty(nameof(UnityEngine.Random.insideUnitSphere))?.GetGetMethod(), "get_" + nameof(RandomTrace.InsideUnitSphere));
            R(random.GetProperty(nameof(UnityEngine.Random.onUnitSphere))?.GetGetMethod(), "get_" + nameof(RandomTrace.OnUnitSphere));
            R(random.GetProperty(nameof(UnityEngine.Random.rotation))?.GetGetMethod(), "get_" + nameof(RandomTrace.Rotation));
            R(random.GetProperty(nameof(UnityEngine.Random.rotationUniform))?.GetGetMethod(), "get_" + nameof(RandomTrace.RotationUniform));
            R(random.GetMethod(nameof(UnityEngine.Random.InitState), new[] { typeof(int) }), nameof(RandomTrace.InitState));
            foreach (int n in new[] { 0, 2, 4, 6, 8 })
                R(random.GetMethod(nameof(UnityEngine.Random.ColorHSV), Enumerable.Repeat(typeof(float), n).ToArray()), nameof(RandomTrace.ColorHSV0).Replace("0", n.ToString()));

            _full = new Dictionary<MethodBase, MethodInfo>(_shared);
            void F(MethodBase from, string to) { if (from != null) _full[from] = Rep(typeof(CaptureClock), to); }
            F(typeof(DateTime).GetProperty(nameof(DateTime.Now))?.GetGetMethod(), "get_" + nameof(CaptureClock.Now));
            F(typeof(DateTime).GetProperty(nameof(DateTime.UtcNow))?.GetGetMethod(), "get_" + nameof(CaptureClock.UtcNow));
            F(typeof(DateTime).GetProperty(nameof(DateTime.Today))?.GetGetMethod(), "get_" + nameof(CaptureClock.Today));
            F(typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.Now))?.GetGetMethod(), "get_" + nameof(CaptureClock.OffsetNow));
            F(typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.UtcNow))?.GetGetMethod(), "get_" + nameof(CaptureClock.OffsetUtcNow));
            F(typeof(Environment).GetProperty(nameof(Environment.TickCount))?.GetGetMethod(), "get_" + nameof(CaptureClock.TickCount));
            F(typeof(Guid).GetMethod(nameof(Guid.NewGuid)), nameof(CaptureClock.NewGuid));
            F(AccessTools.Constructor(typeof(System.Random), Type.EmptyTypes), nameof(CaptureClock.NewRandom));

            // Stopwatch(프로젝트 코드만 — 패키지는 측정·통계용이 대부분이고, 경과를 기다리며 도는 코드가 있으면 프레임이 멈춘다)
            Type stopwatch = typeof(System.Diagnostics.Stopwatch);
            void W(MethodBase from, string to) { if (from != null) _full[from] = Rep(typeof(CaptureStopwatch), to); }
            W(stopwatch.GetMethod(nameof(System.Diagnostics.Stopwatch.StartNew), Type.EmptyTypes), nameof(CaptureStopwatch.StartNew));
            W(stopwatch.GetMethod(nameof(System.Diagnostics.Stopwatch.Start), Type.EmptyTypes), nameof(CaptureStopwatch.Start));
            W(stopwatch.GetMethod(nameof(System.Diagnostics.Stopwatch.Stop), Type.EmptyTypes), nameof(CaptureStopwatch.Stop));
            W(stopwatch.GetMethod(nameof(System.Diagnostics.Stopwatch.Reset), Type.EmptyTypes), nameof(CaptureStopwatch.Reset));
            W(stopwatch.GetMethod(nameof(System.Diagnostics.Stopwatch.Restart), Type.EmptyTypes), nameof(CaptureStopwatch.Restart));
            W(stopwatch.GetProperty(nameof(System.Diagnostics.Stopwatch.IsRunning))?.GetGetMethod(), nameof(CaptureStopwatch.IsRunning));
            W(stopwatch.GetProperty(nameof(System.Diagnostics.Stopwatch.Elapsed))?.GetGetMethod(), nameof(CaptureStopwatch.Elapsed));
            W(stopwatch.GetProperty(nameof(System.Diagnostics.Stopwatch.ElapsedMilliseconds))?.GetGetMethod(), nameof(CaptureStopwatch.ElapsedMilliseconds));
            W(stopwatch.GetProperty(nameof(System.Diagnostics.Stopwatch.ElapsedTicks))?.GetGetMethod(), nameof(CaptureStopwatch.ElapsedTicks));

            // 로딩 코드는 로딩 대기 중에도 흐르는 시계를 본다
            _loader = new Dictionary<MethodBase, MethodInfo>(_shared);
            void L(MethodBase from, string to) { if (from != null) _loader[from] = Rep(typeof(CaptureClock), to); }
            L(typeof(Time).GetProperty(nameof(Time.realtimeSinceStartup))?.GetGetMethod(), "get_" + nameof(CaptureClock.LoadRealtimeSinceStartup));
            L(typeof(Time).GetProperty(nameof(Time.realtimeSinceStartupAsDouble))?.GetGetMethod(), "get_" + nameof(CaptureClock.LoadRealtimeSinceStartupAsDouble));
            L(typeof(Time).GetProperty(nameof(Time.unscaledDeltaTime))?.GetGetMethod(), "get_" + nameof(CaptureClock.LoadUnscaledDeltaTime));
            L(typeof(Time).GetProperty(nameof(Time.unscaledTime))?.GetGetMethod(), "get_" + nameof(CaptureClock.LoadUnscaledTime));
            L(typeof(Time).GetProperty(nameof(Time.unscaledTimeAsDouble))?.GetGetMethod(), "get_" + nameof(CaptureClock.LoadUnscaledTimeAsDouble));
        }

        private static bool IsLoaderAssembly(string name) => name == "Unity.ResourceManager" || name == "Unity.Addressables";

        private static MethodInfo Rep(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(type.Name, name);

        // ── 호출 위치 패치 ─────────────────────────────────────────────────────

        private static void PatchCallSites()
        {
            Dictionary<string, System.Reflection.Assembly> loaded = AppDomain.CurrentDomain.GetAssemblies()
                .GroupBy(a => a.GetName().Name).ToDictionary(g => g.Key, g => g.First());
            var done = new HashSet<string>();
            var player = new HashSet<string>();   // 게임(플레이어) 쪽 어셈블리 — ThreadGate 가 누구의 이어받기인지 가릴 때 쓴다
            foreach (UnityEditor.Compilation.Assembly asm in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (asm.name.StartsWith("CookApps.LocaleCapture") || asm.name.StartsWith("Unity.InputSystem")) continue;
                player.Add(asm.name);
                if (!loaded.TryGetValue(asm.name, out System.Reflection.Assembly assembly)) continue;
                done.Add(asm.name);
                bool project = asm.sourceFiles.Any(f => f.StartsWith("Assets/", StringComparison.Ordinal));
                // Assets/Plugins 아래 소스만으로 된 어셈블리는 가져다 쓰는 코드(DI·트윈 라이브러리 등)로 본다 — 못 바꾼 곳을 "프로젝트 코드" 로 경고하지 않는다.
                bool plugin = project && asm.sourceFiles.All(f => !f.StartsWith("Assets/", StringComparison.Ordinal) || f.StartsWith("Assets/Plugins/", StringComparison.Ordinal));
                if (IsLoaderAssembly(asm.name)) PatchAssembly(assembly, _loader, "loader");
                else PatchAssembly(assembly, project ? _full : _shared, project ? "full" : "shared", project && !plugin);
            }

            // Assets 아래 미리 빌드된 DLL(DOTween 등)도 실시간·입력·난수 호출을 바꾼다
            string assets = System.IO.Path.GetFullPath(Application.dataPath);
            foreach (System.Reflection.Assembly assembly in loaded.Values)
            {
                string name = assembly.GetName().Name;
                if (assembly.IsDynamic || done.Contains(name) || name == "0Harmony" || name.Contains("Editor") || name.StartsWith("CookApps.LocaleCapture")) continue;
                string location;
                try { location = assembly.Location; } catch { continue; }
                if (string.IsNullOrEmpty(location) || !System.IO.Path.GetFullPath(location).StartsWith(assets, StringComparison.OrdinalIgnoreCase)) continue;
                player.Add(name);
                PatchAssembly(assembly, _shared, "shared");
            }

            // UI Toolkit 은 엔진 모듈이라 위 목록에 없다. 런타임 패널의 시계(전환 애니메이션·예약 실행·더블 클릭 판정)는 실제 시계를,
            // 스크롤 관성은 unscaled 시간을 읽는다 — 그대로 두면 재생 속도에 따라 패널이 열리고 닫히는 프레임이 달라져 같은 터치가 다른 곳에 들어간다.
            // 에디터 창의 패널은 자기 시계(EditorApplication.timeSinceStartup)를 따로 걸어 두어 이 기본 시계를 쓰지 않는다.
            System.Reflection.Assembly uiToolkit = AccessTools.TypeByName("UnityEngine.UIElements.VisualElement")?.Assembly;
            if (uiToolkit != null && done.Add(uiToolkit.GetName().Name)) PatchAssembly(uiToolkit, _shared, "shared");
            ThreadGate.PlayerAssemblies = player;
        }

        // kind: 바꿔 끼울 목록 — full(프로젝트 코드)·shared(패키지·DLL)·loader(로딩 코드)
        // ownCode: 프로젝트가 직접 쓴 코드인가(못 바꾼 곳이 있으면 결과에 "주의" 로 알린다)
        private static void PatchAssembly(System.Reflection.Assembly assembly, Dictionary<MethodBase, MethodInfo> map, string kind, bool ownCode = false)
        {
            HarmonyMethod transpiler = new HarmonyMethod(typeof(CapturePatches),
                kind == "full" ? nameof(TranspileFull) : kind == "loader" ? nameof(TranspileLoader) : nameof(TranspileShared));
            foreach (MethodBase method in Targets(assembly, map, kind))
            {
                try { _harmony.Patch(method, transpiler: transpiler); PatchedMethods++; }
                catch (Exception e)
                {
                    // 못 바꾼 메서드(제네릭 타입의 메서드, catch-when 이 든 메서드 등): 그 안에 남은 호출이 무엇인지까지 남긴다.
                    string calls = LeftCalls(method, map, out bool onlyGameRandom);
                    string owner = method.DeclaringType?.FullName ?? "?";
                    Problems.Add($"{owner}.{method.Name}: {e.GetBaseException().Message} — 그대로 남은 호출: {calls}");
                    bool sound = owner.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 || owner.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0;
                    if ((ownCode && !onlyGameRandom) || (sound && calls.Contains("Random.")))
                        RiskyProblems.Add($"{owner}.{method.Name} ({calls})");
                }
            }
        }

        // 못 바꾼 메서드 안에 그대로 남은(바꿔 끼우려던) 호출 이름들. onlyGameRandom: 전부 결과에 영향이 없는 난수 호출인가.
        private static string LeftCalls(MethodBase method, Dictionary<MethodBase, MethodInfo> map, out bool onlyGameRandom)
        {
            onlyGameRandom = true;
            var names = new List<string>();
            try
            {
                byte[] il = method.GetMethodBody()?.GetILAsByteArray();
                if (il == null) { onlyGameRandom = false; return "?"; }
                Module module = method.Module;
                Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
                Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
                for (int i = 0; i + 4 < il.Length; i++)
                {
                    byte op = il[i];
                    if (op != 0x28 && op != 0x6F && op != 0x73) continue;
                    int token = BitConverter.ToInt32(il, i + 1);
                    int table = (token >> 24) & 0xFF;
                    if (table != 0x0A && table != 0x06) continue;
                    MethodBase target;
                    try { target = module.ResolveMethod(token, typeArgs, methodArgs); }
                    catch { continue; }
                    if (target == null || !map.ContainsKey(target)) continue;
                    string name = (target.DeclaringType?.Name ?? "?") + "." + target.Name;
                    if (!names.Contains(name)) names.Add(name);
                    // 결과가 같은 것: 게임 난수(같은 흐름을 그대로 쓴다), new Random()(생성자 패치가 호출 위치와 상관없이 시드를 넣는다)
                    bool harmless = target.DeclaringType == typeof(UnityEngine.Random) || (target.DeclaringType == typeof(System.Random) && target.IsConstructor);
                    if (!harmless) onlyGameRandom = false;
                }
            }
            catch { onlyGameRandom = false; return "?"; }
            if (names.Count == 0) { onlyGameRandom = false; return "?"; }
            return string.Join(", ", names);
        }

        // 바꿔 끼울 호출이 든 메서드. 전부 훑으면 몇 초 걸려서, 어셈블리가 다시 빌드되기 전까지(MVID 가 같으면) 결과를 Library 에 둔다.
        private static List<MethodBase> Targets(System.Reflection.Assembly assembly, Dictionary<MethodBase, MethodInfo> map, string kind)
        {
            Module module = assembly.ManifestModule;
            string key = $"{assembly.GetName().Name}|{module.ModuleVersionId}|{kind}|{MapSignature(map)}";
            var found = new List<MethodBase>();
            if (TargetCache.TryGet(key, out List<int> tokens))
            {
                try
                {
                    foreach (int token in tokens) found.Add(module.ResolveMethod(token));
                    return found;
                }
                catch { found.Clear(); }   // 못 풀면 다시 훑는다
            }

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var tokenCache = new Dictionary<int, bool>();
            foreach (Type type in types)
            {
                IEnumerable<MethodBase> methods;
                try { methods = type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)); }
                catch { continue; }
                foreach (MethodBase method in methods)
                    if (!method.IsAbstract && Calls(method, map, tokenCache)) found.Add(method);
            }
            TargetCache.Set(key, found.Select(m => m.MetadataToken).ToList());
            return found;
        }

        // 바꿔 끼울 목록이 바뀌면(도구 업데이트) 캐시도 무효가 되게.
        private static string MapSignature(Dictionary<MethodBase, MethodInfo> map)
        {
            ulong hash = 14695981039346656037UL;   // FNV-1a — string.GetHashCode 는 실행마다 다를 수 있다
            foreach (string s in map.Keys.Select(m => m.DeclaringType?.FullName + "." + m + ">" + map[m].Name).OrderBy(s => s, StringComparer.Ordinal))
                foreach (char c in s) hash = (hash ^ c) * 1099511628211UL;
            return map.Count + ":" + hash.ToString("x16");
        }

        // IL 을 훑어 바꿀 호출(call·callvirt·newobj 의 멤버 참조 토큰)이 있는지 본다. 같은 모듈의 토큰은 한 번만 푼다.
        private static bool Calls(MethodBase method, Dictionary<MethodBase, MethodInfo> map, Dictionary<int, bool> cache)
        {
            byte[] il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch { return false; }
            if (il == null) return false;
            Module module = method.Module;
            Type[] typeArgs = method.DeclaringType != null && method.DeclaringType.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
            Type[] methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            for (int i = 0; i + 4 < il.Length; i++)
            {
                byte op = il[i];
                if (op != 0x28 && op != 0x6F && op != 0x73) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                int table = (token >> 24) & 0xFF;
                if (table != 0x0A && table != 0x06) continue;   // MemberRef·MethodDef
                int key = token ^ module.MetadataToken * 397;
                if (!cache.TryGetValue(key, out bool hit))
                {
                    try { hit = module.ResolveMethod(token, typeArgs, methodArgs) is MethodBase target && map.ContainsKey(target); }
                    catch { hit = false; }
                    cache[key] = hit;
                }
                if (hit) return true;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> TranspileFull(IEnumerable<CodeInstruction> instructions) => Replace(instructions, _full);
        private static IEnumerable<CodeInstruction> TranspileShared(IEnumerable<CodeInstruction> instructions) => Replace(instructions, _shared);
        private static IEnumerable<CodeInstruction> TranspileLoader(IEnumerable<CodeInstruction> instructions) => Replace(instructions, _loader);

        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, Dictionary<MethodBase, MethodInfo> map)
        {
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Newobj)
                    && ci.operand is MethodBase target && map.TryGetValue(target, out MethodInfo replacement))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = replacement;
                }
                yield return ci;
            }
        }

        // ── 개별 패치 ──────────────────────────────────────────────────────────

        private static readonly ConstructorInfo SeededRandom = typeof(System.Random).GetConstructor(new[] { typeof(int) });

        private static void RandomPostfix(System.Random __instance)
        {
            if (!CaptureClock.Active || CalledFromEditor()) return;   // 에디터 쪽이 만든 Random 은 게임 시드 순번에 넣지 않는다
            if (CaptureClock.TryNextSeed(CaptureClock.CreatorOf(1), out int seed)) SeededRandom.Invoke(__instance, new object[] { seed });
        }

        private static readonly Dictionary<System.Reflection.Assembly, bool> _editorAssembly = new Dictionary<System.Reflection.Assembly, bool>();

        // new Random() 을 부른 첫 바깥 코드가 에디터·컴파일러 어셈블리인지. 런타임·Harmony 프레임은 건너뛴다.
        private static bool CalledFromEditor()
        {
            var trace = new System.Diagnostics.StackTrace(2, false);
            for (int i = 0; i < trace.FrameCount; i++)
            {
                System.Reflection.Assembly asm = trace.GetFrame(i).GetMethod()?.DeclaringType?.Assembly;
                if (asm == null) continue;
                string name = asm.GetName().Name;
                if (name == "mscorlib" || name.StartsWith("System") || name == "0Harmony" || name.StartsWith("CookApps.LocaleCapture")) continue;
                if (!_editorAssembly.TryGetValue(asm, out bool editor))
                    _editorAssembly[asm] = editor = name.Contains("Editor") || name.StartsWith("Microsoft.CodeAnalysis") || string.IsNullOrEmpty(asm.Location);
                return editor;
            }
            return false;
        }

        private static void PatchHttp(MethodInfo method)
        {
            if (method == null) throw new MissingMethodException("SendAsync");
            _harmony.Patch(method, prefix: new HarmonyMethod(typeof(CapturePatches), nameof(HttpPrefix)), postfix: new HarmonyMethod(typeof(CapturePatches), nameof(HttpPostfix)));
        }

        private static bool HttpPrefix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result)
        {
            if (!NetworkTape.TryReplay(request, out Task<HttpResponseMessage> taped)) return true;
            __result = taped;
            return false;
        }

        private static void HttpPostfix(HttpRequestMessage request, ref Task<HttpResponseMessage> __result) => __result = NetworkTape.Record(request, __result);

        private static void WebPrefix(UnityWebRequest __instance) => NetworkTape.BeforeWebRequest(__instance);

        private static void WebPostfix(UnityWebRequest __instance, UnityWebRequestAsyncOperation __result) => NetworkTape.AfterWebRequest(__instance, __result);

        private static void SceneLoadPostfix(AsyncOperation __result) => CaptureMarkers.SceneLoadStarted(__result);

        private static FieldInfo _loadDelay;

        private static void AssetProviderPostfix(object __instance)
        {
            if (CaptureBoot.NoLoadDelay && _loadDelay != null) _loadDelay.SetValue(__instance, 0f);
        }

        // 다른 스레드에서 Unity 메인 스레드 컨텍스트로 넘기는 이어받기. 붙잡았으면 원래 호출을 건너뛰고, ThreadGate 가 때가 되면 다시 넘긴다
        // (그땐 메인 스레드라 붙잡지 않고 그대로 간다).
        private static bool ContextPostPrefix(SynchronizationContext __instance, SendOrPostCallback callback, object state) =>
            !ThreadGate.Capture(callback, state, () => __instance.Post(callback, state));

        // UniTask 가 다른 스레드에서 플레이어 루프로 넘기는 이어받기(SwitchToMainThread·Yield·RunOnThreadPool 의 복귀).
        private static bool UniTaskContinuationPrefix(object[] __args, MethodBase __originalMethod)
        {
            if (__args == null || __args.Length < 2 || !(__args[1] is Action continuation)) return true;
            object[] args = (object[]) __args.Clone();
            return !ThreadGate.Capture(continuation, null, () => __originalMethod.Invoke(null, args));
        }

        private static FieldInfo _deferredCallbacks;

        // ResourceManager 가 이번 프레임에 완료 콜백을 부를 Addressables 작업 수만큼 동기 지점을 남긴다.
        private static void DeferredCallbacksPrefix(object __instance)
        {
            if (!CaptureClock.Active || __instance == null) return;
            _deferredCallbacks ??= __instance.GetType().GetField("m_DeferredCompleteCallbacks", BindingFlags.Instance | BindingFlags.NonPublic);
            if (!(_deferredCallbacks?.GetValue(__instance) is System.Collections.ICollection list)) return;
            foreach (object op in list)
            {
                CaptureMarkers.Hit("addr");
                CaptureMarkers.Loaded(NameOf(op));
            }
        }

        private static readonly Dictionary<Type, PropertyInfo> _debugNames = new Dictionary<Type, PropertyInfo>();

        // 끝난 Addressables 작업의 이름(진단용). 작업 종류와 대상 주소.
        private static string NameOf(object op)
        {
            if (op == null) return "?";
            try
            {
                Type type = op.GetType();
                if (!_debugNames.TryGetValue(type, out PropertyInfo property))
                    _debugNames[type] = property = type.GetProperty("DebugName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                string name = property?.GetValue(op) as string;
                string kind = type.Name;
                int tick = kind.IndexOf('`');
                if (tick > 0) kind = kind.Substring(0, tick);
                return string.IsNullOrEmpty(name) ? kind : kind + " " + (name.Length > 90 ? name.Substring(0, 90) : name);
            }
            catch { return op.GetType().Name; }
        }

        private static bool _collapsing;
        private static FieldInfo _toRegister;
        private static MethodInfo _register;
        private const int MaxCollapse = 500;   // 한 프레임에 이어서 부르는 최대 횟수(콜백이 끝없이 다음 작업을 만드는 경우를 막는다)

        // 콜백을 부르는 동안 끝난 작업(콜백 안에서 시작해 바로 끝난 다음 로드)은 다음 프레임으로 미뤄진다 — 남은 게 없을 때까지 같은 프레임에 이어서 부른다.
        private static void DeferredCallbacksPostfix(object __instance, MethodBase __originalMethod)
        {
            if (_collapsing || !CaptureBoot.CollapseLoads || !CaptureClock.Active || __instance == null) return;
            _collapsing = true;
            try
            {
                Type type = __instance.GetType();
                _toRegister ??= type.GetField("m_DeferredCallbacksToRegister", BindingFlags.Instance | BindingFlags.NonPublic);
                _register ??= AccessTools.Method(type, "RegisterForDeferredCallback");
                if (_toRegister == null || _register == null) return;
                for (int i = 0; i < MaxCollapse; i++)
                {
                    // 콜백 도중 끝난 작업은 "다음 Update 에 등록" 목록에 들어가 있다 — Update 가 하는 대로 옮겨 등록하고 바로 부른다
                    if (!(_toRegister.GetValue(__instance) is System.Collections.IEnumerable pending)) break;
                    var requests = new List<object>();
                    foreach (object request in pending) requests.Add(request);
                    _toRegister.SetValue(__instance, null);
                    if (requests.Count == 0) break;
                    foreach (object request in requests)
                    {
                        Type r = request.GetType();
                        object operation = r.GetField("operation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(request);
                        object increment = r.GetField("incrementRefCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(request);
                        if (operation != null) _register.Invoke(__instance, new[] { operation, increment ?? true });
                    }
                    __originalMethod.Invoke(__instance, null);
                }
            }
            catch (Exception e) { Debug.LogException(e.GetBaseException()); }
            finally { _collapsing = false; }
        }

        private static bool ActivationSetPrefix(AsyncOperation __instance, bool value) => !CaptureMarkers.InterceptSet(__instance, value);

        private static bool ActivationGetPrefix(AsyncOperation __instance, ref bool __result) => !CaptureMarkers.InterceptGet(__instance, out __result);

        // 완료 콜백 전에 본다(콜백이 요청을 Dispose 할 수 있다).
        private static void AsyncPrefix(AsyncOperation __instance)
        {
            if (__instance == null) return;
            // 서버 응답(버퍼로 받는 요청)은 테이프가 도착 프레임을 맞춘다. HttpClient 안쪽 요청은 재생 땐 아예 안 나가니 동기 지점으로 두면 안 된다.
            if (__instance is UnityWebRequestAsyncOperation web && web.webRequest?.downloadHandler is DownloadHandlerBuffer) return;
            CaptureMarkers.Hit("async:" + __instance.GetType().Name);
        }
    }
}
