using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;
using InControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputLeaseBindingsTests
{
    private const string SetKey = "R6/InputActionSet/0:test";
    private static T Empty<T>() where T : class
    {
#pragma warning disable SYSLIB0050
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
    }
    private static void Set(object owner, Type declaring, string field, object? value) => declaring.GetField(field,
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.SetValue(owner, value);
    private sealed class Fixture
    {
        internal readonly TestSet SetOwner = Empty<TestSet>();
        internal readonly PlayerAction Action = Empty<PlayerAction>();
        internal readonly TestSource Injected = Empty<TestSource>(), Original = Empty<TestSource>();
        internal readonly Dictionary<string, object> Roots;
        internal Fixture()
        {
            Set(Action, typeof(PlayerAction), "<Owner>k__BackingField", SetOwner);
            Set(Action, typeof(PlayerAction), "<Name>k__BackingField", "Left");
            Set(Injected, typeof(BindingSource), "<BoundTo>k__BackingField", Action);
            Injected.channel = GameInputChannel.Hero; Injected.index = 0; Injected.value = 0.75f;
            var regular = new List<BindingSource> { Injected }; var visible = new List<BindingSource> { Injected };
            Set(Action, typeof(PlayerAction), "regularBindings", regular);
            Set(Action, typeof(PlayerAction), "visibleBindings", visible);
            Set(Action, typeof(PlayerAction), "unfilteredBindings", new ReadOnlyCollection<BindingSource>(regular));
            Set(Action, typeof(PlayerAction), "bindings", new ReadOnlyCollection<BindingSource>(visible));
            Roots = new() { [SetKey] = SetOwner, [SetKey + "/action"] = Action,
                ["R6/InputTAS-source/" + SetKey + "/0"] = Injected,
                ["R6/InputOriginal-source/" + SetKey + "/0/0"] = Original };
        }
        internal int Validate(CanonicalCarryGraphCodec.PreparedView view) => CanonicalInputLeaseBindings.Validate(view,
            SetKey, SetOwner, GameInputChannel.Hero, new[] { Action }, new BindingSource[] { Injected },
            new[] { new BindingSource[] { Original } });
    }
    private static CanonicalCarryGraphCodec Codec()
    {
        var types = new HashSet<Type> { typeof(TestSet), typeof(TestSource), typeof(PlayerAction), typeof(List<BindingSource>),
            typeof(BindingSource[]), typeof(ReadOnlyCollection<BindingSource>) };
        var queue = new Queue<Type>(types);
        while (queue.Count > 0)
            foreach (var field in CanonicalCarryGraphCodec.Fields(queue.Dequeue()))
                if (field.FieldType.IsValueType && !field.FieldType.IsPrimitive && types.Add(field.FieldType)) queue.Enqueue(field.FieldType);
        return new CanonicalCarryGraphCodec(types, "lease-test-v1");
    }
    [TestMethod]
    public void Distinct_process_objects_retain_sdk_and_tas_source_alias_without_getters_or_equals()
    {
        var source = new Fixture(); var target = new Fixture(); target.Injected.value = -0.5f;
        var codec = Codec(); var graph = codec.Capture(source.Roots, _ => null);
        var validated = 0;
        var apply = codec.PrepareApply(graph, target.Roots, _ => throw new InvalidDataException(), view => {
            validated = target.Validate(view); Assert.AreEqual(-0.5f, target.Injected.value);
        });
        Assert.AreEqual(1, validated); apply(); Assert.AreEqual(0.75f, target.Injected.value);
        var list = (List<BindingSource>)typeof(PlayerAction).GetField("regularBindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target.Action)!;
        Assert.AreSame(target.Injected, list[0]);
    }
    [TestMethod]
    [DataRow("channel")]
    [DataRow("index")]
    [DataRow("nan")]
    [DataRow("owner")]
    [DataRow("clone")]
    public void Invalid_source_ownership_or_slot_is_rejected_before_target_fields_change(string fault)
    {
        var source = new Fixture(); var target = new Fixture(); target.Injected.value = -0.5f;
        if (fault == "channel") source.Injected.channel = GameInputChannel.CustomKey;
        if (fault == "index") source.Injected.index = 1;
        if (fault == "nan") source.Injected.value = float.NaN;
        if (fault == "owner") Set(source.Injected, typeof(BindingSource), "<BoundTo>k__BackingField", null);
        if (fault == "clone") Set(source.Action, typeof(PlayerAction), "regularBindings", new List<BindingSource> { Empty<TestSource>() });
        var codec = Codec(); var graph = codec.Capture(source.Roots, _ => null);
        Assert.ThrowsExactly<InvalidDataException>(() => codec.PrepareApply(graph, target.Roots,
            _ => throw new InvalidDataException(), view => target.Validate(view)));
        Assert.AreEqual(-0.5f, target.Injected.value);
    }
    private sealed class TestSet : PlayerActionSet { private TestSet() => throw new InvalidOperationException("constructor"); }
    private sealed class TestSource : BindingSource
    {
        public GameInputChannel channel; public int index; public float value;
        public override float GetValue(InputDevice device) => throw new InvalidOperationException("callback");
        public override bool GetState(InputDevice device) => throw new InvalidOperationException("callback");
        public override bool Equals(BindingSource other) => throw new InvalidOperationException("game equality");
        public override int GetHashCode() => throw new InvalidOperationException("game hash");
        public override string Name => throw new InvalidOperationException("getter");
        public override string DeviceName => throw new InvalidOperationException("getter");
        public override InputDeviceClass DeviceClass => throw new InvalidOperationException("getter");
        public override InputDeviceStyle DeviceStyle => throw new InvalidOperationException("getter");
        public override BindingSourceType BindingSourceType => throw new InvalidOperationException("getter");
        public override void Save(BinaryWriter writer) => throw new InvalidOperationException("callback");
        public override void Load(BinaryReader reader, ushort version) => throw new InvalidOperationException("callback");
    }
}
