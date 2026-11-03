using System;
using System.IO;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentKernelEventStateTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [TestMethod]
    public void RepeatedNativeQueryDoesNotConsumeSignaledAutoResetEvent()
    {
        using var owner = new EventWaitHandle(true, EventResetMode.AutoReset);
        var api = new Api(); var witness = api.Capture(owner, EventResetMode.AutoReset);
        for (var index = 0; index < 128; index++) api.Require(witness);
        Assert.IsTrue(api.Signaled(witness));
        Assert.IsTrue(owner.WaitOne(0), "The original signal must still be available after all read-only queries.");
        Assert.IsFalse(owner.WaitOne(0));
        api.Reject(() => api.Require(witness));
    }

    [TestMethod]
    public void ManualResetEventUsesItsActualNativeTypeAndDetectsReset()
    {
        using var owner = new EventWaitHandle(true, EventResetMode.ManualReset);
        var api = new Api(); var witness = api.Capture(owner, EventResetMode.ManualReset);
        api.Require(witness); Assert.IsTrue(api.Signaled(witness));
        api.Reject(() => api.Capture(owner, EventResetMode.AutoReset));
        Assert.IsTrue(owner.WaitOne(0)); Assert.IsTrue(owner.WaitOne(0));
        owner.Reset(); api.Reject(() => api.Require(witness));
    }

    [TestMethod]
    public void UnsignaledRequestWitnessRejectsLaterSignalAndDisposedHandle()
    {
        var api = new Api();
        using (var owner = new EventWaitHandle(false, EventResetMode.AutoReset)) {
            var witness = api.Capture(owner, EventResetMode.AutoReset); Assert.IsFalse(api.Signaled(witness));
            owner.Set(); api.Reject(() => api.Require(witness)); Assert.IsTrue(owner.WaitOne(0));
            api.Require(witness); // No internal hidden state was changed by failed observation.
            owner.Dispose(); api.Reject(() => api.Require(witness));
        }
    }

    [TestMethod]
    public void DistinctHandlesForTheSameEventMatchWithoutConsumingItsSignal()
    {
        var api = new Api(); var name = "Local\\HKTAS-alias-" + Guid.NewGuid().ToString("N");
        using var owner = new EventWaitHandle(true, EventResetMode.AutoReset, name);
        using var alias = EventWaitHandle.OpenExisting(name);
        using var unrelated = new EventWaitHandle(true, EventResetMode.AutoReset);
        var witness = api.Alias(owner, alias); api.Require(witness); api.Reject(() => api.Alias(owner, unrelated));
        Assert.IsTrue(alias.WaitOne(0)); Assert.IsFalse(owner.WaitOne(0)); api.Require(witness);
        alias.Dispose(); api.Reject(() => api.Require(witness));
        Assert.IsTrue(owner.Set()); Assert.IsTrue(owner.WaitOne(0));
    }

    [TestMethod]
    public void ExposedSnapshotCannotChangeTheFrozenNativeWitness()
    {
        using var owner = new EventWaitHandle(false, EventResetMode.AutoReset);
        var api = new Api(); var witness = api.Capture(owner, EventResetMode.AutoReset);
        var snapshot = witness.GetType().GetProperty("Snapshot", All)!.GetValue(witness)!;
        snapshot.GetType().GetField("Signaled", All)!.SetValue(snapshot, true);
        api.Require(witness); Assert.IsFalse(api.Signaled(witness));
        Assert.IsFalse(owner.WaitOne(0));
    }

    private sealed class Api
    {
        private readonly Type type;
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            type = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentKernelEventState", true)!;
        }
        internal object Capture(EventWaitHandle owner, EventResetMode mode)
            => type.GetMethod("Capture", All)!.Invoke(null, new object[] { owner, mode, (Action)(() => { }) })!;
        internal object Alias(EventWaitHandle first, EventWaitHandle second)
            => type.GetMethod("RequireSameObject", All)!.Invoke(null, new object[] { first, second, (Action)(() => { }) })!;
        internal void Require(object witness) => witness.GetType().GetMethod("RequireUnchanged", All)!.Invoke(witness, null);
        internal bool Signaled(object witness)
        {
            var snapshot = witness.GetType().GetProperty("Snapshot", All)!.GetValue(witness)!;
            return (bool)snapshot.GetType().GetField("Signaled", All)!.GetValue(snapshot)!;
        }
        internal void Reject(Action action)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(action);
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
    }
}
