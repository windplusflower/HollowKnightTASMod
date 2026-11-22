using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HollowKnightTAS.Core.Localization;
using Microsoft.Win32;

namespace HollowKnightTAS.Companion.Services
{
    /// <summary>
    /// Diagnoses the default Studio UI language from Hollow Knight's PlayerPrefs registry entries and OS UI culture.
    /// </summary>
    public static class GameLanguageReader
    {
        public const string PlayerPrefsSubKeyPath = @"Software\Team Cherry\Hollow Knight";

        /// <summary>
        /// Resolves Studio language index (0 = Chinese, 1 = English) from PlayerPrefs key-value pairs, or null if absent.
        /// </summary>
        public static int? TryResolveFromPlayerPrefs(IEnumerable<KeyValuePair<string, object?>>? values)
        {
            if (values == null)
            {
                return null;
            }

            string? m2hCode = null;
            int? gameLangValue = null;

            foreach (var pair in values)
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                if (IsPlayerPrefKey(pair.Key, "M2H_lastLanguage"))
                {
                    var decoded = DecodeStringValue(pair.Value);
                    if (!string.IsNullOrEmpty(decoded))
                    {
                        m2hCode = decoded;
                    }
                }
                else if (gameLangValue == null && IsPlayerPrefKey(pair.Key, "GameLang"))
                {
                    gameLangValue = TryReadIntValue(pair.Value);
                }
            }

            if (!string.IsNullOrEmpty(m2hCode))
            {
                return GameLanguageDetector.ToStudioLanguageIndex(m2hCode);
            }

            if (gameLangValue.HasValue)
            {
                return GameLanguageDetector.IsChineseLanguageValue(gameLangValue.Value)
                    ? GameLanguageDetector.ChineseStudioIndex
                    : GameLanguageDetector.EnglishStudioIndex;
            }

            return null;
        }

        /// <summary>
        /// Detects the default Studio language index from Hollow Knight's Windows registry PlayerPrefs, falling back to OS UI culture.
        /// </summary>
        public static int DetectStudioLanguageIndex()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PlayerPrefsSubKeyPath, writable: false);
                if (key != null)
                {
                    var names = key.GetValueNames();
                    var pairs = new List<KeyValuePair<string, object?>>(names.Length);
                    foreach (var name in names)
                    {
                        pairs.Add(new KeyValuePair<string, object?>(name, key.GetValue(name)));
                    }

                    var resolved = TryResolveFromPlayerPrefs(pairs);
                    if (resolved.HasValue)
                    {
                        return resolved.Value;
                    }
                }
            }
            catch
            {
            }

            return GameLanguageDetector.ToStudioLanguageIndex(CultureInfo.CurrentUICulture.Name);
        }

        private static bool IsPlayerPrefKey(string actualKey, string prefix)
        {
            return actualKey.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || actualKey.StartsWith(prefix + "_h", StringComparison.OrdinalIgnoreCase);
        }

        private static string? DecodeStringValue(object value)
        {
            var raw = value switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string text => text,
                _ => null
            };
            if (raw == null)
            {
                return null;
            }

            var trimmed = raw.Trim('\0', ' ', '\t', '\r', '\n');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static int? TryReadIntValue(object value)
        {
            return value switch
            {
                int i => i,
                uint u when u <= int.MaxValue => (int)u,
                long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                short s => s,
                byte b => b,
                _ => null
            };
        }
    }
}
