using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace CookApps.LocaleCapture
{
    // 누를 때마다 그 자리에서 UI 가 무엇을 맞췄는지 남긴다. 기록과 재생을 비교해, 같은 입력이 다른 대상에 들어가
    // 플레이가 갈린 곳(가린 팝업, 아직 안 뜬 버튼 등)을 바로 보여 준다. 난수를 안 쓰는 화면의 어긋남도 잡는다.
    public static class TapProbe
    {
        [Serializable]
        public struct Tap
        {
            public int Frame;
            public string Target;   // 맨 위에 맞은 대상
            public string Detail;   // 좌표와 그 아래 대상들(진단용)
        }

        private static readonly List<RaycastResult> _hits = new List<RaycastResult>();

        // 프레임 끝에 부른다. 이번 프레임에 눌린 포인터(마우스·터치)마다 하나.
        internal static void Check(int frame, List<Tap> into)
        {
            if (into == null) return;
            foreach (InputDevice device in InputSystem.devices)
            {
                if (!(device is Pointer pointer) || !device.enabled || !pointer.press.wasPressedThisFrame) continue;
                Vector2 position = pointer.position.ReadValue();
                into.Add(new Tap { Frame = frame, Target = Hit(position, out string under), Detail = $"({position.x:F0},{position.y:F0}) {device.name} 아래: {under}" });
                return;   // 한 프레임에 하나면 충분하다(터치 시뮬레이션은 마우스와 터치가 함께 눌린다)
            }
        }

        private static string Hit(Vector2 position, out string under)
        {
            under = "";
            EventSystem system = EventSystem.current;
            if (system == null) return "UI 없음";
            _hits.Clear();
            system.RaycastAll(new PointerEventData(system) { position = position }, _hits);
            var below = new List<string>();
            for (int i = 1; i < _hits.Count && i < 4; i++) below.Add(PathOf(_hits[i].gameObject.transform));
            under = string.Join(" > ", below);
            return _hits.Count == 0 ? "빈 곳" : PathOf(_hits[0].gameObject.transform);
        }

        // 아래에서 세 단계까지 "부모/자식/대상" (복제 표시는 뗀다)
        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 3 && t != null; i++, t = t.parent) parts.Insert(0, t.name.Replace("(Clone)", ""));
            return string.Join("/", parts);
        }

        // 기록과 재생의 누른 대상을 프레임별로 맞춰 본다. 다른 곳들을 "프레임 녹화[..] 재생[..]" 로.
        internal static List<string> Compare(List<Tap> recorded, List<Tap> replayed)
        {
            var issues = new List<string>();
            if (recorded == null || replayed == null) return issues;
            var byFrame = new Dictionary<int, Tap>();
            foreach (Tap t in replayed) byFrame[t.Frame] = t;
            foreach (Tap t in recorded)
            {
                bool found = byFrame.TryGetValue(t.Frame, out Tap got);
                if (!found || got.Target != t.Target)
                    issues.Add($"{t.Frame}: 녹화 [{t.Target}] {t.Detail} | 재생 [{(found ? got.Target : "안 눌림")}] {(found ? got.Detail : "")}");
            }
            return issues;
        }
    }
}
