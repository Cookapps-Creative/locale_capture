using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 런타임 패치 대상(메서드 토큰) 캐시. 키는 "어셈블리|MVID|종류|바꿔 끼울 목록" — 다시 빌드되면 MVID 가 바뀌어 저절로 무효.
    internal static class TargetCache
    {
        [Serializable] private sealed class Entry { public string Key; public List<int> Tokens = new List<int>(); }
        [Serializable] private sealed class File_ { public List<Entry> Entries = new List<Entry>(); }

        private static string PathOf => Path.GetFullPath("Library/LocaleCapture/patch-targets.json");
        private static Dictionary<string, List<int>> _entries = new Dictionary<string, List<int>>();
        private static readonly HashSet<string> _used = new HashSet<string>();
        private static bool _dirty;

        public static void Load()
        {
            _entries = new Dictionary<string, List<int>>();
            _used.Clear();
            _dirty = false;
            try
            {
                if (!File.Exists(PathOf)) return;
                foreach (Entry e in JsonUtility.FromJson<File_>(File.ReadAllText(PathOf)).Entries) _entries[e.Key] = e.Tokens;
            }
            catch { _entries.Clear(); }
        }

        public static bool TryGet(string key, out List<int> tokens)
        {
            _used.Add(key);
            return _entries.TryGetValue(key, out tokens);
        }

        public static void Set(string key, List<int> tokens)
        {
            _entries[key] = tokens;
            _used.Add(key);
            _dirty = true;
        }

        // 이번에 쓴 키만 남긴다(지난 빌드 항목이 쌓이지 않게).
        public static void Save()
        {
            if (!_dirty && _used.Count == _entries.Count) return;
            try
            {
                var file = new File_();
                foreach (KeyValuePair<string, List<int>> e in _entries)
                    if (_used.Contains(e.Key)) file.Entries.Add(new Entry { Key = e.Key, Tokens = e.Value });
                Directory.CreateDirectory(Path.GetDirectoryName(PathOf));
                File.WriteAllText(PathOf, JsonUtility.ToJson(file));
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 패치 대상 캐시 저장 실패: " + e.Message); }
        }
    }
}
