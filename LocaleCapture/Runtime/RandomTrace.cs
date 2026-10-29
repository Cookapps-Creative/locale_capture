using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace CookApps.LocaleCapture
{
    // 게임 스크립트의 UnityEngine.Random 호출을 바꿔 끼운다.
    //  · 어긋남 검사: 프레임마다 누가 난수를 썼는지 남긴다. 기록은 manifest 에, 재생은 메모리에 두고, 난수 상태가 처음 갈라진 프레임에서 비교해 보고한다.
    //  · 소리 분리: 소리 코드(호출 경로에 Audio·Sound 가 든 타입)가 쓰는 난수는 따로 둔 상태로 돌린다. 소리는 기록(실시간 재생)과
    //    재생(프레임 단위 렌더)에서 끝나는 때가 달라 재생 개수가 하나씩 어긋나는데, 같은 난수를 쓰면 그 뒤 게임 판정(명중·치명타)까지 갈라진다.
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
        private static Random.State _main, _sound;

        internal static void Begin(List<Call> into)
        {
            _calls = into;
            _calls.Clear();
            _frame = 0;
            Random.State main = Random.state;
            Random.InitState(0x50D);
            _sound = Random.state;
            Random.state = main;
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

        // 소리 코드면 소리 쪽 난수 상태로 바꿔 끼우고 true. 아니면 호출을 남긴다. 꺼낸 뒤 Leave 로 되돌린다.
        private static bool Enter()
        {
            if (_calls == null) return false;
            string caller = Caller(out bool sound);
            if (sound)
            {
                _main = Random.state;
                Random.state = _sound;
                return true;
            }
            if (_calls.Count < MaxCalls) _calls.Add(new Call { Frame = _frame, Caller = caller });
            return false;
        }

        private static void Leave(bool sound)
        {
            if (!sound) return;
            _sound = Random.state;
            Random.state = _main;
        }

        // 이 클래스·Unity·시스템을 뺀 호출자 3단계 "타입.메서드 ← 타입.메서드 ← …". 그중 Audio·Sound 가 든 타입이 있으면 소리 코드.
        private static string Caller(out bool sound)
        {
            sound = false;
            StackFrame[] frames = new StackTrace(3, false).GetFrames();
            if (frames == null) return "?";
            var chain = new List<string>();
            foreach (StackFrame f in frames)
            {
                System.Reflection.MethodBase m = f.GetMethod();
                Type t = m?.DeclaringType;
                if (t == null) continue;
                string ns = t.Namespace ?? "";
                if (ns.StartsWith("UnityEngine") || ns.StartsWith("System") || ns.StartsWith("CookApps.LocaleCapture") || ns.StartsWith("Cysharp")) continue;
                string full = t.FullName ?? t.Name;
                if (full.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 || full.IndexOf("Sound", StringComparison.OrdinalIgnoreCase) >= 0) sound = true;
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

        public static int RangeInt(int minInclusive, int maxExclusive) { bool s = Enter(); int r = Random.Range(minInclusive, maxExclusive); Leave(s); return r; }
        public static float RangeFloat(float minInclusive, float maxInclusive) { bool s = Enter(); float r = Random.Range(minInclusive, maxInclusive); Leave(s); return r; }
        public static float Value { get { bool s = Enter(); float r = Random.value; Leave(s); return r; } }
        public static Vector2 InsideUnitCircle { get { bool s = Enter(); Vector2 r = Random.insideUnitCircle; Leave(s); return r; } }
        public static Vector3 InsideUnitSphere { get { bool s = Enter(); Vector3 r = Random.insideUnitSphere; Leave(s); return r; } }
        public static Vector3 OnUnitSphere { get { bool s = Enter(); Vector3 r = Random.onUnitSphere; Leave(s); return r; } }
        public static Quaternion Rotation { get { bool s = Enter(); Quaternion r = Random.rotation; Leave(s); return r; } }
        public static Quaternion RotationUniform { get { bool s = Enter(); Quaternion r = Random.rotationUniform; Leave(s); return r; } }
        public static void InitState(int seed) { bool s = Enter(); Random.InitState(seed); Leave(s); }
        public static Color ColorHSV0() { bool s = Enter(); Color r = Random.ColorHSV(); Leave(s); return r; }
        public static Color ColorHSV2(float h0, float h1) { bool s = Enter(); Color r = Random.ColorHSV(h0, h1); Leave(s); return r; }
        public static Color ColorHSV4(float h0, float h1, float s0, float s1) { bool s = Enter(); Color r = Random.ColorHSV(h0, h1, s0, s1); Leave(s); return r; }
        public static Color ColorHSV6(float h0, float h1, float s0, float s1, float v0, float v1) { bool s = Enter(); Color r = Random.ColorHSV(h0, h1, s0, s1, v0, v1); Leave(s); return r; }
        public static Color ColorHSV8(float h0, float h1, float s0, float s1, float v0, float v1, float a0, float a1) { bool s = Enter(); Color r = Random.ColorHSV(h0, h1, s0, s1, v0, v1, a0, a1); Leave(s); return r; }
    }
}
