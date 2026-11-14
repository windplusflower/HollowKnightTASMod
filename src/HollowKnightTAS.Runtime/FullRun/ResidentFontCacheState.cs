using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentFontCacheState
    {
        private const int MaximumEntries = 16384;
        private const int MaximumBytes = 8 * 1024 * 1024;
        private const string Unknown = "@unidentified-font-source";
        internal sealed class Data
        {
            public string Material = "", Texture = "";
            public string[] Fallbacks = Array.Empty<string>(), FontNames = Array.Empty<string>(), RequiredGaps = Array.Empty<string>();
            public byte[] Header = Array.Empty<byte>(), Kerning = Array.Empty<byte>();
            public byte[][] Scalars = Array.Empty<byte[]>(), Ascii = Array.Empty<byte[]>(), Unicode = Array.Empty<byte[]>(), Rects = Array.Empty<byte[]>();
            public int[][] Positions = Array.Empty<int[]>();
            public int[] Iterator = Array.Empty<int>();
            public bool IteratorAtEnd, TextureFromMaterial, DynamicDataPresent;
            public int CallbackDepth;
            public SourceFont[] Sources = Array.Empty<SourceFont>();
            public Face[] Faces = Array.Empty<Face>();
            internal Data Copy() => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Newtonsoft.Json.JsonConvert.SerializeObject(this))!;
        }
        internal sealed class SourceFont
        {
            public string Key = "";
            public int FontDataBytes;
        }
        internal sealed class Face
        {
            public string Family = "", NativeFamily = "", NativeStyle = "", Source = "";
            public uint Style;
            public int Alias, CharmapIndex = -1;
            public byte[] Metadata = Array.Empty<byte>(), Charmap = Array.Empty<byte>();
        }
        private sealed class SourceView
        {
            internal SourceFont Data = new SourceFont();
            internal IntPtr Bytes;
        }
        private sealed class SetView
        {
            internal IntPtr Head;
            internal readonly List<IntPtr> Nodes = new List<IntPtr>();
            internal readonly List<byte[]> Values = new List<byte[]>();
        }
        private static readonly int[] ScalarOffsets = {32,48,52,272};
        private static readonly int[] ScalarSizes = {16,2,8,36};
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident Font: "+message);
        private static int Count(ulong count,int stride)
        {
            if (count > MaximumEntries || count * (ulong)stride > MaximumBytes) throw Error("container extent exceeds bound");
            return (int)count;
        }
        private static byte[] ArrayBytes(IntPtr header,int stride)
        {
            var bytes=M.Read(header,32); int count=Count(M.U64(bytes,16),stride);
            if ((M.U64(bytes,24)>>1) < (ulong)count) throw Error("dynamic array capacity differs");
            return M.Read(M.Ptr(bytes,0),checked(count*stride));
        }
        private static byte[] VectorBytes(IntPtr header,int stride)
        {
            var bytes=M.Read(header,24); long first=M.Ptr(bytes,0).ToInt64(), last=M.Ptr(bytes,8).ToInt64(), end=M.Ptr(bytes,16).ToInt64();
            if (last < first || end < last || (last-first)%stride != 0 || (end-first)%stride != 0 || first == 0 && (last != 0 || end != 0)) throw Error("vector layout differs");
            int count=Count((ulong)((last-first)/stride),stride); Count((ulong)((end-first)/stride),stride);
            return M.Read(new IntPtr(first),count*stride);
        }
        private static byte[][] Records(byte[] bytes,int stride,int fields)
        {
            if (bytes.Length%stride != 0) throw Error("record stride differs");
            return Enumerable.Range(0,bytes.Length/stride).Select(i=>M.Slice(bytes,i*stride,fields)).ToArray();
        }
        private static SetView ReadTree(IntPtr header,int stride,int valueOffset)
        {
            var bytes=M.Read(header,16); var head=M.Ptr(bytes,0); int count=Count(M.U64(bytes,8),stride);
            if (head == IntPtr.Zero) throw Error("tree sentinel is absent");
            var sentinel=M.Read(head,26); if (sentinel[25] != 1) throw Error("tree sentinel marker differs");
            var result=new SetView {Head=head}; var seen=new HashSet<IntPtr>();
            void Visit(IntPtr pointer,IntPtr parent,int depth)
            {
                if (pointer == head) return;
                if (pointer == IntPtr.Zero || depth > 128 || seen.Count >= count || !seen.Add(pointer)) throw Error("tree escaped declared extent");
                var node=M.Read(pointer,valueOffset+stride);
                if (node[25] != 0 || M.Ptr(node,8) != parent) throw Error("tree parent/marker differs");
                Visit(M.Ptr(node,0),pointer,depth+1); result.Nodes.Add(pointer); result.Values.Add(M.Slice(node,valueOffset,stride)); Visit(M.Ptr(node,16),pointer,depth+1);
            }
            Visit(M.Ptr(sentinel,8),head,0);
            if (result.Nodes.Count != count || M.Ptr(sentinel,0) != (count==0 ? head : result.Nodes[0]) || M.Ptr(sentinel,16) != (count==0 ? head : result.Nodes[count-1])) throw Error("tree extent/extrema differ");
            return result;
        }
        private static string NativeString(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "";
            var bytes=new List<byte>();
            for (int i=0;i<1024;i++) { byte value=M.Read(IntPtr.Add(pointer,i),1)[0]; if (value==0) return Encoding.UTF8.GetString(bytes.ToArray()); bytes.Add(value); }
            throw Error("native string exceeds bound");
        }
        private static SourceView FontSource(Font font,string key)
        {
            var impl=M.Ptr(M.Read(M.Owner(font),80),72); if (impl == IntPtr.Zero) throw Error("FontImpl absent");
            var array=M.Read(IntPtr.Add(impl,128),32); ulong length=M.U64(array,16);
            if (length>int.MaxValue || (M.U64(array,24)>>1)<length || length!=0 && M.Ptr(array,0)==IntPtr.Zero) throw Error("FontData extent differs");
            int count=(int)length;
            var pointer=M.Ptr(array,0);
            return new SourceView {Bytes=pointer,Data=new SourceFont {Key=key,FontDataBytes=count}};
        }
        private static string MatchFaceSource(IEnumerable<SourceView> sources,IntPtr pointer,uint length,bool memoryStream)
        {
            var matches=sources.Where(source=>memoryStream && source.Bytes==pointer && source.Data.FontDataBytes==length && length!=0).ToArray();
            return matches.Length==1 ? matches[0].Data.Key : Unknown;
        }
        internal static Data Capture(Font owner,Func<object,string?> bind)
        {
            if (owner == null || bind == null) throw new ArgumentNullException();
            var pointer=M.Owner(owner); var header=M.Read(pointer,80); var impl=M.Ptr(header,72);
            if (impl == IntPtr.Zero) throw Error("FontImpl absent");
            var body=M.Read(impl,320); if (M.Ptr(body,120) != pointer) throw Error("FontImpl owner backlink differs");
            var gaps=new List<string>(); var data=new Data {Header=M.Slice(header,56,8),CallbackDepth=M.I32(body,308),
                Scalars=ScalarOffsets.Select((offset,i)=>M.Slice(body,offset,ScalarSizes[i])).ToArray(),
                Kerning=VectorBytes(IntPtr.Add(impl,8),8),Ascii=Records(ArrayBytes(IntPtr.Add(impl,64),60),60,57),
                Unicode=Records(VectorBytes(IntPtr.Add(impl,96),60),60,57),Rects=Records(VectorBytes(IntPtr.Add(impl,224),16),16,16)};
            var material=ResidentNativeAssetReference.BorrowLoaded(M.I32(header,64),typeof(Material),bind,out data.Material) as Material;
            Texture? texture=ResidentNativeAssetReference.BorrowLoaded(M.I32(header,68),typeof(Texture2D),bind,out data.Texture) as Texture2D;
            if (texture == null && material != null) {
                data.TextureFromMaterial=true;
                texture=material.GetTexture("_MainTex");
                if (texture != null) data.Texture=bind(texture) ?? throw Error("material atlas is unbound");
            }
            if (texture != null && texture.GetType() != typeof(Texture2D)) gaps.Add("font-atlas-is-not-Texture2D");
            var names=ArrayBytes(IntPtr.Add(impl,160),40);
            data.FontNames=Enumerable.Range(0,names.Length/40).Select(i=>M.String(IntPtr.Add(M.Ptr(body,160),i*40))).ToArray();
            var sources=new List<SourceView> {FontSource(owner,bind(owner) ?? throw Error("root Font identity is unbound"))};
            var fallbacks=ArrayBytes(IntPtr.Add(impl,192),4); var keys=new List<string>();
            for (int i=0;i<fallbacks.Length;i+=4) {
                var font=ResidentNativeAssetReference.BorrowLoaded(M.I32(fallbacks,i),typeof(Font),bind,out var key) as Font;
                keys.Add(key); if (font != null && !sources.Any(source=>source.Data.Key==key)) sources.Add(FontSource(font,key));
            }
            data.Fallbacks=keys.ToArray(); data.Sources=sources.Select(source=>source.Data).ToArray();
            var packing=ReadTree(IntPtr.Add(impl,248),8,28);
            data.Positions=packing.Values.Select(value=>new[] {M.I32(value,0),M.I32(value,4)}).ToArray();
            var iterator=M.Ptr(body,264); data.IteratorAtEnd=iterator==packing.Head;
            int iteratorIndex=packing.Nodes.IndexOf(iterator);
            if (!data.IteratorAtEnd && iteratorIndex<0) throw Error("packing iterator has no owned node");
            data.Iterator=data.IteratorAtEnd ? Array.Empty<int>() : data.Positions[iteratorIndex].ToArray();
            // Fixed player: GetCharacterBitmap (0xc646d0) sets request transform/size/glyph;
            // GetFontMetricsForFace (0xc66390) sets its own size before reading metadata.
            // Coverage uses charmap/metadata, and kerning is the separate serialized map.
            // Match immutable origin/charmap and aliases, retaining every target-local FT handle.
            var dynamic=M.Ptr(body,312); data.DynamicDataPresent=dynamic!=IntPtr.Zero; var aliases=new Dictionary<IntPtr,int>(); var faces=new List<Face>();
            if (dynamic != IntPtr.Zero) {
                var map=ReadTree(dynamic,56,32);
                for (int i=0;i<map.Nodes.Count;i++) {
                    var value=map.Values[i]; var face=M.Ptr(value,48); if (face == IntPtr.Zero) throw Error("face map has null value");
                    var native=M.Read(face,216); var stream=M.Ptr(native,160); var streamBytes=M.Read(stream,72);
                    string origin=MatchFaceSource(sources,M.Ptr(streamBytes,0),M.U32(streamBytes,8),M.Ptr(streamBytes,32)==IntPtr.Zero);
                    if (!aliases.TryGetValue(face,out var alias)) aliases.Add(face,alias=aliases.Count);
                    var row=new Face {Family=M.String(IntPtr.Add(map.Nodes[i],32)),Style=M.U32(value,40),Alias=alias,
                        Source=origin,NativeFamily=NativeString(M.Ptr(native,24)),NativeStyle=NativeString(M.Ptr(native,32)),
                        Metadata=M.Slice(native,0,20).Concat(M.Slice(native,104,16)).ToArray()};
                    if (row.Source==Unknown) gaps.Add("font-face-source-not-known-embedded-FontData");
                    var selected=M.Ptr(native,136); int count=Count(M.U32(native,56),8); var maps=M.Read(M.Ptr(native,64),count*8);
                    for (int index=0;index<count;index++) if (M.Ptr(maps,index*8)==selected) row.CharmapIndex=index;
                    if (selected==IntPtr.Zero || row.CharmapIndex<0) gaps.Add("font-selected-charmap-unidentified");
                    else {var cm=M.Read(selected,16);if (M.Ptr(cm,0)!=face) throw Error("charmap owner differs");row.Charmap=M.Slice(cm,8,8);}
                    faces.Add(row);
                }
            }
            data.Faces=faces.ToArray();
            if (data.CallbackDepth!=0) gaps.Add("font-texture-rebuilt-callback-active");
            data.RequiredGaps=gaps.Concat(RequiredGaps(data)).Distinct().OrderBy(value=>value,StringComparer.Ordinal).ToArray();
            Validate(data); return data;
        }
        internal static void Validate(Data data)
        {
            if (data==null || data.Material==null || data.Texture==null || data.Fallbacks==null || data.FontNames==null || data.RequiredGaps==null
                || data.Header==null || data.Header.Length!=8 || data.Scalars==null || data.Scalars.Length!=ScalarSizes.Length || data.Kerning==null || data.Kerning.Length%8!=0 || data.Kerning.Length>MaximumEntries*8
                || data.Ascii==null || data.Unicode==null || data.Rects==null || data.Positions==null || data.Iterator==null || data.Sources==null || data.Faces==null) throw Error("typed cache shape differs");
            if (data.Scalars.Where((value,i)=>value==null || value.Length!=ScalarSizes[i]).Any()) throw Error("scalar extent differs");
            foreach (var value in data.Fallbacks.Concat(data.FontNames).Concat(data.RequiredGaps)) if (value==null || value.Length>8192 || value.IndexOf('\0')>=0) throw Error("text dependency differs");
            foreach (var array in new[] {data.Ascii,data.Unicode,data.Rects}) {if (array.Length>MaximumEntries) throw Error("record count exceeds bound");}
            if (data.Ascii.Any(value=>value==null || value.Length!=57) || data.Unicode.Any(value=>value==null || value.Length!=57) || data.Rects.Any(value=>value==null || value.Length!=16)
                || data.Positions.Length>MaximumEntries || data.Positions.Any(value=>value==null || value.Length!=2) || data.Positions.Select(value=>value[0]+":"+value[1]).Distinct().Count()!=data.Positions.Length
                || data.IteratorAtEnd && data.Iterator.Length!=0 || !data.IteratorAtEnd && (data.Iterator.Length!=2 || !data.Positions.Any(value=>value.SequenceEqual(data.Iterator)))) throw Error("cache record or iterator differs");
            if (data.Sources.Length>65 || data.Faces.Length>MaximumEntries || data.Sources.Any(value=>value==null || string.IsNullOrEmpty(value.Key) || value.FontDataBytes<0)
                || data.Sources.Select(value=>value.Key).Distinct().Count()!=data.Sources.Length) throw Error("Font source directory differs");
            foreach (var face in data.Faces) if (face==null || face.Family==null || face.NativeFamily==null || face.NativeStyle==null || face.Source==null || face.Alias<0
                || face.Metadata==null || face.Metadata.Length!=36 || face.Charmap==null || face.Charmap.Length!=0 && face.Charmap.Length!=8
                || face.Source!=Unknown && !data.Sources.Any(source=>source.Key==face.Source)) throw Error("face dependency differs");
            long mutableBytes=(long)(data.Ascii.Length+data.Unicode.Length)*60+data.Rects.Length*16L+data.Positions.Length*8L;
            if (mutableBytes>MaximumBytes || data.FontNames.Length>128 || data.Fallbacks.Length>64 || data.FontNames.Sum(value=>(long)value.Length)>65536)
                throw Error("mutable Font cache exceeds aggregate bound");
            for (int i=1;i<data.Positions.Length;i++) if (ComparePositions(data.Positions[i-1],data.Positions[i])>=0)
                throw Error("packing keys are not in native comparator order");
            var aliases=new Dictionary<int,Face>();
            foreach (var face in data.Faces) {
                if (!aliases.TryGetValue(face.Alias,out var previous)) {
                    if (face.Alias!=aliases.Count) throw Error("face aliases are not dense");
                    aliases.Add(face.Alias,face);
                } else if (face.Source!=previous.Source || face.NativeFamily!=previous.NativeFamily || face.NativeStyle!=previous.NativeStyle
                    || face.CharmapIndex!=previous.CharmapIndex || !face.Metadata.SequenceEqual(previous.Metadata) || !face.Charmap.SequenceEqual(previous.Charmap))
                    throw Error("shared face has contradictory immutable dependencies");
            }
            foreach (var value in data.Ascii.Concat(data.Unicode)) if (value[56]>1) throw Error("CharacterInfo bool differs");
            if (data.Scalars[1].Any(value=>value>1)) throw Error("FontImpl bool differs");
        }
        private static int ComparePositions(int[] left,int[] right)
        {
            int a=unchecked(left[0]+left[1]), b=unchecked(right[0]+right[1]);
            return a!=b ? a.CompareTo(b) : left[0].CompareTo(right[0]);
        }
        private static IEnumerable<string> RequiredGaps(Data data)
        {
            if (M.I32(data.Scalars[2],0)!=-2 || !data.DynamicDataPresent)
                yield return "font-cache-is-not-an-initialized-dynamic-Font";
            if (data.Sources.Length==0 || data.Sources.Any(source=>!ResidentAssetBindings.IsSharedBinding(source.Key)))
                yield return "font-source-is-not-a-shared-serialized-Font";
            if (data.Faces.Any(face=>face.Source==Unknown))
                yield return "font-face-source-not-known-embedded-FontData";
            if (data.Faces.Any(face=>face.CharmapIndex<0 || face.Charmap.Length!=8))
                yield return "font-selected-charmap-unidentified";
            if (data.CallbackDepth!=0)
                yield return "font-texture-rebuilt-callback-active";
            if (data.Ascii.Length+data.Unicode.Length!=0 && data.Texture.Length==0)
                yield return "font-glyph-cache-has-no-atlas-binding";
        }
        internal static void RequireComplete(Data data)
        {
            Validate(data);
            var gaps=RequiredGaps(data).Concat(data.RequiredGaps).Distinct().ToArray();
            if (gaps.Length!=0) throw Error("native coverage incomplete: "+string.Join(",",gaps));
        }
        internal static Plan Prepare(Data source,Font target,Func<string,UnityEngine.Object> resolve,Func<object,string?> bind,Action admitted)
            => new Plan(source,target,resolve,bind,admitted);
        private static void RequireDependencies(Data source,Data target)
        {
            Validate(source); Validate(target);
            string Json(object value)=>Newtonsoft.Json.JsonConvert.SerializeObject(value);
            if (source.DynamicDataPresent!=target.DynamicDataPresent || M.I32(source.Scalars[2],0)!=M.I32(target.Scalars[2],0)
                || !source.FontNames.SequenceEqual(target.FontNames) || !source.Fallbacks.SequenceEqual(target.Fallbacks)
                || !source.Kerning.SequenceEqual(target.Kerning) || Json(source.Sources)!=Json(target.Sources) || Json(source.Faces)!=Json(target.Faces)) throw Error("readonly Font source, kerning, fallback, or face dependency differs");
        }
    }
}
