using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentRandomProducerGateTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private sealed class Fixture
    {
        internal readonly object Gate;
        internal readonly List<string> Sent = new();
        internal bool Admitted = true;
        internal Fixture()
        {
            var thread = Thread.CurrentThread.ManagedThreadId;
            Action requireThread = () => { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("wrong thread"); };
            Action<ulong> requireBoundary = token => { if (!Admitted || token == 0) throw new InvalidOperationException("native epoch rejected"); };
            var type = Payload().GetType("HollowKnightTAS.ClockPayload.ResidentRandomProducerGate", true)!;
            Gate = Activator.CreateInstance(type, Flags, null, new object[] { requireThread, requireBoundary }, null)!;
        }
        internal void Request() => Call(Gate, "Request", (Action)(() => { Sent.Add("ack-reset"); Sent.Add("request-set"); }));
    }

    [TestMethod]
    public void TwoNativeEpochsPreserveOutsideRequestsAndRejectStaleReuse()
    {
        var fixture = new Fixture(); fixture.Request();
        Call(fixture.Gate, "Open", 11UL); Call(fixture.Gate, "Require", 11UL); Call(fixture.Gate, "Close", 11UL); fixture.Request();
        Call(fixture.Gate, "Open", 12UL); Call(fixture.Gate, "Require", 12UL); Call(fixture.Gate, "Close", 12UL);
        CollectionAssert.AreEqual(new[] { "ack-reset", "request-set", "ack-reset", "request-set" }, fixture.Sent.ToArray());
        Reject(() => Call(fixture.Gate, "Open", 11UL)); Reject(fixture.Request); Assert.AreEqual(4, fixture.Sent.Count);
    }

    [TestMethod]
    public void FencedRequestCannotResetAcknowledgementOrSignalAndPermanentlyFaults()
    {
        var fixture = new Fixture(); Call(fixture.Gate, "Open", 7UL);
        Reject(fixture.Request); Assert.AreEqual(0, fixture.Sent.Count);
        Reject(() => Call(fixture.Gate, "Require", 7UL)); Reject(() => Call(fixture.Gate, "Open", 8UL)); Reject(fixture.Request);
    }

    [TestMethod]
    public void BackgroundProducerIsRejectedBeforeAnySignal()
    {
        var fixture = new Fixture();
        Task.Run(() => Reject(fixture.Request)).GetAwaiter().GetResult();
        Assert.AreEqual(0, fixture.Sent.Count); Reject(fixture.Request);
    }

    [TestMethod]
    public void ReentrantProducerCannotBeHiddenByCatchingItsError()
    {
        var fixture = new Fixture();
        Reject(() => Call(fixture.Gate, "Request", (Action)(() => {
            try { fixture.Request(); } catch (TargetInvocationException) { }
        })));
        Assert.AreEqual(0, fixture.Sent.Count); Reject(fixture.Request);
    }

    [TestMethod]
    public void NativeAdmissionAndPartialSignalFailuresCannotBeRetried()
    {
        var missing = new Fixture { Admitted = false };
        Reject(() => Call(missing.Gate, "Open", 1UL)); missing.Admitted = true; Reject(missing.Request);
        var partial = new Fixture();
        Reject(() => Call(partial.Gate, "Request", (Action)(() => { partial.Sent.Add("ack-reset"); throw new InvalidOperationException("signal failure"); })));
        Reject(partial.Request); CollectionAssert.AreEqual(new[] { "ack-reset" }, partial.Sent.ToArray());
    }

    [TestMethod]
    public void AbortAfterCloseClosesTheNativeCommitFailureWindow()
    {
        var fixture = new Fixture(); Call(fixture.Gate, "Open", 41UL); Call(fixture.Gate, "Close", 41UL);
        fixture.Admitted = false; // native Complete may now reject or time out
        Call(fixture.Gate, "Abort", 41UL);
        fixture.Admitted = true; Reject(fixture.Request); Reject(() => Call(fixture.Gate, "Open", 42UL));
        Assert.AreEqual(0, fixture.Sent.Count);
        var wrong = new Fixture(); Call(wrong.Gate, "Open", 9UL);
        Reject(() => Call(wrong.Gate, "Abort", 8UL)); Reject(wrong.Request);
        var foreign = new Fixture(); Call(foreign.Gate, "Open", 19UL);
        Task.Run(() => Reject(() => Call(foreign.Gate, "Abort", 19UL))).GetAwaiter().GetResult();
        Reject(foreign.Request); Reject(() => Call(foreign.Gate, "Require", 19UL));
    }

    [TestMethod]
    public void AbortDoesNotWaitForTheProducerMonitor()
    {
        var fixture = new Fixture(); Call(fixture.Gate, "Open", 5UL);
        var sync = fixture.Gate.GetType().GetField("sync", Flags)!.GetValue(fixture.Gate)!;
        using var held = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var timedOut = false;
        var worker = Task.Run(() => { lock (sync) { held.Set(); timedOut = !release.Wait(TimeSpan.FromSeconds(3)); } });
        try { Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(3))); Call(fixture.Gate, "Abort", 5UL); }
        finally { release.Set(); worker.GetAwaiter().GetResult(); }
        Assert.IsFalse(timedOut, "abort waited for the producer monitor"); Reject(fixture.Request);
    }

    [TestMethod]
    public void PayloadExportsExposeTheExactSharedProducerAbi()
    {
        var type = Payload().GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
        // Metadata only: invoking ClockController would require the real engine.
        Assert.AreEqual(typeof(int), type.GetMethod("GetResidentRandomProducerAbi", Flags, null, Type.EmptyTypes, null)!.ReturnType);
        Assert.AreEqual(typeof(void), type.GetMethod("RequestResidentRandom", Flags, null, new[] { typeof(int) }, null)!.ReturnType);
        foreach (var name in new[] { "OpenResidentRandomProducerFence", "RequireResidentRandomProducerFence", "CloseResidentRandomProducerFence", "AbortResidentRandomProducerFence" })
            Assert.AreEqual(typeof(void), type.GetMethod(name, Flags, null, new[] { typeof(ulong) }, null)!.ReturnType);
    }

    private static Assembly Payload()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
        if (root == null) throw new DirectoryNotFoundException("repository root is absent");
        return Assembly.LoadFrom(Path.Combine(root.FullName, "src", "HollowKnightTAS.ClockPayload", "bin", "Release", "HollowKnightTAS.ClockPayload.dll"));
    }
    private static object? Call(object value, string name, params object[] arguments) => value.GetType().GetMethod(name, Flags)!.Invoke(value, arguments);
    private static void Reject(Action action)
    { var error = Assert.Throws<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException); }
}
