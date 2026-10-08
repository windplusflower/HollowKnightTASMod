using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Deployment;
using HollowKnightTAS.Core.Ipc;

namespace HollowKnightTAS.BundleTool
{
    internal static class Program
    {
        private const string Entrypoint =
            "Companion/win-x64/HollowKnightTAS.Companion.exe";

        private static int Main(string[] arguments)
        {
            try
            {
                if (arguments.Length == 0)
                {
                    Usage();
                    return 2;
                }

                switch (arguments[0])
                {
                    case "keygen":
                        if (arguments.Length != 3)
                        {
                            Usage();
                            return 2;
                        }

                        Keygen(arguments[1], arguments[2]);
                        return 0;
                    case "sign":
                        if (arguments.Length != 4)
                        {
                            Usage();
                            return 2;
                        }

                        Sign(
                            arguments[1],
                            arguments[2],
                            arguments[3]);
                        return 0;
                    case "verify":
                        if (arguments.Length != 4)
                        {
                            Usage();
                            return 2;
                        }

                        return Verify(
                            arguments[1],
                            arguments[2],
                            arguments[3]);
                    case "print-public":
                        if (arguments.Length != 2)
                        {
                            Usage();
                            return 2;
                        }

                        PrintPublic(arguments[1]);
                        return 0;
                    default:
                        Usage();
                        return 2;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    exception.GetType().Name
                    + ": "
                    + exception.Message);
                return 3;
            }
        }

        private static void Keygen(
            string privatePath,
            string publicPath)
        {
            RequireNewFile(privatePath);
            RequireNewFile(publicPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    Path.GetFullPath(privatePath))!);
            Directory.CreateDirectory(
                Path.GetDirectoryName(
                    Path.GetFullPath(publicPath))!);
            using (var rsa = RSA.Create(3072))
            {
                WriteKey(
                    privatePath,
                    KeyDocument.FromParameters(
                        rsa.ExportParameters(true)));
                WriteKey(
                    publicPath,
                    KeyDocument.FromParameters(
                        rsa.ExportParameters(false)));
            }

            Console.WriteLine("Generated RSA-3072 signing key.");
        }

        private static void Sign(
            string privatePath,
            string bundleRoot,
            string manifestPath)
        {
            var root = Path.GetFullPath(bundleRoot);
            var companionRoot = Path.Combine(
                root,
                "Companion",
                "win-x64");
            var entrypointPath = Path.Combine(
                root,
                Entrypoint.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(entrypointPath)
                || !Directory.Exists(companionRoot))
            {
                throw new FileNotFoundException(
                    "Published Companion entrypoint is missing.",
                    entrypointPath);
            }

            var files = Directory
                .EnumerateFiles(
                    companionRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(
                    path => new CompanionManifestFile(
                        Path.GetRelativePath(root, path)
                            .Replace('\\', '/'),
                        Sha256Utility.ComputeFileHex(path)))
                .OrderBy(
                    value => value.Path,
                    StringComparer.Ordinal)
                .ToArray();
            var unsigned = new CompanionBundleManifest(
                1,
                CompanionManifestCodec.Product,
                "0.1.8",
                "win-x64",
                1,
                1,
                Entrypoint,
                files,
                string.Empty);
            using (var rsa = RSA.Create())
            {
                rsa.ImportParameters(
                    ReadKey(privatePath).ToParameters(
                        requirePrivate: true));
                var signed = unsigned.WithSignature(
                    CompanionBundleVerifier.Sign(
                        unsigned,
                        rsa));
                WriteAtomic(
                    manifestPath,
                    CompanionManifestCodec.Serialize(signed));
            }

            Console.WriteLine(
                "Signed "
                + files.Length
                + " bundle files.");
        }

        private static int Verify(
            string publicPath,
            string bundleRoot,
            string manifestPath)
        {
            var parameters = ReadKey(publicPath)
                .ToParameters(requirePrivate: false);
            var result = CompanionBundleVerifier.Verify(
                bundleRoot,
                manifestPath,
                "win-x64",
                new ProtocolRange(1, 1),
                new RsaPublicKey(
                    parameters.Modulus!,
                    parameters.Exponent!));
            Console.WriteLine(
                result.Status + ": " + result.Detail);
            return result.Success ? 0 : 4;
        }

        private static void PrintPublic(string publicPath)
        {
            var parameters = ReadKey(publicPath)
                .ToParameters(requirePrivate: false);
            Console.WriteLine(
                "modulus="
                + Convert.ToBase64String(parameters.Modulus!));
            Console.WriteLine(
                "exponent="
                + Convert.ToBase64String(parameters.Exponent!));
        }

        private static KeyDocument ReadKey(string path)
        {
            var result = JsonSerializer.Deserialize<KeyDocument>(
                File.ReadAllText(
                    path,
                    new UTF8Encoding(false, true)),
                JsonOptions());
            return result
                   ?? throw new InvalidDataException(
                       "Signing key document is empty.");
        }

        private static void WriteKey(
            string path,
            KeyDocument document)
        {
            WriteAtomic(
                path,
                new UTF8Encoding(false, true).GetBytes(
                    JsonSerializer.Serialize(
                        document,
                        JsonOptions())));
        }

        private static JsonSerializerOptions JsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNamingPolicy =
                    JsonNamingPolicy.CamelCase,
                WriteIndented = true
            };
        }

        private static void RequireNewFile(string path)
        {
            if (File.Exists(path))
            {
                throw new IOException(
                    "Refusing to overwrite existing key: "
                    + path);
            }
        }

        private static void WriteAtomic(
            string path,
            byte[] bytes)
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(
                Path.GetDirectoryName(full)!);
            var temporary = full
                            + ".tmp-"
                            + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }

                File.Move(temporary, full, true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static void Usage()
        {
            Console.Error.WriteLine(
                "Usage:\n"
                + "  keygen <private.json> <public.json>\n"
                + "  sign <private.json> <bundle-root> <manifest.json>\n"
                + "  verify <public.json> <bundle-root> <manifest.json>\n"
                + "  print-public <public.json>");
        }

        public sealed class KeyDocument
        {
            public string Modulus { get; set; } = string.Empty;
            public string Exponent { get; set; } = string.Empty;
            public string D { get; set; } = string.Empty;
            public string P { get; set; } = string.Empty;
            public string Q { get; set; } = string.Empty;
            public string DP { get; set; } = string.Empty;
            public string DQ { get; set; } = string.Empty;
            public string InverseQ { get; set; } = string.Empty;

            public static KeyDocument FromParameters(
                RSAParameters value)
            {
                return new KeyDocument
                {
                    Modulus = Encode(value.Modulus),
                    Exponent = Encode(value.Exponent),
                    D = Encode(value.D),
                    P = Encode(value.P),
                    Q = Encode(value.Q),
                    DP = Encode(value.DP),
                    DQ = Encode(value.DQ),
                    InverseQ = Encode(value.InverseQ)
                };
            }

            public RSAParameters ToParameters(
                bool requirePrivate)
            {
                var result = new RSAParameters
                {
                    Modulus = Decode(Modulus, "modulus"),
                    Exponent = Decode(Exponent, "exponent")
                };
                if (requirePrivate)
                {
                    result.D = Decode(D, "d");
                    result.P = Decode(P, "p");
                    result.Q = Decode(Q, "q");
                    result.DP = Decode(DP, "dp");
                    result.DQ = Decode(DQ, "dq");
                    result.InverseQ =
                        Decode(InverseQ, "inverseQ");
                }

                return result;
            }

            private static string Encode(byte[]? value)
            {
                return value == null
                    ? string.Empty
                    : Convert.ToBase64String(value);
            }

            private static byte[] Decode(
                string value,
                string name)
            {
                try
                {
                    var bytes = Convert.FromBase64String(value);
                    if (bytes.Length == 0)
                    {
                        throw new InvalidDataException(
                            "Signing key " + name + " is empty.");
                    }

                    return bytes;
                }
                catch (FormatException exception)
                {
                    throw new InvalidDataException(
                        "Signing key " + name + " is invalid.",
                        exception);
                }
            }
        }
    }
}
