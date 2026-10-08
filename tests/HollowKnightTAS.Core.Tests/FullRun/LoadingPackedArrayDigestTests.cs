using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingPackedArrayDigestTests
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Pair { internal float X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Padded { internal byte A; internal int B; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceValue { internal string Text; }
        [StructLayout(LayoutKind.Explicit)]
        private struct UnionColor
        {
            [FieldOffset(0)] internal uint RGBA;
            [FieldOffset(0)] internal byte R;
            [FieldOffset(1)] internal byte G;
            [FieldOffset(2)] internal byte B;
            [FieldOffset(3)] internal byte A;
        }

        private static FieldInfo[] Fields(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static LoadingFieldDigest Digest(bool packed) => new LoadingFieldDigest(_ => null, _ => false,
            packedArrayElement: packed ? type => type == typeof(Pair) || type == typeof(Padded) || type == typeof(ReferenceValue) : null);

        [TestMethod]
        public void RawValuesRetainSignedZeroNanPayloadArrayOrderAndCapacity()
        {
            using var packed = new LoadingPackedArrayDigest(type => type == typeof(Pair), Fields, type => type.FullName!);
            var values = new[] { new Pair { X = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
                Y = BitConverter.Int32BitsToSingle(0x7fc00001) }, new Pair { X = 1, Y = 2 } };
            var layout = packed.Describe(typeof(Pair))!;
            Assert.AreEqual(8, layout.Size);
            Assert.AreEqual(3, layout.Nodes);
            var bytes = new byte[16];
            Buffer.BlockCopy(new[] { unchecked((int)0x80000000), 0x7fc00001, 0x3f800000, 0x40000000 }, 0, bytes, 0, bytes.Length);
            Assert.AreEqual(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), packed.Capture(values, layout));
            using var digest = Digest(true);
            var original = digest.Capture(values, "bits");
            Assert.AreEqual(7, original.Nodes);
            values[0].Y = BitConverter.Int32BitsToSingle(0x7fc00002);
            Assert.AreNotEqual(original.Sha256, digest.Capture(values, "bits").Sha256);
            values[0].Y = BitConverter.Int32BitsToSingle(0x7fc00001);
            values[0].X = 0;
            Assert.AreNotEqual(original.Sha256, digest.Capture(values, "bits").Sha256);
            Assert.AreNotEqual(original.Sha256, digest.Capture(values.Reverse().ToArray(), "bits").Sha256);
            Assert.AreNotEqual(original.Sha256, digest.Capture(new Pair[3], "bits").Sha256);
        }

        [TestMethod]
        public void PaddingReferencesPointersAndUnregisteredStructsUseOrdinaryTraversal()
        {
            using var layouts = new LoadingPackedArrayDigest(_ => true, Fields, type => type.FullName!);
            Assert.IsNull(layouts.Describe(typeof(Padded)));
            Assert.IsNull(layouts.Describe(typeof(ReferenceValue)));
            Assert.IsNull(layouts.Describe(typeof(IntPtr)));
            Assert.IsNull(layouts.Describe(typeof(bool)));
            using var legacy = Digest(false);
            using var compact = Digest(true);
            var values = new[] { new Padded { A = 7, B = 41 } };
            Assert.AreEqual(legacy.Capture(values, "padded").Sha256, compact.Capture(values, "padded").Sha256);
        }

        [TestMethod]
        public void RegisteredScalarUnionRetainsEveryFieldInterpretation()
        {
            using var layouts = new LoadingPackedArrayDigest(type => type == typeof(UnionColor), Fields, type => type.FullName!);
            var layout = layouts.Describe(typeof(UnionColor))!;
            Assert.AreEqual(4, layout.Size);
            Assert.AreEqual(6, layout.Nodes);
            var values = new[] { new UnionColor { RGBA = 0x78563412 } };
            var expected = SHA256.HashData(new byte[] { 0x12, 0x34, 0x56, 0x78 });
            Assert.AreEqual(Convert.ToHexString(expected).ToLowerInvariant(), layouts.Capture(values, layout));
            values[0].G = 0x33;
            Assert.AreNotEqual(Convert.ToHexString(expected).ToLowerInvariant(), layouts.Capture(values, layout));
        }

        [TestMethod]
        public void PackedArraysKeepLogicalNodeBudgetAndCrossRootAliasSemantics()
        {
            using var digest = Digest(true);
            Assert.ThrowsExactly<InvalidOperationException>(() => digest.Capture(new Pair[16667], "budget"));
            var array = new Pair[2];
            digest.BeginSharedGraph();
            try
            {
                Assert.AreEqual(7, digest.Capture(array, "first").Nodes);
                Assert.AreEqual(1, digest.Capture(array, "alias").Nodes);
                Assert.AreNotEqual(digest.Capture(array, "alias").Sha256, digest.Capture(new Pair[2], "fresh").Sha256);
            }
            finally { digest.EndSharedGraph(); }
        }
    }
}
