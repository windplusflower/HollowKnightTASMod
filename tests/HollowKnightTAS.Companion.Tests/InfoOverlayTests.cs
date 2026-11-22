using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using HollowKnightTAS.Companion.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class InfoOverlayTests
    {
        private static System.Collections.Generic.IReadOnlyDictionary<string, JsonElement> Values(string values)
            => InfoOverlayModel.Decode("{\"schemaVersion\":1,\"values\":" + values + "}");

        [TestMethod]
        public void DefaultsExposeEightUsefulRowsWithoutQuickSlotConflict()
        {
            var settings = InfoOverlaySettings.Defaults(); settings.Validate();
            Assert.IsTrue(settings.Enabled); Assert.AreEqual(8, settings.Items.Count);
            CollectionAssert.AreEqual(new[] { "frame", "room", "position", "velocity", "dash", "shade", "healthPair", "soul" }, settings.Items.Select(x => x.Field).ToArray());
            Assert.AreEqual("F11", settings.Hotkey);
            Assert.AreEqual("右上", settings.Anchor);
            Assert.AreEqual(8d, settings.MarginX); Assert.AreEqual(8d, settings.MarginY);
            Assert.IsFalse(settings.IncludeInVideo);
            Assert.AreEqual("", InfoOverlayModel.VideoSettingsJson(settings));
            Assert.AreEqual("rt", InfoOverlayModel.NewItem("rt").Expression);
            Assert.AreEqual("gt", InfoOverlayModel.NewItem("gt").Expression);
        }
        [TestMethod]
        public void PausedValuesDoNotChangeAndNewSnapshotsReplaceRatherThanMerge()
        {
            var row = InfoOverlayModel.NewItem("position");
            var playing = Values("{\"x\":24.3819,\"y\":8.15}");
            Assert.AreEqual("X 24.382   Y 8.150", InfoOverlayModel.Format(row, playing));
            Assert.AreEqual("X 24.382   Y 8.150", InfoOverlayModel.Format(row, playing));
            Assert.AreEqual("—", InfoOverlayModel.Format(row, Values("{\"room\":\"Menu_Title\"}")));
            Assert.AreEqual("X 5.000   Y 6.000", InfoOverlayModel.Format(row, Values("{\"x\":5,\"y\":6}")));
        }
        [TestMethod]
        public void CooldownsClampDisplayOnlyAndReadyIsConfigurable()
        {
            var row = InfoOverlayModel.NewItem("dash"); row.ReadyAtZero = false;
            var values = Values("{\"dash\":-0.001}");
            Assert.AreEqual("0.00 s", InfoOverlayModel.Format(row, values));
            Assert.AreEqual(-.001, values["dash"].GetDouble());
            row.ReadyAtZero = true;
            Assert.AreEqual(UiText.T("就绪"), InfoOverlayModel.Format(row, values));
            Assert.AreEqual("—", InfoOverlayModel.Format(row, Values("{\"dash\":null}")));
            row.Precision = 3; row.Unit = "sec";
            Assert.AreEqual("0.820 sec", InfoOverlayModel.Format(row, Values("{\"dash\":0.82}")));
        }
        [TestMethod]
        public void WrongTypesAndMissingHeroDoNotLookReady()
        {
            foreach (var payload in new[] { "{}", "{\"dash\":\"bad\"}", "{\"dash\":false}" })
                Assert.AreEqual("—", InfoOverlayModel.Format(InfoOverlayModel.NewItem("dash"), Values(payload)));
            Assert.AreEqual("—", InfoOverlayModel.Format(InfoOverlayModel.NewItem("healthPair"), Values("{\"health\":5}")));
        }
        [TestMethod]
        public void SettingsRoundTripPreservesOrderHiddenRowsCustomFormattingAndEmptyList()
        {
            var path = Path.Combine(Path.GetTempPath(), "hktas-info-" + Guid.NewGuid().ToString("N"), "settings.json");
            try
            {
                var settings = InfoOverlaySettings.Defaults();
                settings.Items.Move(0, 3); settings.Items[0].Enabled = false; settings.Items[1].Label = "Knight";
                settings.Items[1].Color = "#FFAABB"; settings.Items[1].Precision = 5;
                settings.Anchor = "右下"; settings.MarginX = 28; settings.Hotkey = "F12"; settings.IncludeInVideo = true;
                settings.Save(path); var loaded = InfoOverlaySettings.Load(path);
                Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));
                loaded.Items.Clear(); loaded.Save(path); Assert.AreEqual(0, InfoOverlaySettings.Load(path).Items.Count);
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        }
        [TestMethod]
        public void InvalidEditsCannotReplaceLastGoodFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "hktas-info-" + Guid.NewGuid().ToString("N"), "settings.json");
            try
            {
                var settings = InfoOverlaySettings.Defaults(); settings.Save(path); var original = File.ReadAllText(path);
                settings.Items[0].Color = "red";
                Assert.ThrowsExactly<InvalidDataException>(() => settings.Save(path));
                Assert.AreEqual(original, File.ReadAllText(path));
                settings.Items[0].Color = ""; settings.MarginX = double.NaN;
                Assert.ThrowsExactly<InvalidDataException>(() => settings.Validate());
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        }

        [TestMethod]
        public void CustomWatchesFormatEachScalarTypeAndKeepIndependentExpressions()
        {
            var row = InfoOverlayModel.NewItem("custom"); row.Expression = "hero.cooldown"; row.Precision = 2;
            Assert.AreEqual("0.82", InfoOverlayModel.Format(row, Values("{\"watch:hero.cooldown\":0.82}")));
            row.Expression = "hero.flag";
            Assert.AreEqual(UiText.T("是"), InfoOverlayModel.Format(row, Values("{\"watch:hero.flag\":true}")));
            row.Expression = "game.state";
            Assert.AreEqual("PLAYING", InfoOverlayModel.Format(row, Values("{\"watch:game.state\":\"PLAYING\"}")));
            Assert.AreEqual("—", InfoOverlayModel.Format(row, Values("{\"watch:hero.flag\":true}")));
        }
        [TestMethod]
        public void MigrationMovesOnlyOldDefaultPositionAndPreservesUserRows()
        {
            var path = Path.GetTempFileName();
            try
            {
                var settings = InfoOverlaySettings.Defaults(); settings.Version = 1;
                settings.Anchor = "左上"; settings.MarginX = 16; settings.MarginY = 160;
                settings.Items.Add(InfoOverlayModel.NewItem("facingRight"));
                File.WriteAllText(path, JsonSerializer.Serialize(settings));
                var migrated = InfoOverlaySettings.Load(path);
                Assert.AreEqual(3, migrated.Version); Assert.AreEqual("右上", migrated.Anchor);
                Assert.AreEqual(8d, migrated.MarginY); Assert.AreEqual(9, migrated.Items.Count);
                settings.MarginX = 42;
                File.WriteAllText(path, JsonSerializer.Serialize(settings));
                var customized = InfoOverlaySettings.Load(path);
                Assert.AreEqual("左上", customized.Anchor); Assert.AreEqual(42d, customized.MarginX);
            }
            finally { File.Delete(path); }
        }
        [TestMethod]
        public void VideoOptionsFreezeEnabledRowsAndShareOverlayFormatting()
        {
            var settings = InfoOverlaySettings.Defaults(); settings.IncludeInVideo = true; settings.Enabled = false;
            settings.Items[0].Enabled = false;
            var rt = InfoOverlayModel.NewItem("rt"); rt.Expression = "rt - 12.5"; settings.Items.Add(rt);
            var json = InfoOverlayModel.VideoSettingsJson(settings);
            var video = JsonSerializer.Deserialize<HollowKnightTAS.Core.Media.InfoOverlayVideoSettings>(json)!;
            video.Validate(); Assert.AreEqual(8, video.Rows.Count);
            Assert.IsFalse(video.Rows.Any(r => r.Id == "frame"));
            Assert.AreEqual("rt - 12.5", video.Rows.Last().Expression);
            Assert.AreEqual(InfoOverlayModel.Format(rt, Values("{\"watch:rt - 12.5\":2.75}")),
                video.Format(video.Rows.Last(), _ => 2.75));
            rt.Expression = "rt - 99";
            Assert.AreEqual("rt - 12.5", video.Rows.Last().Expression);
            Assert.AreEqual(json, JsonSerializer.Serialize(video));
        }
        [TestMethod]
        public void PresetExpressionsCanBeEditedPersistedAndReset()
        {
            var path = Path.Combine(Path.GetTempPath(), "hktas-info-" + Guid.NewGuid().ToString("N"), "settings.json");
            try
            {
                var settings = InfoOverlaySettings.Defaults();
                var row = settings.Items.First(i => i.Field == "healthPair");
                row.Expression = "health / maxHealth * 100"; row.Precision = 1; row.Unit = "%";
                settings.Save(path);
                var loaded = InfoOverlaySettings.Load(path); row = loaded.Items.First(i => i.Field == "healthPair");
                CollectionAssert.AreEqual(new[] { row.Expression }, InfoOverlayModel.Watches(loaded));
                Assert.AreEqual("50.0 %", InfoOverlayModel.Format(row, Values("{\"watch:health / maxHealth * 100\":50}")));
                row.Expression = "false";
                Assert.AreEqual(UiText.T("否"), InfoOverlayModel.Format(row, Values("{\"watch:false\":false}")));
                row.Expression = InfoOverlayModel.DefaultExpression(row.Field); row.Unit = "";
                Assert.AreEqual(0, InfoOverlayModel.Watches(loaded).Length);
                Assert.AreEqual("5 / 9", InfoOverlayModel.Format(row, Values("{\"health\":5,\"maxHealth\":9}")));
                row.Expression = "health +";
                var lastGood = File.ReadAllText(path);
                Assert.ThrowsExactly<FormatException>(() => loaded.Save(path));
                Assert.AreEqual(lastGood, File.ReadAllText(path));
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        }
        [TestMethod]
        public void V2UnusedPresetQueriesDoNotOverrideBuiltinsAfterMigration()
        {
            var path = Path.GetTempFileName();
            try
            {
                var settings = InfoOverlaySettings.Defaults(); settings.Version = 2;
                foreach (var item in settings.Items) item.Expression = "hero.dashCooldownTimer";
                var custom = InfoOverlayModel.NewItem("custom"); custom.Expression = "player.geo"; settings.Items.Add(custom);
                File.WriteAllText(path, JsonSerializer.Serialize(settings));
                var loaded = InfoOverlaySettings.Load(path);
                CollectionAssert.AreEqual(new[] { "player.geo" }, InfoOverlayModel.Watches(loaded));
                Assert.AreEqual("frame", loaded.Items[0].Expression);
            }
            finally { File.Delete(path); }
        }
        [TestMethod]
        public void CompletionTargetsOnlyCaretTokenAndSkipsQuotedNames()
        {
            const string expression = "health + player.ge + 12";
            var match = InfoExpressionCompletion.At(expression, expression.IndexOf(" + 12", StringComparison.Ordinal));
            CollectionAssert.Contains(match.Matches, "player.geo");
            Assert.AreEqual("health + player.geo + 12", expression.Remove(match.Start, match.Length).Insert(match.Start, "player.geo"));
            Assert.AreEqual(0, InfoExpressionCompletion.At("fsm(\"/Knight\", \"Spe", 18).Matches.Length);
            CollectionAssert.Contains(InfoExpressionCompletion.At("hero.cState.", 12).Matches, "hero.cState.onGround");
            CollectionAssert.Contains(InfoExpressionCompletion.At("tf", 2).Matches, "tft");
            CollectionAssert.Contains(InfoExpressionCompletion.At("rn", 2).Matches, "rng");
            CollectionAssert.Contains(InfoExpressionCompletion.At("rng", 3).Matches, "rngRolls");
            CollectionAssert.Contains(InfoExpressionCompletion.At("rng", 3).Matches, "rngDelta");
        }
        [TestMethod]
        public void TftAndRngFieldsFormatValidatePersistAndExport()
        {
            var tft = InfoOverlayModel.NewItem("tft");
            Assert.AreEqual("ms", tft.Unit);
            Assert.AreEqual(4, tft.Precision);
            Assert.AreEqual("tft", tft.Expression);
            Assert.AreEqual("16.6667 ms", InfoOverlayModel.Format(tft, Values("{\"tft\":16.66666}")));
            Assert.AreEqual("—", InfoOverlayModel.Format(tft, Values("{}")));

            var rng = InfoOverlayModel.NewItem("rng");
            Assert.AreEqual("rng", rng.Expression);
            Assert.AreEqual("1234 +5", InfoOverlayModel.Format(rng, Values("{\"rng\":\"1234 +5\"}")));
            Assert.AreEqual("—", InfoOverlayModel.Format(rng, Values("{}")));

            var path = Path.Combine(Path.GetTempPath(), "hktas-info-" + Guid.NewGuid().ToString("N"), "settings.json");
            try
            {
                var settings = InfoOverlaySettings.Defaults();
                settings.IncludeInVideo = true;
                settings.Items.Add(tft);
                settings.Items.Add(rng);
                settings.Validate();
                settings.Save(path);
                var loaded = InfoOverlaySettings.Load(path);
                Assert.AreEqual(JsonSerializer.Serialize(settings), JsonSerializer.Serialize(loaded));

                var videoJson = InfoOverlayModel.VideoSettingsJson(loaded);
                var video = JsonSerializer.Deserialize<HollowKnightTAS.Core.Media.InfoOverlayVideoSettings>(videoJson)!;
                video.Validate();
                Assert.IsTrue(video.Rows.Any(r => r.Id == "tft" && r.Unit == "ms" && r.Precision == 4));
                Assert.IsTrue(video.Rows.Any(r => r.Id == "rng" && r.Kind == "text"));
            }
            finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        }
    }
}
