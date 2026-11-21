using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        internal Action RestoreAbsentProducedGeoStarts(CanonicalSchedulerCarry source,
            IReadOnlyDictionary<int, MonoBehaviour> produced, string prefix, IReadOnlyDictionary<int, string> names,
            Action requireDirectory, HeroController hero,
            Action admission, Action requireSourceNative, IReadOnlyCollection<string>? pendingBorderOwners = null)
            => RestoreAbsentProducedStarts(source, produced, prefix, names, requireDirectory, hero, admission, requireSourceNative, 11, pendingBorderOwners);
        internal Action RestoreAbsentProducedMarkerStarts(CanonicalSchedulerCarry source,
            IReadOnlyDictionary<int, MonoBehaviour> produced, string prefix, IReadOnlyDictionary<int, string> names,
            Action requireDirectory, HeroController hero, Action admission, Action requireSourceNative)
            => RestoreAbsentProducedStarts(source, produced, prefix, names, requireDirectory, hero, admission, requireSourceNative, 8);
        private Action RestoreAbsentProducedStarts(CanonicalSchedulerCarry source,
            IReadOnlyDictionary<int, MonoBehaviour> produced, string prefix, IReadOnlyDictionary<int, string> names,
            Action requireDirectory, HeroController hero, Action admission, Action requireSourceNative, int expectedOwnerCount,
            IReadOnlyCollection<string>? pendingBorderOwners = null)
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping RestoreAbsentProducedStarts in fallback mode.");
                return () => { };
            }
            admission(); requireDirectory(); requireSourceNative(); RequireFrozenCapturedQueue()();
            if (produced.Count != expectedOwnerCount || produced.Any(pair => pair.Value == null || pair.Value.GetInstanceID() != pair.Key))
                throw new InvalidOperationException("Source Geo Start cleanup has no exact original produced owner directory.");
            var manager = capturedManager; var callbacks = capturedCallbacks;
            var managerBytes = (byte[])capturedManagerBytes.Clone();
            var routines = coroutines.ToDictionary(pair => pair.Key, pair => (byte[])pair.Value.NativeBytes.Clone());
            var thread = Thread.CurrentThread.ManagedThreadId;
            List<CallbackView> ReadQueue(out byte[] state) {
                state = Read(manager, 80); var head = Pointer(state, 48); var count = BitConverter.ToUInt64(state, 56);
                if (count > 4096) throw new InvalidOperationException("Source Geo Start queue exceeds its original bound.");
                var result = new List<CallbackView>();
                Walk(Pointer(Read(head, 32), 8), head, result, new HashSet<IntPtr>(), _ => "source-geo-start-read", BitConverter.ToUInt32(state, 72));
                if ((ulong)result.Count != count || !state.SequenceEqual(Read(manager, 80)))
                    throw new InvalidOperationException("Source Geo Start queue changed during its independent read.");
                return result;
            }
            List<CallbackView> retained;
            int removed;
            using (var validation = new LoadingEngineClockProbe.CheckpointValidation()) {
                if (!CheckCode(0x8ab340, "40534883ec4083792002488bd9750b488b41284889442450eb1d488b51184883faff0f8479010000488d4c2450e80e0e0100488b4424504885c00f8461010000488b83f00000004885c0740a807840000f854b010000c6832801000001488b83f80000004885c0743b48837858007434f30f1015648cdf00488d0dc11200008b53080f57c0c7442430070000004533c948c744242800000000f30f11442420e8acd0d7ff488bcbe814fcffff80bb24010000007559488bcbe8830b00004c8b4424504d85c00f84d6000000488d54245848c74424580000000033c9ff15e7601701488b4424584885c00f84b200000048837810000f84a7000000488b03488bcbff90d000000084c00f8493000000488b83f80000004885c074144883784000740dba08000000488bcbe8020c0000488b83f80000004885c074554883783800744eba07000000488bcbe8e20b00004c8b4424504d85c07449488d54245848c74424580000000033c9ff155a601701488b4424584885c0742948837810007422488b03488bcbff90d000000084c07412488bcbe8b9fcffff33d2488bcbe8ff5800004883c4405bc3")
                    || !CheckCode(0x8ac680, "4883ec2880b92801000000742d80b925010000007524c6812501000001488b91f80000004885d27411488b52584885d274084533c0e896270000b8010000004883c428c3")
                    || !CheckCode(0x6285e0, "4056574154415541574883ec20488b41308bfa4d8be14d8be8488bf1488b1048"))
                {
                    return () => { };
                }
                requireDirectory();
                var states = produced.Values.ToDictionary(owner => names[owner.GetInstanceID()], owner =>
                    new CanonicalGeoSourceQueueContract.OwnerState { Lifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(owner), Active = owner.gameObject.activeInHierarchy }, StringComparer.Ordinal);
                var actual = CaptureCanonicalCarry(source.SourceNative, hero);
                var selectedOrders = CanonicalGeoSourceQueueContract.Select(source, actual, prefix, states, pendingBorderOwners);
                var selected = selectedOrders.Select(order => callbacks[order]).ToArray();
                if (selected.Any(item => !produced.ContainsKey(item.OwnerInstanceId) || !OriginalStart(item)))
                    throw new InvalidOperationException("Source Geo Start selection escaped its actual newly produced native owners.");
                var selectedNodes = new HashSet<IntPtr>(selected.Select(item => item.NodeAddress));
                retained = callbacks.Where(item => !selectedNodes.Contains(item.NodeAddress)).ToList(); removed = selected.Length;
                var before = ReadQueue(out var stateBefore);
                if (!managerBytes.SequenceEqual(stateBefore) || before.Count != callbacks.Length
                    || before.Where((item, index) => item.NodeAddress != callbacks[index].NodeAddress
                        || !item.NodeBytes.SequenceEqual(callbacks[index].NodeBytes)).Any())
                    throw new InvalidOperationException("Source Geo Start preparation became stale.");
                var cancel = (CancelDelayedPair)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x6285e0), typeof(CancelDelayedPair));
                capturedQueueValid = false;
                foreach (var item in selected) {
                    admission(); requireDirectory(); LoadingSerialSceneProbe.RequireCanonicalMutationBoundary();
                    if (Thread.CurrentThread.ManagedThreadId != thread || GetModuleHandle("UnityPlayer.dll") != engine)
                        throw new InvalidOperationException("Source Geo Start original cancellation changed thread or module.");
                    var owner = produced[item.OwnerInstanceId]; var flags = BoundaryMonoLifecycle.ObserveUnstartedUi(owner);
                    if (owner.gameObject.activeInHierarchy || flags[1] != 0 || flags[4] != 0)
                        throw new InvalidOperationException("Source Geo owner became active, started or added to manager before original cancellation.");
                    cancel(manager, item.OwnerInstanceId, IntPtr.Add(engine, 0x8ac680), IntPtr.Add(engine, 0x8ac680));
                }
                var after = ReadQueue(out var stateAfter);
                if (after.Count != retained.Count || !managerBytes.Take(48).SequenceEqual(stateAfter.Take(48))
                    || !managerBytes.Skip(72).SequenceEqual(stateAfter.Skip(72))
                    || after.Where((item, index) => item.NodeAddress != retained[index].NodeAddress
                        || !item.NodeBytes.Skip(32).SequenceEqual(retained[index].NodeBytes.Skip(32))).Any()
                    || routines.Any(pair => !pair.Value.SequenceEqual(Read(pair.Key, 136))))
                    throw new InvalidOperationException("Source Geo original Start cancellation altered a non-owned native queue/coroutine payload or order.");
            }
            capturedQueueValid = false;
            // Only a complete fresh native queue read follows before the final
            // source-native check below: no write, callback or PlayerLoop.
            admission();
            var consumed = false;
            return () => {
                if (consumed || Thread.CurrentThread.ManagedThreadId != thread || !capturedQueueValid
                    || ReferenceEquals(callbacks, capturedCallbacks) || capturedManager != manager || capturedCallbacks.Length != retained.Count
                    || !managerBytes.Take(48).SequenceEqual(capturedManagerBytes.Take(48))
                    || !managerBytes.Skip(72).SequenceEqual(capturedManagerBytes.Skip(72))
                    || capturedCallbacks.Where((item, index) => item.NodeAddress != retained[index].NodeAddress
                        || !item.NodeBytes.Skip(32).SequenceEqual(retained[index].NodeBytes.Skip(32))).Any()
                    || routines.Count != coroutines.Count || routines.Any(pair => !coroutines.TryGetValue(pair.Key, out var value)
                        || !pair.Value.SequenceEqual(value.NativeBytes)))
                    throw new InvalidOperationException("Source Geo Start cleanup lacks its complete independent new queue readback.");
                consumed = true; admission(); requireSourceNative(); RequireFrozenCapturedQueue()();
                Modding.Logger.Log("[HKTAS] Source Geo empty pending queue restored by original cancellation: producedStarts=" + removed
                    + ";sourceGeoPendingStarts=0;inactiveAddedToManagerFalseDidStartFalse=true;originalReactivationRegistersStartBeforeAwake=true;otherNativeQueueAndCoroutineBytesEqual=true;completeStateCoverage=false");
            };
        }
    }
}
