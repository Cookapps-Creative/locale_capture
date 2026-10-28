using Unity.Collections;

namespace CookApps.LocaleCapture.Editor
{
    // GPU 원본(아래→위)을 위→아래로 뒤집어 복사한다.
    internal static class FrameFlip
    {
        public static void CopyTo(NativeArray<byte> src, byte[] dst, int width, int height, bool flip)
        {
            int row = width * 4;
            if (!flip) { src.CopyTo(dst); return; }
            for (int y = 0; y < height; y++)
                NativeArray<byte>.Copy(src, (height - 1 - y) * row, dst, y * row, row);
        }
    }
}
