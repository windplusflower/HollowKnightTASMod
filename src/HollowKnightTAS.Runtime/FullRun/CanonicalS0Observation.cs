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
    internal sealed class CanonicalS0ObservationSnapshot
    {
        public int Schema { get; set; } = 1;
        public string Scope { get; set; } = "registered-R2-R6-field-contract-v1";
        public SortedDictionary<string, string> Fields { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string[] Unclassified { get; set; } = Array.Empty<string>();
        public string[] Unread { get; set; } = Array.Empty<string>();
        public string[] Dependencies { get; set; } = Array.Empty<string>();
        public string[] InvariantFailures { get; set; } = Array.Empty<string>();
        public SortedDictionary<string, string> RootOwnership { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public SortedDictionary<string, string> FieldOwnership { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public string Sha256 { get; set; } = "";
        public bool CompleteStateCoverage => false;
        public bool RestoreQualified => false;
        public bool D1Passed => false;
    }

    internal sealed class CanonicalS0Comparison
    {
        public bool ObservedEqual { get; set; }
        public bool ObservedS0Equal { get; set; }
        public string? FirstS0Difference { get; set; }
        public CanonicalS0ClockDependencyDifference[] ClockDependencyDifferences { get; set; } = Array.Empty<CanonicalS0ClockDependencyDifference>();
        public string? FirstDifference { get; set; }
        public string? Expected { get; set; }
        public string? Actual { get; set; }
        public string[] OpenUnclassified { get; set; } = Array.Empty<string>();
        public string[] OpenUnread { get; set; } = Array.Empty<string>();
        public string[] OpenDependencies { get; set; } = Array.Empty<string>();
        public string[] InvariantFailures { get; set; } = Array.Empty<string>();
        public bool CompleteStateCoverage => false;
        public bool RestoreQualified => false;
        public bool D1Passed => false;
        internal static CanonicalS0Comparison Compare(CanonicalS0ObservationSnapshot expected, CanonicalS0ObservationSnapshot actual)
        {
            var result = new CanonicalS0Comparison {
                ObservedEqual = true, ObservedS0Equal = true,
                OpenUnclassified = expected.Unclassified.Concat(actual.Unclassified).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                OpenUnread = expected.Unread.Concat(actual.Unread).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                InvariantFailures = expected.InvariantFailures.Concat(actual.InvariantFailures).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                OpenDependencies = expected.Dependencies.Concat(actual.Dependencies).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray() };
            if (expected.Schema != actual.Schema || expected.Scope != actual.Scope)
            { result.ObservedEqual = false; result.ObservedS0Equal = false; result.FirstDifference = result.FirstS0Difference = "$contract"; return result; }
            var clockDifferences = new List<CanonicalS0ClockDependencyDifference>();
            foreach (var path in expected.Fields.Keys.Concat(actual.Fields.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal))
            {
                expected.Fields.TryGetValue(path, out var left); actual.Fields.TryGetValue(path, out var right);
                expected.FieldOwnership.TryGetValue(path, out var leftOwner);
                actual.FieldOwnership.TryGetValue(path, out var rightOwner);
                if (left == right && leftOwner == rightOwner) continue;
                if (result.ObservedEqual) {
                    result.ObservedEqual = false; result.FirstDifference = path; result.Expected = left; result.Actual = right;
                }
                // Keep original bits and every strict difference. Only this
                // source-verified field is an explicit dependency on original
                // FsmTime, not an independent S0 scalar value. Its restoration
                // binding remains open; this never qualifies D1 or a restore.
                if (left != null && right != null && leftOwner == "Dependency" && rightOwner == "Dependency"
                    && FiniteClockBits(left) && FiniteClockBits(right)
                    && path.EndsWith(".HutongGames.PlayMaker.FsmState::<RealStartTime>k__BackingField", StringComparison.Ordinal))
                    clockDifferences.Add(new CanonicalS0ClockDependencyDifference { Path = path, Expected = left, Actual = right });
                else if (result.ObservedS0Equal) { result.ObservedS0Equal = false; result.FirstS0Difference = path; }
            }
            result.ClockDependencyDifferences = clockDifferences.ToArray();
            return result;
        }
        private static bool FiniteClockBits(string value)
        {
            if (!value.StartsWith("f32:", StringComparison.Ordinal) || value.Length != 15) return false;
            var bytes = new byte[4];
            for (var i = 0; i < bytes.Length; i++) {
                if (i != 0 && value[3 + i * 3] != '-') return false;
                if (!byte.TryParse(value.Substring(4 + i * 3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i])) return false;
            }
            var number = BitConverter.ToSingle(bytes, 0);
            return !float.IsNaN(number) && !float.IsInfinity(number);
        }
    }

    internal sealed class CanonicalS0ClockDependencyDifference
    {
        public string Path { get; set; } = "";
        public string Expected { get; set; } = "";
        public string Actual { get; set; } = "";
        public string DependencySource => "FsmTime.RealtimeSinceStartup";
        public string RequiredClockBinding => "original-realtime-minus-editor-pause-offset";
        public bool RestoreBindingQualified => false;
    }

    // Independent graph: no raw BoundaryDigest node numbering, constructors,
    // property getters, collection enumerators, game writes or FSM events.
    internal sealed class CanonicalS0Observation
    {
        internal enum Ownership { S0, D, Unknown, Dependency }
        internal sealed class Root
        {
            internal Root(string name, object? value, Ownership ownership) { Name = name; Value = value; Domain = ownership; }
            internal string Name; internal object? Value; internal Ownership Domain;
        }
        private sealed class Identity : IEqualityComparer<object>
        {
            internal static readonly Identity Instance = new Identity();
            public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
        private readonly Func<object, FieldInfo, Ownership> classify;
        private readonly Func<object, string?> binding;
        private readonly Func<Type, FieldInfo[]> fields;
        private readonly Func<object, FieldInfo, object?> read;
        private readonly Func<Type, bool>? knownOwner;
        private readonly Dictionary<object, string> aliases = new Dictionary<object, string>(Identity.Instance);
        private readonly Dictionary<object, string> carry = new Dictionary<object, string>(Identity.Instance);
        private readonly HashSet<object> canonicalOwners = new HashSet<object>(Identity.Instance), expanded = new HashSet<object>(Identity.Instance);
        private CanonicalS0ObservationSnapshot sample = new CanonicalS0ObservationSnapshot();
        private readonly SortedSet<string> unknown = new SortedSet<string>(StringComparer.Ordinal), unread = new SortedSet<string>(StringComparer.Ordinal), dependencies = new SortedSet<string>(StringComparer.Ordinal);
        private int nodes, characters;
        internal CanonicalS0Observation(Func<object, FieldInfo, Ownership> classify, Func<object, string?> binding,
            Func<Type, FieldInfo[]> fields, Func<object, FieldInfo, object?> read, Func<Type, bool>? knownOwner = null)
        { this.classify = classify; this.binding = binding; this.fields = fields; this.read = read; this.knownOwner = knownOwner; }
        internal CanonicalS0ObservationSnapshot Capture(IEnumerable<Root> roots)
        {
            sample = new CanonicalS0ObservationSnapshot(); aliases.Clear(); carry.Clear(); unknown.Clear(); unread.Clear(); dependencies.Clear(); nodes = 0; characters = 0;
            canonicalOwners.Clear(); expanded.Clear();
            var ordered = roots.OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
            if (ordered.Length > 4096 || ordered.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != ordered.Length) throw new InvalidOperationException("S0 root registry invalid.");
            foreach (var root in ordered.Where(x => x.Domain == Ownership.S0 && x.Value != null && !x.Value.GetType().IsValueType)) canonicalOwners.Add(root.Value!);
            foreach (var root in ordered.Where(x => x.Domain == Ownership.D && x.Value != null && !x.Value.GetType().IsValueType))
            {
                var named = root.Name.Length + ":" + root.Name;
                if (carry.TryGetValue(root.Value!, out var previous)) carry[root.Value!] = previous + "|" + named;
                else carry.Add(root.Value!, named);
            }
            foreach (var root in ordered)
            {
                sample.RootOwnership.Add(root.Name, root.Domain.ToString());
                if (root.Domain == Ownership.D) continue;
                Visit(root.Value, root.Name, root.Domain, 0);
            }
            sample.Unclassified = unknown.ToArray(); sample.Unread = unread.ToArray(); sample.Dependencies = dependencies.ToArray();
            using (var hash = SHA256.Create()) sample.Sha256 = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", sample.Fields.Select(x => x.Key.Length + ":" + x.Key + x.Value.Length + ":" + x.Value))))).Replace("-", "").ToLowerInvariant();
            return sample;
        }
        private void Put(string path, string value)
        {
            characters += path.Length + value.Length;
            if (characters > 8 * 1024 * 1024) throw new InvalidOperationException("S0 character budget exceeded at " + path);
            sample.Fields.Add(path, value);
        }
        private void Visit(object? value, string path, Ownership domain, int depth)
        {
            if (domain == Ownership.Unknown) { unknown.Add(path + ":ownership-unclassified"); return; }
            if (domain == Ownership.Dependency) dependencies.Add(path + ":D-clock-dependency-original-bits-retained");
            if (depth > 40 || ++nodes > 100000) throw new InvalidOperationException("S0 graph budget exceeded at " + path);
            if (value == null) { Put(path, "null"); return; }
            var type = value.GetType();
            if (domain == Ownership.D || carry.ContainsKey(value) && !canonicalOwners.Contains(value))
            {
                var logical = carry.TryGetValue(value, out var registered) ? registered : binding(value);
                if (logical == null) { unknown.Add(path + ":unregistered-D-binding:" + type.FullName); return; }
                Alias(value, path, "D:" + logical); return;
            }
            if (Scalar(value, type) is string scalar)
            {
                if (type == typeof(string))
                {
                    Alias(value, path, scalar);
                    if (carry.TryGetValue(value, out var stringBindings)) Put(path + ".$D-root-bindings", stringBindings);
                }
                else Put(path, scalar);
                return;
            }
            // External objects are always references, never traversed through Unity/native owners.
            var external = binding(value);
            if (external != null) { Alias(value, path, "binding:" + external); return; }
            if (!type.IsValueType)
            {
                Alias(value, path, "node:" + type.FullName);
                if (!expanded.Add(value)) return;
                if (carry.TryGetValue(value, out var sharedBindings)) Put(path + ".$D-root-bindings", sharedBindings);
            }
            if (value is Array array)
            {
                if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > 100000) { unread.Add(path + ":array-layout-or-budget"); return; }
                Put(path + ".$length", array.Length.ToString(CultureInfo.InvariantCulture));
                for (var i = 0; i < array.Length; i++) Visit(array.GetValue(i), path + "[" + i + "]", Ownership.S0, depth + 1);
                return;
            }
            var layout = fields(type);
            if (knownOwner != null ? !knownOwner(type) : layout.Length == 0) unknown.Add(path + ":owner-unclassified:" + type.FullName);
            foreach (var field in layout)
            {
                var fieldPath = path + "." + field.DeclaringType!.FullName + "::" + field.Name;
                var owner = classify(value, field);
                sample.FieldOwnership.Add(fieldPath, owner.ToString());
                if (owner == Ownership.Unknown) { unknown.Add(fieldPath + ":ownership-unclassified"); continue; }
                try { Visit(read(value, field), fieldPath, owner, depth + 1); }
                catch (Exception error) when (!(error is InvalidOperationException)) { unread.Add(fieldPath + ":" + error.GetType().Name); }
            }
        }
        private bool Alias(object value, string path, string token)
        {
            if (aliases.TryGetValue(value, out var first)) { Put(path, token + "|alias:" + first); return false; }
            aliases.Add(value, path); Put(path, token + "|first:" + path); return true;
        }
        private static string? Scalar(object value, Type type)
        {
            if (type == typeof(string)) { var text = (string)value; return "utf16:" + string.Concat(text.Select(c => ((int)c).ToString("x4", CultureInfo.InvariantCulture))); }
            if (type == typeof(float)) return "f32:" + BitConverter.ToString(BitConverter.GetBytes((float)value));
            if (type == typeof(double)) return "f64:" + BitConverter.ToString(BitConverter.GetBytes((double)value));
            if (type == typeof(decimal)) return "decimal:" + string.Join(":", decimal.GetBits((decimal)value).Select(x => x.ToString("x8", CultureInfo.InvariantCulture)));
            if (type == typeof(IntPtr) || type == typeof(UIntPtr)) return null;
            if (type.IsEnum) return type.FullName + ":" + Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
            if (type.IsPrimitive) return type.FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
            return null;
        }
    }
}
