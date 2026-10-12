using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalPreparedWritePlan
    {
        private readonly Action guard, write, readback;
        private readonly object[] owners;
        private State state;
        private enum State { Prepared, Applying, Complete, Faulted }
        internal CanonicalPreparedWritePlan(Action guard, Action write, Action readback, object[]? owners = null)
        {
            this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
            this.write = write ?? throw new ArgumentNullException(nameof(write));
            this.readback = readback ?? throw new ArgumentNullException(nameof(readback));
            this.owners = owners == null ? Array.Empty<object>() : (object[])owners.Clone();
            if (this.owners.Length > CanonicalCarryGraphCodec.MaximumNodes) throw new ArgumentException("Canonical prepared owner bound exceeded.");
        }
        internal void Guard()
        {
            if (state != State.Prepared) throw new InvalidOperationException("Canonical write preparation was already consumed or faulted.");
            guard();
        }
        internal void Apply()
        {
            if (state != State.Prepared) throw new InvalidOperationException("Canonical write preparation was already consumed or faulted.");
            state = State.Applying;
            try { guard(); write(); readback(); state = State.Complete; }
            catch { state = State.Faulted; throw; }
        }
        internal void VerifyReadback()
        {
            if (state != State.Complete) throw new InvalidOperationException("Canonical graph has not completed application.");
            readback();
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
