using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalBoundaryMutationScope : IDisposable
    {
        private readonly Action admission;
        private State state;
        private enum State { Prepared, Active, Faulted, Disposed }
        internal CanonicalBoundaryMutationScope(Action admission)
            => this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        internal void Enter()
        {
            if (state != State.Prepared) throw new InvalidOperationException("Canonical mutation scope was already entered or retired.");
            try { admission(); state = State.Active; }
            catch { state = State.Faulted; throw; }
        }
        internal void Require()
        {
            if (state != State.Active) throw new InvalidOperationException("Canonical mutation scope is not active.");
            try { admission(); }
            catch { state = State.Faulted; throw; }
        }
        public void Dispose() => state = State.Disposed;
    }
}
