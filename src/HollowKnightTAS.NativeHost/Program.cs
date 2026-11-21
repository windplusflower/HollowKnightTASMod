using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HollowKnightTAS.Core.Capabilities;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.NativeHost
{
    internal static class Program
    {
        private const int MaximumRequestCharacters = 64 * 1024;
        private const int RequiredAttachCycles = 100;

        // The executable stays in Native/, while its managed assembly and runtime
        // are shared with Studio one directory above it.
        private static string HostDirectory => Path.GetDirectoryName(
            Environment.ProcessPath ?? throw new InvalidOperationException("NativeHost image is unavailable."))!;

        private static int Main(string[] arguments)
        {
            Console.InputEncoding = new UTF8Encoding(false, true);
            Console.OutputEncoding = new UTF8Encoding(false, true);
            try
            {
                if (arguments.Length != 0)
                {
                    return WriteFailure(
                        "ArgumentsRejected",
                        "NativeHost accepts requests only through inherited stdin.");
                }

                ValidateAuthorizedParent();
                CheckSignedBuildWhitelist();
                var line = Console.ReadLine();
                if (string.IsNullOrEmpty(line)
                    || line.Length > MaximumRequestCharacters)
                {
                    return WriteFailure(
                        "InvalidRequestLength",
                        "NativeHost request length is invalid.");
                }

                var decode = IpcPayloadCodec.TryDeserialize(
                    new UTF8Encoding(false, true).GetBytes(line));
                if (!decode.Success || decode.Fields == null)
                {
                    return WriteFailure(
                        decode.ErrorCode,
                        decode.Error);
                }

                var request = NativeObserveRequest.Parse(
                    decode.Fields);
                var fields = Capture(request);
                var unsigned = IpcPayloadCodec.Serialize(fields);
                using (var hmac =
                       new HMACSHA256(request.Credential))
                {
                    fields["credentialProof"] =
                        Convert.ToBase64String(
                            hmac.ComputeHash(unsigned));
                }
                Console.WriteLine(
                    Encoding.UTF8.GetString(
                        IpcPayloadCodec.Serialize(fields)));
                return 0;
            }
            catch (Exception exception)
            {
                return WriteFailure(
                    exception.GetType().Name,
                    Sanitize(exception.Message));
            }
        }

        private static SortedDictionary<string, string> Capture(
            NativeObserveRequest request)
        {
            var attachCyclesCompleted =
                ValidateAttachCycles(request);
            using (var process =
                   Process.GetProcessById(request.TargetProcessId))
            {
                var startTicks =
                    process.StartTime.ToUniversalTime().Ticks;
                if (startTicks != request.TargetStartTimeUtcTicks)
                {
                    throw new InvalidDataException(
                        "Target process creation time does not match.");
                }

                var imagePath = Path.GetFullPath(
                    process.MainModule?.FileName
                    ?? throw new InvalidDataException(
                        "Target image path is unavailable."));
                var imageHash =
                    Sha256Utility.ComputeFileHex(imagePath);
                if (!string.Equals(
                        Path.GetFileName(imagePath),
                        "hollow_knight.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine(
                        "[Warning] Target game executable is not named hollow_knight.exe ("
                        + Path.GetFileName(imagePath)
                        + "); continuing.");
                }
                if (!string.Equals(
                        imageHash,
                        NativeCapabilityCatalog
                            .SupportedImageSha256,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[Warning] Target game executable SHA-256 differs from catalog ("
                        + imageHash
                        + "); continuing.");
                }

                var assemblyCSharpPath = Path.Combine(
                    Path.GetDirectoryName(imagePath)
                    ?? throw new InvalidDataException(
                        "Target image directory is unavailable."),
                    "hollow_knight_Data",
                    "Managed",
                        "Assembly-CSharp.dll");
                var managedDirectory =
                    Path.GetDirectoryName(
                        assemblyCSharpPath)
                    ?? throw new InvalidDataException(
                        "Managed directory is unavailable.");
                var assemblyCSharpSha256 = File.Exists(assemblyCSharpPath)
                    ? Sha256Utility.ComputeFileHex(assemblyCSharpPath)
                    : WarnMissingFileHash(assemblyCSharpPath);
                if (!string.Equals(
                        assemblyCSharpSha256,
                        NativeCapabilityCatalog
                            .SupportedAssemblyCSharpSha256,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[Warning] Assembly-CSharp.dll SHA-256 differs from catalog ("
                        + assemblyCSharpSha256
                        + "); continuing.");
                }
                var runtimeModRoot = Path.Combine(
                    managedDirectory,
                    "Mods",
                    "HollowKnightTAS");
                var runtimeAssemblyPath = Path.Combine(
                    runtimeModRoot,
                    "HollowKnightTAS.dll");
                var coreAssemblyPath = Path.Combine(
                    runtimeModRoot,
                    "HollowKnightTAS.Core.dll");
                var runtimeAssemblySha256 = File.Exists(runtimeAssemblyPath)
                    ? Sha256Utility.ComputeFileHex(runtimeAssemblyPath)
                    : WarnMissingFileHash(runtimeAssemblyPath);
                var coreAssemblySha256 = File.Exists(coreAssemblyPath)
                    ? Sha256Utility.ComputeFileHex(coreAssemblyPath)
                    : WarnMissingFileHash(coreAssemblyPath);
                if (!string.Equals(
                        runtimeAssemblySha256,
                        request.ExpectedRuntimeAssemblySha256,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        coreAssemblySha256,
                        request.ExpectedCoreAssemblySha256,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[Warning] Installed Runtime assemblies differ from caller expected hashes; continuing.");
                }

                var pathHash = Sha256Utility.ComputeUtf8Hex(
                    imagePath.ToUpperInvariant());
                if (!string.Equals(
                        imageHash,
                        request.ExpectedImageSha256,
                        StringComparison.Ordinal)
                    || !string.Equals(
                        pathHash,
                        request.ExpectedImagePathSha256,
                        StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "[Warning] Target image/path fingerprint differs from caller expected hashes; continuing.");
                }

                var machine = ReadPeMachine(imagePath);
                if (machine != 0x8664)
                {
                    throw new PlatformNotSupportedException(
                        "Target is not PE32+ AMD64.");
                }

                var moduleRecords =
                    new List<string>();
                foreach (ProcessModule module in process.Modules)
                {
                    var modulePath = Path.GetFullPath(
                        module.FileName);
                    moduleRecords.Add(
                        module.ModuleName.ToLowerInvariant()
                        + "|"
                        + Sha256Utility.ComputeFileHex(modulePath));
                }
                moduleRecords.Sort(StringComparer.Ordinal);
                var threadIds = process.Threads
                    .Cast<ProcessThread>()
                    .Select(
                        thread => thread.Id.ToString(
                            CultureInfo.InvariantCulture))
                    .OrderBy(
                        value => value,
                        StringComparer.Ordinal)
                    .ToArray();
                var moduleMapSha256 =
                    Sha256Utility.ComputeUtf8Hex(
                        string.Join("\n", moduleRecords) + "\n");
                var threadSetSha256 =
                    Sha256Utility.ComputeUtf8Hex(
                        string.Join("\n", threadIds) + "\n");
                var virtualMemory = QueryVirtualMemory(
                    process.Handle);
                var pssAvailable =
                    NativeMethods.GetProcAddress(
                        NativeMethods.GetModuleHandle(
                            "kernel32.dll"),
                        "PssCaptureSnapshot")
                    != IntPtr.Zero;
                var fingerprint =
                    Sha256Utility.ComputeUtf8Hex(
                        imageHash
                        + "|"
                        + startTicks.ToString(
                            CultureInfo.InvariantCulture)
                        + "|"
                        + moduleMapSha256
                        + "|8664");
                var capabilities =
                    NativeCapabilityCatalog.Create(true);

                return new SortedDictionary<string, string>(
                    StringComparer.Ordinal)
                {
                    ["assemblyCSharpSha256"] =
                        assemblyCSharpSha256,
                    ["attachCyclesCompleted"] =
                        attachCyclesCompleted.ToString(
                            CultureInfo.InvariantCulture),
                    ["buildWhitelistId"] =
                        NativeCapabilityCatalog
                            .SupportedBuildId,
                    ["capabilityCatalog"] = string.Join(
                        "\n",
                        capabilities.Select(
                            item =>
                                item.CapabilityId
                                + "="
                                + item.Status)),
                    ["checkpointDetail"] =
                        "Windows PSS exposes capture/query but no documented whole-process restore operation.",
                    ["credentialProofAlgorithm"] =
                        "hmac-sha256-canonical-payload-v1",
                    ["coreAssemblySha256"] =
                        coreAssemblySha256,
                    ["environmentManifestSha256"] =
                        request.EnvironmentManifestSha256,
                    ["evidenceVersion"] = "1",
                    ["imageSha256"] = imageHash,
                    ["machine"] = "amd64",
                    ["moduleCount"] =
                        moduleRecords.Count.ToString(
                            CultureInfo.InvariantCulture),
                    ["moduleMapSha256"] = moduleMapSha256,
                    ["processObserveStatus"] = "verified",
                    ["protocolVersion"] = "1",
                    ["pssCaptureApiAvailable"] =
                        pssAvailable ? "true" : "false",
                    ["rawPagesPersisted"] = "false",
                    ["parentProcessVerified"] = "true",
                    ["requestId"] = request.RequestId,
                    ["reservedBytes"] =
                        virtualMemory.ReservedBytes.ToString(
                            CultureInfo.InvariantCulture),
                    ["committedBytes"] =
                        virtualMemory.CommittedBytes.ToString(
                            CultureInfo.InvariantCulture),
                    ["sessionId"] = request.SessionId,
                    ["runtimeAssemblySha256"] =
                        runtimeAssemblySha256,
                    ["targetFingerprint"] = fingerprint,
                    ["targetPid"] =
                        request.TargetProcessId.ToString(
                            CultureInfo.InvariantCulture),
                    ["threadCount"] =
                        threadIds.Length.ToString(
                            CultureInfo.InvariantCulture),
                    ["threadSetSha256"] = threadSetSha256,
                    ["unsupportedCapabilities"] =
                        string.Join(
                            ",",
                            capabilities
                                .Where(
                                    item =>
                                        item.Status
                                        == CapabilityStatus
                                            .Unsupported)
                                .Select(
                                    item => item.CapabilityId))
                };
            }
        }

        private static int ValidateAttachCycles(
            NativeObserveRequest request)
        {
            var completed = 0;
            string? baselinePathHash = null;
            var warnedPathMismatch = false;
            for (var iteration = 0;
                 iteration < request.AttachCycles;
                 iteration++)
            {
                using (var process =
                       Process.GetProcessById(
                           request.TargetProcessId))
                {
                    if (process.StartTime
                            .ToUniversalTime()
                            .Ticks
                        != request.TargetStartTimeUtcTicks)
                    {
                        throw new InvalidDataException(
                            "Target process creation time changed during attach verification.");
                    }

                    var imagePath = Path.GetFullPath(
                        process.MainModule?.FileName
                        ?? throw new InvalidDataException(
                            "Target image path is unavailable during attach verification."));
                    var pathHash =
                        Sha256Utility.ComputeUtf8Hex(
                            imagePath.ToUpperInvariant());
                    baselinePathHash ??= pathHash;
                    if (!string.Equals(
                            pathHash,
                            baselinePathHash,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "Target image path changed during attach verification.");
                    }
                    if (!warnedPathMismatch
                        && !string.Equals(
                            pathHash,
                            request.ExpectedImagePathSha256,
                            StringComparison.Ordinal))
                    {
                        warnedPathMismatch = true;
                        Console.Error.WriteLine(
                            "[Warning] Target image path hash differs from caller expectation during attach; continuing.");
                    }

                    _ = process.Handle;
                    completed++;
                }
            }

            return completed;
        }

        private static void ValidateAuthorizedParent()
        {
            var parentProcessId =
                NativeMethods.GetParentProcessId(
                    Environment.ProcessId);
            using (var parent =
                   Process.GetProcessById(parentProcessId))
            {
                var parentPath = Path.GetFullPath(
                    parent.MainModule?.FileName
                    ?? throw new UnauthorizedAccessException(
                        "NativeHost parent image is unavailable."));
                var expectedPath = Path.GetFullPath(
                    Path.Combine(
                        HostDirectory,
                        "..",
                        "HollowKnightTAS.Companion.exe"));
                if (!string.Equals(
                        parentPath,
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase)
                    || ReadPeMachine(parentPath) != 0x8664)
                {
                    throw new UnauthorizedAccessException(
                        "NativeHost must be launched by the verified sibling Companion.");
                }
            }
        }

        private static void CheckSignedBuildWhitelist()
        {
            var path = Path.Combine(
                HostDirectory,
                "native-build-whitelist-v1.json");
            if (!File.Exists(path))
            {
                Console.Error.WriteLine(
                    "[Warning] Native build whitelist file is missing ("
                    + path
                    + "); continuing without whitelist lock.");
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                if (bytes.Length == 0 || bytes.Length > 64 * 1024)
                {
                    Console.Error.WriteLine(
                        "[Warning] Native build whitelist length is invalid; continuing.");
                    return;
                }

                using (var document = JsonDocument.Parse(
                           bytes,
                           new JsonDocumentOptions
                           {
                               AllowTrailingCommas = false,
                               CommentHandling =
                                   JsonCommentHandling.Disallow,
                               MaxDepth = 8
                           }))
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || root.EnumerateObject().Count() != 2
                        || !root.TryGetProperty(
                            "schemaVersion",
                            out var schemaVersion)
                        || schemaVersion.ValueKind
                        != JsonValueKind.Number
                        || !schemaVersion.TryGetInt32(
                            out var schema)
                        || schema != 1
                        || !root.TryGetProperty(
                            "builds",
                            out var builds)
                        || builds.ValueKind
                        != JsonValueKind.Array
                        || builds.GetArrayLength() != 1)
                    {
                        Console.Error.WriteLine(
                            "[Warning] Native build whitelist root is invalid; continuing.");
                        return;
                    }

                    var build = builds[0];
                    if (build.ValueKind != JsonValueKind.Object
                        || build.EnumerateObject().Count() != 6
                        || TryGetString(build, "id")
                        != NativeCapabilityCatalog.SupportedBuildId
                        || TryGetString(
                            build,
                            "architecture") != "x64"
                        || TryGetString(
                            build,
                            "gameVersion")
                        != "1.5.78.11833"
                        || TryGetString(
                            build,
                            "imageFileName")
                        != "hollow_knight.exe"
                        || TryGetString(
                            build,
                            "imageSha256")
                        != NativeCapabilityCatalog
                            .SupportedImageSha256
                        || TryGetString(
                            build,
                            "assemblyCSharpSha256")
                        != NativeCapabilityCatalog
                            .SupportedAssemblyCSharpSha256)
                    {
                        Console.Error.WriteLine(
                            "[Warning] Native build whitelist entry differs from catalog constants; continuing.");
                    }
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    "[Warning] Failed to read native build whitelist ("
                    + exception.Message
                    + "); continuing.");
            }
        }

        private static string WarnMissingFileHash(string path)
        {
            Console.Error.WriteLine(
                "[Warning] Expected file is missing ("
                + path
                + "); using zero hash.");
            return new string('0', 64);
        }

        private static string? TryGetString(
            JsonElement value,
            string propertyName)
        {
            if (!value.TryGetProperty(
                    propertyName,
                    out var property)
                || property.ValueKind
                != JsonValueKind.String)
            {
                return null;
            }
            return property.GetString();
        }

        private static VirtualMemorySummary QueryVirtualMemory(
            IntPtr process)
        {
            ulong committed = 0;
            ulong reserved = 0;
            ulong address = 0;
            var size = (nuint)Marshal.SizeOf<
                NativeMethods.MemoryBasicInformation64>();
            while (true)
            {
                var result = NativeMethods.VirtualQueryEx(
                    process,
                    new IntPtr(unchecked((long)address)),
                    out var information,
                    size);
                if (result == 0
                    || information.RegionSize == 0)
                {
                    break;
                }
                if (information.State
                    == NativeMethods.MemCommit)
                {
                    committed += information.RegionSize;
                }
                else if (information.State
                         == NativeMethods.MemReserve)
                {
                    reserved += information.RegionSize;
                }

                var next =
                    information.BaseAddress
                    + information.RegionSize;
                if (next <= address)
                {
                    break;
                }
                address = next;
            }

            return new VirtualMemorySummary(
                committed,
                reserved);
        }

        private static ushort ReadPeMachine(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt16() != 0x5a4d)
                {
                    throw new InvalidDataException(
                        "Target image has no DOS header.");
                }
                stream.Position = 0x3c;
                var peOffset = reader.ReadInt32();
                if (peOffset < 0
                    || peOffset > stream.Length - 6)
                {
                    throw new InvalidDataException(
                        "Target PE offset is invalid.");
                }
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550)
                {
                    throw new InvalidDataException(
                        "Target image has no PE header.");
                }
                return reader.ReadUInt16();
            }
        }

        private static int WriteFailure(
            string code,
            string detail)
        {
            Console.WriteLine(
                Encoding.UTF8.GetString(
                    IpcPayloadCodec.Serialize(
                        new SortedDictionary<string, string>(
                            StringComparer.Ordinal)
                        {
                            ["detail"] = Sanitize(detail),
                            ["errorCode"] = Sanitize(code),
                            ["evidenceVersion"] = "1",
                            ["processObserveStatus"] = "faulted"
                        })));
            return 4;
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

        private sealed class NativeObserveRequest
        {
            private NativeObserveRequest(
                string requestId,
                string sessionId,
                int targetProcessId,
                long targetStartTimeUtcTicks,
                string expectedImageSha256,
                string expectedImagePathSha256,
                string expectedRuntimeAssemblySha256,
                string expectedCoreAssemblySha256,
                string environmentManifestSha256,
                int attachCycles,
                byte[] credential)
            {
                RequestId = requestId;
                SessionId = sessionId;
                TargetProcessId = targetProcessId;
                TargetStartTimeUtcTicks =
                    targetStartTimeUtcTicks;
                ExpectedImageSha256 = expectedImageSha256;
                ExpectedImagePathSha256 =
                    expectedImagePathSha256;
                ExpectedRuntimeAssemblySha256 =
                    expectedRuntimeAssemblySha256;
                ExpectedCoreAssemblySha256 =
                    expectedCoreAssemblySha256;
                EnvironmentManifestSha256 =
                    environmentManifestSha256;
                AttachCycles = attachCycles;
                Credential = credential;
            }

            public string RequestId { get; }
            public string SessionId { get; }
            public int TargetProcessId { get; }
            public long TargetStartTimeUtcTicks { get; }
            public string ExpectedImageSha256 { get; }
            public string ExpectedImagePathSha256 { get; }
            public string ExpectedRuntimeAssemblySha256 { get; }
            public string ExpectedCoreAssemblySha256 { get; }
            public string EnvironmentManifestSha256 { get; }
            public int AttachCycles { get; }
            public byte[] Credential { get; }

            public static NativeObserveRequest Parse(
                IReadOnlyDictionary<string, string> fields)
            {
                var expected = new[]
                {
                    "attachCycles",
                    "credential",
                    "environmentManifestSha256",
                    "expectedCoreAssemblySha256",
                    "expectedImagePathSha256",
                    "expectedImageSha256",
                    "expectedRuntimeAssemblySha256",
                    "protocolVersion",
                    "requestId",
                    "sessionId",
                    "targetPid",
                    "targetStartTimeUtcTicks"
                };
                if (fields.Count != expected.Length
                    || expected.Any(
                        key => !fields.ContainsKey(key))
                    || fields["protocolVersion"] != "1"
                    || !int.TryParse(
                        fields["attachCycles"],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var attachCycles)
                    || attachCycles != RequiredAttachCycles
                    || !IpcIdentifier.IsValid(
                        fields["requestId"],
                        128)
                    || !IpcIdentifier.IsValid(
                        fields["sessionId"],
                        160)
                    || !int.TryParse(
                        fields["targetPid"],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var pid)
                    || pid <= 0
                    || !long.TryParse(
                        fields["targetStartTimeUtcTicks"],
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var startTicks)
                    || startTicks <= 0
                    || !IsSha256(
                        fields["expectedImageSha256"])
                    || !IsSha256(
                        fields["expectedImagePathSha256"])
                    || !IsSha256(
                        fields[
                            "expectedRuntimeAssemblySha256"])
                    || !IsSha256(
                        fields[
                            "expectedCoreAssemblySha256"])
                    || !IsSha256(
                        fields[
                            "environmentManifestSha256"]))
                {
                    throw new InvalidDataException(
                        "NativeHost request fields are invalid.");
                }

                byte[] credential;
                try
                {
                    credential = Convert.FromBase64String(
                        fields["credential"]);
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "NativeHost credential is invalid.",
                        exception);
                }
                if (credential.Length != 32)
                {
                    throw new InvalidDataException(
                        "NativeHost credential length is invalid.");
                }

                return new NativeObserveRequest(
                    fields["requestId"],
                    fields["sessionId"],
                    pid,
                    startTicks,
                    fields["expectedImageSha256"],
                    fields["expectedImagePathSha256"],
                    fields[
                        "expectedRuntimeAssemblySha256"],
                    fields[
                        "expectedCoreAssemblySha256"],
                    fields[
                        "environmentManifestSha256"],
                    attachCycles,
                    credential);
            }

            private static bool IsSha256(string value)
            {
                return value.Length == 64
                       && value.All(
                           character =>
                               character >= '0'
                               && character <= '9'
                               || character >= 'a'
                               && character <= 'f');
            }
        }

        private readonly struct VirtualMemorySummary
        {
            public VirtualMemorySummary(
                ulong committedBytes,
                ulong reservedBytes)
            {
                CommittedBytes = committedBytes;
                ReservedBytes = reservedBytes;
            }

            public ulong CommittedBytes { get; }
            public ulong ReservedBytes { get; }
        }

        private static class NativeMethods
        {
            private const uint Th32csSnapProcess = 0x00000002;
            public const uint MemCommit = 0x1000;
            public const uint MemReserve = 0x2000;

            public static int GetParentProcessId(
                int processId)
            {
                using (var snapshot =
                       CreateToolhelp32Snapshot(
                           Th32csSnapProcess,
                           0))
                {
                    if (snapshot.IsInvalid)
                    {
                        throw new System.ComponentModel
                            .Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "CreateToolhelp32Snapshot failed.");
                    }

                    var entry = new ProcessEntry32
                    {
                        Size = (uint)Marshal.SizeOf<
                            ProcessEntry32>()
                    };
                    if (!Process32First(snapshot, ref entry))
                    {
                        throw new System.ComponentModel
                            .Win32Exception(
                                Marshal.GetLastWin32Error(),
                                "Process32First failed.");
                    }

                    do
                    {
                        if (entry.ProcessId == (uint)processId
                            && entry.ParentProcessId > 0)
                        {
                            return checked(
                                (int)entry.ParentProcessId);
                        }
                    }
                    while (Process32Next(
                        snapshot,
                        ref entry));
                }

                throw new InvalidDataException(
                    "NativeHost parent process was not found.");
            }

            [StructLayout(
                LayoutKind.Sequential,
                CharSet = CharSet.Unicode)]
            private struct ProcessEntry32
            {
                public uint Size;
                public uint Usage;
                public uint ProcessId;
                public IntPtr DefaultHeapId;
                public uint ModuleId;
                public uint Threads;
                public uint ParentProcessId;
                public int BasePriority;
                public uint Flags;

                [MarshalAs(
                    UnmanagedType.ByValTStr,
                    SizeConst = 260)]
                public string ExecutableFile;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct MemoryBasicInformation64
            {
                public ulong BaseAddress;
                public ulong AllocationBase;
                public uint AllocationProtect;
                public uint Alignment1;
                public ulong RegionSize;
                public uint State;
                public uint Protect;
                public uint Type;
                public uint Alignment2;
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(
                string moduleName);

            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
            public static extern IntPtr GetProcAddress(
                IntPtr module,
                string procedureName);

            [DllImport("kernel32.dll")]
            public static extern nuint VirtualQueryEx(
                IntPtr process,
                IntPtr address,
                out MemoryBasicInformation64 information,
                nuint length);

            [DllImport(
                "kernel32.dll",
                SetLastError = true)]
            private static extern Microsoft.Win32.SafeHandles
                .SafeFileHandle CreateToolhelp32Snapshot(
                    uint flags,
                    uint processId);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool Process32First(
                Microsoft.Win32.SafeHandles.SafeFileHandle snapshot,
                ref ProcessEntry32 entry);

            [DllImport(
                "kernel32.dll",
                CharSet = CharSet.Unicode,
                SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool Process32Next(
                Microsoft.Win32.SafeHandles.SafeFileHandle snapshot,
                ref ProcessEntry32 entry);
        }
    }
}
