using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        private struct SerializedIdentifier
        {
            [FieldOffset(0)] internal int File;
            [FieldOffset(8)] internal long Object;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetInstanceIdDelegate(IntPtr remapper, ref SerializedIdentifier id);
        [StructLayout(LayoutKind.Sequential)]
        private struct StringRef
        {
            internal IntPtr Data;
            internal ulong Length;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int AddPathNameDelegate(IntPtr manager, ref StringRef path, [MarshalAs(UnmanagedType.I1)] bool flag);
        private static readonly MethodInfo? s_ForceLoadMethod = typeof(NativeObject).GetMethod(
            "ForceLoadFromInstanceID", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        private static NativeObject? ForceLoad(int id) => s_ForceLoadMethod?.Invoke(null, new object[] { id }) as NativeObject;
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        internal static NativeObject Serialized(ResidentAssetBindings.AssetRecord record, ResidentMutationScope scope)
        {
            scope.Require();
            if (record == null || record.Kind != "serialized") throw Error("serialized lookup lacks exact source identity");
            int id;
            try {
                id = FindRegisteredSourceId(record.SourcePath, record.LocalIdentifier);
            } catch (Exception ex) {
                throw Error($"for record '{record.Binding}' (name='{record.Name}', type='{record.Type?.Key}'): {ex.Message}");
            }
            var pointer = ResidentLoadedAssetIndex.LookupPointer(id, out var registered);
            if (pointer == IntPtr.Zero) {
                scope.Mutate(() => ForceLoad(id));
                pointer = ResidentLoadedAssetIndex.RegisteredPointer(id, out registered);
            }
            RequireOrigin(id, record.SourcePath, record.LocalIdentifier);
            var owner = BindRegistered(id, pointer, record.Type.Resolve(), scope);
            if (owner.name != record.Name) throw Error($"serialized asset '{record.Binding}' name differs: act='{owner.name}' exp='{record.Name}'");
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
            if (ResidentNativeAssetReference.NativeClass(pointer, (int)typeRva) != typeRva)
                throw Error("target native type registration differs");
            string? nativeName = null;
            if (TryNativeName(pointer, engine.BaseAddress, out var directName)) nativeName = directName;
            var owner = ResidentLoadedAssetIndex.BorrowOptional(id, out var originalHandle);
            originalHandle();
            if (ReferenceEquals(owner, null)) {
                scope.Mutate(() => {
                    var created = CreateOneWrapper(pointer,engine.BaseAddress);
                    var cached = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24));
                    owner = (NativeObject)SelectReferencedWrapper(created,cached.Target,expected,id,
                        value => ((NativeObject)value).GetInstanceID(),
                        value => ResidentObjectDirectory.IsLive((NativeObject)value) && M.Owner((NativeObject)value) == pointer,
                        value => cached.CacheMatches && ReferenceEquals(cached.Target,value),
                        ResidentNativeAssetReference.NativeClass(pointer,(int)typeRva) == typeRva);
                    cached.RequireUnchanged(); GC.KeepAlive(created);
                });
            }
            if (ReferenceEquals(owner, null) || !ResidentObjectDirectory.IsLive(owner) || owner!.GetType() != expected
                || owner.GetInstanceID() != id || M.Owner(owner) != pointer
                || (nativeName != null && (owner.name != nativeName || NativeName(pointer, engine.BaseAddress) != nativeName)))
                throw Error("target wrapper type/name/native owner differs");
            var current = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 24));
            if (!ReferenceEquals(current.Target, owner) || !current.CacheMatches) throw Error("target native handle does not uniquely own the wrapper");
            current.RequireUnchanged(); registration(); scope.Require(); return owner;
        }
        internal static NativeObject WrapReferencedAsset(IntPtr pointer, int id, Type expected)
        {
            var nativeScript = expected == typeof(NativeObject)
                && ResidentNativeAssetReference.NativeClass(pointer, ResidentLoadedAssetIndex.MonoScriptClass) == ResidentLoadedAssetIndex.MonoScriptClass
                && M.Ptr(pointer) == IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x16a2c30);
            if ((!nativeScript && expected != typeof(Mesh) && expected != typeof(Material) && expected != typeof(Font) && expected != typeof(Texture2D)
                && expected != typeof(AnimationClip) && expected != typeof(RuntimeAnimatorController)) || !ResidentNativeAssetReference.IsFamily(pointer, expected)
                || ResidentLoadedAssetIndex.RegisteredPointer(id, out var verify) != pointer)
                throw Error("generated resource registration or type differs");
            bool animationProgram = expected == typeof(AnimationClip) || expected == typeof(RuntimeAnimatorController);
            int actualFamily = animationProgram ? ResidentNativeAssetReference.NativeClass(pointer,
                expected == typeof(AnimationClip) ? 0x194bb90 : 0x194ba30) : ResidentLoadedAssetIndex.MonoScriptClass;
            var exactExpected = nativeScript || animationProgram ? ResolveNativeWrapperType(pointer,actualFamily) : expected;
            if (!expected.IsAssignableFrom(exactExpected)) throw Error("native registered animation wrapper has another requested managed family");
            var created = CreateOneWrapper(pointer, LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress);
            // Native Connect may keep an already-issued wrapper instead of the
            // allocation returned by the factory. The native cache is authoritative.
            var cached = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 24));
            var owner = (NativeObject)SelectReferencedWrapper(created, cached.Target, exactExpected, id,
                value => ((NativeObject)value).GetInstanceID(),
                value => ResidentObjectDirectory.IsLive((NativeObject)value) && M.Owner((NativeObject)value) == pointer,
                value => cached.CacheMatches && ReferenceEquals(cached.Target,value),
                ResidentNativeAssetReference.IsFamily(pointer, expected)
                    && (!nativeScript || ResidentNativeAssetReference.NativeClass(pointer,ResidentLoadedAssetIndex.MonoScriptClass) == ResidentLoadedAssetIndex.MonoScriptClass
                        && M.Ptr(pointer) == IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0x16a2c30)
                        && ResolveNativeWrapperType(pointer,ResidentLoadedAssetIndex.MonoScriptClass) == exactExpected)
                    && (!animationProgram || ResidentNativeAssetReference.NativeClass(pointer,actualFamily) == actualFamily
                        && ResolveNativeWrapperType(pointer,actualFamily) == exactExpected));
            cached.RequireUnchanged(); verify();
            GC.KeepAlive(created);
            return owner;
        }
        private static Type ResolveNativeWrapperType(IntPtr pointer,int nativeFamily)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            if (ResidentNativeAssetReference.NativeClass(pointer,nativeFamily) != nativeFamily)
                throw Error("native wrapper mapping has another exact native family");
            // A player can lack a managed class for a native subtype. The
            // engine resolves its registered parent class (MonoScript ->
            // TextAsset here), which remains an exact metadata dependency.
            RequireCode(engine.BaseAddress,0x8c4e00,116,"1339fe0c557937febea1f084248ca935751bc43a4aa0df7cb8a1dbf9bc671069");
            var resolve = (Wrapper)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress,0x8c4e00),typeof(Wrapper));
            resolve(out var nativeClass,IntPtr.Add(engine.BaseAddress,nativeFamily));
            var actual = ResidentScriptingHandles.ReadClass(nativeClass) ?? throw Error("native registered wrapper class has no managed identity");
            return RequireRegisteredWrapperType(actual,nativeClass,ResidentScriptingHandles.ClassPointer);
        }
        internal static Type RequireRegisteredWrapperType(Type actual,IntPtr nativeClass,Func<Type,IntPtr> classPointer)
        {
            if (actual == null || nativeClass == IntPtr.Zero || !typeof(NativeObject).IsAssignableFrom(actual)
                || typeof(Component).IsAssignableFrom(actual) || actual == typeof(GameObject)
                || actual.ContainsGenericParameters || classPointer(actual) != nativeClass)
                throw Error("native registered wrapper class does not round-trip its exact managed metadata");
            return actual;
        }
        internal static object SelectReferencedWrapper(object created, object? authoritative, Type expected, int expectedId,
            Func<object,int> instanceId, Func<object,bool> nativeOwnerMatches, Func<object,bool> cachedTargetMatches, bool familyMatches)
        {
            string TypeName(object? value) {
                var name = value?.GetType().AssemblyQualifiedName ?? "<null>";
                return name.Length <= 160 ? name : name.Substring(0,160);
            }
            bool typeMatches = authoritative != null && authoritative.GetType() == expected;
            int actualId = 0;
            bool idReadable = false;
            bool? pointerMatches = null, cacheMatches = null;
            string pointerStatus = "notChecked", cacheStatus = "notChecked";
            if (typeMatches) {
                try { actualId = instanceId(authoritative!); idReadable = true; } catch (Exception) { }
                try { pointerMatches = nativeOwnerMatches(authoritative!); pointerStatus = pointerMatches.Value.ToString(); }
                catch (Exception) { pointerStatus = "unreadable"; }
                try { cacheMatches = cachedTargetMatches(authoritative!); cacheStatus = cacheMatches.Value.ToString(); }
                catch (Exception) { cacheStatus = "unreadable"; }
            }
            string reason = authoritative == null ? "authoritative cache absent"
                : !typeMatches ? "authoritative type differs"
                : !idReadable || actualId != expectedId ? "authoritative instance ID differs"
                : pointerMatches != true ? "authoritative native owner differs"
                : cacheMatches != true ? "authoritative cache back-reference differs"
                : !familyMatches ? "authoritative native asset family differs" : "";
            if (reason.Length != 0)
                throw Error("generated resource wrapper " + reason + "; expected=" + expected.FullName
                    + "; createdType=" + TypeName(created) + "; handleTargetType=" + TypeName(authoritative)
                    + "; expectedId=" + expectedId + "; actualId=" + (idReadable ? actualId.ToString() : typeMatches ? "unreadable" : "notChecked")
                    + "; pointerMatches=" + pointerStatus + "; cacheMatches=" + cacheStatus + "; familyMatches=" + familyMatches);
            return authoritative!;
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
        private static bool TryNativeName(IntPtr pointer, IntPtr engine, out string name)
        {
            name = "";
            var vtable = M.Ptr(pointer);
            if (vtable == IntPtr.Zero) return false;
            var getter = M.Ptr(IntPtr.Add(vtable, 0x48));
            IntPtr text;
            if (getter == IntPtr.Add(engine, 0xcbd90)) {
                RequireCode(engine, 0xcbd90, 5, "efbd103662ccda4c0f468ab461257f1b371879ef59acc4189da3ceb1394859cc");
                text = M.Ptr(IntPtr.Add(pointer, 48));
            } else if (getter == IntPtr.Add(engine, 0xb9910)) {
                RequireCode(engine, 0xb9910, 8, "a07033cc5f4110bb442872a2da5d26fa6600839a325d104ebc991626761d97b7");
                var code = M.Read(getter, 8); text = IntPtr.Add(getter, 7 + M.I32(code, 3));
            } else {
                return false;
            }
            if (text == IntPtr.Zero) { name = ""; return true; }
            var bytes = new List<byte>();
            for (var index = 0; index <= 262144; index++) {
                var value = M.Read(IntPtr.Add(text, index), 1)[0];
                if (value == 0) { name = new UTF8Encoding(false, true).GetString(bytes.ToArray()); return true; }
                bytes.Add(value);
            }
            throw Error("native asset name exceeds bound");
        }
        private static string NativeName(IntPtr pointer, IntPtr engine)
        {
            if (TryNativeName(pointer, engine, out var name)) return name;
            throw Error("native asset has no verified readonly name getter");
        }
        internal static int FindRegisteredSourceId(string path, long local)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 8192 || local == 0) throw Error("serialized source key differs");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            // GetInstanceIDFromPathAndFileID calls GetOrGenerateInstanceID and
            // is forbidden here. Decode only the existing reverse map instead.
            RequireCode(engine, 0x9073cf, 143, "a685abf1bb03af7f0cf41beccd538c3a881daf517b74605294276ffcde266b68");
            RequireCode(engine, 0x907390, 63, "e6a35ef4135eb1cc5d50a99ab4a8396e8506f24aae4ec3e87916eae2786d46c0");
            RequireCode(engine, 0x902650, 48, "3b5e732a3520f4a8466036a660cdb5da57c58a595a4484cc7dc10116f4efebaa");
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
                var matchedIndices = new List<int>();
                for (var index = 0; (ulong)index < count; index++) {
                    var text = IntPtr.Add(data, index * 40); var storage = M.Read(text, 40); var length = M.U64(storage, 24);
                    if (length > 8192) throw Error("persistent pathname exceeds bound");
                    var characters = M.Ptr(storage, 0); if (characters == IntPtr.Zero) { if (length >= 16) throw Error("persistent pathname inline ABI differs"); characters = IntPtr.Add(text, 8); }
                    var bytes = M.Read(characters, checked((int)length + 1)); if (bytes[(int)length] != 0) throw Error("persistent pathname lacks terminator");
                    if (new UTF8Encoding(false, true).GetString(bytes, 0, (int)length) != path) continue;
                    matchedIndices.Add(index);
                    var id = FindMappedId(remap, index, local, node => M.Read(node, 56)); if (id != 0) ids.Add(id);
                }
                bool registeredPath = false;
                if (matchedIndices.Count == 0) {
                    var addPath = (AddPathNameDelegate)Marshal.GetDelegateForFunctionPointer(
                        IntPtr.Add(engine, 0x902650), typeof(AddPathNameDelegate));
                    var rawBytes = Encoding.UTF8.GetBytes(path + "\0");
                    var pinned = GCHandle.Alloc(rawBytes, GCHandleType.Pinned);
                    try {
                        var stringRef = new StringRef { Data = pinned.AddrOfPinnedObject(), Length = (ulong)(rawBytes.Length - 1) };
                        var fileIndex = addPath(manager, ref stringRef, true);
                        if (fileIndex < 0) throw Error($"failed to register persistent pathname: {path}");
                        matchedIndices.Add(fileIndex);
                        registeredPath = true;
                    } finally {
                        pinned.Free();
                    }
                }
                bool generated = false;
                if (ids.Count == 0 && matchedIndices.Count == 1) {
                    var getOrGen = (GetInstanceIdDelegate)Marshal.GetDelegateForFunctionPointer(
                        IntPtr.Add(engine, 0x907390), typeof(GetInstanceIdDelegate));
                    var identifier = new SerializedIdentifier { File = matchedIndices[0], Object = local };
                    var genId = getOrGen(remapper, ref identifier);
                    if (genId != 0) { ids.Add(genId); generated = true; }
                }
                if (!registeredPath && !paths.SequenceEqual(M.Read(IntPtr.Add(manager, 352), 32)))
                    throw Error("persistent source directory changed during lookup");
                if (!generated && !remap.SequenceEqual(M.Read(remapper, remap.Length)))
                    throw Error("persistent source remapper changed during lookup");
                if (ids.Count != 1) {
                    throw Error($"serialized source has no unique already-mapped identity: path='{path}', local={local}, matchedIndices=[{string.Join(",", matchedIndices)}], idsCount={ids.Count}, totalPathsCount={count}");
                }
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
