using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace CookApps.LocaleCapture.Editor
{
    // Harmony 가 다시 만들지 못하는 메서드의 호출을 바꿔 끼운다 — 예외 필터(catch … when)가 든 메서드.
    //
    // 왜 따로 필요한가: 그런 메서드만 시계 호출이 원래대로 남으면, 같은 클래스 안에서 엔진의 실제 시계와 세션 시계가 섞인다.
    //   예) 마감 시각을 정하는 메서드는 실제 시계로, 남은 시간을 계산하는 쪽은 세션 시계로 — 재생마다 남은 시간이 다르게 나온다.
    // 왜 Harmony 로는 안 되나: Harmony 2.2.2 는 메서드를 읽을 때 필터 절의 "처리 블록 시작" 을 표시하지 않고, 그것을 받아 적는
    //   IL 생성기도 "필터 + 처리 블록" 을 한 절로 잇지 못한다. 그래서 다시 만든 메서드가 깨져 "Incorrect code generation for exception block" 이 난다.
    // 여기서 하는 일: Harmony 의 읽기·쓰기를 거치지 않고, 함께 든 MonoMod 의 메서드 복사(DynamicMethodDefinition)를 직접 쓴다.
    //   그쪽은 예외 절을 원래 모양대로(필터 포함) 옮긴다. 복사본에서 호출 대상만 바꾼 뒤, Harmony 가 쓰는 것과 같은 방법으로 원래 메서드를 복사본으로 돌린다.
    // 해제하지 않는다: Harmony 가 모르는 바꿔치기라 UnpatchAll 에 걸리지 않는다. 바꿔 끼운 호출(CaptureClock 등)은 세션이 아닐 때
    //   실제 값을 그대로 돌려주므로 걸어 둔 채여도 동작이 같고, 도메인이 다시 로드되면 함께 사라진다.
    internal static class CopyPatch
    {
        // 원래 메서드 → 바꿔 끼운 복사본. 복사본이 치워지지 않게 붙들어 두고, 같은 메서드를 두 번 돌리지 않는다.
        private static readonly Dictionary<MethodBase, MethodInfo> _kept = new Dictionary<MethodBase, MethodInfo>();

        public static int Count { get { lock (_kept) return _kept.Count; } }

        // 이 까닭으로 Harmony 가 실패한 메서드인가.
        public static bool Handles(Exception harmonyFailure) =>
            harmonyFailure != null && harmonyFailure.GetBaseException().Message.IndexOf("exception block", StringComparison.OrdinalIgnoreCase) >= 0;

        // method 안의 map 에 든 호출을 바꿔 끼운다. 못 하면 예외(원래 메서드는 그대로다).
        public static void Apply(MethodBase method, Dictionary<MethodBase, MethodInfo> map)
        {
            lock (_kept)
            {
                if (_kept.ContainsKey(method)) return;
                if (method.IsGenericMethod || method.ContainsGenericParameters || (method.DeclaringType != null && method.DeclaringType.IsGenericType))
                    throw new NotSupportedException("제네릭 메서드는 복사하지 못한다");

                string key = Key(method);
                LoadGuard();
                if (_skip.Contains(key))
                    throw new InvalidOperationException($"지난번에 이 메서드를 바꾸는 도중 에디터가 종료돼 건너뛴다({SkipFile} 을 지우면 다시 시도한다)");

                MarkPending(key);
                try
                {
                    MethodInfo copy = Build(method, map);
                    string error = Memory.DetourMethod(method, copy);
                    if (error != null) throw new InvalidOperationException(error);
                    _kept[method] = copy;
                }
                finally { MarkPending(null); }
            }
        }

        // 원래 메서드를 복사해 호출만 바꾼 메서드를 만든다(원래 메서드는 건드리지 않는다).
        internal static MethodInfo Build(MethodBase method, Dictionary<MethodBase, MethodInfo> map)
        {
            // 생성기는 다른 패치와 같은 것으로(CapturePatches 의 설명 — 기본 생성기는 미완성 타입이 든 동적 어셈블리를 남긴다)
            string dmdType = Environment.GetEnvironmentVariable("MONOMOD_DMD_TYPE");
            Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", Generator);
            try
            {
                using (var definition = new DynamicMethodDefinition(method))
                {
                    if (Replace(definition, map) == 0) throw new InvalidOperationException("바꿀 호출을 찾지 못했다");
                    return definition.Generate() ?? throw new InvalidOperationException("복사본을 만들지 못했다");
                }
            }
            finally { Environment.SetEnvironmentVariable("MONOMOD_DMD_TYPE", dmdType); }
        }

        internal static string Generator = "dynamicmethod";

        // "call" 명령. 함께 든 Cecil 의 명령 목록(OpCodes)은 바깥에 열려 있지 않아 이름으로 꺼낸다.
        private static readonly OpCode CallCode = (OpCode) (typeof(Instruction).Assembly.GetType("Mono.Cecil.Cil.OpCodes", true)
            .GetField("Call", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException("Mono.Cecil.Cil.OpCodes.Call")).GetValue(null);

        // 복사본의 호출 가운데 map 에 든 것을 바꾼다. 바꾼 수를 돌려준다.
        // 대상은 이름(Cecil 의 전체 이름)으로 맞춘다 — 복사본의 호출 대상과 map 의 메서드를 같은 모듈로 들여와 같은 꼴의 이름이 되게 한다.
        private static int Replace(DynamicMethodDefinition definition, Dictionary<MethodBase, MethodInfo> map)
        {
            ModuleDefinition module = definition.Module;
            var byName = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
            foreach (KeyValuePair<MethodBase, MethodInfo> pair in map)
            {
                try { byName[module.ImportReference(pair.Key).FullName] = pair.Value; }
                catch { }   // 들여올 수 없는 대상은 이 메서드에서도 쓰일 수 없다
            }
            int replaced = 0;
            foreach (Instruction instruction in definition.Definition.Body.Instructions)
            {
                string code = instruction.OpCode.Name;
                if (code != "call" && code != "callvirt" && code != "newobj") continue;
                if (!(instruction.Operand is MethodReference target) || !byName.TryGetValue(target.FullName, out MethodInfo replacement)) continue;
                instruction.OpCode = CallCode;
                instruction.Operand = module.ImportReference(replacement);
                replaced++;
            }
            return replaced;
        }

        // ── 바꾸는 도중에 에디터가 종료됐을 때 ─────────────────────────────────────
        // 복사본을 기계어로 만드는 일은 런타임이 한다. 거기서 런타임째 죽으면 예외로 잡을 수 없고, 그대로 두면 녹화를 시작할 때마다 죽는다.
        // 그래서 바꾸기 직전에 "지금 바꾸는 메서드" 를 파일에 적고 끝나면 지운다. 다음 실행에 그 파일이 남아 있으면 그때 죽은 것이므로,
        // 그 메서드를 건너뛸 목록에 올리고(예전처럼 "못 바꾼 곳" 으로 알린다) 다시 시도하지 않는다.

        // 표시 파일을 둘 폴더(프로젝트의 Library 아래). null 이면 표시를 남기지 않는다.
        public static string GuardDir;

        // 복사 방식이 바뀌면 올린다 — 건너뛰던 메서드를 다시 시도한다.
        private const int Revision = 1;

        private static HashSet<string> _skip;
        private static string _guardLoadedFor;

        private static string SkipFile => GuardDir == null ? "copy-skip.txt" : Path.Combine(GuardDir, "copy-skip.txt");
        private static string PendingFile => GuardDir == null ? null : Path.Combine(GuardDir, "copy-pending.txt");

        // 건너뛸 목록에 적는 이름표. 게임 코드가 바뀌어도 같은 메서드면 같은 이름표가 나와야 한다.
        internal static string Key(MethodBase method) =>
            $"{method.Module.Assembly.GetName().Name}|{Stable(method.DeclaringType?.FullName)}|{Stable(method.ToString())}";

        // 컴파일러가 만든 이름(<Run>d__229, <>c__DisplayClass87_0, <Run>b__87_0, <Run>g__Make|5_0)에서 순번을 뗀다.
        // 순번은 그 클래스에서 몇 번째 멤버인가라서, 위쪽에 코드가 한 줄 추가되면 밀린다 — 순번째 적으면 게임이 업데이트될 때마다
        // 건너뛰던 메서드를 다시 시도하게 된다. 떼면 한 메서드 안의 람다들이 같은 이름표를 쓰지만, 함께 건너뛸 뿐이다.
        internal static string Stable(string name) =>
            name == null ? "" : System.Text.RegularExpressions.Regex.Replace(name, @"(>[a-z]__(?:\w*\||[A-Za-z]+)?)\d+(?:_\d+)*", "$1");

        private static void LoadGuard()
        {
            if (_skip != null && _guardLoadedFor == GuardDir) return;
            _skip = new HashSet<string>(StringComparer.Ordinal);
            _guardLoadedFor = GuardDir;
            if (GuardDir == null) return;
            try
            {
                string header = "rev " + Revision;
                if (File.Exists(SkipFile))
                {
                    string[] lines = File.ReadAllLines(SkipFile);
                    if (lines.Length > 0 && lines[0] == header)
                        for (int i = 1; i < lines.Length; i++)
                            if (lines[i].Length > 0) _skip.Add(lines[i]);
                }
                if (File.Exists(PendingFile))
                {
                    string crashed = File.ReadAllText(PendingFile).Trim();
                    if (crashed.Length > 0) _skip.Add(crashed);
                    File.Delete(PendingFile);
                }
                if (_skip.Count > 0)
                {
                    var all = new List<string> { header };
                    all.AddRange(_skip);
                    Directory.CreateDirectory(GuardDir);
                    File.WriteAllLines(SkipFile, all.ToArray());
                }
                else if (File.Exists(SkipFile)) File.Delete(SkipFile);   // 방식이 바뀌어 비워진 옛 목록
            }
            catch { }   // 표시를 읽고 쓰지 못해도 패치는 그대로 한다
        }

        // key 를 "지금 바꾸는 중" 으로 적는다. null 이면 지운다.
        private static void MarkPending(string key)
        {
            if (GuardDir == null) return;
            try
            {
                if (key == null) { if (File.Exists(PendingFile)) File.Delete(PendingFile); }
                else
                {
                    Directory.CreateDirectory(GuardDir);
                    File.WriteAllText(PendingFile, key);
                }
            }
            catch { }
        }
    }
}
