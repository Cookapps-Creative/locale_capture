using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace CookApps.LocaleCapture.Editor
{
    // 창을 열 때 도는 점검. 게임 코드는 보지 않고, 이 도구가 기대는 일반 조건(언어 시스템·빌드 씬·Unity 내부 API·도구 파일)만 본다.
    // 문제마다 한 줄. 비어 있으면 정상.
    internal static class CaptureHealth
    {
        public static List<string> Check(int manualLocales)
        {
            var issues = new List<string>();

            if (LocaleCatalog.Codes().Count == 0 && manualLocales == 0)
                issues.Add("언어 시스템(Unity Localization·I2)을 찾지 못했다 — Languages 에 언어 코드를 직접 적는다");
            if (EditorBuildSettings.scenes.Length == 0)
                issues.Add("빌드 설정에 씬이 없다 — 기록을 게임 첫 씬부터 시작할 수 없다");
            if (AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == "0Harmony") > 1)
                issues.Add("프로젝트에 다른 Harmony(0Harmony.dll)가 함께 있다 — 하나만 남긴다");

            // Unity 내부 API(Unity 버전이 바뀌면 깨질 수 있다)
            if (!GameViewResolution.Supported) issues.Add("Unity 게임뷰 크기 API 가 바뀌었다 — Resolution 에 맞춘 게임뷰 자동 변경이 안 된다");
            if (!EditPreview.SoundAvailable) issues.Add("Windows 에디터가 아니다 — 편집 창 미리보기 소리가 안 난다");
            Type gameView = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameView?.GetProperty("vSyncEnabled", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) == null)
                issues.Add("Unity 게임뷰 VSync API 가 바뀌었다 — 재생 녹화가 느려질 수 있다");
            if (typeof(UnityEngine.AsyncOperation).GetMethod("InvokeCompletionEvent", BindingFlags.Instance | BindingFlags.NonPublic) == null)
                issues.Add("Unity AsyncOperation 완료 API 가 바뀌었다 — 로딩 시간 차이를 맞추지 못한다");
            if (typeof(UnityEngine.SceneManagement.SceneManager).GetMethod("LoadSceneAsyncNameIndexInternal", BindingFlags.Static | BindingFlags.NonPublic) == null)
                issues.Add("Unity 씬 로드 API 가 바뀌었다 — 씬 로딩 대기가 서로 기다리다 멈출 수 있다");
            if (typeof(UnityEditor.SceneManagement.EditorSceneManager).GetMethod("LoadSceneInPlayModeInternal", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public) == null)
                issues.Add("Unity 에디터 씬 로드 API 가 바뀌었다 — Addressables 에셋 데이터베이스 모드에서 씬 로딩을 기다리지 못한다");

            if (!BundledFfmpeg.Available) issues.Add("편집·내보내기 도구(Tools/ffmpeg.exe)가 없다");

            if (CapturePatches.CoreProblems.Count > 0)
                issues.Add($"지난 녹화의 핵심 런타임 패치 실패: {string.Join(", ", CapturePatches.CoreProblems)}(콘솔)");
            return issues;
        }
    }
}
