using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using BindingFlags = System.Reflection.BindingFlags;
using PropertyInfo = System.Reflection.PropertyInfo;

namespace CookApps.LocaleCapture
{
    // 기록 때 실제 장치 입력을 재생과 같은 길로 보낸다. 실제 장치 이벤트는 처리 전에 가로채(handled) 모아 두었다가,
    // 프레임 끝에 같은 레이아웃의 가상 장치("LocaleCapture.<레이아웃>")로 다시 넣는다 — 재생이 넣는 시점과 같다.
    // 실제 장치 이벤트는 실시간으로 들어와 게임 Update 사이의 입력 갱신에서 처리될 수 있고, 그러면 게임이
    // "이번 프레임에 뗐다"(wasReleasedThisFrame)를 못 보고 지나간다. 재생은 늘 프레임 시작에 넣으므로 그 차이로 플레이가 갈라졌다.
    //
    // 가로챈 이벤트는 실제 장치 상태를 바꾸면 안 된다. 바뀌면 게임이 실제 장치(그 프레임)와 가상 장치(다음 프레임)를 둘 다 보는데,
    // 재생에는 가상 장치만 있어 장치 상태를 직접 읽는 코드와 터치 검사가 어긋난다. Input System 1.20 부터 handled 이벤트의 기본
    // 처리가 "상태는 갱신하고 액션 알림만 막는다" 로 바뀌어, 기록 동안만 예전 처리(상태도 갱신하지 않는다)로 돌려놓는다.
    // 그래도 실제 장치가 눌린 것으로 보이면 Leak 에 남기고, 기록을 그 자리에서 실패로 끝낸다(ReplaySession).
    //
    // 가상 장치는 세션을 시작할 때 실제 장치마다 미리 만든다. 첫 입력이 올 때 만들면 그 순간 Input System 이 액션 바인딩을
    // 다시 풀면서 이미 움직인 컨트롤(마우스 위치 등)을 한 번 더 발사하고, 그게 방금 들어온 첫 입력과 같은 프레임에 겹친다 —
    // UI 입력 모듈은 마우스와 터치가 함께 오면 터치를 버린다. 재생은 장치를 처음부터 만들어 두므로 그 일이 없어 녹화와 갈렸다
    // (START 를 누르고 게임 화면으로 돌아와 한 첫 드래그가 녹화에서만 안 먹었다). 만든 장치는 Devices 로 기록에 남겨 재생이 똑같이 만든다.
    internal sealed class InputRelay
    {
        private readonly Dictionary<int, InputDevice> _virtual = new Dictionary<int, InputDevice>();   // 실제 장치 id → 가상 장치
        private readonly HashSet<InputDevice> _virtualSet = new HashSet<InputDevice>();
        private readonly List<InputDevice> _seen = new List<InputDevice>();   // 이번 프레임에 이벤트를 가로챈 실제 장치
        private InputEventBuffer _pending;
        private object _manager;         // handled 처리 방식을 바꿔 둔 InputManager(안 바꿨으면 null)
        private PropertyInfo _policy;
        private object _savedPolicy;

        // 가로챈 입력이 실제 장치 상태에 그대로 들어간 장치 이름. 없으면 null.
        internal string Leak { get; private set; }

        // 만든 가상 장치(순서대로). 기록에 남긴다.
        internal List<ReplayManifest.Device> Devices { get; } = new List<ReplayManifest.Device>();
        internal int Frame;   // 지금 세션 프레임(세션이 넣어 준다) — 세션 도중에 붙은 장치를 만든 때

        private const string VirtualPrefix = "LocaleCapture.";
        private readonly HashSet<int> _unmirrored = new HashSet<int>();   // 가상 장치를 못 만든 실제 장치(그 입력은 가로채지 않는다)

        internal InputRelay()
        {
            _pending = new InputEventBuffer();
            HoldHandledPolicy();
            foreach (InputDevice device in InputSystem.devices.ToArray()) Mirror(device);
            InputSystem.onEvent += OnEvent;   // 기록 트레이스보다 먼저 걸어야 실제 장치 이벤트가 기록에 안 남는다
        }

        // 실제 장치와 같은 레이아웃의 가상 장치. 이미 있으면 그것, 만들 수 없으면 null.
        private InputDevice Mirror(InputDevice real)
        {
            if (real == null || _virtualSet.Contains(real) || _unmirrored.Contains(real.deviceId)) return null;
            if (_virtual.TryGetValue(real.deviceId, out InputDevice target)) return target;
            if (real.name.StartsWith(VirtualPrefix, StringComparison.Ordinal)) return null;   // 앞 세션이 남긴 가상 장치
            try
            {
                target = InputSystem.AddDevice(real.layout, VirtualPrefix + real.layout);
            }
            catch (Exception e)
            {
                _unmirrored.Add(real.deviceId);
                Debug.LogWarning($"[LocaleCapture] 가상 장치를 만들지 못했다({real.layout}) — 이 장치의 입력은 실제 장치로 그대로 들어간다: {e.Message}");
                return null;
            }
            _virtual[real.deviceId] = target;
            _virtualSet.Add(target);
            Devices.Add(new ReplayManifest.Device { Frame = Frame, Id = target.deviceId, Layout = real.layout });
            return target;
        }

        // handled 이벤트가 장치 상태를 갱신하지 않게 한다. 이 설정이 없는 버전(1.20 전)은 원래 갱신하지 않는다.
        private void HoldHandledPolicy()
        {
            try
            {
                const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object manager = typeof(InputSystem).GetField("s_Manager", flags)?.GetValue(null);
                PropertyInfo policy = manager?.GetType().GetProperty("inputEventHandledPolicy", flags);
                if (policy == null) return;
                object suppress = Enum.Parse(policy.PropertyType, "SuppressStateUpdates");
                object current = policy.GetValue(manager);
                if (Equals(current, suppress)) return;
                policy.SetValue(manager, suppress);
                _manager = manager;
                _policy = policy;
                _savedPolicy = current;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[LocaleCapture] Input System {InputSystem.version} 의 handled 이벤트 처리 방식을 바꾸지 못했다: {e.Message}");
            }
        }

        private void RestoreHandledPolicy()
        {
            if (_manager == null) return;
            try { _policy.SetValue(_manager, _savedPolicy); }
            catch (Exception e) { Debug.LogWarning("[LocaleCapture] Input System 의 handled 이벤트 처리 방식을 되돌리지 못했다: " + e.Message); }
            _manager = null;
        }

        private unsafe void OnEvent(InputEventPtr e, InputDevice device)
        {
            if (device == null || _virtualSet.Contains(device) || e.handled) return;
            if (!e.IsA<StateEvent>() && !e.IsA<DeltaStateEvent>()) return;
            if (Mirror(device) == null) return;   // 세션 도중에 붙은 장치는 여기서 만든다
            _pending.AppendEvent(e.data);
            e.handled = true;
            if (!_seen.Contains(device)) _seen.Add(device);
        }

        // 프레임 끝(재생이 다음 프레임 입력을 넣는 자리)에 부른다. 다음 입력 갱신에서 처리된다.
        internal unsafe void Flush()
        {
            // 이번 프레임에 가로챈 장치가 눌린 것으로 보이면 가로챈 이벤트가 장치 상태까지 바꾼 것이다.
            if (Leak == null)
                foreach (InputDevice d in _seen)
                    if (d.added && d is Pointer pointer && pointer.press.wasPressedThisFrame) { Leak = d.name; break; }
            _seen.Clear();

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
            RestoreHandledPolicy();
            _pending.Dispose();
            foreach (InputDevice d in _virtualSet) if (d != null && d.added) InputSystem.RemoveDevice(d);
            _virtualSet.Clear();
            _virtual.Clear();
        }
    }
}
