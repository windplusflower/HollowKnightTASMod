using System;
using System.Linq;

namespace HollowKnightTAS.Runtime.Input
{
    public sealed partial class FullRunActionSetAdapter
    {
        internal object ObserveSuspendedActionCaches()
        {
            if (!suspended || leases.Count == 0 || leases.Count > 8)
                throw new InvalidOperationException("S0 neutral input has no bounded live action leases.");
            var channels = leases.Select(lease => {
                var actions = GetActions(lease.Set, lease.Channel);
                if (actions.Length > 1024) throw new InvalidOperationException("S0 action cache budget exceeded.");
                var values = actions.Select(action => new { action.Name, value = action.Value,
                    action.WasPressed, action.WasReleased, action.IsPressed }).ToArray();
                if (values.Any(action => action.value != 0f || action.WasPressed || action.WasReleased || action.IsPressed))
                    throw new InvalidOperationException("S0 suspended action cache is not neutral: " + lease.Channel);
                return new { channel = lease.Channel.ToString(), values };
            }).ToArray();
            return new { diagnosticOnly = true, suspended, currentFrame, sampleIndex, channels };
        }
    }
}
