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
    //  · HttpClient·UnityWebRequest → NetworkTape, AsyncOperation 완료 → CaptureMarkers
    internal static class CapturePatches
    {
        private const string Id = "com.cookapps.localecapture";
        private static Harmony _harmony;
        private static Dictionary<MethodBase, MethodInfo> _full, _shared;

        public static bool Installed => _harmony != null;
        public static int PatchedMethods { get; private set; }
        public static readonly List<string> Problems = new List<string>();

        public static void Install()
        {
            if (_harmony != null) return;
            Problems.Clear();
            PatchedMethods = 0;
            _harmony = new Harmony(Id);
            // 패치 코드를 DynamicMethod 로 만든다. 기본(MethodBuilder)은 미완성 타입이 든 동적 어셈블리를 남겨,
            // 모든 어셈블리의 GetTypes() 를 도는 게임 코드(디버그 패널 등)가 ReflectionTypeLoadException 으로 죽는다.
            string dmdType = Environment.GetEnvironmentVariable("MONOMOD_DMD_TYPE");
            Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", "dynamicmethod");
            try { InstallAll(); }
            finally { Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", dmdType); }
        }

        private static void InstallAll()
        {
            BuildMaps();
            TargetCache.Load();
            PatchCallSites();
            TargetCache.Save();
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
            PropertyInfo activation = typeof(AsyncOperation).GetProperty(nameof(AsyncOperation.allowSceneActivation));
            Try("씬 활성화 지연", () =>
            {
                _harmony.Patch(activation.GetSetMethod(), prefix: new HarmonyMethod(typeof(CapturePatches), nameof(ActivationSetPrefix)));
                _harmony.Patch(activation.GetGetMethod(), prefix: new HarmonyMethod(typeof(CapturePatches), nameof(ActivationGetPrefix)));
            });
            Debug.Log($"[LocaleCapture] 런타임 패치 {PatchedMethods}곳" + (Problems.Count > 0 ? $", 문제 {Problems.Count}건: {string.Join(" / ", Problems.Take(5))}" : ""));
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
            catch (Exception e) { Problems.Add($"{what}: {e.GetBaseException().Message}"); }
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
        }

        private static MethodInfo Rep(Type type, string name) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new MissingMethodException(type.Name, name);

        // ── 호출 위치 패치 ─────────────────────────────────────────────────────

        private static void PatchCallSites()
        {
            Dictionary<string, System.Reflection.Assembly> loaded = AppDomain.CurrentDomain.GetAssemblies()
                .GroupBy(a => a.GetName().Name).ToDictionary(g => g.Key, g => g.First());
            var done = new HashSet<string>();
            foreach (UnityEditor.Compilation.Assembly asm in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                if (asm.name.StartsWith("CookApps.LocaleCapture") || asm.name.StartsWith("Unity.InputSystem")) continue;
                if (!loaded.TryGetValue(asm.name, out System.Reflection.Assembly assembly)) continue;
                done.Add(asm.name);
                bool project = asm.sourceFiles.Any(f => f.StartsWith("Assets/", StringComparison.Ordinal));
                PatchAssembly(assembly, project ? _full : _shared, project);
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
                PatchAssembly(assembly, _shared, false);
            }
        }

        private static void PatchAssembly(System.Reflection.Assembly assembly, Dictionary<MethodBase, MethodInfo> map, bool project)
        {
            HarmonyMethod transpiler = new HarmonyMethod(typeof(CapturePatches), project ? nameof(TranspileFull) : nameof(TranspileShared));
            foreach (MethodBase method in Targets(assembly, map, project))
            {
                try { _harmony.Patch(method, transpiler: transpiler); PatchedMethods++; }
                catch (Exception e) { Problems.Add($"{method.DeclaringType?.FullName}.{method.Name}: {e.GetBaseException().Message}"); }
            }
        }

        // 바꿔 끼울 호출이 든 메서드. 전부 훑으면 몇 초 걸려서, 어셈블리가 다시 빌드되기 전까지(MVID 가 같으면) 결과를 Library 에 둔다.
        private static List<MethodBase> Targets(System.Reflection.Assembly assembly, Dictionary<MethodBase, MethodInfo> map, bool project)
        {
            Module module = assembly.ManifestModule;
            string key = $"{assembly.GetName().Name}|{module.ModuleVersionId}|{(project ? "full" : "shared")}|{MapSignature(map)}";
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
            if (CaptureClock.Active) SeededRandom.Invoke(__instance, new object[] { CaptureClock.NextSeed() });
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
