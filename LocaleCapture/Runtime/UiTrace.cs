using System;
using System.Collections.Generic;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // UI 화면(최상위 Canvas)이 열리고 닫힌 프레임을 남긴다. 기록과 재생을 비교해, 같은 입력인데 팝업이 늦게·일찍 뜬 곳을
    // 바로 보여 준다(누른 대상 비교는 누른 순간만 본다).
    public static class UiTrace
    {
        [Serializable]
        public struct Event
        {
            public int Frame;
            public string Name;
            public bool Open;
        }

        private static readonly HashSet<string> _open = new HashSet<string>();
        private static readonly HashSet<string> _now = new HashSet<string>();

        internal static void Begin() => _open.Clear();

        // 프레임 끝에 부른다. 열린·닫힌 화면을 into 에 더한다.
        internal static void Check(int frame, List<Event> into)
        {
            if (into == null) return;
            _now.Clear();
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                if (canvas.isRootCanvas && canvas.enabled) _now.Add(canvas.name.Replace("(Clone)", ""));
            foreach (string name in _now)
                if (_open.Add(name)) into.Add(new Event { Frame = frame, Name = name, Open = true });
            _open.RemoveWhere(name =>
            {
                if (_now.Contains(name)) return false;
                into.Add(new Event { Frame = frame, Name = name, Open = false });
                return true;
            });
        }

        // 같은 화면의 같은 순번 열림·닫힘끼리 맞춰, 프레임이 다른 첫 곳들을 "이름 열림: 녹화 F 재생 G" 로.
        internal static List<string> Compare(List<Event> recorded, List<Event> replayed)
        {
            var issues = new List<string>();
            if (recorded == null || replayed == null) return issues;
            var queues = new Dictionary<string, Queue<int>>();
            foreach (Event e in replayed)
            {
                string key = e.Name + (e.Open ? "+" : "-");
                if (!queues.TryGetValue(key, out Queue<int> q)) queues[key] = q = new Queue<int>();
                q.Enqueue(e.Frame);
            }
            foreach (Event e in recorded)
            {
                string key = e.Name + (e.Open ? "+" : "-");
                string what = $"{e.Name} {(e.Open ? "열림" : "닫힘")}";
                if (!queues.TryGetValue(key, out Queue<int> q) || q.Count == 0) { issues.Add($"{what}: 녹화 {e.Frame}, 재생 없음"); continue; }
                int got = q.Dequeue();
                if (Math.Abs(got - e.Frame) > 1) issues.Add($"{what}: 녹화 {e.Frame}, 재생 {got}");
            }
            return issues;
        }
    }
}
