using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Companion.Automation
{
    public sealed class AutomationAuthenticationResult
    {
        public AutomationAuthenticationResult(
            bool success,
            string clientId,
            string errorCode,
            string error,
            byte[]? serverNonce)
        {
            Success = success;
            ClientId = clientId;
            ErrorCode = errorCode;
            Error = error;
            ServerNonce = serverNonce;
        }

        public bool Success { get; }
        public string ClientId { get; }
        public string ErrorCode { get; }
        public string Error { get; }
        public byte[]? ServerNonce { get; }
    }

    public sealed class AutomationSessionAuthenticator
    {
        private const int NonceWindow = 4096;
        private readonly object sync = new object();
        private readonly byte[] token;
        private readonly Queue<string> nonceOrder =
            new Queue<string>();
        private readonly HashSet<string> nonceHashes =
            new HashSet<string>(StringComparer.Ordinal);

        public AutomationSessionAuthenticator(byte[] token)
        {
            if (token == null || token.Length != 32)
            {
                throw new ArgumentException(
                    "Automation token must contain 32 bytes.",
                    nameof(token));
            }

            this.token = (byte[])token.Clone();
        }

        public AutomationAuthenticationResult Authenticate(
            IpcEnvelope envelope,
            string expectedSessionId,
            string expectedManifestSha256)
        {
            if (envelope.Sequence != 0
                || envelope.MessageType != AutomationProtocol.Hello
                || envelope.SessionId != expectedSessionId)
            {
                return Fail(
                    "InvalidHello",
                    "First frame is not a valid automation hello.");
            }

            if (envelope.ProtocolVersion != AutomationProtocol.Version)
            {
                Trace.TraceWarning(
                    $"AutomationSessionAuthenticator: hello ProtocolVersion {envelope.ProtocolVersion} differs from expected {AutomationProtocol.Version}; continuing.");
            }

            var decoded = IpcPayloadCodec.TryDeserialize(
                envelope.PayloadUtf8);
            var expected = new[]
            {
                "clientId",
                "clientNonce",
                "manifestSha256",
                "product",
                "schemaVersion",
                "sessionId",
                "token"
            };
            if (!decoded.Success
                || decoded.Fields == null
                || decoded.Fields.Count != expected.Length
                || expected.Any(
                    key => !decoded.Fields.ContainsKey(key))
                || decoded.Fields["product"]
                   != AutomationProtocol.Product
                || decoded.Fields["sessionId"] != expectedSessionId
                || !IpcIdentifier.IsValid(
                    decoded.Fields["clientId"],
                    128))
            {
                return Fail(
                    "InvalidHello",
                    "Automation hello binding is invalid.");
            }

            if (!string.Equals(
                    decoded.Fields["schemaVersion"],
                    "1",
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"AutomationSessionAuthenticator: hello schemaVersion '{decoded.Fields["schemaVersion"]}' differs from '1'; continuing.");
            }

            if (!string.Equals(
                    decoded.Fields["manifestSha256"],
                    expectedManifestSha256,
                    StringComparison.Ordinal))
            {
                Trace.TraceWarning(
                    $"AutomationSessionAuthenticator: hello manifestSha256 '{decoded.Fields["manifestSha256"]}' differs from expected '{expectedManifestSha256}'; continuing.");
            }

            byte[] supplied;
            byte[] nonce;
            try
            {
                supplied = Convert.FromBase64String(
                    decoded.Fields["token"]);
                nonce = Convert.FromBase64String(
                    decoded.Fields["clientNonce"]);
            }
            catch (FormatException)
            {
                return Fail(
                    "AuthenticationFailed",
                    "Automation credential encoding is invalid.");
            }

            if (supplied.Length != 32
                || nonce.Length != 32
                || !FixedTimeEquals(token, supplied))
            {
                return Fail(
                    "AuthenticationFailed",
                    "Automation credential is invalid.");
            }

            var nonceHash = Sha256Utility.ComputeHex(nonce);
            lock (sync)
            {
                if (!nonceHashes.Add(nonceHash))
                {
                    return Fail(
                        "ReplayedNonce",
                        "Automation client nonce was already used.");
                }

                nonceOrder.Enqueue(nonceHash);
                while (nonceOrder.Count > NonceWindow)
                {
                    nonceHashes.Remove(nonceOrder.Dequeue());
                }
            }

            var serverNonce = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(serverNonce);
            }

            return new AutomationAuthenticationResult(
                true,
                decoded.Fields["clientId"],
                string.Empty,
                string.Empty,
                serverNonce);
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

        private static AutomationAuthenticationResult Fail(
            string code,
            string error)
        {
            return new AutomationAuthenticationResult(
                false,
                string.Empty,
                code,
                error,
                null);
        }
    }
}
