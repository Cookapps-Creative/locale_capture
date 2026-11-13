using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace CookApps.LocaleCapture
{
    // 누를 때마다 그 자리에서 UI 가 무엇을 맞췄고 누가 그 입력을 받는지 남긴다. 기록과 재생을 비교해, 같은 입력이 다른 대상에 들어가
    // 플레이가 갈린 곳(가린 팝업, 아직 안 뜬 버튼 등)을 바로 보여 준다. 난수를 안 쓰는 화면의 어긋남도 잡는다.
    public static class TapProbe
    {
        [Serializable]
        public struct Tap
        {
            public int Frame;
            public string Target;   // 맨 위에 맞은 대상
            public string Detail;   // 좌표와 그 아래 대상들(진단용)
            public string Handler;  // 실제로 입력을 받는 대상(누름|클릭|드래그). 이전 기록에는 없다
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
                string target = Hit(position, out string under, out string handler);
                into.Add(new Tap { Frame = frame, Target = target, Handler = handler, Detail = $"({position.x:F0},{position.y:F0}) {device.name} 아래: {under}" });
                return;   // 한 프레임에 하나면 충분하다(터치 시뮬레이션은 마우스와 터치가 함께 눌린다)
            }
        }

        private static string Hit(Vector2 position, out string under, out string handler)
        {
            under = "";
            handler = "";
            EventSystem system = EventSystem.current;
            if (system == null) return "UI 없음";
            _hits.Clear();
            system.RaycastAll(new PointerEventData(system) { position = position }, _hits);
            var below = new List<string>();
            for (int i = 1; i < _hits.Count && i < 4; i++) below.Add(PathOf(_hits[i].gameObject.transform));
            under = string.Join(" > ", below);
            if (_hits.Count == 0) return "빈 곳";
            // 맨 위에 맞은 것은 언어마다 다를 수 있다(문구가 길어져 버튼을 덮는 텍스트 등). 입력은 그 위로 올라가며
            // 처리기가 달린 첫 대상이 받으므로, 같은 플레이인지는 그 대상으로 가린다.
            GameObject top = _hits[0].gameObject;
            handler = HandlerOf<IPointerDownHandler>(top) + "|" + HandlerOf<IPointerClickHandler>(top) + "|" + HandlerOf<IDragHandler>(top);
            return PathOf(top.transform);
        }

        private static string HandlerOf<T>(GameObject top) where T : IEventSystemHandler
        {
            GameObject found = ExecuteEvents.GetEventHandler<T>(top);
            return found == null ? "-" : PathOf(found.transform);
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
                // 양쪽 다 입력 받는 대상을 남겼으면 그것으로, 아니면(이전 기록·빈 곳) 맨 위 대상으로 비교한다.
                bool byHandler = found && !string.IsNullOrEmpty(t.Handler) && !string.IsNullOrEmpty(got.Handler);
                if (!found || (byHandler ? got.Handler != t.Handler : got.Target != t.Target))
                    issues.Add($"{t.Frame}: 녹화 [{t.Target}] {t.Detail} | 재생 [{(found ? got.Target : "안 눌림")}] {(found ? got.Detail : "")}");
            }
            return issues;
        }
    }
}
