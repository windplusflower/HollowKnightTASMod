using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Serialization;

namespace HollowKnightTAS.Core.Deployment
{
    public sealed class CompanionManifestDecodeResult
    {
        internal CompanionManifestDecodeResult(
            bool success,
            CompanionBundleManifest? manifest,
            string errorCode,
            string error)
        {
            Success = success;
            Manifest = manifest;
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Success { get; }
        public CompanionBundleManifest? Manifest { get; }
        public string ErrorCode { get; }
        public string Error { get; }
    }

    public static class CompanionManifestCodec
    {
        public const int MaximumManifestBytes = 1024 * 1024;
        public const int MaximumFiles = 4096;
        public const string Product = "HollowKnightTAS.Companion";

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(false, true);

        public static byte[] SerializeUnsigned(
            CompanionBundleManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var builder = new StringBuilder(1024);
            builder.Append("{\"schemaVersion\":");
            builder.Append(
                manifest.SchemaVersion.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"product\":");
            CanonicalJsonWriter.AppendString(builder, manifest.Product);
            builder.Append(",\"version\":");
            CanonicalJsonWriter.AppendString(builder, manifest.Version);
            builder.Append(",\"rid\":");
            CanonicalJsonWriter.AppendString(builder, manifest.Rid);
            builder.Append(",\"runtimeProtocolMin\":");
            builder.Append(
                manifest.RuntimeProtocolMinimum.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"runtimeProtocolMax\":");
            builder.Append(
                manifest.RuntimeProtocolMaximum.ToString(
                    CultureInfo.InvariantCulture));
            builder.Append(",\"entrypoint\":");
            CanonicalJsonWriter.AppendString(builder, manifest.Entrypoint);
            builder.Append(",\"files\":[");
            for (var index = 0; index < manifest.Files.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var file = manifest.Files[index];
                builder.Append("{\"path\":");
                CanonicalJsonWriter.AppendString(builder, file.Path);
                builder.Append(",\"sha256\":");
                CanonicalJsonWriter.AppendString(builder, file.Sha256);
                builder.Append('}');
            }

            builder.Append("]}");
            return StrictUtf8.GetBytes(builder.ToString());
        }

        public static byte[] Serialize(
            CompanionBundleManifest manifest)
        {
            var unsigned = StrictUtf8.GetString(
                SerializeUnsigned(manifest));
            var builder = new StringBuilder(
                unsigned.Length + manifest.Signature.Length + 32);
            builder.Append(unsigned, 0, unsigned.Length - 1);
            builder.Append(",\"signature\":");
            CanonicalJsonWriter.AppendString(
                builder,
                manifest.Signature);
            builder.Append('}');
            var result = StrictUtf8.GetBytes(builder.ToString());
            if (result.Length > MaximumManifestBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(manifest),
                    "Companion manifest exceeds 1 MiB.");
            }

            return result;
        }

        public static CompanionManifestDecodeResult TryDeserialize(
            byte[] bytes)
        {
            return TryDeserialize(bytes, requireCanonical: false);
        }

        public static CompanionManifestDecodeResult TryDeserialize(
            byte[] bytes,
            bool requireCanonical)
        {
            if (bytes == null)
            {
                return Fail("NullManifest", "Manifest is null.");
            }

            if (bytes.Length == 0 || bytes.Length > MaximumManifestBytes)
            {
                return Fail(
                    "InvalidLength",
                    "Manifest length must be in [1, 1048576].");
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                return Fail("MalformedUtf8", exception.Message);
            }

            var reader = new StrictJsonReader(json);
            if (!reader.TryReadObjectStart(out var error)
                || !reader.TryReadPropertyName(
                    "schemaVersion",
                    out error)
                || !reader.TryReadInt64(
                    out var schemaLong,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName("product", out error)
                || !reader.TryReadString(
                    out var product,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName("version", out error)
                || !reader.TryReadString(
                    out var version,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName("rid", out error)
                || !reader.TryReadString(out var rid, out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName(
                    "runtimeProtocolMin",
                    out error)
                || !reader.TryReadInt64(
                    out var protocolMinimumLong,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName(
                    "runtimeProtocolMax",
                    out error)
                || !reader.TryReadInt64(
                    out var protocolMaximumLong,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName(
                    "entrypoint",
                    out error)
                || !reader.TryReadString(
                    out var entrypoint,
                    out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName("files", out error)
                || !TryReadFiles(reader, out var files, out error)
                || !reader.TryReadComma(out error)
                || !reader.TryReadPropertyName(
                    "signature",
                    out error)
                || !reader.TryReadString(
                    out var signature,
                    out error)
                || !reader.TryReadObjectEnd(out error))
            {
                return Fail("InvalidShape", error);
            }

            if (!reader.IsAtEnd)
            {
                return Fail(
                    "TrailingContent",
                    "Manifest contains trailing JSON content.");
            }

            if (schemaLong < int.MinValue
                || schemaLong > int.MaxValue
                || protocolMinimumLong < int.MinValue
                || protocolMinimumLong > int.MaxValue
                || protocolMaximumLong < int.MinValue
                || protocolMaximumLong > int.MaxValue)
            {
                return Fail(
                    "IntegerOutOfRange",
                    "Manifest integer is outside Int32 range.");
            }

            CompanionBundleManifest manifest;
            byte[] canonical;
            try
            {
                manifest = new CompanionBundleManifest(
                    (int)schemaLong,
                    product,
                    version,
                    rid,
                    (int)protocolMinimumLong,
                    (int)protocolMaximumLong,
                    entrypoint,
                    files!,
                    signature);
                canonical = Serialize(manifest);
            }
            catch (Exception exception)
            {
                return Fail("InvalidManifest", exception.Message);
            }

            if (!IpcPayloadCodec.ByteArraysEqual(canonical, bytes))
            {
                if (requireCanonical)
                {
                    return Fail(
                        "NonCanonical",
                        "Manifest JSON is not canonical.");
                }

                Trace.TraceWarning(
                    "CompanionManifestCodec: Manifest JSON is not canonical; continuing.");
            }

            return new CompanionManifestDecodeResult(
                true,
                manifest,
                string.Empty,
                string.Empty);
        }

        private static bool TryReadFiles(
            StrictJsonReader reader,
            out IReadOnlyList<CompanionManifestFile>? files,
            out string error)
        {
            var result = new List<CompanionManifestFile>();
            if (!reader.TryReadArrayStart(out error))
            {
                files = null;
                return false;
            }

            if (reader.PeekArrayEnd())
            {
                reader.TryReadArrayEnd(out _);
                files = result;
                return true;
            }

            while (true)
            {
                if (!reader.TryReadObjectStart(out error)
                    || !reader.TryReadPropertyName("path", out error)
                    || !reader.TryReadString(out var path, out error)
                    || !reader.TryReadComma(out error)
                    || !reader.TryReadPropertyName(
                        "sha256",
                        out error)
                    || !reader.TryReadString(
                        out var sha256,
                        out error)
                    || !reader.TryReadObjectEnd(out error))
                {
                    files = null;
                    return false;
                }

                result.Add(new CompanionManifestFile(path, sha256));
                if (result.Count > MaximumFiles)
                {
                    files = null;
                    error = "Manifest contains too many files.";
                    return false;
                }

                if (reader.PeekArrayEnd())
                {
                    reader.TryReadArrayEnd(out _);
                    files = result;
                    return true;
                }

                if (!reader.TryReadComma(out error))
                {
                    files = null;
                    return false;
                }
            }
        }

        private static CompanionManifestDecodeResult Fail(
            string code,
            string error)
        {
            return new CompanionManifestDecodeResult(
                false,
                null,
                code,
                error);
        }
    }
}
