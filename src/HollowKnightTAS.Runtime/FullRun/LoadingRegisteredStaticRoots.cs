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
                var type = assemblies.Select(assembly => assembly.GetType(name, false)).FirstOrDefault(value => value != null)
                    ?? throw new InvalidOperationException("Registered P0 static root is unavailable: " + name);
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
    }
}
