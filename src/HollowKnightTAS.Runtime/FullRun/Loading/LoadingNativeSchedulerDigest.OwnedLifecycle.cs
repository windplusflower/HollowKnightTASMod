using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        // A scoped original Destroy/Instantiate may replace its own queued
        // Starts. Consume the old frozen sample before mutation; never apply
        // a plan prepared against it. All other callback/coroutine ownership,
        // order and scalar bits must survive the new independent read.
        internal Action BeginOwnedStartLifecycle(IReadOnlyDictionary<int, MonoBehaviour> oldOwners, Func<int, bool> ownsNewStart, out Action retireStarts)
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping BeginOwnedStartLifecycle in fallback mode.");
                retireStarts = () => { };
                return () => { };
            }
            RequireFrozenCapturedQueue()();
            if (oldOwners == null || oldOwners.Count > 4096 || oldOwners.Any(item => item.Value == null || item.Key != item.Value.GetInstanceID()))
                throw new InvalidOperationException("Owned lifecycle directory is invalid.");
            var owners = oldOwners.ToDictionary(item => item.Key, item => item.Value);
            var oldSet = new HashSet<int>(owners.Keys);
            var thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var oldStarts = capturedCallbacks.Where(value => oldSet.Contains(value.OwnerInstanceId)).ToArray();
            foreach (var callback in capturedCallbacks.Where(value => oldSet.Contains(value.OwnerInstanceId)))
                if (!OriginalStart(callback)) throw new InvalidOperationException("Owned HUD retirement has a non-Start callback.");
            var retained = capturedCallbacks.Where(value => !oldSet.Contains(value.OwnerInstanceId)).ToArray();
            var manager = capturedManager; var timestamp = capturedTimestamp;
            var managerBytes = (byte[])capturedManagerBytes.Clone();
            var routines = coroutines.ToDictionary(item => item.Key, item => (byte[])item.Value.NativeBytes.Clone());
            capturedQueueValid = false;
            var retirementConsumed = false;
            var retirementSkipped = false;
            retireStarts = () => {
                LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
                if (retirementConsumed || System.Threading.Thread.CurrentThread.ManagedThreadId != thread
                    || PlayMakerFSM.NotMainThread || HutongGames.PlayMaker.FsmExecutionStack.StackCount != 0
                    || owners.Any(item => ReferenceEquals(item.Value, null) || item.Value != null))
                    throw new InvalidOperationException("Owned Start retirement is reused or its actual Mono owner was not destroyed.");
                retirementConsumed = true;
                if (!CheckCode(0x8ac680, "4883ec2880b92801000000742d80b925010000007524c6812501000001488b91")
                    || !CheckCode(0x6285e0, "4056574154415541574883ec20488b41308bfa4d8be14d8be8488bf1488b1048"))
                {
                    retirementSkipped = true;
                    return;
                }
                List<CallbackView> ReadQueue(out byte[] state) {
                    state = Read(manager, 80); var head = Pointer(state, 48);
                    var count = BitConverter.ToUInt64(state, 56);
                    if (count > 4096) throw new InvalidOperationException("Owned Start retirement queue exceeded its bound.");
                    var callbacks = new List<CallbackView>();
                    Walk(Pointer(Read(head, 32), 8), head, callbacks, new HashSet<IntPtr>(), _ => "owned-lifecycle-read", BitConverter.ToUInt32(state, 72));
                    if ((ulong)callbacks.Count != count || !state.SequenceEqual(Read(manager, 80)))
                        throw new InvalidOperationException("Owned Start retirement queue changed during its independent read.");
                    return callbacks;
                }
                var before = ReadQueue(out var stateBefore);
                var selected = before.Where(item => oldSet.Contains(item.OwnerInstanceId)).ToArray();
                foreach (var item in selected) {
                    var frozen = oldStarts.SingleOrDefault(value => value.NodeAddress == item.NodeAddress && value.OwnerInstanceId == item.OwnerInstanceId);
                    if (frozen == null || !OriginalStart(item) || !frozen.NodeBytes.Skip(32).SequenceEqual(item.NodeBytes.Skip(32)))
                        throw new InvalidOperationException("Destroyed owned Start does not match its original frozen owner/node/payload.");
                }
                var retainedNow = before.Where(item => !oldSet.Contains(item.OwnerInstanceId)).ToArray();
                var cancel = (CancelDelayedPair)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x6285e0), typeof(CancelDelayedPair));
                var call = IntPtr.Add(engine, 0x8ac680);
                if (!stateBefore.SequenceEqual(Read(manager, 80)) || before.Any(item => !item.NodeBytes.SequenceEqual(Read(item.NodeAddress, 96))))
                    throw new InvalidOperationException("Owned Start cancellation preparation became stale.");
                foreach (var item in selected.GroupBy(value => value.OwnerInstanceId).Select(group => group.First())) {
                    LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
                    cancel(manager, item.OwnerInstanceId, call, call);
                }
                var after = ReadQueue(out var stateAfter);
                if (after.Count != retainedNow.Length || !stateBefore.Take(48).SequenceEqual(stateAfter.Take(48))
                    || !stateBefore.Skip(72).SequenceEqual(stateAfter.Skip(72))
                    || after.Where((item, index) => item.NodeAddress != retainedNow[index].NodeAddress
                        || !item.NodeBytes.Skip(32).SequenceEqual(retainedNow[index].NodeBytes.Skip(32))).Any())
                    throw new InvalidOperationException("Original owned Start cancellation altered another callback or manager state.");
            };
            var consumed = false;
            return () => {
                if (retirementSkipped) return;
                if (consumed || !retirementConsumed || !capturedQueueValid || capturedManager != manager || capturedTimestamp != timestamp
                    || !managerBytes.Take(48).SequenceEqual(capturedManagerBytes.Take(48))
                    || !managerBytes.Skip(72).SequenceEqual(capturedManagerBytes.Skip(72)))
                    throw new InvalidOperationException("Owned lifecycle has no new independent native sample.");
                consumed = true;
                var survivors = capturedCallbacks.Where(value => !oldSet.Contains(value.OwnerInstanceId)
                    && retained.Any(old => old.NodeAddress == value.NodeAddress)).ToArray();
                if (survivors.Length != retained.Length)
                    throw new InvalidOperationException("Owned lifecycle removed or reordered a non-owned native callback.");
                for (var i = 0; i < retained.Length; i++) {
                    var before = retained[i]; var after = survivors[i];
                    // Tree links can rotate on the real native insertion/removal.
                    // The complete callback payload at bytes32..95 cannot change.
                    if (before.NodeAddress != after.NodeAddress || before.OwnerInstanceId != after.OwnerInstanceId
                        || !before.NodeBytes.Skip(32).SequenceEqual(after.NodeBytes.Skip(32)))
                        throw new InvalidOperationException("Owned lifecycle altered a non-owned callback payload or order.");
                }
                if (routines.Count != coroutines.Count || routines.Any(item => !coroutines.TryGetValue(item.Key, out var now)
                    || !item.Value.SequenceEqual(now.NativeBytes)))
                    throw new InvalidOperationException("Owned lifecycle changed a real coroutine native owner.");
                if (capturedCallbacks.Any(value => oldSet.Contains(value.OwnerInstanceId)))
                    throw new InvalidOperationException("Original destroyed HUD owners still have queued callbacks.");
                foreach (var added in capturedCallbacks.Where(value => !retained.Any(old => old.NodeAddress == value.NodeAddress)))
                    if (!ownsNewStart(added.OwnerInstanceId) || !OriginalStart(added))
                        throw new InvalidOperationException("Owned lifecycle introduced an unregistered native callback.");
                RequireFrozenCapturedQueue()();
            };
        }
        private static bool OriginalStart(CallbackView value) => value.Call == "UnityPlayer:8ac680" && value.Cleanup == "none"
            && !value.UserDataPresent && value.Frame == -1 && value.Rate == 0 && value.Repeat == 0 && value.Mode == 7
            && value.TimestampAge == 0 && !double.IsNaN(value.WakeTime) && !double.IsInfinity(value.WakeTime);
    }
}
