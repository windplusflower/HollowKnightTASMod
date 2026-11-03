using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentSchedulerStateTests
{
    private struct WrapperLink { public object? Wrapper, Cycle; }
    private sealed class WrapperRoot { public WrapperLink Value; public WrapperLink[] Values = Array.Empty<WrapperLink>(); }

    [TestMethod]
    public void ManagedPrewalkerFindsReleasedCoroutinesThroughInlineValuesAndReferenceCycles()
    {
        var api = new Api(); var wrapper = (UnityEngine.Coroutine)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Coroutine)); GC.SuppressFinalize(wrapper);
        typeof(UnityEngine.Coroutine).GetField("m_Ptr", All)!.SetValue(wrapper, IntPtr.Zero);
        var root = new WrapperRoot(); root.Value = new WrapperLink { Wrapper = wrapper, Cycle = root }; root.Values = new[] { root.Value };
        var capture = api.Row("CaptureResult"); Set(capture, "graphRoots", new Dictionary<string, object> { ["root"] = root });
        Set(capture, "executionBinding", (Func<object, string?>)(_ => null));
        capture.GetType().GetMethod("FindWrappers", All)!.Invoke(capture, null);
        var wrappers = (IDictionary)Get(capture, "wrappers"); Assert.AreEqual(1, wrappers.Count); Assert.AreEqual(IntPtr.Zero, wrappers[wrapper]);
    }
    [TestMethod] public void ActualOwnerListsAndCallbackKindsNeedNoPathOrIteratorWhitelist()
    {
        var a = new Api(); var data = a.Data(); var routine = a.Routine("resident-scheduler/coroutine/000000", "arbitrary pool/component 19");
        var start = a.Callback("start", "arbitrary pool/component 19", 0.5); var coroutine = a.Callback("coroutine", "arbitrary pool/component 19", 1.5);
        var invoke = a.Callback("invoke", "arbitrary pool/component 19", 9.0); Set(coroutine, "Routine", Get(routine, "Key")); Set(invoke, "MethodName", "AnyLoadedMethod");
        Set(data, "Owners", a.Rows("Owner", a.Owner("arbitrary pool/component 19", (string)Get(routine, "Key")))); Set(data, "Routines", a.Rows("Routine", routine));
        a.Tree(data, start, coroutine, invoke); a.Validate(data);
    }
    [TestMethod] public void RedBlackParentBalanceOrderAndNextIteratorAreChecked()
    {
        var a = new Api(); var data = a.Data(); Set(data, "Owners", a.Rows("Owner", a.Owner("x")));
        var left = a.Callback("start", "x", 1); var root = a.Callback("start", "x", 2); var right = a.Callback("start", "x", 3); a.Tree(data, left, root, right); a.Validate(data);
        Set(left, "Parent", -1); a.Reject(data); Set(left, "Parent", 1); Set(right, "Color", 1); a.Reject(data); Set(right, "Color", 0);
        Set(data, "Next", 3); a.Reject(data); Set(data, "Next", 0); Set(right, "Time", BitConverter.DoubleToInt64Bits(0)); a.Reject(data);
    }
    [TestMethod] public void EveryNativeListEntryAndWaitRelationHasUniqueOwnership()
    {
        var a = new Api(); var data = a.Data(); var first = a.Routine("resident-scheduler/coroutine/000000", "x"); var second = a.Routine("resident-scheduler/coroutine/000001", "x");
        var owner = a.Owner("x", (string)Get(first, "Key"), (string)Get(second, "Key")); Set(data, "Owners", a.Rows("Owner", owner)); Set(data, "Routines", a.Rows("Routine", first, second));
        Set(first, "Waiting", Get(second, "Key")); Set(second, "Continue", Get(first, "Key")); a.Validate(data);
        Set(second, "Continue", ""); a.Reject(data); Set(second, "Continue", Get(first, "Key")); Set(owner, "Order", new[] { (string)Get(first, "Key"), (string)Get(first, "Key") }); a.Reject(data);
    }
    [TestMethod] public void CompletedNativeWrappersCannotPretendToHaveLiveExecution()
    {
        var a = new Api(); var data = a.Data(); var row = a.Row("Routine"); Set(row, "Key", "resident-scheduler/coroutine/000000"); Set(row, "Done", true); Set(row, "WrapperPresent", true);
        Set(data, "Routines", a.Rows("Routine", row)); a.Validate(data);
        Set(row, "EnumeratorPresent", true); Set(row, "EnumeratorWeakness", 2); a.Reject(data); Set(row, "EnumeratorPresent", false);
        Set(row, "WrapperAllocated", true); a.Reject(data); Set(row, "WrapperWeakness", 2); a.Validate(data);
        Set(row, "WrapperPresent", false); a.Reject(data);
    }
    [TestMethod] public void UnknownCallbackNativeDataRequiresItsExplicitCoverageGap()
    {
        var a = new Api(); var data = a.Data(); var row = a.Row("Callback"); Set(row, "Kind", "unsupported"); Set(row, "Call", 123); Set(row, "Color", 1);
        Set(data, "Callbacks", a.Rows("Callback", row)); Set(data, "TreeRoot", 0); a.Reject(data);
        Set(data, "CoverageGaps", new[] { "scheduler-callback-native-data-type:7b/0" }); a.Validate(data);
        Set(row, "Kind", "start"); a.Reject(data);
    }
    [TestMethod] public void ReleasedAndExecutingWrappersUseDistinctStableBindings()
    {
        var a = new Api(); var data = a.Data(); Set(data, "ReleasedWrappers", new[] { "resident-scheduler/released/000000", "resident-scheduler/released/000001" }); a.Validate(data);
        Set(data, "ReleasedWrappers", new[] { "resident-scheduler/released/000000", "resident-scheduler/released/000000" }); a.Reject(data);
        Set(data, "ReleasedWrappers", new[] { "arbitrary-external" }); a.Reject(data);
    }
    [TestMethod] public void IdleCacheEligibilityUsesBothActualHandlesAndAllowsUnchangedActivePeer()
    {
        var a = new Api();
        a.Canonicalizable(a.Capture(wrapperCache: true));
        a.Canonicalizable(a.Capture(enumeratorCache: true, wrapperCache: true));
        a.Canonicalizable(a.Capture());
        a.Canonicalizable(a.Capture(enumeratorAllocated: true, enumeratorPresent: true, enumeratorCache: true,
            enumeratorMatches: true, wrapperCache: true));
    }
    [TestMethod] public void ActiveAndExecutingMismatchesCannotBorrowTheIdleCacheGapPrefix()
    {
        var a = new Api();
        a.RejectCanonicalizable(a.Capture(enumeratorAllocated: true, enumeratorPresent: true, enumeratorCache: true, enumeratorMatches: false));
        a.RejectCanonicalizable(a.Capture(wrapperAllocated: true, wrapperCache: true, wrapperMatches: false));
        a.RejectCanonicalizable(a.Capture(enumeratorAllocated: true, enumeratorPresent: true, enumeratorCache: true,
            enumeratorMatches: true, wrapperCache: true, execution: true));
        a.RejectCanonicalizable(a.Capture(enumeratorAllocated: true, enumeratorPresent: false, enumeratorMatches: true, wrapperCache: true));
    }
    [TestMethod] public void CanonGapInventoryMustExactlyMatchItsCapturedNativeWitness()
    {
        var a = new Api(); var unrelated = a.Capture(wrapperCache: true);
        a.AddGap(unrelated, "scheduler-handle-cache-needs-canon:unobserved-coroutine"); a.RejectCanonicalizable(unrelated);
        var otherDomain = a.Capture(wrapperCache: true); a.AddGap(otherDomain, "scheduler-coroutine-native-async-operation:resident-scheduler/coroutine/000000"); a.RejectCanonicalizable(otherDomain);
        var missing = a.Capture(wrapperCache: true); a.ClearGaps(missing); a.RejectCanonicalizable(missing);
        var allocation = a.Capture(wrapperCache: true); var live = ((IDictionary)Get(allocation, "routines")).Values.Cast<object>().Single();
        var bytes = (byte[])Get(live, "Bytes"); Buffer.BlockCopy(BitConverter.GetBytes(7UL), 0, bytes, 40, 8); a.RejectCanonicalizable(allocation);
        var cache = a.Capture(wrapperCache: true); live = ((IDictionary)Get(cache, "routines")).Values.Cast<object>().Single();
        Array.Clear((byte[])Get(live, "Bytes"), 56, 8); a.RejectCanonicalizable(cache);
    }
    [TestMethod] public void MutatingPortableFlagsCannotReclassifyCapturedExecutionOrHandleOwnership()
    {
        var a = new Api(); var allocated = a.Capture(wrapperAllocated: true, wrapperCache: true, wrapperMatches: false);
        var row = ((Array)Get(Get(allocated, "Snapshot"), "Routines")).GetValue(0)!; Set(row, "WrapperAllocated", false); a.RejectCanonicalizable(allocated);
        var executing = a.Capture(enumeratorAllocated: true, enumeratorPresent: true, enumeratorCache: true,
            enumeratorMatches: true, wrapperCache: true, execution: true);
        row = ((Array)Get(Get(executing, "Snapshot"), "Routines")).GetValue(0)!; Set(row, "Execution", ""); a.RejectCanonicalizable(executing);
        var done = a.Capture(wrapperCache: true); row = ((Array)Get(Get(done, "Snapshot"), "Routines")).GetValue(0)!;
        Set(row, "Done", false); a.RejectCanonicalizable(done);
    }

    private sealed class UncalledIterator : IEnumerator
    {
        public object Current => throw new InvalidOperationException("Eligibility must not read IEnumerator.Current.");
        public bool MoveNext() => throw new InvalidOperationException("Eligibility must not advance a coroutine.");
        public void Reset() => throw new InvalidOperationException("Eligibility must not reset a coroutine.");
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static object Get(object row, string name) => row.GetType().GetField(name, All)!.GetValue(row)!;
    private static void Set(object row, string name, object value) => row.GetType().GetField(name, All)!.SetValue(row, value);
    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root); type = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll")).GetType("HollowKnightTAS.Runtime.FullRun.ResidentSchedulerState", true)!;
        }
        internal object Row(string name) => Activator.CreateInstance(type.GetNestedType(name, All)!, true)!;
        internal Array Rows(string name, params object[] rows) { var result = Array.CreateInstance(type.GetNestedType(name, All)!, rows.Length); for (var i = 0; i < rows.Length; i++) result.SetValue(rows[i], i); return result; }
        internal object Data() => Row("Data");
        internal object Owner(string binding, params string[] order) { var row = Row("Owner"); Set(row, "Binding", binding); Set(row, "Order", order); return row; }
        internal object Routine(string key, string owner)
        { var row = Row("Routine"); Set(row, "Key", key); Set(row, "Owner", owner); Set(row, "References", 1); Set(row, "EnumeratorPresent", true); Set(row, "EnumeratorWeakness", 2); return row; }
        internal object Callback(string kind, string owner, double time)
        {
            var row = Row("Callback"); Set(row, "Kind", kind); Set(row, "Owner", owner); Set(row, "Time", BitConverter.DoubleToInt64Bits(time));
            Set(row, "Call", kind == "start" ? 0x8ac680 : kind == "coroutine" ? 0x8a6c70 : 0x8c0ea0); Set(row, "Cleanup", kind == "start" ? 0 : kind == "coroutine" ? 0x8a6950 : 0x8c0f00); return row;
        }
        internal void Tree(object data, object left, object root, object right)
        { Set(left, "Parent", 1); Set(right, "Parent", 1); Set(root, "Left", 0); Set(root, "Right", 2); Set(root, "Color", 1); Set(data, "Callbacks", Rows("Callback", left, root, right)); Set(data, "TreeRoot", 1); }
        internal void Validate(object data) => type.GetMethod("Validate", All)!.Invoke(null, new[] { data });
        internal void Reject(object data) { var error = Assert.ThrowsExactly<TargetInvocationException>(() => Validate(data)); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
        internal object Capture(bool enumeratorAllocated = false, bool enumeratorPresent = false, bool enumeratorCache = false,
            bool? enumeratorMatches = null, bool wrapperAllocated = false, bool wrapperCache = false, bool? wrapperMatches = null, bool execution = false)
        {
            var capture = Row("CaptureResult"); var live = Row("RoutineLive"); var pointer = new IntPtr(0x1000); var bytes = new byte[136];
            var iterator = enumeratorPresent ? new UncalledIterator() : null;
            var wrapper = (UnityEngine.Coroutine)RuntimeHelpers.GetUninitializedObject(typeof(UnityEngine.Coroutine)); GC.SuppressFinalize(wrapper);
            typeof(UnityEngine.Coroutine).GetField("m_Ptr", All)!.SetValue(wrapper, pointer);
            var handleType = type.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentScriptingHandles+Capture", true)!;
            object Handle(int offset, bool allocated, bool cache, bool matches, object? target)
            {
                var raw = new byte[24]; Buffer.BlockCopy(BitConverter.GetBytes(allocated ? 7UL : ulong.MaxValue), 0, raw, 0, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(allocated ? 2 : 0), 0, raw, 8, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(cache ? 0x2200L : 0L), 0, raw, 16, 8); Buffer.BlockCopy(raw, 0, bytes, offset, raw.Length);
                return Activator.CreateInstance(handleType, All, null, new object?[] { IntPtr.Add(pointer, offset), raw, target, allocated ? 2 : 0, allocated, matches }, null)!;
            }
            var iteratorMatches = enumeratorMatches ?? !enumeratorCache; var managedWrapperMatches = wrapperMatches ?? !wrapperCache;
            var iteratorHandle = Handle(16, enumeratorAllocated, enumeratorCache, iteratorMatches, iterator);
            var wrapperHandle = Handle(40, wrapperAllocated, wrapperCache, managedWrapperMatches, wrapperAllocated ? wrapper : null);
            var row = Row("Routine"); var key = execution ? "resident-scheduler/execution/current-loader" : "resident-scheduler/coroutine/000000";
            Set(row, "Key", key); Set(row, "Execution", execution ? "current-loader" : ""); Set(row, "References", enumeratorPresent ? 1 : 0); Set(row, "Done", !enumeratorPresent);
            Set(row, "EnumeratorPresent", enumeratorPresent); Set(row, "EnumeratorWeakness", enumeratorAllocated ? 2 : 0);
            Set(row, "WrapperPresent", true); Set(row, "WrapperAllocated", wrapperAllocated); Set(row, "WrapperWeakness", wrapperAllocated ? 2 : 0);
            Buffer.BlockCopy(BitConverter.GetBytes(enumeratorPresent ? 1 : 0), 0, bytes, 96, 4); bytes[100] = enumeratorPresent ? (byte)0 : (byte)1;
            Set(live, "Pointer", pointer); Set(live, "Bytes", bytes); Set(live, "Row", row); Set(live, "Enumerator", iteratorHandle); Set(live, "WrapperHandle", wrapperHandle); Set(live, "Wrapper", wrapper);
            Set(Get(live, "Slot"), "Value", iterator!); ((IDictionary)Get(capture, "routines")).Add(pointer, live); ((IDictionary)Get(capture, "wrappers")).Add(wrapper, pointer);
            Set(capture, "executionBinding", (Func<object, string?>)(value => execution && ReferenceEquals(value, iterator) ? "current-loader" : null));
            var data = Data(); Set(data, "Routines", Rows("Routine", row)); Set(capture, "Snapshot", data);
            if (!iteratorMatches || !managedWrapperMatches) AddGap(capture, "scheduler-handle-cache-needs-canon:" + key);
            return capture;
        }
        internal void AddGap(object capture, string gap)
        {
            var set = Get(capture, "gaps"); set.GetType().GetMethod("Add")!.Invoke(set, new object[] { gap });
            Set(Get(capture, "Snapshot"), "CoverageGaps", ((IEnumerable)set).Cast<string>().ToArray());
        }
        internal void ClearGaps(object capture) { var set = Get(capture, "gaps"); set.GetType().GetMethod("Clear")!.Invoke(set, null); Set(Get(capture, "Snapshot"), "CoverageGaps", Array.Empty<string>()); }
        internal void Canonicalizable(object capture) => type.GetMethod("RequireCanonicalizableCoverage", All)!.Invoke(null, new[] { capture });
        internal void RejectCanonicalizable(object capture) { var error = Assert.ThrowsExactly<TargetInvocationException>(() => Canonicalizable(capture)); Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException()); }
    }
}
