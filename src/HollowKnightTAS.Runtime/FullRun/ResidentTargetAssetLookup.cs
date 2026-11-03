using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Target-only. Read the already registered resource identity first; only
    // that proven Object* may acquire a wrapper, inside the restore scope.
    // No Resources enumeration, path loading or bystander wrapper allocation.
    internal static class ResidentTargetAssetLookup
    {
        private const int Maximum = 1000000;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool TryLock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Unlock(IntPtr mutex);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Wrapper(out IntPtr result, IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint NewHandle(IntPtr value, int pinned);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeHandle(uint value);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        internal static NativeObject Serialized(ResidentAssetBindings.AssetRecord record, ResidentMutationScope scope)
        {
            scope.Require();
            if (record == null || record.Kind != "serialized") throw Error("serialized lookup lacks exact source identity");
            var id = FindRegisteredSourceId(record.SourcePath, record.LocalIdentifier);
            var pointer = ResidentLoadedAssetIndex.RegisteredPointer(id, out var registered);
            RequireOrigin(id, record.SourcePath, record.LocalIdentifier);
            var owner = BindRegistered(id, pointer, record.Type.Resolve(), scope);
            registered(); RequireOrigin(id, record.SourcePath, record.LocalIdentifier); scope.Require(); return owner;
        }

        internal static NativeObject BindRegistered(int id, IntPtr pointer, Type expected, ResidentMutationScope scope)
        {
            scope.Require();
            if (id == 0 || pointer == IntPtr.Zero || expected == null || !typeof(NativeObject).IsAssignableFrom(expected)
                || ResidentLoadedAssetIndex.RegisteredPointer(id, out var registration) != pointer) throw Error("target owner is not the exact registered native Object");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var header = M.Read(pointer, 16);
            if (M.I32(header, 8) != id) throw Error("target owner ID back-reference differs");
            var typeIndex = M.U32(header, 12) >> 21; var table = M.Read(IntPtr.Add(engine.BaseAddress, 0x19d59f0), 8200);
            var count = M.U32(table, 0);
            if (count == 0 || count > 1024 || typeIndex >= count) throw Error("target native RTTI index differs");
            var typePointer = M.Ptr(table, checked(8 + (int)typeIndex * 8)); var typeRva = typePointer.ToInt64() - engine.BaseAddress.ToInt64();
            if (typeRva <= 0 || typeRva > engine.ModuleMemorySize - 80) throw Error("target native RTTI lies outside pinned engine");
            var members = ResidentLoadedAssetIndex.Family((int)typeRva, out var family);
            if (members.Count(member => member.Id == id && member.Pointer == pointer && member.TypeRva == typeRva) != 1)
                throw Error("target Object has no unique actual-type registration");
            var name = NativeName(pointer, engine.BaseAddress);
            var owner = ResidentLoadedAssetIndex.BorrowOptional(id, out var originalHandle);
            originalHandle();
            if (ReferenceEquals(owner, null)) {
                scope.Mutate(() => owner = CreateOneWrapper(pointer, engine.BaseAddress));
            }
            if (ReferenceEquals(owner, null) || !ResidentObjectDirectory.IsLive(owner) || owner!.GetType() != expected
                || owner.GetInstanceID() != id || M.Owner(owner) != pointer || owner.name != name
                || NativeName(pointer, engine.BaseAddress) != name) throw Error("target wrapper type/name/native owner differs");
            var current = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 24));
            if (!ReferenceEquals(current.Target, owner) || !current.CacheMatches) throw Error("target native handle does not uniquely own the wrapper");
            current.RequireUnchanged(); registration(); family(); scope.Require(); return owner;
        }

        private static NativeObject CreateOneWrapper(IntPtr pointer, IntPtr engine)
        {
            // ScriptingWrapperFor dispatches the native owner's own registered
            // scripting class. It does not accept a payload-chosen MonoClass.
            // Its new-object path calls mono_object_new then Connect; the
            // separate constructor-running routine at 0x8a8080 is not called.
            RequireCode(engine, 0x8c4e80, 291, "ac6a5606d3a31d56b8a6526bd5d6e060c60cd87b588d1af56e5f7fcf3b89419c");
            RequireCode(engine, 0x8c8630, 116, "47784e426d11ec38cab0b313e17b62fc0bfabb62dbcfbd71515f10ad951de2af");
            RequireCode(engine, 0x8c2180, 296, "6b0f4205a50263274af66166ab65a8268be55a78e9fcc8705f1a1ab3096314b5");
            _ = ResidentScriptingHandles.ClassPointer(typeof(NativeObject)); // establishes pinned Mono ABI
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var newAddress = GetProcAddress(mono, "mono_gchandle_new"); var freeAddress = GetProcAddress(mono, "mono_gchandle_free");
            if (mono == IntPtr.Zero || newAddress == IntPtr.Zero || freeAddress == IntPtr.Zero
                || M.Ptr(IntPtr.Add(engine, 0x1a21988)) != newAddress || M.Ptr(IntPtr.Add(engine, 0x1a215a8)) != GetProcAddress(mono, "mono_object_new"))
                throw Error("wrapper factory Mono import binding differs");
            var retain = (NewHandle)Marshal.GetDelegateForFunctionPointer(newAddress, typeof(NewHandle));
            var free = (FreeHandle)Marshal.GetDelegateForFunctionPointer(freeAddress, typeof(FreeHandle));
            var create = (Wrapper)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x8c4e80), typeof(Wrapper));
            create(out var reflected, pointer);
            if (reflected == IntPtr.Zero) throw Error("native owner has no proven safe wrapper construction path");
            var temporary = retain(reflected, 0); // retain before any managed allocation/borrow
            if (temporary == 0) throw Error("target wrapper temporary root is absent");
            try {
                return GCHandle.FromIntPtr(new IntPtr(unchecked((int)temporary))).Target as NativeObject
                    ?? throw Error("native wrapper factory returned a non-Unity object");
            } finally { free(temporary); }
        }

        private static string NativeName(IntPtr pointer, IntPtr engine)
        {
            var getter = M.Ptr(IntPtr.Add(M.Ptr(pointer), 0x48));
            IntPtr text;
            if (getter == IntPtr.Add(engine, 0xcbd90)) {
                RequireCode(engine, 0xcbd90, 5, "efbd103662ccda4c0f468ab461257f1b371879ef59acc4189da3ceb1394859cc");
                text = M.Ptr(IntPtr.Add(pointer, 48)); // NamedObject::GetName is a single load
            } else if (getter == IntPtr.Add(engine, 0xb9910)) {
                RequireCode(engine, 0xb9910, 8, "a07033cc5f4110bb442872a2da5d26fa6600839a325d104ebc991626761d97b7");
                var code = M.Read(getter, 8); text = IntPtr.Add(getter, 7 + M.I32(code, 3));
            } else throw Error("native asset has no verified readonly name getter");
            if (text == IntPtr.Zero) return "";
            var bytes = new List<byte>();
            for (var index = 0; index <= 262144; index++) {
                var value = M.Read(IntPtr.Add(text, index), 1)[0];
                if (value == 0) return new UTF8Encoding(false, true).GetString(bytes.ToArray());
                bytes.Add(value);
            }
            throw Error("native asset name exceeds bound");
        }

        private static int FindRegisteredSourceId(string path, long local)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 8192 || local == 0) throw Error("serialized source key differs");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            // GetInstanceIDFromPathAndFileID calls GetOrGenerateInstanceID and
            // is forbidden here. Decode only the existing reverse map instead.
            RequireCode(engine, 0x9073cf, 143, "a685abf1bb03af7f0cf41beccd538c3a881daf517b74605294276ffcde266b68");
            var manager = M.Ptr(IntPtr.Add(engine, 0x1a23bc8));
            if (manager == IntPtr.Zero || M.Ptr(manager) != IntPtr.Add(engine, 0x16aad40)) throw Error("persistent source manager ABI differs");
            var enter = (TryLock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xedc160), typeof(TryLock));
            var leave = (Unlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xedc170), typeof(Unlock));
            var mutex = IntPtr.Add(manager, 184); if (!enter(mutex)) throw Error("persistent source manager is busy");
            try {
                var remapper = M.Ptr(IntPtr.Add(manager, 88)); if (remapper == IntPtr.Zero) throw Error("persistent source remapper absent");
                var paths = M.Read(IntPtr.Add(manager, 352), 32); var count = M.U64(paths, 16); var capacity = M.U64(paths, 24); var data = M.Ptr(paths, 0);
                if (count > capacity || capacity > 65536 || count != 0 && data == IntPtr.Zero) throw Error("persistent pathname array ABI differs");
                var remap = M.Read(remapper, 0x90); var ids = new HashSet<int>();
                for (var index = 0; (ulong)index < count; index++) {
                    var text = IntPtr.Add(data, index * 40); var storage = M.Read(text, 40); var length = M.U64(storage, 24);
                    if (length > 8192) throw Error("persistent pathname exceeds bound");
                    var characters = M.Ptr(storage, 0); if (characters == IntPtr.Zero) { if (length >= 16) throw Error("persistent pathname inline ABI differs"); characters = IntPtr.Add(text, 8); }
                    var bytes = M.Read(characters, checked((int)length + 1)); if (bytes[(int)length] != 0) throw Error("persistent pathname lacks terminator");
                    if (new UTF8Encoding(false, true).GetString(bytes, 0, (int)length) != path) continue;
                    var id = FindMappedId(remap, index, local, node => M.Read(node, 56)); if (id != 0) ids.Add(id);
                }
                if (!remap.SequenceEqual(M.Read(remapper, remap.Length)) || !paths.SequenceEqual(M.Read(IntPtr.Add(manager, 352), 32)))
                    throw Error("persistent source directory changed during lookup");
                return UniqueSourceId(ids);
            } finally { leave(mutex); }
        }

        internal static int UniqueSourceId(IEnumerable<int> identities)
        {
            var ids = identities.Where(value => value != 0).Distinct().Take(2).ToArray();
            if (ids.Length != 1) throw Error("serialized source has no unique already-mapped identity");
            return ids[0];
        }
        internal static int FindMappedId(byte[] remapper, int file, long local, Func<IntPtr, byte[]> read)
        {
            if (remapper == null || remapper.Length != 0x90 || file < 0 || local == 0 || read == null) throw Error("reverse source lookup input differs");
            if (M.I32(remapper, 0x8c) == file) {
                var id = checked((long)M.I32(remapper, 0x84) + checked(local * 2));
                if (local >= 0 && id >= M.I32(remapper, 0x84) && id <= M.I32(remapper, 0x88) && id != 0) return checked((int)id);
                return 0;
            }
            var head = M.Ptr(remapper, 0x50); var count = M.U64(remapper, 0x58);
            if (head == IntPtr.Zero || count > Maximum) throw Error("reverse source tree header differs");
            var sentinel = read(head); if (sentinel == null || sentinel.Length != 56 || sentinel[25] != 1) throw Error("reverse source sentinel differs");
            var current = M.Ptr(sentinel, 8); var parent = head; var seen = new HashSet<IntPtr>();
            while (current != head) {
                if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > 128 || (ulong)seen.Count > count) throw Error("reverse source tree is invalid");
                var node = read(current);
                if (node == null || node.Length != 56 || node[25] != 0 || M.Ptr(node, 8) != parent) throw Error("reverse source node/back-reference differs");
                var order = file.CompareTo(M.I32(node, 32)); if (order == 0) order = local.CompareTo(BitConverter.ToInt64(node, 40));
                if (order == 0) { var id = M.I32(node, 48); if (id == 0) throw Error("reverse source map contains a null identity"); return id; }
                parent = current; current = M.Ptr(node, order < 0 ? 0 : 16);
            }
            return 0;
        }

        internal static bool IsUnmapped(int id)
        {
            var value = new LoadingAssetProvenanceObservation().Capture(id);
            return (string?)value.GetType().GetProperty("kind")?.GetValue(value, null) == "unmapped";
        }
        internal static void RequireOrigin(int id, string path, long local)
        {
            var value = new LoadingAssetProvenanceObservation().Capture(id); var type = value.GetType();
            if ((string?)type.GetProperty("kind")?.GetValue(value, null) != "serialized"
                || (string?)type.GetProperty("sourcePath")?.GetValue(value, null) != path
                || !Equals(type.GetProperty("localIdentifier")?.GetValue(value, null), local)) throw Error("registered resource reverse/forward provenance differs");
        }
        private static void RequireCode(IntPtr engine, int rva, int size, string expected)
        {
            using var hash = SHA256.Create();
            if (BitConverter.ToString(hash.ComputeHash(M.Read(IntPtr.Add(engine, rva), size))).Replace("-", "").ToLowerInvariant() != expected)
                throw Error("native target lookup code ABI differs");
        }
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident target asset lookup " + detail + ".");
    }
}
