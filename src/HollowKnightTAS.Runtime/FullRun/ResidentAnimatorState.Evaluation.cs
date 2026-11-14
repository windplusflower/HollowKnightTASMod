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
        internal sealed class Evaluation
        {
            public bool Dormant;
            public Cache Cache = new Cache();
            public Backing Backing = new Backing();
        }
        internal sealed class Cache
        {
            public byte[][] Fields = EvalSizes.Select(n => new byte[n]).ToArray();
            public int[] Edges = Enumerable.Repeat(-1,EvalEdges.Length).ToArray();
            public string Hierarchy = "";
            public bool IndexTable;
        }
        internal sealed class Backing
        {
            public bool GenericPresent, SetPresent, ControllerPresent, ClipSetPresent;
            public Generic[][] Tables = {Array.Empty<Generic>(),Array.Empty<Generic>(),Array.Empty<Generic>()};
            public string[] Clips = Array.Empty<string>();
            public int[] OptimizedCurves = Array.Empty<int>(), ClipMaps = Array.Empty<int>();
            public byte[] ParameterSchema = Array.Empty<byte>();
            public SetBindingDependency? SetDependency;
            public int DefaultValues = -1;
            public GenericOwnerDependency? GenericDependency;
            public bool[] BoundCurveTargets = new bool[4];
        }
        private static readonly int[] EvalEdges = {8,16,24,32,48,64,80,88,96,144,152};
        private static readonly int[] EvalOffsets = {0,40,56,72,104,132,168};
        private static readonly int[] EvalSizes = {4,8,8,8,27,5,8};
        private sealed class EvaluationMarks { internal int Count; }
        private static readonly ConditionalWeakTable<Reader,EvaluationMarks> evaluationMarks = new ConditionalWeakTable<Reader,EvaluationMarks>();
        private static void MarkAnimatorScalars(Reader reader,IEnumerable<string> gaps)
        {
            if (!ResidentFailureEvidence.Collecting) return;
            var marks = evaluationMarks.GetValue(reader,_ => new EvaluationMarks());
            foreach (var gap in gaps.Take(Math.Max(0,3-marks.Count))) {
                ResidentFailureEvidence.Mark("Animator "+gap); marks.Count++;
            }
        }
        // Fixed-PDB AnimationPlayableEvaluationConstant (184 bytes): these
        // ranges contain fields only. Padding at 4..7, 131 and 137..143 is
        // deliberately excluded; integer indices and versions remain exact D.
        private static byte[][] EvaluationFields(byte[] body,int offset)
        {
            if (body == null || offset < 0 || offset > body.Length - 184) throw Error("evaluation native body extent differs");
            return EvalOffsets.Select((off,i) => M.Slice(body,offset+off,EvalSizes[i])).ToArray();
        }
        // AvatarDataSet::Reset clears its six owners and lifetime words.
        // DeallocatePlayableEvaluationInput clears only its two owned masks.
        // Borrowed avatar/root pointers and scalar inputs can retain freed or
        // never-initialized bytes until ProcessAnimationsStep fills them again.
        private static bool RetiredEvaluation(byte[] body, int avatarOffset, int bindingsOffset, int inputOffset)
        {
            return Enumerable.Range(0,6).All(index => M.Ptr(body,avatarOffset + index * 8) == IntPtr.Zero)
                && M.U64(body,avatarOffset + 48) == 0 && body[avatarOffset + 56] == 0 && body[avatarOffset + 57] == 0
                && M.Ptr(body,bindingsOffset) == IntPtr.Zero && M.Ptr(body,bindingsOffset + 24) == IntPtr.Zero
                && M.Ptr(body,inputOffset + 8) == IntPtr.Zero && M.Ptr(body,inputOffset + 32) == IntPtr.Zero;
        }
        private sealed class EvalView
        {
            internal readonly Evaluation Data = new Evaluation();
            private readonly IntPtr location;
            private readonly IntPtr[] dependencies = new IntPtr[EvalEdges.Length];
            internal EvalView(IntPtr owner, byte[] body, int cacheOffset, int avatarOffset, int bindingsOffset, Reader reader, bool dormant)
            {
                location = IntPtr.Add(owner,cacheOffset);
                if (dormant) { Data.Dormant = true; return; }
                var cached = Data.Cache; var backing = Data.Backing;
                cached.Fields = EvaluationFields(body,cacheOffset);
                MarkAnimatorScalars(reader,EvaluationBooleanGaps(cached).Select(gap => "evaluation +"+cacheOffset+": "+gap));
                dependencies[3] = M.Ptr(body,avatarOffset); dependencies[9] = owner; dependencies[10] = IntPtr.Add(owner,64);
                var generic = M.Ptr(body,bindingsOffset);
                if (generic != IntPtr.Zero) {
                    backing.GenericPresent = true; var bytes = reader.Memory.Read(generic,96);
                    for (int i = 0; i < 4; i++) backing.BoundCurveTargets[i] = M.U64(bytes,i*16) != 0 || M.Ptr(bytes,i*16+8) != IntPtr.Zero;
                    var controller = M.Ptr(bytes,72); dependencies[2] = controller;
                    if (controller != IntPtr.Zero) {
                        backing.ControllerPresent = true; var bindings = reader.Memory.Read(controller,32);
                        dependencies[0] = M.Ptr(bindings,0); dependencies[1] = M.Ptr(bindings,8); dependencies[7] = M.Ptr(bindings,16);
                        backing.DefaultValues = reader.Live.Avatars!.Retained(dependencies[1],AvatarKind.Values);
                        if (dependencies[0] != IntPtr.Zero)
                            backing.ParameterSchema = reader.BankSnapshot(dependencies[0],12);
                    }
                }
                var set = M.Ptr(body,bindingsOffset+24);
                if (set != IntPtr.Zero) {
                    backing.SetPresent = true; var bytes = reader.Memory.Read(set,96);
                    backing.SetDependency = CaptureSetBindingDependency(reader,bytes);
                    for (int i = 0; i < 3; i++) {
                        var count = Size(M.U64(bytes,i*16)); var table = M.Ptr(bytes,i*16+8); dependencies[i+4] = table;
                        backing.Tables[i] = Decode(ReadBuffer(reader,table,count,20),20,(array,offset) => GenericBinding(reader,array,offset));
                    }
                    var clips = M.Ptr(bytes,88);
                    if (clips != IntPtr.Zero) {
                        backing.ClipSetPresent = true; var info = reader.Memory.Read(clips,40);
                        CaptureAnimationSetDependency(reader,info,backing.SetDependency);
                        var clipBindingCounts = ClipBindingCounts(backing.SetDependency.FullSchema);
                        var count = Count(M.U32(info,0)); var array = M.Ptr(info,8); dependencies[8] = array;
                        var entries = ReadBuffer(reader,array,count,88);
                        backing.Clips = new string[count]; backing.ClipMaps = new int[count]; backing.OptimizedCurves = new int[count];
                        for (int i = 0; i < count; i++) {
                            int offset = i * 88; backing.Clips[i] = reader.Key<AnimationClip>(M.Ptr(entries,offset));
                            backing.OptimizedCurves[i] = Count(M.U32(entries,offset+8));
                            RegisterClipDependency(reader, M.Ptr(entries,offset), IntPtr.Add(array,offset+16),
                                reader.Key<Animator>(owner) + "/" + cacheOffset + "/" + i, backing.Clips[i]);
                            for (int j = 0; j < 9; j++) if (M.Ptr(entries,offset+16+j*8) != IntPtr.Zero) backing.ClipMaps[i] |= 1 << j;
                            backing.SetDependency.ClipBindings.Add(CaptureClipCurveMapDependency(reader,M.Ptr(entries,offset),entries,offset+16,clipBindingCounts));
                        }
                    }
                }
                if (generic != IntPtr.Zero)
                    backing.GenericDependency = CaptureGenericDependency(reader,generic,set,M.Ptr(body,avatarOffset),M.Ptr(body,avatarOffset+40),body[1485] == 0);
                // SetupPlayableWorkspace selects these exact backing fields.
                // Compare stale caches by pointer equality only. Never follow
                // an obsolete cached clip/index/generic binding allocation.
                cached.Edges = CacheEdges(body,cacheOffset,dependencies);
                cached.Hierarchy = reader.Hierarchy(M.Ptr(body,cacheOffset+160)); cached.IndexTable = M.Ptr(body,cacheOffset+176) != IntPtr.Zero;
            }
            private static int Size(ulong count) => count <= MaximumEntries ? (int)count : throw Error("animation set binding count exceeds bound");
            private static byte[] ReadBuffer(Reader reader, IntPtr data, int count, int stride)
            { if (count != 0 && data == IntPtr.Zero) throw Error("animation binding storage absent"); return reader.Memory.Read(data,checked(count * stride)); }
            internal void Prepare(Evaluation source,Func<string,NativeObject> resolve,Func<object,string?> bind,List<PendingWrite> writes)
            {
                ValidateEvaluation(source);
                if (source.Dormant != Data.Dormant) throw Error("evaluation cache lifetime differs");
                if (source.Dormant) return;
                RequireGenericDependenciesMatch(source.Backing,Data.Backing);
                Reject(EvaluationBooleanGaps(source.Cache).Concat(EvaluationBooleanGaps(Data.Cache))
                    .Concat(source.Backing.Tables.SelectMany(table => table).Concat(Data.Backing.Tables.SelectMany(table => table)).SelectMany(GenericScalarGaps)));
                if (Json(source.Backing) != Json(Data.Backing) || Data.Cache.IndexTable || source.Cache.IndexTable)
                    throw Error("evaluation asset backing or borrowed transform-index ownership differs");
                for (int i = 0; i < EvalEdges.Length; i++) {
                    var edge = source.Cache.Edges[i];
                    if (edge == UnknownEdge || Data.Cache.Edges[i] == UnknownEdge || edge == 0 && dependencies[i] == IntPtr.Zero) throw Error("stale evaluation edge cannot be rebound");
                    var at = IntPtr.Add(location,EvalEdges[i]); var value = edge == -1 ? IntPtr.Zero : dependencies[i];
                    writes.Add(new PendingWrite(at,M.Read(at,8),BitConverter.GetBytes(value.ToInt64())));
                }
                for (int i = 0; i < EvalOffsets.Length; i++)
                    writes.Add(new PendingWrite(IntPtr.Add(location,EvalOffsets[i]),Data.Cache.Fields[i],source.Cache.Fields[i]));
                var hierarchy = IntPtr.Zero;
                if (source.Cache.Hierarchy.Length != 0) {
                    var member = ResidentNativeAssetReference.ResolveTyped(source.Cache.Hierarchy,typeof(Transform),resolve);
                    if (ResidentNativeAssetReference.BindPointer(member,typeof(Transform),bind) != source.Cache.Hierarchy) throw Error("evaluation hierarchy binding has no target member");
                    hierarchy = M.Ptr(IntPtr.Add(member,56));
                    if (hierarchy == IntPtr.Zero) throw Error("evaluation hierarchy absent on target");
                }
                var slot = IntPtr.Add(location,160); writes.Add(new PendingWrite(slot,M.Read(slot,8),BitConverter.GetBytes(hierarchy.ToInt64())));
            }
        }
        private static Generic GenericBinding(Reader reader,byte[] bytes,int offset) => new Generic { Path = M.U32(bytes,offset), Attribute = M.U32(bytes,offset+4),
            Script = reader.Id<NativeObject>(M.I32(bytes,offset+8)), Type = M.I32(bytes,offset+12), Custom = bytes[offset+16],
            ObjectCurve = bytes[offset+17], IntCurve = bytes[offset+18] };
        private static int[] CacheEdges(byte[] body,int offset,IntPtr[] dependencies)
        {
            var result = new int[EvalEdges.Length];
            for (int i = 0; i < result.Length; i++) result[i] = Edge(M.Ptr(body,offset+EvalEdges[i]),dependencies[i]);
            return result;
        }
        private static void ValidateEvaluation(Evaluation value)
        {
            if (value == null || value.Backing == null || value.Cache == null) throw Error("evaluation backing/cached domain absent");
            var b = value.Backing; var c = value.Cache;
            if (c.Edges == null || c.Edges.Length != EvalEdges.Length) throw Error("evaluation native cache edge count differs");
            for (int i = 0; i < c.Edges.Length; i++)
                if (c.Edges[i] < UnknownEdge || c.Edges[i] > 0) throw Error("evaluation native cache edge at +"+EvalEdges[i]+" differs: "+c.Edges[i]);
            if (!Text(c.Hierarchy)) throw Error("evaluation native cache hierarchy key differs");
            if (c.Fields == null || c.Fields.Length != EvalSizes.Length) throw Error("evaluation native cache field-range count differs");
            for (int i = 0; i < EvalSizes.Length; i++)
                if (c.Fields[i] == null || c.Fields[i].Length != EvalSizes[i])
                    throw Error("evaluation native cache field range at +"+EvalOffsets[i]+" has length "+(c.Fields[i] == null ? -1 : c.Fields[i].Length)+", expected "+EvalSizes[i]);
            if (b.Tables == null || b.Tables.Length != 3 || b.Tables.Any(table => table == null || table.Length > MaximumEntries)
                || !Keys(b.Clips) || b.Clips.Length > MaximumEntries || b.OptimizedCurves == null || b.OptimizedCurves.Length != b.Clips.Length
                || b.OptimizedCurves.Any(count => count < 0 || count > MaximumEntries) || b.ClipMaps == null || b.ClipMaps.Length != b.Clips.Length
                || b.ClipMaps.Any(mask => mask < 0 || mask > 511) || b.BoundCurveTargets == null || b.BoundCurveTargets.Length != 4) throw Error("evaluation binding registry differs");
            BankSize(b.ParameterSchema,12);
            ValidateSetBindingDependency(b);
            ValidateGenericDependency(b);
            foreach (var table in b.Tables) foreach (var item in table)
                if (item == null || !Text(item.Script)) throw Error("evaluation generic script binding differs");
            if (b.DefaultValues < -1 || !b.ControllerPresent && b.DefaultValues != -1) throw Error("evaluation default value root differs");
            if (!b.GenericPresent && (b.ControllerPresent || b.BoundCurveTargets.Any(bit => bit)) || !b.SetPresent && (b.ClipSetPresent || b.Tables.Any(t => t.Length != 0))
                || !b.ClipSetPresent && b.Clips.Length != 0 || !b.ControllerPresent && b.ParameterSchema.Length != 0) throw Error("absent evaluation binding owns native tables");
            if (value.Dormant && (Json(value.Cache) != Json(new Cache()) || Json(value.Backing) != Json(new Backing())))
                throw Error("dormant evaluation cache retains active state");
        }
        private static IEnumerable<string> EvaluationGaps(Evaluation value)
        {
            var b = value.Backing; var c = value.Cache;
            foreach (var gap in EvaluationBooleanGaps(c)) yield return gap;
            foreach (var item in b.Tables.SelectMany(table => table)) foreach (var gap in GenericScalarGaps(item)) yield return gap;
            if (c.Edges.Contains(UnknownEdge)) yield return "stale-evaluation-cache-edge";
            foreach (var gap in GenericDependencyGaps(b)) yield return gap;
            if (b.ClipMaps.Any(mask => mask != 0) && b.SetDependency == null) yield return "clip-curve-index-and-integer-remap";
            if (b.SetDependency != null && b.SetDependency.ClipBindings.Any(clip => clip.Objects.Contains(Unbound))) yield return "unbound-clip-integer-remap-object";
            if (b.Tables.SelectMany(table => table).Any(item => item.Script == Unbound) || b.Clips.Contains(Unbound) || c.Hierarchy == Unbound)
                yield return "unbound-evaluation-resource-or-hierarchy";
            for (int i = 0; i < 3; i++) {
                var count = M.U64(c.Fields[1+i],0);
                if (count != 0 && (c.Edges[4+i] != 0 || count > (ulong)b.Tables[i].Length)) yield return "cached-generic-binding-count-outside-backing";
            }
            if (M.U32(c.Fields[4],0) != 0 && (c.Edges[8] != 0 || M.U32(c.Fields[4],0) > b.Clips.Length)) yield return "cached-clip-count-outside-backing";
            if (c.IndexTable || M.U64(c.Fields[6],0) != 0) yield return "borrowed-transform-index-table";
        }
        private static IEnumerable<string> EvaluationBooleanGaps(Cache cache)
        {
            foreach (var entry in new[] {Tuple.Create(4,24,"hasRootTransformValues",128),Tuple.Create(4,25,"allowConstantCurveOptimization",129),
                Tuple.Create(4,26,"isHuman",130),Tuple.Create(5,4,"affectMassCenter",136)}) {
                var value = cache.Fields[entry.Item1][entry.Item2];
                if (value > 1) yield return "noncanonical-evaluation-bool:"+entry.Item3+":+"+entry.Item4+"="+value;
            }
        }
    }
}
