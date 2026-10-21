using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.SceneCheckpoints;

namespace HollowKnightTAS.Runtime.FullRun
{
    // A field-only graph, separate from the observation digest. All allocating
    // types and live roots come from the caller's frozen registry. Import never
    // resolves a payload type name, invokes a constructor or runs game events.
    internal sealed class CanonicalCarryGraphCodec
    {
        internal const int MaximumNodes = 100000, MaximumValues = 1000000;
        private readonly Dictionary<string, Layout> layouts = new Dictionary<string, Layout>(StringComparer.Ordinal);
        private readonly Dictionary<string, Type> types = new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly Dictionary<Type, string> typeIdentities = new Dictionary<Type, string>();
        private readonly string schema;
        private readonly CanonicalListEmptyArrayBindings listEmptyArrays;
        private readonly Dictionary<Type, int> leafSizes = new Dictionary<Type, int>();
        private readonly Func<object, string?>? portability;
        private readonly Func<object, string?>? allocationPortability;
        private readonly Action<CanonicalCarryGraphData>? importValidation;

        internal CanonicalCarryGraphCodec(IEnumerable<Type> registered, string schema, Func<object, string?>? portability = null,
            Func<object, string?>? allocationPortability = null, Action<CanonicalCarryGraphData>? importValidation = null)
        {
            if (string.IsNullOrEmpty(schema)) throw new ArgumentException("Carry schema identity is required.", nameof(schema));
            this.schema = schema + "\nframework-list-static-empty-v1";
            var registeredTypes = registered.Distinct().ToArray();
            listEmptyArrays = new CanonicalListEmptyArrayBindings(registeredTypes);
            this.portability = portability;
            this.allocationPortability = allocationPortability;
            this.importValidation = importValidation;
            foreach (var type in new[] { typeof(bool), typeof(byte), typeof(sbyte), typeof(char), typeof(short), typeof(ushort),
                typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(string), typeof(decimal) })
            { var id = Identity(type); types.Add(id, type); typeIdentities.Add(type, id); }
            foreach (var type in registeredTypes)
            {
                if (type.ContainsGenericParameters || type.IsPointer || type.IsByRef || type == typeof(IntPtr) || type == typeof(UIntPtr)
                    || typeof(Delegate).IsAssignableFrom(type) || typeof(MemberInfo).IsAssignableFrom(type)
                    || typeof(MarshalByRefObject).IsAssignableFrom(type) || type.IsInterface || type.IsAbstract
                    || Hierarchy(type).Any(t => t.FullName == "UnityEngine.Object"))
                    throw new InvalidDataException("Carry allocation type is not managed data: " + type.FullName);
                var id = Identity(type);
                types[id] = type;
                typeIdentities[type] = id;
                if (!IsScalar(type) && !type.IsArray)
                    layouts.Add(id, new Layout(type, Fields(type)));
            }
        }

        internal static string Identity(Type type) => type.Assembly.GetName().Name + ":" + type.FullName;
        private string TypeIdentity(Type type) => typeIdentities.TryGetValue(type, out var id) ? id : Identity(type);
        internal static FieldInfo[] Fields(Type type) => Hierarchy(type).SelectMany(t => t.GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .OrderBy(f => f.DeclaringType!.FullName, StringComparer.Ordinal).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        private static IEnumerable<Type> Hierarchy(Type type)
        { for (var current = type; current != null && current != typeof(object); current = current.BaseType) yield return current; }
        private static string FieldIdentity(FieldInfo field) => Identity(field.DeclaringType!) + "/" + field.Name;
        private static bool IsScalar(Type type) => type == typeof(string) || type == typeof(decimal)
            || type.IsEnum || (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr));

        internal object ResolveExternalBinding(string binding, Func<string, object> other) => listEmptyArrays.Resolve(binding, other);
        internal CanonicalCarryGraphData Capture(IReadOnlyDictionary<string, object> roots,
            Func<object, string?> externalBinding, Action<object, int>? capturedNode = null)
        {
            var nodes = new List<CanonicalCarryGraphData.Node>();
            var pending = new Queue<Tuple<object, int, int>>();
            var seen = new Dictionary<object, int>(new References());
            var externals = new List<CanonicalCarryGraphData.External>();
            var externalValues = new Dictionary<string, object>(StringComparer.Ordinal);
            var externalIds = new Dictionary<object, int>(new References());
            var unsupported = new SortedSet<string>(StringComparer.Ordinal);
            var count = 0;
            int AddNode(object value, int depth, string path)
            {
                if (seen.TryGetValue(value, out var known)) return known;
                if (nodes.Count >= MaximumNodes || depth > 128) throw new InvalidDataException("Carry graph exceeds its bound at " + path
                    + ";nodes=" + nodes.Count + ";depth=" + depth + ";type=" + Identity(value.GetType()));
                var id = nodes.Count + 1;
                seen.Add(value, id);
                capturedNode?.Invoke(value, id);
                nodes.Add(new CanonicalCarryGraphData.Node { Id = id, Type = TypeIdentity(value.GetType()) });
                pending.Enqueue(Tuple.Create(value, id, depth));
                return id;
            }
            CanonicalCarryGraphData.Value Visit(object? value, Type declared, string path, int depth)
            {
                if (++count > MaximumValues || depth > 128) throw new InvalidDataException("Carry field graph exceeds its bound at " + path
                    + ";values=" + count + ";depth=" + depth);
                if (value == null) return new CanonicalCarryGraphData.Value { Kind = "null" };
                var type = value.GetType(); var id = TypeIdentity(type);
                var issue = portability?.Invoke(value);
                if (issue != null) { unsupported.Add(path + ":" + issue); return Unsupported(id, issue); }
                if (type.IsValueType && !declared.IsValueType && IsScalar(type) && types.ContainsKey(id))
                    return new CanonicalCarryGraphData.Value { Kind = "node", Type = id, Reference = AddNode(value, depth, path) };
                if (IsScalar(type))
                {
                    if (!types.ContainsKey(id))
                    { unsupported.Add(path + ":unregistered-scalar:" + id); return Unsupported(id, "unregistered-scalar"); }
                    return new CanonicalCarryGraphData.Value { Kind = "scalar", Type = id, Text = EncodeScalar(value, type) };
                }
                // Plain structs are inline field values, not reference-graph
                // nodes. Keep their original logical field cost. A struct boxed
                // into object/interface storage stays a node to retain aliases.
                if (declared.IsValueType && types.ContainsKey(id) && LeafSize(type) > 0)
                {
                    count = checked(count + LeafSize(type) - 1);
                    if (count > MaximumValues) throw new InvalidDataException("Carry leaf field count exceeds its bound at " + path);
                    return new CanonicalCarryGraphData.Value { Kind = "value", Type = id, Text = EncodeLeaf(value, type) };
                }
                // Root references remain inside the graph, even if the caller
                // also has an external binding for the same singleton.
                if (seen.TryGetValue(value, out var existing))
                    return new CanonicalCarryGraphData.Value { Kind = "node", Type = id, Reference = existing };
                string? binding;
                try { binding = listEmptyArrays.Bind(value) ?? externalBinding(value); }
                catch (InvalidDataException error)
                { unsupported.Add(path + ":" + error.Message); return Unsupported(id, error.Message); }
                if (binding != null)
                {
                    if (type.IsValueType || binding.Length == 0) throw new InvalidDataException("Carry external binding is invalid.");
                    if (externalValues.TryGetValue(binding, out var bound) && !ReferenceEquals(bound, value))
                        throw new InvalidDataException("Carry external identity is ambiguous: " + binding);
                    if (!externalIds.TryGetValue(value, out var ordinal))
                    {
                        ordinal = externals.Count + 1; externalIds.Add(value, ordinal); externalValues.Add(binding, value);
                        externals.Add(new CanonicalCarryGraphData.External { Binding = binding, Type = id });
                    }
                    else if (externals[ordinal - 1].Binding != binding)
                        throw new InvalidDataException("Carry external alias changed identity.");
                    return new CanonicalCarryGraphData.Value { Kind = "external", Type = id, Reference = ordinal };
                }
                if (!types.ContainsKey(id) || (!type.IsArray && !layouts.ContainsKey(id)))
                { unsupported.Add(path + ":unregistered-managed-type:" + id); return Unsupported(id, "unregistered-managed-type"); }
                var allocationIssue = allocationPortability?.Invoke(value);
                if (allocationIssue != null) { unsupported.Add(path + ":" + allocationIssue); return Unsupported(id, allocationIssue); }
                return new CanonicalCarryGraphData.Value { Kind = "node", Type = id, Reference = AddNode(value, depth, path) };
            }
            var rootData = roots.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
            {
                if (string.IsNullOrEmpty(pair.Key) || pair.Value == null || pair.Value.GetType().IsValueType)
                    throw new InvalidDataException("Carry root must bind an existing managed reference.");
                var id = TypeIdentity(pair.Value.GetType());
                if (!layouts.ContainsKey(id)) throw new InvalidDataException("Carry root type is unregistered: " + id);
                if (portability?.Invoke(pair.Value) is string rootIssue) unsupported.Add(pair.Key + ":" + rootIssue);
                return new CanonicalCarryGraphData.Root { Binding = pair.Key, Node = AddNode(pair.Value, 0, pair.Key) };
            }).ToArray();
            if (rootData.Length == 0) throw new InvalidDataException("Carry roots are absent.");
            while (pending.Count != 0)
            {
                var entry = pending.Dequeue(); var owner = entry.Item1; var node = nodes[entry.Item2 - 1];
                var path = "node[" + entry.Item2 + "]";
                var frameworkEmpty = listEmptyArrays.ObserveList(owner);
                if (frameworkEmpty != null && seen.ContainsKey(frameworkEmpty))
                    unsupported.Add(path + ":framework-list-static-empty-was-allocated-before-original-list-provenance");
                if (IsScalar(owner.GetType()))
                { node.Kind = "boxed-scalar"; node.Text = EncodeScalar(owner, owner.GetType()); }
                else if (owner is Array array)
                {
                    if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > MaximumNodes)
                        throw new InvalidDataException("Carry array shape exceeds its bound.");
                    node.Kind = "array"; node.Items = new CanonicalCarryGraphData.Value[array.Length];
                    for (var i = 0; i < array.Length; i++) node.Items[i] = Visit(array.GetValue(i), array.GetType().GetElementType()!, path + "[" + i + "]", entry.Item3 + 1);
                }
                else
                {
                    node.Kind = "object"; var layout = layouts[node.Type];
                    node.Fields = new CanonicalCarryGraphData.Field[layout.Fields.Length];
                    for (var index = 0; index < layout.Fields.Length; index++) {
                        var field = layout.Fields[index]; var identity = layout.FieldIdentities[index];
                        node.Fields[index] = new CanonicalCarryGraphData.Field { Name = identity,
                            Value = Visit(field.GetValue(owner), field.FieldType, path + "/" + identity, entry.Item3 + 1) };
                    }
                }
            }
            return new CanonicalCarryGraphData { Schema = schema, Roots = rootData, Nodes = nodes.ToArray(),
                Externals = externals.ToArray(), Unsupported = unsupported.ToArray() };
        }
        private static CanonicalCarryGraphData.Value Unsupported(string type, string reason)
            => new CanonicalCarryGraphData.Value { Kind = "unsupported", Type = type, Text = reason };

        internal byte[] Encode(CanonicalCarryGraphData value) => CanonicalCarryGraphWire.Encode(value);
        internal CanonicalCarryGraphData Decode(byte[] bytes) => CanonicalCarryGraphWire.Decode(bytes);

        // Freeze the caller's data by a bounded canonical round trip, then
        // resolve every value before exposing a single live field write.
        internal Action PrepareApply(CanonicalCarryGraphData input, IReadOnlyDictionary<string, object> roots,
            Func<string, object> resolveExternal, Action<PreparedView>? preflight = null)
            => PrepareGraph(input, roots, resolveExternal, preflight).Apply;

        internal CanonicalPreparedWritePlan PrepareGraph(CanonicalCarryGraphData input, IReadOnlyDictionary<string, object> roots,
            Func<string, object> resolveExternal, Action<PreparedView>? preflight = null)
            => PrepareOwnedGraph(Decode(Encode(input)), roots, resolveExternal, preflight);
        // Caller owns a fresh decoded packet on the parked game thread. All
        // schema/coverage/layout/reachability/alias checks still run below.
        // This avoids a redundant JSON encode/decode of already owned data.
        internal CanonicalPreparedWritePlan PrepareOwnedGraph(CanonicalCarryGraphData value, IReadOnlyDictionary<string, object> roots,
            Func<string, object> resolveExternal, Action<PreparedView>? preflight = null,
            Action<ExternalRebinding>? exposeExternalRebinding = null,
            IReadOnlyCollection<string>? lifecycleWriteRoots = null, Action<PreparedLifecycleWrites>? exposeLifecycleWrites = null)
        {
            // Domain checks receive the frozen input before allocation or
            // external resolution. All generic checks still run afterwards.
            importValidation?.Invoke(value);
            if (value.Version != 1 || value.Schema != schema || value.Unsupported == null || value.Unsupported.Length != 0
                || value.Nodes == null || value.Nodes.Length == 0 || value.Nodes.Length > MaximumNodes || value.Roots == null
                || value.Externals == null || value.Externals.Length > MaximumNodes
                || !value.Roots.Select(r => r.Binding).SequenceEqual(roots.Keys.OrderBy(key => key, StringComparer.Ordinal)))
                throw new InvalidDataException("Carry graph schema, roots or coverage differs.");
            var objects = new object[value.Nodes.Length];
            var nodeTypes = new Type[value.Nodes.Length];
            for (var i = 0; i < value.Nodes.Length; i++)
            {
                var node = value.Nodes[i];
                if (node == null || node.Id != i + 1 || node.Type == null || !types.TryGetValue(node.Type, out var type)
                    || node.Kind != (type.IsArray ? "array" : IsScalar(type) ? "boxed-scalar" : "object")
                    || (IsScalar(type) ? node.Text == null || node.Items != null || node.Fields != null || !type.IsValueType
                        : type.IsArray ? node.Items == null || node.Items.Length > MaximumNodes || node.Fields != null || node.Text != null
                        : node.Items != null || node.Text != null || node.Fields == null || !layouts.ContainsKey(node.Type)
                        || !node.Fields.Select(f => f.Name).SequenceEqual(layouts[node.Type].FieldIdentities)))
                    throw new InvalidDataException("Carry node type or field layout differs: " + (i + 1));
                nodeTypes[i] = type;
            }
            if ((lifecycleWriteRoots == null) != (exposeLifecycleWrites == null))
                throw new InvalidDataException("Lifecycle roots and controller must be paired.");
            var lifecycleNames = lifecycleWriteRoots == null ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(lifecycleWriteRoots, StringComparer.Ordinal);
            if (lifecycleWriteRoots != null && (lifecycleNames.Count == 0 || lifecycleNames.Count != lifecycleWriteRoots.Count
                || lifecycleNames.Any(name => name == null || !roots.ContainsKey(name))))
                throw new InvalidDataException("Lifecycle root selection is empty, duplicated or unknown.");
            var lifecycleNodes = new HashSet<int>(value.Roots.Where(root => lifecycleNames.Contains(root.Binding)).Select(root => root.Node));
            if (value.Roots.Any(root => lifecycleNodes.Contains(root.Node) && !lifecycleNames.Contains(root.Binding)))
                throw new InvalidDataException("Lifecycle root selection omits an owner alias.");
            var boundNodes = new HashSet<int>(); var boundObjects = new Dictionary<object, int>(new References());
            foreach (var root in value.Roots)
            {
                if (root.Node < 1 || root.Node > objects.Length || roots[root.Binding] == null
                    || roots[root.Binding].GetType() != nodeTypes[root.Node - 1] || nodeTypes[root.Node - 1].IsValueType
                    || nodeTypes[root.Node - 1].IsArray)
                    throw new InvalidDataException("Carry live root binding differs: " + root.Binding);
                var owner = roots[root.Binding];
                if (objects[root.Node - 1] != null && !ReferenceEquals(objects[root.Node - 1], owner)
                    || boundObjects.TryGetValue(owner, out var prior) && prior != root.Node)
                    throw new InvalidDataException("Carry live root aliases differ.");
                objects[root.Node - 1] = owner; boundNodes.Add(root.Node); boundObjects[owner] = root.Node;
            }
            for (var i = 0; i < objects.Length; i++)
                if (objects[i] == null)
                {
                    var type = nodeTypes[i];
#pragma warning disable SYSLIB0050
                    objects[i] = IsScalar(type) ? DecodeBox(value.Nodes[i].Text!, type)
                        : type.IsArray ? Array.CreateInstance(type.GetElementType()!, value.Nodes[i].Items!.Length)
                        : FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
                }
            var external = new object[value.Externals.Length];
            var externalKeys = new HashSet<string>(StringComparer.Ordinal); var externalObjects = new HashSet<object>(new References());
            for (var i = 0; i < external.Length; i++)
            {
                var saved = value.Externals[i];
                if (saved == null || string.IsNullOrEmpty(saved.Binding) || !externalKeys.Add(saved.Binding))
                    throw new InvalidDataException("Carry external identity is invalid or duplicated.");
                var owner = listEmptyArrays.Resolve(saved.Binding, resolveExternal);
                if (owner == null || owner.GetType().IsValueType || TypeIdentity(owner.GetType()) != saved.Type
                    || !externalObjects.Add(owner) || boundObjects.ContainsKey(owner))
                    throw new InvalidDataException("Carry external type or alias binding differs: " + saved.Binding);
                external[i] = owner;
            }
            var visitedNodes = new HashSet<int>(); var visitedExternal = new HashSet<int>(); var valueCount = 0;
            object? Read(CanonicalCarryGraphData.Value item, Type expected)
            {
                if (++valueCount > MaximumValues) throw new InvalidDataException("Carry value count exceeds its bound.");
                object? result;
                switch (item.Kind)
                {
                    case "null":
                        if (item.Type != null || item.Text != null || item.Reference != 0 || expected.IsValueType && Nullable.GetUnderlyingType(expected) == null)
                            throw new InvalidDataException("Carry null field is invalid.");
                        return null;
                    case "scalar":
                        if (item.Reference != 0 || item.Type == null || !types.TryGetValue(item.Type, out var scalar) || !IsScalar(scalar)
                            || item.Text == null) throw new InvalidDataException("Carry scalar type is invalid.");
                        result = DecodeScalar(item.Text, scalar); break;
                    case "value":
                        if (item.Reference != 0 || item.Type == null || !types.TryGetValue(item.Type, out var leaf)
                            || !expected.IsValueType || LeafSize(leaf) <= 0 || item.Text == null)
                            throw new InvalidDataException("Carry inline value type is invalid.");
                        valueCount = checked(valueCount + LeafSize(leaf) - 1);
                        if (valueCount > MaximumValues) throw new InvalidDataException("Carry leaf field count exceeds its bound.");
                        result = DecodeLeaf(item.Text, leaf); break;
                    case "node":
                        if (item.Text != null || item.Reference < 1 || item.Reference > objects.Length
                            || item.Type != value.Nodes[item.Reference - 1].Type) throw new InvalidDataException("Carry managed reference is invalid.");
                        if (expected.IsValueType && (IsScalar(nodeTypes[item.Reference - 1]) || LeafSize(nodeTypes[item.Reference - 1]) > 0))
                            throw new InvalidDataException("Carry boxed value cannot replace an inline field.");
                        visitedNodes.Add(item.Reference); result = objects[item.Reference - 1]; break;
                    case "external":
                        if (item.Text != null || item.Reference < 1 || item.Reference > external.Length
                            || item.Type != value.Externals[item.Reference - 1].Type) throw new InvalidDataException("Carry external reference is invalid.");
                        visitedExternal.Add(item.Reference); result = external[item.Reference - 1]; break;
                    default: throw new InvalidDataException("Unresolved carry field kind.");
                }
                if (!expected.IsInstanceOfType(result) && !(Nullable.GetUnderlyingType(expected)?.IsInstanceOfType(result) ?? false))
                    throw new InvalidDataException("Carry value does not match its declared field type.");
                if (item.Kind != "node" && portability?.Invoke(result!) is string issue)
                    throw new InvalidDataException("Carry value is not portable: " + issue);
                return result;
            }
            // Fill detached nodes first. Value-type nodes must be filled before
            // their parent copies the boxed struct into a field or array.
            var status = new byte[objects.Length]; var writes = new List<Action>();
            var lifecycleInitial = new List<Action>(); var lifecycleFreeze = new List<Action>();
            var beforeChecks = new List<Action>(); var afterChecks = new List<Action>();
            var externalSlots = new List<Tuple<int, Func<object?>, Action<object>>>();
            var copiedExternal = new HashSet<int>();
            var rootOwners = roots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            void CheckSlot(object? expectedValue, object? currentValue, Type declared, string name)
            {
                if (!SameRawSlot(expectedValue, currentValue, declared, 0))
                    throw new InvalidOperationException("Carry prepared live slot changed or readback differs: " + name);
            }
            var preparedFields = preflight == null ? null : new object?[objects.Length][];
            void Fill(int index, int depth)
            {
                if (depth > 128) throw new InvalidDataException("Carry restore graph exceeds its depth bound.");
                if (status[index] == 2) return;
                if (status[index] == 1)
                { if (nodeTypes[index].IsValueType) throw new InvalidDataException("Carry value-type cycle is invalid."); return; }
                status[index] = 1;
                var node = value.Nodes[index]; var owner = objects[index];
                object? Prepared(CanonicalCarryGraphData.Value item, Type expected)
                {
                    var result = Read(item, expected);
                    if (item.Kind == "node") Fill(item.Reference - 1, depth + 1);
                    return result;
                }
                if (node.Kind == "boxed-scalar") { }
                else if (node.Kind == "array")
                {
                    var array = (Array)owner; var element = nodeTypes[index].GetElementType()!;
                    // Capture and import both prohibit arrays as live roots.
                    // Every array here is detached until its owning field is
                    // applied; preparation cannot mutate an existing array.
                    for (var i = 0; i < node.Items!.Length; i++) {
                        array.SetValue(Prepared(node.Items[i], element), i);
                        if (node.Items[i].Kind == "external") {
                            var at = i;
                            externalSlots.Add(Tuple.Create(node.Items[i].Reference - 1,
                                (Func<object?>)(() => array.GetValue(at)), (Action<object>)(next => array.SetValue(next, at))));
                        }
                    }
                }
                else
                {
                    var layout = layouts[node.Type];
                    if (preparedFields != null) preparedFields[index] = new object?[layout.Fields.Length];
                    for (var i = 0; i < layout.Fields.Length; i++)
                    {
                        var field = layout.Fields[i]; var fieldValue = Prepared(node.Fields![i].Value, field.FieldType);
                        var externalIndex = node.Fields[i].Value.Kind == "external" ? node.Fields[i].Value.Reference - 1 : -1;
                        if (externalIndex >= 0) {
                            if (nodeTypes[index].IsValueType) copiedExternal.Add(externalIndex);
                            var at = i;
                            externalSlots.Add(Tuple.Create(externalIndex, (Func<object?>)(() => field.GetValue(owner)),
                                (Action<object>)(next => { field.SetValue(owner, next); if (preparedFields != null) preparedFields[index][at] = next; })));
                        }
                        if (preparedFields != null) preparedFields[index][i] = fieldValue;
                        if (boundNodes.Contains(index + 1)) {
                            var previous = field.GetValue(owner);
                            var fieldIdentity = layout.FieldIdentities[i];
                            Action InitialCheck = () => CheckSlot(previous, field.GetValue(owner), field.FieldType, fieldIdentity + "/initial");
                            if (lifecycleNodes.Contains(index + 1)) {
                                lifecycleInitial.Add(InitialCheck);
                                lifecycleFreeze.Add(() => {
                                    var before = field.GetValue(owner);
                                    beforeChecks.Add(() => CheckSlot(before, field.GetValue(owner), field.FieldType, fieldIdentity + "/post-lifecycle"));
                                });
                            } else beforeChecks.Add(InitialCheck);
                            writes.Add(() => field.SetValue(owner, fieldValue));
                            afterChecks.Add(() => CheckSlot(externalIndex >= 0 ? external[externalIndex] : fieldValue,
                                field.GetValue(owner), field.FieldType, fieldIdentity));
                        }
                        else field.SetValue(owner, fieldValue);
                    }
                }
                if (portability?.Invoke(owner) is string issue) throw new InvalidDataException("Carry node is not portable: " + issue);
                if (!boundNodes.Contains(index + 1) && allocationPortability?.Invoke(owner) is string allocationIssue)
                    throw new InvalidDataException("Carry allocated node is not portable: " + allocationIssue);
                status[index] = 2;
            }
            foreach (var root in value.Roots) { visitedNodes.Add(root.Node); Fill(root.Node - 1, 0); }
            if (visitedNodes.Count != objects.Length || visitedExternal.Count != external.Length)
                throw new InvalidDataException("Carry graph contains unreachable nodes or bindings.");
            if (preflight != null) preflight(new PreparedView(roots, objects, preparedFields!, value.Nodes.Select(node =>
                layouts.TryGetValue(node.Type, out var layout) ? layout.Fields : Array.Empty<FieldInfo>()).ToArray()));
            void GuardOwnersAndFields()
            {
                if (roots.Count != rootOwners.Count || rootOwners.Any(pair => !roots.TryGetValue(pair.Key, out var current)
                    || !ReferenceEquals(pair.Value, current))) throw new InvalidOperationException("Carry prepared root owners changed.");
                foreach (var check in beforeChecks) check();
            }
            GuardOwnersAndFields();
            foreach (var check in lifecycleInitial) check();
            PreparedLifecycleWrites? lifecycle = lifecycleWriteRoots == null ? null
                : new PreparedLifecycleWrites(GuardOwnersAndFields, () => { foreach (var check in lifecycleInitial) check(); },
                    () => { foreach (var freeze in lifecycleFreeze) freeze(); });
            void Guard() {
                if (lifecycle == null) { GuardOwnersAndFields(); return; }
                lifecycle.GuardReady(GuardOwnersAndFields);
            }
            var plan = new CanonicalPreparedWritePlan(Guard, () => { foreach (var write in writes) write(); },
                () => {
                    void Verify() { foreach (var check in afterChecks) check(); }
                    if (lifecycle == null) Verify(); else lifecycle.GuardReady(Verify);
                }, rootOwners.Values.ToArray());
            if (lifecycle != null) exposeLifecycleWrites!(lifecycle);
            exposeExternalRebinding?.Invoke(new ExternalRebinding(value.Externals, external, externalSlots, copiedExternal,
                rootOwners.Values.ToArray(), plan));
            return plan;
        }

        // A single explicit original-lifecycle window. Failed admission or use
        // permanently poisons the controller; no failed field guard is rearmed.
        internal sealed class PreparedLifecycleWrites
        {
            private readonly Action guard, initial, freeze;
            private int state, thread;
            internal PreparedLifecycleWrites(Action guard, Action initial, Action freeze)
            { this.guard = guard; this.initial = initial; this.freeze = freeze; }
            private void Run(int expected, Action action, int next) {
                try {
                    if (state != expected || (thread != 0 && thread != System.Threading.Thread.CurrentThread.ManagedThreadId))
                        throw new InvalidOperationException("Lifecycle write phase is consumed, faulted or on another thread.");
                    action(); state = next;
                } catch { state = -1; throw; }
            }
            internal void BeginOriginalLifecycle(Action admission) => Run(0, () => {
                thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                admission(); guard(); initial();
            }, 1);
            internal void PrepareWritesAfterOriginalLifecycle(Action admission) => Run(1, () => {
                admission(); guard(); freeze();
            }, 2);
            internal void GuardReady(Action check) => Run(2, check, 2);
        }

        // Only the exact external slots already validated by this source graph
        // may move to a newly produced owner. No roots or managed nodes change.
        // Preparation is available only after the original graph applied and
        // passed its full readback. Any failed application remains terminal.
        internal sealed class ExternalRebinding
        {
            private readonly CanonicalCarryGraphData.External[] source;
            private readonly object[] external;
            private readonly List<Tuple<int, Func<object?>, Action<object>>> slots;
            private readonly CanonicalPreparedWritePlan parent;
            private readonly HashSet<int> copiedExternal;
            private readonly object[] rootOwners;
            private bool consumed;
            private bool markerConsumed, markerCompleted, parentConsumed, parentCompleted, mapKeyConsumed;
            private CanonicalPreparedWritePlan? parentPlan;
            private CanonicalPreparedWritePlan? markerPlan;
            private bool geoCompleted, terminal;
            private CanonicalPreparedWritePlan? geoPlan;
            private readonly HashSet<int> transferred = new HashSet<int>();
            internal ExternalRebinding(CanonicalCarryGraphData.External[] source, object[] external,
                List<Tuple<int, Func<object?>, Action<object>>> slots, HashSet<int> copiedExternal,
                object[] rootOwners, CanonicalPreparedWritePlan parent)
            { this.source = source; this.external = external; this.slots = slots; this.parent = parent;
                this.copiedExternal = copiedExternal; this.rootOwners = rootOwners; }
            internal CanonicalPreparedWritePlan Prepare(IReadOnlyDictionary<string, object> replacements)
            {
                if (terminal || consumed || markerConsumed || geoCompleted || replacements.Count == 0) {
                    terminal = true;
                    throw new InvalidOperationException("Deferred external phase is absent or consumed.");
                }
                parent.VerifyReadback(); consumed = true;
                try { geoPlan = PrepareExact(replacements); return geoPlan; } catch { terminal = true; throw; }
            }
            internal void CompleteGeoPhase()
            {
                if (terminal || geoCompleted || markerConsumed) {
                    terminal = true;
                    throw new InvalidOperationException("Geo external completion is consumed or terminal.");
                }
                try { geoPlan?.VerifyReadback(); parent.VerifyReadback(); consumed = true; geoCompleted = true; } catch { terminal = true; throw; }
            }
            // Marker is the second finite original producer. The original Geo
            // phase stays single-use; no external slot can be transferred twice.
            internal CanonicalPreparedWritePlan PrepareMarker(IReadOnlyDictionary<string, object> replacements)
            {
                if (terminal || !geoCompleted || markerConsumed || markerCompleted || mapKeyConsumed || replacements.Count == 0) {
                    terminal = true;
                    throw new InvalidOperationException("Marker external phase is out of order, absent or consumed.");
                }
                markerConsumed = true;
                try { parent.VerifyReadback(); markerPlan = PrepareExact(replacements); return markerPlan; } catch { terminal = true; throw; }
            }
            internal void CompleteMarkerPhase()
            {
                if (terminal || !geoCompleted || markerCompleted || mapKeyConsumed) {
                    terminal = true; throw new InvalidOperationException("Marker external completion is out of order or consumed.");
                }
                try { markerPlan?.VerifyReadback(); parent.VerifyReadback(); markerCompleted = true; }
                catch { terminal = true; throw; }
            }
            internal CanonicalPreparedWritePlan PrepareMapKeyParents(IReadOnlyDictionary<string, object> replacements)
            {
                if (terminal || !markerCompleted || parentConsumed || parentCompleted || mapKeyConsumed || replacements.Count == 0) {
                    terminal = true; throw new InvalidOperationException("Map Key parent external phase is out of order, absent or consumed.");
                }
                parentConsumed = true;
                try { parent.VerifyReadback(); parentPlan = PrepareExact(replacements); return parentPlan; }
                catch { terminal = true; throw; }
            }
            internal void CompleteMapKeyParentPhase()
            {
                if (terminal || !markerCompleted || parentCompleted || mapKeyConsumed) {
                    terminal = true; throw new InvalidOperationException("Map Key parent external completion is out of order or consumed.");
                }
                try { parentPlan?.VerifyReadback(); parent.VerifyReadback(); parentCompleted = true; }
                catch { terminal = true; throw; }
            }
            internal CanonicalPreparedWritePlan PrepareMapKey(IReadOnlyDictionary<string, object> replacements)
            {
                if (terminal || !parentCompleted || mapKeyConsumed || replacements.Count == 0) {
                    terminal = true; throw new InvalidOperationException("Map Key external phase is out of order, absent or consumed.");
                }
                mapKeyConsumed = true;
                try { parent.VerifyReadback(); return PrepareExact(replacements); }
                catch { terminal = true; throw; }
            }
            private CanonicalPreparedWritePlan PrepareExact(IReadOnlyDictionary<string, object> replacements)
            {
                var proposed = (object[])external.Clone(); var selected = new HashSet<int>();
                foreach (var pair in replacements) {
                    var index = Array.FindIndex(source, item => item.Binding == pair.Key);
                    if (index < 0 || transferred.Contains(index) || pair.Value == null || Identity(pair.Value.GetType()) != source[index].Type
                        || ReferenceEquals(pair.Value, external[index]) || rootOwners.Any(owner => ReferenceEquals(owner, pair.Value)))
                        throw new InvalidDataException("Deferred external replacement has no distinct exact source binding: " + pair.Key);
                    if (copiedExternal.Contains(index))
                        throw new InvalidDataException("Deferred external inside a copied value node needs an explicit owner binding.");
                    proposed[index] = pair.Value; selected.Add(index);
                }
                if (proposed.Distinct(new References()).Count() != proposed.Length)
                    throw new InvalidDataException("Deferred external replacement collapses a source alias.");
                var changed = slots.Where(slot => selected.Contains(slot.Item1)).ToArray();
                if (selected.Any(index => !changed.Any(slot => slot.Item1 == index)))
                    throw new InvalidDataException("Deferred external replacement has no reachable field slot.");
                foreach (var index in selected) transferred.Add(index);
                void Guard() {
                    try {
                        if (terminal) throw new InvalidOperationException("Deferred external phase is terminal.");
                        parent.VerifyReadback();
                        foreach (var slot in changed) if (!ReferenceEquals(slot.Item2(), external[slot.Item1]))
                            throw new InvalidOperationException("Deferred external source slot changed before application.");
                    } catch { terminal = true; throw; }
                }
                void Verify() {
                    try {
                        if (terminal) throw new InvalidOperationException("Deferred external phase is terminal.");
                        foreach (var slot in changed) if (!ReferenceEquals(slot.Item2(), proposed[slot.Item1]))
                            throw new InvalidOperationException("Deferred external source slot readback differs.");
                        parent.VerifyReadback();
                    } catch { terminal = true; throw; }
                }
                return new CanonicalPreparedWritePlan(Guard, () => {
                    try {
                        foreach (var slot in changed) slot.Item3(proposed[slot.Item1]);
                        foreach (var index in selected) external[index] = proposed[index];
                    } catch { terminal = true; throw; }
                }, Verify);
            }
        }

        // Declared reference slots retain identity; value slots compare every
        // frozen raw field and scalar bit, never user Equals or a property.
        internal bool SameRawSlot(object? expected, object? current, Type declared, int depth)
        {
            if (!declared.IsValueType) return ReferenceEquals(expected, current);
            if (expected == null || current == null) return expected == null && current == null;
            if (depth > 128 || expected.GetType() != current.GetType()) return false;
            var type = Nullable.GetUnderlyingType(declared) ?? declared;
            if (IsScalar(type)) return SameScalarBits(expected, current, type);
            if (!layouts.TryGetValue(TypeIdentity(type), out var layout)) throw new InvalidDataException("Carry stale-check value layout is absent.");
            return layout.Fields.All(field => SameRawSlot(field.GetValue(expected), field.GetValue(current), field.FieldType, depth + 1));
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct SingleBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal float Value;
            [System.Runtime.InteropServices.FieldOffset(0)] internal uint Bits;
        }
        private static bool SameScalarBits(object expected, object current, Type type)
        {
            if (type == typeof(float)) return new SingleBits { Value = (float)expected }.Bits == new SingleBits { Value = (float)current }.Bits;
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)expected) == BitConverter.DoubleToInt64Bits((double)current);
            if (type == typeof(decimal)) {
                var left = decimal.GetBits((decimal)expected); var right = decimal.GetBits((decimal)current);
                for (var i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
                return true;
            }
            // This is reached only for a frozen scalar value type, with equal
            // runtime types checked above: primitive integers/bool/char or enum.
            // Their built-in Equals cannot call an application override.
            return expected.Equals(current);
        }

        // Reads decoded field values, including proposed fields of existing
        // roots. Reading those roots with reflection would see preparation
        // state instead of source state. No live writes or property getters
        // are exposed here; callers can validate consumer aliases before apply.
        internal sealed class PreparedView
        {
            private readonly Dictionary<string, object> roots;
            private readonly Dictionary<object, int> nodes = new Dictionary<object, int>(new References());
            private readonly object?[][] values;
            private readonly FieldInfo[][] declarations;
            private readonly object[] objects;
            internal PreparedView(IReadOnlyDictionary<string, object> roots, object[] objects,
                object?[][] values, FieldInfo[][] declarations)
            {
                this.roots = roots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                this.values = values; this.declarations = declarations;
                this.objects = objects;
                for (var index = 0; index < objects.Length; index++) nodes.Add(objects[index], index);
            }
            internal object Root(string name) => roots.TryGetValue(name, out var root) ? root
                : throw new InvalidDataException("Prepared carry root is absent: " + name);
            // A later domain may share an actual object allocated by this graph.
            // The caller must consume it only after this plan's Apply/readback;
            // this exposes identities, never bypasses the plan's owner guards.
            internal object Node(int id) => id > 0 && id <= objects.Length ? objects[id - 1]
                : throw new InvalidDataException("Prepared carry node identity is outside its source graph.");
            internal object? Field(object owner, Type declaringType, string name)
            {
                if (!nodes.TryGetValue(owner, out var node) || values[node] == null)
                    throw new InvalidDataException("Prepared carry field owner is outside the managed graph.");
                for (var index = 0; index < declarations[node].Length; index++)
                    if (declarations[node][index].DeclaringType == declaringType && declarations[node][index].Name == name)
                        return values[node][index];
                throw new InvalidDataException("Prepared carry field is absent: " + declaringType.FullName + "/" + name);
            }
        }

        private static string EncodeScalar(object value, Type type)
        {
            if (type == typeof(string)) return (string)value;
            if (type == typeof(float)) return BitConverter.ToUInt32(BitConverter.GetBytes((float)value), 0).ToString("x8", CultureInfo.InvariantCulture);
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)value).ToString("x16", CultureInfo.InvariantCulture);
            if (type == typeof(decimal)) return string.Join(",", decimal.GetBits((decimal)value).Select(v => v.ToString("x8", CultureInfo.InvariantCulture)));
            if (type == typeof(char)) return ((int)(char)value).ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(type.IsEnum ? Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture) : value,
                CultureInfo.InvariantCulture)!;
        }
        private int LeafSize(Type type)
        {
            if (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr) || type.IsEnum || type == typeof(decimal)) return 1;
            if (!type.IsValueType || !layouts.TryGetValue(TypeIdentity(type), out var layout)) return 0;
            if (leafSizes.TryGetValue(type, out var cached)) return cached;
            var total = 1;
            foreach (var field in layout.Fields)
            {
                var size = LeafSize(field.FieldType);
                if (size == 0) { leafSizes.Add(type, 0); return 0; }
                total = checked(total + size);
            }
            if (layout.Fields.Length == 0) total = 0;
            leafSizes.Add(type, total); return total;
        }
        private string EncodeLeaf(object value, Type type)
        {
            var parts = new List<string>();
            void Append(object owner, Type current)
            {
                if (IsScalar(current)) { parts.Add(EncodeScalar(owner, current)); return; }
                foreach (var field in layouts[TypeIdentity(current)].Fields) Append(field.GetValue(owner)!, field.FieldType);
            }
            Append(value, type); return string.Join("|", parts);
        }
        private object DecodeLeaf(string text, Type type)
        {
            var parts = text.Split('|'); var cursor = 0;
            object Read(Type current)
            {
                if (IsScalar(current))
                {
                    if (cursor >= parts.Length) throw new InvalidDataException("Carry leaf bits are incomplete.");
                    return DecodeScalar(parts[cursor++], current);
                }
#pragma warning disable SYSLIB0050
                var result = FormatterServices.GetUninitializedObject(current);
#pragma warning restore SYSLIB0050
                foreach (var field in layouts[TypeIdentity(current)].Fields) field.SetValue(result, Read(field.FieldType));
                return result;
            }
            var value = Read(type);
            if (cursor != parts.Length || EncodeLeaf(value, type) != text) throw new InvalidDataException("Carry leaf bits are noncanonical.");
            return value;
        }
        private static object DecodeScalar(string text, Type type)
        {
            object value;
            try
            {
                if (type == typeof(string)) value = text;
                else if (type == typeof(float)) value = BitConverter.ToSingle(BitConverter.GetBytes(uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture)), 0);
                else if (type == typeof(double)) value = BitConverter.Int64BitsToDouble(long.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                else if (type == typeof(decimal)) value = new decimal(text.Split(',').Select(part => int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray());
                else if (type == typeof(char)) value = (char)ushort.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
                else if (type.IsEnum) value = Enum.ToObject(type, Convert.ChangeType(text, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)!);
                else value = Convert.ChangeType(text, type, CultureInfo.InvariantCulture)!;
            }
            catch (Exception error) when (error is FormatException || error is OverflowException || error is ArgumentException)
            { throw new InvalidDataException("Invalid carry scalar bits.", error); }
            if (EncodeScalar(value, type) != text) throw new InvalidDataException("Noncanonical carry scalar.");
            return value;
        }
        private static object DecodeBox(string text, Type type)
        {
            // Array.GetValue returns a fresh boxed value. Distinct source boxes
            // must not collapse merely because their scalar bits are equal.
            var array = Array.CreateInstance(type, 1);
            array.SetValue(DecodeScalar(text, type), 0);
            return array.GetValue(0)!;
        }
        private sealed class Layout
        {
            internal Layout(Type type, FieldInfo[] fields) { Type = type; Fields = fields; FieldIdentities = fields.Select(FieldIdentity).ToArray(); }
            internal Type Type { get; }
            internal FieldInfo[] Fields { get; }
            internal string[] FieldIdentities { get; }
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }

    [Serializable]
    internal sealed class CanonicalCarryGraphData
    {
        public int Version = 1;
        public string Schema = "";
        public Root[] Roots = null!;
        public Node[] Nodes = null!;
        public External[] Externals = null!;
        public string[] Unsupported = null!;
        [Serializable] internal struct Root { public string Binding; public int Node; }
        [Serializable] internal sealed class Node
        { public int Id; public string Type = "", Kind = ""; public string? Text; public Field[]? Fields; public Value[]? Items; }
        [Serializable] internal struct Field { public string Name; public Value Value; }
        [Serializable] internal struct Value { public string Kind; public string? Type, Text; public int Reference; }
        [Serializable] internal sealed class External { public string Binding = "", Type = ""; }
    }
}
