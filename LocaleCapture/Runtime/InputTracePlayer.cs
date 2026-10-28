using System;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CookApps.LocaleCapture
{
    // 기록된 입력 트레이스를 프레임 단위로 되돌린다. 기록 장치마다 새 가상 장치를 만들어 그쪽으로 이벤트를 보내고,
    // 실제 장치는 재생 동안 꺼 둔다(사람 손이 섞이지 않게).
    public sealed class InputTracePlayer : IDisposable
    {
        private readonly InputEventTrace _trace;
        private readonly IReadOnlyList<int> _eventCounts;
        private readonly Dictionary<int, int> _deviceMap = new Dictionary<int, int>();
        private readonly List<InputDevice> _virtualDevices = new List<InputDevice>();
        private readonly List<InputDevice> _disabled = new List<InputDevice>();
        private IEnumerator<InputEventPtr> _events;
        private int _played; // 지금까지 큐에 넣은 이벤트 수(프레임 마커 제외)
        private int _queued, _delivered;   // 가상 장치로 보낸 수 / 입력 시스템이 실제로 처리한 수(어긋남 진단)
        private string _current;           // 첫 입력 때 게임이 보는 현재 터치·마우스 장치

        public InputTracePlayer(string tracePath, IReadOnlyList<int> eventCounts)
        {
            _trace = new InputEventTrace();
            _trace.ReadFrom(tracePath);
            _eventCounts = eventCounts;

            // 실제 장치는 끄고, 기록 장치마다 같은 레이아웃의 가상 장치를 만든다.
            DisableRealDevices();
            foreach (InputEventTrace.DeviceInfo info in _trace.deviceInfos)
            {
                try
                {
                    InputDevice device = InputSystem.AddDevice(info.layout, "LocaleCapture." + info.layout);
                    _deviceMap[info.deviceId] = device.deviceId;
                    _virtualDevices.Add(device);
                }
                catch (Exception e) { UnityEngine.Debug.LogWarning($"[LocaleCapture] 장치 생성 실패 {info.layout}: {e.Message}"); }
            }
            _events = _trace.GetEnumerator();
            InputSystem.onEvent += OnEvent;
        }

        private void OnEvent(InputEventPtr e, InputDevice device)
        {
            if (device != null && _virtualDevices.Contains(device)) _delivered++;
        }

        // 사람 손이 섞이지 않게 실제 장치를 끈다. 부팅 중에 늦게 붙은 장치도 있어 한 번 더 부른다.
        public void DisableRealDevices()
        {
            foreach (InputDevice device in InputSystem.devices)
            {
                if (!device.enabled || _virtualDevices.Contains(device)) continue;
                InputSystem.DisableDevice(device);
                _disabled.Add(device);
            }
        }

        // 프레임 frame 의 이벤트를 모두 큐에 넣는다. 다음 입력 갱신에서 처리된다.
        public void QueueFrame(int frame)
        {
            int until = frame < _eventCounts.Count ? _eventCounts[frame] : int.MaxValue;
            while (_played < until && _events.MoveNext())
            {
                InputEventPtr e = _events.Current;
                if (e.type == InputEventTrace.FrameMarkerEvent) continue;
                _played++;
                if (!_deviceMap.TryGetValue(e.deviceId, out int mapped)) continue;
                e.deviceId = mapped;
                e.time = InputState.currentTime;
                InputSystem.QueueEvent(e);
                _queued++;
                _current ??= $"Touchscreen.current={Touchscreen.current?.name ?? "없음"}, Mouse.current={Mouse.current?.name ?? "없음"}";
            }
        }

        public void Dispose()
        {
            InputSystem.onEvent -= OnEvent;
            CaptureVerify.Input(_queued, _delivered, _current);
            _events?.Dispose();
            foreach (InputDevice d in _virtualDevices) if (d != null && d.added) InputSystem.RemoveDevice(d);
            foreach (InputDevice d in _disabled) if (d != null && d.added) InputSystem.EnableDevice(d);
            _trace.Dispose();
        }
    }
}
