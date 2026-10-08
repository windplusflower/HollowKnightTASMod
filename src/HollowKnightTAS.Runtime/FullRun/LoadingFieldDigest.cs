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
    internal sealed class LoadingFieldDigest : IDisposable
    {
        private const int MaximumNodes = 50000;
        private const int MaximumDepth = 40;
        private const int MaximumCharacters = 2 * 1024 * 1024;
        private readonly Func<object, string?> reference;
        private readonly Func<Type, bool> nativeBase;
        private readonly Func<object, FieldInfo, object?, string?>? fieldIdentity;
        private readonly Func<Delegate, bool>? observerCallback;
        private readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<FieldInfo, string> fieldTokens = new Dictionary<FieldInfo, string>();
        private readonly Dictionary<FieldInfo, string> fieldPaths = new Dictionary<FieldInfo, string>();
        private readonly Dictionary<int, string> indexPaths = new Dictionary<int, string>();
        private readonly SHA256 digestHash = SHA256.Create();
        private readonly Dictionary<object, int> visited = new Dictionary<object, int>(IdentityComparer.Instance);
        private Dictionary<object, int>? sharedGraph;
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private readonly StringBuilder text = new StringBuilder();
        private int nodes;
        private string rootPath = "";
        private readonly List<string> pathSegments = new List<string>();
        private string CurrentPath => rootPath + string.Concat(pathSegments);
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
            rootPath = root;
            this.includeFieldDiagnostics = includeFieldDiagnostics;
            Append(value, 0, expandRoot);
            return Finish();
        }

        internal LoadingFieldDigestResult CaptureFields(object owner, string root, params string[] names)
        {
            // Extra field diagnostics must not change ownership/IDs in the main graph.
            var graph = sharedGraph;
            sharedGraph = null;
            try
            {
            Reset();
            rootPath = root;
            foreach (var name in names.OrderBy(value => value, StringComparer.Ordinal))
            {
                Token(name);
                var field = Fields(owner.GetType()).FirstOrDefault(candidate => candidate.Name == name);
                pathSegments.Add("." + name);
                try { if (field == null) Omit("field-unavailable"); else AppendField(owner, field, 0); }
                finally { pathSegments.RemoveAt(pathSegments.Count - 1); }
            }
            return Finish();
            }
            finally { sharedGraph = graph; }
        }

        internal void BeginSharedGraph()
        {
            if (sharedGraph != null) throw new InvalidOperationException("P0 shared field graph is already active.");
            sharedGraph = new Dictionary<object, int>(IdentityComparer.Instance);
        }

        internal void EndSharedGraph() { sharedGraph?.Clear(); sharedGraph = null; }

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
            foreach (var field in fields)
                if (!fieldTokens.ContainsKey(field))
                { fieldTokens.Add(field, field.DeclaringType!.FullName + "." + field.Name); fieldPaths.Add(field, "." + field.Name); }
            fieldCache.Add(type, fields);
            return fields;
        }

        private void Reset()
        { nodes = 0; text.Clear(); visited.Clear(); unsupported.Clear(); fieldDiagnostics.Clear(); pathSegments.Clear(); rootPath = ""; includeFieldDiagnostics = false; }

        private LoadingFieldDigestResult Finish()
        {
            var result = new LoadingFieldDigestResult(Digest(text.ToString()),
                nodes, unsupported.ToArray(), fieldDiagnostics.ToArray());
            visited.Clear();
            return result;
        }

        private string Digest(string value) => BitConverter.ToString(digestHash.ComputeHash(
            Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();

        public void Dispose() { EndSharedGraph(); visited.Clear(); digestHash.Dispose(); }

        private void Token(string value)
        {
            if (text.Length + value.Length + 24 > MaximumCharacters)
                throw new InvalidOperationException("P0 field digest character budget exceeded at " + CurrentPath);
            text.Append(value.Length).Append(':').Append(value).Append(';');
        }

        internal string DiagnosticPath => CurrentPath;

        private void Omit(string reason)
        {
            unsupported.Add(CurrentPath + ":" + reason);
            Token("omitted:" + reason);
        }

        private void Append(object? value, int depth, bool expand)
        {
            if (++nodes > MaximumNodes || depth > MaximumDepth)
                throw new InvalidOperationException("P0 field digest graph budget exceeded at " + CurrentPath);
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
            { if (pointer == IntPtr.Zero) Token("0"); else Omit("native-pointer"); return; }
            if (value is UIntPtr unsignedPointer)
            { if (unsignedPointer == UIntPtr.Zero) Token("0"); else Omit("native-pointer"); return; }
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
                { Omit("dynamic-member"); }
                return;
            }
            if (!type.IsValueType)
            {
                var graph = sharedGraph ?? visited;
                if (graph.TryGetValue(value, out var id)) { Token("ref:" + id); return; }
                // Full boundary inventories retain cross-root aliases for every
                // dormant FSM too; this is independent of the per-root node cap.
                if (graph.Count >= 1048576)
                    throw new InvalidOperationException("P0 shared field graph reference budget exceeded at " + CurrentPath);
                graph.Add(value, graph.Count);
            }
            if (value is Array array)
            {
                if (array.Rank != 1) { Omit("multidimensional-array"); return; }
                Token(array.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < array.Length; i++) AppendAt(array.GetValue(i), IndexPath(i), depth + 1);
                return;
            }
            if (value is Delegate callback)
            {
                var calls = gameplayCallbacks ?? callback.GetInvocationList();
                Token(calls.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < calls.Length; i++)
                {
                    AppendAt(calls[i].Method, ".method", depth + 1, i);
                    AppendAt(calls[i].Target, ".target", depth + 1, i);
                }
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                AppendDictionary(value, depth);
                return;
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                AppendHashSet(value, depth);
                return;
            }
            foreach (var field in Fields(type))
            {
                Token(fieldTokens[field]);
                pathSegments.Add(fieldPaths[field]);
                try { AppendField(value, field, depth + 1); }
                finally { pathSegments.RemoveAt(pathSegments.Count - 1); }
            }
        }

        private void AppendField(object owner, FieldInfo field, int depth)
        {
            var start = text.Length;
            var value = field.GetValue(owner);
            var identity = fieldIdentity?.Invoke(owner, field, value);
            if (identity != null) Token(identity);
            else Append(value, depth, false);
            if (includeFieldDiagnostics)
            {
                // Export only digests of the exact tokens already observed.
                // Do not serialize field values or traverse the graph again.
                if (fieldDiagnostics.Count >= MaximumNodes)
                    throw new InvalidOperationException("P0 field diagnostic count budget exceeded.");
                fieldDiagnostics.Add(new { path = CurrentPath, type = field.FieldType.FullName,
                    digest = Digest(text.ToString(start, text.Length - start)) });
            }
        }

        private void AppendDictionary(object owner, int depth)
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
                AppendAt(Find(name).GetValue(owner), "." + name, depth + 1);
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
                if (free) AppendAt(link, ".free", depth + 1, index);
                else
                {
                    AppendAt(key.GetValue(entry), ".key", depth + 1, index);
                    AppendAt(item.GetValue(entry), ".value", depth + 1, index);
                }
            }
        }

        private void AppendHashSet(object owner, int depth)
        {
            // Active bucket links and cached hashes are derived from the rebound
            // values/comparer. Preserve slot order, capacity, free reuse and version.
            // Read fields only: user hash/equality code must never run in the observer.
            var members = Fields(owner.GetType());
            FieldInfo Find(string name) => members.FirstOrDefault(field => field.Name == "_" + name)
                ?? throw new InvalidOperationException("P0 HashSet layout unavailable: " + name);
            var mono = members.Any(field => field.Name == "_slots");
            var entries = Find(mono ? "slots" : "entries").GetValue(owner) as Array;
            var used = (int)Find(mono ? "lastIndex" : "count").GetValue(owner)!;
            if (used < 0 || used > MaximumNodes || (used > 0 && (entries == null || used > entries.Length)))
                throw new InvalidOperationException("P0 HashSet slot count is invalid.");
            Token("hashset-rebind-slots-v1");
            foreach (var name in mono
                         ? new[] { "count", "lastIndex", "freeList", "version", "comparer", "siInfo" }
                         : new[] { "count", "freeCount", "freeList", "version", "comparer" })
            {
                Token(name);
                AppendAt(Find(name).GetValue(owner), "." + name, depth + 1);
            }
            Token(entries == null ? "unallocated" : "capacity=" + entries.Length.ToString(CultureInfo.InvariantCulture));
            if (entries == null) return;
            var slotMembers = Fields(entries.GetType().GetElementType()!);
            var hash = slotMembers.Single(field => field.Name == (mono ? "hashCode" : "HashCode"));
            var next = slotMembers.Single(field => field.Name == (mono ? "next" : "Next"));
            var value = slotMembers.Single(field => field.Name == (mono ? "value" : "Value"));
            for (var index = 0; index < used; index++)
            {
                var slot = entries.GetValue(index)!;
                var link = (int)next.GetValue(slot)!;
                var free = mono ? (int)hash.GetValue(slot)! < 0 : link < -1;
                Token(index.ToString(CultureInfo.InvariantCulture));
                Token(free ? "free" : "active");
                if (free) AppendAt(link, ".free", depth + 1, index);
                else AppendAt(value.GetValue(slot), ".value", depth + 1, index);
            }
        }

        private string IndexPath(int index)
        {
            if (!indexPaths.TryGetValue(index, out var path))
                indexPaths.Add(index, path = "[" + index.ToString(CultureInfo.InvariantCulture) + "]");
            return path;
        }

        private void AppendAt(object? value, string segment, int depth, int? index = null)
        {
            // Materialize the complete path only for a diagnostic or failure.
            // The canonical value tokens and graph traversal stay unchanged.
            pathSegments.Add(segment);
            if (index != null) pathSegments.Add(IndexPath(index.Value));
            try { Append(value, depth, false); }
            finally
            {
                if (index != null) pathSegments.RemoveAt(pathSegments.Count - 1);
                pathSegments.RemoveAt(pathSegments.Count - 1);
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
