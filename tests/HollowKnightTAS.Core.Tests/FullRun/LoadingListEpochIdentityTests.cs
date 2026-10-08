using System;
using System.Collections.Generic;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingListEpochIdentityTests
    {
        private static readonly FieldInfo Version = typeof(List<int>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private static string Capture(int origin, int mutations, bool changedValue = false)
        {
            var list = new List<int> { 4, changedValue ? 6 : 5 };
            Version.SetValue(list, origin);
            var iterator = list.GetEnumerator();
            for (var i = 0; i < mutations; i++) list[0] = 4;
            var identity = new LoadingListEpochIdentity<int>(list);
            using var digest = new LoadingFieldDigest(value => null, type => false, identity.Field);
            return digest.Capture(new { list, iterator }, "root").Sha256;
        }

        [TestMethod]
        public void ValidAndInvalidEnumeratorsPreserveTheirRelationAcrossDifferentOrigins()
        {
            Assert.AreEqual(Capture(10, 0), Capture(17, 0));
            Assert.AreEqual(Capture(10, 2), Capture(17, 2));
            Assert.AreNotEqual(Capture(10, 0), Capture(10, 1));
            Assert.AreNotEqual(Capture(10, 1), Capture(10, 2));
            Assert.AreNotEqual(Capture(10, 0), Capture(10, 0, true));
        }

        [TestMethod]
        public void SignedVersionWrapAndRealEnumeratorFailureRemainEquivalent()
        {
            Assert.AreEqual(Capture(int.MaxValue, 1), Capture(10, 1));
            var list = new List<int> { 4, 5 };
            Version.SetValue(list, int.MaxValue);
            var iterator = list.GetEnumerator();
            list[0] = 4;
            Assert.ThrowsExactly<InvalidOperationException>(() => iterator.MoveNext());
            var identity = new LoadingListEpochIdentity<int>(list);
            using var digest = new LoadingFieldDigest(value => null, type => false, identity.Field);
            var before = (int)Version.GetValue(list)!;
            digest.Capture(new { list, iterator }, "root");
            Assert.AreEqual(before, Version.GetValue(list));
        }

        [TestMethod]
        public void OtherListsAreNotReboundByTheRegisteredIdentity()
        {
            var registered = new List<int>();
            var other = new List<int>();
            var identity = new LoadingListEpochIdentity<int>(registered);
            using var digest = new LoadingFieldDigest(value => null, type => false, identity.Field);
            var before = digest.Capture(other, "root").Sha256;
            other.Clear();
            Assert.AreNotEqual(before, digest.Capture(other, "root").Sha256);
        }
    }
}
