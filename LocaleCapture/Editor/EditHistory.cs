using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 편집 창의 실행 취소 대상. 편집 내용을 JSON 한 줄로 들고 있어 Unity Undo(Ctrl+Z·Ctrl+Y)가 되돌린다.
    internal sealed class EditHistory : ScriptableObject
    {
        public string Json;
    }
}
