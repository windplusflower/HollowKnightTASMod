using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Manifest;
using HollowKnightTAS.Runtime.Settings;
using Modding;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Manifest
{
    public static class RuntimeEnvironmentReader
    {
        public static IReadOnlyDictionary<string, string> CaptureLoadedMods()
        {
            HollowKnightTAS.Core.SceneCheckpoints.SceneCheckpointEnvironmentPolicy.ValidateMods(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId,
                ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true).Select(mod => mod.GetName()));
            var loadedMods = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var mod in ModHooks.GetAllMods(
                         onlyEnabled: true,
                         allowLoadError: false))
            {
                var name = mod.GetName();
                string version;
                try
                {
                    version = mod.GetVersion();
                }
                catch (Exception exception)
                {
                    version = "ERROR:" + exception.GetType().Name;
                }

                loadedMods[name] = string.IsNullOrWhiteSpace(version)
                    ? "unknown"
                    : version;
            }

            return loadedMods;
        }

        public static EnvironmentManifest Read(
            TasGlobalSettings settings,
            VerificationPreflightResult preflight,
            IReadOnlyDictionary<string, string> loadedMods,
            string rngCodecId = "not-captured",
            string rngCoverage = "none")
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (preflight == null)
            {
                throw new ArgumentNullException(nameof(preflight));
            }

            if (loadedMods == null)
            {
                throw new ArgumentNullException(nameof(loadedMods));
            }

            var managedDirectory = Path.GetDirectoryName(typeof(GameManager).Assembly.Location);
            if (string.IsNullOrWhiteSpace(managedDirectory))
            {
                throw new InvalidOperationException("Unable to locate the Hollow Knight Managed directory.");
            }

            var assemblyHashes = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["Assembly-CSharp.dll"] = HashRequired(Path.Combine(managedDirectory, "Assembly-CSharp.dll")),
                ["UnityEngine.CoreModule.dll"] = HashRequired(Path.Combine(managedDirectory, "UnityEngine.CoreModule.dll")),
                ["MMHOOK_Assembly-CSharp.dll"] = HashRequired(Path.Combine(managedDirectory, "MMHOOK_Assembly-CSharp.dll")),
                ["PlayMaker.dll"] = HashRequired(Path.Combine(managedDirectory, "PlayMaker.dll")),
                ["HollowKnightTAS.Runtime.dll"] = HashRequired(typeof(HollowKnightTASMod).Assembly.Location),
                ["HollowKnightTAS.Core.dll"] = HashRequired(typeof(EnvironmentManifest).Assembly.Location)
            };

            var gameSettings = GameManager.instance == null
                ? null
                : GameManager.instance.gameSettings;
            var gameVersion = ModHooks.version == null
                ? "unknown"
                : ModHooks.version.GetGameVersionString();

            return new EnvironmentManifest(
                schemaVersion: 2,
                gameVersion: gameVersion,
                moddingApiVersion: string.IsNullOrWhiteSpace(ModHooks.ModVersion)
                    ? "unknown"
                    : ModHooks.ModVersion,
                assemblySha256: assemblyHashes,
                loadedMods: loadedMods,
                tasSettingsSha256: settings.ComputeCanonicalSha256(),
                baselineId: "none",
                baselineSha256: "none",
                operatingSystem: Environment.OSVersion.VersionString,
                architecture: Environment.Is64BitProcess ? "x64" : "x86",
                locale: CultureInfo.CurrentCulture.Name,
                gameLanguage: gameSettings == null
                    ? "unknown"
                    : gameSettings.gameLanguage.ToString(),
                graphicsQualityLevel: QualitySettings.GetQualityLevel(),
                screenWidth: Screen.width,
                screenHeight: Screen.height,
                fullScreenMode: Screen.fullScreenMode.ToString(),
                targetFrameRate: Application.targetFrameRate,
                audioEnabled: gameSettings != null && gameSettings.masterVolume > 0f,
                verificationModeRequested: preflight.Requested,
                verificationModeAllowed: preflight.Allowed,
                unexpectedMods: preflight.UnexpectedMods,
                rngCodecId: rngCodecId,
                rngCoverage: rngCoverage);
        }

        private static string HashRequired(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    "A required fingerprint assembly was not found.",
                    Path.GetFileName(path));
            }

            return Sha256Utility.ComputeFileHex(path);
        }
    }
}
