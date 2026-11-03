using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        // PDB field declarations + actual native RTTI ancestry. Only these
        // native field kinds implement UserListBase's reciprocal slot ABI.
        // Actual owners and edges are discovered from each admitted Animator.
        private static readonly int[] AnimatorUserOffsets = {1264,1288,1328,1368};
        private static readonly Dictionary<int,int[]> UserOffsets = new Dictionary<int,int[]> {
            [0x194b970] = new[] {88}, [0x194b410] = AnimatorUserOffsets, [0x194ba30] = new[] {56,96},
            [0x194bcf0] = new[] {296}, [0x194b860] = new[] {120}, [0x194ba80] = new[] {232}, [0x194c6a0] = new[] {200}
        };
        private static bool SingleUserSlot(int rtti,int offset) => rtti == 0x194b410 && offset == 1264 || rtti == 0x194ba80 && offset == 232;
        private static string UserKey(string owner,int offset) => owner + "/native-user-list/" + offset.ToString(CultureInfo.InvariantCulture);
        internal sealed class UserSlot
        {
            public string Owner = "";
            public int DeclaringRtti, Offset;
            public bool EntryPadding;
            public Storage Storage = new Storage { Label = 74 };
            public UserEntry[] Entries = Array.Empty<UserEntry>();
            internal bool Single => SingleUserSlot(DeclaringRtti,Offset);
            internal string Key => UserKey(Owner,Offset);
        }
        internal sealed class UserEntry { public string Peer = ""; public int Reverse = -1; }
        private sealed class UsersView
        {
            internal readonly UserSlot[] Rows;
            private readonly Dictionary<string,IntPtr> pointers = new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            internal UsersView(Reader reader)
            {
                var values = new Dictionary<IntPtr,UserSlot>(); var pending = new Queue<IntPtr>();
                void Add(IntPtr pointer,UserSlot slot) {
                    if (values.ContainsKey(pointer)) return;
                    if (values.Count == MaximumEntries || pointers.ContainsKey(slot.Key)) throw Error("native user-list field has duplicated logical identity");
                    values.Add(pointer,slot); pointers.Add(slot.Key,pointer); pending.Enqueue(pointer);
                }
                foreach (var row in reader.Live.Data.Rows) {
                    var owner = M.Owner(reader.DirectoryOwner(row.Owner));
                    foreach (var offset in AnimatorUserOffsets)
                        Add(IntPtr.Add(owner,offset),new UserSlot {Owner=row.Owner,DeclaringRtti=0x194b410,Offset=offset});
                }
                string Peer(IntPtr address) {
                    if (address == IntPtr.Zero) return "";
                    if (values.TryGetValue(address,out var known)) return known.Key;
                    var owner = M.Ptr(reader.Memory.Read(address,8),0);
                    if (owner == IntPtr.Zero) return Unbound;
                    long displacement = address.ToInt64() - owner.ToInt64();
                    if (displacement < 0 || displacement > int.MaxValue || !reader.Registered(owner)) return Unbound;
                    int offset = (int)displacement;
                    var vt = M.Ptr(reader.Memory.Read(owner,8),0);
                    if (!reader.InEngine(vt,72)) return Unbound;
                    var getter = M.Ptr(reader.Memory.Read(IntPtr.Add(vt,64),8),0);
                    if (!reader.InEngine(getter,8)) return Unbound;
                    var code = reader.Memory.Read(getter,8);
                    if (code[0] != 0x48 || code[1] != 0x8d || code[2] != 5 || code[7] != 0xc3) return Unbound;
                    var type = IntPtr.Add(IntPtr.Add(getter,7),M.I32(code,3)); var seen = new HashSet<IntPtr>();
                    while (type != IntPtr.Zero) {
                        if (!seen.Add(type) || !reader.InEngine(type,80)) throw Error("invalid native user-list RTTI ancestry");
                        int rva = checked((int)(type.ToInt64() - reader.Engine.ToInt64()));
                        if (UserOffsets.TryGetValue(rva,out var fields) && fields.Contains(offset)) {
                            var binding = reader.Key<NativeObject>(owner); if (binding == Unbound) return Unbound;
                            var slot = new UserSlot {Owner=binding,DeclaringRtti=rva,Offset=offset}; Add(address,slot); return slot.Key;
                        }
                        type = M.Ptr(reader.Memory.Read(type,8),0);
                    }
                    return Unbound;
                }
                while (pending.Count != 0) {
                    var pointer = pending.Dequeue(); var slot = values[pointer]; var bytes = reader.Memory.Read(pointer,slot.Single ? 24 : 40);
                    if (M.Ptr(bytes,0) != IntPtr.Subtract(pointer,slot.Offset)) throw Error("native user-list target differs from its actual typed owner");
                    if (slot.Single) {
                        slot.EntryPadding=M.U32(bytes,20)!=0;
                        var entry = new UserEntry {Peer=Peer(M.Ptr(bytes,8)),Reverse=M.I32(bytes,16)};
                        if (entry.Peer.Length != 0 || entry.Reverse != -1) slot.Entries = new[] {entry};
                    } else {
                        var entries = reader.ArrayBytes(bytes,8,16);
                        slot.Storage = StorageAt(bytes,8);
                        for (int i=12;i<entries.Length;i+=16) if (M.U32(entries,i)!=0) slot.EntryPadding=true;
                        slot.Entries = Decode(entries,16,(array,i) => new UserEntry {Peer=Peer(M.Ptr(array,i)),Reverse=M.I32(array,i+8)});
                    }
                }
                Rows = values.Values.OrderBy(s => s.Key,StringComparer.Ordinal).ToArray();
            }
            internal void Canon(Action admitted,Action<string> requireOwner)
            {
                foreach (var slot in Rows) {
                    var pointer=pointers[slot.Key];
                    if(slot.EntryPadding || !slot.Single && !Compact(slot.Storage,slot.Entries.Length)) requireOwner(slot.Owner);
                    if (!slot.Single && !Compact(slot.Storage,slot.Entries.Length)) {
                        ResizePod(IntPtr.Add(pointer,8),slot.Entries.Length,16,8,slot.Storage.Label,admitted);SetCompact(slot.Storage,slot.Entries.Length);
                    }
                    if (slot.EntryPadding) {
                        var first=slot.Single ? IntPtr.Add(pointer,8) : M.Ptr(IntPtr.Add(pointer,8));
                        for (int i=0;i<(slot.Single?1:slot.Entries.Length);i++) M.Write(IntPtr.Add(first,16*i+12),new byte[4]);
                        slot.EntryPadding=false;
                    }
                }
            }
            internal Action Prepare(UserSlot[] desired, Action admitted, Action<string> requireOwner)
            {
                // Every peer is a typed owner field; subslot indices come
                // from the source and have already been verified in BOTH
                // directions. Reorder any existing equal-sized lists.
                if (!Rows.Select(r => r.Key).SequenceEqual(desired.Select(r => r.Key))) throw Error("target native user-list field closure differs");
                var changes=new List<Action>();
                for (int i = 0; i < Rows.Length; i++) {
                    var source = desired[i]; var target = Rows[i]; var pointer = pointers[target.Key];
                    if (source.DeclaringRtti != target.DeclaringRtti || source.Storage.Label!=target.Storage.Label || source.EntryPadding || target.EntryPadding)
                        throw Error("target native user-list type/allocator differs");
                    if (Json(source)==Json(target)) continue;
                    requireOwner(source.Owner);
                    int count = source.Single ? 1 : source.Entries.Length;
                    var entries=new byte[checked(count*16)];
                    for (int j = 0; j < count; j++) {
                        var entry = j < source.Entries.Length ? source.Entries[j] : new UserEntry();
                        var other = entry.Peer.Length == 0 ? IntPtr.Zero : pointers[entry.Peer];
                        BitConverter.GetBytes(other.ToInt64()).CopyTo(entries,j*16);BitConverter.GetBytes(entry.Reverse).CopyTo(entries,j*16+8);
                    }
                    changes.Add(()=> {
                        admitted();
                        if (!source.Single && source.Entries.Length!=target.Entries.Length) ResizePod(IntPtr.Add(pointer,8),count,16,8,source.Storage.Label,admitted);
                        var first=source.Single ? IntPtr.Add(pointer,8) : M.Ptr(IntPtr.Add(pointer,8));M.Write(first,entries);admitted();
                    });
                }
                return ()=> {foreach(var change in changes) change();};
            }
        }
        private static Storage StorageAt(byte[] bytes,int at)
        {
            var cap = M.U64(bytes,at+24);
            if (cap >> 1 > MaximumEntries) throw Error("native container capacity exceeded");
            return new Storage { Label=M.I32(bytes,at+8), Capacity=(int)(cap>>1), Allocated=M.Ptr(bytes,at)!=IntPtr.Zero, Borrowed=(cap&1)!=0, Padding=M.U32(bytes,at+12)!=0 };
        }
        private static void ValidateUsers(UserSlot[] slots)
        {
            if (slots == null || slots.Length > MaximumEntries) throw Error("native user-list directory differs");
            var rows = new Dictionary<string,UserSlot>(StringComparer.Ordinal); string? previous = null;
            foreach (var slot in slots) {
                if (slot == null || !Text(slot.Owner) || slot.Owner.Length == 0 || slot.Owner == Unbound || !UserOffsets.TryGetValue(slot.DeclaringRtti,out var fields)
                    || !fields.Contains(slot.Offset) || StringComparer.Ordinal.Compare(previous,slot.Key) >= 0 || slot.Entries == null || slot.Entries.Length > MaximumEntries
                    || slot.Storage == null || slot.Single && slot.Entries.Length > 1 || !slot.Single && (slot.Storage.Capacity < slot.Entries.Length
                    || slot.Storage.Capacity > MaximumEntries || slot.Storage.Capacity != 0 && !slot.Storage.Allocated)) throw Error("native user-list schema differs");
                previous = slot.Key; rows.Add(slot.Key,slot);
            }
            foreach (var slot in slots) for (int i = 0; i < slot.Entries.Length; i++) {
                var entry = slot.Entries[i];
                if (entry == null || !Text(entry.Peer) || entry.Peer.Length == 0 || entry.Reverse < -1) throw Error("native user-list edge absent");
                if (entry.Peer == Unbound) continue;
                if (!rows.TryGetValue(entry.Peer,out var peer) || peer.Single != (entry.Reverse == -1) || peer.Single && slot.Single)
                    throw Error("native user-list edge has wrong peer kind");
                int index = peer.Single ? 0 : entry.Reverse;
                if (index >= peer.Entries.Length || peer.Entries[index] == null || peer.Entries[index].Peer != slot.Key
                    || peer.Entries[index].Reverse != (slot.Single ? -1 : i)) throw Error("native user-list index does not have an exact reciprocal field/slot");
            }
        }
    }
}
