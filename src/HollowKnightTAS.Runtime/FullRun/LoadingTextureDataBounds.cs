using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingTextureDataBounds
    {
        internal static int Length(byte[] header)
        {
            if (header == null || header.Length != 80) throw new InvalidDataException("Invalid SharedTextureData header.");
            var count = BitConverter.ToInt64(header, 32);
            var capacity = BitConverter.ToUInt64(header, 40) >> 1;
            if (count < 0 || (ulong)count > capacity || count > 64 * 1024 * 1024
                || (count != 0 && BitConverter.ToInt64(header, 16) == 0))
                throw new InvalidDataException("Texture CPU array exceeds its resident data bounds.");
            return (int)count;
        }
    }
}
