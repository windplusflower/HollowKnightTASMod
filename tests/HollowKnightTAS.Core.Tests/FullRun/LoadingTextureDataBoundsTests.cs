using System;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingTextureDataBoundsTests
    {
        [TestMethod]
        [DataRow(0L, 0UL, 0L)]
        [DataRow(32L, 64UL, 1L)]
        [DataRow(32L, 65UL, 1L)]
        [DataRow(67108864L, 134217729UL, 1L)]
        public void AcceptsResidentBoundsAndBothOwnershipBits(long count, ulong capacity, long pointer)
            => Assert.AreEqual(count, (long) LoadingTextureDataBounds.Length(Header(count, capacity, pointer)));

        [TestMethod]
        [DataRow(-1L, 65UL, 1L)]
        [DataRow(33L, 65UL, 1L)]
        [DataRow(32L, 65UL, 0L)]
        [DataRow(67108865L, 134217730UL, 1L)]
        public void RejectsMalformedOrOversizedBuffers(long count, ulong capacity, long pointer)
            => Assert.ThrowsExactly<InvalidDataException>(() => LoadingTextureDataBounds.Length(Header(count, capacity, pointer)));

        [TestMethod]
        public void RejectsTruncatedHeader()
            => Assert.ThrowsExactly<InvalidDataException>(() => LoadingTextureDataBounds.Length(new byte[79]));

        private static byte[] Header(long count, ulong capacity, long pointer)
        {
            var bytes = new byte[80];
            BitConverter.GetBytes(pointer).CopyTo(bytes, 16);
            BitConverter.GetBytes(count).CopyTo(bytes, 32);
            BitConverter.GetBytes(capacity).CopyTo(bytes, 40);
            return bytes;
        }
    }
}
