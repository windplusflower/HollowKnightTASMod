using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Exact-type Camera/Canvas/CanvasRenderer adapter for the pinned player.
    // D: camera matrices/settings/history, UI CPU geometry, clipping/color,
    // instructions and retained parent/handle relationships. Dependency: local
    // object/surface/texture IDs, engine callbacks, scene pointers, native array
    // storage and registries. S0: no in-flight camera context or Canvas GPU batch
    // cache; Canon uses the original engine teardown and invalidation operations.
    internal static partial class ResidentCameraCanvasState
    {
        private const int Maximum = 100000, MaximumBytes = 32 * 1024 * 1024;
        internal sealed class Data
        {
            public int Version = 2;
            public CameraRow[] Cameras = Array.Empty<CameraRow>();
            public CanvasRow[] Canvases = Array.Empty<CanvasRow>();
            public CanvasRendererRow[] Renderers = Array.Empty<CanvasRendererRow>();
            public SharedGroup[] Shared = Array.Empty<SharedGroup>();
            public CameraRegistry CameraManager = new CameraRegistry();
            public CanvasRegistry CanvasManager = new CanvasRegistry();
            public RenderPool RenderTextures = new RenderPool();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class NativeBuffer
        {
            public int Capacity;
            public byte[] Bytes = Array.Empty<byte>();
        }
        internal sealed class BindingList
        {
            public int Capacity;
            public string[] Owners = Array.Empty<string>();
        }
        internal sealed class TextureBinding
        {
            public string Owner = "";
            public int Slot;
            public bool Unknown;
        }
        internal sealed class SurfaceBinding
        {
            public string Owner = "";
            public int Slot;
            public bool Unknown;
        }
        internal sealed class TransientTexture
        {
            public string Alias = "";
            public ResidentObjectDirectory.TypeRecord? Type;
        }
        internal sealed class Callback
        {
            public int Rva, UserRva;
            public string User = "", UserKind = "none";
            public bool WithUser;
        }
        internal sealed class SharedGroup
        {
            public string Key = "";
            public string[] Meshes = Array.Empty<string>(), Sprites = Array.Empty<string>();
            public ResidentSharedMeshState.Data State = new ResidentSharedMeshState.Data();
        }
        private sealed class Range
        {
            internal readonly int Offset, Count;
            internal Range(int offset, int count) { Offset = offset; Count = count; }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Unary(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Binary(IntPtr first, IntPtr second);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Allocate(UIntPtr bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Delete(IntPtr pointer, UIntPtr bytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AssignString(IntPtr storage, IntPtr bytes, UIntPtr length);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool FenceDone(IntPtr fence);
        private static T Function<T>(int rva, string prefix) where T : Delegate
        {
            var pointer = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, rva);
            if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix)
                throw Error("native receiver ABI differs: " + rva.ToString("x"));
            return (T)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        // Process-local exclusion witness, never serialized. The native object
        // and its optional issued wrapper are observed separately.
        internal sealed class TransientOwner
        {
            internal readonly int Id;
            internal readonly IntPtr Pointer;
            internal readonly ResidentScriptingHandles.Capture Handle;
            internal TransientOwner(IntPtr pointer)
            {
                Pointer = pointer; Id = M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
                Handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24)); Require();
            }
            internal void Require()
            {
                if (Id == 0 || ResidentLoadedAssetIndex.RegisteredPointer(Id,out var check) != Pointer
                    || ResidentNativeAssetReference.NativeClass(Pointer,ResidentLoadedAssetIndex.RenderTextureClass) != ResidentLoadedAssetIndex.RenderTextureClass)
                    throw Error("transient RenderTexture lost its exact registration or native class");
                if (!ReferenceEquals(Handle.Target,null) && (!(Handle.Target is RenderTexture rt) || rt.GetType() != typeof(RenderTexture) || M.Owner(rt) != Pointer))
                    throw Error("transient texture has a foreign issued wrapper");
                Handle.RequireUnchanged(); check();
            }
        }
        private sealed class Reader
        {
            internal readonly ResidentObjectDirectory Directory;
            internal readonly Func<object, string?> Bind;
            internal readonly Dictionary<IntPtr, byte[]> Frozen = new Dictionary<IntPtr, byte[]>();
            internal readonly Dictionary<IntPtr, string> SharedKeys = new Dictionary<IntPtr, string>();
            internal readonly Dictionary<IntPtr, string> Handles = new Dictionary<IntPtr, string>();
            internal readonly Dictionary<IntPtr, string> CanvasData = new Dictionary<IntPtr, string>();
            private readonly Dictionary<int, ResidentLoadedAssetIndex.Member[]> families = new Dictionary<int, ResidentLoadedAssetIndex.Member[]>();
            private readonly List<Action> familyChecks = new List<Action>();
            private readonly Dictionary<IntPtr,string> pointers = new Dictionary<IntPtr,string>();
            private readonly Dictionary<int,string> sceneKeys;
            private Dictionary<uint, Tuple<ResidentLoadedAssetIndex.Member, int>>? textures;
            private Dictionary<long, Tuple<ResidentLoadedAssetIndex.Member, int>>? surfaces;
            private readonly Dictionary<IntPtr,TransientOwner> transientOwners = new Dictionary<IntPtr,TransientOwner>();
            private readonly Dictionary<IntPtr, string> transientAliases = new Dictionary<IntPtr, string>();
            private readonly Action<TransientOwner>? observeTransient;
            internal Reader(ResidentObjectDirectory directory, Func<object, string?> bind, Action<TransientOwner>? observeTransient = null)
            {
                Directory = directory; Bind = bind; this.observeTransient = observeTransient;
                sceneKeys = directory.CopySnapshot().Scenes.ToDictionary(scene => directory.SceneFor(scene.Key).handle,scene => scene.Key);
                foreach (var pair in directory.NativeBindings) pointers.Add(M.Owner(pair.Value),pair.Key);
                foreach (var pair in directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(CanvasRenderer))) {
                    Handles.Add(IntPtr.Add(M.Owner(pair.Value), 256), pair.Key + "/push");
                    Handles.Add(IntPtr.Add(M.Owner(pair.Value), 280), pair.Key + "/pop");
                }
                foreach (var pair in directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(Canvas)))
                    CanvasData.Add(IntPtr.Add(M.Owner(pair.Value), 144), pair.Key);
            }
            internal byte[] Read(IntPtr pointer, int count)
            {
                var bytes = M.Read(pointer, count);
                if (count != 0) {
                    if (Frozen.TryGetValue(pointer, out var previous)) {
                        if (!previous.Take(Math.Min(previous.Length, count)).SequenceEqual(bytes.Take(Math.Min(previous.Length, count))))
                            throw Error("native state changed during capture");
                        if (previous.Length < count) Frozen[pointer] = bytes;
                    } else Frozen.Add(pointer, bytes);
                }
                return bytes;
            }
            internal void RequireFrozen()
            { foreach (var check in familyChecks) check(); foreach (var pair in Frozen) if (!M.Read(pair.Key, pair.Value.Length).SequenceEqual(pair.Value)) throw Error("native state changed before capture completed"); }
            private ResidentLoadedAssetIndex.Member[] Family(int rva)
            {
                if (!families.TryGetValue(rva, out var values)) {
                    values = ResidentLoadedAssetIndex.Family(rva, out var check); families.Add(rva, values); familyChecks.Add(check);
                }
                return values;
            }
            internal string Id<T>(int id) where T : NativeObject
            {
                if (id == 0) return "";
                var pointer = ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify); var key = Pointer<T>(pointer); verify(); return key;
            }
            internal string Pointer<T>(IntPtr pointer) where T : NativeObject
            {
                if (pointer == IntPtr.Zero) return "";
                var key = ResidentNativeAssetReference.BindPointer(pointer,typeof(T),Bind);
                if (pointers.TryGetValue(pointer,out var old) && old != key) throw Error("native pointer was given two bindings");
                pointers[pointer] = key; return key;
            }
            private readonly Dictionary<int,HashSet<IntPtr>> dirtyMembers = new Dictionary<int,HashSet<IntPtr>>();
            internal string DirtyOwner(IntPtr pointer, int type, int pendingCount)
            {
                if (!dirtyMembers.TryGetValue(type, out var members)) {
                    members = new HashSet<IntPtr>(Family(type).Select(member => member.Pointer));
                    dirtyMembers.Add(type, members);
                }
                if (!members.Contains(pointer))
                    throw Error("pending Canvas dirty group of " + pendingCount + " has an unregistered " +
                        (type == ResidentLoadedAssetIndex.CanvasClass ? "Canvas key" : "CanvasRenderer member"));
                return type == ResidentLoadedAssetIndex.CanvasClass ? Pointer<Canvas>(pointer) : Pointer<CanvasRenderer>(pointer);
            }
            internal string Scene(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return "";
                var handle = M.I32(Read(IntPtr.Add(pointer,8),4),0);
                return sceneKeys.TryGetValue(handle,out var key) ? key : throw Error("camera native scene is outside the complete directory");
            }
            internal TransientTexture Transient(IntPtr pointer, string firstUse)
            {
                if (pointer == IntPtr.Zero) return new TransientTexture();
                if (!transientAliases.TryGetValue(pointer,out var alias)) {
                    var owner = new TransientOwner(pointer); transientOwners.Add(pointer,owner); familyChecks.Add(owner.Require);
                    transientAliases.Add(pointer,alias = "camera-cache/" + firstUse); observeTransient?.Invoke(owner);
                }
                return new TransientTexture { Alias = alias, Type = ResidentObjectDirectory.TypeRecord.Capture(typeof(RenderTexture)) };
            }
            internal IntPtr RenderTexturePointer(int id)
            {
                var pointer = ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify); familyChecks.Add(verify);
                if (ResidentNativeAssetReference.NativeClass(pointer,ResidentLoadedAssetIndex.RenderTextureClass) != ResidentLoadedAssetIndex.RenderTextureClass)
                    throw Error("temporary pool does not own an exact native RenderTexture");
                return pointer;
            }
            internal TextureBinding Texture(uint id)
            {
                if (id == 0) return new TextureBinding();
                if (textures == null) {
                    textures = new Dictionary<uint, Tuple<ResidentLoadedAssetIndex.Member, int>>();
                    var renderTargets = new HashSet<IntPtr>(Family(ResidentLoadedAssetIndex.RenderTextureClass).Select(row => row.Pointer));
                    foreach (var owner in Family(ResidentLoadedAssetIndex.TextureClass)) {
                        var native = owner.Pointer; var count = renderTargets.Contains(native) ? 3 : 1;
                        for (var slot = 0; slot < count; slot++) {
                            var value = M.U32(Read(IntPtr.Add(native, slot == 0 ? 80 : slot == 1 ? 332 : 336), 4), 0);
                            if (value == 0) continue;
                            if (textures.TryGetValue(value, out var previous)) {
                                if (!ReferenceEquals(previous.Item1, owner)) throw Error("native texture ID has unrelated owners");
                            } else textures.Add(value, Tuple.Create(owner, slot));
                        }
                    }
                }
                return textures.TryGetValue(id, out var binding) ? new TextureBinding { Owner = Pointer<Texture>(binding.Item1.Pointer), Slot = binding.Item2 }
                    : new TextureBinding { Unknown = true };
            }
            internal SurfaceBinding Surface(long handle)
            {
                if (handle == 0) return new SurfaceBinding();
                if (surfaces == null) {
                    surfaces = new Dictionary<long, Tuple<ResidentLoadedAssetIndex.Member, int>>();
                    foreach (var owner in Family(ResidentLoadedAssetIndex.RenderTextureClass)) {
                        var native = owner.Pointer;
                        for (var slot = 0; slot < 3; slot++) {
                            var value = BitConverter.ToInt64(Read(IntPtr.Add(native, 344 + slot * 8), 8), 0); if (value == 0) continue;
                            if (surfaces.TryGetValue(value, out var previous)) {
                                if (!ReferenceEquals(previous.Item1, owner)) throw Error("render surface has unrelated owners");
                            } else surfaces.Add(value, Tuple.Create(owner, slot));
                        }
                    }
                }
                return surfaces.TryGetValue(handle, out var binding) ? new SurfaceBinding { Owner = Pointer<RenderTexture>(binding.Item1.Pointer), Slot = binding.Item2 }
                    : new SurfaceBinding { Unknown = true };
            }
            internal Callback Callback(byte[] bytes, int offset)
            {
                var result = new Callback { Rva = Rva(M.Ptr(bytes, offset)), WithUser = Bool(bytes[offset + 16]) };
                var user = M.Ptr(bytes, offset + 8);
                if (!result.WithUser || user == IntPtr.Zero) return result;
                if (pointers.TryGetValue(user, out var owner)) { result.UserKind = "owner"; result.User = owner; }
                else if (transientOwners.ContainsKey(user)) {
                    result.UserKind = "owner"; result.User = Pointer<RenderTexture>(user);
                } else if (IsEngine(user)) { result.UserKind = "engine"; result.UserRva = Rva(user); }
                else result.UserKind = "unsupported";
                return result;
            }
            internal string Shared(IntPtr pointer, string firstOwner)
            {
                if (pointer == IntPtr.Zero) return "";
                if (!SharedKeys.TryGetValue(pointer, out var key)) SharedKeys.Add(pointer, key = "ui-shared/" + firstOwner);
                return key;
            }
            internal SharedGroup[] SharedGroups()
            {
                if (SharedKeys.Count == 0) return Array.Empty<SharedGroup>();
                // Index live mesh and sprite-cache aliases once, retaining the native
                // family and every inspected slot in this observation's witness.
                var meshes = new Dictionary<IntPtr,List<string>>(); var sprites = new Dictionary<IntPtr,List<string>>();
                void Add(Dictionary<IntPtr,List<string>> groups,IntPtr shared,Func<string> key) {
                    if (!SharedKeys.ContainsKey(shared)) return;
                    if (!groups.TryGetValue(shared,out var list)) groups.Add(shared,list = new List<string>());
                    list.Add(key());
                }
                foreach (var member in Family(ResidentLoadedAssetIndex.MeshClass))
                    Add(meshes,M.Ptr(Read(IntPtr.Add(member.Pointer,56),8),0),() => Pointer<Mesh>(member.Pointer));
                foreach (var member in Family(ResidentLoadedAssetIndex.SpriteRendererClass)) {
                    var cache = M.Ptr(Read(IntPtr.Add(member.Pointer,584),8),0);
                    if (cache != IntPtr.Zero) Add(sprites,M.Ptr(Read(IntPtr.Add(cache,96),8),0),() => {
                        var key = ResidentNativeAssetReference.Bind(member.Pointer,ResidentLoadedAssetIndex.SpriteRendererClass,typeof(SpriteRenderer),Bind);
                        return key != ResidentNativeAssetReference.Missing ? key : throw Error("shared Sprite cache owner is unbound");
                    });
                }
                string[] Keys(Dictionary<IntPtr,List<string>> source,IntPtr shared) => source.TryGetValue(shared,out var keys)
                    ? keys.OrderBy(key => key,StringComparer.Ordinal).ToArray() : Array.Empty<string>();
                return SharedKeys.OrderBy(pair => pair.Value,StringComparer.Ordinal).Select(pair => new SharedGroup {
                    Key = pair.Value, State = ResidentSharedMeshState.Capture(pair.Key),
                    Meshes = Keys(meshes,pair.Key), Sprites = Keys(sprites,pair.Key)
                }).ToArray();
            }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind, Action<TransientOwner>? observeTransient = null)
        {
            if (directory == null || bind == null) throw new ArgumentNullException(); LoadingEngineClockProbe.RequirePinnedEngine();
            var reader = new Reader(directory, bind, observeTransient); var data = new Data();
            data.Cameras = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(Camera)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CaptureCamera(reader, pair.Key, (Camera)pair.Value)).ToArray();
            data.Renderers = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(CanvasRenderer)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CaptureCanvasRenderer(reader, pair.Key, (CanvasRenderer)pair.Value)).ToArray();
            data.Canvases = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(Canvas)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CaptureCanvas(reader, pair.Key, (Canvas)pair.Value)).ToArray();
            data.Shared = reader.SharedGroups(); data.CameraManager = CaptureCameraRegistry(reader); data.CanvasManager = CaptureCanvasRegistry(reader);
            data.RenderTextures = CaptureRenderPool(reader);
            data.CoverageGaps = RequiredCoverageGaps(data); Validate(data); reader.RequireFrozen(); return data;
        }
        private static byte[][] CaptureRanges(byte[] bytes, Range[] ranges) => ranges.Select(range => M.Slice(bytes, range.Offset, range.Count)).ToArray();
        private static void ApplyRanges(IntPtr native, Range[] ranges, byte[][] values)
        { for (var index = 0; index < ranges.Length; index++) M.Write(IntPtr.Add(native, ranges[index].Offset), values[index]); }
        private static NativeBuffer Dynamic(Reader reader, IntPtr pointer, int stride)
        {
            var header = reader.Read(pointer, 32); var size = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
            if (size > capacity || capacity > Maximum || capacity * (ulong)stride > MaximumBytes) throw Error("native dynamic array exceeds bounds");
            return new NativeBuffer { Capacity = (int)capacity, Bytes = reader.Read(M.Ptr(header, 0), checked((int)size * stride)) };
        }
        private static NativeBuffer Vector(Reader reader, IntPtr pointer, int stride)
        {
            var header = reader.Read(pointer, 24); var start = M.Ptr(header, 0).ToInt64(); var end = M.Ptr(header, 8).ToInt64(); var last = M.Ptr(header, 16).ToInt64();
            var size = end - start; var capacity = last - start;
            if (size < 0 || capacity < size || size % stride != 0 || capacity % stride != 0 || capacity > MaximumBytes || capacity / stride > Maximum)
                throw Error("native vector exceeds bounds");
            return new NativeBuffer { Capacity = (int)(capacity / stride), Bytes = reader.Read(new IntPtr(start), checked((int)size)) };
        }
        private static BindingList BindList(NativeBuffer buffer, int stride, Func<byte[], int, string> binding)
            => new BindingList { Capacity = buffer.Capacity, Owners = Enumerable.Range(0, buffer.Bytes.Length / stride).Select(index => binding(buffer.Bytes, index * stride)).ToArray() };
        private static void WriteDynamic(IntPtr pointer, byte[] bytes, int capacity, int stride, int alignment, Action admitted)
        {
            admitted(); M.Write(IntPtr.Add(pointer, 16), BitConverter.GetBytes(0UL)); M.Reserve(pointer, capacity, stride, alignment, admitted);
            if (M.U64(M.Read(pointer, 32), 24) >> 1 != (ulong)capacity) throw Error("native dynamic-array capacity did not read back");
            M.Write(M.Ptr(pointer), bytes); M.Write(IntPtr.Add(pointer, 16), BitConverter.GetBytes((ulong)(bytes.Length / stride))); admitted();
        }
        private static void WriteVector(IntPtr pointer, byte[] bytes, int capacity, int stride, Action admitted)
        {
            admitted(); var before = M.Read(pointer, 24); var old = M.Ptr(before, 0); var oldBytes = M.Ptr(before, 16).ToInt64() - old.ToInt64();
            if (oldBytes < 0 || oldBytes > MaximumBytes) throw Error("target vector ownership differs");
            var count = checked(capacity * stride); var replacement = old;
            if (oldBytes != count) {
                replacement = count == 0 ? IntPtr.Zero : Function<Allocate>(0x567ff0, "40534883ec40488bd9488b0d18a94501")(new UIntPtr((uint)count));
                if (count != 0 && replacement == IntPtr.Zero) throw Error("target native vector allocation failed");
            }
            M.Write(replacement, bytes);
            M.Write(pointer, BitConverter.GetBytes(replacement.ToInt64()).Concat(BitConverter.GetBytes(replacement.ToInt64() + bytes.Length))
                .Concat(BitConverter.GetBytes(replacement.ToInt64() + count)).ToArray());
            if (old != IntPtr.Zero && old != replacement) Function<Delete>(0x1551d80, "e98b6301ff")(old, new UIntPtr((uint)oldBytes)); admitted();
        }
        private static void WriteString(IntPtr pointer, string value, Action admitted)
        {
            admitted(); var bytes = new UTF8Encoding(false, true).GetBytes(value); var storage = Marshal.AllocHGlobal(bytes.Length + 1);
            try { M.Write(storage, bytes.Concat(new byte[1]).ToArray()); Function<AssignString>(0x8ece0, "48895c241048896c24184889742420574883ec20488bf149")(pointer, storage, new UIntPtr((uint)bytes.Length)); }
            finally { Marshal.FreeHGlobal(storage); } admitted();
        }
        private static bool IsEngine(IntPtr pointer)
        { var engine = LoadingEngineClockProbe.RequirePinnedEngine(); var rva = pointer.ToInt64() - engine.BaseAddress.ToInt64(); return rva > 0 && rva < engine.ModuleMemorySize; }
        private static int Rva(IntPtr pointer)
        { if (pointer == IntPtr.Zero) return 0; if (!IsEngine(pointer)) throw Error("native callback is outside the pinned engine"); return checked((int)(pointer.ToInt64() - LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress.ToInt64())); }
        private static IntPtr EnginePointer(int rva) => rva == 0 ? IntPtr.Zero : IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, rva);
        private static bool Bool(byte value) => value <= 1 ? value != 0 : throw Error("native boolean has another representation");
        private static bool Text(string? value, bool empty = true) => value != null && value.Length <= 262144 && (empty || value.Length != 0) && value.IndexOf('\0') < 0;
        private static void ValidateList(BindingList list, bool empty = true)
        { if (list == null || list.Owners == null || list.Capacity < list.Owners.Length || list.Capacity > Maximum || list.Owners.Any(value => !Text(value, empty))) throw Error("native binding-list shape differs"); }
        private static void ValidateBuffer(NativeBuffer buffer, int stride)
        { if (buffer == null || buffer.Bytes == null || buffer.Bytes.Length % stride != 0 || buffer.Capacity < buffer.Bytes.Length / stride || buffer.Capacity > Maximum || (long)buffer.Capacity * stride > MaximumBytes) throw Error("native byte-buffer shape differs"); }
        private static void ValidateRanges(byte[][] values, Range[] ranges)
        { if (values == null || values.Length != ranges.Length || values.Where((value, index) => value == null || value.Length != ranges[index].Count).Any()) throw Error("typed native scalar layout differs"); }
        private static void ValidateTexture(TextureBinding binding)
        { if (binding == null || !Text(binding.Owner) || binding.Slot < 0 || binding.Slot > 2 || binding.Owner.Length == 0 && binding.Slot != 0 || binding.Unknown && binding.Owner.Length != 0) throw Error("texture binding schema differs"); }
        private static void ValidateSurface(SurfaceBinding binding)
        { if (binding == null || !Text(binding.Owner) || binding.Slot < 0 || binding.Slot > 2 || binding.Owner.Length == 0 && binding.Slot != 0 || binding.Unknown && binding.Owner.Length != 0) throw Error("surface binding schema differs"); }
        private static void ValidateCallback(Callback value)
        {
            if (value == null || value.Rva <= 0 || value.Rva >= 0x4000000 || value.UserRva < 0 || value.UserRva >= 0x4000000 || !Text(value.User)
                || !new[] { "none", "owner", "engine", "unsupported" }.Contains(value.UserKind) || !value.WithUser && value.UserKind != "none"
                || (value.UserKind == "owner") != (value.User.Length != 0) || (value.UserKind == "engine") != (value.UserRva != 0)) throw Error("native callback schema differs");
        }
        private static uint TextureId(TextureBinding binding, Func<string, NativeObject> resolve)
        {
            if (binding.Unknown) throw Error("unbound native texture ID cannot be restored"); if (binding.Owner.Length == 0) return 0;
            var owner = ResidentNativeAssetReference.ResolveTyped(binding.Owner,binding.Slot == 0 ? typeof(Texture) : typeof(RenderTexture),resolve);
            return M.U32(M.Read(IntPtr.Add(owner, binding.Slot == 0 ? 80 : binding.Slot == 1 ? 332 : 336), 4), 0);
        }
        private static long SurfaceHandle(SurfaceBinding binding, Func<string, NativeObject> resolve)
        {
            if (binding.Unknown) throw Error("unbound native surface cannot be restored"); if (binding.Owner.Length == 0) return 0;
            var owner = ResidentNativeAssetReference.ResolveTyped(binding.Owner,typeof(RenderTexture),resolve);
            return BitConverter.ToInt64(M.Read(IntPtr.Add(owner, 344 + binding.Slot * 8), 8), 0);
        }
        private static IntPtr Pointer(string binding, Func<string, NativeObject> resolve) => ResidentNativeAssetReference.ResolveTyped(binding,typeof(NativeObject),resolve);
        private static int Id(string binding, Func<string, NativeObject> resolve) => ResidentNativeAssetReference.ResolveId(binding,typeof(NativeObject),resolve);
        private static byte[] CallbackBytes(Callback value, Func<string, NativeObject> resolve)
        {
            if (value.UserKind == "unsupported") throw Error("unbound native callback context cannot be restored");
            var bytes = new byte[24]; System.Buffer.BlockCopy(BitConverter.GetBytes(EnginePointer(value.Rva).ToInt64()), 0, bytes, 0, 8);
            var user = value.UserKind == "owner" ? Pointer(value.User, resolve) : value.UserKind == "engine" ? EnginePointer(value.UserRva) : IntPtr.Zero;
            System.Buffer.BlockCopy(BitConverter.GetBytes(user.ToInt64()), 0, bytes, 8, 8); bytes[16] = value.WithUser ? (byte)1 : (byte)0; return bytes;
        }
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(expected) != Json(actual)) throw Error("camera/UI native independent readback differs"); }
        private static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
        private static Data Copy(Data value) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Json(value)) ?? throw Error("camera/UI snapshot copy is absent");
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident camera/UI: " + reason);
    }
}
