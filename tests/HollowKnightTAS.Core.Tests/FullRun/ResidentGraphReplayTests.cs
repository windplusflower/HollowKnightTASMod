using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentGraphReplayTests
{
    private sealed class State
    {
        public Child? Child, Alias;
        public object? Box, BoxAlias;
        public Action? Callback;
        public Value[]? Values;
    }
    private sealed class Child
    {
        public int Number;
        public readonly int Readonly;
        public int[] Numbers = Array.Empty<int>();
        public State? Owner;
        public Action? Callback;
        public Child(int number) { Number = number; Readonly = number + 1; }
        public void Run() => Number++;
    }
    private struct Value { public Child? Child; public int Count; }
    private sealed class NativeOwner : UnityEngine.Object { public int Number; }
    private static readonly FieldInfo Pointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static Dictionary<string, object> Roots(object owner) => new(StringComparer.Ordinal) { ["root"] = owner };
    private static object NoExternal(string binding) => throw new InvalidDataException(binding);
    private static CanonicalPreparedWritePlan Prepare(object source, object target, ResidentGraphPolicy sourcePolicy, ResidentGraphPolicy targetPolicy,
        out Dictionary<string, object> targetRoots)
    {
        var sourceRoots = Roots(source); var catalog = ResidentManagedTypeCatalog.Capture(sourceRoots, sourcePolicy, _ => null);
        var codec = new CanonicalCarryGraphCodec(catalog.Types, catalog.SchemaIdentity, residentPolicy: sourcePolicy);
        var graph = codec.Decode(codec.Encode(codec.Capture(sourceRoots, _ => null))); Assert.AreEqual(0, graph.Unsupported.Length);
        var restoredCatalog = ResidentManagedTypeCatalog.ResolveManifest(catalog.Manifest, targetPolicy); targetRoots = Roots(target);
        return new CanonicalCarryGraphCodec(restoredCatalog.Types, restoredCatalog.SchemaIdentity, residentPolicy: targetPolicy).PrepareOwnedGraph(graph, targetRoots, NoExternal);
    }
    private static (CanonicalPreparedWritePlan plan, State target, Dictionary<string, object> roots) PrepareState()
    {
        var source = new State(); var child = new Child(17) { Owner = source, Numbers = new[] { 3, 5, 7 } }; Action callback = child.Run;
        source.Child = child; source.Alias = child; source.Callback = callback; child.Callback = callback;
        source.Box = 41; source.BoxAlias = source.Box; source.Values = new[] { new Value { Child = child, Count = 73 } };
        var target = new State(); var policy = new ResidentGraphPolicy(_ => null); var plan = Prepare(source, target, policy, policy, out var roots);
        plan.Apply(); return (plan, target, roots);
    }

    [TestMethod]
    public void Replay_restores_all_detached_and_bound_fields_arrays_and_boxes_without_reallocating_owners()
    {
        var (plan, target, _) = PrepareState(); var child = target.Child!; var numbers = child.Numbers; var values = target.Values!;
        var box = target.Box!; var callback = target.Callback!; var admissions = 0;
        plan.ReapplyOriginalGraph(() => admissions++, () => {
            child.Number = 999; numbers[1] = 999; child.Owner = null; child.Callback = null;
            typeof(Child).GetField(nameof(Child.Readonly))!.SetValue(child, 999);
            Unsafe.Unbox<int>(box) = 999;
            values[0] = new Value { Child = new Child(999), Count = 999 };
            target.Child = new Child(999); target.Alias = null; target.Box = null; target.BoxAlias = null; target.Callback = null;
            target.Values = Array.Empty<Value>();
        });
        Assert.AreEqual(3, admissions); Assert.AreSame(child, target.Child); Assert.AreSame(child, target.Alias); Assert.AreSame(target, child.Owner);
        Assert.AreSame(numbers, child.Numbers); CollectionAssert.AreEqual(new[] { 3, 5, 7 }, numbers); Assert.AreEqual(17, child.Number); Assert.AreEqual(18, child.Readonly);
        Assert.AreSame(box, target.Box); Assert.AreSame(box, target.BoxAlias); Assert.AreEqual(41, (int)box);
        Assert.AreSame(callback, target.Callback); Assert.AreSame(callback, child.Callback); Assert.AreSame(child, callback.Target);
        Assert.AreSame(values, target.Values); Assert.AreSame(child, values[0].Child); Assert.AreEqual(73, values[0].Count); plan.VerifyReadback();
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.ReapplyOriginalGraph(() => { }, () => { }));
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Native_pointer_and_root_identity_changes_abort_before_any_replay_write()
    {
        var source = (NativeOwner)RuntimeHelpers.GetUninitializedObject(typeof(NativeOwner)); var target = (NativeOwner)RuntimeHelpers.GetUninitializedObject(typeof(NativeOwner));
        Pointer.SetValue(source, new IntPtr(1111)); Pointer.SetValue(target, new IntPtr(2222)); source.Number = 17;
        var sourcePolicy = new ResidentGraphPolicy(owner => ReferenceEquals(owner, source) ? "native/owner" : null);
        var targetPolicy = new ResidentGraphPolicy(owner => ReferenceEquals(owner, target) ? "native/owner" : null);
        var plan = Prepare(source, target, sourcePolicy, targetPolicy, out _); plan.Apply();
        Assert.ThrowsExactly<InvalidOperationException>(() => plan.ReapplyOriginalGraph(() => { }, () => { target.Number = 999; Pointer.SetValue(target, new IntPtr(3333)); }));
        Assert.AreEqual(999, target.Number); Pointer.SetValue(target, new IntPtr(2222)); Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
        var second = PrepareState(); var original = second.target.Child!;
        Assert.ThrowsExactly<InvalidOperationException>(() => second.plan.ReapplyOriginalGraph(() => { }, () => { original.Number = 999; second.roots["root"] = new State(); }));
        Assert.AreEqual(999, original.Number); Assert.ThrowsExactly<InvalidOperationException>(second.plan.VerifyReadback);
    }

    [TestMethod]
    public void Delegate_target_identity_is_guarded_even_when_its_owner_fields_are_mutable()
    {
        var (plan, target, _) = PrepareState(); var callback = target.Callback!;
        var targetField = typeof(Delegate).GetField("_target", BindingFlags.Instance | BindingFlags.NonPublic); Assert.IsNotNull(targetField);
        Assert.ThrowsExactly<InvalidDataException>(() => plan.ReapplyOriginalGraph(() => { }, () => targetField.SetValue(callback, new Child(123))));
        Assert.ThrowsExactly<InvalidOperationException>(plan.VerifyReadback);
    }

    [TestMethod]
    public void Native_callback_or_admission_failure_permanently_faults_the_same_plan()
    {
        var first = PrepareState(); var operations = 0;
        Assert.ThrowsExactly<IOException>(() => first.plan.ReapplyOriginalGraph(() => throw new IOException("admission"), () => operations++));
        Assert.AreEqual(0, operations); Assert.ThrowsExactly<InvalidOperationException>(first.plan.VerifyReadback);
        var second = PrepareState();
        Assert.ThrowsExactly<IOException>(() => second.plan.ReapplyOriginalGraph(() => { }, () => { second.target.Child!.Number = 999; throw new IOException("native"); }));
        Assert.AreEqual(999, second.target.Child!.Number);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.plan.ReapplyOriginalGraph(() => { }, () => { }));
        Assert.ThrowsExactly<InvalidOperationException>(second.plan.VerifyReadback);
    }

    [TestMethod]
    public void Swallowed_reentry_and_cross_thread_attempts_cannot_rearm_a_faulted_plan()
    {
        var first = PrepareState();
        Assert.ThrowsExactly<InvalidOperationException>(() => first.plan.ReapplyOriginalGraph(() => { }, () => {
            try { first.plan.ReapplyOriginalGraph(() => { }, () => { }); } catch (InvalidOperationException) { }
        }));
        Assert.ThrowsExactly<InvalidOperationException>(first.plan.VerifyReadback);
        var second = PrepareState();
        Task.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => second.plan.ReapplyOriginalGraph(() => { }, () => { }))).GetAwaiter().GetResult();
        Assert.ThrowsExactly<InvalidOperationException>(second.plan.VerifyReadback);
    }

    [TestMethod]
    public void Mutation_before_the_admitted_window_and_boxed_scalar_drift_are_rejected()
    {
        var first = PrepareState(); Unsafe.Unbox<int>(first.target.Box!) = 999; var calls = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() => first.plan.ReapplyOriginalGraph(() => { }, () => calls++));
        Assert.AreEqual(0, calls); Assert.ThrowsExactly<InvalidOperationException>(first.plan.VerifyReadback);
        var second = PrepareState(); Unsafe.Unbox<int>(second.target.Box!) = 999;
        Assert.ThrowsExactly<InvalidOperationException>(second.plan.VerifyReadback);
        Assert.ThrowsExactly<InvalidOperationException>(() => second.plan.ReapplyOriginalGraph(() => { }, () => { }));
    }

    [TestMethod]
    public void Delegate_binding_succeeds_across_name_presence_and_control_plane_token_drift()
    {
        var child = new Child(42);
        Action callback = child.Run;
        var actText = ResidentGraphPolicy.DescribeDelegate(callback);
        // Simulate legacy snapshot where Name is absent and Token was different in ControlPlane
        var methods = ResidentGraphPolicy.ResolveDelegateMethods(actText, typeof(Action), 1);
        var bound = ResidentGraphPolicy.BindDelegate(actText, typeof(Action), new object[] { child }, methods);
        Assert.IsNotNull(bound);
        Assert.IsTrue(ResidentGraphPolicy.EquivalentDelegateDescriptors(ResidentGraphPolicy.DescribeDelegate(bound), actText));
    }
}
