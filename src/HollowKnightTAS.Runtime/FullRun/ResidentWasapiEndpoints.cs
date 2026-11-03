using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The system's current Output*, both embedded FMOD threads, and the
    // worker's effective callback/data must identify the same endpoint.
    internal static class ResidentWasapiEndpoints
    {
        internal sealed class Row
        {
            public uint SystemIndex;
            public ResidentWasapiMixerRing.Data Mixer = new ResidentWasapiMixerRing.Data();
        }

        internal sealed class Live
        {
            private readonly List<KeyValuePair<IntPtr,byte[]>> witness = new List<KeyValuePair<IntPtr,byte[]>>();
            private readonly Action require;
            internal readonly Row[] Rows;
            internal readonly Dictionary<uint,IntPtr> Owners = new Dictionary<uint,IntPtr>();
            internal readonly ResidentNativeAudioFenceState.Worker[] Workers;
            internal readonly Action Admission;

            internal Live(ResidentNativeAudioFenceState.Worker[] workers, Action require)
            {
                this.require = require; Admission = require; Workers = workers;
                require();
                var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                var manager = Pointer(IntPtr.Add(engine, 0x19d7a48));
                var managerSystem = manager == IntPtr.Zero ? IntPtr.Zero : Pointer(IntPtr.Add(manager, 328));
                var systems = new Dictionary<IntPtr,(uint index, IntPtr output)>();
                _ = ResidentFmodSystemState.Capture(engine,managerSystem,Read,(ptr,id,output) => systems.Add(ptr,(id,output)));
                var outputs = new Dictionary<IntPtr,HashSet<uint>>();
                foreach (var worker in workers.Where(w => w.Kind == 1 || w.Kind == 2 || w.Kind == 3)) {
                    var owner = new IntPtr(unchecked((long)worker.UserData));
                    var thread = new IntPtr(unchecked((long)worker.Thread));
                    var offset = worker.Kind == 1 ? 1176 : worker.Kind == 2 ? 1528 : 2472;
                    var callback = IntPtr.Add(engine, worker.Kind == 1 ? 0x1143ee0 : worker.Kind == 2 ? 0x11453f0 : 0x11464a0);
                    if (owner == IntPtr.Zero || IntPtr.Add(owner,offset) != thread || Pointer(IntPtr.Add(thread,288)) != owner ||
                        Pointer(IntPtr.Add(thread,320)) != callback || unchecked((ulong)callback.ToInt64()) != worker.Callback)
                        throw Error("worker does not match its native OutputWASAPI receiver and embedded thread");
                    if (!outputs.TryGetValue(owner,out var roles)) outputs.Add(owner, roles = new HashSet<uint>());
                    if (!roles.Add(worker.Kind)) throw Error("one output owns two workers for the same embedded slot");
                }
                var rows = new List<Row>();
                var ringStorage = new HashSet<IntPtr>();
                foreach (var output in outputs) {
                    var system = Pointer(IntPtr.Add(output.Key,96));
                    if (!systems.TryGetValue(system,out var info) || info.output != output.Key)
                        throw Error("WASAPI receiver is not its registered system's active output");
                    if (!ringStorage.Add(Pointer(IntPtr.Add(output.Key,1872)))) throw Error("distinct WASAPI outputs share a mixer buffer");
                    Owners.Add(info.index,output.Key);
                    rows.Add(new Row { SystemIndex = info.index, Mixer = ResidentWasapiMixerRing.Capture(output.Key,
                        output.Value.Contains(1),output.Value.Contains(2),Read) });
                }
                Rows = rows.OrderBy(row => row.SystemIndex).ToArray();
                Validate(Rows);
                RequireUnchanged();
                ResidentFailureEvidence.Mark("WASAPI mixer rings=" + Rows.Length + ", PCM bytes=" +
                    string.Join(",",Rows.Select(row => row.Mixer.Bytes.Length.ToString())));
            }

            private byte[] Read(IntPtr ptr,int count)
            {
                var bytes = M.Read(ptr,count);
                witness.Add(new KeyValuePair<IntPtr,byte[]>(ptr,bytes));
                return bytes;
            }
            private IntPtr Pointer(IntPtr ptr) => M.Ptr(Read(ptr,8),0);
            internal void RequireUnchanged()
            {
                require();
                foreach (var read in witness)
                    if (!M.Read(read.Key,read.Value.Length).SequenceEqual(read.Value))
                        throw Error("FMOD output ownership, ring bytes or cursor changed while the epoch was held");
                require();
            }
        }

        internal static void Validate(Row[] rows)
        {
            if (rows == null || rows.Length > ResidentNativeAudioFenceState.MaximumWorkers)
                throw Error("WASAPI output directory exceeds bounds");
            uint? prior = null;
            foreach (var row in rows) {
                if (row == null || row.Mixer == null || prior.HasValue && row.SystemIndex <= prior.Value)
                    throw Error("WASAPI system keys are duplicated or out of order");
                ResidentWasapiMixerRing.Validate(row.Mixer);
                prior = row.SystemIndex;
            }
        }
        internal static void RequireWorkerCensus(Row[] rows, ResidentNativeAudioFenceState.Data fence)
        {
            Validate(rows);
            if (rows.Count(row => row.Mixer.HasFeeder) != fence.Domains.Where(domain => domain.Kind == 1).Sum(domain => domain.Count)
                || rows.Count(row => row.Mixer.HasMixer) != fence.Domains.Where(domain => domain.Kind == 2).Sum(domain => domain.Count))
                throw Error("WASAPI workers and observed output slots disagree");
        }

        internal sealed class Plan
        {
            private readonly Row[] source;
            private readonly Live target;
            private readonly ResidentMutationScope scope;
            private readonly Action requireAudio;
            private bool applied;

            internal Plan(Row[] source, Live target, ResidentMutationScope scope, Action requireAudio)
            {
                this.source = source; this.target = target; this.scope = scope; this.requireAudio = requireAudio;
                Validate(source); Validate(target.Rows);
                scope.Require(); requireAudio(); target.RequireUnchanged();
                RequireLayout(source,target.Rows);
            }

            internal void Apply()
            {
                if (applied) throw Error("mixer ring plan is single use");
                scope.Require(); requireAudio(); target.RequireUnchanged();
                try {
                    foreach (var row in source) {
                        var owner = target.Owners[row.SystemIndex];
                        var buffer = M.Ptr(IntPtr.Add(owner,1872));
                        var data = row.Mixer;
                        requireAudio(); M.Write(buffer, data.Bytes);
                        var cursors = new byte[8];
                        Buffer.BlockCopy(BitConverter.GetBytes(data.ReadFrame),0,cursors,0,4);
                        Buffer.BlockCopy(BitConverter.GetBytes(data.WriteFrame),0,cursors,4,4);
                        M.Write(IntPtr.Add(owner,1892),cursors);
                    }
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("mixer ring verification precedes apply");
                scope.Require(); requireAudio();
                var fresh = new Live(target.Workers,target.Admission);
                RequireLayout(source,fresh.Rows);
                for (var i=0; i<source.Length; i++) {
                    var row = source[i]; var actual = fresh.Rows[i].Mixer;
                    if (target.Owners[row.SystemIndex] != fresh.Owners[row.SystemIndex] ||
                        actual.ReadFrame != row.Mixer.ReadFrame || actual.WriteFrame != row.Mixer.WriteFrame ||
                        !actual.Bytes.SequenceEqual(row.Mixer.Bytes)) throw Error("independent mixer ring readback differs");
                }
            }
        }

        internal static void RequireLayout(Row[] source,Row[] target)
        {
            Validate(source); Validate(target);
            if (source.Length != target.Length) throw Error("WASAPI endpoint count differs");
            for (int i=0; i<source.Length; i++) {
                var a = source[i].Mixer; var b = target[i].Mixer;
                if (source[i].SystemIndex != target[i].SystemIndex || a.RenderFrames != b.RenderFrames ||
                    a.BlockAlign != b.BlockAlign || a.Format != b.Format || a.Channels != b.Channels ||
                    a.RingFrames != b.RingFrames || a.BlockFrames != b.BlockFrames || a.BlockCount != b.BlockCount ||
                    a.HasClient != b.HasClient || a.HasRenderClient != b.HasRenderClient ||
                    a.HasFeeder != b.HasFeeder || a.HasMixer != b.HasMixer)
                    throw Error("WASAPI endpoint binding or native PCM layout differs");
            }
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident WASAPI: " + message);
    }
}
