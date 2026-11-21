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
    // Only pumps Unity's preloader while scene activation
    // is blocked; the original caller still owns activation and its lifecycle.
    internal sealed class DeterministicSceneFetch : IDisposable
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
        private readonly ILHook? hook;
        private readonly int thread = Thread.CurrentThread.ManagedThreadId;
        private NativePreloadUpdate? update;
        private bool fallbackToNativeAsync;
        private static bool baselineWarningLogged;
        private bool pumping;
        private int diagnosticRows;

        public DeterministicSceneFetch()
        {
            var method = typeof(UnityEngine.SceneManagement.SceneManager).GetMethod("LoadSceneAsyncNameIndexInternal",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                fallbackToNativeAsync = true;
                Modding.Logger.LogWarn("[HKTAS] SceneManager.LoadSceneAsyncNameIndexInternal is unavailable; falling back to native async scene loading.");
                return;
            }
            var ilMatched = false;
            hook = new ILHook(method, il =>
            {
                var returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
                if (returns.Length != 1)
                {
                    Modding.Logger.LogWarn("[HKTAS] Unexpected scene async wrapper IL (ret count=" + returns.Length + "); falling back to native async scene loading.");
                    return;
                }
                ilMatched = true;
                var cursor = new ILCursor(il);
                cursor.Goto(returns[0], MoveType.Before);
                cursor.EmitDelegate<Func<AsyncOperation?, AsyncOperation?>>(Pump);
            });
            if (!ilMatched)
            {
                hook.Dispose();
                hook = null;
                fallbackToNativeAsync = true;
                return;
            }
            Modding.Logger.Log("[HKTAS] Deterministic scene fetch attached.");
            Milestone("attached");
        }

        internal void Milestone(string name)
        {
            var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(directory)) return;
            if (++diagnosticRows > 512) return;
            var process = System.Diagnostics.Process.GetCurrentProcess().Id;
            var row = new { name, process, frame = Time.frameCount,
                timeBits = BitConverter.DoubleToInt64Bits(Time.timeAsDouble).ToString("x16"),
                captureBits = BitConverter.ToInt32(BitConverter.GetBytes(Time.captureDeltaTime), 0).ToString("x8") };
            System.IO.File.AppendAllText(System.IO.Path.Combine(directory, "preloader-events-" + process + ".jsonl"),
                Newtonsoft.Json.JsonConvert.SerializeObject(row) + "\n");
        }

        private bool TryResolveUpdatePreloading(out NativePreloadUpdate? resolved)
        {
            resolved = null;
            try
            {
                var modules = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().ToArray();
                var engine = modules.FirstOrDefault(m => string.Equals(m.ModuleName, "UnityPlayer.dll", StringComparison.OrdinalIgnoreCase));
                var systems = Flatten(PlayerLoop.GetCurrentPlayerLoop()).Where(s => s.type == typeof(UnityEngine.PlayerLoop.EarlyUpdate.UpdatePreloading)).ToArray();
                if (systems.Length != 1 || systems[0].updateFunction == IntPtr.Zero || systems[0].updateDelegate != null)
                {
                    Modding.Logger.LogWarn("[HKTAS] Native preloading PlayerLoop system is unavailable; falling back to native async scene loading.");
                    return false;
                }

                // PlayerLoop's native updateFunction can refer to a function
                // pointer slot. Validate page permissions before invoking it.
                var pointer = systems[0].updateFunction;
                if (VirtualQuery(pointer, out var info, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero
                    || info.State != 0x1000 || (info.Protect & 0x100) != 0 || (info.Protect & 0xfe) == 0)
                {
                    Modding.Logger.LogWarn("[HKTAS] Preloader pointer is not readable committed memory; falling back to native async scene loading.");
                    return false;
                }
                var slot = pointer;
                if ((info.Protect & 0xf0) == 0)
                {
                    pointer = Marshal.ReadIntPtr(pointer);
                    if (pointer == IntPtr.Zero)
                    {
                        Modding.Logger.LogWarn("[HKTAS] Dereferenced preloader pointer is null; falling back to native async scene loading.");
                        return false;
                    }
                    if (VirtualQuery(pointer, out info, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero
                        || info.State != 0x1000 || (info.Protect & 0xf0) == 0 || (info.Protect & 0x100) != 0)
                    {
                        Modding.Logger.LogWarn("[HKTAS] Preloader target is not executable memory; falling back to native async scene loading.");
                        return false;
                    }
                }

                var targetModule = modules.FirstOrDefault(m =>
                    pointer.ToInt64() >= m.BaseAddress.ToInt64()
                    && pointer.ToInt64() < m.BaseAddress.ToInt64() + m.ModuleMemorySize);
                if (targetModule == null)
                {
                    Modding.Logger.LogWarn("[HKTAS] Dynamic UpdatePreloading pointer is outside loaded module memory; falling back to native async scene loading.");
                    return false;
                }

                var rva = engine != null ? pointer.ToInt64() - engine.BaseAddress.ToInt64() : -1L;
                var code = new byte[24];
                Marshal.Copy(pointer, code, 0, code.Length);
                if (!baselineWarningLogged)
                {
                    var engineMatched = LoadingEngineClockProbe.IsPinnedEngineMatched;
                    var expectedBytes = new byte[] { 0x48,0x83,0xec,0x28,0xe8,0x07,0xba,0x00,0x00,0x48,
                        0x8b,0xc8,0x48,0x83,0xc4,0x28,0xe9,0x0b,0xde,0x00,0x00 };
                    var rvaMatched = engine != null && ReferenceEquals(targetModule, engine) && rva == 0x765c80;
                    var codeMatched = code.Take(21).SequenceEqual(expectedBytes);
                    if (!engineMatched || !rvaMatched || !codeMatched)
                    {
                        baselineWarningLogged = true;
                        Modding.Logger.LogWarn("[HKTAS] DeterministicSceneFetch baseline differs (engineMatch=" + engineMatched
                            + ", rva=0x" + rva.ToString("x") + ", codeMatch=" + codeMatched
                            + "); using dynamic PlayerLoop UpdatePreloading pointer in module " + targetModule.ModuleName + ".");
                    }
                }

                var report = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                if (!string.IsNullOrEmpty(report))
                    System.IO.File.WriteAllText(System.IO.Path.Combine(report, "preloader-pointer.txt"),
                        "slot=" + slot.ToInt64().ToString("x") + " target=" + pointer.ToInt64().ToString("x")
                        + " rva=" + rva.ToString("x") + " code=" + BitConverter.ToString(code).Replace("-", "")
                        + " protection=" + info.Protect.ToString("x"));
                resolved = (NativePreloadUpdate)Marshal.GetDelegateForFunctionPointer(pointer, typeof(NativePreloadUpdate));
                return true;
            }
            catch (Exception ex)
            {
                Modding.Logger.LogWarn("[HKTAS] Dynamic UpdatePreloading pointer resolution failed (" + ex.Message + "); falling back to native async scene loading.");
                return false;
            }
        }

        private AsyncOperation? Pump(AsyncOperation? operation)
        {
            if (fallbackToNativeAsync || operation == null || operation.isDone || operation.progress >= 0.9f) return operation;
            if (pumping || Thread.CurrentThread.ManagedThreadId != thread)
            {
                Modding.Logger.LogWarn("[HKTAS] Scene fetch pump reentered or called off Unity thread; skipping synchronous pump.");
                return operation;
            }
            if (update == null)
            {
                if (!TryResolveUpdatePreloading(out update) || update == null)
                {
                    fallbackToNativeAsync = true;
                    return operation;
                }
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
                operation.allowSceneActivation = false;
                while (operation.progress < 0.9f && !operation.isDone)
                {
                    update();
                    iterations++;
                    if (unchecked(Environment.TickCount - wallStart) > 30000)
                    {
                        Modding.Logger.LogWarn("[HKTAS] Native scene fetch pump exceeded 30 seconds; falling back to native async scene loading.");
                        fallbackToNativeAsync = true;
                        break;
                    }
                    if (operation.progress < 0.9f) Thread.Sleep(1);
                }
                if (Time.frameCount != frame || BitConverter.DoubleToInt64Bits(Time.timeAsDouble) != time
                    || BitConverter.DoubleToInt64Bits(Time.fixedTimeAsDouble) != fixedTime)
                {
                    Modding.Logger.LogWarn("[HKTAS] Preloading advanced engine clock outside PlayerLoop; disabling synchronous pump.");
                    fallbackToNativeAsync = true;
                }
                else if (!fallbackToNativeAsync)
                {
                    Modding.Logger.Log("[HKTAS] Scene fetch completed without advancing clock: frame=" + frame
                        + " iterations=" + iterations + " wallMs=" + unchecked(Environment.TickCount - wallStart));
                    Milestone("scene-fetch-complete");
                }
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

        public void Dispose() => hook?.Dispose();
    }
}
