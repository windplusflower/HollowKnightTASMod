using System;
using System.IO;
using System.Text.Json;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests
{
    [TestClass]
    public sealed class VerifiedStartupProfileIdentityTests
    {
        [TestMethod]
        [DataRow("profile")]
        [DataRow("policy")]
        [DataRow("seed")]
        [DataRow("current")]
        public void IdentityRejectsMixedScenePoliciesBeforeRuntimeFileValidation(string variant)
        {
            var root = Path.Combine(Path.GetTempPath(), "hktas-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var game = Path.Combine(root, "hollow_knight.exe");
                File.WriteAllBytes(game, Array.Empty<byte>());
                File.WriteAllText(Path.Combine(root, "clock-build-whitelist-v1.json"), "{}");
                File.WriteAllText(Path.Combine(root, "clock-build-manifest-v1.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    capabilityId = VerifiedStartupProfile.CapabilityId,
                    profile = variant == "profile"
                        ? "external-unity-startup-continuous-clock-v39-post-root-request-first"
                        : StartupProfileContract.ProfileId,
                    startupPolicy = VerifiedStartupProfile.StartupPolicy,
                    bridgeAbi = StartupProfileContract.BridgeAbi,
                    startupFrameGateAbi = 1,
                    randomSynchronizationPolicy = variant == "policy"
                        ? "unity-init-state-at-root-post-root-request-first-and-activation-finish-aligned-scene-boundaries-v18"
                        : StartupProfileContract.RandomSynchronizationPolicyId,
                    randomSynchronizationSeed = StartupProfileContract.RandomSynchronizationSeed + (variant == "seed" ? 1 : 0)
                    // Deliberately omit game metadata and optional runtime files:
                    // missing core startup binaries must fail, while manifest mismatches only warn.
                }));

                Assert.ThrowsExactly<FileNotFoundException>(() => VerifiedStartupProfile.Load(root, game));

                File.WriteAllBytes(Path.Combine(root, "HollowKnightTAS.ClockInjector.exe"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(root, "HollowKnightTAS.ClockBridge.dll"), new byte[] { 2 });
                File.WriteAllBytes(Path.Combine(root, "HollowKnightTAS.ClockPayload.dll"), new byte[] { 3 });

                var loaded = VerifiedStartupProfile.Load(root, game);
                Assert.IsNotNull(loaded);
                Assert.AreEqual(Path.GetFullPath(root), loaded.BundleRoot);
                Assert.AreEqual(Path.GetFullPath(game), loaded.GameExecutablePath);
                Assert.AreEqual(
                    Path.Combine(Path.GetFullPath(root), "HollowKnightTAS.ClockInjector.exe"),
                    loaded.InjectorPath);
                Assert.AreEqual(
                    Path.Combine(Path.GetFullPath(root), "clock-build-manifest-v1.json"),
                    loaded.ManifestPath);
                Assert.AreEqual(64, loaded.StartupProfileSha256.Length);
                Assert.AreEqual(64, loaded.GameExecutableSha256.Length);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [TestMethod]
        [DataRow("hollow_knight.exe")]
        [DataRow("HollowKnightTAS.ClockInjector.exe")]
        [DataRow("HollowKnightTAS.ClockBridge.dll")]
        [DataRow("HollowKnightTAS.ClockPayload.dll")]
        public void LoadRequiresAllCoreStartupFiles(string missingFile)
        {
            var root = Path.Combine(Path.GetTempPath(), "hktas-profile-core-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var game = Path.Combine(root, "hollow_knight.exe");
                foreach (var file in new[]
                         {
                             "hollow_knight.exe",
                             "HollowKnightTAS.ClockInjector.exe",
                             "HollowKnightTAS.ClockBridge.dll",
                             "HollowKnightTAS.ClockPayload.dll"
                         })
                {
                    if (!string.Equals(file, missingFile, StringComparison.Ordinal))
                    {
                        File.WriteAllBytes(Path.Combine(root, file), new byte[] { 1 });
                    }
                }

                Assert.ThrowsExactly<FileNotFoundException>(() => VerifiedStartupProfile.Load(root, game));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
