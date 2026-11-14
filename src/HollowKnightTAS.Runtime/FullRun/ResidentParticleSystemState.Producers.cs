using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentParticleSystemState
    {
        internal sealed class Fence
        {
            // Group is only a snapshot-local alias. The underlying JobGroup is
            // asynchronously retired; its address must not be followed or sent.
            public int Group;
            public uint Version;
        }
        internal sealed class Producers
        {
            public bool NativeMainThread, ManagedPublicationHeld;
            // Legacy wire observations remain readable. New captures leave
            // graphics-backend receipts at their neutral defaults.
            public bool CurrentIsMainDevice = false, MainIsRealDevice = false;
            public int MainVTable = 0, RealVTable = 0;
            public bool Threaded = false, Serialize = false, GraphicsJob = false, HasWorker = false,
                HasQueue = false, ClientRealIsGlobal = false, PendingPresent = false;
            public int RecordDepth = 0, OwnershipDepth = 0, ReservedClientCount = 0;
            public bool WorkerIsKnown = false, WorkerHasThread = false, WorkerOwnsDevice = false,
                WorkerRealIsGlobal = false, WorkerUsesMainQueue = false, WorkerReachedClientFence = false;
        }
        private sealed partial class Reader
        {
            private readonly Dictionary<IntPtr, int> jobGroups = new Dictionary<IntPtr, int>();
            internal int Group(IntPtr group)
            {
                if (group == IntPtr.Zero) return 0;
                if (!jobGroups.TryGetValue(group, out var id)) jobGroups.Add(group, id = jobGroups.Count + 1);
                return id;
            }
        }
        private const int ClientVTable = 0x166bad8;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsNativeMainThread();
        private static Producers CaptureProducers()
        {
            var state = new Producers { NativeMainThread = Entry<IsNativeMainThread>(0x81c590, "40534883ec20488b1d3b2a2001e8ce080000483bc30f94c04883c4205bc3")() };
            if (!state.NativeMainThread) throw Error("producer observation requires Unity main thread");
            state.ManagedPublicationHeld = ResidentParticlePublicationLease.HasVerifiedPublicationFence();
            // Device ownership, presents and graphics client queues belong to
            // this process's rendering backend. Actual emitter update fences
            // and manager geometry jobs are captured separately below.
            return state;
        }
        // Pure wire validation can inspect a historical snapshot offline. Any
        // admission decision that trusts its publication bit must independently
        // verify the current process's bound native epoch.
        private static void RequireLivePublication(Data data)
        {
            if (data.Registry.Producers.ManagedPublicationHeld && !ResidentParticlePublicationLease.HasVerifiedPublicationFence())
                throw Error("claimed managed publication coverage has no live joint fence");
        }
        private static Fence[] CaptureFences(Reader reader, IntPtr top)
        {
            var seen = new HashSet<IntPtr>(); var values = new List<Fence>();
            while (top != IntPtr.Zero) {
                if (values.Count >= 100000 || !seen.Add(top)) throw Error("render job-fence chain exceeds bounds or cycles");
                // OnGeometryJobsScheduledRenderThread (1b97e0) stores the actual
                // JobFence at +32 and AtomicStack::Push publishes the node at +0.
                var node = reader.Read(top, 44);
                values.Add(new Fence { Group = reader.Group(M.Ptr(node, 32)), Version = M.U32(node, 40) });
                top = M.Ptr(node, 0);
            }
            return values.ToArray();
        }
        private static void ProducerGaps(Data data, SortedSet<string> gaps)
        {
            if (!data.Registry.Present) return;
            if (data.Rows.Length != 0) {
                // ScheduleManagedJob_Injected schedules before the independent
                // SetManagedJobHandle_Injected publishes m_UpdateFence at +112.
                if (!data.Registry.Producers.ManagedPublicationHeld) gaps.Add("managed-job-schedule-to-handle-publication");
                if (data.Rows.Any(row => row.HasJob)) gaps.Add("job-group-graph-and-work-item");
            }
            if (!data.Registry.Producers.NativeMainThread) gaps.Add("particle-main-thread-admission");
        }
        private static void ValidateProducers(Data data)
        {
            var manager = data.Registry;
            var p = manager.Producers;
            if (p == null || manager.Fences == null || manager.Fences.Length > 100000 || (manager.Fences.Length != 0) != manager.PendingJobs
                || p.ReservedClientCount < 0 || p.RecordDepth < 0 || p.OwnershipDepth < 0 || p.MainVTable < -1 || p.RealVTable < -1)
                throw Error("manager producer receipt differs");
            if (p.ManagedPublicationHeld && (!p.NativeMainThread || !manager.Present)) throw Error("managed publication proof lacks the active main-thread context");
            if (!manager.Present && (p.NativeMainThread || p.ReservedClientCount != 0 || p.MainVTable != 0 || p.RealVTable != 0)
                || p.MainIsRealDevice && (p.MainVTable == 0 || p.MainVTable != p.RealVTable)
                || p.CurrentIsMainDevice && p.MainVTable == 0) throw Error("producer device lifetime differs");
            if (p.MainVTable != ClientVTable && (p.Threaded || p.Serialize || p.GraphicsJob || p.HasWorker || p.HasQueue || p.ClientRealIsGlobal
                || p.PendingPresent || p.RecordDepth != 0 || p.OwnershipDepth != 0)) throw Error("producer receipt uses the wrong client layout");
            if (p.WorkerIsKnown && !p.HasWorker || !p.WorkerIsKnown && (p.WorkerHasThread || p.WorkerOwnsDevice || p.WorkerRealIsGlobal || p.WorkerUsesMainQueue || p.WorkerReachedClientFence))
                throw Error("producer receipt uses the wrong worker layout");
            var seen = new HashSet<int>();
            void Group(int id) {
                if (id < 0 || id > 0 && seen.Add(id) && id != seen.Count) throw Error("job-group alias first-use order differs");
            }
            foreach (var fence in manager.Fences) { if (fence == null) throw Error("manager fence is absent"); Group(fence.Group); }
            foreach (var row in data.Rows) {
                if (row.HasJob != (row.JobGroup != 0)) throw Error("emitter update fence omits its job-group identity");
                Group(row.JobGroup);
            }
        }
    }
}
