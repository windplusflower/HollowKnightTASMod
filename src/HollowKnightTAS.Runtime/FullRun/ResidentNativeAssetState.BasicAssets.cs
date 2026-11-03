using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        internal static void ValidateScriptableAsset(Snapshot source)
        {
            var data = source.Scriptable ?? throw Error("ScriptableObject native payload is absent"); ResidentMonoNativeState.Validate(data);
            var actual = source.Type.Resolve();
            if (!typeof(ScriptableObject).IsAssignableFrom(actual) || actual.IsAbstract || actual.ContainsGenericParameters || !data.Scriptable
                || data.Owner != source.Binding || data.Type != new ResidentGraphPolicy(_ => null).BindMetadata(actual))
                throw Error("ScriptableObject native payload has another managed type or owner binding");
        }
        internal static ScriptableObject CreateScriptableAsset(Type actualType, ResidentMutationScope scope)
        {
            scope.Require();
            var method = typeof(ScriptableObject).GetMethod("CreateScriptableObjectInstanceFromType", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw Error("native ScriptableObject creation API is absent");
            var create = (Func<Type, bool, ScriptableObject>)Delegate.CreateDelegate(typeof(Func<Type, bool, ScriptableObject>), method);
            using (scope.ScriptableConstruction(actualType)) {
                // False skips native ResetAndApplyDefaultInstances. The exact
                // type's managed constructor and messages are scoped as well;
                // all ordinary fields arrive later through the unified graph.
                var result = create(actualType, false);
                if (!ResidentObjectDirectory.IsLive(result) || result.GetType() != actualType) throw Error("native ScriptableObject construction returned another type");
                scope.Track(result); return result;
            }
        }
        // TextAsset96 = NamedObject56 + raw coreString40. Its bytes are not
        // necessarily text: embedded null and invalid UTF8 remain exact D.
        // Common S0 reconstructs only local coreString allocation/storage.
        // NamedObject's name uses the engine string registry; Object48 is in
        // the shared native base adapter, never duplicated here.
        internal sealed class TextAssetState
        {
            public byte[] Bytes = Array.Empty<byte>();
            internal TextAssetState Copy() => new TextAssetState { Bytes = (byte[])Bytes.Clone() };
        }
        private static TextAssetState CaptureTextAsset(TextAsset owner)
        {
            RequireAssetVtable(owner, 0x1696fa8);
            return new TextAssetState { Bytes = ReadRawString(IntPtr.Add(ResidentNativeMemory.Owner(owner), 56)) };
        }
        internal static void ValidateTextAsset(TextAssetState state)
        {
            if (state == null || state.Bytes == null) throw Error("TextAsset byte directory is absent");
            RequireBytes(state.Bytes.Length);
        }
        internal static void ApplyTextAsset(Snapshot source, TextAsset owner, Action admitted)
        {
            admitted();
            if (source == null || source.Kind != "TextAsset" || source.Text == null || source.Type.Resolve() != typeof(TextAsset)
                || !ResidentObjectDirectory.IsLive(owner) || owner.GetType() != typeof(TextAsset)) throw Error("TextAsset transfer has another exact type");
            ValidateTextAsset(source.Text); RequireAssetVtable(owner, 0x1696fa8);
            owner.name = source.Name; owner.hideFlags = (HideFlags)source.HideFlags;
            WriteRawString(IntPtr.Add(ResidentNativeMemory.Owner(owner), 56), source.Text.Bytes, admitted);
            if (!source.Text.Bytes.SequenceEqual(CaptureTextAsset(owner).Bytes)) throw Error("TextAsset byte readback differs");
        }
        private static void RequireAssetVtable(NativeObject owner, int expected)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var native = ResidentNativeMemory.Owner(owner);
            if (ResidentNativeMemory.Ptr(native).ToInt64() - engine.BaseAddress.ToInt64() != expected)
                throw Error("asset native type differs from its typed layout");
        }
        private static byte[] ReadRawString(IntPtr storage)
        {
            var header = Read(storage, 40); var size = BitConverter.ToUInt64(header, 24);
            if (size > MaximumBytes) throw Error("raw native string exceeds its byte bound");
            var pointer = Pointer(header, 0);
            if (pointer == IntPtr.Zero) {
                if (size >= 16) throw Error("raw native inline string exceeds storage");
                pointer = IntPtr.Add(storage, 8);
            } else if (BitConverter.ToUInt64(header, 8) < size) throw Error("raw native string capacity differs");
            var bytes = Read(pointer, (int)size);
            if (Read(IntPtr.Add(pointer, (int)size), 1)[0] != 0 || !header.SequenceEqual(Read(storage, 40)) || !bytes.SequenceEqual(Read(pointer, (int)size)))
                throw Error("raw native string terminator or capture stability differs");
            return bytes;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr RawStringAssignment(IntPtr owner, IntPtr first, IntPtr last);
        private static void WriteRawString(IntPtr storage, byte[] bytes, Action admitted)
        {
            if (bytes == null || admitted == null) throw Error("raw native string lacks its source or admission");
            RequireBytes(bytes.Length); admitted(); _ = ReadRawString(storage);
            var address = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0xbe6d0);
            using (var hash = SHA256.Create()) {
                if (BitConverter.ToString(hash.ComputeHash(Read(address, 206))).Replace("-", "").ToLowerInvariant()
                    != "567e65c59d2c2f724589d70858acfc5ed729dd627f1d3e1a164163e5b02e9055") throw Error("raw native string assignment ABI differs");
            }
            var buffer = Marshal.AllocHGlobal(bytes.Length + 1);
            try {
                Marshal.Copy(bytes, 0, buffer, bytes.Length); Marshal.WriteByte(buffer, bytes.Length, 0);
                ((RawStringAssignment)Marshal.GetDelegateForFunctionPointer(address, typeof(RawStringAssignment)))(storage, buffer, IntPtr.Add(buffer, bytes.Length));
                if (!bytes.SequenceEqual(ReadRawString(storage))) throw Error("raw native string write did not read back");
                admitted();
            } finally { Marshal.FreeHGlobal(buffer); }
        }
    }
}
