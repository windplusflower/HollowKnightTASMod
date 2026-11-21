using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Opt-in P0 inspection and same-callback round-trip experiments. Native
    // state stays in memory; diagnostics contain observations and hashes only.
    internal static class LoadingEngineClockProbe
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr LookupInternalCall(IntPtr method);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetTimeManager();
        private const string EngineSha256 = "d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e";
        [ThreadStatic] private static CheckpointValidation? checkpointValidation;
        [ThreadStatic] private static ActualDValidation? actualDValidation;
        private static ProcessModule? cachedEngine;
        private static bool engineCheckedOnce;
        private static bool engineSha256Matched;
        private static bool coroutineFallbackWarned;
        private static bool roundTripFallbackWarned;

        internal static bool IsPinnedEngineMatched
        {
            get
            {
                EnsureEngineCheckedOnce();
                return engineSha256Matched;
            }
        }

        private static ProcessModule EnsureEngineCheckedOnce()
        {
            var engine = cachedEngine ??= Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "UnityPlayer.dll");
            if (!engineCheckedOnce)
            {
                engineCheckedOnce = true;
                try
                {
                    var timer = Stopwatch.StartNew();
                    using (var file = File.Open(engine.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        var actualHash = BitConverter.ToString(CanonicalEngineFileSha256.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                        engineSha256Matched = string.Equals(actualHash, EngineSha256, StringComparison.Ordinal);
                        if (!engineSha256Matched)
                        {
                            Modding.Logger.LogWarn("[HKTAS] UnityPlayer.dll SHA-256 differs from baseline (" + actualHash
                                + "); native offset hooks will run in safe fallback/no-op mode.");
                        }
                        else
                        {
                            Modding.Logger.Log("[HKTAS] Engine validation: fullHashes=1;fileBytes=" + file.Length + ";hashMilliseconds=" + timer.ElapsedMilliseconds);
                        }
                    }
                }
                catch (Exception ex)
                {
                    engineSha256Matched = false;
                    Modding.Logger.LogWarn("[HKTAS] Unable to hash UnityPlayer.dll (" + ex.Message + "); native offset hooks will run in safe fallback/no-op mode.");
                }
            }
            return engine;
        }

        // Only the synchronous, parked actual-D command may share validation
        // across its smaller native readback scopes. Keep the hashed file open
        // without write/delete sharing until every complete readback finishes.
        internal sealed class ActualDValidation : IDisposable
        {
            private readonly ProcessModule engine;
            private readonly FileStream? file;
            private readonly int thread, size;
            private readonly IntPtr address;
            private long validations;
            internal long ModuleIdentityChecks => validations;
            private bool disposed;

            internal ActualDValidation()
            {
                if (actualDValidation != null || checkpointValidation != null)
                    throw new InvalidOperationException("Actual D engine validation cannot nest or borrow an earlier transaction.");
                engine = EnsureEngineCheckedOnce();
                thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                address = engine.BaseAddress; size = engine.ModuleMemorySize;
                try { file = File.Open(engine.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (Exception ex) { Modding.Logger.LogWarn("[HKTAS] Actual D engine file lease open failed: " + ex.Message); }
                RequireEngine();
                actualDValidation = this;
            }
            internal ProcessModule RequireEngine()
            {
                if (disposed || thread != System.Threading.Thread.CurrentThread.ManagedThreadId
                    || (file != null && file.SafeFileHandle.IsClosed)
                    || GetModuleHandle("UnityPlayer.dll") != address || engine.BaseAddress != address || engine.ModuleMemorySize != size)
                    throw new InvalidOperationException("Actual D validated engine/file lease changed during its synchronous transaction.");
                validations++;
                return engine;
            }
            public void Dispose()
            {
                try {
                    if (disposed || !ReferenceEquals(actualDValidation, this) || checkpointValidation != null
                        || thread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                        throw new InvalidOperationException("Actual D engine validation scope ownership differs.");
                    RequireEngine();
                } finally {
                    disposed = true;
                    if (ReferenceEquals(actualDValidation, this)) actualDValidation = null;
                    file?.Dispose();
                }
            }
        }

        // A single synchronous C transaction never crosses PlayerLoop or a
        // thread boundary. Share its validated loaded module, and forget it in
        // Dispose; every later transaction validates the file afresh.
        internal sealed class CheckpointValidation : IDisposable
        {
            internal readonly ProcessModule Engine;
            internal CheckpointValidation()
            {
                if (checkpointValidation != null) throw new InvalidOperationException("Checkpoint engine validation scopes cannot nest.");
                Engine = RequirePinnedEngine(); checkpointValidation = this;
            }
            public void Dispose()
            {
                if (!ReferenceEquals(checkpointValidation, this)) throw new InvalidOperationException("Checkpoint engine validation scope ownership differs.");
                checkpointValidation = null;
            }
        }

        // A synchronous read helper may already be inside the boundary's
        // validated scope. Reuse only that same-thread live scope; standalone
        // reads acquire and dispose their own validation. Never cross a loop.
        internal static CheckpointValidation? ReadOnlyCheckpointScope()
        {
            if (checkpointValidation == null) return new CheckpointValidation();
            RequirePinnedEngine();
            return null;
        }

        internal static ProcessModule RequirePinnedEngine()
        {
            var transactionEngine = actualDValidation?.RequireEngine();
            if (checkpointValidation != null)
            {
                var checkedEngine = checkpointValidation.Engine;
                if (GetModuleHandle("UnityPlayer.dll") != checkedEngine.BaseAddress)
                    Modding.Logger.LogWarn("[HKTAS] Validated checkpoint engine module handle changed during C.");
                return checkedEngine;
            }
            if (transactionEngine != null) return transactionEngine;
            return EnsureEngineCheckedOnce();
        }

        internal static void WithCoroutineScheduleTime(double time, long frame, int mode, Action schedule)
            => new CoroutineScheduler().WithTime(time, frame, mode, schedule);
        internal sealed class CoroutineScheduler
        {
            private readonly ProcessModule engine = RequirePinnedEngine();
            internal void WithTime(double time, long frame, int mode, Action schedule)
                => WithCoroutineScheduleTime(engine, time, frame, mode, schedule);
        }
        private static void WithCoroutineScheduleTime(ProcessModule engine, double time, long frame, int mode, Action schedule)
        {
            if (!IsPinnedEngineMatched)
            {
                if (!coroutineFallbackWarned)
                {
                    coroutineFallbackWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] Skipping native TimeManager coroutine scheduling on unmatched UnityPlayer.dll; using default schedule.");
                }
                schedule();
                return;
            }
            if (double.IsNaN(time) || double.IsInfinity(time) || (mode & 8) != 0 && (frame < 0 || frame > int.MaxValue))
            {
                Modding.Logger.LogWarn("[HKTAS] Coroutine schedule time/frame is invalid; falling back to default schedule.");
                schedule();
                return;
            }
            var getter = (GetTimeManager)Marshal.GetDelegateForFunctionPointer(
                IntPtr.Add(engine.BaseAddress, 0x52b330), typeof(GetTimeManager));
            var manager = getter();
            if (manager == IntPtr.Zero)
            {
                Modding.Logger.LogWarn("[HKTAS] Coroutine TimeManager binding is absent; falling back to default schedule.");
                schedule();
                return;
            }
            // Pinned CallDelayed at 0x628490 selects 0x30 for mode bit 1,
            // 0x60 for bit 2 (the ordinary seconds wait), their minimum for
            // both, and 0x90 otherwise. Bit 8 records the frame counter + 1.
            var offsets = (mode & 3) == 0 ? new[] { 0x90 }
                : new[] { (mode & 1) != 0 ? 0x30 : -1, (mode & 2) != 0 ? 0x60 : -1 }.Where(o => o >= 0).ToArray();
            var fields = offsets.Select(o => IntPtr.Add(manager, o)).ToArray();
            var before = fields.Select(field => Marshal.ReadInt64(field)).ToArray();
            var frameTimeBefore = BitConverter.DoubleToInt64Bits(Time.timeAsDouble);
            var frameField = IntPtr.Add(manager, 0xc8); var frameBefore = Marshal.ReadInt64(frameField);
            if (frameTimeBefore != Marshal.ReadInt64(IntPtr.Add(manager, 0x90)) || Time.frameCount != frameBefore)
            {
                Modding.Logger.LogWarn("[HKTAS] Coroutine reconstruction is not at the scaled frame clock boundary; falling back to default schedule.");
                schedule();
                return;
            }
            try { foreach (var field in fields) Marshal.WriteInt64(field, BitConverter.DoubleToInt64Bits(time));
                if ((mode & 8) != 0) Marshal.WriteInt64(frameField, frame); schedule(); }
            finally { for (var i = 0; i < fields.Length; i++) Marshal.WriteInt64(fields[i], before[i]);
                Marshal.WriteInt64(frameField, frameBefore); }
            if (fields.Select(field => Marshal.ReadInt64(field)).Where((v, i) => v != before[i]).Any()
                || BitConverter.DoubleToInt64Bits(Time.timeAsDouble) != frameTimeBefore || Time.frameCount != frameBefore)
            {
                Modding.Logger.LogWarn("[HKTAS] Coroutine reconstruction observed unexpected frame clock delta.");
            }
        }

        public static void Capture(string directory)
        {
            var inspect = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_ENGINE_LAYOUT") == "1";
            var roundTrip = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP") == "1";
            if (!inspect && !roundTrip) return;
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var lookupPointer = GetProcAddress(mono, "mono_lookup_internal_call");
            if (mono == IntPtr.Zero || lookupPointer == IntPtr.Zero)
            {
                Modding.Logger.LogWarn("[HKTAS] Installed Mono icall lookup is unavailable; skipping LoadingEngineClockProbe.Capture.");
                return;
            }
            var lookup = (LookupInternalCall)Marshal.GetDelegateForFunctionPointer(lookupPointer, typeof(LookupInternalCall));
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();
            var getters = new[] { "time", "timeAsDouble", "fixedTime", "fixedTimeAsDouble", "unscaledTime", "unscaledTimeAsDouble", "realtimeSinceStartup", "realtimeSinceStartupAsDouble", "frameCount", "deltaTime", "fixedDeltaTime", "timeScale" };
            var calls = getters.Select(name =>
            {
                var method = typeof(Time).GetProperty(name)?.GetGetMethod();
                if (method == null) return null;
                var pointer = lookup(method.MethodHandle.Value);
                if (pointer == IntPtr.Zero) return null;
                var module = modules.SingleOrDefault(m => pointer.ToInt64() >= m.BaseAddress.ToInt64()
                    && pointer.ToInt64() < m.BaseAddress.ToInt64() + m.ModuleMemorySize);
                if (module == null) return null;
                var bytes = new byte[128];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return new { name, module = module.ModuleName, rva = pointer.ToInt64() - module.BaseAddress.ToInt64(),
                    code = BitConverter.ToString(bytes).Replace("-", "") };
            }).Where(item => item != null).ToArray();
            if (inspect) File.WriteAllText(Path.Combine(directory, "engine-clock-layout.json"), JsonConvert.SerializeObject(calls, Formatting.Indented));
            if (roundTrip) VerifyRoundTrip(directory);
        }

        private static void VerifyRoundTrip(string directory)
        {
            var engine = RequirePinnedEngine();
            if (!IsPinnedEngineMatched)
            {
                if (!roundTripFallbackWarned)
                {
                    roundTripFallbackWarned = true;
                    Modding.Logger.LogWarn("[HKTAS] Skipping engine clock round-trip probe on unmatched UnityPlayer.dll.");
                }
                File.WriteAllText(Path.Combine(directory, "engine-clock-roundtrip.json"), JsonConvert.SerializeObject(new
                    { engineSha256 = EngineSha256, skipped = true, reason = "unmatched-engine-sha256" }, Formatting.Indented));
                return;
            }
            var factory = IntPtr.Add(engine.BaseAddress, 0x52b330);
            var getManager = (GetTimeManager)Marshal.GetDelegateForFunctionPointer(factory, typeof(GetTimeManager));
            var manager = getManager();
            if (manager == IntPtr.Zero)
            {
                Modding.Logger.LogWarn("[HKTAS] Engine TimeManager is unavailable; skipping round-trip probe.");
                return;
            }
            // Matching PDB: TimeManager is 0x430 bytes, with a 0x30-byte Object
            // base followed by scalar TimeHolders, flags, counters and doubles.
            // A field-only test does not cover engine scheduling or QPC state.
            const int stateOffset = 0x30, stateLength = 0x400;
            var nativeBefore = new byte[stateLength];
            Marshal.Copy(IntPtr.Add(manager, stateOffset), nativeBefore, 0, stateLength);
            var clusterApi = ProbeClusterSerialization();
            var nativeAfterApi = new byte[stateLength];
            Marshal.Copy(IntPtr.Add(manager, stateOffset), nativeAfterApi, 0, stateLength);
            if (!nativeBefore.SequenceEqual(nativeAfterApi))
            {
                Marshal.Copy(nativeBefore, 0, IntPtr.Add(manager, stateOffset), stateLength);
                Modding.Logger.LogWarn("[HKTAS] Cluster serialization changed TimeManager during the read experiment; skipping round-trip writes.");
                return;
            }
            // These offsets are directly confirmed by the Time icalls and the
            // installed engine's update routine. Mutations never span PlayerLoop.
            var offsets = new[] { 0x30, 0x90, 0xa0, 0xc8, 0xe8 };
            var before = offsets.Select(o => Marshal.ReadInt64(manager, o)).ToArray();
            if (before[0] != BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble)
                || before[1] != BitConverter.DoubleToInt64Bits(Time.timeAsDouble)
                || before[2] != BitConverter.DoubleToInt64Bits(Time.unscaledTimeAsDouble)
                || before[3] != Time.frameCount)
            {
                Modding.Logger.LogWarn("[HKTAS] TimeManager field observations do not match Unity getters; skipping round-trip writes.");
                return;
            }
            bool propertyWritesObserved = false;
            try
            {
                for (var i = 0; i < offsets.Length; i++)
                    Marshal.WriteInt64(manager, offsets[i], i == 3 ? before[i] + 7
                        : BitConverter.DoubleToInt64Bits(BitConverter.Int64BitsToDouble(before[i]) + 0.25d));
                propertyWritesObserved = Time.frameCount == before[3] + 7
                    && BitConverter.DoubleToInt64Bits(Time.timeAsDouble) == Marshal.ReadInt64(manager, 0x90)
                    && BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble) == Marshal.ReadInt64(manager, 0x30)
                    && BitConverter.DoubleToInt64Bits(Time.unscaledTimeAsDouble) == Marshal.ReadInt64(manager, 0xa0);
            }
            finally
            {
                Marshal.Copy(nativeBefore, 0, IntPtr.Add(manager, stateOffset), stateLength);
            }
            var nativeAfter = new byte[stateLength];
            Marshal.Copy(IntPtr.Add(manager, stateOffset), nativeAfter, 0, stateLength);
            var restored = nativeAfter.SequenceEqual(nativeBefore);
            string Hash(byte[] value)
            {
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant();
            }
            File.WriteAllText(Path.Combine(directory, "engine-clock-roundtrip.json"), JsonConvert.SerializeObject(new
                { engineSha256 = EngineSha256, offsets, stateOffset, stateLength, propertyWritesObserved, restored,
                    beforeHash = Hash(nativeBefore), afterHash = Hash(nativeAfter), clusterApi,
                    completeClockCoverage = false }, Formatting.Indented));
            if (!propertyWritesObserved || !restored)
                Modding.Logger.LogWarn("[HKTAS] Engine clock round-trip check did not pass.");
        }

        private static object ProbeClusterSerialization()
        {
            object? buffer = null;
            var bytes = -1;
            string error = "";
            try
            {
                var module = Assembly.Load("UnityEngine.ClusterRendererModule");
                var type = module.GetType("UnityEngine.ClusterSerialization", true)!;
                var arrayType = typeof(Time).Assembly.GetType("Unity.Collections.NativeArray`1", true)!.MakeGenericType(typeof(byte));
                var allocator = typeof(Time).Assembly.GetType("Unity.Collections.Allocator", true)!;
                var options = typeof(Time).Assembly.GetType("Unity.Collections.NativeArrayOptions", true)!;
                buffer = Activator.CreateInstance(arrayType, 65536, Enum.Parse(allocator, "Temp"), Enum.Parse(options, "ClearMemory"));
                bytes = (int)type.GetMethod("SaveTimeManagerState", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new[] { buffer });
            }
            catch (Exception ex)
            {
                var actual = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                error = actual.GetType().FullName + ": " + actual.Message;
            }
            finally { (buffer as IDisposable)?.Dispose(); }
            return new { method = "UnityEngine.ClusterSerialization.SaveTimeManagerState", bytes, error,
                savedPayloadWrittenToDisk = false, featureFlagsChanged = false };
        }
    }
}
