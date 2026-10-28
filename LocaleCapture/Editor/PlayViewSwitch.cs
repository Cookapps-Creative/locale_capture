using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 플레이 화면 전환. Unity 는 마지막으로 포커스한 플레이 뷰(Game·Simulator)를 플레이 화면으로 쓴다.
    // 사람이 하는 기록은 Simulator 에서(기기 느낌·안전 영역), 언어별 재생은 Game 에서(기록 크기 그대로) 돌리고, 끝나면 Simulator 로 돌려 둔다.
    internal static class PlayViewSwitch
    {
        private static Type GameViewType => typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");

        private static Type SimulatorType =>
            AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.DeviceSimulation.SimulatorWindow")).FirstOrDefault(t => t != null);

        public static void ShowSimulator() => Show(SimulatorType, GameViewType);
        public static void ShowGame() => Show(GameViewType, SimulatorType);

        // 열려 있으면 앞으로, 없으면 다른 뷰 옆에 탭으로 연다.
        private static void Show(Type type, Type dockNextTo)
        {
            if (type == null) return;
            try
            {
                EditorWindow window = Resources.FindObjectsOfTypeAll(type).OfType<EditorWindow>().FirstOrDefault();
                if (window == null)
                {
                    var open = typeof(EditorWindow).GetMethods()
                        .First(m => m.Name == nameof(EditorWindow.GetWindow) && m.IsGenericMethod && m.GetParameters().Length == 3
                                    && m.GetParameters()[2].ParameterType == typeof(Type[]));
                    window = (EditorWindow) open.MakeGenericMethod(type).Invoke(null, new object[] { null, true, dockNextTo != null ? new[] { dockNextTo } : Array.Empty<Type>() });
                }
                window.Show();
                window.Focus();
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 플레이 화면을 바꾸지 못했다: " + e.GetBaseException().Message); }
        }
    }
}
