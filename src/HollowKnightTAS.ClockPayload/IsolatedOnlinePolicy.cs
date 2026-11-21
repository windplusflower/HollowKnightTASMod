using System;
using System.Collections.Generic;
using System.Reflection;
using HollowKnightTAS.Core.Movie;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using Steamworks;

namespace HollowKnightTAS.ClockPayload
{
    // Installed before the first PlayerLoop. Only the explicitly selected V10 TAS
    // process owns these detours; no configuration or account state is persisted.
    internal sealed class IsolatedOnlinePolicy : IDisposable
    {
        private readonly List<IDisposable> hooks = new List<IDisposable>();
        private readonly SessionAchievements achievements = new SessionAchievements();
        private FieldInfo? online;
        private MemoryOnlineSubsystem? adapter;
        private DesktopPlatform? platform;
        private readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private string? failure;
        private bool skipped;
        private string skipReason = string.Empty;
        private bool skipWarned;

        internal IsolatedOnlinePolicy()
        {
            try
            {
                InstallHooks();
            }
            catch (Exception exception)
            {
                MarkSkipped(exception.GetType().Name + ": " + exception.Message);
            }
        }

        private void MarkSkipped(string reason)
        {
            skipped = true;
            skipReason = reason;
            failure = null;
            Dispose();
            platform = null;
            adapter = null;
            if (!skipWarned)
            {
                skipWarned = true;
                UnityEngine.Debug.LogWarning("[HKTAS] Online isolation skipped: " + reason);
            }
        }

        private void InstallHooks()
        {
            if (!NativeExecutionProfile.UsesIsolatedOnline(NativeExecutionProfile.SelectedProfileId)
                || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
            {
                MarkSkipped("Online isolation requires a V10 TAS process.");
                return;
            }

            online = typeof(DesktopPlatform).GetField("onlineSubsystem", BindingFlags.Instance | BindingFlags.NonPublic);
            if (online == null)
            {
                MarkSkipped("Missing DesktopPlatform.onlineSubsystem field.");
                return;
            }

            // BeforeSceneLoad may already have initialized Steam. No game consumer
            // may have subscribed yet: otherwise an account-dependent event could
            // already have affected state and this startup is not eligible.
            var fetched = typeof(Platform).GetField("AchievementsFetched", BindingFlags.Static | BindingFlags.NonPublic);
            if (fetched == null)
            {
                MarkSkipped("Missing Platform.AchievementsFetched field.");
                return;
            }
            if (fetched.GetValue(null) != null)
            {
                MarkSkipped("Platform.AchievementsFetched already has subscribers.");
                return;
            }

            if (Platform.Current != null)
            {
                if (!(Platform.Current is DesktopPlatform desktop))
                {
                    MarkSkipped("Current platform is not DesktopPlatform (" + Platform.Current.GetType().FullName + ").");
                    return;
                }
                var previous = online.GetValue(desktop) as DesktopOnlineSubsystem;
                if (previous != null && !(previous is SteamOnlineSubsystem))
                {
                    MarkSkipped("Unsupported online subsystem: " + previous.GetType().FullName + ".");
                    return;
                }
                // Shutdown disconnects this process; it does not reset/store stats.
                previous?.Dispose();
                Attach(desktop);
            }

            if (!TryHook(typeof(DesktopPlatform), "CreateOnlineSubsystem", (Action<Action<DesktopPlatform>, DesktopPlatform>)((orig, self) => Attach(self)))
                || !TryHook(typeof(DesktopPlatform), "IsAchievementUnlocked", (Func<Func<DesktopPlatform, string, bool?>, DesktopPlatform, string, bool?>)((orig, self, id) => Read(id)))
                || !TryHook(typeof(DesktopPlatform), "PushAchievementUnlock", (Action<Action<DesktopPlatform, string>, DesktopPlatform, string>)((orig, self, id) => Unlock(id)))
                || !TryHook(typeof(DesktopPlatform), "ResetAchievements", (Action<Action<DesktopPlatform>, DesktopPlatform>)((orig, self) => Reset()))
                || !TryHook(typeof(SteamAPI), "RunCallbacks", (Action<Action>)(orig => CheckThread()))
                || !TryHook(typeof(SteamAPI), "Init", (Func<Func<bool>, bool>)(orig => { CheckThread(); return false; }))
                || !TryHook(typeof(SteamAPI), "Shutdown", (Action<Action>)(orig => CheckThread()))
                || !TryHook(typeof(SteamUserStats), "RequestCurrentStats", (Func<Func<bool>, bool>)(orig => { CheckThread(); return true; }))
                || !TryHook(typeof(SteamUserStats), "StoreStats", (Func<Func<bool>, bool>)(orig => { CheckThread(); return true; }))
                || !TryHook(typeof(SteamUserStats), "SetAchievement", (Func<Func<string, bool>, string, bool>)((orig, id) => { Unlock(id); return true; }))
                || !TryHook(typeof(SteamUserStats), "ClearAchievement", (Func<Func<string, bool>, string, bool>)((orig, id) => { CheckThread(); achievements.Clear(id); return true; }))
                || !TryHook(typeof(SteamUserStats), "ResetAllStats", (Func<Func<bool, bool>, bool, bool>)((orig, resetAchievements) => { CheckThread(); if (resetAchievements) Reset(); return true; }))
                || !TryHook(typeof(SteamUserStats), "GetAchievement", (GetAchievementHook)GetAchievement))
            {
                return;
            }

            var supported = new HashSet<string>(StringComparer.Ordinal) { "RequestCurrentStats", "StoreStats",
                "SetAchievement", "ClearAchievement", "ResetAllStats", "GetAchievement" };
            foreach (var method in typeof(SteamUserStats).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (supported.Contains(method.Name)) continue;
                hooks.Add(new ILHook(method, il =>
                {
                    il.Body.Instructions.Clear();
                    il.Body.ExceptionHandlers.Clear();
                    il.Body.Variables.Clear();
                    var cursor = new ILCursor(il);
                    cursor.EmitDelegate<Func<Exception>>(() => Unsupported(method.Name));
                    cursor.Emit(OpCodes.Throw);
                }));
            }
        }

        private delegate bool GetAchievementOriginal(string id, out bool unlocked);
        private delegate bool GetAchievementHook(GetAchievementOriginal original, string id, out bool unlocked);
        private bool GetAchievement(GetAchievementOriginal original, string id, out bool unlocked)
        { unlocked = Read(id); return true; }

        private void CheckThread()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread)
                throw Unsupported("background-thread");
        }
        private Exception Unsupported(string operation)
        {
            failure = "Unregistered online operation in TAS: " + operation;
            return new InvalidOperationException(failure);
        }
        private bool Read(string id) { CheckThread(); return achievements.IsUnlocked(id); }
        private void Unlock(string id) { CheckThread(); achievements.Unlock(id); }
        private void Reset() { CheckThread(); achievements.Reset(); }

        private void Attach(DesktopPlatform self)
        {
            CheckThread();
            if (failure != null) throw new InvalidOperationException(failure);
            if (platform != null) throw new InvalidOperationException("TAS platform initialized more than once.");
            platform = self;
            adapter = new MemoryOnlineSubsystem(achievements);
            online?.SetValue(self, adapter);
            self.OnOnlineSubsystemAchievementsFetched();
        }

        private bool TryHook(Type type, string name, Delegate replacement)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (method == null)
            {
                MarkSkipped("Missing method " + type.FullName + "." + name);
                return false;
            }
            hooks.Add(new Hook(method, replacement));
            return true;
        }

        internal void Verify()
        {
            if (skipped)
            {
                if (!skipWarned)
                {
                    skipWarned = true;
                    UnityEngine.Debug.LogWarning("[HKTAS] Online isolation verification skipped: " + skipReason);
                }
                return;
            }
            if (failure != null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Online isolation verification warning: " + failure);
                return;
            }
            if (ReferenceEquals(platform, null) || adapter == null || online == null || !ReferenceEquals(online.GetValue(platform), adapter))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Online isolation subsystem was not installed or was replaced; skipping strict verification.");
            }
        }

        internal string[] Capture()
        {
            if (skipped)
            {
                return Array.Empty<string>();
            }
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread || failure != null || ReferenceEquals(platform, null))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Online isolation capture unavailable on current state; returning empty achievements.");
                return Array.Empty<string>();
            }
            return achievements.Capture();
        }

        internal void SelfTest()
        {
            if (skipped)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping V10 online isolation probe because online isolation was skipped: " + skipReason);
                return;
            }
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread || failure != null
                || ReferenceEquals(platform, null) || adapter == null || online == null
                || !ReferenceEquals(online.GetValue(platform), adapter))
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping V10 online isolation probe because online isolation is not active.");
                return;
            }
            const string id = "__HKTAS_V10_ISOLATION_PROBE__";
            if (achievements.Capture().Length != 0)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping V10 online isolation probe because achievements are non-empty.");
                return;
            }
            var write = typeof(PlayerPrefsSharedData).GetMethod("SetBool");
            if (write == null)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] Skipping V10 online isolation probe: PlayerPrefsSharedData.SetBool not found.");
                return;
            }
            try
            {
                // A broken DesktopPlatform detour must not let this synthetic probe
                // touch PlayerPrefs. The temporary second guard fails before writing.
                using (var guard = new Hook(write,
                    (Action<Action<PlayerPrefsSharedData, string, bool>, PlayerPrefsSharedData, string, bool>)
                    ((original, data, key, value) =>
                    {
                        if (key == id) throw new InvalidOperationException("Online isolation leaked a local achievement write.");
                        original(data, key, value);
                    })))
                {
                    if (!SteamUserStats.GetAchievement(id, out var unlocked) || unlocked)
                    {
                        UnityEngine.Debug.LogWarning("[HKTAS] Online isolation initial read check failed.");
                        return;
                    }
                    if (!SteamUserStats.SetAchievement(id) || platform!.IsAchievementUnlocked(id) != true)
                    {
                        UnityEngine.Debug.LogWarning("[HKTAS] Online isolation Steam write routing check failed.");
                        return;
                    }
                    if (!SteamUserStats.ClearAchievement(id) || platform.IsAchievementUnlocked(id) != false)
                    {
                        UnityEngine.Debug.LogWarning("[HKTAS] Online isolation clear routing check failed.");
                        return;
                    }
                    platform.PushAchievementUnlock(id);
                    if (!SteamUserStats.GetAchievement(id, out unlocked) || !unlocked || !SteamUserStats.StoreStats())
                    {
                        UnityEngine.Debug.LogWarning("[HKTAS] Online isolation Desktop write routing check failed.");
                        return;
                    }
                    if (!SteamUserStats.ResetAllStats(true) || achievements.Capture().Length != 0)
                    {
                        UnityEngine.Debug.LogWarning("[HKTAS] Online isolation reset routing check failed.");
                        return;
                    }
                    SteamAPI.RunCallbacks();
                }
                UnityEngine.Debug.Log("[HKTAS] V10 online isolation probe passed: Steam/Desktop memory reads and writes; local persistence guarded; empty state restored.");
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning("[HKTAS] V10 online isolation probe skipped/failed safely: " + exception.Message);
            }
        }
        public void Dispose()
        {
            for (var i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
            hooks.Clear();
        }

        private sealed class MemoryOnlineSubsystem : DesktopOnlineSubsystem
        {
            private readonly SessionAchievements state;
            internal MemoryOnlineSubsystem(SessionAchievements state) { this.state = state; }
            public override bool AreAchievementsFetched => true;
            public override bool? IsAchievementUnlocked(string id) => state.IsUnlocked(id);
            public override void PushAchievementUnlock(string id) => state.Unlock(id);
            public override void ResetAchievements() => state.Reset();
        }
    }
}
