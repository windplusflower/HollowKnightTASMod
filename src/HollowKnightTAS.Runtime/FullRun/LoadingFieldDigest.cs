using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
    internal sealed class LoadingReferenceReadResult
    {
        internal LoadingReferenceReadResult(int nodes, int characters, string[] unsupported)
        { Nodes = nodes; Characters = characters; Unsupported = unsupported; }
        internal int Nodes { get; }
        internal int Characters { get; }
        internal IReadOnlyList<string> Unsupported { get; }
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
        private readonly Action<object, FieldInfo?, int, object?, string>? slotObservation;
        private readonly Func<object?, bool>? observeSlotValue;
        internal bool Capturing { get; private set; }
        private readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();
        private readonly Dictionary<Type, TypeShape> typeShapes = new Dictionary<Type, TypeShape>();
        private readonly Dictionary<FieldInfo, string> fieldTokens = new Dictionary<FieldInfo, string>();
        private readonly Dictionary<FieldInfo, string> fieldPaths = new Dictionary<FieldInfo, string>();
        private readonly Dictionary<int, string> indexPaths = new Dictionary<int, string>();
        private readonly SHA256 digestHash = SHA256.Create();
        private readonly LoadingFieldReader fieldReader;
        private readonly LoadingObservationTypeIdentity typeIdentity = new LoadingObservationTypeIdentity();
        private readonly LoadingPackedArrayDigest? packedArrays;
        private readonly Func<FieldInfo, bool>? directFloatingField;
        private readonly Dictionary<object, int> visited = new Dictionary<object, int>(IdentityComparer.Instance);
        private Dictionary<object, int>? sharedGraph;
        private LoadingSharedGraphEvidence? sharedEvidence;
        private bool sharedEvidenceSelectedRoot;
        internal object? SharedGraphEvidence => sharedEvidence?.Snapshot();
        internal object? SharedGraphEvidenceSummary => sharedEvidence?.Summary();
        private readonly SortedSet<string> unsupported = new SortedSet<string>(StringComparer.Ordinal);
        private readonly StringBuilder text = new StringBuilder();
        private int nodes;
        private bool referenceOnly;
        private int referenceCharacters;
        private string rootPath = "";
        private readonly List<string> pathSegments = new List<string>();
        private string CurrentPath => rootPath + string.Concat(pathSegments);
        private bool includeFieldDiagnostics;
        private readonly List<object> fieldDiagnostics = new List<object>();

        internal LoadingFieldDigest(Func<object, string?> reference, Func<Type, bool> nativeBase,
            Func<object, FieldInfo, object?, string?>? fieldIdentity = null,
            Func<Delegate, bool>? observerCallback = null, bool? compiledFieldReaders = null,
            Func<Type, bool>? packedArrayElement = null, Func<FieldInfo, bool>? directFloatingField = null,
            Action<object, FieldInfo?, int, object?, string>? slotObservation = null,
            Func<object?, bool>? observeSlotValue = null)
        {
            this.reference = reference; this.nativeBase = nativeBase;
            this.fieldIdentity = fieldIdentity; this.observerCallback = observerCallback;
            this.directFloatingField = directFloatingField;
            this.slotObservation = slotObservation;
            this.observeSlotValue = observeSlotValue;
            fieldReader = new LoadingFieldReader(compiledFieldReaders
                ?? Environment.GetEnvironmentVariable("HKTAS_LOADING_ORACLE_REFLECTION_FIELDS") != "1");
            if (packedArrayElement != null) packedArrays = new LoadingPackedArrayDigest(packedArrayElement, Fields, typeIdentity.Bind);
        }

        internal LoadingFieldDigestResult Capture(object? value, string root, bool expandRoot = false,
            bool includeFieldDiagnostics = false)
        {
            Reset();
            rootPath = root;
            sharedEvidenceSelectedRoot = sharedEvidence != null && LoadingSharedGraphEvidence.WantsTokens(root);
            sharedEvidence?.BeginRoot(root);
            this.includeFieldDiagnostics = includeFieldDiagnostics;
            Capturing = true;
            try { Append(value, 0, expandRoot); return Finish(); }
            finally { Capturing = false; }
        }
        // Same field/array/alias walker and the same logical budgets. A caller
        // collecting only strong reference slots does not consume token bytes
        // or their SHA, so avoid allocating and hashing that unused encoding.
        internal LoadingReferenceReadResult CaptureReferences(object? value, string root, bool expandRoot = false)
        {
            Reset(); rootPath = root; referenceOnly = true; Capturing = true;
            try {
                Append(value, 0, expandRoot);
                return new LoadingReferenceReadResult(nodes, referenceCharacters, unsupported.ToArray());
            }
            finally { visited.Clear(); referenceOnly = false; Capturing = false; }
        }

        internal LoadingFieldDigestResult CaptureFields(object owner, string root, params string[] names)
        {
            // Extra field diagnostics must not change ownership/IDs in the main graph.
            var graph = sharedGraph;
            sharedGraph = null;
            var evidence = sharedEvidence; var selected = sharedEvidenceSelectedRoot; sharedEvidence = null;
            Capturing = true;
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
            finally { sharedGraph = graph; sharedEvidence = evidence; sharedEvidenceSelectedRoot = selected; Capturing = false; }
        }

        internal void BeginSharedGraph(bool evidence = false)
        {
            if (sharedGraph != null) throw new InvalidOperationException("P0 shared field graph is already active.");
            sharedGraph = new Dictionary<object, int>(IdentityComparer.Instance);
            sharedEvidence = evidence ? new LoadingSharedGraphEvidence() : null;
        }

        internal void EndSharedGraph() { sharedGraph?.Clear(); sharedGraph = null; sharedEvidence?.Dispose(); sharedEvidence = null; }

        internal int BindSharedReference(object value)
        {
            if (sharedGraph == null || value.GetType().IsValueType)
                throw new InvalidOperationException("Boundary reference binding needs its shared managed graph.");
            if (sharedGraph.TryGetValue(value, out var id)) { ObserveSharedReference(id, false); return id; }
            if (sharedGraph.Count >= 1048576)
                throw new InvalidOperationException("P0 shared field graph reference budget exceeded at " + CurrentPath);
            id = sharedGraph.Count; sharedGraph.Add(value, id); ObserveSharedReference(id, true); return id;
        }

        internal object? Read(object owner, string name)
        {
            var field = Fields(owner.GetType()).FirstOrDefault(value => value.Name == name);
            return field == null ? null : fieldReader.Read(field, owner);
        }

        internal object? Read(object owner, FieldInfo field) => fieldReader.Read(field, owner);

        internal object Schema()
        {
            // Lossless name interning keeps the same 16 MiB output budget.
            // Preserve every type, field, declaring owner and value type.
            var names = fieldCache.Keys.Select(typeIdentity.Bind).Concat(fieldCache.Values.SelectMany(value => value)
                .SelectMany(field => new[] { typeIdentity.Bind(field.DeclaringType!), typeIdentity.Bind(field.FieldType) }))
                .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var indices = names.Select((name, index) => new { name, index }).ToDictionary(value => value.name,
                value => value.index, StringComparer.Ordinal);
            return new { encoding = "interned-field-schema-v2", names,
                types = fieldCache.OrderBy(pair => typeIdentity.Bind(pair.Key), StringComparer.Ordinal).Select(pair => new
                {
                    type = indices[typeIdentity.Bind(pair.Key)],
                    fields = pair.Value.Select(field => new { owner = indices[typeIdentity.Bind(field.DeclaringType!)],
                        field = field.Name, type = indices[typeIdentity.Bind(field.FieldType)] }).ToArray()
                }).ToArray() };
        }

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
                { fieldTokens.Add(field, typeIdentity.Bind(field.DeclaringType!) + "." + field.Name); fieldPaths.Add(field, "." + field.Name); }
            fieldCache.Add(type, fields);
            return fields;
        }

        private void Reset()
        { nodes = 0; referenceCharacters = 0; text.Clear(); visited.Clear(); unsupported.Clear(); fieldDiagnostics.Clear(); pathSegments.Clear(); rootPath = ""; includeFieldDiagnostics = false; }

        private LoadingFieldDigestResult Finish()
        {
            var result = new LoadingFieldDigestResult(Digest(text.ToString()),
                nodes, unsupported.ToArray(), fieldDiagnostics.ToArray());
            sharedEvidence?.EndRoot(result.Sha256);
            visited.Clear();
            return result;
        }

        private string Digest(string value) => BitConverter.ToString(digestHash.ComputeHash(
            Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();

        public void Dispose() { EndSharedGraph(); visited.Clear(); typeShapes.Clear(); packedArrays?.Dispose(); digestHash.Dispose(); typeIdentity.Dispose(); }

        private void ObserveSharedReference(int id, bool first)
        {
            if (sharedEvidence != null) sharedEvidence.Reference(id, rootPath,
                sharedEvidenceSelectedRoot ? string.Concat(pathSegments) : null, first);
        }
        private void Token(string value)
        {
            if ((referenceOnly ? referenceCharacters : text.Length) + value.Length + 24 > MaximumCharacters)
                throw new InvalidOperationException("P0 field digest character budget exceeded at " + CurrentPath);
            if (referenceOnly) {
                var digits = 1;
                for (var length = value.Length; length >= 10; length /= 10) digits++;
                referenceCharacters += digits + value.Length + 2;
                return;
            }
            if (sharedEvidence != null && sharedEvidenceSelectedRoot) sharedEvidence.Token(rootPath, string.Concat(pathSegments), value);
            text.Append(value.Length).Append(':').Append(value).Append(';');
        }

        internal string DiagnosticPath => CurrentPath;
        internal string DiagnosticRoot => rootPath;

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
            Token(typeIdentity.Bind(type));
            if (value is IntPtr pointer)
            { if (pointer == IntPtr.Zero) Token("0"); else Omit("native-pointer"); return; }
            if (value is UIntPtr unsignedPointer)
            { if (unsignedPointer == UIntPtr.Zero) Token("0"); else Omit("native-pointer"); return; }
            if (value is float f) { Token(new SingleBits { Value = f }.Bits.ToString("x8")); return; }
            if (value is double d) { Token(BitConverter.DoubleToInt64Bits(d).ToString("x16")); return; }
            if (value is decimal m) { foreach (var part in decimal.GetBits(m)) Token(part.ToString("x8")); return; }
            var shape = Shape(type);
            if (shape.Enum)
            {
                Token(shape.UnsignedEnum ? Convert.ToUInt64(value).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (shape.Primitive || value is string)
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
                if (member is System.Reflection.Emit.DynamicMethod) { Omit("dynamic-member"); return; }
                Token(member.DeclaringType?.FullName ?? "");
                try { Token(member.MetadataToken.ToString(CultureInfo.InvariantCulture)); }
                catch (Exception error) when (error is InvalidOperationException || error is NotSupportedException
                    || error is NotImplementedException)
                { Omit("dynamic-member"); }
                return;
            }
            if (!shape.ValueType)
            {
                var graph = sharedGraph ?? visited;
                if (graph.TryGetValue(value, out var id)) { ObserveSharedReference(id, false); Token("ref:" + id); return; }
                // Full boundary inventories retain cross-root aliases for every
                // dormant FSM too; this is independent of the per-root node cap.
                if (graph.Count >= 1048576)
                    throw new InvalidOperationException("P0 shared field graph reference budget exceeded at " + CurrentPath);
                if (sharedGraph != null) ObserveSharedReference(graph.Count, true);
                graph.Add(value, graph.Count);
            }
            if (value is Array array)
            {
                if (array.Rank != 1) { Omit("multidimensional-array"); return; }
                Token(array.Length.ToString(CultureInfo.InvariantCulture));
                var layout = packedArrays?.Describe(type.GetElementType()!);
                if (layout != null)
                {
                    // Preserve the old logical node/depth limits even though
                    // the padding-free values are encoded without boxing.
                    var logicalNodes = checked(array.Length * layout.Nodes);
                    if (logicalNodes > MaximumNodes - nodes || (array.Length != 0 && depth + 1 + layout.Depth > MaximumDepth))
                        throw new InvalidOperationException("P0 field digest graph budget exceeded at " + CurrentPath);
                    nodes += logicalNodes;
                    Token("packed-leaf-array-bits-v1");
                    Token(layout.Size.ToString(CultureInfo.InvariantCulture));
                    Token(layout.Schema);
                    Token(packedArrays!.Capture(array, layout));
                    return;
                }
                for (var i = 0; i < array.Length; i++) {
                    var element = array.GetValue(i);
                    if (slotObservation != null && (observeSlotValue?.Invoke(element) ?? true))
                        slotObservation(array, null, i, element, CurrentPath + IndexPath(i));
                    AppendAt(element, IndexPath(i), depth + 1);
                }
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
            if (shape.Dictionary)
            {
                AppendDictionary(value, depth);
                return;
            }
            if (shape.HashSet)
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
            if (!((fieldIdentity == null || directFloatingField?.Invoke(field) == true)
                && fieldReader.AppendFloating(field, owner, this, depth)))
            {
                var value = fieldReader.Read(field, owner);
                if (slotObservation != null && (observeSlotValue?.Invoke(value) ?? true))
                    slotObservation(owner, field, -1, value, CurrentPath);
                var identity = fieldIdentity?.Invoke(owner, field, value);
                if (identity != null) Token(identity);
                else Append(value, depth, false);
            }
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

        private TypeShape Shape(Type type)
        {
            if (typeShapes.TryGetValue(type, out var shape)) return shape;
            var generic = type.IsGenericType ? type.GetGenericTypeDefinition() : null;
            shape = new TypeShape { Enum = type.IsEnum, Primitive = type.IsPrimitive, ValueType = type.IsValueType,
                Dictionary = generic == typeof(Dictionary<,>), HashSet = generic == typeof(HashSet<>),
                UnsignedEnum = type.IsEnum && Enum.GetUnderlyingType(type) == typeof(ulong) };
            typeShapes.Add(type, shape);
            return shape;
        }

        private sealed class TypeShape
        {
            internal bool Enum, UnsignedEnum, Primitive, ValueType, Dictionary, HashSet;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct SingleBits
        {
            [FieldOffset(0)] internal float Value;
            [FieldOffset(0)] internal int Bits;
        }

        internal void AppendSingleBits(float value, int depth)
        {
            if (++nodes > MaximumNodes || depth > MaximumDepth)
                throw new InvalidOperationException("P0 field digest graph budget exceeded at " + CurrentPath);
            Token("System.Single");
            Token(new SingleBits { Value = value }.Bits.ToString("x8"));
        }

        internal void AppendDoubleBits(double value, int depth)
        {
            if (++nodes > MaximumNodes || depth > MaximumDepth)
                throw new InvalidOperationException("P0 field digest graph budget exceeded at " + CurrentPath);
            Token("System.Double");
            Token(BitConverter.DoubleToInt64Bits(value).ToString("x16"));
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
