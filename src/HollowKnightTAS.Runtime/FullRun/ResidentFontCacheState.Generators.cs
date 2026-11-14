using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentFontCacheState
    {
        // Process-local derived cache. Never enters Data or portable comparison.
        private sealed class GeneratorDirectory
        {
            internal IntPtr Header, RuntimeOwnerSlot;
            internal byte[] Vector = Array.Empty<byte>(), Entries = Array.Empty<byte>();
            internal readonly List<(IntPtr Owner, byte[] Body)> Owners = new List<(IntPtr, byte[])>();
            internal void RequireUnchanged()
            {
                RequireOwner();
                if (!M.Read(Header, 24).SequenceEqual(Vector) || !M.Read(M.Ptr(Vector, 0), Entries.Length).SequenceEqual(Entries)
                    || Owners.Any(owner => !M.Read(owner.Owner, 320).SequenceEqual(owner.Body)))
                    throw Error("generator cache changed during Font restoration");
            }
            private void RequireOwner()
            {
                if (RuntimeOwnerSlot != IntPtr.Zero && M.Ptr(M.Read(RuntimeOwnerSlot, 8), 0) != Header)
                    throw Error("generator RuntimeStatic owner changed");
            }
            internal void RequireEmpty()
            {
                RequireOwner();
                var after = M.Read(Header, 24);
                if (M.Ptr(after, 0) != M.Ptr(Vector, 0) || M.Ptr(after, 16) != M.Ptr(Vector, 16) || M.Ptr(after, 8) != M.Ptr(after, 0))
                    throw Error("generator cache did not invalidate in its existing local owner");
            }
        }
        private static GeneratorDirectory ReadGeneratorVector(IntPtr header)
        {
            if (header == IntPtr.Zero) throw Error("generator RuntimeStatic owner is absent");
            var vector = M.Read(header, 24);
            long first = M.Ptr(vector, 0).ToInt64(), last = M.Ptr(vector, 8).ToInt64(), end = M.Ptr(vector, 16).ToInt64();
            if (last < first || end < last || (last - first) % 8 != 0 || (end - first) % 8 != 0)
                throw Error("generator cache vector extent differs");
            int count = Count((ulong)((last - first) / 8), 8);
            Count((ulong)((end - first) / 8), 8);
            var entries = M.Read(new IntPtr(first), checked(count * 8));
            var result = new GeneratorDirectory {Header = header, Vector = vector, Entries = entries};
            var seen = new HashSet<IntPtr>();
            for (int i = 0; i < count; i++) {
                var owner = M.Ptr(entries, i * 8);
                if (owner == IntPtr.Zero || !seen.Add(owner)) throw Error("generator cache has a null or repeated owner");
                result.Owners.Add((owner, M.Read(owner, 320)));
            }
            return result;
        }
        private static GeneratorDirectory ReadGenerators()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            // Direct RuntimeStatic data pointer, never lazy initialization or object search.
            var slot = IntPtr.Add(engine, 0x1988fc0);
            var result = ReadGeneratorVector(M.Ptr(M.Read(slot, 8), 0));
            result.RuntimeOwnerSlot = slot;
            return result;
        }
        private static void InvalidateGenerators(Action admitted, Action requireUnchanged, Action flush, Action requireEmpty)
        {
            // Caller has already validated all dependencies/native entries and restored the
            // Font. Admission is the existing mutation scope + resident clock boundary;
            // this does not claim a new global graphics-idle fence.
            admitted();
            requireUnchanged();
            flush();
            requireEmpty();
            admitted();
        }
    }
}
