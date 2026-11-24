using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using InControl;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    // Bridges Modding.Preloader.Preload and early input suppression into ClockPayload
    // so startup preloading can run before HollowKnightTAS.Runtime is constructed.
    public static class EarlyModPreloadBridge
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeSetStartupPreloadActive(int active);

        private delegate IEnumerator OrigPreload(
            object self,
            object toPreload,
            object preloadedObjects,
            object sceneHooks);

        private delegate IEnumerator HookPreload(
            OrigPreload original,
            object self,
            object toPreload,
            object preloadedObjects,
            object sceneHooks);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private static readonly object Sync = new object();
        private static Hook? preloadHook;
        private static int installed;
        private static int preloadActive;
        private static int preloadOccurred;
        private static int earlyInputGuardInstalled;
        private static int suppressInput;
        private static long suppressedUpdates;
        private static bool missingNativeExportWarned;

        public static bool IsInstalled => Volatile.Read(ref installed) != 0;
        public static bool IsPreloadActive => Volatile.Read(ref preloadActive) != 0;
        public static bool PreloadOccurred => Volatile.Read(ref preloadOccurred) != 0;
        public static bool HasEarlyInputGuard => Volatile.Read(ref earlyInputGuardInstalled) != 0;
        public static long SuppressedInputUpdates => Interlocked.Read(ref suppressedUpdates);

        public static void Install()
        {
            lock (Sync)
            {
                if (Volatile.Read(ref installed) != 0)
                {
                    return;
                }

                InstallInputGuard();
                InstallPreloadHook();
                Volatile.Write(ref installed, 1);
                UnityEngine.Debug.Log("[HKTAS] EarlyModPreloadBridge installed (early input guard + Preloader.Preload hook).");
            }
        }

        private static void InstallInputGuard()
        {
            if (Volatile.Read(ref earlyInputGuardInstalled) != 0)
            {
                return;
            }
            if (InputManager.CurrentTick != 0)
            {
                throw new InvalidOperationException("Early mod startup input guard was installed too late.");
            }
            Volatile.Write(ref suppressInput, 1);
            Interlocked.Exchange(ref suppressedUpdates, 0);
            On.InControl.InputManager.UpdateInternal += OnInputUpdate;
            Volatile.Write(ref earlyInputGuardInstalled, 1);
        }

        private static void InstallPreloadHook()
        {
            if (preloadHook != null)
            {
                return;
            }
            var preloaderType = typeof(Modding.ModHooks).Assembly.GetType("Modding.Preloader", false);
            var preloadMethod = preloaderType?.GetMethod(
                "Preload",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (preloadMethod == null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Modding.Preloader.Preload not found; skipping preload lifecycle hook.");
                return;
            }
            preloadHook = new Hook(preloadMethod, (HookPreload)OnPreload);
        }

        private static IEnumerator OnPreload(
            OrigPreload original,
            object self,
            object toPreload,
            object preloadedObjects,
            object sceneHooks)
        {
            var inner = original(self, toPreload, preloadedObjects, sceneHooks);
            if (inner == null)
            {
                return EmptyEnumerator();
            }
            return new PreloadEnumeratorWrapper(inner);
        }

        private static IEnumerator EmptyEnumerator()
        {
            yield break;
        }

        private static void OnInputUpdate(On.InControl.InputManager.orig_UpdateInternal original)
        {
            if (Volatile.Read(ref suppressInput) != 0)
            {
                Interlocked.Increment(ref suppressedUpdates);
                return;
            }
            original();
        }

        public static void ReleaseStartupInputGuard()
        {
            lock (Sync)
            {
                if (Interlocked.Exchange(ref earlyInputGuardInstalled, 0) == 0)
                {
                    return;
                }
                Volatile.Write(ref suppressInput, 0);
                On.InControl.InputManager.UpdateInternal -= OnInputUpdate;
                UnityEngine.Debug.Log("[HKTAS] Early preload input guard released; suppressed updates="
                    + Interlocked.Read(ref suppressedUpdates));
            }
        }

        internal static void BeginPreloadWindow()
        {
            Volatile.Write(ref preloadOccurred, 1);
            Volatile.Write(ref preloadActive, 1);
            SetNativeStartupPreloadActive(true);
            UnityEngine.Debug.Log("[HKTAS] Mod preloading started; native startup clock freeze & timeout paused.");
        }

        internal static void EndPreloadWindow()
        {
            if (Interlocked.Exchange(ref preloadActive, 0) == 0)
            {
                return;
            }
            SetNativeStartupPreloadActive(false);
            UnityEngine.Debug.Log("[HKTAS] Mod preloading finished; native startup clock reset queued.");
        }

        private static void SetNativeStartupPreloadActive(bool active)
        {
            try
            {
                var module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
                if (module == IntPtr.Zero)
                {
                    if (!missingNativeExportWarned)
                    {
                        missingNativeExportWarned = true;
                        UnityEngine.Debug.LogWarning("[HKTAS] ClockBridge native DLL is not loaded; skipping SetStartupPreloadActive.");
                    }
                    return;
                }
                var proc = GetProcAddress(module, "HktasClockBridge_SetStartupPreloadActive");
                if (proc == IntPtr.Zero)
                {
                    if (!missingNativeExportWarned)
                    {
                        missingNativeExportWarned = true;
                        UnityEngine.Debug.LogWarning("[HKTAS] HktasClockBridge_SetStartupPreloadActive export is unavailable; skipping.");
                    }
                    return;
                }
                var fn = Marshal.GetDelegateForFunctionPointer<NativeSetStartupPreloadActive>(proc);
                fn(active ? 1 : 0);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Failed to update native startup preload state: "
                    + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Dispose()
        {
            lock (Sync)
            {
                EndPreloadWindow();
                ReleaseStartupInputGuard();
                preloadHook?.Dispose();
                preloadHook = null;
                Volatile.Write(ref installed, 0);
            }
        }

        private sealed class PreloadEnumeratorWrapper : IEnumerator, IDisposable
        {
            private readonly IEnumerator inner;
            private bool started;
            private bool completed;

            public PreloadEnumeratorWrapper(IEnumerator inner)
            {
                this.inner = inner;
            }

            public object? Current => inner.Current;

            public bool MoveNext()
            {
                if (completed)
                {
                    return false;
                }
                if (!started)
                {
                    started = true;
                    BeginPreloadWindow();
                }

                bool hasNext;
                try
                {
                    hasNext = inner.MoveNext();
                }
                catch
                {
                    completed = true;
                    EndPreloadWindow();
                    throw;
                }

                if (!hasNext)
                {
                    completed = true;
                    EndPreloadWindow();
                }
                return hasNext;
            }

            public void Reset() => inner.Reset();

            public void Dispose()
            {
                try
                {
                    (inner as IDisposable)?.Dispose();
                }
                finally
                {
                    if (started && !completed)
                    {
                        completed = true;
                        EndPreloadWindow();
                    }
                }
            }
        }
    }
}
