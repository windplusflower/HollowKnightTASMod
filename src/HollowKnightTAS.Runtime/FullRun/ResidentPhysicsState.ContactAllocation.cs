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
                        throw Error($"contact migration would displace a live foreign allocation: chunk={i} slot={j} blockSize={wanted.BlockSize} from='{from}' to='{to}'");
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

        private static int ShapeNativeSize(int shapeType) => shapeType switch {
            0 => 24, 1 => 56, 2 => 160, 3 => 48, 4 => 32,
            _ => throw Error("unknown Box2D shape type: " + shapeType)
        };

        private static bool NeedsBlockAllocatorReconstruction(ResidentPhysicsGlobals.Allocator source, ResidentPhysicsGlobals.Allocator target)
        {
            if (source.ChunkCapacity != target.ChunkCapacity || source.Chunks.Length != target.Chunks.Length) return true;
            for (var i = 0; i < source.Chunks.Length; i++) {
                var a = source.Chunks[i]; var b = target.Chunks[i];
                if (a.BlockSize != b.BlockSize || a.Owners.Length != b.Owners.Length) return true;
                for (var j = 0; j < a.Owners.Length; j++) {
                    if (a.Owners[j] == b.Owners[j]) continue;
                    if (a.BlockSize != ContactBlock || (a.Owners[j].Length != 0 && !IsContactOwner(a.Owners[j]))
                        || (b.Owners[j].Length != 0 && !IsContactOwner(b.Owners[j]))) return true;
                }
            }
            return false;
        }

        private static void ReconstructBlockAllocators(Data source, Local target, Action admitted)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var actualScenes = target.Globals!.Data.Scenes.ToDictionary(scene => scene.Key, StringComparer.Ordinal);
            var worldsToRebuild = source.Globals!.Scenes
                .Where(desired => NeedsBlockAllocatorReconstruction(desired.Blocks, actualScenes[desired.Key].Blocks))
                .ToArray();
            if (worldsToRebuild.Length == 0) return;

            RequireCanonicalizableCoverage(target.Data);
            CheckEntry(engine, 0xbafc20, 51, "a4a2f47aaafa6f758a329229bcb3478ce04cc6854458695ade685a48368e12b6");
            CheckEntry(engine, 0xba8dd0, 425, "f08b40074c9fcf3a2392fc09acc60811574a03e71b63c4d6bff1d5dcd42fda8d");
            CheckEntry(engine, 0x56b640, 94, "2890014de154460d79e3ec3357c76ec9af9f8198edd1243f07e27c9708f58447");
            var lookup = M.Read(IntPtr.Add(engine, 0x1a2a5a0), 641);
            var bucketSizes = new int[14];
            var reqSizeForBlockSize = new Dictionary<int, (int Bucket, int ReqSize)>();
            for (var b = 0; b < 14; b++) {
                bucketSizes[b] = M.I32(M.Read(IntPtr.Add(engine, 0x1985940 + 4 * b), 4), 0);
                var req = -1;
                for (var s = 1; s <= 640; s++) if (lookup[s] == b) { req = s; break; }
                if (req < 0) throw Error("block allocator bucket has no request size");
                reqSizeForBlockSize[bucketSizes[b]] = (b, req);
            }
            var allocate = (AllocateBlock)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xba8dd0), typeof(AllocateBlock));
            var destroy = (DestroyContact)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xbafc20), typeof(DestroyContact));
            var freeChunk = (FreeChunk)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b640), typeof(FreeChunk));
            var allocFile = IntPtr.Add(engine, 0x15c6004);

            foreach (var desiredScene in worldsToRebuild) {
                admitted();
                var worldKey = desiredScene.Key;
                var desiredBlocks = desiredScene.Blocks;
                var actualBlocks = actualScenes[worldKey].Blocks;
                if (actualBlocks.ChunkCapacity > desiredBlocks.ChunkCapacity)
                    throw Error("target block allocator chunk capacity exceeds source capacity");

                var worldMeta = target.Data.Worlds.Single(w => w.Key == worldKey);
                var allocator = target.Worlds[worldKey];
                var scene = target.Scenes[worldKey];
                var state = IntPtr.Add(allocator, WorldStateOffset);

                // 1. Destroy live contacts in this world before touching block chunks.
                WritePointer(IntPtr.Add(state, 128), IntPtr.Zero);
                M.Write(IntPtr.Add(state, 176), BitConverter.GetBytes(0L));
                M.Write(IntPtr.Add(state, 208), BitConverter.GetBytes(0L));
                foreach (var body in worldMeta.BodyOrder) WritePointer(IntPtr.Add(target.Bodies[body], 136), IntPtr.Zero);
                foreach (var c in worldMeta.Contacts) {
                    admitted();
                    var ptr = target.Contacts[c.Key];
                    var vtable = M.Ptr(M.Read(ptr, 8), 0);
                    if (vtable != IntPtr.Add(engine, c.Vtable) || M.Ptr(M.Read(IntPtr.Add(vtable, 8), 8), 0) != IntPtr.Add(engine, 0xbaf590))
                        throw Error("contact destructor dispatch differs");
                    destroy(ptr, allocator);
                    target.Contacts.Remove(c.Key);
                    target.ContactNames.Remove(ptr);
                }

                // 2. Collect every non-contact cell in this world's chunks and snapshot its raw bytes.
                var cellToWorldSlot = new HashSet<IntPtr>();
                for (var ci = 0; ci < actualBlocks.Chunks.Length; ci++) {
                    var chunk = actualBlocks.Chunks[ci];
                    for (var si = 0; si < chunk.Owners.Length; si++)
                        cellToWorldSlot.Add(target.Globals.Cells[worldKey + "/chunk[" + ci + "]/cell[" + si + "]"]);
                }

                var liveObjects = new Dictionary<string, (IntPtr OldPtr, byte[] Bytes)>(StringComparer.Ordinal);
                var proxyCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                var chainShapes = new List<string>();
                foreach (var bodyKey in worldMeta.BodyOrder) {
                    var bodyPtr = target.Bodies[bodyKey];
                    if (cellToWorldSlot.Contains(bodyPtr)) liveObjects.Add(bodyKey, (bodyPtr, M.Read(bodyPtr, 200)));
                }
                foreach (var col in target.Data.Colliders) {
                    foreach (var fix in col.Fixtures) {
                        var fixPtr = target.Fixtures[fix.Key];
                        if (!cellToWorldSlot.Contains(fixPtr)) continue;
                        liveObjects.Add(fix.Key, (fixPtr, M.Read(fixPtr, 88)));
                        var shapePtr = target.Shapes[fix.Key];
                        if (cellToWorldSlot.Contains(shapePtr))
                            liveObjects.Add(fix.Key + "/shape", (shapePtr, M.Read(shapePtr, ShapeNativeSize(fix.Shape.Type))));
                        if (fix.Proxies.Length != 0) {
                            var proxyBase = target.Proxies[fix.Key + "/proxy[0]"];
                            if (cellToWorldSlot.Contains(proxyBase)) {
                                liveObjects.Add(fix.Key + "/proxy-buffer", (proxyBase, M.Read(proxyBase, checked(fix.Proxies.Length * 32))));
                                proxyCounts[fix.Key] = fix.Proxies.Length;
                            }
                        }
                        if (fix.Shape.Type == 3) {
                            var verts = M.Ptr(M.Read(shapePtr, 24), 16);
                            if (verts != IntPtr.Zero && cellToWorldSlot.Contains(verts)) {
                                liveObjects.Add(fix.Key + "/chain-vertices", (verts, M.Read(verts, checked(fix.Shape.VertexCount * 8))));
                                chainShapes.Add(fix.Key);
                            }
                        }
                    }
                }

                var desiredPlacements = new Dictionary<string, (int Chunk, int Slot, int BlockSize)>(StringComparer.Ordinal);
                for (var ci = 0; ci < desiredBlocks.Chunks.Length; ci++) {
                    var chunk = desiredBlocks.Chunks[ci];
                    for (var si = 0; si < chunk.Owners.Length; si++) {
                        var owner = chunk.Owners[si];
                        if (owner.Length == 0 || IsContactOwner(owner)) continue;
                        desiredPlacements.Add(owner, (ci, si, chunk.BlockSize));
                    }
                }
                if (desiredPlacements.Count != liveObjects.Count || !desiredPlacements.Keys.All(liveObjects.ContainsKey))
                    throw Error("physics block allocator object set differs between source and target: " + worldKey);
                foreach (var kv in desiredPlacements) {
                    if (liveObjects[kv.Key].Bytes.Length > kv.Value.BlockSize)
                        throw Error("physics block object exceeds target slot size: " + kv.Key);
                }

                // 3. Free all current chunks and allocate fresh chunks matching source BlockSize sequence.
                for (var b = 0; b < 14; b++) WritePointer(IntPtr.Add(allocator, 24 + 8 * b), IntPtr.Zero);
                var allocHeader = M.Read(allocator, 20);
                var chunkDirPtr = M.Ptr(allocHeader, 0);
                var memLabel = Int(allocHeader, 16);
                for (var ci = actualBlocks.Chunks.Length - 1; ci >= 0; ci--) {
                    admitted();
                    var slot = IntPtr.Add(chunkDirPtr, checked(ci * 16));
                    var row = M.Read(slot, 16);
                    var chunkPtr = M.Ptr(row, 8);
                    M.Write(IntPtr.Add(allocator, 8), BitConverter.GetBytes(ci));
                    M.Write(slot, new byte[16]);
                    freeChunk(chunkPtr, memLabel, allocFile, 0x26);
                }
                M.Write(IntPtr.Add(allocator, 8), BitConverter.GetBytes(0));

                var newChunkPtrs = new IntPtr[desiredBlocks.Chunks.Length];
                for (var ci = 0; ci < desiredBlocks.Chunks.Length; ci++) {
                    admitted();
                    var wantedSize = desiredBlocks.Chunks[ci].BlockSize;
                    var (bucket, reqSize) = reqSizeForBlockSize[wantedSize];
                    WritePointer(IntPtr.Add(allocator, 24 + 8 * bucket), IntPtr.Zero);
                    var cell0 = allocate(allocator, reqSize);
                    var hdr = M.Read(allocator, 16);
                    var entry = M.Read(IntPtr.Add(M.Ptr(hdr, 0), 16 * ci), 16);
                    var chunkPtr = M.Ptr(entry, 8);
                    if (cell0 == IntPtr.Zero || cell0 != chunkPtr || Int(hdr, 8) != ci + 1 || Int(entry, 0) != wantedSize)
                        throw Error("native block allocator did not create requested chunk " + ci);
                    M.Write(chunkPtr, new byte[16384]);
                    newChunkPtrs[ci] = chunkPtr;
                }
                if (Int(M.Read(allocator, 16), 12) != desiredBlocks.ChunkCapacity)
                    throw Error("reconstructed block allocator chunk capacity differs from source");

                // 4. Write live objects to exact target (chunk, slot) positions and build pointer remap.
                var remap = new Dictionary<IntPtr, IntPtr>();
                var newBodies = new List<IntPtr>();
                var newFixtures = new List<IntPtr>();
                var newProxyBuffers = new List<(IntPtr Ptr, int Count)>();
                var newChainShapePtrs = new List<IntPtr>();

                foreach (var kv in liveObjects) {
                    var p = desiredPlacements[kv.Key];
                    var newPtr = IntPtr.Add(newChunkPtrs[p.Chunk], p.Slot * p.BlockSize);
                    M.Write(newPtr, kv.Value.Bytes);
                    remap[kv.Value.OldPtr] = newPtr;
                    if (kv.Key.EndsWith("/body", StringComparison.Ordinal) || kv.Key.EndsWith("/ground", StringComparison.Ordinal))
                        newBodies.Add(newPtr);
                    else if (kv.Key.IndexOf("/fixture[", StringComparison.Ordinal) >= 0 && !kv.Key.EndsWith("/shape", StringComparison.Ordinal)
                        && !kv.Key.EndsWith("/proxy-buffer", StringComparison.Ordinal) && !kv.Key.EndsWith("/chain-vertices", StringComparison.Ordinal))
                        newFixtures.Add(newPtr);
                }
                foreach (var kv in proxyCounts) {
                    var oldBase = liveObjects[kv.Key + "/proxy-buffer"].OldPtr;
                    var newBase = remap[oldBase];
                    newProxyBuffers.Add((newBase, kv.Value));
                    for (var idx = 1; idx < kv.Value; idx++)
                        remap[IntPtr.Add(oldBase, idx * 32)] = IntPtr.Add(newBase, idx * 32);
                }
                foreach (var fixKey in chainShapes) newChainShapePtrs.Add(remap[liveObjects[fixKey + "/shape"].OldPtr]);

                void PatchPtr(IntPtr address) {
                    var cur = M.Ptr(address);
                    if (remap.TryGetValue(cur, out var next)) WritePointer(address, next);
                }

                // 5. Remap all pointers in Scene, WorldState, Rigidbodies, Colliders, Bodies, Fixtures, Proxies, ChainShapes.
                PatchPtr(IntPtr.Add(scene, 16)); // m_GroundBody
                PatchPtr(IntPtr.Add(state, 224)); // m_bodyList
                foreach (var arrOff in new[] { 248, 280 }) {
                    var arrHdr = M.Read(IntPtr.Add(state, arrOff), 32);
                    var cnt = Bound(Int(arrHdr, 16), MaximumOwners, "body array count");
                    var dataPtr = M.Ptr(arrHdr, 0);
                    for (var idx = 0; idx < cnt; idx++) PatchPtr(IntPtr.Add(dataPtr, idx * 8));
                }
                var treeHdr = M.Read(IntPtr.Add(state, 8), 40);
                var treeCap = Bound(Int(treeHdr, 20), 32768, "tree capacity");
                var treeNodes = M.Ptr(treeHdr, 8);
                for (var ni = 0; ni < treeCap; ni++) {
                    var nodePtr = IntPtr.Add(treeNodes, ni * 40);
                    var nb = M.Read(nodePtr, 40);
                    if (Int(nb, 36) != -1 && Int(nb, 28) == -1) PatchPtr(IntPtr.Add(nodePtr, 16));
                }
                foreach (var rb in source.Rigidbodies) PatchPtr(IntPtr.Add(target.Owners[rb.Owner], 120));
                foreach (var col in source.Colliders) {
                    var fixHdr = M.Read(IntPtr.Add(target.Owners[col.Owner], 88), 32);
                    var cnt = DynamicCount(fixHdr, 8, out _);
                    var dataPtr = M.Ptr(fixHdr, 0);
                    for (var idx = 0; idx < cnt; idx++) PatchPtr(IntPtr.Add(dataPtr, idx * 8));
                }
                foreach (var bp in newBodies) {
                    PatchPtr(IntPtr.Add(bp, 96));  // m_prev
                    PatchPtr(IntPtr.Add(bp, 104)); // m_next
                    PatchPtr(IntPtr.Add(bp, 112)); // m_fixtureList
                }
                foreach (var fp in newFixtures) {
                    PatchPtr(IntPtr.Add(fp, 24));  // m_next
                    PatchPtr(IntPtr.Add(fp, 32));  // m_body
                    PatchPtr(IntPtr.Add(fp, 40));  // m_shape
                    PatchPtr(IntPtr.Add(fp, 56));  // m_proxies
                }
                foreach (var (pb, cnt) in newProxyBuffers) {
                    for (var idx = 0; idx < cnt; idx++) PatchPtr(IntPtr.Add(pb, idx * 32 + 16));
                }
                foreach (var sp in newChainShapePtrs) PatchPtr(IntPtr.Add(sp, 16));

                // 6. Rebuild free lists cleanly for all buckets so Read() sees zero unbound cells.
                var bucketFreeCells = Enumerable.Range(0, 14).Select(_ => new List<IntPtr>()).ToArray();
                for (var ci = 0; ci < desiredBlocks.Chunks.Length; ci++) {
                    var chunk = desiredBlocks.Chunks[ci];
                    var bucket = reqSizeForBlockSize[chunk.BlockSize].Bucket;
                    for (var si = 0; si < chunk.Owners.Length; si++) {
                        var owner = chunk.Owners[si];
                        if (owner.Length == 0 || IsContactOwner(owner))
                            bucketFreeCells[bucket].Add(IntPtr.Add(newChunkPtrs[ci], si * chunk.BlockSize));
                    }
                }
                for (var b = 0; b < 14; b++) {
                    var list = bucketFreeCells[b];
                    WritePointer(IntPtr.Add(allocator, 24 + 8 * b), list.Count == 0 ? IntPtr.Zero : list[0]);
                    for (var idx = 0; idx < list.Count; idx++)
                        WritePointer(list[idx], idx + 1 == list.Count ? IntPtr.Zero : list[idx + 1]);
                }
            }
        }
    }
}
