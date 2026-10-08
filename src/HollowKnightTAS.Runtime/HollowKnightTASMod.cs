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
using UnityEngine;

namespace HollowKnightTAS.Runtime
{
    public sealed class HollowKnightTASMod :
        Mod,
        IGlobalSettings<TasGlobalSettings>,
        ICustomMenuMod
    {
        public const string Version = "0.1.8";

        private TasGlobalSettings? settings;
        private TasRuntimeHost? runtimeHost;
        private ProtectedSaveRedirector? protectedSaves;
        private RuntimeFullRunSession? fullRunSession;
        private bool hooksRegistered;
        private ManualStartupService? manualStartup;
        private ModStartupCompatibility? modStartup;
        private string startupMessage = "打开 Studio（重启游戏）";
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
                    var token = Environment.GetEnvironmentVariable("HKTAS_BOOT_GATE_TOKEN")
                        ?? throw new InvalidOperationException("Full-run gate token is missing.");
                    var clock = NativeFullRunFrameClock.Attach(token,
                        HollowKnightTAS.Core.Movie.MovieProtocolV2.NativeProfileId);
                    fullRunSession = RuntimeFullRunBootstrap.Attach(token, clock,
                        protectedSaves, message => Log(message));
                    modStartup?.FinishPreloading();
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
            // A normal Steam launch must not create any TAS services or gameplay hooks.
            if (StartupActivationPolicy.ShouldStartRuntime(SavePathResolver.ProtectionRequested,
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
                    Label = startupMessage,
                    Style = MenuButtonStyle.VanillaStyle,
                    SubmitAction = _ => OpenStudio(),
                    CancelAction = _ => UIManager.instance.UIGoToDynamicMenu(modListMenu),
                    Description = new DescriptionInfo { Text = "仅标题界面可用；重启后暂停在第 0 帧。" }
                }, out var button);
                startupLabel = button.GetComponentInChildren<UnityEngine.UI.Text>();
            });
            return builder.Build();
        }

        private void SetStartupMessage(string message)
        {
            startupMessage = message;
            if (startupLabel != null) startupLabel.text = message;
        }

        private void OpenStudio()
        {
            if (manualStartup != null) return;
            if (runtimeHost != null)
            {
                SetStartupMessage("当前已是受控游戏");
                return;
            }
            if (!ManualStartupService.IsStableTitle())
            {
                SetStartupMessage("请先保存退出到标题，再打开 Studio");
                return;
            }
            try
            {
                SetStartupMessage("正在打开 Studio，请稍候…");
                manualStartup = new ManualStartupService(message => Log(message), message => LogWarn(message),
                    message => LogError(message), reason =>
                    {
                        manualStartup?.Dispose();
                        manualStartup = null;
                        SetStartupMessage("启动未完成，点击重试");
                        LogWarn(reason);
                    });
                manualStartup.Start();
            }
            catch (Exception error)
            {
                manualStartup?.Dispose();
                manualStartup = null;
                SetStartupMessage("启动失败，点击重试");
                LogError("Manual Studio launch failed: " + error);
            }
        }

        private void OnFinishedLoadingMods()
        {
            try
            {
                runtimeHost?.Start();
            }
            catch (Exception exception)
            {
                LogError("T01 runtime host failed to start: " + exception);
                if (fullRunSession != null)
                {
                    NativeFullRunFrameClock.TryFaultEarly(44);
                    throw;
                }
            }
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
            LogDebug("T01 hooks unregistered.");
        }
    }
}
