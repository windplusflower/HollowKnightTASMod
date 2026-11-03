using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentAnimatorStateTests
{
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"))
            .GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState", true)!;
    }
    private static object Call(Type type, string method, params object[] arguments) => type.GetMethod(method, Flags)!.Invoke(null, arguments)!;
    private static void Invalid(Action action)
    {
        var error = Assert.ThrowsExactly<TargetInvocationException>(action);
        Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
    }
    private static object New(Type type, string nested) => Activator.CreateInstance(type.GetNestedType(nested, Flags)!, true)!;
    private static void Set(object value, string name, object data) => value.GetType().GetField(name, Flags)!.SetValue(value, data);
    private static void Rows(object target, string field, params object[] values)
    {
        var info = target.GetType().GetField(field, Flags)!;
        var array = Array.CreateInstance(info.FieldType.GetElementType()!, values.Length);
        for (int i = 0; i < values.Length; i++) array.SetValue(values[i], i);
        info.SetValue(target, array);
    }
    private sealed class Memory : IDisposable
    {
        private readonly List<IntPtr> allocations = new();
        internal readonly IntPtr Root, Table, Machine, Weights, Layers, Values, Bools, Floats;
        internal Memory()
        {
            Root = Alloc(48); Table = Alloc(8); Machine = Alloc(120); Weights = Alloc(8); Layers = Alloc(8); Values = Alloc(96);
            Bools = Alloc(2); Floats = Alloc(4);
            Marshal.WriteInt32(Root, 0, 1); Relative(Root, 8, Table); Relative(Table, 0, Machine);
            Marshal.WriteInt32(Root, 16, 2); Relative(Root, 24, Layers); Relative(Root, 32, Values);
            Marshal.WriteInt32(Machine, 0, 2); Relative(Machine, 8, Weights);
            Marshal.WriteInt32(Values, 48, 1); Relative(Values, 56, Floats);
            Marshal.WriteInt32(Values, 80, 2); Relative(Values, 88, Bools);
        }
        internal static void Relative(IntPtr owner, int member, IntPtr target)
            => Marshal.WriteInt64(owner, member, target.ToInt64() - owner.ToInt64() - member);
        private IntPtr Alloc(int length)
        {
            var pointer = Marshal.AllocHGlobal(length);
            allocations.Add(pointer); Marshal.Copy(new byte[length], 0, pointer, length);
            return pointer;
        }
        public void Dispose() { foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer); }
    }
    private static IList Writes(Type type) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;
    [TestMethod]
    public void EvaluationFieldProjectionExcludesOnlyProvenAlignmentPadding()
    {
        var type = Load(); var body = new byte[184];
        var padding = new[] {4,5,6,7,131,137,138,139,140,141,142,143};
        var clean = (byte[][])Call(type,"EvaluationFields",body,0);
        foreach (var offset in padding) body[offset] = 0xed;
        var padded = (byte[][])Call(type,"EvaluationFields",body,0);
        for (int i = 0; i < clean.Length; i++) CollectionAssert.AreEqual(clean[i],padded[i]);
        foreach (var pair in new (int Offset,int Field,int At)[] {(0,0,0),(108,4,4),(112,4,8),(116,4,12),(120,4,16),(124,4,20),(132,5,0),(168,6,0)}) {
            body[pair.Offset] = 0xa5; var fields = (byte[][])Call(type,"EvaluationFields",body,0);
            Assert.AreEqual((byte)0xa5,fields[pair.Field][pair.At]); body[pair.Offset] = 0;
        }
        Invalid(() => Call(type,"EvaluationFields",new byte[183],0));
    }
    [TestMethod]
    public void EvaluationRetainsSignedIndexDAndDerivesHardGapsFromEveryMalformedBoolean()
    {
        var type = Load(); var evaluation = New(type,"Evaluation"); var cache = evaluation.GetType().GetField("Cache",Flags)!.GetValue(evaluation)!;
        var body = new byte[184];
        foreach (var offset in new[] {108,112,116,120,124,132}) Buffer.BlockCopy(BitConverter.GetBytes(-1),0,body,offset,4);
        Set(cache,"Fields",Call(type,"EvaluationFields",body,0)); Call(type,"ValidateEvaluation",evaluation);
        foreach (var entry in new (int Offset,string Name)[] {(128,"hasRootTransformValues"),(129,"allowConstantCurveOptimization"),(130,"isHuman"),(136,"affectMassCenter")}) {
            body[entry.Offset] = 2; Set(cache,"Fields",Call(type,"EvaluationFields",body,0));
            Call(type,"ValidateEvaluation",evaluation);
            var gaps = ((IEnumerable<string>)Call(type,"EvaluationGaps",evaluation)).ToArray();
            Assert.IsTrue(gaps.Any(gap => gap.Contains(entry.Name) && gap.Contains("+"+entry.Offset+"=2")));
            Assert.AreEqual((byte)2,((byte[][])cache.GetType().GetField("Fields",Flags)!.GetValue(cache)!)[entry.Offset == 136 ? 5 : 4][entry.Offset == 136 ? 4 : entry.Offset - 104]);
            var data = MeasuredCold(type,out var row,out _); Rows(row,"Evaluation",evaluation,New(type,"Evaluation"));
            var diagnostic = (string[])Call(type,"RequiredCoverageGaps",data); Array.Clear(diagnostic,0,diagnostic.Length);
            Invalid(() => Call(type,"RequireCompleteCoverage",data)); Invalid(() => Call(type,"RequireCanonicalizableCoverage",data));
            body[entry.Offset] = 1; Set(cache,"Fields",Call(type,"EvaluationFields",body,0)); Call(type,"ValidateEvaluation",evaluation);
            body[entry.Offset] = 0;
        }
        Set(cache,"Fields",new[] {new byte[3],new byte[8],new byte[8],new byte[8],new byte[27],new byte[5],new byte[8]});
        Invalid(() => Call(type,"ValidateEvaluation",evaluation));
    }
    [TestMethod]
    public void ControllerTransfersTransitionTimesLayerWeightsAndParameterBitsAcrossDiscontiguousAllocations()
    {
        var type = Load(); using var source = new Memory(); using var target = new Memory();
        Marshal.WriteInt32(source.Root, 40, 2); Marshal.WriteByte(source.Root, 44, 1);
        Marshal.WriteInt32(source.Machine, 16, 3); Marshal.WriteInt32(source.Machine, 48, 0x3eaaf113);
        Marshal.WriteInt32(source.Machine, 100, 0x3f174302); Marshal.WriteByte(source.Machine, 113, 1);
        Marshal.WriteInt32(source.Weights, 4, 0x3ec00000); Marshal.WriteInt32(source.Layers, 4, 0x3e800000);
        Marshal.WriteInt32(source.Floats, 0, unchecked((int)0x80000000)); Marshal.WriteByte(source.Bools, 1, 1);
        var targetTableOffset = Marshal.ReadInt64(target.Root, 8);
        var targetValuesOffset = Marshal.ReadInt64(target.Root, 32);
        var saved = Call(type, "CaptureMemory", source.Root);
        var writes = Writes(type); Call(type, "PrepareMemory", saved, target.Root, writes);
        foreach (var write in writes) write!.GetType().GetMethod("Apply", Flags)!.Invoke(write, null);
        Assert.AreEqual(targetTableOffset, Marshal.ReadInt64(target.Root, 8)); Assert.AreEqual(targetValuesOffset, Marshal.ReadInt64(target.Root, 32));
        Assert.AreEqual(3, Marshal.ReadInt32(target.Machine, 16)); Assert.AreEqual(0x3eaaf113, Marshal.ReadInt32(target.Machine, 48));
        Assert.AreEqual(0x3f174302, Marshal.ReadInt32(target.Machine, 100)); Assert.AreEqual((byte)1, Marshal.ReadByte(target.Machine, 113));
        Assert.AreEqual(0x3ec00000, Marshal.ReadInt32(target.Weights, 4)); Assert.AreEqual(0x3e800000, Marshal.ReadInt32(target.Layers, 4));
        Assert.AreEqual(unchecked((int)0x80000000), Marshal.ReadInt32(target.Floats)); Assert.AreEqual((byte)1, Marshal.ReadByte(target.Bools, 1));
        Assert.AreEqual((byte)1, Marshal.ReadByte(target.Root, 44));
    }
    [TestMethod]
    public void DanglingRelativeBankAndOverlapWithControllerHeaderAreRejected()
    {
        var type = Load(); using var native = new Memory();
        Marshal.WriteInt64(native.Values, 56, 0); Invalid(() => Call(type, "CaptureMemory", native.Root));
        Memory.Relative(native.Values, 56, native.Root); Invalid(() => Call(type, "CaptureMemory", native.Root));
    }
    [TestMethod]
    public void AChangedTargetBankShapeFailsBeforeStateOrTriggerMutation()
    {
        var type = Load(); using var source = new Memory(); using var target = new Memory();
        Marshal.WriteByte(source.Machine, 112, 1); Marshal.WriteByte(source.Bools, 1, 1);
        var saved = Call(type, "CaptureMemory", source.Root);
        Marshal.WriteInt32(target.Values, 80, 1);
        Invalid(() => Call(type, "PrepareMemory", saved, target.Root, Writes(type)));
        Assert.AreEqual((byte)0, Marshal.ReadByte(target.Machine, 112)); Assert.AreEqual((byte)0, Marshal.ReadByte(target.Bools, 1));
    }
    private static object Row(Type type)
    {
        var row = New(type, "Row"); Set(row, "Owner", "owner");
        Set(row, "Fields", new[] {2,1,2,12,157,4,16,14,4}.Select(length => new byte[length]).ToArray());
        Set(row, "Padding", new byte[7]); return row;
    }
    [TestMethod]
    public void EmptyPublicAnimationStateNeverProvesDormantNativeClosure()
    {
        var type = Load(); var data = New(type, "Data"); Call(type, "RequireCompleteCoverage", data);
        Rows(data, "Rows", Row(type)); Call(type, "Validate", data);
        Invalid(() => Call(type, "RequireCompleteCoverage", data)); Invalid(() => Call(type, "RequireCanonicalizableCoverage", data));
    }
    [TestMethod]
    public void RepeatGraphRegistrationAndDanglingPlayableOrControllerBindingAreRejected()
    {
        var type = Load(); var data = New(type, "Data"); var row = Row(type); Rows(data, "Rows", row);
        Set(row, "Graph", 0); var manager = New(type, "Director"); Set(data, "Registry", manager); Set(manager, "Present", true);
        Set(manager.GetType().GetField("Queues",Flags)!.GetValue(manager)!,"OutputCounts",new[] {0});
        Set(manager, "Playing", new[] {0}); Set(manager, "Stopped", new[] {0}); Invalid(() => Call(type, "Validate", data));
        Set(manager, "Stopped", Array.Empty<int>()); Call(type, "Validate", data);
        Set(row, "Primary", 0); Invalid(() => Call(type, "Validate", data)); Set(row, "Primary", -1);
        Set(row, "Controller", "asset1"); Invalid(() => Call(type, "Validate", data));
    }
    [TestMethod]
    public void DirectorReorderRetainsReciprocalTargetLinksAndRejectsMembershipMigration()
    {
        var type = Load();
        var head = Marshal.AllocHGlobal(16); var first = Marshal.AllocHGlobal(16); var second = Marshal.AllocHGlobal(16);
        static void Links(IntPtr address, IntPtr prior, IntPtr next)
        { Marshal.WriteIntPtr(address, 0, prior); Marshal.WriteIntPtr(address, 8, next); }
        try {
            Links(head, second, first); Links(first, head, second); Links(second, first, head);
            var nodes = new Dictionary<int, IntPtr> { [7] = first, [2] = second };
            var writes = Writes(type); Call(type, "PrepareOrder", head, nodes, new[] {7,2}, new[] {2,7}, writes);
            foreach (var write in writes) write!.GetType().GetMethod("Apply", Flags)!.Invoke(write, null);
            Assert.AreEqual(second, Marshal.ReadIntPtr(head, 8)); Assert.AreEqual(first, Marshal.ReadIntPtr(head));
            Assert.AreEqual(first, Marshal.ReadIntPtr(second, 8)); Assert.AreEqual(head, Marshal.ReadIntPtr(second));
            Assert.AreEqual(head, Marshal.ReadIntPtr(first, 8)); Assert.AreEqual(second, Marshal.ReadIntPtr(first));
            Invalid(() => Call(type, "PrepareOrder", head, nodes, new[] {2,7}, new[] {7}, Writes(type)));
        } finally { Marshal.FreeHGlobal(head); Marshal.FreeHGlobal(first); Marshal.FreeHGlobal(second); }
    }
    [TestMethod]
    public void ReadbackDetectsDifferentRootMotionAndTargetMatchingTimeBits()
    {
        var type = Load(); var source = New(type, "Data"); var actual = New(type, "Data");
        var left = Row(type); var right = Row(type);
        Rows(source, "Rows", left); Rows(actual, "Rows", right);
        Call(type, "RequireEqual", source, actual);
        var block = ((byte[][])right.GetType().GetField("Fields", Flags)!.GetValue(right)!)[4];
        block[0] = 1; Invalid(() => Call(type, "RequireEqual", source, actual)); block[0] = 0;
        block[92] = 1; Invalid(() => Call(type, "RequireEqual", source, actual));
    }
    private sealed class RuntimeImage : IDisposable
    {
        internal readonly IntPtr Player = Marshal.AllocHGlobal(1312);
        internal IntPtr At(int offset) => IntPtr.Add(Player, offset);
        internal void Int(int offset, int value) => Marshal.WriteInt32(Player, offset, value);
        internal void Ptr(int field, int dest) => Marshal.WriteIntPtr(Player, field, At(dest));
        private void Rel(int field, int dest) => Marshal.WriteInt64(Player, field, dest - field);
        internal RuntimeImage()
        {
            Marshal.Copy(new byte[1312], 0, Player, 1312);
            // One machine/layer, two trigger/value entries, two blend nodes.
            Ptr(296,480); Ptr(304,672); Ptr(320,736); Ptr(400,1264); Ptr(408,1280); Ptr(440,880); Int(448,1);
            Int(480,1); Int(496,1); Rel(504,544); Rel(512,624); Rel(520,1264); Rel(544,560);
            Int(560,1); Rel(568,896); Int(612,1); Int(624,2); Rel(632,640); Int(644,4); Int(656,9);
            Ptr(680,368); Ptr(688,1280); Ptr(696,704); Ptr(736,784); Ptr(744,792); Ptr(752,864); Int(760,1); Ptr(768,868);
            Ptr(784,800); Ptr(792,832); Ptr(800,1040); Ptr(808,624); Ptr(816,752); Int(824,2); Ptr(848,880); Int(832,128); Int(836,128); Int(840,128);
            Rel(896,912); Int(928,1); Rel(936,1008); Int(944,1); Rel(952,1016); Rel(1016,1024); Int(1024,2);
            Ptr(1040,1072); Ptr(1048,1080); Ptr(1056,1088); Int(1064,1);
            Ptr(1072,1104); Ptr(1080,1120); Ptr(1088,1136); Ptr(1104,624); Ptr(1112,1264);
            Ptr(1120,1168); Int(1128,2); Ptr(1136,1216); Ptr(1144,1224); Ptr(1152,1232); Ptr(1160,1240);
        }
        public void Dispose() => Marshal.FreeHGlobal(Player);
    }
    [TestMethod]
    public void ControllerGotoTriggerOutputAndBlendScratchRestoreThroughLocalBindings()
    {
        var type = Load(); using var source = new RuntimeImage(); using var target = new RuntimeImage();
        source.Int(704, 452); source.Int(716, 0x3e197364); Marshal.WriteByte(source.Player, 724, 1);
        Marshal.WriteByte(source.Player, 865, 1); source.Int(868, unchecked((int)0x80000000)); source.Int(836, 64);
        source.Int(1132, 0x3f765421); source.Int(1168+24+8, 0x3f114422); source.Int(1252, 0x3e8dee11);
        // Padding never supplies a scalar or semantic native link.
        Marshal.WriteByte(target.Player, 725, 0xa5); Marshal.WriteByte(target.Player, 1168+13, 0xa5);
        Marshal.WriteIntPtr(target.Player, 680, IntPtr.Zero); Marshal.WriteIntPtr(target.Player, 1112, IntPtr.Zero);
        var saved = Call(type, "CaptureRuntime", source.Player);
        var writes = Writes(type); Call(type, "PrepareRuntime", saved, target.Player, writes);
        foreach (var write in writes) write!.GetType().GetMethod("Apply", Flags)!.Invoke(write, null);
        Assert.AreEqual(452, Marshal.ReadInt32(target.Player,704)); Assert.AreEqual(0x3e197364, Marshal.ReadInt32(target.Player,716));
        Assert.AreEqual((byte)1, Marshal.ReadByte(target.Player,724)); Assert.AreEqual((byte)1, Marshal.ReadByte(target.Player,865));
        Assert.AreEqual(unchecked((int)0x80000000), Marshal.ReadInt32(target.Player,868)); Assert.AreEqual(64, Marshal.ReadInt32(target.Player,836));
        Assert.AreEqual(0x3f765421, Marshal.ReadInt32(target.Player,1132)); Assert.AreEqual(0x3f114422, Marshal.ReadInt32(target.Player,1200));
        Assert.AreEqual(0x3e8dee11, Marshal.ReadInt32(target.Player,1252)); Assert.AreEqual(target.At(368), Marshal.ReadIntPtr(target.Player,680));
        Assert.AreEqual(target.At(1264), Marshal.ReadIntPtr(target.Player,1112));
        Assert.AreEqual((byte)0xa5, Marshal.ReadByte(target.Player,725)); Assert.AreEqual((byte)0xa5, Marshal.ReadByte(target.Player,1181));
    }
    [TestMethod]
    public void ControllerRuntimeRejectsInvalidTreeIndexAndIncompatibleTargetParameterLayoutBeforeWriting()
    {
        var type = Load(); using var source = new RuntimeImage(); using var target = new RuntimeImage();
        var saved = Call(type, "CaptureRuntime", source.Player);
        source.Int(1008, 1); Invalid(() => Call(type, "CaptureRuntime", source.Player));
        target.Int(644,1); Invalid(() => Call(type, "PrepareRuntime", saved, target.Player, Writes(type)));
        Assert.AreEqual(0, Marshal.ReadInt32(target.Player,704));
    }
    private sealed class AvatarImage : IDisposable
    {
        private readonly IntPtr block = Marshal.AllocHGlobal(2048);
        internal readonly object Graph, Set;
        internal Array Nodes
        {
            get {
                var list = (IList)Graph.GetType().GetField("Nodes",Flags)!.GetValue(Graph)!;
                var nodes = Array.CreateInstance(list.GetType().GetGenericArguments()[0],list.Count);
                list.CopyTo(nodes,0); return nodes;
            }
        }
        internal IntPtr At(int offset) => IntPtr.Add(block,offset);
        private void Ptr(int member,int target) => Marshal.WriteIntPtr(block,member,At(target));
        internal void Int(int field,int value) => Marshal.WriteInt32(block,field,value);
        internal AvatarImage(Type type, int instance)
        {
            Marshal.Copy(new byte[2048],0,block,2048);
            // Output pose and both workspace scratch references deliberately
            // share one native SkeletonPose across a dataset.
            Ptr(256+16,464); Ptr(336,464); Ptr(344,464); Ptr(256+32,512);
            Int(464,1); Marshal.WriteInt64(block,472,912-472);
            Ptr(256+8,992); Int(992+48,2); Marshal.WriteInt64(block,992+56,1080-(992+56)); Marshal.WriteByte(block,1081,1);
            Ptr(256,1104); Int(1104+64,1); Marshal.WriteInt64(block,1104+72,1224-(1104+72));
            // Actual ID-based Animator binding and pointer-based skeleton /
            // exposed Transform entries reference target-local stand-ins.
            Marshal.WriteInt64(block,1232,1); Ptr(1240,1328); Int(1248,instance); Marshal.WriteInt64(block,1256,1);
            Ptr(1264,1344); Ptr(1328,1408); Ptr(1344,1408); Int(1376+8,instance);
            var state = new byte[80];
            void Edge(int offset,int target) => BitConverter.GetBytes(At(target).ToInt64()).CopyTo(state,offset);
            Edge(8,0); Edge(16,256); Edge(24,64); Edge(32,336); Edge(40,1232);
            var reader = New(type,"MemoryReader");
            string Transform(IntPtr pointer) => pointer == IntPtr.Zero ? "" : pointer == At(1408) ? "transform" : throw new InvalidDataException();
            string Animator(int id) => id == 0 ? "" : id == instance ? "animator" : throw new InvalidDataException();
            Graph = Activator.CreateInstance(type.GetNestedType("AvatarGraph",Flags)!,Flags,null,new object[] {reader,(Func<IntPtr,string>)Transform,(Func<int,string>)Animator},null)!;
            Set = Graph.GetType().GetMethod("Set",Flags)!.Invoke(Graph,new object[] {state,0})!;
        }
        internal object Snapshot(Type type)
        {
            var data = New(type,"Data"); var row = Row(type);
            Rows(row,"Datasets",Set,Set); Rows(data,"Rows",row);
            var list = ((IEnumerable)Graph.GetType().GetField("Nodes",Flags)!.GetValue(Graph)!).Cast<object>().ToArray();
            Rows(data,"AvatarNodes",list); return data;
        }
        internal void Transfer(Type type, Array source)
        {
            var writes = Writes(type);
            IntPtr Resolve(string key,bool isAnimator) => isAnimator && key == "animator" ? At(1376) : key == "transform" ? At(1408) : throw new InvalidDataException();
            Graph.GetType().GetMethod("Prepare",Flags)!.Invoke(Graph,new object[] {source,writes,(Func<string,bool,IntPtr>)Resolve,new HashSet<int>()});
            foreach (var write in writes) write!.GetType().GetMethod("Apply",Flags)!.Invoke(write,null);
        }
        public void Dispose() => Marshal.FreeHGlobal(block);
    }
    [TestMethod]
    public void AvatarPoseAndMotionReadBackAcrossTargetLocalSkeletonAndAnimatorBindings()
    {
        var type = Load(); using var source = new AvatarImage(type,143); using var target = new AvatarImage(type,352);
        // Mutate the captured source field DTO as a portable source image.
        var nodes = source.Nodes;
        object Node(string kind) => nodes.Cast<object>().Single(n => n.GetType().GetField("Kind",Flags)!.GetValue(n)!.ToString() == kind);
        byte[][] Fields(string kind) { var n=Node(kind); return (byte[][])n.GetType().GetField("Fields",Flags)!.GetValue(n)!; }
        Fields("TrsPose")[0][20] = 0x51; Fields("Motion")[2][16] = 0x72; Fields("Memory")[0][148] = 0x89;
        target.Transfer(type,nodes);
        Assert.AreEqual((byte)0x51,Marshal.ReadByte(target.At(912),20)); Assert.AreEqual((byte)0x72,Marshal.ReadByte(target.At(512),80));
        Assert.AreEqual((byte)0x89,Marshal.ReadByte(target.At(64),148)); Assert.AreEqual(352,Marshal.ReadInt32(target.At(1232),16));
        Assert.AreEqual(target.At(1408),Marshal.ReadIntPtr(target.At(1344)));
        var data = source.Snapshot(type); Call(type,"Validate",data);
        var gaps = (string[])Call(type,"RequiredCoverageGaps",data);
        CollectionAssert.Contains(gaps,"avatar-integer-and-object-channel-remap");
    }
    [TestMethod]
    public void BinderUserDataRequiresExactTypedOwnerCallbackAndLiveReceiverMembershipBothWays()
    {
        var type = Load(); var owner = Marshal.AllocHGlobal(512);
        try {
            Marshal.Copy(new byte[512],0,owner,512); var binder = IntPtr.Add(owner,64);
            var transform = IntPtr.Add(owner,448); var target = IntPtr.Add(owner,464); var wrong = IntPtr.Add(owner,480);
            Marshal.WriteIntPtr(binder,32,IntPtr.Add(owner,192)); Marshal.WriteInt64(binder,48,1); Marshal.WriteInt64(binder,56,2);
            Marshal.WriteIntPtr(owner,192,transform);
            Marshal.WriteIntPtr(binder,64,IntPtr.Add(owner,224)); Marshal.WriteInt64(binder,80,1); Marshal.WriteInt64(binder,88,2);
            Marshal.WriteIntPtr(owner,224+48,target); Marshal.WriteIntPtr(owner,224+56,transform);
            byte[] Read(IntPtr ptr,int length) { var bytes = new byte[length]; if (length != 0) Marshal.Copy(ptr,bytes,0,length); return bytes; }
            Func<IntPtr,int,byte[]> read = Read;
            Func<IntPtr,string?> identify = ptr => ptr == owner ? "anim" : null;
            Func<string,IntPtr> resolve = key => key == "anim" ? owner : IntPtr.Zero;
            bool Bind(int callback,IntPtr receiver,IntPtr user) {
                object[] args = {callback,receiver,user,identify,read,""};
                var result = (bool)Call(type,"TryBindBinderUserData",args); Assert.AreEqual(result ? "anim" : "",args[5]); return result;
            }
            bool Resolve(int callback,IntPtr receiver,string key) {
                object[] args = {callback,receiver,key,resolve,read,IntPtr.Zero};
                var result = (bool)Call(type,"TryResolveBinderUserData",args); Assert.AreEqual(result ? binder : IntPtr.Zero,args[5]); return result;
            }
            Assert.IsTrue(Bind(0x11b760,transform,binder)); Assert.IsTrue(Resolve(0x11b760,transform,"anim"));
            Assert.IsTrue(Bind(0x11b6f0,target,binder)); Assert.IsTrue(Resolve(0x11b6f0,target,"anim"));
            Assert.IsFalse(Bind(0x11b6f0,transform,binder)); Assert.IsFalse(Resolve(0x11b760,target,"anim"));
            Assert.IsFalse(Bind(0x11b760,wrong,binder)); Assert.IsFalse(Resolve(0x11b760,wrong,"anim"));
            Assert.IsFalse(Bind(0x11b760,transform,owner)); Assert.IsFalse(Resolve(0x11b760,transform,"other"));
            Assert.IsFalse(Bind(0x11b761,transform,binder)); Assert.IsFalse(Resolve(0x11b761,transform,"anim"));
        } finally { Marshal.FreeHGlobal(owner); }
    }
    private sealed class DirectorImage : IDisposable
    {
        internal readonly IntPtr Manager = Marshal.AllocHGlobal(2272);
        internal readonly object View, Data;
        internal IntPtr At(int offset) => IntPtr.Add(Manager,offset);
        private void P(int slot,int value) => Marshal.WriteIntPtr(Manager,slot,At(value));
        private void Count(int header,int first,int n)
        { P(header,first); Marshal.WriteInt64(Manager,header+16,n); Marshal.WriteInt64(Manager,header+24,n*2L); }
        internal DirectorImage(Type type,int stamp,bool corruptOutput = false)
        {
            Marshal.Copy(new byte[2272],0,Manager,2272);
            const int graph = 1136, output = 1392, job = 1648, group = 1744, outputTable = 1808, graphNode = 1856, notification = 1936, stack = 2048, command = 2144;
            P(graph+16,graphNode); P(graphNode+40,graph); Marshal.WriteInt32(Manager,graphNode+32,8);
            P(graph+104,output+8); P(graph+112,output+8); P(output+8,graph+104); P(output+16,graph+104); P(output+40,corruptOutput ? output : graph);
            Count(144,job,1); Marshal.WriteInt32(Manager,job,stamp); P(job+72,graph); Marshal.WriteInt32(Manager,0,stamp+1); Marshal.WriteInt32(Manager,72+56,stamp+2);
            P(592,group); P(600,group+40); P(608,group+40); P(group,2208); Count(group+8,outputTable,1); P(outputTable,output);
            Count(840,notification,1); P(notification,graphNode); Marshal.WriteInt32(Manager,notification+8,9);
            P(1080,stack); P(stack,command); P(command+8,graphNode); Marshal.WriteInt32(Manager,command+16,8); Marshal.WriteInt64(Manager,command+24,2);
            try {
                var graphMap = new Dictionary<IntPtr,int> { [At(graph)] = 0 };
                var outputs = new Dictionary<IntPtr,int> { [At(output)] = 0 };
                View = Activator.CreateInstance(type.GetNestedType("QueueView",Flags)!,Flags,null,
                    new object[] {Manager,Manager,2272,graphMap,outputs,1,New(type,"MemoryReader")},null)!;
                Data = View.GetType().GetField("Data",Flags)!.GetValue(View)!;
            } catch { Marshal.FreeHGlobal(Manager); throw; }
        }
        public void Dispose() => Marshal.FreeHGlobal(Manager);
    }
    [TestMethod]
    public void DirectorStagesRemainBoundToTargetGraphAndOutputAndPreserveDspTimeBits()
    {
        var type = Load(); using var source = new DirectorImage(type,113); using var target = new DirectorImage(type,2);
        var writes = Writes(type);
        target.View.GetType().GetMethod("Prepare",Flags)!.Invoke(target.View,new object[] {source.Data,writes});
        foreach (var write in writes) write!.GetType().GetMethod("Apply",Flags)!.Invoke(write,null);
        Assert.AreEqual(114,Marshal.ReadInt32(target.Manager,0)); Assert.AreEqual(115,Marshal.ReadInt32(target.Manager,128));
        Assert.AreEqual(113,Marshal.ReadInt32(target.Manager,1648)); Assert.AreEqual(target.At(1136),Marshal.ReadIntPtr(target.Manager,1720));
        Assert.AreEqual(target.At(1392),Marshal.ReadIntPtr(target.Manager,1808));
        var domain = New(type,"Data"); var row = Row(type); Set(row,"Graph",0); Set(row,"Output",0); Rows(domain,"Rows",row);
        var manager = New(type,"Director"); Set(manager,"Present",true); Set(manager,"Queues",source.Data); Set(manager,"Playing",new[] {0}); Set(domain,"Registry",manager);
        CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",domain),"director-pending-atomic-command-stack");
    }
    [TestMethod]
    public void DirectorRejectsAnIntrusiveOutputClaimedByAnotherGraph()
    {
        var type = Load(); Invalid(() => { using var _ = new DirectorImage(type,1,true); });
    }
    [TestMethod]
    public void ASharedControllerValueArrayOwnsItsIntegerBankAndMustMatchTheAvatarValueView()
    {
        var type = Load(); var data = New(type,"Data"); var player = New(type,"Playable"); var runtime = New(type,"ControllerRuntime"); Set(runtime,"AnimationFlags",new byte[5]);
        var memory = New(type,"ControllerMemory"); var machine = New(type,"Machine"); Set(machine,"State",new byte[103]);
        Rows(memory,"Machines",machine); Set(memory,"Evaluation",new byte[5]); Set(memory,"LayerWeights",new byte[4]);
        var banks = new[] {16,16,16,4,4,1}.Select(n => new byte[n]).ToArray(); Set(memory,"ParameterBanks",banks);
        Set(runtime,"HasConstant",true); Set(runtime,"Layers",1); Set(runtime,"SynchronizedLayers",new[] {0}); Set(runtime,"StateCounts",new[] {1});
        var parameters = new byte[72]; var tags = new[] {6,7,8,1,3,4};
        for (int i = 0; i < tags.Length; i++) BitConverter.GetBytes(tags[i]).CopyTo(parameters,i * 12 + 4);
        Set(runtime,"Parameters",parameters); Set(player,"Runtime",runtime); Set(player,"Memory",memory); Set(player,"Clock",new byte[58]); Set(player,"SharedAvatarValues",0);
        var node = New(type,"AvatarNode"); var kind = node.GetType().GetField("Kind",Flags)!;
        kind.SetValue(node,Enum.Parse(kind.FieldType,"Values")); Set(node,"Extents",new[] {1,1,1,1,1,1});
        Set(node,"Fields",banks.Where((_,i) => i != 4).Select(b => (byte[])b.Clone()).ToArray());
        var row = Row(type); Set(row,"Primary",0);
        var inputs = (Array)row.GetType().GetField("Inputs",Flags)!.GetValue(row)!;
        Set(inputs.GetValue(0)!,"Edges",new[] {-1,-1,-1,-1,0});
        Rows(data,"AvatarNodes",node); Rows(data,"Rows",row); Rows(data,"Playables",player);
        var gaps = (string[])Call(type,"RequiredCoverageGaps",data); Assert.IsFalse(gaps.Contains("avatar-integer-and-object-channel-remap"));
        banks[3][0] = 0x40; Invalid(() => Call(type,"Validate",data)); banks[3][0] = 0;
        BitConverter.GetBytes(1).CopyTo(parameters,4*12+8); Invalid(() => Call(type,"Validate",data));
    }
    [TestMethod]
    public void ScenePropertyFloatCastInstanceIdsRequireUniqueSourceAndTargetOwners()
    {
        var type = Load(); var map = (Dictionary<int,int>)Call(type,"IndexFloatObjectIds",new[] {17,16777216,16777217,-16777216,-16777217});
        static int Bits(int id) => BitConverter.ToInt32(BitConverter.GetBytes((float)id));
        Assert.AreEqual(17,map[Bits(17)]); Assert.AreEqual(0,map[Bits(16777216)]); Assert.AreEqual(0,map[Bits(-16777217)]);
        var data=New(type,"Data"); var row=Row(type); Rows(data,"Rows",row);
        var handles=row.GetType().GetField("Binder",Flags)!.GetValue(row)!; var property=New(type,"SceneProperty");
        var generic=property.GetType().GetField("Binding",Flags)!.GetValue(property)!; Set(generic,"ObjectCurve",(byte)1);
        Rows(handles,"Properties",property);
        var props=((Array)handles.GetType().GetField("Storage",Flags)!.GetValue(handles)!).GetValue(2)!;
        Set(props,"Allocated",true); Set(props,"Borrowed",false); Set(props,"Capacity",1);
        Set(property,"FloatBits",Bits(17)); Invalid(() => Call(type,"Validate",data));
        Set(property,"FloatBits",0); Set(property,"ObjectValue","#unresolved-native-object");
        CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",data),"binder-unresolved-semantic-reference");
    }
    [TestMethod]
    public void BinderOwnedEmptyCapacityAndBorrowedStorageHaveDifferentAdmissionRequirements()
    {
        var type=Load(); var data=New(type,"Data"); var row=Row(type); Rows(data,"Rows",row);
        var handles=row.GetType().GetField("Binder",Flags)!.GetValue(row)!;
        var empty=((Array)handles.GetType().GetField("Storage",Flags)!.GetValue(handles)!).GetValue(0)!;
        Set(empty,"Allocated",true); Set(empty,"Capacity",5); Set(empty,"Borrowed",false);
        var gaps=(string[])Call(type,"RequiredCoverageGaps",data);
        CollectionAssert.Contains(gaps,"binder-empty-capacity-requires-common-canon"); Assert.IsFalse(gaps.Contains("binder-storage-owner-or-allocator"));
        Set(empty,"Borrowed",true);
        CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",data),"binder-storage-owner-or-allocator");
    }
    [TestMethod]
    public void EvaluationAcceptsOnlyExactNativeBackingReferencesAndRejectsOutOfRangeCachedCounts()
    {
        var type=Load(); var body=new byte[184]; var dependencies=new IntPtr[11];
        dependencies[0]=new IntPtr(4096); BitConverter.GetBytes(4096L).CopyTo(body,8); BitConverter.GetBytes(7L).CopyTo(body,16);
        var edges=(int[])Call(type,"CacheEdges",body,0,dependencies);
        Assert.AreEqual(0,edges[0]); Assert.AreEqual(-2,edges[1]); Assert.AreEqual(-1,edges[2]);
        var data=New(type,"Data"); var row=Row(type); Rows(data,"Rows",row);
        var eval=((Array)row.GetType().GetField("Evaluation",Flags)!.GetValue(row)!).GetValue(0)!;
        var cache=eval.GetType().GetField("Cache",Flags)!.GetValue(eval)!;
        Set(cache,"Edges",edges); var fields=(byte[][])cache.GetType().GetField("Fields",Flags)!.GetValue(cache)!; fields[1][0]=1;
        var gaps=(string[])Call(type,"RequiredCoverageGaps",data);
        CollectionAssert.Contains(gaps,"cached-generic-binding-count-outside-backing"); CollectionAssert.Contains(gaps,"stale-evaluation-cache-edge");
    }
    private static object MeasuredCold(Type type, out object row, out object retained)
    {
        var data=New(type,"Data");row=Row(type);Rows(data,"Rows",row);
        ((byte[][])row.GetType().GetField("Fields",Flags)!.GetValue(row)!)[6]=Enumerable.Repeat((byte)255,16).ToArray();
        retained=New(type,"Residual");Set(row,"Retained",retained);
        Rows(retained,"Arrays",Enumerable.Range(0,6).Select(_=>New(type,"Storage")).ToArray());
        Set(retained,"AllocatorRvas",Enumerable.Repeat(0x15d5b60,6).ToArray());Set(retained,"Labels",Enumerable.Repeat(30,6).ToArray());
        Set(retained,"PlaybackVtable",0x15d5b70);Set(retained,"SetLabels",new[]{30,30});Set(retained,"DestroyRvas",new int[2]);
        Rows(data,"Users",new[]{1264,1288,1328,1368}.Select(offset=>{
            var s=New(type,"UserSlot");Set(s,"Owner","owner");Set(s,"Offset",offset);Set(s,"DeclaringRtti",0x194b410);return s;
        }).ToArray());return data;
    }
    private static string[] ScalarList(Type type,string method,object value) => ((IEnumerable<string>)Call(type,method,value)).ToArray();
    [TestMethod]
    public void ColdEvaluationInputKeepsAllRawFlagsAndStreamSourceButCannotBecomeWritable()
    {
        var type=Load(); var offsets=new[]{0,24,40,41};
        for(int index=0;index<5;index++) {
            var data=MeasuredCold(type,out var row,out _); Call(type,"RequireCompleteCoverage",data);
            var input=((Array)row.GetType().GetField("Inputs",Flags)!.GetValue(row)!).GetValue(0)!;
            var flags=(byte[])input.GetType().GetField("Flags",Flags)!.GetValue(input)!;
            var versions=(byte[])input.GetType().GetField("Versions",Flags)!.GetValue(input)!;
            Buffer.BlockCopy(BitConverter.GetBytes(unchecked((int)0xf1234567)),0,versions,0,4);
            if(index<4) flags[index]=237; else Buffer.BlockCopy(BitConverter.GetBytes(2),0,versions,4,4);
            Call(type,"Validate",data); var gaps=(string[])Call(type,"RequiredCoverageGaps",data);
            Assert.IsTrue(gaps.Any(g=>g.Contains(index<4 ? "+"+offsets[index]+"=237" : "AnimationStreamSource:+84=2")));
            Array.Clear(gaps,0,gaps.Length); Invalid(()=>Call(type,"RequireCompleteCoverage",data)); Invalid(()=>Call(type,"RequireCanonicalizableCoverage",data));
            Assert.AreEqual(unchecked((int)0xf1234567),BitConverter.ToInt32(versions,0));
            if(index<4) Assert.AreEqual((byte)237,flags[index]);
        }
        var malformed=MeasuredCold(type,out var badRow,out _); var badInput=((Array)badRow.GetType().GetField("Inputs",Flags)!.GetValue(badRow)!).GetValue(0)!;
        Set(badInput,"Flags",new byte[3]); Invalid(()=>Call(type,"Validate",malformed));
    }
    [TestMethod]
    public void AnimatorAndDatasetNoncanonicalBytesAreObservationsWithUnclearableHardGaps()
    {
        var type=Load();
        foreach(var item in new (int Field,int Offset)[]{(0,0),(0,1),(1,0),(2,0),(2,1),(4,144),(4,145),(4,146),(4,147),(4,152),(4,153),(4,154),(4,155),(4,156),(7,12),(7,13),(3,0),(3,4),(7,0)}) {
            var data=MeasuredCold(type,out var row,out _); var fields=(byte[][])row.GetType().GetField("Fields",Flags)!.GetValue(row)!;
            fields[item.Field][item.Offset]=239; Call(type,"Validate",data);
            Assert.IsTrue(ScalarList(type,"ScalarGaps",data).Any()); Invalid(()=>Call(type,"RequireCanonicalizableCoverage",data));
            Assert.AreEqual((byte)239,fields[item.Field][item.Offset]);
        }
        foreach(var name in new[]{"OwnsAvatar","Initialized"}) {
            var data=MeasuredCold(type,out var row,out _); var set=((Array)row.GetType().GetField("Datasets",Flags)!.GetValue(row)!).GetValue(0)!;
            Set(set,name,(byte)231); Call(type,"Validate",data); Assert.AreEqual((byte)231,set.GetType().GetField(name,Flags)!.GetValue(set));
            Assert.IsTrue(ScalarList(type,"ScalarGaps",data).Any(g=>g.Contains("AvatarDataSet"))); Invalid(()=>Call(type,"RequireCompleteCoverage",data));
        }
    }
    [TestMethod]
    public void EveryCapturedAvatarBooleanBankRetainsMalformedBytesWithoutCallingThemPadding()
    {
        var type=Load();
        foreach(var shape in new (string Kind,int[] Sizes,int Field,int Offset)[]{("Input",new[]{21},0,16),("Workspace",new[]{51},0,48),
            ("Human",new[]{48,235},1,232),("Memory",new[]{166},0,164),("Memory",new[]{166},0,165),
            ("Values",new[]{0,0,0,0,1},4,0),("Mask",new[]{1,0,0,0,0},0,0)}) {
            var node=New(type,"AvatarNode"); Set(node,"Kind",Enum.Parse(type.GetNestedType("AvatarKind",Flags)!,shape.Kind));
            var fields=shape.Sizes.Select(n=>new byte[n]).ToArray(); fields[shape.Field][shape.Offset]=212; Set(node,"Fields",fields);
            Assert.IsTrue(ScalarList(type,"AvatarNodeScalarGaps",node).Any(g=>g.Contains("=212")));
            Assert.AreEqual((byte)212,fields[shape.Field][shape.Offset]); fields[shape.Field][shape.Offset]=1;
            Assert.AreEqual(0,ScalarList(type,"AvatarNodeScalarGaps",node).Length);
        }
    }
    [TestMethod]
    public void MalformedControllerMemoryScalarsAreCapturedButNeverReachPendingWrites()
    {
        var type=Load();
        foreach(var kind in new[]{0,1,2,3}) {
            using var source=new Memory(); using var target=new Memory();
            if(kind==0) Marshal.WriteInt32(source.Root,40,3);
            if(kind==1) Marshal.WriteByte(source.Root,44,229);
            if(kind==2) Marshal.WriteByte(source.Machine,112,229);
            if(kind==3) Marshal.WriteByte(source.Bools,1,229);
            var saved=Call(type,"CaptureMemory",source.Root); Assert.IsTrue(ScalarList(type,"MemoryScalarGaps",saved).Any());
            var writes=Writes(type); Invalid(()=>Call(type,"PrepareMemory",saved,target.Root,writes)); Assert.AreEqual(0,writes.Count);
            Assert.AreEqual(0,Marshal.ReadInt32(target.Root,40)); Assert.AreEqual((byte)0,Marshal.ReadByte(target.Machine,112));
        }
    }
    [TestMethod]
    public void ColdControllerWorkspaceFlagsAreNotInterpretedOrAppliedAsTypedBooleans()
    {
        var type=Load();
        foreach(var offset in new[]{724,856,864}) {
            using var source=new RuntimeImage(); using var target=new RuntimeImage(); Marshal.WriteByte(source.Player,offset,219);
            var saved=Call(type,"CaptureRuntime",source.Player); Assert.IsTrue(ScalarList(type,"RuntimeScalarGaps",saved).Any(g=>g.Contains("=219")));
            var writes=Writes(type); Invalid(()=>Call(type,"PrepareRuntime",saved,target.Player,writes)); Assert.AreEqual(0,writes.Count);
            Assert.AreEqual((byte)0,Marshal.ReadByte(target.Player,offset));
        }
    }
    [TestMethod]
    public void UnprovenStateMachineMessageCombinationsRemainRawAndCannotBeWritten()
    {
        var type=Load();
        foreach(var message in new[]{0,65,256,-1}) {
            using var source=new RuntimeImage(); using var target=new RuntimeImage(); source.Int(836,message);
            var saved=Call(type,"CaptureRuntime",source.Player);
            Assert.IsTrue(ScalarList(type,"RuntimeScalarGaps",saved).Any(g=>g.Contains("StateMachineMessage:+4=")));
            var writes=Writes(type); Invalid(()=>Call(type,"PrepareRuntime",saved,target.Player,writes));
            Assert.AreEqual(0,writes.Count); Assert.AreEqual(message,Marshal.ReadInt32(source.Player,836)); Assert.AreEqual(128,Marshal.ReadInt32(target.Player,836));
        }
    }
    [TestMethod]
    public void AnimationInitializationFlagsAreCapturedAndPreProcessDReadsBackThroughItsOwner()
    {
        var type=Load(); using var source=new RuntimeImage(); using var target=new RuntimeImage();
        Marshal.WriteByte(source.Player,257,1);
        var saved=Call(type,"CaptureRuntime",source.Player);
        CollectionAssert.AreEqual(new byte[]{0,1,0,0,0},(byte[])saved.GetType().GetField("AnimationFlags",Flags)!.GetValue(saved)!);
        var writes=Writes(type); Call(type,"PrepareRuntime",saved,target.Player,writes);
        foreach(var write in writes) write!.GetType().GetMethod("Apply",Flags)!.Invoke(write,null);
        Assert.AreEqual((byte)1,Marshal.ReadByte(target.Player,257));
        Assert.AreEqual(Call(type,"Json",saved),Call(type,"Json",Call(type,"CaptureRuntime",target.Player)));
        for(int offset=256;offset<=260;offset++) if(offset!=257) Assert.AreEqual((byte)0,Marshal.ReadByte(target.Player,offset));
    }
    [TestMethod]
    public void AnimationNoncanonicalAndPendingAllocationFlagsCannotBecomeScalarWrites()
    {
        var type=Load();
        foreach(var item in new (int Index,byte Value)[]{(0,2),(1,2),(2,2),(3,2),(4,2),(0,1),(2,1),(3,1)}) {
            using var source=new RuntimeImage(); using var target=new RuntimeImage(); Marshal.WriteByte(source.Player,256+item.Index,item.Value);
            var saved=Call(type,"CaptureRuntime",source.Player); Assert.IsTrue(ScalarList(type,"RuntimeScalarGaps",saved).Any(g=>g.Contains("+"+(256+item.Index)+"="+item.Value)));
            var writes=Writes(type); Invalid(()=>Call(type,"PrepareRuntime",saved,target.Player,writes)); Assert.AreEqual(0,writes.Count);
            Assert.AreEqual(item.Value,Marshal.ReadByte(source.Player,256+item.Index)); Assert.AreEqual((byte)0,Marshal.ReadByte(target.Player,256+item.Index));
        }
        using var allocatedSource=new RuntimeImage(); using var emptyTarget=new RuntimeImage(); Marshal.WriteByte(allocatedSource.Player,260,1);
        var allocated=Call(type,"CaptureRuntime",allocatedSource.Player); var denied=Writes(type);
        Invalid(()=>Call(type,"PrepareRuntime",allocated,emptyTarget.Player,denied)); Assert.AreEqual(0,denied.Count);
    }
    [TestMethod]
    public void OnlyProvenAnimationPlayableTypesHaveTheFiveByteTail()
    {
        var type=Load();
        foreach(var vtable in new[]{0x15d91f8,0x15d89d8,0x15d9380,0x15d8f08}) Assert.AreEqual(5,Call(type,"AnimationFlagBytes",vtable));
        foreach(var vtable in new[]{0x16a23b8,0,0x1234567}) Assert.AreEqual(0,Call(type,"AnimationFlagBytes",vtable));
        var node=New(type,"Node"); Set(node,"Vtable",0x16a23b8); Set(node,"Modes",new byte[12]); Set(node,"Clock",new byte[58]);
        var graph=New(type,"Topology"); Set(graph,"Timing",new byte[28]); Rows(graph,"Arrays",New(type,"Storage"),New(type,"Storage")); Rows(graph,"Nodes",node);
        Call(type,"ValidateTopology",graph); Set(node,"AnimationFlags",new byte[5]); Invalid(()=>Call(type,"ValidateTopology",graph));
        Set(node,"Vtable",0x15d89d8); Call(type,"ValidateTopology",graph);
        Assert.IsTrue(ScalarList(type,"GraphGaps",graph).Any(g=>g.StartsWith("native-playable-subtype:",StringComparison.Ordinal)));
    }
    [TestMethod]
    public void TemporaryStateMachineFirstEvalHasExactlyOnePersistentControllerMemoryOwner()
    {
        var type=Load(); using var source=new Memory(); using var target=new Memory(); Marshal.WriteInt32(source.Root,40,2);
        var saved=Call(type,"CaptureMemory",source.Root); CollectionAssert.AreEqual(BitConverter.GetBytes(2),(byte[])Call(type,"StateMachineInputFirstEval",saved));
        var writes=Writes(type); Call(type,"PrepareMemory",saved,target.Root,writes);
        foreach(var write in writes) write!.GetType().GetMethod("Apply",Flags)!.Invoke(write,null);
        CollectionAssert.AreEqual(BitConverter.GetBytes(2),(byte[])Call(type,"StateMachineInputFirstEval",Call(type,"CaptureMemory",target.Root)));
        Assert.AreEqual((byte)0,Marshal.ReadByte(target.Root,44));
        Marshal.WriteInt32(source.Root,40,3); var unknown=Call(type,"CaptureMemory",source.Root);
        CollectionAssert.AreEqual(BitConverter.GetBytes(3),(byte[])Call(type,"StateMachineInputFirstEval",unknown));
        Assert.IsTrue(ScalarList(type,"MemoryScalarGaps",unknown).Any(g=>g.Contains("StateMachineInput.FirstEval")));
        var denied=Writes(type); Invalid(()=>Call(type,"PrepareMemory",unknown,target.Root,denied)); Assert.AreEqual(0,denied.Count);
    }
    [TestMethod]
    public void MissingInitializationCaptureAndColdSamplingFirstEvalCannotClaimCoverage()
    {
        var type=Load(); Invalid(()=>Call(type,"ValidateRuntime",New(type,"ControllerRuntime")));
        var data=MeasuredCold(type,out _,out var retained); using var native=new Memory();
        Marshal.WriteInt32(native.Root,40,3); var memory=Call(type,"CaptureMemory",native.Root);
        Rows(data,"ExtraMemories",memory); Set(retained,"SamplingMemory",-1); Call(type,"Validate",data);
        var gaps=(string[])Call(type,"RequiredCoverageGaps",data); Assert.IsTrue(gaps.Any(g=>g.Contains("StateMachineInput.FirstEval")));
        Array.Clear(gaps,0,gaps.Length); Invalid(()=>Call(type,"RequireCompleteCoverage",data)); Invalid(()=>Call(type,"RequireCanonicalizableCoverage",data));
    }
    [TestMethod]
    public void UnprovenGenericCurveFlagsPreserveRawUnionBitsAndGraphRecursionCannotHideBehindADiagnosticBoolean()
    {
        var type=Load(); var handles=New(type,"Handles"); var property=New(type,"SceneProperty");
        var binding=property.GetType().GetField("Binding",Flags)!.GetValue(property)!; Set(binding,"ObjectCurve",(byte)218); Set(binding,"IntCurve",(byte)217);
        Set(property,"FloatBits",unchecked((int)0xdeadbeef)); Rows(handles,"Properties",property);
        var storage=((Array)handles.GetType().GetField("Storage",Flags)!.GetValue(handles)!).GetValue(2)!;
        Set(storage,"Capacity",1); Set(storage,"Allocated",true); Call(type,"ValidateHandles",handles);
        var gaps=ScalarList(type,"HandleScalarGaps",handles); Assert.AreEqual(2,gaps.Length);
        Assert.AreEqual(unchecked((int)0xdeadbeef),property.GetType().GetField("FloatBits",Flags)!.GetValue(property));
        var graph=New(type,"Topology"); Set(graph,"RecursingRaw",(byte)1); Set(graph,"Recursing",false);
        CollectionAssert.Contains(ScalarList(type,"GraphScalarGaps",graph),"native-playable-graph-is-recursing");
        Set(graph,"RecursingRaw",(byte)216); Assert.IsTrue(ScalarList(type,"GraphScalarGaps",graph).Any(g=>g.Contains("+184=216")));
    }
    [TestMethod]
    public void MeasuredEmptyRecorderUserSlotsAndJobFencesCanPassWithoutIgnoringAnActualProducer()
    {
        var type=Load();var data=MeasuredCold(type,out var row,out var retained);Call(type,"RequireCompleteCoverage",data);
        var fence=retained.GetType().GetField("Pose",Flags)!.GetValue(retained)!;
        Set(fence,"Group",0);Set(fence,"Version",9U);Set(fence,"Tag",7UL);
        CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",data),"outstanding-animator-job-producer");
        Set(fence,"Tag",19UL);CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",data),"completed-animator-job-group-binding");
        Set(fence,"Group",-1);Set(fence,"Tag",0UL);
        ((byte[][])row.GetType().GetField("Fields",Flags)!.GetValue(row)!)[6]=new byte[16];
        CollectionAssert.Contains((string[])Call(type,"RequiredCoverageGaps",data),"recorder-cursor-or-active-frame-memory");
    }
    [TestMethod]
    public void ReciprocalUserListNodeAndVectorMustAgreeWithEveryActualPeerIndex()
    {
        var type=Load();var data=MeasuredCold(type,out _,out _);
        var slots=(Array)data.GetType().GetField("Users",Flags)!.GetValue(data)!;var node=slots.GetValue(0)!;var list=slots.GetValue(1)!;
        var a=New(type,"UserEntry");Set(a,"Peer","owner/native-user-list/1288");Set(a,"Reverse",0);Rows(node,"Entries",a);
        var b=New(type,"UserEntry");Set(b,"Peer","owner/native-user-list/1264");Set(b,"Reverse",-1);Rows(list,"Entries",b);
        var storage=list.GetType().GetField("Storage",Flags)!.GetValue(list)!;Set(storage,"Allocated",true);Set(storage,"Borrowed",false);Set(storage,"Capacity",1);
        Call(type,"RequireCompleteCoverage",data);Set(a,"Reverse",1);Invalid(()=>Call(type,"Validate",data));
    }
    [TestMethod]
    public void DirectedPlayablePortsRequireAReciprocalOutputPortAndPreserveWeightBits()
    {
        var type=Load();var topology=New(type,"Topology");Set(topology,"Timing",new byte[28]);Rows(topology,"Arrays",New(type,"Storage"),New(type,"Storage"));
        object Node(out object ports) {var n=New(type,"Node");Set(n,"Modes",new byte[12]);Set(n,"Clock",new byte[58]);ports=New(type,"Connections");Set(n,"Ports",ports);return n;}
        var source=Node(out var a);var dest=Node(out var b);Rows(topology,"Nodes",source,dest);
        var input=New(type,"InPort");Set(input,"From",0);Set(input,"Port",0);Set(input,"WeightBits",unchecked((int)0x80000000));
        Rows(b,"Inputs",input);Set(a,"Outputs",new[]{1});
        foreach(var item in new[]{(a,"OutputStorage"),(b,"InputStorage")}) {
            var s=item.Item1.GetType().GetField(item.Item2,Flags)!.GetValue(item.Item1)!;Set(s,"Allocated",true);Set(s,"Capacity",1);Set(s,"Borrowed",false);
        }
        Call(type,"ValidateTopology",topology);Set(input,"Port",1);Invalid(()=>Call(type,"ValidateTopology",topology));
        Set(input,"Port",0);Set(a,"Outputs",new[]{-1});Invalid(()=>Call(type,"ValidateTopology",topology));
    }
    private sealed class PortImage : IDisposable
    {
        internal readonly IntPtr Graph=Marshal.AllocHGlobal(1280);
        internal IntPtr At(int n)=>IntPtr.Add(Graph,n);
        private void P(int member,int dest)=>Marshal.WriteIntPtr(Graph,member,At(dest));
        internal PortImage(bool forward)
        {
            Marshal.Copy(new byte[1280],0,Graph,1280);
            P(56,472);P(64,264);Marshal.WriteInt32(Graph,44,2);Marshal.WriteInt64(Graph,160,-1);
            foreach(int h in new[]{72,120}) Marshal.WriteInt64(Graph,h+24,1);
            for(int i=0;i<2;i++) {
                int p=i==0?256:464,h=680+48*i,c=800+112*i,input=1040+16*i,output=1088+8*i;
                P(p,1216);P(p+8,i==0?56:264);P(p+16,i==0?472:56);P(p+32,0);P(p+40,h);P(h+40,p);
                Marshal.WriteInt32(Graph,h+32,2+2*i);P(p+48,c);Marshal.WriteInt64(Graph,p+144,-1);
                foreach(var a in new[]{(c+32,input),(c+64,output)}) {
                    P(a.Item1,a.Item2);Marshal.WriteInt64(Graph,a.Item1+16,1);Marshal.WriteInt64(Graph,a.Item1+24,2);
                }
                if ((i==1)==forward) {P(input,i==1?256:464);Marshal.WriteInt32(Graph,input+8,unchecked((int)0x80000000));}
                else P(output,i==0?464:256);
            }
        }
        internal object View(Type type)=>Activator.CreateInstance(type.GetNestedType("GraphView",Flags)!,Flags,null,new object[] {0,Graph,New(type,"MemoryReader"),
            (Func<IntPtr,int>)(_=>0x16a23b8),new Dictionary<IntPtr,int>()},null)!;
        public void Dispose()=>Marshal.FreeHGlobal(Graph);
    }
    [TestMethod]
    public void ConnectionPlanRestoresBothSidesAtNewNativeAddressesThenReadsThemAgain()
    {
        var type=Load();using var source=new PortImage(true);using var target=new PortImage(false);
        object Data(object view)=>view.GetType().GetField("Data",Flags)!.GetValue(view)!;
        var saved=Data(source.View(type));var plan=target.View(type);Call(type,"ValidateTopology",saved);
        var writes=Writes(type);plan.GetType().GetMethod("Prepare",Flags)!.Invoke(plan,new object[]{saved,writes});
        foreach(var write in writes) write!.GetType().GetMethod("Apply",Flags)!.Invoke(write,null);
        Assert.AreEqual(target.At(464),Marshal.ReadIntPtr(target.Graph,1088));Assert.AreEqual(target.At(256),Marshal.ReadIntPtr(target.Graph,1056));
        Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(target.Graph,1096));Assert.AreEqual(IntPtr.Zero,Marshal.ReadIntPtr(target.Graph,1040));
        Assert.AreEqual(unchecked((int)0x80000000),Marshal.ReadInt32(target.Graph,1064));
        var fresh=Data(target.View(type));Call(type,"ValidateTopology",fresh);
        Assert.AreEqual((string)Call(type,"Json",saved),(string)Call(type,"Json",fresh));
    }
    [TestMethod]
    public void DirectorPoolWitnessChecksStackGenerationFreeCountAndInFlightOwnership()
    {
        var type=Load();var manager=Marshal.AllocHGlobal(2304);
        try {
            Marshal.Copy(new byte[2304],0,manager,2304);
            void P(int member,int dest)=>Marshal.WriteIntPtr(manager,member,IntPtr.Add(manager,dest));
            P(760,1200);P(1200,1200);P(1208,1200);P(1216,1200);Marshal.WriteByte(manager,1224,1);Marshal.WriteByte(manager,1225,1);
            P(1040,1408);P(1408,1536);Marshal.WriteInt64(manager,1416,7);Marshal.WriteInt32(manager,1000,2);Marshal.WriteInt32(manager,1020,1);
            P(1088,1664);P(1664,1792);Marshal.WriteInt64(manager,1672,8);Marshal.WriteInt32(manager,1824,42);
            var p=Call(type,"CaptureProducers",manager,New(type,"MemoryReader"));Call(type,"ValidateProducers",p);
            var buckets=(Array)p.GetType().GetField("Buckets",Flags)!.GetValue(p)!;var b=buckets.GetValue(0)!;
            Assert.AreEqual(7UL,b.GetType().GetField("Tag",Flags)!.GetValue(b));Set(b,"InFlight",0);Invalid(()=>Call(type,"ValidateProducers",p));
            Set(b,"InFlight",1);var data=MeasuredCold(type,out _,out _);var registry=data.GetType().GetField("Registry",Flags)!.GetValue(data)!;
            Set(registry,"Present",true);Set(registry,"Producers",p);
            Set(registry.GetType().GetField("Queues",Flags)!.GetValue(registry)!,"OutputCounts",new[]{-1});
            var gaps=(string[])Call(type,"RequiredCoverageGaps",data);
            CollectionAssert.Contains(gaps,"director-inflight-connection-outside-owned-graphs");CollectionAssert.Contains(gaps,"director-atomic-producer-admission");
        } finally {Marshal.FreeHGlobal(manager);}
    }
}
