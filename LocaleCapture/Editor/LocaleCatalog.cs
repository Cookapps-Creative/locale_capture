using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace CookApps.LocaleCapture.Editor
{
    // 프로젝트가 지원하는 언어 목록과, 언어 코드의 여러 표기(저장 값·서버 요청에서 찾을 때 쓴다).
    // Unity Localization → I2 Localization 순으로 찾는다. 둘 다 없으면 창에서 직접 적는다.
    internal static class LocaleCatalog
    {
        public static string Source { get; private set; } = "";

        public static List<string> Codes()
        {
            List<string> codes = UnityLocalizationCodes();
            if (codes.Count > 0) { Source = "Unity Localization"; return codes; }
            codes = I2Codes();
            if (codes.Count > 0) { Source = "I2 Localization"; return codes; }
            Source = "";
            return codes;
        }

        private static List<string> UnityLocalizationCodes()
        {
            var codes = new List<string>();
            try
            {
                Type settings = Type.GetType("UnityEditor.Localization.LocalizationEditorSettings, Unity.Localization.Editor");
                if (!(settings?.GetMethod("GetLocales", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)?.Invoke(null, null) is IEnumerable locales)) return codes;
                foreach (object locale in locales)
                {
                    object id = locale?.GetType().GetProperty("Identifier")?.GetValue(locale);
                    if (id?.GetType().GetProperty("Code")?.GetValue(id) is string code && !string.IsNullOrEmpty(code)) codes.Add(code);
                }
            }
            catch { /* 패키지 API 가 다르면 비운다 */ }
            return codes.Distinct().ToList();
        }

        private static List<string> I2Codes()
        {
            var codes = new List<string>();
            try
            {
                Type manager = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("I2.Loc.LocalizationManager")).FirstOrDefault(t => t != null);
                MethodInfo get = manager?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "GetAllLanguagesCode");
                if (get == null) return codes;
                object[] args = get.GetParameters().Select(p => p.HasDefaultValue ? p.DefaultValue : (object) true).ToArray();
                if (get.Invoke(null, args) is IEnumerable list) codes.AddRange(list.OfType<string>().Where(c => !string.IsNullOrEmpty(c)));
            }
            catch { /* I2 없음 */ }
            return codes.Distinct().ToList();
        }

        // 코드 → Unity 기기 언어.
        public static SystemLanguage SystemLanguageOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return SystemLanguage.Unknown;
            string lower = code.ToLowerInvariant();
            if (lower.StartsWith("zh"))
                return lower.Contains("tw") || lower.Contains("hk") || lower.Contains("hant") || lower.Contains("mo") ? SystemLanguage.ChineseTraditional : SystemLanguage.ChineseSimplified;
            try
            {
                CultureInfo culture = CultureInfo.GetCultureInfo(code.Replace('_', '-'));
                while (!culture.IsNeutralCulture && culture.Parent != null && culture.Parent != CultureInfo.InvariantCulture) culture = culture.Parent;
                string name = culture.EnglishName.Split(' ', '(')[0];
                if (name == "Norwegian") return SystemLanguage.Norwegian;
                return Enum.TryParse(name, true, out SystemLanguage language) ? language : SystemLanguage.Unknown;
            }
            catch { return SystemLanguage.Unknown; }
        }

        // 저장 값·요청에서 이 언어를 나타낼 수 있는 표기. 순서가 같아서 언어끼리 같은 칸끼리 바꿔 끼울 수 있다.
        // [0] 코드(ko-KR) [1] 밑줄(ko_KR) [2] 기기 언어 이름(Korean) [3] 언어만(ko)
        public static string[] Forms(string code)
        {
            if (string.IsNullOrEmpty(code)) return Array.Empty<string>();
            SystemLanguage language = SystemLanguageOf(code);
            string languageOnly = code.Split('-', '_')[0];
            return new[] { code, code.Replace('-', '_'), language == SystemLanguage.Unknown ? null : language.ToString(), languageOnly };
        }

        // 언어마다 달라야 하는 서버 요청을 알아보는 표기(짧은 언어 코드는 오탐이 많아 뺀다).
        public static string[] LiveMarkers(IEnumerable<string> codes) =>
            codes.Where(c => !string.IsNullOrEmpty(c)).SelectMany(c => Forms(c).Take(3)).Where(f => !string.IsNullOrEmpty(f)).Distinct().ToArray();
    }
}
