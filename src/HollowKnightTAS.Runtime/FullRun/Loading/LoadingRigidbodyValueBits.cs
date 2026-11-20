using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Offsets are for the hash-pinned Unity build only. These copies retain the
    // same non-pointer value ranges as LoadingNativePhysicsDigest, including
    // opaque bytes; they do not claim that the native layout is closed.
    internal static class LoadingRigidbodyValueBits
    {
        internal const string Policy = "pinned-owner-and-box2d-value-ranges-without-autosync-v1";
        internal const string OwnerRanges = "56:19,76:20,100:2,104:12,152:4,168:111";
        internal const string BodyRanges = "0:6,8:80,144:44";

        internal readonly struct Primary
        {
            internal readonly float X, Y, Vx, Vy;
            internal Primary(float x, float y, float vx, float vy)
            { X = x; Y = y; Vx = vx; Vy = vy; }
        }

        internal static Primary Decode(byte[] owner, byte[]? body, float transformX, float transformY)
        {
            Require(owner, 296);
            if (body == null) return new Primary(transformX, transformY, 0f, 0f);
            Require(body, 200);
            // GetVelocity's bodyType==2 branch returns zero. Position reads the
            // existing body transform before any pending Unity transform sync.
            var isStatic = BitConverter.ToInt32(owner, 0x5c) == 2;
            return new Primary(BitConverter.ToSingle(body, 0xc), BitConverter.ToSingle(body, 0x10),
                isStatic ? 0f : BitConverter.ToSingle(body, 0x40),
                isStatic ? 0f : BitConverter.ToSingle(body, 0x44));
        }

        internal static byte[] CopyOwnerValues(byte[] owner)
        {
            Require(owner, 296);
            return Copy(owner, new[] { 56, 19, 76, 20, 100, 2, 104, 12, 152, 4, 168, 111 });
        }

        internal static byte[] CopyBodyValues(byte[] body)
        {
            Require(body, 200);
            return Copy(body, new[] { 0, 6, 8, 80, 144, 44 });
        }

        private static byte[] Copy(byte[] source, int[] ranges)
        {
            var length = 0;
            for (var i = 1; i < ranges.Length; i += 2) length += ranges[i];
            var result = new byte[length];
            var destination = 0;
            for (var i = 0; i < ranges.Length; i += 2)
            {
                Buffer.BlockCopy(source, ranges[i], result, destination, ranges[i + 1]);
                destination += ranges[i + 1];
            }
            return result;
        }

        private static void Require(byte[] bytes, int length)
        {
            if (bytes == null || bytes.Length != length)
                throw new InvalidOperationException("P0 passive rigidbody value range is invalid.");
        }
    }
}
