using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Reuses the existing complete incoming field walk. Only its three finite
    // retired managed owners are added as raw roots; no new world scan occurs.
    internal sealed class CanonicalBossChallengePolicy : IDisposable
    {
        internal const string SetupRoot = "static:Assembly-CSharp:BossSceneController.SetupEvent";
        private const string PrimaryRoot = "explicit-challenge/original-primary", UiRoot = "explicit-challenge/retired-ui", StatueRoot = "explicit-challenge/retired-statue";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static CanonicalBossChallengePolicy? Incoming { get; private set; }
        private readonly CanonicalBossChallengeAssets assets;
        private readonly BossSceneController.SetupEventDelegate? original;
        private object? primary;
        private BossChallengeUI? ui;
        private BossStatue? statue;
        private Action? cached;
        private CanonicalBossChallengeContext? context;
        private string? unsupported;
        private bool completedWalk, sawSetup;
        private static FieldInfo Field(Type type, string field) => type.GetField(field, Flags)
            ?? throw new InvalidDataException("Original challenge field absent: " + field);

        internal CanonicalBossChallengePolicy(CanonicalBossChallengeAssets assets)
        {
            if (Incoming != null) throw new InvalidDataException("Challenge incoming scope was reentered.");
            this.assets = assets; original = BossSceneController.SetupEvent;
            if (original == null) return;
            if (original.Target is CanonicalBossChallengeContext) {
                CanonicalBossChallengeCarry.Require(new CanonicalBossChallengeCarry.Data { AssetManifest = assets.Manifest, Pending = original }, assets);
                return;
            }
            try {
                if (!CanonicalBossChallengeCarry.IsKnownSequence || original.GetInvocationList().Length != 1
                    || original.Target?.GetType() != CanonicalBossChallengeOriginal.Primary
                    || original.Method.Name != "<LoadBoss>b__0")
                    throw new InvalidDataException("Challenge pending producer or boss sequence is unsupported.");
                if (original.Method.MetadataToken != 100666692 || original.Method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                    CanonicalSceneMappingCarry.WarnOnce("BossChallengePolicy:Original:" + original.Method.Module.ModuleVersionId + ":" + original.Method.MetadataToken,
                        "[HKTAS] Challenge pending producer MVID/token differs from pinned original (continuing): mvid="
                        + original.Method.Module.ModuleVersionId.ToString("D") + ";token=" + original.Method.MetadataToken);
                primary = original.Target;
                ui = (BossChallengeUI?)Field(primary.GetType(), "<>4__this").GetValue(primary);
                statue = ReferenceEquals(ui, null) ? null : (BossStatue?)Field(typeof(BossChallengeUI), "bossStatue").GetValue(ui);
                if (ReferenceEquals(ui, null) || ReferenceEquals(statue, null) || ui!.GetType() != typeof(BossChallengeUI)
                    || statue!.GetType() != typeof(BossStatue) || ui != null || statue != null)
                    throw new InvalidDataException("Challenge conversion requires the exact original retired UI and statue.");
                cached = (Action?)Field(primary.GetType(), "<>9__1").GetValue(primary);
                if (cached != null && (cached.GetInvocationList().Length != 1 || !ReferenceEquals(cached.Target, primary)
                    || cached.Method.Name != "<LoadBoss>b__1" || cached.Method.Module != original.Method.Module))
                    throw new InvalidDataException("Challenge original completion cache is foreign.");
                if (cached != null && cached.Method.MetadataToken != 100666693)
                    CanonicalSceneMappingCarry.WarnOnce("BossChallengePolicy:Cached:" + cached.Method.MetadataToken,
                        "[HKTAS] Challenge cached completion token differs from pinned 100666693 (continuing): actual=" + cached.Method.MetadataToken);
                context = new CanonicalBossChallengeContext { Level = (int)Field(primary.GetType(), "level").GetValue(primary)!,
                    Statue = new CanonicalBossStatueContext { StatueStatePD = statue!.statueStatePD, DreamStatueStatePD = statue.dreamStatueStatePD,
                        IsAlwaysUnlocked = statue.isAlwaysUnlocked, BossScene = statue.bossScene } };
                if (cached != null) context.CachedCompletion = context.Complete;
                CanonicalBossChallengeCarry.Require(new CanonicalBossChallengeCarry.Data { AssetManifest = assets.Manifest, Pending = context.Setup }, assets);
                Incoming = this;
            } catch (InvalidDataException error) { unsupported = error.Message; }
        }
        internal bool Owns(object? value) => value != null && (ReferenceEquals(value, original) || ReferenceEquals(value, primary)
            || ReferenceEquals(value, ui) || ReferenceEquals(value, statue) || ReferenceEquals(value, cached));
        internal void BeginWalk() { completedWalk = false; sawSetup = false; }
        internal void ObserveReference(object value, string root)
        {
            if (!Owns(value)) return;
            if (ReferenceEquals(value, original) && root == SetupRoot) { sawSetup = true; return; }
            if (ReferenceEquals(value, primary) && (root == SetupRoot || root == PrimaryRoot)) return;
            if (ReferenceEquals(value, ui) && (root == SetupRoot || root == PrimaryRoot)) return;
            if (ReferenceEquals(value, statue) && root == UiRoot) return;
            if (ReferenceEquals(value, cached) && (root == SetupRoot || root == PrimaryRoot)) return;
            throw new InvalidDataException("Challenge retired graph has an unregistered incoming root: " + root);
        }
        internal void ObserveSlot(object owner, FieldInfo? field, object? value)
        {
            if (!Owns(value)) return;
            if (field != null && (ReferenceEquals(owner, primary) && field.DeclaringType == primary!.GetType()
                    && (field.Name == "<>4__this" && ReferenceEquals(value, ui) || field.Name == "<>9__1" && ReferenceEquals(value, cached))
                || ReferenceEquals(owner, ui) && field.DeclaringType == typeof(BossChallengeUI)
                    && field.Name == "bossStatue" && ReferenceEquals(value, statue))) return;
            throw new InvalidDataException("Challenge retired graph has an unregistered incoming field/array alias.");
        }
        internal void ReadFiniteRoots(Action<object, string, bool> read)
        {
            read(primary!, PrimaryRoot, true); read(ui!, UiRoot, true); read(statue!, StatueRoot, true);
        }
        internal void CompleteWalk()
        {
            if (!sawSetup) throw new InvalidDataException("Original challenge pending static was not observed by the complete incoming walk.");
            completedWalk = true;
        }
        internal object Apply()
        {
            if (!ReferenceEquals(original, BossSceneController.SetupEvent)) throw new InvalidDataException("Challenge static changed during Canon.");
            if (context == null) return new { converted = false, unsupported, pending = original != null };
            if (!completedWalk || !CanonicalBossChallengeCarry.IsKnownSequence) throw new InvalidDataException("Challenge incoming proof or sequence admission is absent.");
            // The raw retired owners remain read-only. Check their complete
            // semantic binding again after all original Canon callbacks.
            if (!ReferenceEquals(Field(primary!.GetType(), "<>4__this").GetValue(primary), ui)
                || !ReferenceEquals(Field(typeof(BossChallengeUI), "bossStatue").GetValue(ui), statue)
                || (int)Field(primary.GetType(), "level").GetValue(primary)! != context.Level
                || !ReferenceEquals(Field(primary.GetType(), "<>9__1").GetValue(primary), cached)
                || statue!.statueStatePD != context.Statue.StatueStatePD || statue.dreamStatueStatePD != context.Statue.DreamStatueStatePD
                || statue.isAlwaysUnlocked != context.Statue.IsAlwaysUnlocked || !ReferenceEquals(statue.bossScene, context.Statue.BossScene))
                throw new InvalidDataException("Challenge retired semantic binding changed after incoming proof.");
            var pending = (BossSceneController.SetupEventDelegate)context.Setup;
            CanonicalBossChallengeCarry.Require(new CanonicalBossChallengeCarry.Data { AssetManifest = assets.Manifest, Pending = pending }, assets);
            BossSceneController.SetupEvent = pending;
            if (!ReferenceEquals(BossSceneController.SetupEvent, pending)) throw new InvalidDataException("Canonical challenge static readback differs.");
            return new { converted = true, cachePreserved = cached != null, originalReturnMethod = true, incomingCompleteWithinRegisteredWorld = true };
        }
        public void Dispose() { if (ReferenceEquals(Incoming, this)) Incoming = null; }
    }
}
