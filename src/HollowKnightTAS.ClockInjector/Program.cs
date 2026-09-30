using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HollowKnightTAS.ClockInjector
{
    internal static class Program
    {
        private const string BridgeFileName =
            "HollowKnightTAS.ClockBridge.dll";
        private const string PayloadFileName =
            "HollowKnightTAS.ClockPayload.dll";
        private const string WhitelistFileName =
            "clock-build-whitelist-v1.json";

        private const uint ProcessCreateThread = 0x0002;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessVmWrite = 0x0020;
        private const uint MemCommit = 0x1000;
        private const uint MemReserve = 0x2000;
        private const uint MemRelease = 0x8000;
        private const uint PageReadWrite = 0x04;
        private const uint WaitObject0 = 0x00000000;
        private const uint CreateSuspended = 0x00000004;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const string StartupLatchEnvironment =
            "HKTAS_CLOCK_STARTUP_LATCH";

        private static int Main(string[] args)
        {
            try
            {
                var arguments = ParseArguments(args);
                var baseDirectory = Path.GetFullPath(
                    AppContext.BaseDirectory);
                var bridgePath = FixedSibling(
                    baseDirectory,
                    BridgeFileName);
                var payloadPath = FixedSibling(
                    baseDirectory,
                    PayloadFileName);
                var whitelistPath = FixedSibling(
                    baseDirectory,
                    WhitelistFileName);
                RequireFile(bridgePath);
                RequireFile(payloadPath);
                RequireFile(whitelistPath);

                var whitelist = JsonSerializer.Deserialize<ClockWhitelist>(
                    File.ReadAllText(
                        whitelistPath,
                        new UTF8Encoding(false, true)),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? throw new InvalidDataException(
                        "Clock whitelist is empty.");
                whitelist.Validate();
                RequireHash(
                    bridgePath,
                    whitelist.BridgeSha256,
                    "Clock Bridge");
                RequireHash(
                    payloadPath,
                    whitelist.PayloadSha256,
                    "Clock Payload");
                if (arguments.ContainsKey("launch-game"))
                {
                    return LaunchClockedProcess(
                        arguments,
                        bridgePath,
                        payloadPath,
                        whitelist);
                }

                var processId = ParsePositiveInt(arguments, "pid");
                var expectedStartTicks = ParsePositiveLong(
                    arguments,
                    "start-time-utc-ticks");
                using var process = Process.GetProcessById(processId);
                process.Refresh();
                var actualStartTicks =
                    process.StartTime.ToUniversalTime().Ticks;
                if (actualStartTicks != expectedStartTicks)
                {
                    throw new InvalidOperationException(
                        "Target process creation time does not match.");
                }

                var imagePath = Path.GetFullPath(
                    process.MainModule?.FileName
                    ?? throw new InvalidOperationException(
                        "Target process image is unavailable."));
                if (!string.Equals(
                        Path.GetFileName(imagePath),
                        "hollow_knight.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Target image is not Hollow Knight.");
                }

                var gameDirectory = Path.GetDirectoryName(imagePath)
                                    ?? throw new InvalidOperationException(
                                        "Target game directory is unavailable.");
                var unityPlayerPath = Path.Combine(
                    gameDirectory,
                    "UnityPlayer.dll");
                var assemblyCSharpPath = Path.Combine(
                    gameDirectory,
                    "hollow_knight_Data",
                    "Managed",
                    "Assembly-CSharp.dll");
                RequireHash(
                    imagePath,
                    whitelist.ProcessImageSha256,
                    "Hollow Knight image");
                RequireHash(
                    unityPlayerPath,
                    whitelist.UnityPlayerSha256,
                    "UnityPlayer");
                RequireHash(
                    assemblyCSharpPath,
                    whitelist.AssemblyCSharpSha256,
                    "Assembly-CSharp");

                Inject(process, bridgePath);
                var result = new SortedDictionary<string, object>(
                    StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 1,
                    ["status"] = "bridge-loaded",
                    ["capabilityId"] =
                        "native.clock-rng-pause.override.experimental.v31",
                    ["processId"] = processId,
                    ["processStartUtcTicks"] = actualStartTicks,
                    ["processImageSha256"] =
                        whitelist.ProcessImageSha256,
                    ["bridgeSha256"] = whitelist.BridgeSha256,
                    ["payloadSha256"] = whitelist.PayloadSha256,
                    ["profile"] =
                        "external-unity-startup-continuous-clock-v40-native-scene-lifecycle",
                    ["bridgeAbi"] = 10,
                    ["randomSynchronizationPolicy"] =
                        "unity-init-state-at-root-only-native-scene-lifecycle-v19",
                    ["randomSynchronizationSeed"] = 1212896321
                };
                Console.Out.WriteLine(
                    JsonSerializer.Serialize(result));
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    JsonSerializer.Serialize(
                        new SortedDictionary<string, object>(
                            StringComparer.Ordinal)
                        {
                            ["schemaVersion"] = 1,
                            ["status"] = "rejected",
                            ["errorType"] =
                                exception.GetType().Name,
                            ["error"] = exception.Message
                        }));
                return 1;
            }
        }

        private static void Inject(Process process, string bridgePath)
        {
            var bridgeBytes = Encoding.Unicode.GetBytes(
                bridgePath + '\0');
            var handle = OpenProcess(
                ProcessCreateThread
                | ProcessQueryInformation
                | ProcessVmOperation
                | ProcessVmWrite,
                false,
                process.Id);
            if (handle == IntPtr.Zero)
            {
                throw Win32("OpenProcess");
            }

            IntPtr remotePath = IntPtr.Zero;
            IntPtr thread = IntPtr.Zero;
            try
            {
                remotePath = VirtualAllocEx(
                    handle,
                    IntPtr.Zero,
                    checked((nuint)bridgeBytes.Length),
                    MemCommit | MemReserve,
                    PageReadWrite);
                if (remotePath == IntPtr.Zero)
                {
                    throw Win32("VirtualAllocEx");
                }

                if (!WriteProcessMemory(
                        handle,
                        remotePath,
                        bridgeBytes,
                        checked((nuint)bridgeBytes.Length),
                        out var written)
                    || written != checked((nuint)bridgeBytes.Length))
                {
                    throw Win32("WriteProcessMemory");
                }

                var localKernel32 = GetModuleHandleW("kernel32.dll");
                var localLoadLibrary = GetProcAddress(
                    localKernel32,
                    "LoadLibraryW");
                if (localKernel32 == IntPtr.Zero
                    || localLoadLibrary == IntPtr.Zero)
                {
                    throw Win32("GetProcAddress(LoadLibraryW)");
                }

                var remoteKernel32 = process.Modules
                    .Cast<ProcessModule>()
                    .FirstOrDefault(
                        module => string.Equals(
                            module.ModuleName,
                            "kernel32.dll",
                            StringComparison.OrdinalIgnoreCase))
                    ?.BaseAddress
                    ?? throw new InvalidOperationException(
                        "Target kernel32.dll module is unavailable.");
                var loadLibraryOffset =
                    localLoadLibrary.ToInt64()
                    - localKernel32.ToInt64();
                var remoteLoadLibrary = new IntPtr(
                    checked(
                        remoteKernel32.ToInt64()
                        + loadLibraryOffset));
                thread = CreateRemoteThread(
                    handle,
                    IntPtr.Zero,
                    0,
                    remoteLoadLibrary,
                    remotePath,
                    0,
                    out _);
                if (thread == IntPtr.Zero)
                {
                    throw Win32("CreateRemoteThread");
                }

                var wait = WaitForSingleObject(thread, 10000);
                if (wait != WaitObject0)
                {
                    throw new TimeoutException(
                        "Clock Bridge LoadLibrary thread did not finish.");
                }

                if (!GetExitCodeThread(thread, out var exitCode)
                    || exitCode == 0)
                {
                    throw Win32("Clock Bridge LoadLibraryW");
                }
            }
            finally
            {
                if (thread != IntPtr.Zero)
                {
                    CloseHandle(thread);
                }

                if (remotePath != IntPtr.Zero)
                {
                    VirtualFreeEx(
                        handle,
                        remotePath,
                        0,
                        MemRelease);
                }

                CloseHandle(handle);
            }
        }

        private static int LaunchClockedProcess(
            IReadOnlyDictionary<string, string> arguments,
            string bridgePath,
            string payloadPath,
            ClockWhitelist whitelist)
        {
            var gamePath = Path.GetFullPath(arguments["launch-game"]);
            if (!string.Equals(
                    Path.GetFileName(gamePath),
                    "hollow_knight.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Launch target is not Hollow Knight.");
            }
            var gameDirectory = Path.GetDirectoryName(gamePath)
                                ?? throw new InvalidOperationException(
                                    "Launch game directory is unavailable.");
            var unityPlayerPath = Path.Combine(
                gameDirectory,
                "UnityPlayer.dll");
            var assemblyCSharpPath = Path.Combine(
                gameDirectory,
                "hollow_knight_Data",
                "Managed",
                "Assembly-CSharp.dll");
            RequireHash(
                gamePath,
                whitelist.ProcessImageSha256,
                "Hollow Knight image");
            RequireHash(
                unityPlayerPath,
                whitelist.UnityPlayerSha256,
                "UnityPlayer");
            RequireHash(
                assemblyCSharpPath,
                whitelist.AssemblyCSharpSha256,
                "Assembly-CSharp");

            var launchArguments = DecodeLaunchArguments(
                arguments["launch-arguments-base64"]);
            const string runPrefix = "--hktas-reference-run=";
            var runIds = launchArguments
                .Where(argument => argument.StartsWith(
                    runPrefix,
                    StringComparison.Ordinal))
                .Select(argument => argument.Substring(runPrefix.Length))
                .ToArray();
            if (runIds.Length != 1 || !IsIdentifier(runIds[0]))
            {
                throw new ArgumentException(
                    "Startup launch requires one valid T24 run identifier.");
            }
            var runId = runIds[0];
            var commandLine = new StringBuilder(QuoteWindowsArgument(gamePath));
            foreach (var argument in launchArguments)
            {
                commandLine.Append(' ');
                commandLine.Append(QuoteWindowsArgument(argument));
            }

            var environment = Marshal.StringToHGlobalUni(
                BuildStartupEnvironmentBlock(runId));
            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                // Restore presentation owns the visible still image until the target is ready.
                Flags = Environment.GetEnvironmentVariable("HKTAS_RESTORE_HIDDEN_WINDOW") == "1" ? 1 : 0,
                ShowWindow = 0
            };
            ProcessInformation created = default;
            var createdProcess = false;
            var launchSucceeded = false;
            var remotePaths = new List<IntPtr>();
            try
            {
                if (!CreateProcessW(
                        gamePath,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreateSuspended | CreateUnicodeEnvironment,
                        environment,
                        gameDirectory,
                        ref startup,
                        out created))
                {
                    throw Win32("CreateProcessW(CREATE_SUSPENDED)");
                }
                createdProcess = true;

                var localKernel32 = GetModuleHandleW("kernel32.dll");
                var localLoadLibrary = GetProcAddress(
                    localKernel32,
                    "LoadLibraryW");
                if (localKernel32 == IntPtr.Zero
                    || localLoadLibrary == IntPtr.Zero)
                {
                    throw Win32("GetProcAddress(LoadLibraryW)");
                }

                foreach (var path in new[] { unityPlayerPath, bridgePath })
                {
                    var bytes = Encoding.Unicode.GetBytes(path + '\0');
                    var remotePath = VirtualAllocEx(
                        created.Process,
                        IntPtr.Zero,
                        checked((nuint)bytes.Length),
                        MemCommit | MemReserve,
                        PageReadWrite);
                    if (remotePath == IntPtr.Zero)
                    {
                        throw Win32("VirtualAllocEx(startup path)");
                    }
                    remotePaths.Add(remotePath);
                    if (!WriteProcessMemory(
                            created.Process,
                            remotePath,
                            bytes,
                            checked((nuint)bytes.Length),
                            out var written)
                        || written != checked((nuint)bytes.Length))
                    {
                        throw Win32("WriteProcessMemory(startup path)");
                    }
                    if (QueueUserAPC(
                            localLoadLibrary,
                            created.Thread,
                            checked((nuint)remotePath.ToInt64())) == 0)
                    {
                        throw Win32("QueueUserAPC(LoadLibraryW)");
                    }
                }

                var previousSuspendCount = ResumeThread(created.Thread);
                if (previousSuspendCount == uint.MaxValue
                    || previousSuspendCount != 1u)
                {
                    throw Win32("ResumeThread(primary)");
                }

                using var process = Process.GetProcessById(
                    checked((int)created.ProcessId));
                ProcessModule? remoteKernel32 = null;
                var unityLoaded = false;
                var bridgeLoaded = false;
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            "Hollow Knight exited during startup APC handoff.");
                    }
                    try
                    {
                        var modules = process.Modules.Cast<ProcessModule>().ToArray();
                        remoteKernel32 = modules.FirstOrDefault(
                            module => string.Equals(
                                module.ModuleName,
                                "kernel32.dll",
                                StringComparison.OrdinalIgnoreCase));
                        unityLoaded = modules.Any(
                            module => string.Equals(
                                module.ModuleName,
                                "UnityPlayer.dll",
                                StringComparison.OrdinalIgnoreCase));
                        bridgeLoaded = modules.Any(
                            module => string.Equals(
                                module.ModuleName,
                                BridgeFileName,
                                StringComparison.OrdinalIgnoreCase));
                    }
                    catch (InvalidOperationException)
                    {
                        // The loader can temporarily make the module list
                        // unavailable. The fixed deadline remains fail-closed.
                    }
                    if (remoteKernel32 != null && unityLoaded && bridgeLoaded)
                    {
                        break;
                    }
                    System.Threading.Thread.Sleep(10);
                }
                if (remoteKernel32 == null || !unityLoaded || !bridgeLoaded)
                {
                    throw new TimeoutException(
                        "Startup APC modules did not become observable.");
                }

                var loadLibraryOffset =
                    localLoadLibrary.ToInt64() - localKernel32.ToInt64();
                var remoteLoadLibrary = new IntPtr(
                    checked(
                        remoteKernel32.BaseAddress.ToInt64()
                        + loadLibraryOffset));
                if (remoteLoadLibrary != localLoadLibrary)
                {
                    throw new InvalidOperationException(
                        "Queued and verified remote LoadLibraryW addresses differ.");
                }

                process.Refresh();
                var startTicks = process.StartTime.ToUniversalTime().Ticks;
                var result = new SortedDictionary<string, object>(
                    StringComparer.Ordinal)
                {
                    ["schemaVersion"] = 2,
                    ["status"] = "startup-bridge-loaded",
                    ["capabilityId"] =
                        "native.clock-rng-pause.override.experimental.v31",
                    ["profile"] =
                        "external-unity-startup-continuous-clock-v40-native-scene-lifecycle",
                    ["bridgeAbi"] = 10,
                    ["processId"] = process.Id,
                    ["processStartUtcTicks"] = startTicks,
                    ["primaryThreadId"] = created.ThreadId,
                    ["runId"] = runId,
                    ["startupPolicy"] =
                        "create-suspended-early-apc-unity-then-bridge-v1",
                    ["queuedApcCount"] = 2,
                    ["queuedApcOrder"] = new[]
                    {
                        "UnityPlayer.dll",
                        BridgeFileName
                    },
                    ["localLoadLibraryAddress"] =
                        localLoadLibrary.ToInt64(),
                    ["verifiedRemoteLoadLibraryAddress"] =
                        remoteLoadLibrary.ToInt64(),
                    ["loadLibraryAddressEquivalent"] = true,
                    ["processImageSha256"] =
                        whitelist.ProcessImageSha256,
                    ["unityPlayerSha256"] =
                        whitelist.UnityPlayerSha256,
                    ["assemblyCSharpSha256"] =
                        whitelist.AssemblyCSharpSha256,
                    ["bridgeSha256"] = whitelist.BridgeSha256,
                    ["payloadSha256"] = whitelist.PayloadSha256,
                    ["randomSynchronizationPolicy"] =
                        "unity-init-state-at-root-only-native-scene-lifecycle-v19",
                    ["randomSynchronizationSeed"] = 1212896321
                };
                if (string.Equals(Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2"),
                        "1", StringComparison.Ordinal))
                {
                    var token = Environment.GetEnvironmentVariable("HKTAS_BOOT_GATE_TOKEN");
                    if (token == null || token.Length != 32
                        || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_SAVE_GUARD") != "1")
                        throw new InvalidDataException("Full-run gate or save guard launch intent is incomplete.");
                    using var stateMap = MemoryMappedFile.OpenExisting(
                        "Local\\HKTAS.Boot." + token + ".V2State");
                    using var stateView = stateMap.CreateViewAccessor();
                    if (stateView.ReadInt32(0) != 0x32544648 || stateView.ReadInt32(4) != 2)
                        throw new InvalidDataException("Full-run frame mapping capability is invalid.");
                    result["fullRunCapability"] = HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId;
                    result["fullRunRandomPolicy"] = "scene-input-boundary-seed-render-isolation-v1";
                    result["fullRunGateAbi"] = 2;
                    result["saveGuardStatus"] = stateView.ReadInt32(92) == 1
                        ? "armed"
                        : stateView.ReadInt32(88) != 0 ? "fault" : "installing";
                    result["fullRunFaultCode"] = stateView.ReadInt32(88);
                }
                Console.Out.WriteLine(JsonSerializer.Serialize(result));
                launchSucceeded = true;
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal(environment);
                if (createdProcess)
                {
                    foreach (var remotePath in remotePaths)
                    {
                        if (launchSucceeded)
                        {
                            VirtualFreeEx(
                                created.Process,
                                remotePath,
                                0,
                                MemRelease);
                        }
                    }
                    if (!launchSucceeded)
                    {
                        TerminateProcess(created.Process, 0x544153u);
                        WaitForSingleObject(created.Process, 5000);
                    }
                    if (created.Thread != IntPtr.Zero)
                    {
                        CloseHandle(created.Thread);
                    }
                    if (created.Process != IntPtr.Zero)
                    {
                        CloseHandle(created.Process);
                    }
                }
            }
        }

        private static string[] DecodeLaunchArguments(string encoded)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(encoded);
            }
            catch (FormatException exception)
            {
                throw new ArgumentException(
                    "Launch arguments are not valid Base64.",
                    exception);
            }
            if (bytes.Length > 32768)
            {
                throw new ArgumentException("Launch argument payload is too large.");
            }
            var values = JsonSerializer.Deserialize<string[]>(
                new UTF8Encoding(false, true).GetString(bytes))
                ?? throw new ArgumentException("Launch argument array is null.");
            if (values.Length > 128
                || values.Any(
                    value => value == null
                             || value.Length > 4096
                             || value.IndexOf('\0') >= 0))
            {
                throw new ArgumentException("Launch argument array is invalid.");
            }
            return values;
        }

        private static string BuildStartupEnvironmentBlock(
            string runId)
        {
            var values = new SortedDictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = Convert.ToString(
                    entry.Key,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                var value = Convert.ToString(
                    entry.Value,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                if (key.Length == 0
                    || key.IndexOf('\0') >= 0
                    || key.IndexOf('=') >= 0
                    || value.IndexOf('\0') >= 0)
                {
                    continue;
                }
                values[key] = value;
            }
            values[StartupLatchEnvironment] = "1";
            values["HKTAS_CLOCK_RUN_ID"] = runId;
            values["SteamAppId"] = "367520";
            values["SteamGameId"] = "367520";

            var builder = new StringBuilder();
            foreach (var pair in values)
            {
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(pair.Value);
                builder.Append('\0');
            }
            builder.Append('\0');
            return builder.ToString();
        }

        private static string QuoteWindowsArgument(string value)
        {
            if (value.Length != 0
                && value.All(
                    character => character != ' '
                                 && character != '\t'
                                 && character != '\n'
                                 && character != '\v'
                                 && character != '"'))
            {
                return value;
            }

            var builder = new StringBuilder();
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }
                builder.Append('\\', backslashes);
                backslashes = 0;
                builder.Append(character);
            }
            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }

        private static Dictionary<string, string> ParseArguments(
            IReadOnlyList<string> args)
        {
            var result = new Dictionary<string, string>(
                StringComparer.Ordinal);
            foreach (var argument in args)
            {
                if (!argument.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "Only named arguments are accepted.");
                }

                var separator = argument.IndexOf('=');
                if (separator <= 2 || separator == argument.Length - 1)
                {
                    throw new ArgumentException(
                        "Arguments must use --name=value syntax.");
                }

                var name = argument.Substring(2, separator - 2);
                var value = argument.Substring(separator + 1);
                if (!result.TryAdd(name, value))
                {
                    throw new ArgumentException(
                        "Duplicate argument: " + name);
                }
            }

            var attachAllowed = new HashSet<string>(
                new[] { "pid", "start-time-utc-ticks" },
                StringComparer.Ordinal);
            var launchAllowed = new HashSet<string>(
                new[]
                {
                    "launch-game",
                    "launch-arguments-base64"
                },
                StringComparer.Ordinal);
            var attach = result.Count == attachAllowed.Count
                         && result.Keys.All(attachAllowed.Contains);
            var launch = result.Count == launchAllowed.Count
                         && result.Keys.All(launchAllowed.Contains);
            if (!attach && !launch)
            {
                throw new ArgumentException(
                    "Use exactly the attach or startup-launch argument set.");
            }

            return result;
        }

        private static int ParsePositiveInt(
            IReadOnlyDictionary<string, string> values,
            string name)
        {
            return values.TryGetValue(name, out var text)
                   && int.TryParse(
                       text,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var value)
                   && value > 0
                ? value
                : throw new ArgumentException(
                    "A positive integer is required for " + name + ".");
        }

        private static bool IsIdentifier(string value)
        {
            if (value.Length < 1 || value.Length > 96)
            {
                return false;
            }
            return value.All(
                character => (character >= 'a' && character <= 'z')
                             || (character >= 'A' && character <= 'Z')
                             || (character >= '0' && character <= '9')
                             || character == '-'
                             || character == '_');
        }

        private static long ParsePositiveLong(
            IReadOnlyDictionary<string, string> values,
            string name)
        {
            return values.TryGetValue(name, out var text)
                   && long.TryParse(
                       text,
                       NumberStyles.None,
                       CultureInfo.InvariantCulture,
                       out var value)
                   && value > 0
                ? value
                : throw new ArgumentException(
                    "A positive Int64 is required for " + name + ".");
        }

        private static string FixedSibling(
            string baseDirectory,
            string fileName)
        {
            var candidate = Path.GetFullPath(
                Path.Combine(baseDirectory, fileName));
            var prefix = baseDirectory.TrimEnd(
                             Path.DirectorySeparatorChar,
                             Path.AltDirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    Path.GetFileName(candidate),
                    fileName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Clock component path escaped its fixed bundle root.");
            }

            return candidate;
        }

        private static void RequireFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "Required fixed clock component is missing.",
                    path);
            }
        }

        private static void RequireHash(
            string path,
            string expected,
            string label)
        {
            RequireFile(path);
            var actual = Sha256(path);
            if (!string.Equals(
                    actual,
                    expected,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    label + " SHA-256 does not match the fixed whitelist.");
            }
        }

        private static string Sha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            using var sha256 = SHA256.Create();
            return Convert.ToHexString(
                    sha256.ComputeHash(stream))
                .ToLowerInvariant();
        }

        private static Exception Win32(string operation)
        {
            return new InvalidOperationException(
                operation
                + " failed with Win32 error "
                + Marshal.GetLastWin32Error().ToString(
                    CultureInfo.InvariantCulture)
                + ".");
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(
            IntPtr process,
            IntPtr address,
            nuint size,
            uint allocationType,
            uint protection);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualFreeEx(
            IntPtr process,
            IntPtr address,
            nuint size,
            uint freeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteProcessMemory(
            IntPtr process,
            IntPtr baseAddress,
            byte[] buffer,
            nuint size,
            out nuint bytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(
            IntPtr process,
            IntPtr threadAttributes,
            nuint stackSize,
            IntPtr startAddress,
            IntPtr parameter,
            uint creationFlags,
            out uint threadId);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint QueueUserAPC(
            IntPtr function,
            IntPtr thread,
            nuint data);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            IntPtr process,
            uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(
            IntPtr handle,
            uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeThread(
            IntPtr thread,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(
            IntPtr module,
            string procedureName);

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StdInput;
            public IntPtr StdOutput;
            public IntPtr StdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        private sealed class ClockWhitelist
        {
            public int SchemaVersion { get; set; }
            public string ProcessImageSha256 { get; set; } = string.Empty;
            public string UnityPlayerSha256 { get; set; } = string.Empty;
            public string AssemblyCSharpSha256 { get; set; } = string.Empty;
            public string BridgeSha256 { get; set; } = string.Empty;
            public string PayloadSha256 { get; set; } = string.Empty;

            public void Validate()
            {
                if (SchemaVersion != 1)
                {
                    throw new InvalidDataException(
                        "Unsupported clock whitelist schema.");
                }

                foreach (var value in new[]
                         {
                             ProcessImageSha256,
                             UnityPlayerSha256,
                             AssemblyCSharpSha256,
                             BridgeSha256,
                             PayloadSha256
                         })
                {
                    if (value.Length != 64
                        || value.Any(
                            character => character is not
                                (>= '0' and <= '9')
                                and not (>= 'a' and <= 'f')))
                    {
                        throw new InvalidDataException(
                            "Clock whitelist contains an invalid SHA-256.");
                    }
                }
            }
        }
    }
}
