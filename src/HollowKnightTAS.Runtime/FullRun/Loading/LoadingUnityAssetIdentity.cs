using System;
using System.Collections.Generic;
using System.Globalization;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Instance IDs are used only to recognize aliases within one frozen sample.
    // Canonical IDs depend on ordered graph discovery, never the process ID value.
    internal sealed class LoadingUnityAssetIdentity
    {
        private readonly Dictionary<string, string> identities = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<object> bindings = new List<object>();
        internal object[] Snapshot() => bindings.ToArray();
        internal void Clear() { identities.Clear(); occurrences.Clear(); bindings.Clear(); }

        internal string Bind(int instance, string kind, string type, string name, object? provenance = null)
        {
            if (instance == 0) throw new InvalidOperationException("P0 zero-instance wrapper requires a managed alias binding.");
            return BindKey("native:" + instance.ToString(CultureInfo.InvariantCulture), kind, type, name, provenance);
        }

        internal string BindFakeNull(int wrapper, string type)
            => BindKey("wrapper:" + wrapper.ToString(CultureInfo.InvariantCulture), "fake-null", type, "");

        private string BindKey(string binding, string kind, string type, string name, object? provenance = null)
        {
            if (identities.TryGetValue(binding, out var known)) return known;
            if (identities.Count >= 16384) throw new InvalidOperationException("P0 asset alias budget exceeded.");
            var key = kind + ":" + type + ":" + name;
            occurrences.TryGetValue(key, out var ordinal);
            var id = key + "[" + ordinal.ToString(CultureInfo.InvariantCulture) + "]";
            occurrences[key] = checked(ordinal + 1);
            identities.Add(binding, id);
            bindings.Add(new { id, kind, type, name, provenance });
            return id;
        }
    }
}
