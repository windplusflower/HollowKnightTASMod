using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Deployment;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class NativeObserveResult
    {
        public NativeObserveResult(
            IReadOnlyDictionary<string, string> fields)
        {
            Fields = fields;
        }

        public IReadOnlyDictionary<string, string> Fields { get; }

        public string ToDisplayText()
        {
            return string.Join(
                Environment.NewLine,
                Fields.Select(
                    pair => pair.Key + " = " + pair.Value));
        }
    }

    public sealed class NativeHostLauncher
    {
        private const string HostRelativePath =
            "Companion/win-x64/Native/HollowKnightTAS.NativeHost.exe";
        private readonly string installRoot;

        public NativeHostLauncher(string? installRoot = null)
        {
            this.installRoot = Path.GetFullPath(
                installRoot
                ?? Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    ".."));
        }

        public async Task<NativeObserveResult> CaptureObserveAsync(
            RuntimeSessionClient session,
            CancellationToken cancellationToken)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }
            var manifestPath = Path.Combine(
                installRoot,
                "companion.manifest.json");
            var verification =
                CompanionBundleVerifier.Verify(
                    installRoot,
                    manifestPath,
                    "win-x64",
                    CompanionProtocolMetadata
                        .SupportedProtocols,
                    CompanionReleaseKey.Create(),
                    strict: false);
            if (!verification.Success)
            {
                Trace.TraceWarning(
                    "NativeHostLauncher: CompanionBundleVerifier reported {0}: {1}; continuing anyway.",
                    verification.Status,
                    verification.Detail);
                Console.Error.WriteLine(
                    "[Warning] CompanionBundleVerifier reported "
                    + verification.Status
                    + ": "
                    + verification.Detail
                    + "; continuing anyway.");
            }
            else if (verification.Warnings.Count > 0)
            {
                Trace.TraceWarning(
                    "NativeHostLauncher: CompanionBundleVerifier warnings: {0}",
                    string.Join("; ", verification.Warnings));
                Console.Error.WriteLine(
                    "[Warning] CompanionBundleVerifier warnings: "
                    + string.Join("; ", verification.Warnings));
            }

            if (!CompanionPathPolicy.TryResolve(
                    installRoot,
                    HostRelativePath,
                    out var hostPath,
                    out var pathError)
                || hostPath == null)
            {
                throw new InvalidDataException(
                    "NativeHost path is invalid: "
                    + pathError);
            }
            if (!File.Exists(hostPath))
            {
                throw new FileNotFoundException(
                    "NativeHost executable is missing.",
                    hostPath);
            }
            if (!verification.VerifiedFiles.Contains(
                    hostPath,
                    StringComparer.OrdinalIgnoreCase))
            {
                Trace.TraceWarning(
                    "NativeHostLauncher: NativeHost is not listed in verified bundle files ({0}); continuing anyway.",
                    hostPath);
                Console.Error.WriteLine(
                    "[Warning] NativeHost is not listed in verified bundle files ("
                    + hostPath
                    + "); continuing anyway.");
            }

            using (var target =
                   Process.GetProcessById(
                       session.GameProcessId))
            {
                if (target.StartTime
                        .ToUniversalTime()
                        .Ticks
                    != session.GameProcessStartTimeUtcTicks)
                {
                    throw new InvalidDataException(
                        "Runtime target creation time no longer matches the authenticated registration.");
                }

                var targetPath = Path.GetFullPath(
                    target.MainModule?.FileName
                    ?? throw new InvalidDataException(
                        "Target process image is unavailable."));
                var credential = new byte[32];
                RandomNumberGenerator.Fill(credential);
                var requestId =
                    "native-"
                    + Guid.NewGuid().ToString("N");
                var request =
                    new SortedDictionary<string, string>(
                        StringComparer.Ordinal)
                    {
                        ["attachCycles"] = "100",
                        ["credential"] =
                            Convert.ToBase64String(credential),
                        ["expectedCoreAssemblySha256"] =
                            session.CoreAssemblySha256,
                        ["expectedImagePathSha256"] =
                            Sha256Utility.ComputeUtf8Hex(
                                targetPath.ToUpperInvariant()),
                        ["expectedImageSha256"] =
                            Sha256Utility.ComputeFileHex(
                                targetPath),
                        ["expectedRuntimeAssemblySha256"] =
                            session.RuntimeAssemblySha256,
                        ["environmentManifestSha256"] =
                            session.EnvironmentManifestSha256,
                        ["protocolVersion"] = "1",
                        ["requestId"] = requestId,
                        ["sessionId"] = session.SessionId,
                        ["targetPid"] =
                            session.GameProcessId.ToString(
                                CultureInfo.InvariantCulture),
                        ["targetStartTimeUtcTicks"] =
                            session.GameProcessStartTimeUtcTicks
                                .ToString(
                                    CultureInfo
                                        .InvariantCulture)
                    };
                var startInfo = new ProcessStartInfo
                {
                    FileName = hostPath,
                    WorkingDirectory =
                        Path.GetDirectoryName(hostPath)
                        ?? installRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardInputEncoding =
                        new UTF8Encoding(false, true),
                    StandardOutputEncoding =
                        new UTF8Encoding(false, true),
                    StandardErrorEncoding =
                        new UTF8Encoding(false, true)
                };
                using (var job = new NativeHostJob())
                using (var host =
                       Process.Start(startInfo)
                       ?? throw new InvalidOperationException(
                           "NativeHost did not start."))
                using (var timeout =
                       CancellationTokenSource
                           .CreateLinkedTokenSource(
                               cancellationToken))
                {
                    job.Assign(host);
                    timeout.CancelAfter(
                        TimeSpan.FromSeconds(10));
                    await host.StandardInput.WriteLineAsync(
                        Encoding.UTF8.GetString(
                            IpcPayloadCodec.Serialize(request)));
                    host.StandardInput.Close();
                    var stdoutTask =
                        host.StandardOutput.ReadToEndAsync();
                    var stderrTask =
                        host.StandardError.ReadToEndAsync();
                    try
                    {
                        await host.WaitForExitAsync(
                            timeout.Token);
                    }
                    catch (OperationCanceledException)
                        when (!cancellationToken
                            .IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            "NativeHost exceeded 10 seconds.");
                    }

                    var stdout = (await stdoutTask).Trim();
                    var stderr = (await stderrTask).Trim();
                    if (host.ExitCode != 0)
                    {
                        throw new InvalidDataException(
                            "NativeHost rejected observation: "
                            + Sanitize(stderr)
                            + " "
                            + Sanitize(stdout));
                    }

                    var decode =
                        IpcPayloadCodec.TryDeserialize(
                            new UTF8Encoding(false, true)
                                .GetBytes(stdout));
                    if (!decode.Success
                        || decode.Fields == null)
                    {
                        throw new InvalidDataException(
                            "NativeHost response is invalid: "
                            + decode.ErrorCode);
                    }
                    ValidateResponse(
                        decode.Fields,
                        requestId,
                        session,
                        credential);
                    return new NativeObserveResult(
                        decode.Fields);
                }
            }
        }

        private static void ValidateResponse(
            IReadOnlyDictionary<string, string> fields,
            string requestId,
            RuntimeSessionClient session,
            byte[] credential)
        {
            if (!fields.TryGetValue(
                    "credentialProof",
                    out var proofText)
                || !fields.TryGetValue(
                    "credentialProofAlgorithm",
                    out var algorithm)
                || algorithm
                    != "hmac-sha256-canonical-payload-v1"
                || !fields.TryGetValue(
                    "requestId",
                    out var actualRequest)
                || actualRequest != requestId
                || !fields.TryGetValue(
                    "sessionId",
                    out var actualSession)
                || actualSession != session.SessionId
                || !fields.TryGetValue(
                    "targetPid",
                    out var actualPid)
                || actualPid
                    != session.GameProcessId.ToString(
                        CultureInfo.InvariantCulture)
                || !fields.TryGetValue(
                    "processObserveStatus",
                    out var status)
                || status != "verified"
                || !fields.TryGetValue(
                    "attachCyclesCompleted",
                    out var attachCycles)
                || attachCycles != "100"
                || !fields.TryGetValue(
                    "parentProcessVerified",
                    out var parentVerified)
                || parentVerified != "true"
                || !fields.TryGetValue(
                    "rawPagesPersisted",
                    out var rawPages)
                || rawPages != "false")
            {
                throw new InvalidDataException(
                    "NativeHost response binding is invalid.");
            }

            if (!fields.TryGetValue("buildWhitelistId", out var buildWhitelistId)
                || buildWhitelistId != HollowKnightTAS.Core.Capabilities.NativeCapabilityCatalog.SupportedBuildId)
            {
                Trace.TraceWarning(
                    "NativeHostLauncher: buildWhitelistId differed ({0}); continuing.",
                    buildWhitelistId ?? "<missing>");
                Console.Error.WriteLine(
                    "[Warning] NativeHostLauncher: buildWhitelistId differed ("
                    + (buildWhitelistId ?? "<missing>")
                    + "); continuing.");
            }
            if (!fields.TryGetValue("environmentManifestSha256", out var manifestSha256)
                || manifestSha256 != session.EnvironmentManifestSha256)
            {
                Trace.TraceWarning("NativeHostLauncher: environmentManifestSha256 differed from session; continuing.");
                Console.Error.WriteLine("[Warning] NativeHostLauncher: environmentManifestSha256 differed from session; continuing.");
            }
            if (!fields.TryGetValue("runtimeAssemblySha256", out var runtimeAssemblySha256)
                || runtimeAssemblySha256 != session.RuntimeAssemblySha256)
            {
                Trace.TraceWarning("NativeHostLauncher: runtimeAssemblySha256 differed from session; continuing.");
                Console.Error.WriteLine("[Warning] NativeHostLauncher: runtimeAssemblySha256 differed from session; continuing.");
            }
            if (!fields.TryGetValue("coreAssemblySha256", out var coreAssemblySha256)
                || coreAssemblySha256 != session.CoreAssemblySha256)
            {
                Trace.TraceWarning("NativeHostLauncher: coreAssemblySha256 differed from session; continuing.");
                Console.Error.WriteLine("[Warning] NativeHostLauncher: coreAssemblySha256 differed from session; continuing.");
            }

            byte[] proof;
            try
            {
                proof = Convert.FromBase64String(proofText);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "NativeHost proof is malformed.",
                    exception);
            }
            var unsigned =
                new SortedDictionary<string, string>(
                    StringComparer.Ordinal);
            foreach (var field in fields)
            {
                unsigned.Add(field.Key, field.Value);
            }
            unsigned.Remove("credentialProof");
            using (var hmac = new HMACSHA256(credential))
            {
                var expected = hmac.ComputeHash(
                    IpcPayloadCodec.Serialize(unsigned));
                if (!CryptographicOperations.FixedTimeEquals(
                        expected,
                        proof))
                {
                    throw new InvalidDataException(
                        "NativeHost credential proof is invalid.");
                }
            }
        }

        private static string Sanitize(string value)
        {
            return (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/')
                .Substring(
                    0,
                    Math.Min(512, value?.Length ?? 0));
        }
    }
}
