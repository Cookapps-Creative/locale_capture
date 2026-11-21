using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

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
            public int Gesture;     // 이 누름이 끌기가 됐나: GestureTap·GestureDrag. 0 은 모름(이 값을 남기기 전의 기록)
        }

        internal const int GestureTap = 1;    // 끌기가 되지 않았다(떼면 클릭이 될 수 있다)
        internal const int GestureDrag = 2;   // 눌린 채로 끌기 기준 이상 움직였다

        private static readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private static int _latchedFrame = -1;
        private static Vector2 _latchedPosition;
        private static string _latchedDevice;
        private static Pointer _latchedPointer;
        // 아직 눌려 있는 누름(끌기가 되는지 지켜본다): 포인터, 누른 자리, 그 누름이 들어 있는 목록과 순번
        private static Pointer _held;
        private static Vector2 _heldFrom;
        private static List<Tap> _heldList;
        private static int _heldIndex;

        // 재생에서 Unity 프레임마다 부른다. 로딩 대기로 세션 프레임 하나가 여러 Unity 프레임에 걸치면, 눌림은 그 첫 Unity 프레임에만
        // 보이고 Check 는 대기가 풀린 뒤(나중 Unity 프레임)에 돈다 — 눌린 것을 붙잡아 두지 않으면 "안 눌림" 으로 잘못 나온다.
        internal static void Latch(int frame)
        {
            if (_latchedFrame == frame) return;   // 이 세션 프레임의 첫 눌림을 지킨다
            if (!Pressed(out Vector2 position, out Pointer pointer)) return;
            _latchedFrame = frame;
            _latchedPosition = position;
            _latchedDevice = pointer.name;
            _latchedPointer = pointer;
        }

        internal static void Reset()
        {
            _latchedFrame = -1;
            _latchedPointer = null;
            _held = null;
            _heldList = null;
        }

        // 프레임 끝에 부른다. 이번 프레임에 눌린 포인터(마우스·터치)마다 하나.
        internal static void Check(int frame, List<Tap> into)
        {
            if (into == null) return;
            Follow(into);
            bool latched = _latchedFrame == frame;
            _latchedFrame = -1;
            string device;
            if (Pressed(out Vector2 position, out Pointer pointer)) device = pointer.name;
            else
            {
                if (!latched) return;
                position = _latchedPosition;   // 대기 프레임에 눌렸다 — 대상은 지금(대기가 풀린 뒤, 기록과 같은 시점) 가린다
                device = _latchedDevice;
                pointer = _latchedPointer;
            }
            string target = Hit(position, out string under, out string handler);
            into.Add(new Tap { Frame = frame, Target = target, Handler = handler, Gesture = GestureTap, Detail = $"({position.x:F0},{position.y:F0}) {device} 아래: {under}" });
            _held = pointer;
            _heldFrom = position;
            _heldList = into;
            _heldIndex = into.Count - 1;
        }

        // 지켜보던 누름이 끌기가 됐는지 본다. uGUI 입력 모듈은 눌린 채로 끌기 기준(EventSystem.pixelDragThreshold) 이상 움직인 프레임에
        // 끌기를 시작하고, 끌기를 받는 대상이 누름을 받은 대상과 다르면 그때 클릭을 취소한다. 뗀 프레임에는 클릭을 먼저 가리고 끌기를
        // 나중에 보므로, 뗀 프레임에 움직인 것은 세지 않는다 — 여기서 끌기라고 적는 것은 엔진이 클릭을 취소한 경우뿐이어야 한다.
        private static void Follow(List<Tap> into)
        {
            if (_held == null) return;
            ButtonControl press = _held.press;
            if (_heldList != into || _heldIndex >= into.Count || !_held.added || !_held.enabled ||
                !press.isPressed || press.wasPressedThisFrame || press.wasReleasedThisFrame)
            {
                _held = null;   // 뗐거나(다시 눌렀거나) 장치가 사라졌다 — 이 누름은 끝났다
                return;
            }
            // 잠긴 마우스 커서로는 엔진이 끌기를 시작하지 않는다
            if (Cursor.lockState == CursorLockMode.Locked && !(_held is Touchscreen)) return;
            EventSystem system = EventSystem.current;
            if (system == null) return;
            float threshold = system.pixelDragThreshold;
            if ((_held.position.ReadValue() - _heldFrom).sqrMagnitude < threshold * threshold) return;
            Tap tap = into[_heldIndex];
            tap.Gesture = GestureDrag;
            into[_heldIndex] = tap;
            _held = null;   // 한 번 끌기가 되면 뗄 때까지 끌기다
        }

        // 이번 Unity 프레임에 눌린 포인터. 한 프레임에 하나면 충분하다(터치 시뮬레이션은 마우스와 터치가 함께 눌린다).
        private static bool Pressed(out Vector2 position, out Pointer pressed)
        {
            foreach (InputDevice d in InputSystem.devices)
            {
                if (!(d is Pointer pointer) || !d.enabled || !pointer.press.wasPressedThisFrame) continue;
                position = pointer.position.ReadValue();
                pressed = pointer;
                return true;
            }
            position = default;
            pressed = null;
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
        // notes: 끌기가 돼 클릭을 받을 대상을 견주지 않은 누름 "프레임: 내용"(다름이 아니다 — 무엇을 안 봤는지 남긴다).
        internal static List<string> Compare(List<Tap> recorded, List<Tap> replayed, List<int> frames, bool handlersRecorded, List<string> notes)
        {
            var issues = new List<string>();
            if (recorded == null || replayed == null) return issues;
            var byFrame = new Dictionary<int, Tap>();
            foreach (Tap t in replayed) byFrame[t.Frame] = t;
            foreach (Tap t in recorded)
            {
                bool found = byFrame.TryGetValue(t.Frame, out Tap got);
                if (found && Same(t, got, handlersRecorded, out string note))
                {
                    if (note != null) notes?.Add($"{t.Frame}: {note}");
                    continue;
                }
                issues.Add($"{t.Frame}: 녹화 [{t.Target}] {t.Detail} | 재생 [{(found ? got.Target : "안 눌림")}] {(found ? got.Detail : "")}");
                frames?.Add(t.Frame);
            }
            return issues;
        }

        private static bool Same(Tap recorded, Tap replayed, bool handlersRecorded, out string note)
        {
            note = null;
            if (!handlersRecorded) return Neutral(recorded.Target) == Neutral(replayed.Target);
            bool a = string.IsNullOrEmpty(recorded.Handler), b = string.IsNullOrEmpty(replayed.Handler);
            if (a != b) return false;                                    // 한쪽은 UI 를 맞췄고 한쪽은 빈 곳
            if (a) return Neutral(recorded.Target) == Neutral(replayed.Target);
            string rec = Neutral(recorded.Handler), rep = Neutral(replayed.Handler);
            if (rec == rep) return true;
            return ClickNeverSent(recorded, replayed, rec, rep, out note);
        }

        // 입력을 받는 대상 가운데 "클릭을 받을 대상" 만 다르고, 그 클릭이 어느 쪽에서도 전달되지 않았나.
        // 끌기가 된 누름이 그렇다: 엔진은 끌기를 시작하면서 클릭을 취소한다(끌기를 받는 대상이 누름을 받은 대상과 다를 때). 스크롤 목록의
        // 줄 높이가 언어마다 달라, 같은 자리에서 끌기를 시작해도 한 언어는 빈 곳·다른 언어는 줄 위일 수 있다 — 둘 다 목록이 끌렸을 뿐이다.
        // 누름·끌기를 받는 대상은 실제로 입력을 받으므로 그대로 같아야 하고, 끌기가 되지 않은 누름(탭)은 클릭을 받을 대상도 같아야 한다.
        private static bool ClickNeverSent(Tap recorded, Tap replayed, string rec, string rep, out string note)
        {
            note = null;
            // 끌기 여부는 기록과 재생이 모두 끌기여야 한다. 기록에 그 값이 없으면(이전 기록) 재생에서 잰 것으로 본다 — 재생은 같은 입력을 다시 넣는다.
            if (replayed.Gesture != GestureDrag || (recorded.Gesture != GestureDrag && recorded.Gesture != 0)) return false;
            string[] x = rec.Split('|'), y = rep.Split('|');
            if (x.Length != 3 || y.Length != 3) return false;                  // 이름에 | 가 든 대상 — 나눠 볼 수 없다
            if (x[0] != y[0] || x[2] != y[2] || x[2] == "-") return false;     // 누름·끌기를 받는 대상이 다르거나, 끌기를 받는 대상이 없다(클릭이 취소되지 않는다)
            if (PressOf(x) == x[2] || PressOf(y) == y[2]) return false;        // 누름을 받은 대상이 끌기도 받으면 엔진이 클릭을 취소하지 않는다
            note = $"누름이 끌기가 돼 클릭은 전달되지 않았다 — 클릭을 받을 대상(녹화 {Named(x[1])}, 재생 {Named(y[1])})은 견주지 않았다";
            return true;
        }

        // 엔진이 "누름을 받은 대상" 으로 치는 것: 누름 처리기가 있는 대상, 없으면 클릭 처리기가 있는 대상
        private static string PressOf(string[] handler) => handler[0] != "-" ? handler[0] : handler[1];

        private static string Named(string handler) => handler == "-" ? "없음" : handler;

        // 엔진이 오브젝트 이름에 화면 문구를 넣은 것을 뗀다. uGUI·TMP 의 Dropdown 은 펼친 항목을 "Item 3: <선택지 문구>" 로 이름 짓는데,
        // 문구는 언어마다 달라 그대로 견주면 드롭다운을 연 것만으로 다른 대상이 된다. 순번("Item 3")은 남긴다.
        // path: "부모/자식/대상" 또는 그것들을 | 로 이은 것. 끝의 " (잠김)" 은 그대로 둔다.
        internal static string Neutral(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf("Item ", StringComparison.Ordinal) < 0) return path;
            string[] parts = path.Split('|');
            for (int p = 0; p < parts.Length; p++)
            {
                string[] segments = parts[p].Split('/');
                for (int s = 0; s < segments.Length; s++) segments[s] = NeutralSegment(segments[s]);
                parts[p] = string.Join("/", segments);
            }
            return string.Join("|", parts);
        }

        private const string LockedSuffix = " (잠김)";

        private static string NeutralSegment(string segment)
        {
            if (!segment.StartsWith("Item ", StringComparison.Ordinal)) return segment;
            int i = 5;
            while (i < segment.Length && segment[i] >= '0' && segment[i] <= '9') i++;
            if (i == 5 || i >= segment.Length || segment[i] != ':') return segment;
            return segment.Substring(0, i) + (segment.EndsWith(LockedSuffix, StringComparison.Ordinal) ? LockedSuffix : "");
        }
    }
}
