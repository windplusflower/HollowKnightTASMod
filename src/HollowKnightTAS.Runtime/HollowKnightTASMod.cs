using System;
using System.Collections.Generic;
using System.IO;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Runtime.ReplaySave;
using HollowKnightTAS.Runtime.FullRun;
using HollowKnightTAS.Runtime.Timing;
using HollowKnightTAS.Runtime.Runtime;
using HollowKnightTAS.Runtime.Settings;
using Modding;
using Modding.Menu;
using Modding.Menu.Config;
using HollowKnightTAS.Core.Ipc;
using HollowKnightTAS.Runtime.Companion;
using HollowKnightTAS.Runtime.Localization;
using UnityEngine;

namespace HollowKnightTAS.Runtime
{
    public sealed class HollowKnightTASMod :
        Mod,
        IGlobalSettings<TasGlobalSettings>,
        ICustomMenuMod
    {
        public const string Version = "1.0.0";

        private TasGlobalSettings? settings;
        private TasRuntimeHost? runtimeHost;
        private ProtectedSaveRedirector? protectedSaves;
        private RuntimeFullRunSession? fullRunSession;
        private bool hooksRegistered;
        private ManualStartupService? manualStartup;
        private ModStartupCompatibility? modStartup;
        private enum StartupMessageKind
        {
            Default,
            AlreadyControlled,
            NeedTitle,
            Opening,
            IncompleteRetry,
            FailedRetry
        }

        private StartupMessageKind startupMessageKind = StartupMessageKind.Default;
        private UnityEngine.UI.Text? startupLabel;

        public HollowKnightTASMod()
            : base("HollowKnightTAS")
        {
            settings ??= new TasGlobalSettings();
            settings.Normalize();
            if (SavePathResolver.ProtectionRequested)
            {
                try { modStartup = new ModStartupCompatibility(); }
                catch (Exception error)
                {
                    if (!NativeFullRunFrameClock.TryFaultEarly(43))
                        Environment.FailFast("TAS mod startup guard failed.", error);
                    throw;
                }
            }
        }

        public override string GetVersion()
        {
            return Version;
        }

        public override void Initialize(
            Dictionary<string, Dictionary<string, GameObject>> preloadedObjects)
        {
            if (hooksRegistered)
            {
                LogWarn("T01 Initialize called more than once; duplicate registration was ignored.");
                return;
            }

            if (SavePathResolver.ProtectionRequested)
            {
                try
                {
                    if (Environment.GetEnvironmentVariable("HKTAS_FULL_RUN_V2") != "1")
                        throw new InvalidOperationException("Protected launch is missing its v2 frame gate.");
                    var descriptorPath = Environment.GetEnvironmentVariable("HKTAS_SAVE_DESCRIPTOR_PATH");
                    if (string.IsNullOrWhiteSpace(descriptorPath))
                        throw new InvalidOperationException("Protected save descriptor path is missing.");
                    var fullPath = Path.GetFullPath(descriptorPath);
                    var size = new FileInfo(fullPath).Length;
                    if (size <= 0 || size > ProtectedSaveDescriptorCodec.MaximumBytes)
                        throw new InvalidDataException("Protected save descriptor size is invalid.");
                    var descriptor = ProtectedSaveDescriptorCodec.Parse(File.ReadAllBytes(fullPath));
                    if (!string.Equals(fullPath,
                            Path.Combine(descriptor.ShadowRoot, "descriptor.json"),
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Protected save descriptor path does not match its shadow root.");
                    protectedSaves = ProtectedSaveRedirector.Install(descriptor,
                        message => Log(message));
                }
                catch (Exception exception)
                {
                    LogError("Protected full-run bootstrap failed: " + exception);
                    if (!NativeFullRunFrameClock.TryFaultEarly(43))
                        Environment.FailFast("Protected full-run bootstrap could not stop the native frame gate.",
                            exception);
                    throw;
                }
            }

            hooksRegistered = true;

            ModHooks.FinishedLoadingModsHook += OnFinishedLoadingMods;
            ModHooks.ApplicationQuitHook += OnApplicationQuit;
            Log("T01 hooks registered.");
        }

        public void OnLoadGlobal(TasGlobalSettings loadedSettings)
        {
            settings = loadedSettings ?? new TasGlobalSettings();
            settings.Normalize();
        }

        public TasGlobalSettings OnSaveGlobal()
        {
            settings ??= new TasGlobalSettings();
            settings.Normalize();
            return settings;
        }

        public bool ToggleButtonInsideMenu => false;

        public MenuScreen GetMenuScreen(MenuScreen modListMenu, ModToggleDelegates? toggleDelegates)
        {
            var builder = MenuUtils.CreateMenuBuilderWithBackButton("HollowKnightTAS", modListMenu, out _);
            builder.AddContent(RegularGridLayout.CreateVerticalLayout(150f), content =>
            {
                content.AddMenuButton("OpenStudio", new MenuButtonConfig
                {
                    Label = FormatStartupMessage(startupMessageKind),
                    Style = MenuButtonStyle.VanillaStyle,
                    SubmitAction = _ => OpenStudio(),
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(modListMenu),
                    Description = new DescriptionInfo
                    {
                        Text = RuntimeUiText.T(
                            "仅标题界面可用；重启后暂停在第 0 帧。",
                            "Title screen only; restarts paused at frame 0.")
                    }
                }, out var button);
                startupLabel = button.GetComponentInChildren<UnityEngine.UI.Text>();
            });
            return builder.Build();
        }

        private static string FormatStartupMessage(StartupMessageKind kind)
        {
            return kind switch
            {
                StartupMessageKind.AlreadyControlled => RuntimeUiText.T(
                    "当前已是受控游戏",
                    "Already running in controlled mode"),
                StartupMessageKind.NeedTitle => RuntimeUiText.T(
                    "请先保存退出到标题，再打开 Studio",
                    "Save & quit to title screen before opening Studio"),
                StartupMessageKind.Opening => RuntimeUiText.T(
                    "正在打开 Studio，请稍候…",
                    "Opening Studio, please wait..."),
                StartupMessageKind.IncompleteRetry => RuntimeUiText.T(
                    "启动未完成，点击重试",
                    "Launch incomplete, click to retry"),
                StartupMessageKind.FailedRetry => RuntimeUiText.T(
                    "启动失败，点击重试",
                    "Launch failed, click to retry"),
                _ => RuntimeUiText.T(
                    "打开 Studio（重启游戏）",
                    "Open Studio (Restart Game)")
            };
        }

        private void SetStartupMessage(StartupMessageKind kind)
        {
            startupMessageKind = kind;
            if (startupLabel != null) startupLabel.text = FormatStartupMessage(kind);
        }

        private void OpenStudio()
        {
            if (manualStartup != null) return;
            if (runtimeHost != null
                || fullRunSession != null
                || StartupActivationPolicy.ShouldStartRuntime(SavePathResolver.ProtectionRequested,
                    Environment.GetEnvironmentVariable("HKTAS_CLOCK_STARTUP_LATCH")))
            {
                SetStartupMessage(StartupMessageKind.AlreadyControlled);
                return;
            }
            if (!ManualStartupService.IsStableTitle())
            {
                SetStartupMessage(StartupMessageKind.NeedTitle);
                return;
            }
            try
            {
                SetStartupMessage(StartupMessageKind.Opening);
                manualStartup = new ManualStartupService(message => Log(message), message => LogWarn(message),
                    message => LogError(message), reason =>
                    {
                        manualStartup?.Dispose();
                        manualStartup = null;
                        SetStartupMessage(StartupMessageKind.IncompleteRetry);
                        LogWarn(reason);
                    });
                manualStartup.Start();
            }
            catch (Exception error)
            {
                manualStartup?.Dispose();
                manualStartup = null;
                SetStartupMessage(StartupMessageKind.FailedRetry);
                LogError("Manual Studio launch failed: " + error);
            }
        }

        private void OnFinishedLoadingMods()
        {
            try
            {
                _ = RuntimeUiText.CurrentLanguageCode();
                if (SavePathResolver.ProtectionRequested)
                {
                    if (protectedSaves == null)
                        throw new InvalidOperationException("Protected save redirector was not initialized before FinishedLoadingMods.");

                    if (ModStartupCompatibility.TryQueryPreloadOccurred())
                    {
                        if (ModStartupCompatibility.TryQueryIsPreloadActive())
                            throw new InvalidOperationException("Mod preloading was still active when FinishedLoadingModsHook ran.");
                        NormalizeBaselineAfterPreload();
                    }

                    var token = Environment.GetEnvironmentVariable("HKTAS_BOOT_GATE_TOKEN")
                        ?? throw new InvalidOperationException("Full-run gate token is missing.");
                    var clock = NativeFullRunFrameClock.Attach(token,
                        HollowKnightTAS.Core.Movie.MovieProtocolV2.SelectedNativeProfileId);
                    fullRunSession = RuntimeFullRunBootstrap.Attach(token, clock,
                        protectedSaves, message => Log(message));
                    modStartup?.FinishPreloading();
                    StartupObjectPoolRegistry.CloseRegistrationWindow();
                }

                // A normal Steam launch must not create any TAS services or gameplay hooks.
                if (runtimeHost == null && StartupActivationPolicy.ShouldStartRuntime(
                        SavePathResolver.ProtectionRequested,
                        Environment.GetEnvironmentVariable("HKTAS_CLOCK_STARTUP_LATCH")))
                {
                    runtimeHost = new TasRuntimeHost(
                        settings ?? new TasGlobalSettings(),
                        message => Log(message),
                        message => LogDebug(message),
                        message => LogWarn(message),
                        message => LogError(message),
                        fullRunSession);
                }

                runtimeHost?.Start();
            }
            catch (Exception exception)
            {
                LogError("T01 runtime host failed to start: " + exception);
                if (SavePathResolver.ProtectionRequested || fullRunSession != null)
                {
                    if (!NativeFullRunFrameClock.TryFaultEarly(44))
                        Environment.FailFast("Protected full-run startup could not stop the native frame gate.",
                            exception);
                    throw;
                }
            }
        }

        private void NormalizeBaselineAfterPreload()
        {
            UnityEngine.Random.InitState(1212896321);
            ModStartupCompatibility.TryRebaselineParticlesAfterPreload();
            if (AudioListener.pause)
            {
                AudioListener.pause = false;
                LogWarn("Reset AudioListener.pause=false after mod preloading.");
            }
            Log("[HKTAS] Post-preload baseline normalized (UnityEngine.Random, particles, AudioListener).");
        }

        private void OnApplicationQuit()
        {
            try
            {
                manualStartup?.Dispose();
                manualStartup = null;
                runtimeHost?.Stop("application-quit");
            }
            finally
            {
                UnregisterHooks();
            }
        }

        private void UnregisterHooks()
        {
            if (!hooksRegistered)
            {
                return;
            }

            hooksRegistered = false;
            ModHooks.FinishedLoadingModsHook -= OnFinishedLoadingMods;
            ModHooks.ApplicationQuitHook -= OnApplicationQuit;
            runtimeHost = null;
            fullRunSession?.Dispose();
            fullRunSession = null;
            protectedSaves?.Dispose();
            protectedSaves = null;
            modStartup?.Dispose();
            modStartup = null;
            StartupObjectPoolRegistry.UninstallHooks();
            LogDebug("T01 hooks unregistered.");
        }
    }
}
