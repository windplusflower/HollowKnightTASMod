using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Rng;
using Mono.Cecil;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Rng
{
    public sealed class RuntimeRngCapabilityResolution
    {
        internal RuntimeRngCapabilityResolution(
            UnityRandomCodecResolution codec,
            RngWhitelist whitelist,
            RngAssemblyIdentity actualCallSiteAssembly,
            RngWhitelistResolution callSites,
            IReadOnlyList<RngCallSiteDescriptor> actualCallSiteDescriptors)
        {
            Codec = codec
                    ?? throw new ArgumentNullException(nameof(codec));
            Whitelist = whitelist
                        ?? throw new ArgumentNullException(nameof(whitelist));
            ActualCallSiteAssembly =
                actualCallSiteAssembly
                ?? throw new ArgumentNullException(
                    nameof(actualCallSiteAssembly));
            CallSites = callSites
                        ?? throw new ArgumentNullException(nameof(callSites));
            ActualCallSiteDescriptors =
                actualCallSiteDescriptors
                ?? throw new ArgumentNullException(
                    nameof(actualCallSiteDescriptors));
        }

        public UnityRandomCodecResolution Codec { get; }
        public RngWhitelist Whitelist { get; }
        public RngAssemblyIdentity ActualCallSiteAssembly { get; }
        public RngWhitelistResolution CallSites { get; }
        public IReadOnlyList<RngCallSiteDescriptor>
            ActualCallSiteDescriptors { get; }
        public bool StateAvailable => Codec.Ready;
        public bool HooksAvailable => Codec.Ready && CallSites.Ready;
        public string CodecId => Codec.Ready
            ? Codec.Codec!.CodecId
            : "unsupported";
        public string CoverageId => HooksAvailable
            ? RngWhitelist.CoverageId
            : StateAvailable
                ? "unity-state-only-build-mismatch"
                : "disabled-codec-mismatch";
    }

    public static class RuntimeRngCapabilityResolver
    {
        public static RuntimeRngCapabilityResolution Resolve()
        {
            var codec = UnityRandomStateCodec_1_5_78_11833.Resolve();
            var whitelist = RngWhitelist.CreateTargetBuild();
            var assembly = typeof(HeroController).Assembly;
            var location = assembly.Location;
            string assemblyName;
            string assemblySha256;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                assemblyName = Path.GetFileName(location);
                assemblySha256 = Sha256Utility.ComputeFileHex(location);
            }
            else
            {
                assemblyName = assembly.GetName().Name ?? "Assembly-CSharp.dll";
                assemblySha256 = new string('0', 64);
                Modding.Logger.LogWarn("[HKTAS] Assembly-CSharp path is unavailable; resolving RNG call sites via reflection.");
            }

            var actualAssembly = new RngAssemblyIdentity(
                assemblyName,
                assemblySha256,
                assembly.ManifestModule.ModuleVersionId.ToString("D"));
            var actualSites = ResolveCallSites(
                location,
                assembly,
                whitelist.CallSites);
            var callSites = whitelist.Resolve(
                actualAssembly,
                actualSites);
            if (!string.Equals(actualAssembly.Sha256, whitelist.Assembly.Sha256, StringComparison.Ordinal)
                || !string.Equals(actualAssembly.ModuleVersionId, whitelist.Assembly.ModuleVersionId, StringComparison.Ordinal))
            {
                Modding.Logger.LogWarn("[HKTAS] Assembly-CSharp identity differs from baseline; RNG call-site hooks resolved via reflection/name fallback.");
            }
            return new RuntimeRngCapabilityResolution(
                codec,
                whitelist,
                actualAssembly,
                callSites,
                new ReadOnlyCollection<RngCallSiteDescriptor>(
                    actualSites));
        }

        private static List<RngCallSiteDescriptor> ResolveCallSites(
            string path,
            Assembly reflectionAssembly,
            IReadOnlyList<RngCallSiteDescriptor> expected)
        {
            var result = new List<RngCallSiteDescriptor>();
            AssemblyDefinition? definition = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    definition = AssemblyDefinition.ReadAssembly(path);
                }
            }
            catch (Exception ex)
            {
                Modding.Logger.LogWarn("[HKTAS] Failed to open Assembly-CSharp via Cecil (" + ex.Message + "); falling back to reflection.");
            }

            using (definition)
            {
                foreach (var item in expected)
                {
                    var targetMethodName = ExtractMethodName(item.MethodSignature);
                    MethodDefinition? method = null;
                    if (definition != null)
                    {
                        var byToken = definition.MainModule.LookupToken(item.MetadataToken) as MethodDefinition;
                        if (byToken != null && byToken.HasBody
                            && byToken.DeclaringType != null
                            && string.Equals(byToken.DeclaringType.FullName, item.DeclaringType, StringComparison.Ordinal)
                            && string.Equals(byToken.Name, targetMethodName, StringComparison.Ordinal))
                        {
                            method = byToken;
                        }
                        else
                        {
                            method = FindCecilMethodByName(definition.MainModule, item.DeclaringType, targetMethodName);
                            if (method != null)
                            {
                                Modding.Logger.LogWarn("[HKTAS] RNG call site '" + item.CallSiteId + "' token 0x"
                                    + item.MetadataToken.ToString("x8") + " relocated by name to 0x"
                                    + method.MetadataToken.ToInt32().ToString("x8") + ".");
                            }
                        }
                    }

                    MethodBase? reflected = null;
                    if (method != null)
                    {
                        try
                        {
                            reflected = reflectionAssembly.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32());
                        }
                        catch
                        {
                            reflected = null;
                        }
                    }
                    if (reflected == null)
                    {
                        reflected = FindReflectedMethodByName(reflectionAssembly, item.DeclaringType, targetMethodName);
                    }

                    if (method == null && reflected == null)
                    {
                        Modding.Logger.LogWarn("[HKTAS] RNG call site '" + item.CallSiteId + "' could not be located ("
                            + item.DeclaringType + "." + targetMethodName + ").");
                        continue;
                    }

                    var ilHash = item.MethodIlSha256;
                    var bytes = reflected?.GetMethodBody()?.GetILAsByteArray();
                    if (bytes != null)
                    {
                        ilHash = Sha256Utility.ComputeHex(bytes);
                    }
                    else
                    {
                        ilHash = new string('0', 64);
                        Modding.Logger.LogWarn("[HKTAS] Unable to read IL bytes for RNG call site '" + item.CallSiteId + "'; using placeholder hash.");
                    }

                    var randomApi = item.ExpectedRandomApi;
                    var randomCount = item.ExpectedRandomCallCount;
                    if (method != null && method.HasBody)
                    {
                        var randomCalls = method.Body.Instructions
                            .Select(value => value.Operand)
                            .OfType<MethodReference>()
                            .Where(
                                value => string.Equals(
                                    value.DeclaringType.FullName,
                                    "UnityEngine.Random",
                                    StringComparison.Ordinal))
                            .ToList();
                        var randomApis = randomCalls
                            .Select(value => value.FullName)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        if (randomCalls.Count > 0 && randomApis.Count == 1)
                        {
                            randomApi = randomApis[0];
                            randomCount = randomCalls.Count;
                        }
                        else
                        {
                            Modding.Logger.LogWarn("[HKTAS] RNG call site '" + item.CallSiteId + "' UnityEngine.Random call shape differs (count="
                                + randomCalls.Count + ", distinctApis=" + randomApis.Count + "); keeping hook active.");
                        }
                    }

                    var resolvedToken = method != null
                        ? method.MetadataToken.ToInt32()
                        : (reflected != null && reflected.MetadataToken > 0 ? reflected.MetadataToken : item.MetadataToken);
                    var declaringType = method?.DeclaringType?.FullName ?? reflected?.DeclaringType?.FullName ?? item.DeclaringType;
                    var signature = method?.FullName ?? item.MethodSignature;

                    result.Add(
                        new RngCallSiteDescriptor(
                            item.CallSiteId,
                            declaringType,
                            signature,
                            resolvedToken > 0 ? resolvedToken : item.MetadataToken,
                            ilHash,
                            item.HookType,
                            randomApi,
                            randomCount));
                }
            }

            return result;
        }

        private static string ExtractMethodName(string methodSignature)
        {
            var colonIndex = methodSignature.IndexOf("::", StringComparison.Ordinal);
            var start = colonIndex >= 0 ? colonIndex + 2 : 0;
            var parenIndex = methodSignature.IndexOf('(', start);
            return parenIndex > start
                ? methodSignature.Substring(start, parenIndex - start)
                : methodSignature.Substring(start);
        }

        private static MethodDefinition? FindCecilMethodByName(
            ModuleDefinition module,
            string declaringType,
            string methodName)
        {
            var type = module.Types.FirstOrDefault(t => string.Equals(t.FullName, declaringType, StringComparison.Ordinal))
                ?? module.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, declaringType, StringComparison.Ordinal));
            return type?.Methods.FirstOrDefault(m => m.HasBody && string.Equals(m.Name, methodName, StringComparison.Ordinal));
        }

        private static MethodBase? FindReflectedMethodByName(
            Assembly assembly,
            string declaringType,
            string methodName)
        {
            var type = assembly.GetType(declaringType, throwOnError: false);
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            return type.GetMethods(flags).FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.Ordinal));
        }
    }

    public sealed class RngCallSiteHooks : IDisposable
    {
        private readonly RuntimeRngProbe owner;
        private readonly bool supported;
        private bool attached;

        public RngCallSiteHooks(
            RuntimeRngProbe owner,
            RuntimeRngCapabilityResolution capability)
        {
            this.owner = owner
                         ?? throw new ArgumentNullException(nameof(owner));
            if (capability == null)
            {
                throw new ArgumentNullException(nameof(capability));
            }

            supported = capability.HooksAvailable;
            if (!supported)
            {
                Modding.Logger.LogWarn("[HKTAS] RngCallSiteHooks constructed without full hook capability (" + capability.CallSites.Detail + "); Attach() will be a no-op.");
            }
        }

        public void Attach()
        {
            if (attached || !supported)
            {
                return;
            }

            try
            {
                On.Helper.GetRandomVector2InRange +=
                    OnGetRandomVector2InRange;
                On.HeroController.TakeDamage += OnHeroTakeDamage;
                attached = true;
            }
            catch (Exception ex)
            {
                On.Helper.GetRandomVector2InRange -=
                    OnGetRandomVector2InRange;
                On.HeroController.TakeDamage -= OnHeroTakeDamage;
                Modding.Logger.LogWarn("[HKTAS] Failed to attach RNG call-site hooks (" + ex.Message + "); continuing without RNG call-site hooks.");
            }
        }

        public void Dispose()
        {
            if (!attached)
            {
                return;
            }

            On.HeroController.TakeDamage -= OnHeroTakeDamage;
            On.Helper.GetRandomVector2InRange -=
                OnGetRandomVector2InRange;
            attached = false;
        }

        private Vector2 OnGetRandomVector2InRange(
            On.Helper.orig_GetRandomVector2InRange original,
            Vector2 minimum,
            Vector2 maximum)
        {
            var scope = owner.TryBeginCall("helper.random-vector2");
            try
            {
                return original(minimum, maximum);
            }
            finally
            {
                owner.TryCompleteCall(scope);
            }
        }

        private void OnHeroTakeDamage(
            On.HeroController.orig_TakeDamage original,
            HeroController self,
            GameObject source,
            CollisionSide damageSide,
            int damageAmount,
            int hazardType)
        {
            var scope = owner.TryBeginCall("hero.take-damage");
            try
            {
                original(
                    self,
                    source,
                    damageSide,
                    damageAmount,
                    hazardType);
            }
            finally
            {
                owner.TryCompleteCall(scope);
            }
        }
    }
}
