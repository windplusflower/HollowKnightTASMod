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
public sealed class ResidentAnimatorValueChannelTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static Type Load()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
        Assert.IsNotNull(repo);
        return Assembly.LoadFrom(Environment.GetEnvironmentVariable("HKTAS_TEST_RUNTIME_ASSEMBLY") ?? Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentAnimatorState", true)!;
    }
    private static object New(Type type, string nested) => Activator.CreateInstance(type.GetNestedType(nested, Flags)!, true)!;
    private static object Get(object value, string field) => value.GetType().GetField(field, Flags)!.GetValue(value)!;
    private static void Set(object value, string field, object? content) => value.GetType().GetField(field, Flags)!.SetValue(value, content);
    private static object Call(Type type, string method, params object[] args) => type.GetMethod(method, Flags)!.Invoke(null, args)!;
    private static object Member(object value, string method, params object[] args) => value.GetType().GetMethod(method, Flags)!.Invoke(value, args)!;
    private static void Invalid(Action action) => Assert.IsInstanceOfType<InvalidDataException>(Assert.ThrowsExactly<TargetInvocationException>(action).InnerException);
    private static byte[] Schema(int ordinary, int objects, uint id = 17)
    {
        var bytes = new byte[(ordinary + objects) * 12];
        for (int i = 0; i < ordinary + objects; i++)
        {
            BitConverter.GetBytes(id + (uint)i).CopyTo(bytes, i * 12);
            BitConverter.GetBytes(3u).CopyTo(bytes, i * 12 + 4);
            BitConverter.GetBytes((uint)i).CopyTo(bytes, i * 12 + 8);
        }
        return bytes;
    }
    private static void ArrayField(object value, string name, params object[] rows)
    {
        var field = value.GetType().GetField(name, Flags)!;
        var array = Array.CreateInstance(field.FieldType.GetElementType()!, rows.Length);
        for (int i = 0; i < rows.Length; i++) array.SetValue(rows[i], i);
        field.SetValue(value, array);
    }
    private static object Backing(Type type, int ordinary, int objects, uint schemaId = 17)
    {
        var b = New(type, "Backing");
        Set(b, "SetPresent", true);
        Set(b, "GenericPresent", true);
        Set(b, "ControllerPresent", true);
        Set(b, "SetDependency", New(type, "SetBindingDependency"));
        Set(b, "ParameterSchema", Schema(ordinary, objects, schemaId));
        var tables = (Array)Get(b, "Tables");
        var generic = type.GetNestedType("Generic", Flags)!;
        tables.SetValue(Array.CreateInstance(generic, 0), 0);
        for (int n = 1; n <= 2; n++)
        {
            var rows = Array.CreateInstance(generic, n == 1 ? ordinary : objects);
            for (int i = 0; i < rows.Length; i++) rows.SetValue(Activator.CreateInstance(generic, true), i);
            tables.SetValue(rows, n);
        }
        return b;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly object Data, Graph, Node, Backing;
        internal readonly IntPtr Header, Integers;
        private readonly Type type;
        internal Fixture(Type type, int plain, int id)
        {
            this.type = type;
            Header = Marshal.AllocHGlobal(96);
            Integers = Marshal.AllocHGlobal(8);
            Marshal.Copy(new byte[96], 0, Header, 96);
            Marshal.WriteInt32(Header, 64, 2);
            Marshal.WriteInt64(Header, 72, Integers.ToInt64() - Header.ToInt64() - 72);
            Marshal.WriteInt32(Integers, 0, plain);
            Marshal.WriteInt32(Integers, 4, id);
            Graph = Activator.CreateInstance(type.GetNestedType("AvatarGraph", Flags)!, Flags, null, new object[] { New(type, "MemoryReader"), new Func<IntPtr, string>(_ => throw new InvalidOperationException()), new Func<int, string>(_ => throw new InvalidOperationException()) }, null)!;
            var kind = Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "Values");
            Member(Graph, "Retained", Header, kind);
            Node = ((IList)Get(Graph, "Nodes"))[0]!;
            Data = New(type, "Data");
            ArrayField(Data, "AvatarNodes", Node);
            var row = New(type, "Row");
            Set(row, "Owner", "animator/test");
            Set(row, "Fields", new[] { 2, 1, 2, 12, 157, 4, 16, 14, 4 }.Select(n => new byte[n]).ToArray());
            Set(row, "Padding", new byte[7]);
            Backing = ResidentAnimatorValueChannelTests.Backing(type, 1, 1);
            Set(Backing, "DefaultValues", 0);
            var evaluations = (Array)Get(row, "Evaluation");
            Set(evaluations.GetValue(0)!, "Backing", Backing);
            ArrayField(Data, "Rows", row);
            Member(Graph, "RegisterValueChannels", Data, new Func<int, string>(value => value == 0 ? "" : value == id ? "asset/object" : "#unresolved-native-object"));
        }
        public void Dispose()
        {
            Marshal.FreeHGlobal(Header);
            Marshal.FreeHGlobal(Integers);
        }
    }
    [TestMethod]
    public void MixedIntegerObjectPayloadRestoresAcrossAddressesAndDifferentNativeIds()
    {
        var type = Load();
        using var source = new Fixture(type, unchecked((int)0xffeeddcc), 11);
        using var target = new Fixture(type, 7, 99);
        Assert.AreNotEqual(source.Header, target.Header);
        Call(type, "ValidateAvatar", source.Data);
        Call(type, "ValidateAvatar", target.Data);
        var writes = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;
        Member(target.Graph, "Prepare", Get(source.Data, "AvatarNodes"), writes, new Func<string, bool, IntPtr>((_, _) => throw new InvalidOperationException()), new HashSet<int>(), new Func<string, int>(key => key == "asset/object" ? 99 : throw new InvalidOperationException()));
        foreach (var write in writes) Member(write!, "Apply");
        Assert.AreEqual(unchecked((int)0xffeeddcc), Marshal.ReadInt32(target.Integers));
        Assert.AreEqual(99, Marshal.ReadInt32(target.Integers, 4));
        Assert.AreEqual(target.Integers.ToInt64() - target.Header.ToInt64() - 72, Marshal.ReadInt64(target.Header, 72));
    }
    [TestMethod]
    public void SharedDefaultsAndInputValuesKeepOneNodeAndConflictingSchemaIsRejected()
    {
        var type = Load();
        using var fixture = new Fixture(type, 1, 11);
        var row = ((Array)Get(fixture.Data, "Rows")).GetValue(0)!;
        var inputs = (Array)Get(row, "Inputs");
        Set(inputs.GetValue(0)!, "Edges", new[] { -1, -1, -1, 0, -1 });
        Call(type, "ValidateAvatar", fixture.Data);
        Assert.AreEqual(false, Get(fixture.Node, "ReadOnly"));
        var evaluations = (Array)Get(row, "Evaluation");
        var other = Backing(type, 2, 0);
        Set(other, "DefaultValues", 0);
        Set(evaluations.GetValue(1)!, "Backing", other);
        Invalid(() => Call(type, "ValidateAvatar", fixture.Data));
        Set(evaluations.GetValue(1)!, "Backing", Backing(type, 1, 1, 99));
        Set(Get(evaluations.GetValue(1)!, "Backing"), "DefaultValues", 0);
        Invalid(() => Call(type, "ValidateAvatar", fixture.Data));
    }
    [TestMethod]
    public void TypedPayloadRequiresOwnerCountsAndLegacyPayloadStillProducesCoverageGap()
    {
        var type = Load();
        using var fixture = new Fixture(type, 1, 11);
        var typed = Get(fixture.Node, "Integers");
        Set(typed, "Ordinary", new byte[0]);
        Invalid(() => Call(type, "ValidateAvatar", fixture.Data));
        Set(typed, "Ordinary", new byte[4]);
        Set(typed, "Objects", new[] { "#unresolved-native-object" });
        Assert.IsTrue(((IEnumerable<string>)Call(type, "ValueChannelGaps", fixture.Data)).Contains("avatar-integer-object-binding-unresolved"));
        Set(fixture.Node, "Integers", null);
        Call(type, "ValidateAvatar", fixture.Data);
        Assert.IsTrue(((string[])Call(type, "RequiredCoverageGaps", fixture.Data)).Contains("avatar-integer-and-object-channel-remap"));
    }
    [TestMethod]
    public void SchemaMustHaveDenseUniqueTypeThreeIndicesAndControllerOwnerCannotClaimObjects()
    {
        var type = Load();
        var schemaType = type.GetNestedType("ValueChannelSchema", Flags)!;
        void Create(byte[] bytes, int plain, int objects, bool parameters) => Activator.CreateInstance(schemaType, Flags, null, new object[] { bytes, plain, objects, parameters }, null);
        var duplicate = Schema(1, 1);
        BitConverter.GetBytes(0u).CopyTo(duplicate, 20);
        Invalid(() => Create(duplicate, 1, 1, false));
        Invalid(() => Create(Schema(1, 0), 1, 1, false));
        using var fixture = new Fixture(type, 1, 11);
        var player = New(type, "Playable");
        Set(player, "SharedAvatarValues", 0);
        var runtime = Get(player, "Runtime");
        Set(runtime, "Parameters", Schema(2, 0));
        ArrayField(fixture.Data, "Playables", player);
        Invalid(() => Call(type, "ValidateAvatar", fixture.Data));
    }
    [TestMethod]
    public void TypedOwnerEdgesRegisterOutputInputBackupPlayableStateAndResetWithoutGuessing()
    {
        var type = Load();
        using var fixture = new Fixture(type, 1, 11);
        var rows = (Array)Get(fixture.Data, "Rows");
        var row = rows.GetValue(0)!;
        var output = New(type, "AvatarNode");
        Set(output, "Kind", Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "Output"));
        Set(output, "Edges", new[] { 0, -1, -1, -1, -1, -1, -1, -1 });
        var state = New(type, "AvatarNode");
        Set(state, "Kind", Enum.Parse(type.GetNestedType("AvatarKind", Flags)!, "NodeState"));
        Set(state, "Edges", new[] { 0, -1, -1, -1, -1 });
        ArrayField(fixture.Data, "AvatarNodes", fixture.Node, output, state);
        Set(((Array)Get(row, "Datasets")).GetValue(0)!, "Edges", new[] { -1, -1, 1, -1, -1, -1 });
        Set(((Array)Get(row, "Inputs")).GetValue(0)!, "Edges", new[] { -1, -1, -1, 0, 0 });
        var retained = New(type, "Residual");
        Set(retained, "Backup", 0);
        Set(row, "Retained", retained);
        var player = New(type, "Playable");
        Set(player, "ResetValues", 0);
        ArrayField(fixture.Data, "Playables", player);
        var graph = New(type, "Topology");
        Set(graph, "Owner", 0);
        var node = New(type, "Node");
        Set(node, "Controller", 0);
        Set(node, "AnimationFlags", new byte[] { 0, 0, 0, 0, 1 });
        var pose = New(type, "PoseState");
        Set(pose, "State", 2);
        Set(node, "Pose", pose);
        ArrayField(graph, "Nodes", node);
        ArrayField(fixture.Data, "Graphs", graph);
        var assignments = (IDictionary)Call(type, "ValueChannelOwners", fixture.Data);
        Assert.AreEqual(1, assignments.Count);
        Assert.IsTrue(assignments.Contains(0));
        Set(node, "AnimationFlags", new byte[5]);
        Invalid(() => Call(type, "ValueChannelOwners", fixture.Data));
    }

    private static IList Writes(Type type)
        => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetNestedType("PendingWrite", Flags)!))!;

    private static void Prepare(Fixture source, Fixture target, IList writes, Func<string, int> resolve, HashSet<int>? controller = null)
    {
        Member(target.Graph, "Prepare", Get(source.Data, "AvatarNodes"), writes,
            new Func<string, bool, IntPtr>((_, _) => throw new InvalidOperationException("unexpected Transform reference")),
            controller ?? new HashSet<int>(), resolve);
    }

    [TestMethod]
    public void ObjectNullUsesZeroWithoutResolverAndInvalidObjectKeysCannotPrepare()
    {
        var type = Load();
        using var source = new Fixture(type, 1, 11);
        using var target = new Fixture(type, 2, 99);
        var channels = Get(source.Node, "Integers");
        Set(channels, "Objects", new[] { "" });
        var writes = Writes(type);
        Prepare(source, target, writes, _ => throw new InvalidOperationException("null key must not resolve"));
        foreach (var write in writes) Member(write!, "Apply");
        Assert.AreEqual(0, Marshal.ReadInt32(target.Integers, 4));

        Set(channels, "Objects", new string[] { null! });
        Invalid(() => Call(type, "ValidateAvatar", source.Data));
        Set(channels, "Objects", new[] { "#unresolved-native-object" });
        Invalid(() => Prepare(source, target, Writes(type), _ => 99));
        Set(channels, "Objects", new[] { "asset/object" });
        Invalid(() => Prepare(source, target, Writes(type), _ => 0));
    }

    [TestMethod]
    public void ControllerIntegerPayloadMustMatchUniqueWriterAndCreatesNoSecondWrites()
    {
        var type = Load();
        using var source = new Fixture(type, 1, 11);
        using var target = new Fixture(type, 2, 99);
        foreach (var fixture in new[] { source, target })
        {
            var channels = Get(fixture.Node, "Integers");
            Set(channels, "ControllerParameters", true);
            Set(channels, "Ordinary", new byte[8]);
            Set(channels, "Objects", Array.Empty<string>());
            var player = New(type, "Playable");
            Set(player, "SharedAvatarValues", 0);
            var memory = New(type, "ControllerMemory");
            Set(memory, "ParameterBanks", new[] { Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), new byte[8], Array.Empty<byte>() });
            Set(player, "Memory", memory);
            ArrayField(fixture.Data, "Playables", player);
            Call(type, "ControllerOwnedAvatarValues", fixture.Data);
        }
        var ownerSet = new HashSet<int> { 0 };
        var writes = Writes(type);
        Prepare(source, target, writes, _ => throw new InvalidOperationException("controller bank cannot resolve objects"), ownerSet);
        Assert.AreEqual(0, writes.Count);
        Set(Get(source.Node, "Integers"), "Ordinary", new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });
        Invalid(() => Call(type, "ControllerOwnedAvatarValues", source.Data));
    }

    [TestMethod]
    public void MissingTypedPayloadOnEitherSideAndLegacyCoverageCannotRestore()
    {
        var type = Load();
        using var source = new Fixture(type, 1, 11);
        using var target = new Fixture(type, 2, 99);
        var sourceChannels = Get(source.Node, "Integers");
        var targetChannels = Get(target.Node, "Integers");
        Set(source.Node, "Integers", null);
        Invalid(() => Prepare(source, target, Writes(type), _ => 99));
        Invalid(() => Call(type, "RequireCompleteCoverage", source.Data));
        Set(source.Node, "Integers", sourceChannels);
        Set(target.Node, "Integers", null);
        Invalid(() => Prepare(source, target, Writes(type), _ => 99));
        Set(source.Node, "Integers", null);
        Invalid(() => Prepare(source, target, Writes(type), _ => 99));
        Set(target.Node, "Integers", targetChannels);
    }

}
