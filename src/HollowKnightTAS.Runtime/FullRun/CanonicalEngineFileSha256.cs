using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only the repeatedly validated engine file uses the Windows SHA-256
    // provider. No file/hash cache or wider native validation scope is added.
    internal static class CanonicalEngineFileSha256
    {
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string name, string? implementation, uint flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int BCryptGetProperty(IntPtr owner, string name, [Out] byte[] output, int size, out int written, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr storage, int size, IntPtr secret, int secretSize, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(IntPtr hash, [In] byte[] input, int size, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, int size, uint flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyHash(IntPtr hash);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
        private static void Require(int status)
        {
            if (status != 0) throw new CryptographicException("Pinned engine SHA-256 provider failed: 0x" + unchecked((uint)status).ToString("x8"));
        }
        internal static byte[] ComputeHash(Stream stream)
        {
            if (stream == null || !stream.CanRead) throw new ArgumentException("Readable engine hash stream is required.");
            IntPtr algorithm = IntPtr.Zero, hash = IntPtr.Zero;
            try {
                Require(BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 0));
                if (algorithm == IntPtr.Zero) throw new CryptographicException("Pinned engine SHA-256 algorithm is absent.");
                var length = new byte[4];
                Require(BCryptGetProperty(algorithm, "HashDigestLength", length, length.Length, out var written, 0));
                if (written != 4 || BitConverter.ToInt32(length, 0) != 32)
                    throw new CryptographicException("Pinned engine SHA-256 digest length differs.");
                // Windows 7+ owns the hash object allocation when storage is
                // null. The target is Windows; no managed buffer is retained
                // unpinned by the provider across these synchronous calls.
                Require(BCryptCreateHash(algorithm, out hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0));
                if (hash == IntPtr.Zero) throw new CryptographicException("Pinned engine SHA-256 hash is absent.");
                var buffer = new byte[131072]; int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) {
                    if (count < 0 || count > buffer.Length) throw new IOException("Pinned engine SHA-256 read count differs.");
                    Require(BCryptHashData(hash, buffer, count, 0));
                }
                var result = new byte[32]; Require(BCryptFinishHash(hash, result, result.Length, 0)); return result;
            } finally {
                // Always attempt both releases, including a failed read/hash.
                try { if (hash != IntPtr.Zero) Require(BCryptDestroyHash(hash)); }
                finally { if (algorithm != IntPtr.Zero) Require(BCryptCloseAlgorithmProvider(algorithm, 0)); }
            }
        }
    }
}
