using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalClockCarry
    {
        public byte[] Native = Array.Empty<byte>(), Random = Array.Empty<byte>(), Observer = Array.Empty<byte>();
        internal static string SchemaIdentity => "CanonicalClockCarry/v1:Native=HTC1/1128;Random=CheckpointRandomState/v1;Observer=HCO1/32";
        private static MethodInfo Method(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Single(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload")
            .GetType("HollowKnightTAS.ClockPayload.ClockController", true)!.GetMethod(name, BindingFlags.Static | BindingFlags.Public)
            ?? throw new MissingMethodException("Original clock continuation method is absent: " + name);
        private static byte[] Export(string name) => Method(name).Invoke(null, null) as byte[]
            ?? throw new InvalidDataException("Original clock continuation export returned no byte array: " + name);
        internal static CanonicalClockCarry Capture(long nativeFrame)
        {
            var value = new CanonicalClockCarry { Native = Export("ExportCheckpointNativeClock"),
                Random = Export("ExportCheckpointRandomState"), Observer = Export("ExportCheckpointClockObserver") };
            value.Validate(nativeFrame); return value;
        }
        internal void Validate(long nativeFrame)
        {
            var observer = CheckpointClockObserverState.Decode(Observer);
            observer.RequireNativeReadback(Native, nativeFrame, observer.CaptureUnityFrameCount);
            var random = CheckpointRandomState.Decode(Random);
            if (random.AdvanceSequence != observer.AdvanceSequence)
                throw new InvalidDataException("Canonical source random/native/observer sequence differs.");
        }
    }
}
