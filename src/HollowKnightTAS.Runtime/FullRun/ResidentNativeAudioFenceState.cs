using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read-only evidence for the native producer fence. This receipt describes
    // admission, not FMOD sound/channel/DSP state or the external audio device.
    // Process addresses, OS thread IDs and the wall-clock lease remain local to
    // Live; they are never imported as source game state or compared as S0.
    internal static class ResidentNativeAudioFenceState
    {
        internal const int StatusBytes = 96, WorkerBytes = 64, MaximumWorkers = 256;
        private const uint Magic = 0x31464152, WireVersion = 1, SupportedGaps = 7;
        private const ulong CountMask = 0xffffffffUL, ClosedBit = 0x4000000000000000UL, FailedBit = 0x8000000000000000UL;
        private const ulong WallLimit = 9000;
        private static readonly string[] DomainNames = { "unknown-or-virtual", "wasapi-feeder", "wasapi-mixer", "wasapi-record", "fmod-stream", "fmod-async-decoder-file", "fmod-file-io" };

        internal sealed class Status
        {
            public uint Capability, CoverageBits, WorkerCount, RecordVersion, PendingStartups, MainThread, CallerThread, PinnedAbi;
            public ulong Epoch, StartedTick, ObservedTick, State;
            public int Fault;
            internal uint InFlight => (uint)(State & CountMask);
            internal bool Closed => (State & ClosedBit) != 0;
            internal bool Failed => (State & FailedBit) != 0;
        }
        internal sealed class Worker
        {
            public ulong Thread, System, Callback, SystemCallback, UserData, LastEpoch;
            public uint ThreadId, Kind, Phase, Depth;
        }
        internal sealed class Domain
        {
            public int Kind, Count;
            public string Name = "";
            public bool HasSystem, HasSystemCallback, HasUserData, VirtualDispatch;
        }
        internal sealed class Data
        {
            public int Version = 1, NativeVersion = 1, NativeStatusBytes = StatusBytes, NativeWorkerBytes = WorkerBytes, NativeMaximumWorkers = MaximumWorkers;
            public uint Capability, NativeCoverageBits, InFlight, PendingStartups;
            public bool PinnedAbi, Closed, Failed;
            public int Fault, WorkerCount;
            public Domain[] Domains = Array.Empty<Domain>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Readback
        {
            internal Status Status = null!;
            internal Worker[] Workers = Array.Empty<Worker>();
            internal byte[] WorkerPacket = Array.Empty<byte>();
        }
        internal sealed class Live
        {
            internal readonly Data Snapshot;
            // Local diagnostics are intentionally separate from Snapshot.
            internal readonly Status Receipt;
            private readonly ulong token;
            private readonly Func<byte[]> readStatus, readWorkers;
            private readonly Action admitted;
            private readonly Readback original;
            private readonly ResidentWasapiEndpoints.Live? endpoints;
            internal ResidentWasapiEndpoints.Live Endpoints => endpoints ?? throw Error("native output observation is absent");
            private readonly string frozen;
            private readonly int managedThread = Thread.CurrentThread.ManagedThreadId;

            internal Live(ulong token, Func<byte[]> readStatus, Func<byte[]> readWorkers, Action admitted, Readback readback, bool readEndpoints = false)
            {
                this.token = token; this.readStatus = readStatus; this.readWorkers = readWorkers; this.admitted = admitted;
                original = readback; Receipt = CopyStatus(readback.Status); Snapshot = Project(readback.Status, readback.Workers);
                if (readEndpoints) {
                    endpoints = new ResidentWasapiEndpoints.Live(readback.Workers, admitted);
                    ResidentWasapiEndpoints.RequireWorkerCensus(endpoints.Rows,Snapshot);
                }
                frozen = JsonConvert.SerializeObject(Snapshot);
            }
            internal string DescribeWorkers(Func<Worker,string> describe)
            {
                RequireUnchanged();
                var result = string.Join(",", original.Workers.Select(describe).OrderBy(value => value, StringComparer.Ordinal));
                RequireUnchanged();
                return result;
            }
            internal void Require() => RequireUnchanged();
            internal void RequireUnchanged()
            {
                if (Thread.CurrentThread.ManagedThreadId != managedThread) throw Error("observation moved away from the admitted thread");
                if (frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("portable observation was modified");
                RequireSameStatus(original.Status, Receipt, false);
                var current = Read(token, readStatus, readWorkers, admitted);
                RequireSameStatus(original.Status, current.Status, true);
                if (!original.WorkerPacket.SequenceEqual(current.WorkerPacket)) throw Error("native worker identity, aliases or admission phase changed");
                RequireFence(Snapshot);
            }
            internal void RequireComplete() { RequireUnchanged(); RequireCompleteCoverage(Snapshot); }
        }

        internal static Live Capture(ulong token, Func<byte[]> readStatus, Func<byte[]> readWorkers, Action admitted)
        {
            if (readStatus == null || readWorkers == null || admitted == null) throw new ArgumentNullException();
            return new Live(token, readStatus, readWorkers, admitted, Read(token, readStatus, readWorkers, admitted));
        }
        internal static Live CaptureBackend(ulong token, Func<byte[]> readStatus, Func<byte[]> readWorkers, Action admitted)
            => new Live(token,readStatus,readWorkers,admitted,Read(token,readStatus,readWorkers,admitted),true);

        private static Readback Read(ulong token, Func<byte[]> readStatus, Func<byte[]> readWorkers, Action admitted)
        {
            admitted(); var before = DecodeStatus(Packet(readStatus(), StatusBytes)); RequireStatus(before, token);
            var packet = readWorkers() ?? throw Error("native worker packet is absent");
            if (packet.Length > MaximumWorkers * WorkerBytes) throw Error("native worker packet exceeds the ABI bound");
            packet = (byte[])packet.Clone(); var workers = DecodeWorkers(packet, before);
            var after = DecodeStatus(Packet(readStatus(), StatusBytes)); RequireStatus(after, token);
            RequireSameStatus(before, after, true); admitted();
            return new Readback { Status = after, Workers = workers, WorkerPacket = packet };
        }
        private static byte[] Packet(byte[]? value, int size)
        { return value == null || value.Length != size ? throw Error("native status packet has the wrong size") : (byte[])value.Clone(); }

        internal static Status DecodeStatus(byte[] bytes)
        {
            if (bytes == null || bytes.Length != StatusBytes) throw Error("native status packet has the wrong size");
            using (var reader = new BinaryReader(new MemoryStream(bytes, false))) {
                if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != WireVersion || reader.ReadUInt32() != StatusBytes || reader.ReadUInt32() != WorkerBytes)
                    throw Error("native status magic, version or layout differs");
                var result = new Status { Capability = reader.ReadUInt32(), CoverageBits = reader.ReadUInt32(), Epoch = reader.ReadUInt64(),
                    StartedTick = reader.ReadUInt64(), ObservedTick = reader.ReadUInt64(), State = reader.ReadUInt64(), Fault = reader.ReadInt32(),
                    WorkerCount = reader.ReadUInt32(), RecordVersion = reader.ReadUInt32(), PendingStartups = reader.ReadUInt32(),
                    MainThread = reader.ReadUInt32(), CallerThread = reader.ReadUInt32(), PinnedAbi = reader.ReadUInt32() };
                if (reader.ReadUInt32() != MaximumWorkers || reader.ReadUInt64() != 0) throw Error("native worker bound or reserved bytes differ");
                if (result.Capability > 1 || result.PinnedAbi > 1 || result.Fault < 0 || result.WorkerCount > MaximumWorkers || result.PendingStartups > int.MaxValue
                    || result.InFlight > MaximumWorkers || (result.State & ~(CountMask | ClosedBit | FailedBit)) != 0
                    || (result.CoverageBits & ~SupportedGaps) != 0 || (result.CoverageBits & 1) == 0
                    || ((result.CoverageBits & 4) != 0) != (result.PendingStartups != 0)) throw Error("native status fields are inconsistent or unsupported");
                return result;
            }
        }
        private static Status CopyStatus(Status value) => new Status {
            Capability = value.Capability, CoverageBits = value.CoverageBits, Epoch = value.Epoch, StartedTick = value.StartedTick,
            ObservedTick = value.ObservedTick, State = value.State, Fault = value.Fault, WorkerCount = value.WorkerCount,
            RecordVersion = value.RecordVersion, PendingStartups = value.PendingStartups, MainThread = value.MainThread,
            CallerThread = value.CallerThread, PinnedAbi = value.PinnedAbi
        };
        private static void RequireStatus(Status value, ulong token)
        {
            if (token == 0 || value.Epoch != token || value.Capability != 1 || value.PinnedAbi != 1 || value.State != ClosedBit || value.Fault != 0
                || value.MainThread == 0 || value.CallerThread != value.MainThread || value.ObservedTick < value.StartedTick
                || value.ObservedTick - value.StartedTick >= WallLimit) throw Error("native lease is absent, open, busy, faulted, stale, off-thread or expired");
        }
        private static void RequireSameStatus(Status expected, Status actual, bool wallMayAdvance)
        {
            if (expected.Capability != actual.Capability || expected.CoverageBits != actual.CoverageBits || expected.Epoch != actual.Epoch
                || expected.StartedTick != actual.StartedTick || expected.State != actual.State || expected.Fault != actual.Fault
                || expected.WorkerCount != actual.WorkerCount || expected.RecordVersion != actual.RecordVersion || expected.PendingStartups != actual.PendingStartups
                || expected.MainThread != actual.MainThread || expected.CallerThread != actual.CallerThread || expected.PinnedAbi != actual.PinnedAbi
                || (wallMayAdvance ? actual.ObservedTick < expected.ObservedTick : actual.ObservedTick != expected.ObservedTick))
                throw Error("native lease or worker publication changed during observation");
        }
        internal static Worker[] DecodeWorkers(byte[] bytes, Status status)
        {
            if (bytes == null || status == null || status.WorkerCount > MaximumWorkers || bytes.Length != checked((int)status.WorkerCount * WorkerBytes))
                throw Error("native worker packet count differs from status");
            var threads = new HashSet<ulong>(); var ids = new HashSet<uint>(); var result = new List<Worker>();
            var callbackKinds = new Dictionary<ulong, uint>(); var knownCallbacks = new Dictionary<uint, ulong>();
            using (var reader = new BinaryReader(new MemoryStream(bytes, false))) while (reader.BaseStream.Position < reader.BaseStream.Length) {
                var row = new Worker { Thread = reader.ReadUInt64(), System = reader.ReadUInt64(), Callback = reader.ReadUInt64(), SystemCallback = reader.ReadUInt64(),
                    UserData = reader.ReadUInt64(), LastEpoch = reader.ReadUInt64(), ThreadId = reader.ReadUInt32(), Kind = reader.ReadUInt32(), Phase = reader.ReadUInt32(), Depth = reader.ReadUInt32() };
                if (row.Thread == 0 || row.ThreadId == 0 || row.ThreadId == status.MainThread || !threads.Add(row.Thread) || !ids.Add(row.ThreadId)
                    || row.Kind >= DomainNames.Length || row.Kind != 0 && row.Callback == 0 || row.System == 0 && row.SystemCallback != 0
                    || row.Phase != 2 || row.Depth != 0 || row.LastEpoch > status.Epoch || row.Kind == 0 && (status.CoverageBits & 2) == 0)
                    throw Error("native worker identity, callback domain or drained phase differs");
                if (row.Callback != 0) {
                    if (callbackKinds.TryGetValue(row.Callback, out var kind) && kind != row.Kind
                        || row.Kind != 0 && knownCallbacks.TryGetValue(row.Kind, out var callback) && callback != row.Callback)
                        throw Error("a pinned worker callback address has conflicting native domain identities");
                    callbackKinds[row.Callback] = row.Kind; if (row.Kind != 0) knownCallbacks[row.Kind] = row.Callback;
                }
                result.Add(row);
            }
            return result.ToArray();
        }

        private static Data Project(Status status, Worker[] workers)
        {
            var domains = workers.GroupBy(row => new { row.Kind, HasSystem = row.System != 0, HasSystemCallback = row.SystemCallback != 0,
                HasUserData = row.UserData != 0, VirtualDispatch = row.Callback == 0 }).Select(group => new Domain {
                    Kind = (int)group.Key.Kind, Name = DomainNames[group.Key.Kind], Count = group.Count(), HasSystem = group.Key.HasSystem,
                    HasSystemCallback = group.Key.HasSystemCallback, HasUserData = group.Key.HasUserData, VirtualDispatch = group.Key.VirtualDispatch
                }).OrderBy(DomainKey, StringComparer.Ordinal).ToArray();
            var data = new Data { Capability = status.Capability, NativeCoverageBits = status.CoverageBits, InFlight = status.InFlight, PendingStartups = status.PendingStartups,
                PinnedAbi = status.PinnedAbi == 1, Closed = status.Closed, Failed = status.Failed, Fault = status.Fault, WorkerCount = workers.Length, Domains = domains };
            data.CoverageGaps = RequiredCoverageGaps(data); Validate(data); return data;
        }
        private static string DomainKey(Domain row) => row.Kind + "/" + (row.HasSystem ? "1" : "0") + (row.HasSystemCallback ? "1" : "0")
            + (row.HasUserData ? "1" : "0") + (row.VirtualDispatch ? "1" : "0");

        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || data.NativeVersion != WireVersion || data.NativeStatusBytes != StatusBytes || data.NativeWorkerBytes != WorkerBytes
                || data.NativeMaximumWorkers != MaximumWorkers || data.Capability > 1 || data.Fault < 0 || data.InFlight > MaximumWorkers || data.PendingStartups > int.MaxValue
                || (data.NativeCoverageBits & ~SupportedGaps) != 0 || (data.NativeCoverageBits & 1) == 0
                || ((data.NativeCoverageBits & 4) != 0) != (data.PendingStartups != 0) || data.WorkerCount < 0 || data.WorkerCount > MaximumWorkers
                || data.Domains == null || data.Domains.Length > MaximumWorkers || data.CoverageGaps == null || data.CoverageGaps.Length > 128
                || data.CoverageGaps.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 512)) throw Error("portable fence observation has an invalid shape");
            string? previous = null; var count = 0;
            foreach (var row in data.Domains) {
                if (row == null || row.Kind < 0 || row.Kind >= DomainNames.Length || row.Name != DomainNames[row.Kind] || row.Count <= 0 || row.Count > MaximumWorkers
                    || row.HasSystemCallback && !row.HasSystem || row.VirtualDispatch && row.Kind != 0 || row.Kind == 0 && (data.NativeCoverageBits & 2) == 0)
                    throw Error("portable worker domain is unsupported or inconsistent");
                var key = DomainKey(row); if (previous != null && StringComparer.Ordinal.Compare(previous, key) >= 0) throw Error("portable worker domains are duplicated or unordered");
                previous = key; count = checked(count + row.Count);
            }
            if (count != data.WorkerCount) throw Error("portable worker census does not match its actual count");

        }
        internal static void RequireFence(Data data)
        {
            Validate(data);
            if (data.Capability != 1 || !data.PinnedAbi || !data.Closed || data.Failed || data.Fault != 0 || data.InFlight != 0)
                throw Error("portable observation does not prove a closed, drained and valid fence");
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            // These domains are not encoded by this helper. The native status
            // export cannot establish that any is absent, even with no workers.
            var gaps = new SortedSet<string>(StringComparer.Ordinal) {
                "audio-native-device-and-thread-state",
                "audio-fence-non-thread-producer-admission",
                "audio-fmod-channel-dsp-codec-and-sample-clock-state",
                "audio-output-device-ring-and-buffered-pcm-state"
            };
            if (data.Domains.Any(domain => domain.Kind == 6)) gaps.Add("audio-fmod-file-requests-and-read-cursors");
            if (data.WorkerCount != 0) gaps.Add("audio-native-worker-owner-and-callback-alias-state");
            if ((data.NativeCoverageBits & 2) != 0) gaps.Add("audio-fence-unknown-or-virtual-callback-domain");
            if (data.PendingStartups != 0) gaps.Add("audio-fence-pending-thread-startup-state");
            return gaps.ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        {
            RequireFence(data);
            var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (gaps.Length != 0) throw Error("coverage gaps: " + string.Join(";", gaps));
        }
        internal static byte[] EncodeS0(Data data)
        {
            // This is only the process admission projection. Explicit hard gaps
            // travel with it; encoding succeeds before eligibility is granted.
            RequireFence(data);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) {
                writer.Write(Magic); writer.Write(data.Version); writer.Write(data.NativeVersion); writer.Write(data.NativeStatusBytes);
                writer.Write(data.NativeWorkerBytes); writer.Write(data.NativeMaximumWorkers); writer.Write(data.Capability); writer.Write(data.PinnedAbi);
                writer.Write(data.Closed); writer.Write(data.Failed); writer.Write(data.Fault); writer.Write(data.InFlight); writer.Write(data.PendingStartups);
                writer.Write(data.NativeCoverageBits); writer.Write(data.WorkerCount); writer.Write(data.Domains.Length);
                foreach (var row in data.Domains) {
                    writer.Write(row.Kind); writer.Write(row.Name); writer.Write(row.Count); writer.Write(row.HasSystem); writer.Write(row.HasSystemCallback);
                    writer.Write(row.HasUserData); writer.Write(row.VirtualDispatch);
                }
                var gaps = RequiredCoverageGaps(data).Concat(data.CoverageGaps).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
                writer.Write(gaps.Length); foreach (var gap in gaps) writer.Write(gap); writer.Flush(); return stream.ToArray();
            }
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident native audio fence: " + message);
    }
}
