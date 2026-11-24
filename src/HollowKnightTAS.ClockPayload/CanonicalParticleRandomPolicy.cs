using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HollowKnightTAS.Core.Movie;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    // One initialization before the first PlayerLoop, never a per-frame reseed.
    // Particle Play/Stop and module random draws retain their original implementation.
    internal sealed class CanonicalParticleRandomPolicy
    {
        private const string EngineSha256 = "d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e";
        private const uint RootSeed = 1212896321;
        private readonly IntPtr managerSlot;
        private readonly IntPtr manager;
        private readonly bool supported;
        private readonly string unsupportedReason = string.Empty;
        private bool unsupportedWarned;
        private bool checkpointImported;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(
            IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

        internal CanonicalParticleRandomPolicy()
        {
            try
            {
                if (!NativeExecutionProfile.UsesCanonicalParticles(NativeExecutionProfile.SelectedProfileId)
                    || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
                {
                    unsupportedReason = "Particle root initialization disabled outside V11 full-run.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }

                var engine = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .FirstOrDefault(m => string.Equals(m.ModuleName, "UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
                if (engine == null || string.IsNullOrWhiteSpace(engine.FileName) || !File.Exists(engine.FileName))
                {
                    unsupportedReason = "UnityPlayer.dll module or path is unavailable.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Particle random policy marked passthrough/unsupported.");
                    return;
                }

                string digest;
                using (var file = File.OpenRead(engine.FileName))
                using (var sha = SHA256.Create())
                    digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (digest != EngineSha256)
                {
                    unsupportedReason = "UnityPlayer.dll SHA-256 differs from baseline (" + digest + ").";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Particle random policy marked passthrough/unsupported.");
                    return;
                }

                // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2 age 1:
                // ResetSeeds at 0x1bbb30 reads this manager and its Rand at +0xc0.
                managerSlot = IntPtr.Add(engine.BaseAddress, 0x19b71f0);
                if (!TryRead(managerSlot, 8, out var slotBytes))
                {
                    unsupportedReason = "Particle manager slot read failed.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }
                manager = new IntPtr(BitConverter.ToInt64(slotBytes, 0));
                if (manager == IntPtr.Zero || !TryRead(manager, 64, out var header))
                {
                    unsupportedReason = "Particle manager is not initialized or header read failed.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }

                var activeCount = BitConverter.ToUInt64(header, 16);
                var stoppingCount = BitConverter.ToUInt64(header, 48);
                var loadedParticles = Resources.FindObjectsOfTypeAll<ParticleSystem>();
                var loaded = loadedParticles.Length;
                if (activeCount != 0 || stoppingCount != 0 || loaded != 2
                    || !loadedParticles.Select(p => p.name).OrderBy(name => name, StringComparer.Ordinal)
                        .SequenceEqual(new[] { "Particles BG", "Particles FG" })
                    || loadedParticles.Any(p => !PristineIntroParticle(p)))
                {
                    unsupportedReason = "Particle root preconditions differ: active="
                        + activeCount + " stopping=" + stoppingCount + " loaded=" + loaded + " details="
                        + string.Join(";", loadedParticles.Select(p => p.name + ":scene=" + p.gameObject.scene.name
                            + ":valid=" + p.gameObject.scene.IsValid() + ":playing=" + p.isPlaying
                            + ":stopped=" + p.isStopped + ":emitting=" + p.isEmitting + ":time=" + p.time
                            + ":auto=" + p.useAutoRandomSeed + ":seed=" + p.randomSeed));
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }

                if (!TryRead(IntPtr.Add(manager, 192), 16, out var before))
                {
                    unsupportedReason = "Particle random state read failed.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }

                var state = new uint[4];
                state[0] = RootSeed;
                for (var i = 1; i < state.Length; i++) state[i] = unchecked(state[i - 1] * 1812433253u + 1u);
                var bytes = new byte[16]; Buffer.BlockCopy(state, 0, bytes, 0, bytes.Length);
                if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(manager, 192), bytes, (UIntPtr)bytes.Length, out var written)
                    || written.ToUInt64() != (ulong)bytes.Length
                    || !TryRead(IntPtr.Add(manager, 192), 16, out var afterReadback)
                    || !afterReadback.SequenceEqual(bytes))
                {
                    unsupportedReason = "Particle random root initialization write failed.";
                    UnityEngine.Debug.LogWarning("[HKTAS] " + unsupportedReason + " Marked passthrough/unsupported.");
                    return;
                }

                supported = true;
                UnityEngine.Debug.Log("[HKTAS] V11 particle random root initialized before first play; verified two pristine intro emitters: before="
                    + BitConverter.ToString(before) + " after=" + BitConverter.ToString(bytes));
            }
            catch (Exception ex)
            {
                supported = false;
                unsupportedReason = ex.GetType().Name + ": " + ex.Message;
                UnityEngine.Debug.LogWarning("[HKTAS] Particle random policy marked passthrough/unsupported: " + unsupportedReason);
            }
        }

        internal bool IsSupported => supported;

        internal void RebaselineAfterPreload()
        {
            if (!supported)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random RebaselineAfterPreload() (passthrough/unsupported): " + unsupportedReason);
                return;
            }
            if (!TryRead(managerSlot, 8, out var slotBytes)
                || new IntPtr(BitConverter.ToInt64(slotBytes, 0)) != manager)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random RebaselineAfterPreload(): particle manager slot changed.");
                return;
            }
            var state = new uint[4];
            state[0] = RootSeed;
            for (var i = 1; i < state.Length; i++) state[i] = unchecked(state[i - 1] * 1812433253u + 1u);
            RestoreWords(state);
            UnityEngine.Debug.Log("[HKTAS] V11 particle random baseline restored after mod preloading.");
        }

        private static bool PristineIntroParticle(ParticleSystem particle)
        {
            if (particle.gameObject.scene.name != "Pre_Menu_Intro" || particle.isPlaying || !particle.isStopped
                || particle.isEmitting || particle.time != 0f || particle.randomSeed != 0 || !particle.useAutoRandomSeed)
                return false;
            var field = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null) return false;
            var pointer = (IntPtr)field.GetValue(particle);
            // Pinned ParticleSystem::m_NeedsSync (+108) must be clear before
            // invoking the same count copy query used by the read-only oracle.
            if (pointer == IntPtr.Zero || !TryRead(IntPtr.Add(pointer, 108), 1, out var sync) || sync[0] != 0) return false;
            return particle.particleCount == 0;
        }

        internal uint[] Capture()
        {
            if (!supported)
            {
                if (!unsupportedWarned)
                {
                    unsupportedWarned = true;
                    UnityEngine.Debug.LogWarning("[HKTAS] Particle random Capture() returning empty array (passthrough/unsupported): " + unsupportedReason);
                }
                return Array.Empty<uint>();
            }
            if (!TryRead(managerSlot, 8, out var slotBytes)
                || new IntPtr(BitConverter.ToInt64(slotBytes, 0)) != manager
                || !TryRead(IntPtr.Add(manager, 192), 16, out var wordBytes))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Particle manager read failed or slot changed during Capture(); returning empty array.");
                return Array.Empty<uint>();
            }
            var result = new uint[4];
            Buffer.BlockCopy(wordBytes, 0, result, 0, 16);
            return result;
        }

        internal void RestoreCheckpoint(uint[] source)
        {
            if (!supported)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random RestoreCheckpoint() (passthrough/unsupported): " + unsupportedReason);
                return;
            }
            if (checkpointImported || source == null || source.Length != 4 || source.All(word => word == 0))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random RestoreCheckpoint(): source state invalid or already imported.");
                return;
            }
            checkpointImported = true;
            RestoreWords(source);
        }

        private void RestoreWords(uint[] source)
        {
            var current = Capture();
            if (current.Length != 4 || !TryRead(manager, 64, out var beforeHeader))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping particle random RestoreWords(): unable to observe current manager state.");
                return;
            }
            var bytes = new byte[16]; Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
            if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(manager, 192), bytes, (UIntPtr)bytes.Length, out var written)
                || written.ToUInt64() != (ulong)bytes.Length
                || !Capture().SequenceEqual(source)
                || !TryRead(manager, 64, out var afterHeader)
                || !afterHeader.SequenceEqual(beforeHeader))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Particle random continuation raw readback or manager header check failed.");
            }
        }

        private static bool TryRead(IntPtr address, int length, out byte[] bytes)
        {
            bytes = new byte[length];
            return address != IntPtr.Zero
                && ReadProcessMemory(GetCurrentProcess(), address, bytes, (UIntPtr)length, out var read)
                && read.ToUInt64() == (ulong)length;
        }
    }
}
