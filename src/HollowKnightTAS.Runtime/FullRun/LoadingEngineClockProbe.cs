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

        // Only the synchronous, parked actual-D command may share validation
        // across its smaller native readback scopes. Keep the hashed file open
        // without write/delete sharing until every complete readback finishes.
        internal sealed class ActualDValidation : IDisposable
        {
            private readonly ProcessModule engine;
            private readonly FileStream file;
            private readonly int thread, size;
            private readonly IntPtr address;
            private long validations;
            internal long ModuleIdentityChecks => validations;
            private bool disposed;
            internal ActualDValidation()
            {
                if (actualDValidation != null || checkpointValidation != null)
                    throw new InvalidOperationException("Actual D engine validation cannot nest or borrow an earlier transaction.");
                engine = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "UnityPlayer.dll");
                thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                address = engine.BaseAddress; size = engine.ModuleMemorySize;
                file = File.Open(engine.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                try {
                    var timer = Stopwatch.StartNew();
                    if (BitConverter.ToString(CanonicalEngineFileSha256.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != EngineSha256)
                        throw new InvalidOperationException("Actual D requires the exact installed engine build.");
                    RequireEngine(); actualDValidation = this;
                    Modding.Logger.Log("[HKTAS] Actual D engine validation: fullHashes=1;fileBytes=" + file.Length + ";hashMilliseconds=" + timer.ElapsedMilliseconds);
                } catch { disposed = true; file.Dispose(); throw; }
            }
            internal ProcessModule RequireEngine()
            {
                if (disposed || thread != System.Threading.Thread.CurrentThread.ManagedThreadId || file.SafeFileHandle.IsClosed
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
                    file.Dispose();
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
                    throw new InvalidOperationException("Validated checkpoint engine module changed during C.");
                return checkedEngine;
            }
            if (transactionEngine != null) return transactionEngine;
            var engine = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Single(m => m.ModuleName == "UnityPlayer.dll");
            using (var file = File.OpenRead(engine.FileName))
                if (BitConverter.ToString(CanonicalEngineFileSha256.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != EngineSha256)
                    throw new InvalidOperationException("Native loading experiments require the exact installed engine build.");
            return engine;
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
            if (double.IsNaN(time) || double.IsInfinity(time) || (mode & 8) != 0 && (frame < 0 || frame > int.MaxValue))
                throw new InvalidOperationException("Coroutine schedule time/frame is invalid.");
            var getter = (GetTimeManager)Marshal.GetDelegateForFunctionPointer(
                IntPtr.Add(engine.BaseAddress, 0x52b330), typeof(GetTimeManager));
            var manager = getter();
            if (manager == IntPtr.Zero) throw new InvalidOperationException("Coroutine TimeManager binding is absent.");
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
                throw new InvalidOperationException("Coroutine reconstruction is not at the scaled frame clock boundary.");
            try { foreach (var field in fields) Marshal.WriteInt64(field, BitConverter.DoubleToInt64Bits(time));
                if ((mode & 8) != 0) Marshal.WriteInt64(frameField, frame); schedule(); }
            finally { for (var i = 0; i < fields.Length; i++) Marshal.WriteInt64(fields[i], before[i]);
                Marshal.WriteInt64(frameField, frameBefore); }
            if (fields.Select(field => Marshal.ReadInt64(field)).Where((v, i) => v != before[i]).Any()
                || BitConverter.DoubleToInt64Bits(Time.timeAsDouble) != frameTimeBefore || Time.frameCount != frameBefore)
                throw new InvalidOperationException("Coroutine reconstruction changed the frame clock.");
        }

        public static void Capture(string directory)
        {
            var inspect = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_ENGINE_LAYOUT") == "1";
            var roundTrip = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_CLOCK_ROUNDTRIP") == "1";
            if (!inspect && !roundTrip) return;
            var mono = GetModuleHandle("mono-2.0-bdwgc.dll");
            var lookupPointer = GetProcAddress(mono, "mono_lookup_internal_call");
            if (mono == IntPtr.Zero || lookupPointer == IntPtr.Zero)
                throw new InvalidOperationException("Installed Mono icall lookup is unavailable.");
            var lookup = (LookupInternalCall)Marshal.GetDelegateForFunctionPointer(lookupPointer, typeof(LookupInternalCall));
            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().ToArray();
            var getters = new[] { "time", "timeAsDouble", "fixedTime", "fixedTimeAsDouble", "unscaledTime", "unscaledTimeAsDouble", "realtimeSinceStartup", "realtimeSinceStartupAsDouble", "frameCount", "deltaTime", "fixedDeltaTime", "timeScale" };
            var calls = getters.Select(name =>
            {
                var method = typeof(Time).GetProperty(name)?.GetGetMethod()
                    ?? throw new InvalidOperationException("Installed Time getter is missing: " + name);
                var pointer = lookup(method.MethodHandle.Value);
                if (pointer == IntPtr.Zero) throw new InvalidOperationException("Time icall not resolved: " + name);
                var module = modules.SingleOrDefault(m => pointer.ToInt64() >= m.BaseAddress.ToInt64()
                    && pointer.ToInt64() < m.BaseAddress.ToInt64() + m.ModuleMemorySize)
                    ?? throw new InvalidOperationException("Time icall is outside loaded module images: " + name);
                var bytes = new byte[128];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return new { name, module = module.ModuleName, rva = pointer.ToInt64() - module.BaseAddress.ToInt64(),
                    code = BitConverter.ToString(bytes).Replace("-", "") };
            }).ToArray();
            if (inspect) File.WriteAllText(Path.Combine(directory, "engine-clock-layout.json"), JsonConvert.SerializeObject(calls, Formatting.Indented));
            if (roundTrip) VerifyRoundTrip(directory);
        }

        private static void VerifyRoundTrip(string directory)
        {
            var engine = RequirePinnedEngine();
            var factory = IntPtr.Add(engine.BaseAddress, 0x52b330);
            var getManager = (GetTimeManager)Marshal.GetDelegateForFunctionPointer(factory, typeof(GetTimeManager));
            var manager = getManager();
            if (manager == IntPtr.Zero) throw new InvalidOperationException("Engine TimeManager is unavailable.");
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
                throw new InvalidOperationException("Cluster serialization changed TimeManager during the read experiment.");
            }
            // These offsets are directly confirmed by the Time icalls and the
            // installed engine's update routine. Mutations never span PlayerLoop.
            var offsets = new[] { 0x30, 0x90, 0xa0, 0xc8, 0xe8 };
            var before = offsets.Select(o => Marshal.ReadInt64(manager, o)).ToArray();
            if (before[0] != BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble)
                || before[1] != BitConverter.DoubleToInt64Bits(Time.timeAsDouble)
                || before[2] != BitConverter.DoubleToInt64Bits(Time.unscaledTimeAsDouble)
                || before[3] != Time.frameCount)
                throw new InvalidOperationException("TimeManager field observations do not match Unity getters.");
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
            if (!propertyWritesObserved || !restored) throw new InvalidOperationException("Engine clock round-trip failed.");
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
