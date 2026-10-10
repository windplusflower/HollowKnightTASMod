using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The caller supplies original serialized configuration slots, never a
    // runtime search result. Names describe an origin; they do not select it.
    internal sealed class CanonicalAssetOriginRegistry
    {
        internal const int MaximumOrigins = 50000, MaximumAssets = 10000;
        private readonly Dictionary<string, object> slots = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<object, Asset> assets = new Dictionary<object, Asset>(new References());
        private readonly Dictionary<string, Asset> bindings = new Dictionary<string, Asset>(StringComparer.Ordinal);
        private bool sealedRegistry;

        internal void Add(string origin, object value, string type, string name)
        {
            if (sealedRegistry) throw new InvalidDataException("Asset origins are already sealed.");
            CheckText(origin); CheckText(type); CheckText(name, true);
            if (value == null) throw new InvalidDataException("Asset origin value is absent.");
            if (slots.TryGetValue(origin, out var existing))
            {
                if (!ReferenceEquals(existing, value)) throw new InvalidDataException("Original asset slot was replaced: " + origin);
            }
            else if (slots.Count >= MaximumOrigins) throw new InvalidDataException("Asset origin slot budget exceeded.");
            if (!assets.TryGetValue(value, out var asset))
            {
                if (assets.Count >= MaximumAssets) throw new InvalidDataException("Asset origin object budget exceeded.");
                asset = new Asset(value, type, name);
            }
            if (asset.Type != type || asset.Name != name) throw new InvalidDataException("Original asset metadata changed: " + origin);
            if (existing == null) slots.Add(origin, value);
            if (!assets.ContainsKey(value)) assets.Add(value, asset);
            asset.Origins.Add(origin);
        }

        internal void Seal()
        {
            if (sealedRegistry) throw new InvalidDataException("Asset origins cannot be sealed twice.");
            if (assets.Count == 0) throw new InvalidDataException("Original asset origins are absent.");
            foreach (var asset in assets.Values)
            {
                asset.Binding = "HeroAssetOrigin/v1/" + Hash(new[] { asset.Type, asset.Name }.Concat(asset.Origins));
                if (bindings.ContainsKey(asset.Binding)) throw new InvalidDataException("Original asset origin identity is ambiguous.");
                bindings.Add(asset.Binding, asset);
            }
            sealedRegistry = true;
        }

        internal string? Bind(object value, string type, string name)
        {
            RequireSealed();
            if (!assets.TryGetValue(value, out var asset)) return null;
            if (asset.Type != type || asset.Name != name) throw new InvalidDataException("Original asset metadata changed: " + asset.Binding);
            return asset.Binding;
        }

        internal object Resolve(string binding)
        {
            RequireSealed();
            return bindings.TryGetValue(binding, out var asset) ? asset.Value
                : throw new InvalidDataException("Original asset origin is absent or its aliases differ: " + binding);
        }

        internal Entry[] Snapshot()
        {
            RequireSealed();
            return bindings.Values.OrderBy(asset => asset.Binding, StringComparer.Ordinal).Select(asset => new Entry {
                Binding = asset.Binding, Type = asset.Type, Name = asset.Name, Origins = asset.Origins.ToArray() }).ToArray();
        }

        internal string ManifestHash() { RequireSealed(); return Hash(bindings.Keys.OrderBy(key => key, StringComparer.Ordinal)); }
        private void RequireSealed() { if (!sealedRegistry) throw new InvalidDataException("Original asset origins are not sealed."); }
        private static void CheckText(string value, bool allowEmpty = false)
        {
            if (value == null || !allowEmpty && value.Length == 0 || value.Length > 16384 || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Original asset origin text is invalid.");
        }
        private static string Hash(IEnumerable<string> values)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
                foreach (var value in values) writer.Write(value);
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
        }
        internal sealed class Entry
        {
            public string Binding { get; set; } = "";
            public string Type { get; set; } = "";
            public string Name { get; set; } = "";
            public string[] Origins { get; set; } = Array.Empty<string>();
        }
        private sealed class Asset
        {
            internal readonly object Value;
            internal readonly string Type, Name;
            internal readonly SortedSet<string> Origins = new SortedSet<string>(StringComparer.Ordinal);
            internal string Binding = "";
            internal Asset(object value, string type, string name) { Value = value; Type = type; Name = name; }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
