using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class VerifiedStartupProfile
    {
        public const string CapabilityId =
            "native.clock-rng-pause.override.experimental.v31";
        public const string ProfileId = StartupProfileContract.ProfileId;
        public const string StartupPolicy =
            "create-suspended-early-apc-unity-then-bridge-v1";

        private static readonly string[] RuntimeFileNames =
        {
            "HollowKnightTAS.ClockBridge.dll",
            "HollowKnightTAS.ClockInjector.deps.json",
            "HollowKnightTAS.ClockInjector.dll",
            "HollowKnightTAS.ClockInjector.exe",
            "HollowKnightTAS.ClockInjector.runtimeconfig.json",
            "HollowKnightTAS.ClockPayload.dll"
        };

        private VerifiedStartupProfile(
            string bundleRoot,
            string gameExecutablePath,
            string injectorPath,
            string manifestPath,
            string startupProfileSha256,
            string gameExecutableSha256,
            string unityPlayerSha256,
            string assemblyCSharpSha256)
        {
            BundleRoot = bundleRoot;
            GameExecutablePath = gameExecutablePath;
            InjectorPath = injectorPath;
            ManifestPath = manifestPath;
            StartupProfileSha256 = startupProfileSha256;
            GameExecutableSha256 = gameExecutableSha256;
            UnityPlayerSha256 = unityPlayerSha256;
            AssemblyCSharpSha256 = assemblyCSharpSha256;
        }

        public string BundleRoot { get; }
        public string GameExecutablePath { get; }
        public string InjectorPath { get; }
        public string ManifestPath { get; }
        public string StartupProfileSha256 { get; }
        public string GameExecutableSha256 { get; }
        public string UnityPlayerSha256 { get; }
        public string AssemblyCSharpSha256 { get; }

        public void RequireStartupFrameGate()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(ManifestPath));
            if (!document.RootElement.TryGetProperty("startupFrameGateAbi", out var version)
                || !version.TryGetInt32(out var abi) || abi != 1)
                throw new InvalidOperationException("当前 ClockStartup 包不支持启动帧接管；原游戏不会因此被自动关闭。");
        }

        public static VerifiedStartupProfile Load(
            string bundleRoot,
            string gameExecutablePath)
        {
            var root = RequireDirectory(bundleRoot, nameof(bundleRoot));
            var game = RequireFile(
                gameExecutablePath,
                nameof(gameExecutablePath));
            if (!string.Equals(
                    Path.GetFileName(game),
                    "hollow_knight.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Verified startup target is not Hollow Knight.");
            }

            var manifestPath = RequireFixedFile(
                root,
                "clock-build-manifest-v1.json");
            var whitelistPath = RequireFixedFile(
                root,
                "clock-build-whitelist-v1.json");
            var manifest = JsonSerializer.Deserialize<ClockManifest>(
                File.ReadAllBytes(manifestPath),
                JsonOptions()) ?? throw new InvalidDataException(
                "Startup profile manifest is empty.");
            var whitelist = JsonSerializer.Deserialize<ClockWhitelist>(
                File.ReadAllBytes(whitelistPath),
                JsonOptions()) ?? throw new InvalidDataException(
                "Startup profile whitelist is empty.");

            if (manifest.SchemaVersion != 2
                || !string.Equals(
                    manifest.CapabilityId,
                    CapabilityId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    manifest.Profile,
                    ProfileId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    manifest.StartupPolicy,
                    StartupPolicy,
                    StringComparison.Ordinal)
                || manifest.BridgeAbi != StartupProfileContract.BridgeAbi
                || !string.Equals(manifest.RandomSynchronizationPolicy,
                    StartupProfileContract.RandomSynchronizationPolicyId,
                    StringComparison.Ordinal)
                || manifest.RandomSynchronizationSeed != StartupProfileContract.RandomSynchronizationSeed)
            {
                throw new InvalidDataException(
                    "Startup profile identity is not the required v40 native-scene profile.");
            }

            if (string.IsNullOrWhiteSpace(manifest.GameExecutable)
                || !string.Equals(
                    Path.GetFileName(manifest.GameExecutable),
                    "hollow_knight.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Startup profile does not declare Hollow Knight.");
            }

            var gameDirectory = Path.GetDirectoryName(game)
                                ?? throw new InvalidDataException(
                                    "Hollow Knight directory is unavailable.");
            var unityPlayer = RequireFile(
                Path.Combine(gameDirectory, "UnityPlayer.dll"),
                "UnityPlayer.dll");
            var assemblyCSharp = RequireFile(
                Path.Combine(
                    gameDirectory,
                    "hollow_knight_Data",
                    "Managed",
                    "Assembly-CSharp.dll"),
                "Assembly-CSharp.dll");
            RequireHash(game, manifest.GameExecutableSha256, "game executable");
            RequireHash(
                unityPlayer,
                manifest.UnityPlayerSha256,
                "UnityPlayer");
            RequireHash(
                assemblyCSharp,
                manifest.AssemblyCSharpSha256,
                "Assembly-CSharp");

            var declared = (manifest.RuntimeFileSet
                            ?? Array.Empty<ClockRuntimeFile>())
                .ToDictionary(
                    value => value.File ?? string.Empty,
                    StringComparer.Ordinal);
            if (declared.Count != RuntimeFileNames.Length
                || RuntimeFileNames.Any(name => !declared.ContainsKey(name))
                || declared.Keys.Any(
                    name => !RuntimeFileNames.Contains(
                        name,
                        StringComparer.Ordinal)))
            {
                throw new InvalidDataException(
                    "Startup profile runtime file set is not exact.");
            }

            foreach (var name in RuntimeFileNames)
            {
                var path = RequireFixedFile(root, name);
                var entry = declared[name];
                var length = new FileInfo(path).Length;
                if (entry.Length != length)
                {
                    throw new InvalidDataException(
                        "Startup profile length mismatch: " + name + ".");
                }

                RequireHash(path, entry.Sha256, name);
            }

            if (!string.Equals(
                    whitelist.ProcessImageSha256,
                    manifest.GameExecutableSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    whitelist.UnityPlayerSha256,
                    manifest.UnityPlayerSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    whitelist.AssemblyCSharpSha256,
                    manifest.AssemblyCSharpSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    whitelist.BridgeSha256,
                    manifest.BridgeSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    whitelist.PayloadSha256,
                    manifest.PayloadSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Startup whitelist and manifest do not have the same build binding.");
            }

            var injector = RequireFixedFile(
                root,
                "HollowKnightTAS.ClockInjector.exe");
            RequireHash(injector, manifest.InjectorSha256, "ClockInjector");
            return new VerifiedStartupProfile(
                root,
                game,
                injector,
                manifestPath,
                Sha256Utility.ComputeFileHex(manifestPath),
                manifest.GameExecutableSha256!,
                manifest.UnityPlayerSha256!,
                manifest.AssemblyCSharpSha256!);
        }

        public ColdRestoreBuildFingerprint BuildFingerprint(
            string environmentManifestSha256,
            string runtimeAssemblyPath,
            string companionAssemblyPath,
            string observerAssemblyPath = "")
        {
            return new ColdRestoreBuildFingerprint(
                environmentManifestSha256,
                GameExecutableSha256,
                UnityPlayerSha256,
                AssemblyCSharpSha256,
                Sha256Utility.ComputeFileHex(
                    RequireFile(runtimeAssemblyPath, nameof(runtimeAssemblyPath))),
                Sha256Utility.ComputeFileHex(
                    RequireFile(
                        companionAssemblyPath,
                        nameof(companionAssemblyPath))),
                StartupProfileSha256,
                string.IsNullOrEmpty(observerAssemblyPath)
                    ? string.Empty
                    : Sha256Utility.ComputeFileHex(
                        RequireFile(
                            observerAssemblyPath,
                            nameof(observerAssemblyPath))));
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling =
                    System.Text.Json.Serialization.JsonUnmappedMemberHandling
                        .Disallow
            };
        }

        private static string RequireDirectory(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A directory is required.", name);
            }

            var full = Path.GetFullPath(value);
            if (!Directory.Exists(full))
            {
                throw new DirectoryNotFoundException(full);
            }

            return full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static string RequireFile(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("A file is required.", name);
            }

            var full = Path.GetFullPath(value);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    "Verified startup file is missing.",
                    full);
            }

            return full;
        }

        private static string RequireFixedFile(string root, string name)
        {
            var path = RequireFile(Path.Combine(root, name), name);
            var expectedPrefix = root + Path.DirectorySeparatorChar;
            if (!path.StartsWith(
                    expectedPrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Startup component escaped its fixed bundle root.");
            }

            return path;
        }

        private static void RequireHash(
            string path,
            string? expected,
            string label)
        {
            if (!string.Equals(
                    Sha256Utility.ComputeFileHex(path),
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    label + " SHA-256 does not match the startup profile.");
            }
        }

        private sealed class ClockManifest
        {
            public int SchemaVersion { get; set; }
            public string? CapabilityId { get; set; }
            public string? Profile { get; set; }
            public int BridgeAbi { get; set; }
            public int StartupFrameGateAbi { get; set; }
            public string? StartupPolicy { get; set; }
            public string? RandomSynchronizationPolicy { get; set; }
            public int RandomSynchronizationSeed { get; set; }
            public string? Configuration { get; set; }
            public string? CreatedUtc { get; set; }
            public string? GameExecutable { get; set; }
            public string? GameExecutableSha256 { get; set; }
            public string? UnityPlayerSha256 { get; set; }
            public string? AssemblyCSharpSha256 { get; set; }
            public string? InjectorSha256 { get; set; }
            public string? InjectorManagedSha256 { get; set; }
            public string? InjectorDepsSha256 { get; set; }
            public string? InjectorRuntimeConfigSha256 { get; set; }
            public string? BridgeSha256 { get; set; }
            public string? PayloadSha256 { get; set; }
            public string? RuntimeFileSetSha256 { get; set; }
            public ClockRuntimeFile[]? RuntimeFileSet { get; set; }
            public string? BridgeSourceSha256 { get; set; }
            public string? PayloadSourceSha256 { get; set; }
            public string? InjectorSourceSha256 { get; set; }
        }

        private sealed class ClockRuntimeFile
        {
            public string? File { get; set; }
            public long Length { get; set; }
            public string? Sha256 { get; set; }
        }

        private sealed class ClockWhitelist
        {
            public int SchemaVersion { get; set; }
            public string? ProcessFileName { get; set; }
            public string? ProcessImageSha256 { get; set; }
            public string? UnityPlayerSha256 { get; set; }
            public string? AssemblyCSharpSha256 { get; set; }
            public string? BridgeSha256 { get; set; }
            public string? PayloadSha256 { get; set; }
        }
    }

    public sealed class VerifiedGameLaunchHandle :
        IColdRestoreGameLaunchHandle
    {
        private readonly NativeHostJob job;
        private readonly Process process;
        private bool released;
        private bool disposed;

        internal VerifiedGameLaunchHandle(
            NativeHostJob job,
            Process process,
            DateTimeOffset processStartedAtUtc,
            string launcherEvidence)
        {
            this.job = job;
            this.process = process;
            ProcessStartedAtUtc = processStartedAtUtc;
            LauncherEvidence = launcherEvidence;
        }

        public int ProcessId => process.Id;
        public DateTimeOffset ProcessStartedAtUtc { get; }
        public string LauncherEvidence { get; }
        public bool HasExited
        {
            get
            {
                process.Refresh();
                return process.HasExited;
            }
        }

        public void ReleaseSupervision()
        {
            if (disposed || released)
            {
                return;
            }

            job.ReleaseWithoutTermination();
            released = true;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            job.Dispose();
            process.Dispose();
        }
    }

    public sealed class VerifiedGameLauncher : IColdRestoreGameLauncher
    {
        private readonly VerifiedStartupProfile profile;
        private readonly VerifiedLaunchReceiptStore? receiptStore;

        public VerifiedGameLauncher(
            VerifiedStartupProfile profile,
            VerifiedLaunchReceiptStore? receiptStore = null)
        {
            this.profile = profile
                           ?? throw new ArgumentNullException(nameof(profile));
            this.receiptStore = receiptStore;
        }

        public async Task<VerifiedGameLaunchHandle> LaunchAsync(
            ColdRestoreIntent intent,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (intent == null)
            {
                throw new ArgumentNullException(nameof(intent));
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            if (!string.Equals(
                    intent.BuildFingerprint.GameExecutableSha256,
                    profile.GameExecutableSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.UnityPlayerSha256,
                    profile.UnityPlayerSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.AssemblyCSharpSha256,
                    profile.AssemblyCSharpSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    intent.BuildFingerprint.StartupProfileSha256,
                    profile.StartupProfileSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cold intent does not match the verified startup profile.");
            }

            return await LaunchRunAsync(
                intent.OperationId,
                timeout,
                cancellationToken);
        }

        public async Task<VerifiedGameLaunchHandle> LaunchInteractiveAsync(
            string runId,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            StartupBootGate? bootGate = null, bool hideWindow = false)
        {
            if (bootGate?.IsFrameBased == true) profile.RequireStartupFrameGate();
            if (!IpcIdentifier.IsValid(runId, 96))
            {
                throw new ArgumentException(
                    "The interactive startup run ID is invalid.",
                    nameof(runId));
            }
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            return await LaunchRunAsync(runId, timeout, cancellationToken, bootGate, hideWindow);
        }

        private async Task<VerifiedGameLaunchHandle> LaunchRunAsync(
            string runId,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            StartupBootGate? bootGate = null, bool hideWindow = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var launchGate = GameLaunchGate.Acquire();
            // All interactive and cold launch callers share this gate. Their
            // earlier preflight checks alone cannot prevent competing starts.
            var existingGames = Process.GetProcessesByName("hollow_knight");
            try
            {
                // Windows can still enumerate an exited process while another
                // observer holds its handle during a protected cold restart.
                if (existingGames.Any(game => !game.HasExited))
                    throw new InvalidOperationException("A game is already running. No additional game was started.");
            }
            finally
            {
                foreach (var game in existingGames) game.Dispose();
            }
            PendingSlotRecovery.RecoverBeforeLaunch();
            var runArgument = "--hktas-reference-run=" + runId;
            var launchArguments = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(
                    new[] { runArgument }));
            var start = new ProcessStartInfo
            {
                FileName = profile.InjectorPath,
                WorkingDirectory = profile.BundleRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(
                "--launch-game=" + profile.GameExecutablePath);
            start.ArgumentList.Add(
                "--launch-arguments-base64=" + launchArguments);
            // A Studio auto-started by a game can inherit its gate variables.
            // Only this explicit interactive launch may arm a fresh gate.
            start.Environment.Remove("HKTAS_BOOT_GATE_TOKEN");
            start.Environment.Remove("HKTAS_BOOT_GATE_OWNER");
            start.Environment.Remove("HKTAS_BOOT_FRAME_GATE");
            bootGate?.ConfigureInjector(start);
            start.Environment.Remove(RestorePresentation.HiddenLaunchVariable);
            if (hideWindow) start.Environment[RestorePresentation.HiddenLaunchVariable] = "1";
            // Diagnostic traces are opt-in via the launching environment.
            // Ordinary restores should not format/hash thousands of trace rows.
            if (bootGate?.IsFullRun != true)
                start.Environment.Remove("HKTAS_FULL_RUN_BOSS_TRACE");

            var job = new NativeHostJob();
            Process? injector = null;
            try
            {
                DiagnosticLogExporter.PreserveGameLogs();
                injector = Process.Start(start)
                           ?? throw new InvalidOperationException(
                               "Verified ClockInjector did not start.");
                job.Assign(injector);
                var standardOutput = injector.StandardOutput.ReadToEndAsync();
                var standardError = injector.StandardError.ReadToEndAsync();
                using (var linked = CancellationTokenSource
                           .CreateLinkedTokenSource(cancellationToken))
                {
                    linked.CancelAfter(timeout);
                    await injector.WaitForExitAsync(linked.Token);
                }

                var stdout = await standardOutput;
                var stderr = await standardError;
                if (injector.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "Verified ClockInjector rejected launch: "
                        + Sanitize(stderr));
                }

                using (var document = JsonDocument.Parse(stdout))
                {
                    var root = document.RootElement;
                    var processId = ReadPositiveInt32(root, "processId");
                    var startTicks = ReadPositiveInt64(
                        root,
                        "processStartUtcTicks");
                    if (!ReadExact(root, "status", "startup-bridge-loaded")
                        || !ReadExact(
                            root,
                            "capabilityId",
                            VerifiedStartupProfile.CapabilityId)
                        || !ReadExact(
                            root,
                            "profile",
                            VerifiedStartupProfile.ProfileId)
                        || !ReadExact(
                            root,
                            "startupPolicy",
                            VerifiedStartupProfile.StartupPolicy)
                        || !ReadExact(root, "runId", runId)
                        || !ReadExact(
                            root,
                            "processImageSha256",
                            profile.GameExecutableSha256)
                        || !ReadExact(
                            root,
                            "unityPlayerSha256",
                            profile.UnityPlayerSha256)
                        || !ReadExact(
                            root,
                            "assemblyCSharpSha256",
                            profile.AssemblyCSharpSha256))
                    {
                        throw new InvalidDataException(
                            "ClockInjector launch evidence is not bound to the cold intent.");
                    }
                    if (bootGate?.IsFullRun == true)
                    {
                        if (!ReadExact(root, "fullRunCapability", MovieProtocolV2.SelectedNativeProfileId)
                            || !ReadExact(root, "fullRunRandomPolicy",
                                MovieProtocolV2.RandomSynchronizationPolicyId)
                            || !root.TryGetProperty("fullRunGateAbi", out var gateAbi)
                            || gateAbi.GetInt32() != 2
                            || !root.TryGetProperty("saveGuardStatus", out var guardStatus)
                            || guardStatus.GetString() == "fault")
                            throw new InvalidDataException(
                                "ClockInjector did not confirm the full-run frame and save-guard capability.");
                    }

                    var game = Process.GetProcessById(processId);
                    game.Refresh();
                    var startedAtUtc = new DateTimeOffset(
                        game.StartTime.ToUniversalTime());
                    if (startedAtUtc.UtcTicks != startTicks)
                    {
                        game.Dispose();
                        throw new InvalidDataException(
                            "Launched Hollow Knight PID creation time is stale.");
                    }

                    injector.Dispose();
                    injector = null;
                    var handle = new VerifiedGameLaunchHandle(
                        job,
                        game,
                        startedAtUtc,
                        stdout);
                    try
                    {
                        receiptStore?.Record(profile, runId, handle);
                        return handle;
                    }
                    catch
                    {
                        handle.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                job.Dispose();
                throw;
            }
            finally
            {
                injector?.Dispose();
            }
        }

        async Task<IColdRestoreGameLaunchHandle>
            IColdRestoreGameLauncher.LaunchAsync(
                ColdRestoreIntent intent,
                TimeSpan timeout,
                CancellationToken cancellationToken)
        {
            return await LaunchAsync(intent, timeout, cancellationToken);
        }

        private static bool ReadExact(
            JsonElement root,
            string name,
            string expected)
        {
            return root.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && string.Equals(
                       value.GetString(),
                       expected,
                       StringComparison.Ordinal);
        }

        private static int ReadPositiveInt32(
            JsonElement root,
            string name)
        {
            if (!root.TryGetProperty(name, out var value)
                || !value.TryGetInt32(out var parsed)
                || parsed <= 0)
            {
                throw new InvalidDataException(
                    "ClockInjector did not return a valid " + name + ".");
            }

            return parsed;
        }

        private static long ReadPositiveInt64(
            JsonElement root,
            string name)
        {
            if (!root.TryGetProperty(name, out var value)
                || !value.TryGetInt64(out var parsed)
                || parsed <= 0)
            {
                throw new InvalidDataException(
                    "ClockInjector did not return a valid " + name + ".");
            }

            return parsed;
        }

        private static string Sanitize(string value)
        {
            var sanitized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace('\\', '/');
            return sanitized.Substring(
                0,
                Math.Min(512, sanitized.Length));
        }
    }
}
