using HollowKnightTAS.Core.Localization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests
{
    [TestClass]
    public sealed class GameLanguageDetectorTests
    {
        [TestMethod]
        public void ClassifiesChineseLanguageCodesAndFallsBackToEnglishForAllOthers()
        {
            string[] chineseCodes =
            {
                "ZH",
                "zh",
                "ZH_CN",
                "ZH_TW",
                "ZH_HK",
                "ZH_SG",
                "zh-Hans",
                "zh-Hant",
                "Chinese",
                "ChineseSimplified",
                "ChineseTraditional",
                "  ZH\0\r\n "
            };

            foreach (var code in chineseCodes)
            {
                Assert.IsTrue(GameLanguageDetector.IsChineseLanguageCode(code), code);
                Assert.AreEqual(GameLanguageDetector.ChineseStudioIndex, GameLanguageDetector.ToStudioLanguageIndex(code), code);
                Assert.AreEqual("zh", GameLanguageDetector.NormalizeCode(code), code);
            }

            string?[] nonChineseCodes =
            {
                "EN",
                "JA",
                "KO",
                "FR",
                "DE",
                "ES",
                "IT",
                "PT",
                "RU",
                "unknown",
                "",
                "  \0 ",
                null
            };

            foreach (var code in nonChineseCodes)
            {
                Assert.IsFalse(GameLanguageDetector.IsChineseLanguageCode(code), code ?? "<null>");
                Assert.AreEqual(GameLanguageDetector.EnglishStudioIndex, GameLanguageDetector.ToStudioLanguageIndex(code), code ?? "<null>");
                Assert.AreEqual("en", GameLanguageDetector.NormalizeCode(code), code ?? "<null>");
            }
        }

        [TestMethod]
        public void ClassifiesHollowKnightLanguageEnumValues()
        {
            for (var code = 199; code <= 203; code++)
            {
                Assert.IsTrue(GameLanguageDetector.IsChineseLanguageValue(code), code.ToString());
            }

            int[] nonChineseValues = { 0, 37, 44, 57, 82, 105, 109, 117, 147, 154, 198, 204 };
            foreach (var code in nonChineseValues)
            {
                Assert.IsFalse(GameLanguageDetector.IsChineseLanguageValue(code), code.ToString());
            }
        }
    }
}
