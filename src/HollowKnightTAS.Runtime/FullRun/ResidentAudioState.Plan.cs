using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAudioState
    {
        internal static Action Canon(ResidentObjectDirectory directory, ResidentMutationScope scope, Func<object, string?> bind, Action admitted)
        {
            if (directory == null || scope == null || bind == null || admitted == null) throw new ArgumentNullException();
            scope.Require(); admitted(); var before = Capture(directory, bind); RequireCanonicalizableCoverage(before);
            foreach (var row in before.Sources) if (!scope.Owns((Component)directory.Resolve(row.Owner))) throw Error("audio Canon owner is outside the mutation scope");
            try {
                scope.Mutate(() => {
                    foreach (var row in before.Sources) {
                        var pointer = M.Owner(directory.Resolve(row.Owner));
                        for (var index = 0; index < row.Curves.Length; index++) {
                            scope.Require(); admitted(); ResidentManagedNativeWrappers.CanonicalizeCurveNative(IntPtr.Add(pointer, 64 + index * 104), admitted);
                        }
                    }
                });
                var expected = Copy(before);
                foreach (var row in expected.Sources) foreach (var curve in row.Curves) ResidentManagedNativeWrappers.NormalizeCurveStorage(curve);
                expected.CoverageGaps = RequiredCoverageGaps(expected); RequireCanonicalState(expected);
                Action verify = () => { scope.Require(); admitted(); RequireEqual(expected, Capture(directory, bind)); admitted(); }; verify(); return verify;
            } catch (Exception error) { scope.Fault(error); throw; }
        }
        internal static Plan Prepare(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(source, directory, scope, resolve, bind, admitted);
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string, NativeObject> resolve;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private bool applied;
            internal Plan(Data source, ResidentObjectDirectory directory, ResidentMutationScope scope, Func<string, NativeObject> resolve,
                Func<object, string?> bind, Action admitted)
            {
                RequireCanonicalState(source); this.source = Copy(source); this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
                this.bind = bind ?? throw new ArgumentNullException(nameof(bind)); this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                scope.Require(); admitted(); before = Capture(directory, bind); RequireCanonicalState(before);
                if (!source.Sources.Select(row => row.Owner).SequenceEqual(before.Sources.Select(row => row.Owner))
                    || !source.Listeners.Select(row => row.Owner).SequenceEqual(before.Listeners.Select(row => row.Owner)) || source.Manager.Present != before.Manager.Present)
                    throw Error("target audio native owner/manager directory differs");
                void Owner(string key, ResidentObjectDirectory.TypeRecord type) {
                    var owner = directory.Resolve(key);
                    if (!(owner is Component component) || !scope.Owns(component) || type.Resolve() != owner.GetType()) throw Error("target audio owner/type is outside the mutation scope");
                }
                void Dependency(string key, Type type) { if (key.Length != 0) _ = ResidentNativeAssetReference.ResolveTyped(key,type,resolve); }
                foreach (var row in source.Sources) {
                    Owner(row.Owner, row.Type); Dependency(row.Clip, typeof(AudioClip)); Dependency(row.Mixer, typeof(UnityEngine.Audio.AudioMixerGroup)); Dependency(row.ScheduledSource, typeof(AudioSource));
                }
                foreach (var row in source.Listeners) { Owner(row.Owner, row.Type); Dependency(row.AlternateTransform, typeof(Transform)); }
                admitted();
            }
            internal void Apply()
            {
                if (applied) throw Error("audio native application is single-use"); scope.Require(); admitted(); RequireEqual(before, Capture(directory, bind));
                try {
                    scope.Mutate(() => {
                        foreach (var row in source.Sources) {
                            scope.Require(); admitted(); var pointer = M.Owner(directory.Resolve(row.Owner));
                            for (var index = 0; index < row.Curves.Length; index++) ResidentManagedNativeWrappers.ApplyCurveNative(row.Curves[index], IntPtr.Add(pointer, 64 + index * 104), admitted);
                            EmptyChannels(IntPtr.Add(pointer, 760), row.Cached.Length, row.CachedCapacity, admitted);
                            EmptyChannels(IntPtr.Add(pointer, 976), row.Providers.Length, row.ProviderCapacity, admitted);
                            M.Write(IntPtr.Add(pointer, 824), BitConverter.GetBytes(Id(row.Clip, resolve)).Concat(BitConverter.GetBytes(Id(row.Mixer, resolve))).ToArray());
                            M.Write(IntPtr.Add(pointer, 880), BitConverter.GetBytes(Pointer(row.ScheduledSource, resolve).ToInt64()));
                            ApplyRanges(pointer, SourceRanges, row.Scalars);
                        }
                        foreach (var row in source.Listeners) {
                            scope.Require(); admitted(); var pointer = M.Owner(directory.Resolve(row.Owner));
                            M.Write(IntPtr.Add(pointer, 92), BitConverter.GetBytes(Id(row.AlternateTransform, resolve))); ApplyRanges(pointer, ListenerRanges, row.Scalars);
                        }
                        ApplyRegistry(source, directory, admitted);
                    });
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("audio native verification precedes application"); scope.Require(); admitted(); RequireEqual(source, Capture(directory, bind)); admitted();
            }
        }
        private static void EmptyChannels(IntPtr header, int size, int capacity, Action admitted)
        {
            // CompleteCoverage has proved every source and target WeakPtr is
            // null. No retain is leaked by resizing this empty-value array.
            admitted(); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes(0UL)); M.Reserve(header, capacity, 8, 8, admitted);
            if (M.U64(M.Read(header, 32), 24) >> 1 != (ulong)capacity) throw Error("audio channel-array capacity failed readback");
            M.Write(M.Ptr(header), new byte[size * 8]); M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)size)); admitted();
        }
        private static void ApplyRanges(IntPtr owner, Range[] ranges, byte[][] values)
        { for (var index = 0; index < ranges.Length; index++) M.Write(IntPtr.Add(owner, ranges[index].Offset), values[index]); }
        private static int Id(string key, Func<string, NativeObject> resolve) => key.Length == 0 ? 0 : M.I32(M.Read(IntPtr.Add(Pointer(key,resolve),8),4),0);
        private static IntPtr Pointer(string key, Func<string, NativeObject> resolve) => ResidentNativeAssetReference.ResolveTyped(key,typeof(NativeObject),resolve);
        private static void ApplyRegistry(Data source, ResidentObjectDirectory directory, Action admitted)
        {
            var manager = ManagerPointer(); if (source.Manager.Present != (manager != IntPtr.Zero)) throw Error("audio native manager lifetime changed");
            if (manager == IntPtr.Zero) return;
            void List(int offset, int nodeOffset, IEnumerable<string> owners, string[] order) {
                admitted(); var sentinel = IntPtr.Add(manager, offset);
                var nodes = owners.ToDictionary(key => key, key => IntPtr.Add(M.Owner(directory.Resolve(key)), nodeOffset), StringComparer.Ordinal);
                foreach (var node in nodes.Values) M.Write(node, new byte[16]);
                M.Write(sentinel, BitConverter.GetBytes((order.Length == 0 ? sentinel : nodes[order[order.Length - 1]]).ToInt64())
                    .Concat(BitConverter.GetBytes((order.Length == 0 ? sentinel : nodes[order[0]]).ToInt64())).ToArray());
                for (var index = 0; index < order.Length; index++)
                    M.Write(nodes[order[index]], BitConverter.GetBytes((index == 0 ? sentinel : nodes[order[index - 1]]).ToInt64())
                        .Concat(BitConverter.GetBytes((index + 1 == order.Length ? sentinel : nodes[order[index + 1]]).ToInt64())).ToArray());
                admitted();
            }
            List(144, 832, source.Sources.Select(row => row.Owner), source.Manager.Sources);
            List(160, 160, source.Listeners.Select(row => row.Owner), source.Manager.Listeners);
            List(176, 864, source.Sources.Select(row => row.Owner), source.Manager.Scheduled);
        }
    }
}
