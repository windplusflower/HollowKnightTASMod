using System;
using System.IO;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBossChallengeCarry
    {
        internal const string Root = "D/explicit-boss-challenge-context";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(CanonicalBossChallengeContext), typeof(CanonicalBossStatueContext) };
        internal sealed class Data
        {
            public int Version = 1;
            public string AssetManifest = "";
            public BossSceneController.SetupEventDelegate? Pending = null;
            public bool WasCompleted = false;
            public int BossIndex = 0;
            // Independent UI selection history; never infer it from Level.
            // Original RecordLastSelected accepts every Int32; other values
            // follow the original SetFirstSelected default branch unchanged.
            public int LastSelectedButton = -1;
            public BossSequenceController.BossSequenceData? CurrentData = null;
            public BossSequence? CurrentSequence = null;
        }
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo LastSelected = typeof(BossChallengeUI).GetField("lastSelectedButton", Static)
            ?? throw new InvalidDataException("Original challenge selection scalar is absent.");
        private static FieldInfo Field(string name) => typeof(BossSequenceController).GetField(name, Static)
            ?? throw new InvalidDataException("Original boss service static is absent: " + name);
        internal static bool IsKnownSequence => !(bool)Field("<WasCompleted>k__BackingField").GetValue(null)!
            && (int)Field("bossIndex").GetValue(null)! == 0
            && ReferenceEquals(Field("currentData").GetValue(null), null)
            && ReferenceEquals(Field("currentSequence").GetValue(null), null);
        internal static void Require(Data data, CanonicalBossChallengeAssets assets)
        {
            if (data.Version != 1 || data.AssetManifest != assets.Manifest || data.WasCompleted || data.BossIndex != 0
                || !ReferenceEquals(data.CurrentData, null) || !ReferenceEquals(data.CurrentSequence, null))
                throw new InvalidDataException("Boss service source version, asset origin or sequence state differs.");
            if (data.Pending == null) return;
            if (CanonicalBossChallengeCallback.Describe(data.Pending) != "setup") throw new InvalidDataException("Boss service setup identity differs.");
            var context = (CanonicalBossChallengeContext)data.Pending.Target!;
            if (context.Level < 0 || context.Level > 2 || context.Statue == null
                || context.Statue.StatueStatePD?.Length > 512 || context.Statue.DreamStatueStatePD?.Length > 512)
                throw new InvalidDataException("Boss service context fields differ.");
            if (!ReferenceEquals(context.Statue.BossScene, null)) assets.RequireOriginal(context.Statue.BossScene);
            if (context.CachedCompletion != null && (CanonicalBossChallengeCallback.Describe(context.CachedCompletion) != "complete"
                || !ReferenceEquals(context.CachedCompletion.Target, context)))
                throw new InvalidDataException("Boss service completion cache alias differs.");
        }
        internal static Data Capture(CanonicalBossChallengeAssets assets, bool directoryOnly)
        {
            if (!IsKnownSequence) throw new InvalidDataException("Active or unknown boss sequence is outside the admitted snapshot domain.");
            var data = new Data { AssetManifest = assets.Manifest, Pending = directoryOnly ? null : BossSceneController.SetupEvent,
                LastSelectedButton = (int)LastSelected.GetValue(null)! };
            Require(data, assets); return data;
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var root = view.Root(Root);
            return new Data {
                Version = (int)view.Field(root, typeof(Data), nameof(Data.Version))!,
                AssetManifest = (string)view.Field(root, typeof(Data), nameof(Data.AssetManifest))!,
                Pending = (BossSceneController.SetupEventDelegate?)view.Field(root, typeof(Data), nameof(Data.Pending)),
                WasCompleted = (bool)view.Field(root, typeof(Data), nameof(Data.WasCompleted))!,
                BossIndex = (int)view.Field(root, typeof(Data), nameof(Data.BossIndex))!,
                LastSelectedButton = (int)view.Field(root, typeof(Data), nameof(Data.LastSelectedButton))!,
                CurrentData = (BossSequenceController.BossSequenceData?)view.Field(root, typeof(Data), nameof(Data.CurrentData)),
                CurrentSequence = (BossSequence?)view.Field(root, typeof(Data), nameof(Data.CurrentSequence)) };
        }
        internal static Action PrepareApply(Data source, CanonicalBossChallengeAssets assets, Func<bool> admission)
        {
            Require(source, assets);
            if (!IsKnownSequence || !admission()) throw new InvalidDataException("Boss service target sequence is outside its admitted domain.");
            var previous = BossSceneController.SetupEvent;
            var previousSelection = (int)LastSelected.GetValue(null)!;
            return () => {
                if (!admission() || !IsKnownSequence || !ReferenceEquals(previous, BossSceneController.SetupEvent)
                    || (int)LastSelected.GetValue(null)! != previousSelection)
                    throw new InvalidDataException("Boss service target changed after preparation.");
                Require(source, assets);
                BossSceneController.SetupEvent = source.Pending;
                LastSelected.SetValue(null, source.LastSelectedButton);
                if (!ReferenceEquals(BossSceneController.SetupEvent, source.Pending) || !IsKnownSequence
                    || (int)LastSelected.GetValue(null)! != source.LastSelectedButton)
                    throw new InvalidDataException("Boss service static readback differs.");
            };
        }
    }
}
