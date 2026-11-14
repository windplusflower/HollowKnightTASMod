using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;
using Curve = HollowKnightTAS.Runtime.FullRun.ResidentManagedNativeWrappers.CurveData;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Exact AudioSource/AudioListener layouts in pinned Unity. Playback state is
    // D: Stop/Pause/Play and AddToManager are not canonicalization operations.
    // AudioSource::AddToManager can PlayOnAwake, and RemoveFromManager cleans
    // channels. Registration is reconstructed from the actual intrusive nodes.
    // Only the lists registering resident components belong to the snapshot.
    internal static partial class ResidentAudioState
    {
        private const int Maximum = 100000;
        internal sealed class Data
        {
            public int Version = 5;
            public Source[] Sources = Array.Empty<Source>();
            public Listener[] Listeners = Array.Empty<Listener>();
            public Registry Manager = new Registry();
            public string[] CoverageGaps = Array.Empty<string>();
            public VoiceState[] Voices = Array.Empty<VoiceState>();
        }
        internal sealed class Source
        {
            public string Owner = "", Clip = "", Mixer = "", ScheduledSource = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution, Registered, Scheduled;
            public byte[][] Scalars = Array.Empty<byte[]>();
            public Curve[] Curves = Array.Empty<Curve>();
            public Channel Main = new Channel();
            public Channel[] Cached = Array.Empty<Channel>(), Providers = Array.Empty<Channel>(), OneShots = Array.Empty<Channel>();
            public int CachedCapacity, ProviderCapacity;
            public bool[] NativeResources = new bool[7];
        }
        internal sealed class Listener
        {
            public string Owner = "", AlternateTransform = "";
            public ResidentObjectDirectory.TypeRecord Type = new ResidentObjectDirectory.TypeRecord();
            public bool Execution, Registered;
            public byte[][] Scalars = Array.Empty<byte[]>();
        }
        internal sealed class Channel
        {
            // WeakPtr storage itself is a native retain even when its instance
            // has expired. Neither case may be flattened to a zero pointer.
            public bool WeakOwner, LiveInstance;
            public string Alias = "";
        }
        internal sealed class Registry
        {
            public bool Present;
            public string[] Sources = Array.Empty<string>(), Listeners = Array.Empty<string>(), Scheduled = Array.Empty<string>();
        }
        private sealed class Range
        {
            internal readonly int Offset, Count;
            internal Range(int offset, int count) { Offset = offset; Count = count; }
        }
        private static readonly Range[] SourceRanges = {
            new Range(56, 2), new Range(480, 40), new Range(520, 233), new Range(756, 4), new Range(808, 1),
            new Range(888, 8), new Range(912, 6), new Range(920, 4), new Range(928, 14), new Range(1008, 1)
        };
        private static readonly Range[] ListenerRanges = { new Range(56, 2), new Range(64, 28), new Range(96, 64), new Range(184, 1) };
        private static readonly int[] SourceResources = { 816, 896, 904, 944, 952, 960, 968 };
        private static readonly string[] ResourceNames = { "dsp-connection", "dry-channel-group", "wet-channel-group", "custom-filter", "spatializer-dsp", "spatializer-data", "ambisonic-data" };
        private sealed class Reader
        {
            internal readonly ResidentObjectDirectory Directory;
            internal readonly Func<object, string?> Bind;
            private readonly Dictionary<IntPtr, byte[]> frozen = new Dictionary<IntPtr, byte[]>();
            private readonly Dictionary<IntPtr, string> channelAliases = new Dictionary<IntPtr, string>();
            internal IReadOnlyDictionary<IntPtr,string> DiagnosticAliases => channelAliases;
            internal Action VerifyVoicePCM = () => { };
            internal readonly Dictionary<string,ResidentAudioBackendControl.Data> BackendReceipts = new Dictionary<string,ResidentAudioBackendControl.Data>(StringComparer.Ordinal);
            internal readonly Dictionary<string,string> BackendFailures = new Dictionary<string,string>(StringComparer.Ordinal);
            internal readonly Dictionary<string,ResidentAudioBackendControl.DelayDiagnostic> BackendDelays = new Dictionary<string,ResidentAudioBackendControl.DelayDiagnostic>(StringComparer.Ordinal);
            internal Reader(ResidentObjectDirectory directory, Func<object, string?> bind) { Directory = directory; Bind = bind; }
            internal byte[] Read(IntPtr pointer, int count)
            {
                var bytes = M.Read(pointer, count);
                if (count != 0) {
                    if (frozen.TryGetValue(pointer, out var previous)) {
                        var common = Math.Min(count, previous.Length);
                        if (!previous.Take(common).SequenceEqual(bytes.Take(common))) throw Error("native audio changed during capture");
                        if (count > previous.Length) frozen[pointer] = bytes;
                    } else frozen.Add(pointer, bytes);
                }
                return bytes;
            }
            internal void RequireFrozen()
            {
                foreach (var pair in frozen) if (!M.Read(pair.Key, pair.Value.Length).SequenceEqual(pair.Value)) throw Error("native audio changed before capture completed");
                VerifyVoicePCM();
            }
            internal string Id<T>(int id) where T : NativeObject
            {
                if (id == 0) return "";
                return ResidentNativeAssetReference.BindId(id,typeof(T),Bind);
            }
            internal string Pointer<T>(IntPtr pointer) where T : NativeObject
            {
                if (pointer == IntPtr.Zero) return "";
                return ResidentNativeAssetReference.BindPointer(pointer,typeof(T),Bind);
            }
            internal Channel Channel(IntPtr pointer, string firstUse)
            {
                if (pointer == IntPtr.Zero) return new Channel();
                var weak = Read(pointer, 24);
                if (!channelAliases.TryGetValue(pointer, out var alias)) channelAliases.Add(pointer, alias = "audio-channel/" + firstUse);
                return new Channel { WeakOwner = true, LiveInstance = M.Ptr(weak, 16) != IntPtr.Zero, Alias = alias };
            }
        }
        internal static Data Capture(ResidentObjectDirectory directory, Func<object, string?> bind)
        {
            if (directory == null || bind == null) throw new ArgumentNullException();
            LoadingEngineClockProbe.RequirePinnedEngine(); var reader = new Reader(directory, bind); var data = new Data();
            data.Sources = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(AudioSource)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CaptureSource(reader, pair.Key, (AudioSource)pair.Value)).ToArray();
            data.Listeners = directory.NativeBindings.Where(pair => pair.Value.GetType() == typeof(AudioListener)).OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => CaptureListener(reader, pair.Key, (AudioListener)pair.Value)).ToArray();
            data.Manager = CaptureRegistry(reader, data);
            try { data.Voices = CaptureVoices(reader,data); }
            catch { RecordFailedVoiceDiagnostics(reader,data); throw; }
            data.CoverageGaps = RequiredCoverageGaps(data);
            if (ResidentFailureEvidence.Collecting && data.CoverageGaps.Length != 0) {
                try { RecordChannelDiagnostics(reader,data); }
                catch (Exception error) { ResidentFailureEvidence.Mark("audio channel diagnostic failure "+error.GetType().Name); }
            }
            Validate(data);
            try { reader.RequireFrozen(); }
            catch { RecordFailedVoiceDiagnostics(reader,data); throw; }
            return data;
        }
        private static void RecordFailedVoiceDiagnostics(Reader reader,Data data)
            => CollectFailedVoiceDiagnostics(ResidentFailureEvidence.Collecting,()=>RecordChannelDiagnostics(reader,data),ResidentFailureEvidence.Mark);
        private static void CollectFailedVoiceDiagnostics(bool collecting,Action collect,Action<string> mark)
        {
            if (!collecting) return;
            try { collect(); }
            catch (Exception error) {
                try { mark("audio failed-capture diagnostic failure "+error.GetType().Name); }
                catch (Exception) { /* Evidence cannot replace the capture fault. */ }
            }
        }
        private static Source CaptureSource(Reader reader, string key, AudioSource owner)
        {
            var native = M.Owner(owner); var bytes = reader.Read(native, 1016);
            var row = new Source { Owner = key, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()), Execution = reader.Directory.Ownership.IsExecution(owner),
                Scalars = CaptureRanges(bytes, SourceRanges), Clip = reader.Id<AudioClip>(M.I32(bytes, 824)), Mixer = reader.Id<UnityEngine.Audio.AudioMixerGroup>(M.I32(bytes, 828)),
                ScheduledSource = reader.Pointer<AudioSource>(M.Ptr(bytes, 880)), Registered = Linked(reader, native, bytes, 832, true), Scheduled = Linked(reader, native, bytes, 864, false),
                Curves = Enumerable.Range(0, 4).Select(index => ResidentManagedNativeWrappers.CaptureCurveNative(IntPtr.Add(native, 64 + index * 104))).ToArray(),
                NativeResources = SourceResources.Select(offset => M.Ptr(bytes, offset) != IntPtr.Zero).ToArray(), Main = reader.Channel(M.Ptr(bytes, 856), key + "/main") };
            row.Cached = Channels(reader, IntPtr.Add(native, 760), key + "/cached", out row.CachedCapacity);
            row.Providers = Channels(reader, IntPtr.Add(native, 976), key + "/provider", out row.ProviderCapacity);
            row.OneShots = Nodes(reader, IntPtr.Add(native, 792), 32).Select((value, index) => {
                if (M.Ptr(value.Bytes, 16) != value.Pointer) throw Error("one-shot native node does not own its actual data");
                return reader.Channel(M.Ptr(value.Bytes, 24), key + "/one-shot/" + index);
            }).ToArray(); return row;
        }
        private static Listener CaptureListener(Reader reader, string key, AudioListener owner)
        {
            var native = M.Owner(owner); var bytes = reader.Read(native, 192);
            return new Listener { Owner = key, Type = ResidentObjectDirectory.TypeRecord.Capture(owner.GetType()), Execution = reader.Directory.Ownership.IsExecution(owner),
                Scalars = CaptureRanges(bytes, ListenerRanges), AlternateTransform = reader.Id<Transform>(M.I32(bytes, 92)), Registered = Linked(reader, native, bytes, 160, true) };
        }
        private static byte[][] CaptureRanges(byte[] bytes, Range[] ranges) => ranges.Select(range => M.Slice(bytes, range.Offset, range.Count)).ToArray();
        private static bool Linked(Reader reader, IntPtr owner, byte[] bytes, int offset, bool selfData)
        {
            var previous = M.Ptr(bytes, offset); var next = M.Ptr(bytes, offset + 8); var node = IntPtr.Add(owner, offset);
            if ((previous == IntPtr.Zero) != (next == IntPtr.Zero) || selfData && M.Ptr(bytes, offset + 16) != owner) throw Error("audio intrusive owner/registration shape differs");
            if (previous != IntPtr.Zero && (M.Ptr(reader.Read(IntPtr.Add(previous, 8), 8), 0) != node || M.Ptr(reader.Read(next, 8), 0) != node))
                throw Error("audio intrusive registration is not reciprocal");
            return previous != IntPtr.Zero;
        }
        private sealed class Node
        {
            internal IntPtr Pointer;
            internal byte[] Bytes = Array.Empty<byte>();
        }
        private static Node[] Nodes(Reader reader, IntPtr sentinel, int size)
        {
            var head = reader.Read(sentinel, 16); var previous = sentinel; var current = M.Ptr(head, 8); var seen = new HashSet<IntPtr>(); var values = new List<Node>();
            while (current != sentinel) {
                if (current == IntPtr.Zero || !seen.Add(current) || seen.Count > Maximum) throw Error("audio list is cyclic, invalid or exceeds bounds");
                var bytes = reader.Read(current, size); if (M.Ptr(bytes, 0) != previous) throw Error("audio native list back-link differs");
                values.Add(new Node { Pointer = current, Bytes = bytes }); previous = current; current = M.Ptr(bytes, 8);
            }
            if (M.Ptr(head, 0) != previous) throw Error("audio list tail differs"); return values.ToArray();
        }
        private static Channel[] Channels(Reader reader, IntPtr storage, string key, out int capacity)
        {
            var header = reader.Read(storage, 32); var size = M.U64(header, 16); var count = M.U64(header, 24) >> 1;
            if (size > count || count > Maximum) throw Error("audio channel array exceeds bounds"); capacity = (int)count;
            var bytes = reader.Read(M.Ptr(header, 0), checked((int)size * 8));
            return Enumerable.Range(0, (int)size).Select(index => reader.Channel(M.Ptr(bytes, index * 8), key + "/" + index)).ToArray();
        }
        private static IntPtr ManagerPointer()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine(); const string code = "4863c1488d0d264d4601488b04c1c3";
            if (Hex(M.Read(IntPtr.Add(engine.BaseAddress, 0x572d00), code.Length / 2)) != code) throw Error("native manager context ABI differs");
            // GetAudioManager is GetManagerFromContext(3), a read-only table
            // lookup. Capture must not invoke a lazy getter or initialize audio.
            return M.Ptr(IntPtr.Add(engine.BaseAddress, 0x19d7a48));
        }
        private static Registry CaptureRegistry(Reader reader, Data data)
        {
            var native = ManagerPointer();
            if (native == IntPtr.Zero) return new Registry();
            var sourceNodes = data.Sources.ToDictionary(row => IntPtr.Add(M.Owner(reader.Directory.Resolve(row.Owner)), 832), row => row.Owner);
            var listenerNodes = data.Listeners.ToDictionary(row => IntPtr.Add(M.Owner(reader.Directory.Resolve(row.Owner)), 160), row => row.Owner);
            var scheduledNodes = data.Sources.ToDictionary(row => IntPtr.Add(M.Owner(reader.Directory.Resolve(row.Owner)), 864), row => row.Owner);
            string[] List(int offset, Dictionary<IntPtr, string> known, int ownerOffset) => Nodes(reader, IntPtr.Add(native, offset), 24).Select(node => {
                if (!known.TryGetValue(node.Pointer, out var owner) || M.Ptr(node.Bytes, 16) != M.Owner(reader.Directory.Resolve(owner)))
                    throw Error("audio manager list has an owner outside the complete native directory");
                if (node.Pointer != IntPtr.Add(M.Owner(reader.Directory.Resolve(owner)), ownerOffset)) throw Error("audio registry node offset differs"); return owner;
            }).ToArray();
            return new Registry { Present = true, Sources = List(144, sourceNodes, 832),
                Listeners = List(160, listenerNodes, 160), Scheduled = List(176, scheduledNodes, 864) };
        }
        internal static string[] RequiredCoverageGaps(Data data)
        {
            var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in data.Sources) {
                if (row.Execution) gaps.Add("audio-source-execution-domain");
                if (row.Curves.Any(curve => curve.Capacity != curve.KeyCount)) gaps.Add("audio-custom-curve-capacity-requires-common-canon");
                if (row.Curves.Any(curve => curve.KeyStorageFlags != (curve.KeyCount == 0 ? 1 : 0) || curve.StoragePadding.Any(value => value != 0)))
                    gaps.Add("audio-custom-curve-storage-requires-common-canon");
                if (new[] { row.Main }.Concat(row.Cached).Concat(row.Providers).Concat(row.OneShots).Any(channel => channel.WeakOwner && data.Version < 4)) gaps.Add("audio-native-channel-instance-graph");
                if (row.OneShots.Length != 0 && data.Version < 4) gaps.Add("audio-one-shot-native-owner-list");
                for (var index = 0; index < row.NativeResources.Length; index++) {
                    if (index == 1 || index == 2) continue;
                    if (row.NativeResources[index]) gaps.Add("audio-" + ResourceNames[index]);
                }
            }
            if (data.Listeners.Any(row => row.Execution)) gaps.Add("audio-listener-execution-domain");
            foreach (var gap in VoiceCoverageGaps(data)) gaps.Add(gap);
            return gaps.ToArray();
        }
        internal static void RequireCompleteCoverage(Data data)
        { Validate(data); RejectGaps(RequiredCoverageGaps(data).Concat(data.CoverageGaps)); }
        internal static void RequireCanonicalizableCoverage(Data data)
        {
            Validate(data);
            foreach (var row in data.Sources) foreach (var curve in row.Curves) ResidentManagedNativeWrappers.RequireCanonicalizableCurveStorage(curve);
            RejectGaps(RequiredCoverageGaps(data).Concat(data.CoverageGaps).Where(gap => gap != "audio-custom-curve-capacity-requires-common-canon" && gap != "audio-custom-curve-storage-requires-common-canon"));
        }
        internal static void RequireCanonicalState(Data data)
        {
            RequireCompleteCoverage(data);
            foreach (var row in data.Sources) foreach (var curve in row.Curves) ResidentManagedNativeWrappers.RequireCanonicalCurveStorage(curve);
        }
        private static void RejectGaps(IEnumerable<string> values)
        { var gaps = values.Distinct(StringComparer.Ordinal).ToArray(); if (gaps.Length != 0) throw Error("native coverage is incomplete: " + string.Join(",", gaps)); }
        internal static void Validate(Data data)
        {
            if (data == null || (data.Version != 3 && data.Version != 4 && data.Version != 5) || data.Sources == null || data.Listeners == null || data.Manager == null || data.CoverageGaps == null
                || data.Sources.Length + data.Listeners.Length > Maximum || data.CoverageGaps.Length > Maximum || data.CoverageGaps.Any(value => !Text(value, false))) throw Error("audio snapshot shape differs");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            void Identity(string owner, ResidentObjectDirectory.TypeRecord type, string name) {
                if (!Text(owner, false) || !keys.Add(owner) || type == null || type.Name != name) throw Error("audio actual owner/type differs"); type.Validate();
            }
            foreach (var row in data.Sources) {
                if (row == null) throw Error("audio source row is absent"); Identity(row.Owner, row.Type, "UnityEngine.AudioSource"); Ranges(row.Scalars, SourceRanges);
                if (!Text(row.Clip) || !Text(row.Mixer) || !Text(row.ScheduledSource) || row.Curves == null || row.Curves.Length != 4 || row.NativeResources == null || row.NativeResources.Length != 7
                    || row.Cached == null || row.Providers == null || row.OneShots == null || row.CachedCapacity < row.Cached.Length || row.CachedCapacity > Maximum
                    || row.ProviderCapacity < row.Providers.Length || row.ProviderCapacity > Maximum || row.OneShots.Length > Maximum) throw Error("audio source retained domain differs");
                foreach (var curve in row.Curves) ResidentManagedNativeWrappers.ValidateCurve(curve);
                foreach (var channel in new[] { row.Main }.Concat(row.Cached).Concat(row.Providers).Concat(row.OneShots))
                    if (channel == null || !Text(channel.Alias) || channel.LiveInstance && !channel.WeakOwner || channel.WeakOwner != (channel.Alias.Length != 0)) throw Error("audio native channel identity differs");
                if (row.Scheduled && row.ScheduledSource != row.Owner || row.ScheduledSource.Length != 0 && !data.Sources.Any(value => value.Owner == row.ScheduledSource))
                    throw Error("audio scheduled node has an unknown source");
                if (row.Scalars[0].Concat(row.Scalars[1].Skip(32)).Concat(row.Scalars[2].Skip(232)).Concat(row.Scalars[4])
                    .Concat(row.Scalars[6]).Concat(row.Scalars[8].Skip(12)).Concat(row.Scalars[9]).Any(value => value > 1)) throw Error("audio source native boolean differs");
            }
            foreach (var row in data.Listeners) {
                if (row == null) throw Error("audio listener row is absent"); Identity(row.Owner, row.Type, "UnityEngine.AudioListener"); Ranges(row.Scalars, ListenerRanges);
                if (!Text(row.AlternateTransform) || row.Scalars[0].Concat(row.Scalars[3]).Any(value => value > 1)) throw Error("audio listener binding/boolean differs");
            }
            foreach (var owners in new[] { data.Sources.Select(row => row.Owner), data.Listeners.Select(row => row.Owner) })
                if (!owners.SequenceEqual(owners.OrderBy(value => value, StringComparer.Ordinal))) throw Error("audio owner directory is not ordered");
            ValidateRegistry(data);
            ValidateVoices(data);
        }
        private static void ValidateRegistry(Data data)
        {
            var manager = data.Manager;
            if (manager.Sources == null || manager.Listeners == null || manager.Scheduled == null) throw Error("audio registry shape differs");
            foreach (var list in new[] { manager.Sources, manager.Listeners, manager.Scheduled })
                if (list.Length > Maximum || list.Any(value => !Text(value, false)) || list.Distinct(StringComparer.Ordinal).Count() != list.Length) throw Error("audio registry repeats or omits an owner");
            if (!new HashSet<string>(data.Sources.Where(row => row.Registered).Select(row => row.Owner), StringComparer.Ordinal).SetEquals(manager.Sources)
                || !new HashSet<string>(data.Listeners.Where(row => row.Registered).Select(row => row.Owner), StringComparer.Ordinal).SetEquals(manager.Listeners)
                || !new HashSet<string>(data.Sources.Where(row => row.Scheduled).Select(row => row.Owner), StringComparer.Ordinal).SetEquals(manager.Scheduled))
                throw Error("audio registration list disagrees with component native nodes");
            if (!manager.Present && (manager.Sources.Length + manager.Listeners.Length + manager.Scheduled.Length != 0)) throw Error("absent audio manager has retained native state");
        }
        private static void Ranges(byte[][] values, Range[] ranges)
        { if (values == null || values.Length != ranges.Length || values.Where((value, index) => value == null || value.Length != ranges[index].Count).Any()) throw Error("audio native field layout differs"); }
        private static bool Text(string value, bool empty = true) => value != null && value.Length <= 262144 && (empty || value.Length != 0);
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(ProjectDeterministic(expected)) != Json(ProjectDeterministic(actual))) throw Error("native audio independent readback differs"); }
        private static object ProjectDeterministic(Data data)
        {
            return new {
                data.Version,
                data.CoverageGaps,
                Manager = new {
                    data.Manager.Present,
                    data.Manager.Sources,
                    data.Manager.Listeners,
                    data.Manager.Scheduled
                },
                Listeners = data.Listeners.Select(l => new {
                    l.Owner,
                    l.AlternateTransform,
                    l.Type.Assembly, l.Type.Module, l.Type.Name, l.Type.Token,
                    l.Execution,
                    l.Registered,
                    l.Scalars
                }),
                Sources = data.Sources.Select(s => new {
                    s.Owner,
                    s.Clip,
                    s.Mixer,
                    s.ScheduledSource,
                    s.Type.Assembly, s.Type.Module, s.Type.Name, s.Type.Token,
                    s.Execution,
                    s.Registered,
                    s.Scheduled,
                    s.Scalars,
                    s.Curves,
                    s.CachedCapacity,
                    s.ProviderCapacity,
                    NativeResources = new bool[] {
                        s.NativeResources[0],
                        false,
                        false,
                        s.NativeResources[3],
                        s.NativeResources[4],
                        s.NativeResources[5],
                        s.NativeResources[6]
                    },
                    Main = s.Main != null && s.Main.WeakOwner ? new { s.Main.WeakOwner, s.Main.Alias } : null,
                    Cached = s.Cached.Select(c => c.WeakOwner ? new { c.WeakOwner, c.Alias } : null),
                    Providers = s.Providers.Select(c => c.WeakOwner ? new { c.WeakOwner, c.Alias } : null),
                    OneShots = s.OneShots.Select(c => c.WeakOwner ? new { c.WeakOwner, c.Alias } : null)
                }),
                Voices = data.Voices.Select(v => new { v.Alias })
            };
        }
        private static string Json(object value) => Newtonsoft.Json.JsonConvert.SerializeObject(value);
        private static Data Copy(Data value) => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Json(value)) ?? throw Error("audio snapshot copy is absent");
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident audio state: " + reason);
    }
}
