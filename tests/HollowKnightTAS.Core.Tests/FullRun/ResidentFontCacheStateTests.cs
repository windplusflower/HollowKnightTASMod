using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentFontCacheStateTests
{
    private const BindingFlags Flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
    private static Type Load()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo!=null&&!File.Exists(Path.Combine(repo.FullName,"HollowKnightTAS.sln"))) repo=repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY")??Path.Combine(repo.FullName,"src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentFontCacheState",true)!;
    }
    private static object New(Type type,string nested)=>Activator.CreateInstance(type.GetNestedType(nested,Flags)!,true)!;
    private static object Call(Type type,string method,params object[] values)=>type.GetMethod(method,Flags)!.Invoke(null,values)!;
    private static void Set(object value,string field,object data)=>value.GetType().GetField(field,Flags)!.SetValue(value,data);
    private static object Get(object value,string field)=>value.GetType().GetField(field,Flags)!.GetValue(value)!;
    private static void Invalid(Action action)=>Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static object Data(Type type)
    {
        var data=New(type,"Data");Set(data,"Header",new byte[8]);Set(data,"Scalars",new[]{new byte[16],new byte[2],new byte[8],new byte[36]});Set(data,"IteratorAtEnd",true);return data;
    }
    [TestMethod]
    public void FontCaptureRequestsExactAtlasTypeAcceptedByBorrowLoaded()
    {
        // Capture needs a live native Font, so inspect its actual compiled call site.
        // This catches the run71 Texture/Texture2D mismatch before any native lookup.
        var type = Load();
        var capture = type.GetMethod("Capture",Flags)!;
        var code = capture.GetMethodBody()!.GetILAsByteArray()!;
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
            .Where(field=>field.FieldType==typeof(OpCode)).Select(field=>(OpCode)field.GetValue(null)!)
            .ToDictionary(op=>(ushort)op.Value);
        Type? requested = null;
        var borrowed = new System.Collections.Generic.List<Type>();
        for (int offset=0;offset<code.Length;) {
            ushort value = code[offset++];
            if (value==0xfe) value=(ushort)(0xfe00|code[offset++]);
            var op = opcodes[value];
            if (op==OpCodes.Ldtoken) requested=capture.Module.ResolveType(BitConverter.ToInt32(code,offset));
            if (op==OpCodes.Call) {
                var called=capture.Module.ResolveMethod(BitConverter.ToInt32(code,offset))!;
                if (called.Name=="BorrowLoaded") {
                    Assert.IsNotNull(requested);
                    borrowed.Add(requested);
                }
            }
            offset += op.OperandType switch {
                OperandType.InlineNone=>0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
                OperandType.InlineVar=>2,
                OperandType.InlineI8 or OperandType.InlineR=>8,
                OperandType.InlineSwitch=>4+4*BitConverter.ToInt32(code,offset),
                _=>4
            };
        }
        CollectionAssert.AreEqual(new[]{"UnityEngine.Material","UnityEngine.Texture2D","UnityEngine.Font"},borrowed.Select(t=>t.FullName).ToArray());
        var reference=type.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAssetReference",true)!;
        var texture=borrowed[1].BaseType!;
        var arguments=new object?[]{1,texture,new Func<object,string?>(_=>throw new AssertFailedException("binding must not run")),null};
        var error=Assert.ThrowsExactly<TargetInvocationException>(()=>reference.GetMethod("BorrowLoaded",Flags)!.Invoke(null,arguments));
        Assert.IsInstanceOfType<ArgumentException>(error.InnerException);
    }
    [TestMethod]
    public void CharacterRecordsPreserveAllFieldsIncludingFrameAndFloatBitsButDiscardPadding()
    {
        var type=Load();var bytes=new byte[120];
        BitConverter.GetBytes(unchecked((int)0x7fc01234)).CopyTo(bytes,4);
        BitConverter.GetBytes(unchecked((int)0x80000000)).CopyTo(bytes,36);
        BitConverter.GetBytes(7654321).CopyTo(bytes,52);bytes[56]=1;
        bytes[57]=0xa5;bytes[58]=0xa6;bytes[59]=0xa7;bytes[60]=17;
        var records=(byte[][])Call(type,"Records",bytes,60,57);
        Assert.AreEqual(2,records.Length);Assert.AreEqual(57,records[0].Length);
        Assert.AreEqual(unchecked((int)0x7fc01234),BitConverter.ToInt32(records[0],4));
        Assert.AreEqual(unchecked((int)0x80000000),BitConverter.ToInt32(records[0],36));
        Assert.AreEqual(7654321,BitConverter.ToInt32(records[0],52));Assert.AreEqual((byte)1,records[0][56]);Assert.AreEqual((byte)17,records[1][0]);
        Invalid(()=>Call(type,"Records",new byte[59],60,57));
    }
    [TestMethod]
    public void FontTypedCacheRejectsInvalidBooleanIteratorAndDuplicatePackingKeys()
    {
        var type=Load();var data=Data(type);Call(type,"Validate",data);
        Set(data,"Ascii",new[]{new byte[57]});((byte[][])Get(data,"Ascii"))[0][56]=2;
        Invalid(()=>Call(type,"Validate",data));((byte[][])Get(data,"Ascii"))[0][56]=1;
        Set(data,"Positions",new[]{new[]{3,4},new[]{3,4}});Invalid(()=>Call(type,"Validate",data));
        Set(data,"Positions",new[]{new[]{3,4}});Set(data,"IteratorAtEnd",false);Set(data,"Iterator",new[]{0,0});Invalid(()=>Call(type,"Validate",data));
        Set(data,"Iterator",new[]{3,4});Call(type,"Validate",data);
        var copy=data.GetType().GetMethod("Copy",Flags)!.Invoke(data,null)!;
        ((byte[][])Get(copy,"Ascii"))[0][52]=99;Assert.AreEqual((byte)0,((byte[][])Get(data,"Ascii"))[0][52]);
    }
    [TestMethod]
    public void KerningContentIsAReadOnlyDependencyEvenWhenAllMutableCacheFieldsDiffer()
    {
        var type=Load();var source=Data(type);var target=Data(type);
        Set(source,"Kerning",new byte[]{1,0,2,0,0,0,0,128});Set(target,"Kerning",new byte[]{1,0,2,0,0,0,0,128});
        ((byte[][])Get(source,"Scalars"))[3][0]=19;Call(type,"RequireDependencies",source,target);
        ((byte[])Get(target,"Kerning"))[7]=0;Invalid(()=>Call(type,"RequireDependencies",source,target));
    }
    [TestMethod]
    public void PackingTreeReadsValueAtTwentyEightAndRejectsWrongParentWithoutCallbacks()
    {
        var type=Load();var memory=Marshal.AllocHGlobal(112);
        try {
            Marshal.Copy(new byte[112],0,memory,112);var head=IntPtr.Add(memory,16);var node=IntPtr.Add(memory,48);
            Marshal.WriteIntPtr(memory,head);Marshal.WriteInt64(memory,8,1);
            for(int i=0;i<3;i++)Marshal.WriteIntPtr(head,i*8,node);Marshal.WriteByte(head,25,1);
            for(int i=0;i<3;i++)Marshal.WriteIntPtr(node,i*8,head);
            Marshal.WriteInt32(node,28,17);Marshal.WriteInt32(node,32,29);
            var view=Call(type,"ReadTree",memory,8,28);var values=(System.Collections.IList)Get(view,"Values");
            CollectionAssert.AreEqual(BitConverter.GetBytes(17).Concat(BitConverter.GetBytes(29)).ToArray(),(byte[])values[0]!);
            Marshal.WriteIntPtr(node,8,node);Invalid(()=>Call(type,"ReadTree",memory,8,28));
        } finally {Marshal.FreeHGlobal(memory);}
    }
    [TestMethod]
    public void IncompleteFontRestoreCannotBecomeCompleteByClearingDiagnosticGaps()
    {
        var type=Load();var data=Data(type);Set(data,"RequiredGaps",Array.Empty<string>());Invalid(()=>Call(type,"RequireComplete",data));
    }
    private sealed class NativeFixture : IDisposable
    {
        internal readonly System.Collections.Generic.HashSet<IntPtr> Owned = new();
        internal readonly System.Collections.Generic.List<IntPtr> Freed = new();
        internal int EraseCalls, InsertCalls;
        internal IntPtr Allocate(UIntPtr bytes,UIntPtr alignment,int label,int options,IntPtr file,int line)
        {
            Assert.AreEqual(16UL,alignment.ToUInt64());Assert.AreEqual(39,label);Assert.AreEqual(0,options);
            var pointer=Marshal.AllocHGlobal(checked((int)bytes.ToUInt64()));Owned.Add(pointer);return pointer;
        }
        internal void Free(IntPtr pointer,int label,IntPtr file,int line)
        {
            Assert.AreEqual(39,label);Assert.IsTrue(Owned.Remove(pointer));Freed.Add(pointer);Marshal.FreeHGlobal(pointer);
        }
        internal void Erase(IntPtr tree,IntPtr allocator,IntPtr root)
        {
            Assert.AreEqual(tree,allocator);EraseCalls++;var head=Marshal.ReadIntPtr(tree);
            void Retire(IntPtr node) {
                if(node==head)return;
                var left=Marshal.ReadIntPtr(node);var right=Marshal.ReadIntPtr(node,16);
                Retire(left);Retire(right);Assert.IsTrue(Owned.Remove(node));Marshal.FreeHGlobal(node);
            }
            Assert.AreEqual(Marshal.ReadIntPtr(head,8),root);Retire(root);
        }
        internal IntPtr Insert(IntPtr tree,IntPtr result,IntPtr value)
        {
            InsertCalls++;var head=Marshal.ReadIntPtr(tree);var parent=head;var current=Marshal.ReadIntPtr(head,8);bool left=false;
            int x=Marshal.ReadInt32(value),y=Marshal.ReadInt32(value,4);
            while(current!=head) {
                parent=current;int nx=Marshal.ReadInt32(current,28),ny=Marshal.ReadInt32(current,32);
                left=unchecked(x+y)!=unchecked(nx+ny)?unchecked(x+y)<unchecked(nx+ny):x<nx;
                current=Marshal.ReadIntPtr(current,left?0:16);
            }
            var node=Marshal.AllocHGlobal(40);Owned.Add(node);Marshal.Copy(new byte[40],0,node,40);
            Marshal.WriteIntPtr(node,0,head);Marshal.WriteIntPtr(node,8,parent);Marshal.WriteIntPtr(node,16,head);Marshal.WriteInt32(node,28,x);Marshal.WriteInt32(node,32,y);
            if(parent==head)for(int i=0;i<3;i++)Marshal.WriteIntPtr(head,i*8,node);
            else {
                Marshal.WriteIntPtr(parent,left?0:16,node);
                if(left&&Marshal.ReadIntPtr(head)==parent)Marshal.WriteIntPtr(head,node);
                if(!left&&Marshal.ReadIntPtr(head,16)==parent)Marshal.WriteIntPtr(head,16,node);
            }
            Marshal.WriteInt64(tree,8,Marshal.ReadInt64(tree,8)+1);Marshal.WriteIntPtr(result,node);Marshal.WriteByte(result,8,1);return result;
        }
        internal object Operations(Type api)
        {
            var type=api.GetNestedType("NativeOperations",Flags)!;var value=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            foreach(var pair in new[]{("allocate","Allocate"),("free","Free"),("Erase","Erase"),("Insert","Insert")}) {
                var field=type.GetField(pair.Item1,Flags)!;
                field.SetValue(value,Delegate.CreateDelegate(field.FieldType,this,GetType().GetMethod(pair.Item2,Flags)!));
            }
            return value;
        }
        public void Dispose(){foreach(var pointer in Owned)Marshal.FreeHGlobal(pointer);Owned.Clear();}
    }
    [TestMethod]
    public void FontVectorReplacementPublishesOnlyTargetLocalStorageAndReleasesUnadoptedAllocation()
    {
        var type=Load();using var native=new NativeFixture();var ops=native.Operations(type);var vector=Marshal.AllocHGlobal(24);
        var old=native.Allocate(new UIntPtr(16),new UIntPtr(16),39,0,IntPtr.Zero,0);
        try {
            Marshal.WriteIntPtr(vector,0,old);Marshal.WriteIntPtr(vector,8,IntPtr.Add(old,16));Marshal.WriteIntPtr(vector,16,IntPtr.Add(old,16));
            var fields=new byte[57];BitConverter.GetBytes(unchecked((int)0x80000000)).CopyTo(fields,4);fields[56]=1;
            var contents=(byte[])Call(type,"ExpandRecords",new[]{fields},60);
            Assert.AreEqual((byte)0,contents[59]);
            var replacementType=type.GetNestedType("VectorReplacement",Flags)!;
            var replacement=(IDisposable)Activator.CreateInstance(replacementType,Flags,null,new object[]{ops,vector,contents},null)!;
            replacementType.GetMethod("Publish",Flags)!.Invoke(replacement,null);replacement.Dispose();
            var actual=Marshal.ReadIntPtr(vector);Assert.AreNotEqual(old,actual);Assert.IsTrue(native.Freed.Contains(old));
            var bytes=new byte[60];Marshal.Copy(actual,bytes,0,60);CollectionAssert.AreEqual(contents,bytes);
            Assert.AreEqual(IntPtr.Add(actual,60),Marshal.ReadIntPtr(vector,8));Assert.AreEqual(IntPtr.Add(actual,60),Marshal.ReadIntPtr(vector,16));
            int alive=native.Owned.Count;
            var unadopted=(IDisposable)Activator.CreateInstance(replacementType,Flags,null,new object[]{ops,vector,new byte[16]},null)!;
            Assert.AreEqual(alive+1,native.Owned.Count);unadopted.Dispose();Assert.AreEqual(alive,native.Owned.Count);
            Assert.AreEqual(actual,Marshal.ReadIntPtr(vector));
        } finally {Marshal.FreeHGlobal(vector);}
    }
    [TestMethod]
    public void FontPackingRestorationDoesNotInventOriginAndRebindsIteratorToOwnedNode()
    {
        var type=Load();using var native=new NativeFixture();var ops=native.Operations(type);var impl=Marshal.AllocHGlobal(320);var head=Marshal.AllocHGlobal(32);
        try {
            Marshal.Copy(new byte[320],0,impl,320);Marshal.Copy(new byte[32],0,head,32);
            for(int i=0;i<3;i++)Marshal.WriteIntPtr(head,i*8,head);Marshal.WriteByte(head,25,1);
            Marshal.WriteIntPtr(impl,248,head);Marshal.WriteIntPtr(impl,264,head);
            var source=Data(type);Set(source,"Positions",new[]{new[]{2,3},new[]{4,5}});Set(source,"IteratorAtEnd",false);Set(source,"Iterator",new[]{4,5});
            Call(type,"RestorePacking",impl,source,ops);
            Assert.AreEqual(2L,Marshal.ReadInt64(impl,256));Assert.AreEqual(head,Marshal.ReadIntPtr(impl,248));
            var iterator=Marshal.ReadIntPtr(impl,264);Assert.AreEqual(4,Marshal.ReadInt32(iterator,28));Assert.AreEqual(5,Marshal.ReadInt32(iterator,32));
            Assert.AreEqual(1,native.EraseCalls);Assert.AreEqual(2,native.InsertCalls);
            var empty=Data(type);Call(type,"RestorePacking",impl,empty,ops);
            Assert.AreEqual(0L,Marshal.ReadInt64(impl,256));Assert.AreEqual(head,Marshal.ReadIntPtr(impl,264));
            for(int i=0;i<3;i++)Assert.AreEqual(head,Marshal.ReadIntPtr(head,i*8));Assert.AreEqual(0,native.Owned.Count);
        } finally {Marshal.FreeHGlobal(impl);Marshal.FreeHGlobal(head);}
    }
    [TestMethod]
    public void EmbeddedFaceSourceMatchingRequiresExactAddressLengthAndNoReadCallbackWithoutCopyingFontData()
    {
        var type=Load();var source=New(type,"SourceView");var data=Get(source,"Data");Set(data,"Key","resident-asset/serialized/font");Set(data,"FontDataBytes",32*1024*1024);Set(source,"Bytes",new IntPtr(12345));
        var sources=Array.CreateInstance(source.GetType(),1);sources.SetValue(source,0);
        Assert.AreEqual("resident-asset/serialized/font",Call(type,"MatchFaceSource",sources,new IntPtr(12345),32u*1024*1024,true));
        Assert.AreEqual("@unidentified-font-source",Call(type,"MatchFaceSource",sources,new IntPtr(12346),32u*1024*1024,true));
        Assert.AreEqual("@unidentified-font-source",Call(type,"MatchFaceSource",sources,new IntPtr(12345),32u*1024*1024-1,true));
        Assert.AreEqual("@unidentified-font-source",Call(type,"MatchFaceSource",sources,new IntPtr(12345),32u*1024*1024,false));
        Assert.IsFalse(data.GetType().GetFields(Flags).Any(field=>field.FieldType==typeof(byte[])));
    }

    [TestMethod]
    public void GeneratorInvalidationRunsOnceAfterValidationAndRejectsDriftBeforeNativeFlush()
    {
        var type=Load();var events=new System.Collections.Generic.List<string>{"dependencies","native-entries"};
        Call(type,"InvalidateGenerators",(Action)(()=>events.Add("admitted")),(Action)(()=>events.Add("unchanged")),
            (Action)(()=>events.Add("Flush")),(Action)(()=>events.Add("empty")));
        CollectionAssert.AreEqual(new[]{"dependencies","native-entries","admitted","unchanged","Flush","empty","admitted"},events);
        Assert.AreEqual(1,events.Count(value=>value=="Flush"));
        Assert.IsFalse(type.GetNestedType("Data",Flags)!.GetFields(Flags).Any(field=>field.Name.Contains("Generator")));
        bool flushed=false;
        Invalid(()=>Call(type,"InvalidateGenerators",(Action)(()=>{}),(Action)(()=>throw new InvalidDataException("drift")),
            (Action)(()=>flushed=true),(Action)(()=>Assert.Fail("must not inspect post-flush state"))));
        Assert.IsFalse(flushed);
    }
    [TestMethod]
    public void GeneratorDirectoryUsesLocalOwnerAndRetainsVectorStorageWhenInvalidated()
    {
        var type=Load();var memory=Marshal.AllocHGlobal(384);
        try {
            Marshal.Copy(new byte[384],0,memory,384);var entries=IntPtr.Add(memory,32);var owner=IntPtr.Add(memory,64);
            Marshal.WriteIntPtr(memory,0,entries);Marshal.WriteIntPtr(memory,8,IntPtr.Add(entries,8));Marshal.WriteIntPtr(memory,16,IntPtr.Add(entries,8));
            Marshal.WriteIntPtr(entries,owner);
            // GPU buffer presence is opaque local state. Inventory does not call or free it.
            Marshal.WriteIntPtr(owner,96,new IntPtr(12345));
            var directory=Call(type,"ReadGeneratorVector",memory);
            directory.GetType().GetMethod("RequireUnchanged",Flags)!.Invoke(directory,null);
            Marshal.WriteByte(owner,312,1);
            var error=Assert.ThrowsExactly<TargetInvocationException>(()=>directory.GetType().GetMethod("RequireUnchanged",Flags)!.Invoke(directory,null));
            Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
            Marshal.WriteByte(owner,312,0);
            Marshal.WriteIntPtr(memory,8,entries);
            directory.GetType().GetMethod("RequireEmpty",Flags)!.Invoke(directory,null);
            Assert.AreEqual(entries,Marshal.ReadIntPtr(memory));Assert.AreEqual(IntPtr.Add(entries,8),Marshal.ReadIntPtr(memory,16));
            Assert.AreEqual(new IntPtr(12345),Marshal.ReadIntPtr(owner,96));
        } finally {Marshal.FreeHGlobal(memory);}
    }

    [TestMethod]
    public void VectorPublicationReadbackFailureNeverReleasesPossiblyAdoptedAllocation()
    {
        var type=Load();
        foreach(bool wroteHeader in new[]{true,false}) {
            using var native=new NativeFixture();var operations=native.Operations(type);var vector=Marshal.AllocHGlobal(24);
            var old=native.Allocate(new UIntPtr(16),new UIntPtr(16),39,0,IntPtr.Zero,0);
            try {
                Marshal.WriteIntPtr(vector,0,old);Marshal.WriteIntPtr(vector,8,IntPtr.Add(old,16));Marshal.WriteIntPtr(vector,16,IntPtr.Add(old,16));
                Action<IntPtr,byte[]> write=(address,body)=>{
                    Assert.AreEqual(vector,address);
                    if(wroteHeader)Marshal.Copy(body,0,address,body.Length);
                    throw new InvalidDataException("publication readback failed");
                };
                var replacementType=type.GetNestedType("VectorReplacement",Flags)!;
                var replacement=(IDisposable)Activator.CreateInstance(replacementType,Flags,null,new object[]{operations,vector,new byte[32],write},null)!;
                var pending=native.Owned.Single(pointer=>pointer!=old);
                var error=Assert.ThrowsExactly<TargetInvocationException>(()=>replacementType.GetMethod("Publish",Flags)!.Invoke(replacement,null));
                Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
                replacement.Dispose();
                Assert.IsTrue(native.Owned.Contains(pending),"An indeterminate publication retains its allocation for fail-closed retirement.");
                Assert.IsTrue(native.Owned.Contains(old),"Old storage retires only after confirmed publication.");
                Assert.AreEqual(0,native.Freed.Count);
                Assert.AreEqual(wroteHeader?pending:old,Marshal.ReadIntPtr(vector));
                Invalid(()=>replacementType.GetMethod("Publish",Flags)!.Invoke(replacement,null));
            } finally {Marshal.FreeHGlobal(vector);}
        }
    }
    [TestMethod]
    public void ReadonlyPreflightDependenciesRejectFaceCharmapAndSourceChangesButNotOldAtlasBinding()
    {
        var type=Load();var source=Data(type);Set(source,"Material","new-material");Set(source,"Texture","new-atlas");Set(source,"TextureFromMaterial",true);
        var descriptor=New(type,"SourceFont");Set(descriptor,"Key","resident-asset/serialized/root");Set(descriptor,"FontDataBytes",32*1024*1024);
        var sources=Array.CreateInstance(descriptor.GetType(),1);sources.SetValue(descriptor,0);Set(source,"Sources",sources);
        var face=New(type,"Face");Set(face,"Family","font");Set(face,"Source","resident-asset/serialized/root");Set(face,"Metadata",new byte[36]);Set(face,"Charmap",new byte[8]);Set(face,"CharmapIndex",0);
        var faces=Array.CreateInstance(face.GetType(),1);faces.SetValue(face,0);Set(source,"Faces",faces);
        var target=source.GetType().GetMethod("Copy",Flags)!.Invoke(source,null)!;
        Set(target,"Material","old-material");Set(target,"Texture","old-atlas");Call(type,"RequireDependencies",source,target);
        var targetFace=((Array)Get(target,"Faces")).GetValue(0)!;
        ((byte[])Get(targetFace,"Charmap"))[0]=1;Invalid(()=>Call(type,"RequireDependencies",source,target));((byte[])Get(targetFace,"Charmap"))[0]=0;
        ((byte[])Get(targetFace,"Metadata"))[4]=1;Invalid(()=>Call(type,"RequireDependencies",source,target));((byte[])Get(targetFace,"Metadata"))[4]=0;
        var targetSource=((Array)Get(target,"Sources")).GetValue(0)!;Set(targetSource,"FontDataBytes",32*1024*1024+1);Invalid(()=>Call(type,"RequireDependencies",source,target));
    }

}
