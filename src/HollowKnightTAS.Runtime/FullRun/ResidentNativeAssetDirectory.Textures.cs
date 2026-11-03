using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetDirectory
    {
        // Full known CPU/native field definitions, not a GPU-image substitute.
        // No Unity property, constructor, GetPixels or wrapper factory is used.
        internal sealed class TextureBuffer
        {
            public int Count, Capacity, Label;
            public bool Owned, Present, RawAllocation;
            public byte[] HeaderPadding = new byte[4], Bytes = Array.Empty<byte>();
        }
        internal sealed class TextureImage
        {
            public string[] Owners = Array.Empty<string>();
            public bool OwnersComplete;
            public byte[] Fields = new byte[72], Padding = new byte[4];
            public TextureBuffer Data = new TextureBuffer();
        }
        internal sealed class TextureReference
        {
            public string Binding = "";
            public bool Loaded, Null = true;
            public int NativeTypeRva;
            public ResidentSerializedAssetImage.Data? Source;
        }
        internal sealed class TextureBody
        {
            // Exact field bits with process pointers, IDs and separately typed
            // containers removed. Explicit masks below are pinned PDB ABI.
            public byte[] Fields = Array.Empty<byte>(), Padding = Array.Empty<byte>(), UnusedFlags = Array.Empty<byte>();
            public TextStorage StreamPath = new TextStorage();
            public string StreamImageHash = "";
            public bool[] ResourcePresent = Array.Empty<bool>(), ResourceMapped = Array.Empty<bool>(), AsyncPresent = new bool[3], SurfacePresent = Array.Empty<bool>();
            public int[] ResourceAliases = Array.Empty<int>(), SurfaceAliases = Array.Empty<int>();
            public bool MainResourceMapped, RenderRegistryLinked, RenderRegistryBackref, RenderPoolPresent;
            public TextureBuffer Platform = new TextureBuffer(), Standalone = new TextureBuffer();
            public TextureImage? Shared;
            public TextureReference[] SourceFaces = Array.Empty<TextureReference>();
            public byte[] SourceFaceUnused = Array.Empty<byte>();
            public int SourceFaceCapacity;
            public bool SourceFaceStoragePresent;
        }
        private sealed class TextureProfile
        {
            internal readonly int Rva, Vtable, Size;
            internal readonly string Kind;
            internal readonly int[] Padding, Resources;
            internal TextureProfile(int rva, int vtable, int size, string kind, int[] tailPadding, params int[] resources)
            {
                Rva = rva; Vtable = vtable; Size = size; Kind = kind;
                Padding = new[] { 84,85,86,87,115,140,141,142,143,196,197,198,199,212,213,214,215,228,229,230,231,244,245,246,247 }.Concat(tailPadding).ToArray();
                Resources = new[] { 80 }.Concat(resources).ToArray();
            }
        }
        private static int[] Range(int first, int count) => Enumerable.Range(first, count).ToArray();
        private static readonly TextureProfile[] TextureProfiles = {
            new TextureProfile(0x194ce60,0x15e7950,280,"texture",Array.Empty<int>()),
            new TextureProfile(0x194ceb0,0x1685618,352,"texture-2d",Range(329,3).Concat(Range(341,3)).Concat(Range(348,4)).ToArray(),332),
            new TextureProfile(0x1960fb0,0x1688b00,376,"cubemap",Range(329,3).Concat(Range(341,3)).Concat(Range(348,4)).ToArray(),332),
            new TextureProfile(0x1960470,0x1685180,320,"texture-3d",Range(314,2)),
            new TextureProfile(0x1961550,0x1680e48,328,"texture-2d-array",Range(322,6)),
            new TextureProfile(0x1961750,0x1686920,320,"cubemap-array",Range(292,4).Concat(Range(314,6)).ToArray()),
            new TextureProfile(0x194c6f0,0x1682860,408,"render-texture",Range(340,4).Concat(Range(405,3)).ToArray(),332,336),
            new TextureProfile(0x19645a0,0x1686788,288,"lower-res-blit-texture",Array.Empty<int>())
        };
        private static TextureProfile? TextureType(int rva) => TextureProfiles.SingleOrDefault(profile => profile.Rva == rva);
        internal static int[] TextureResourceOffsets(int rva)
            => (int[])(TextureType(rva) ?? throw Error("texture resource role type is unsupported")).Resources.Clone();
        internal static int TextureResourceMask(int rva, int[] ids, int resource)
        {
            if (ids == null || ids.Length != TextureResourceOffsets(rva).Length || resource == 0) throw Error("texture resource role shape differs");
            var mask = 0; for (var i = 0; i < ids.Length; i++) if (ids[i] == resource) mask |= 1 << i;
            if (mask == 0) throw Error("texture resource does not belong to any actual owner slot");
            return mask;
        }
        internal static int[] TextureResourcePartition(int rva, int[] ids)
        {
            if (ids == null || ids.Length != TextureResourceOffsets(rva).Length) throw Error("texture resource role shape differs");
            return AliasIds(ids);
        }
        internal static int[] ReadTextureResourceIds(IntPtr pointer, out int rva)
        {
            rva = ResidentNativeAssetReference.NativeClass(pointer, ResidentLoadedAssetIndex.TextureClass);
            var profile = TextureType(rva) ?? throw Error("texture resource owner has an unsupported actual type");
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            ResidentLoadedAssetIndex.RequireCode(engine,0x69bfe0,364,"7f3d7abdb710fc7dcb46da44f0f6f29d061080c41cc409f8a8cf131bdcd62587");
            if(profile.Kind=="texture-2d"||profile.Kind=="cubemap")ResidentLoadedAssetIndex.RequireCode(engine,0x67b610,31,"245f2be19ef72c4c8352df518d1da94393811a844f12a2bb1572bff5fa0d2a6b");
            if(profile.Kind=="render-texture")ResidentLoadedAssetIndex.RequireCode(engine,0x657120,161,"bdb282481deb68f8a60026e6f988533424fcef3bb8a4b1985a59ff5bc59f6148");
            var bytes = M.Read(pointer, profile.Size);
            if (M.Ptr(bytes,0) != IntPtr.Add(engine,profile.Vtable)) throw Error("texture resource owner vtable differs");
            var owner = ResidentLoadedAssetIndex.RegisteredPointer(M.I32(bytes,8),out var verify);
            if (owner != pointer) throw Error("texture resource owner registration differs");
            var ids = profile.Resources.Select(offset => M.I32(bytes,offset)).ToArray();
            foreach (var offset in profile.Resources) if (M.I32(M.Read(IntPtr.Add(pointer,offset),4),0) != M.I32(bytes,offset)) throw Error("texture resource slots changed during observation");
            verify(); return ids;
        }
        internal sealed class TextureLocal
        {
            internal IntPtr Pointer, Platform, Standalone, Shared, SharedData;
            internal TextureBody Data = null!;
        }
        private static bool ReadTexture(Live live, Row row, IntPtr pointer, IntPtr engine, IReadOnlyDictionary<int, string> bindings)
        {
            var profile = TextureType(row.TypeRva); if (profile == null) return false;
            if (row.Size != profile.Size || row.Vtable != profile.Vtable) throw Error("actual texture native class size/vtable differs");
            var bytes = live.Read(pointer, profile.Size);
            if (M.Ptr(bytes, 0) != IntPtr.Add(engine, profile.Vtable)) throw Error("texture owner vtable differs");
            row.Kind = "texture/" + profile.Kind; ReadName(live, row, M.Ptr(bytes, 48));
            var body = new TextureBody { Fields = M.Slice(bytes, 56, bytes.Length - 56), Padding = profile.Padding.Select(offset => bytes[offset]).ToArray(),
                UnusedFlags = (profile.Kind=="texture-2d"||profile.Kind=="cubemap") ? new[]{(byte)(bytes[114]&0xfe),(byte)(bytes[320]&0xfc)} : new[]{(byte)(bytes[114]&0xfe)},
                StreamPath = ReadTextureString(live, IntPtr.Add(pointer, 144)), AsyncPresent = new[] { M.Ptr(bytes,184)!=IntPtr.Zero,M.Ptr(bytes,200)!=IntPtr.Zero,M.Ptr(bytes,216)!=IntPtr.Zero } };
            var ids = profile.Resources.Select(offset => M.U32(bytes, offset)).ToArray(); body.ResourcePresent = ids.Select(id => id != 0).ToArray(); body.ResourceAliases = AliasIds(ids);
            body.ResourceMapped = ids.Select(id => {
                if (id == 0) return false;
                var registered = ResidentLoadedAssetIndex.FindTextureResourcePointer(unchecked((int)id));
                if (registered != IntPtr.Zero && registered != pointer) throw Error("texture resource ID belongs to a different registered native owner: " + row.TypeKey);
                return registered == pointer;
            }).ToArray();
            body.MainResourceMapped = body.ResourceMapped[0];
            var local = new TextureLocal { Pointer = pointer, Shared = M.Ptr(bytes, 88), Data = body };
            body.Platform = ReadTextureBuffer(live, M.Slice(bytes,248,32), out local.Platform);
            if (local.Shared != IntPtr.Zero) {
                if (!live.TextureImages.TryGetValue(local.Shared, out var image)) {
                    var shared = live.Read(local.Shared, 80);
                    if (M.Ptr(shared,0)!=IntPtr.Add(engine,0x16855a8) || M.I32(shared,12)<1) throw Error("SharedTextureData actual identity/reference count differs");
                    image = new TextureImage { Fields = M.Slice(shared,8,72), Padding = M.Slice(shared,60,4) };
                    image.Data = ReadTextureBuffer(live,M.Slice(shared,16,32),out var dataPointer); local.SharedData = dataPointer;
                    Array.Clear(image.Fields,16-8,32); Array.Clear(image.Fields,60-8,4);
                    EnsureTextureAliases(live,bindings);
                    var group=live.TextureAliasOwners![local.Shared];
                    image.Owners=group.Owners.OrderBy(value=>value,StringComparer.Ordinal).ToArray();image.OwnersComplete=group.Complete;
                    live.TextureImages.Add(local.Shared,image);
                } else local.SharedData = M.Ptr(live.Read(IntPtr.Add(local.Shared,16),8),0);
                body.Shared = image;
            }
            if (profile.Kind=="texture-3d" || profile.Kind=="texture-2d-array" || profile.Kind=="cubemap-array") {
                local.Standalone=M.Ptr(bytes,296); var count = profile.Kind=="cubemap-array" ? M.U32(bytes,304) : M.U64(bytes,304);
                var extra = 0;
                if (profile.Kind=="texture-3d" && local.Standalone!=IntPtr.Zero) {
                    // AllocateTextureData adds s_FormatDescTable[format].byte0
                    // to the logical data size, before aligned malloc32.
                    var format = M.U32(bytes,292); var at=format<=150?format:0;
                    extra=live.Read(IntPtr.Add(engine,checked(0x1961d00+(int)at*64)),1)[0];
                }
                var capacity=checked(count+(uint)extra);
                if (capacity>32*1024*1024 || count!=0 && local.Standalone==IntPtr.Zero) throw Error("standalone texture CPU extent differs");
                body.Standalone=new TextureBuffer { RawAllocation=true,Owned=true,Present=local.Standalone!=IntPtr.Zero,Count=(int)count,Capacity=local.Standalone==IntPtr.Zero?0:(int)capacity,
                    Label=M.I32(live.Read(IntPtr.Add(engine,profile.Kind=="texture-3d"?0x1684b80:profile.Kind=="texture-2d-array"?0x167daf8:0x168691c),4),0),Bytes=local.Standalone==IntPtr.Zero?Array.Empty<byte>():live.Read(local.Standalone,(int)capacity) };
            }
            if (profile.Kind=="cubemap") {
                var first=M.Ptr(bytes,352); var last=M.Ptr(bytes,360); var end=M.Ptr(bytes,368);
                var count=last.ToInt64()-first.ToInt64(); var capacity=end.ToInt64()-first.ToInt64();
                if (count<0 || capacity<count || count%4!=0 || capacity%4!=0 || capacity>4*65536 || first==IntPtr.Zero && capacity!=0) throw Error("cubemap source texture vector shape differs");
                var raw=live.Read(first,(int)capacity); body.SourceFaceCapacity=(int)capacity/4; body.SourceFaceStoragePresent=first!=IntPtr.Zero;
                body.SourceFaceUnused=M.Slice(raw,(int)count,(int)(capacity-count)); var faces=new List<TextureReference>();
                for(var index=0;index<count/4;index++) {
                    var id=M.I32(raw,index*4); var reference=new TextureReference();
                    if(id!=0) {
                        reference.Null=false;reference.Loaded=live.Globals.RegisteredPointers.ContainsKey(id);
                        if(reference.Loaded)reference.NativeTypeRva=live.Globals.RegisteredRows[id].TypeRva;
                        if(bindings.TryGetValue(id,out var key)) reference.Binding=key;
                        else { reference.Source=ResidentSerializedAssetImage.CaptureMappedId(id);reference.NativeTypeRva=reference.Source.NativeTypeRva; if(reference.Source.CoverageGaps.Length==0) reference.Binding=SourceKey(reference.Source); }
                    }
                    faces.Add(reference);
                }
                body.SourceFaces=faces.ToArray();
            }
            if(profile.Kind=="render-texture") {
                var handles=new[]{M.U64(bytes,344),M.U64(bytes,352),M.U64(bytes,360)}; body.SurfacePresent=handles.Select(value=>value!=0).ToArray(); body.SurfaceAliases=AliasIds(handles);
                var node=IntPtr.Add(pointer,368); var previous=M.Ptr(bytes,368); var next=M.Ptr(bytes,376);
                body.RenderRegistryLinked=previous!=IntPtr.Zero || next!=IntPtr.Zero;
                body.RenderRegistryBackref=M.Ptr(bytes,384)==pointer;
                body.RenderPoolPresent=M.Ptr(bytes,392)!=IntPtr.Zero;
            }
            var streamSize=M.U32(bytes,136);
            if(streamSize!=0 && body.StreamPath.Bytes.Length!=0) {
                var path=new UTF8Encoding(false,true).GetString(body.StreamPath.Bytes);
                if(streamSize<=32*1024*1024 && ResidentSerializedAssetImage.AbsoluteFilesystemPath(path)) {
                    var size=new FileInfo(path).Length; var slice=ResidentSerializedAssetImage.ReadFilesystemSlice(path,size,M.U64(bytes,128),checked((int)streamSize));
                    using(var sha=System.Security.Cryptography.SHA256.Create()) body.StreamImageHash=BitConverter.ToString(sha.ComputeHash(slice)).Replace("-","").ToLowerInvariant();
                }
            }
            ClearTextureProcessFields(body.Fields,profile); row.Texture=body;
            live.TextureLocals.Add(row.Binding.Length==0?"unbound/"+M.I32(bytes,8):row.Binding,local); return true;
        }
        internal sealed class TextureAliasGroup { internal readonly List<string> Owners=new List<string>();internal bool Complete=true; }
        private static void EnsureTextureAliases(Live live,IReadOnlyDictionary<int,string> bindings)
        {
            if(live.TextureAliasOwners!=null)return;
            var result=new Dictionary<IntPtr,TextureAliasGroup>();var classes=live.Globals.Snapshot.Classes.ToDictionary(type=>type.Rva);var family=classes[0x194ce60];
            // One scan of the already-verified native registration, not one
            // Resources scan per image. No managed wrapper is requested.
            foreach(var member in live.Globals.RegisteredPointers){
                var type=classes[live.Globals.RegisteredRows[member.Key].TypeRva];if(type.Index<family.Index||type.Index>=family.Index+family.DescendantCount)continue;
                var shared=M.Ptr(live.Read(IntPtr.Add(member.Value,88),8),0);if(shared==IntPtr.Zero)continue;
                if(!result.TryGetValue(shared,out var group)){group=new TextureAliasGroup();result.Add(shared,group);}
                if(bindings.TryGetValue(member.Key,out var key)&&key.Length!=0)group.Owners.Add(key);else group.Complete=false;
            }
            live.TextureAliasOwners=result;
        }
        private static int[] AliasIds<T>(T[] ids) where T:struct,IEquatable<T>
        {
            var result=new int[ids.Length];var groups=new Dictionary<T,int>();
            for(var index=0;index<ids.Length;index++){if(ids[index].Equals(default(T))){result[index]=-1;continue;}if(!groups.TryGetValue(ids[index],out var group)){group=groups.Count;groups.Add(ids[index],group);}result[index]=group;}return result;
        }
        private static TextStorage ReadTextureString(Live live,IntPtr address)
        {
            var header=live.Read(address,40);var pointer=M.Ptr(header,0);var size=M.U64(header,24);var capacity=pointer==IntPtr.Zero?15UL:M.U64(header,8);
            if(size>16384 || capacity>32*1024*1024 || pointer==IntPtr.Zero&&size>15 || pointer!=IntPtr.Zero&&capacity!=0&&capacity<size)throw Error("texture source path storage differs");
            var bytes=live.Read(pointer==IntPtr.Zero?IntPtr.Add(address,8):pointer,checked((int)size+1));if(bytes[bytes.Length-1]!=0)throw Error("texture source path terminator differs");
            return new TextStorage {Bytes=M.Slice(bytes,0,bytes.Length-1),Inline=pointer==IntPtr.Zero,Capacity=(int)capacity,Label=M.I32(header,32),
                Unused=(pointer==IntPtr.Zero?M.Slice(header,9+(int)size,15-(int)size):M.Slice(header,16,8)).Concat(M.Slice(header,36,4)).ToArray()};
        }
        private static TextureBuffer ReadTextureBuffer(Live live,byte[] header,out IntPtr pointer)
        {
            pointer=M.Ptr(header,0);var count=M.U64(header,16);var capacity=M.U64(header,24)>>1;
            if(count>capacity || capacity>32*1024*1024 || capacity!=0&&pointer==IntPtr.Zero)throw Error("texture byte array shape differs");
            return new TextureBuffer { Count=(int)count,Capacity=(int)capacity,Owned=(M.U64(header,24)&1)==0,Present=pointer!=IntPtr.Zero,Label=M.I32(header,8),HeaderPadding=M.Slice(header,12,4),Bytes=live.Read(pointer,(int)capacity)};
        }
        private static void ClearTextureProcessFields(byte[] fields,TextureProfile profile)
        {
            void Clear(int offset,int length)=>Array.Clear(fields,offset-56,length);
            foreach(var offset in profile.Resources)Clear(offset,4);
            Clear(88,8);Clear(144,40);foreach(var offset in new[]{184,200,216})Clear(offset,8);Clear(248,32);
            if(profile.Kind=="texture-3d"||profile.Kind=="texture-2d-array"||profile.Kind=="cubemap-array")Clear(296,8);
            if(profile.Kind=="cubemap")Clear(352,24);if(profile.Kind=="render-texture")Clear(344,56);
            foreach(var offset in profile.Padding)fields[offset-56]=0;
            fields[114-56]&=1;if(profile.Kind=="texture-2d"||profile.Kind=="cubemap")fields[320-56]&=3;
        }
        private static void ReadName(Live live,Row row,IntPtr pointer)
        {
            row.NamePresent = pointer != IntPtr.Zero;
            if (!row.NamePresent) return;
            // Probe only within the current readable page. The definitive
            // read and the independent witness stop at the actual terminator.
            var pageSize = Environment.SystemPageSize;
            for (var offset = 0; offset < 16384;) {
                live.Admitted();
                var address = checked(pointer.ToInt64() + offset);
                if (address <= 0) throw Error("invalid native asset name address");
                var count = Math.Min(Math.Min(128, 16384-offset), pageSize-(int)(address%pageSize));
                var end = Array.IndexOf(M.Read(new IntPtr(address),count),(byte)0);
                if (end >= 0) {
                    var size = offset + end;
                    var actual = live.Read(pointer,size+1);
                    if (Array.IndexOf(actual,(byte)0) != size) throw Error("native asset name extent changed");
                    row.Name = M.Slice(actual,0,size);
                    return;
                }
                offset += count;
            }
            throw Error("native asset name exceeds bounds");
        }
        private static void ValidateTexture(Row row)
        {
            var profile=TextureType(row.TypeRva);var data=row.Texture;
            if(profile==null||data==null||row.Size!=profile.Size||row.Vtable!=profile.Vtable||row.Kind!="texture/"+profile.Kind||row.Values.Length!=0||row.Padding.Length!=0||row.Text!=null||row.Owner.Length!=0
                ||data.Fields==null||data.Fields.Length!=profile.Size-56||data.Padding==null||data.Padding.Length!=profile.Padding.Length||data.UnusedFlags==null||data.UnusedFlags.Length!=(profile.Kind=="texture-2d"||profile.Kind=="cubemap"?2:1)
                ||data.UnusedFlags[0]%2!=0||data.UnusedFlags.Length==2&&(data.UnusedFlags[1]&3)!=0
                ||data.ResourcePresent==null||data.ResourcePresent.Length!=profile.Resources.Length||data.ResourceMapped==null||data.ResourceMapped.Length!=profile.Resources.Length||data.ResourceAliases==null||data.ResourceAliases.Length!=profile.Resources.Length
                ||data.AsyncPresent==null||data.AsyncPresent.Length!=3||data.SurfacePresent==null||data.SurfaceAliases==null||data.SourceFaces==null||data.SourceFaces.Length>65536||data.SourceFaceUnused==null
                ||data.SourceFaceCapacity<data.SourceFaces.Length||data.SourceFaceCapacity>65536||data.SourceFaceUnused.Length!=(data.SourceFaceCapacity-data.SourceFaces.Length)*4
                ||data.SourceFaceCapacity!=0&&!data.SourceFaceStoragePresent||data.StreamImageHash==null||data.StreamImageHash.Length!=0&&(data.StreamImageHash.Length!=64||data.StreamImageHash.Any(value=>value<'0'||value>'9'&&value<'a'||value>'f')))
                throw Error("native texture typed schema differs");
            var booleans=new List<int>{112,113};
            if(profile.Kind=="texture-2d"||profile.Kind=="cubemap")booleans.AddRange(new[]{321,322,323,328,340});
            if(profile.Kind=="texture-3d"||profile.Kind=="cubemap-array")booleans.AddRange(new[]{312,313});
            if(profile.Kind=="texture-2d-array")booleans.AddRange(new[]{320,321});
            if(profile.Kind=="render-texture")booleans.AddRange(new[]{400,401,402,403,404});
            if(booleans.Any(offset=>data.Fields[offset-56]>1)||M.I32(data.Fields,104-56)<0||M.I32(data.Fields,124-56)<0||M.I32(data.Fields,124-56)>32)throw Error("texture native boolean/reference/mip fields differ");
            int[] dimensions=profile.Kind=="texture-2d"||profile.Kind=="cubemap"?new[]{284,288,308,312}:profile.Kind=="texture-3d"||profile.Kind=="texture-2d-array"?new[]{280,284,288}:profile.Kind=="render-texture"||profile.Kind=="lower-res-blit-texture"||profile.Kind=="cubemap-array"?new[]{280,284}:Array.Empty<int>();
            if(dimensions.Any(offset=>M.I32(data.Fields,offset-56)<0||M.I32(data.Fields,offset-56)>65536))throw Error("texture native dimensions exceed bounds");
            var masked=(byte[])data.Fields.Clone();ClearTextureProcessFields(masked,profile);if(!masked.SequenceEqual(data.Fields))throw Error("texture payload contains process pointers/IDs or inline padding");
            if(data.MainResourceMapped!=data.ResourceMapped[0])throw Error("texture primary resource observation differs");
            var resourceGroups=new List<bool>();
            for(var index=0;index<data.ResourceAliases.Length;index++) {
                var alias=data.ResourceAliases[index];
                if(data.ResourcePresent[index]!=(alias>=0)||alias < -1||alias>resourceGroups.Count||data.ResourceMapped[index]&&!data.ResourcePresent[index])throw Error("texture resource alias role differs");
                if(alias==resourceGroups.Count)resourceGroups.Add(data.ResourceMapped[index]);
                else if(alias>=0&&resourceGroups[alias]!=data.ResourceMapped[index])throw Error("texture aliased resource registration differs");
            }
            var render=profile.Kind=="render-texture";
            if(data.SurfacePresent.Length!=(render?3:0)||data.SurfaceAliases.Length!=data.SurfacePresent.Length)throw Error("render texture surface role schema differs");
            for(var index=0;index<data.SurfaceAliases.Length;index++)if(data.SurfacePresent[index]!=(data.SurfaceAliases[index]>=0)||data.SurfaceAliases[index]>index)throw Error("render texture surface alias differs");
            ValidateTextureString(data.StreamPath);ValidateTextureBuffer(data.Platform);ValidateTextureBuffer(data.Standalone);
            if(data.Platform.RawAllocation)throw Error("platform image has another storage owner");
            var raw=profile.Kind=="texture-3d"||profile.Kind=="texture-2d-array"||profile.Kind=="cubemap-array";
            if(data.Standalone.RawAllocation!=raw)throw Error("texture CPU storage kind differs from actual type");
            if(raw){var declared=profile.Kind=="cubemap-array"?M.U32(data.Fields,304-56):M.U64(data.Fields,304-56);
                if(declared!=(ulong)data.Standalone.Count||profile.Kind!="texture-3d"&&data.Standalone.Capacity!=data.Standalone.Count||profile.Kind=="texture-3d"&&data.Standalone.Capacity-data.Standalone.Count>255)throw Error("standalone texture native byte count/format tail differs");}

            if(!raw&&(data.Standalone.Count!=0||data.Standalone.Capacity!=0||data.Standalone.Present))throw Error("texture has invented standalone CPU image");
            if(data.Shared!=null){var image=data.Shared;
                if(image.Fields==null||image.Fields.Length!=72||image.Padding==null||image.Padding.Length!=4||image.Owners==null||image.Owners.Any(string.IsNullOrEmpty)
                    ||image.Owners.Distinct(StringComparer.Ordinal).Count()!=image.Owners.Length||!image.Owners.SequenceEqual(image.Owners.OrderBy(value=>value,StringComparer.Ordinal))
                    ||M.I32(image.Fields,4)<1||image.Fields.Skip(8).Take(32).Any(value=>value!=0)||image.Fields.Skip(52).Take(4).Any(value=>value!=0))throw Error("SharedTextureData typed schema differs");
                ValidateTextureBuffer(image.Data);if(image.Data.RawAllocation)throw Error("shared image has another allocator owner");
                if(M.I32(image.Fields,0)<0||new[]{40,44}.Any(offset=>M.I32(image.Fields,offset)<0||M.I32(image.Fields,offset)>65536)||M.I32(image.Fields,64)<0||M.I32(image.Fields,64)>4096||M.I32(image.Fields,68)<0||M.I32(image.Fields,68)>32)throw Error("shared image dimensions/mip/reference fields differ");
            }
            foreach(var reference in data.SourceFaces){
                if(reference==null||reference.Binding==null||reference.NativeTypeRva<0||reference.Null&&(reference.Loaded||reference.Binding.Length!=0||reference.Source!=null||reference.NativeTypeRva!=0)
                    ||!reference.Null&&reference.NativeTypeRva!=0&&reference.NativeTypeRva!=0x194ceb0&&reference.NativeTypeRva!=0x1960fb0)throw Error("cubemap source PPtr schema differs");
                if(reference.Source!=null){ResidentSerializedAssetImage.Validate(reference.Source);if(reference.Source.NativeTypeRva!=0&&reference.Source.NativeTypeRva!=reference.NativeTypeRva)throw Error("cubemap source type differs");
                    if(reference.Binding.Length!=0&&reference.Binding!=SourceKey(reference.Source))throw Error("cubemap source binding differs");}
            }
            if(profile.Kind!="cubemap"&&(data.SourceFaces.Length!=0||data.SourceFaceCapacity!=0||data.SourceFaceStoragePresent))throw Error("non-cubemap has source-face storage");
        }
        private static void ValidateTextureString(TextStorage text)
        {
            if(text==null||text.Bytes==null||text.Bytes.Length>16384||text.Capacity<0||text.Capacity>32*1024*1024||text.Label<0||text.Unused==null
                ||text.Inline&&(text.Capacity!=15||text.Bytes.Length>15)||!text.Inline&&text.Capacity!=0&&text.Capacity<text.Bytes.Length||text.Unused.Length!=(text.Inline?19-text.Bytes.Length:12))throw Error("texture source path schema differs");
        }
        private static void ValidateTextureBuffer(TextureBuffer buffer)
        {
            if(buffer==null||buffer.Count<0||buffer.Capacity<buffer.Count||buffer.Capacity>32*1024*1024||buffer.Bytes==null||buffer.Bytes.Length!=buffer.Capacity
                ||buffer.Label<0||buffer.Capacity!=0&&!buffer.Present||buffer.HeaderPadding==null||buffer.HeaderPadding.Length!=4)throw Error("texture byte storage schema differs");
        }
        private static IEnumerable<string> TextureGaps(Row row)
        {
            ValidateTexture(row);var data=row.Texture!;var profile=TextureType(row.TypeRva)!;
            // Always derive this gap: no editable bool/presence marker can claim
            // to contain actual device allocation, format/image/sampler state.
            yield return "native-texture/device-resource-entry-image-and-sampler-unproven:"+row.TypeKey;
            if(!data.ResourcePresent[0]||!data.MainResourceMapped)yield return "native-texture/resource-namespace-owner-unproven:"+row.TypeKey;
            for(var index=0;index<data.ResourcePresent.Length;index++)if(data.ResourcePresent[index]&&!data.ResourceMapped[index])yield return "native-texture/resource-slot-unregistered:"+row.TypeKey+"/"+profile.Resources[index];
            if(data.AsyncPresent.Any(value=>value)||M.I32(data.Fields,232-56)!=0||M.I32(data.Fields,236-56)!=0)yield return "native-texture/async-producer-fence-ownership:"+row.TypeKey;
            if(M.U32(data.Fields,136-56)!=0&&data.StreamImageHash.Length!=64)yield return "native-texture/external-stream-image-unresolved:"+row.TypeKey;
            if((profile.Kind=="texture-2d"||profile.Kind=="cubemap")&&(data.Fields[323-56]!=0||M.I32(data.Fields,336-56)>=0))yield return "native-texture/streaming-manager-residency-unproven:"+row.TypeKey;
            if(data.Shared!=null){if(!data.Shared.OwnersComplete||data.Shared.Owners.Length!=M.I32(data.Shared.Fields,4))yield return "native-texture/shared-cpu-image-external-owners:"+row.TypeKey;
                if(data.Shared.Owners.Length>1)yield return "native-texture/shared-cpu-image-alias-transfer-unproven:"+row.TypeKey;}
            foreach(var buffer in new[]{data.Platform,data.Standalone,data.Shared?.Data}.Where(value=>value!=null)){
                if(buffer!.Present&&!buffer.Owned)yield return "native-texture/borrowed-cpu-byte-storage:"+row.TypeKey;
                if(!buffer.RawAllocation&&buffer.Capacity!=buffer.Count)yield return "native-texture/array-capacity-canon-ownership-unproven:"+row.TypeKey;
            }
            if(data.SourceFaceCapacity!=data.SourceFaces.Length)yield return "native-texture/cubemap-source-vector-capacity-canon-unproven:"+row.TypeKey;
            foreach(var reference in data.SourceFaces.Where(value=>!value.Null)){
                if(reference.Binding.Length==0)yield return "native-texture/cubemap-source-face-unbound:"+row.TypeKey;
                if(reference.Source!=null)foreach(var gap in reference.Source.CoverageGaps)yield return "native-texture/"+gap;
            }
            if(profile.Kind=="render-texture")yield return "native-texture/render-surface-device-pool-and-registry-unproven:"+row.TypeKey;
            if(TextureHasPadding(data))yield return "native-texture/padding-needs-canon:"+row.Binding;
        }
        private static bool TextureHasPadding(TextureBody data)=>data.Padding.Concat(data.UnusedFlags).Concat(data.StreamPath.Unused).Concat(data.Platform.HeaderPadding)
            .Concat(data.Shared?.Padding??Array.Empty<byte>()).Concat(data.Shared?.Data.HeaderPadding??Array.Empty<byte>()).Any(value=>value!=0);
        private static TextureBody TextureDependency(TextureBody data)
        {
            var copy=JsonConvert.DeserializeObject<TextureBody>(JsonConvert.SerializeObject(data))!;
            Array.Clear(copy.Fields,240-56,4);Array.Clear(copy.Platform.Bytes,0,copy.Platform.Bytes.Length);Array.Clear(copy.Standalone.Bytes,0,copy.Standalone.Bytes.Length);
            if(copy.Shared!=null)Array.Clear(copy.Shared.Data.Bytes,0,copy.Shared.Data.Bytes.Length);return copy;
        }
        private static void RequireTextureReadonlyDependencies(Row source,Row target)
        {
            ValidateTexture(source);ValidateTexture(target);
            if(JsonConvert.SerializeObject(TextureDependency(source.Texture!))!=JsonConvert.SerializeObject(TextureDependency(target.Texture!)))throw Error("texture storage/configuration/alias dependency differs; reallocation/device mutation is unsupported");
        }
        private static void ApplyTextureKnownD(Row row,Live target,Action admitted)
        {
            // Called only after the containing full-directory coverage gate.
            // Existing pointers, capacities, labels, aliases and descriptors were
            // frozen in Prepare. CPU buffers are written in place, never resized.
            var local=target.TextureLocals[row.Binding];var data=row.Texture!;admitted();
            void Copy(TextureBuffer buffer,IntPtr pointer){if(buffer.Bytes.Length==0)return;if(!buffer.Owned||pointer==IntPtr.Zero)throw Error("texture in-place CPU write lacks owned storage");M.Write(pointer,buffer.Bytes);}
            Copy(data.Platform,local.Platform);Copy(data.Standalone,local.Standalone);if(data.Shared!=null)Copy(data.Shared.Data,local.SharedData);
            M.Write(IntPtr.Add(local.Pointer,240),M.Slice(data.Fields,240-56,4));admitted();
        }
        private static void CanonTexturePadding(Row row,Live target,Action admitted)
        {
            var local=target.TextureLocals[row.Binding];var profile=TextureType(row.TypeRva)!;var data=row.Texture!;admitted();
            foreach(var offset in profile.Padding)M.Write(IntPtr.Add(local.Pointer,offset),new byte[1]);
            M.Write(IntPtr.Add(local.Pointer,114),new[]{(byte)(data.Fields[114-56]&1)});
            if(profile.Kind=="texture-2d"||profile.Kind=="cubemap")M.Write(IntPtr.Add(local.Pointer,320),new[]{(byte)(data.Fields[320-56]&3)});
            M.Write(IntPtr.Add(local.Pointer,248+12),new byte[4]);
            var length=data.StreamPath.Bytes.Length;
            if(data.StreamPath.Inline)M.Write(IntPtr.Add(local.Pointer,144+9+length),new byte[15-length]);else M.Write(IntPtr.Add(local.Pointer,144+16),new byte[8]);
            M.Write(IntPtr.Add(local.Pointer,144+36),new byte[4]);
            if(data.Shared!=null){M.Write(IntPtr.Add(local.Shared,60),new byte[4]);M.Write(IntPtr.Add(local.Shared,16+12),new byte[4]);}admitted();
        }
        private static void CanonTextureSnapshot(TextureBody data)
        {
            Array.Clear(data.Padding,0,data.Padding.Length);Array.Clear(data.UnusedFlags,0,data.UnusedFlags.Length);Array.Clear(data.StreamPath.Unused,0,data.StreamPath.Unused.Length);
            Array.Clear(data.Platform.HeaderPadding,0,4);if(data.Shared!=null){Array.Clear(data.Shared.Padding,0,4);Array.Clear(data.Shared.Data.HeaderPadding,0,4);}
        }
    }
}
