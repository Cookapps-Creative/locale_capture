using System.Collections.Concurrent;
using System.Collections.Generic;
using Unity.Collections;

namespace CookApps.LocaleCapture.Editor
{
    // 세션이 GPU 읽기 대상으로 빌려 가는 NativeArray 풀. Rent 는 메인 스레드, Return 은 워커 스레드에서도 불린다.
    internal sealed class FramePool
    {
        private readonly ConcurrentBag<NativeArray<byte>> _free = new ConcurrentBag<NativeArray<byte>>();
        private readonly List<NativeArray<byte>> _all = new List<NativeArray<byte>>();

        public NativeArray<byte> Rent(int length)
        {
            if (_free.TryTake(out NativeArray<byte> buffer) && buffer.Length == length) return buffer;
            buffer = new NativeArray<byte>(length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            lock (_all) _all.Add(buffer);
            return buffer;
        }

        public void Return(NativeArray<byte> buffer)
        {
            if (buffer.IsCreated) _free.Add(buffer);
        }

        public void DisposeAll()
        {
            lock (_all)
            {
                foreach (NativeArray<byte> b in _all) if (b.IsCreated) b.Dispose();
                _all.Clear();
            }
            while (_free.TryTake(out _)) { }
        }
    }
}
