using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Rng;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Rng
{
    public enum UnityRandomCodecResolutionStatus : byte
    {
        Ready = 1,
        AssemblyMismatch = 2,
        ModuleMismatch = 3,
        FieldLayoutMismatch = 4
    }

    public sealed class UnityRandomCodecResolution
    {
        internal UnityRandomCodecResolution(
            UnityRandomCodecResolutionStatus status,
            string detail,
            RngAssemblyIdentity actualAssembly,
            UnityRandomStateCodec_1_5_78_11833? codec)
        {
            Status = status;
            Detail = detail ?? string.Empty;
            ActualAssembly = actualAssembly
                             ?? throw new ArgumentNullException(
                                 nameof(actualAssembly));
            Codec = codec;
        }

        public UnityRandomCodecResolutionStatus Status { get; }
        public string Detail { get; }
        public RngAssemblyIdentity ActualAssembly { get; }
        public UnityRandomStateCodec_1_5_78_11833? Codec { get; }
        public bool Ready =>
            Status == UnityRandomCodecResolutionStatus.Ready
            && Codec != null;
    }

    public sealed class UnityRandomCodecRoundTripReport
    {
        internal UnityRandomCodecRoundTripReport(
            bool stableOneHundredEncodes,
            bool seedChangedHash,
            bool restoredExactly,
            RngStateFingerprint before,
            RngStateFingerprint seeded,
            RngStateFingerprint restored)
        {
            StableOneHundredEncodes = stableOneHundredEncodes;
            SeedChangedHash = seedChangedHash;
            RestoredExactly = restoredExactly;
            Before = before;
            Seeded = seeded;
            Restored = restored;
        }

        public bool StableOneHundredEncodes { get; }
        public bool SeedChangedHash { get; }
        public bool RestoredExactly { get; }
        public RngStateFingerprint Before { get; }
        public RngStateFingerprint Seeded { get; }
        public RngStateFingerprint Restored { get; }
        public bool Success =>
            StableOneHundredEncodes
            && SeedChangedHash
            && RestoredExactly;
    }

    public sealed class UnityRandomStateCodec_1_5_78_11833 :
        IUnityRandomStateCodec
    {
        public const string Identifier =
            "unity-random-state-1.5.78.11833-s0-s3-be-v1";
        public const string ExpectedAssemblySha256 =
            "0f0bec6f864da12ea7fe2d068af73c04d8c4dc267b21490e5c3ca1e730e59b14";
        public const string ExpectedModuleVersionId =
            "047294ce-45cf-4b9a-9441-e48d2b8435e4";

        private static readonly string[] ExpectedFieldNames =
        {
            "s0",
            "s1",
            "s2",
            "s3"
        };

        private readonly FieldInfo[] fields;

        private UnityRandomStateCodec_1_5_78_11833(FieldInfo[] fields)
        {
            this.fields = fields;
        }

        private static bool assemblyIdentityWarningLogged;

        public string CodecId => Identifier;

        public static UnityRandomCodecResolution Resolve()
        {
            var assembly = typeof(UnityEngine.Random).Assembly;
            var location = assembly.Location;
            string actualSha256;
            string assemblyName;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                assemblyName = Path.GetFileName(location);
                actualSha256 = Sha256Utility.ComputeFileHex(location);
            }
            else
            {
                assemblyName = assembly.GetName().Name ?? "UnityEngine.CoreModule.dll";
                actualSha256 = new string('0', 64);
                if (!assemblyIdentityWarningLogged)
                {
                    Modding.Logger.LogWarn("[HKTAS] UnityEngine.CoreModule.dll path is unavailable; continuing with reflection-based Random.State codec.");
                }
            }

            var identity = new RngAssemblyIdentity(
                assemblyName,
                actualSha256,
                assembly.ManifestModule.ModuleVersionId.ToString("D"));
            var shaMatch = string.Equals(
                identity.Sha256,
                ExpectedAssemblySha256,
                StringComparison.Ordinal);
            var mvidMatch = string.Equals(
                identity.ModuleVersionId,
                ExpectedModuleVersionId,
                StringComparison.Ordinal);
            if ((!shaMatch || !mvidMatch) && !assemblyIdentityWarningLogged)
            {
                assemblyIdentityWarningLogged = true;
                Modding.Logger.LogWarn("[HKTAS] UnityEngine.CoreModule identity differs from baseline (shaMatch="
                    + shaMatch + ", mvidMatch=" + mvidMatch + "); using reflection-based Random.State s0..s3 codec.");
            }

            var stateType = typeof(UnityEngine.Random.State);
            var resolvedFields = new FieldInfo[ExpectedFieldNames.Length];
            for (var index = 0; index < ExpectedFieldNames.Length; index++)
            {
                var field = stateType.GetField(
                    ExpectedFieldNames[index],
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic);
                if (field == null || field.FieldType != typeof(int) || field.IsStatic)
                {
                    Modding.Logger.LogWarn("[HKTAS] UnityEngine.Random.State missing expected int field '"
                        + ExpectedFieldNames[index] + "'.");
                    return Failure(
                        UnityRandomCodecResolutionStatus.FieldLayoutMismatch,
                        "Random.State field '" + ExpectedFieldNames[index] + "' is missing or has unexpected type.",
                        identity);
                }
                resolvedFields[index] = field;
            }

            return new UnityRandomCodecResolution(
                UnityRandomCodecResolutionStatus.Ready,
                "Random.State s0..s3 fields resolved via reflection.",
                identity,
                new UnityRandomStateCodec_1_5_78_11833(resolvedFields));
        }

        public byte[] Encode(UnityEngine.Random.State state)
        {
            object boxed = state;
            var bytes = new byte[16];
            for (var index = 0; index < fields.Length; index++)
            {
                var value = (int)fields[index].GetValue(boxed);
                WriteInt32BigEndian(bytes, checked(index * 4), value);
            }

            return bytes;
        }

        public RngStateFingerprint Fingerprint(
            UnityEngine.Random.State state)
        {
            return RngStateFingerprint.FromBytes(
                CodecId,
                Encode(state));
        }

        public RngStateFingerprint CaptureCurrent()
        {
            return Fingerprint(UnityEngine.Random.state);
        }

        public UnityRandomCodecRoundTripReport VerifyRoundTrip(int seed)
        {
            var original = UnityEngine.Random.state;
            var before = Fingerprint(original);
            var canonical = Encode(original);
            var stable = true;
            for (var index = 0; index < 100; index++)
            {
                if (!BytesEqual(canonical, Encode(original)))
                {
                    stable = false;
                    break;
                }
            }

            RngStateFingerprint seeded;
            RngStateFingerprint restored;
            try
            {
                UnityEngine.Random.InitState(seed);
                seeded = CaptureCurrent();
            }
            finally
            {
                UnityEngine.Random.state = original;
                restored = CaptureCurrent();
            }

            return new UnityRandomCodecRoundTripReport(
                stable,
                !before.Equals(seeded),
                before.Equals(restored),
                before,
                seeded,
                restored);
        }

        private static UnityRandomCodecResolution Failure(
            UnityRandomCodecResolutionStatus status,
            string detail,
            RngAssemblyIdentity identity)
        {
            return new UnityRandomCodecResolution(
                status,
                detail,
                identity,
                null);
        }

        private static void WriteInt32BigEndian(
            byte[] bytes,
            int offset,
            int value)
        {
            unchecked
            {
                bytes[offset] = (byte)((uint)value >> 24);
                bytes[offset + 1] = (byte)((uint)value >> 16);
                bytes[offset + 2] = (byte)((uint)value >> 8);
                bytes[offset + 3] = (byte)value;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
