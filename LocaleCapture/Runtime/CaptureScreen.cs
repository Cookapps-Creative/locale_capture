using System;
using UnityEngine;

namespace CookApps.LocaleCapture
{
    // 화면의 안전 영역·DPI·노치를 기록 때 값으로 고정한다. Device Simulator 로 기록하고 게임뷰에서 재생하면 이 값이 달라
    // UI 배치(안전 영역 패널 등)가 바뀌고, 같은 좌표의 터치가 다른 대상에 들어간다. 에디터가 스크립트의 Screen 호출을 이쪽으로 바꿔 끼운다.
    public static class CaptureScreen
    {
        private static bool _fixed;
        private static Rect _safeArea;
        private static float _dpi;
        private static Rect[] _cutouts = Array.Empty<Rect>();

        internal static void Begin(ReplayManifest manifest, bool recording)
        {
            if (recording)
            {
                manifest.SafeArea = Screen.safeArea;
                manifest.Dpi = Screen.dpi;
                manifest.Cutouts = new System.Collections.Generic.List<Rect>(Screen.cutouts);
                _fixed = false;
                return;
            }
            _fixed = manifest.SafeArea.width > 0;   // 이전 기록에는 값이 없다
            _safeArea = manifest.SafeArea;
            _dpi = manifest.Dpi;
            _cutouts = manifest.Cutouts?.ToArray() ?? Array.Empty<Rect>();
        }

        internal static void End() => _fixed = false;

        public static Rect SafeArea => _fixed ? _safeArea : Screen.safeArea;
        public static float Dpi => _fixed ? _dpi : Screen.dpi;
        public static Rect[] Cutouts => _fixed ? (Rect[]) _cutouts.Clone() : Screen.cutouts;
    }
}
