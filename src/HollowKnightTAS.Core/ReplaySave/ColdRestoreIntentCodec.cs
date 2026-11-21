using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using HollowKnightTAS.Core.Cryptography;

namespace HollowKnightTAS.Core.ReplaySave
{
    public static class ColdRestoreIntentCodec
    {
        public const int MaximumBytes = 64 * 1024;

        public static byte[] Serialize(ColdRestoreIntent value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var builder = new StringBuilder(4096);
            builder.Append('{');
            ReplaySaveJson.AppendString(
                builder,
                "baselineObjectSha256",
                value.BaselineObjectSha256);
            AppendBuildFingerprint(builder, value.BuildFingerprint);
            ReplaySaveJson.AppendString(
                builder,
                "createdAtUtc",
                ReplaySaveJson.FormatUtc(value.CreatedAtUtc));
            ReplaySaveJson.AppendString(
                builder,
                "expiresAtUtc",
                ReplaySaveJson.FormatUtc(value.ExpiresAtUtc));
            ReplaySaveJson.AppendString(builder, "intentId", value.IntentId);
            ReplaySaveJson.AppendString(
                builder,
                "journalHeadSha256",
                value.JournalHeadSha256);
            ReplaySaveJson.AppendString(
                builder,
                "operationId",
                value.OperationId);
            if (value.SchemaVersion >= 3)
                ReplaySaveJson.AppendString(builder, "lifecyclePlanObjectSha256", value.LifecyclePlanObjectSha256);
            if (value.SchemaVersion >= 5)
                ReplaySaveJson.AppendString(builder, "slotOverwriteAuthorization", value.SlotOverwriteAuthorization);
            ReplaySaveJson.AppendString(
                builder,
                "operationKind",
                value.OperationKind.ToString());
            ReplaySaveJson.AppendString(
                builder,
                "parentOperationId",
                value.ParentOperationId);
            ReplaySaveJson.AppendString(
                builder,
                "prefixSha256",
                value.PrefixSha256);
            ReplaySaveJson.AppendString(
                builder,
                "replaySaveId",
                value.ReplaySaveId);
            ReplaySaveJson.AppendString(
                builder,
                "requesterSurface",
                value.RequesterSurface);
            ReplaySaveJson.AppendInt32(
                builder,
                "schemaVersion",
                value.SchemaVersion);
            ReplaySaveJson.AppendInt64(
                builder,
                "sourceCommittedMovieTick",
                value.SourceCommittedMovieTick);
            ReplaySaveJson.AppendString(
                builder,
                "sourceMovieObjectSha256",
                value.SourceMovieObjectSha256);
            ReplaySaveJson.AppendInt32(
                builder,
                "sourceProcessId",
                value.SourceProcessId);
            ReplaySaveJson.AppendString(
                builder,
                "sourceProcessStartedAtUtc",
                ReplaySaveJson.FormatUtc(value.SourceProcessStartedAtUtc));
            ReplaySaveJson.AppendInt32(
                builder,
                "sourceSceneEpoch",
                value.SourceSceneEpoch);
            ReplaySaveJson.AppendString(
                builder,
                "sourceSessionId",
                value.SourceSessionId);
            ReplaySaveJson.AppendString(
                builder,
                "targetMovieObjectSha256",
                value.TargetMovieObjectSha256);
            ReplaySaveJson.AppendInt64(
                builder,
                "targetMovieTick",
                value.TargetMovieTick);
            ReplaySaveJson.AppendString(
                builder,
                "targetSemanticSha256",
                value.TargetSemanticSha256);
            builder.Append('}');
            var bytes = ReplaySaveJson.StrictUtf8.GetBytes(builder.ToString());
            if (bytes.Length == 0 || bytes.Length > MaximumBytes)
            {
                throw new InvalidDataException(
                    "Cold-restore intent exceeds its size limit.");
            }

            return bytes;
        }

        public static ColdRestoreIntent Deserialize(byte[] bytes)
        {
            var data = ReplaySaveJson.Deserialize<ColdRestoreIntentData>(
                bytes,
                MaximumBytes);
            try
            {
                if (!Enum.TryParse(
                        data.OperationKind,
                        ignoreCase: false,
                        out ColdRestoreOperationKind operationKind)
                    || !Enum.IsDefined(
                        typeof(ColdRestoreOperationKind),
                        operationKind))
                {
                    throw new InvalidDataException(
                        "Cold-restore operation kind is invalid.");
                }

                var build = data.BuildFingerprint
                            ?? throw new InvalidDataException(
                                "Cold-restore build fingerprint is missing.");
                var value = new ColdRestoreIntent(
                    data.SchemaVersion,
                    data.IntentId ?? string.Empty,
                    data.OperationId ?? string.Empty,
                    operationKind,
                    data.ReplaySaveId ?? string.Empty,
                    data.SourceSessionId ?? string.Empty,
                    data.SourceProcessId,
                    ReplaySaveJson.ParseUtc(
                        data.SourceProcessStartedAtUtc,
                        "sourceProcessStartedAtUtc"),
                    ReplaySaveJson.ParseUtc(
                        data.CreatedAtUtc,
                        "createdAtUtc"),
                    ReplaySaveJson.ParseUtc(
                        data.ExpiresAtUtc,
                        "expiresAtUtc"),
                    data.SourceCommittedMovieTick,
                    data.SourceSceneEpoch,
                    data.BaselineObjectSha256 ?? string.Empty,
                    data.SourceMovieObjectSha256 ?? string.Empty,
                    data.TargetMovieObjectSha256 ?? string.Empty,
                    data.PrefixSha256 ?? string.Empty,
                    data.JournalHeadSha256 ?? string.Empty,
                    data.TargetMovieTick,
                    data.TargetSemanticSha256 ?? string.Empty,
                    data.ParentOperationId ?? string.Empty,
                    data.RequesterSurface ?? string.Empty,
                    new ColdRestoreBuildFingerprint(
                        build.EnvironmentManifestSha256 ?? string.Empty,
                        build.GameExecutableSha256 ?? string.Empty,
                        build.UnityPlayerSha256 ?? string.Empty,
                        build.AssemblyCSharpSha256 ?? string.Empty,
                        build.RuntimeAssemblySha256 ?? string.Empty,
                        build.CompanionAssemblySha256 ?? string.Empty,
                        build.StartupProfileSha256 ?? string.Empty,
                        build.ObserverAssemblySha256 ?? string.Empty),
                    data.LifecyclePlanObjectSha256 ?? string.Empty, data.SlotOverwriteAuthorization ?? string.Empty);
                RequireCanonical(bytes, Serialize(value));
                return value;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidDataException)
            {
                throw new InvalidDataException(
                    "Cold-restore intent is invalid.",
                    exception);
            }
        }

        public static string ComputeSha256(ColdRestoreIntent value)
        {
            return Sha256Utility.ComputeHex(Serialize(value));
        }

        private static void AppendBuildFingerprint(
            StringBuilder builder,
            ColdRestoreBuildFingerprint value)
        {
            if (builder[builder.Length - 1] != '{')
            {
                builder.Append(',');
            }

            builder.Append("\"buildFingerprint\":{");
            ReplaySaveJson.AppendString(
                builder,
                "assemblyCSharpSha256",
                value.AssemblyCSharpSha256);
            ReplaySaveJson.AppendString(
                builder,
                "companionAssemblySha256",
                value.CompanionAssemblySha256);
            ReplaySaveJson.AppendString(
                builder,
                "environmentManifestSha256",
                value.EnvironmentManifestSha256);
            ReplaySaveJson.AppendString(
                builder,
                "gameExecutableSha256",
                value.GameExecutableSha256);
            ReplaySaveJson.AppendString(
                builder,
                "observerAssemblySha256",
                value.ObserverAssemblySha256);
            ReplaySaveJson.AppendString(
                builder,
                "runtimeAssemblySha256",
                value.RuntimeAssemblySha256);
            ReplaySaveJson.AppendString(
                builder,
                "startupProfileSha256",
                value.StartupProfileSha256);
            ReplaySaveJson.AppendString(
                builder,
                "unityPlayerSha256",
                value.UnityPlayerSha256);
            builder.Append('}');
        }

        private static void RequireCanonical(byte[] actual, byte[] expected)
        {
            if (actual == null || actual.Length != expected.Length)
            {
                throw new InvalidDataException(
                    "Cold-restore intent JSON is not canonical.");
            }

            var difference = 0;
            for (var index = 0; index < actual.Length; index++)
            {
                difference |= actual[index] ^ expected[index];
            }

            if (difference != 0)
            {
                throw new InvalidDataException(
                    "Cold-restore intent JSON is not canonical.");
            }
        }
    }

    public enum ColdRestoreIntentValidationCode : byte
    {
        Ready = 1,
        NotYetValid = 2,
        Expired = 3,
        BuildMismatch = 4
    }

    public sealed class ColdRestoreIntentValidationResult
    {
        public ColdRestoreIntentValidationResult(
            ColdRestoreIntentValidationCode code,
            string detail)
        {
            if (!Enum.IsDefined(
                    typeof(ColdRestoreIntentValidationCode),
                    code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Code = code;
            Detail = detail ?? string.Empty;
        }

        public ColdRestoreIntentValidationCode Code { get; }
        public string Detail { get; }
        public bool Success => Code == ColdRestoreIntentValidationCode.Ready;
    }

    public static class ColdRestoreIntentValidator
    {
        public static ColdRestoreIntentValidationResult ValidateForClaim(
            ColdRestoreIntent intent,
            ColdRestoreBuildFingerprint actualBuild,
            DateTimeOffset nowUtc)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            if (actualBuild == null)
            {
                throw new ArgumentNullException(nameof(actualBuild));
            }

            var now = nowUtc.ToUniversalTime();
            if (now < intent.CreatedAtUtc)
            {
                return new ColdRestoreIntentValidationResult(
                    ColdRestoreIntentValidationCode.NotYetValid,
                    "Intent creation time is in the future.");
            }

            if (intent.IsExpired(now))
            {
                return new ColdRestoreIntentValidationResult(
                    ColdRestoreIntentValidationCode.Expired,
                    "Intent has expired.");
            }

            if (!intent.BuildFingerprint.Equals(actualBuild))
            {
                Trace.TraceWarning(
                    $"ColdRestoreIntentValidator.ValidateForClaim: build fingerprint mismatch for intent '{intent.IntentId}'; continuing.");
            }

            return new ColdRestoreIntentValidationResult(
                ColdRestoreIntentValidationCode.Ready,
                string.Empty);
        }
    }

    [DataContract]
    internal sealed class ColdRestoreIntentData
    {
        [DataMember(Name = "baselineObjectSha256")]
        public string? BaselineObjectSha256 { get; set; }

        [DataMember(Name = "buildFingerprint")]
        public ColdRestoreBuildFingerprintData? BuildFingerprint { get; set; }

        [DataMember(Name = "createdAtUtc")]
        public string? CreatedAtUtc { get; set; }

        [DataMember(Name = "expiresAtUtc")]
        public string? ExpiresAtUtc { get; set; }

        [DataMember(Name = "intentId")]
        public string? IntentId { get; set; }

        [DataMember(Name = "journalHeadSha256")]
        public string? JournalHeadSha256 { get; set; }

        [DataMember(Name = "operationId")]
        public string? OperationId { get; set; }

        [DataMember(Name = "operationKind")]
        public string? OperationKind { get; set; }

        [DataMember(Name = "parentOperationId")]
        public string? ParentOperationId { get; set; }

        [DataMember(Name = "prefixSha256")]
        public string? PrefixSha256 { get; set; }

        [DataMember(Name = "replaySaveId")]
        public string? ReplaySaveId { get; set; }

        [DataMember(Name = "requesterSurface")]
        public string? RequesterSurface { get; set; }

        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "sourceCommittedMovieTick")]
        public long SourceCommittedMovieTick { get; set; }

        [DataMember(Name = "sourceMovieObjectSha256")]
        public string? SourceMovieObjectSha256 { get; set; }

        [DataMember(Name = "sourceProcessId")]
        public int SourceProcessId { get; set; }

        [DataMember(Name = "sourceProcessStartedAtUtc")]
        public string? SourceProcessStartedAtUtc { get; set; }

        [DataMember(Name = "sourceSceneEpoch")]
        public int SourceSceneEpoch { get; set; }

        [DataMember(Name = "sourceSessionId")]
        public string? SourceSessionId { get; set; }

        [DataMember(Name = "targetMovieObjectSha256")]
        public string? TargetMovieObjectSha256 { get; set; }

        [DataMember(Name = "lifecyclePlanObjectSha256")]
        public string? LifecyclePlanObjectSha256 { get; set; }
        [DataMember(Name = "slotOverwriteAuthorization")]
        public string? SlotOverwriteAuthorization { get; set; }

        [DataMember(Name = "targetMovieTick")]
        public long TargetMovieTick { get; set; }

        [DataMember(Name = "targetSemanticSha256")]
        public string? TargetSemanticSha256 { get; set; }
    }

    [DataContract]
    internal sealed class ColdRestoreBuildFingerprintData
    {
        [DataMember(Name = "assemblyCSharpSha256")]
        public string? AssemblyCSharpSha256 { get; set; }

        [DataMember(Name = "companionAssemblySha256")]
        public string? CompanionAssemblySha256 { get; set; }

        [DataMember(Name = "environmentManifestSha256")]
        public string? EnvironmentManifestSha256 { get; set; }

        [DataMember(Name = "gameExecutableSha256")]
        public string? GameExecutableSha256 { get; set; }

        [DataMember(Name = "observerAssemblySha256")]
        public string? ObserverAssemblySha256 { get; set; }

        [DataMember(Name = "runtimeAssemblySha256")]
        public string? RuntimeAssemblySha256 { get; set; }

        [DataMember(Name = "startupProfileSha256")]
        public string? StartupProfileSha256 { get; set; }

        [DataMember(Name = "unityPlayerSha256")]
        public string? UnityPlayerSha256 { get; set; }
    }
}
