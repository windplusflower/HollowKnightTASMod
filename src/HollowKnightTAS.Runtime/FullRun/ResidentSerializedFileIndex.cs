using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Standalone metadata parser. It never opens a pathname, follows an external
    // reference, reads an object body, loads Unity objects or changes the stream.
    // Locally proven format: SerializedFile22, 2020.2.2f1 (48-byte BE header).
    internal static class ResidentSerializedFileIndex
    {
        private const int HeaderSize = 48, MaximumMetadata = 32 * 1024 * 1024, MaximumEntries = 1000000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false,true);
        internal sealed class Entry
        {
            public long LocalId { get; }
            public long Offset { get; }
            public int Length { get; }
            public int ClassId { get; }
            public int TypeIndex { get; }
            private readonly byte[] hash;
            public byte[] TypeHash => (byte[])hash.Clone();
            internal Entry(long id,long offset,int length,int index,SerializedType type)
            { LocalId=id; Offset=offset; Length=length; TypeIndex=index; ClassId=type.ClassId; hash=type.OldTypeHash; }
        }
        internal sealed class TreeNode
        {
            public short Version { get; }
            public byte Level { get; }
            public byte Flags { get; }
            public uint TypeOffset { get; }
            public uint NameOffset { get; }
            public string TypeName { get; }
            public string Name { get; }
            public int ByteSize { get; }
            public int Index { get; }
            public int MetaFlags { get; }
            public ulong RefTypeHash { get; }
            internal TreeNode(short version,byte level,byte flags,uint type,uint name,int size,int index,int meta,ulong reference,Func<uint,string> resolve)
            { Version=version; Level=level; Flags=flags; TypeOffset=type; NameOffset=name; ByteSize=size; Index=index; MetaFlags=meta; RefTypeHash=reference;
                TypeName=resolve(type); Name=resolve(name); }
        }
        internal sealed class SerializedType
        {
            public int ClassId { get; }
            public bool IsStripped { get; }
            public short ScriptTypeIndex { get; }
            private readonly byte[] script,hash;
            public byte[] ScriptId => (byte[])script.Clone();
            public byte[] OldTypeHash => (byte[])hash.Clone();
            public IReadOnlyList<TreeNode> Tree { get; }
            public IReadOnlyList<int> Dependencies { get; }
            public string ClassName { get; }
            public string Namespace { get; }
            public string AssemblyName { get; }
            internal SerializedType(int id,bool stripped,short index,byte[] scriptId,byte[] oldHash,TreeNode[] tree,int[] dependencies,string name,string ns,string assembly)
            { ClassId=id; IsStripped=stripped; ScriptTypeIndex=index; script=scriptId; hash=oldHash; Tree=Array.AsReadOnly(tree);
                Dependencies=Array.AsReadOnly(dependencies); ClassName=name; Namespace=ns; AssemblyName=assembly; }
            internal string Identity => ClassId+"/"+ScriptTypeIndex+"/"+Convert.ToBase64String(script)+"/"+Convert.ToBase64String(hash)+"/"+ClassName+"/"+Namespace+"/"+AssemblyName;
        }
        internal sealed class ScriptIdentifier
        {
            public int FileIndex { get; }
            public long LocalId { get; }
            internal ScriptIdentifier(int file,long id) { FileIndex=file; LocalId=id; }
        }
        internal sealed class External
        {
            public string EmptyName { get; }
            public string Path { get; }
            public int Type { get; }
            private readonly byte[] guid;
            public byte[] Guid => (byte[])guid.Clone();
            internal External(string empty,byte[] id,int type,string path) { EmptyName=empty; guid=id; Type=type; Path=path; }
        }
        internal sealed class Index
        {
            public int FormatVersion => 22;
            public byte Endianness { get; }
            public long FileLength { get; }
            public long DataOffset { get; }
            public int MetadataSize { get; }
            public string UnityVersion { get; }
            public int TargetPlatform { get; }
            public bool HasTypeTree { get; }
            public string UserInformation { get; }
            public IReadOnlyList<SerializedType> Types { get; }
            public IReadOnlyList<SerializedType> ReferencedTypes { get; }
            public IReadOnlyList<Entry> Objects { get; }
            public IReadOnlyList<ScriptIdentifier> Scripts { get; }
            public IReadOnlyList<External> Externals { get; }
            private readonly Dictionary<long,Entry> byId;
            internal Index(byte endian,long length,long data,int metadata,string unity,int target,bool tree,SerializedType[] types,Entry[] objects,
                ScriptIdentifier[] scripts,External[] externals,SerializedType[] refs,string user)
            { Endianness=endian; FileLength=length; DataOffset=data; MetadataSize=metadata; UnityVersion=unity; TargetPlatform=target; HasTypeTree=tree;
                Types=Array.AsReadOnly(types); Objects=Array.AsReadOnly(objects); Scripts=Array.AsReadOnly(scripts); Externals=Array.AsReadOnly(externals);
                ReferencedTypes=Array.AsReadOnly(refs); UserInformation=user; byId=objects.ToDictionary(row=>row.LocalId); }
            public Entry Lookup(long localId) => byId.TryGetValue(localId,out var row) ? row : throw Error("local object ID is absent");
        }
        internal static Index Read(Stream stream) => ReadCore(stream, null);
        internal static Index ReadVerified(Stream stream, Stream witness)
        {
            if (witness == null || !witness.CanSeek || !witness.CanRead || ReferenceEquals(witness, stream))
                throw Error("an independent readable seekable stream is required");
            var origin = witness.Position;
            try { return ReadCore(stream, witness); }
            finally { witness.Position = origin; }
        }
        private static Index ReadCore(Stream stream, Stream? witness)
        {
            if (stream == null || !stream.CanRead || !stream.CanSeek) throw Error("a readable seekable stream is required");
            var origin=stream.Position; var physical=stream.Length;
            try {
                if (origin<0 || origin>physical || physical-origin<HeaderSize) throw Error("header is truncated");
                if (witness != null && witness.Length - witness.Position != physical - origin)
                    throw Error("independent stream extent differs");
                byte[] ReadSection(int size) {
                    var section = ReadExactly(stream, size);
                    if (witness != null) {
                        var buffer = new byte[Math.Min(65536, size)];
                        for (var offset = 0; offset < size;) {
                            var read = witness.Read(buffer, 0, Math.Min(buffer.Length, size - offset));
                            if (read <= 0) throw Error("independent metadata stream ended early");
                            for (var i = 0; i < read; i++)
                                if (buffer[i] != section[offset + i]) throw Error("independent metadata byte differs");
                            offset += read;
                        }
                    }
                    return section;
                }
                var header=ReadSection(HeaderSize); var initial=new Reader(header,false);
                var legacyMetadata=initial.U32(); var legacyLength=initial.U32(); var version=initial.U32(); var legacyData=initial.U32();
                if(version!=22) throw Error("unsupported SerializedFile version: "+version);
                var endian=initial.Byte(); var reserved=initial.Bytes(3);
                if(endian>1 || reserved.Any(b=>b!=0) || legacyMetadata!=0 || legacyLength!=0 || legacyData!=0) throw Error("format22 header variant is unsupported");
                var metadata=initial.U32(); var length=initial.I64(); var data=initial.I64(); var unknown=initial.I64();
                if(metadata>MaximumMetadata || length!=physical-origin || length<HeaderSize || data<HeaderSize || data>length || metadata>(ulong)(data-HeaderSize)
                    || unknown!=0) throw Error("header file/metadata/data ranges differ");
                var reader=new Reader(ReadSection((int)metadata),endian==0);
                var unity=reader.String(256); if(unity.Length==0) throw Error("Unity version is absent");
                var target=reader.I32(); var trees=reader.Bool();
                var types=ReadTypes(reader,trees,false);
                var objectCount=reader.Count(24); var objects=new Entry[objectCount]; var ids=new HashSet<long>();
                for(int i=0;i<objectCount;i++) {
                    reader.Align(); var id=reader.I64(); var relative=reader.I64(); var size=reader.U32(); var type=reader.I32();
                    if(id==0 || !ids.Add(id) || type<0 || type>=types.Length || relative<0 || relative>length-data || size>int.MaxValue
                        || (ulong)size>(ulong)(length-data-relative)) throw Error("object ID/type/range is invalid or duplicated");
                    objects[i]=new Entry(id,checked(data+relative),(int)size,type,types[type]);
                }
                long end=data;
                foreach(var row in objects.Where(row=>row.Length!=0).OrderBy(row=>row.Offset)) {
                    if(row.Offset<end) throw Error("serialized object bodies overlap"); end=checked(row.Offset+row.Length);
                }
                var scriptCount=reader.Count(12); var scripts=new ScriptIdentifier[scriptCount]; var scriptIds=new HashSet<Tuple<int,long>>();
                for(int i=0;i<scriptCount;i++) {
                    var file=reader.I32(); reader.Align(); var id=reader.I64();
                    if(file<0 || id==0 || !scriptIds.Add(Tuple.Create(file,id))) throw Error("script identifier is invalid or duplicated");
                    scripts[i]=new ScriptIdentifier(file,id);
                }
                var externalCount=reader.Count(22); var externals=new External[externalCount]; var paths=new HashSet<string>(StringComparer.Ordinal);
                for(int i=0;i<externalCount;i++) {
                    var empty=reader.String(); var guid=reader.Bytes(16); var type=reader.I32(); var path=reader.String();
                    if(type<0 || type>3 || path.Length==0 || !paths.Add(path)) throw Error("external file row is invalid or duplicated");
                    externals[i]=new External(empty,guid,type,path);
                }
                var refs=ReadTypes(reader,trees,true); var user=reader.String();
                if(reader.Remaining!=0) throw Error("metadata contains unparsed trailing bytes");
                foreach(var type in types) if(type.ScriptTypeIndex>=scripts.Length) throw Error("type script index is outside the script directory");
                foreach(var script in scripts) if(script.FileIndex>externals.Length || script.FileIndex==0 && !ids.Contains(script.LocalId))
                    throw Error("script file/local object reference is outside its directory");
                if(stream.Length!=physical) throw Error("stream length changed during metadata read");
                return new Index(endian,length,data,(int)metadata,unity,target,trees,types,objects,scripts,externals,refs,user);
            } catch(OverflowException error) { throw Error("integer overflow",error); }
            catch(EndOfStreamException error) { throw Error("stream ended before metadata",error); }
            catch(DecoderFallbackException error) { throw Error("metadata string is not valid UTF8",error); }
            finally { stream.Position=origin; }
        }
        private static SerializedType[] ReadTypes(Reader r,bool tree,bool reference)
        {
            var count=r.Count(23); var rows=new SerializedType[count]; var keys=new HashSet<string>(StringComparer.Ordinal);
            for(int i=0;i<count;i++) {
                var id=r.I32(); var stripped=r.Bool(); var scriptIndex=r.I16();
                if(scriptIndex < -1) throw Error("script type index is invalid");
                var script=(reference && scriptIndex>=0 || id==114) ? r.Bytes(16) : Array.Empty<byte>(); var hash=r.Bytes(16);
                var nodes=tree ? ReadTree(r) : Array.Empty<TreeNode>(); var dependencies=Array.Empty<int>(); var name=""; var ns=""; var assembly="";
                if(tree) {
                    if(reference) { name=r.String(); ns=r.String(); assembly=r.String(); }
                    else {
                        var n=r.Count(4); dependencies=new int[n]; var seen=new HashSet<int>();
                        for(int j=0;j<n;j++) { dependencies[j]=r.I32(); if(!seen.Add(dependencies[j])) throw Error("type dependency is duplicated"); }
                    }
                }
                var row=new SerializedType(id,stripped,scriptIndex,script,hash,nodes,dependencies,name,ns,assembly);
                if(!keys.Add(row.Identity)) throw Error("serialized type identity is duplicated"); rows[i]=row;
            }
            return rows;
        }
        private static TreeNode[] ReadTree(Reader r)
        {
            var count=r.Count(32); var stringSize=r.Count(1,MaximumMetadata); if(count==0) throw Error("enabled type tree has no root");
            var bytes=r.Bytes(checked(count*32)); var strings=r.Bytes(stringSize); var nodes=new TreeNode[count]; var n=new Reader(bytes,r.Little);
            var names=new Dictionary<uint,string>();
            string Resolve(uint offset) {
                if(names.TryGetValue(offset,out var cached)) return cached;
                var value=TreeString(strings,offset,r); names.Add(offset,value); return value;
            }
            for(int i=0;i<count;i++) {
                var version=n.I16(); var level=n.Byte(); var flags=n.Byte(); var type=n.U32(); var name=n.U32(); var size=n.I32(); var index=n.I32(); var meta=n.I32(); var hash=n.U64();
                if(i==0 && level!=0 || i!=0 && (level==0 || level>nodes[i-1].Level+1)) throw Error("type tree hierarchy is invalid");
                nodes[i]=new TreeNode(version,level,flags,type,name,size,index,meta,hash,Resolve);
            }
            return nodes;
        }
        private static string TreeString(byte[] strings,uint offset,Reader owner)
        {
            if((offset&0x80000000U)!=0) return CommonStrings.TryGetValue(offset&0x7fffffffU,out var value) ? value : throw Error("unknown fixed common-string offset");
            if(offset>=strings.Length) throw Error("type tree local string offset is outside its buffer");
            var end=Array.IndexOf(strings,(byte)0,(int)offset); if(end<0 || end-offset>65536) throw Error("type tree local string is unterminated or exceeds bounds");
            owner.StringBytes(end-(int)offset);
            return Utf8.GetString(strings,(int)offset,end-(int)offset);
        }
        private static readonly IReadOnlyDictionary<uint,string> CommonStrings=BuildCommonStrings();
        private static IReadOnlyDictionary<uint,string> BuildCommonStrings()
        {
            // Fixed built-in string offsets cross-checked against installed
            // UnityPy1.25.0 CommonString table. Unknown references fail closed.
            const string names="AABB|AnimationClip|AnimationCurve|AnimationState|Array|Base|BitField|bitset|bool|char|ColorRGBA|Component|data|deque|double|dynamic_array|FastPropertyName|first|float|Font|GameObject|Generic Mono|GradientNEW|GUID|GUIStyle|int|list|long long|map|Matrix4x4f|MdFour|MonoBehaviour|MonoScript|m_ByteSize|m_Curve|m_EditorClassIdentifier|m_EditorHideFlags|m_Enabled|m_ExtensionPtr|m_GameObject|m_Index|m_IsArray|m_IsStatic|m_MetaFlag|m_Name|m_ObjectHideFlags|m_PrefabInternal|m_PrefabParentObject|m_Script|m_StaticEditorFlags|m_Type|m_Version|Object|pair|PPtr<Component>|PPtr<GameObject>|PPtr<Material>|PPtr<MonoBehaviour>|PPtr<MonoScript>|PPtr<Object>|PPtr<Prefab>|PPtr<Sprite>|PPtr<TextAsset>|PPtr<Texture>|PPtr<Texture2D>|PPtr<Transform>|Prefab|Quaternionf|Rectf|RectInt|RectOffset|second|set|short|size|SInt16|SInt32|SInt64|SInt8|staticvector|string|TextAsset|TextMesh|Texture|Texture2D|Transform|TypelessData|UInt16|UInt32|UInt64|UInt8|unsigned int|unsigned long long|unsigned short|vector|Vector2f|Vector3f|Vector4f|m_ScriptingClassIdentifier|Gradient|Type*|int2_storage|int3_storage|BoundsInt|m_CorrespondingSourceObject|m_PrefabInstance|m_PrefabAsset|FileSize";
            var result=new Dictionary<uint,string>(); uint offset=0;
            foreach(var name in names.Split('|')) { result.Add(offset,name); offset=checked(offset+(uint)name.Length+1); }
            return new ReadOnlyDictionary<uint,string>(result);
        }
        private static byte[] ReadExactly(Stream stream,int length)
        {
            var bytes=new byte[length]; var at=0;
            while(at<length) { var count=stream.Read(bytes,at,length-at); if(count<=0) throw new EndOfStreamException(); at+=count; }
            return bytes;
        }
        private sealed class Reader
        {
            private readonly byte[] bytes;
            internal int Position;
            internal readonly bool Little;
            private int remainingStrings=MaximumMetadata;
            internal void StringBytes(int length) { if(length<0 || length>remainingStrings) throw Error("expanded metadata strings exceed bounds"); remainingStrings-=length; }
            internal int Remaining=>bytes.Length-Position;
            internal Reader(byte[] data,bool little) { bytes=data; Little=little; }
            private void Need(int count) { if(count<0 || count>Remaining) throw Error("metadata read exceeds its declared extent"); }
            internal byte Byte() { Need(1); return bytes[Position++]; }
            internal bool Bool() { var b=Byte(); return b<=1 ? b!=0 : throw Error("noncanonical metadata control boolean"); }
            internal byte[] Bytes(int count) { Need(count); var result=new byte[count]; Buffer.BlockCopy(bytes,Position,result,0,count); Position+=count; return result; }
            internal ulong U64() { Need(8); ulong value=0; for(int i=0;i<8;i++) value|=(ulong)bytes[Position+i]<<(8*(Little ? i : 7-i)); Position+=8; return value; }
            internal uint U32() { Need(4); uint value=0; for(int i=0;i<4;i++) value|=(uint)bytes[Position+i]<<(8*(Little ? i : 3-i)); Position+=4; return value; }
            internal long I64()=>unchecked((long)U64());
            internal int I32()=>unchecked((int)U32());
            internal short I16() { Need(2); var value=(ushort)(Little ? bytes[Position]|bytes[Position+1]<<8 : bytes[Position]<<8|bytes[Position+1]); Position+=2; return unchecked((short)value); }
            internal int Count(int minimum,int maximum=MaximumEntries) { var count=I32(); if(count<0 || count>maximum || (long)count*minimum>Remaining) throw Error("metadata count exceeds bounds"); return count; }
            internal void Align() { var padding=(4-(Position&3))&3; Need(padding); Position+=padding; }
            internal string String(int maximum=65536) { var end=Array.IndexOf(bytes,(byte)0,Position); if(end<0 || end-Position>maximum) throw Error("metadata string is unterminated or exceeds bounds");
                StringBytes(end-Position); var value=Utf8.GetString(bytes,Position,end-Position); Position=end+1; return value; }
        }
        private static InvalidDataException Error(string message,Exception? inner=null) => new InvalidDataException("Resident SerializedFile index: "+message,inner);
    }
}
