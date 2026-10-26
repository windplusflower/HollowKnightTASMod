using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Exact ResourceManager -> serialized array slot. No name selects an asset;
    // names are validation only. Source and target run the same startup load.
    internal sealed class CanonicalBossChallengeAssets
    {
        internal const string ResourcePath = "gg/boss sequence tier 2";
        internal const int MarmuIndex = 6;
        internal const string Origin = "OriginalSerialized/resources.assets/19562/BossSequence.bossScenes[6]/resources.assets/19472";
        internal const string BindingPrefix = "BossChallengeAsset/v1/";
        private static readonly KeyValuePair<string, string>[] Files = {
            new KeyValuePair<string, string>("globalgamemanagers", "57ebbd860f452878d82000ed7caa9d40f22436ffcc1e56088eec28683c486efa"),
            new KeyValuePair<string, string>("globalgamemanagers.assets", "094a137c257826aca23e4ba78b12d639c9f6b5f32f92b43fdb99a0348306bc01"),
            new KeyValuePair<string, string>("resources.assets", "f2a9851f0424d94c0231b2689aeb51327fc7054f284bd77db2e3293d80b9386c"),
            new KeyValuePair<string, string>("level470", "a2298edcdfa6888997f39a436ac0a2f1a97d66d3589e7a5b25473401845be6b5"),
            new KeyValuePair<string, string>("Managed/mscorlib.dll", "356e3b1f103313773263feb40e505403bf93f17a86d646d395717f19399ab819"),
            new KeyValuePair<string, string>("level1", CanonicalHudPresentationDirectory.OriginalLevel1Sha256),
            new KeyValuePair<string, string>("Managed/Assembly-CSharp.dll", "5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d")
        };
        private static readonly FieldInfo Scenes = typeof(BossSequence).GetField("bossScenes", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Original BossSequence.bossScenes is absent.");
        private readonly BossSequence sequence;
        private readonly BossScene[] scenes;
        private readonly CanonicalAssetOriginRegistry origins = new CanonicalAssetOriginRegistry();
        private readonly Dictionary<string, UnityEngine.Object> assetSlots = new Dictionary<string, UnityEngine.Object>(StringComparer.Ordinal);
        private bool capturingOriginalSlots = true;
        private readonly string fingerprint;
        private sealed class References : IEqualityComparer<UnityEngine.Object> {
            public bool Equals(UnityEngine.Object? x, UnityEngine.Object? y) => ReferenceEquals(x, y);
            public int GetHashCode(UnityEngine.Object value) => RuntimeHelpers.GetHashCode(value);
        }
        internal BossScene Marmu => scenes[MarmuIndex];
        internal string Manifest => origins.ManifestHash() + ":" + fingerprint;

        internal CanonicalBossChallengeAssets()
        {
            CanonicalBossChallengeOriginal.Validate();
            if (Scenes.FieldType != typeof(BossScene[]) || Scenes.DeclaringType != typeof(BossSequence))
                throw new InvalidDataException("Original BossSequence serialized array ABI differs.");
            foreach (var file in Files) {
                using var stream = File.OpenRead(Path.Combine(Application.dataPath, file.Key));
                using var hash = SHA256.Create();
                var actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (actual != file.Value) throw new InvalidDataException("Original challenge serialized source file differs: " + file.Key);
            }
            // Call the original Resources factory exactly once. Never call the
            // gameplay GetBossScene/IsUnlocked methods to resolve this slot.
            var beforeRandom = UnityEngine.Random.state;
            var beforeParticles = ParticleRandom();
            sequence = Resources.Load<BossSequence>(ResourcePath);
            if (!beforeRandom.Equals(UnityEngine.Random.state) || !beforeParticles.SequenceEqual(ParticleRandom()))
                throw new InvalidDataException("Original challenge resource preparation changed a random stream.");
            if (sequence == null || sequence.GetType() != typeof(BossSequence))
                throw new InvalidDataException("Original exact challenge sequence resource is absent.");
            scenes = (BossScene[]?)Scenes.GetValue(sequence) ?? throw new InvalidDataException("Original challenge array is absent.");
            if (scenes.Length != 12 || scenes.Any(scene => scene == null || scene.GetType() != typeof(BossScene))
                || Marmu.name != "Marmu Boss Scene")
                throw new InvalidDataException("Original challenge resource array shape differs.");
            origins.Add(Origin, Marmu, typeof(BossScene).FullName!, Marmu.name);
            origins.Seal();
            fingerprint = Fingerprint();
            capturingOriginalSlots = false;
            Modding.Logger.Log("[HKTAS] Original challenge asset startup prepared: resource=" + ResourcePath
                + ";slot=" + MarmuIndex + ";arrayLength=12;manifest=" + Manifest + ";gameplayGetterInvoked=false");
        }

        private string Fingerprint()
        {
            var paths = new Dictionary<UnityEngine.Object, string>(new References());
            void Register(UnityEngine.Object asset, string path)
            {
                if (asset == null) throw new InvalidDataException("Original challenge asset died.");
                if (!paths.ContainsKey(asset)) paths.Add(asset, path);
            }
            Register(sequence, "sequence");
            for (var i = 0; i < scenes.Length; i++) Register(scenes[i], "bossScenes[" + i + "]");
            LoadingFieldDigest? reader = null;
            var observed = new HashSet<string>(StringComparer.Ordinal);
            string? Reference(object value)
            {
                if (!(value is UnityEngine.Object asset)) return null;
                if (asset == null) throw new InvalidDataException("Original challenge asset reference died.");
                var slot = reader!.DiagnosticRoot + ":" + reader.DiagnosticPath;
                observed.Add(slot);
                if (capturingOriginalSlots) {
                    if (assetSlots.TryGetValue(slot, out var previous) && !ReferenceEquals(previous, asset))
                        throw new InvalidDataException("Original challenge source slot is ambiguous: " + slot);
                    assetSlots[slot] = asset;
                } else if (!assetSlots.TryGetValue(slot, out var original) || !ReferenceEquals(original, asset))
                    throw new InvalidDataException("Original challenge serialized asset slot was replaced: " + slot);
                if (paths.TryGetValue(asset, out var path)) return path;
                // Other native serialized references are observations, not
                // resolver candidates. Their complete source file is pinned.
                return "serialized-native:" + asset.GetType().FullName + ":" + asset.name;
            }
            using (reader = new LoadingFieldDigest(Reference, type => type == typeof(UnityEngine.Object))) {
            reader.BeginSharedGraph();
            try {
                var tokens = new List<string>();
                foreach (var asset in new UnityEngine.Object[] { sequence }.Concat(scenes)) {
                    var result = reader.Capture(asset, paths[asset], expandRoot: true);
                    if (result.Unsupported.Count != 0) throw new InvalidDataException("Original challenge asset fields are unsupported.");
                    tokens.Add(result.Sha256);
                }
                if (!capturingOriginalSlots && observed.Count != assetSlots.Count)
                    throw new InvalidDataException("Original challenge serialized asset slot disappeared.");
                using var hash = SHA256.Create();
                return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", tokens))))
                    .Replace("-", "").ToLowerInvariant();
            } finally { reader.EndSharedGraph(); }
            }
        }
        private static uint[] ParticleRandom()
        {
            var payload = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload");
            return (uint[])payload.GetType("HollowKnightTAS.ClockPayload.EarlyModWallClockBootstrap", true)!
                .GetMethod("CaptureParticleRandomState", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;
        }
        internal void RequireOriginal(BossScene? source)
        {
            if (sequence == null || !ReferenceEquals(Scenes.GetValue(sequence), scenes) || scenes.Length != 12
                || !ReferenceEquals(source, Marmu) || source == null || Fingerprint() != fingerprint)
                throw new InvalidDataException("Source challenge asset is not the sealed original serialized slot.");
            origins.RequireOrigin(Origin, source);
        }
        internal string? Bind(object source)
        {
            if (!ReferenceEquals(source, Marmu)) return null;
            RequireOriginal(Marmu);
            return BindingPrefix + origins.Bind(source, typeof(BossScene).FullName!, Marmu.name);
        }
        internal object Resolve(string binding)
        {
            if (!binding.StartsWith(BindingPrefix, StringComparison.Ordinal)) throw new InvalidDataException("Challenge asset binding prefix differs.");
            var source = origins.Resolve(binding.Substring(BindingPrefix.Length));
            RequireOriginal(source as BossScene);
            return source;
        }
    }
}
