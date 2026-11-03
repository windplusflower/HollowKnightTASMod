using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            public bool NativeMainThread, CurrentIsMainDevice, MainIsRealDevice, ManagedPublicationHeld;
            // -1 is a foreign implementation, 0 is absent, otherwise image RVA.
            public int MainVTable, RealVTable;
            public bool Threaded, Serialize, GraphicsJob, HasWorker, HasQueue, ClientRealIsGlobal, PendingPresent;
            public int RecordDepth, OwnershipDepth, ReservedClientCount;
            public bool WorkerIsKnown, WorkerHasThread, WorkerOwnsDevice, WorkerRealIsGlobal, WorkerUsesMainQueue;
            public bool WorkerReachedClientFence;
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
        private const int ClientVTable = 0x166bad8, WorkerVTable = 0x17211d0;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool IsNativeMainThread();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CurrentGfxDevice();
        private static Producers CaptureProducers(Reader reader, ProcessModule engine)
        {
            var state = new Producers { NativeMainThread = Entry<IsNativeMainThread>(0x81c590, "40534883ec20488b1d3b2a2001e8ce080000483bc30f94c04883c4205bc3")() };
            if (!state.NativeMainThread) throw Error("producer observation requires Unity main thread");
            state.ManagedPublicationHeld = ResidentParticlePublicationLease.HasVerifiedPublicationFence();
            var globals = reader.Read(IntPtr.Add(engine.BaseAddress, 0x1a1f6b0), 16);
            var main = M.Ptr(globals, 0); var real = M.Ptr(globals, 8);
            var current = Entry<CurrentGfxDevice>(0x8869c0, "8b0d3a830e0148ff25037fd3")();
            state.MainIsRealDevice = main != IntPtr.Zero && main == real;
            state.CurrentIsMainDevice = main != IntPtr.Zero && main == current;
            int VTable(IntPtr owner) {
                if (owner == IntPtr.Zero) return 0;
                var rva = M.Ptr(reader.Read(owner, 8), 0).ToInt64() - engine.BaseAddress.ToInt64();
                return rva > 0 && rva < engine.ModuleMemorySize ? (int)rva : -1;
            }
            state.MainVTable = VTable(main); state.RealVTable = VTable(real);
            // GetGfxDeviceClientPool (4d5e30) returns an actual static object,
            // not a pointer slot. Count only; other clients can retire off-thread.
            state.ReservedClientCount = Bounded(M.U64(reader.Read(IntPtr.Add(engine.BaseAddress, 0x19bc980 + 40 + 16), 8), 0));
            if (state.MainVTable != ClientVTable) return state;
            var flags = reader.Read(IntPtr.Add(main, 9472), 32);
            var tail = reader.Read(IntPtr.Add(main, 10144), 17);
            var worker = M.Ptr(flags, 0); var queue = M.Ptr(flags, 24);
            state.Threaded = Boolean(flags, 16); state.Serialize = Boolean(flags, 17); state.GraphicsJob = Boolean(flags, 18);
            state.RecordDepth = M.I32(flags, 20); state.OwnershipDepth = M.I32(tail, 0); state.PendingPresent = Boolean(tail, 16);
            state.HasQueue = queue != IntPtr.Zero; state.HasWorker = worker != IntPtr.Zero;
            state.ClientRealIsGlobal = real != IntPtr.Zero && M.Ptr(flags, 8) == real;
            state.WorkerIsKnown = VTable(worker) == WorkerVTable;
            if (!state.WorkerIsKnown) return state;
            var workerBody = reader.Read(IntPtr.Add(worker, 80), 41);
            state.WorkerHasThread = M.Ptr(workerBody, 8) != IntPtr.Zero;
            state.WorkerRealIsGlobal = real != IntPtr.Zero && M.Ptr(workerBody, 0) == real;
            state.WorkerUsesMainQueue = queue != IntPtr.Zero && M.Ptr(workerBody, 16) == queue;
            state.WorkerOwnsDevice = Boolean(workerBody, 40);
            // Same signed wrap comparison as GfxDeviceWorker::WaitOnCPUFence.
            state.WorkerReachedClientFence = unchecked((int)(M.U32(tail, 4) - M.U32(workerBody, 32))) <= 0;
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
            var p = data.Registry.Producers;
            if (!p.NativeMainThread || !p.CurrentIsMainDevice || p.MainVTable <= 0 || p.RealVTable <= 0) gaps.Add("gfx-device-thread-identity");
            if (p.ReservedClientCount != 0 || p.GraphicsJob) gaps.Add("pooled-graphics-job-client");
            if (p.MainVTable == ClientVTable && (p.Threaded || p.HasWorker)) {
                if (!p.WorkerIsKnown || !p.WorkerUsesMainQueue || !p.WorkerRealIsGlobal || !p.ClientRealIsGlobal) gaps.Add("graphics-worker-dependency");
                if (p.WorkerOwnsDevice || !p.WorkerReachedClientFence || p.PendingPresent || p.Serialize) gaps.Add("graphics-command-producer-or-pending-fence");
            }
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
