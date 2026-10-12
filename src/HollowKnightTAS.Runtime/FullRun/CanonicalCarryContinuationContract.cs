using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read the proposed source fields of bound roots. Their current live
    // fields still belong to preparation and cannot validate a saved world.
    internal static class CanonicalCarryContinuationContract
    {
        internal sealed class Prepared
        {
            internal CanonicalInputEpoch Epoch = null!;
            internal CanonicalClockCarry Clock = null!;
            internal CanonicalSchedulerCarry Scheduler = null!;
            internal CanonicalSceneLoadCarry.Data SceneLoad = null!;
        }
        internal static Prepared Read(CanonicalCarryGraphCodec.PreparedView view)
        {
            var epochRoot = view.Root("D/InputEpoch"); var epochType = typeof(CanonicalInputEpoch);
            object? Epoch(string name) => view.Field(epochRoot, epochType, name);
            var epoch = new CanonicalInputEpoch {
                Version = (int)Epoch(nameof(CanonicalInputEpoch.Version))!, OriginNative = (long?)Epoch(nameof(CanonicalInputEpoch.OriginNative)),
                SourceNative = (long)Epoch(nameof(CanonicalInputEpoch.SourceNative))!, CurrentTick = (ulong)Epoch(nameof(CanonicalInputEpoch.CurrentTick))!,
                SchedulerTimestamp = (uint)Epoch(nameof(CanonicalInputEpoch.SchedulerTimestamp))!,
                ControlOriginBound = (bool)Epoch(nameof(CanonicalInputEpoch.ControlOriginBound))!, OriginTick = (ulong)Epoch(nameof(CanonicalInputEpoch.OriginTick))!,
                OriginSchedulerTimestamp = (uint)Epoch(nameof(CanonicalInputEpoch.OriginSchedulerTimestamp))!,
                InitialTimeBits = (int)Epoch(nameof(CanonicalInputEpoch.InitialTimeBits))!, CurrentTimeBits = (int)Epoch(nameof(CanonicalInputEpoch.CurrentTimeBits))!,
                LastUpdateTimeBits = (int)Epoch(nameof(CanonicalInputEpoch.LastUpdateTimeBits))! };
            epoch.Validate(epoch.SourceNative); epoch.RequireControlOrigin();
            var clockRoot = view.Root("D/Clock");
            byte[] Clock(string name) => (byte[])((byte[])view.Field(clockRoot, typeof(CanonicalClockCarry), name)!).Clone();
            var clock = new CanonicalClockCarry { Native = Clock(nameof(CanonicalClockCarry.Native)),
                Random = Clock(nameof(CanonicalClockCarry.Random)), Observer = Clock(nameof(CanonicalClockCarry.Observer)) };
            epoch.RequireNativeContinuation(clock.Native); clock.Validate(epoch.SourceNative);
            var schedulerRoot = view.Root(CanonicalSchedulerCarry.Root); var schedulerType = typeof(CanonicalSchedulerCarry);
            var scheduler = new CanonicalSchedulerCarry {
                Version = (int)view.Field(schedulerRoot, schedulerType, nameof(CanonicalSchedulerCarry.Version))!,
                SourceNative = (long)view.Field(schedulerRoot, schedulerType, nameof(CanonicalSchedulerCarry.SourceNative))!,
                Timestamp = (uint)view.Field(schedulerRoot, schedulerType, nameof(CanonicalSchedulerCarry.Timestamp))!,
                Callbacks = (CanonicalSchedulerCarry.Callback[])view.Field(schedulerRoot, schedulerType, nameof(CanonicalSchedulerCarry.Callbacks))!,
                Coroutines = (CanonicalSchedulerCarry.Coroutine[])view.Field(schedulerRoot, schedulerType, nameof(CanonicalSchedulerCarry.Coroutines))! };
            scheduler.RequireEpoch(epoch);
            return new Prepared { Epoch = epoch, Clock = clock, Scheduler = scheduler, SceneLoad = CanonicalSceneLoadCarry.FromPrepared(view) };
        }
    }
}
