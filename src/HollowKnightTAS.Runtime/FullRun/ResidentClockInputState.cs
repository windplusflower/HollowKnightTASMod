using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.Input;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Native clock transport belongs to the admitted process. Its implementation
    // must support a fresh transaction at each shared boundary; the historical
    // experiment's process-wide one-import flags are not reset or bypassed.
    internal static class ResidentClockInputState
    {
        internal interface IBoundaryBackend
        {
            string Identity { get; }
            bool IsFrameObservation { get; }
            string[] CoverageGaps { get; }
            long NativeFrame { get; }
            void RequireBoundary();
            byte[] CaptureNativeClock();
            byte[] CaptureRandom();
            byte[] CaptureObserver();
            uint[] CaptureParticles();
            void ImportRandom(byte[] bytes, long expectedTargetNative);
            void ImportNative(byte[] bytes, long expectedTargetNative);
            void ImportObserver(byte[] bytes, long sourceNative);
            void ImportParticles(uint[] words, long sourceNative);
        }
        internal sealed class Data
        {
            public int Version = 1;
            public bool FrameOnly;
            public string Backend = "";
            public long NativeFrame, MovieFrame;
            public byte[] Native = Array.Empty<byte>(), Random = Array.Empty<byte>(), Observer = Array.Empty<byte>();
            public uint[] Particles = Array.Empty<uint>();
            public short[] ConfiguredKeys = Array.Empty<short>();
            public bool MouseEnabled, CanonicalMouseCaches, KeyboardPresent;
            public int ViewportWidth, ViewportHeight;
            public Owner[] Owners = Array.Empty<Owner>();
            public Dependency[] Dependencies = Array.Empty<Dependency>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Owner
        {
            public string Key = "";
            public ResidentManagedTypeCatalog.TypeReference Type = null!;
            public Field[] Fields = Array.Empty<Field>();
        }
        internal sealed class Field
        {
            public string Name = "", Storage = "", Root = "";
            public int Token, Attributes;
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!, ValueType = null!;
        }
        internal sealed class Dependency
        {
            public string Domain = "", Ownership = "", Description = "";
            public ResidentManagedTypeCatalog.TypeReference? Type;
            public int[] FieldTokens = Array.Empty<int>();
        }

        private const string Prefix = "resident-clock-input/";
        private const int MaximumPacket = 2 * 1024 * 1024;
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly IReadOnlyDictionary<string, string> InputFields = Ownership(
            "D:leases,customKnightInputs,currentFrame,sampleIndex,authoredEdgeChannels,transitionedEdgeChannels,authoredSampleCounts,suspended",
            "Dependency:movie,customSamples,runStarts,expected,authored,recording,replaying",
            "Execution:keyboard,hooked,nativeFrameActive,nativeFrameRunning,disposed,cutsceneInput,fault,Sampled,EdgesObserved,Faulted,checkpointInputImported");
        private static readonly IReadOnlyDictionary<string, string> MouseFields = Ownership(
            "D:hkModules,inControlModules,inControlState,hkState,previousInControlButtons,previousHkButtons,suspended,previousHkPosition",
            "Dependency:viewportWidth,viewportHeight,replaying,canonicalMouseCaches,<Enabled>k__BackingField",
            "Execution:input,originalProvider,filteredProvider,installed,disposed,standaloneLastPosition,standaloneThisPosition,checkpointMouseImported");
        private static readonly IReadOnlyDictionary<string, string> KeyboardFields = Ownership(
            "Dependency:keys", "Execution:hooks,active,state");
        private static readonly IReadOnlyDictionary<string, string> KeyStateFields = Ownership("D:previous,current");

        internal static CaptureResult Capture(IBoundaryBackend backend, FullRunActionSetAdapter input,
            FullRunMouseBridge mouse, Action admitted)
            => CaptureOwners(backend, input, mouse, admitted, false);

        internal static CaptureResult CaptureFrame(IBoundaryBackend backend, FullRunActionSetAdapter input,
            FullRunMouseBridge mouse, Action admitted)
            => CaptureOwners(backend, input, mouse, admitted, true);

        private static CaptureResult CaptureOwners(IBoundaryBackend backend, FullRunActionSetAdapter input,
            FullRunMouseBridge mouse, Action admitted, bool frameOnly)
        {
            if (backend == null || input == null || mouse == null || admitted == null) throw new ArgumentNullException();
            admitted(); backend.RequireBoundary();
            if (backend.IsFrameObservation != frameOnly) throw Error("native lease kind does not match the requested capture");
            RequireInputOwners(input, mouse, frameOnly);
            var frame = backend.NativeFrame;
            var data = new Data { FrameOnly = frameOnly, Backend = backend.Identity, NativeFrame = frame,
                MovieFrame = Read<long>(input, "currentFrame"), Native = Bytes(backend.CaptureNativeClock()),
                Random = Bytes(backend.CaptureRandom()), Observer = Bytes(backend.CaptureObserver()),
                Particles = Words(backend.CaptureParticles()),
                MouseEnabled = Read<bool>(mouse, "<Enabled>k__BackingField"),
                CanonicalMouseCaches = Read<bool>(mouse, "canonicalMouseCaches"),
                ViewportWidth = Read<int>(mouse, "viewportWidth"), ViewportHeight = Read<int>(mouse, "viewportHeight") };
            var owners = new List<OwnerBinding>(); var gaps = new SortedSet<string>(backend.CoverageGaps ?? throw Error("backend coverage is absent"), StringComparer.Ordinal);
            AddOwner("input", input, OwnershipFor("input", frameOnly), owners, gaps);
            AddOwner("mouse", mouse, OwnershipFor("mouse", frameOnly), owners, gaps);
            var keyboard = Read<object?>(input, "keyboard");
            data.KeyboardPresent = keyboard != null;
            if (keyboard != null) {
                if (keyboard.GetType() != typeof(FullRunKeyboardBridge)) throw Error("keyboard control owner type differs");
                AddOwner("keyboard", keyboard, OwnershipFor("keyboard", frameOnly), owners, gaps);
                var state = Read<object?>(keyboard, "state") ?? throw Error("keyboard state owner is absent");
                if (state.GetType() != typeof(CustomKeyState)) throw Error("keyboard state owner type differs");
                AddOwner("keyboard-state", state, KeyStateFields, owners, gaps);
                data.ConfiguredKeys = ((IEnumerable<short>)(Read<object>(keyboard, "keys"))).OrderBy(value => value).ToArray();
            }
            data.Owners = owners.Select(owner => owner.Descriptor).OrderBy(owner => owner.Key, StringComparer.Ordinal).ToArray();
            data.Dependencies = DescribeDependencies(data.Owners);
            data.CoverageGaps = gaps.ToArray(); ValidateCapture(data, frameOnly);
            var result = new CaptureResult(data, backend, input, mouse, owners.ToArray(), admitted);
            if (frameOnly) result.RequireFrameUnchanged(); else result.RequireUnchanged(); return result;
        }

        internal sealed class CaptureResult
        {
            internal readonly Data Snapshot;
            internal IReadOnlyDictionary<string, object> ManagedRoots { get; }
            internal readonly IBoundaryBackend Backend;
            internal readonly FullRunActionSetAdapter Input;
            internal readonly FullRunMouseBridge Mouse;
            internal readonly OwnerBinding[] Owners;
            private readonly Dictionary<object, string> external = new Dictionary<object, string>(References.Instance);
            private readonly Dictionary<string, object> resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly Action admitted;
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            private readonly string frozen;
            private readonly List<Action> executionChecks = new List<Action>();

            internal CaptureResult(Data snapshot, IBoundaryBackend backend, FullRunActionSetAdapter input,
                FullRunMouseBridge mouse, OwnerBinding[] owners, Action admitted)
            {
                Snapshot = snapshot; Backend = backend; Input = input; Mouse = mouse; Owners = owners; this.admitted = admitted;
                frozen = JsonConvert.SerializeObject(snapshot);
                ManagedRoots = owners.SelectMany(owner => owner.Fields.Where(field => field.Slot != null))
                    .ToDictionary(field => field.Descriptor.Root, field => field.Slot!, StringComparer.Ordinal);
                // Only actual control instances are external. In particular the
                // leases/original bindings and injected input sources remain D
                // in the same graph as their InControl action-set references.
                foreach (var owner in owners.OrderBy(owner => owner.Descriptor.Key, StringComparer.Ordinal))
                    AddExternal(owner.Owner, Prefix + "execution/" + owner.Descriptor.Key);
                foreach (var owner in owners.OrderBy(owner => owner.Descriptor.Key, StringComparer.Ordinal))
                    foreach (var field in owner.Fields.Where(field => field.Descriptor.Storage == "Execution")) {
                        if (!field.Field.FieldType.IsValueType) AddExternal(field.Original, Prefix + "execution/" + owner.Descriptor.Key + "/" + field.Field.Name);
                        if (field.Original is IList list) ObserveList(list);
                    }
                var cutscene = Read<object?>(input, "cutsceneInput");
                if (cutscene != null) {
                    var hooks = Read<object>(cutscene, "hooks") as IList ?? throw Error("cutscene hook owner changed layout");
                    ObserveList(hooks);
                }
                var provider = Read<object?>(mouse, "filteredProvider");
                if (provider != null) {
                    var providerFields = CanonicalCarryGraphCodec.Fields(provider.GetType());
                    if (providerFields.Length != 2 || providerFields.Any(field => field.Name != "owner" && field.Name != "original"))
                        throw Error("mouse provider execution layout changed");
                    var expected = providerFields.Select(field => field.GetValue(provider)).ToArray();
                    executionChecks.Add(() => {
                        for (var index = 0; index < providerFields.Length; index++)
                            if (!ReferenceEquals(expected[index], providerFields[index].GetValue(provider))) throw Error("mouse provider execution identity changed");
                    });
                }
            }
            private void AddExternal(object? value, string key)
            {
                if (value == null || value.GetType().IsValueType || value is string || value is Delegate || ResidentGraphPolicy.IsMetadata(value)) return;
                if (external.ContainsKey(value)) return;
                external.Add(value, key); resolved.Add(key, value);
            }
            private void ObserveList(IList list)
            {
                if (list.Count > 4096) throw Error("execution hook list exceeds its bound");
                var expected = new object?[list.Count]; list.CopyTo(expected, 0);
                executionChecks.Add(() => {
                    if (list.Count != expected.Length) throw Error("execution hook membership changed");
                    for (var index = 0; index < expected.Length; index++) if (!ReferenceEquals(list[index], expected[index])) throw Error("execution hook identity changed");
                });
            }
            internal string? BindExternal(object value) => external.TryGetValue(value, out var key) ? key : null;
            internal object ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : throw Error("unknown control identity: " + key);
            internal object ResolveManagedRoot(string key) => ManagedRoots.TryGetValue(key, out var value) ? value : throw Error("unknown input slot root: " + key);
            internal void RequireComplete()
            { if (Snapshot.CoverageGaps.Length != 0) throw Error("coverage gaps: " + string.Join(";", Snapshot.CoverageGaps.Take(16))); }
            internal void RequireUnchanged()
            {
                if (Snapshot.FrameOnly) throw Error("read-only frame observation cannot be used for restore");
                RequireFields(false);
            }
            internal void RequireFrameUnchanged()
            {
                if (!Snapshot.FrameOnly) throw Error("frame witness requires a read-only frame observation");
                RequireFields(true);
            }
            private void RequireFields(bool frameOnly)
            {
                RequireControlUnchanged(); RequireInputOwners(Input, Mouse, frameOnly); RequirePackets(Snapshot, Backend);
                foreach (var owner in Owners) foreach (var field in owner.Fields)
                    if (!SameSlot(field.Original, ResidentValueAccess.ReadField(owner.Owner,field.Field), field.Field.FieldType)
                        || frameOnly && field.Slot != null && !SameSlot(field.Original, ResidentValueAccess.ReadField(field.Slot!,field.Value!), field.Field.FieldType))
                        throw Error("input owner field changed: " + owner.Descriptor.Key + "/" + field.Field.Name);
                RequireControlUnchanged();
            }
            internal void RequireControlUnchanged()
            {
                if (thread != Thread.CurrentThread.ManagedThreadId) throw Error("input observation moved away from its admitted thread");
                admitted(); Backend.RequireBoundary();
                if (Snapshot.FrameOnly != Backend.IsFrameObservation || Snapshot.Backend != Backend.Identity || frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("clock/input observation or backend identity changed");
                foreach (var owner in Owners) foreach (var field in owner.Fields.Where(field => field.Descriptor.Storage == "Execution"))
                    if (!SameSlot(field.Original, ResidentValueAccess.ReadField(owner.Owner,field.Field), field.Field.FieldType)) throw Error("execution field identity changed: " + owner.Descriptor.Key + "/" + field.Field.Name);
                foreach (var owner in Owners) foreach (var field in owner.Fields.Where(field => field.Descriptor.Storage == "Dependency")) {
                    // These fields deliberately become the requested Movie's
                    // continuation. The other dependencies stay target-local.
                    if (owner.Descriptor.Key == "input" || owner.Descriptor.Key == "mouse" && field.Field.Name == "replaying") continue;
                    if (!SameSlot(field.Original, ResidentValueAccess.ReadField(owner.Owner,field.Field), field.Field.FieldType)) throw Error("configured input dependency changed: " + owner.Descriptor.Key + "/" + field.Field.Name);
                }
                if (Snapshot.KeyboardPresent && !((IEnumerable<short>)Read<object>(Read<object>(Input, "keyboard"), "keys")).OrderBy(key => key).SequenceEqual(Snapshot.ConfiguredKeys))
                    throw Error("configured keyboard hook keys changed");
                foreach (var check in executionChecks) check();
            }
        }

        internal static Plan Prepare(Data source, CaptureResult target, MovieV2Document nextMovie,
            ResidentMutationScope scope, Action admitted) => new Plan(source, target, nextMovie, scope, admitted);

        internal sealed class Plan
        {
            private readonly Data source;
            private readonly CaptureResult target;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly PendingMovie pending;
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            private State state;
            private enum State { Prepared, Applying, Applied, Faulted }
            internal readonly string[] Unsupported;
            private readonly Dictionary<string, object> managedRoots;
            internal IReadOnlyDictionary<string, object> ManagedRoots => managedRoots;
            internal string? BindExternal(object value) => target.BindExternal(value);
            internal object ResolveExternal(string key) => target.ResolveExternal(key);

            internal Plan(Data source, CaptureResult target, MovieV2Document nextMovie, ResidentMutationScope scope, Action admitted)
            {
                if (source == null || target == null || nextMovie == null || scope == null || admitted == null) throw new ArgumentNullException();
                Validate(source); target.RequireUnchanged();
                this.source = JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(source)) ?? throw Error("clock/input source copy failed");
                this.target = target; this.scope = scope; this.admitted = admitted;
                this.managedRoots = target.ManagedRoots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                if (source.Owners != null) {
                    var remapped = new List<(string sourceRoot, string targetRoot, object slot)>();
                    foreach (var sOwner in source.Owners) {
                        var tOwner = target.Owners.FirstOrDefault(o => o.Descriptor.Key == sOwner.Key);
                        if (tOwner != null && sOwner.Fields != null) {
                            foreach (var sField in sOwner.Fields) {
                                if (sField.Storage == "D" && !string.IsNullOrEmpty(sField.Root)) {
                                    var tField = tOwner.Fields.FirstOrDefault(f => f.Descriptor.Name == sField.Name);
                                    if (tField?.Slot != null) {
                                        remapped.Add((sField.Root, tField.Descriptor.Root, tField.Slot));
                                    }
                                }
                            }
                        }
                    }
                    foreach (var item in remapped) {
                        if (!string.IsNullOrEmpty(item.targetRoot) && item.targetRoot != item.sourceRoot) {
                            this.managedRoots.Remove(item.targetRoot);
                        }
                    }
                    foreach (var item in remapped) {
                        this.managedRoots[item.sourceRoot] = item.slot;
                    }
                }
                var gaps = new SortedSet<string>(source.CoverageGaps.Concat(target.Snapshot.CoverageGaps), StringComparer.Ordinal);
                if (!CompatibleBackendIdentity(source.Backend, target.Snapshot.Backend)) gaps.Add("clock-input-backend-differs");
                if (source.KeyboardPresent != target.Snapshot.KeyboardPresent) gaps.Add("input-keyboard-owner-presence-differs");
                if (CanonicalOwnersJson(source.Owners) != CanonicalOwnersJson(target.Snapshot.Owners)) gaps.Add("input-owner-layout-differs");
                if (CanonicalDependenciesJson(source.Dependencies) != CanonicalDependenciesJson(target.Snapshot.Dependencies)) gaps.Add("input-dependency-layout-differs");
                if (!source.ConfiguredKeys.SequenceEqual(target.Snapshot.ConfiguredKeys)) gaps.Add("input-keyboard-hook-key-set-differs");
                if (source.MouseEnabled != target.Snapshot.MouseEnabled || source.CanonicalMouseCaches != target.Snapshot.CanonicalMouseCaches
                    || source.ViewportWidth != target.Snapshot.ViewportWidth || source.ViewportHeight != target.Snapshot.ViewportHeight) gaps.Add("input-mouse-environment-differs");
                pending = PrepareMovie(this.source, nextMovie);
                if (!target.Snapshot.KeyboardPresent) gaps.Add("input-replay-keyboard-control-owner-absent");
                Unsupported = gaps.ToArray();
            }
            internal void RequireComplete()
            { if (Unsupported.Length != 0) throw Error("coverage gaps: " + string.Join(";", Unsupported.Take(16))); }
            // The coordinator calls this after its one shared graph has filled
            // all typed slots and replayed controlled native callback effects.
            // No old process-wide checkpointImported flag is set or reset.
            internal void Apply()
            {
                if (state != State.Prepared || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("input plan is reused, reentered or on a different thread"); }
                try {
                    RequireComplete(); admitted(); scope.Require(); target.RequireUnchanged(); state = State.Applying;
                    scope.Mutate(() => {
                        RequireApplying();
                        foreach (var owner in target.Owners) foreach (var field in owner.Fields.Where(field => field.Slot != null)) {
                            RequireApplying(); ResidentValueAccess.WriteField(owner.Owner,field.Field,ResidentValueAccess.ReadField(field.Slot!,field.Value!));
                        }
                        RequireApplying(); pending.Apply(target.Input, target.Mouse); RequirePreparedInputValues();
                        target.Backend.ImportRandom((byte[])source.Random.Clone(), target.Snapshot.NativeFrame); RequireApplying();
                        target.Backend.ImportNative((byte[])source.Native.Clone(), target.Snapshot.NativeFrame); RequireApplying();
                        target.Backend.ImportObserver((byte[])source.Observer.Clone(), source.NativeFrame); RequireApplying();
                        target.Backend.ImportParticles((uint[])source.Particles.Clone(), source.NativeFrame); RequireApplying();
                        RequirePackets(source, target.Backend); RequirePreparedInputValues();
                    });
                    RequireApplying(); state = State.Applied;
                } catch { state = State.Faulted; throw; }
            }
            internal void Verify()
            {
                if (state != State.Applied || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("input verification requires one completed transaction"); }
                try { admitted(); scope.Require(); target.RequireControlUnchanged(); RequirePreparedInputValues(); RequirePackets(source, target.Backend); }
                catch { state = State.Faulted; throw; }
            }
            private void RequireApplying()
            {
                if (state != State.Applying || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("input transaction became reentrant or changed threads"); }
                admitted(); scope.Require(); target.RequireControlUnchanged();
                if (state != State.Applying) throw Error("input transaction failed inside its admission callback");
            }
            private void RequirePreparedInputValues()
            {
                target.RequireControlUnchanged(); RequireInputOwners(target.Input, target.Mouse, false);
                foreach (var owner in target.Owners) foreach (var field in owner.Fields.Where(field => field.Slot != null))
                    if (!SameSlot(ResidentValueAccess.ReadField(field.Slot!,field.Value!), ResidentValueAccess.ReadField(owner.Owner,field.Field), field.Field.FieldType)) throw Error("input D field readback differs: " + owner.Descriptor.Key + "/" + field.Field.Name);
                if (Read<long>(target.Input, "currentFrame") != source.MovieFrame) throw Error("input graph cursor differs from its native snapshot");
                pending.Verify(target.Input, target.Mouse);
                if (source.KeyboardPresent) {
                    var keyboard = Read<object>(target.Input, "keyboard");
                    var actual = ((IEnumerable<short>)Read<object>(keyboard, "keys")).OrderBy(value => value).ToArray();
                    if (!actual.SequenceEqual(source.ConfiguredKeys)) throw Error("keyboard hook key set changed");
                    var stateOwner = Read<object>(keyboard, "state");
                    var previous = ((IEnumerable<short>)Read<object>(stateOwner, "previous")).ToArray();
                    var current = ((IEnumerable<short>)Read<object>(stateOwner, "current")).ToArray();
                    if (previous.Concat(current).Any(key => !actual.Contains(key))) throw Error("keyboard D state exceeds configured hook keys");
                }
            }
        }

        internal sealed class OwnerBinding
        {
            internal readonly object Owner;
            internal readonly Owner Descriptor;
            internal readonly FieldBinding[] Fields;
            internal OwnerBinding(object owner, Owner descriptor, FieldBinding[] fields) { Owner = owner; Descriptor = descriptor; Fields = fields; }
        }
        internal sealed class FieldBinding
        {
            internal readonly FieldInfo Field;
            internal readonly Field Descriptor;
            internal readonly object? Original, Slot;
            internal readonly FieldInfo? Value;
            internal FieldBinding(FieldInfo field, Field descriptor, object? original)
            {
                Field = field; Descriptor = descriptor; Original = original;
                if (descriptor.Root.Length != 0) {
                    var slotType = typeof(ResidentStaticRoots.Slot<>).MakeGenericType(field.FieldType);
                    Slot = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(slotType);
                    Value = slotType.GetField("Value")!; ResidentValueAccess.WriteField(Slot,Value,original);
                }
            }
        }
        private static void AddOwner(string key, object owner, IReadOnlyDictionary<string, string> ownership,
            List<OwnerBinding> owners, SortedSet<string> gaps)
        {
            var fields = CanonicalCarryGraphCodec.Fields(owner.GetType());
            foreach (var absent in ownership.Keys.Where(name => !fields.Any(field => field.Name == name))) gaps.Add("input-declared-field-absent:" + key + "/" + absent);
            var bindings = fields.Select(field => {
                var storage = ownership.TryGetValue(field.Name, out var declared) ? declared : "Unsupported";
                if (storage == "Unsupported") gaps.Add("input-field-unclassified:" + key + "/" + field.Name);
                var descriptor = new Field { Name = field.Name, Storage = storage,
                    Root = InputRoot(key, field, storage),
                    Token = field.MetadataToken, Attributes = (int)field.Attributes,
                    DeclaringType = ResidentManagedTypeCatalog.DescribeType(field.DeclaringType!), ValueType = ResidentManagedTypeCatalog.DescribeType(field.FieldType) };
                return new FieldBinding(field, descriptor, ResidentValueAccess.ReadField(owner,field));
            }).ToArray();
            owners.Add(new OwnerBinding(owner, new Owner { Key = key, Type = ResidentManagedTypeCatalog.DescribeType(owner.GetType()),
                Fields = bindings.Select(binding => binding.Descriptor).ToArray() }, bindings));
        }

        internal static void Validate(Data data)
            => ValidateCapture(data, false);
        private static void ValidateCapture(Data data, bool frameOnly)
        {
            if (data == null || data.FrameOnly != frameOnly || data.Version != 1 || string.IsNullOrEmpty(data.Backend) || data.Backend.Length > 512 || data.Backend.IndexOf('\0') >= 0
                || data.NativeFrame < 0 || data.MovieFrame < (frameOnly ? -1 : 0) || data.Native == null || data.Native.Length == 0 || data.Native.Length > MaximumPacket
                || data.Random == null || data.Random.Length == 0 || data.Random.Length > MaximumPacket || data.Observer == null || data.Observer.Length == 0 || data.Observer.Length > MaximumPacket
                || data.Particles == null || data.Particles.Length == 0 || data.Particles.Length > MaximumPacket / 4
                || data.ConfiguredKeys == null || data.ConfiguredKeys.Length > 1024 || !data.ConfiguredKeys.SequenceEqual(data.ConfiguredKeys.Distinct().OrderBy(key => key))
                || data.ConfiguredKeys.Any(key => !CustomKeyInput.Names.ContainsKey(key)) || !data.KeyboardPresent && data.ConfiguredKeys.Length != 0
                || data.ViewportWidth <= 0 || data.ViewportHeight <= 0 || data.ViewportWidth > 32768 || data.ViewportHeight > 32768
                || data.Owners == null || data.Owners.Length > 4 || data.Dependencies == null || data.Dependencies.Length > 32
                || data.CoverageGaps == null || data.CoverageGaps.Length > 256 || !data.CoverageGaps.SequenceEqual(data.CoverageGaps.Distinct().OrderBy(value => value, StringComparer.Ordinal))
                || data.CoverageGaps.Any(value => string.IsNullOrEmpty(value) || value.Length > 4096 || value.IndexOf('\0') >= 0)) throw Error("manifest shape differs");
            var expectedKeys = data.KeyboardPresent ? new[] { "input", "keyboard", "keyboard-state", "mouse" } : new[] { "input", "mouse" };
            if (!data.Owners.Select(owner => owner?.Key).SequenceEqual(expectedKeys)) throw Error("control owner inventory differs");
            foreach (var owner in data.Owners) {
                var expectedType = owner.Key == "input" ? typeof(FullRunActionSetAdapter) : owner.Key == "mouse" ? typeof(FullRunMouseBridge)
                    : owner.Key == "keyboard" ? typeof(FullRunKeyboardBridge) : typeof(CustomKeyState);
                if (owner.Type == null || ResidentManagedTypeCatalog.ResolveType(owner.Type) != expectedType || owner.Fields == null || owner.Fields.Length > 128) throw Error("control owner type differs");
                var fields = CanonicalCarryGraphCodec.Fields(expectedType);
                if (owner.Fields.Length != fields.Length) throw Error("control owner omits an actual field");
                var ownership = OwnershipFor(owner.Key, frameOnly);
                for (var index = 0; index < fields.Length; index++) {
                    var actual = fields[index]; var field = owner.Fields[index];
                    var storage = ownership.TryGetValue(actual.Name, out var declared) ? declared : "Unsupported";
                    var isControlPlane = ResidentManagedTypeCatalog.IsControlPlaneAssembly(actual.DeclaringType?.Assembly?.GetName().Name);
                    if (field == null || field.Name != actual.Name || (!isControlPlane && field.Token != actual.MetadataToken) || field.Attributes != (int)actual.Attributes
                        || field.DeclaringType == null || ResidentManagedTypeCatalog.ResolveType(field.DeclaringType) != actual.DeclaringType
                        || field.ValueType == null || ResidentManagedTypeCatalog.ResolveType(field.ValueType) != actual.FieldType || field.Storage != storage
                        || (!isControlPlane && field.Root != InputRoot(owner.Key, actual, storage))) {
                        var diff = $"owner={owner.Key} actualField={actual.Name} (srcField={field?.Name}) " +
                                   $"token src=0x{field?.Token:X8} act=0x{actual.MetadataToken:X8} " +
                                   $"attr src={field?.Attributes} act={(int)actual.Attributes} " +
                                   $"decl src={field?.DeclaringType?.Name} act={actual.DeclaringType?.FullName} " +
                                   $"val src={field?.ValueType?.Name} act={actual.FieldType?.FullName} " +
                                   $"storage src={field?.Storage} act={storage} " +
                                   $"root src={field?.Root} act={InputRoot(owner.Key, actual, storage)}";
                        throw Error("control field ABI or ownership differs: " + diff);
                    }
                    if (storage == "Unsupported" && !data.CoverageGaps.Contains("input-field-unclassified:" + owner.Key + "/" + actual.Name)) throw Error("unclassified field lacks its coverage gap");
                }
                foreach (var absent in ownership.Keys.Where(name => !fields.Any(field => field.Name == name)))
                    if (!data.CoverageGaps.Contains("input-declared-field-absent:" + owner.Key + "/" + absent)) throw Error("absent declared field lacks its coverage gap");
            }
            if (CanonicalDependenciesJson(data.Dependencies) != CanonicalDependenciesJson(DescribeDependencies(data.Owners))) throw Error("dependency ownership declaration differs");
        }

        private static Dependency[] DescribeDependencies(Owner[] owners)
        {
            var result = new List<Dependency> {
                new Dependency { Domain = "native-clock", Ownership = "Dependency", Description = "exact native frame/time/fixed-step state; local IPC admission is excluded by the backend" },
                new Dependency { Domain = "unity-random", Ownership = "D", Description = "complete native Unity RNG words; no reseed or advancement" },
                new Dependency { Domain = "observer-clock-random", Ownership = "Dependency", Description = "complete observer clock plus second RNG stream; byte-exact backend import" },
                new Dependency { Domain = "particle-random", Ownership = "D", Description = "complete native particle RNG manager state; no reseed or advancement" }
            };
            foreach (var owner in owners) foreach (var group in owner.Fields.GroupBy(field => field.Storage).OrderBy(group => group.Key, StringComparer.Ordinal))
                result.Add(new Dependency { Domain = "input/" + owner.Key + "/" + group.Key, Ownership = group.Key,
                    Description = group.Key == "D" || group.Key == "Frame" ? "typed slots in the one shared graph including all aliases and collection storage"
                        : group.Key == "Dependency" ? "movie continuation or configured viewport/key dependencies rebuilt and checked against the requested Movie"
                        : group.Key == "Execution" ? "actual admitted target control identities and hooks; never copied from another process" : "unclassified actual fields; qualification is rejected",
                    Type = owner.Type, FieldTokens = group.Select(field => field.Token).OrderBy(token => token).ToArray() });
            return result.ToArray();
        }

        private static string CanonicalOwnersJson(Owner[]? owners)
        {
            if (owners == null) return "";
            var copy = owners.Select(o => new {
                o.Key,
                Type = ResidentManagedTypeCatalog.NormalizeType(o.Type),
                Fields = o.Fields.Select(f => new {
                    f.Name,
                    f.Storage,
                    Root = ResidentManagedTypeCatalog.IsControlPlaneAssembly(f.DeclaringType?.Assembly) ? "" : f.Root,
                    Token = ResidentManagedTypeCatalog.IsControlPlaneAssembly(f.DeclaringType?.Assembly) ? 0 : f.Token,
                    f.Attributes,
                    DeclaringType = ResidentManagedTypeCatalog.NormalizeType(f.DeclaringType),
                    ValueType = ResidentManagedTypeCatalog.NormalizeType(f.ValueType)
                }).ToArray()
            }).ToArray();
            return JsonConvert.SerializeObject(copy);
        }

        private static string CanonicalDependenciesJson(Dependency[]? dependencies)
        {
            if (dependencies == null) return "";
            var copy = dependencies.Select(d => new {
                d.Domain,
                d.Ownership,
                d.Description,
                Type = ResidentManagedTypeCatalog.NormalizeType(d.Type),
                FieldTokens = (d.Type != null && ResidentManagedTypeCatalog.IsControlPlaneAssembly(d.Type.Assembly)) ? Array.Empty<int>() : d.FieldTokens
            }).ToArray();
            return JsonConvert.SerializeObject(copy);
        }

        internal sealed class PendingMovie
        {
            internal readonly MovieV2Document Movie;
            internal readonly long Frame;
            internal readonly long[] Starts;
            internal readonly GameInputSample[] Expected, Custom;
            internal readonly bool Authored;
            internal PendingMovie(MovieV2Document movie, long frame, long[] starts, GameInputSample[] expected, GameInputSample[] custom, bool authored)
            { Movie = movie; Frame = frame; Starts = starts; Expected = expected; Custom = custom; Authored = authored; }
            internal void Apply(FullRunActionSetAdapter input, FullRunMouseBridge mouse)
            {
                Set(input, "movie", Movie); Set(input, "runStarts", Starts); Set(input, "expected", Expected); Set(input, "customSamples", Custom);
                Set(input, "authored", Authored); Set(input, "recording", false); Set(input, "replaying", true); Set(mouse, "replaying", true);
            }
            internal void Verify(FullRunActionSetAdapter input, FullRunMouseBridge mouse)
            {
                if (!ReferenceEquals(Read<object>(input, "movie"), Movie) || !ReferenceEquals(Read<object>(input, "runStarts"), Starts)
                    || !ReferenceEquals(Read<object>(input, "expected"), Expected) || !ReferenceEquals(Read<object>(input, "customSamples"), Custom)
                    || Read<bool>(input, "authored") != Authored || Read<bool>(input, "recording") || !Read<bool>(input, "replaying") || !Read<bool>(mouse, "replaying")
                    || Read<long>(input, "currentFrame") != Frame) throw Error("prepared Movie dependencies differ");
                // The Movie is immutable at its public boundary. Arrays owned by
                // this plan are still checked so a hook cannot silently advance
                // or replace pending samples while native import is running.
                var fresh = BuildMovie(Movie, Frame);
                if (!Starts.SequenceEqual(fresh.Starts) || Authored != fresh.Authored || !Expected.SequenceEqual(fresh.Expected) || !Custom.SequenceEqual(fresh.Custom)) throw Error("prepared Movie storage changed");
            }
        }
        internal static PendingMovie PrepareMovie(Data source, MovieV2Document movie)
        {
            if (source == null || source.FrameOnly || movie == null || source.MovieFrame < 0) throw Error("pending writable Movie boundary is absent");
            if (movie.Header.MouseEnabled != source.MouseEnabled || (source.MouseEnabled && (movie.Header.ViewportWidth != source.ViewportWidth || movie.Header.ViewportHeight != source.ViewportHeight))
                || !CustomKeyInput.Keys(movie).OrderBy(key => key).SequenceEqual(source.ConfiguredKeys)) {
                var keysMovie = string.Join(",", CustomKeyInput.Keys(movie).OrderBy(k => k));
                var keysSrc = string.Join(",", source.ConfiguredKeys);
                throw Error($"pending Movie environment or keyboard hook keys differ: mouse movie={movie.Header.MouseEnabled} src={source.MouseEnabled}, vw movie={movie.Header.ViewportWidth} src={source.ViewportWidth}, vh movie={movie.Header.ViewportHeight} src={source.ViewportHeight}, keys movie=[{keysMovie}] src=[{keysSrc}]");
            }
            return BuildMovie(movie, source.MovieFrame);
        }
        private static PendingMovie BuildMovie(MovieV2Document movie, long frame)
        {
            if (movie.Runs.Count == 0 || movie.Runs.Count > MovieProtocolV2.MaximumExpandedFrames) throw Error("pending Movie run count is outside its bound");
            var starts = new long[movie.Runs.Count]; long total = 0; NativeFrameRun? run = null;
            for (var index = 0; index < starts.Length; index++) {
                var current = movie.Runs[index]; if (current.RepeatCount <= 0 || current.RepeatCount > MovieProtocolV2.MaximumExpandedFrames
                    || current.Samples.Count > MovieProtocolV2.MaximumSamplesPerFrame || !MovieFrameRate.IsValid(current.FramesPerSecond)
                    || current.RngSeed.HasValue && current.RepeatCount != 1) throw Error("pending Movie run is invalid");
                starts[index] = total;
                try { total = checked(total + current.RepeatCount); } catch (OverflowException error) { throw new InvalidDataException("Resident clock/input pending Movie length overflowed.", error); }
                if (total > MovieProtocolV2.MaximumExpandedFrames) throw Error("pending Movie frame count exceeds its protocol bound");
                if (frame >= starts[index] && frame < total) run = current;
            }
            if (run == null) throw Error("pending Movie cursor has no unconsumed run");
            CustomKeyInput.Validate(run.Samples);
            foreach (var sample in run.Samples) {
                int count;
                try { count = MovieProtocolV2.ExpectedValueCount(sample.Channel); }
                catch (ArgumentOutOfRangeException) { throw Error("pending Movie input channel is unknown"); }
                var mask = count == 0 ? 0UL : (1UL << count) - 1;
                if (sample.Values.Count != count || ((sample.PressedMask | sample.ReleasedMask) & ~mask) != 0
                    || MovieProtocolV2.IsMouseChannel(sample.Channel) != (sample.Mouse != null)) throw Error("pending Movie input sample layout differs");
                if (sample.Channel == GameInputChannel.CustomKey && !movie.Header.CustomKeys.Contains(sample.Values[0])) throw Error("pending Movie key has no configured hook");
                if (sample.Mouse != null && (!movie.Header.MouseEnabled || sample.Mouse.XQ16 < 0 || sample.Mouse.XQ16 > ushort.MaxValue
                    || sample.Mouse.YQ16 < 0 || sample.Mouse.YQ16 > ushort.MaxValue || (sample.Mouse.Buttons & ~511u) != 0)) throw Error("pending Movie mouse sample is outside its configured domain");
            }
            return new PendingMovie(movie, frame, starts, run.Samples.Where(sample => sample.Channel != GameInputChannel.CustomKey).ToArray(),
                run.Samples.Where(sample => sample.Channel == GameInputChannel.CustomKey).ToArray(), run.Authored);
        }

        private static void RequireInputOwners(FullRunActionSetAdapter input, FullRunMouseBridge mouse, bool frameOnly)
        {
            if (!frameOnly && (Read<bool>(input, "nativeFrameActive") || Read<int>(input, "sampleIndex") != 0 || Read<long>(input, "currentFrame") < 0
                || Read<string>(input, "fault").Length != 0 || Count(Read<object>(input, "transitionedEdgeChannels")) != 0 || Count(Read<object>(input, "authoredSampleCounts")) != 0))
                throw Error("input adapter is outside its unconsumed boundary");
            if (Read<bool>(input, "disposed") || !Read<bool>(input, "hooked") || Read<bool>(input, "recording") == Read<bool>(input, "replaying")
                || Read<object?>(input, "cutsceneInput") == null) throw Error("input adapter control ownership differs");
            if (Read<bool>(mouse, "disposed") || !Read<bool>(mouse, "installed") || !ReferenceEquals(Read<object?>(mouse, "input"), input)
                || Read<object?>(mouse, "filteredProvider") == null || Read<object?>(mouse, "originalProvider") == null
                || Read<int>(mouse, "viewportWidth") <= 0 || Read<int>(mouse, "viewportHeight") <= 0) throw Error("mouse control is outside its admitted boundary");
            if (Count(Read<object>(input, "leases")) > 64 || Count(Read<object>(mouse, "hkModules")) > 4096 || Count(Read<object>(mouse, "inControlModules")) > 4096)
                throw Error("input control membership exceeds its bound");
            var providerSlot = typeof(InControl.InputManager).GetField("<MouseProvider>k__BackingField", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                ?? throw Error("actual mouse provider registration slot is absent");
            if (!ReferenceEquals(providerSlot.GetValue(null), Read<object>(mouse, "filteredProvider"))) throw Error("actual mouse provider registration differs from its control owner");
            RequireLeaseAliases(input);
        }
        private static void RequireLeaseAliases(FullRunActionSetAdapter input)
        {
            var leases = Read<object>(input, "leases") as IList ?? throw Error("input lease list type differs");
            var leaseType = typeof(FullRunActionSetAdapter).GetNestedType("ActionSetLease", BindingFlags.NonPublic) ?? throw Error("input lease type is absent");
            var sets = new HashSet<object>(References.Instance);
            foreach (var lease in leases) {
                if (lease == null || lease.GetType() != leaseType || Read<bool>(lease, "restored")) throw Error("active input lease has been restored or changed type");
                var set = Read<object>(lease, "<Set>k__BackingField"); var channel = ReadChannel(lease, "<Channel>k__BackingField");
                if (set == null || !sets.Add(set)) throw Error("input action-set lease ownership repeats");
                var actions = Read<InControl.PlayerAction[]>(lease, "actions");
                var injected = Read<Array>(lease, "injected"); var originals = Read<InControl.BindingSource[][]>(lease, "originals");
                if (actions.Length == 0 || actions.Length > 1024 || injected.Length != actions.Length || originals.Length != actions.Length) throw Error("input lease array shape differs");
                var seen = new HashSet<object>(References.Instance);
                for (var index = 0; index < actions.Length; index++) {
                    var action = actions[index]; var source = injected.GetValue(index);
                    if (action == null || source == null || originals[index] == null || originals[index].Length > 256 || !seen.Add(action) || !seen.Add(source)
                        || !ReferenceEquals(Read<object>(action, "<Owner>k__BackingField"), set)
                        || ReadChannel(source, "channel") != channel || Read<int>(source, "index") != index
                        || !ReferenceEquals(Read<object>(source, "<BoundTo>k__BackingField"), action)) throw Error("injected source owner/channel/index alias differs");
                    var value = Read<float>(source, "value");
                    if (float.IsNaN(value) || float.IsInfinity(value) || value < -1f || value > 1f) throw Error("injected source value is invalid");
                    foreach (var name in new[] { "regularBindings", "visibleBindings" }) {
                        var list = Read<object>(action, name) as IList ?? throw Error("SDK input bindings type differs");
                        if (list.Count != 1 || !ReferenceEquals(list[0], source)) throw Error("SDK input bindings lost the retained source alias");
                    }
                    if (originals[index].Any(original => original == null || Read<object?>(original, "<BoundTo>k__BackingField") != null)) throw Error("retained original input source is not detached");
                }
            }
        }
        private static int Count(object collection)
        {
            if (collection is ICollection direct) return direct.Count;
            // The admitted in-process collection types are fixed above; this
            // reads their framework Count only, never an arbitrary game getter.
            var type = collection.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(HashSet<>)) throw Error("input collection layout differs");
            return (int)(type.GetProperty("Count")!.GetValue(collection, null) ?? throw Error("input collection count is absent"));
        }
        private static bool CompatibleBackendIdentity(string expected, string actual)
            => expected == actual
                || (expected != null && actual != null
                    && expected.StartsWith("resident-clock-v3/", StringComparison.Ordinal)
                    && actual.StartsWith("resident-clock-v3/", StringComparison.Ordinal));
        private static void RequirePackets(Data expected, IBoundaryBackend backend)
        {
            backend.RequireBoundary();
            if (!CompatibleBackendIdentity(expected.Backend, backend.Identity)) throw Error($"backend identity differs: {backend.Identity} != {expected.Backend}");
            if (backend.NativeFrame != expected.NativeFrame) throw Error($"native frame differs: {backend.NativeFrame} != {expected.NativeFrame}");
            var native = backend.CaptureNativeClock();
            if (expected.FrameOnly && expected.Native.Length == 1192 && native.Length == 1192) {
                int expQpc = BitConverter.ToInt32(expected.Native, 1164);
                int actQpc = BitConverter.ToInt32(native, 1164);
                if (actQpc < expQpc) throw Error($"native frame QPC counter regressed: exp={expQpc} act={actQpc}");
                for (int i = 0; i < 1192; i++) {
                    if (i >= 1164 && i < 1168) continue;
                    if (expected.Native[i] != native[i])
                        throw Error($"native clock bytes differ at offset {i}: exp={expected.Native[i]:X2} act={native[i]:X2}");
                }
            } else if (!expected.Native.SequenceEqual(native)) {
                int diffAt = -1; for(int i=0; i<Math.Min(expected.Native.Length, native.Length); i++) if(expected.Native[i] != native[i]) { diffAt = i; break; }
                throw Error($"native clock bytes differ at offset {diffAt}: exp={expected.Native.ElementAtOrDefault(diffAt):X2} act={native.ElementAtOrDefault(diffAt):X2}");
            }
            var random = backend.CaptureRandom();
            if (!expected.Random.SequenceEqual(random)) {
                int diffAt = -1; for(int i=0; i<Math.Min(expected.Random.Length, random.Length); i++) if(expected.Random[i] != random[i]) { diffAt = i; break; }
                throw Error($"random bytes differ at offset {diffAt}: exp={expected.Random.ElementAtOrDefault(diffAt):X2} act={random.ElementAtOrDefault(diffAt):X2}");
            }
            var observer = backend.CaptureObserver();
            if (!expected.Observer.SequenceEqual(observer)) {
                int diffAt = -1; for(int i=0; i<Math.Min(expected.Observer.Length, observer.Length); i++) if(expected.Observer[i] != observer[i]) { diffAt = i; break; }
                throw Error($"observer bytes differ at offset {diffAt}: exp={expected.Observer.ElementAtOrDefault(diffAt):X2} act={observer.ElementAtOrDefault(diffAt):X2}");
            }
            var particles = backend.CaptureParticles();
            if (!expected.Particles.SequenceEqual(particles)) {
                int diffAt = -1; for(int i=0; i<Math.Min(expected.Particles.Length, particles.Length); i++) if(expected.Particles[i] != particles[i]) { diffAt = i; break; }
                throw Error($"particles words differ at index {diffAt}: exp={expected.Particles.ElementAtOrDefault(diffAt):X8} act={particles.ElementAtOrDefault(diffAt):X8}");
            }
            backend.RequireBoundary();
            if (backend.NativeFrame != expected.NativeFrame) throw Error("native frame moved while reading clock/RNG");
        }
        private static byte[] Bytes(byte[] value)
        { if (value == null || value.Length == 0 || value.Length > MaximumPacket) throw Error("backend byte packet exceeds its bound"); return (byte[])value.Clone(); }
        private static uint[] Words(uint[] value)
        { if (value == null || value.Length == 0 || value.Length > MaximumPacket / 4) throw Error("backend particle packet exceeds its bound"); return (uint[])value.Clone(); }
        private static bool SameSlot(object? first, object? second, Type type)
            => ResidentValueAccess.SameControlValue(first,second,type,false);
        private static GameInputChannel ReadChannel(object owner, string name)
        {
            var field = GetField(owner.GetType(),name);
            if (field.FieldType != typeof(GameInputChannel)) throw Error("input channel storage type differs");
            return (GameInputChannel)(int)ResidentValueAccess.Scalar(ResidentValueAccess.ReadField(owner,field)!);
        }
        private static T Read<T>(object owner, string name) => (T)(GetField(owner.GetType(), name).GetValue(owner)!);
        private static void Set(object owner, string name, object? value) => GetField(owner.GetType(), name).SetValue(owner, value);
        private static FieldInfo GetField(Type type, string name)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType) {
                var found = current.GetField(name, InstanceFields | BindingFlags.DeclaredOnly); if (found != null) return found;
            }
            throw Error("actual input field is absent: " + type.FullName + "/" + name);
        }
        private static IReadOnlyDictionary<string, string> Ownership(params string[] groups)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var group in groups) { var split = group.IndexOf(':'); foreach (var name in group.Substring(split + 1).Split(',')) result.Add(name, group.Substring(0, split)); }
            return result;
        }
        private static string InputRoot(string owner, FieldInfo field, string storage)
            => storage == "D" || storage == "Frame" ? Prefix + storage + "/" + owner + "/" + field.MetadataToken.ToString("x8") : "";
        private static IReadOnlyDictionary<string, string> OwnershipFor(string owner, bool frameOnly)
        {
            var contract = owner == "input" ? InputFields : owner == "mouse" ? MouseFields : owner == "keyboard" ? KeyboardFields : KeyStateFields;
            if (!frameOnly) return contract;
            // These fields drive an in-progress native frame. Restore derives them
            // from the next Movie; a frame witness must retain their actual values.
            var observed = owner == "input" ? new[] { "nativeFrameActive", "nativeFrameRunning", "recording", "replaying", "authored", "customSamples", "expected", "runStarts", "fault" }
                : owner == "mouse" ? new[] { "replaying" } : owner == "keyboard" ? new[] { "active" } : Array.Empty<string>();
            var result = contract.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var name in observed) result[name] = "Frame";
            return result;
        }
        private sealed class References : IEqualityComparer<object>
        { internal static readonly References Instance = new References(); public new bool Equals(object? a, object? b) => ReferenceEquals(a, b); public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value); }
        private static InvalidDataException Error(string text) => new InvalidDataException("Resident clock/input " + text + ".");
    }
}
