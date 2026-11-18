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
        private static int _latchedFrame = -1;
        private static Vector2 _latchedPosition;
        private static string _latchedDevice;

        // 재생에서 Unity 프레임마다 부른다. 로딩 대기로 세션 프레임 하나가 여러 Unity 프레임에 걸치면, 눌림은 그 첫 Unity 프레임에만
        // 보이고 Check 는 대기가 풀린 뒤(나중 Unity 프레임)에 돈다 — 눌린 것을 붙잡아 두지 않으면 "안 눌림" 으로 잘못 나온다.
        internal static void Latch(int frame)
        {
            if (_latchedFrame == frame) return;   // 이 세션 프레임의 첫 눌림을 지킨다
            if (!Pressed(out Vector2 position, out string device)) return;
            _latchedFrame = frame;
            _latchedPosition = position;
            _latchedDevice = device;
        }

        internal static void Reset() => _latchedFrame = -1;

        // 프레임 끝에 부른다. 이번 프레임에 눌린 포인터(마우스·터치)마다 하나.
        internal static void Check(int frame, List<Tap> into)
        {
            if (into == null) return;
            bool latched = _latchedFrame == frame;
            _latchedFrame = -1;
            if (!Pressed(out Vector2 position, out string device))
            {
                if (!latched) return;
                position = _latchedPosition;   // 대기 프레임에 눌렸다 — 대상은 지금(대기가 풀린 뒤, 기록과 같은 시점) 가린다
                device = _latchedDevice;
            }
            string target = Hit(position, out string under, out string handler);
            into.Add(new Tap { Frame = frame, Target = target, Handler = handler, Detail = $"({position.x:F0},{position.y:F0}) {device} 아래: {under}" });
        }

        // 이번 Unity 프레임에 눌린 포인터. 한 프레임에 하나면 충분하다(터치 시뮬레이션은 마우스와 터치가 함께 눌린다).
        private static bool Pressed(out Vector2 position, out string device)
        {
            foreach (InputDevice d in InputSystem.devices)
            {
                if (!(d is Pointer pointer) || !d.enabled || !pointer.press.wasPressedThisFrame) continue;
                position = pointer.position.ReadValue();
                device = d.name;
                return true;
            }
            position = default;
            device = null;
            return false;
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
        internal static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; i < 3 && t != null; i++, t = t.parent) parts.Insert(0, t.name.Replace("(Clone)", ""));
            return string.Join("/", parts);
        }

        // 기록과 재생의 누른 대상을 프레임별로 맞춰 본다. 다른 곳들을 "프레임 녹화[..] 재생[..]" 로, 그 프레임은 frames 에.
        // handlersRecorded: 기록에 "입력을 받는 대상" 이 남아 있는가. 없으면(이전 기록) 맨 위 대상으로만 가려, 언어마다 문구가
        // 길어져 다른 그래픽이 맞은 것도 다르다고 나올 수 있다.
        internal static List<string> Compare(List<Tap> recorded, List<Tap> replayed, List<int> frames, bool handlersRecorded)
        {
            var issues = new List<string>();
            if (recorded == null || replayed == null) return issues;
            var byFrame = new Dictionary<int, Tap>();
            foreach (Tap t in replayed) byFrame[t.Frame] = t;
            foreach (Tap t in recorded)
            {
                bool found = byFrame.TryGetValue(t.Frame, out Tap got);
                if (found && Same(t, got, handlersRecorded)) continue;
                issues.Add($"{t.Frame}: 녹화 [{t.Target}] {t.Detail} | 재생 [{(found ? got.Target : "안 눌림")}] {(found ? got.Detail : "")}");
                frames?.Add(t.Frame);
            }
            return issues;
        }

        private static bool Same(Tap recorded, Tap replayed, bool handlersRecorded)
        {
            if (!handlersRecorded) return recorded.Target == replayed.Target;
            bool a = string.IsNullOrEmpty(recorded.Handler), b = string.IsNullOrEmpty(replayed.Handler);
            if (a != b) return false;                                    // 한쪽은 UI 를 맞췄고 한쪽은 빈 곳
            return a ? recorded.Target == replayed.Target : recorded.Handler == replayed.Handler;
        }
    }
}
