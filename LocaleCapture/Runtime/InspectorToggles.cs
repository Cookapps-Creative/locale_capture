using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CookApps.LocaleCapture
{
    // 기록 중 인스펙터에서 바꾼 켜기/끄기(오브젝트 Active, 컴포넌트 Enabled). 기록은 프레임과 함께 남기고,
    // 재생은 같은 프레임에 같은 대상을 찾아 같은 값으로 둔다. 대상은 "씬/이름#같은이름순번/…" 경로로 찾는다.
    public static class InspectorToggles
    {
        [Serializable]
        public struct Toggle
        {
            public int Frame;
            public string Path;
            public string Component;   // 비면 오브젝트 Active, 있으면 그 타입의 Index 번째 컴포넌트 Enabled
            public int Index;
            public bool On;
        }

        // 에디터 훅이 부른다. 지금 기록 중이고 켜기/끄기를 가진 대상이면 현재 값을 남긴다.
        public static void Record(UnityEngine.Object target)
        {
            ReplaySession session = ReplaySession.Current;
            if (session == null || !session.IsRecording || target == null) return;
            Toggle t;
            switch (target)
            {
                case GameObject go:
                    t = new Toggle { Path = PathOf(go.transform), On = go.activeSelf };
                    break;
                case Component c when TryEnabled(c, out bool on):
                    t = new Toggle { Path = PathOf(c.transform), Component = c.GetType().FullName, Index = IndexOf(c), On = on };
                    break;
                default:
                    return;
            }
            session.RecordToggle(t);
        }

        internal static void Apply(Toggle t)
        {
            Transform tr = Find(t.Path);
            if (tr == null) { Debug.LogWarning($"[LocaleCapture] 인스펙터 켜기/끄기 대상을 못 찾았다: {t.Path}"); return; }
            if (string.IsNullOrEmpty(t.Component)) { tr.gameObject.SetActive(t.On); return; }
            int n = 0;
            foreach (Component c in tr.GetComponents<Component>())
            {
                if (c == null || c.GetType().FullName != t.Component || n++ != t.Index) continue;
                SetEnabled(c, t.On);
                return;
            }
        }

        private static bool TryEnabled(Component c, out bool on)
        {
            switch (c)
            {
                case Behaviour b: on = b.enabled; return true;
                case Renderer r: on = r.enabled; return true;
                case Collider col: on = col.enabled; return true;
                default: on = false; return false;
            }
        }

        private static void SetEnabled(Component c, bool on)
        {
            switch (c)
            {
                case Behaviour b: b.enabled = on; break;
                case Renderer r: r.enabled = on; break;
                case Collider col: col.enabled = on; break;
            }
        }

        private static int IndexOf(Component c)
        {
            int n = 0;
            foreach (Component other in c.GetComponents(c.GetType()))
            {
                if (other == c) return n;
                if (other.GetType() == c.GetType()) n++;
            }
            return 0;
        }

        // 같은 부모 아래 같은 이름이 여럿이면 #순번으로 가른다.
        private static string Step(Transform t)
        {
            int n = 0;
            if (t.parent != null)
            {
                for (int i = 0; i < t.parent.childCount; i++)
                {
                    Transform s = t.parent.GetChild(i);
                    if (s == t) break;
                    if (s.name == t.name) n++;
                }
            }
            else
            {
                foreach (GameObject root in t.gameObject.scene.GetRootGameObjects())
                {
                    if (root.transform == t) break;
                    if (root.name == t.name) n++;
                }
            }
            return t.name + "#" + n;
        }

        private static string PathOf(Transform t)
        {
            var steps = new List<string>();
            for (Transform x = t; x != null; x = x.parent) steps.Add(Step(x));
            steps.Add(t.gameObject.scene.name);
            steps.Reverse();
            return string.Join("/", steps);
        }

        private static Transform Find(string path)
        {
            string[] steps = path.Split('/');
            if (steps.Length < 2) return null;
            foreach (Scene scene in Scenes())
            {
                if (scene.name != steps[0]) continue;
                Transform found = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (Step(root.transform) == steps[1]) { found = root.transform; break; }
                for (int i = 2; i < steps.Length && found != null; i++)
                {
                    Transform next = null;
                    for (int c = 0; c < found.childCount; c++)
                        if (Step(found.GetChild(c)) == steps[i]) { next = found.GetChild(c); break; }
                    found = next;
                }
                if (found != null) return found;
            }
            return null;
        }

        // 열린 씬들 + DontDestroyOnLoad 씬(직접 열거가 안 돼 잠깐 만든 오브젝트로 얻는다).
        private static IEnumerable<Scene> Scenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++) yield return SceneManager.GetSceneAt(i);
            var probe = new GameObject("LocaleCapture.SceneProbe") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(probe);
            Scene ddol = probe.scene;
            UnityEngine.Object.DestroyImmediate(probe);
            yield return ddol;
        }
    }
}
