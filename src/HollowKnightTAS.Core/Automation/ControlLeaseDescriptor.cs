using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HollowKnightTAS.Core.Automation
{
    public sealed class ControlLeaseDescriptor
    {
        public ControlLeaseDescriptor(
            string leaseId,
            string clientId,
            IEnumerable<string> scopes,
            DateTimeOffset issuedAtUtc,
            DateTimeOffset expiresAtUtc,
            string sessionId,
            string manifestSha256)
        {
            LeaseId = leaseId;
            ClientId = clientId;
            Scopes = (scopes
                      ?? throw new ArgumentNullException(nameof(scopes)))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (Scopes.Count == 0
                || Scopes.Any(
                    scope =>
                        !AutomationScope.IsKnown(scope)
                        || AutomationScope.IsReadOnly(scope))
                || expiresAtUtc <= issuedAtUtc)
            {
                throw new ArgumentException(
                    "Control lease scopes or lifetime are invalid.");
            }

            IssuedAtUtc = issuedAtUtc;
            ExpiresAtUtc = expiresAtUtc;
            SessionId = sessionId;
            ManifestSha256 = manifestSha256;
        }

        public string LeaseId { get; }
        public string ClientId { get; }
        public IReadOnlyList<string> Scopes { get; }
        public DateTimeOffset IssuedAtUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public string SessionId { get; }
        public string ManifestSha256 { get; }

        public bool Covers(
            string clientId,
            string leaseId,
            string scope,
            string sessionId,
            string manifestSha256,
            DateTimeOffset nowUtc)
        {
            var covered = nowUtc < ExpiresAtUtc
                          && string.Equals(
                              ClientId,
                              clientId,
                              StringComparison.Ordinal)
                          && string.Equals(
                              LeaseId,
                              leaseId,
                              StringComparison.Ordinal)
                          && string.Equals(
                              SessionId,
                              sessionId,
                              StringComparison.Ordinal)
                          && Scopes.Contains(scope, StringComparer.Ordinal);
            if (covered
                && !string.Equals(
                    ManifestSha256,
                    manifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"ControlLeaseDescriptor.Covers: ManifestSha256 differs for lease '{LeaseId}' (lease={ManifestSha256}, current={manifestSha256}); keeping lease valid.");
            }

            return covered;
        }
    }
}
