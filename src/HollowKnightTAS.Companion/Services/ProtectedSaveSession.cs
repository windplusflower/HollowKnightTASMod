using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using HollowKnightTAS.Core.ReplaySave;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class ProtectedSaveSession
    {
        public const long MaximumFileBytes = 32L * 1024 * 1024;
        public const long MaximumTotalBytes = 128L * 1024 * 1024;
        public const int MaximumFiles = 128;

        private readonly IReadOnlyDictionary<string, string> originalSha256;
        private readonly IReadOnlyDictionary<string, long> originalLengths;

        private ProtectedSaveSession(ProtectedSaveDescriptor descriptor,
            IDictionary<string, string> hashes, IDictionary<string, long> lengths, InitialSaveSnapshot initialSaves)
        {
            Descriptor = descriptor;
            InitialSaves = initialSaves;
            originalSha256 = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(hashes, StringComparer.OrdinalIgnoreCase));
            originalLengths = new ReadOnlyDictionary<string, long>(
                new Dictionary<string, long>(lengths, StringComparer.OrdinalIgnoreCase));
        }

        public ProtectedSaveDescriptor Descriptor { get; }
        public InitialSaveSnapshot InitialSaves { get; }
        public string DescriptorPath => Path.Combine(Descriptor.ShadowRoot, "descriptor.json");
        public IReadOnlyDictionary<string, string> OriginalSha256 => originalSha256;
        public IReadOnlyDictionary<string, long> OriginalLengths => originalLengths;

        public static ProtectedSaveSession Prepare(string runId, string originalRoot, string shadowRoot,
            InitialSaveSnapshot? initialSaves = null)
        {
            if (!ProtectedSaveDescriptor.IsSafeRunId(runId))
                throw new ArgumentException("Invalid protected run ID.", nameof(runId));
            if (string.IsNullOrWhiteSpace(originalRoot)) throw new ArgumentException("Original save root is required.", nameof(originalRoot));
            if (string.IsNullOrWhiteSpace(shadowRoot)) throw new ArgumentException("Shadow save root is required.", nameof(shadowRoot));
            var original = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalRoot));
            var shadow = Path.TrimEndingDirectorySeparator(Path.GetFullPath(shadowRoot));
            if (IsDriveRoot(original) || IsDriveRoot(shadow))
                throw new InvalidDataException("A drive root cannot be used as a protected save directory.");
            if (IsSameOrBelow(original, shadow) || IsSameOrBelow(shadow, original))
                throw new InvalidDataException("Original and shadow save directories must not overlap.");
            RejectReparseAncestors(original);
            RejectReparseAncestors(shadow);
            if (Directory.Exists(shadow) || File.Exists(shadow))
                throw new IOException("Shadow save directory must be new for this run.");

            var files = new List<(string Name, string Path, long Length)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalBytes = 0;
            if (Directory.Exists(original))
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(original, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(path);
                    if (!LooksLikeSlotName(name)) continue;
                    if (!ProtectedSaveDescriptor.IsSlotFileName(name))
                    {
                        Trace.TraceWarning("ProtectedSaveSession: skipping non-standard slot-side entry: " + name);
                        continue;
                    }
                    if (!seen.Add(name))
                        throw new InvalidDataException("Case-insensitive duplicate save file name: " + name);
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || Directory.Exists(path))
                        throw new InvalidDataException("Save file is a directory or reparse point: " + name);
                    var length = new FileInfo(path).Length;
                    if (length < 0 || length > MaximumFileBytes)
                        throw new InvalidDataException("Save file exceeds the per-file limit: " + name);
                    if (length > MaximumTotalBytes - totalBytes)
                        throw new InvalidDataException("Save files exceed the total size limit.");
                    totalBytes += length;
                    files.Add((name, path, length));
                    if (files.Count > MaximumFiles)
                        throw new InvalidDataException("Too many slot-side save files.");
                }
            }
            files.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

            Directory.CreateDirectory(shadow);
            RestrictDirectoryToCurrentUser(shadow);
            RejectReparseAncestors(shadow);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var originalBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in files)
            {
                if ((File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Original save file became a reparse point.");
                using var input = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length != item.Length) throw new IOException("Original save changed during preparation.");
                var bytes = new byte[checked((int)item.Length)];
                input.ReadExactly(bytes);
                if (input.ReadByte() != -1) throw new IOException("Original save grew during preparation.");
                var hash = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(bytes);
                hashes.Add(item.Name, hash);
                lengths.Add(item.Name, item.Length);
                if (initialSaves == null) originalBytes.Add(item.Name, bytes);
            }
            var baseline = initialSaves ?? new InitialSaveSnapshot(originalBytes);
            baseline.WriteToNewShadow(shadow);
            var token = new byte[32];
            RandomNumberGenerator.Fill(token);
            // Descriptor hashes identify the replay seed; the separate audit above
            // identifies this machine's real saves, which may be completely different.
            var descriptor = new ProtectedSaveDescriptor(runId, original, shadow, baseline.Hashes,
                Convert.ToHexString(token).ToLowerInvariant());
            var session = new ProtectedSaveSession(descriptor, hashes, lengths, baseline);
            var temporaryDescriptor = session.DescriptorPath + ".new";
            using (var stream = new FileStream(temporaryDescriptor, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = ProtectedSaveDescriptorCodec.Serialize(descriptor);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            File.Move(temporaryDescriptor, session.DescriptorPath);
            return session;
        }

        private static bool LooksLikeSlotName(string name)
            => name.Length >= 6 && name.StartsWith("user", StringComparison.OrdinalIgnoreCase)
               && name[4] >= '1' && name[4] <= '4' && name[5] == '.';

        private static bool IsDriveRoot(string path)
            => string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);

        private static bool IsSameOrBelow(string path, string root)
            => path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        private static void RejectReparseAncestors(string path)
        {
            string? current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(current) || Directory.Exists(current))
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Protected save path contains a reparse point: " + current);
                }
                var parent = Path.GetDirectoryName(current);
                if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Protected save sessions require Windows ACLs.");
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("Current Windows SID is unavailable.");
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
        }
    }
}
