using UnityEditor;

namespace CookApps.LocaleCapture.Editor
{
    // 인스펙터에서 바꾼 오브젝트·컴포넌트 값을 받아, 켜기/끄기 대상이면 기록에 남긴다(기록 중일 때만).
    [InitializeOnLoad]
    internal static class InspectorToggleHook
    {
        static InspectorToggleHook()
        {
            ObjectChangeEvents.changesPublished -= OnChanges;
            ObjectChangeEvents.changesPublished += OnChanges;
        }

        private static void OnChanges(ref ObjectChangeEventStream stream)
        {
            ReplaySession session = ReplaySession.Current;
            if (session == null || !session.IsRecording) return;
            for (int i = 0; i < stream.length; i++)
            {
                if (stream.GetEventType(i) != ObjectChangeKind.ChangeGameObjectOrComponentProperties) continue;
                stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out ChangeGameObjectOrComponentPropertiesEventArgs e);
                InspectorToggles.Record(EditorUtility.InstanceIDToObject(e.instanceId));
            }
        }
    }
}
