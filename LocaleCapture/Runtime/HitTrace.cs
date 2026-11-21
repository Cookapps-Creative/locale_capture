using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CookApps.LocaleCapture
{
    // 입력을 받을 수 있는 UI(클릭·누르기·드래그 처리기가 달린 오브젝트)가 켜지고 꺼진 프레임을 남긴다.
    // 누른 대상 비교(TapProbe)는 누른 순간만 보지만, 이쪽은 매 프레임이라 흐름이 녹화보다 밀리거나 당겨진 첫 프레임을 잡는다 —
    // 예: 턴 종료 버튼이 녹화는 812 프레임, 재생은 815 프레임에 켜짐. 처리기 오브젝트 이름으로 보므로 언어마다 달라지는
    // 문구·글자 크기와 무관하다. 단 스크롤 목록(ScrollRect) 안은 예외다 — 줄 높이가 글자 길이로 정해져, 같은 플레이라도 줄이 화면에
    // 들어오는 프레임·처음 보이는 줄 수·스크롤 막대가 뜨는지가 언어마다 다를 수 있다. 그래서 목록 안의 대상은 목록별로 따로 세고
    // 줄 크기를 함께 남기며, 목록의 내용·보이는 영역 크기도 따로 남긴다(ListState). 다른 언어 패스에서 목록 안이 녹화와 달라지면
    // 그 크기들이 녹화와 다르다는 근거가 있을 때만, 그 목록이 떠 있던 구간만 비교에서 뺀다(LayoutShifted).
    public static class HitTrace
    {
        [Serializable]
        public struct Event
        {
            public int Frame;
            public string Name;   // 처리기 오브젝트 "부모/자식/대상". 잠긴(interactable 이 꺼진) Selectable 은 뒤에 " (잠김)"
            public int Delta;     // 같은 이름의 개수 변화. + 켜짐, − 꺼짐
            // 스크롤 목록 안의 대상이면 그 목록(가장 바깥 ScrollRect) "부모/자식/대상". 목록 밖이면 비어 있다(이전 기록도 비어 있다).
            // 같은 이름이라도 목록 안의 것과 밖의 것, 서로 다른 목록의 것은 따로 센다.
            public string Scroll;
            // 목록 안일 때: 이 변화 뒤에 켜져 있는 대상들의 크기 묶음(순서 무관)을 섞은 값. 0 = 목록 밖, 1 = 모두 꺼짐.
            // 켜진 개수가 같은데 이 값이 녹화와 다르면 줄 크기가 다른 화면이다.
            public int Shape;
            // 목록 안일 때: 그 대상들의 (가로 + 세로) 합. 개수가 다른 두 화면의 평균 줄 크기를 견줄 때 쓴다.
            public float Size;
        }

        // 스크롤 목록의 크기가 바뀐 프레임(목록마다). 안쪽에 든 목록은 가장 바깥 목록에 합친다.
        [Serializable]
        public struct ListState
        {
            public int Frame;
            public string Name;     // 목록(가장 바깥 ScrollRect) "부모/자식/대상"
            public int View;        // 보이는 영역 크기를 섞은 값
            public int Content;     // 내용 크기를 섞은 값
            public int Children;    // 내용 바로 아래 켜진 자식 수. -1 = 목록이 꺼짐
        }

        // 줄 크기가 녹화와 달라 비교하지 않는 구간 하나: 그 목록이 떠 있던 동안의, 그 목록 안의 대상.
        public sealed class Shift
        {
            public string Scroll;
            public int From, To;   // 프레임(양 끝 포함)
            public string Note;    // 결과에 적을 설명

            public bool Covers(Event e) => e.Scroll == Scroll && e.Frame >= From && e.Frame <= To;
        }

        private sealed class Info
        {
            public GameObject Handler;
            public string Name, Locked;       // Locked = Name + " (잠김)"
            public string Key, LockedKey;     // 세는 단위 — 목록 밖이면 이름, 목록 안이면 이름 + 목록
            public Selectable Selectable;
            public CanvasGroup[] Groups;
            public RectTransform Rect;        // 처리기의 자리(목록 안일 때만 쓴다)
            public ScrollRect Scroll;         // 이 처리기가 들어 있는 가장 바깥 스크롤 목록. 목록 밖이면 null
            public string ScrollName;
        }

        private struct Row
        {
            public string Name, Scroll;
            public int Count;
            public ulong Sum;    // 켜진 대상 크기의 지문 합(더하기 — 순서와 무관)
            public float Size;
        }

        private struct Lay
        {
            public ulong View, Content;
            public int Children;
        }

        // 그래픽마다 처리기를 다시 찾는 주기. 풀링으로 부모가 바뀐 오브젝트의 낡은 이름이 이보다 오래 남지 않는다.
        // 한 프레임에 몰아서 다시 찾으면 그 프레임이 끊기므로, 목록 순번에 따라 프레임마다 1/30 씩 나눠 한다 —
        // 순번은 등록 순서라 기록과 재생이 같아서, 캐시가 낡은 구간도 양쪽이 똑같이 본다.
        private const int RefreshFrames = 30;
        private const int PurgeFrames = 900;    // 없어진 그래픽을 캐시에서 치우는 주기
        private const int MaxListStates = 20000;
        private const int EmptyShape = 1;       // 목록 안의 대상이 모두 꺼진 뒤
        private const ulong Seed = 14695981039346656037UL;
        private const float MeanSizeGap = 0.5f;   // 처음 보이는 줄의 평균 크기가 이만큼(캔버스 단위) 넘게 다르면 줄 크기가 다른 화면
        private static readonly Dictionary<Graphic, Info> _info = new Dictionary<Graphic, Info>();
        private static readonly HashSet<GameObject> _seen = new HashSet<GameObject>();
        private static Dictionary<string, Row> _prev = new Dictionary<string, Row>(), _now = new Dictionary<string, Row>();
        private static Dictionary<string, Lay> _prevLists = new Dictionary<string, Lay>(), _lists = new Dictionary<string, Lay>();
        private static readonly Dictionary<ScrollRect, string> _listNames = new Dictionary<ScrollRect, string>();
        private static bool _failed;

        // 추적이 꺼진 프레임(안 꺼졌으면 -1). 그 뒤로는 타임라인이 비어 있어 비교할 수 없다.
        public static int FailedFrame { get; private set; } = -1;

        internal static void Begin()
        {
            _info.Clear();
            _prev.Clear();
            _now.Clear();
            _prevLists.Clear();
            _lists.Clear();
            _listNames.Clear();
            _failed = false;
            FailedFrame = -1;
        }

        // 프레임 끝에 부른다. 지난 프레임과 달라진 것을 into 에, 크기가 바뀐 스크롤 목록을 lists 에 더한다.
        internal static void Check(int frame, List<Event> into, List<ListState> lists)
        {
            if (into == null || _failed) return;
            try { Collect(frame); }
            catch (Exception e)
            {
                _failed = true;
                FailedFrame = frame;
                Debug.LogWarning($"[LocaleCapture] 입력 받는 UI 추적을 껐다(프레임 {frame} 부터는 그 검사 없이 간다): " + e.Message);
                return;
            }
            foreach (KeyValuePair<string, Row> pair in _now)
            {
                Row row = pair.Value;
                _prev.TryGetValue(pair.Key, out Row before);
                if (row.Count == before.Count) continue;
                bool scrolled = row.Scroll != null;
                into.Add(new Event { Frame = frame, Name = row.Name, Delta = row.Count - before.Count, Scroll = row.Scroll, Shape = scrolled ? Fold(row.Sum) : 0, Size = scrolled ? row.Size : 0f });
            }
            foreach (KeyValuePair<string, Row> pair in _prev)
            {
                if (_now.ContainsKey(pair.Key)) continue;
                Row row = pair.Value;
                into.Add(new Event { Frame = frame, Name = row.Name, Delta = -row.Count, Scroll = row.Scroll, Shape = row.Scroll != null ? EmptyShape : 0 });
            }
            (_prev, _now) = (_now, _prev);

            if (lists == null) return;
            foreach (KeyValuePair<string, Lay> pair in _lists)
            {
                Lay lay = pair.Value;
                if (_prevLists.TryGetValue(pair.Key, out Lay before) && before.View == lay.View && before.Content == lay.Content && before.Children == lay.Children) continue;
                if (lists.Count < MaxListStates) lists.Add(new ListState { Frame = frame, Name = pair.Key, View = Fold(lay.View), Content = Fold(lay.Content), Children = lay.Children });
            }
            foreach (KeyValuePair<string, Lay> pair in _prevLists)
                if (!_lists.ContainsKey(pair.Key) && lists.Count < MaxListStates) lists.Add(new ListState { Frame = frame, Name = pair.Key, Children = -1 });
            (_prevLists, _lists) = (_lists, _prevLists);
        }

        private static void Collect(int frame)
        {
            if (frame % PurgeFrames == PurgeFrames - 1) Purge();
            _now.Clear();
            _seen.Clear();
            foreach (GraphicRaycaster raycaster in UnityEngine.Object.FindObjectsByType<GraphicRaycaster>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!raycaster.enabled) continue;
                Canvas canvas = raycaster.GetComponent<Canvas>();
                if (canvas == null || !canvas.enabled) continue;
                IList<Graphic> graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
                for (int i = 0; i < graphics.Count; i++)
                {
                    Graphic g = graphics[i];
                    if (g == null || !g.raycastTarget) continue;
                    if (!_info.TryGetValue(g, out Info info) || (i + frame) % RefreshFrames == 0) _info[g] = info = Describe(g);
                    if (info.Handler == null || g.depth == -1 || Blocked(info)) continue;   // 처리기 없음·아직 안 그려짐·CanvasGroup 이 막음
                    if (!_seen.Add(info.Handler)) continue;
                    bool locked = info.Selectable != null && !info.Selectable.IsInteractable();
                    bool scrolled = info.Scroll != null && info.Rect != null;
                    string key = scrolled ? (locked ? info.LockedKey : info.Key) : (locked ? info.Locked : info.Name);
                    _now.TryGetValue(key, out Row row);
                    if (row.Count == 0)
                    {
                        row.Name = locked ? info.Locked : info.Name;
                        row.Scroll = scrolled ? info.ScrollName : null;
                    }
                    row.Count++;
                    if (scrolled)
                    {
                        Vector2 size = info.Rect.rect.size;
                        row.Sum += Mix(Seed, size);
                        row.Size += Finite(size.x) + Finite(size.y);
                    }
                    _now[key] = row;
                }
            }
            CollectLists(frame);
        }

        // 켜져 있는 스크롤 목록마다 보이는 영역·내용 크기와 내용 바로 아래 켜진 자식 수. 안쪽 목록은 가장 바깥 목록 이름으로 합친다.
        private static void CollectLists(int frame)
        {
            _lists.Clear();
            if (frame % RefreshFrames == 0) _listNames.Clear();
            foreach (ScrollRect scroll in UnityEngine.Object.FindObjectsByType<ScrollRect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (!scroll.isActiveAndEnabled) continue;
                if (!_listNames.TryGetValue(scroll, out string name)) _listNames[scroll] = name = TapProbe.PathOf(Outermost(scroll).transform);
                _lists.TryGetValue(name, out Lay lay);
                RectTransform view = scroll.viewport != null ? scroll.viewport : scroll.transform as RectTransform;
                if (view != null) lay.View += Mix(Seed, view.rect.size);
                RectTransform content = scroll.content;
                if (content != null)
                {
                    lay.Content += Mix(Seed, content.rect.size);
                    for (int i = 0; i < content.childCount; i++)
                        if (content.GetChild(i).gameObject.activeSelf) lay.Children++;
                }
                _lists[name] = lay;
            }
        }

        private static ulong Mix(ulong hash, Vector2 size) => Mix(Mix(hash, size.x), size.y);

        // 캔버스 단위 1 로 반올림한다 — 같은 배치는 실행마다 같은 값이 나오고, 줄 수가 달라진 줄은 그보다 훨씬 크게 다르다.
        private static ulong Mix(ulong hash, float value) => (hash ^ (uint) Mathf.RoundToInt(Finite(value))) * 1099511628211UL;

        private static float Finite(float value) => float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Clamp(value, -1000000f, 1000000f);

        private static int Fold(ulong sum)
        {
            int shape = (int) (sum ^ (sum >> 32));
            return shape == 0 || shape == EmptyShape ? 2 : shape;   // 0 = 목록 밖, 1 = 모두 꺼짐
        }

        private static void Purge()
        {
            var dead = new List<Graphic>();
            foreach (Graphic g in _info.Keys) if (g == null) dead.Add(g);
            foreach (Graphic g in dead) _info.Remove(g);
        }

        // 이 그래픽을 누르면 입력을 받을 오브젝트. 클릭 → 누르기 → 드래그 순으로 가장 가까운 처리기를 찾는다.
        private static Info Describe(Graphic g)
        {
            GameObject go = g.gameObject;
            GameObject handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go);
            if (handler == null) handler = ExecuteEvents.GetEventHandler<IPointerDownHandler>(go);
            if (handler == null) handler = ExecuteEvents.GetEventHandler<IDragHandler>(go);
            var info = new Info { Handler = handler };
            if (handler == null) return info;
            info.Name = TapProbe.PathOf(handler.transform);
            info.Locked = info.Name + " (잠김)";
            info.Selectable = handler.GetComponent<Selectable>();
            info.Groups = go.GetComponentsInParent<CanvasGroup>(true);   // 가까운 것부터
            info.Rect = handler.transform as RectTransform;
            info.Scroll = info.Rect != null ? ScrollOf(handler.transform) : null;
            if (info.Scroll == null) return info;
            info.ScrollName = TapProbe.PathOf(info.Scroll.transform);
            info.Key = info.Name + "\n" + info.ScrollName;
            info.LockedKey = info.Locked + "\n" + info.ScrollName;
            return info;
        }

        // 이 대상이 들어 있는 가장 바깥 스크롤 목록: 내용(content) 아래에 있거나 그 목록의 스크롤 막대인 ScrollRect. 없으면 null.
        // 이런 대상은 글자 길이로 정해지는 줄 높이·내용 길이에 따라 켜지고 꺼진다(화면에 들어온 줄, 재활용 목록이 만든 줄, 내용이 넘칠 때만 뜨는 막대).
        // 가장 바깥 것으로 묶는 까닭: 목록 안에 든 목록의 줄은 바깥 목록의 줄 높이에 따라서도 화면에 들어오고 나간다.
        private static ScrollRect ScrollOf(Transform handler)
        {
            ScrollRect found = null;
            for (Transform t = handler.parent; t != null;)
            {
                ScrollRect scroll = t.GetComponentInParent<ScrollRect>();
                if (scroll == null) break;
                if (Holds(scroll, handler)) found = scroll;
                t = scroll.transform.parent;
            }
            return found;
        }

        private static bool Holds(ScrollRect scroll, Transform t) =>
            (scroll.content != null && t != scroll.content && t.IsChildOf(scroll.content)) ||
            (scroll.verticalScrollbar != null && t.IsChildOf(scroll.verticalScrollbar.transform)) ||
            (scroll.horizontalScrollbar != null && t.IsChildOf(scroll.horizontalScrollbar.transform));

        // 이 목록을 내용 안에 담고 있는 가장 바깥 목록(없으면 자신).
        private static ScrollRect Outermost(ScrollRect scroll)
        {
            ScrollRect found = scroll;
            for (Transform t = scroll.transform.parent; t != null;)
            {
                ScrollRect up = t.GetComponentInParent<ScrollRect>();
                if (up == null) break;
                if (up.content != null && scroll.transform.IsChildOf(up.content)) found = up;
                t = up.transform.parent;
            }
            return found;
        }

        // 이전 형식 기록(이름별로만 센 것)과 견줄 수 있게, 프레임·이름이 같은 변화를 하나로 합친다.
        internal static List<Event> ByName(List<Event> events)
        {
            var merged = new List<Event>();
            var at = new Dictionary<(int frame, string name), int>();
            foreach (Event e in events)
            {
                if (at.TryGetValue((e.Frame, e.Name), out int i))
                {
                    Event m = merged[i];
                    m.Delta += e.Delta;
                    merged[i] = m;
                }
                else
                {
                    at[(e.Frame, e.Name)] = merged.Count;
                    merged.Add(new Event { Frame = e.Frame, Name = e.Name, Delta = e.Delta });
                }
            }
            merged.RemoveAll(e => e.Delta == 0);
            return merged;
        }

        // 스크롤 목록 안 대상의 켜짐·꺼짐을 녹화와 맞춰 보고, 줄 크기가 녹화와 달라 프레임을 비교할 수 없는 구간을 돌려준다.
        // 목록 안을 통째로 빼면 실제 어긋남을 놓치므로, 목록이 떠 있던 구간마다 따로, 근거가 있을 때만 뺀다.
        //  · 목록이 뜨고 꺼진 프레임이 녹화와 다르면 빼지 않는다(목록 자체가 늦게 뜬 것은 실제 어긋남이다).
        //  · 그 구간에서 녹화와 처음 달라진 변화를 찾고, 그 프레임까지의 기록에서 아래 근거 중 하나가 있어야 뺀다.
        //     1. 같은 프레임·같은 이름·같은 개수로 맞은 변화인데 켜져 있는 대상들의 크기 묶음이 다르다 — 같은 줄들이 켜져 있는데 크기가 다르다.
        //     2. 내용 바로 아래 자식 수는 같은데, 목록의 내용·보이는 영역 크기가 양쪽에 서로 없는 값이다. 한쪽에만 없는 값이면
        //        (같은 크기 변화가 한 프레임 늦게 온 것일 수 있어) 근거로 삼지 않는다.
        //     3. 처음 달라진 변화가 "그 이름이 처음 켜지는 프레임은 같은데 개수만 다른" 것이고 줄의 평균 크기가 다르다 —
        //        스크롤하기 전이라 무엇이 보이는지가 배치만으로 정해진다. 줄 크기가 같은데 개수만 다르면 내용이 다른 것이므로 빼지 않는다.
        //  · 근거가 없으면 돌려주지 않는다 — 배치는 같은데 프레임이 다른 것이므로 그대로 비교된다.
        // limit: 기록의 프레임 수(그 뒤 프레임은 보지 않는다).
        internal static List<Shift> LayoutShifted(List<Event> recorded, List<Event> replayed, List<ListState> recordedLists, List<ListState> replayedLists, int limit)
        {
            var shifts = new List<Shift>();
            if (recorded == null || replayed == null || recordedLists == null || replayedLists == null) return shifts;
            var scrolls = new List<string>();
            foreach (List<Event> events in new[] { recorded, replayed })
                foreach (Event e in events)
                    if (e.Frame < limit && !string.IsNullOrEmpty(e.Scroll) && !scrolls.Contains(e.Scroll)) scrolls.Add(e.Scroll);
            scrolls.Sort(string.CompareOrdinal);

            foreach (string scroll in scrolls)
            {
                List<(int from, int to)> spans = Spans(recordedLists, scroll, limit), other = Spans(replayedLists, scroll, limit);
                if (spans.Count != other.Count) continue;
                for (int s = 0; s < spans.Count; s++)
                {
                    if (spans[s].from != other[s].from || spans[s].to != other[s].to) continue;
                    Shift shift = Judge(scroll, spans[s].from, spans[s].to, recorded, replayed, recordedLists, replayedLists);
                    if (shift != null) shifts.Add(shift);
                }
            }
            return shifts;
        }

        // 그 목록이 떠 있던 구간들(양 끝 프레임 포함). 꺼진 프레임까지 넣는다 — 그 안의 대상도 그 프레임에 꺼진다.
        private static List<(int from, int to)> Spans(List<ListState> lists, string scroll, int limit)
        {
            var spans = new List<(int from, int to)>();
            int from = -1;
            foreach (ListState state in lists)
            {
                if (state.Name != scroll || state.Frame >= limit) continue;
                if (state.Children < 0)
                {
                    if (from >= 0) spans.Add((from, state.Frame));
                    from = -1;
                }
                else if (from < 0) from = state.Frame;
            }
            if (from >= 0) spans.Add((from, limit - 1));
            return spans;
        }

        private static Shift Judge(string scroll, int from, int to, List<Event> recorded, List<Event> replayed, List<ListState> recordedLists, List<ListState> replayedLists)
        {
            Dictionary<(int frame, string name), Event> a = Index(recorded, scroll, from, to), b = Index(replayed, scroll, from, to);
            // 처음 달라진 변화(프레임, 이름 순)
            bool differs = false;
            (int frame, string name) first = (int.MaxValue, "");
            for (int side = 0; side < 2; side++)
            {
                Dictionary<(int frame, string name), Event> one = side == 0 ? a : b, other = side == 0 ? b : a;
                foreach (KeyValuePair<(int frame, string name), Event> pair in one)
                {
                    if (other.TryGetValue(pair.Key, out Event same) && same.Delta == pair.Value.Delta) continue;
                    if (differs && !Before(pair.Key, first)) continue;
                    differs = true;
                    first = pair.Key;
                }
            }
            if (!differs) return null;

            string evidence = null;
            foreach (KeyValuePair<(int frame, string name), Event> pair in a)
            {
                if (pair.Key.frame > first.frame || !b.TryGetValue(pair.Key, out Event same) || same.Delta != pair.Value.Delta) continue;
                if (same.Shape == pair.Value.Shape) continue;
                evidence = $"프레임 {pair.Key.frame} 에 켜져 있던 {pair.Key.name} 들의 크기가 녹화와 다르다";
                break;
            }
            if (evidence == null && TwoSided(recordedLists, replayedLists, scroll, from, first.frame)) evidence = "목록의 내용·보이는 영역 크기가 녹화와 다르다";
            bool inA = a.TryGetValue(first, out Event recordedFirst), inB = b.TryGetValue(first, out Event replayedFirst);
            if (evidence == null && inA && inB && recordedFirst.Delta > 0 && replayedFirst.Delta > 0 && CountBefore(a, first) == 0 && CountBefore(b, first) == 0 &&
                Math.Abs(recordedFirst.Size / recordedFirst.Delta - replayedFirst.Size / replayedFirst.Delta) > MeanSizeGap)
                evidence = $"처음 보이는 줄의 평균 크기가 녹화와 다르다({recordedFirst.Size / recordedFirst.Delta:F0} 대 {replayedFirst.Size / replayedFirst.Delta:F0})";
            if (evidence == null) return null;

            return new Shift
            {
                Scroll = scroll, From = from, To = to,
                Note = $"스크롤 목록 '{scroll}' 의 줄 크기가 녹화 언어와 달라({evidence}), 프레임 {from}~{to} 동안 그 안의 대상이 화면에 들어오고 나간 프레임은 비교하지 않았다 — " +
                       $"처음 다른 곳 프레임 {first.frame} {first.name} (녹화 {(inA ? recordedFirst.Delta.ToString("+0;-0") : "없음")}, 재생 {(inB ? replayedFirst.Delta.ToString("+0;-0") : "없음")})",
            };
        }

        private static bool Before((int frame, string name) x, (int frame, string name) y) =>
            x.frame != y.frame ? x.frame < y.frame : string.CompareOrdinal(x.name, y.name) < 0;

        private static Dictionary<(int frame, string name), Event> Index(List<Event> events, string scroll, int from, int to)
        {
            var index = new Dictionary<(int frame, string name), Event>();
            foreach (Event e in events)
                if (e.Scroll == scroll && e.Frame >= from && e.Frame <= to) index[(e.Frame, e.Name)] = e;
            return index;
        }

        // 이 변화 전에 켜져 있던 같은 이름의 개수(같은 구간 안에서)
        private static int CountBefore(Dictionary<(int frame, string name), Event> events, (int frame, string name) key)
        {
            int count = 0;
            foreach (KeyValuePair<(int frame, string name), Event> pair in events)
                if (pair.Key.name == key.name && pair.Key.frame < key.frame) count += pair.Value.Delta;
            return count;
        }

        // 자식 수가 같은 상태끼리 견줘, 목록 크기가 양쪽에 서로 없는 값이 있는지(from~until 프레임).
        private static bool TwoSided(List<ListState> recorded, List<ListState> replayed, string scroll, int from, int until)
        {
            Dictionary<int, HashSet<(int view, int content)>> a = Sizes(recorded, scroll, from, until), b = Sizes(replayed, scroll, from, until);
            foreach (KeyValuePair<int, HashSet<(int view, int content)>> pair in a)
            {
                if (!b.TryGetValue(pair.Key, out HashSet<(int view, int content)> other)) continue;
                bool onlyA = false, onlyB = false;
                foreach ((int view, int content) size in pair.Value) if (!other.Contains(size)) { onlyA = true; break; }
                foreach ((int view, int content) size in other) if (!pair.Value.Contains(size)) { onlyB = true; break; }
                if (onlyA && onlyB) return true;
            }
            return false;
        }

        private static Dictionary<int, HashSet<(int view, int content)>> Sizes(List<ListState> lists, string scroll, int from, int until)
        {
            var sizes = new Dictionary<int, HashSet<(int view, int content)>>();
            foreach (ListState state in lists)
            {
                if (state.Name != scroll || state.Frame < from || state.Frame > until || state.Children < 0) continue;
                if (!sizes.TryGetValue(state.Children, out HashSet<(int view, int content)> set)) sizes[state.Children] = set = new HashSet<(int view, int content)>();
                set.Add((state.View, state.Content));
            }
            return sizes;
        }

        // Graphic.Raycast 와 같은 규칙: 가까운 CanvasGroup 부터 보고, blocksRaycasts 가 꺼져 있으면 막힌 것. ignoreParentGroups 면 더 위는 안 본다.
        private static bool Blocked(Info info)
        {
            foreach (CanvasGroup group in info.Groups)
            {
                if (group == null || !group.enabled) continue;
                if (!group.blocksRaycasts) return true;
                if (group.ignoreParentGroups) break;
            }
            return false;
        }
    }

    // UI 배치(레이아웃)를 다시 계산하면서 실제로 자리·크기가 바뀐 프레임을 남긴다(재생 패스에서만).
    // 글자 길이·줄 높이는 언어마다 달라서, 같은 플레이라도 어떤 언어에서만 목록 줄이 밀리거나 간격이 바뀌는 프레임이 있다. 영상 비교는
    // 그런 프레임을 "한쪽 영상만 더 많이 바뀌었다" 로 보는데, 화면만으로는 플레이가 갈린 것과 구분하지 못한다. 그래서 엔진이 배치를 다시
    // 계산하는 자리(LayoutRebuilder.Rebuild — 에디터가 Harmony 로 아래 두 메서드를 붙인다)에서 그 앞뒤의 자리·크기를 재, 배치 때문에
    // 움직인 양을 프레임마다 적어 둔다. 영상 비교가 걸린 프레임에 한쪽 언어에서만 배치가 움직였으면 언어별 화면 차이다.
    // 애니메이션·스크롤로 움직인 것은 여기 안 잡힌다(배치 계산 안에서 바뀐 것만 잰다).
    public static class LayoutTrace
    {
        [Serializable]
        public struct Move
        {
            public int Frame;
            public int Count;      // 자리·크기가 바뀐 UI 개수
            public float Amount;   // 바뀐 양의 합(자리 + 크기, 캔버스 단위)
            public string Name;    // 그중 가장 많이 바뀐 것 "부모/자식/대상"
        }

        private const int MaxMoves = 20000;   // 프레임 수
        private const int MaxRects = 4000;    // 이보다 큰 뿌리는 재지 않는다(그 프레임은 배치 기록 없이 간다)
        private const float Epsilon = 0.01f;
        private static List<Move> _moves;
        private static readonly List<RectTransform> _rects = new List<RectTransform>();
        private static readonly List<Vector4> _before = new List<Vector4>();
        private static RectTransform _root;
        private static int _rootFrame;
        private static bool _failed;

        public static bool Active => _moves != null && !_failed;

        internal static void Begin()
        {
            _moves = new List<Move>();
            _root = null;
            _failed = false;
        }

        internal static List<Move> End()
        {
            List<Move> moves = _moves ?? new List<Move>();
            _moves = null;
            _root = null;
            _rects.Clear();
            _before.Clear();
            return moves;
        }

        // Harmony prefix(LayoutRebuilder.Rebuild): 배치 단계면 이 뿌리 아래의 자리·크기를 재 둔다.
        public static void RebuildPrefix(RectTransform ___m_ToRebuild, CanvasUpdate executing)
        {
            if (!Active || executing != CanvasUpdate.Layout || ___m_ToRebuild == null) return;
            int frame = ReplaySession.Current != null ? ReplaySession.Current.Frame : 0;
            if (_root != null && _rootFrame == frame) return;   // 배치 계산 안에서 또 부른 배치 — 바깥 것이 같이 잰다
            try
            {
                _root = null;
                ___m_ToRebuild.GetComponentsInChildren(false, _rects);
                if (_rects.Count == 0 || _rects.Count > MaxRects) return;
                _before.Clear();
                for (int i = 0; i < _rects.Count; i++)
                {
                    Vector2 position = _rects[i].anchoredPosition, size = _rects[i].rect.size;
                    _before.Add(new Vector4(position.x, position.y, size.x, size.y));
                }
                _root = ___m_ToRebuild;
                _rootFrame = frame;
            }
            catch (Exception e) { Fail(e); }
        }

        // Harmony postfix(LayoutRebuilder.Rebuild): 배치가 바꾼 것을 센다.
        public static void RebuildPostfix(RectTransform ___m_ToRebuild, CanvasUpdate executing)
        {
            if (!Active || executing != CanvasUpdate.Layout || _root == null || ___m_ToRebuild != _root) return;
            _root = null;
            try
            {
                int count = 0;
                float amount = 0, most = 0;
                RectTransform mover = null;
                for (int i = 0; i < _rects.Count && i < _before.Count; i++)
                {
                    RectTransform rect = _rects[i];
                    if (rect == null) continue;
                    Vector2 position = rect.anchoredPosition, size = rect.rect.size;
                    Vector4 before = _before[i];
                    float moved = Mathf.Abs(position.x - before.x) + Mathf.Abs(position.y - before.y) + Mathf.Abs(size.x - before.z) + Mathf.Abs(size.y - before.w);
                    if (!(moved >= Epsilon)) continue;   // NaN 도 건너뛴다
                    count++;
                    amount += moved;
                    if (moved > most) { most = moved; mover = rect; }
                }
                if (count > 0) Add(_rootFrame, count, amount, mover);
            }
            catch (Exception e) { Fail(e); }
        }

        // 같은 프레임의 배치 계산은 한 줄로 모은다.
        private static void Add(int frame, int count, float amount, RectTransform mover)
        {
            int last = _moves.Count - 1;
            if (last >= 0 && _moves[last].Frame == frame)
            {
                Move move = _moves[last];
                // 이름은 한 번의 배치 계산에서 가장 많이 움직인 것 중 가장 큰 쪽
                if (mover != null && amount > move.Amount) move.Name = TapProbe.PathOf(mover);
                move.Count += count;
                move.Amount += amount;
                _moves[last] = move;
                return;
            }
            if (_moves.Count >= MaxMoves) return;
            _moves.Add(new Move { Frame = frame, Count = count, Amount = amount, Name = mover != null ? TapProbe.PathOf(mover) : "" });
        }

        private static void Fail(Exception e)
        {
            _failed = true;
            _root = null;
            Debug.LogWarning("[LocaleCapture] UI 배치 추적을 껐다(이 패스는 영상 비교가 배치 차이를 가려내지 못한다): " + e.Message);
        }
    }
}
