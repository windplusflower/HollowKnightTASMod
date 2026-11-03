using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalActualDWireTests
    {
        [TestMethod]
        public void WirePreservesMetadataAndEveryDomainInFixedLittleEndianOrder()
        {
            var metadata = new byte[] { 0, 255, 17, 128, 5 };
            var domains = Enumerable.Range(0, CanonicalActualDWire.DomainCount)
                .Select(index => Enumerable.Range(0, index * 7).Select(value => (byte)((index * 19 + value) & 255)).ToArray()).ToArray();
            var bytes = CanonicalActualDWire.Encode(metadata, domains);
            using (var reader = new BinaryReader(new MemoryStream(bytes))) {
                Assert.AreEqual(0x31444B48, reader.ReadInt32());
                Assert.AreEqual(1, reader.ReadInt32());
                Assert.AreEqual(metadata.Length, reader.ReadInt32());
                Assert.AreEqual(12, reader.ReadInt32());
                foreach (var domain in domains) Assert.AreEqual(domain.Length, reader.ReadInt32());
                Assert.AreEqual(64L, reader.BaseStream.Position);
                CollectionAssert.AreEqual(metadata, reader.ReadBytes(metadata.Length));
                foreach (var domain in domains) CollectionAssert.AreEqual(domain, reader.ReadBytes(domain.Length));
                Assert.AreEqual(reader.BaseStream.Length, reader.BaseStream.Position);
            }
            var decoded = CanonicalActualDWire.Decode(bytes);
            CollectionAssert.AreEqual(metadata, decoded.Metadata);
            Assert.AreEqual(domains.Length, decoded.Domains.Length);
            for (var index = 0; index < domains.Length; index++) CollectionAssert.AreEqual(domains[index], decoded.Domains[index]);
            CollectionAssert.AreEqual(bytes, CanonicalActualDWire.Encode(decoded.Metadata, decoded.Domains));
        }

        [TestMethod]
        public void WireAcceptsExactlyThirtyTwoMiBAndRejectsOneAdditionalByte()
        {
            var metadata = new byte[] { 123, 125 };
            var domains = Enumerable.Range(0, CanonicalActualDWire.DomainCount).Select(_ => Array.Empty<byte>()).ToArray();
            domains[11] = new byte[CanonicalActualDWire.MaximumBytes - 64 - metadata.Length];
            domains[11][0] = 17; domains[11][domains[11].Length - 1] = 231;
            var bytes = CanonicalActualDWire.Encode(metadata, domains);
            Assert.AreEqual(CanonicalActualDWire.MaximumBytes, bytes.Length);
            var decoded = CanonicalActualDWire.Decode(bytes);
            CollectionAssert.AreEqual(metadata, decoded.Metadata);
            CollectionAssert.AreEqual(domains[11], decoded.Domains[11]);
            domains[0] = new byte[1];
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Encode(metadata, domains));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(new byte[CanonicalActualDWire.MaximumBytes + 1]));
        }

        [TestMethod]
        public void WireRejectsMalformedLengthsHeadersTruncationTrailingBytesAndLegacyJson()
        {
            var metadata = new byte[] { 123, 125 };
            var domains = Enumerable.Range(0, CanonicalActualDWire.DomainCount).Select(index => new[] { (byte)index }).ToArray();
            var bytes = CanonicalActualDWire.Encode(metadata, domains);
            void RejectHeader(int offset, int value) {
                var malformed = (byte[])bytes.Clone();
                using (var writer = new BinaryWriter(new MemoryStream(malformed))) {
                    writer.BaseStream.Position = offset; writer.Write(value);
                }
                Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(malformed));
            }
            RejectHeader(0, 0); RejectHeader(4, 2); RejectHeader(8, 0); RejectHeader(8, -1);
            RejectHeader(12, 11); RejectHeader(12, 13); RejectHeader(16, -1); RejectHeader(8, int.MaxValue);
            var overflowing = (byte[])bytes.Clone();
            using (var writer = new BinaryWriter(new MemoryStream(overflowing))) {
                writer.BaseStream.Position = 16;
                for (var index = 0; index < CanonicalActualDWire.DomainCount; index++) writer.Write(int.MaxValue);
            }
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(overflowing));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(bytes.Take(63).ToArray()));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(bytes.Concat(new byte[1]).ToArray()));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Decode(Encoding.UTF8.GetBytes(
                "{\"Schema\":28,\"Metadata\":\"legacy-json\",\"Main\":\"" + new string('A', 64) + "\"}")));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Encode(Array.Empty<byte>(), domains));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Encode(metadata, domains.Take(11).ToArray()));
            domains[7] = null!;
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalActualDWire.Encode(metadata, domains));
        }
    }
}
