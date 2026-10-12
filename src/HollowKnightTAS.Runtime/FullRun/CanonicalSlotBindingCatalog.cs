using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    // An explicit local slot directory. A saved key only resolves a previously
    // registered object; no type/name search or object allocation is involved.
    internal sealed class CanonicalSlotBindingCatalog
    {
        internal const int MaximumBindings = 10000;
        private readonly Dictionary<string, object> slots = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<object, string> bindings = new Dictionary<object, string>(new References());
        private bool sealedCatalog;
        internal int Count => slots.Count;
        internal void Add(string key, object value)
        {
            if (sealedCatalog || string.IsNullOrEmpty(key) || key.Length > 16384 || key.IndexOf('\0') >= 0
                || value == null || value.GetType().IsValueType || slots.Count >= MaximumBindings
                || slots.ContainsKey(key) || bindings.ContainsKey(value))
                throw new InvalidDataException("Canonical slot binding is unregistered, ambiguous or outside its bound.");
            slots.Add(key, value); bindings.Add(value, key);
        }
        internal void Seal()
        {
            if (sealedCatalog || slots.Count == 0) throw new InvalidDataException("Canonical slot catalog cannot be sealed.");
            sealedCatalog = true;
        }
        internal string? Bind(object value)
        {
            RequireSealed();
            return bindings.TryGetValue(value, out var key) ? key : null;
        }
        internal object Resolve(string key)
        {
            RequireSealed();
            if (key == null || !slots.TryGetValue(key, out var value)) throw new InvalidDataException("Canonical slot key is absent.");
            return value;
        }
        private void RequireSealed()
        {
            if (!sealedCatalog) throw new InvalidDataException("Canonical slot catalog was not sealed.");
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? first, object? second) => ReferenceEquals(first, second);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
