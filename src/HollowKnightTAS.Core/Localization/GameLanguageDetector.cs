using System;

namespace HollowKnightTAS.Core.Localization
{
    /// <summary>
    /// Classifies Hollow Knight language codes and enum values into Chinese or English UI selection.
    /// </summary>
    public static class GameLanguageDetector
    {
        public const int ChineseStudioIndex = 0;
        public const int EnglishStudioIndex = 1;

        /// <summary>
        /// Returns true when the given language code or culture name belongs to Chinese.
        /// </summary>
        public static bool IsChineseLanguageCode(string? languageCode)
        {
            if (languageCode == null)
            {
                return false;
            }

            var trimmed = languageCode.Trim('\0', ' ', '\t', '\r', '\n');
            if (trimmed.Length == 0)
            {
                return false;
            }

            return trimmed.Equals("zh", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("zh_", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)
                || trimmed.IndexOf("chinese", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Returns true when the Hollow Knight SupportedLanguages / LanguageCode enum integer represents Chinese.
        /// </summary>
        public static bool IsChineseLanguageValue(int enumValue)
        {
            return enumValue >= 199 && enumValue <= 203;
        }

        /// <summary>
        /// Maps a game language code to Studio's language index (0 = Chinese, 1 = English).
        /// </summary>
        public static int ToStudioLanguageIndex(string? languageCode)
        {
            return IsChineseLanguageCode(languageCode)
                ? ChineseStudioIndex
                : EnglishStudioIndex;
        }

        /// <summary>
        /// Normalizes a game language code to "zh" or "en".
        /// </summary>
        public static string NormalizeCode(string? languageCode)
        {
            return IsChineseLanguageCode(languageCode) ? "zh" : "en";
        }
    }
}
