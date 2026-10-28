using System;
using System.Linq.Expressions;
using System.Reflection;

namespace CookApps.LocaleCapture
{
    // Addressables(있으면) 로딩 완료를 동기 지점으로 남긴다. 패키지를 직접 참조하지 않고 진단 콜백을 리플렉션으로 건다.
    // 이름 대신 "addr" 개수로 맞춘다 — 언어마다 다른 에셋(글꼴 등)으로 바뀌어도 개수는 같아서 헛기다림이 없다.
    internal static class AddressablesProbe
    {
        private static object _resourceManager;
        private static Delegate _callback;
        private static MethodInfo _unregister;

        internal static void Register()
        {
            if (_callback != null) return;
            try
            {
                Type addressables = Type.GetType("UnityEngine.AddressableAssets.Addressables, Unity.Addressables");
                Type context = Type.GetType("UnityEngine.ResourceManagement.ResourceManager+DiagnosticEventContext, Unity.ResourceManager");
                if (addressables == null || context == null) return;
                _resourceManager = addressables.GetProperty("ResourceManager", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (_resourceManager == null) return;
                Type actionType = typeof(Action<>).MakeGenericType(context);
                MethodInfo register = _resourceManager.GetType().GetMethod("RegisterDiagnosticCallback", new[] { actionType });
                _unregister = _resourceManager.GetType().GetMethod("UnregisterDiagnosticCallback", new[] { actionType });
                if (register == null) return;
                ParameterExpression p = Expression.Parameter(context, "ctx");
                MethodInfo handler = typeof(AddressablesProbe).GetMethod(nameof(OnEvent), BindingFlags.NonPublic | BindingFlags.Static);
                _callback = Expression.Lambda(actionType, Expression.Call(handler, Expression.Convert(p, typeof(object))), p).Compile();
                register.Invoke(_resourceManager, new object[] { _callback });
            }
            catch (Exception e)
            {
                _callback = null;
                UnityEngine.Debug.LogWarning("[LocaleCapture] Addressables 로딩 감시를 걸지 못했다: " + e.GetBaseException().Message);
            }
        }

        internal static void Unregister()
        {
            try { if (_callback != null) _unregister?.Invoke(_resourceManager, new object[] { _callback }); }
            catch { /* 이미 해제 */ }
            _callback = null;
            _resourceManager = null;
        }

        private static void OnEvent(object context)
        {
            object type = context.GetType().GetProperty("Type")?.GetValue(context);
            if (type != null && type.ToString() == "AsyncOperationComplete") CaptureMarkers.Hit("addr");
        }
    }
}
