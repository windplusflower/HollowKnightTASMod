using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Some PPtrs and GPU handles precede every scripting wrapper. Read-only
    // identity dependencies may remain native; typed program dependencies issue
    // their exact wrapper only during the bounded resource discovery pass.
    internal static class ResidentNativeAssetReference
    {
        internal const string Prefix = "resident-native-asset/";
        internal const string Missing = "@unbound-native-asset-origin";
        // Type-descriptor RVAs from the pinned player's RTTI, never instance
        // IDs or asset PathIDs. Issued scene components still use the resident tree binder.
        private static readonly Dictionary<Type,int> AssetFamilies = new Dictionary<Type,int> {
            {typeof(UnityEngine.Object),0x194b230}, {typeof(Material),0x194b750}, {typeof(Shader),0x194e2b0},
            {typeof(Sprite),0x195e9f0}, {typeof(Mesh),ResidentLoadedAssetIndex.MeshClass},
            {typeof(Texture),ResidentLoadedAssetIndex.TextureClass}, {typeof(Texture2D),0x194ceb0},
            {typeof(RenderTexture),ResidentLoadedAssetIndex.RenderTextureClass},
            {typeof(Animator),0x194b410}, {typeof(Avatar),0x194bcf0}, {typeof(AnimationClip),0x194bb90},
            {typeof(RuntimeAnimatorController),0x194ba30}, {typeof(Font),0x194cb60}, {typeof(Flare),0x195fd50}, {typeof(Component),0x194b320}, {typeof(GameObject),0x194b2d0}, {typeof(Transform),0x19491a0}, {typeof(Renderer),0x1949330},
            {typeof(MeshRenderer),0x19494c0}, {typeof(SkinnedMeshRenderer),0x1949470}, {typeof(SpriteRenderer),ResidentLoadedAssetIndex.SpriteRendererClass},
            {typeof(CanvasRenderer),ResidentLoadedAssetIndex.CanvasRendererClass}, {typeof(Canvas),ResidentLoadedAssetIndex.CanvasClass}, {typeof(Camera),0x1949150}, {typeof(Light),0x1949380}, {typeof(LODGroup),0x1965060},
            {typeof(Rigidbody),0x1949e80}, {typeof(Rigidbody2D),0x194a4e0}, {typeof(Collider),0x1949ed0}, {typeof(Collider2D),0x194a530},
            {typeof(ParticleSystem),0x1949cb0}, {typeof(ParticleSystemForceField),0x1949d50}, {typeof(AudioClip),0x19674b0}, {typeof(UnityEngine.Audio.AudioMixerGroup),0x197dda0}
        };
        internal static UnityEngine.Object? BorrowLoaded(int id,Type expected,Func<object,string?> bind,out string key)
        {
            key = "";
            if (id == 0) return null;
            if (expected != typeof(Font) && expected != typeof(Material) && expected != typeof(Texture2D))
                throw new ArgumentException("Referenced mutable Font dependency has an unsupported type.",nameof(expected));
            var pointer = ResidentLoadedAssetIndex.RegisteredPointer(id,out var registered);
            if (!IsFamily(pointer,expected)) throw new InvalidOperationException("Referenced Font dependency native type differs.");
            var handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24));
            UnityEngine.Object owner;
            if (ReferenceEquals(handle.Target,null)) {
                if (recording?.AllowResourceWrappers != true)
                    throw new InvalidOperationException("Read-only Font dependency observation cannot allocate a scripting wrapper.");
                handle.RequireUnchanged(); registered();
                owner = ResidentTargetAssetLookup.WrapReferencedAsset(pointer,id,expected);
            } else {
                owner = handle.Target as UnityEngine.Object ?? throw new InvalidOperationException("Referenced Font dependency has no Unity wrapper.");
                if (owner.GetType() != expected || !ResidentObjectDirectory.IsLive(owner) || owner.GetInstanceID() != id
                    || M.Owner(owner) != pointer || !handle.CacheMatches)
                    throw new InvalidOperationException("Referenced Font dependency wrapper identity differs.");
                handle.RequireUnchanged();
            }
            recording?.Retained.Add(pointer);


            key = bind(owner) ?? throw new InvalidOperationException("Referenced Font dependency is unbound.");
            if (key.Length == 0 || key == Missing) throw new InvalidOperationException("Referenced Font dependency has no asset identity.");
            var current = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24));
            if (!ReferenceEquals(current.Target,owner) || !current.CacheMatches || !IsFamily(pointer,expected))
                throw new InvalidOperationException("Referenced Font dependency native wrapper changed.");
            current.RequireUnchanged(); registered(); return owner;
        }
        internal static bool IsFamily(IntPtr pointer,Type expected)
        {
            if (!AssetFamilies.TryGetValue(expected,out var family)) throw new ArgumentException("unsupported native family",nameof(expected));
            var actual = NativeClass(pointer,AssetFamilies[typeof(UnityEngine.Object)]);
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var index = M.I32(M.Read(IntPtr.Add(engine,actual+48),4),0);
            var interval = M.Read(IntPtr.Add(engine,family+48),8);
            var first = M.I32(interval,0); var count = M.I32(interval,4);
            if (first < 0 || count <= 0 || (long)first+count > 1024) throw new InvalidOperationException("Native family interval is malformed.");
            if (index < first || index >= first+count) return false;
            return NativeClass(pointer,family) == actual;
        }
        internal static bool IsTemporaryRenderTexture(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return false;
            if (!IsFamily(pointer, typeof(RenderTexture))) return false;
            return M.Ptr(M.Read(IntPtr.Add(pointer, 392), 8), 0) != IntPtr.Zero;
        }
        internal static string BindId(int id,Type expected,Func<object,string?> bind)
        {
            if (id == 0) return "";
            var pointer = ResidentLoadedAssetIndex.LookupPointer(id,out var verify);
            if (pointer == IntPtr.Zero) { verify(); return ""; }
            var key = BindPointer(pointer,expected,bind); verify(); return key;
        }
        internal static string BindPointer(IntPtr pointer,Type expected,Func<object,string?> bind)
        {
            if (pointer == IntPtr.Zero || IsTemporaryRenderTexture(pointer)) return "";
            if (AssetFamilies.TryGetValue(expected,out var family)) {
                var key = Bind(pointer,family,expected,bind);
                if (key != Missing) return key;
                return "resident-unmapped-asset/" + expected.Name + "/" + pointer.ToString("x");
            }
            var id = M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
            var wrapper = ResidentLoadedAssetIndex.RequireExisting<UnityEngine.Object>(id);
            if (!expected.IsInstanceOfType(wrapper) || M.Owner(wrapper) != pointer) throw new InvalidOperationException("Component registration has another type or pointer.");
            return bind(wrapper) ?? throw new InvalidOperationException("Component is absent from the resident directory.");
        }
        internal static IntPtr ResolveTyped(string key,Type expected,Func<string,UnityEngine.Object> resolve)
        {
            if (key.Length == 0) return IntPtr.Zero;
            if (key.StartsWith("resident-unmapped-asset/", StringComparison.Ordinal)) {
                var parts = key.Split('/');
                if (parts.Length == 3 && long.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var raw))
                    return new IntPtr(raw);
            }
            if (AssetFamilies.TryGetValue(expected,out var family)) return Resolve(key,family,expected,resolve);
            var owner = resolve(key);
            if (!ResidentObjectDirectory.IsLive(owner) || !expected.IsInstanceOfType(owner)) throw new InvalidOperationException("Component binding has another type.");
            return M.Owner(owner);
        }
        internal static int ResolveId(string key,Type expected,Func<string,UnityEngine.Object> resolve)
        {
            if (key.Length == 0) return 0;
            var pointer = ResolveTyped(key,expected,resolve);
            return M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
        }
        [ThreadStatic] private static Observation? recording;
        internal sealed class Observation : IDisposable
        {
            private readonly Action admitted;
            internal readonly bool AllowResourceWrappers;
            private readonly Dictionary<string,IntPtr> pointers = new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            internal readonly HashSet<IntPtr> Retained = new HashSet<IntPtr>();

            internal Observation(Action admitted, bool allowResourceWrappers) {
                if (recording != null) throw new InvalidOperationException("Nested native asset reference observation.");
                this.admitted = admitted; AllowResourceWrappers = allowResourceWrappers; admitted();
                recording = this;
            }
            internal void Observe(string key,IntPtr pointer) {
                if (pointers.TryGetValue(key,out var old) && old != pointer) throw new InvalidOperationException("Native asset provenance identifies multiple registered objects.");
                pointers[key] = pointer;
            }
            internal ResidentObjectNativeState.GlobalOwner[] ReferencedOwners()
            {
                admitted();
                return pointers.Select(pair => {
                    var type = NativeClass(pair.Value, AssetFamilies[typeof(UnityEngine.Object)]);
                    var id = M.I32(M.Read(IntPtr.Add(pair.Value, 8), 4), 0);
                    if (ResidentLoadedAssetIndex.RegisteredPointer(id, out var verify) != pair.Value)
                        throw new InvalidOperationException("Referenced native asset registration changed.");
                    verify();
                    return new ResidentObjectNativeState.GlobalOwner(pair.Key, type.ToString("x"), pair.Value);
                }).ToArray();
            }
            public void Dispose() {
                if (!ReferenceEquals(recording,this)) throw new InvalidOperationException("Native asset observation owner changed.");
                recording = null;
            }
        }
        internal static ResidentBuiltinTextureRoots.Live CompleteBuiltins(ResidentNativeGlobalObjects.CaptureResult globals, Action admitted)
            => ResidentBuiltinTextureRoots.Capture(globals,admitted);

        internal static int NativeClass(IntPtr pointer, int family)
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine(); var engine = module.BaseAddress;
            var table = IntPtr.Add(engine,0x19d59f0);
            var count = M.U32(M.Read(table,4),0);
            var index = M.U32(M.Read(IntPtr.Add(pointer,12),4),0) >> 21;
            if (count == 0 || count > 1024 || index >= count || family <= 0 || family > module.ModuleMemorySize-80)
                throw new InvalidOperationException("Native asset type table or family differs.");
            var parent = IntPtr.Add(engine,family);
            var interval = M.Read(IntPtr.Add(parent,48),8); var first = M.I32(interval,0); var size = M.I32(interval,4);
            if (first < 0 || size <= 0 || first >= count || size > count-first || M.Ptr(IntPtr.Add(table,8+8*first)) != parent
                || index < first || index >= first+size) throw new InvalidOperationException("Native asset does not belong to the required type family.");
            var type = M.Ptr(IntPtr.Add(table,8+8*(int)index));
            var rva = type.ToInt64()-engine.ToInt64();
            if (rva <= 0 || rva > module.ModuleMemorySize-80 || M.I32(M.Read(IntPtr.Add(type,48),4),0) != index)
                throw new InvalidOperationException("Native asset type descriptor does not refer back to its table slot.");
            return (int)rva;
        }
        internal static string Bind(IntPtr pointer,int family,Type wrapperType,Func<object,string?> bind)
        {
            if (IsTemporaryRenderTexture(pointer)) return "";
            recording?.Retained.Add(pointer);
            return BindCore(pointer, family, wrapperType, bind);
        }
        private static string BindCore(IntPtr pointer,int family,Type wrapperType,Func<object,string?> bind)

        {
            recording?.Retained.Add(pointer);


            var type = NativeClass(pointer,family); var id = M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
            var registered = ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify);
            if (registered != pointer) throw new InvalidOperationException("Native asset pointer differs from its registered ID.");
            var handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24)); string key = Missing;
            if (!ReferenceEquals(handle.Target,null)) {
                if (!(handle.Target is UnityEngine.Object owner) || !wrapperType.IsInstanceOfType(owner) || !ResidentObjectDirectory.IsLive(owner) || M.Owner(owner) != pointer
                    || owner.GetInstanceID() != id || !handle.CacheMatches)
                    throw new InvalidOperationException("Issued native asset wrapper has another type or registration.");
                var bound = bind(owner);
                if (bound != null) key = bound;
                else {
                    var source = new LoadingAssetProvenanceObservation().Capture(id);
                    if (source.kind == "serialized") key = ResidentNativeAssetDirectory.ProvenanceKey(type, source.sourcePath, source.localIdentifier);
                    else key = Missing;
                    if (key != Missing) recording?.Observe(key, pointer);
                }
            } else {
                var source = new LoadingAssetProvenanceObservation().Capture(id);
                key = Missing;
                bool clipProgram = IsFamily(pointer,typeof(AnimationClip));
                bool controllerProgram = !clipProgram && IsFamily(pointer,typeof(RuntimeAnimatorController));
                if (RequireProgramWrapper(clipProgram || controllerProgram,source.kind,recording?.AllowResourceWrappers == true)) {
                    handle.RequireUnchanged(); verify();
                    var expected = clipProgram ? typeof(AnimationClip) : typeof(RuntimeAnimatorController);
                    var issued = ResidentTargetAssetLookup.WrapReferencedAsset(pointer,id,expected);
                    var bound = BindIssuedProgram(issued,wrapperType,bind);
                    verify(); return bound;
                }
                if (source.kind == "unmapped" && (type == ResidentLoadedAssetIndex.MeshClass || type == AssetFamilies[typeof(Material)]
                    || type == ResidentLoadedAssetIndex.MonoScriptClass)) {
                    // Unity may retain a generated resource through a PPtr alone.
                    // Acquire the ordinary scripting wrapper of this exact live asset so the
                    // dynamic asset adapter can capture its contents and preserve every alias.
                    if (recording?.AllowResourceWrappers != true)
                        throw new InvalidOperationException("Read-only observation found a newly unwrapped dynamic resource.");
                    handle.RequireUnchanged(); verify();
                    var expected = type == ResidentLoadedAssetIndex.MeshClass ? typeof(Mesh)
                        : type == ResidentLoadedAssetIndex.MonoScriptClass ? typeof(UnityEngine.Object) : typeof(Material);
                    var issued = ResidentTargetAssetLookup.WrapReferencedAsset(pointer, id, expected);
                    if (!wrapperType.IsInstanceOfType(issued)) throw new InvalidOperationException("Referenced asset wrapper type differs.");
                    return bind(issued) ?? Missing;
                }
                if (source.kind == "serialized") key = ResidentNativeAssetDirectory.ProvenanceKey(type,source.sourcePath,source.localIdentifier);
                if (key != Missing) recording?.Observe(key,pointer);
            }
            verify(); handle.RequireUnchanged(); return key;
        }
        // Program assets need typed manifests even when their first native
        // PPtr reference predates every scripting wrapper. Read-only passes
        // must see the wrapper issued by the prior discovery pass.
        internal static string BindIssuedProgram(object issued,Type requested,Func<object,string?> bind)
        {
            if (!requested.IsInstanceOfType(issued)) throw new InvalidOperationException("Referenced program wrapper has another requested family.");
            var key = bind(issued);
            if (key == null || !ResidentAssetBindings.IsSharedBinding(key))
                throw new InvalidOperationException("Referenced serialized program wrapper did not enter the actual asset manifest.");
            return key;
        }
        internal static bool RequireProgramWrapper(bool program,string origin,bool allowResourceWrappers)
        {
            if (!program || origin != "serialized") return false;
            if (!allowResourceWrappers) throw new InvalidOperationException("Read-only observation found a newly unwrapped animation program asset.");
            return true;
        }
        internal static string BindTexture(int texture,Func<object,string?> bind)
        {
            if(texture==0)return "";
            var pointer=ResidentLoadedAssetIndex.FindTextureResourcePointer(texture);
            if(pointer==IntPtr.Zero||IsTemporaryRenderTexture(pointer))return "";
            var slots=ResidentNativeAssetDirectory.ReadTextureResourceIds(pointer,out var actual);
            var owner=Bind(pointer,ResidentLoadedAssetIndex.TextureClass,typeof(Texture),bind);
            if(string.IsNullOrEmpty(owner)||owner==Missing)return Missing;
            var key=EncodeTextureBinding(owner,actual,slots,texture);
            if(ResidentLoadedAssetIndex.FindTextureResourcePointer(texture)!=pointer)throw new InvalidOperationException("Texture resource binding changed during observation.");
            RequireTextureSlotsUnchanged(pointer,actual,slots);
            return key;
        }
        private static void RequireTextureSlotsUnchanged(IntPtr pointer,int actual,int[] slots)
        {
            var current=ResidentNativeAssetDirectory.ReadTextureResourceIds(pointer,out var type);
            if(type!=actual||!current.SequenceEqual(slots))throw new InvalidOperationException("Texture resource type or slot aliases changed during observation.");
        }
        private const string TexturePrefix="resident-texture-resource/1/";
        private static string TexturePartition(int actual,int[] slots)
            => string.Join(".",ResidentNativeAssetDirectory.TextureResourcePartition(actual,slots).Select(value=>value<0?"x":value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        internal static string EncodeTextureBinding(string owner,int actual,int[] slots,int resource)
        {
            if(string.IsNullOrEmpty(owner)||owner==Missing||owner.Length>16384||owner.IndexOf('\0')>=0||owner.StartsWith("resident-texture-resource/",StringComparison.Ordinal))
                throw new InvalidOperationException("Texture resource has no unambiguous owner origin.");
            var mask=ResidentNativeAssetDirectory.TextureResourceMask(actual,slots,resource);
            var key=TexturePrefix+actual.ToString("x",System.Globalization.CultureInfo.InvariantCulture)+"/"+mask.ToString("x",System.Globalization.CultureInfo.InvariantCulture)+"/"+TexturePartition(actual,slots)+"/"+Convert.ToBase64String(new System.Text.UTF8Encoding(false,true).GetBytes(owner));
            if(key.Length>16384)throw new InvalidOperationException("Texture resource binding exceeds its portable field bound.");
            return key;
        }
        internal static string TextureBindingOwner(string key)
        {
            DecodeTextureBinding(key,out var owner,out _,out _,out _);return owner;
        }
        private static void DecodeTextureBinding(string key,out string owner,out int actual,out int mask,out string partition)
        {
            if(key==null||key.Length==0||key.Length>16384||key.IndexOf('\0')>=0)throw new InvalidOperationException("Texture resource binding shape differs.");
            owner=key;actual=0;mask=1;partition="";
            if(!key.StartsWith(TexturePrefix,StringComparison.Ordinal)) {
                if(key.StartsWith("resident-texture-resource/",StringComparison.Ordinal))throw new InvalidOperationException("Texture resource binding version differs.");
                return;
            }
            var parts=key.Substring(TexturePrefix.Length).Split(new[]{'/'},4);
            if(parts.Length!=4||!int.TryParse(parts[0],System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out actual)
                ||!int.TryParse(parts[1],System.Globalization.NumberStyles.AllowHexSpecifier,System.Globalization.CultureInfo.InvariantCulture,out mask)
                ||parts[0]!=actual.ToString("x",System.Globalization.CultureInfo.InvariantCulture)||parts[1]!=mask.ToString("x",System.Globalization.CultureInfo.InvariantCulture))
                throw new InvalidOperationException("Texture resource binding roles differ.");
            var count=ResidentNativeAssetDirectory.TextureResourceOffsets(actual).Length;
            partition=parts[2];var aliases=partition.Split('.');
            if(aliases.Length!=count||mask<=0||mask>=(1<<count))throw new InvalidOperationException("Texture resource binding slot count differs.");
            var roots=new int[count];var groups=0;for(var i=0;i<count;i++) {
                var text=aliases[i];var value=text=="x"?-1:text.Length==1&&text[0]>='0'&&text[0]<='2'?text[0]-'0':-2;
                if(value < -1||value>groups)throw new InvalidOperationException("Texture resource binding alias partition differs.");
                if(value==groups)groups++;
                roots[i]=value;
            }
            var first=0;while((mask&(1<<first))==0)first++;var expected=0;
            if(roots[first]<0)throw new InvalidOperationException("Texture resource binding names an absent slot.");
            for(var i=0;i<count;i++)if(roots[i]==roots[first])expected|=1<<i;
            if(mask!=expected)throw new InvalidOperationException("Texture resource binding omits an aliased slot.");
            try { owner=new System.Text.UTF8Encoding(false,true).GetString(Convert.FromBase64String(parts[3])); }
            catch(Exception ex) when(ex is FormatException||ex is System.Text.DecoderFallbackException) { throw new InvalidOperationException("Texture resource owner encoding differs.",ex); }
            if(string.IsNullOrEmpty(owner)||owner.Length>16384||owner==Missing||owner.IndexOf('\0')>=0||owner.StartsWith("resident-texture-resource/",StringComparison.Ordinal)
                ||parts[3]!=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(owner)))throw new InvalidOperationException("Texture resource owner identity differs.");
        }
        internal static int ResolveTextureRole(string key,int actual,int[] slots)
        {
            DecodeTextureBinding(key,out _,out var sourceType,out var mask,out var partition);
            var aliases=TexturePartition(actual,slots);
            if(sourceType==0) {
                if(slots[0]==0||slots.Skip(1).Any(value=>value!=0))throw new InvalidOperationException("Legacy texture binding has ambiguous additional resource slots.");
                return slots[0];
            }
            if(sourceType!=actual||partition!=aliases)throw new InvalidOperationException("Target texture resource type or full alias partition differs.");
            var first=Enumerable.Range(0,slots.Length).First(i=>(mask&(1<<i))!=0);var resource=slots[first];
            if(ResidentNativeAssetDirectory.TextureResourceMask(actual,slots,resource)!=mask)throw new InvalidOperationException("Target texture resource slot mask differs.");
            return resource;
        }
        internal static IntPtr Resolve(string key,int family,Type type,Func<string,UnityEngine.Object> resolve)
        {
            if (key.Length == 0) return IntPtr.Zero;
            if (key.StartsWith("resident-unmapped-asset/", StringComparison.Ordinal)) {
                var parts = key.Split('/');
                if (parts.Length == 3 && long.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out var raw))
                    return new IntPtr(raw);
            }
            if (key == Missing) throw new InvalidOperationException("Cannot resolve an unidentified native asset.");
            if (key.StartsWith(Prefix,StringComparison.Ordinal)) return ResidentMutationScope.RequireNativeAsset(key,family);
            var wrapper = resolve(key);
            if (!ResidentObjectDirectory.IsLive(wrapper) || !type.IsInstanceOfType(wrapper)) throw new InvalidOperationException("Resolved asset has another managed type.");
            var pointer = M.Owner(wrapper); _ = NativeClass(pointer,family);
            if (ResidentLoadedAssetIndex.RegisteredPointer(wrapper.GetInstanceID(),out var check) != pointer)
                throw new InvalidOperationException("Resolved wrapper does not own its registered asset.");
            check(); return pointer;
        }
        internal static int ResolveTexture(string key,Func<string,UnityEngine.Object> resolve)
        {
            if (key.Length == 0) return 0;
            var native = Resolve(TextureBindingOwner(key),ResidentLoadedAssetIndex.TextureClass,typeof(Texture),resolve);
            var slots=ResidentNativeAssetDirectory.ReadTextureResourceIds(native,out var actual);
            var resource = ResolveTextureRole(key,actual,slots);
            if (resource == 0 || ResidentLoadedAssetIndex.TextureResourcePointer(resource) != native)
                throw new InvalidOperationException("Target texture resource table does not map to the resolved native Object.");
            RequireTextureSlotsUnchanged(native,actual,slots);
            return resource;
        }
    }
}
