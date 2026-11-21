using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HollowKnightTAS.Core.Movie;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    // V5's first registered Mod adapter. Install before its static root creates
    // SwapManager; reject a late install instead of rewriting an existing timer.
    // Only these two pinned call sites change. BCL clocks remain untouched.
    internal sealed class CustomKnightModWallClockPolicy : IDisposable
    {
        private const string AssemblyName = "CustomKnight";
        private const string AssemblySha256 = "e23d2360f844099093ad84ff3687176c84dc6b4df6f2fcc2ca5fb15cf13098da";
        private readonly List<ILHook> hooks = new List<ILHook>();
        private Assembly? target;
        private string? failure;
        private bool installed;
        private bool skipped;
        private int constructorReads;
        private int nonEngineThreadWarned;
        private readonly uint engineThread;

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        internal CustomKnightModWallClockPolicy()
        {
            engineThread = GetCurrentThreadId();
            if (engineThread == 0 || engineThread != ClockController.StartupHookThreadId)
            {
                skipped = true;
                failure = "V5 Mod wall clocks constructed off the registered engine thread.";
                UnityEngine.Debug.LogWarning("[HKTAS] " + failure + " Skipping CustomKnight wall-clock policy.");
                return;
            }
            AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetName().Name == AssemblyName) SafeInstall(assembly);
        }

        private void OnAssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (args.LoadedAssembly.GetName().Name != AssemblyName) return;
            SafeInstall(args.LoadedAssembly);
        }

        private void SafeInstall(Assembly assembly)
        {
            try
            {
                Install(assembly);
            }
            catch (Exception error)
            {
                failure = error.GetType().Name + ": " + error.Message;
                skipped = true;
                installed = false;
                DisposeHooks();
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight wall-clock hook skipped due to error: " + failure);
            }
        }

        private void Install(Assembly assembly)
        {
            if (target != null)
            {
                if (!ReferenceEquals(target, assembly))
                    UnityEngine.Debug.LogWarning("[HKTAS] Multiple CustomKnight assemblies detected; ignoring duplicate assembly.");
                return;
            }
            target = assembly;
            if (!string.IsNullOrWhiteSpace(assembly.Location) && File.Exists(assembly.Location))
            {
                string digest;
                using (var file = File.OpenRead(assembly.Location))
                using (var hash = SHA256.Create())
                    digest = BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
                if (digest != AssemblySha256)
                {
                    UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.dll SHA-256 differs from baseline (" + digest + "); attempting reflection-based wall-clock hook.");
                }
            }
            else
            {
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.dll file path unavailable; attempting reflection-based wall-clock hook.");
            }

            var type = assembly.GetType("CustomKnight.SwapManager", throwOnError: false);
            if (type == null)
            {
                skipped = true;
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.SwapManager type not found; skipping wall-clock hook.");
                return;
            }

            var constructor = type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            var update = type.GetMethod("checkForMissedObjects", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            if (constructor == null || update == null || update.ReturnType != typeof(void))
            {
                skipped = true;
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.SwapManager target methods or signatures differ; skipping wall-clock hook.");
                return;
            }

            var ctorMatched = false;
            var updateMatched = false;
            var ctorHook = new ILHook(constructor, il => ctorMatched = ReplaceNow(il, true, "SwapManager..ctor"));
            if (!ctorMatched)
            {
                ctorHook.Dispose();
                skipped = true;
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.SwapManager..ctor IL shape differs (no DateTime.Now call); skipping wall-clock hook.");
                return;
            }
            hooks.Add(ctorHook);

            var updateHook = new ILHook(update, il => updateMatched = ReplaceNow(il, false, "SwapManager.checkForMissedObjects"));
            if (!updateMatched)
            {
                updateHook.Dispose();
                DisposeHooks();
                skipped = true;
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight.SwapManager.checkForMissedObjects IL shape differs (no DateTime.Now call); skipping wall-clock hook.");
                return;
            }
            hooks.Add(updateHook);
            installed = true;
        }

        private bool ReplaceNow(ILContext il, bool constructor, string methodName)
        {
            var cursor = new ILCursor(il);
            var calls = 0;
            while (cursor.TryGotoNext(MoveType.Before, instruction => instruction.MatchCall(typeof(DateTime), "get_Now")))
            {
                // Preserve the original instruction because a branch can target
                // the DateTime.Now call (the pinned update method does this).
                cursor.Next.OpCode = OpCodes.Nop;
                cursor.Next.Operand = null;
                cursor.Index++;
                cursor.EmitDelegate<Func<DateTime>>(() => ReadNow(constructor));
                calls++;
            }
            if (calls == 0) return false;
            if (calls != 1)
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight " + methodName + " has " + calls + " DateTime.Now call(s) (expected 1); adapted all calls.");
            return true;
        }

        private DateTime ReadNow(bool constructor)
        {
            if (GetCurrentThreadId() != engineThread)
            {
                if (Interlocked.Exchange(ref nonEngineThreadWarned, 1) == 0)
                    UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight wall-clock read occurred off the engine thread; falling back to DateTime.Now.");
                return DateTime.Now;
            }
            var result = ModWallClock.LocalNow(Time.realtimeSinceStartupAsDouble);
            if (constructor) Interlocked.Increment(ref constructorReads);
            return result;
        }

        internal void Verify()
        {
            if (failure != null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight wall-clock verification skipped: " + failure);
                return;
            }
            if (skipped || target == null) return;
            if (!installed || Volatile.Read(ref constructorReads) < 1)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] CustomKnight wall-clock verification condition not met (installed="
                    + installed + ", constructorReads=" + Volatile.Read(ref constructorReads) + "); continuing without strict enforcement.");
            }
        }

        private void DisposeHooks()
        {
            for (var index = hooks.Count - 1; index >= 0; index--) hooks[index].Dispose();
            hooks.Clear();
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
            DisposeHooks();
        }
    }
}
