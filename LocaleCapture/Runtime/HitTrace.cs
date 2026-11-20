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
    // 문구·글자 크기와 무관하다.
    public static class HitTrace
    {
        [Serializable]
        public struct Event
        {
            public int Frame;
            public string Name;   // 처리기 오브젝트 "부모/자식/대상". 잠긴(interactable 이 꺼진) Selectable 은 뒤에 " (잠김)"
            public int Delta;     // 같은 이름의 개수 변화. + 켜짐, − 꺼짐
        }

        private sealed class Info
        {
            public GameObject Handler;
            public string Name;
            public Selectable Selectable;
            public CanvasGroup[] Groups;
        }

        // 그래픽마다 처리기를 다시 찾는 주기. 풀링으로 부모가 바뀐 오브젝트의 낡은 이름이 이보다 오래 남지 않는다.
        // 한 프레임에 몰아서 다시 찾으면 그 프레임이 끊기므로, 목록 순번에 따라 프레임마다 1/30 씩 나눠 한다 —
        // 순번은 등록 순서라 기록과 재생이 같아서, 캐시가 낡은 구간도 양쪽이 똑같이 본다.
        private const int RefreshFrames = 30;
        private const int PurgeFrames = 900;    // 없어진 그래픽을 캐시에서 치우는 주기
        private static readonly Dictionary<Graphic, Info> _info = new Dictionary<Graphic, Info>();
        private static readonly HashSet<GameObject> _seen = new HashSet<GameObject>();
        private static Dictionary<string, int> _prev = new Dictionary<string, int>(), _now = new Dictionary<string, int>();
        private static bool _failed;

        internal static void Begin()
        {
            _info.Clear();
            _prev.Clear();
            _now.Clear();
            _failed = false;
        }

        // 프레임 끝에 부른다. 지난 프레임과 달라진 것을 into 에 더한다.
        internal static void Check(int frame, List<Event> into)
        {
            if (into == null || _failed) return;
            try { Collect(frame); }
            catch (Exception e)
            {
                _failed = true;
                Debug.LogWarning("[LocaleCapture] 입력 받는 UI 추적을 껐다(이 테이크는 그 검사 없이 간다): " + e.Message);
                return;
            }
            foreach (KeyValuePair<string, int> pair in _now)
            {
                _prev.TryGetValue(pair.Key, out int before);
                if (pair.Value != before) into.Add(new Event { Frame = frame, Name = pair.Key, Delta = pair.Value - before });
            }
            foreach (KeyValuePair<string, int> pair in _prev)
                if (!_now.ContainsKey(pair.Key)) into.Add(new Event { Frame = frame, Name = pair.Key, Delta = -pair.Value });
            (_prev, _now) = (_now, _prev);
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
                    string name = info.Selectable != null && !info.Selectable.IsInteractable() ? info.Name + " (잠김)" : info.Name;
                    _now.TryGetValue(name, out int count);
                    _now[name] = count + 1;
                }
            }
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
            info.Selectable = handler.GetComponent<Selectable>();
            info.Groups = go.GetComponentsInParent<CanvasGroup>(true);   // 가까운 것부터
            return info;
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
