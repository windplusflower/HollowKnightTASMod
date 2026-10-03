using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingUnityAssetIdentityTests
    {
        [TestMethod]
        public void SameNameDifferentAssetsRemainDistinctButAliasBindingsStayEqual()
        {
            var identities = new LoadingUnityAssetIdentity();
            var first = identities.Bind(100, "asset", "Texture", "same");
            Assert.AreEqual(first, identities.Bind(100, "asset", "Texture", "same"));
            Assert.AreNotEqual(first, identities.Bind(200, "asset", "Texture", "same"));
            Assert.AreEqual(2, identities.Snapshot().Length);
            var otherProcess = new LoadingUnityAssetIdentity();
            Assert.AreEqual(first, otherProcess.Bind(-780, "asset", "Texture", "same"));
            Assert.AreEqual(identities.Bind(200, "asset", "Texture", "same"),
                otherProcess.Bind(-910, "asset", "Texture", "same"));
        }

        [TestMethod]
        public void DestroyedObjectsRetainDistinctAliasesAndSamplesResetTheirOrdinal()
        {
            var identities = new LoadingUnityAssetIdentity();
            var first = identities.Bind(100, "destroyed", "GameObject", "");
            Assert.AreEqual(first, identities.Bind(100, "destroyed", "GameObject", ""));
            Assert.AreNotEqual(first, identities.Bind(200, "destroyed", "GameObject", ""));
            identities.Clear();
            Assert.AreEqual(first, identities.Bind(300, "destroyed", "GameObject", ""));
        }

        [TestMethod]
        public void ZeroInstanceWrappersUseManagedIdentityInsteadOfCollapsingAllNulls()
        {
            var identities = new LoadingUnityAssetIdentity();
            var first = identities.BindFakeNull(10, "GameObject");
            Assert.AreEqual(first, identities.BindFakeNull(10, "GameObject"));
            Assert.AreNotEqual(first, identities.BindFakeNull(20, "GameObject"));
            Assert.AreNotEqual(first, identities.BindFakeNull(30, "SceneManager"));
            Assert.AreEqual(3, identities.Snapshot().Length);
            Assert.ThrowsExactly<System.InvalidOperationException>(() => identities.Bind(0, "destroyed", "GameObject", ""));
        }
    }
}
