using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Engine-pinned reader; explicit parked restore preparation lives in the
    // separate partial. No pointer payload or IEnumerator.Current/MoveNext.
    internal sealed partial class LoadingNativeSchedulerDigest
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint CreateHandle(IntPtr target, int pinned);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void FreeHandle(uint handle);
        private readonly CreateHandle? createHandle;
        private readonly FreeHandle? freeHandle;
        private readonly IntPtr engine;
        private readonly int engineLength;
        private readonly bool nativeSchedulerSupported;
        private static readonly HashSet<int> warnedRvas = new HashSet<int>();
        private static bool fallbackWarned;
        private readonly Dictionary<IntPtr, CoroutineView> coroutines = new Dictionary<IntPtr, CoroutineView>();
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private Func<IntPtr, string?> behaviourId = _ => null;
        private readonly List<object> inventory = new List<object>();
        private object[] fieldDiagnostics = Array.Empty<object>();
        private readonly List<CallbackView> nonCoroutineCallbacks = new List<CallbackView>();
        private CallbackView[] capturedCallbacks = Array.Empty<CallbackView>();
        private uint capturedTimestamp;
        private bool capturedQueueValid;
        private IntPtr capturedManager;
        private byte[] capturedManagerBytes = Array.Empty<byte>();

        internal LoadingNativeSchedulerDigest()
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            engine = module.BaseAddress; engineLength = module.ModuleMemorySize;
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnUnsupportedOnce("UnityPlayer.dll SHA-256 differs from baseline; native scheduler inspection will run in fallback/no-op mode.");
                return;
            }
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var create = GetProcAddress(mono, "mono_gchandle_new");
            var free = GetProcAddress(mono, "mono_gchandle_free");
            if (mono == IntPtr.Zero || create == IntPtr.Zero || free == IntPtr.Zero)
            {
                WarnUnsupportedOnce("Mono GC handle accessors are unavailable; native scheduler inspection will run in fallback/no-op mode.");
                return;
            }
            createHandle = (CreateHandle)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateHandle));
            freeHandle = (FreeHandle)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeHandle));
            // Matching PDB getter: index 8 into the existing manager table.
            if (!CheckCode(0x629ef0, "b908000000e9068ef4ff")
                || !CheckCode(0x572d00, "4863c1488d0d264d4601488b04c1c3")
                || !CheckCode(0x771ec0, "8b414883f8020f94c0c3")
                || !CheckCode(0x8ac680, "4883ec2880b92801000000742d80b925010000007524c6812501000001"))
            {
                return;
            }
            nativeSchedulerSupported = true;
        }

        private static void WarnUnsupportedOnce(string message)
        {
            if (!fallbackWarned)
            {
                fallbackWarned = true;
                Modding.Logger.LogWarn("[HKTAS] " + message);
            }
        }

        internal object[] Inventory => inventory.ToArray();
        internal void RequireCapturedPersistentAnimatorCallbacksAbsent()
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping persistent animator native callback check in fallback mode.");
                return;
            }
            if (!capturedQueueValid)
            {
                Modding.Logger.LogWarn("[HKTAS] Persistent animator has no original scheduler snapshot; skipping check.");
                return;
            }
            var owners = CanonicalPersistentSpriteContinuation.NativeOwners().Select(value => value.GetInstanceID()).ToArray();
            if (owners.Distinct().Count() != CanonicalPersistentSpriteContinuation.NativeOwnerCount || capturedCallbacks.Any(callback => owners.Contains(callback.OwnerInstanceId)))
                throw new InvalidOperationException("Persistent animator/health has an unsupported original pending native callback.");
        }
        internal Action RequireFrozenCapturedQueue()
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping frozen scheduler queue check in fallback mode.");
                return () => { };
            }
            if (!capturedQueueValid)
            {
                Modding.Logger.LogWarn("[HKTAS] Original scheduler has no frozen observation; returning no-op verification.");
                return () => { };
            }
            void Require() {
                if (!capturedManagerBytes.SequenceEqual(Read(capturedManager, 80))
                    || capturedCallbacks.Any(c => !c.NodeBytes.SequenceEqual(Read(c.NodeAddress, 96)))
                    || coroutines.Any(c => !c.Value.NativeBytes.SequenceEqual(Read(c.Key, 136))))
                    throw new InvalidOperationException("Original scheduler queue/coroutine bytes changed during S0 clock binding.");
            }
            Require(); return Require;
        }
        internal string[] Unsupported => unsupported.ToArray();
        internal object[] FieldDiagnostics => fieldDiagnostics;
        internal uint CaptureManagerTimestamp()
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping CaptureManagerTimestamp in fallback mode; returning 0.");
                return 0u;
            }
            try
            {
                var manager = Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
                var before = Read(manager, 80);
                var after = Read(manager, 80);
                if (!before.SequenceEqual(after))
                {
                    Modding.Logger.LogWarn("[HKTAS] Scheduler manager changed during epoch capture; returning timestamp from first read.");
                }
                return BitConverter.ToUInt32(before, 72);
            }
            catch (Exception ex)
            {
                Modding.Logger.LogWarn("[HKTAS] CaptureManagerTimestamp failed (" + ex.Message + "); returning 0.");
                return 0u;
            }
        }
        internal int AsyncOperationCount => coroutines.Values.Count(value => value.HasAsyncOperation);
        internal string[] CanonicalWaitIssues(HeroController? hero, Func<int, bool> ownsCanonicalStart)
        {
            if (!nativeSchedulerSupported) return Array.Empty<string>();
            var issues = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var coroutine in coroutines.Values)
            {
                if (!(coroutine.Enumerator is IEnumerator iterator)
                    || !(hero != null && CheckpointHeroInvulnerability.IsBoundIterator(iterator, hero)
                        || LoadingSerialSceneProbe.OwnsBoundaryIterator(iterator)))
                    issues.Add("unregistered-iterator:" + coroutine.Id);
                if (coroutine.Done || coroutine.HasAsyncOperation)
                    issues.Add("completed-or-async-carry:" + coroutine.Id);
            }
            foreach (var callback in nonCoroutineCallbacks)
                if (callback.Call != "UnityPlayer:8ac680" || callback.Cleanup != "none" || callback.UserDataPresent
                    || callback.Frame != -1 || callback.Rate != 0 || callback.Repeat != 0 || callback.Mode != 7
                    || callback.TimestampAge != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime)
                    || !ownsCanonicalStart(callback.OwnerInstanceId))
                    issues.Add("unregistered-native-callback:" + callback.Description);
            foreach (var omission in unsupported) issues.Add("native-scheduler-coverage:" + omission);
            return issues.ToArray();
        }
        internal void Release() { coroutines.Clear(); nonCoroutineCallbacks.Clear(); capturedCallbacks = Array.Empty<CallbackView>(); capturedQueueValid = false; behaviourId = _ => null; }
        internal CanonicalSchedulerCarry CaptureCanonicalCarry(long sourceNative, HeroController hero)
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Returning empty CanonicalSchedulerCarry in fallback mode.");
                return new CanonicalSchedulerCarry { SourceNative = Math.Max(0L, sourceNative) };
            }
            if (!capturedQueueValid || unsupported.Count != 0 || CaptureManagerTimestamp() != capturedTimestamp)
                throw new InvalidOperationException("Canonical scheduler carry has no unchanged complete native queue observation.");
            var data = new CanonicalSchedulerCarry { SourceNative = sourceNative, Timestamp = capturedTimestamp,
                Callbacks = capturedCallbacks.Select((callback, index) => new CanonicalSchedulerCarry.Callback {
                    Order = index, Owner = callback.OwnerIdentity, Call = callback.Call, Cleanup = callback.Cleanup,
                    WakeTimeBits = BitConverter.DoubleToInt64Bits(callback.WakeTime), Frame = callback.Frame,
                    Rate = callback.Rate, Mode = callback.Mode, Repeat = callback.Repeat, TimestampAge = callback.TimestampAge,
                    UserDataPresent = callback.UserDataPresent, Coroutine = callback.Coroutine == IntPtr.Zero ? "" : coroutines[callback.Coroutine].Id }).ToArray(),
                Coroutines = coroutines.Values.OrderBy(item => item.Id, StringComparer.Ordinal).Select(item => new CanonicalSchedulerCarry.Coroutine {
                    Identity = item.Id, Family = item.Enumerator is IEnumerator iterator && LoadingSerialSceneProbe.OwnsBoundaryIterator(iterator) ? "serial-boundary"
                        : item.Enumerator is IEnumerator invulnerable && CheckpointHeroInvulnerability.IsBoundIterator(invulnerable, hero) ? "hero-invulnerability" : "unregistered",
                    Done = item.Done, EnumeratorBased = item.EnumeratorBased, HasAsyncOperation = item.HasAsyncOperation,
                    WaitingFor = Relation(item.WaitingFor), ContinueWhenFinished = Relation(item.ContinueWhenFinished) }).ToArray() };
            data.Validate(); return data;
        }

        internal sealed class InvulnerabilityWait
        {
            internal IEnumerator Enumerator = null!;
            internal double WakeTime;
            internal long Frame;
            internal int Rate, Mode;
            internal byte Repeat;
            internal bool Flag128;
            internal string Call = "";
        }

        // Read only the actual queued wait and its existing iterator. The
        // packet receives scalar values, never a coroutine or native address.
        internal InvulnerabilityWait[] CaptureInvulnerabilityWaits(HeroController hero)
            => CaptureWaits(iterator => CheckpointHeroInvulnerability.IsBoundIterator(iterator, hero), 8, "Hero invulnerability");

        // Reuse the complete boundary sample. The legacy wait-only traversal
        // replaces the sample and cannot be used while preparing the D queue.
        internal InvulnerabilityWait[] CapturedInvulnerabilityWaits(HeroController hero)
        {
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping CapturedInvulnerabilityWaits in fallback mode.");
                return Array.Empty<InvulnerabilityWait>();
            }
            if (!capturedQueueValid || !capturedManagerBytes.SequenceEqual(Read(capturedManager, 80))
                || capturedCallbacks.Any(item => !item.NodeBytes.SequenceEqual(Read(item.NodeAddress, 96)))
                || coroutines.Any(item => !item.Value.NativeBytes.SequenceEqual(Read(item.Key, 136))))
                throw new InvalidOperationException("Hero wait carry has no unchanged complete scheduler sample.");
            var result = new List<InvulnerabilityWait>();
            foreach (var callback in capturedCallbacks.Where(item => item.Coroutine != IntPtr.Zero)) {
                var view = coroutines[callback.Coroutine];
                if (!(view.Enumerator is IEnumerator iterator) || !CheckpointHeroInvulnerability.IsBoundIterator(iterator, hero)) continue;
                if (view.Done || view.HasAsyncOperation || view.WaitingFor != IntPtr.Zero || view.ContinueWhenFinished != IntPtr.Zero
                    || callback.Repeat != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime))
                    throw new InvalidOperationException("Hero wait carry has an unregistered native state or relation.");
                result.Add(new InvulnerabilityWait { Enumerator = iterator, WakeTime = callback.WakeTime, Frame = callback.Frame,
                    Rate = callback.Rate, Mode = callback.Mode, Repeat = callback.Repeat, Call = callback.Call, Flag128 = view.EnumeratorBased });
            }
            if (result.Count > 8) throw new InvalidOperationException("Hero wait carry exceeds its bound.");
            return result.ToArray();
        }

        internal InvulnerabilityWait[] CaptureWaits(Func<IEnumerator, bool> accepts, int maximum, string family)
        {
            capturedQueueValid = false; capturedCallbacks = Array.Empty<CallbackView>();
            coroutines.Clear(); unsupported.Clear(); behaviourId = _ => "existing-behaviour";
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping native CaptureWaits in fallback mode.");
                return Array.Empty<InvulnerabilityWait>();
            }
            var manager = Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
            var before = Read(manager, 80);
            var head = Pointer(before, 48); var count = BitConverter.ToUInt64(before, 56);
            if (count > 4096) throw new InvalidOperationException("Checkpoint scheduler exceeds its callback bound.");
            var callbacks = new List<CallbackView>();
            Walk(Pointer(Read(head, 32), 8), head, callbacks, new HashSet<IntPtr>(),
                _ => "existing-object", BitConverter.ToUInt32(before, 72));
            if ((ulong)callbacks.Count != count) throw new InvalidOperationException("Checkpoint callback tree count differs.");
            var result = new List<InvulnerabilityWait>();
            foreach (var callback in callbacks.Where(c => c.Coroutine != IntPtr.Zero))
            {
                var coroutine = Coroutine(callback.Coroutine, 0);
                if (!(coroutine.Enumerator is IEnumerator iterator)
                    || !accepts(iterator)) continue;
                if (coroutine.Done || coroutine.HasAsyncOperation
                    || coroutine.WaitingFor != IntPtr.Zero || coroutine.ContinueWhenFinished != IntPtr.Zero
                    || callback.Repeat != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime))
                    throw new InvalidOperationException(family + " native wait binding: done=" + coroutine.Done
                        + " enumerator=" + coroutine.EnumeratorBased + " async=" + coroutine.HasAsyncOperation
                        + " waiting=" + (coroutine.WaitingFor != IntPtr.Zero)
                        + " continuation=" + (coroutine.ContinueWhenFinished != IntPtr.Zero)
                        + " repeat=" + callback.Repeat + " frame=" + callback.Frame + " mode=" + callback.Mode);
                result.Add(new InvulnerabilityWait { Enumerator = iterator, WakeTime = callback.WakeTime,
                    Frame = callback.Frame, Rate = callback.Rate, Mode = callback.Mode,
                    Repeat = callback.Repeat, Call = callback.Call, Flag128 = coroutine.EnumeratorBased });
            }
            var after = Read(manager, 80);
            if (Pointer(after, 48) != head || BitConverter.ToUInt64(after, 56) != count
                || BitConverter.ToUInt32(after, 72) != BitConverter.ToUInt32(before, 72))
                throw new InvalidOperationException("Checkpoint callback queue changed during its read.");
            if (result.Count > maximum) throw new InvalidOperationException(family + " exceeds its coroutine bound.");
            return result.ToArray();
        }

        internal string Capture(LoadingFieldDigest fields, Func<int, string?> objectId,
            Func<IntPtr, string?> nativeBehaviourId, bool includeFieldDiagnostics = false, Func<int, string?>? retiredObjectId = null)
        {
            coroutines.Clear(); unsupported.Clear(); inventory.Clear(); behaviourId = nativeBehaviourId;
            capturedQueueValid = false; capturedCallbacks = Array.Empty<CallbackView>();
            nonCoroutineCallbacks.Clear();
            if (!nativeSchedulerSupported)
            {
                WarnUnsupportedOnce("Skipping native scheduler Capture() in fallback mode; returning fallback digest.");
                unsupported.Add("native-scheduler-unsupported");
                fieldDiagnostics = Array.Empty<object>();
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("native-scheduler-unsupported\n"))).Replace("-", "").ToLowerInvariant();
            }
            var manager = Pointer(Read(IntPtr.Add(engine, 0x19d7a70), 8), 0);
            if (manager == IntPtr.Zero) throw new InvalidOperationException("P0 delayed-call manager is unavailable.");
            var state = Read(manager, 80);
            var head = Pointer(state, 48);
            var count = BitConverter.ToUInt64(state, 56);
            if (count > 4096) throw new InvalidOperationException("P0 native scheduler exceeds 4096 callbacks.");
            var callbacks = new List<CallbackView>();
            var visited = new HashSet<IntPtr>();
            var root = Pointer(Read(head, 32), 8);
            var timestamp = BitConverter.ToUInt32(state, 72);
            string? ResolveOwner(int instance)
            {
                var live = objectId(instance); var retired = retiredObjectId?.Invoke(instance);
                if (live != null && retired != null) throw new InvalidOperationException("Native callback instance identity was reused after R1 destruction.");
                return live ?? retired;
            }
            Walk(root, head, callbacks, visited, ResolveOwner, timestamp);
            if ((ulong)callbacks.Count != count)
                throw new InvalidOperationException("P0 native callback tree count mismatch.");
            // Register every enumerator first, so cross-coroutine references
            // can resolve while their individual managed fields are hashed.
            foreach (var callback in callbacks)
                if (callback.Coroutine != IntPtr.Zero) Coroutine(callback.Coroutine, 0);
            var text = new StringBuilder();
            var diagnostics = new List<object>();
            text.Append("callback-order-and-timestamp-age-v1\n");
            inventory.Add(new { kind = "manager", timestamp = BitConverter.ToUInt32(state, 72), callbackCount = count });
            foreach (var callback in callbacks)
            {
                if (callback.Coroutine == IntPtr.Zero) nonCoroutineCallbacks.Add(callback);
                text.Append(callback.Description).Append(':');
                if (callback.Coroutine == IntPtr.Zero) text.Append(callback.UserDataPresent ? "opaque-user-data" : "no-user-data");
                else text.Append(coroutines[callback.Coroutine].Id);
                text.Append('\n');
                inventory.Add(new { kind = "callback", description = callback.Description,
                    coroutine = callback.Coroutine == IntPtr.Zero ? null : coroutines[callback.Coroutine].Id });
            }
            foreach (var coroutine in coroutines.Values.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray())
            {
                var digest = fields.Capture(coroutine.Enumerator, coroutine.Id, expandRoot: true,
                    includeFieldDiagnostics: includeFieldDiagnostics);
                if (includeFieldDiagnostics)
                    diagnostics.Add(new { root = coroutine.Id, fields = digest.FieldDigests });
                foreach (var omitted in digest.Unsupported) unsupported.Add(omitted);
                text.Append(coroutine.Id).Append(':').Append(coroutine.Done).Append(':')
                    .Append(coroutine.EnumeratorBased).Append(':').Append(coroutine.HasAsyncOperation).Append(':')
                    .Append(coroutine.AsyncOperationState).Append(':')
                    .Append(Relation(coroutine.WaitingFor)).Append(':').Append(Relation(coroutine.ContinueWhenFinished))
                    .Append(':').Append(digest.Sha256).Append('\n');
                inventory.Add(new { kind = "coroutine", id = coroutine.Id,
                    enumeratorType = coroutine.Enumerator?.GetType().FullName, digest = digest.Sha256,
                    nodes = digest.Nodes, coroutine.Done, coroutine.EnumeratorBased, coroutine.HasAsyncOperation,
                    coroutine.AsyncOperationState,
                    waitingFor = Relation(coroutine.WaitingFor), continueWhenFinished = Relation(coroutine.ContinueWhenFinished) });
            }
            var after = Read(manager, 80);
            if (Pointer(after, 48) != head || BitConverter.ToUInt64(after, 56) != count
                || BitConverter.ToUInt32(after, 72) != BitConverter.ToUInt32(state, 72))
                throw new InvalidOperationException("P0 native callback queue changed during observation.");
            capturedCallbacks = callbacks.ToArray(); capturedTimestamp = timestamp; capturedQueueValid = true;
            capturedManager = manager; capturedManagerBytes = (byte[])after.Clone();
            text.Append(string.Join("\n", unsupported));
            if (includeFieldDiagnostics) fieldDiagnostics = diagnostics.ToArray();
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(
                Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        internal string Reference(UnityEngine.Coroutine wrapper, LoadingFieldDigest fields)
        {
            var pointer = fields.Read(wrapper, "m_Ptr") is IntPtr value ? value : IntPtr.Zero;
            if (pointer == IntPtr.Zero) return "coroutine:released";
            if (coroutines.TryGetValue(pointer, out var coroutine)) return "coroutine:" + coroutine.Id;
            // A stored wrapper can outlive its queue entry. Do not pretend that
            // an unregistered native object is covered by a type-only token.
            unsupported.Add("stored-coroutine-not-in-registered-queue");
            return "coroutine:unregistered";
        }

        private string Relation(IntPtr value) => value == IntPtr.Zero ? "none"
            : coroutines.TryGetValue(value, out var coroutine) ? coroutine.Id : "unregistered";

        private CoroutineView Coroutine(IntPtr pointer, int depth)
        {
            if (coroutines.TryGetValue(pointer, out var existing)) return existing;
            if (coroutines.Count >= 4096 || depth > 64) throw new InvalidOperationException("P0 native coroutine graph budget exceeded.");
            var bytes = Read(pointer, 136);
            var owner = behaviourId(Pointer(bytes, 88));
            if (owner == null) { owner = "unbound-behaviour"; unsupported.Add("coroutine-owner-unbound"); }
            var handle = BitConverter.ToInt64(bytes, 16);
            var weakness = BitConverter.ToInt32(bytes, 24);
            object? enumerator = null;
            if (handle != -1 && handle != 0)
            {
                if (weakness == 2)
                {
                    var target = Pointer(bytes, 32);
                    if (target != IntPtr.Zero)
                    {
                        // MonoObject pointers must never be cast to managed
                        // objects. A temporary Mono GC handle obtains a normal
                        // managed reference, then is released in this callback.
                        var temporary = createHandle!(target, 0);
                        if (temporary == 0) throw new InvalidOperationException("P0 temporary enumerator handle failed.");
                        try { enumerator = GCHandle.FromIntPtr(new IntPtr(temporary)).Target; }
                        finally { freeHandle!(temporary); }
                    }
                }
                else if (handle < 0 || handle > uint.MaxValue)
                    unsupported.Add("coroutine-enumerator-non-gchandle");
                else enumerator = GCHandle.FromIntPtr(new IntPtr(handle)).Target;
            }
            var baseId = owner + "/" + (enumerator?.GetType().FullName ?? "no-enumerator");
            var view = new CoroutineView
            {
                NativeBytes = (byte[])bytes.Clone(),
                Id = baseId + "[" + coroutines.Values.Count(value => value.Id.StartsWith(baseId + "[", StringComparison.Ordinal)) + "]",
                Enumerator = enumerator, Done = bytes[100] != 0, EnumeratorBased = bytes[128] != 0,
                WaitingFor = Pointer(bytes, 112), ContinueWhenFinished = Pointer(bytes, 104),
                HasAsyncOperation = Pointer(bytes, 120) != IntPtr.Zero
            };
            coroutines.Add(pointer, view);
            if (view.HasAsyncOperation)
            {
                var operation = Read(Pointer(bytes, 120), 72);
                var table = Pointer(operation, 0);
                view.AsyncOperationState = Function(table);
                if (table == IntPtr.Add(engine, 0x1692310))
                {
                    var status = BitConverter.ToInt32(Read(IntPtr.Add(Pointer(bytes, 120), 72), 4), 0);
                    view.AsyncOperationState += ":UnloadSceneOperation:status=" + status;
                    // A completed operation may remain referenced by the coroutine;
                    // keep that lifecycle fact distinct from outstanding I/O.
                    unsupported.Add(status == 2 ? "completed-unload-operation-binding-needs-rebuild"
                        : "coroutine-pending-unload-operation");
                }
                else unsupported.Add("coroutine-native-async-operation");
            }
            if (view.Enumerator == null && !view.Done) unsupported.Add("coroutine-enumerator-unavailable");
            if (view.WaitingFor != IntPtr.Zero) Coroutine(view.WaitingFor, depth + 1);
            if (view.ContinueWhenFinished != IntPtr.Zero) Coroutine(view.ContinueWhenFinished, depth + 1);
            return view;
        }

        private void Walk(IntPtr node, IntPtr head, List<CallbackView> callbacks,
            HashSet<IntPtr> visited, Func<int, string?> objectId, uint timestamp)
        {
            if (node == head) return;
            if (node == IntPtr.Zero || !visited.Add(node) || visited.Count > 4096)
                throw new InvalidOperationException("P0 native callback tree is invalid.");
            var bytes = Read(node, 96);
            if (bytes[25] != 0) throw new InvalidOperationException("P0 unexpected native tree sentinel.");
            Walk(Pointer(bytes, 0), head, callbacks, visited, objectId, timestamp);
            var call = Pointer(bytes, 64); var cleanup = Pointer(bytes, 72);
            var ownerNumber = BitConverter.ToInt32(bytes, 80);
            var owner = ownerNumber == 0 ? "system" : objectId(ownerNumber);
            if (owner == null) { owner = "unbound-object"; unsupported.Add("callback-owner-unbound"); }
            var userData = Pointer(bytes, 56);
            var coroutine = cleanup == IntPtr.Add(engine, 0x8a6950) ? userData : IntPtr.Zero;
            if (userData != IntPtr.Zero && coroutine == IntPtr.Zero)
                unsupported.Add("opaque-callback-user-data:" + Function(call));
            var description = owner + ":time=" + BitConverter.ToInt64(bytes, 32).ToString("x16")
                + ":frame=" + BitConverter.ToInt64(bytes, 40).ToString(CultureInfo.InvariantCulture)
                + ":rate=" + BitConverter.ToInt32(bytes, 48).ToString("x8")
                + ":repeat=" + bytes[52] + ":mode=" + BitConverter.ToInt32(bytes, 84)
                + ":stamp-age=" + unchecked(timestamp - BitConverter.ToUInt32(bytes, 88))
                + ":call=" + Function(call) + ":cleanup=" + Function(cleanup);
            callbacks.Add(new CallbackView { Description = description, OwnerIdentity = owner, NodeAddress = node, NodeBytes = (byte[])bytes.Clone(),
                Coroutine = coroutine, UserDataPresent = userData != IntPtr.Zero,
                WakeTime = BitConverter.ToDouble(bytes, 32), Frame = BitConverter.ToInt64(bytes, 40),
                Rate = BitConverter.ToInt32(bytes, 48), Repeat = bytes[52], Mode = BitConverter.ToInt32(bytes, 84), Call = Function(call),
                Cleanup = Function(cleanup), OwnerInstanceId = ownerNumber,
                TimestampAge = unchecked(timestamp - BitConverter.ToUInt32(bytes, 88)) });
            Walk(Pointer(bytes, 16), head, callbacks, visited, objectId, timestamp);
        }

        private string Function(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return "none";
            var rva = pointer.ToInt64() - engine.ToInt64();
            if (rva >= 0 && rva < engineLength) return "UnityPlayer:" + rva.ToString("x");
            unsupported.Add("callback-function-outside-pinned-engine");
            return "external-function";
        }

        private static IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
        private static byte[] Read(IntPtr address, int length)
        {
            var bytes = new byte[length];
            if (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, bytes,
                new UIntPtr((uint)length), out var read) || read.ToUInt64() != (ulong)length)
                throw new InvalidOperationException("P0 native scheduler memory read failed.");
            return bytes;
        }
        private bool CheckCode(int rva, string expected)
        {
            if (!nativeSchedulerSupported && !LoadingEngineClockProbe.IsPinnedEngineMatched) return false;
            var byteCount = expected.Length / 2;
            if (rva < 0 || rva + byteCount > engineLength)
            {
                if (warnedRvas.Add(rva))
                    Modding.Logger.LogWarn("[HKTAS] Native scheduler RVA 0x" + rva.ToString("x") + " is outside UnityPlayer.dll image bounds; skipping native scheduler hooks.");
                return false;
            }
            var buffer = new byte[byteCount];
            if (!ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(engine, rva), buffer, new UIntPtr((uint)byteCount), out var read)
                || read.ToUInt64() != (ulong)byteCount)
            {
                if (warnedRvas.Add(rva))
                    Modding.Logger.LogWarn("[HKTAS] Failed to read native scheduler code at RVA 0x" + rva.ToString("x") + "; skipping native scheduler hooks.");
                return false;
            }
            var actual = BitConverter.ToString(buffer).Replace("-", "").ToLowerInvariant();
            if (actual != expected)
            {
                if (warnedRvas.Add(rva))
                    Modding.Logger.LogWarn("[HKTAS] Native scheduler code mismatch at RVA 0x" + rva.ToString("x") + "; skipping native scheduler hooks.");
                return false;
            }
            return true;
        }

        private sealed class CoroutineView
        {
            internal byte[] NativeBytes = Array.Empty<byte>();
            internal string Id = "";
            internal string AsyncOperationState = "none";
            internal object? Enumerator;
            internal bool Done, EnumeratorBased, HasAsyncOperation;
            internal IntPtr WaitingFor, ContinueWhenFinished;
        }
        private sealed class CallbackView
        {
            internal string Description = "";
            internal string OwnerIdentity = "";
            internal IntPtr NodeAddress;
            internal byte[] NodeBytes = Array.Empty<byte>();
            internal IntPtr Coroutine;
            internal bool UserDataPresent;
            internal double WakeTime;
            internal long Frame;
            internal int Rate, Mode;
            internal byte Repeat;
            internal string Call = "";
            internal string Cleanup = "";
            internal int OwnerInstanceId;
            internal uint TimestampAge;
        }
    }
}
