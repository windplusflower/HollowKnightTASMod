using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingRuntimeReferencePolicy
    {
        internal static bool IsOpaqueController(Type type, Assembly runtimeAssembly)
        {
            if (type.Assembly != runtimeAssembly) return false;
            // Array.Assembly follows its element type; the container still has
            // observable length/order/content and must be traversed.
            if (type.IsArray) return false;
            if (type == typeof(CanonicalBossChallengeContext) || type == typeof(CanonicalBossStatueContext)
                || type == typeof(CanonicalBossChallengeCarry.Data)) return false;
            // The SDK comparer is an explicitly registered zero-field S0
            // value with a frozen algorithm; it is not an opaque controller.
            if (type == typeof(CanonicalStableTypeComparer)) return false;
            // Copy-only native observations use compiler-generated anonymous
            // value records. Treating these as controllers discards their nested
            // data (GUIStyle states, material properties, component snapshots).
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                && type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
            return true;
        }
    }
}
