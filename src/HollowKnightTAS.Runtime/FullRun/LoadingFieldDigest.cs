using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class LoadingFieldDigestResult
    {
        internal LoadingFieldDigestResult(string sha256, int nodes, string[] unsupported, object[] fieldDigests)
        { Sha256 = sha256; Nodes = nodes; Unsupported = unsupported; FieldDigests = fieldDigests; }
        internal string Sha256 { get; }
        internal int Nodes { get; }
        internal IReadOnlyList<string> Unsupported { get; }
        internal object[] FieldDigests { get; }
    }

    // A diagnostic digest, not a serializer. Never evaluate arbitrary
    // properties, ToString, collection enumerators or a static constructor.
    // Nonzero native pointers are omissions, never cross-process identities.
    internal sealed class LoadingFieldDigest
    {
        private const int MaximumNodes = 50000;
        private const int MaximumDepth = 40;
        private const int MaximumCharacters = 2 * 1024 * 1024;
        private readonly Func<object, string?> reference;
        private readonly Func<Type, bool> nativeBase;
        private readonly Func<object, FieldInfo, object?, string?>? fieldIdentity;
        private readonly Func<Delegate, bool>? observerCallback;
        private readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<object, int> visited = new Dictionary<object, int>(IdentityComparer.Instance);
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private readonly StringBuilder text = new StringBuilder();
        private int nodes;
        private string currentPath = "";
        private bool includeFieldDiagnostics;
        private readonly List<object> fieldDiagnostics = new List<object>();

        internal LoadingFieldDigest(Func<object, string?> reference, Func<Type, bool> nativeBase,
            Func<object, FieldInfo, object?, string?>? fieldIdentity = null,
            Func<Delegate, bool>? observerCallback = null)
        {
            this.reference = reference; this.nativeBase = nativeBase;
            this.fieldIdentity = fieldIdentity; this.observerCallback = observerCallback;
        }

        internal LoadingFieldDigestResult Capture(object? value, string root, bool expandRoot = false,
            bool includeFieldDiagnostics = false)
        {
            Reset();
            this.includeFieldDiagnostics = includeFieldDiagnostics;
            Append(value, root, 0, expandRoot);
            return Finish();
        }

        internal LoadingFieldDigestResult CaptureFields(object owner, string root, params string[] names)
        {
            Reset();
            foreach (var name in names.OrderBy(value => value, StringComparer.Ordinal))
            {
                Token(name);
                var field = Fields(owner.GetType()).FirstOrDefault(candidate => candidate.Name == name);
                if (field == null) Omit(root + "." + name, "field-unavailable");
                else AppendField(owner, field, root + "." + name, 0);
            }
            return Finish();
        }

        internal object? Read(object owner, string name)
            => Fields(owner.GetType()).FirstOrDefault(field => field.Name == name)?.GetValue(owner);

        internal object[] Schema() => fieldCache.OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal)
            .Select(pair => (object)new { type = pair.Key.FullName, fields = pair.Value.Select(field => new
                { owner = field.DeclaringType!.FullName, field = field.Name, type = field.FieldType.FullName }).ToArray() }).ToArray();

        internal FieldInfo[] Fields(Type type)
        {
            if (fieldCache.TryGetValue(type, out var fields)) return fields;
            var all = new List<FieldInfo>();
            for (var current = type; current != null && !nativeBase(current); current = current.BaseType)
                all.AddRange(current.GetFields(BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
            fields = all.OrderBy(field => field.DeclaringType!.FullName, StringComparer.Ordinal)
                .ThenBy(field => field.Name, StringComparer.Ordinal).ToArray();
            fieldCache.Add(type, fields);
            return fields;
        }

        private void Reset()
        { nodes = 0; text.Clear(); visited.Clear(); unsupported.Clear(); fieldDiagnostics.Clear(); includeFieldDiagnostics = false; }

        private LoadingFieldDigestResult Finish()
        {
            using (var hash = SHA256.Create())
            {
                var result = new LoadingFieldDigestResult(BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant(),
                    nodes, unsupported.ToArray(), fieldDiagnostics.ToArray());
                visited.Clear();
                return result;
            }
        }

        private void Token(string value)
        {
            if (text.Length + value.Length + 24 > MaximumCharacters)
                throw new InvalidOperationException("P0 field digest character budget exceeded at " + currentPath);
            text.Append(value.Length).Append(':').Append(value).Append(';');
        }

        private void Omit(string path, string reason)
        {
            unsupported.Add(path + ":" + reason);
            Token("omitted:" + reason);
        }

        private void Append(object? value, string path, int depth, bool expand)
        {
            currentPath = path;
            if (++nodes > MaximumNodes || depth > MaximumDepth)
                throw new InvalidOperationException("P0 field digest graph budget exceeded at " + path);
            if (value == null) { Token("null"); return; }
            Delegate[]? gameplayCallbacks = null;
            if (value is Delegate observed && observerCallback != null)
            {
                gameplayCallbacks = observed.GetInvocationList().Where(callback => !observerCallback(callback)).ToArray();
                // A field containing only observation subscriptions has the
                // same gameplay value as an unsubscribed (null) event.
                if (gameplayCallbacks.Length == 0) { Token("null"); return; }
            }
            var type = value.GetType();
            Token(type.FullName ?? type.Name);
            if (value is IntPtr pointer)
            { if (pointer == IntPtr.Zero) Token("0"); else Omit(path, "native-pointer"); return; }
            if (value is UIntPtr unsignedPointer)
            { if (unsignedPointer == UIntPtr.Zero) Token("0"); else Omit(path, "native-pointer"); return; }
            if (value is float f) { Token(BitConverter.ToInt32(BitConverter.GetBytes(f), 0).ToString("x8")); return; }
            if (value is double d) { Token(BitConverter.DoubleToInt64Bits(d).ToString("x16")); return; }
            if (value is decimal m) { foreach (var part in decimal.GetBits(m)) Token(part.ToString("x8")); return; }
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                Token(underlying == typeof(ulong) ? Convert.ToUInt64(value).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (type.IsPrimitive || value is string)
            { Token(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""); return; }
            if (!expand)
            {
                var stable = reference(value);
                if (stable != null) { Token(stable); return; }
            }
            if (value is Type reflectedType)
            { Token(reflectedType.Assembly.GetName().Name + ":" + reflectedType.FullName); return; }
            if (value is MemberInfo member)
            {
                Token(member.DeclaringType?.FullName ?? "");
                try { Token(member.MetadataToken.ToString(CultureInfo.InvariantCulture)); }
                catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException)
                { Omit(path, "dynamic-member"); }
                return;
            }
            if (!type.IsValueType)
            {
                if (visited.TryGetValue(value, out var id)) { Token("ref:" + id); return; }
                visited.Add(value, visited.Count);
            }
            if (value is Array array)
            {
                if (array.Rank != 1) { Omit(path, "multidimensional-array"); return; }
                Token(array.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < array.Length; i++) Append(array.GetValue(i), path + "[" + i + "]", depth + 1, false);
                return;
            }
            if (value is Delegate callback)
            {
                var calls = gameplayCallbacks ?? callback.GetInvocationList();
                Token(calls.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < calls.Length; i++)
                {
                    Append(calls[i].Method, path + ".method[" + i + "]", depth + 1, false);
                    Append(calls[i].Target, path + ".target[" + i + "]", depth + 1, false);
                }
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                AppendDictionary(value, path, depth);
                return;
            }
            foreach (var field in Fields(type))
            {
                Token(field.DeclaringType!.FullName + "." + field.Name);
                AppendField(value, field, path + "." + field.Name, depth + 1);
            }
        }

        private void AppendField(object owner, FieldInfo field, string path, int depth)
        {
            var start = text.Length;
            var value = field.GetValue(owner);
            var identity = fieldIdentity?.Invoke(owner, field, value);
            if (identity != null) Token(identity);
            else Append(value, path, depth, false);
            if (includeFieldDiagnostics)
            {
                // Export only digests of the exact tokens already observed.
                // Do not serialize field values or traverse the graph again.
                if (fieldDiagnostics.Count >= MaximumNodes)
                    throw new InvalidOperationException("P0 field diagnostic count budget exceeded.");
                using (var hash = SHA256.Create())
                    fieldDiagnostics.Add(new { path, type = field.FieldType.FullName,
                        digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                            text.ToString(start, text.Length - start)))).Replace("-", "").ToLowerInvariant() });
            }
        }

        private void AppendDictionary(object owner, string path, int depth)
        {
            // BCL dictionaries cache process-specific hashes of Unity keys.
            // Preserve slot/iteration order, free-list reuse, version and
            // comparer semantics. Never call a key's hash or an enumerator.
            var members = Fields(owner.GetType());
            FieldInfo Find(string name) => members.FirstOrDefault(field => field.Name == name || field.Name == "_" + name)
                ?? throw new InvalidOperationException("P0 dictionary layout unavailable: " + name);
            var count = (int)Find("count").GetValue(owner)!;
            var entries = Find("entries").GetValue(owner) as Array;
            if (count < 0 || count > MaximumNodes || (count > 0 && (entries == null || count > entries.Length)))
                throw new InvalidOperationException("P0 dictionary entry count is invalid.");
            Token("dictionary-slots-v1");
            foreach (var name in new[] { "count", "freeCount", "freeList", "version", "comparer" })
            {
                Token(name);
                Append(Find(name).GetValue(owner), path + "." + name, depth + 1, false);
            }
            if (entries == null) return;
            var entryType = entries.GetType().GetElementType()!;
            var hash = Fields(entryType).Single(field => field.Name == "hashCode");
            var next = Fields(entryType).Single(field => field.Name == "next");
            var key = Fields(entryType).Single(field => field.Name == "key");
            var item = Fields(entryType).Single(field => field.Name == "value");
            for (var index = 0; index < count; index++)
            {
                var entry = entries.GetValue(index)!;
                var link = (int)next.GetValue(entry)!;
                var free = hash.FieldType == typeof(int) ? (int)hash.GetValue(entry)! < 0 : link < -1;
                Token(index.ToString(CultureInfo.InvariantCulture));
                Token(free ? "free" : "active");
                if (free) Append(link, path + ".free[" + index + "]", depth + 1, false);
                else
                {
                    Append(key.GetValue(entry), path + ".key[" + index + "]", depth + 1, false);
                    Append(item.GetValue(entry), path + ".value[" + index + "]", depth + 1, false);
                }
            }
        }

        private sealed class IdentityComparer : IEqualityComparer<object>
        {
            internal static readonly IdentityComparer Instance = new IdentityComparer();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
