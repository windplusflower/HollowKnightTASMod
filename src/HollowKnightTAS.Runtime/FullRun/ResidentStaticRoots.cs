using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Module ownership is supplied by the boundary coordinator. This reader
    // contains no game type/path/field whitelist and never runs a game cctor.
    internal static class ResidentStaticRoots
    {
        internal enum ModuleOwnership { Gameplay, ControlPlane, Runtime }
        internal enum Initialization { Dormant, Initialized, Failed }
        private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaximumTypes = 100000, MaximumSlots = 100000;
        private static readonly Lazy<MonoInitializationReader> MonoReader = new Lazy<MonoInitializationReader>(() => new MonoInitializationReader());
        private static readonly object SchemaGate = new object();
        private static readonly Dictionary<Type, StaticSchema> Schemas = new Dictionary<Type, StaticSchema>();
        private static readonly Lazy<string> ToolDtoContract = new Lazy<string>(() => JsonConvert.SerializeObject(
            new ResidentManagedTypeCatalog.TypeReference {
                Kind = "array", Name = "metadata-preparation", Rank = 1, SzArray = true,
                Element = new ResidentManagedTypeCatalog.TypeReference { Kind = "type", Name = "metadata-preparation" }
            }, Formatting.None));

        [Serializable] internal sealed class Data
        {
            public int Version = 1;
            public string RuntimeAbi = "";
            public ModuleData[] Modules = Array.Empty<ModuleData>();
            public TypeData[] Types = Array.Empty<TypeData>();
            public string[] Unsupported = Array.Empty<string>();
        }
        [Serializable] internal sealed class ModuleData
        {
            public string Assembly = "", Module = "", Ownership = "";
        }
        [Serializable] internal sealed class TypeData
        {
            public string Key = "", Initialization = "";
            public ResidentManagedTypeCatalog.TypeReference Type = null!;
            public FieldData[] Fields = Array.Empty<FieldData>();
        }
        [Serializable] internal sealed class FieldData
        {
            public string Key = "", Name = "", Signature = "", Storage = "";
            public int Token, Attributes;
        }
        // Typed slots are essential: boxing an entire value-type static in an
        // object slot would invent a reference alias that does not exist in CLR.
        internal sealed class Slot<T> { public T Value = default!; }
        internal sealed class Observation
        {
            internal readonly Type Type;
            internal readonly Initialization State;
            internal readonly bool NativeVtableAbsent;
            internal readonly IntPtr NativeClass;
            internal readonly IntPtr NativeType, NativeGenericClass, NativeVtable;
            internal Observation(Type type, Initialization state, bool nativeVtableAbsent = false, IntPtr nativeClass = default, IntPtr nativeGenericClass = default, IntPtr nativeVtable = default)
            { Type = type; State = state; NativeVtableAbsent = nativeVtableAbsent; NativeClass = nativeClass;
                NativeType = nativeClass == IntPtr.Zero ? IntPtr.Zero : type.TypeHandle.Value; NativeGenericClass = nativeGenericClass; NativeVtable = nativeVtable; }
        }
        internal interface IInitializationReader
        {
            string AbiIdentity { get; }
            Observation[] Read(Assembly[] gameplayModules);
        }
        internal interface IMetadataShapeReader
        {
            Type[] ReadPreparationShapes(Assembly[] gameplayModules);
        }

        internal static CaptureResult Capture(Func<Assembly, ModuleOwnership> classify)
            => Capture(classify, MonoReader.Value);

        // Called explicitly by the common bootstrap before any world baseline
        // is frozen; formal Capture never invokes preparation. This prepares tool metadata only: never reads static game
        // values, allocates a game owner, runs a game cctor, or changes an old
        // CaptureResult's baseline. All newly loaded modules must have an actual
        // AssemblyRef path from the previously loaded metadata.
        internal static string[] PrepareMetadata(Func<Assembly, ModuleOwnership> classify)
            => PrepareMetadata(classify, MonoReader.Value);

        internal static string[] PrepareMetadata(Func<Assembly, ModuleOwnership> classify, IInitializationReader reader)
        {
            if (classify == null || reader == null) throw new ArgumentNullException();
            var gained = new SortedSet<string>(StringComparer.Ordinal);
            for (var pass = 0; pass < 12; pass++) {
                var beforeAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                var beforeModules = LoadedModuleIdentities(beforeAssemblies);
                var selected = beforeAssemblies.Where(assembly => !assembly.IsDynamic && classify(assembly) == ModuleOwnership.Gameplay).ToArray();
                var before = reader.Read(selected);
                int schemaCountBefore; lock (SchemaGate) schemaCountBefore = Schemas.Count;
                var shapes = reader is IMetadataShapeReader metadata ? metadata.ReadPreparationShapes(selected) : Array.Empty<Type>();
                foreach (var type in before.Select(value => value.Type).Concat(shapes).Distinct()) {
                    if (type.IsPointer || type.IsByRef || type.IsGenericParameter || type.Assembly.IsDynamic) continue;
                    try { _ = Schema(type); }
                    catch (ResidentMonoGenericMetadata.UnownedParameterException) { /* Capture retains the exact storage proof/gap. */ }
                }
                foreach (var value in before.Where(value => value.State == Initialization.Initialized)) {
                    try {
                        foreach (var field in Schema(value.Type).Fields)
                            if (field.Gap == null && field.SlotType != null) _ = field.Accessors();
                    } catch (ResidentMonoGenericMetadata.UnownedParameterException) { /* Capture retains the exact gap. */ }
                }
                // Exercise the exact DTO serializer contract, without passing
                // a gameplay object or metadata object to Json.NET.
                _ = ToolDtoContract.Value;
                var after = reader.Read(AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assembly => !assembly.IsDynamic && classify(assembly) == ModuleOwnership.Gameplay).ToArray());
                RequireMetadataStateUnchanged(before, after);
                var afterAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                RequireMetadataDependencyClosure(beforeAssemblies, afterAssemblies);
                var afterModules = LoadedModuleIdentities(afterAssemblies);
                foreach (var identity in afterModules.Except(beforeModules, StringComparer.Ordinal)) gained.Add(identity);
                int schemaCountAfter; lock (SchemaGate) schemaCountAfter = Schemas.Count;
                if (schemaCountBefore == schemaCountAfter && beforeModules.SequenceEqual(afterModules) && before.Length == after.Length
                    && new HashSet<Type>(before.Select(value => value.Type)).SetEquals(after.Select(value => value.Type))) return gained.ToArray();
            }
            throw Error("metadata preparation did not reach a fixed point");
        }

        internal static void RequireMetadataStateUnchanged(Observation[] before, Observation[] after)
        {
            var previous = before.ToDictionary(value => value.Type);
            foreach (var value in after) {
                if (previous.TryGetValue(value.Type, out var old)) {
                    if (old.State != value.State || old.NativeVtableAbsent != value.NativeVtableAbsent
                        || old.NativeClass != value.NativeClass || old.NativeType != value.NativeType || old.NativeGenericClass != value.NativeGenericClass || old.NativeVtable != value.NativeVtable)
                        throw Error("metadata preparation changed gameplay initialization/storage: " + TypeKey(value.Type)
                            + ":" + old.State + "/absent=" + old.NativeVtableAbsent + "->" + value.State + "/absent=" + value.NativeVtableAbsent);
                    previous.Remove(value.Type);
                } else if (value.State != Initialization.Dormant)
                    throw Error("metadata preparation gained an initialized gameplay type: " + TypeKey(value.Type));
            }
            if (previous.Count != 0) throw Error("metadata preparation lost authoritative gameplay types: count=" + previous.Count + ";"
                + string.Join(";", previous.Values.Select(ObservationIdentity).OrderBy(value => value, StringComparer.Ordinal).Take(24)));
        }

        private static string ObservationIdentity(Observation value)
        {
            var type = value.Type;
            var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
            string shape;
            try { shape = JsonConvert.SerializeObject(ResidentManagedTypeCatalog.DescribeType(type), Formatting.None); }
            catch (ResidentMonoGenericMetadata.UnownedParameterException error) { shape = "unproven(" + error.Message + ")"; }
            return definition.Module.ModuleVersionId.ToString("D") + "/" + definition.MetadataToken.ToString("x8")
                + "/" + definition.FullName + ";handle=" + type.TypeHandle.Value.ToInt64().ToString("x")
                + ";class=" + value.NativeClass.ToInt64().ToString("x") + ";generic=" + value.NativeGenericClass.ToInt64().ToString("x")
                + ";vtable=" + value.NativeVtable.ToInt64().ToString("x") + ";state=" + value.State + ";absent=" + value.NativeVtableAbsent
                + ";valueType=" + type.IsValueType + ";shape=" + shape;
        }

        private static bool SameObservation(Observation before, Observation after)
            => before.State == after.State && before.NativeVtableAbsent == after.NativeVtableAbsent && before.NativeClass == after.NativeClass
                && before.NativeType == after.NativeType && before.NativeGenericClass == after.NativeGenericClass && before.NativeVtable == after.NativeVtable;

        private static bool CompatibleObservation(Observation before, Observation after)
        {
            if (SameObservation(before, after)) return true;
            if (!before.Type.ContainsGenericParameters && before.State == Initialization.Dormant && after.State == Initialization.Dormant
                && before.NativeClass == after.NativeClass && before.NativeType == after.NativeType && before.NativeGenericClass == after.NativeGenericClass
                && before.NativeVtableAbsent && !after.NativeVtableAbsent && after.NativeVtable != IntPtr.Zero)
                return true;
            return false;
        }

        internal static string InitializationDifference(Observation[] before, Observation[] after)
        {
            var prior = before.GroupBy(value => value.Type).ToDictionary(group => group.Key, group => group.First());
            var actual = after.GroupBy(value => value.Type).ToDictionary(group => group.Key, group => group.First());
            var gained = actual.Where(pair => !prior.ContainsKey(pair.Key)).Select(pair => ObservationIdentity(pair.Value)).ToArray();
            var lost = prior.Where(pair => !actual.ContainsKey(pair.Key)).Select(pair => ObservationIdentity(pair.Value)).ToArray();
            var changed = actual.Where(pair => prior.TryGetValue(pair.Key, out var old) && !SameObservation(old, pair.Value))
                .Select(pair => "before={" + ObservationIdentity(prior[pair.Key]) + "}->after={" + ObservationIdentity(pair.Value) + "}").ToArray();
            return "before=" + before.Length + ";after=" + after.Length + ";duplicates=" + (after.Length - actual.Count)
                + ";gained(" + gained.Length + ")=[" + string.Join(";", gained.OrderBy(value => value, StringComparer.Ordinal).Take(24))
                + "];lost(" + lost.Length + ")=[" + string.Join(";", lost.OrderBy(value => value, StringComparer.Ordinal).Take(24))
                + "];changed(" + changed.Length + ")=[" + string.Join(";", changed.OrderBy(value => value, StringComparer.Ordinal).Take(24)) + "]";
        }

        internal static bool DomainVtableBackreferencesMatch(IntPtr domain, IntPtr table, IntPtr klass,
            IntPtr recordedDomain, byte initialized, IntPtr classDomainSlot)
            => domain != IntPtr.Zero && table != IntPtr.Zero && klass != IntPtr.Zero && recordedDomain == domain
                && initialized <= 1 && classDomainSlot == table;

        private static void RequireMetadataDependencyClosure(Assembly[] before, Assembly[] after)
        {
            if (before.Except(after).Any()) throw Error("metadata preparation lost loaded assemblies");
            var reachable = new HashSet<Assembly>(before); var pending = new Queue<Assembly>(before);
            while (pending.Count != 0) {
                var owner = pending.Dequeue();
                if (owner.IsDynamic) continue;
                var references = owner.GetReferencedAssemblies();
                foreach (var candidate in after) {
                    if (reachable.Contains(candidate) || candidate.IsDynamic) continue;
                    if (references.Any(reference => AssemblyName.ReferenceMatchesDefinition(reference, candidate.GetName()))) {
                        reachable.Add(candidate); pending.Enqueue(candidate);
                    }
                }
            }
            var unproven = after.Where(assembly => !reachable.Contains(assembly)).ToArray();
            if (unproven.Length != 0) throw Error("metadata preparation loaded unproven dependencies: "
                + string.Join(";", LoadedModuleIdentities(unproven)));
        }

        private static string[] LoadedModuleIdentities(IEnumerable<Assembly> assemblies)
            => assemblies.SelectMany(assembly => assembly.IsDynamic ? new[] { assembly.FullName + "/" + Guid.Empty.ToString("D") }
                : assembly.GetLoadedModules().Select(module => assembly.FullName + "/" + module.ModuleVersionId.ToString("D")))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();

        internal static string ModuleDifference(IEnumerable<string> expected, IEnumerable<string> actual)
            => "gained=[" + string.Join(";", actual.Except(expected, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                + "];lost=[" + string.Join(";", expected.Except(actual, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal)) + "]";

        // Creates a read-only proof for control owners too. Metadata lookup or
        // calling an export cannot stand in for proving that its .cctor already
        // ran before the first static value is observed.
        internal static bool IsInitialized(Type type) => MonoReader.Value.ReadOne(type).State == Initialization.Initialized;

        internal static Action CreateInitializationGuard(params Type[] types)
        {
            if (types == null || types.Length == 0 || types.Any(type => type == null || type.ContainsGenericParameters))
                throw Error("explicit initialization proof lacks closed actual types");
            var owners = types.Distinct().ToArray(); var reader = MonoReader.Value;
            void Require() {
                foreach (var owner in owners)
                    if (reader.ReadOne(owner).State != Initialization.Initialized)
                        throw Error("control type is not already initialized: " + TypeKey(owner));
            }
            Require(); return Require;
        }

        // The injectable reader supports offline tests of dormant-type safety;
        // production always uses the pinned, read-only Mono implementation.
        internal static CaptureResult Capture(Func<Assembly, ModuleOwnership> classify, IInitializationReader reader)
        {
            if (classify == null || reader == null) throw new ArgumentNullException();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var selected = new List<Assembly>(); var modules = new List<ModuleData>(); var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies) {
                var ownership = classify(assembly);
                if (!Enum.IsDefined(typeof(ModuleOwnership), ownership)) throw Error("module ownership is invalid");
                if (assembly.IsDynamic) {
                    if (ownership == ModuleOwnership.Gameplay) gaps.Add("dynamic-gameplay-module:" + assembly.GetName().Name);
                    modules.Add(new ModuleData { Assembly = assembly.GetName().Name!, Module = Guid.Empty.ToString("D"), Ownership = ownership.ToString() });
                    continue;
                }
                if (ownership == ModuleOwnership.Gameplay) selected.Add(assembly);
                foreach (var module in assembly.GetLoadedModules()) modules.Add(new ModuleData { Assembly = assembly.GetName().Name!,
                    Module = module.ModuleVersionId.ToString("D"), Ownership = ownership.ToString() });
            }
            if (selected.Count == 0) throw Error("gameplay module ownership is empty");
            var observed = reader.Read(selected.ToArray());
            if (observed == null || observed.Length > MaximumTypes || observed.Any(value => value == null || value.Type == null)
                || observed.Any(value => value.NativeVtableAbsent && value.State != Initialization.Dormant)
                || observed.Select(value => value.Type).Distinct().Count() != observed.Length) throw Error("type initialization inventory differs");
            var bindings = new List<Binding>(); var types = new List<TypeData>();
            var describable = new List<Tuple<string, Observation>>();
            foreach (var item in observed) {
                if (!selected.Contains(item.Type.Assembly) || !Enum.IsDefined(typeof(Initialization), item.State))
                    throw Error("type escaped gameplay module ownership");
                if (item.Type.IsPointer || item.Type.IsByRef || item.Type.IsGenericParameter || item.Type.Assembly.IsDynamic) {
                    gaps.Add("unsupported-static-type:" + item.Type.Module.ModuleVersionId.ToString("D") + "/" + item.Type.Name); continue;
                }
                try { describable.Add(Tuple.Create(Schema(item.Type).Key, item)); }
                catch (ResidentMonoGenericMetadata.UnownedParameterException error) {
                    // No vtable means no allocated static slots in this domain.
                    // This actual storage proof permits skipping a sharing
                    // artifact without claiming that anonymity implies safety.
                    // The full raw inventory guard retains even skipped classes
                    // and rejects a later vtable allocation (even still dormant).
                    if (error.Anonymous && item.NativeVtableAbsent) continue;
                    var definition = item.Type.IsGenericType ? item.Type.GetGenericTypeDefinition() : item.Type;
                    gaps.Add("unproven-static-generic-owner:" + definition.Module.ModuleVersionId.ToString("D")
                        + "/" + definition.MetadataToken + ":" + error.Message);
                }
            }
            foreach (var row in describable.OrderBy(value => value.Item1, StringComparer.Ordinal)) {
                var item = row.Item2;
                var type = item.Type;
                var schema = Schema(type); var key = schema.Key;
                if (item.State == Initialization.Failed) gaps.Add("failed-type-initializer:" + key);
                var layout = new List<FieldData>();
                foreach (var metadata in schema.Fields) {
                    var field = metadata.Field; var storage = metadata.Storage;
                    var fieldKey = ResidentStaticManifestWire.SlotKey(key, field.MetadataToken);
                    var descriptor = new FieldData { Key = fieldKey, Name = field.Name, Token = field.MetadataToken,
                        Attributes = (int)field.Attributes, Signature = metadata.Signature, Storage = storage };
                    layout.Add(descriptor);
                    if (metadata.Gap != null) { gaps.Add(metadata.Gap + ":" + fieldKey); continue; }
                    if (field.IsLiteral || type.ContainsGenericParameters || item.State != Initialization.Initialized) continue;
                    if (storage != "static") { gaps.Add(storage + ":" + fieldKey); continue; }
                    if (field.FieldType.IsPointer || field.FieldType.IsByRef || field.FieldType.ContainsGenericParameters) {
                        gaps.Add("unsupported-static-field:" + fieldKey); continue;
                    }
                    if (bindings.Count >= MaximumSlots) throw Error("static slot inventory exceeds its bound");
                    var slotType = metadata.SlotType!;
                    var slot = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(slotType);
                    var valueField = metadata.SlotValue!;
                    // Only the already-initialized branch may read static data.
                    var access = metadata.Accessors();
                    access.Read(slot);
                    var value = field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr) ? valueField.GetValue(slot) : null;
                    var nullHandle = value is IntPtr signed && signed == IntPtr.Zero || value is UIntPtr unsigned && unsigned == UIntPtr.Zero;
                    // A null static native slot carries no cross-process address.
                    // Its target must independently have the same null state.
                    if (nullHandle) descriptor.Storage = "null-native-handle";
                    bindings.Add(new Binding(fieldKey, field, slot, valueField, access, nullHandle));
                }
                types.Add(new TypeData { Key = key, Type = CopyReference(schema.Type),
                    Initialization = item.State.ToString(), Fields = layout.ToArray() });
            }
            var data = new Data { RuntimeAbi = reader.AbiIdentity,
                Modules = modules.OrderBy(value => value.Assembly, StringComparer.Ordinal).ThenBy(value => value.Module, StringComparer.Ordinal).ToArray(),
                Types = types.ToArray(), Unsupported = gaps.ToArray() };
            var result = new CaptureResult(data, bindings.ToArray(), classify, reader, observed);
            result.RequireInitializationUnchanged();
            return result;
        }

        internal sealed class CaptureResult
        {
            internal readonly Data Snapshot;
            internal readonly IReadOnlyDictionary<string, object> ManagedRoots;
            internal string[] Unsupported => Snapshot.Unsupported;
            private readonly Binding[] bindings;
            private readonly Func<Assembly, ModuleOwnership> classify;
            private readonly IInitializationReader reader;
            private readonly Dictionary<Type, Observation> observed;
            internal CaptureResult(Data snapshot, Binding[] bindings, Func<Assembly, ModuleOwnership> classify, IInitializationReader reader, Observation[] observations)
            {
                Snapshot = snapshot; this.bindings = bindings; this.classify = classify; this.reader = reader;
                observed = observations.ToDictionary(value => value.Type);
                ManagedRoots = bindings.Where(binding => !binding.WitnessOnly).ToDictionary(value => value.Key, value => value.Slot, StringComparer.Ordinal);
            }
            internal void RequireUnchanged(CanonicalCarryGraphCodec codec)
            {
                RequireInitializationUnchanged();
                foreach (var binding in bindings)
                    if (!binding.Access.Same(binding.Slot))
                        throw Error("static slot changed: " + binding.Key);
                RequireInitializationUnchanged();
            }
            internal void RequireInitializationUnchanged()
            {
                var gameplay = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => classify(assembly) == ModuleOwnership.Gameplay).ToArray();
                var selected = gameplay.Where(assembly => !assembly.IsDynamic).ToArray();
                var modules = gameplay.SelectMany(assembly => assembly.IsDynamic ? new[] { assembly.GetName().Name + "/" + Guid.Empty.ToString("D") }
                    : assembly.GetLoadedModules().Select(module => assembly.GetName().Name + "/" + module.ModuleVersionId.ToString("D")))
                    .OrderBy(value => value, StringComparer.Ordinal).ToArray();
                var expectedModules = Snapshot.Modules.Where(value => value.Ownership == ModuleOwnership.Gameplay.ToString())
                    .Select(value => value.Assembly + "/" + value.Module).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (!modules.SequenceEqual(expectedModules)) throw Error("gameplay modules changed during observation: " + ModuleDifference(expectedModules, modules));
                var actual = reader.Read(selected);
                if (actual.Length != observed.Count || actual.Select(value => value.Type).Distinct().Count() != actual.Length
                    || actual.Any(value => !observed.TryGetValue(value.Type, out var before) || !CompatibleObservation(before, value)))
                    throw Error("type initialization changed during observation: " + InitializationDifference(observed.Values.ToArray(), actual));
            }
            internal Binding[] Bindings => bindings;
        }

        internal static Plan Prepare(Data source, CaptureResult target, Action admitted)
            => new Plan(source, target, admitted);

        private static bool IsControlPlaneAssembly(string? assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return false;
            return assembly!.StartsWith("MonoMod", StringComparison.Ordinal)
                || assembly!.StartsWith("Mono.Cecil", StringComparison.Ordinal)
                || assembly == "HollowKnightTAS" || assembly == "HollowKnightTAS.Core"
                || assembly == "HollowKnightTAS.ClockPayload" || assembly == "Newtonsoft.Json";
        }

        internal sealed class Plan
        {
            internal readonly string[] Unsupported;
            internal IReadOnlyDictionary<string, object> ManagedRoots => target.ManagedRoots;
            private readonly CaptureResult target;
            private readonly Action admitted;
            private bool applied, replayed;
            internal Plan(Data source, CaptureResult target, Action admitted)
            {
                if (source == null || target == null || admitted == null) throw new ArgumentNullException();
                _ = ManifestBytes(source); _ = ManifestBytes(target.Snapshot);
                this.target = target; this.admitted = admitted;
                var gaps = new SortedSet<string>(source.Unsupported.Concat(target.Unsupported), StringComparer.Ordinal);
                if (source.RuntimeAbi != target.Snapshot.RuntimeAbi) gaps.Add("static-runtime-abi-differs");
                var sourceModules = source.Modules.Where(value => value.Ownership == ModuleOwnership.Gameplay.ToString() && !IsControlPlaneAssembly(value.Assembly)).ToArray();
                var targetModules = target.Snapshot.Modules.Where(value => value.Ownership == ModuleOwnership.Gameplay.ToString() && !IsControlPlaneAssembly(value.Assembly)).ToArray();
                if (JsonConvert.SerializeObject(sourceModules) != JsonConvert.SerializeObject(targetModules)) gaps.Add("static-gameplay-modules-differ");
                var targetTypes = target.Snapshot.Types.Where(t => !IsControlPlaneAssembly(t.Type?.Assembly) && !t.Key.Contains("MonoMod.")).ToDictionary(value => value.Key, StringComparer.Ordinal);
                foreach (var type in source.Types) {
                    if (IsControlPlaneAssembly(type.Type?.Assembly) || type.Key.Contains("MonoMod.")) continue;
                    if (!targetTypes.TryGetValue(type.Key, out var other)) { gaps.Add("static-type-absent:" + type.Key); continue; }
                    targetTypes.Remove(type.Key);
                    if (type.Initialization != other.Initialization) gaps.Add("static-initialization-differs:" + type.Key + ":" + type.Initialization + "->" + other.Initialization);
                    if (JsonConvert.SerializeObject(type.Type) != JsonConvert.SerializeObject(other.Type)
                        || JsonConvert.SerializeObject(type.Fields) != JsonConvert.SerializeObject(other.Fields)) gaps.Add("static-type-layout-differs:" + type.Key);
                }
                foreach (var extra in targetTypes.Keys) gaps.Add("static-type-extra:" + extra);
                Unsupported = gaps.ToArray(); target.RequireInitializationUnchanged();
            }
            internal void RequireComplete()
            {
                if (Unsupported.Length != 0) throw Error("coverage gaps: " + string.Join(";", Unsupported.Take(16)));
            }
            // Invoke after the unified graph has populated the typed slot roots.
            internal void Apply()
            {
                RequireComplete(); if (applied) throw Error("static plan was already applied");
                WriteSlots();
                applied = true; target.RequireInitializationUnchanged();
            }
            // Native setters may notify managed game code. After the same
            // allocated graph has been replayed, restore its typed static slots
            // once more without allocating another graph or running cctors.
            internal void Reapply()
            {
                RequireComplete(); if (!applied || replayed) throw Error("static replay requires one completed unreplayed application");
                replayed = true; WriteSlots(); target.RequireInitializationUnchanged();
            }
            private void WriteSlots()
            {
                admitted(); target.RequireInitializationUnchanged();
                foreach (var binding in target.Bindings) {
                    admitted();
                    if (binding.WitnessOnly) {
                        if (!binding.Access.Same(binding.Slot)) throw Error("null native static slot changed: " + binding.Key);
                        continue;
                    }
                    try { binding.Access.Write(binding.Slot); }
                    catch (Exception error) { throw new InvalidOperationException("Resident static slot write failed: " + binding.Key, error); }
                }
            }
            // Reads actual static slots; the separate graph readback checks every
            // pointed-to field/alias, including cross-component/static cycles.
            internal void Verify(CanonicalCarryGraphCodec codec)
            {
                RequireComplete(); if (!applied) throw Error("static plan has not been applied");
                admitted(); target.RequireUnchanged(codec);
            }
        }

        internal static byte[] ManifestBytes(Data source)
        {
            if (source == null || source.Version != 1 || string.IsNullOrEmpty(source.RuntimeAbi) || source.RuntimeAbi.Length > 256
                || source.Modules == null || source.Modules.Length > 4096 || source.Types == null || source.Types.Length > MaximumTypes
                || source.Unsupported == null || source.Unsupported.Length > MaximumSlots) throw Error("static manifest shape differs");
            var keys = new HashSet<string>(StringComparer.Ordinal); var slots = 0;
            foreach (var type in source.Types) {
                if (type == null || string.IsNullOrEmpty(type.Key) || type.Key.Length > 16384 || !keys.Add(type.Key) || type.Type == null
                    || !Enum.TryParse(type.Initialization, out Initialization initialization) || initialization.ToString() != type.Initialization
                    || !Enum.IsDefined(typeof(Initialization), initialization) || type.Fields == null || (slots += type.Fields.Length) > MaximumSlots)
                    throw Error("static type manifest differs");
                var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in type.Fields)
                    if (field == null || string.IsNullOrEmpty(field.Key) || !fieldKeys.Add(field.Key) || field.Token <= 0 || field.Name == null
                        || field.Signature == null || field.Storage != "static" && field.Storage != "null-native-handle" && field.Storage != "literal" && field.Storage != "thread-static" && field.Storage != "context-static")
                        throw Error("static field manifest differs");
            }
            if (source.Modules.Any(value => value == null || string.IsNullOrEmpty(value.Assembly) || !Guid.TryParseExact(value.Module, "D", out _)
                || !Enum.TryParse(value.Ownership, out ModuleOwnership ownership) || ownership.ToString() != value.Ownership
                || !Enum.IsDefined(typeof(ModuleOwnership), ownership)) || source.Unsupported.Any(value => string.IsNullOrEmpty(value))) throw Error("static module/gap manifest differs");
            return ResidentStaticManifestWire.Encode(source);
        }
        internal static Data DecodeManifest(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 8 * 1024 * 1024) throw Error("static manifest byte size differs");
            try {
                var result = ResidentStaticManifestWire.Decode(bytes);
                if (!ManifestBytes(result).SequenceEqual(bytes)) throw Error("static manifest is noncanonical");
                return result;
            } catch (Exception error) when (error is JsonException || error is DecoderFallbackException) { throw new InvalidDataException("Resident static manifest is malformed.", error); }
        }
        private static string TypeKey(Type type) => type.Module.ModuleVersionId.ToString("D") + "/"
            + ((type.ContainsGenericParameters && !type.IsGenericTypeDefinition ? null : type.FullName)
            ?? JsonConvert.SerializeObject(ResidentManagedTypeCatalog.DescribeType(type), Formatting.None));
        internal static string FieldSignature(Type type) => type.IsPointer ? "pointer(" + FieldSignature(type.GetElementType()!) + ")"
            : type.IsByRef ? "byref(" + FieldSignature(type.GetElementType()!) + ")"
            : JsonConvert.SerializeObject(ResidentManagedTypeCatalog.DescribeType(type), Formatting.None);
        private sealed class StaticFieldSchema
        {
            internal readonly FieldInfo Field;
            internal readonly string Storage, Signature;
            internal readonly string? Gap;
            internal readonly Type? SlotType;
            internal readonly FieldInfo? SlotValue;
            private SlotAccess? access;
            internal SlotAccess Accessors()
            {
                lock (SchemaGate) return access ?? (access = new SlotAccess(Field, SlotType!, SlotValue!));
            }
            internal StaticFieldSchema(FieldInfo field)
            {
                Field = field;
                Storage = field.IsLiteral ? "literal" : field.IsDefined(typeof(ThreadStaticAttribute), false) ? "thread-static"
                    : field.IsDefined(typeof(ContextStaticAttribute), false) ? "context-static" : "static";
                try { Signature = FieldSignature(field.FieldType); }
                catch (ResidentMonoGenericMetadata.UnownedParameterException error) {
                    Signature = "unproven-generic-owner(" + error.Message + ")";
                    Gap = "unproven-static-field-generic-owner";
                }
                if (field.FieldType.IsPointer || field.FieldType.IsByRef) Gap = "unsupported-static-field";
                if (Gap == null && !field.IsLiteral && Storage == "static" && !field.DeclaringType!.ContainsGenericParameters
                    && !field.FieldType.ContainsGenericParameters) {
                    SlotType = typeof(Slot<>).MakeGenericType(field.FieldType);
                    SlotValue = SlotType.GetField(nameof(Slot<int>.Value))!;
                }
            }
        }
        private sealed class StaticSchema
        {
            internal readonly string Key;
            internal readonly ResidentManagedTypeCatalog.TypeReference Type;
            internal readonly StaticFieldSchema[] Fields;
            internal StaticSchema(Type type)
            {
                Key = TypeKey(type); Type = ResidentManagedTypeCatalog.DescribeType(type);
                Fields = type.GetFields(StaticFields).OrderBy(field => field.MetadataToken).Select(field => new StaticFieldSchema(field)).ToArray();
            }
        }
        // All three operations keep T inline. Reflection GetValue boxes T and
        // can allocate a previously absent gameplay value-type vtable even when
        // no .cctor runs. These accessors are created only for an owner already
        // proven Initialized; preparing dormant owner accessors must not force
        // that owner's static storage or initializer into existence.
        internal sealed class SlotAccess
        {
            internal readonly Action<object> Read, Write;
            internal readonly Func<object, bool> Same;
            internal SlotAccess(FieldInfo source, Type slotType, FieldInfo value)
            {
                DynamicMethod Method(string name, Type result) => new DynamicMethod(name, result, new[] { typeof(object) }, typeof(ResidentStaticRoots).Module, true);
                var read = Method("resident-static-read", typeof(void)); var il = read.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, slotType); il.Emit(OpCodes.Ldsfld, source); il.Emit(OpCodes.Stfld, value); il.Emit(OpCodes.Ret);
                Read = (Action<object>)read.CreateDelegate(typeof(Action<object>));
                var write = Method("resident-static-write", typeof(void)); il = write.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, slotType); il.Emit(OpCodes.Ldfld, value); il.Emit(OpCodes.Stsfld, source); il.Emit(OpCodes.Ret);
                Write = (Action<object>)write.CreateDelegate(typeof(Action<object>));
                var same = Method("resident-static-same", typeof(bool)); il = same.GetILGenerator();
                var left = il.DeclareLocal(source.FieldType.MakeByRefType()); var right = il.DeclareLocal(source.FieldType.MakeByRefType()); var index = il.DeclareLocal(typeof(int));
                var loop = il.DefineLabel(); var check = il.DefineLabel(); var different = il.DefineLabel();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, slotType); il.Emit(OpCodes.Ldflda, value); il.Emit(OpCodes.Stloc, left);
                il.Emit(OpCodes.Ldsflda, source); il.Emit(OpCodes.Stloc, right); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stloc, index); il.Emit(OpCodes.Br, check);
                il.MarkLabel(loop);
                il.Emit(OpCodes.Ldloc, left); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldind_U1);
                il.Emit(OpCodes.Ldloc, right); il.Emit(OpCodes.Conv_U); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldind_U1); il.Emit(OpCodes.Bne_Un, different);
                il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, index);
                il.MarkLabel(check); il.Emit(OpCodes.Ldloc, index); il.Emit(OpCodes.Sizeof, source.FieldType); il.Emit(OpCodes.Blt, loop);
                il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret); il.MarkLabel(different); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret);
                Same = (Func<object, bool>)same.CreateDelegate(typeof(Func<object, bool>));
                RuntimeHelpers.PrepareDelegate(Read); RuntimeHelpers.PrepareDelegate(Write); RuntimeHelpers.PrepareDelegate(Same);
            }
        }
        private static StaticSchema Schema(Type type)
        {
            lock (SchemaGate) {
                if (!Schemas.TryGetValue(type, out var schema)) { schema = new StaticSchema(type); Schemas.Add(type, schema); }
                return schema;
            }
        }
        private static ResidentManagedTypeCatalog.TypeReference CopyReference(ResidentManagedTypeCatalog.TypeReference source)
            => new ResidentManagedTypeCatalog.TypeReference { Kind = source.Kind, Name = source.Name, Assembly = source.Assembly,
                Module = source.Module, Token = source.Token, Rank = source.Rank, Position = source.Position, SzArray = source.SzArray,
                Element = source.Element == null ? null : CopyReference(source.Element), Arguments = source.Arguments.Select(CopyReference).ToArray() };
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident static roots " + detail + ".");
        internal sealed class Binding
        {
            internal readonly string Key;
            internal readonly FieldInfo Field, Value;
            internal readonly object Slot;
            internal readonly SlotAccess Access;
            internal readonly bool WitnessOnly;
            internal Binding(string key, FieldInfo field, object slot, FieldInfo value, SlotAccess access, bool witnessOnly = false)
            { Key = key; Field = field; Slot = slot; Value = value; Access = access; WitnessOnly = witnessOnly; }
        }

        private sealed class MonoInitializationReader : IInitializationReader, IMetadataShapeReader
        {
            private const string MonoHash = "211e11bab370bc6acbd5b8f1af4f8a5536cd62df91c2ca8bdc4512e1a2d8834f";
            [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
            [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ClassCallback(IntPtr klass, IntPtr data);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Classes(ClassCallback callback, IntPtr data);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr NoArguments();
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DomainGate(IntPtr domain);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr OneArgument(IntPtr value);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TwoArguments(IntPtr first, IntPtr second);
            private readonly Classes enumerate;
            private readonly NoArguments domain;
            private readonly OneArgument classType, typeClass;
            private readonly TwoArguments tryVtable;
            private readonly DomainGate lockDomain, unlockDomain;
            private readonly byte[] domainArrayPointer = new byte[8], domainArrayHeader = new byte[16];
            private byte[] domainVtablePointers = Array.Empty<byte>();
            private readonly ConstructorInfo handleConstructor;
            private readonly Dictionary<IntPtr, Type> reflectedTypes = new Dictionary<IntPtr, Type>();
            private readonly Dictionary<Assembly, Type[]> definitions = new Dictionary<Assembly, Type[]>();
            private readonly object inventoryGate = new object();
            public string AbiIdentity => "mono-domain-static-storage/2/" + MonoHash;
            internal MonoInitializationReader()
            {
                if (IntPtr.Size != 8 || Type.GetType("Mono.Runtime") == null) throw Error("requires the pinned 64-bit Mono runtime");
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(value => string.Equals(value.ModuleName, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1) throw Error("Mono module is absent or ambiguous");
                using (var hash = SHA256.Create()) using (var file = File.OpenRead(modules[0].FileName))
                    if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != MonoHash) throw Error("Mono module ABI differs");
                var module = modules[0].BaseAddress;
                T Export<T>(string name) where T : class {
                    var address = GetProcAddress(module, name); if (address == IntPtr.Zero) throw Error("Mono export is absent: " + name);
                    return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
                }
                enumerate = Export<Classes>("mono_unity_class_for_each"); domain = Export<NoArguments>("mono_domain_get");
                classType = Export<OneArgument>("mono_class_get_type"); typeClass = Export<OneArgument>("mono_class_from_mono_type");
                tryVtable = Export<TwoArguments>("mono_unity_class_try_get_vtable");
                // These are checks of the loaded engine ABI, never calls to init.
                RequireBytes(IntPtr.Add(module, 0xbad8b), "4038692d"); // initialized byte at vtable+0x2d.
                RequireBytes(IntPtr.Add(module, 0xbae4a), "f6473002"); // init_failed bit at vtable+0x30.
                RequireBytes(IntPtr.Add(module, 0x43266), "488b4120"); // MonoGenericClass.cached_class +0x20.
                RequireBytes(IntPtr.Add(module, 0xb55ff), "498b8c24e0000000e80432f5ff"); // append every created vtable to domain+0xe0.
                RequireBytes(IntPtr.Add(module, 0x8897), "8b4b08488b0348893cc8ff4308"); // GPtrArray data+0/count+8.
                RequireBytes(IntPtr.Add(module, 0x2cca0), "40574883ec20488bf9ff15");
                RequireBytes(IntPtr.Add(module, 0x2ce10), "48ff2589f53500");
                lockDomain = (DomainGate)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module, 0x2cca0), typeof(DomainGate));
                unlockDomain = (DomainGate)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(module, 0x2ce10), typeof(DomainGate));
                handleConstructor = typeof(RuntimeTypeHandle).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null)
                    ?? throw Error("Mono RuntimeTypeHandle ABI differs");
                var fields = typeof(RuntimeTypeHandle).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fields.Length != 1 || fields[0].FieldType != typeof(IntPtr)) throw Error("Mono type handle storage differs");
            }
            public Observation[] Read(Assembly[] gameplayModules)
            {
                lock (inventoryGate) return ReadCurrent(gameplayModules);
            }
            private Type[] DefinitionTypes(Assembly[] modules)
            {
                var types = new List<Type>();
                foreach (var assembly in modules) {
                    if (!definitions.TryGetValue(assembly, out var metadata)) { metadata = assembly.GetTypes(); definitions.Add(assembly, metadata); }
                    types.AddRange(metadata);
                }
                return types.ToArray();
            }
            private Type ReflectClass(IntPtr klass, bool requireStorageIdentity = true)
            {
                if (!reflectedTypes.TryGetValue(klass, out var type)) {
                    var nativeType = classType(klass);
                    if (nativeType == IntPtr.Zero) throw Error("Mono class has no type identity");
                    var handle = (RuntimeTypeHandle)handleConstructor.Invoke(new object[] { nativeType });
                    type = Type.GetTypeFromHandle(handle) ?? throw Error("Mono class type cannot be resolved");
                    reflectedTypes.Add(klass, type);
                }
                if (requireStorageIdentity && (type.TypeHandle.Value != classType(klass) || typeClass(type.TypeHandle.Value) != klass))
                    throw Error("Mono storage class/type reverse identity differs");
                return type;
            }
            private static bool StaticOwner(Type type, HashSet<Assembly> selected)
                => selected.Contains(type.Assembly) && !type.IsArray && !type.IsPointer && !type.IsByRef && !type.IsGenericParameter;
            private Observation[] ReadCurrent(Assembly[] gameplayModules)
            {
                var selected = new HashSet<Assembly>(gameplayModules);
                var types = new HashSet<Type>(DefinitionTypes(gameplayModules));
                var storage = ReadDomainVtables();
                foreach (var pair in storage) {
                    var type = ReflectClass(pair.Key);
                    if (StaticOwner(type, selected)) types.Add(type);
                }
                if (types.Count > MaximumTypes) throw Error("gameplay type inventory exceeds its bound");
                var result = types.Select(ReadOne).ToArray();
                foreach (var value in result) {
                    if (!value.NativeVtableAbsent && (!storage.TryGetValue(value.NativeClass, out var table) || table != value.NativeVtable))
                        throw Error("gameplay vtable lacks current domain lifetime ownership: " + ObservationIdentity(value));
                }
                // Independent second current-domain read, never a union with a
                // prior observation or the oscillating metadata cache index.
                var after = ReadDomainVtables();
                var beforeGameplay = storage.Where(pair => StaticOwner(ReflectClass(pair.Key), selected)).ToDictionary(pair => pair.Key, pair => pair.Value);
                var afterGameplay = after.Where(pair => StaticOwner(ReflectClass(pair.Key), selected)).ToDictionary(pair => pair.Key, pair => pair.Value);
                if (beforeGameplay.Count != afterGameplay.Count || beforeGameplay.Any(pair => !afterGameplay.TryGetValue(pair.Key, out var table) || table != pair.Value))
                    throw Error("gameplay domain vtable lifetime directory changed during observation");
                return result;
            }
            public Type[] ReadPreparationShapes(Assembly[] gameplayModules)
            {
                lock (inventoryGate) {
                    var selected = new HashSet<Assembly>(gameplayModules);
                    var classes = new List<IntPtr>(); var overflow = false;
                    ClassCallback callback = (klass, _) => { if (classes.Count >= MaximumTypes * 4) overflow = true; else classes.Add(klass); };
                    enumerate(callback, IntPtr.Zero); GC.KeepAlive(callback);
                    if (overflow) throw Error("Mono metadata shape index exceeds its bound");
                    // This index can hide/reveal existing initialized classes.
                    // It is only a schema discovery hint; Read() gets every
                    // actual storage owner independently from the domain list.
                    return classes.Distinct().Select(klass => ReflectClass(klass, requireStorageIdentity: false)).Where(type => StaticOwner(type, selected)).Distinct().ToArray();
                }
            }
            private Dictionary<IntPtr, IntPtr> ReadDomainVtables()
            {
                var currentDomain = domain(); if (currentDomain == IntPtr.Zero) throw Error("Mono domain is absent");
                var count = 0; var complete = false;
                for (var attempt = 0; attempt < 4 && !complete; attempt++) {
                    var required = 0; var valid = true;
                    lockDomain(currentDomain);
                    try {
                        // Buffers and delegate stubs exist before acquiring the
                        // native domain lock. No reflection, user code or managed
                        // allocation occurs while the lifetime array is locked.
                        valid = ReadInto(IntPtr.Add(currentDomain, 0xe0), domainArrayPointer, 8);
                        var array = valid ? new IntPtr(BitConverter.ToInt64(domainArrayPointer, 0)) : IntPtr.Zero;
                        valid = valid && array != IntPtr.Zero && ReadInto(array, domainArrayHeader, 16);
                        count = valid ? BitConverter.ToInt32(domainArrayHeader, 8) : -1;
                        var capacity = valid ? BitConverter.ToInt32(domainArrayHeader, 12) : -1;
                        valid = valid && count >= 0 && count <= MaximumTypes * 4 && capacity >= count && capacity <= MaximumTypes * 8;
                        if (valid) {
                            required = count * 8;
                            if (domainVtablePointers.Length >= required) {
                                valid = required == 0 || ReadInto(new IntPtr(BitConverter.ToInt64(domainArrayHeader, 0)), domainVtablePointers, required);
                                complete = valid;
                            }
                        }
                    } finally { unlockDomain(currentDomain); }
                    if (!valid) throw Error("Mono domain vtable lifetime array ABI/read differs");
                    if (!complete) domainVtablePointers = new byte[required];
                }
                if (!complete) throw Error("Mono domain vtable lifetime array did not stabilize");
                var result = new Dictionary<IntPtr, IntPtr>();
                for (var index = 0; index < count; index++) {
                    var table = new IntPtr(BitConverter.ToInt64(domainVtablePointers, index * 8));
                    var bytes = ReadBytes(table, 0x34);
                    var klass = new IntPtr(BitConverter.ToInt64(bytes, 0));
                    if (!DomainVtableBackreferencesMatch(currentDomain, table, klass, new IntPtr(BitConverter.ToInt64(bytes, 0x10)),
                        bytes[0x2d], klass == IntPtr.Zero ? IntPtr.Zero : tryVtable(currentDomain, klass)) || result.ContainsKey(klass))
                        throw Error("Mono domain vtable reverse identity/unique lifetime ownership differs");
                    result.Add(klass, table);
                }
                return result;
            }
            private static bool ReadInto(IntPtr address, byte[] bytes, int length)
                => address != IntPtr.Zero && ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)length), out var read)
                    && read.ToUInt64() == (ulong)length;
            internal Observation ReadOne(Type type)
            {
                var currentDomain = domain(); if (currentDomain == IntPtr.Zero) throw Error("Mono domain is absent");
                var klass = typeClass(type.TypeHandle.Value);
                if (klass == IntPtr.Zero) throw Error("Mono type has no class identity");
                var genericClass = IntPtr.Zero;
                if (type.IsGenericType && !type.IsGenericTypeDefinition) {
                    var nativeType = ReadBytes(classType(klass), 16);
                    if (nativeType[10] != 0x15) throw Error("Mono instantiated generic type code differs");
                    genericClass = new IntPtr(BitConverter.ToInt64(nativeType, 0));
                }
                var table = tryVtable(currentDomain, klass);
                if (table == IntPtr.Zero) return new Observation(type, Initialization.Dormant, nativeVtableAbsent: true, nativeClass: klass, nativeGenericClass: genericClass);
                var bytes = ReadBytes(table, 0x34);
                if (new IntPtr(BitConverter.ToInt64(bytes, 0)) != klass || new IntPtr(BitConverter.ToInt64(bytes, 0x10)) != currentDomain
                    || bytes[0x2d] > 1) throw Error("Mono vtable identity or initialization flag differs");
                return new Observation(type, (bytes[0x30] & 2) != 0 ? Initialization.Failed : bytes[0x2d] == 1 ? Initialization.Initialized : Initialization.Dormant, nativeClass: klass, nativeGenericClass: genericClass, nativeVtable: table);
            }
            private static void RequireBytes(IntPtr address, string hex)
            {
                var bytes = ReadBytes(address, hex.Length / 2);
                if (BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant() != hex) throw Error("loaded Mono initialization ABI differs");
            }
            private static byte[] ReadBytes(IntPtr address, int length)
            {
                var result = new byte[length];
                if (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, result, new UIntPtr((uint)length), out var read)
                    || read.ToUInt64() != (ulong)length) throw Error("Mono initialization memory read failed");
                return result;
            }
        }
    }
}
