using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 게임뷰를 고정 해상도로 바꾼다(Unity 내부 API, 리플렉션). 녹화 화면을 늘리거나 자르지 않고 원하는 크기로 그리게 한다.
    internal static class GameViewResolution
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Assembly Editor = typeof(EditorWindow).Assembly;

        public static Vector2 Current => Handles.GetMainGameViewSize();

        public static bool Matches(int width, int height)
        {
            Vector2 size = Current;
            return Mathf.RoundToInt(size.x) == width && Mathf.RoundToInt(size.y) == height;
        }

        // 이 Unity 버전에서 게임뷰 크기 내부 API 를 모두 찾을 수 있는지(아무것도 바꾸지 않는다).
        public static bool Supported
        {
            get
            {
                Type sizesType = Editor.GetType("UnityEditor.GameViewSizes");
                Type sizeType = Editor.GetType("UnityEditor.GameViewSize");
                Type kindType = Editor.GetType("UnityEditor.GameViewSizeType");
                Type gameViewType = Editor.GetType("UnityEditor.GameView");
                if (sizesType == null || sizeType == null || kindType == null || gameViewType == null) return false;
                Type groupType = Editor.GetType("UnityEditor.GameViewSizeGroup");
                return sizesType.GetProperty("currentGroup", All) != null
                       && groupType?.GetMethod("GetTotalCount", All) != null
                       && groupType.GetMethod("GetGameViewSize", All) != null
                       && groupType.GetMethod("AddCustomSize", All) != null
                       && sizeType.GetConstructor(new[] { kindType, typeof(int), typeof(int), typeof(string) }) != null
                       && gameViewType.GetProperty("selectedSizeIndex", All) != null
                       && Enum.IsDefined(kindType, "FixedResolution");
            }
        }

        // 성공하면 true. 같은 크기 항목이 없으면 "Locale Capture" 이름으로 추가한다.
        public static bool Set(int width, int height)
        {
            try
            {
                Type sizesType = Editor.GetType("UnityEditor.GameViewSizes");
                Type sizeType = Editor.GetType("UnityEditor.GameViewSize");
                Type kindType = Editor.GetType("UnityEditor.GameViewSizeType");
                Type gameViewType = Editor.GetType("UnityEditor.GameView");
                object sizes = typeof(ScriptableSingleton<>).MakeGenericType(sizesType).GetProperty("instance", All).GetValue(null);
                object group = sizesType.GetProperty("currentGroup", All).GetValue(sizes);
                Type groupType = group.GetType();

                int index = -1;
                int count = (int) groupType.GetMethod("GetTotalCount", All).Invoke(group, null);
                object fixedKind = Enum.Parse(kindType, "FixedResolution");
                for (int i = 0; i < count && index < 0; i++)
                {
                    object size = groupType.GetMethod("GetGameViewSize", All).Invoke(group, new object[] { i });
                    if ((int) sizeType.GetProperty("width", All).GetValue(size) == width
                        && (int) sizeType.GetProperty("height", All).GetValue(size) == height
                        && sizeType.GetProperty("sizeType", All).GetValue(size).Equals(fixedKind))
                        index = i;
                }
                if (index < 0)
                {
                    object added = Activator.CreateInstance(sizeType, fixedKind, width, height, "Locale Capture");
                    groupType.GetMethod("AddCustomSize", All).Invoke(group, new[] { added });
                    index = (int) groupType.GetMethod("GetTotalCount", All).Invoke(group, null) - 1;
                }

                EditorWindow gameView = EditorWindow.GetWindow(gameViewType, false, null, false);
                gameViewType.GetProperty("selectedSizeIndex", All).SetValue(gameView, index);
                // 게임뷰 탭이 다른 탭 뒤에 가려져 있으면 그려지지 않아 Screen 크기가 그대로 남는다 — 앞으로 꺼낸다.
                gameView.ShowTab();
                gameView.Repaint();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocaleCapture] 게임뷰를 {width}x{height} 로 바꾸지 못했다: {e.GetBaseException().Message}");
                return false;
            }
        }
    }
}
