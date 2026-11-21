using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class ColdRestoreSupervisorIdentityTests
    {
        [TestMethod]
        public void LoadOrCreate_ReusesStableIdentityAndProtectsSecretCopy()
        {
            var root = TemporaryDirectory();
            try
            {
                var first = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                var expectedSecret = first.ClaimSecret;
                first.ClaimSecret[0] ^= 0xff;

                var second = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                Assert.AreEqual(
                    first.CompanionInstanceId,
                    second.CompanionInstanceId);
                CollectionAssert.AreEqual(
                    expectedSecret,
                    second.ClaimSecret);
                Assert.AreEqual(32, second.ClaimSecret.Length);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void LoadOrCreate_RejectsChecksumTampering()
        {
            var root = TemporaryDirectory();
            try
            {
                var initial = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                var path = Path.Combine(root, "identity-v1.txt");
                var bytes = File.ReadAllBytes(path);
                var marker = Encoding.UTF8.GetBytes("companion-");
                var index = Find(bytes, marker);
                Assert.IsGreaterThanOrEqualTo(0, index);
                bytes[index + marker.Length] =
                    bytes[index + marker.Length] == (byte)'a'
                        ? (byte)'b'
                        : (byte)'a';
                File.WriteAllBytes(path, bytes);

                var rebuilt = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                AssertRebuiltIdentity(path, initial, rebuilt);

                var reloaded = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                Assert.AreEqual(rebuilt.CompanionInstanceId, reloaded.CompanionInstanceId);
                CollectionAssert.AreEqual(rebuilt.ClaimSecret, reloaded.ClaimSecret);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [TestMethod]
        public void LoadOrCreate_RejectsNonCanonicalTrailingBytes()
        {
            var root = TemporaryDirectory();
            try
            {
                var initial = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                var path = Path.Combine(root, "identity-v1.txt");
                File.AppendAllText(
                    path,
                    "trailing\n",
                    new UTF8Encoding(false));

                var rebuilt = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                AssertRebuiltIdentity(path, initial, rebuilt);

                var reloaded = ColdRestoreSupervisorIdentity.LoadOrCreate(root);
                Assert.AreEqual(rebuilt.CompanionInstanceId, reloaded.CompanionInstanceId);
                CollectionAssert.AreEqual(rebuilt.ClaimSecret, reloaded.ClaimSecret);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void AssertRebuiltIdentity(
            string path,
            ColdRestoreSupervisorIdentity previous,
            ColdRestoreSupervisorIdentity rebuilt)
        {
            Assert.IsNotNull(rebuilt);
            Assert.IsTrue(rebuilt.CompanionInstanceId.StartsWith("companion-", StringComparison.Ordinal));
            Assert.AreEqual(32, rebuilt.ClaimSecret.Length);
            Assert.AreNotEqual(previous.CompanionInstanceId, rebuilt.CompanionInstanceId);
            Assert.IsFalse(previous.ClaimSecret.SequenceEqual(rebuilt.ClaimSecret));

            var raw = File.ReadAllText(path, new UTF8Encoding(false, true));
            var lines = raw.Split('\n');
            Assert.AreEqual(5, lines.Length);
            Assert.AreEqual("HKTAS-COLD-IDENTITY-V1", lines[0]);
            Assert.AreEqual(rebuilt.CompanionInstanceId, lines[1]);
            Assert.AreEqual(Convert.ToBase64String(rebuilt.ClaimSecret), lines[2]);
            var expectedChecksum = Sha256Utility.ComputeUtf8Hex(
                lines[0] + "\n" + lines[1] + "\n" + lines[2] + "\n");
            Assert.AreEqual(expectedChecksum, lines[3]);
            Assert.AreEqual(string.Empty, lines[4]);
        }

        private static int Find(byte[] value, byte[] marker)
        {
            for (var index = 0;
                 index <= value.Length - marker.Length;
                 index++)
            {
                if (marker.SequenceEqual(
                        value.Skip(index).Take(marker.Length)))
                {
                    return index;
                }
            }

            return -1;
        }

        private static string TemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "hktas-cold-identity-tests-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
