using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class GenericOwnerDependency
        {
            public bool Optimized, SetAlias, TqsPresent;
            public int ExposedCount, SkeletonCount, ScaleOffset = -1;
            public byte[] ScaleChanges = Array.Empty<byte>();
            public byte AllowConstantOptimization;
            public int[] RootIndices = Enumerable.Repeat(-1,3).ToArray(), TqsIndices = Array.Empty<int>();
            public BoundCurveTable[] Tables = Enumerable.Range(0,4).Select(_ => new BoundCurveTable()).ToArray();
        }
        internal sealed class BoundCurveTable
        {
            // -1 null; -2 a nonnull allocation outside the proven owner layout.
            public int OwnerOffset = -1;
            public BoundCurveDependency[] Rows = Array.Empty<BoundCurveDependency>();
        }
        internal sealed class BoundCurveDependency
        {
            public uint Type;
            public bool TargetPointer, CustomBinding;
            public string TargetObject = "";
        }

        private static readonly ConditionalWeakTable<Reader,Dictionary<IntPtr,IntPtr>> genericTqsOwners
            = new ConditionalWeakTable<Reader,Dictionary<IntPtr,IntPtr>>();

        private static void RegisterTqsOwner(Dictionary<IntPtr,IntPtr> owners, IntPtr map, IntPtr controller)
        {
            if (map == IntPtr.Zero) return;
            if (owners.TryGetValue(map,out var previous) && previous != controller)
                throw Error("SkeletonTQSMap allocation is shared by different controller owners");
            owners[map] = controller;
        }

        private static int[] GenericTableOffsets(bool optimized, int exposed, int[] counts)
        {
            long cursor = 96L + (optimized ? exposed : 0);
            var offsets = Enumerable.Repeat(-1,4).ToArray();
            foreach (int table in new[] {0,1,3,2})
            {
                if (counts[table] == 0) continue;
                cursor = checked((cursor + 7) & ~7L);
                if (cursor > MaximumBytes) throw Error("generic binding layout exceeds package bound");
                offsets[table] = (int)cursor;
                cursor = checked(cursor + (long)counts[table]*32);
            }
            if (cursor > MaximumBytes) throw Error("generic binding layout exceeds package bound");
            return offsets;
        }

        private static int RelativeGenericOffset(IntPtr owner, IntPtr pointer, int expected)
        {
            if (pointer == IntPtr.Zero) return -1;
            return expected >= 0 && pointer == IntPtr.Add(owner,expected) ? expected : UnknownEdge;
        }

        private static GenericOwnerDependency CaptureGenericDependency(Reader reader, IntPtr owner, IntPtr set,
            IntPtr avatar, IntPtr binding, bool optimized)
        {
            var bytes = reader.Memory.Read(owner,96);
            var value = new GenericOwnerDependency
            {
                Optimized = optimized,
                AllowConstantOptimization = bytes[80],
                RootIndices = new[] {M.I32(bytes,84),M.I32(bytes,88),M.I32(bytes,92)}
            };
            if (binding != IntPtr.Zero)
                value.ExposedCount = BindingSize(M.U64(reader.Memory.Read(binding,48),24));
            if (avatar != IntPtr.Zero)
            {
                var header = reader.Memory.Read(avatar,176);
                var skeleton = Relative(avatar,header,0);
                if (skeleton != IntPtr.Zero)
                    value.SkeletonCount = Count(M.U32(reader.Memory.Read(skeleton,40),0));
            }
            var counts = Enumerable.Range(0,4).Select(index => BindingSize(M.U64(bytes,index*16))).ToArray();
            var offsets = GenericTableOffsets(optimized,value.ExposedCount,counts);
            for (int table = 0; table < 4; table++)
            {
                var pointer = M.Ptr(bytes,table*16+8);
                var rows = BindingBuffer(reader,pointer,counts[table],32);
                var captured = new BoundCurveTable {OwnerOffset = RelativeGenericOffset(owner,pointer,offsets[table])};
                captured.Rows = Decode(rows,32,(entry,offset) =>
                {
                    var type = M.U32(entry,offset+8);
                    var target = M.Ptr(entry,offset+24);
                    bool field = M.Ptr(entry,offset) != IntPtr.Zero;
                    bool custom = M.Ptr(entry,offset+16) != IntPtr.Zero;
                    bool transform = table == 0 && type >= 1 && type <= 4 && !field && !custom;
                    return new BoundCurveDependency
                    {
                        Type = type, TargetPointer = field, CustomBinding = custom,
                        TargetObject = transform ? reader.Key<Transform>(target) : reader.Key<NativeObject>(target)
                    };
                });
                value.Tables[table] = captured;
            }
            var scale = M.Ptr(bytes,64);
            int scaleOffset = optimized && value.ExposedCount != 0 ? 96 : -1;
            value.ScaleOffset = RelativeGenericOffset(owner,scale,scaleOffset);
            if (scale != IntPtr.Zero)
                value.ScaleChanges = BindingBuffer(reader,scale,value.ExposedCount,1);
            var controller = M.Ptr(bytes,72);
            if (controller != IntPtr.Zero)
            {
                var header = reader.Memory.Read(controller,32);
                var map = M.Ptr(header,16);
                value.TqsPresent = map != IntPtr.Zero;
                RegisterTqsOwner(genericTqsOwners.GetValue(reader,_ => new Dictionary<IntPtr,IntPtr>()),map,controller);
                var rows = BindingBuffer(reader,map,value.SkeletonCount,12);
                value.TqsIndices = Enumerable.Range(0,rows.Length/4).Select(index => M.I32(rows,index*4)).ToArray();
                var animationSet = set == IntPtr.Zero ? IntPtr.Zero : M.Ptr(reader.Memory.Read(set,96),88);
                value.SetAlias = animationSet != IntPtr.Zero && M.Ptr(header,24) == animationSet;
            }
            return value;
        }

        private static bool GenericLayoutKnown(GenericOwnerDependency value)
        {
            var counts = value.Tables.Select(table => table.Rows.Length).ToArray();
            var offsets = GenericTableOffsets(value.Optimized,value.ExposedCount,counts);
            if (value.Optimized && counts[0] != 0) return false;
            if (value.ScaleOffset != (value.Optimized && value.ExposedCount != 0 ? 96 : -1)) return false;
            for (int table = 0; table < 4; table++)
            {
                if (value.Tables[table].OwnerOffset != offsets[table]) return false;
            }
            return true;
        }

        private static void ValidateGenericDependency(Backing backing)
        {
            var value = backing.GenericDependency;
            if (value == null) return;
            if (!backing.GenericPresent || value.Tables == null || value.Tables.Length != 4
                || value.Tables.Any(table => table == null || table.Rows == null || table.Rows.Length > MaximumEntries
                    || table.OwnerOffset < UnknownEdge || table.OwnerOffset > MaximumBytes
                    || table.Rows.Any(row => row == null || !Text(row.TargetObject)))
                || value.RootIndices == null || value.RootIndices.Length != 3 || value.ScaleChanges == null
                || value.ExposedCount < 0 || value.ExposedCount > MaximumEntries
                || value.SkeletonCount < 0 || value.SkeletonCount > MaximumEntries
                || value.ScaleOffset < UnknownEdge || value.ScaleOffset > MaximumBytes
                || value.ScaleChanges.Length != (value.ScaleOffset == -1 ? 0 : value.ExposedCount)
                || value.TqsIndices == null || value.TqsIndices.Length != checked(value.SkeletonCount*3))
                throw Error("generic binding dependency shape differs");
            if (!backing.ControllerPresent && (value.SetAlias || value.TqsPresent || value.TqsIndices.Length != 0))
                throw Error("absent controller owns generic map dependencies");
            if (backing.ControllerPresent && value.TqsPresent != (value.SkeletonCount != 0))
                throw Error("SkeletonTQSMap presence differs from the avatar skeleton");
            for (int table = 0; table < 4; table++)
            {
                bool present = value.Tables[table].Rows.Length != 0 || value.Tables[table].OwnerOffset != -1;
                if (present != backing.BoundCurveTargets[table]) throw Error("generic curve presence differs from its typed table");
            }
            if (backing.SetPresent && backing.SetDependency != null)
            {
                for (int table = 1; table < 4; table++)
                {
                    if (value.Tables[table].Rows.Length != backing.Tables[table-1].Length)
                        throw Error("generic curve count differs from the owning set schema");
                }
                if (value.Tables[0].Rows.Length > backing.SetDependency.TransformCount)
                    throw Error("generic transform table exceeds its owning set schema");
                var schema = backing.SetDependency.FullSchema;
                if (backing.ParameterSchema.Length > schema.Length
                    || !backing.ParameterSchema.SequenceEqual(schema.Take(backing.ParameterSchema.Length)))
                    throw Error("controller dynamic schema is not its animation set prefix");
            }
            if (backing.ControllerPresent)
            {
                var counts = ClipBindingCounts(backing.ParameterSchema);
                var limits = new[] {counts[0],counts[1],counts[4]};
                for (int axis = 0; axis < 3; axis++)
                {
                    if (value.RootIndices[axis] < -1 || value.RootIndices[axis] >= limits[axis])
                        throw Error("generic root value index lies outside its schema");
                }
                for (int index = 0; index < value.TqsIndices.Length; index++)
                {
                    int item = value.TqsIndices[index];
                    if (item < -1 || item >= limits[index%3]) throw Error("SkeletonTQSMap index lies outside its schema");
                }
            }
            GenericTableOffsets(value.Optimized,value.ExposedCount,value.Tables.Select(table => table.Rows.Length).ToArray());
        }

        private static void RequireGenericDependenciesMatch(Backing source, Backing target)
        {
            ValidateGenericDependency(source);
            ValidateGenericDependency(target);
            Reject(GenericDependencyGaps(source).Concat(GenericDependencyGaps(target)));
            if ((source.GenericDependency == null) != (target.GenericDependency == null))
                throw Error("target generic binding payload presence differs");
            if (source.GenericDependency != null && target.GenericDependency != null
                && Json(source.GenericDependency) != Json(target.GenericDependency))
                throw Error("target generic binding structure or logical target differs");
        }

        private static void ValidateGenericDomains(Row row, AvatarNode[] nodes)
        {
            for (int domain = 0; domain < 2; domain++)
            {
                var value = row.Evaluation[domain].Backing.GenericDependency;
                if (value == null) continue;
                if (row.Fields.Length <= 7 || row.Fields[7].Length != 14)
                    throw Error("generic binding has no Animator hierarchy mode owner");
                if (value.Optimized != (row.Fields[7][13] == 0))
                    throw Error("generic binding constructor mode differs from its Animator");
                var dataset = row.Datasets[domain];
                int skeletonCount = 0;
                if (dataset.Edges[0] >= 0)
                {
                    int skeleton = nodes[dataset.Edges[0]].Edges[0];
                    if (skeleton >= 0) skeletonCount = nodes[skeleton].Extents[0];
                }
                int exposed = dataset.Edges[5] < 0 ? 0 : nodes[dataset.Edges[5]].Extents[1];
                if (value.SkeletonCount != skeletonCount || value.ExposedCount != exposed)
                    throw Error("generic map extents differ from their actual Avatar owners");
            }
        }

        private static IEnumerable<string> GenericDependencyGaps(Backing backing)
        {
            var value = backing.GenericDependency;
            if (value == null)
            {
                if (backing.GenericPresent || backing.SetPresent) yield return "generic-and-asset-binding-owner-allocation";
                if (backing.BoundCurveTargets.Any(present => present)) yield return "generic-native-bound-curves";
                yield break;
            }
            bool completeOwner = GenericLayoutKnown(value) && backing.ControllerPresent && backing.SetPresent
                && backing.SetDependency != null && value.SetAlias;
            if (!completeOwner) yield return "generic-and-asset-binding-owner-allocation";
            if (value.AllowConstantOptimization > 1 || value.ScaleChanges.Any(item => item > 1))
                yield return "noncanonical-generic-binding-bool";
            if (value.Tables[0].Rows.Any(row => row.Type < 1 || row.Type > 4 || row.TargetPointer || row.CustomBinding))
                yield return "generic-native-bound-curves:unsupported-transform-binding";
            var names = new[] {"transform","float","integer","object"};
            for (int table = 1; table < 4; table++)
            {
                if (value.Tables[table].Rows.Length != 0)
                    yield return "generic-native-bound-curves:unsupported-" + names[table] + "-target-storage";
            }
            if (value.Tables.SelectMany(table => table.Rows).Any(row => row.TargetObject == Unbound))
                yield return "generic-native-bound-curves:unresolved-target-object";
        }
    }
}
