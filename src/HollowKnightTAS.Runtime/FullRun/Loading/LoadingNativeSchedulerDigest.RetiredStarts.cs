using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        // Only previously registered Canon pool owners, whose original Awake
        // ran but Start did not, may retire this exact engine Start family.
        internal int CancelRetiredCanonicalStarts(Func<int, bool> ownsRetiredPendingStart)
        {
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            if (PlayMakerFSM.NotMainThread || HutongGames.PlayMaker.FsmExecutionStack.StackCount != 0)
                throw new InvalidOperationException("Retired Start cleanup overlaps original FSM execution.");
            RequireCode(0x6285e0, "4056574154415541574883ec20488b41308bfa4d8be14d8be8488bf1488b1048");
            RequireCode(0x8ac680, "4883ec2880b92801000000742d80b925010000007524c6812501000001");
            var thread = Thread.CurrentThread.ManagedThreadId;
            var manager = Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
            List<CallbackView> Queue(out byte[] state)
            {
                state = Read(manager, 80); var head = Pointer(state, 48); var count = BitConverter.ToUInt64(state, 56);
                if (count > 4096) throw new InvalidOperationException("Retired Start callback queue exceeds its bound.");
                var queue = new List<CallbackView>();
                Walk(Pointer(Read(head, 32), 8), head, queue, new HashSet<IntPtr>(), _ => "existing-owner", BitConverter.ToUInt32(state, 72));
                if ((ulong)queue.Count != count || !Read(manager, 80).SequenceEqual(state))
                    throw new InvalidOperationException("Retired Start callback queue changed during preparation.");
                return queue;
            }
            var before = Queue(out var stateBefore);
            var selected = before.Where(item => ownsRetiredPendingStart(item.OwnerInstanceId)).ToArray();
            foreach (var callback in selected)
                if (callback.Call != "UnityPlayer:8ac680" || callback.Cleanup != "none" || callback.UserDataPresent
                    || callback.Frame != -1 || callback.Mode != 7 || callback.Repeat != 0 || callback.Rate != 0
                    || callback.TimestampAge != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime))
                    throw new InvalidOperationException("Retired canonical owner has an unregistered native callback.");
            if (selected.GroupBy(item => item.OwnerInstanceId).Any(group => group.Count() != 1))
                throw new InvalidOperationException("Retired canonical owner has ambiguous original Start callbacks.");
            if (selected.Length == 0) return 0;
            var remaining = before.Where(item => !selected.Contains(item)).ToArray();
            LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
            if (Thread.CurrentThread.ManagedThreadId != thread || !Read(manager, 80).SequenceEqual(stateBefore)
                || before.Any(item => !Read(item.NodeAddress, 96).SequenceEqual(item.NodeBytes))
                || selected.Any(item => !ownsRetiredPendingStart(item.OwnerInstanceId)))
                throw new InvalidOperationException("Retired Start preparation became stale.");
            var call = IntPtr.Add(engine, 0x8ac680);
            var cancel = (CancelDelayedPair)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x6285e0), typeof(CancelDelayedPair));
            foreach (var callback in selected) {
                LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
                cancel(manager, callback.OwnerInstanceId, call, call);
            }
            var after = Queue(out var stateAfter);
            if (after.Count != remaining.Length || !stateBefore.Take(48).SequenceEqual(stateAfter.Take(48))
                || !stateBefore.Skip(72).SequenceEqual(stateAfter.Skip(72))
                || after.Where((item, index) => item.NodeAddress != remaining[index].NodeAddress
                    || !item.NodeBytes.Skip(32).SequenceEqual(remaining[index].NodeBytes.Skip(32))).Any())
                throw new InvalidOperationException("Retired Start cleanup changed another callback or scheduler state.");
            return selected.Length;
        }
    }
}
