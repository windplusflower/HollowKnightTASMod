using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned b2PolygonShape: only storage outside m_count is canonicalized.
    // Preserve the vtable/type/radius/centroid, active geometry, count and padding.
    internal static class CanonicalPolygonCapacity
    {
        internal static byte[] Prepare(byte[] source)
        {
            if (source == null || source.Length != 160 || BitConverter.ToInt32(source, 8) != 2)
                throw new InvalidDataException("Pinned polygon shape layout/type differs.");
            var count = BitConverter.ToInt32(source, 152);
            if (count < 3 || count > 8) throw new InvalidDataException("Active polygon vertex count differs.");
            foreach (var start in new[] { 24, 88 })
                for (var offset = start; offset < start + count * 8; offset += 4)
                {
                    var value = BitConverter.ToSingle(source, offset);
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Active polygon geometry is not finite.");
                }
            var expected = (byte[])source.Clone();
            Array.Clear(expected, 24 + count * 8, (8 - count) * 8);
            Array.Clear(expected, 88 + count * 8, (8 - count) * 8);
            return expected;
        }
    }
}
