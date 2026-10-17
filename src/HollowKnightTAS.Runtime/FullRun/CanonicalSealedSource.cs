using System;
using HollowKnightTAS.Core.Cryptography;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The encoder creates the bytes from this exact admitted envelope once.
    // Export reads frozen metadata and bytes; it does not reparse a large packet.
    internal sealed class CanonicalSealedSource<T> where T : class
    {
        private T? source;
        private byte[]? bytes;
        private readonly Func<T, string> metadata;
        private readonly string frozenMetadata, hash;
        private readonly string[] chunkHashes;
        private readonly int chunkBytes;
        private int nextOffset;
        internal CanonicalSealedSource(T source, Func<T, byte[]> encode, Func<T, string> metadata, int chunkBytes = 262144)
        {
            if (chunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(chunkBytes));
            this.chunkBytes = chunkBytes;
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.metadata = metadata;
            bytes = encode(source);
            frozenMetadata = metadata(source); hash = Sha256Utility.ComputeHex(bytes);
            chunkHashes = new string[(bytes.Length + chunkBytes - 1) / chunkBytes];
            for (var offset = 0; offset < bytes.Length; offset += chunkBytes)
                chunkHashes[offset / chunkBytes] = Sha256Utility.ComputeHex(CopyChunk(bytes, offset));
            // Publish only after both hash directories describe the exact same
            // encoded version. No chunk/packet reference escapes the seal here.
            Require(bytes);
        }
        internal byte[] Bytes => bytes ?? throw new InvalidOperationException("Sealed source export was faulted.");
        internal T Require(byte[] current)
        {
            try {
                RequireIdentity(current);
                if (Sha256Utility.ComputeHex(current) != hash)
                    throw new InvalidOperationException("Sealed source bytes, envelope metadata or identity changed during export.");
                return source!;
            } catch { Fault(); throw; }
        }
        private void RequireIdentity(byte[] current)
        {
            if (source == null || bytes == null || !ReferenceEquals(bytes, current) || metadata(source) != frozenMetadata)
                throw new InvalidOperationException("Sealed source envelope metadata or identity changed during export.");
        }
        private byte[] CopyChunk(byte[] current, int offset)
        {
            var result = new byte[Math.Min(chunkBytes, current.Length - offset)];
            Buffer.BlockCopy(current, offset, result, 0, result.Length); return result;
        }
        internal T ReadChunk(byte[] current, int offset, out byte[] chunk)
        {
            try {
                RequireIdentity(current);
                if (offset != nextOffset || offset < 0 || offset >= current.Length || offset % chunkBytes != 0)
                    throw new InvalidOperationException("Sealed source chunks must be read once in exact order.");
                // Hash and send this same private copy. Concurrent alteration of
                // the exposed original cannot change bytes after this check.
                chunk = CopyChunk(current, offset);
                if (Sha256Utility.ComputeHex(chunk) != chunkHashes[offset / chunkBytes])
                    throw new InvalidOperationException("Sealed source chunk changed during export.");
                RequireIdentity(current); nextOffset += chunk.Length; return source!;
            } catch { Fault(); throw; }
        }
        internal T RequireCompleted(byte[] current)
        {
            try {
                var result = Require(current);
                if (nextOffset != current.Length) throw new InvalidOperationException("Sealed source chunk export is incomplete.");
                return result;
            } catch { Fault(); throw; }
        }
        internal void Fault()
        {
            if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            source = null; bytes = null;
        }
    }
}
