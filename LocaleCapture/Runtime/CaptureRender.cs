using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace CookApps.LocaleCapture
{
    // 재생 화면 그리기에 관한 도움. 게임 코드는 참조하지 않는다.
    //  · 캡처 픽셀의 채널 순서(RGBA/BGRA)를 포맷 이름으로 짐작하지 않고 실제로 읽어 본다 — 짐작이 틀리면 영상이 파랗게 나온다.
    //  · 영상에 남지 않는 구간(START 전, 스템 패스)은 URP 렌더 스케일을 낮춰 GPU 시간을 줄인다. 화면 좌표·UI 레이아웃은 그대로다.
    public static class CaptureRender
    {
        // like 와 같은 포맷의 작은 텍스처를 빨강으로 칠해 읽어, 첫 바이트가 R 이 아니면 BGRA 다.
        public static bool ReadbackIsBgra(RenderTexture like)
        {
            RenderTexture probe = null;
            try
            {
                RenderTextureDescriptor d = like.descriptor;
                d.width = 4; d.height = 4; d.depthBufferBits = 0; d.useMipMap = false; d.msaaSamples = 1;
                probe = new RenderTexture(d) { name = "LocaleCapture.Probe" };
                probe.Create();
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = probe;
                GL.Clear(false, true, new Color(1f, 0f, 0f, 1f));
                RenderTexture.active = previous;
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(probe, 0);
                request.WaitForCompletion();
                if (request.hasError) return false;
                Unity.Collections.NativeArray<byte> data = request.GetData<byte>();
                bool bgra = data.Length >= 4 && data[2] > data[0];
                Debug.Log($"[LocaleCapture] 캡처 채널 순서 {(bgra ? "BGRA" : "RGBA")} ({like.graphicsFormat})");
                return bgra;
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 채널 순서를 확인하지 못해 RGBA 로 본다: " + e.Message); return false; }
            finally { if (probe != null) { probe.Release(); UnityEngine.Object.Destroy(probe); } }
        }

        // ── 렌더 스케일 ───────────────────────────────────────────────────────

        private const float LowScale = 0.1f;
        private static object _pipeline;
        private static PropertyInfo _renderScale;
        private static float _saved;
        private static bool _low;

        public static bool LowRes => _low;

        // URP 가 아니거나 renderScale 이 없으면 아무것도 하지 않는다. 에셋(파이프라인 설정)을 바꾸므로 반드시 EndLowRes 로 되돌린다.
        public static void BeginLowRes()
        {
            if (_low) return;
            try
            {
                RenderPipelineAsset asset = GraphicsSettings.currentRenderPipeline;
                PropertyInfo prop = asset?.GetType().GetProperty("renderScale", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || prop.PropertyType != typeof(float) || !prop.CanWrite) return;
                _saved = (float) prop.GetValue(asset);
                if (_saved <= LowScale) return;
                _pipeline = asset;
                _renderScale = prop;
                prop.SetValue(asset, LowScale);
                _low = true;
            }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 렌더 스케일을 낮추지 못했다: " + e.Message); }
        }

        public static void EndLowRes()
        {
            if (!_low) return;
            _low = false;
            try { _renderScale.SetValue(_pipeline, _saved); }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] 렌더 스케일을 되돌리지 못했다: " + e.Message); }
            _pipeline = null;
            _renderScale = null;
        }
    }
}
