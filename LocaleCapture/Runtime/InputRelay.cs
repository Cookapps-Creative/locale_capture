using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CookApps.LocaleCapture
{
    // 기록 때 실제 장치 입력을 재생과 같은 길로 보낸다. 실제 장치 이벤트는 처리 전에 가로채(handled) 모아 두었다가,
    // 프레임 끝에 같은 레이아웃의 가상 장치("LocaleCapture.<레이아웃>")로 다시 넣는다 — 재생이 넣는 시점과 같다.
    // 실제 장치 이벤트는 실시간으로 들어와 게임 Update 사이의 입력 갱신에서 처리될 수 있고, 그러면 게임이
    // "이번 프레임에 뗐다"(wasReleasedThisFrame)를 못 보고 지나간다. 재생은 늘 프레임 시작에 넣으므로 그 차이로 플레이가 갈라졌다.
    internal sealed class InputRelay
    {
        private readonly Dictionary<int, InputDevice> _virtual = new Dictionary<int, InputDevice>();   // 실제 장치 id → 가상 장치
        private readonly HashSet<InputDevice> _virtualSet = new HashSet<InputDevice>();
        private InputEventBuffer _pending;

        internal InputRelay()
        {
            _pending = new InputEventBuffer();
            InputSystem.onEvent += OnEvent;   // 기록 트레이스보다 먼저 걸어야 실제 장치 이벤트가 기록에 안 남는다
        }

        private unsafe void OnEvent(InputEventPtr e, InputDevice device)
        {
            if (device == null || _virtualSet.Contains(device) || e.handled) return;
            if (!e.IsA<StateEvent>() && !e.IsA<DeltaStateEvent>()) return;
            if (!_virtual.TryGetValue(device.deviceId, out InputDevice target))
            {
                target = InputSystem.AddDevice(device.layout, "LocaleCapture." + device.layout);
                _virtual[device.deviceId] = target;
                _virtualSet.Add(target);
            }
            _pending.AppendEvent(e.data);
            e.handled = true;
        }

        // 프레임 끝(재생이 다음 프레임 입력을 넣는 자리)에 부른다. 다음 입력 갱신에서 처리된다.
        internal unsafe void Flush()
        {
            if (_pending.eventCount == 0) return;
            foreach (InputEventPtr each in _pending)
            {
                InputEventPtr e = each;
                if (!_virtual.TryGetValue(e.deviceId, out InputDevice target)) continue;
                e.deviceId = target.deviceId;
                e.handled = false;
                e.time = InputState.currentTime;
                InputSystem.QueueEvent(e);
            }
            _pending.Reset();
        }

        internal void Dispose()
        {
            InputSystem.onEvent -= OnEvent;
            _pending.Dispose();
            foreach (InputDevice d in _virtualSet) if (d != null && d.added) InputSystem.RemoveDevice(d);
            _virtualSet.Clear();
            _virtual.Clear();
        }
    }
}
