using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned Unity: lookup bfd8f0, resize 3d3e40. Hash is local evidence, never wire state.
    internal static class ResidentPhysicsCallbackMap
    {
        internal const string IdleStorageGap = "unused callback map bytes need Canon";
        internal sealed class Slot
        {
            public int Kind; // 0 live, 1 virgin empty, 2 tombstone
            public string KeyA = "", KeyB = "";
            public int Index;
            internal uint Hash;
        }
        internal sealed class Data
        {
            public int Mask, Count, Growth, Header16, AllocatorLabel;
            public Slot[] Slots = new Slot[0];
            public bool StaticEmpty;
            public string[] CoverageGaps = new string[0];
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ResizeMap(IntPtr header, int mask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeNodes(IntPtr nodes, int label, IntPtr file, int line);
        private static InvalidOperationException Gap(string s) => new InvalidOperationException("Physics callback map gap: " + s);
        internal static uint Hash(int a, int b)
        {
            unchecked {
                uint x = (uint)a * 0x1001u + 0x7ed55d16u;
                uint y = (uint)b ^ (x >> 19) ^ x ^ 0xc761c23cu;
                uint z = y * 33u + 0x165667b1u;
                return ((z - 0x2c5d9b94u) ^ (z << 9)) & 0xfffffffcu;
            }
        }
        private static int Capacity(int mask)
        {
            if (mask < 0 || (mask & 31) != 0 || mask > 32 * 262143) throw Gap("mask");
            int c = mask / 32 + 1;
            if ((c & (c - 1)) != 0) throw Gap("capacity");
            return c;
        }
        internal static void ValidateRows(Data d, Func<int, Tuple<string,string>> row)
        {
            ValidateWireCore(d, true);
            var seen = new HashSet<int>();
            foreach (var s in d.Slots.Where(s => s.Kind == 0)) {
                if (s.Index < 0 || !seen.Add(s.Index)) throw Gap("duplicate/negative collision index");
                var r = row(s.Index);
                if (r == null || r.Item1 != s.KeyA || r.Item2 != s.KeyB) throw Gap("collision row keys differ");
            }
        }
        private static void ValidateWire(Data d) => ValidateWireCore(d, false);
        private static void ValidateWireCore(Data d, bool allowIdleGap)
        {
            int c = Capacity(d.Mask);
            if (d.Slots == null || d.Slots.Length != (d.StaticEmpty ? 0 : c)) throw Gap("slot extent");
            if (d.StaticEmpty && (d.Mask != 0 || d.Count != 0)) throw Gap("static extent");
            if(d.CoverageGaps == null || d.CoverageGaps.Any(g=>!allowIdleGap || g!=IdleStorageGap) || d.CoverageGaps.Distinct().Count()!=d.CoverageGaps.Length) throw Gap("unclassified native storage");
            int live = 0;
            var pairs = new HashSet<Tuple<string,string>>();
            foreach (var s in d.Slots) {
                if (s == null || s.Kind < 0 || s.Kind > 2) throw Gap("slot kind");
                if (s.Kind != 0) {
                    if (s.KeyA != "" || s.KeyB != "" || s.Index != 0) throw Gap("dead wire payload");
                    continue;
                }
                live++;
                if (!pairs.Add(Tuple.Create(s.KeyA,s.KeyB))) throw Gap("duplicate pair");
                if (string.IsNullOrEmpty(s.KeyA) || string.IsNullOrEmpty(s.KeyB) || s.Index < 0) throw Gap("live binding");
            }
            if (live != d.Count) throw Gap("count");
            if(!d.StaticEmpty) {
                int tombs=d.Slots.Count(s=>s.Kind==2);
                int remaining=(2*c)/3-live-tombs;
                if(remaining<0 || d.Growth!=remaining || !d.Slots.Any(s=>s.Kind==1)) throw Gap("load/growth leaves unsafe lookup or insertion");
            }
            // Header16 has no proven semantic classification yet. Nonzero is not portable POD.
            if (d.Header16 != 0) throw Gap("unclassified header+16");
        }
        private static void ValidateLocal(Data d)
        {
            ValidateWireCore(d, true);
            int c = Capacity(d.Mask);
            for (int i = 0; i < d.Slots.Length; i++) {
                var s = d.Slots[i];
                if (s.Kind != 0) continue;
                if ((s.Hash & 3) != 0 || s.Hash >= 0xfffffffeu) throw Gap("hash tag");
                uint p = s.Hash & (uint)d.Mask, step = 0;
                bool found = false;
                for (int n = 0; n < c; n++) {
                    int j = (int)(p / 32);
                    if (d.Slots[j].Kind == 1) break;
                    if (j == i) { found = true; break; }
                    step += 32;
                    p = (p + step) & (uint)d.Mask;
                }
                if (!found) throw Gap("unreachable live key");
            }
        }
        private static uint ColliderHash(IntPtr a, IntPtr b)
        {
            if (a == IntPtr.Zero || b == IntPtr.Zero) throw Gap("collider absent");
            int idA=BitConverter.ToInt32(ResidentNativeMemory.Read(IntPtr.Add(a,8),4),0);
            int idB=BitConverter.ToInt32(ResidentNativeMemory.Read(IntPtr.Add(b,8),4),0);
            if(idA>idB) throw Gap("collider-ID ordering reversed");
            return Hash(idA,idB);
        }
        internal static Data Capture(IntPtr header, Func<IntPtr,string> bindCollider, Action frozen)
        {
            frozen();
            var h = ResidentNativeMemory.Read(header, 28);
            var p = ResidentNativeMemory.Ptr(h,0);
            var d = new Data {
                Mask = BitConverter.ToInt32(h,8), Count = BitConverter.ToInt32(h,12),
                Header16 = BitConverter.ToInt32(h,16), Growth = BitConverter.ToInt32(h,20),
                AllocatorLabel = BitConverter.ToInt32(h,24)
            };
            int c = Capacity(d.Mask);
            var sentinel = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0x1723b3c);
            d.StaticEmpty = p == sentinel;
            if (d.StaticEmpty) {
                if (d.Mask != 0 || d.Count != 0 || BitConverter.ToUInt32(ResidentNativeMemory.Read(p,4),0) != 0xffffffffu)
                    throw Gap("static sentinel");
            } else {
                var bytes = ResidentNativeMemory.Read(p,c*32);
                d.Slots = new Slot[c];
                for (int i=0;i<c;i++) {
                    int o=i*32;
                    uint hash = BitConverter.ToUInt32(bytes,o);
                    var s = new Slot { Hash=hash, Kind=hash==0xffffffffu ? 1 : hash==0xfffffffeu ? 2 : 0 };
                    d.Slots[i]=s;
                    // Payload of dead nodes and padding are native indeterminate storage, not values.
                    // They are canonicalized to zero by Restore, never copied as addresses.
                    if (s.Kind != 0) {
                        if(bytes.Skip(o+4).Take(28).Any(v=>v!=0)) d.CoverageGaps=new[]{IdleStorageGap};
                        continue;
                    }
                    if(bytes.Skip(o+4).Take(4).Concat(bytes.Skip(o+28).Take(4)).Any(v=>v!=0)) d.CoverageGaps=new[]{IdleStorageGap};
                    var a=ResidentNativeMemory.Ptr(bytes,o+8);
                    var b=ResidentNativeMemory.Ptr(bytes,o+16);
                    s.KeyA=bindCollider(a); s.KeyB=bindCollider(b);
                    s.Index=BitConverter.ToInt32(bytes,o+24);
                    if (ColliderHash(a,b) != hash) throw Gap("source hash");
                }
                if (!ResidentNativeMemory.Read(p,c*32).SequenceEqual(bytes)) throw Gap("map mutated");
            }
            ValidateLocal(d);
            frozen();
            if (!ResidentNativeMemory.Read(header,28).SequenceEqual(h)) throw Gap("header mutated");
            return d;
        }
        // The pinned lookup reads only tag/pair/index; resize copies only live nodes.
        // Normalize only proven unused bytes, with the shared 4-byte sentinel excluded.
        internal static void CanonicalizeIdle(IntPtr header, Func<IntPtr,string> bind, Action admitted)
        {
            var before=Capture(header,bind,admitted);
            if(before.CoverageGaps.Length==0) return;
            if(before.StaticEmpty) throw Gap("static sentinel unexpectedly needs normalization");
            admitted();
            var h=ResidentNativeMemory.Read(header,28);
            var p=ResidentNativeMemory.Ptr(h,0);
            var bytes=ResidentNativeMemory.Read(p,Capacity(before.Mask)*32);
            var normalized=(byte[])bytes.Clone();
            for(int i=0;i<before.Slots.Length;i++) {
                int o=i*32;
                if(before.Slots[i].Kind!=0) Array.Clear(normalized,o+4,28);
                else {
                    Array.Clear(normalized,o+4,4);
                    Array.Clear(normalized,o+28,4);
                }
            }
            admitted();
            if(!ResidentNativeMemory.Read(header,28).SequenceEqual(h)
                || !ResidentNativeMemory.Read(p,bytes.Length).SequenceEqual(bytes)) throw Gap("idle storage mutated before Canon");
            ResidentNativeMemory.Write(p,normalized);
            var after=Capture(header,bind,admitted);
            if(after.CoverageGaps.Length!=0) throw Gap("idle Canon readback");
            // No semantic slot or header byte may change during this operation.
            if(after.Mask!=before.Mask || after.Count!=before.Count || after.Growth!=before.Growth
                || after.Header16!=before.Header16 || after.AllocatorLabel!=before.AllocatorLabel) throw Gap("idle Canon header changed");
            for(int i=0;i<before.Slots.Length;i++) {
                var a=after.Slots[i]; var b=before.Slots[i];
                if(a.Kind!=b.Kind || a.Hash!=b.Hash || a.Index!=b.Index || a.KeyA!=b.KeyA || a.KeyB!=b.KeyB)
                    throw Gap("idle Canon live semantics changed");
            }
        }
        private static Data RebindHashes(Data d, Func<string,IntPtr> resolve)
        {
            ValidateWire(d);
            var r=new Data { Mask=d.Mask,Count=d.Count,Growth=d.Growth,Header16=d.Header16,
                AllocatorLabel=d.AllocatorLabel,StaticEmpty=d.StaticEmpty,Slots=new Slot[d.Slots.Length] };
            for(int i=0;i<d.Slots.Length;i++) {
                var s=d.Slots[i];
                var n=new Slot { Kind=s.Kind,KeyA=s.KeyA,KeyB=s.KeyB,Index=s.Index };
                r.Slots[i]=n;
                n.Hash=s.Kind==1 ? 0xffffffffu : s.Kind==2 ? 0xfffffffeu : ColliderHash(resolve(s.KeyA),resolve(s.KeyB));
            }
                        var owners=new Dictionary<IntPtr,string>();
            foreach(var s in r.Slots.Where(s=>s.Kind==0)) {
                foreach(var key in new[]{s.KeyA,s.KeyB}) {
                    var ptr=resolve(key);
                    if(owners.TryGetValue(ptr,out var old) && old!=key) throw Gap("aliased target binding");
                    owners[ptr]=key;
                }
            }
            ValidateLocal(r);
            return r;
        }
        private static void Resize(IntPtr header, int mask, Action frozen)
        {
            var engine=LoadingEngineClockProbe.RequirePinnedEngine();
            var address=IntPtr.Add(engine.BaseAddress,0x3d3e40);
            const string expected="61b34d6a1d71f269614793e38161c4606908a7342f8de36ac99dcc10a5b8d220";
            using(var sha=SHA256.Create())
            if (BitConverter.ToString(sha.ComputeHash(ResidentNativeMemory.Read(address,316))).Replace("-","").ToLowerInvariant()!=expected)
                throw Gap("resize entry differs");
            frozen();
            ((ResizeMap)Marshal.GetDelegateForFunctionPointer(address,typeof(ResizeMap)))(header,mask);
            if (BitConverter.ToInt32(ResidentNativeMemory.Read(IntPtr.Add(header,8),4),0)!=mask)
                throw Gap("resize readback");
        }
        internal static void Restore(Data d, IntPtr header, Func<string,IntPtr> resolve, Action frozen)
        {
            d = RebindHashes(d,resolve);
            frozen();
            var h=ResidentNativeMemory.Read(header,28);
            if (BitConverter.ToInt32(h,24)!=d.AllocatorLabel) throw Gap("allocator label differs");
            var p=ResidentNativeMemory.Ptr(h,0);
            var sentinel=IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0x1723b3c);
            if (d.StaticEmpty) {
                if(p!=sentinel) {
                    // resize(3d3e40) proves these nodes are POD and releases the whole block
                    // via free_alloc_internal(nodes, header.label, __FILE__, 0x3c5).
                    Capture(header,value=>value.ToInt64().ToString("x"),frozen);
                    var engine=LoadingEngineClockProbe.RequirePinnedEngine();
                    var free=IntPtr.Add(engine.BaseAddress,0x56b640);
                    const string expected="2890014de154460d79e3ec3357c76ec9af9f8198edd1243f07e27c9708f58447";
                    using(var sha=SHA256.Create())
                    if(BitConverter.ToString(sha.ComputeHash(ResidentNativeMemory.Read(free,94))).Replace("-","").ToLowerInvariant()!=expected)
                        throw Gap("node free procedure differs");
                    if(BitConverter.ToUInt32(ResidentNativeMemory.Read(sentinel,4),0)!=0xffffffffu) throw Gap("shared sentinel differs");
                    frozen();
                    if(!ResidentNativeMemory.Read(header,28).SequenceEqual(h)) throw Gap("target header mutated before release");
                    // No callbacks/iterators may execute inside the admitted frozen transaction.
                    ((FreeNodes)Marshal.GetDelegateForFunctionPointer(free,typeof(FreeNodes)))(p,d.AllocatorLabel,
                        IntPtr.Add(engine.BaseAddress,0x15c6004),0x3c5);
                    var reset=(byte[])h.Clone();
                    Array.Copy(BitConverter.GetBytes(sentinel.ToInt64()),0,reset,0,8);
                    Array.Copy(BitConverter.GetBytes(d.Mask),0,reset,8,4);
                    Array.Copy(BitConverter.GetBytes(d.Count),0,reset,12,4);
                    Array.Copy(BitConverter.GetBytes(d.Header16),0,reset,16,4);
                    Array.Copy(BitConverter.GetBytes(d.Growth),0,reset,20,4);
                    ResidentNativeMemory.Write(header,reset);
                }
                Verify(d,header,resolve,frozen);
                return;
            }
            // resize rehashes existing native keys; validate target before invoking it.
            if (p == sentinel || BitConverter.ToInt32(h,8)!=d.Mask) {
                Capture(header,value=>value.ToInt64().ToString("x"),frozen);
                if(BitConverter.ToInt32(h,12)>=Capacity(d.Mask)) throw Gap("resize cannot hold target live keys");
                Resize(header,d.Mask,frozen);
                p=ResidentNativeMemory.Ptr(ResidentNativeMemory.Read(header,8),0);
                if(p==sentinel || p==IntPtr.Zero) throw Gap("resize storage absent");
            }
            var bytes=new byte[d.Slots.Length*32];
            for(int i=0;i<d.Slots.Length;i++) {
                var s=d.Slots[i]; int o=i*32;
                Array.Copy(BitConverter.GetBytes(s.Hash),0,bytes,o,4);
                if(s.Kind!=0) continue;
                var a=resolve(s.KeyA); var b=resolve(s.KeyB);
                if(ColliderHash(a,b)!=s.Hash) throw Gap("target collider mutated");
                Array.Copy(BitConverter.GetBytes(a.ToInt64()),0,bytes,o+8,8);
                Array.Copy(BitConverter.GetBytes(b.ToInt64()),0,bytes,o+16,8);
                Array.Copy(BitConverter.GetBytes(s.Index),0,bytes,o+24,4);
            }
            frozen();
            ResidentNativeMemory.Write(p,bytes);
            ResidentNativeMemory.Write(IntPtr.Add(header,12),BitConverter.GetBytes(d.Count));
            ResidentNativeMemory.Write(IntPtr.Add(header,16),BitConverter.GetBytes(d.Header16));
            ResidentNativeMemory.Write(IntPtr.Add(header,20),BitConverter.GetBytes(d.Growth));
            Verify(d,header,resolve,frozen);
        }
        internal static void Verify(Data d, IntPtr header, Func<string,IntPtr> resolve, Action frozen)
        {
            d=RebindHashes(d,resolve);
            var keys=new Dictionary<IntPtr,string>();
            foreach(var s in d.Slots.Where(s=>s.Kind==0)) {
                var a=resolve(s.KeyA); var b=resolve(s.KeyB);
                if(keys.TryGetValue(a,out var ak) && ak!=s.KeyA || keys.TryGetValue(b,out var bk) && bk!=s.KeyB)
                    throw Gap("aliased target binding");
                keys[a]=s.KeyA; keys[b]=s.KeyB;
            }
            var actual=Capture(header,p=>keys[p],frozen);
            if(actual.Mask!=d.Mask || actual.Count!=d.Count || actual.Growth!=d.Growth || actual.Header16!=d.Header16
                || actual.AllocatorLabel!=d.AllocatorLabel || actual.StaticEmpty!=d.StaticEmpty || actual.Slots.Length!=d.Slots.Length)
                throw Gap("readback header");
            for(int i=0;i<d.Slots.Length;i++) {
                var a=actual.Slots[i]; var b=d.Slots[i];
                if(a.Kind!=b.Kind || a.Hash!=b.Hash || a.KeyA!=b.KeyA || a.KeyB!=b.KeyB || a.Index!=b.Index)
                    throw Gap("readback slot");
            }
        }
    }
}
