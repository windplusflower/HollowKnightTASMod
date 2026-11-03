using System;
using System.IO;
using System.Linq;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // PDB PhysicsMaterial2D: NamedObject56 + friction4 + bounciness4
        // + PPtr<Object>4, then alignment padding. Object48 is owned by the
        // common native-object adapter. D retains both exact float bit patterns
        // and the owner reference; the ConstantString name is a local binding.
        // Box2D fixture material/contact state belongs to the physics adapter.
        internal sealed class PhysicsMaterialState
        {
            public byte[] Values = new byte[8];
            public string OwnerBinding = "";
            internal PhysicsMaterialState Copy() => new PhysicsMaterialState { Values = (byte[])Values.Clone(), OwnerBinding = OwnerBinding };
        }
        private static PhysicsMaterialState CapturePhysicsMaterial(PhysicsMaterial2D owner, Func<object, string?> bind)
        {
            LoadingEngineClockProbe.RequirePinnedEngine(); var pointer = ResidentNativeMemory.Owner(owner);
            var native = Read(pointer, 72); var instance = BitConverter.ToInt32(native, 64); var ownerBinding = "";
            if (instance != 0) {
                ownerBinding = ResidentNativeAssetReference.BindId(instance,typeof(NativeObject),bind);
            }
            if (!native.SequenceEqual(Read(pointer, 72))) throw Error("physics material changed during native capture");
            var result = new PhysicsMaterialState { Values = native.Skip(56).Take(8).ToArray(), OwnerBinding = ownerBinding };
            ValidatePhysicsMaterial(result); return result;
        }
        internal static void ValidatePhysicsMaterial(PhysicsMaterialState value)
        {
            if (value == null || value.Values == null || value.Values.Length != 8 || value.OwnerBinding == null || value.OwnerBinding.Length > 32768)
                throw Error("physics material native values or owner binding differ");
        }
        internal static byte[] EncodePhysicsMaterial(PhysicsMaterialState value)
        {
            ValidatePhysicsMaterial(value);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(value.Values); writer.Write(value.OwnerBinding); writer.Flush(); return stream.ToArray();
            }
        }
        internal static void ApplyPhysicsMaterial(Snapshot source, PhysicsMaterial2D owner, Func<string, NativeObject> resolve,
            Func<object, string?> bind, Action admitted)
        {
            admitted();
            if (source == null || source.Kind != "PhysicsMaterial2D" || source.PhysicsMaterial == null || source.Type.Resolve() != typeof(PhysicsMaterial2D)
                || !ResidentObjectDirectory.IsLive(owner) || owner.GetType() != typeof(PhysicsMaterial2D)) throw Error("physics material transfer has another exact type");
            ValidatePhysicsMaterial(source.PhysicsMaterial); var state = source.PhysicsMaterial;
            var dependency = ResidentNativeAssetReference.ResolveId(state.OwnerBinding,typeof(NativeObject),resolve);
            var pointer = ResidentNativeMemory.Owner(owner);
            owner.name = source.Name; owner.hideFlags = (HideFlags)source.HideFlags;
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 56), state.Values);
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(dependency));
            admitted();
            if (!EncodePhysicsMaterial(state).SequenceEqual(EncodePhysicsMaterial(CapturePhysicsMaterial(owner, bind))))
                throw Error("physics material independent native readback differs");
        }
    }
}
