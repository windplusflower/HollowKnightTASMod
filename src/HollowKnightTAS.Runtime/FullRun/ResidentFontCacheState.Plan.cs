using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentFontCacheState
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr AllocatePod(UIntPtr size, UIntPtr alignment, int label, int options, IntPtr file, int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreePod(IntPtr pointer, int label, IntPtr file, int line);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EraseTree(IntPtr tree, IntPtr allocator, IntPtr root);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InsertPosition(IntPtr tree, IntPtr result, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FlushGenerators();

        private sealed class NativeOperations
        {
            private readonly AllocatePod allocate;
            private readonly FreePod free;
            internal readonly EraseTree Erase;
            internal readonly InsertPosition Insert;
            internal readonly FlushGenerators Flush;
            private readonly IntPtr allocatorFile;

            internal NativeOperations()
            {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                // All native entries are checked before the first allocation or cache mutation.
                ResidentLoadedAssetIndex.RequireCode(engine, 0x56b6a0, 32, "44fc12689eabeb9bb4855c72bacb1f77e2fa422a95a4ac4781857914592ca30d");
                ResidentLoadedAssetIndex.RequireCode(engine, 0x56b640, 32, "650954ac999d4bc59974b76371ebc4f80ee1d7f58ce21d0ea7948a18181326b7");
                ResidentLoadedAssetIndex.RequireCode(engine, 0xc5f970, 32, "bccf6b64c0c5d2115fe8916c2228b89c2ea6dad7ceea1cd7f4c56a245b770b4e");
                ResidentLoadedAssetIndex.RequireCode(engine, 0xc60400, 32, "8a76c278c7de7cc3eaddfd26560cde847366012042e569c1291f6981c4c277f2");
                ResidentLoadedAssetIndex.RequireCode(engine, 0x7feeb0, 32, "89cf4b7513f830e5dbbbe7ccb4ccd639dba97afec98b295463574369365c0d6a");
                ResidentLoadedAssetIndex.RequireCode(engine, 0xc63a00, 32, "f713b3feca12ee2d2661ce23153969f8bfa1e9bd769d7065a3692fc6da3a9597");
                Flush = (FlushGenerators)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xc63a00), typeof(FlushGenerators));
                allocate = (AllocatePod)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b6a0), typeof(AllocatePod));
                free = (FreePod)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x56b640), typeof(FreePod));
                Erase = (EraseTree)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xc5f970), typeof(EraseTree));
                Insert = (InsertPosition)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xc60400), typeof(InsertPosition));
                // Existing pinned stl_allocator source literal, also used by the original Font containers.
                allocatorFile = IntPtr.Add(engine, 0x15c6004);
            }
            internal IntPtr Allocate(int bytes)
            {
                if (bytes == 0) return IntPtr.Zero;
                if (bytes < 0 || bytes > MaximumBytes) throw Error("POD allocation exceeds bound");
                var pointer = allocate(new UIntPtr((uint)bytes), new UIntPtr(16), 39, 0, allocatorFile, 94);
                if (pointer == IntPtr.Zero) throw Error("Font POD allocation failed");
                if ((pointer.ToInt64() & 15) != 0) {
                    Free(pointer);
                    throw Error("Font POD allocation is misaligned");
                }
                return pointer;
            }
            internal void Free(IntPtr pointer)
            {
                if (pointer != IntPtr.Zero) free(pointer, 39, allocatorFile, 99);
            }
        }
        private sealed class NativeLayout
        {
            internal readonly IntPtr Owner, Impl;
            internal readonly byte[] AsciiHeader;
            internal readonly SetView Packing;
            private readonly List<(long Start, long End)> ranges = new List<(long, long)>();

            internal NativeLayout(Font owner)
            {
                Owner = M.Owner(owner);
                Impl = M.Ptr(M.Read(Owner, 80), 72);
                if (Impl == IntPtr.Zero) throw Error("target FontImpl is absent");
                AddRange(Owner, 80);
                AddRange(Impl, 320);
                AsciiHeader = M.Read(IntPtr.Add(Impl, 64), 32);
                ulong capacity = M.U64(AsciiHeader, 24) >> 1;
                int count = Count(capacity, 60);
                if (M.U64(AsciiHeader, 16) > capacity || (M.U64(AsciiHeader, 24) & 1) != 0 && M.Ptr(AsciiHeader, 0) != IntPtr.Zero)
                    throw Error("target ASCII storage is borrowed or malformed");
                AddRange(M.Ptr(AsciiHeader, 0), checked(count * 60));
                CheckVector(IntPtr.Add(Impl, 96), 60);
                CheckVector(IntPtr.Add(Impl, 224), 16);
                // Also protect read-only dependencies against accidental aliasing of mutable storage.
                CheckVector(IntPtr.Add(Impl, 8), 8);
                CheckArray(IntPtr.Add(Impl, 128), 1);
                CheckArray(IntPtr.Add(Impl, 160), 40);
                CheckArray(IntPtr.Add(Impl, 192), 4);
                Packing = ReadTree(IntPtr.Add(Impl, 248), 8, 28);
                AddRange(Packing.Head, 32);
                foreach (var node in Packing.Nodes) AddRange(node, 40);
            }
            private void CheckVector(IntPtr header, int stride)
            {
                var bytes = M.Read(header, 24);
                long start = M.Ptr(bytes, 0).ToInt64(), last = M.Ptr(bytes, 8).ToInt64(), end = M.Ptr(bytes, 16).ToInt64();
                if (last < start || end < last || (last - start) % stride != 0 || (end - start) % stride != 0)
                    throw Error("target vector ownership/extent differs");
                Count((ulong)((end - start) / stride), stride);
                if (start != 0 && (start & 15) != 0) throw Error("target POD vector alignment differs");
                AddRange(new IntPtr(start), checked((int)(end - start)));
            }
            private void CheckArray(IntPtr header, int stride)
            {
                var bytes = M.Read(header, 32);
                ulong size = M.U64(bytes, 16), capacity = M.U64(bytes, 24) >> 1;
                if (size > capacity || capacity > (ulong)int.MaxValue / (uint)stride) throw Error("readonly Font array extent differs");
                AddRange(M.Ptr(bytes, 0), checked((int)capacity * stride));
            }
            private void AddRange(IntPtr pointer, int length)
            {
                if (length == 0) return;
                long start = pointer.ToInt64(), end = checked(start + length);
                if (pointer == IntPtr.Zero || ranges.Any(range => start < range.End && end > range.Start))
                    throw Error("target Font allocations overlap or lack storage");
                ranges.Add((start, end));
            }
        }
        private sealed class VectorReplacement : IDisposable
        {
            private readonly NativeOperations operations;
            private readonly IntPtr header, previous;
            private readonly int bytes;
            private IntPtr pending;
            private readonly Action<IntPtr,byte[]> publish;
            private bool publicationAttempted;
            internal VectorReplacement(NativeOperations operations, IntPtr header, byte[] contents)
                : this(operations,header,contents,M.Write) { }
            internal VectorReplacement(NativeOperations operations, IntPtr header, byte[] contents, Action<IntPtr,byte[]> publish)
            {
                this.operations = operations;
                this.header = header;
                this.publish = publish ?? throw new ArgumentNullException(nameof(publish));
                previous = M.Ptr(M.Read(header, 24), 0);
                bytes = contents.Length;
                pending = operations.Allocate(bytes);
                try { if (bytes != 0) M.Write(pending, contents); }
                catch { operations.Free(pending); pending = IntPtr.Zero; throw; }
            }
            internal void Publish()
            {
                if (publicationAttempted) throw Error("vector publication was attempted twice");
                var body = new byte[24];
                BitConverter.GetBytes(pending.ToInt64()).CopyTo(body, 0);
                BitConverter.GetBytes(pending == IntPtr.Zero ? 0L : checked(pending.ToInt64() + bytes)).CopyTo(body, 8);
                Buffer.BlockCopy(body, 8, body, 16, 8);
                // M.Write can publish successfully and then fail its readback. Ownership is
                // indeterminate once publication starts: never free this allocation in Dispose.
                // A failed transaction retains it for fail-closed process retirement.
                publicationAttempted = true;
                pending = IntPtr.Zero;
                publish(header, body);
                operations.Free(previous);
            }
            public void Dispose()
            {
                operations.Free(pending);
                pending = IntPtr.Zero;
            }
        }
        private static byte[] ExpandRecords(byte[][] records, int stride)
        {
            int length = checked(records.Length * stride);
            if (length > MaximumBytes) throw Error("expanded cache exceeds bound");
            var bytes = new byte[length];
            for (int i = 0; i < records.Length; i++) records[i].CopyTo(bytes, i * stride);
            return bytes;
        }
        private static void RestorePacking(IntPtr impl, Data source, NativeOperations operations)
        {
            var tree = IntPtr.Add(impl, 248);
            var before = ReadTree(tree, 8, 28);
            operations.Erase(tree, tree, M.Ptr(M.Read(before.Head, 24), 8));
            for (int offset = 0; offset < 24; offset += 8)
                M.Write(IntPtr.Add(before.Head, offset), BitConverter.GetBytes(before.Head.ToInt64()));
            M.Write(IntPtr.Add(tree, 8), new byte[8]);
            M.Write(IntPtr.Add(impl, 264), BitConverter.GetBytes(before.Head.ToInt64()));
            var scratch = Marshal.AllocHGlobal(24);
            try {
                foreach (var key in source.Positions) {
                    var value = BitConverter.GetBytes(key[0]).Concat(BitConverter.GetBytes(key[1])).ToArray();
                    M.Write(IntPtr.Add(scratch, 16), value);
                    operations.Insert(tree, scratch, IntPtr.Add(scratch, 16));
                    if (M.Read(IntPtr.Add(scratch, 8), 1)[0] != 1) throw Error("packing key was not inserted uniquely");
                }
            } finally { Marshal.FreeHGlobal(scratch); }
            var after = ReadTree(tree, 8, 28);
            var actual = after.Values.Select(value => new[] {M.I32(value, 0), M.I32(value, 4)}).ToArray();
            if (actual.Length != source.Positions.Length || actual.Where((value, i) => !value.SequenceEqual(source.Positions[i])).Any())
                throw Error("target packing keys differ after insertion");
            var iterator = after.Head;
            if (!source.IteratorAtEnd) {
                int index = Array.FindIndex(actual, value => value.SequenceEqual(source.Iterator));
                if (index < 0) throw Error("target packing iterator key is absent");
                iterator = after.Nodes[index];
            }
            M.Write(IntPtr.Add(impl, 264), BitConverter.GetBytes(iterator.ToInt64()));
        }
        internal static void Preflight(Data source, Font target, Func<string,UnityEngine.Object> resolve, Func<object,string?> bind, Action admitted)
        {
            if (target == null || resolve == null || bind == null || admitted == null) throw new ArgumentNullException();
            RequireComplete(source);
            admitted();
            // Desired references are checked without asking the old Material for its atlas.
            // The Material's restored _MainTex is checked later by the immediate Font Plan.
            ResolveReference(source.Material,typeof(Material),resolve,bind);
            ResolveReference(source.Texture,typeof(Texture2D),resolve,bind);
            var dependencies = CaptureReadonlyDependencies(source,target,resolve,bind);
            RequireDependencies(source,dependencies);
            if (dependencies.CallbackDepth != 0) throw Error("target Font callback is active during preflight");
            _ = new NativeLayout(target);
            _ = new NativeOperations();
            _ = ReadGenerators();
            admitted();
        }
        private static IntPtr ResolveReference(string key, Type type, Func<string,UnityEngine.Object> resolve, Func<object,string?> bind)
        {
            if (key.Length == 0) return IntPtr.Zero;
            var pointer = ResidentNativeAssetReference.ResolveTyped(key,type,resolve);
            if (ResidentNativeAssetReference.BindPointer(pointer,type,bind) != key)
                throw Error("target Font reference reverse identity differs");
            return pointer;
        }
        private static Data CaptureReadonlyDependencies(Data source, Font target, Func<string,UnityEngine.Object> resolve, Func<object,string?> bind)
        {
            var owner = M.Owner(target);
            var impl = M.Ptr(M.Read(owner,80),72);
            if (impl == IntPtr.Zero) throw Error("target FontImpl is absent during preflight");
            var body = M.Read(impl,320);
            if (M.Ptr(body,120) != owner) throw Error("target FontImpl owner backlink differs");
            var rootKey = bind(target) ?? throw Error("target Font has no source identity");
            var sources = new List<SourceView> {FontSource(target,rootKey)};
            var fallbackBytes = ArrayBytes(IntPtr.Add(impl,192),4);
            if (fallbackBytes.Length/4 != source.Fallbacks.Length) throw Error("target Font fallback count differs");
            var fallbacks = new List<string>();
            for (int i = 0; i < source.Fallbacks.Length; i++) {
                string key = source.Fallbacks[i];
                var pointer = ResolveReference(key,typeof(Font),resolve,bind);
                int expectedId = pointer == IntPtr.Zero ? 0 : M.I32(M.Read(IntPtr.Add(pointer,8),4),0);
                if (M.I32(fallbackBytes,i*4) != expectedId) throw Error("target Font fallback identity differs");
                fallbacks.Add(key);
                if (pointer != IntPtr.Zero && !sources.Any(value=>value.Data.Key == key)) {
                    if (!(resolve(key) is Font font) || M.Owner(font) != pointer) throw Error("target fallback wrapper identity differs");
                    sources.Add(FontSource(font,key));
                }
            }
            var names = ArrayBytes(IntPtr.Add(impl,160),40);
            var actual = new Data {
                Material=source.Material, Texture=source.Texture, TextureFromMaterial=source.TextureFromMaterial,
                Header=source.Header, Scalars=ScalarOffsets.Select((offset,i)=>M.Slice(body,offset,ScalarSizes[i])).ToArray(),
                Ascii=source.Ascii, Unicode=source.Unicode, Rects=source.Rects, Positions=source.Positions,
                Iterator=source.Iterator, IteratorAtEnd=source.IteratorAtEnd,
                Kerning=VectorBytes(IntPtr.Add(impl,8),8), Fallbacks=fallbacks.ToArray(),
                FontNames=Enumerable.Range(0,names.Length/40).Select(i=>M.String(IntPtr.Add(M.Ptr(body,160),i*40))).ToArray(),
                Sources=sources.Select(value=>value.Data).ToArray(), DynamicDataPresent=M.Ptr(body,312)!=IntPtr.Zero,
                CallbackDepth=M.I32(body,308)
            };
            // Mutable glyph and atlas fields above are not read or treated as readonly
            // dependencies. Only these fields are consumed by RequireDependencies.
            actual.Faces=ReadPreflightFaces(M.Ptr(body,312),sources);
            return actual;
        }
        private static Face[] ReadPreflightFaces(IntPtr dynamic, List<SourceView> sources)
        {
            if (dynamic == IntPtr.Zero) return Array.Empty<Face>();
            var map=ReadTree(dynamic,56,32);
            var faces=new List<Face>();
            var aliases=new Dictionary<IntPtr,int>();
            for (int i=0;i<map.Nodes.Count;i++) {
                var value=map.Values[i];
                var pointer=M.Ptr(value,48);
                if (pointer == IntPtr.Zero) throw Error("target face map contains a null face");
                var native=M.Read(pointer,216);
                var stream=M.Read(M.Ptr(native,160),72);
                if (!aliases.TryGetValue(pointer,out int alias)) aliases.Add(pointer,alias=aliases.Count);
                var face=new Face {
                    Family=M.String(IntPtr.Add(map.Nodes[i],32)), Style=M.U32(value,40), Alias=alias,
                    Source=MatchFaceSource(sources,M.Ptr(stream,0),M.U32(stream,8),M.Ptr(stream,32)==IntPtr.Zero),
                    NativeFamily=NativeString(M.Ptr(native,24)), NativeStyle=NativeString(M.Ptr(native,32)),
                    Metadata=M.Slice(native,0,20).Concat(M.Slice(native,104,16)).ToArray()
                };
                int count=Count(M.U32(native,56),8);
                var charmaps=M.Read(M.Ptr(native,64),count*8);
                var selected=M.Ptr(native,136);
                for (int index=0;index<count;index++) if (M.Ptr(charmaps,index*8)==selected) face.CharmapIndex=index;
                if (selected!=IntPtr.Zero && face.CharmapIndex>=0) {
                    var charmap=M.Read(selected,16);
                    if (M.Ptr(charmap,0)!=pointer) throw Error("target selected charmap owner differs");
                    face.Charmap=M.Slice(charmap,8,8);
                }
                faces.Add(face);
            }
            return faces.ToArray();
        }
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly Font target;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private readonly NativeOperations operations;
            private readonly int materialId, textureId;
            private readonly GeneratorDirectory generators;
            private bool applied;

            internal Plan(Data source, Font target, Func<string, UnityEngine.Object> resolve, Func<object, string?> bind, Action admitted)
            {
                if (target == null || resolve == null || bind == null || admitted == null) throw new ArgumentNullException();
                RequireComplete(source);
                admitted();
                before = Capture(target, bind);
                RequireComplete(before);
                RequireDependencies(source, before);
                this.source = source.Copy();
                this.target = target;
                this.bind = bind;
                this.admitted = admitted;
                materialId = Resolve(source.Material, typeof(Material));
                textureId = source.TextureFromMaterial ? 0 : Resolve(source.Texture, typeof(Texture2D));
                if (source.TextureFromMaterial) {
                    var material = source.Material.Length == 0 ? null : resolve(source.Material) as Material;
                    var texture = material == null ? null : material.GetTexture("_MainTex");
                    if (source.Texture.Length == 0 ? texture != null : texture == null || bind(texture) != source.Texture)
                        throw Error("target material atlas fallback differs");
                }
                _ = new NativeLayout(target);
                operations = new NativeOperations();
                generators = ReadGenerators();
                admitted();

                int Resolve(string key, Type type)
                {
                    if (key.Length == 0) return 0;
                    var pointer = ResidentNativeAssetReference.ResolveTyped(key, type, resolve);
                    if (ResidentNativeAssetReference.BindPointer(pointer, type, bind) != key)
                        throw Error("target Font reference reverse identity differs");
                    return M.I32(M.Read(IntPtr.Add(pointer, 8), 4), 0);
                }
            }
            internal void Apply()
            {
                if (applied) throw Error("Font plan was applied twice");
                admitted();
                var current = Capture(target, bind);
                if (Json(current) != Json(before)) throw Error("target Font cache changed after preparation");
                generators.RequireUnchanged();
                var layout = new NativeLayout(target);
                var ascii = ExpandRecords(source.Ascii, 60);
                using (var unicode = new VectorReplacement(operations, IntPtr.Add(layout.Impl, 96), ExpandRecords(source.Unicode, 60)))
                using (var rectangles = new VectorReplacement(operations, IntPtr.Add(layout.Impl, 224), ExpandRecords(source.Rects, 16))) {
                    var header = IntPtr.Add(layout.Impl, 64);
                    ulong capacity = M.U64(layout.AsciiHeader, 24) >> 1;
                    if ((ulong)source.Ascii.Length > capacity) M.Reserve(header, source.Ascii.Length, 60, 4, admitted);
                    var storage = M.Ptr(M.Read(header, 32), 0);
                    if (ascii.Length != 0) M.Write(storage, ascii);
                    M.Write(IntPtr.Add(header, 16), BitConverter.GetBytes((ulong)source.Ascii.Length));
                    unicode.Publish();
                    rectangles.Publish();
                    RestorePacking(layout.Impl, source, operations);
                    for (int i = 0; i < ScalarOffsets.Length; i++) M.Write(IntPtr.Add(layout.Impl, ScalarOffsets[i]), source.Scalars[i]);
                    M.Write(IntPtr.Add(layout.Owner, 56), source.Header);
                    M.Write(IntPtr.Add(layout.Owner, 64), BitConverter.GetBytes(materialId));
                    M.Write(IntPtr.Add(layout.Owner, 68), BitConverter.GetBytes(textureId));
                }
                // The native cache owns generated UV/layout and may own GfxBuffers. This is
                // normal native cache invalidation, including backend GPU resource releases.
                // It neither enumerates TextMesh objects nor calls font rebuilding callbacks.
                InvalidateGenerators(admitted, generators.RequireUnchanged, operations.Flush.Invoke, generators.RequireEmpty);
                applied = true;
                admitted();
            }
            internal void Verify()
            {
                if (!applied) throw Error("Font plan was not applied");
                admitted();
                var actual = Capture(target, bind);
                RequireComplete(actual);
                if (Json(actual) != Json(source)) throw Error("Font cache independent readback differs");
                admitted();
            }
            private static string Json(Data data) => Newtonsoft.Json.JsonConvert.SerializeObject(data);
        }
    }
}
