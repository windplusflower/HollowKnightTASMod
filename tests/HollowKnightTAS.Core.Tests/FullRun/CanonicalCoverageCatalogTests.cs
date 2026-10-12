using System;
using System.IO;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalCoverageCatalogTests
    {
        [TestMethod]
        public void SimilarRootNamesNeverInferCarryOrCanonicalOwnership()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.RegisterObject("destroyed:Menu[0]", "MenuButton", "destroyed-wrapper", -17);
            catalog.Record("destroyed-unity-wrapper-rebind-codec:MenuButton", "Knight[0]/looks-like-D", ".button", "destroyed:Menu[0]");
            var snapshot = catalog.Capture(new[] { "destroyed-unity-wrapper-rebind-codec:MenuButton" });
            Assert.AreEqual(CanonicalCoverageCatalog.Domain.Unclassified, snapshot.Records.Single().Owner);
            Assert.IsFalse(snapshot.CompleteStateCoverage);
            Assert.IsFalse(snapshot.RestoreQualified);
            Assert.IsTrue(snapshot.DiagnosticOnly);
        }

        [TestMethod]
        public void ExactDeclarationsAndInheritedFsmSlotsRetainTheirEvidence()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.BindRoot("fsm", CanonicalCoverageCatalog.Domain.D, "frozen-fsm-registry");
            catalog.InheritRoot("fsm/state[0]/action[1]", "fsm");
            catalog.BindRoot("visual", CanonicalCoverageCatalog.Domain.S0, "original-visual-reset");
            catalog.Record("asset", "fsm/state[0]/action[1]", ".audio");
            catalog.Record("native", "visual", ".controller");
            var snapshot = catalog.Capture(new[] { "asset", "native" });
            var carry = snapshot.Records.Single(value => snapshot.Names[value.Root] == "fsm/state[0]/action[1]");
            Assert.AreEqual(CanonicalCoverageCatalog.Domain.D, carry.Owner);
            Assert.AreEqual("frozen-fsm-registry", snapshot.Names[carry.Evidence]);
            Assert.AreEqual(CanonicalCoverageCatalog.Domain.S0, snapshot.Records.Single(value => snapshot.Names[value.Root] == "visual").Owner);
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.BindRoot("fsm", CanonicalCoverageCatalog.Domain.S0, "other"));
        }

        [TestMethod]
        public void SharedDestroyedWrapperKeepsAllOwnerFieldsAndDeduplicatesIdenticalVisits()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.RegisterObject("destroyed:Menu[0]", "MenuButton", "destroyed-wrapper", -17);
            catalog.Record("destroyed", "root1", ".button", "destroyed:Menu[0]");
            catalog.Record("destroyed", "root1", ".button", "destroyed:Menu[0]");
            catalog.Record("destroyed", "root2", ".selection[3]", "destroyed:Menu[0]");
            var snapshot = catalog.Capture(new[] { "destroyed" });
            Assert.AreEqual(2, snapshot.Records.Length);
            Assert.AreEqual(1, snapshot.Objects.Length);
            Assert.IsTrue(snapshot.Records.All(value => value.Object == 0));
            Assert.AreEqual(-17, snapshot.Objects[0].RawInstanceId);
        }

        [TestMethod]
        public void ExactSourceOriginAndMissingContextSurviveLosslessNameInterning()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.RegisterObject("asset:Texture[0]", "Texture2D", "asset-reference", 34, "serialized", "resources.assets", 981);
            catalog.Record("texture-unreadable", "hero/state[2]", ".sprite.texture", "asset:Texture[0]");
            var snapshot = catalog.Capture(new[] { "texture-unreadable", "prior-gap" });
            var source = snapshot.Objects.Single();
            Assert.AreEqual("resources.assets", snapshot.Names[source.SourcePath]);
            Assert.AreEqual("serialized", snapshot.Names[source.SourceKind]);
            Assert.AreEqual(981L, source.LocalIdentifier);
            var missing = snapshot.Records.Single(value => snapshot.Names[value.Label] == "prior-gap");
            Assert.AreEqual(-1, missing.Object);
            Assert.AreEqual("<unattributed-observer-gap>", snapshot.Names[missing.Root]);
            Assert.AreEqual(CanonicalCoverageCatalog.Domain.Unclassified, missing.Owner);
        }

        [TestMethod]
        public void ReaderHashIsOrderIndependentButDetectsIdentityOriginAndFieldChanges()
        {
            CanonicalCoverageCatalog.Snapshot Make(bool reverse, int instance = 8, string path = "file", string field = ".x")
            {
                var catalog = new CanonicalCoverageCatalog();
                catalog.RegisterObject("a", "T", "asset-reference", instance, "serialized", path, 7);
                foreach (var root in reverse ? new[] { "r2", "r1" } : new[] { "r1", "r2" }) catalog.Record("gap", root, field, "a");
                return catalog.Capture(new[] { "gap" });
            }
            Assert.AreEqual(Make(false).ReadSha256, Make(true).ReadSha256);
            Assert.AreNotEqual(Make(false).ReadSha256, Make(false, instance: 9).ReadSha256);
            Assert.AreNotEqual(Make(false).ReadSha256, Make(false, path: "changed").ReadSha256);
            Assert.AreNotEqual(Make(false).ReadSha256, Make(false, field: ".y").ReadSha256);
        }

        [TestMethod]
        public void ReturnedSnapshotCopiesCannotMutateCatalogOrFrozenReadAudit()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.RegisterObject("a", "T", "destroyed-wrapper", 4);
            catalog.Record("gap", "r", ".x", "a");
            var original = catalog.Capture(new[] { "gap" });
            var copy = original.Copy(); copy.Names[0] = "modified"; copy.Objects[0].RawInstanceId = 999; copy.Records[0].Object = -1;
            Assert.AreEqual(4, original.Objects[0].RawInstanceId);
            Assert.AreEqual(0, original.Records[0].Object);
            Assert.AreEqual(original.ReadSha256, catalog.Capture(new[] { "gap" }).ReadSha256);
        }

        [TestMethod]
        public void ConflictingObjectMetadataAndUnboundOccurrenceAreRejected()
        {
            var catalog = new CanonicalCoverageCatalog();
            catalog.RegisterObject("a", "T", "asset-reference", 7);
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.RegisterObject("a", "Other", "asset-reference", 7));
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.RegisterObject("a", "T", "asset-reference", 8));
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.Record("gap", "r", ".x", "missing"));
        }

        [TestMethod]
        public void BudgetsFailClosedAndNewSampleDoesNotKeepDeadOwners()
        {
            var catalog = new CanonicalCoverageCatalog(2);
            catalog.Record("gap", "r", ".x"); catalog.Record("gap", "r", ".y");
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.Record("gap", "r", ".z"));
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.Record("gap", "r", "x\0y"));
            Assert.ThrowsExactly<InvalidDataException>(() => catalog.Record("gap", new string('x', CanonicalCoverageCatalog.MaximumText + 1), ".x"));
            catalog.BeginSample(); catalog.Record("new-gap", "new-owner", ".new");
            var snapshot = catalog.Capture(new[] { "new-gap" });
            Assert.AreEqual(1, snapshot.Records.Length);
            Assert.IsFalse(snapshot.Names.Contains("r"));
        }
    }
}
