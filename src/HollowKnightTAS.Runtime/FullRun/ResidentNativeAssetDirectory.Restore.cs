using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetDirectory
    {
        // No wrapper creation, asset load, string interning or storage release.
        // Unsupported identities/storage stop before the first native write.
        internal static Plan Prepare(Data source, ResidentNativeGlobalObjects.CaptureResult globals,
            IReadOnlyDictionary<IntPtr, string> known, IReadOnlyDictionary<string, IntPtr> allBindings,
            ResidentMutationScope scope, Action admitted) => new Plan(source, globals, known, allBindings, scope, admitted);
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly IReadOnlyDictionary<IntPtr, string> known;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Live before;
            private bool attempted, applied;
            internal Plan(Data source, ResidentNativeGlobalObjects.CaptureResult globals, IReadOnlyDictionary<IntPtr, string> known,
                IReadOnlyDictionary<string, IntPtr> allBindings, ResidentMutationScope scope, Action admitted)
            {
                RequireCompleteBodies(source); this.source = source.Copy(); this.known = known;
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); this.admitted = admitted;
                before = Capture(globals, known, admitted); RequireCompleteBodies(before.Snapshot);
                RequireReadonlyDependencies(this.source, before.Snapshot);
            }
            internal void Apply()
            {
                if (attempted) throw Error("native asset plan reused"); attempted = true; scope.Require(); admitted(); before.RequireUnchanged();
                scope.Mutate(() => {
                    foreach (var row in source.Rows) {
                        admitted(); var pointer = before.Pointers[row.Binding];
                        if(row.Texture!=null){ApplyTextureKnownD(row,before,admitted);continue;}
                        if (row.Kind != "physics-material-2d") throw Error("unsupported native body reached apply");
                        M.Write(IntPtr.Add(pointer, 56), row.Values);
                        // PPtr owner and interned name are readonly dependencies;
                        // no reverse-owner/cache protocol is inferred or replayed.
                    }
                });
                applied = true;
                // Object48/handles and final registry may be restored by the
                // coordinator after typed native bodies. Final Verify requires
                // a fresh globals observation, not this pre-mutation snapshot.
            }
            internal Live Verify(ResidentNativeGlobalObjects.CaptureResult freshGlobals)
            {
                if (!applied) throw Error("native asset verify precedes application"); scope.Require(); admitted();
                var live = Capture(freshGlobals, known, admitted); RequireEqual(source, live.Snapshot); return live;
            }
        }
        internal static void RequireReadonlyDependencies(Data source, Data target)
        {
            RequireCompleteBodies(source); RequireCompleteBodies(target);
            if (!source.BuiltinSlotOwners.SequenceEqual(target.BuiltinSlotOwners))
                throw Error("builtin texture slot occupancy, ownership or alias topology differs");
            if (source.Rows.Length != target.Rows.Length) throw Error("native asset membership differs; allocation/loading is unsupported");
            var targetRows = target.Rows.ToDictionary(row => row.Binding, StringComparer.Ordinal);
            foreach (var row in source.Rows) {
                if (!targetRows.TryGetValue(row.Binding, out var actual) || row.TypeKey != actual.TypeKey || row.TypeRva != actual.TypeRva || row.Size != actual.Size
                    || row.Kind != actual.Kind || row.Owner != actual.Owner || row.Persistent != actual.Persistent || row.NamePresent != actual.NamePresent || !row.Name.SequenceEqual(actual.Name))
                    throw Error("native asset readonly identity/name dependency differs");
                if (!row.Binding.StartsWith(ResidentBuiltinTextureRoots.Prefix,StringComparison.Ordinal))
                    ResidentSerializedAssetImage.RequireEqual(row.Source, actual.Source);
                if(row.Texture!=null)RequireTextureReadonlyDependencies(row,actual);
            }
        }
    }
}
