using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CookApps.LocaleCapture
{
    // 어긋남 검사용. 게임 스크립트의 UnityEngine.Random 호출을 바꿔 끼워, 프레임마다 누가 난수를 썼는지 남긴다.
    // 기록은 manifest 에, 재생은 메모리에 두고, 난수 상태가 처음 갈라진 프레임에서 둘을 비교해 보고한다.
    public static class RandomTrace
    {
        [Serializable]
        public struct Call
        {
            public int Frame;
            public string Caller;
        }

        private const int MaxCalls = 5000;
        private static List<Call> _calls;
        private static int _frame;

        internal static void Begin(List<Call> into)
        {
            _calls = into;
            _calls.Clear();
            _frame = 0;
        }

        internal static List<Call> End()
        {
            List<Call> calls = _calls;
            _calls = null;
            return calls;
        }

        internal static void SetFrame(int frame) => _frame = frame;

        // 기록·재생 호출 중 frame 근처(앞 2프레임부터 뒤 60프레임까지 처음 3개)를 한 줄로.
        internal static string Around(List<Call> calls, int frame)
        {
            if (calls == null) return "-";
            var parts = new List<string>();
            foreach (Call c in calls)
                if (c.Frame >= frame - 2 && c.Frame <= frame + 60 && parts.Count < 3) parts.Add($"{c.Frame}: {c.Caller}");
            return parts.Count == 0 ? "없음" : string.Join(" / ", parts);
        }

        private static void Hit()
        {
            if (_calls == null || _calls.Count >= MaxCalls) return;
            _calls.Add(new Call { Frame = _frame, Caller = Caller() });
        }

        // 이 클래스·Unity·시스템을 뺀 호출자 3단계 "타입.메서드 ← 타입.메서드 ← …"
        private static string Caller()
        {
            StackFrame[] frames = new StackTrace(2, false).GetFrames();
            if (frames == null) return "?";
            var chain = new List<string>();
            foreach (StackFrame f in frames)
            {
                System.Reflection.MethodBase m = f.GetMethod();
                Type t = m?.DeclaringType;
                if (t == null) continue;
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("UnityEngine") || ns.StartsWith("System") || ns.StartsWith("CookApps.LocaleCapture") || ns.StartsWith("Cysharp")) continue;
                string name = Name(t, m.Name);
                if (chain.Count == 0 || chain[chain.Count - 1] != name) chain.Add(name);
                if (chain.Count == 3) break;
            }
            return chain.Count == 0 ? "?" : string.Join(" ← ", chain);
        }

        private static string Name(Type t, string method)
        {
            string type = t.FullName ?? t.Name;
            int nested = type.IndexOf('+');   // async·람다는 바깥 타입.메서드로
            if (nested > 0 && type.IndexOf('<', nested) > 0)
            {
                int open = type.IndexOf('<', nested), close = type.IndexOf('>', open);
                if (close > open) method = type.Substring(open + 1, close - open - 1);
                type = type.Substring(0, nested);
            }
            if (method.StartsWith(type + ".")) method = method.Substring(type.Length + 1);   // 패치된 메서드 이름 "타입.메서드_PatchN"
            int patch = method.LastIndexOf("_Patch", StringComparison.Ordinal);
            if (patch > 0) method = method.Substring(0, patch);
            int dot = type.LastIndexOf('.');
            return (dot >= 0 ? type.Substring(dot + 1) : type) + "." + method;
        }

        // ── 바꿔 끼울 호출(에디터 패치가 게임 스크립트의 UnityEngine.Random 호출을 이쪽으로) ──

        public static int RangeInt(int minInclusive, int maxExclusive) { Hit(); return Random.Range(minInclusive, maxExclusive); }
        public static float RangeFloat(float minInclusive, float maxInclusive) { Hit(); return Random.Range(minInclusive, maxInclusive); }
        public static float Value { get { Hit(); return Random.value; } }
        public static Vector2 InsideUnitCircle { get { Hit(); return Random.insideUnitCircle; } }
        public static Vector3 InsideUnitSphere { get { Hit(); return Random.insideUnitSphere; } }
        public static Vector3 OnUnitSphere { get { Hit(); return Random.onUnitSphere; } }
        public static Quaternion Rotation { get { Hit(); return Random.rotation; } }
        public static Quaternion RotationUniform { get { Hit(); return Random.rotationUniform; } }
        public static void InitState(int seed) { Hit(); Random.InitState(seed); }
        public static Color ColorHSV0() { Hit(); return Random.ColorHSV(); }
        public static Color ColorHSV2(float h0, float h1) { Hit(); return Random.ColorHSV(h0, h1); }
        public static Color ColorHSV4(float h0, float h1, float s0, float s1) { Hit(); return Random.ColorHSV(h0, h1, s0, s1); }
        public static Color ColorHSV6(float h0, float h1, float s0, float s1, float v0, float v1) { Hit(); return Random.ColorHSV(h0, h1, s0, s1, v0, v1); }
        public static Color ColorHSV8(float h0, float h1, float s0, float s1, float v0, float v1, float a0, float a1) { Hit(); return Random.ColorHSV(h0, h1, s0, s1, v0, v1, a0, a1); }
    }
}
