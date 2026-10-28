using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace CookApps.LocaleCapture
{
    // 게임의 언어 시스템(Unity Localization → I2 Localization 순)의 선택 언어를 리플렉션으로 읽고 바꾼다.
    // 어느 패키지도 직접 참조하지 않아 없어도 컴파일된다.
    public static class LocalizationBridge
    {
        private const BindingFlags Static = BindingFlags.Public | BindingFlags.Static;
        private static readonly Type UnitySettings = Type.GetType("UnityEngine.Localization.Settings.LocalizationSettings, Unity.Localization");
        private static Type _i2;
        private static bool _i2Searched;

        private static Type I2
        {
            get
            {
                if (_i2Searched) return _i2;
                _i2Searched = true;
                _i2 = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("I2.Loc.LocalizationManager")).FirstOrDefault(t => t != null);
                return _i2;
            }
        }

        public static bool Available => UnitySettings != null || I2 != null;

        // 지금 선택 언어 코드. 모르면 null. 처음 부르면 패키지 초기화가 시작될 수 있으니 게임이 초기화한 뒤에 부른다.
        public static string SelectedCode()
        {
            try
            {
                if (UnitySettings != null) return CodeOf(UnitySettings.GetProperty("SelectedLocale", Static)?.GetValue(null));
                return I2?.GetProperty("CurrentLanguageCode", Static)?.GetValue(null) as string;
            }
            catch { return null; }
        }

        // 사용 가능한 언어 중 code 로 바꾼다. 성공하면 true.
        public static bool Select(string code)
        {
            try
            {
                if (UnitySettings != null)
                {
                    object locale = FindUnityLocale(code);
                    if (locale == null) return false;
                    UnitySettings.GetProperty("SelectedLocale", Static).SetValue(null, locale);
                    return true;
                }
                PropertyInfo current = I2?.GetProperty("CurrentLanguageCode", Static);
                if (current == null) return false;
                current.SetValue(null, code);
                return true;
            }
            catch { return false; }
        }

        private static object FindUnityLocale(string code)
        {
            object provider = UnitySettings.GetProperty("AvailableLocales", Static)?.GetValue(null);
            if (!(provider?.GetType().GetProperty("Locales")?.GetValue(provider) is IEnumerable locales)) return null;
            foreach (object locale in locales)
                if (string.Equals(CodeOf(locale), code, StringComparison.OrdinalIgnoreCase)) return locale;
            return null;
        }

        private static string CodeOf(object locale)
        {
            if (locale == null) return null;
            object id = locale.GetType().GetProperty("Identifier")?.GetValue(locale);
            return id?.GetType().GetProperty("Code")?.GetValue(id) as string;
        }
    }
}
