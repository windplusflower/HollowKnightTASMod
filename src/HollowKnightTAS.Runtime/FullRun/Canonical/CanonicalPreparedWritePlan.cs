using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalPreparedWritePlan
    {
        private readonly Action guard, write, readback;
        private readonly Action? reapplyGuard, reapplyWrite;
        private readonly object[] owners;
        private volatile State state;
        private bool reapplyConsumed;
        private int appliedThread;
        private enum State { Prepared, Applying, Reapplying, Complete, Faulted }
        internal CanonicalPreparedWritePlan(Action guard, Action write, Action readback, object[]? owners = null,
            Action? reapplyGuard = null, Action? reapplyWrite = null)
        {
            this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
            this.write = write ?? throw new ArgumentNullException(nameof(write));
            this.readback = readback ?? throw new ArgumentNullException(nameof(readback));
            this.owners = owners == null ? Array.Empty<object>() : (object[])owners.Clone();
            if ((reapplyGuard == null) != (reapplyWrite == null)) throw new ArgumentException("Resident graph replay guards and writes must be paired.");
            this.reapplyGuard = reapplyGuard; this.reapplyWrite = reapplyWrite;
            if (this.owners.Length > CanonicalCarryGraphCodec.MaximumNodes) throw new ArgumentException("Canonical prepared owner bound exceeded.");
        }
        internal void Guard()
        {
            Require(State.Prepared, "Canonical write preparation was already consumed or faulted.");
            try { guard(); RequireActive(State.Prepared); } catch { if (reapplyGuard != null) state = State.Faulted; throw; }
        }
        internal void Apply()
        {
            Require(State.Prepared, "Canonical write preparation was already consumed or faulted.");
            state = State.Applying;
            try { guard(); RequireActive(State.Applying); write(); RequireActive(State.Applying); readback(); RequireActive(State.Applying);
                appliedThread = System.Threading.Thread.CurrentThread.ManagedThreadId; state = State.Complete; }
            catch { state = State.Faulted; throw; }
        }
        // Validate this exact frozen incoming set before a sealed external
        // rebind moves overlapping slots. Both plans still perform their full
        // readback; the dependency may only be another guarded source plan.
        internal void ApplyWithGuardedDependency(Action dependency)
        {
            Require(State.Prepared, "Canonical write preparation was already consumed or faulted.");
            state = State.Applying;
            try { guard(); RequireActive(State.Applying); dependency(); RequireActive(State.Applying); write(); RequireActive(State.Applying);
                readback(); RequireActive(State.Applying); appliedThread = System.Threading.Thread.CurrentThread.ManagedThreadId; state = State.Complete; }
            catch { state = State.Faulted; throw; }
        }
        internal void VerifyReadback()
        {
            Require(State.Complete, "Canonical graph has not completed application.");
            try { readback(); RequireActive(State.Complete); } catch { if (reapplyGuard != null) state = State.Faulted; throw; }
        }
        // Native operations run inside this single admitted window. Replay uses
        // every original graph owner and prepared value, never a new allocation
        // pass. Mutable fields may change inside the callback; roots, native
        // pointers, delegate descriptors and external identities may not.
        internal void ReapplyOriginalGraph(Action admission, Action controlledNativeOperations)
        {
            try {
                if (reapplyGuard == null || reapplyWrite == null || admission == null || controlledNativeOperations == null || state != State.Complete
                    || reapplyConsumed || appliedThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("Resident graph replay is absent, consumed, faulted, or on another thread.");
                reapplyConsumed = true; state = State.Reapplying;
                admission(); RequireActive(State.Reapplying); reapplyGuard(); RequireActive(State.Reapplying); readback(); RequireActive(State.Reapplying);
                controlledNativeOperations(); RequireActive(State.Reapplying);
                admission(); RequireActive(State.Reapplying);
                if (appliedThread != System.Threading.Thread.CurrentThread.ManagedThreadId) throw new InvalidOperationException("Resident graph replay changed execution thread.");
                reapplyGuard(); RequireActive(State.Reapplying); reapplyWrite(); RequireActive(State.Reapplying); admission(); RequireActive(State.Reapplying);
                reapplyGuard(); RequireActive(State.Reapplying); readback(); RequireActive(State.Reapplying); state = State.Complete;
            } catch { state = State.Faulted; throw; }
        }
        private void Require(State expected, string message)
        {
            if (state == expected) return;
            if (reapplyGuard != null) state = State.Faulted;
            throw new InvalidOperationException(message);
        }
        private void RequireActive(State expected)
        {
            if (reapplyGuard != null && state != expected) throw new InvalidOperationException("Resident graph transaction was faulted by a nested operation.");
        }
        internal bool SharesOwnersWith(CanonicalPreparedWritePlan other)
        {
            var set = new HashSet<object>(owners, new References());
            foreach (var owner in other.owners) if (set.Contains(owner)) return true;
            return false;
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
