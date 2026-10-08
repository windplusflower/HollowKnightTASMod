using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingRuntimeReferencePolicyTests
    {
        private static LoadingFieldDigest Observer() => new LoadingFieldDigest(value =>
            LoadingRuntimeReferencePolicy.IsOpaqueController(value.GetType(), typeof(LoadingRuntimeReferencePolicyTests).Assembly)
                ? "TAS-controller:" + value.GetType().FullName : null, type => false);

        [TestMethod]
        public void NestedCopiedNativeValuesAndArrayElementsAffectDigest()
        {
            using var digest = Observer();
            object Snapshot(int color, int property) => new {
                style = new { normal = new { color } },
                materialProperties = new[] { new { name = "_FlashAmount", property } }
            };
            var first = digest.Capture(Snapshot(1, 10), "native", expandRoot: true);
            var changedColor = digest.Capture(Snapshot(2, 10), "native", expandRoot: true);
            var changedProperty = digest.Capture(Snapshot(1, 11), "native", expandRoot: true);
            Assert.AreNotEqual(first.Sha256, changedColor.Sha256);
            Assert.AreNotEqual(first.Sha256, changedProperty.Sha256);
            Assert.AreEqual(first.Sha256, digest.Capture(Snapshot(1, 10), "native", expandRoot: true).Sha256);
            Assert.AreEqual(0, first.Unsupported.Count);
        }

        [TestMethod]
        public void ActualRuntimeControllersRetainTheirSeparateCodecBoundary()
        {
            using var digest = Observer();
            var controller = new Controller { Value = 1 };
            var first = digest.Capture(new { controller }, "native", expandRoot: true).Sha256;
            controller.Value = 2;
            Assert.AreEqual(first, digest.Capture(new { controller }, "native", expandRoot: true).Sha256);
            Assert.IsTrue(LoadingRuntimeReferencePolicy.IsOpaqueController(typeof(Controller), typeof(Controller).Assembly));
            Assert.IsFalse(LoadingRuntimeReferencePolicy.IsOpaqueController(typeof(string), typeof(Controller).Assembly));
        }

        private sealed class Controller { public int Value; }
    }
}
