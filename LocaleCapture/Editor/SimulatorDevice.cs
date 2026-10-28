using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 출력 해상도(Custom) 그대로의 Device Simulator 기기를 만들어 고른다. 기록 화면 = 출력 화면이 되어
    // 편집 Export 에서 늘리거나 블러를 채울 필요가 없다. 노치 없음, 안전 영역 = 화면 전체.
    internal static class SimulatorDevice
    {
        private const string Folder = "Assets/CookApps/LocaleCapture/Devices";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static string NameFor(int width, int height) => $"LocaleCapture {width}x{height}";

        // 없으면 만들고, 열린 Simulator 에서 고른다. 실패하면 경고만 남긴다(사용자가 직접 고르면 된다).
        public static void Use(int width, int height)
        {
            string name = NameFor(width, height);
            try
            {
                string path = $"{Folder}/{name}.device";
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(path, Json(name, width, height));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                }
                if (!Select(name)) Debug.LogWarning($"[LocaleCapture] Simulator 에서 '{name}' 기기를 고르지 못했다 — Simulator 상단 기기 목록에서 직접 고른다");
            }
            catch (Exception e) { Debug.LogWarning($"[LocaleCapture] Simulator 기기 '{name}' 준비 실패: {e.GetBaseException().Message}"); }
        }

        private static bool Select(string name)
        {
            Type window = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.DeviceSimulation.SimulatorWindow")).FirstOrDefault(t => t != null);
            UnityEngine.Object instance = window != null ? Resources.FindObjectsOfTypeAll(window).FirstOrDefault() : null;
            object main = instance != null ? window.GetField("m_Main", Any)?.GetValue(instance) : null;
            if (main == null) return false;
            Type mainType = main.GetType();
            mainType.GetMethod("UpdateDeviceList", Any)?.Invoke(main, null);
            if (!(mainType.GetProperty("devices", Any)?.GetValue(main) is Array devices)) return false;
            for (int i = 0; i < devices.Length; i++)
            {
                object asset = devices.GetValue(i);
                object info = asset?.GetType().GetField("deviceInfo", Any)?.GetValue(asset);
                if (info?.GetType().GetField("friendlyName", Any)?.GetValue(info) as string != name) continue;
                mainType.GetProperty("deviceIndex", Any)?.SetValue(main, i);
                return true;
            }
            return false;
        }

        // 세로 한 방향, 안전 영역 전체, 노치·내비게이션 바 없음. systemInfo 는 평범한 안드로이드 값.
        private static string Json(string name, int width, int height) => $@"{{
    ""friendlyName"": ""{name}"",
    ""version"": 1,
    ""screens"": [
        {{
            ""width"": {width},
            ""height"": {height},
            ""navigationBarHeight"": 0,
            ""dpi"": 420.0,
            ""orientations"": [
                {{
                    ""orientation"": 1,
                    ""safeArea"": {{ ""serializedVersion"": ""2"", ""x"": 0.0, ""y"": 0.0, ""width"": {width}.0, ""height"": {height}.0 }},
                    ""cutouts"": []
                }}
            ]
        }}
    ],
    ""systemInfo"": {{
        ""deviceModel"": ""LocaleCapture"",
        ""deviceType"": 1,
        ""operatingSystem"": ""Android OS 14 / API-34"",
        ""operatingSystemFamily"": 0,
        ""processorCount"": 8,
        ""processorFrequency"": 2800,
        ""processorType"": ""ARM64 FP ASIMD AES"",
        ""supportsAccelerometer"": true,
        ""supportsAudio"": true,
        ""supportsGyroscope"": true,
        ""supportsLocationService"": true,
        ""supportsVibration"": true,
        ""systemMemorySize"": 8000,
        ""graphicsDependentData"": [
            {{
                ""graphicsDeviceType"": 21,
                ""graphicsMemorySize"": 8000,
                ""graphicsDeviceName"": ""Mali-G78"",
                ""graphicsDeviceVendor"": ""ARM"",
                ""graphicsDeviceVersion"": ""Vulkan 1.1.0"",
                ""graphicsShaderLevel"": 50,
                ""graphicsMultiThreaded"": true,
                ""renderingThreadingMode"": 0
            }}
        ]
    }}
}}
";
    }
}
