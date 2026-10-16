using System;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Both graphs and shared owners must pass before the first write. This is
    // a preparation/application coordinator, not a rollback of Unity native
    // state: any application failure is terminal and its process must be discarded.
    internal sealed class CanonicalCarryPairPreparation
    {
        private readonly CanonicalPreparedWritePlan main, input;
        private readonly Action sharedOwnerGuard;
        private readonly Func<bool> applicationAdmitted;
        private State state;
        private enum State { Prepared, Applying, Complete, Faulted }
        internal CanonicalCarryPairPreparation(CanonicalPreparedWritePlan main, CanonicalPreparedWritePlan input,
            Action sharedOwnerGuard, Func<bool> applicationAdmitted)
        {
            this.main = main ?? throw new ArgumentNullException(nameof(main));
            this.input = input ?? throw new ArgumentNullException(nameof(input));
            if (ReferenceEquals(main, input)) throw new ArgumentException("Canonical carry domains require separate prepared graphs.");
            if (main.SharesOwnersWith(input)) throw new ArgumentException("Canonical carry graphs cannot both write the same root owner.");
            this.sharedOwnerGuard = sharedOwnerGuard ?? throw new ArgumentNullException(nameof(sharedOwnerGuard));
            this.applicationAdmitted = applicationAdmitted ?? throw new ArgumentNullException(nameof(applicationAdmitted));
            Guard();
        }
        private void GuardAll() { sharedOwnerGuard(); main.Guard(); input.Guard(); }
        internal void Guard()
        {
            if (state != State.Prepared) throw new InvalidOperationException("Canonical carry pair is no longer prepared.");
            GuardAll();
        }
        private void RequireAdmission()
        {
            if (!applicationAdmitted()) throw new InvalidOperationException("Canonical carry application is not qualified at the paused boundary.");
        }
        internal void Apply() => Apply(() => { }, () => { });
        internal void Apply(Action applyDomains, Action verifySourceDomains, Action<string>? reportStage = null)
        {
            if (applyDomains == null || verifySourceDomains == null) throw new ArgumentNullException();
            if (state != State.Prepared) throw new InvalidOperationException("Canonical carry pair was already consumed or faulted.");
            state = State.Applying;
            try {
                reportStage?.Invoke("pair-guard"); RequireAdmission(); GuardAll(); RequireAdmission();
                reportStage?.Invoke("apply-main");
                main.Apply();
                reportStage?.Invoke("apply-input");
                RequireAdmission(); sharedOwnerGuard(); input.Apply();
                reportStage?.Invoke("verify-main-input");
                RequireAdmission(); sharedOwnerGuard(); main.VerifyReadback(); input.VerifyReadback();
                // Preparation guards freeze target values. After actual domain
                // writes those values are intentionally different: validate
                // source values and retained target owners instead.
                RequireAdmission(); applyDomains(); RequireAdmission();
                reportStage?.Invoke("final-verify-main-input");
                main.VerifyReadback(); input.VerifyReadback(); verifySourceDomains(); RequireAdmission();
                state = State.Complete;
            } catch { state = State.Faulted; throw; }
        }
    }
}
