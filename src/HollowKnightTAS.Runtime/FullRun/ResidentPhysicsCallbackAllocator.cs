using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned PDB 0334be90-63dd-4644-bee1-fc8d56c840c2 age 1.
    // Chunk storage contains owned Collision2D values, never portable POD.
    internal static class ResidentPhysicsCallbackAllocator
    {
        internal sealed class Data
        {
            public bool Present; public int OwnerLabel; public int Label; public string[] CoverageGaps = new[] { "retired-slot payload needs typed Canon" }; public int AllocChunk, DeallocChunk;
            public byte[][] FreeState = Array.Empty<byte[]>();
            public int[] Rows = Array.Empty<int>();
        }
        internal sealed class Live
        {
            internal Data Data = new Data();
            internal IntPtr Owner; internal byte[] OwnerWitness = Array.Empty<byte>(); internal byte[] HeaderWitness = Array.Empty<byte>(); internal byte[][] ChunkWitness = Array.Empty<byte[]>();
            internal IntPtr[] Chunks = Array.Empty<IntPtr>();
            internal IntPtr[] Rows = Array.Empty<IntPtr>();
            internal Action Admitted = null!;
            internal void CanonicalizeIdle(Action? requireEpoch = null)
            {
                RequireUnchanged();
                var zero = new byte[103];
                for (int c = 0; c < Data.FreeState.Length; ++c)
                    for (int i = 0; i < Data.FreeState[c][1]; ++i)
                    {
                        if ((i & 31) == 0) { requireEpoch?.Invoke(); Admitted(); }
                        int slot = Data.FreeState[c][i + 2];
                        var offset = slot * 104 + 1;
                        for (var j = 0; j < zero.Length; j++) if (ChunkWitness[c][offset + j] != 0) {
                            ResidentNativeMemory.Write(IntPtr.Add(Chunks[c], offset), zero); break;
                        }
                    }
                requireEpoch?.Invoke();
                var after = Capture(Owner, Rows, Admitted);
                if (!Equal(Data, after.Data)) throw Gap("idle Canon changed topology");
                Data = after.Data; OwnerWitness = after.OwnerWitness; HeaderWitness = after.HeaderWitness; ChunkWitness = after.ChunkWitness;
            }
            internal void RequireCanonicalizable() { Validate(Data, Rows.Length); RequireUnchanged(); }
            internal void RequireComplete() { RequireUnchanged(); if (Data.CoverageGaps.Length != 0) throw Gap(string.Join(";", Data.CoverageGaps)); }
            internal void RequireUnchanged()
            {
                var current = Capture(Owner, Rows, Admitted);
                if (!OwnerWitness.SequenceEqual(current.OwnerWitness) || !HeaderWitness.SequenceEqual(current.HeaderWitness) || !ChunkWitness.Zip(current.ChunkWitness, (x,y) => x.SequenceEqual(y)).All(x => x) || !Chunks.SequenceEqual(current.Chunks) || !Equal(Data, current.Data))
                    throw Gap("allocator changed after capture");
            }
        }
        private static InvalidOperationException Gap(string reason) => new InvalidOperationException("resident physics callback allocator gap: " + reason);
        private static int Index(List<IntPtr> chunks, IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return -1;
            int index = chunks.IndexOf(pointer);
            if (index < 0) throw Gap("current chunk is outside allocation chain");
            return index;
        }
        internal static Live Capture(IntPtr collisionAllocator, IntPtr[] collisionRows, Action admitted)
        {
            admitted();
            LoadingEngineClockProbe.RequirePinnedEngine();
            var allocator = ResidentNativeMemory.Ptr(IntPtr.Add(collisionAllocator, 8));
            if (allocator == IntPtr.Zero) { if (collisionRows.Length != 0) throw Gap("null allocator has active rows"); return new Live { Owner = collisionAllocator, OwnerWitness = ResidentNativeMemory.Read(collisionAllocator, 16), Admitted = admitted, Data = new Data { Present = false, OwnerLabel = ResidentNativeMemory.I32(ResidentNativeMemory.Read(collisionAllocator, 4), 0), CoverageGaps = Array.Empty<string>(), AllocChunk = -1, DeallocChunk = -1 } }; }
            var header = ResidentNativeMemory.Read(allocator, 32);
            var chunks = new List<IntPtr>();
            var states = new List<byte[]>();
            var freeSlots = new HashSet<int>(); bool dirtyIdle = false;
            var next = ResidentNativeMemory.Ptr(header, 0);
            while (next != IntPtr.Zero)
            {
                admitted();
                if (chunks.Count >= 4096 || chunks.Contains(next)) throw Gap("chunk chain cycle or bound exceeded");
                int chunkIndex = chunks.Count;
                chunks.Add(next);
                var bytes = ResidentNativeMemory.Read(next, 26536);
                int first = bytes[26528], available = bytes[26529];
                var state = new List<byte> { (byte)first, (byte)available };
                for (int i = 0; i < available; ++i)
                {
                    if (first >= 255 || !freeSlots.Add(chunkIndex * 255 + first)) throw Gap("invalid free chain");
                    state.Add((byte)first);
                    for (int b = 1; b < 104; ++b) if (bytes[first * 104 + b] != 0) dirtyIdle = true; first = bytes[first * 104];
                }
                // Preserve terminal link too: it influences future native allocation.
                state.Add((byte)first);
                states.Add(state.ToArray());
                next = ResidentNativeMemory.Ptr(bytes, 26520);
            }
            var rows = new int[collisionRows.Length];
            var occupied = new HashSet<int>();
            for (int i = 0; i < rows.Length; ++i)
            {
                int found = -1;
                for (int c = 0; c < chunks.Count; ++c)
                {
                    long delta = collisionRows[i].ToInt64() - chunks[c].ToInt64();
                    if (delta >= 0 && delta < 26520 && delta % 104 == 0) found = c * 255 + (int)(delta / 104);
                }
                if (found < 0 || freeSlots.Contains(found) || !occupied.Add(found)) throw Gap("active Collision2D row is invalid or free");
                rows[i] = found;
            }
            // Unknown occupied slots may own dynamic arrays and cannot be silently retired.
            if (occupied.Count + freeSlots.Count != chunks.Count * 255) throw Gap("occupied slots missing from Collision2D directory");
            return new Live { Owner = collisionAllocator, OwnerWitness = ResidentNativeMemory.Read(collisionAllocator, 16), HeaderWitness = header, ChunkWitness = chunks.Select(x => ResidentNativeMemory.Read(x, 26536)).ToArray(), Chunks = chunks.ToArray(), Rows = (IntPtr[])collisionRows.Clone(), Admitted = admitted,
                Data = new Data { Present = true, OwnerLabel = ResidentNativeMemory.I32(ResidentNativeMemory.Read(collisionAllocator, 4), 0), CoverageGaps = dirtyIdle ? new[] { "retired-slot payload needs typed Canon" } : Array.Empty<string>(), Label = ResidentNativeMemory.I32(header, 24), AllocChunk = Index(chunks, ResidentNativeMemory.Ptr(header, 8)), DeallocChunk = Index(chunks, ResidentNativeMemory.Ptr(header, 16)), FreeState = states.ToArray(), Rows = rows } };
        }
        private static bool Equal(Data a, Data b) => a.Present == b.Present && a.AllocChunk == b.AllocChunk && a.DeallocChunk == b.DeallocChunk && a.Rows.SequenceEqual(b.Rows)
            && a.FreeState.Length == b.FreeState.Length && a.FreeState.Zip(b.FreeState, (x, y) => x.SequenceEqual(y)).All(x => x);
        internal static void Validate(Data data, int rowCount)
        {
            if (data == null || data.Rows == null || data.FreeState == null || data.CoverageGaps == null || data.Rows.Length != rowCount || data.FreeState.Length > 64
                || data.Present && data.Label != data.OwnerLabel || !data.Present && data.Label != 0) throw Gap("invalid serialized allocator");
            int count = data.FreeState.Length;
            if (data.AllocChunk < -1 || data.AllocChunk >= count || data.DeallocChunk < -1 || data.DeallocChunk >= count) throw Gap("invalid current chunk indices");
            if (count > 0 && (data.AllocChunk < 0 || data.DeallocChunk < 0)) throw Gap("nonempty allocator has null current chunk");
            var slots = new HashSet<int>();
            foreach (int row in data.Rows) if (row < 0 || row >= count * 255 || !slots.Add(row)) throw Gap("invalid duplicate active slot");
            for (int c = 0; c < count; ++c)
            {
                var state = data.FreeState[c];
                if (state == null || state.Length < 3 || state.Length != state[1] + 3 || state[1] != 0 && state[0] != state[2]) throw Gap("invalid free state length/head");
                for (int i = 0; i < state[1]; ++i) if (state[i + 2] >= 255 || !slots.Add(c * 255 + state[i + 2])) throw Gap("duplicate or active free slot");
            }
            if (slots.Count != count * 255 || !data.Present && (count != 0 || rowCount != 0)) throw Gap("allocator directory coverage differs");
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr AllocateRow(IntPtr allocator, IntPtr copyFrom);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeRow(IntPtr allocator, IntPtr row);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyAllocator(IntPtr allocator);
        // Caller must unlink every contact/hash alias before entry. Owner itself stays
        // at the same address; native Free destroys each live manifold allocation.
        private static void RequireProcedure(IntPtr module, int rva, int extent, string expected)
        {
            using (var hash = SHA256.Create())
                if (BitConverter.ToString(hash.ComputeHash(ResidentNativeMemory.Read(IntPtr.Add(module, rva), extent))).Replace("-", "").ToLowerInvariant() != expected)
                    throw Gap("pinned allocator procedure differs at " + rva.ToString("x"));
        }
        private static Live Rebuild(Data source, Live target, Action admitted)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            RequireProcedure(engine.BaseAddress, 0xbfe6d0, 866, "07332bc697b86fc59d68ce16dc99069d8c959403f50ace226e9be01cf4f36c7a");
            RequireProcedure(engine.BaseAddress, 0xc02c90, 215, "2c548ac7d42e6b48d390fb42018dc15d43532be1f2dcb78286b9842aea81e7bd");
            RequireProcedure(engine.BaseAddress, 0xbfdcb0, 144, "1c9fc6eefcaeea75d8a4d5ee00e60b61a53d40b041076f5073f1bf809f20ee90");
            var allocate = (AllocateRow)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0xbfe6d0), typeof(AllocateRow));
            var free = (FreeRow)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0xc02c90), typeof(FreeRow));
            var destroy = (DestroyAllocator)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress, 0xbfdcb0), typeof(DestroyAllocator));
            foreach (var row in target.Rows) { admitted(); free(target.Owner, row); }
            admitted(); destroy(target.Owner);
            if (ResidentNativeMemory.Ptr(IntPtr.Add(target.Owner, 8)) != IntPtr.Zero) throw Gap("allocator destructor did not clear outer pointer");
            if (!source.Present) return Capture(target.Owner, Array.Empty<IntPtr>(), admitted);
            // Allocate every slot to construct all chunk objects through engine code.
            // Admission bounds this reconstruction to 64 chunks; total native allocator overhead still needs measured acceptance.
            if (source.FreeState.Length == 0) throw Gap("present zero-chunk allocator needs standalone constructor");
            var all = new IntPtr[source.FreeState.Length * 255];
            for (int i = 0; i < all.Length; ++i) { admitted(); all[i] = allocate(target.Owner, IntPtr.Zero); if (all[i] == IntPtr.Zero) throw Gap("native row allocation failed"); }
            var allocatedLive = Capture(target.Owner, all, admitted);
            var chunks = allocatedLive.Chunks;
            if (chunks.Length != source.FreeState.Length) throw Gap("native creation chunk count differs");
            for (int i = 0; i < all.Length; ++i)
                if (all[i] != IntPtr.Add(chunks[i / 255], i % 255 * 104)) throw Gap("native creation and linked chunk order differ");
            for (int c = 0; c < chunks.Length; ++c)
            {
                var state = source.FreeState[c];
                // Free prepends to the per-chunk list. Reverse source chain order.
                for (int i = state[1] - 1; i >= 0; --i) { admitted(); free(target.Owner, all[c * 255 + state[i + 2]]); }
            }
            // Free has no chunk-reclamation branch (full guarded 215B body).
            // Independently enumerate again before touching any chunk/header byte.
            var retiredLive = Capture(target.Owner, source.Rows.Select(x => all[x]).ToArray(), admitted);
            if (!chunks.SequenceEqual(retiredLive.Chunks)) throw Gap("native retirement changed chunk chain");
            for (int c = 0; c < chunks.Length; ++c)
            {
                var state = source.FreeState[c];
                for (int i = 0; i < state[1]; ++i)
                    ResidentNativeMemory.Write(all[c * 255 + state[i + 2]], new[] { state[i + 3] });
                ResidentNativeMemory.Write(IntPtr.Add(chunks[c], 26528), new[] { state[0], state[1] });
            }
            var header = ResidentNativeMemory.Ptr(IntPtr.Add(target.Owner, 8));
            ResidentNativeMemory.Write(IntPtr.Add(header, 8), BitConverter.GetBytes(source.AllocChunk < 0 ? 0L : chunks[source.AllocChunk].ToInt64()));
            ResidentNativeMemory.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(source.DeallocChunk < 0 ? 0L : chunks[source.DeallocChunk].ToInt64()));
            var result = Capture(target.Owner, source.Rows.Select(x => all[x]).ToArray(), admitted);
            result.CanonicalizeIdle();
            return result;
        }
        internal static IntPtr[] PrepareRows(Data source, Live target, Action admitted)
        {
            admitted(); Validate(source, source.Rows.Length);
            if (source.OwnerLabel != target.Data.OwnerLabel || source.CoverageGaps.Length != 0) throw Gap("source allocator label or complete coverage differs");
            if (source.Present && source.FreeState.Length == 0 && !Equal(source, target.Data)) throw Gap("present zero-chunk allocator reconstruction not proven");
            target.RequireUnchanged();
            if (!Equal(source, target.Data)) target = Rebuild(source, target, admitted);
            if (!Equal(source, target.Data)) throw Gap("different topology requires proven native chunk and Collision2D lifecycle reconstruction");
            // Caller restores typed row values and dynamic_array<Manifold> in the returned
            // process-local slots. Rebuild has already destroyed old owned arrays.
            return source.Rows.Select(index => IntPtr.Add(target.Chunks[index / 255], (index % 255) * 104)).ToArray();
        }
    }
}
