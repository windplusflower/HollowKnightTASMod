using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativePhysicsDigest
    {
        // Read-only evidence beside the unchanged strict groups. No raw pointer
        // identity is exported and no inactive slot is omitted or normalized.
        internal sealed class ContactEvidence
        {
            public string Binding = "", FlagsHex = "", CurrentManifoldHex = "", OldManifoldHex = "", ToiHex = "", ControlHex = "";
            public uint Flags;
            public int PointCount, OldPointCount;
            public bool Touching, Enabled, ToiCacheFlag, SensorA, SensorB, UserDataPresent;
        }
        internal static ContactEvidence CaptureContactRaw(string binding, byte[] contact, byte[] fixtureA, byte[] fixtureB)
        {
            if (string.IsNullOrEmpty(binding) || binding.Length > 16384 || contact == null || contact.Length != 312
                || fixtureA == null || fixtureA.Length != 88 || fixtureB == null || fixtureB.Length != 88)
                throw new InvalidDataException("Contact raw evidence ABI/binding differs.");
            var count = BitConverter.ToInt32(contact, 188);
            if (count < 0 || count > 2) throw new InvalidDataException("Contact current point count differs.");
            var flags = BitConverter.ToUInt32(contact, 8);
            return new ContactEvidence {
                Binding = binding, Flags = flags, FlagsHex = Hex(contact, 8, 4), PointCount = count,
                OldPointCount = BitConverter.ToInt32(contact, 260), Touching = (flags & 2) != 0, Enabled = (flags & 4) != 0,
                ToiCacheFlag = (flags & 32) != 0, SensorA = fixtureA[74] != 0, SensorB = fixtureB[74] != 0,
                CurrentManifoldHex = Hex(contact, 128, 72), OldManifoldHex = Hex(contact, 200, 72),
                ToiHex = Hex(contact, 276, 4), ControlHex = Hex(contact, 272, 28),
                UserDataPresent = BitConverter.ToInt64(contact, 304) != 0
            };
        }
    }
}
