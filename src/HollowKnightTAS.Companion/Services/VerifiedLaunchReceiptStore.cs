using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.Services
{
    public interface ITrustedSourceLaunchVerifier
    {
        void RequireTrusted(
            RuntimeSessionClient session,
            StartupProfileAttestation attestation);
    }

    public sealed class VerifiedLaunchReceipt
    {
        internal VerifiedLaunchReceipt(
            string companionInstanceId,
            string runId,
            int processId,
            DateTimeOffset processStartedAtUtc,
            DateTimeOffset recordedAtUtc,
            string capabilityId,
            string profileId,
            string startupPolicy,
            string startupProfileSha256,
            string gameExecutableSha256,
            string unityPlayerSha256,
            string assemblyCSharpSha256,
            string launcherEvidenceSha256)
        {
            CompanionInstanceId = companionInstanceId;
            RunId = runId;
            ProcessId = processId;
            ProcessStartedAtUtc = processStartedAtUtc.ToUniversalTime();
            RecordedAtUtc = recordedAtUtc.ToUniversalTime();
            CapabilityId = capabilityId;
            ProfileId = profileId;
            StartupPolicy = startupPolicy;
            StartupProfileSha256 = startupProfileSha256;
            GameExecutableSha256 = gameExecutableSha256;
            UnityPlayerSha256 = unityPlayerSha256;
            AssemblyCSharpSha256 = assemblyCSharpSha256;
            LauncherEvidenceSha256 = launcherEvidenceSha256;
        }

        public string CompanionInstanceId { get; }
        public string RunId { get; }
        public int ProcessId { get; }
        public DateTimeOffset ProcessStartedAtUtc { get; }
        public DateTimeOffset RecordedAtUtc { get; }
        public string CapabilityId { get; }
        public string ProfileId { get; }
        public string StartupPolicy { get; }
        public string StartupProfileSha256 { get; }
        public string GameExecutableSha256 { get; }
        public string UnityPlayerSha256 { get; }
        public string AssemblyCSharpSha256 { get; }
        public string LauncherEvidenceSha256 { get; }
    }

    public sealed class VerifiedLaunchReceiptStore
    {
        private const string SchemaVersion = "1";
        private const int MaximumReceiptBytes = 32768;

        private static readonly string[] SignedFieldNames =
        {
            "assemblyCSharpSha256",
            "capabilityId",
            "companionInstanceId",
            "gameExecutableSha256",
            "launcherEvidenceSha256",
            "processId",
            "processStartTimeUtcTicks",
            "profileId",
            "recordedAtUtc",
            "runId",
            "schemaVersion",
            "startupPolicy",
            "startupProfileSha256",
            "unityPlayerSha256"
        };

        private readonly string root;
        private readonly string companionInstanceId;
        private readonly byte[] secret;

        public VerifiedLaunchReceiptStore(
            string root,
            string companionInstanceId,
            byte[] secret)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException(
                    "A verified-launch receipt root is required.",
                    nameof(root));
            }
            if (!IpcIdentifier.IsValid(companionInstanceId, 128))
            {
                throw new ArgumentException(
                    "The Companion instance ID is invalid.",
                    nameof(companionInstanceId));
            }
            if (secret == null || secret.Length < 32)
            {
                throw new ArgumentException(
                    "The verified-launch receipt secret is too short.",
                    nameof(secret));
            }

            this.root = Path.GetFullPath(root);
            this.companionInstanceId = companionInstanceId;
            this.secret = (byte[])secret.Clone();
            Directory.CreateDirectory(this.root);
        }

        public VerifiedLaunchReceipt Record(
            VerifiedStartupProfile profile,
            string runId,
            IColdRestoreGameLaunchHandle launch)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            if (!IpcIdentifier.IsValid(runId, 96))
            {
                throw new ArgumentException(
                    "The startup run ID is invalid.",
                    nameof(runId));
            }
            if (launch == null)
            {
                throw new ArgumentNullException(nameof(launch));
            }
            if (launch.ProcessId <= 0 || launch.HasExited)
            {
                throw new InvalidOperationException(
                    "A verified launch receipt requires a live process.");
            }

            var recordedAtUtc = DateTimeOffset.UtcNow;
            if (launch.ProcessStartedAtUtc > recordedAtUtc.AddSeconds(1))
            {
                throw new InvalidDataException(
                    "The verified launch process start time is in the future.");
            }

            var receipt = new VerifiedLaunchReceipt(
                companionInstanceId,
                runId,
                launch.ProcessId,
                launch.ProcessStartedAtUtc,
                recordedAtUtc,
                VerifiedStartupProfile.CapabilityId,
                VerifiedStartupProfile.ProfileId,
                VerifiedStartupProfile.StartupPolicy,
                profile.StartupProfileSha256,
                profile.GameExecutableSha256,
                profile.UnityPlayerSha256,
                profile.AssemblyCSharpSha256,
                Sha256Utility.ComputeUtf8Hex(
                    launch.LauncherEvidence ?? string.Empty));
            var bytes = Serialize(receipt);
            var path = ReceiptPath(runId);
            var temporary = Path.Combine(
                root,
                ".launch-receipt-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteDurable(temporary, bytes);
                File.Move(temporary, path, false);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return receipt;
        }

        public VerifiedLaunchReceipt RequireValid(
            VerifiedStartupProfile profile,
            string runId,
            int processId,
            long processStartTimeUtcTicks)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            if (!IpcIdentifier.IsValid(runId, 96)
                || processId <= 0
                || processStartTimeUtcTicks <= 0)
            {
                throw new InvalidDataException(
                    "The source launch binding is invalid.");
            }

            var receipt = Deserialize(ReadBounded(ReceiptPath(runId)));
            if (!string.Equals(
                    receipt.CompanionInstanceId,
                    companionInstanceId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.RunId,
                    runId,
                    StringComparison.Ordinal)
                || receipt.ProcessId != processId
                || receipt.ProcessStartedAtUtc.UtcTicks
                   != processStartTimeUtcTicks)
            {
                throw new InvalidDataException(
                    "The source process is not bound to the trusted Companion launch receipt.");
            }

            if (!string.Equals(
                    receipt.CapabilityId,
                    VerifiedStartupProfile.CapabilityId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.ProfileId,
                    VerifiedStartupProfile.ProfileId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.StartupPolicy,
                    VerifiedStartupProfile.StartupPolicy,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"VerifiedLaunchReceiptStore.RequireValid: startup metadata differs for run '{runId}' (capability={receipt.CapabilityId}, profile={receipt.ProfileId}, policy={receipt.StartupPolicy}); continuing.");
            }

            if (!string.Equals(
                    receipt.StartupProfileSha256,
                    profile.StartupProfileSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.GameExecutableSha256,
                    profile.GameExecutableSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.UnityPlayerSha256,
                    profile.UnityPlayerSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    receipt.AssemblyCSharpSha256,
                    profile.AssemblyCSharpSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"VerifiedLaunchReceiptStore.RequireValid: build/environment hashes differ for run '{runId}'; continuing.");
            }

            return receipt;
        }

        private byte[] Serialize(VerifiedLaunchReceipt receipt)
        {
            var signed = SignedFields(receipt);
            var fields = new Dictionary<string, string>(
                signed,
                StringComparer.Ordinal)
            {
                ["macSha256"] = ComputeMac(IpcPayloadCodec.Serialize(signed))
            };
            return IpcPayloadCodec.Serialize(fields);
        }

        private VerifiedLaunchReceipt Deserialize(byte[] bytes)
        {
            var decoded = IpcPayloadCodec.TryDeserialize(bytes);
            var fields = decoded.Fields;
            if (!decoded.Success || fields == null)
            {
                throw new InvalidDataException(
                    "Verified launch receipt is invalid: "
                    + decoded.ErrorCode + ".");
            }
            if (fields.Count != SignedFieldNames.Length + 1
                || !fields.ContainsKey("macSha256")
                || SignedFieldNames.Any(name => !fields.ContainsKey(name)))
            {
                throw new InvalidDataException(
                    "Verified launch receipt fields are not exact.");
            }

            var signed = fields
                .Where(value => value.Key != "macSha256")
                .ToDictionary(
                    value => value.Key,
                    value => value.Value,
                    StringComparer.Ordinal);
            var expectedMac = ComputeMac(IpcPayloadCodec.Serialize(signed));
            if (!FixedHexEquals(expectedMac, fields["macSha256"]))
            {
                throw new InvalidDataException(
                    "Verified launch receipt authentication failed.");
            }
            if (!string.Equals(
                    fields["schemaVersion"],
                    SchemaVersion,
                    StringComparison.Ordinal)
                || !IpcIdentifier.IsValid(fields["companionInstanceId"], 128)
                || !IpcIdentifier.IsValid(fields["runId"], 96)
                || !int.TryParse(
                    fields["processId"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processId)
                || processId <= 0
                || !long.TryParse(
                    fields["processStartTimeUtcTicks"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var processStartTicks)
                || processStartTicks <= 0
                || !DateTimeOffset.TryParseExact(
                    fields["recordedAtUtc"],
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var recordedAtUtc)
                || !MovieProtocolV1.IsLowerSha256(
                    fields["startupProfileSha256"])
                || !MovieProtocolV1.IsLowerSha256(
                    fields["gameExecutableSha256"])
                || !MovieProtocolV1.IsLowerSha256(
                    fields["unityPlayerSha256"])
                || !MovieProtocolV1.IsLowerSha256(
                    fields["assemblyCSharpSha256"])
                || !MovieProtocolV1.IsLowerSha256(
                    fields["launcherEvidenceSha256"]))
            {
                throw new InvalidDataException(
                    "Verified launch receipt values are invalid.");
            }

            var processStartedAtUtc = new DateTimeOffset(
                processStartTicks,
                TimeSpan.Zero);
            if (recordedAtUtc < processStartedAtUtc.AddSeconds(-1)
                || recordedAtUtc > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                throw new InvalidDataException(
                    "Verified launch receipt time binding is invalid.");
            }

            return new VerifiedLaunchReceipt(
                fields["companionInstanceId"],
                fields["runId"],
                processId,
                processStartedAtUtc,
                recordedAtUtc,
                fields["capabilityId"],
                fields["profileId"],
                fields["startupPolicy"],
                fields["startupProfileSha256"],
                fields["gameExecutableSha256"],
                fields["unityPlayerSha256"],
                fields["assemblyCSharpSha256"],
                fields["launcherEvidenceSha256"]);
        }

        private static Dictionary<string, string> SignedFields(
            VerifiedLaunchReceipt receipt)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["assemblyCSharpSha256"] = receipt.AssemblyCSharpSha256,
                ["capabilityId"] = receipt.CapabilityId,
                ["companionInstanceId"] = receipt.CompanionInstanceId,
                ["gameExecutableSha256"] = receipt.GameExecutableSha256,
                ["launcherEvidenceSha256"] =
                    receipt.LauncherEvidenceSha256,
                ["processId"] = receipt.ProcessId.ToString(
                    CultureInfo.InvariantCulture),
                ["processStartTimeUtcTicks"] =
                    receipt.ProcessStartedAtUtc.UtcTicks.ToString(
                        CultureInfo.InvariantCulture),
                ["profileId"] = receipt.ProfileId,
                ["recordedAtUtc"] = receipt.RecordedAtUtc.ToString(
                    "O",
                    CultureInfo.InvariantCulture),
                ["runId"] = receipt.RunId,
                ["schemaVersion"] = SchemaVersion,
                ["startupPolicy"] = receipt.StartupPolicy,
                ["startupProfileSha256"] =
                    receipt.StartupProfileSha256,
                ["unityPlayerSha256"] = receipt.UnityPlayerSha256
            };
        }

        private string ComputeMac(byte[] canonicalSignedBytes)
        {
            using (var hmac = new HMACSHA256(secret))
            {
                return Convert.ToHexString(
                        hmac.ComputeHash(canonicalSignedBytes))
                    .ToLowerInvariant();
            }
        }

        private string ReceiptPath(string runId)
        {
            return Path.Combine(
                root,
                Sha256Utility.ComputeUtf8Hex(runId) + ".launch.json");
        }

        private static byte[] ReadBounded(string path)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumReceiptBytes)
                {
                    throw new InvalidDataException(
                        "Verified launch receipt size is invalid.");
                }

                var bytes = new byte[checked((int)stream.Length)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        offset,
                        bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    offset += read;
                }

                return bytes;
            }
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool FixedHexEquals(string expected, string actual)
        {
            if (expected.Length != actual.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < expected.Length; index++)
            {
                difference |= expected[index] ^ actual[index];
            }

            return difference == 0;
        }
    }

    public sealed class VerifiedSourceLaunchReceiptVerifier :
        ITrustedSourceLaunchVerifier
    {
        private readonly VerifiedLaunchReceiptStore store;
        private readonly VerifiedStartupProfile profile;

        public VerifiedSourceLaunchReceiptVerifier(
            VerifiedLaunchReceiptStore store,
            VerifiedStartupProfile profile)
        {
            this.store = store
                         ?? throw new ArgumentNullException(nameof(store));
            this.profile = profile
                           ?? throw new ArgumentNullException(nameof(profile));
        }

        public void RequireTrusted(
            RuntimeSessionClient session,
            StartupProfileAttestation attestation)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            if (attestation == null)
            {
                throw new ArgumentNullException(nameof(attestation));
            }
            if (attestation.Status
                != StartupProfileAttestationStatus.Verified
                || string.IsNullOrEmpty(attestation.RunId))
            {
                Trace.TraceWarning(
                    $"VerifiedSourceLaunchReceiptVerifier.RequireTrusted: attestation status '{attestation.Status}' or runId '{attestation.RunId}' is not receipt-eligible; continuing.");
                return;
            }

            try
            {
                store.RequireValid(
                    profile,
                    attestation.RunId,
                    session.GameProcessId,
                    session.GameProcessStartTimeUtcTicks);
            }
            catch (Exception exception)
                when (exception is FileNotFoundException
                      || exception is DirectoryNotFoundException
                      || exception is InvalidDataException)
            {
                Trace.TraceWarning(
                    $"VerifiedSourceLaunchReceiptVerifier.RequireTrusted: launch receipt check warning ({exception.Message}); continuing.");
            }
        }
    }
}
