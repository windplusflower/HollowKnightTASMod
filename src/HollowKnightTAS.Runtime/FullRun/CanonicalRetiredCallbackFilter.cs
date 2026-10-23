using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalRetiredCallbackFilter
    {
        internal static Delegate? Filter(Delegate? original, Func<Delegate, bool> remove, out int removed)
        {
            removed = 0;
            var calls = original?.GetInvocationList() ?? Array.Empty<Delegate>();
            if (calls.Length > 256) throw new InvalidDataException("Canonical UI callback invocation bound exceeded.");
            Delegate? result = null;
            foreach (var callback in calls) {
                if (remove(callback)) removed++;
                else result = Delegate.Combine(result, callback);
            }
            return removed == 0 ? original : result;
        }
    }
}
