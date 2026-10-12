using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);

        internal Action PrepareCanonicalRestore(CanonicalSchedulerCarry source, HeroController hero, Func<bool> isPaused)
        {
            var prepared = CaptureCanonicalCarry(source.SourceNative, hero);
            var plan = CanonicalSchedulerRestorePlan.Prepare(source, prepared);
            var manager = capturedManager;
            var callbacks = capturedCallbacks;
            var addresses = new[] { manager }.Concat(callbacks.Select(item => item.NodeAddress)).ToArray();
            if (manager == IntPtr.Zero || addresses.Distinct().Count() != addresses.Length)
                throw new InvalidOperationException("Scheduler native blocks are absent or aliased.");
            var transaction = CanonicalSchedulerMemoryTransaction.Prepare(plan, capturedManagerBytes,
                callbacks.Select(item => item.NodeBytes).ToArray());
            var head = Pointer(capturedManagerBytes, 48); var headBytes = Read(head, 32);
            var coroutineBlocks = coroutines.Select(item => new KeyValuePair<IntPtr, byte[]>(item.Key, (byte[])item.Value.NativeBytes.Clone())).ToArray();
            var thread = Thread.CurrentThread.ManagedThreadId;
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            if (module.BaseAddress != engine || module.ModuleMemorySize != engineLength)
                throw new InvalidOperationException("Scheduler engine changed before preparation.");
            void Guard()
            {
                if (Thread.CurrentThread.ManagedThreadId != thread || !capturedQueueValid
                    || !ReferenceEquals(capturedCallbacks, callbacks)
                    || GetModuleHandle("UnityPlayer.dll") != engine
                    || Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0) != manager)
                    throw new InvalidOperationException("Scheduler native owner, thread or observation changed.");
                LoadingSerialSceneProbe.AddCanonicalLoadInfo(new Dictionary<string, object>(StringComparer.Ordinal));
                if (!headBytes.SequenceEqual(Read(head, 32)) || coroutineBlocks.Any(item => !item.Value.SequenceEqual(Read(item.Key, 136))))
                    throw new InvalidOperationException("Scheduler tree head or coroutine ownership changed.");
            }
            byte[] ReadBlock(int index, int length) => Read(addresses[index], length);
            transaction.ValidateBefore(ReadBlock, Guard);
            return () => {
                void ApplyGuard()
                {
                    if (!isPaused()) throw new InvalidOperationException("Scheduler native application requires a paused continuation clock.");
                    Guard();
                }
                try {
                    transaction.Apply(ReadBlock, (index, offset, bytes) => {
                        if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(addresses[index], offset), bytes,
                            new UIntPtr((uint)bytes.Length), out var written) || written.ToUInt64() != (ulong)bytes.Length)
                            throw new InvalidOperationException("Scheduler native scalar write failed.");
                    }, ApplyGuard);
                }
                finally { capturedQueueValid = false; }
            };
        }
    }
}
