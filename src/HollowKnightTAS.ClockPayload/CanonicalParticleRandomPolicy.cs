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
        private bool checkpointImported;
        private ulong residentImportedToken;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(
            IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr written);

        internal CanonicalParticleRandomPolicy()
        {
            if (!NativeExecutionProfile.UsesCanonicalParticles(NativeExecutionProfile.SelectedProfileId)
                || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
                throw new InvalidOperationException("Particle root initialization requires V11 full-run.");
            var engine = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "UnityPlayer.dll");
            using (var file = File.OpenRead(engine.FileName))
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != EngineSha256)
                    throw new InvalidOperationException("Particle root initialization requires the pinned engine.");
            // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2 age 1:
            // ResetSeeds at 0x1bbb30 reads this manager and its Rand at +0xc0.
            managerSlot = IntPtr.Add(engine.BaseAddress, 0x19b71f0);
            manager = new IntPtr(BitConverter.ToInt64(Read(managerSlot, 8), 0));
            if (manager == IntPtr.Zero) throw new InvalidOperationException("Particle manager is not initialized.");
            var header = Read(manager, 64);
            var activeCount = BitConverter.ToUInt64(header, 16);
            var stoppingCount = BitConverter.ToUInt64(header, 48);
            var loadedParticles = Resources.FindObjectsOfTypeAll<ParticleSystem>();
            var loaded = loadedParticles.Length;
            if (activeCount != 0 || stoppingCount != 0 || loaded != 2
                || !loadedParticles.Select(p => p.name).OrderBy(name => name, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "Particles BG", "Particles FG" })
                || loadedParticles.Any(p => !PristineIntroParticle(p)))
                throw new InvalidOperationException("Particle root initialization is too late: active="
                    + activeCount + " stopping=" + stoppingCount + " loaded=" + loaded + " details="
                    + string.Join(";", loadedParticles.Select(p => p.name + ":scene=" + p.gameObject.scene.name
                        + ":valid=" + p.gameObject.scene.IsValid() + ":playing=" + p.isPlaying
                        + ":stopped=" + p.isStopped + ":emitting=" + p.isEmitting + ":time=" + p.time
                        + ":auto=" + p.useAutoRandomSeed + ":seed=" + p.randomSeed)));
            var before = Read(IntPtr.Add(manager, 192), 16);
            var state = new uint[4];
            state[0] = RootSeed;
            for (var i = 1; i < state.Length; i++) state[i] = unchecked(state[i - 1] * 1812433253u + 1u);
            var bytes = new byte[16]; Buffer.BlockCopy(state, 0, bytes, 0, bytes.Length);
            if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(manager, 192), bytes, (UIntPtr)bytes.Length, out var written)
                || written.ToUInt64() != (ulong)bytes.Length
                || !Read(IntPtr.Add(manager, 192), 16).SequenceEqual(bytes))
                throw new InvalidOperationException("Particle random root initialization failed.");
            UnityEngine.Debug.Log("[HKTAS] V11 particle random root initialized before first play; verified two pristine intro emitters: before="
                + BitConverter.ToString(before) + " after=" + BitConverter.ToString(bytes));
        }

        private static bool PristineIntroParticle(ParticleSystem particle)
        {
            if (particle.gameObject.scene.name != "Pre_Menu_Intro" || particle.isPlaying || !particle.isStopped
                || particle.isEmitting || particle.time != 0f || particle.randomSeed != 0 || !particle.useAutoRandomSeed)
                return false;
            var field = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
            var pointer = (IntPtr)field.GetValue(particle);
            // Pinned ParticleSystem::m_NeedsSync (+108) must be clear before
            // invoking the same count copy query used by the read-only oracle.
            if (pointer == IntPtr.Zero || Read(IntPtr.Add(pointer, 108), 1)[0] != 0) return false;
            return particle.particleCount == 0;
        }

        internal uint[] Capture()
        {
            if (new IntPtr(BitConverter.ToInt64(Read(managerSlot, 8), 0)) != manager)
                throw new InvalidOperationException("Particle manager was replaced after root initialization.");
            var result = new uint[4];
            Buffer.BlockCopy(Read(IntPtr.Add(manager, 192), 16), 0, result, 0, 16);
            return result;
        }

        internal void RestoreCheckpoint(uint[] source)
        {
            if (checkpointImported || source == null || source.Length != 4 || source.All(word => word == 0))
                throw new InvalidOperationException("Particle random continuation requires one valid source state.");
            checkpointImported = true;
            RestoreWords(source);
        }
        internal void RestoreResident(uint[] source, ulong token)
        {
            ClockController.RequireResidentClockBoundary(token, true);
            if (token == 0 || token <= residentImportedToken || source == null || source.Length != 4 || source.All(word => word == 0))
                throw new InvalidOperationException("Particle continuation requires one valid resident epoch.");
            residentImportedToken = token;
            RestoreWords(source);
            ClockController.RequireResidentClockBoundary(token, true);
        }
        private void RestoreWords(uint[] source)
        {
            Capture(); // Retain the actual target manager/slot, never a source address.
            var beforeHeader = Read(manager, 64);
            var bytes = new byte[16]; Buffer.BlockCopy(source, 0, bytes, 0, bytes.Length);
            if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(manager, 192), bytes, (UIntPtr)bytes.Length, out var written)
                || written.ToUInt64() != (ulong)bytes.Length || !Capture().SequenceEqual(source)
                || !Read(manager, 64).SequenceEqual(beforeHeader))
                throw new InvalidOperationException("Particle random continuation raw readback or original manager header differs.");
        }

        private static byte[] Read(IntPtr address, int length)
        {
            var bytes = new byte[length];
            if (!ReadProcessMemory(GetCurrentProcess(), address, bytes, (UIntPtr)length, out var read)
                || read.ToUInt64() != (ulong)length)
                throw new InvalidOperationException("Particle manager memory read failed.");
            return bytes;
        }
    }
}
