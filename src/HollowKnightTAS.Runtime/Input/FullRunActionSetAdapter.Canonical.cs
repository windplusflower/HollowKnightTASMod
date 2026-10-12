using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.FullRun;

namespace HollowKnightTAS.Runtime.Input
{
    public sealed partial class FullRunActionSetAdapter
    {
        internal static Type[] CanonicalSourceTypes => new[] { typeof(FullRunBindingSource), typeof(GameInputChannel) };
        internal static string CanonicalSourceSchema => string.Join(";", CanonicalCarryGraphCodec.Fields(typeof(FullRunBindingSource))
            .Select(field => field.Name + "=" + CanonicalCarryGraphCodec.Identity(field.FieldType)));
        internal void AddCanonicalSourceRoots(IDictionary<string, object> roots)
        {
            if (disposed || !hooked || nativeFrameActive || sampleIndex != 0 || fault.Length != 0 || leases.Count > 64)
                throw new InvalidOperationException("Canonical sources require the original parked input adapter.");
            foreach (var lease in leases) {
                var names = roots.Where(pair => pair.Key.StartsWith("R6/InputActionSet/", StringComparison.Ordinal)
                    && ReferenceEquals(pair.Value, lease.Set)).Select(pair => pair.Key).ToArray();
                if (names.Length != 1) throw new InvalidDataException("Canonical injected sources have no exact original action-set root.");
                lease.AddCanonicalRoots(roots, names[0]);
            }
        }
    }
}
