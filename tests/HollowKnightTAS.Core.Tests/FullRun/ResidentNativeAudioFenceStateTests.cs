using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentNativeAudioFenceStateTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const ulong Closed = 0x4000000000000000UL;
    private sealed class Api
    {
        internal readonly Type Type;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            Type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentNativeAudioFenceState", true)!;
        }
        internal object? Call(string name, params object[] args) => Type.GetMethod(name, All)!.Invoke(null, args);
        internal object Capture(ulong token, Func<byte[]> header, Func<byte[]> workers, Action? admitted = null)
            => Call("Capture", token, header, workers, admitted ?? (() => { }))!;
        internal object Capture(byte[] header, byte[] workers, ulong token = 7) => Capture(token, () => header, () => workers);
        internal byte[] Encode(object data) => (byte[])Call("EncodeS0", data)!;
    }
    private static T Get<T>(object owner, string name) => (T)owner.GetType().GetField(name, All)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, All)!.SetValue(owner, value);
    private static void Require(object live) => live.GetType().GetMethod("RequireUnchanged", All)!.Invoke(live, null);
    private static void Reject(Action action)
    {
        var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
    }
    private static void U32(byte[] bytes, int offset, uint value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    private static void U64(byte[] bytes, int offset, ulong value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 8);
    private static byte[] Header(uint count = 1, ulong token = 7, ulong start = 100, ulong now = 110, uint main = 44)
    {
        var bytes = new byte[96]; U32(bytes, 0, 0x31464152); U32(bytes, 4, 1); U32(bytes, 8, 96); U32(bytes, 12, 64);
        U32(bytes, 16, 1); U32(bytes, 20, 1); U64(bytes, 24, token); U64(bytes, 32, start); U64(bytes, 40, now); U64(bytes, 48, Closed);
        U32(bytes, 60, count); U32(bytes, 64, 10); U32(bytes, 72, main); U32(bytes, 76, main); U32(bytes, 80, 1); U32(bytes, 84, 256); return bytes;
    }
    private static byte[] Worker(uint kind = 1, ulong thread = 0x1000, uint id = 55, ulong origin = 0, ulong epoch = 6)
    {
        var bytes = new byte[64]; U64(bytes, 0, thread + origin); U64(bytes, 8, 0x2000 + origin);
        U64(bytes, 16, 0x3000UL + kind * 0x100UL + origin); U64(bytes, 24, 0x4000 + origin); U64(bytes, 32, 0x5000 + origin);
        U64(bytes, 40, epoch); U32(bytes, 48, id); U32(bytes, 52, kind); U32(bytes, 56, 2); return bytes;
    }

    [TestMethod]
    public void CaptureBracketsTheWorkerCopyAndRetainsTheLocalNativeLease()
    {
        var api = new Api(); var status = Header(); var workers = Worker(); var calls = "";
        var live = api.Capture(7, () => { calls += "s"; return status; }, () => { calls += "w"; return workers; }, () => calls += "a");
        Assert.AreEqual("aswsa", calls); var receipt = Get<object>(live, "Receipt");
        Assert.AreEqual(7UL, Get<ulong>(receipt, "Epoch")); Assert.AreEqual(100UL, Get<ulong>(receipt, "StartedTick"));
        Assert.AreEqual(110UL, Get<ulong>(receipt, "ObservedTick")); Assert.AreEqual(44U, Get<uint>(receipt, "MainThread"));
        var data = Get<object>(live, "Snapshot"); api.Call("RequireFence", data);
        Assert.AreEqual(1, Get<int>(data, "WorkerCount")); var row = Get<Array>(data, "Domains").GetValue(0)!;
        Assert.AreEqual("wasapi-feeder", Get<string>(row, "Name")); Assert.AreEqual(1, Get<int>(row, "Count"));
        U64(status, 40, 112); calls = ""; Require(live); Assert.AreEqual("aswsa", calls);
    }
    [TestMethod]
    public void UnsupportedAbiOrUnclosedFaultedBusyExpiredOrWrongThreadCannotReadWorkers()
    {
        var api = new Api();
        var invalid = new Action<byte[]>[] { b => U32(b, 0, 0), b => U32(b, 4, 2), b => U32(b, 8, 88), b => U32(b, 12, 56),
            b => U32(b, 16, 0), b => U32(b, 20, 0), b => U32(b, 20, 9), b => U64(b, 24, 8), b => U64(b, 40, 99), b => U64(b, 40, 9100),
            b => U64(b, 48, 0), b => U64(b, 48, Closed | 1), b => U64(b, 48, Closed | (1UL << 38)), b => U64(b, 48, Closed | (1UL << 63)),
            b => U32(b, 56, 36), b => U32(b, 60, 257), b => U32(b, 68, 1), b => U32(b, 72, 0), b => U32(b, 76, 81),
            b => U32(b, 80, 0), b => U32(b, 84, 128), b => U64(b, 88, 1) };
        foreach (var edit in invalid) {
            var bytes = Header(); edit(bytes); var copies = 0;
            Reject(() => api.Capture(7, () => bytes, () => { ++copies; return Worker(); })); Assert.AreEqual(0, copies);
        }
        Reject(() => api.Capture(new byte[95], Worker())); Reject(() => api.Capture(new byte[97], Worker()));
        Reject(() => api.Capture(Header(), Worker(), 0));
        api.Capture(Header(now: 9099), Worker()); // Exactly one millisecond of native wall budget remains.
    }
    [TestMethod]
    public void MixedPublicationAndLeaseChangesAcrossTheCopyReject()
    {
        var api = new Api();
        var invalid = new Action<byte[]>[] { b => U32(b, 64, 11), b => U64(b, 32, 99), b => U64(b, 40, 109),
            b => U32(b, 60, 0), b => { U32(b, 68, 1); U32(b, 20, 5); }, b => { U32(b, 72, 45); U32(b, 76, 45); } };
        foreach (var edit in invalid) {
            var before = Header(); var after = (byte[])before.Clone(); edit(after); var reads = 0;
            Reject(() => api.Capture(7, () => ++reads == 1 ? before : after, () => Worker()));
        }
    }
    [TestMethod]
    public void FileIoWorkerHasAnIdentityButStillNeedsItsRequestState()
    {
        var api = new Api(); var data = Get<object>(api.Capture(Header(1), Worker(6)), "Snapshot");
        var domain = Get<Array>(data, "Domains").GetValue(0)!;
        Assert.AreEqual("fmod-file-io", Get<string>(domain, "Name"));
        CollectionAssert.Contains((string[])api.Call("RequiredCoverageGaps",data)!, "audio-fmod-file-requests-and-read-cursors");
    }
    [TestMethod]
    public void MalformedOrAliasedWorkerRecordsCannotMasqueradeAsDrainedDomains()
    {
        var api = new Api(); var header = Header();
        var invalid = new Action<byte[]>[] { b => U64(b, 0, 0), b => U64(b, 8, 0), b => U64(b, 16, 0),
            b => U64(b, 40, 8), b => U32(b, 48, 0), b => U32(b, 48, 44), b => U32(b, 52, 7), b => U32(b, 52, 0),
            b => U32(b, 56, 3), b => U32(b, 60, 1) };
        foreach (var edit in invalid) { var row = Worker(); edit(row); Reject(() => api.Capture(header, row)); }
        Reject(() => api.Capture(header, new byte[63])); Reject(() => api.Capture(header, new byte[128]));
        var first = Worker(); var second = Worker(2, 0x1100, 56);
        var duplicatePointer = (byte[])second.Clone(); U64(duplicatePointer, 0, 0x1000);
        Reject(() => api.Capture(Header(2), first.Concat(duplicatePointer).ToArray()));
        var duplicateThread = (byte[])second.Clone(); U32(duplicateThread, 48, 55);
        Reject(() => api.Capture(Header(2), first.Concat(duplicateThread).ToArray()));
        var conflictingKind = (byte[])second.Clone(); U64(conflictingKind, 16, 0x3100);
        Reject(() => api.Capture(Header(2), first.Concat(conflictingKind).ToArray()));
        var conflictingAddress = (byte[])second.Clone(); U32(conflictingAddress, 52, 1);
        Reject(() => api.Capture(Header(2), first.Concat(conflictingAddress).ToArray()));
    }
    [TestMethod]
    public void PortableAdmissionProjectionExcludesProcessIdentitiesButRetainsActualDomains()
    {
        var api = new Api(); var a = api.Capture(Header(2), Worker().Concat(Worker(2, 0x1100, 56)).ToArray());
        var b = api.Capture(Header(2, 31, 3000, 3027, 91), Worker(2, 0x1100, 70, 0x10000, 30).Concat(Worker(1, 0x1000, 72, 0x10000, 29)).ToArray(), 31);
        CollectionAssert.AreEqual(api.Encode(Get<object>(a, "Snapshot")), api.Encode(Get<object>(b, "Snapshot")));
        var c = api.Capture(Header(2), Worker().Concat(Worker(3, 0x1100, 56)).ToArray());
        Assert.IsFalse(api.Encode(Get<object>(a, "Snapshot")).SequenceEqual(api.Encode(Get<object>(c, "Snapshot"))));
        var missingUserData = Worker(); U64(missingUserData, 32, 0);
        var d = api.Capture(Header(2), missingUserData.Concat(Worker(2, 0x1100, 56)).ToArray());
        Assert.IsFalse(api.Encode(Get<object>(a, "Snapshot")).SequenceEqual(api.Encode(Get<object>(d, "Snapshot"))));
    }
    [TestMethod]
    public void LocalReadbackDetectsPointerAliasAndReleaseDriftEvenWithIdenticalDomainCensus()
    {
        var api = new Api(); var header = Header(); var workers = Worker(); var live = api.Capture(header, workers);
        var expected = api.Encode(Get<object>(live, "Snapshot")); U64(workers, 32, 0xdeadbeef);
        var changed = api.Capture(header, workers); CollectionAssert.AreEqual(expected, api.Encode(Get<object>(changed, "Snapshot"))); Reject(() => Require(live));
        U64(workers, 32, 0x5000); Require(live); U64(workers, 40, 5); Reject(() => Require(live));
        U64(workers, 40, 6); U64(header, 48, 0); Reject(() => Require(live));
    }
    [TestMethod]
    public void EmptyDrainedFenceAndClearedGapTextNeverGrantFmodOrDeviceCoverage()
    {
        var api = new Api(); var empty = Get<object>(api.Capture(Header(0), Array.Empty<byte>()), "Snapshot");
        api.Call("RequireFence", empty); Set(empty, "CoverageGaps", Array.Empty<string>()); Reject(() => api.Call("RequireCompleteCoverage", empty));
        var gaps = (string[])api.Call("RequiredCoverageGaps", empty)!;
        CollectionAssert.Contains(gaps, "audio-native-device-and-thread-state"); CollectionAssert.Contains(gaps, "audio-fence-non-thread-producer-admission");
        CollectionAssert.Contains(gaps, "audio-fmod-channel-dsp-codec-and-sample-clock-state"); CollectionAssert.Contains(gaps, "audio-output-device-ring-and-buffered-pcm-state");
        Set(empty, "NativeCoverageBits", 0U); Reject(() => api.Call("RequireFence", empty));
    }
    [TestMethod]
    public void PendingStartupAndUnknownCallbackStayExplicitAndCannotBeErased()
    {
        var api = new Api(); var header = Header(); U32(header, 20, 7); U32(header, 68, 2);
        var unknown = Worker(0); U64(unknown, 16, 0); var live = api.Capture(header, unknown); var data = Get<object>(live, "Snapshot");
        var gaps = (string[])api.Call("RequiredCoverageGaps", data)!;
        CollectionAssert.Contains(gaps, "audio-fence-pending-thread-startup-state"); CollectionAssert.Contains(gaps, "audio-fence-unknown-or-virtual-callback-domain");
        CollectionAssert.Contains(gaps, "audio-native-worker-owner-and-callback-alias-state");
        Set(data, "CoverageGaps", Array.Empty<string>()); Reject(() => Require(live)); Reject(() => api.Call("RequireCompleteCoverage", data));
        Set(data, "NativeCoverageBits", 5U); Reject(() => api.Call("RequireFence", data));
    }
}
