using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingRegisteredStaticRoots
    {
        // These types have necessarily initialized before the Mod preload guard
        // opens: loader/hooks/input are used by bootstrap, CustomKnight's
        // constructor is verified by the early clock policy, and EnviousMarmu
        // is an instantiated member of this probe's component fingerprint.
        // Do not replace this list with arbitrary GetTypes/GetValue scanning:
        // reading an untouched type's static fields can run its initializer.
        private static readonly string[] Names = {
            "Modding.ModLoader", "Modding.ModHooks", "InControl.InputManager",
            "CustomKnight.CustomKnight", "CustomKnight.SwapManager", "EnviousMarmu.EnviousMarmu"
        };

        internal static IEnumerable<Tuple<string, object?>> Read()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).ToArray();
            foreach (var name in Names)
            {
                var type = assemblies.Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null);
                if (type == null)
                {
                    // A pure installation has no external Mod constructors or
                    // static roots. Present Mods retain the explicit registry.
                    if (name.StartsWith("CustomKnight.", StringComparison.Ordinal) || name.StartsWith("EnviousMarmu.", StringComparison.Ordinal)) continue;
                    throw new InvalidOperationException("Registered P0 static root is unavailable: " + name);
                }
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                             .Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    if (field.IsDefined(typeof(ThreadStaticAttribute), false))
                        throw new InvalidOperationException("Registered P0 root requires thread-local observation: " + name + "." + field.Name);
                    yield return Tuple.Create<string, object?>("static:" + type.Assembly.GetName().Name + ":" + name + "." + field.Name,
                        field.GetValue(null));
                }
            }
        }

        internal static IEnumerable<Tuple<string, object?>> ReadCanonical()
        {
            foreach (var root in Read()) yield return root;
            // Apply has already used these initialized singletons/policies.
            // Reading this closed list cannot initialize arbitrary dormant types.
            var types = new[] { typeof(ObjectPool), typeof(iTween), typeof(PlayMakerFSM), typeof(HutongGames.PlayMaker.FsmVariables),
                typeof(HutongGames.PlayMaker.Fsm), typeof(HutongGames.PlayMaker.FsmEvent), typeof(HutongGames.PlayMaker.FsmExecutionStack),
                typeof(HutongGames.PlayMaker.FsmStateAction), typeof(HutongGames.PlayMaker.ActionData), typeof(HutongGames.PlayMaker.ReflectionUtils),
                typeof(GameManager), typeof(HeroController), typeof(GameCameras), typeof(PlayMakerGlobals) };
            foreach (var type in types.OrderBy(type => type.FullName, StringComparer.Ordinal))
                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    if (field.IsDefined(typeof(ThreadStaticAttribute), false))
                        throw new InvalidOperationException("Canonical static root requires thread-local observation: " + type.FullName + "." + field.Name);
                    yield return Tuple.Create<string, object?>("static:" + type.Assembly.GetName().Name + ":" + type.FullName + "." + field.Name,
                        field.GetValue(null));
                }
            if (HollowKnightTAS.Core.Movie.NativeExecutionProfile.UsesCanonicalBossChallengeContext(
                HollowKnightTAS.Core.Movie.NativeExecutionProfile.SelectedProfileId))
                foreach (var type in new[] { typeof(BossSceneController), typeof(BossSequenceController), typeof(BossChallengeUI), typeof(BossStatue) }) {
                    CanonicalBossChallengeOriginal.RequireStaticInventory(type);
                    foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal))
                        yield return Tuple.Create<string, object?>("static:" + type.Assembly.GetName().Name + ":" + type.FullName + "." + field.Name, field.GetValue(null));
                }
        }
    }
}
