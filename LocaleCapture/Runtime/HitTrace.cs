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
}
