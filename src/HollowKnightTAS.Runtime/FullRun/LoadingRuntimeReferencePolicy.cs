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
            // Copy-only native observations use compiler-generated anonymous
            // value records. Treating these as controllers discards their nested
            // data (GUIStyle states, material properties, component snapshots).
            if (type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                && type.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
            return true;
        }
    }
}
