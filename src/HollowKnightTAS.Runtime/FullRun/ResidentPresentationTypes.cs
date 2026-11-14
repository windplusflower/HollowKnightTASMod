using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Rendering;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1. D contains native
    // values and order; target allocation/owner pointers are Dependency. The
    // Object header is owned by ResidentObjectNativeState, never copied here.
    internal static partial class ResidentPresentationTypes
    {
        internal sealed class Data
        {
            public int Version = 1;
            public Row[] Rows = Array.Empty<Row>();
            public Registry Sorting = new Registry();
            public LightRegistry Lights = new LightRegistry();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Row
        {
            public string Owner = "", Kind = "", Font = "", Mesh = "", Text = "", Flare = "", Cookie = "";
            public bool Execution, MeshOwned;
            public bool StaticFontDependency, FontCacheDependency;
            public byte[][] Scalars = Array.Empty<byte[]>();
            // Light native resources are observed explicitly but not flattened
            // into serialized pointer bytes. Its full backend stays fail closed.
            public bool SharedLight, CookieResource, RenderEvents;
            public int LightReferences;
            public string CookieNativeTexture = "";
            public byte[] ShadowDistances = Array.Empty<byte>();
            public int[] LightEventCounts = Array.Empty<int>();
            public byte[][] LightEventMasks = Array.Empty<byte[]>();
        }
        internal sealed class Registry
        {
            public bool Present;
            public string[] Owners = Array.Empty<string>(), Dirty = Array.Empty<string>();
            public byte[] Values = Array.Empty<byte>();
            public int OwnerCapacity, ValueCapacity;
        }
        private static readonly int[][] CanvasRanges = { new[] {56,2}, new[] {64,7} };
        private static readonly int[][] SortingRanges = { new[] {56,2}, new[] {64,9}, new[] {76,8} };
        private static readonly int[][] TextRanges = { new[] {100,33} };
        private static readonly int[][] LightRanges = { new[] {56,2}, new[] {92,12} };
        private static int[][] Ranges(string kind) => kind == "canvas-group" ? CanvasRanges : kind == "sorting-group" ? SortingRanges : kind == "text-mesh" ? TextRanges : kind == "light" ? LightRanges : throw Error("unknown actual type");
        internal static bool Supports(Type type) => type == typeof(CanvasGroup) || type == typeof(SortingGroup) || type == typeof(TextMesh) || type == typeof(Light);
        internal static string Kind(Type type) => type == typeof(CanvasGroup) ? "canvas-group" : type == typeof(SortingGroup) ? "sorting-group" : type == typeof(TextMesh) ? "text-mesh" : type == typeof(Light) ? "light" : throw Error("unknown type");
        private static IntPtr SortingManager() => M.Ptr(IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x19c2808));
        private static byte[][] Scalars(IntPtr pointer, int[][] ranges) => ranges.Select(range => M.Read(IntPtr.Add(pointer, range[0]), range[1])).ToArray();
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            _ = LoadingEngineClockProbe.RequirePinnedEngine();
            var result = new Data(); var rows = new List<Row>();
            var fontDiagnostics = new Dictionary<IntPtr,FontCacheDiagnostic>();
            bool fontDiagnosticsTruncated = false;
            var reverse = directory.NativeBindings.ToDictionary(pair => M.Owner(pair.Value), pair => pair.Key);
            var witness = new Dictionary<IntPtr, byte[]>();
            byte[] Read(IntPtr address, int size) { var bytes = M.Read(address, size); witness[address] = bytes; return bytes; }
            string Id<T>(int id) where T : UnityEngine.Object {
                return ResidentNativeAssetReference.BindId(id,typeof(T),bind);
            }
            string Pointer(IntPtr pointer) {
                if (pointer == IntPtr.Zero) return "";
                return reverse.TryGetValue(pointer, out var key) ? key : "@unsupported";
            }
            foreach (var pair in directory.NativeBindings.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                if (!(pair.Value is Component component) || !Supports(component.GetType())) continue;
                var pointer = M.Owner(component); var kind = Kind(component.GetType());
                var bytes = Read(pointer, kind == "canvas-group" ? 72 : kind == "sorting-group" ? 112 : kind == "text-mesh" ? 144 : 104);
                var row = new Row { Owner = pair.Key, Kind = kind, Execution = directory.Ownership.IsExecution(component), Scalars = Ranges(kind).Select(range => M.Slice(bytes, range[0], range[1])).ToArray() };
                if (kind == "text-mesh") {
                    row.Text = M.String(IntPtr.Add(pointer, 56)); row.Font = Id<Font>(M.I32(bytes, 96));
                    if (row.Font.Length != 0) {
                        var font = ((TextMesh)component).font;
                        if (!ResidentObjectDirectory.IsLive(font) || font.GetInstanceID() != M.I32(bytes, 96))
                            throw Error("TextMesh font PPtr and actual font differ");
                        row.StaticFontDependency = !font.dynamic && row.Font.StartsWith("resident-asset/serialized/", StringComparison.Ordinal);
                        row.FontCacheDependency = font.dynamic;
                        // The dynamic Font already carries the existing atlas gap.
                        // Separate evidence only; never add native cache data to D.
                        if (!row.StaticFontDependency && font.dynamic && ResidentFailureEvidence.Collecting) {
                            var fontPointer = M.Owner(font);
                            if (!fontDiagnostics.ContainsKey(fontPointer)) {
                                if (fontDiagnostics.Count < 4) {
                                    fontDiagnostics.Add(fontPointer,CaptureFontCacheDiagnostic(fontPointer,row.Font,M.Read));
                                    ResidentFailureEvidence.RecordDomain("FontCache",new { Fonts = fontDiagnostics.Values.ToArray(), Truncated = fontDiagnosticsTruncated, Reason = fontDiagnosticsTruncated ? "Font diagnostic truncated at four distinct native owners" : "" });
                                } else {
                                    fontDiagnosticsTruncated = true;
                                    ResidentFailureEvidence.Mark("Font cache diagnostic truncated at four distinct native owners");
                                    ResidentFailureEvidence.RecordDomain("FontCache",new { Fonts = fontDiagnostics.Values.ToArray(), Truncated = true, Reason = "Font diagnostic truncated at four distinct native owners" });
                                }
                            }
                        }
                    }
                    var mesh = M.Ptr(bytes, 136); row.Mesh = ResidentNativeAssetReference.BindPointer(mesh,typeof(Mesh),bind);
                    if (mesh != IntPtr.Zero) { RequireUniqueTextMeshOwner(component, mesh); row.MeshOwned = true; }
                }
                if (kind == "light") {
                    row.Flare = Id<Flare>(M.I32(bytes, 88)); var shared = M.Ptr(bytes, 80); row.SharedLight = shared != IntPtr.Zero;
                    if (row.SharedLight) CaptureLightShared(row, shared, Read, id => ResidentNativeAssetReference.BindId(id,typeof(Texture),bind), bind);
                }
                rows.Add(row);
            }
            result.Rows = rows.ToArray();
            { // Global manager is observed even when this directory has no SortingGroup.
                var manager = SortingManager(); result.Sorting.Present = manager != IntPtr.Zero;
                if (manager != IntPtr.Zero) {
                    var body = Read(manager,80); var owners = ArrayInfo(body,0,8); var values = ArrayInfo(body,32,28);
                    result.Sorting.OwnerCapacity = owners.Item2; result.Sorting.ValueCapacity = values.Item2;
                    var ownerBytes = Read(M.Ptr(body,0),checked(owners.Item1*8));
                    result.Sorting.Owners = Enumerable.Range(0,owners.Item1).Select(index => Pointer(M.Ptr(ownerBytes,index*8))).ToArray();
                    result.Sorting.Values = Read(M.Ptr(body,32),checked(values.Item1*28));
                    var root = IntPtr.Add(manager,64); var current = M.Ptr(body,72); var previous = root; var dirty = new List<string>(); var seen = new HashSet<IntPtr>();
                    while (current != root) {
                        if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > 100000) throw Error("invalid sorting dirty list");
                        var node = Read(current,24); if (M.Ptr(node,0) != previous) throw Error("sorting dirty back link differs");
                        var owner = M.Ptr(node,16); if (IntPtr.Add(owner,88) != current) throw Error("sorting dirty node owner differs");
                        dirty.Add(Pointer(owner)); previous = current; current = M.Ptr(node,8);
                    }
                    if (M.Ptr(body,64) != previous) throw Error("sorting dirty tail differs");
                    result.Sorting.Dirty = dirty.ToArray();
                    foreach (var row in result.Rows.Where(row => row.Kind == "sorting-group")) {
                        var owner = M.Owner(directory.NativeBindings[row.Owner]); var node = Read(IntPtr.Add(owner,88),24);
                        if (M.Ptr(node,16) != owner) throw Error("sorting node retained owner differs");
                        var linked = M.Ptr(node,0) != IntPtr.Zero || M.Ptr(node,8) != IntPtr.Zero;
                        if (linked != dirty.Contains(row.Owner)) throw Error("sorting node is linked outside complete manager list");
                    }
                }
            }
            result.Lights = CaptureLightRegistry(result.Rows, directory, Read);
            foreach (var pair in witness) if (!M.Read(pair.Key,pair.Value.Length).SequenceEqual(pair.Value)) throw Error("native body changed during capture");
            foreach (var row in result.Rows.Where(row => row.Kind == "text-mesh"))
                if (M.String(IntPtr.Add(M.Owner(directory.NativeBindings[row.Owner]),56)) != row.Text) throw Error("text changed during capture");
            result.CoverageGaps = RequiredCoverageGaps(result); Validate(result); return result;
        }
        private static Tuple<int,int> ArrayInfo(byte[] bytes,int offset,int stride)
        {
            var size=M.U64(bytes,offset+16); var capacity=M.U64(bytes,offset+24)>>1;
            if(size>capacity || capacity>100000 || capacity*(ulong)stride>32*1024*1024 || size!=0 && M.Ptr(bytes,offset)==IntPtr.Zero) throw Error("invalid sorting array");
            return Tuple.Create((int)size,(int)capacity);
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            var gaps=new SortedSet<string>(StringComparer.Ordinal);
            foreach(var row in data.Rows) {
                if(row.Execution) gaps.Add(row.Kind+"/execution-native-domain");
                if(row.Kind=="light") { foreach(var gap in LightGaps(row)) gaps.Add(gap); if(!data.Lights.Present) gaps.Add("light/manager-absent"); }
                if(row.Kind=="text-mesh" && row.Font.Length!=0 && !row.StaticFontDependency && !row.FontCacheDependency) gaps.Add("text-mesh/font-native-glyph-atlas-and-rebuild-callbacks");
                if(row.Kind=="text-mesh" && row.Mesh.Length!=0 && !row.MeshOwned) gaps.Add("text-mesh/retained-mesh-ownership");
                if(row.Kind=="sorting-group" && !data.Sorting.Present) gaps.Add("sorting-group/manager-absent");
            }
            if(data.Sorting.Owners.Concat(data.Sorting.Dirty).Contains("@unsupported")) gaps.Add("sorting-group/nonresident-manager-owner");
            foreach(var gap in LightRegistryGaps(data.Lights)) gaps.Add(gap);
            return gaps.ToArray();
        }
        internal static void Validate(Data data)
        {
            if(data==null || data.Version!=1 || data.Rows==null || data.Sorting==null || data.Lights==null || data.CoverageGaps==null || data.Rows.Length>100000) throw Error("snapshot shape differs");
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in data.Rows) {
                if(row==null || string.IsNullOrEmpty(row.Owner) || !keys.Add(row.Owner) || row.Scalars==null || row.Text==null || row.Font==null || row.Mesh==null || row.Flare==null || row.Cookie==null) throw Error("row shape differs");
                var ranges=Ranges(row.Kind); var sizes=ranges.Select(range=>range[1]).Concat(row.Kind=="light" && row.SharedLight ? new[]{102,44,5,96,64,20,156}:Array.Empty<int>()).ToArray();
                if(row.Scalars.Length!=sizes.Length || row.Scalars.Where((bytes,index)=>bytes==null || bytes.Length!=sizes[index]).Any()) throw Error("native scalar layout differs");
                if(row.MeshOwned && (row.Kind!="text-mesh" || row.Mesh.Length==0)) throw Error("owned mesh slot shape differs");
                if(row.StaticFontDependency && (row.Kind!="text-mesh" || !row.Font.StartsWith("resident-asset/serialized/", StringComparison.Ordinal)))
                    throw Error("static font dependency lacks a serialized Font binding");
                if(row.FontCacheDependency && (row.Kind != "text-mesh" || row.Font.Length == 0 || row.StaticFontDependency))
                    throw Error("Font cache dependency marker has no actual TextMesh Font reference");
                if(row.Kind!="text-mesh" && (row.Text.Length!=0 || row.Font.Length!=0 || row.Mesh.Length!=0)) throw Error("text references on another type");
                if(row.Kind!="light" && (row.SharedLight || row.CookieResource || row.RenderEvents || row.LightReferences!=0 || row.Flare.Length!=0 || row.Cookie.Length!=0)) throw Error("light resources on another type");
                if(row.Kind=="canvas-group" && row.Scalars[1].Skip(4).Any(value=>value>1)) throw Error("CanvasGroup booleans differ");
                if(row.Kind!="text-mesh" && row.Scalars[0].Any(value=>value>1)) throw Error("Behaviour bits differ");
            }
            ValidateLights(data);
            var registry=data.Sorting;
            if(registry.Owners==null || registry.Dirty==null || registry.Values==null || registry.Owners.Length>100000 || registry.Values.Length%28!=0 || registry.Values.Length/28>100000 || registry.OwnerCapacity<registry.Owners.Length || registry.OwnerCapacity>100000 || registry.ValueCapacity<registry.Values.Length/28 || registry.ValueCapacity>100000) throw Error("sorting registry shape differs");
            if(!registry.Present && (registry.Owners.Length!=0 || registry.Dirty.Length!=0 || registry.Values.Length!=0 || registry.OwnerCapacity!=0 || registry.ValueCapacity!=0)) throw Error("absent sorting manager has data");
            if(registry.Owners.Distinct(StringComparer.Ordinal).Count()!=registry.Owners.Length || registry.Dirty.Distinct(StringComparer.Ordinal).Count()!=registry.Dirty.Length) throw Error("sorting duplicate registration");
            foreach(var key in registry.Owners.Concat(registry.Dirty).Where(key=>key!="@unsupported"))
                if(!data.Rows.Any(row=>row.Owner==key && row.Kind=="sorting-group")) throw Error("registry owner is not actual SortingGroup");
            foreach(var row in data.Rows.Where(row=>row.Kind=="sorting-group")) {
                var index=M.U32(row.Scalars[2],4); var expected=Array.IndexOf(registry.Owners,row.Owner);
                if(index != (expected<0 ? 0xfffffU : (uint)expected)) throw Error("sorting manager index differs");
            }
        }
        internal static void RequireCompleteCoverage(Data data) { Validate(data); var gaps=RequiredCoverageGaps(data).Concat(data.CoverageGaps).Distinct().ToArray(); if(gaps.Length!=0) throw Error("coverage gaps: "+string.Join(",",gaps)); }
        internal static void RequireCanonicalizableCoverage(Data data)
        {
            Validate(data); var allowed="light/shared-data-nonunique-references";
            var gaps=RequiredCoverageGaps(data).Concat(data.CoverageGaps).Where(gap=>gap!=allowed).Distinct().ToArray();
            if(gaps.Length!=0) throw Error("native state cannot enter common Canon: "+string.Join(",",gaps));
        }
        internal static void RequireCanonicalState(Data data) => RequireCompleteCoverage(data);
        internal static void RequireEqual(Data expected,Data actual)
        { Validate(expected); Validate(actual); if(Comparison(expected)!=Comparison(actual)) throw Error("independent native readback differs"); }
        private static string Comparison(Data data)
        {
            // Capacity belongs to the target allocator, and is validated but
            // cannot transport semantic history between processes.
            return JsonConvert.SerializeObject(new { data.Version, data.Rows,
                data.Sorting.Present, data.Sorting.Owners, data.Sorting.Dirty,
                data.Sorting.Values, data.Lights, Gaps = RequiredCoverageGaps(data) });
        }
        internal static Action Canon(ResidentObjectDirectory directory,ResidentMutationScope scope,Func<object,string?> bind,Action admitted)
        {
            scope.Require(); admitted(); var state=Capture(directory,bind); RequireCanonicalizableCoverage(state);
            foreach(var row in state.Rows.Where(row=>row.Kind=="light" && row.SharedLight)) {
                var owner=(Component)directory.Resolve(row.Owner); if(!scope.Owns(owner)) throw Error("Light Canon owner is not owned");
                MakeLightUnique(M.Owner(owner), admitted);
            }
            foreach(var row in state.Rows.Where(row=>row.Kind=="light" && row.SharedLight)) row.LightReferences=1;
            state.CoverageGaps=RequiredCoverageGaps(state);
            var after=Capture(directory,bind); RequireCompleteCoverage(after); RequireEqual(state,after);
            state=after;
            // These bodies have no history storage to clear. Preserve all D,
            // including pending sorting work. Text string capacity is target
            // ownership, not observable text; strings rebuild only on Apply.
            return ()=>{scope.Require(); admitted(); RequireEqual(state,Capture(directory,bind));};
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident presentation types: "+reason);
    }
}
