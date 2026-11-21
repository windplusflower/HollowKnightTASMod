using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Runtime.Companion
{
    internal enum CompanionAttachStatus
    {
        Absent = 0,
        Attached = 1,
        Rejected = 2,
        Incompatible = 3
    }

    internal sealed class CompanionAttachResult
    {
        public CompanionAttachResult(
            CompanionAttachStatus status,
            string detail,
            string instanceId,
            string version)
        {
            Status = status;
            Detail = detail;
            InstanceId = instanceId;
            Version = version;
        }

        public CompanionAttachStatus Status { get; }
        public string Detail { get; }
        public string InstanceId { get; }
        public string Version { get; }
    }

    internal sealed class CompanionControlClient
    {
        private readonly string controlPipeName;

        public CompanionControlClient(string controlPipeName)
        {
            if (!IpcIdentifier.IsValid(controlPipeName, 240))
            {
                throw new ArgumentException(
                    "Companion control pipe name is invalid.",
                    nameof(controlPipeName));
            }

            this.controlPipeName = controlPipeName;
        }

        public async Task<CompanionAttachResult> RegisterAsync(
            CompanionSessionRegistration registration,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendAsync(
                        registration,
                        IpcMessageTypes.RegisterSession,
                        IpcMessageTypes.RegisterSessionAck,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (response == null)
                {
                    return new CompanionAttachResult(
                        CompanionAttachStatus.Absent,
                        "No Companion control pipe accepted the connection.",
                        string.Empty,
                        string.Empty);
                }

                if (!TryReadMetadata(
                        response,
                        registration.ProtocolRange,
                        out var accepted,
                        out var detail,
                        out var instanceId,
                        out var version))
                {
                    return new CompanionAttachResult(
                        CompanionAttachStatus.Incompatible,
                        "Existing Companion returned incompatible metadata.",
                        string.Empty,
                        string.Empty);
                }

                return new CompanionAttachResult(
                    accepted
                        ? CompanionAttachStatus.Attached
                        : CompanionAttachStatus.Rejected,
                    detail,
                    instanceId,
                    version);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                return new CompanionAttachResult(
                    CompanionAttachStatus.Absent,
                    "Companion control pipe was not available before timeout.",
                    string.Empty,
                    string.Empty);
            }
            catch (TimeoutException)
            {
                return new CompanionAttachResult(
                    CompanionAttachStatus.Absent,
                    "Companion control pipe timed out.",
                    string.Empty,
                    string.Empty);
            }
            catch (IOException)
            {
                return new CompanionAttachResult(
                    CompanionAttachStatus.Absent,
                    "Companion control pipe is absent.",
                    string.Empty,
                    string.Empty);
            }
            catch (UnauthorizedAccessException)
            {
                return new CompanionAttachResult(
                    CompanionAttachStatus.Incompatible,
                    "Existing Companion control pipe rejected current-user access.",
                    string.Empty,
                    string.Empty);
            }
            catch (Win32Exception exception)
                when (exception.NativeErrorCode == 2
                      || exception.NativeErrorCode == 3
                      || exception.NativeErrorCode == 53
                      || exception.NativeErrorCode == 231)
            {
                return new CompanionAttachResult(
                    CompanionAttachStatus.Absent,
                    "Companion control pipe is absent or busy.",
                    string.Empty,
                    string.Empty);
            }
        }

        public async Task<bool> ShutdownAsync(
            CompanionSessionRegistration registration,
            bool requestCompanionExit,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await SendAsync(
                        registration,
                        requestCompanionExit
                            ? IpcMessageTypes.RequestCompanionExit
                            : IpcMessageTypes.ShutdownSession,
                        IpcMessageTypes.ControlAck,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                return response != null
                       && TryReadMetadata(
                           response,
                           registration.ProtocolRange,
                           out var accepted,
                           out _,
                           out _,
                           out _)
                       && accepted;
            }
            catch
            {
                return false;
            }
        }

        private async Task<IpcEnvelope?> SendAsync(
            CompanionSessionRegistration registration,
            string messageType,
            string expectedResponseType,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using (var linked =
                   CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken))
            using (var client = new NamedPipeClientStream(
                       ".",
                       controlPipeName,
                       PipeDirection.InOut,
                       PipeOptions.Asynchronous))
            {
                linked.CancelAfter(timeout);
                var timeoutMilliseconds = checked(
                    (int)Math.Max(
                        1,
                        Math.Min(
                            int.MaxValue,
                            timeout.TotalMilliseconds)));
                await Task.Run(
                        () => client.Connect(
                            timeoutMilliseconds),
                        linked.Token)
                    .ConfigureAwait(false);
                await IpcCodec.WriteFrameAsync(
                        client,
                        new IpcEnvelope(
                            CompanionProtocolMetadata.ProtocolMaximum,
                            registration.SessionId,
                            0,
                            messageType,
                            registration.ToPayload()),
                        linked.Token)
                    .ConfigureAwait(false);
                var response = await IpcCodec.ReadFrameAsync(
                        client,
                        linked.Token)
                    .ConfigureAwait(false);
                if (!string.Equals(
                        response.MessageType,
                        expectedResponseType,
                        StringComparison.Ordinal)
                    || response.Sequence != 0
                    || !string.Equals(
                        response.SessionId,
                        registration.SessionId,
                        StringComparison.Ordinal))
                {
                    return null;
                }

                if (!registration.ProtocolRange.Contains(response.ProtocolVersion))
                {
                    Modding.Logger.LogWarn(
                        $"[HKTAS] Companion response ProtocolVersion {response.ProtocolVersion} differs from Runtime range {registration.ProtocolRange}; continuing.");
                }

                return response;
            }
        }

        private static bool TryReadMetadata(
            IpcEnvelope response,
            ProtocolRange runtimeProtocols,
            out bool accepted,
            out string detail,
            out string instanceId,
            out string version)
        {
            accepted = false;
            detail = string.Empty;
            instanceId = string.Empty;
            version = string.Empty;
            var payload = IpcPayloadCodec.TryDeserialize(
                response.PayloadUtf8);
            if (!payload.Success
                || payload.Fields == null
                || !payload.Fields.TryGetValue(
                    "accepted",
                    out var acceptedText))
            {
                return false;
            }

            if (payload.Fields.Count != 7)
            {
                Modding.Logger.LogWarn(
                    $"[HKTAS] Companion metadata field count {payload.Fields.Count} differs from expected 7; continuing.");
            }

            if (payload.Fields.TryGetValue("detail", out var detailValue)
                && detailValue != null)
            {
                detail = detailValue;
            }
            else
            {
                Modding.Logger.LogWarn(
                    "[HKTAS] Companion metadata is missing 'detail'; continuing.");
            }

            if (!payload.Fields.TryGetValue("product", out var product)
                || !string.Equals(
                    product,
                    CompanionProtocolMetadata.Product,
                    StringComparison.Ordinal))
            {
                Modding.Logger.LogWarn(
                    $"[HKTAS] Companion metadata product '{product ?? string.Empty}' differs from expected '{CompanionProtocolMetadata.Product}'; continuing.");
            }

            if (payload.Fields.TryGetValue(
                    "companionInstanceId",
                    out var instanceValue)
                && instanceValue != null)
            {
                instanceId = instanceValue;
            }

            if (!IpcIdentifier.IsValid(instanceId, 128))
            {
                Modding.Logger.LogWarn(
                    $"[HKTAS] Companion metadata companionInstanceId '{instanceId}' is missing or invalid; continuing.");
            }

            if (payload.Fields.TryGetValue("version", out var versionValue)
                && versionValue != null)
            {
                version = versionValue;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                Modding.Logger.LogWarn(
                    "[HKTAS] Companion metadata version is missing or empty; continuing.");
            }

            string? minimumText = null;
            string? maximumText = null;
            if (!payload.Fields.TryGetValue(
                    "protocolMin",
                    out minimumText)
                || !payload.Fields.TryGetValue(
                    "protocolMax",
                    out maximumText)
                || !int.TryParse(
                    minimumText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var minimum)
                || !int.TryParse(
                    maximumText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var maximum)
                || minimum <= 0
                || maximum < minimum)
            {
                Modding.Logger.LogWarn(
                    $"[HKTAS] Companion metadata protocol range ('{minimumText ?? string.Empty}'-'{maximumText ?? string.Empty}') is invalid; continuing.");
            }
            else if (!runtimeProtocols.Intersects(
                         new ProtocolRange(minimum, maximum)))
            {
                Modding.Logger.LogWarn(
                    $"[HKTAS] Companion protocol range {minimum}-{maximum} does not intersect Runtime range {runtimeProtocols}; continuing.");
            }

            if (string.Equals(
                    acceptedText,
                    "true",
                    StringComparison.Ordinal))
            {
                accepted = true;
                return true;
            }

            return string.Equals(
                acceptedText,
                "false",
                StringComparison.Ordinal);
        }
    }
}
