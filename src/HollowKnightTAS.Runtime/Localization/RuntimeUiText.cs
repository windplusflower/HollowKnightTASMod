using HollowKnightTAS.Core.Localization;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Localization
{
    /// <summary>
    /// Resolves in-game Mod UI strings dynamically from the active Hollow Knight language.
    /// </summary>
    public static class RuntimeUiText
    {
        /// <summary>
        /// Reads the current raw Hollow Knight language identifier without caching so live language changes apply immediately.
        /// </summary>
        public static string DetectRawGameLanguage()
        {
            try
            {
                var current = global::Language.Language.CurrentLanguage().ToString();
                if (!string.IsNullOrWhiteSpace(current) && current != "N")
                {
                    return current;
                }
            }
            catch
            {
            }

            try
            {
                var gameManager = GameManager.instance;
                var gameSettings = gameManager == null ? null : gameManager.gameSettings;
                if (gameSettings != null)
                {
                    var settingLanguage = gameSettings.gameLanguage.ToString();
                    if (!string.IsNullOrWhiteSpace(settingLanguage))
                    {
                        return settingLanguage;
                    }
                }
            }
            catch
            {
            }

            try
            {
                var systemLanguage = Application.systemLanguage.ToString();
                if (!string.IsNullOrWhiteSpace(systemLanguage))
                {
                    return systemLanguage;
                }
            }
            catch
            {
            }

            return "EN";
        }

        /// <summary>
        /// Returns true when the current game language belongs to Chinese.
        /// </summary>
        public static bool IsChinese()
        {
            return GameLanguageDetector.IsChineseLanguageCode(DetectRawGameLanguage());
        }

        /// <summary>
        /// Returns the normalized UI language code ("zh" or "en") for the current game language.
        /// </summary>
        public static string CurrentLanguageCode()
        {
            return IsChinese() ? "zh" : "en";
        }

        /// <summary>
        /// Picks Chinese or English UI text according to the active game language.
        /// </summary>
        public static string T(string zh, string en)
        {
            return IsChinese() ? zh : en;
        }
    }
}
