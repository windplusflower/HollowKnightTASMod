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
    // Read-only and engine-pinned. No queue changes, coroutine execution,
    // pointer payload export, or calls to IEnumerator.Current/MoveNext.
    internal sealed class LoadingNativeSchedulerDigest
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
        private readonly CreateHandle createHandle;
        private readonly FreeHandle freeHandle;
        private readonly IntPtr engine;
        private readonly int engineLength;
        private readonly Dictionary<IntPtr, CoroutineView> coroutines = new Dictionary<IntPtr, CoroutineView>();
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private Func<IntPtr, string?> behaviourId = _ => null;
        private readonly List<object> inventory = new List<object>();
        private object[] fieldDiagnostics = Array.Empty<object>();

        internal LoadingNativeSchedulerDigest()
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            engine = module.BaseAddress; engineLength = module.ModuleMemorySize;
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var create = GetProcAddress(mono, "mono_gchandle_new");
            var free = GetProcAddress(mono, "mono_gchandle_free");
            if (mono == IntPtr.Zero || create == IntPtr.Zero || free == IntPtr.Zero)
                throw new InvalidOperationException("P0 Mono GC handle accessors are unavailable.");
            createHandle = (CreateHandle)Marshal.GetDelegateForFunctionPointer(create, typeof(CreateHandle));
            freeHandle = (FreeHandle)Marshal.GetDelegateForFunctionPointer(free, typeof(FreeHandle));
            // Matching PDB getter: index 8 into the existing manager table.
            RequireCode(0x629ef0, "b908000000e9068ef4ff");
            RequireCode(0x572d00, "4863c1488d0d264d4601488b04c1c3");
            RequireCode(0x771ec0, "8b414883f8020f94c0c3"); // PreloadManagerOperation.IsDone: status == 2.
        }

        internal object[] Inventory => inventory.ToArray();
        internal string[] Unsupported => unsupported.ToArray();
        internal object[] FieldDiagnostics => fieldDiagnostics;
        internal int AsyncOperationCount => coroutines.Values.Count(value => value.HasAsyncOperation);
        internal void Release() { coroutines.Clear(); behaviourId = _ => null; }

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
        {
            coroutines.Clear(); unsupported.Clear(); behaviourId = _ => "existing-behaviour";
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
                    || !CheckpointHeroInvulnerability.IsBoundIterator(iterator, hero)) continue;
                if (coroutine.Done || coroutine.HasAsyncOperation
                    || coroutine.WaitingFor != IntPtr.Zero || coroutine.ContinueWhenFinished != IntPtr.Zero
                    || callback.Repeat != 0 || double.IsNaN(callback.WakeTime) || double.IsInfinity(callback.WakeTime))
                    throw new InvalidOperationException("Invulnerability native wait binding: done=" + coroutine.Done
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
            if (result.Count > 8) throw new InvalidOperationException("Hero invulnerability exceeds its coroutine bound.");
            return result.ToArray();
        }

        internal string Capture(LoadingFieldDigest fields, Func<int, string?> objectId,
            Func<IntPtr, string?> nativeBehaviourId, bool includeFieldDiagnostics = false)
        {
            coroutines.Clear(); unsupported.Clear(); inventory.Clear(); behaviourId = nativeBehaviourId;
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
            Walk(root, head, callbacks, visited, objectId, timestamp);
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
                        var temporary = createHandle(target, 0);
                        if (temporary == 0) throw new InvalidOperationException("P0 temporary enumerator handle failed.");
                        try { enumerator = GCHandle.FromIntPtr(new IntPtr(temporary)).Target; }
                        finally { freeHandle(temporary); }
                    }
                }
                else if (handle < 0 || handle > uint.MaxValue)
                    unsupported.Add("coroutine-enumerator-non-gchandle");
                else enumerator = GCHandle.FromIntPtr(new IntPtr(handle)).Target;
            }
            var baseId = owner + "/" + (enumerator?.GetType().FullName ?? "no-enumerator");
            var view = new CoroutineView
            {
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
            callbacks.Add(new CallbackView { Description = description, Coroutine = coroutine, UserDataPresent = userData != IntPtr.Zero,
                WakeTime = BitConverter.ToDouble(bytes, 32), Frame = BitConverter.ToInt64(bytes, 40),
                Rate = BitConverter.ToInt32(bytes, 48), Repeat = bytes[52], Mode = BitConverter.ToInt32(bytes, 84), Call = Function(call) });
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
        private void RequireCode(int rva, string expected)
        {
            var actual = BitConverter.ToString(Read(IntPtr.Add(engine, rva), expected.Length / 2)).Replace("-", "").ToLowerInvariant();
            if (actual != expected) throw new InvalidOperationException("P0 native scheduler getter code mismatch.");
        }

        private sealed class CoroutineView
        {
            internal string Id = "";
            internal string AsyncOperationState = "none";
            internal object? Enumerator;
            internal bool Done, EnumeratorBased, HasAsyncOperation;
            internal IntPtr WaitingFor, ContinueWhenFinished;
        }
        private sealed class CallbackView
        {
            internal string Description = "";
            internal IntPtr Coroutine;
            internal bool UserDataPresent;
            internal double WakeTime;
            internal long Frame;
            internal int Rate, Mode;
            internal byte Repeat;
            internal string Call = "";
        }
    }
}
