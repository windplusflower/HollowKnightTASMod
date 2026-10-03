using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;
using UnityEngine.LowLevel;

namespace HollowKnightTAS.Runtime.FullRun
{
    // P0 opt-in experiment. Only pumps Unity's preloader while scene activation
    // is blocked; the original caller still owns activation and its lifecycle.
    internal sealed class LoadingSynchronousFetchProbe : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativePreloadUpdate();
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo
        {
            public IntPtr BaseAddress, AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State, Protect, Type;
        }
        [DllImport("kernel32.dll")]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr size);
        private readonly ILHook hook;
        private static LoadingSynchronousFetchProbe? instance;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private NativePreloadUpdate? update;
        private bool pumping;
        private int diagnosticRows;

        public LoadingSynchronousFetchProbe()
        {
            var method = typeof(UnityEngine.SceneManagement.SceneManager).GetMethod("LoadSceneAsyncNameIndexInternal",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            if (method == null) throw new InvalidOperationException("Installed scene async wrapper is unavailable.");
            hook = new ILHook(method, il =>
            {
                var returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
                if (returns.Length != 1) throw new InvalidOperationException("Unexpected scene async wrapper IL.");
                var cursor = new ILCursor(il);
                cursor.Goto(returns[0], MoveType.Before);
                cursor.EmitDelegate<Func<AsyncOperation?, AsyncOperation?>>(Pump);
            });
            Modding.Logger.Log("[HKTAS] P0 synchronous scene fetch experiment attached.");
            instance = this;
            Milestone("attached");
        }

        internal void Milestone(string name)
        {
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            if (++diagnosticRows > 512) throw new InvalidOperationException("Preloader diagnostic row budget exceeded.");
            var process = System.Diagnostics.Process.GetCurrentProcess().Id;
            var row = new { name, process, frame = Time.frameCount,
                timeBits = BitConverter.DoubleToInt64Bits(Time.timeAsDouble).ToString("x16"),
                captureBits = BitConverter.ToInt32(BitConverter.GetBytes(Time.captureDeltaTime), 0).ToString("x8") };
            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "preloader-events-" + process + ".jsonl"),
                Newtonsoft.Json.JsonConvert.SerializeObject(row) + "\n");
        }

        private AsyncOperation? Pump(AsyncOperation? operation) => PumpOperation(operation, false);

        internal static void CompleteSerialActivation(AsyncOperation operation)
        {
            if (!HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesSerialScenes(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId)
                || !operation.allowSceneActivation)
                throw new InvalidOperationException("Synchronous activation requires an authorized V6 scene operation.");
            (instance ?? throw new InvalidOperationException("Scene preloader is unavailable."))
                .PumpOperation(operation, true);
        }

        private AsyncOperation? PumpOperation(AsyncOperation? operation, bool completeActivation)
        {
            if (operation == null || operation.isDone || (!completeActivation && operation.progress >= 0.9f)) return operation;
            if (pumping || Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Scene fetch cannot reenter or run outside the Unity thread.");
            if (update == null)
            {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                var systems = Flatten(PlayerLoop.GetCurrentPlayerLoop()).Where(s => s.type == typeof(UnityEngine.PlayerLoop.EarlyUpdate.UpdatePreloading)).ToArray();
                if (systems.Length != 1 || systems[0].updateFunction == IntPtr.Zero || systems[0].updateDelegate != null)
                    throw new InvalidOperationException("Native preloading PlayerLoop system is unavailable.");
                // PlayerLoop's native updateFunction can refer to a function
                // pointer slot. Validate page permissions before invoking it.
                var pointer = systems[0].updateFunction;
                if (VirtualQuery(pointer, out var info, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero
                    || info.State != 0x1000 || (info.Protect & 0x100) != 0)
                    throw new InvalidOperationException("Preloader pointer is not committed memory.");
                var slot = pointer;
                if ((info.Protect & 0xf0) == 0) pointer = Marshal.ReadIntPtr(pointer);
                if (VirtualQuery(pointer, out info, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero
                    || info.State != 0x1000 || (info.Protect & 0xf0) == 0 || (info.Protect & 0x100) != 0)
                    throw new InvalidOperationException("Preloader target is not executable memory.");
                var rva = pointer.ToInt64() - engine.BaseAddress.ToInt64();
                // Matching image disassembly: no-argument wrapper calls
                // GetPreloadManager (0x771690), then tail-calls UpdatePreloading
                // (0x773aa0) with the returned instance in RCX.
                if (rva != 0x765c80)
                    throw new InvalidOperationException("Preloader target does not match the verified no-argument wrapper.");
                var code = new byte[24];
                Marshal.Copy(pointer, code, 0, code.Length);
                if (!code.Take(21).SequenceEqual(new byte[] { 0x48,0x83,0xec,0x28,0xe8,0x07,0xba,0x00,0x00,0x48,
                    0x8b,0xc8,0x48,0x83,0xc4,0x28,0xe9,0x0b,0xde,0x00,0x00 }))
                    throw new InvalidOperationException("Preloader wrapper code was changed after engine verification.");
                var report = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                if (!string.IsNullOrEmpty(report))
                    System.IO.File.WriteAllText(System.IO.Path.Combine(report, "preloader-pointer.txt"),
                        "slot=" + slot.ToInt64().ToString("x") + " target=" + pointer.ToInt64().ToString("x")
                        + " rva=" + rva.ToString("x") + " code=" + BitConverter.ToString(code).Replace("-", "")
                        + " protection=" + info.Protect.ToString("x"));
                update = (NativePreloadUpdate)Marshal.GetDelegateForFunctionPointer(pointer, typeof(NativePreloadUpdate));
            }
            var frame = Time.frameCount;
            var time = BitConverter.DoubleToInt64Bits(Time.timeAsDouble);
            var fixedTime = BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble);
            var activation = operation.allowSceneActivation;
            var wallStart = Environment.TickCount;
            var iterations = 0;
            pumping = true;
            try
            {
                operation.allowSceneActivation = completeActivation;
                bool Pending() => !operation.isDone && (completeActivation || operation.progress < 0.9f);
                while (Pending())
                {
                    update();
                    iterations++;
                    if (unchecked(Environment.TickCount - wallStart) > 30000)
                        throw new TimeoutException("Native scene fetch pump exceeded 30 seconds.");
                    if (Pending()) Thread.Sleep(1);
                }
                if (Time.frameCount != frame || BitConverter.DoubleToInt64Bits(Time.timeAsDouble) != time
                    || BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble) != fixedTime)
                    throw new InvalidOperationException("Preloading advanced the engine clock outside PlayerLoop.");
                Modding.Logger.Log("[HKTAS] P0 fetch completed without advancing clock: frame=" + frame
                    + " iterations=" + iterations + " wallMs=" + unchecked(Environment.TickCount - wallStart));
                Milestone(completeActivation ? "serial-scene-activation-complete" : "scene-fetch-complete");
            }
            finally { operation.allowSceneActivation = activation; pumping = false; }
            return operation;
        }

        private static IEnumerable<PlayerLoopSystem> Flatten(PlayerLoopSystem node)
        {
            yield return node;
            if (node.subSystemList != null)
                foreach (var child in node.subSystemList)
                foreach (var nested in Flatten(child)) yield return nested;
        }

        public void Dispose()
        {
            if (instance == this) instance = null;
            hook.Dispose();
        }
    }
}
