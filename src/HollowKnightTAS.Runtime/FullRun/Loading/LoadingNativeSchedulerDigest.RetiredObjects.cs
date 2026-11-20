using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void CancelDelayedPair(IntPtr manager, int owner, IntPtr call, IntPtr secondCall);

        // Use Unity's own matching-PDB removal, rather than rewriting its tree.
        // Retired original meshes/R2 tweens come from their bounded registries.
        internal int CancelRetiredObjectDestroys(Func<int, Action?> prepareDestruction)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            if (PlayMakerFSM.NotMainThread || HutongGames.PlayMaker.FsmExecutionStack.StackCount != 0)
                throw new InvalidOperationException("Retired object cleanup overlaps original FSM execution.");
            RequireCode(0x629d80, "4883ec2833d2e8354a120033c04883c428c3");
            RequireCode(0x6285e0, "4056574154415541574883ec20488b41308bfa4d8be14d8be8488bf1488b1048");
            var thread = Thread.CurrentThread.ManagedThreadId;
            var manager = Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
            List<CallbackView> Queue(out byte[] state)
            {
                state = Read(manager, 80); var head = Pointer(state, 48); var count = BitConverter.ToUInt64(state, 56);
                if (count > 4096) throw new InvalidOperationException("Retired object callback queue exceeds its bound.");
                var queue = new List<CallbackView>();
                Walk(Pointer(Read(head, 32), 8), head, queue, new HashSet<IntPtr>(), _ => "existing-owner", BitConverter.ToUInt32(state, 72));
                if ((ulong)queue.Count != count || !Read(manager, 80).SequenceEqual(state))
                    throw new InvalidOperationException("Retired object callback queue changed during preparation.");
                return queue;
            }
            var before = Queue(out var stateBefore);
            var selected = new List<Tuple<CallbackView, Action>>();
            foreach (var callback in before.Where(item => item.Call == "UnityPlayer:629d80"))
            {
                var destroy = prepareDestruction(callback.OwnerInstanceId);
                if (destroy == null) continue;
                if (callback.Cleanup != "none" || callback.UserDataPresent || callback.Frame != -1 || callback.Mode != 67
                    || callback.Repeat != 0 || callback.Rate != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime))
                    throw new InvalidOperationException("Original retired object delayed destruction signature differs.");
                selected.Add(Tuple.Create(callback, destroy));
            }
            if (selected.Count == 0) return 0;
            var remaining = before.Where(item => !selected.Any(pair => ReferenceEquals(pair.Item1, item))).ToArray();
            if (Thread.CurrentThread.ManagedThreadId != thread || !Read(manager, 80).SequenceEqual(stateBefore)
                || before.Any(item => !Read(item.NodeAddress, 96).SequenceEqual(item.NodeBytes)))
                throw new InvalidOperationException("Retired object callback preparation became stale.");
            var call = IntPtr.Add(engine, 0x629d80);
            var cancel = (CancelDelayedPair)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x6285e0), typeof(CancelDelayedPair));
            foreach (var pair in selected.GroupBy(pair => pair.Item1.OwnerInstanceId).Select(group => group.First()))
            {
                // DestroyImmediate may itself remove the original pending entry;
                // the original matching cancellation is idempotent for that ID.
                pair.Item2(); cancel(manager, pair.Item1.OwnerInstanceId, call, call);
            }
            var after = Queue(out var stateAfter);
            if (after.Count != remaining.Length || !stateBefore.Take(48).SequenceEqual(stateAfter.Take(48))
                || !stateBefore.Skip(72).SequenceEqual(stateAfter.Skip(72))
                || after.Where((item, index) => item.NodeAddress != remaining[index].NodeAddress
                    || !item.NodeBytes.Skip(32).SequenceEqual(remaining[index].NodeBytes.Skip(32))).Any())
                throw new InvalidOperationException("Retired object cleanup changed a remaining callback or scheduler state.");
            return selected.Count;
        }
    }
}
