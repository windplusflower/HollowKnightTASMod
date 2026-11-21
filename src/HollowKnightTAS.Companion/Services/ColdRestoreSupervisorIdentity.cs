using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using HollowKnightTAS.Core.Cryptography;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class ColdRestoreSupervisorIdentity
    {
        private const string Header = "HKTAS-COLD-IDENTITY-V1";
        private const int MaximumBytes = 4096;
        private readonly byte[] claimSecret;

        private ColdRestoreSupervisorIdentity(
            string companionInstanceId,
            byte[] claimSecret)
        {
            CompanionInstanceId = companionInstanceId;
            this.claimSecret = (byte[])claimSecret.Clone();
        }

        public string CompanionInstanceId { get; }
        public byte[] ClaimSecret => (byte[])claimSecret.Clone();

        public static ColdRestoreSupervisorIdentity LoadOrCreate(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException(
                    "A cold-restore identity root is required.",
                    nameof(root));
            }

            var fullRoot = Path.GetFullPath(root);
            Directory.CreateDirectory(fullRoot);
            RestrictDirectoryToCurrentUser(fullRoot);
            var path = Path.Combine(fullRoot, "identity-v1.txt");
            if (File.Exists(path))
            {
                try
                {
                    var bytes = ReadBounded(path);
                    var result = Deserialize(bytes);
                    var canonical = Serialize(result);
                    if (FixedEquals(bytes, canonical))
                    {
                        return result;
                    }

                    Trace.TraceWarning(
                        "ColdRestoreSupervisorIdentity: identity-v1.txt is non-canonical; regenerating.");
                    Console.Error.WriteLine(
                        "[Warning] ColdRestoreSupervisorIdentity: identity-v1.txt is non-canonical; regenerating.");
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "ColdRestoreSupervisorIdentity: identity-v1.txt is invalid or corrupted ({0}); regenerating.",
                        exception.Message);
                    Console.Error.WriteLine(
                        "[Warning] ColdRestoreSupervisorIdentity: identity-v1.txt is invalid or corrupted ("
                        + exception.Message
                        + "); regenerating.");
                }
            }

            return CreateAndPersist(fullRoot, path);
        }

        private static ColdRestoreSupervisorIdentity CreateAndPersist(
            string fullRoot,
            string path)
        {
            var secret = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(secret);
            }

            var identity = new ColdRestoreSupervisorIdentity(
                "companion-" + Guid.NewGuid().ToString("N"),
                secret);
            var temporary = Path.Combine(
                fullRoot,
                ".identity-" + Guid.NewGuid().ToString("N"));
            try
            {
                WriteDurable(temporary, Serialize(identity));
                File.Move(temporary, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return identity;
        }

        private static byte[] Serialize(
            ColdRestoreSupervisorIdentity identity)
        {
            var body = Header
                       + "\n"
                       + identity.CompanionInstanceId
                       + "\n"
                       + Convert.ToBase64String(identity.claimSecret)
                       + "\n";
            var bodyBytes = new UTF8Encoding(false, true).GetBytes(body);
            var checksum = Sha256Utility.ComputeHex(bodyBytes);
            return new UTF8Encoding(false, true).GetBytes(
                body + checksum + "\n");
        }

        private static ColdRestoreSupervisorIdentity Deserialize(byte[] bytes)
        {
            var lines = new UTF8Encoding(false, true)
                .GetString(bytes)
                .Split(new[] { '\n' }, StringSplitOptions.None);
            if (lines.Length != 5
                || lines[0] != Header
                || lines[4].Length != 0)
            {
                throw new InvalidDataException(
                    "Cold-restore supervisor identity shape is invalid.");
            }

            var body = lines[0]
                       + "\n"
                       + lines[1]
                       + "\n"
                       + lines[2]
                       + "\n";
            var expected = Sha256Utility.ComputeUtf8Hex(body);
            if (!string.Equals(expected, lines[3], StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Cold-restore supervisor identity checksum failed.");
            }

            var companionInstanceId =
                HollowKnightTAS.Core.ReplaySave.ColdRestoreIntent
                    .RequireIdentifier(lines[1], "companionInstanceId");
            byte[] secret;
            try
            {
                secret = Convert.FromBase64String(lines[2]);
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException(
                    "Cold-restore claim secret is invalid.",
                    exception);
            }

            if (secret.Length < 32)
            {
                throw new InvalidDataException(
                    "Cold-restore claim secret is too short.");
            }

            return new ColdRestoreSupervisorIdentity(
                companionInstanceId,
                secret);
        }

        private static byte[] ReadBounded(string path)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumBytes)
                {
                    throw new InvalidDataException(
                        "Cold-restore identity size is invalid.");
                }

                var bytes = new byte[checked((int)stream.Length)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = stream.Read(
                        bytes,
                        offset,
                        bytes.Length - offset);
                    if (read == 0)
                    {
                        throw new EndOfStreamException();
                    }

                    offset += read;
                }

                return bytes;
            }
        }

        private static void WriteDurable(string path, byte[] bytes)
        {
            using (var stream = new FileStream(
                       path,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }

            return difference == 0;
        }

        private static void RestrictDirectoryToCurrentUser(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Cold-restore supervisor identity requires Windows ACLs.");
            }

            using (var identity = WindowsIdentity.GetCurrent())
            {
                var user = identity.User
                           ?? throw new InvalidOperationException(
                               "Current Windows SID is unavailable.");
                var security = new DirectorySecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(
                    new FileSystemAccessRule(
                        user,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit
                        | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                new DirectoryInfo(path).SetAccessControl(security);
            }
        }
    }
}
