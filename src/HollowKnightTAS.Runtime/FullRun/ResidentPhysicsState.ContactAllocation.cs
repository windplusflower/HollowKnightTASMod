using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPhysicsState
    {
        private const int ContactSize = 312, ContactBlock = 320, ContactBucket = 9, FactoryTableRva = 0x1a2a840;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateContact(IntPtr a, int childA, IntPtr b, int childB, IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyContact(IntPtr contact, IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateBlock(IntPtr allocator, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeChunk(IntPtr buffer, int label, IntPtr file, int line);

        // b2Contact::Create's entire lazy registration prologue, pinned in
        // CheckEntry below. The sign encodes the concrete factory's primary side.
        private static readonly int[] FactoryFunctions = {
            0xbaf720,-0xbafaa0,-0xbb04b0,-0xbaf620,-0xbaf5c0,
            0xbafaa0,0,0xbafb00,0,0xbb03d0,
            0xbb04b0,-0xbafb00,0xbafbc0,-0xbaf6a0,0xbafb60,
            0xbaf620,0,0xbaf6a0,0,0xbb0430,
            0xbaf5c0,-0xbb03d0,-0xbafb60,-0xbb0430,0xbb03d0
        };
        private static byte[] FactoryImage(IntPtr engine, bool initialized)
        {
            var bytes = new byte[25 * 24 + 1];
            if (!initialized) return bytes;
            for (var i = 0; i < FactoryFunctions.Length; i++) {
                var function = FactoryFunctions[i]; if (function == 0) continue;
                Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(engine, Math.Abs(function)).ToInt64()), 0, bytes, 24 * i, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(engine, 0xbafc20).ToInt64()), 0, bytes, 24 * i + 8, 8);
                bytes[24 * i + 16] = (byte)(function > 0 ? 1 : 0);
            }
            bytes[600] = 1; return bytes;
        }
        private static bool ReadContactFactory(Local live, IntPtr engine)
        {
            var actual = live.Read(IntPtr.Add(engine, FactoryTableRva), 601);
            var hot = actual[600] == 1;
            if (!actual.SequenceEqual(FactoryImage(engine, hot))) throw Error("actual contact factory table, primary flags or initialization differs");
            return hot;
        }
        private static void CheckEntry(IntPtr engine, int rva, int size, string hash)
        {
            using var sha = SHA256.Create(); var digest = sha.ComputeHash(M.Read(IntPtr.Add(engine, rva), size));
            if (!string.Equals(BitConverter.ToString(digest).Replace("-", ""), hash, StringComparison.OrdinalIgnoreCase)) throw Error("native contact lifetime entry differs");
        }
        private static bool IsContactOwner(string owner) => owner.StartsWith("physics/contact/", StringComparison.Ordinal);
        internal static bool RequireContactBlockLayout(ResidentPhysicsGlobals.Allocator source, ResidentPhysicsGlobals.Allocator target)
        {
            var capacity = target.ChunkCapacity; var changed = target.Chunks.Length != source.Chunks.Length;
            for (var i = source.Chunks.Length; i < target.Chunks.Length; i++)
                if (target.Chunks[i].BlockSize != ContactBlock || target.Chunks[i].Owners.Any(owner => owner.Length != 0 && !IsContactOwner(owner)))
                    throw Error("retiring a contact chunk would release a foreign owner");
            for (var i = 0; i < source.Chunks.Length; i++) {
                var wanted = source.Chunks[i]; var actual = i < target.Chunks.Length ? target.Chunks[i] : null;
                if (actual == null) {
                    if (wanted.BlockSize != ContactBlock || wanted.Owners.Any(owner => owner.Length != 0 && !IsContactOwner(owner)))
                        throw Error("new block chunk is not exclusively contact/free storage");
                    if (i == capacity) capacity = checked(capacity + 128);
                    continue;
                }
                if (actual.BlockSize != wanted.BlockSize || actual.Owners.Length != wanted.Owners.Length) throw Error("non-contact block class layout differs");
                for (var j = 0; j < wanted.Owners.Length; j++) {
                    var from = actual.Owners[j]; var to = wanted.Owners[j];
                    if (from == to) continue;
                    if (wanted.BlockSize != ContactBlock || from.Length != 0 && !IsContactOwner(from) || to.Length != 0 && !IsContactOwner(to))
                        throw Error("contact migration would displace a live foreign allocation");
                    changed = true;
                }
            }
            if (capacity != source.ChunkCapacity) throw Error("target allocator capacity cannot match the contact chunk plan");
            return changed;
        }
        private static void PrepareContactNodes(Data source, Local target, Action admitted)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var sourceBodies = source.Bodies.ToDictionary(body => body.Key, StringComparer.Ordinal);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            var actualScenes = target.Globals!.Data.Scenes.ToDictionary(scene => scene.Key, StringComparer.Ordinal);
            foreach (var desired in source.Globals!.Scenes)
                if (RequireContactBlockLayout(desired.Blocks, actualScenes[desired.Key].Blocks)) changed.Add(desired.Key);
            RequireCanonicalizableCoverage(target.Data);
            if (changed.Count != 0 || source.ContactFactoryInitialized != target.Data.ContactFactoryInitialized) {
                CheckEntry(engine, 0xbaf780, 785, "a0ad4adaaaf1f6ced1fcc3a77df28f9380dd91080993d0d4176c2ecee2e8d206");
                CheckEntry(engine, 0xbafc20, 51, "a4a2f47aaafa6f758a329229bcb3478ce04cc6854458695ade685a48368e12b6");
                CheckEntry(engine, 0xba8dd0, 425, "f08b40074c9fcf3a2392fc09acc60811574a03e71b63c4d6bff1d5dcd42fda8d");
                CheckEntry(engine, 0x56b640, 94, "2890014de154460d79e3ec3357c76ec9af9f8198edd1243f07e27c9708f58447");
                if (M.Read(IntPtr.Add(engine, 0x1a2a5a0 + ContactSize), 1)[0] != ContactBucket ||
                    M.I32(M.Read(IntPtr.Add(engine, 0x1985940 + 4 * ContactBucket), 4), 0) != ContactBlock)
                    throw Error("actual block allocator contact bucket differs");
            }
            var allocate = (AllocateBlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xba8dd0), typeof(AllocateBlock));
            var create = (CreateContact)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xbaf780), typeof(CreateContact));
            var destroy = (DestroyContact)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xbafc20), typeof(DestroyContact));
            var freeChunk = (FreeChunk)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b640), typeof(FreeChunk));
            foreach (var world in source.Worlds) PrepareCallbacks(world, target, admitted);
            var factory = IntPtr.Add(engine, FactoryTableRva);
            foreach (var world in source.Worlds.Where(w => changed.Contains(w.Key))) {
                admitted();
                var original = target.Data.Worlds.Single(w => w.Key == world.Key);
                var blocks = source.Globals.Scenes.Single(scene => scene.Key == world.Key).Blocks;
                var allocator = target.Worlds[world.Key]; var state = IntPtr.Add(allocator, WorldStateOffset);
                var head = IntPtr.Add(allocator, 24 + 8 * ContactBucket);
                // No contact listener, wake, Step, callback or pair generation.
                // Unpublish the isolated graph before retiring its native nodes.
                WritePointer(IntPtr.Add(state, 128), IntPtr.Zero);
                M.Write(IntPtr.Add(state, 176), BitConverter.GetBytes(0L)); M.Write(IntPtr.Add(state, 208), BitConverter.GetBytes(0L));
                foreach (var body in original.BodyOrder) WritePointer(IntPtr.Add(target.Bodies[body], 136), IntPtr.Zero);
                foreach (var c in original.Contacts) {
                    admitted(); var ptr = target.Contacts[c.Key]; var vtable = M.Ptr(M.Read(ptr, 8), 0);
                    if (vtable != IntPtr.Add(engine, c.Vtable) || M.Ptr(M.Read(IntPtr.Add(vtable, 8), 8), 0) != IntPtr.Add(engine, 0xbaf590))
                        throw Error("contact destructor dispatch differs");
                    destroy(ptr, allocator); target.Contacts.Remove(c.Key); target.ContactNames.Remove(ptr);
                }
                var before = actualScenes[world.Key].Blocks;
                if (before.Chunks.Length > blocks.Chunks.Length) {
                    // Every occupied cell in these trailing chunks was one of
                    // the isolated nodes just destroyed. Drop their only other
                    // consumer (the bucket free list) before the original
                    // b2BlockAllocator destructor's labeled free, RVA ba8d80.
                    WritePointer(head, IntPtr.Zero);
                    var dir = M.Read(allocator, 20); var directoryPtr = M.Ptr(dir, 0);
                    var label = Int(dir, 16);
                    for (var i = before.Chunks.Length - 1; i >= blocks.Chunks.Length; i--) {
                        admitted(); var slot = IntPtr.Add(directoryPtr, checked(i * 16)); var row = M.Read(slot, 16);
                        var chunk = M.Ptr(row, 8);
                        if (Int(row, 0) != ContactBlock || chunk != target.Globals.Cells[world.Key + "/chunk[" + i + "]/cell[0]"])
                            throw Error("retired native block chunk changed owner");
                        M.Write(IntPtr.Add(allocator, 8), BitConverter.GetBytes(i));
                        M.Write(slot, new byte[16]);
                        freeChunk(chunk, label, IntPtr.Add(engine, 0x15c6004), 0x26);
                    }
                }
                for (var i = actualScenes[world.Key].Blocks.Chunks.Length; i < blocks.Chunks.Length; i++) {
                    admitted();
                    // Stock Allocate(312) with an empty class head grows exactly
                    // one 16-KiB, allocator-owned chunk. Its first cell is reserved
                    // here and rejoins the checked partition immediately below.
                    WritePointer(head, IntPtr.Zero);
                    var first = allocate(allocator, ContactSize); var header = M.Read(allocator, 16);
                    var chunk = M.Read(IntPtr.Add(M.Ptr(header, 0), 16 * i), 16);
                    if (first == IntPtr.Zero || first != M.Ptr(chunk, 8) || Int(header, 8) != i+1 || Int(chunk, 0) != ContactBlock)
                        throw Error("native allocator did not create the requested contact chunk");
                }
                var directory = M.Read(allocator, 16);
                var chunks = M.Read(M.Ptr(directory, 0), checked(16 * blocks.Chunks.Length));
                var available = new List<IntPtr>(); var wanted = new List<KeyValuePair<ContactData, IntPtr>>();
                var sourceContacts = world.Contacts.ToDictionary(c => c.Key, StringComparer.Ordinal);
                var free = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
                for (var i = 0; i < blocks.Chunks.Length; i++) {
                    if (blocks.Chunks[i].BlockSize != ContactBlock) continue;
                    if (Int(chunks, i * 16) != ContactBlock) throw Error("native contact chunk type changed");
                    var chunk = M.Ptr(chunks, i*16+8);
                    for (var j = 0; j < blocks.Chunks[i].Owners.Length; j++) {
                        var name = blocks.Chunks[i].Owners[j]; var ptr = IntPtr.Add(chunk, j*ContactBlock);
                        if (IsContactOwner(name)) { wanted.Add(new KeyValuePair<ContactData, IntPtr>(sourceContacts[name], ptr)); available.Add(ptr); }
                        else if (name.Length == 0) free.Add(world.Key + "/chunk[" + i + "]/cell[" + j + "]", ptr);
                    }
                }
                // Source's complete occupied/free partition was validated before
                // mutation. Retain foreign cells and reconstruct only this bucket.
                var nfree = 0;
                foreach (var key in blocks.Free[ContactBucket]) {
                    if ((nfree++ & 31) == 0) admitted();
                    var ptr = free[key]; M.Write(IntPtr.Add(ptr, 8), new byte[ContactBlock - 8]); available.Add(ptr);
                }
                for (var i = 0; i < available.Count; i++) {
                    if ((i & 31) == 0) admitted();
                    WritePointer(available[i], i+1 == available.Count ? IntPtr.Zero : available[i+1]);
                }
                WritePointer(head, available.Count == 0 ? IntPtr.Zero : available[0]);
                foreach (var pair in wanted) {
                    admitted(); var c = pair.Key;
                    var ptr = create(target.Fixtures[c.FixtureA], c.ChildA, target.Fixtures[c.FixtureB], c.ChildB, allocator);
                    if (ptr != pair.Value || M.Ptr(M.Read(ptr, 8), 0) != IntPtr.Add(engine, c.Vtable)) throw Error("contact factory returned the wrong cell or native type");
                    Bind(target.Contacts, target.ContactNames, c.Key, ptr);
                }
            }
            foreach (var world in source.Worlds) {
                var state = IntPtr.Add(target.Worlds[world.Key], WorldStateOffset);
                ApplyContacts(world, sourceBodies, target, state, admitted);
                WriteCallbackRows(world.Callbacks, target, target.CollisionRows[world.Key], admitted);
            }
            // If the source never constructed a contact, its entire dispatch
            // table is still zero. Otherwise its exact 25 fixed registrations
            // are known, even when all its former world contacts have ended.
            admitted(); var desiredFactory = FactoryImage(engine, source.ContactFactoryInitialized);
            if (!M.Read(factory, 601).SequenceEqual(desiredFactory)) M.Write(factory, desiredFactory);
            if (!M.Read(factory, 601).SequenceEqual(desiredFactory)) throw Error("contact factory initialization did not read back");
        }
    }
}
