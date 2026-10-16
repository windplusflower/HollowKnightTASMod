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
        internal CanonicalSealedSource(T source, Func<T, byte[]> encode, Func<T, string> metadata)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.metadata = metadata;
            bytes = encode(source);
            frozenMetadata = metadata(source); hash = Sha256Utility.ComputeHex(bytes);
        }
        internal byte[] Bytes => bytes ?? throw new InvalidOperationException("Sealed source export was faulted.");
        internal T Require(byte[] current)
        {
            try {
                if (source == null || bytes == null || !ReferenceEquals(bytes, current)
                    || metadata(source) != frozenMetadata || Sha256Utility.ComputeHex(current) != hash)
                    throw new InvalidOperationException("Sealed source bytes, envelope metadata or identity changed during export.");
                return source;
            } catch { source = null; bytes = null; throw; }
        }
    }
}
