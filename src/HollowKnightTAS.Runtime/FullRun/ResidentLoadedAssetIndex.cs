using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1, core::hash_map<int,Object*>::find
    // RVA 87eb0. Do not call FindObjectFromInstanceID: its ScriptingWrapperFor
    // creates a wrapper when the native Object has none. Only immutable code
    // proofs/delegates are cached; every query witnesses the actual map, probe chain, owner and borrowed handle.
    internal static class ResidentLoadedAssetIndex
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool MainThread();
        private static readonly Dictionary<int, byte[]> verifiedCode = new Dictionary<int, byte[]>();
        private static readonly object codeGate = new object();
        private static IntPtr verifiedBase;
        private static MainThread? mainThread;
        private static IntPtr Engine()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            RequireMainThread(engine);
            return engine;
        }
        private static void RequireMainThread(IntPtr engine)
        {
            RequireCode(engine, 0x81c590, 30, "730b0ebf0c4d9a6bec6ff34df66477641fc299b8a008a553b195d5283b05556d");
            var thread = mainThread;
            if (thread == null) mainThread = thread = (MainThread)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x81c590), typeof(MainThread));
            if (!thread())
                throw Error("asset index requires admitted native main thread");
        }
        internal static void RequireCode(IntPtr engine, int rva, int size, string hash)
        {
            var current = M.Read(IntPtr.Add(engine, rva), size);
            lock (codeGate) {
                if (verifiedBase != IntPtr.Zero && verifiedBase != engine) throw Error("native lookup module changed");
                if (verifiedCode.TryGetValue(rva, out var expected)) {
                    if (!expected.SequenceEqual(current)) throw Error("native lookup ABI changed");
                    return;
                }
                using (var sha = SHA256.Create()) if (BitConverter.ToString(sha.ComputeHash(current)).Replace("-", "").ToLowerInvariant() != hash)
                    throw Error("native lookup ABI differs");
                verifiedBase = engine; verifiedCode.Add(rva, current);
            }
        }
        internal const int MeshClass = 0x194cd70, SpriteRendererClass = 0x1949510, CanvasClass = 0x194ae70, CanvasRendererClass = 0x194d880;
        internal const int TextureClass = 0x194ce60, RenderTextureClass = 0x194c6f0, MonoScriptClass = 0x194b370;
        internal sealed class Member
        {
            internal IntPtr Pointer;
            internal int Id, TypeRva, Index;
            internal T Borrow<T>() where T : NativeObject {
                var owner = RequireExisting<T>(Id);
                return M.Owner(owner) == Pointer ? owner : throw Error("borrowed family member differs from the registered owner");
            }
        }
        internal static Member[] Family(int typeRva, out Action requireUnchanged)
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine(); var engine = module.BaseAddress;
            RequireMainThread(engine);
            var witness = new List<Tuple<IntPtr,byte[]>>();
            byte[] Read(IntPtr addr, int count) { var bytes = M.Read(addr,count); witness.Add(Tuple.Create(addr,bytes)); return bytes; }
            var types = Read(IntPtr.Add(engine,0x19d59f0),8200); var count = M.U32(types,0);
            if (count == 0 || count > 1024 || typeRva <= 0 || typeRva > module.ModuleMemorySize-80) throw Error("native type table bound differs");
            var basePtr = IntPtr.Add(engine,typeRva); var interval = Read(IntPtr.Add(basePtr,48),8);
            var first = M.I32(interval,0); var descendants = M.I32(interval,4);
            if (first < 0 || first >= count || descendants < 1 || descendants > count-first || M.Ptr(types,8+8*first)!=basePtr)
                throw Error("native type family interval differs");
            var sets = M.Ptr(Read(IntPtr.Add(engine,0x19d7bf0),8),0); if (sets == IntPtr.Zero) throw Error("native object sets absent");
            var result = new List<Member>(); var ids = new HashSet<int>(); var addresses = new HashSet<IntPtr>();
            for (int index = first; index < first+descendants; index++) {
                var t = M.Ptr(types,8+index*8); var rva = t.ToInt64()-engine.ToInt64();
                if (rva <= 0 || rva > module.ModuleMemorySize-80 || M.I32(Read(IntPtr.Add(t,48),4),0)!=index) throw Error("registered native type index differs");
                var header = Read(IntPtr.Add(sets,index*32),32);
                foreach (var row in ResidentNativeGlobalObjects.DecodeHashRows(header,Read(M.Ptr(header,0),ResidentNativeGlobalObjects.HashByteLength(header,false)),false)) {
                    var owner = Read(row.Pointer,16); var id = M.I32(owner,8);
                    if (id == 0 || (M.U32(owner,12)>>21)!=index || !ids.Add(id) || !addresses.Add(row.Pointer) || result.Count >= ResidentObjectDirectory.MaximumComponents)
                        throw Error("native type set contains a different or duplicate member");
                    result.Add(new Member { Pointer=row.Pointer, Id=id, TypeRva=(int)rva, Index=index });
                }
            }
            requireUnchanged = () => { foreach (var item in witness) if (!item.Item2.SequenceEqual(M.Read(item.Item1,item.Item2.Length))) throw Error("native type membership changed"); };
            requireUnchanged(); return result.OrderBy(value=>value.Id).ToArray();
        }
        // TextureID is a separate resource identity. The actual registry is an
        // unsigned-key std::map, not Object::ms_IDToPointer. Borrow its value,
        // then cross-check it through the Object index and existing GCHandle.
        internal static IntPtr TextureResourcePointer(int id)
        {
            var pointer = FindTextureResourcePointer(id);
            return pointer != IntPtr.Zero ? pointer : throw Error("texture resource absent from native registry");
        }
        // Resource references must resolve through the strict entry above.
        // Inventory also records IDs without a registration. A proven miss
        // remains distinct from a damaged tree or changed native owner.
        internal static IntPtr FindTextureResourcePointer(int id)
        {
            if (id == 0) throw Error("zero texture resource identity");
            var engine = Engine();
            RequireCode(engine, 0x64aeb0, 243, "71e9c38ff229b3d2b198f0928563d52361f7ae0092dd0bb7ca62d37818d187d6");
            var witness = new Dictionary<IntPtr, byte[]>();
            byte[] Read(IntPtr address, int size) { var bytes = M.Read(address, size); witness[address] = bytes; return bytes; }
            var map = M.Ptr(Read(IntPtr.Add(engine, 0x1a16440), 8), 0);
            if (map == IntPtr.Zero) throw Error("texture resource registry absent");
            var found = FindTextureResourceInTree(id,Read(map,16),Read,(pointer,resource) => {
                var registered=RegisteredPointer(M.I32(Read(IntPtr.Add(pointer,8),4),0),out var verify);
                if(registered!=pointer)throw Error("texture resource Object registration differs");
                var slots=ResidentNativeAssetDirectory.ReadTextureResourceIds(pointer,out var actual);
                _=ResidentNativeAssetDirectory.TextureResourceMask(actual,slots,resource);
                verify();
            });
            foreach (var pair in witness) if (!M.Read(pair.Key, pair.Value.Length).SequenceEqual(pair.Value)) throw Error("texture resource lookup changed during observation");
            return found;
        }
        // Injectable byte reader exercises the exact native lookup invariants
        // offline without constructing a Unity object or calling its API.
        internal static IntPtr FindTextureResourceInTree(int id,byte[] header,Func<IntPtr,int,byte[]> read,Action<IntPtr,int> validateOwner)
        {
            if(id==0||header==null||header.Length!=16)throw Error("texture resource lookup shape differs");
            var head = M.Ptr(header, 0); var count = M.U64(header, 8);
            if (count > 100000 || head == IntPtr.Zero) throw Error("texture resource registry shape differs");
            var sentinel = read(head, 48); if (sentinel.Length!=48||sentinel[25] != 1||sentinel[24]!=1) throw Error("texture resource sentinel differs");
            var current = M.Ptr(sentinel, 8); var seen = new HashSet<IntPtr>(); var found = IntPtr.Zero;
            if((count==0)!=(current==head)||current==IntPtr.Zero||count==0&&(M.Ptr(sentinel,0)!=head||M.Ptr(sentinel,16)!=head))throw Error("texture resource root/count differs");
            var parent=head;ulong lower=0,upper=1UL<<32;
            while (current != head) {
                if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > 128 || (ulong)seen.Count > count) throw Error("texture resource tree is invalid");
                var node = read(current, 48); if (node.Length!=48||node[25] != 0||node[24]>1||M.Ptr(node,8)!=parent) throw Error("texture resource node differs");
                var key = M.U32(node, 32);
                if(key<lower||key>=upper)throw Error("texture resource tree key order differs");
                if (key == unchecked((uint)id)) {
                    var pointer = M.Ptr(node, 40); if (pointer == IntPtr.Zero) throw Error("texture resource has no native owner");
                    validateOwner(pointer,id);found=pointer;
                    break;
                }
                parent=current;
                if(unchecked((uint)id)<key){upper=key;current=M.Ptr(node,0);}else{lower=(ulong)key+1;current=M.Ptr(node,16);}
            }
            return found;
        }
        // Native per-type set, not the managed Resources enumeration. This
        // validates ownership even for an owner with no managed wrapper.
        internal static void RequireUniqueOwnerSlot(NativeObject expected, int slotOffset, IntPtr owned)
        {
            var engine = Engine(); var witness = new Dictionary<IntPtr, byte[]>();
            byte[] Read(IntPtr address, int size) { var bytes = M.Read(address, size); witness[address] = bytes; return bytes; }
            var expectedPointer = M.Owner(expected); var ownerHeader = Read(expectedPointer, 16);
            var type = M.U32(ownerHeader, 12) >> 21;
            var typeCount = M.U32(Read(IntPtr.Add(engine, 0x19d59f0), 4), 0);
            if (typeCount == 0 || typeCount > 1024 || type >= typeCount || slotOffset < 48) throw Error("native owner type/slot differs");
            var sets = M.Ptr(Read(IntPtr.Add(engine, 0x19d7bf0), 8), 0);
            if (sets == IntPtr.Zero) throw Error("native Object type registry absent");
            var header = Read(IntPtr.Add(sets, checked((int)type * 32)), 32);
            if (M.Ptr(header, 0) == IntPtr.Zero) throw Error("native Object type-set storage absent");
            var rows = ResidentNativeGlobalObjects.DecodeHashRows(header, Read(M.Ptr(header, 0), ResidentNativeGlobalObjects.HashByteLength(header, false)), false);
            var matches = 0; var expectedFound = false;
            foreach (var row in rows) {
                if (M.U32(Read(IntPtr.Add(row.Pointer, 12), 4), 0) >> 21 != type) throw Error("native type set contains a different actual type");
                if (M.Ptr(Read(IntPtr.Add(row.Pointer, slotOffset), 8), 0) != owned) continue;
                matches++; expectedFound |= row.Pointer == expectedPointer;
            }
            foreach (var pair in witness) if (!M.Read(pair.Key, pair.Value.Length).SequenceEqual(pair.Value)) throw Error("native owned-asset slots changed during observation");
            if (matches != 1 || !expectedFound) throw Error("native asset slot has no unique actual-type owner");
        }
        internal static uint Hash(int id)
        {
            unchecked {
                var a = (uint)id * 0x1001U + 0x7ed55d16U;
                var b = (a >> 19) ^ a ^ 0xc761c23cU;
                var c = b * 0x21U + 0x165667b1U;
                var d = (c - 0x2c5d9b94U) ^ (c << 9);
                var e = d * 9U + 0xfd7046c5U;
                return (e >> 16) ^ e ^ 0xb55a4f09U;
            }
        }
        // Borrow a registered Object* without touching its managed handle. The
        // returned check re-reads the exact ID table probe and native owner ID.
        internal static IntPtr RegisteredPointer(int id, out Action recheck)
        {
            var pointer = LookupPointer(id,out recheck);
            return pointer != IntPtr.Zero ? pointer : throw Error("native Object registration is absent");
        }
        internal static IntPtr LookupPointer(int id, out Action recheck)
        {
            if (id == 0) throw Error("zero is a null identity, not a registered Object");
            var engine = Engine();
            RequireCode(engine, 0x87eb0, 273, "c4da46504f73785ec5b8e10c5b3420ec553fb96cf7a4657e3364c8be6672506d");
            var witnesses = new Dictionary<IntPtr, byte[]>();
            byte[] Read(IntPtr address, int size) { var bytes = M.Read(address, size); witnesses[address] = bytes; return bytes; }
            var map = M.Ptr(Read(IntPtr.Add(engine, 0x19d7be8), 8), 0);
            if (map == IntPtr.Zero) throw Error("native Object ID registry is absent");
            var header = Read(map, 32);
            var nodes = M.Ptr(header, 0); if (nodes == IntPtr.Zero) throw Error("native Object hash storage absent");
            var pointer = FindIdPointer(header, id, offset => Read(IntPtr.Add(nodes, offset), 24));
            if (pointer != IntPtr.Zero && M.I32(Read(IntPtr.Add(pointer, 8), 4), 0) != id)
                throw Error("native Object registration does not own this ID");
            recheck = () => { foreach (var item in witnesses) if (!item.Value.SequenceEqual(M.Read(item.Key, item.Value.Length)))
                throw Error("native Object ID registration changed during lookup"); };
            recheck(); return pointer;
        }
        // Absence, including a registered Object with no issued wrapper, is
        // witnessed. An unresolved nonzero PPtr remains a coverage gap.
        internal static NativeObject? BorrowOptional(int id,out Action recheck)
        {
            var pointer = LookupPointer(id,out var checkRegistration);
            if (pointer == IntPtr.Zero) { recheck = checkRegistration; return null; }
            var handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer,24));
            recheck = () => { checkRegistration(); handle.RequireUnchanged(); };
            recheck();
            if (handle.Target == null) return null;
            if (!(handle.Target is NativeObject result) || !ResidentObjectDirectory.IsLive(result) || M.Owner(result) != pointer)
                throw Error("issued wrapper has inconsistent registration or native owner");
            return result;
        }
        internal static T RequireExisting<T>(int id) where T : NativeObject
        {
            var pointer = RegisteredPointer(id, out var recheck);
            var handle = ResidentScriptingHandles.Read(IntPtr.Add(pointer, 24));
            if (!(handle.Target is T result) || !ResidentObjectDirectory.IsLive(result) || M.Owner(result) != pointer)
                throw Error("registered native Object has no existing managed wrapper of requested type; observation cannot create one (ID " + id + ")");
            handle.RequireUnchanged(); recheck(); return result;
        }
        internal static IntPtr FindIdPointer(byte[] header, int id, Func<int, byte[]> readNode)
        {
            var length = ResidentNativeGlobalObjects.HashByteLength(header, true);
            if (id == 0) throw Error("zero Object identity");
            var mask = M.U32(header, 8); var hash = Hash(id); var bucket = hash & mask;
            for (uint step = 8, probes = 0; probes < length / 24; probes++, step += 8) {
                var node = readNode(checked((int)((ulong)bucket * 3)));
                if (node == null || node.Length != 24) throw Error("native Object hash node shape differs");
                var stored = M.U32(node, 0);
                if (stored == uint.MaxValue) return IntPtr.Zero;
                if (stored == (hash & 0xfffffffcU) && M.I32(node, 8) == id) {
                    var pointer = M.Ptr(node, 16);
                    if (pointer == IntPtr.Zero) throw Error("live native Object hash node has null owner");
                    return pointer;
                }
                bucket = unchecked(bucket + step) & mask;
            }
            // A corrupt/full table cannot become an unbounded native probe.
            throw Error("native Object hash probe exhausted its capacity");
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("ResidentLoadedAssetIndex: " + reason);
    }
}
