using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class BoundaryAssetArrayRegistryTests
    {
        private sealed class Definition { public double[] Points = new double[200]; }
        private sealed class Asset { public Definition[] Definitions = null!; public Definition[] Alias = null!; }

        [TestMethod]
        public void LargeArrayIsReadWithoutRaisingSingleRootBudgets()
        {
            var definitions = Enumerable.Range(0, 1000).Select(_ => new Definition()).ToArray();
            var asset = new Asset { Definitions = definitions, Alias = definitions };
            using var whole = new LoadingFieldDigest(_ => null, _ => false);
            Assert.ThrowsExactly<InvalidOperationException>(() => whole.Capture(asset, "asset", true));
            var registry = new BoundaryAssetArrayRegistry(); registry.Register(definitions, "definitions");
            using var split = new LoadingFieldDigest(registry.Reference, _ => false);
            Assert.AreEqual(0, split.Capture(asset, "asset", true).Unsupported.Count);
            for (var index = 0; index < definitions.Length; index++)
                Assert.AreEqual(0, split.Capture(definitions[index], "definitions[" + index + "]", true).Unsupported.Count);
            var before = split.Capture(definitions[999], "definitions[999]", true).Sha256;
            definitions[999].Points[199] = BitConverter.Int64BitsToDouble(long.MinValue); // -0 differs by its bit.
            Assert.AreNotEqual(before, split.Capture(definitions[999], "definitions[999]", true).Sha256);
        }

        [TestMethod]
        public void SharedArrayAndSeparateEqualArrayHaveDifferentLinks()
        {
            var one = new Definition[0]; var two = new Definition[0];
            var registry = new BoundaryAssetArrayRegistry(); registry.Register(one, "one"); registry.Register(two, "two");
            registry.Register(one, "another-name");
            Assert.AreEqual(2, registry.Entries.Count);
            using var fields = new LoadingFieldDigest(registry.Reference, _ => false);
            Assert.AreNotEqual(fields.Capture(new Asset { Definitions = one, Alias = one }, "asset", true).Sha256,
                fields.Capture(new Asset { Definitions = one, Alias = two }, "asset", true).Sha256);
        }

        [TestMethod]
        public void CollisionNonzeroBoundsAndBudgetAreRejected()
        {
            var registry = new BoundaryAssetArrayRegistry(); registry.Register(new int[0], "same");
            Assert.ThrowsExactly<InvalidOperationException>(() => registry.Register(new int[0], "same"));
            Assert.ThrowsExactly<InvalidOperationException>(() => registry.Register(Array.CreateInstance(typeof(int), new[] { 1 }, new[] { 1 }), "shifted"));
            Assert.ThrowsExactly<InvalidOperationException>(() => registry.Register(new byte[200001], "too-many"));
        }

        [TestMethod]
        public void SharedElementAndEqualCloneDifferAcrossRootReads()
        {
            var one = new Definition(); var clone = new Definition();
            using var shared = new LoadingFieldDigest(_ => null, _ => false);
            shared.BeginSharedGraph();
            shared.Capture(one, "element[0]");
            var alias = shared.Capture(one, "element[1]").Sha256;
            var distinct = shared.Capture(clone, "element[1]").Sha256;
            shared.EndSharedGraph();
            Assert.AreNotEqual(alias, distinct);
        }
    }
}
