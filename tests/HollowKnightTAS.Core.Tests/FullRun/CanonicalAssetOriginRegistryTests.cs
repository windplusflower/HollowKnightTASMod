using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalAssetOriginRegistryTests
    {
        [TestMethod]
        public void MapKeySerializedFontMaterialRelationsRejectRuntimeReplacementAndAliasChanges()
        {
            var source = new CanonicalAssetOriginRegistry(); var target = new CanonicalAssetOriginRegistry();
            var original = new object(); var cold = new object(); var sameNameOther = new object();
            foreach (var slot in new[] { "Key/Text/TMP_Text/m_sharedMaterial", "Key/Text/TMP_FontAsset/material" }) {
                source.Add(slot, original, "Material", "perpetua_tmpro Material");
                target.Add(slot, cold, "Material", "perpetua_tmpro Material");
            }
            source.Add("unrelated", sameNameOther, "Material", "perpetua_tmpro Material");
            source.Seal(); target.Seal();
            source.RequireOrigin("Key/Text/TMP_Text/m_sharedMaterial", original);
            var key = source.Bind(original, "Material", "perpetua_tmpro Material")!;
            Assert.AreSame(cold, target.Resolve(key));
            var mapKeyConsumer = target.Resolve(key); var markerConsumer = target.Resolve(key);
            Assert.AreSame(mapKeyConsumer, markerConsumer); // Shared native external, never a material copy.
            Assert.IsFalse(source.ManifestHash() == target.ManifestHash()); // Source has an additional unrelated origin.
            Assert.ThrowsExactly<InvalidDataException>(() => source.RequireOrigin("Key/Text/TMP_Text/m_sharedMaterial", sameNameOther));
            Assert.ThrowsExactly<InvalidDataException>(() => source.RequireOrigin("Key/Text/TMP_Text/m_sharedMaterial", new object()));
            Assert.ThrowsExactly<InvalidDataException>(() => source.RequireOrigin("unknown", original));
            var split = new CanonicalAssetOriginRegistry();
            split.Add("Key/Text/TMP_Text/m_sharedMaterial", new object(), "Material", "perpetua_tmpro Material");
            split.Add("Key/Text/TMP_FontAsset/material", new object(), "Material", "perpetua_tmpro Material"); split.Seal();
            Assert.ThrowsExactly<InvalidDataException>(() => split.Resolve(key));
        }
        [TestMethod]
        public void ColdObjectsBindByOriginalSlotsAndRetainSharedAliases()
        {
            var source = new CanonicalAssetOriginRegistry(); var target = new CanonicalAssetOriginRegistry();
            var original = new object(); var cold = new object();
            source.Add("FSM/1", original, "AudioClip", "same"); source.Add("FSM/2", original, "AudioClip", "same");
            target.Add("FSM/2", cold, "AudioClip", "same"); target.Add("FSM/1", cold, "AudioClip", "same");
            source.Seal(); target.Seal();
            var key = source.Bind(original, "AudioClip", "same")!;
            Assert.AreEqual(source.ManifestHash(), target.ManifestHash());
            Assert.AreSame(cold, target.Resolve(key));
            Assert.IsNull(target.Bind(new object(), "AudioClip", "same"));
        }
        [TestMethod]
        public void EqualAssetNamesDoNotSelectTheWrongOriginalObject()
        {
            var registry = new CanonicalAssetOriginRegistry(); var first = new object(); var second = new object();
            registry.Add("death/snapshot", first, "Snapshot", "Normal");
            registry.Add("dream/snapshot", second, "Snapshot", "Normal"); registry.Seal();
            var firstKey = registry.Bind(first, "Snapshot", "Normal")!;
            var secondKey = registry.Bind(second, "Snapshot", "Normal")!;
            Assert.AreNotEqual(firstKey, secondKey);
            Assert.AreSame(first, registry.Resolve(firstKey)); Assert.AreSame(second, registry.Resolve(secondKey));
        }
        [TestMethod]
        public void SplitOrMergedAliasesCannotResolveARecordedOrigin()
        {
            var source = new CanonicalAssetOriginRegistry(); var target = new CanonicalAssetOriginRegistry();
            var shared = new object(); source.Add("a", shared, "T", "n"); source.Add("b", shared, "T", "n");
            target.Add("a", new object(), "T", "n"); target.Add("b", new object(), "T", "n");
            source.Seal(); target.Seal();
            Assert.ThrowsExactly<InvalidDataException>(() => target.Resolve(source.Bind(shared, "T", "n")!));
            Assert.AreNotEqual(source.ManifestHash(), target.ManifestHash());
        }
        [TestMethod]
        public void ChangedSlotsAndMetadataAreRejected()
        {
            var registry = new CanonicalAssetOriginRegistry(); var value = new object();
            registry.Add("a", value, "T", "n");
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add("a", new object(), "T", "n"));
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add("b", value, "T", "changed"));
            registry.Seal();
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Bind(value, "T", "changed"));
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Resolve("HeroAssetOrigin/v1/unknown"));
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add("c", value, "T", "n"));
        }
        [TestMethod]
        public void SnapshotEditsCannotChangeBindingsAndEmptyNamesAreValid()
        {
            var registry = new CanonicalAssetOriginRegistry(); var value = new object();
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Bind(value, "T", ""));
            registry.Add("a", value, "T", ""); registry.Seal();
            var key = registry.Bind(value, "T", "")!;
            registry.Snapshot()[0].Origins[0] = "changed";
            Assert.AreEqual("a", registry.Snapshot()[0].Origins[0]);
            Assert.AreSame(value, registry.Resolve(key));
        }
        [TestMethod]
        public void OriginTextAndObjectCountAreBounded()
        {
            var registry = new CanonicalAssetOriginRegistry();
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add("a\0b", new object(), "T", "n"));
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add(new string('x', 16385), new object(), "T", "n"));
            for (var index = 0; index < CanonicalAssetOriginRegistry.MaximumAssets; index++) registry.Add("s" + index, new object(), "T", "n");
            Assert.ThrowsExactly<InvalidDataException>(() => registry.Add("over", new object(), "T", "n"));
        }
    }
}
