using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace HollowKnightTAS.Core.Ipc
{
    public sealed class IpcHandshakeResult
    {
        internal IpcHandshakeResult(
            bool success,
            int negotiatedProtocol,
            string companionInstanceId,
            byte[]? clientNonce,
            string errorCode,
            string error)
        {
            Success = success;
            NegotiatedProtocol = negotiatedProtocol;
            CompanionInstanceId = companionInstanceId;
            ClientNonce = clientNonce == null
                ? null
                : (byte[])clientNonce.Clone();
            ErrorCode = errorCode;
            Error = error;
        }

        public bool Success { get; }
        public int NegotiatedProtocol { get; }
        public string CompanionInstanceId { get; }
        public byte[]? ClientNonce { get; }
        public string ErrorCode { get; }
        public string Error { get; }
    }

    public static class IpcHandshakeValidator
    {
        public const string CompanionProduct =
            CompanionProtocolMetadata.Product;

        private static readonly string[] RequiredHelloFields =
        {
            "clientNonce",
            "companionInstanceId",
            "gameProcessId",
            "product",
            "protocolMax",
            "protocolMin",
            "runtimeSessionId",
            "token",
            "version"
        };

        public static IpcHandshakeResult ValidateHello(
            IpcEnvelope envelope,
            string expectedSessionId,
            int expectedGameProcessId,
            ProtocolRange runtimeProtocols,
            byte[] expectedToken)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            if (expectedToken == null
                || expectedToken.Length != 32)
            {
                throw new ArgumentException(
                    "Expected session token must contain 32 bytes.",
                    nameof(expectedToken));
            }

            if (!string.Equals(
                    envelope.MessageType,
                    IpcMessageTypes.Hello,
                    StringComparison.Ordinal)
                || envelope.Sequence != 0)
            {
                return Fail(
                    "ExpectedHello",
                    "First message must be hello sequence 0.");
            }

            if (!string.Equals(
                    envelope.SessionId,
                    expectedSessionId,
                    StringComparison.Ordinal))
            {
                return Fail(
                    "SessionMismatch",
                    "Envelope session does not match Runtime session.");
            }

            var payload = IpcPayloadCodec.TryDeserialize(
                envelope.UnsafePayloadUtf8);
            if (!payload.Success || payload.Fields == null)
            {
                return Fail(
                    "InvalidHelloPayload",
                    payload.ErrorCode + ": " + payload.Error);
            }

            if (payload.Fields.Count != RequiredHelloFields.Length)
            {
                return Fail(
                    "InvalidHelloShape",
                    "Hello contains an unexpected field count.");
            }

            foreach (var field in RequiredHelloFields)
            {
                if (!payload.Fields.ContainsKey(field))
                {
                    return Fail(
                        "MissingHelloField",
                        "Hello is missing " + field + ".");
                }
            }

            if (!string.Equals(
                    payload.Fields["product"],
                    CompanionProduct,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"IPC hello product '{payload.Fields["product"]}' differs from expected '{CompanionProduct}'; continuing.");
            }

            if (!string.Equals(
                    payload.Fields["runtimeSessionId"],
                    expectedSessionId,
                    StringComparison.Ordinal))
            {
                return Fail(
                    "SessionMismatch",
                    "Hello Runtime session binding does not match.");
            }

            if (!int.TryParse(
                    payload.Fields["gameProcessId"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var gameProcessId)
                || gameProcessId != expectedGameProcessId)
            {
                return Fail(
                    "ProcessMismatch",
                    "Hello game process binding does not match.");
            }

            int negotiatedProtocol;
            if (!int.TryParse(
                    payload.Fields["protocolMin"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var protocolMinimum)
                || !int.TryParse(
                    payload.Fields["protocolMax"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var protocolMaximum)
                || protocolMinimum <= 0
                || protocolMaximum < protocolMinimum)
            {
                negotiatedProtocol = 1;
                Trace.TraceWarning(
                    $"IPC hello protocol range ('{payload.Fields["protocolMin"]}'-'{payload.Fields["protocolMax"]}') is invalid; falling back to protocol {negotiatedProtocol}.");
            }
            else
            {
                var companionProtocols = new ProtocolRange(
                    protocolMinimum,
                    protocolMaximum);
                if (!runtimeProtocols.TryNegotiate(
                        companionProtocols,
                        out negotiatedProtocol))
                {
                    negotiatedProtocol = 1;
                    Trace.TraceWarning(
                        $"IPC protocol ranges do not intersect (runtime={runtimeProtocols}, companion={companionProtocols}); falling back to protocol {negotiatedProtocol}.");
                }
            }

            if (envelope.ProtocolVersion != negotiatedProtocol)
            {
                Trace.TraceWarning(
                    $"IPC hello envelope ProtocolVersion {envelope.ProtocolVersion} differs from negotiated protocol {negotiatedProtocol}; continuing.");
            }

            if (!IpcIdentifier.IsValid(
                    payload.Fields["companionInstanceId"],
                    128))
            {
                return Fail(
                    "InvalidInstance",
                    "Companion instance ID is invalid.");
            }

            if (!TryDecodeFixed(
                    payload.Fields["token"],
                    32,
                    out var suppliedToken)
                || !FixedTimeEquals(expectedToken, suppliedToken!))
            {
                return Fail(
                    "AuthenticationFailed",
                    "Session token is invalid.");
            }

            if (!TryDecodeFixed(
                    payload.Fields["clientNonce"],
                    32,
                    out var clientNonce))
            {
                return Fail(
                    "InvalidNonce",
                    "Client nonce must contain 32 bytes.");
            }

            return new IpcHandshakeResult(
                true,
                negotiatedProtocol,
                payload.Fields["companionInstanceId"],
                clientNonce,
                string.Empty,
                string.Empty);
        }

        public static byte[] CreateHelloPayload(
            string version,
            ProtocolRange companionProtocols,
            int gameProcessId,
            string runtimeSessionId,
            string companionInstanceId,
            byte[] clientNonce,
            byte[] token)
        {
            if (clientNonce == null || clientNonce.Length != 32)
            {
                throw new ArgumentException(
                    "Client nonce must contain 32 bytes.",
                    nameof(clientNonce));
            }

            if (token == null || token.Length != 32)
            {
                throw new ArgumentException(
                    "Session token must contain 32 bytes.",
                    nameof(token));
            }

            return IpcPayloadCodec.Serialize(
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["clientNonce"] = Convert.ToBase64String(clientNonce),
                    ["companionInstanceId"] = companionInstanceId,
                    ["gameProcessId"] = gameProcessId.ToString(
                        CultureInfo.InvariantCulture),
                    ["product"] = CompanionProduct,
                    ["protocolMax"] =
                        companionProtocols.Maximum.ToString(
                            CultureInfo.InvariantCulture),
                    ["protocolMin"] =
                        companionProtocols.Minimum.ToString(
                            CultureInfo.InvariantCulture),
                    ["runtimeSessionId"] = runtimeSessionId,
                    ["token"] = Convert.ToBase64String(token),
                    ["version"] = version
                });
        }

        private static bool TryDecodeFixed(
            string value,
            int expectedLength,
            out byte[]? bytes)
        {
            try
            {
                bytes = Convert.FromBase64String(value);
                return bytes.Length == expectedLength;
            }
            catch (FormatException)
            {
                bytes = null;
                return false;
            }
        }

        private static bool FixedTimeEquals(
            byte[] left,
            byte[] right)
        {
            var difference = left.Length ^ right.Length;
            var length = Math.Min(left.Length, right.Length);
            for (var index = 0; index < length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static IpcHandshakeResult Fail(
            string code,
            string error)
        {
            return new IpcHandshakeResult(
                false,
                0,
                string.Empty,
                null,
                code,
                error);
        }
    }

    public sealed class IpcSequenceValidator
    {
        private long lastSequence;

        public IpcSequenceValidator(long initialSequence)
        {
            if (initialSequence < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialSequence));
            }

            lastSequence = initialSequence;
        }

        public long LastSequence => lastSequence;

        public bool TryAccept(long sequence, out string error)
        {
            if (sequence != checked(lastSequence + 1))
            {
                error = sequence <= lastSequence
                    ? "Sequence is duplicate or out of order."
                    : "Sequence contains a gap.";
                return false;
            }

            lastSequence = sequence;
            error = string.Empty;
            return true;
        }
    }
}
