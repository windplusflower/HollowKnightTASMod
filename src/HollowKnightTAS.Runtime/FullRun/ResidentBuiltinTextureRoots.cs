using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Provenance only. These roots do not establish native texture contents, GPU
    // state, immutable assets, or restoration qualification. Never invokes getters.
    internal static partial class ResidentBuiltinTextureRoots
    {
        private sealed class Root
        {
            internal readonly int Rva, Count, DeclaredRtti; internal readonly string Label;
            internal Root(int rva, int count, int declaredRtti, string label) { Rva=rva; Count=count; DeclaredRtti=declaredRtti; Label=label; }
        }
        private sealed class OwnerRoot
        {
            internal readonly int Rva, Vtable, Offset, DeclaredRtti;
            internal OwnerRoot(int rva,int vtable,int offset,int declaredRtti) { Rva=rva;Vtable=vtable;Offset=offset;DeclaredRtti=declaredRtti; }
        }
        internal sealed class TypeInfo
        {
            internal int Rva, BaseRva, Index, Size;
            internal bool ObjectDerived, Abstract, Stripped;
        }
        internal sealed class Entry { internal int TypeRva, Vtable; }
        internal const string Prefix = "resident-native-asset/builtin-texture/v1/";
        private static readonly Lazy<SortedDictionary<string,int>> declared = new Lazy<SortedDictionary<string,int>>(BuildDeclarations);
        private static SortedDictionary<string,int> BuildDeclarations() {
            var result = new SortedDictionary<string,int>(StringComparer.Ordinal);
            foreach (var root in Layout) for (var i=0;i<root.Count;i++)
                result.Add("S/"+Hex(root.Rva)+"/"+i.ToString(CultureInfo.InvariantCulture),root.DeclaredRtti);
            result.Add("P/"+Hex(ScreenBlit.Rva)+"/"+Hex(ScreenBlit.Offset),ScreenBlit.DeclaredRtti);
            return result;
        }
        internal static string[] DeclaredOrigins() => declared.Value.Keys.ToArray();
        private static void RequireSlotType(int family, ResidentNativeGlobalObjects.NativeClass actual,
            IReadOnlyDictionary<int,ResidentNativeGlobalObjects.NativeClass> types)
        {
            for (var depth=0;actual.Rva != family;depth++)
                if (depth >= types.Count || !types.TryGetValue(actual.BaseRva,out actual)) throw Error("builtin slot registered type differs");
        }
        internal static void RequireKeyType(string key, ResidentNativeGlobalObjects.NativeClass actual,
            IReadOnlyDictionary<int,ResidentNativeGlobalObjects.NativeClass> types)
        {
            foreach (var alias in Decode(key)) RequireSlotType(declared.Value[alias],actual,types);
        }
        internal static void ValidateBindings(string[] owners, ResidentNativeGlobalObjects.Data directory)
        {
            if (owners.Length != declared.Value.Count) throw Error("builtin slot count differs from the pinned layout");
            var rows = directory.Rows.Where(row => row.Binding.Length != 0).ToDictionary(row=>row.Binding,StringComparer.Ordinal);
            var types = directory.Classes.ToDictionary(type => type.Rva);
            var i=0;
            foreach (var slot in declared.Value) {
                var key = owners[i++];
                if (key.Length == 0) continue;
                if (!rows.TryGetValue(key,out var row) || !types.TryGetValue(row.TypeRva,out var type)
                    || !type.ObjectDerived || type.Abstract || type.Stripped) throw Error("builtin slot lacks its concrete registered owner");
                RequireSlotType(slot.Value,type,types);
            }
        }
        internal sealed class Observation
        {
            private readonly IntPtr engine; private readonly int engineLength;
            private readonly Func<IntPtr,int,byte[]> read; private readonly Action admitted;
            private readonly List<Tuple<IntPtr,byte[]>> witnesses=new List<Tuple<IntPtr,byte[]>>();
            private readonly Dictionary<string,IntPtr> origins=new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            private readonly Dictionary<string,int> expected=new Dictionary<string,int>(StringComparer.Ordinal);
            internal IReadOnlyDictionary<string,IntPtr> Origins => new ReadOnlyDictionary<string,IntPtr>(origins);
            // Only a candidate for the early reference pass. The registry
            // pass must subsequently validate every slot and its actual RTTI.
            internal string? Candidate(IntPtr pointer)
            {
                if (pointer == IntPtr.Zero) return null;
                var aliases = origins.Where(pair => pair.Value == pointer).Select(pair => pair.Key)
                    .OrderBy(key => key, StringComparer.Ordinal).ToArray();
                return aliases.Length == 0 ? null : Encode(aliases);
            }
            internal Observation(IntPtr engine,int engineLength,Func<IntPtr,int,byte[]> read,Action admitted)
            {
                if(engine==IntPtr.Zero || engineLength<=0 || read==null || admitted==null) throw Error("observation admission differs");
                this.engine=engine;this.engineLength=engineLength;this.read=read;this.admitted=admitted;
                foreach(var root in Layout) {
                    if(root.Count<1 || root.Count>4096) throw Error("generated array bounds differ");
                    var values=Read(Engine(root.Rva,checked(root.Count*8)),checked(root.Count*8));
                    for(int index=0;index<root.Count;index++) Add("S/"+Hex(root.Rva)+"/"+index.ToString(CultureInfo.InvariantCulture),Ptr(values,index*8),root.DeclaredRtti);
                }
                var owner=Ptr(Read(Engine(ScreenBlit.Rva,8),8),0);var leaf=IntPtr.Zero;
                if(owner!=IntPtr.Zero) {
                    if(Ptr(Read(owner,8),0)!=Engine(ScreenBlit.Vtable,8)) throw Error("typed ScreenManager owner vtable differs");
                    leaf=Ptr(Read(IntPtr.Add(owner,ScreenBlit.Offset),8),0);
                }
                Add("P/"+Hex(ScreenBlit.Rva)+"/"+Hex(ScreenBlit.Offset),leaf,ScreenBlit.DeclaredRtti);
                RequireUnchanged();
            }
            private void Add(string key,IntPtr pointer,int rtti) { origins.Add(key,pointer);expected.Add(key,rtti); }
            private IntPtr Engine(int rva,int length) { if(rva<=0 || rva>engineLength-length)throw Error("generated address is outside pinned engine");return IntPtr.Add(engine,rva); }
            private int Rva(IntPtr pointer,int length) { long rva=pointer.ToInt64()-engine.ToInt64();if(rva<=0 || rva>engineLength-length)throw Error("actual native type is outside pinned engine");return (int)rva; }
            private byte[] Read(IntPtr pointer,int length) { admitted();var value=read(pointer,length);if(value==null || value.Length!=length)throw Error("root observation is unreadable");witnesses.Add(Tuple.Create(pointer,(byte[])value.Clone()));return value; }
            internal void RequireUnchanged() { admitted();foreach(var item in witnesses) { var actual=read(item.Item1,item.Item2.Length);if(actual==null || !actual.SequenceEqual(item.Item2))throw Error("static root, owner chain or native identity changed"); }admitted(); }
            internal Live Complete(ResidentNativeGlobalObjects.CaptureResult globals)
            {
                if(globals==null)throw Error("native registration is absent");
                var types=globals.Snapshot.Classes.Select(type=>new TypeInfo { Rva=type.Rva,BaseRva=type.BaseRva,Index=type.Index,Size=type.Size,ObjectDerived=type.ObjectDerived,Abstract=type.Abstract,Stripped=type.Stripped }).ToArray();
                return Complete(globals.RegisteredPointers,id=> {
                    if(!globals.RegisteredRows.TryGetValue(id,out var row))throw Error("native registration row is absent");
                    return new Entry { TypeRva=row.TypeRva,Vtable=row.Vtable };
                },types);
            }
            // Testable observation kernel. Production registrations/classes come only
            // from the independently verified native ID and type-set directory above.
            internal Live Complete(IReadOnlyDictionary<int,IntPtr> registered,Func<int,Entry> entry,IReadOnlyList<TypeInfo> classes)
            {
                RequireUnchanged();var byRva=new Dictionary<int,TypeInfo>();
                for(int i=0;i<classes.Count;i++) { var type=classes[i];if(type==null || type.Rva<=0 || type.Index!=i || byRva.ContainsKey(type.Rva))throw Error("native RTTI directory differs");byRva.Add(type.Rva,type); }
                var aliases=new Dictionary<IntPtr,List<string>>();
                foreach(var origin in origins) {
                    if(origin.Value==IntPtr.Zero)continue;
                    if(!aliases.TryGetValue(origin.Value,out var list)) {
                        var header=Read(origin.Value,16);int id=BitConverter.ToInt32(header,8),index=(int)(BitConverter.ToUInt32(header,12)>>21);
                        if(id==0 || !registered.TryGetValue(id,out var pointer) || pointer!=origin.Value)throw Error("nonempty static root lacks exact registered pointer/instanceID");
                        var row=entry(id);if(row==null || index<0 || index>=classes.Count || classes[index].Rva!=row.TypeRva)throw Error("static root cached native RTTI differs");
                        var actual=classes[index];if(!actual.ObjectDerived || actual.Abstract || actual.Stripped || actual.Size<48)throw Error("static root native type is pending or invalid");
                        var vtable=Ptr(header,0);if(Rva(vtable,72)!=row.Vtable)throw Error("static root registered vtable differs");
                        var getter=Ptr(Read(IntPtr.Add(vtable,64),8),0);Rva(getter,8);var code=Read(getter,8);
                        if(code[0]!=0x48 || code[1]!=0x8d || code[2]!=0x05 || code[7]!=0xc3
                            || Rva(new IntPtr(checked(getter.ToInt64()+7+BitConverter.ToInt32(code,3))),80)!=row.TypeRva)throw Error("static root actual virtual RTTI differs");
                        list=new List<string>();aliases.Add(origin.Value,list);
                    }
                    // Different aliases may have different declared base types. Validate
                    // every declaration even when the object's identity was seen already.
                    int nativeIndex=(int)(BitConverter.ToUInt32(Read(origin.Value,16),12)>>21);
                    if(nativeIndex<0 || nativeIndex>=classes.Count)throw Error("static root native type index changed");
                    var cursor=classes[nativeIndex];var seen=new HashSet<int>();
                    while(cursor.Rva!=expected[origin.Key]) {
                        if(!seen.Add(cursor.Rva) || cursor.BaseRva==0 || !byRva.TryGetValue(cursor.BaseRva,out cursor))throw Error("static root violates declared RTTI inheritance");
                    }
                    list.Add(origin.Key);
                }
                RequireUnchanged();return new Live(this,origins,aliases.ToDictionary(pair=>pair.Key,pair=>pair.Value.OrderBy(value=>value,StringComparer.Ordinal).ToArray()));
            }
        }
        internal sealed class Live
        {
            private readonly Observation observation;
            private readonly Dictionary<IntPtr,string[]> canonicalAliases;
            internal readonly IReadOnlyDictionary<IntPtr,string[]> Aliases;
            internal readonly IReadOnlyDictionary<string,IntPtr> Origins;
            internal Live(Observation observation,Dictionary<string,IntPtr> origins,Dictionary<IntPtr,string[]> aliases) {
                this.observation=observation;Origins=new ReadOnlyDictionary<string,IntPtr>(new Dictionary<string,IntPtr>(origins,StringComparer.Ordinal));canonicalAliases=aliases.ToDictionary(pair=>pair.Key,pair=>(string[])pair.Value.Clone());Aliases=new ReadOnlyDictionary<IntPtr,string[]>(aliases.ToDictionary(pair=>pair.Key,pair=>(string[])pair.Value.Clone()));
            }
            internal void RequireUnchanged()=>observation.RequireUnchanged();
            internal string Key(IntPtr pointer) { RequireUnchanged();if(!canonicalAliases.TryGetValue(pointer,out var aliases))throw Error("pointer has no validated builtin origin");return Encode(aliases); }
            internal IntPtr Resolve(string key) {
                RequireUnchanged();var aliases=Decode(key);var pointer=IntPtr.Zero;
                foreach(var alias in aliases) { if(!Origins.TryGetValue(alias,out var value) || value==IntPtr.Zero || pointer!=IntPtr.Zero && pointer!=value)throw Error("builtin source aliases are absent or split");pointer=value; }
                if(!canonicalAliases.TryGetValue(pointer,out var actual) || !actual.SequenceEqual(aliases))throw Error("builtin source alias set formed or changed");return pointer;
            }
        }
        internal static Observation Observe(Action admitted) { var engine=LoadingEngineClockProbe.RequirePinnedEngine();return new Observation(engine.BaseAddress,engine.ModuleMemorySize,M.Read,admitted); }
        internal static Observation Observe(IntPtr engine,int length,Func<IntPtr,int,byte[]> read,Action admitted)=>new Observation(engine,length,read,admitted);
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals,Action admitted)=>Observe(admitted).Complete(globals);
        internal static IReadOnlyDictionary<IntPtr,IntPtr> RequireSameTopology(Live source,Live target) {
            source.RequireUnchanged();target.RequireUnchanged();
            if(!source.Origins.Keys.OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(target.Origins.Keys.OrderBy(x=>x,StringComparer.Ordinal)))throw Error("builtin origin schema differs");
            foreach(var pair in source.Origins)if((pair.Value==IntPtr.Zero)!=(target.Origins[pair.Key]==IntPtr.Zero))throw Error("builtin null slot topology changed");
            var result=new Dictionary<IntPtr,IntPtr>();var used=new HashSet<IntPtr>();
            foreach(var pointer in source.Aliases.Keys) { var destination=target.Resolve(source.Key(pointer));if(!used.Add(destination))throw Error("builtin origins are not one-to-one");result.Add(pointer,destination); }
            return new ReadOnlyDictionary<IntPtr,IntPtr>(result);
        }
        internal static string Encode(string[] aliases) {
            if(aliases==null || aliases.Length==0 || aliases.Length>32768 || aliases.Any(x=>!ValidOrigin(x)) || !aliases.SequenceEqual(aliases.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal)))throw Error("builtin alias identity is not canonical");
            return Prefix+EngineSha256+"/"+Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join("|",aliases)));
        }
        internal static string[] Decode(string key) {
            string prefix=Prefix+EngineSha256+"/";if(key==null || !key.StartsWith(prefix,StringComparison.Ordinal) || key.Length>1024*1024)throw Error("builtin key engine/schema differs");
            try { var aliases=new UTF8Encoding(false,true).GetString(Convert.FromBase64String(key.Substring(prefix.Length))).Split('|');if(Encode(aliases)!=key)throw Error("builtin key is not canonical");return aliases; }
            catch(FormatException){throw Error("builtin key encoding differs");}catch(DecoderFallbackException){throw Error("builtin key is not UTF8");}
        }
        private static bool ValidOrigin(string key) {
            if(key==null)return false;
            return declared.Value.ContainsKey(key);
        }
        private static string Hex(int value)=>value.ToString("x",CultureInfo.InvariantCulture);
        private static IntPtr Ptr(byte[] value,int offset)=>new IntPtr(BitConverter.ToInt64(value,offset));
        private static InvalidDataException Error(string message)=>new InvalidDataException("Resident builtin texture roots: "+message);
    }
}
