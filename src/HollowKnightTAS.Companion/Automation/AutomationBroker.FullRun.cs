using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.Automation
{
    public sealed partial class AutomationBroker
    {
        // Installed by Studio. Only authenticated external commands enter this bridge;
        // in-process UI requests must not recursively re-enter it.
        public Func<AutomationCommandEnvelope,
            Func<AutomationCommandEnvelope, Task<AutomationResultEnvelope>>,
            Task<AutomationResultEnvelope>>? StudioFullRunCommand { get; set; }

        private async Task<AutomationResultEnvelope> RouteFullRunAsync(
            AutomationBootstrapDescriptor binding, string clientId,
            string connectionId, AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var coordinator = fullRunMovies
                ?? throw new InvalidOperationException("Full-run coordinator is unavailable.");
            var shapeError = ValidateArgumentShape(command);
            if (shapeError != null)
                return Result(command, false, "InvalidArguments", shapeError);
            var valueError = ValidateArgumentValues(command);
            if (valueError != null)
                return Result(command, false, "InvalidArguments", valueError);

            if (command.CommandId == AutomationCommandIds.AcquireControl)
                return AcquireBound(binding.SessionId, binding.ManifestSha256,
                    binding.Mode, clientId, connectionId, command);
            if (command.CommandId == AutomationCommandIds.RenewControl)
                return Renew(clientId, connectionId, command);
            if (command.CommandId == AutomationCommandIds.ReleaseControl)
                return Release(clientId, connectionId, command);

            var catalog = capabilityCatalog
                ?? new AutomationCapabilityCatalog(binding.Mode, false, fullRunOnly: true);
            if (!catalog.TryGet(command.CommandId, out var capability)
                || capability.Scope != command.RequiredScope)
                return Result(command, false, "CapabilityMismatch",
                    "Command scope is unavailable in this full-run session.");
            if (capability.Availability == "disabled")
                return Result(command, false, "CapabilityDisabled",
                    "Full-run control is disabled by the user policy.");
            if (capability.RequiresLease && !leases.Validate(clientId,
                    connectionId, command.LeaseId, command.RequiredScope,
                    binding.SessionId, binding.ManifestSha256))
                return Result(command, false, "LeaseRequired",
                    "A matching unexpired exclusive control lease is required.");

            if (connectionId != "companion-ui-inproc" && StudioFullRunCommand != null)
                return await StudioFullRunCommand(command,
                    next => RouteFullRunCoreAsync(binding, next, cancellationToken));
            return await RouteFullRunCoreAsync(binding, command, cancellationToken);
        }

        private async Task<AutomationResultEnvelope> RouteFullRunCoreAsync(
            AutomationBootstrapDescriptor binding, AutomationCommandEnvelope command,
            CancellationToken cancellationToken)
        {
            var coordinator = fullRunMovies!;
            var catalog = capabilityCatalog
                ?? new AutomationCapabilityCatalog(binding.Mode, false, fullRunOnly: true);
            var gate = coordinator.Gate
                ?? throw new InvalidOperationException("Full-run gate is unavailable.");
            if (command.CommandId == AutomationCommandIds.GetStatus
                || command.CommandId == AutomationCommandIds.FullRunStatus)
            {
                var fields = new System.Collections.Generic.Dictionary<string, string>(
                    Fields("sessionId", binding.SessionId,
                        "manifestSha256", binding.ManifestSha256,
                        "automationMode", binding.Mode.ToString(),
                        "mode", coordinator.Mode,
                        "nativeFrame", gate.NativeCompletedFrames.ToString(CultureInfo.InvariantCulture),
                        "paused", gate.IsWaiting ? "true" : "false",
                        "runtimeConnected", GetBoundSession()?.IsConnected == true ? "true" : "false",
                        "faultCode", gate.FullRunFaultCode.ToString(CultureInfo.InvariantCulture),
                        "saveGuardArmed", gate.SaveGuardArmed.ToString(CultureInfo.InvariantCulture)),
                    StringComparer.Ordinal);
                var runtime = GetBoundSession();
                if (runtime?.IsConnected == true)
                {
                    var observed = await ForwardAsync(runtime, command,
                        IpcMessageTypes.FullRunStatus,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.FullRunState, cancellationToken);
                    if (observed.Success)
                        foreach (var item in observed.Data)
                            fields[item.Key] = item.Value;
                }
                return Result(command, true, "Ok", "Full-run state returned.", fields);
            }
            if (command.CommandId == AutomationCommandIds.GetCapabilities)
                return Result(command, true, "Ok", "Full-run capability catalog returned.",
                    Fields("catalog", catalog.Serialize(),
                        "catalogJson", catalog.SerializeJson(),
                        "mode", binding.Mode.ToString(),
                        "debugMutationEnabled", "false"));

            if (command.CommandId == AutomationCommandIds.StartVideoExport
                || command.CommandId == AutomationCommandIds.CancelVideoExport)
            {
                var runtime = GetBoundSession();
                if (runtime == null || !runtime.IsConnected)
                    return Result(command, false, "RuntimeNotReady", "The full-run Runtime has not connected yet.");
                if (command.CommandId == AutomationCommandIds.StartVideoExport
                    && (!gate.IsWaiting || command.ExpectedRuntimeMode != "Paused"))
                    return Result(command, false, "PreconditionFailed", "Pause the v2 replay before exporting video.");
                var fields = new System.Collections.Generic.Dictionary<string, string>(command.Arguments, StringComparer.Ordinal)
                { ["requestId"] = command.RequestId };
                if (command.CommandId == AutomationCommandIds.StartVideoExport
                    && !fields.ContainsKey("replayLoadedMovie")) fields["replayLoadedMovie"] = "true";
                if (command.CommandId == AutomationCommandIds.StartVideoExport)
                    HollowKnightTAS.Companion.Services.BundledFfmpeg.ApplyDefault(fields);
                var result = await ForwardAsync(runtime, command, command.CommandId,
                    fields, IpcMessageTypes.CommandAccepted, cancellationToken);
                if (result.Success && command.CommandId == AutomationCommandIds.StartVideoExport)
                {
                    // Match v1's start-and-replay contract. The Runtime captures the
                    // current remaining suffix and auto-finishes at the loaded Movie end.
                    var boundary = await VideoExportStartRecovery.RunAsync(
                        () => coordinator.RunAsync(gate.NativeCompletedFrames, cancellationToken),
                        () => gate.FullRunFaultCode != 0,
                        async cleanupToken =>
                        {
                            if (!result.Data.TryGetValue("detail", out var operationId) || string.IsNullOrWhiteSpace(operationId))
                                throw new InvalidOperationException("Accepted capture has no operation ID.");
                            var cleanupId = "video-start-cancel-" + Guid.NewGuid().ToString("N");
                            var cancelled = await SendRuntimeAsync(runtime, IpcMessageTypes.CancelVideoExport,
                                Fields("requestId", cleanupId, "operationId", operationId), cleanupId,
                                IpcMessageTypes.CommandAccepted, cleanupToken);
                            if (cancelled.MessageType != IpcMessageTypes.CommandAccepted)
                                throw new InvalidOperationException("Runtime rejected video cancellation.");
                        });
                    if (boundary.Mode == "Fault") return BoundaryResult(command, boundary);
                }
                return result;
            }

            if (command.CommandId == AutomationCommandIds.GetWorldSnapshot
                || command.CommandId == AutomationCommandIds.GetObjectDetails)
            {
                var runtime = GetBoundSession();
                if (runtime == null || !runtime.IsConnected)
                    return Result(command, false, "RuntimeNotReady",
                        "The full-run Runtime has not connected yet.");

                var fields = new System.Collections.Generic.Dictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["requestId"] = command.RequestId
                };
                if (command.CommandId == AutomationCommandIds.GetWorldSnapshot)
                {
                    var view = ArgumentOrDefault(command, "view", "world");
                    fields["view"] = view;
                    if (view == "fsms" || view == "info")
                    {
                        if (command.Arguments.TryGetValue("watches", out var watches))
                            fields["watches"] = watches;
                    }
                    else
                    {
                        fields["snapshotId"] = ArgumentOrDefault(command, "snapshotId", string.Empty);
                        fields["includeInactive"] = ArgumentOrDefault(command, "includeInactive", "false");
                        fields["offset"] = ArgumentOrDefault(command, "offset", "0");
                        fields["limit"] = ArgumentOrDefault(command, "limit", "64");
                        if (command.Arguments.TryGetValue("watches", out var watches))
                            fields["watches"] = watches;
                    }
                    return await ForwardAsync(runtime, command,
                        IpcMessageTypes.GetWorldSnapshot, fields,
                        IpcMessageTypes.WorldSnapshot, cancellationToken);
                }

                fields["objectId"] = command.Arguments["objectId"];
                if (command.Arguments.TryGetValue("expectedNativeFrame", out var expectedNativeFrame))
                    fields["expectedNativeFrame"] = expectedNativeFrame;
                if (command.Arguments.TryGetValue("detailsId", out var detailsId))
                    fields["detailsId"] = detailsId;
                fields["cursor"] = ArgumentOrDefault(command, "cursor", "0");
                fields["maxCharacters"] = ArgumentOrDefault(command, "maxCharacters", "100000");
                return await ForwardAsync(runtime, command,
                    IpcMessageTypes.GetObjectDetails, fields,
                    IpcMessageTypes.ObjectDetails, cancellationToken);
            }

            if (command.CommandId == AutomationCommandIds.FullRunMovie)
            {
                var runtime = GetBoundSession();
                return runtime == null
                    ? Result(command, false, "RuntimeNotReady",
                        "The full-run Runtime has not connected yet.")
                    : await ForwardAsync(runtime, command, IpcMessageTypes.FullRunMovie,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.FullRunMovieDocument, cancellationToken);
            }

            if (command.CommandId == AutomationCommandIds.FullRunUpdateMovie)
            {
                var runtime = GetBoundSession();
                if (runtime == null || !gate.IsWaiting
                    || !long.TryParse(command.Arguments["expectedNativeFrame"], out var expected)
                    || expected != gate.NativeCompletedFrames)
                    return Result(command, false, "PreconditionFailed", "Movie update requires a connected, paused frame.");
                var path = Path.GetFullPath(command.Arguments["moviePath"]);
                if (!path.StartsWith(Path.GetFullPath(coordinator.ShadowRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return Result(command, false, "InvalidArguments", "Movie escaped the protected session.");
                var updated = await ForwardAsync(runtime, command, IpcMessageTypes.FullRunUpdateMovie,
                    Fields("requestId", command.RequestId, "moviePath", path,
                        "expectedNativeFrame", expected.ToString(CultureInfo.InvariantCulture)),
                    IpcMessageTypes.CommandAccepted, cancellationToken);
                if (updated.Success) coordinator.MarkLiveReplay();
                return updated;
            }

            if (command.CommandId == AutomationCommandIds.FullRunSnapshot || command.CommandId == AutomationCommandIds.FullRunSeek)
            {
                if (!gate.IsWaiting) return Result(command, false, "PreconditionFailed", "Pause at a frame boundary first.");
                var runtime = GetBoundSession();
                if (runtime == null) return Result(command, false, "RuntimeNotReady", "Runtime has not connected yet.");
                if (command.CommandId == AutomationCommandIds.FullRunSnapshot)
                    return await ForwardAsync(runtime, command, IpcMessageTypes.FullRunSnapshot,
                        Fields("requestId", command.RequestId), IpcMessageTypes.FullRunMovieDocument, cancellationToken);
                if (!long.TryParse(command.Arguments["targetFrame"], out var target) || target < 0 || target > MovieProtocolV2.MaximumExpandedFrames
                    || !long.TryParse(command.Arguments["expectedNativeFrame"], out var observed) || observed != gate.NativeCompletedFrames)
                    return Result(command, false, "PreconditionFailed", "Invalid target or stale native frame.");
                return await ForwardAsync(runtime, command, IpcMessageTypes.FullRunSeek,
                    Fields("requestId", command.RequestId, "targetFrame", target.ToString(CultureInfo.InvariantCulture),
                        "expectedNativeFrame", observed.ToString(CultureInfo.InvariantCulture)), IpcMessageTypes.CommandAccepted, cancellationToken);
            }

            if (command.CommandId == AutomationCommandIds.QuitGame)
            {
                if (!gate.IsWaiting)
                    return Result(command, false, "PreconditionFailed",
                        "Protected game exit requires a paused native frame.");
                var runtime = GetBoundSession();
                return runtime == null
                    ? Result(command, false, "RuntimeNotReady",
                        "The full-run Runtime has not connected yet.")
                    : await ForwardAsync(runtime, command, IpcMessageTypes.QuitGame,
                        Fields("requestId", command.RequestId),
                        IpcMessageTypes.CommandAccepted, cancellationToken);
            }

            var expectedFrame = long.Parse(command.Arguments["expectedNativeFrame"],
                CultureInfo.InvariantCulture);
            if (command.CommandId == AutomationCommandIds.FullRunPause)
            {
                if (command.ExpectedRuntimeMode != "Running"
                    || expectedFrame > gate.NativeCompletedFrames)
                    return Result(command, false, "PreconditionFailed",
                        "Full-run pause requires an observed running frame.");
                var boundary = await coordinator.PauseAsync(cancellationToken);
                return BoundaryResult(command, boundary);
            }
            if (command.ExpectedRuntimeMode != "Paused"
                || !gate.IsWaiting || gate.NativeCompletedFrames != expectedFrame)
                return Result(command, false, "PreconditionFailed",
                    "Full-run command requires the exact paused native frame.");

            if (command.CommandId == AutomationCommandIds.BeginFullRunRecording)
            {
                coordinator.ArmRecording(command.Arguments["mouseEnabled"] == "true",
                    command.Arguments.TryGetValue("fps", out var fps) ? HollowKnightTAS.Core.Movie.MovieFrameRate.Parse(fps) : 50);
                return Result(command, true, "Ok", "Recording armed at native frame 0.",
                    Fields("mode", coordinator.Mode, "nativeFrame", "0"));
            }
            if (command.CommandId == AutomationCommandIds.BeginFullRunReplay)
            {
                if (!command.Arguments.TryGetValue("movieBase64", out var movieBase64)
                    || string.IsNullOrEmpty(movieBase64))
                    return Result(command, false, "InvalidMovie", "movieBase64 is required.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(movieBase64); }
                catch (FormatException)
                {
                    return Result(command, false, "InvalidMovie", "movieBase64 is invalid.");
                }
                if (bytes.Length == 0 || bytes.Length > MovieProtocolV2.MaximumSourceUtf8Bytes)
                    return Result(command, false, "InvalidMovie", "Full-run movie size is invalid.");
                var source = new UTF8Encoding(false, true).GetString(bytes);
                var codec = new MovieV2Codec();
                var parsed = codec.Parse(new StringReader(source), "<automation>");
                if (!parsed.Success || parsed.Document == null
                    || codec.WriteCanonical(parsed.Document) != source)
                    return Result(command, false, "InvalidMovie",
                        "Full-run replay requires a canonical v2 movie.");
                coordinator.ArmReplay(parsed.Document, command.Arguments.TryGetValue("pauseAtFrame", out var target)
                    ? long.Parse(target, CultureInfo.InvariantCulture) : -1);
                return Result(command, true, "Ok", "Replay armed at native frame 0.",
                    Fields("mode", coordinator.Mode, "nativeFrame", "0",
                        "movieId", codec.ComputeMovieId(parsed.Document)));
            }
            if (command.CommandId == AutomationCommandIds.FullRunStep)
                return BoundaryResult(command,
                    await coordinator.StepAsync(expectedFrame, cancellationToken));
            if (command.CommandId == AutomationCommandIds.FullRunPlay)
            {
                var boundary = await coordinator.RunAsync(expectedFrame, cancellationToken);
                if (boundary.Mode == "Fault") return BoundaryResult(command, boundary);
                return Result(command, true, "Ok", "Full-run playback started.",
                    Fields("mode", coordinator.Mode,
                        "nativeFrame", expectedFrame.ToString(CultureInfo.InvariantCulture)));
            }
            if (command.CommandId == AutomationCommandIds.FullRunStop)
            {
                var runtime = GetBoundSession();
                if (runtime == null)
                    return Result(command, false, "RuntimeNotReady",
                        "The full-run Runtime has not connected yet.");
                var stopped = await ForwardAsync(runtime, command,
                    IpcMessageTypes.FullRunStop,
                    Fields("requestId", command.RequestId, "expectedNativeFrame",
                        expectedFrame.ToString(CultureInfo.InvariantCulture)),
                    IpcMessageTypes.FullRunMovieDocument, cancellationToken);
                if (stopped.Success) coordinator.MarkStopped();
                return stopped;
            }
            return Result(command, false, "CapabilityMismatch",
                "Command is unavailable in this full-run session.");
        }

        private AutomationResultEnvelope BoundaryResult(
            AutomationCommandEnvelope command,
            NativeFrameBoundary boundary)
            => Result(command, boundary.Mode != "Fault",
                boundary.Mode == "Fault" ? "NativeFault" : "Ok",
                boundary.Mode == "Fault" ? boundary.Error : "Native frame boundary reached.",
                Fields("mode", boundary.Mode,
                    "nativeFrame", boundary.CompletedFrame.ToString(CultureInfo.InvariantCulture),
                    "ackSequence", boundary.AckSequence.ToString(CultureInfo.InvariantCulture)));

        private static string ArgumentOrDefault(
            AutomationCommandEnvelope command,
            string name,
            string fallback)
        {
            return command.Arguments.TryGetValue(name, out var value)
                ? value
                : fallback;
        }
    }
}
