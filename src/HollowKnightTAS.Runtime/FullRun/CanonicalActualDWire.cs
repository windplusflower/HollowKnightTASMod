using System;
using System.Collections.Generic;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Frame the existing domain bytes directly. No domain encoding, state or
    // size allowance changes when the outer JSON/base64 representation ends.
    internal static class CanonicalActualDWire
    {
        internal const int MaximumBytes = 32 * 1024 * 1024;
        internal const int DomainCount = 12;
        private const int Magic = 0x31444B48; // HKD1, little-endian.
        private const int Version = 1;
        private const int HeaderBytes = 16 + DomainCount * 4;

        internal sealed class Decoded
        {
            internal readonly byte[] Metadata;
            internal readonly byte[][] Domains;

            internal Decoded(byte[] metadata, byte[][] domains)
            {
                Metadata = metadata;
                Domains = domains;
            }
        }

        internal static byte[] Encode(byte[] metadata, IReadOnlyList<byte[]> domains)
        {
            if (metadata == null || metadata.Length == 0)
                throw new InvalidDataException("Actual D metadata is absent.");
            if (domains == null || domains.Count != DomainCount)
                throw new InvalidDataException("Actual D domain count differs.");
            long length = checked((long)HeaderBytes + metadata.Length);
            for (var index = 0; index < DomainCount; index++) {
                var domain = domains[index];
                if (domain == null) throw new InvalidDataException("Actual D domain bytes are absent.");
                length = checked(length + domain.Length);
            }
            if (length > MaximumBytes) throw new InvalidDataException("Actual D packet exceeds its byte bound.");

            var bytes = new byte[(int)length];
            WriteInt32(bytes, 0, Magic);
            WriteInt32(bytes, 4, Version);
            WriteInt32(bytes, 8, metadata.Length);
            WriteInt32(bytes, 12, DomainCount);
            for (var index = 0; index < DomainCount; index++) WriteInt32(bytes, 16 + index * 4, domains[index].Length);
            Buffer.BlockCopy(metadata, 0, bytes, HeaderBytes, metadata.Length);
            var offset = HeaderBytes + metadata.Length;
            for (var index = 0; index < DomainCount; index++) {
                var domain = domains[index];
                Buffer.BlockCopy(domain, 0, bytes, offset, domain.Length);
                offset += domain.Length;
            }
            return bytes;
        }

        internal static Decoded Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < HeaderBytes || bytes.Length > MaximumBytes)
                throw new InvalidDataException("Actual D packet length is outside its bound.");
            if (ReadInt32(bytes, 0) != Magic || ReadInt32(bytes, 4) != Version || ReadInt32(bytes, 12) != DomainCount)
                throw new InvalidDataException("Actual D wire magic, version or domain count differs.");
            var metadataLength = ReadInt32(bytes, 8);
            if (metadataLength <= 0) throw new InvalidDataException("Actual D metadata length is invalid.");
            long length = checked((long)HeaderBytes + metadataLength);
            for (var index = 0; index < DomainCount; index++) {
                var domainLength = ReadInt32(bytes, 16 + index * 4);
                if (domainLength < 0) throw new InvalidDataException("Actual D domain length is negative.");
                length = checked(length + domainLength);
            }
            // Validate every length and the exact total before allocating any
            // segment; untrusted lengths never become allocation requests.
            if (length != bytes.Length || length > MaximumBytes)
                throw new InvalidDataException("Actual D packet is truncated, has trailing bytes or exceeds its byte bound.");

            var metadata = new byte[metadataLength];
            Buffer.BlockCopy(bytes, HeaderBytes, metadata, 0, metadataLength);
            var domains = new byte[DomainCount][];
            var offset = HeaderBytes + metadataLength;
            for (var index = 0; index < DomainCount; index++) {
                var domainLength = ReadInt32(bytes, 16 + index * 4);
                var domain = new byte[domainLength];
                Buffer.BlockCopy(bytes, offset, domain, 0, domainLength);
                domains[index] = domain;
                offset += domainLength;
            }
            return new Decoded(metadata, domains);
        }

        private static int ReadInt32(byte[] bytes, int offset)
            => bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;

        private static void WriteInt32(byte[] bytes, int offset, int value)
        {
            unchecked {
                bytes[offset] = (byte)value;
                bytes[offset + 1] = (byte)(value >> 8);
                bytes[offset + 2] = (byte)(value >> 16);
                bytes[offset + 3] = (byte)(value >> 24);
            }
        }
    }
}
