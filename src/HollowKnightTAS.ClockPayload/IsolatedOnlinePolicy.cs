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
        private readonly FieldInfo online = typeof(DesktopPlatform).GetField("onlineSubsystem", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("DesktopPlatform.onlineSubsystem");
        private MemoryOnlineSubsystem? adapter;
        private DesktopPlatform? platform;
        private readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private string? failure;

        internal IsolatedOnlinePolicy()
        {
            if (NativeExecutionProfile.SelectedProfileId != NativeExecutionProfile.IsolatedOnlineProfileId
                || Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
                throw new InvalidOperationException("Online isolation requires a V10 TAS process.");
            // BeforeSceneLoad may already have initialized Steam. No game consumer
            // may have subscribed yet: otherwise an account-dependent event could
            // already have affected state and this startup is not eligible.
            var fetched = typeof(Platform).GetField("AchievementsFetched", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingFieldException("Platform.AchievementsFetched");
            if (fetched.GetValue(null) != null)
                throw new InvalidOperationException("Online isolation was installed after achievement consumers.");

            if (Platform.Current != null)
            {
                var desktop = Platform.Current as DesktopPlatform
                    ?? throw new InvalidOperationException("Online isolation supports DesktopPlatform only.");
                var previous = online.GetValue(desktop) as DesktopOnlineSubsystem;
                if (previous != null && !(previous is SteamOnlineSubsystem))
                    throw new InvalidOperationException("Unregistered online subsystem: " + previous.GetType().FullName);
                // Shutdown disconnects this process; it does not reset/store stats.
                previous?.Dispose();
                Attach(desktop);
            }
            Hook(typeof(DesktopPlatform), "CreateOnlineSubsystem", (Action<Action<DesktopPlatform>, DesktopPlatform>)((orig, self) => Attach(self)));
            Hook(typeof(DesktopPlatform), "IsAchievementUnlocked", (Func<Func<DesktopPlatform, string, bool?>, DesktopPlatform, string, bool?>)
                ((orig, self, id) => Read(id)));
            Hook(typeof(DesktopPlatform), "PushAchievementUnlock", (Action<Action<DesktopPlatform, string>, DesktopPlatform, string>)
                ((orig, self, id) => Unlock(id)));
            Hook(typeof(DesktopPlatform), "ResetAchievements", (Action<Action<DesktopPlatform>, DesktopPlatform>)
                ((orig, self) => Reset()));

            Hook(typeof(SteamAPI), "RunCallbacks", (Action<Action>)(orig => CheckThread()));
            Hook(typeof(SteamAPI), "Init", (Func<Func<bool>, bool>)(orig => { CheckThread(); return false; }));
            Hook(typeof(SteamAPI), "Shutdown", (Action<Action>)(orig => CheckThread()));
            Hook(typeof(SteamUserStats), "RequestCurrentStats", (Func<Func<bool>, bool>)(orig => { CheckThread(); return true; }));
            Hook(typeof(SteamUserStats), "StoreStats", (Func<Func<bool>, bool>)(orig => { CheckThread(); return true; }));
            Hook(typeof(SteamUserStats), "SetAchievement", (Func<Func<string, bool>, string, bool>)
                ((orig, id) => { Unlock(id); return true; }));
            Hook(typeof(SteamUserStats), "ClearAchievement", (Func<Func<string, bool>, string, bool>)
                ((orig, id) => { CheckThread(); achievements.Clear(id); return true; }));
            Hook(typeof(SteamUserStats), "ResetAllStats", (Func<Func<bool, bool>, bool, bool>)
                ((orig, resetAchievements) => { CheckThread(); if (resetAchievements) Reset(); return true; }));
            Hook(typeof(SteamUserStats), "GetAchievement", (GetAchievementHook)GetAchievement);
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
            online.SetValue(self, adapter);
            self.OnOnlineSubsystemAchievementsFetched();
        }

        private void Hook(Type type, string name, Delegate replacement)
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                ?? throw new MissingMethodException(type.FullName, name);
            hooks.Add(new Hook(method, replacement));
        }

        internal void Verify()
        {
            // ClockController.Bootstrap also verifies on its native loader
            // worker. Verification observes the installation; it is not an
            // achievement operation and must not latch a thread-order fault.
            if (failure != null) throw new InvalidOperationException(failure);
            if (ReferenceEquals(platform, null) || adapter == null || !ReferenceEquals(online.GetValue(platform), adapter))
                throw new InvalidOperationException("TAS online subsystem was not installed or was replaced.");
        }

        internal string[] Capture() { CheckThread(); Verify(); return achievements.Capture(); }

        internal void SelfTest()
        {
            CheckThread(); Verify();
            const string id = "__HKTAS_V10_ISOLATION_PROBE__";
            if (achievements.Capture().Length != 0)
                throw new InvalidOperationException("Online isolation probe must precede gameplay.");
            var write = typeof(PlayerPrefsSharedData).GetMethod("SetBool")
                ?? throw new MissingMethodException("PlayerPrefsSharedData.SetBool");
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
                    throw new InvalidOperationException("Online isolation initial read failed.");
                if (!SteamUserStats.SetAchievement(id) || platform!.IsAchievementUnlocked(id) != true)
                    throw new InvalidOperationException("Online isolation Steam write routing failed.");
                if (!SteamUserStats.ClearAchievement(id) || platform.IsAchievementUnlocked(id) != false)
                    throw new InvalidOperationException("Online isolation clear routing failed.");
                platform.PushAchievementUnlock(id);
                if (!SteamUserStats.GetAchievement(id, out unlocked) || !unlocked || !SteamUserStats.StoreStats())
                    throw new InvalidOperationException("Online isolation Desktop write routing failed.");
                if (!SteamUserStats.ResetAllStats(true) || achievements.Capture().Length != 0)
                    throw new InvalidOperationException("Online isolation reset routing failed.");
                SteamAPI.RunCallbacks();
            }
            UnityEngine.Debug.Log("[HKTAS] V10 online isolation probe passed: Steam/Desktop memory reads and writes; local persistence guarded; empty state restored.");
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
