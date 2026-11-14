using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // These are native owners despite not deriving from UnityEngine.Object.
    // The one managed graph uses explicit wrapper identities; native pointers
    // never become scalar payloads. Subclass fields remain typed graph slots.
    internal static partial class ResidentManagedNativeWrappers
    {
        private const string Prefix = "resident-native-wrapper/";
        private const string Abi = "d97e92a7640b10580b4e60139eacf01828f74baaef53f75e08b9fdd6193fbe5e/wrappers-v2-storage";
        private const int MaximumWrappers = 32768, MaximumKeys = 262144;
        private const BindingFlags AllFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Type[] Bases = { typeof(MaterialPropertyBlock), typeof(AnimationCurve), typeof(Gradient) };
        private static readonly FieldInfo[] PointerFields = Bases.Select(type => type.GetField("m_Ptr", AllFields)
            ?? throw Error("native wrapper pointer field is absent")).ToArray();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateSimple();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateCurve(IntPtr emptyKeys);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DestroyNative(IntPtr owner);

        internal sealed class Data
        {
            public int Version = 2;
            public string NativeAbi = Abi;
            public Wrapper[] Wrappers = Array.Empty<Wrapper>();
            public Native[] Natives = Array.Empty<Native>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class Wrapper
        {
            public string Key = "", Native = "";
            public int Kind;
            public ResidentManagedTypeCatalog.TypeReference Type = null!;
            public ManagedField[] Fields = Array.Empty<ManagedField>();
        }
        internal sealed class ManagedField
        {
            public string Name = "", Storage = "", Root = "";
            public int Token, Attributes;
            public ResidentManagedTypeCatalog.TypeReference DeclaringType = null!, ValueType = null!;
        }
        internal sealed class Native
        {
            public string Key = "";
            public int Kind, References;
            public ResidentShaderPropertySheet.Data? Sheet;
            public SheetStorageData? SheetStorage;
            public CurveData? Curve;
            public GradientData? Gradient;
        }
        internal sealed class CurveData
        {
            // Both native interpolation caches are semantic D. Evaluate's
            // loop offset uses the previous sample's rounded float time;
            // recomputing a cold cache can change the next returned bits.
            // Preserve both complete 28-byte caches, including invalid tails.
            public byte[] Caches = Array.Empty<byte>(), Keys = Array.Empty<byte>();
            public int Capacity, KeyCount, KeyMemoryLabel = 0x4a, KeyStorageFlags = 1;
            public byte[] UnusedKeys = Array.Empty<byte>(), StoragePadding = new byte[8];
            public int PreInfinity, PostInfinity, RotationOrder;
        }
        internal sealed class GradientData
        {
            // All eight slots are represented, including currently unused
            // entries; the color and alpha counts do not truncate storage.
            public byte[] Keys = Array.Empty<byte>(), ColorTimes = Array.Empty<byte>(), AlphaTimes = Array.Empty<byte>();
            public byte[] StoragePadding = new byte[2];
            public int ColorCount, AlphaCount, Mode;
        }
        internal sealed class Discovery
        {
            internal readonly object[] Wrappers;
            internal readonly int[] Kinds;
            internal readonly IntPtr[] Pointers;
            internal Discovery(object[] wrappers, int[] kinds, IntPtr[] pointers) { Wrappers = wrappers; Kinds = kinds; Pointers = pointers; }
        }

        internal sealed class Collector
        {
            private readonly Dictionary<object, WrapperLive> known = new Dictionary<object, WrapperLive>(References.Instance);
            internal readonly List<WrapperLive> Rows = new List<WrapperLive>();
            internal string? Discover(object owner, Action<string, object> addRoot)
            {
                var kind = Kind(owner.GetType());
                if (kind < 0) return null;
                if (known.TryGetValue(owner, out var existing)) return existing.Row.Key;
                if (Rows.Count >= MaximumWrappers) throw Error("managed native wrapper directory exceeds its bound");
                var row = DescribeWrapper(owner.GetType(), WrapperKey(Rows.Count), "");
                var pointer = (IntPtr)PointerFields[kind].GetValue(owner)!;
                var wrapper = new WrapperLive(owner, row, pointer, MakeSlots(row, owner));
                Rows.Add(wrapper); known.Add(owner, wrapper);
                foreach (var field in wrapper.Fields) addRoot(field.Row.Root, field.Slot);
                return row.Key;
            }
        }

        internal static CaptureResult Capture(IReadOnlyDictionary<string, object> graphRoots, ResidentGraphPolicy policy,
            Func<object, string?> externalBinding, Func<object, string?> bindAsset, Action admitted, Collector collected)
        {
            admitted(); LoadingEngineClockProbe.RequirePinnedEngine();
            var data = new Data(); var rows = collected.Rows; var natives = new List<NativeLive>();
            var pointers = new Dictionary<IntPtr, NativeLive>(); var gaps = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var wrapper in rows) {
                var row = wrapper.Row; var kind = row.Kind; var pointer = wrapper.Pointer;
                if (pointer != IntPtr.Zero) {
                    if (!pointers.TryGetValue(pointer, out var native)) {
                        native = ReadNative(NativeKey(natives.Count), kind, pointer, bindAsset); natives.Add(native); pointers.Add(pointer, native);
                    } else if (native.Row.Kind != kind) throw Error("one native address is claimed by different wrapper types");
                    native.Wrappers++;
                    row.Native = native.Row.Key;
                }
                foreach (var field in row.Fields.Where(field => field.Storage == "unsupported-pointer"))
                    gaps.Add("managed-wrapper-pointer-field:" + row.Key + "/" + field.Name);
            }
            foreach (var native in natives) {
                if (native.Row.Kind == 0) {
                    if (native.Row.References < native.Wrappers) throw Error("native sheet references cannot own all distinct wrappers");
                    if (native.Row.References != native.Wrappers) gaps.Add("property-sheet-shared-native-references:" + native.Row.Key);
                    foreach (var gap in native.Row.Sheet!.CoverageGaps) gaps.Add("wrapper-sheet:" + native.Row.Key + "/" + gap);
                    foreach (var gap in SheetStorageGaps(native.Row.SheetStorage!, native.Row.Key)) gaps.Add(gap);
                } else {
                    native.Row.References = native.Wrappers;
                    if (native.Wrappers != 1) gaps.Add("unowned-native-wrapper-alias:" + native.Row.Key);
                    if (native.Row.Kind == 1) foreach (var gap in CurveStorageGaps(native.Row.Curve!, native.Row.Key)) gaps.Add(gap);
                }
            }
            data.Wrappers = rows.Select(row => row.Row).ToArray(); data.Natives = natives.Select(native => native.Row).ToArray(); data.CoverageGaps = gaps.ToArray(); Validate(data);
            var result = new CaptureResult(data, graphRoots, policy, externalBinding, bindAsset, admitted, rows.ToArray(), natives.ToArray());
            result.RequireUnchanged(); return result;
        }

        // Discovery is deliberately managed-only and can be tested without
        // dereferencing a fake native pointer. Actual capture always pins ABI.
        internal static Discovery Discover(IReadOnlyDictionary<string, object> graphRoots, ResidentGraphPolicy policy, Func<object, string?> externalBinding)
        {
            if (graphRoots == null || policy == null || externalBinding == null) throw new ArgumentNullException();
            var seen = new HashSet<object>(References.Instance); var pending = new Queue<Tuple<object, int>>();
            var wrappers = new List<object>(); var kinds = new List<int>(); var pointers = new List<IntPtr>(); var values = 0;
            void Visit(object? value, int depth) {
                if (value == null) return;
                if (++values > CanonicalCarryGraphCodec.MaximumValues || depth > 128) throw Error("managed wrapper discovery exceeds its graph bound");
                var type = ResidentValueAccess.TypeOf(value);
                if (type.IsPrimitive || type.IsEnum || value is string || value is decimal || type == typeof(IntPtr) || type == typeof(UIntPtr) || ResidentGraphPolicy.IsMetadata(value)) return;
                var external = ResidentValueAccess.IsView(value) ? null : externalBinding(value);
                if (external != null && !external.StartsWith(Prefix, StringComparison.Ordinal)) return;
                var kind = Kind(type);
                if (kind < 0 && ResidentGraphPolicy.IsTerminalForWrapperSearch(value, type)) return;
                if (!ResidentValueAccess.IsView(value) && !seen.Add(value)) return;
                if (seen.Count > CanonicalCarryGraphCodec.MaximumNodes) throw Error("managed wrapper discovery exceeds its node bound: " + ResidentGraphPolicy.DescribeNodeBudget(seen));
                if (kind >= 0) {
                    if (wrappers.Count >= MaximumWrappers) throw Error("managed native wrapper directory exceeds its bound");
                    RequirePointerLayout(kind); wrappers.Add(value); kinds.Add(kind); pointers.Add((IntPtr)PointerFields[kind].GetValue(value)!);
                }
                pending.Enqueue(Tuple.Create(value, depth));
            }
            foreach (var root in graphRoots.Where(pair => !pair.Key.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal)) Visit(root.Value, 0);
            while (pending.Count != 0) {
                var item = pending.Dequeue(); var owner = item.Item1;
                if (owner is Delegate callback) { foreach (var call in callback.GetInvocationList()) Visit(call.Target, item.Item2 + 1); }
                else if (owner is Array array) {
                    if (array.Rank != 1 || array.GetLowerBound(0) != 0 || array.Length > CanonicalCarryGraphCodec.MaximumNodes) throw Error("managed wrapper array shape differs");
                    if (!ResidentGraphPolicy.CanContainReferences(array.GetType().GetElementType()!)) continue;
                    for (var index = 0; index < array.Length; index++) Visit(ResidentValueAccess.ReadArray(array, index), item.Item2 + 1);
                } else foreach (var read in ResidentGraphPolicy.ReadersForReferences(ResidentValueAccess.TypeOf(owner)))
                    Visit(read(owner), item.Item2 + 1);
            }
            return new Discovery(wrappers.ToArray(), kinds.ToArray(), pointers.ToArray());
        }

        internal sealed class CaptureResult
        {
            internal readonly Data Snapshot;
            internal readonly IReadOnlyDictionary<string, object> ManagedRoots;
            internal readonly WrapperLive[] Wrappers;
            internal readonly NativeLive[] Natives;
            internal readonly IReadOnlyDictionary<string, object> GraphRoots;
            internal readonly ResidentGraphPolicy Policy;
            internal readonly Func<object, string?> ExternalBinding, BindAsset;
            private readonly Action admitted;
            private readonly string frozen;
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            private readonly Dictionary<object, string> bindings;
            private readonly Dictionary<string, object> resolved;
            internal CaptureResult(Data data, IReadOnlyDictionary<string, object> roots, ResidentGraphPolicy policy,
                Func<object, string?> external, Func<object, string?> bindAsset, Action admitted, WrapperLive[] wrappers, NativeLive[] natives)
            {
                Snapshot = data; GraphRoots = roots; Policy = policy; ExternalBinding = external; BindAsset = bindAsset;
                this.admitted = admitted; Wrappers = wrappers; Natives = natives; frozen = JsonConvert.SerializeObject(data);
                ManagedRoots = wrappers.SelectMany(wrapper => wrapper.Fields).ToDictionary(field => field.Row.Root, field => field.Slot, StringComparer.Ordinal);
                bindings = wrappers.ToDictionary(wrapper => wrapper.Owner, wrapper => wrapper.Row.Key, References.Instance);
                resolved = wrappers.ToDictionary(wrapper => wrapper.Row.Key, wrapper => wrapper.Owner, StringComparer.Ordinal);
            }
            internal string? BindExternal(object value) => bindings.TryGetValue(value, out var key) ? key : null;
            internal object ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : throw Error("unknown managed wrapper identity: " + key);
            internal object ResolveManagedRoot(string key) => ManagedRoots.TryGetValue(key, out var value) ? value : throw Error("unknown wrapper field root: " + key);
            internal void RequireComplete() => RequireCompleteCoverage(Snapshot);
            internal void RequireUnchanged()
            {
                RequireNativeUnchanged();
                // Reachability is independently rediscovered with the whole managed graph.
                // Check the frozen native owners and every projected subclass slot here.
                foreach (var wrapper in Wrappers) foreach (var field in wrapper.Fields)
                    if (!SameSlot(field.Original, ResidentValueAccess.ReadField(wrapper.Owner, field.Field), field.Field.FieldType)) throw Error("wrapper managed field changed: " + field.Row.Root);
                RequireNativeUnchanged();
            }
            internal void RequireNativeUnchanged()
            {
                if (Thread.CurrentThread.ManagedThreadId != thread) throw Error("wrapper observation moved to another thread");
                admitted();
                if (frozen != JsonConvert.SerializeObject(Snapshot)) throw Error("wrapper snapshot was changed after capture");
                foreach (var wrapper in Wrappers) if ((IntPtr)PointerFields[wrapper.Row.Kind].GetValue(wrapper.Owner)! != wrapper.Pointer) throw Error("original wrapper native identity changed");
                foreach (var native in Natives) native.RequireUnchanged(); admitted();
            }
        }

        internal static Plan Prepare(Data source, CaptureResult target, ResidentMutationScope scope,
            Func<string, UnityEngine.Object> resolveAsset, Func<object, string?> bindAsset, Action admitted)
            => new Plan(source, target, scope, resolveAsset, bindAsset, admitted);

        internal sealed class Plan : IDisposable
        {
            private readonly Data source;
            private readonly CaptureResult target;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Func<object, string?> bindAsset;
            private readonly Dictionary<string, AllocatedNative> natives = new Dictionary<string, AllocatedNative>(StringComparer.Ordinal);
            private readonly List<WrapperLive> wrappers = new List<WrapperLive>();
            private readonly Dictionary<object, string> bindings = new Dictionary<object, string>(References.Instance);
            private readonly Dictionary<string, object> resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            private readonly int thread = Thread.CurrentThread.ManagedThreadId;
            private State state;
            private bool ownershipTransferred;
            private enum State { Prepared, Applying, Applied, Faulted, Disposed }
            internal IReadOnlyDictionary<string, object> ManagedRoots { get; private set; } = new Dictionary<string, object>();
            internal string? BindExternal(object value) => bindings.TryGetValue(value, out var key) ? key : null;
            internal object ResolveExternal(string key) => resolved.TryGetValue(key, out var value) ? value : throw Error("unknown prepared wrapper identity: " + key);
            internal object ResolveManagedRoot(string key) => ManagedRoots.TryGetValue(key, out var value) ? value : throw Error("unknown prepared wrapper field root: " + key);

            internal Plan(Data source, CaptureResult target, ResidentMutationScope scope, Func<string, UnityEngine.Object> resolveAsset,
                Func<object, string?> bindAsset, Action admitted)
            {
                RequireCanonicalState(source);
                this.target = target ?? throw new ArgumentNullException(nameof(target)); this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.bindAsset = bindAsset ?? throw new ArgumentNullException(nameof(bindAsset)); this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                this.source = Copy(source); scope.Require(); admitted(); target.RequireUnchanged();
                try {
                    foreach (var native in this.source.Natives) { scope.Require(); admitted(); natives.Add(native.Key, new AllocatedNative(native.Kind, CreateNativeOwner(native, resolveAsset, bindAsset, admitted))); }
                    foreach (var row in this.source.Wrappers) {
                        scope.Require(); admitted(); var type = ResidentManagedTypeCatalog.ResolveType(row.Type);
                        var owner = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type); GC.SuppressFinalize(owner);
                        var pointer = row.Native.Length == 0 ? IntPtr.Zero : natives[row.Native].Pointer;
                        PointerFields[row.Kind].SetValue(owner, pointer);
                        wrappers.Add(new WrapperLive(owner, row, pointer, MakeSlots(row, owner))); bindings.Add(owner, row.Key); resolved.Add(row.Key, owner);
                    }
                    foreach (var native in this.source.Natives) {
                        var group = natives[native.Key]; var count = this.source.Wrappers.Count(wrapper => wrapper.Native == native.Key);
                        if (native.Kind == 0) { M.Write(IntPtr.Add(group.Pointer, 12), BitConverter.GetBytes(count)); group.Releases = count; }
                    }
                    ManagedRoots = wrappers.SelectMany(wrapper => wrapper.Fields).ToDictionary(field => field.Row.Root, field => field.Slot, StringComparer.Ordinal);
                    VerifyNative(); target.RequireUnchanged();
                } catch { DisposeUntransferred(); state = State.Faulted; throw; }
            }
            internal void RequireComplete() => RequireCompleteCoverage(source);
            internal void Apply()
            {
                if (state != State.Prepared || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("wrapper plan is reused, reentered or on another thread"); }
                try {
                    // The graph already points at the prepared wrappers here.
                    // Its retired target wrappers are still rooted by capture;
                    // only their native ownership must remain unchanged.
                    scope.Require(); admitted(); target.RequireNativeUnchanged(); state = State.Applying;
                    scope.Mutate(() => {
                        foreach (var wrapper in wrappers) foreach (var field in wrapper.Fields) {
                            RequireApplying(); ResidentValueAccess.WriteField(wrapper.Owner, field.Field, ResidentValueAccess.ReadField(field.Slot, field.Value));
                        }
                        RequireApplying(); VerifyFields(); VerifyNative(); RequireApplying();
                        // Native ownership moves to the normal Unity wrapper
                        // finalizers only after its complete graph fields exist.
                        foreach (var wrapper in wrappers) GC.ReRegisterForFinalize(wrapper.Owner);
                        ownershipTransferred = true;
                    });
                    RequireApplying(); state = State.Applied;
                } catch { if (!ownershipTransferred) foreach (var wrapper in wrappers) GC.SuppressFinalize(wrapper.Owner); state = State.Faulted; throw; }
            }
            internal void Verify(IReadOnlyDictionary<string, object> currentGraphRoots)
            {
                if (state != State.Applied || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("wrapper readback requires one completed application"); }
                try {
                    scope.Require(); admitted(); VerifyFields(); VerifyNative();
                    foreach (var pair in ManagedRoots)
                        if (!currentGraphRoots.TryGetValue(pair.Key, out var observed) || !ReferenceEquals(pair.Value, observed))
                            throw Error("restored wrapper field root differs");
                    // The independent post-apply graph pass checks all incoming aliases.

                } catch { state = State.Faulted; throw; }
            }
            private void RequireApplying()
            {
                if (state != State.Applying || thread != Thread.CurrentThread.ManagedThreadId) { state = State.Faulted; throw Error("wrapper transaction became reentrant or changed threads"); }
                scope.Require(); admitted(); if (state != State.Applying) throw Error("wrapper transaction fault was swallowed by its admission callback");
            }
            private void VerifyFields()
            {
                foreach (var wrapper in wrappers) {
                    if ((IntPtr)PointerFields[wrapper.Row.Kind].GetValue(wrapper.Owner)! != wrapper.Pointer) throw Error("prepared wrapper native pointer changed");
                    foreach (var field in wrapper.Fields) if (!SameSlot(ResidentValueAccess.ReadField(field.Slot, field.Value), ResidentValueAccess.ReadField(wrapper.Owner, field.Field), field.Field.FieldType)) throw Error("wrapper graph field readback differs: " + field.Row.Root);
                }
            }
            private void VerifyNative()
            {
                foreach (var native in source.Natives) {
                    admitted(); var actual = ReadNative(native.Key, native.Kind, natives[native.Key].Pointer, bindAsset).Row;
                    if (native.Kind != 0) actual.References = source.Wrappers.Count(wrapper => wrapper.Native == native.Key);
                    RequireNativeEqual(native, actual);
                }
            }
            public void Dispose()
            {
                if (state == State.Disposed) return;
                if (ownershipTransferred) { state = State.Disposed; return; }
                DisposeUntransferred(); state = State.Disposed;
            }
            private void DisposeUntransferred()
            {
                foreach (var wrapper in wrappers) { GC.SuppressFinalize(wrapper.Owner); PointerFields[wrapper.Row.Kind].SetValue(wrapper.Owner, IntPtr.Zero); }
                foreach (var native in natives.Values) native.Dispose(); natives.Clear();
            }
        }

        // Canon uses the exact same typed native state but keeps all managed
        // wrapper identities. Allocator capacity and sheet sharing are rebuilt
        // dependencies; no game animation evaluation or property getter runs.
        internal static void Canonicalize(CaptureResult before, ResidentMutationScope scope,
            Func<string, UnityEngine.Object> resolveAsset, Func<object, string?> bindAsset, Action admitted)
        {
            if (before == null || scope == null || resolveAsset == null || bindAsset == null || admitted == null) throw new ArgumentNullException();
            before.RequireUnchanged(); var normalized = NormalizeStorage(before.Snapshot); RequireCanonicalState(normalized);
            var next = new Dictionary<string, AllocatedNative>(StringComparer.Ordinal); var mutationStarted = false;
            try {
                scope.Require(); admitted();
                foreach (var row in normalized.Natives) next.Add(row.Key, new AllocatedNative(row.Kind, CreateNativeOwner(row, resolveAsset, bindAsset, admitted)));
                foreach (var row in normalized.Natives.Where(row => row.Kind == 0)) {
                    var group = next[row.Key]; M.Write(IntPtr.Add(group.Pointer, 12), BitConverter.GetBytes(row.References)); group.Releases = row.References;
                }
                before.RequireUnchanged();
                scope.Mutate(() => {
                    // Once a visible wrapper has acquired a new pointer, a
                    // fault requires process retirement. Never free a partially
                    // installed owner and leave that wrapper dangling.
                    mutationStarted = true;
                    foreach (var wrapper in before.Wrappers) {
                        scope.Require(); admitted(); var pointer = wrapper.Row.Native.Length == 0 ? IntPtr.Zero : next[wrapper.Row.Native].Pointer;
                        PointerFields[wrapper.Row.Kind].SetValue(wrapper.Owner, pointer);
                    }
                    // Every old graph wrapper has relinquished one reference.
                    // Other renderer owners, if any, retain their own COW sheet.
                    foreach (var native in before.Natives) for (var index = 0; index < native.Wrappers; index++) Destroy(native.Row.Kind, native.Pointer);
                    var collector = new Collector();
                    foreach (var wrapper in before.Wrappers) collector.Discover(wrapper.Owner, (_, __) => { });
                    var after = Capture(before.GraphRoots, before.Policy, before.ExternalBinding, bindAsset, admitted, collector);
                    if (JsonConvert.SerializeObject(normalized) != JsonConvert.SerializeObject(after.Snapshot)) throw Error("common wrapper Canon did not reproduce complete native/managed identity metadata");
                });
            } finally { if (!mutationStarted) foreach (var native in next.Values) native.Dispose(); }
        }

        internal static Data NormalizeStorage(Data source)
        {
            Validate(source); var result = Copy(source);
            foreach (var native in result.Natives) {
                if (native.Kind == 0) {
                    native.References = result.Wrappers.Count(wrapper => wrapper.Native == native.Key);
                    if (native.Sheet!.CoverageGaps.Length == 0) NormalizeSheetStorage(native.Sheet, native.SheetStorage!);
                }
                if (native.Kind == 1) { NormalizeCurveStorage(native.Curve!); native.Curve!.KeyMemoryLabel = 0x4a; }
                if (native.Kind == 2) native.Gradient!.StoragePadding = new byte[2];
            }
            var removed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var native in source.Natives) {
                if (native.Kind == 0) removed.Add("property-sheet-shared-native-references:" + native.Key);
                if (native.Kind == 1) removed.Add("curve-storage-capacity:" + native.Key);
            }
            result.CoverageGaps = result.CoverageGaps.Where(gap => !removed.Contains(gap)).ToArray(); Validate(result); return result;
        }

        internal sealed class WrapperLive
        {
            internal readonly object Owner;
            internal readonly Wrapper Row;
            internal readonly IntPtr Pointer;
            internal readonly FieldSlot[] Fields;
            internal WrapperLive(object owner, Wrapper row, IntPtr pointer, FieldSlot[] fields) { Owner = owner; Row = row; Pointer = pointer; Fields = fields; }
        }
        internal sealed class FieldSlot
        {
            internal readonly ManagedField Row;
            internal readonly FieldInfo Field, Value;
            internal readonly object Slot;
            internal readonly object? Original;
            internal FieldSlot(ManagedField row, FieldInfo field, object owner)
            {
                Row = row; Field = field; Original = ResidentValueAccess.ReadField(owner, field);
                var type = typeof(ResidentStaticRoots.Slot<>).MakeGenericType(field.FieldType);
                Slot = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type); Value = type.GetField("Value")!; ResidentValueAccess.WriteField(Slot, Value, Original);
            }
        }
        internal sealed class NativeLive
        {
            internal readonly Native Row;
            internal readonly IntPtr Pointer;
            internal int Wrappers;
            private readonly Action unchanged;
            internal NativeLive(Native row, IntPtr pointer, Action unchanged) { Row = row; Pointer = pointer; this.unchanged = unchanged; }
            internal void RequireUnchanged() => unchanged();
        }
        private sealed class AllocatedNative : IDisposable
        {
            internal readonly int Kind;
            internal IntPtr Pointer;
            internal int Releases = 1;
            internal AllocatedNative(int kind, IntPtr pointer) { Kind = kind; Pointer = pointer; }
            public void Dispose() { var old = Pointer; Pointer = IntPtr.Zero; if (old != IntPtr.Zero) for (var index = 0; index < Releases; index++) Destroy(Kind, old); }
        }

        // AudioSource embeds the very same 104-byte curve type. These helpers
        // retain the destination address and transfer only target-owned array
        // storage through the engine's normal constructor/destructor pair.
        internal static CurveData CaptureCurveNative(IntPtr pointer)
        {
            LoadingEngineClockProbe.RequirePinnedEngine(); var result = ReadCurve(pointer, out _, out _); ValidateCurve(result); return result;
        }
        internal static void ApplyCurveNative(CurveData source, IntPtr destination, Action admitted)
        {
            if (admitted == null) throw new ArgumentNullException(nameof(admitted));
            RequireCanonicalCurveStorage(source);
            admitted(); _ = ReadCurve(destination, out var before, out var previousKeys);
            if (M.I32(before, 64) != source.KeyMemoryLabel) throw Error("embedded curve allocator category differs from its target owner");
            var temporary = CreateNativeOwner(new Native { Key = "embedded-curve", Kind = 1, References = 1, Curve = source },
                _ => throw Error("curve cannot resolve an asset"), _ => null, admitted);
            var installed = false;
            try {
                var next = M.Read(temporary, 104); admitted();
                if (!before.SequenceEqual(M.Read(destination, 104)) || !previousKeys.SequenceEqual(M.Read(M.Ptr(before, 56), previousKeys.Length))) throw Error("embedded curve changed before its admitted write");
                installed = true; M.Write(destination, next);
                // The temporary object now owns the old destination array, so
                // its normal destructor releases that array exactly once.
                M.Write(temporary, before); var retired = temporary; temporary = IntPtr.Zero; Destroy(1, retired);
                var actual = CaptureCurveNative(destination);
                if (JsonConvert.SerializeObject(source) != JsonConvert.SerializeObject(actual)) throw Error("embedded curve native readback differs"); admitted();
            } finally {
                // A partial visible write is an outer mutation-scope fault;
                // freeing its possible new array would leave a dangling owner.
                if (!installed && temporary != IntPtr.Zero) Destroy(1, temporary);
            }
        }
        internal static void CanonicalizeCurveNative(IntPtr destination, Action admitted)
        {
            var value = CaptureCurveNative(destination); NormalizeCurveStorage(value); ApplyCurveNative(value, destination, admitted);
        }
        private static CurveData ReadCurve(IntPtr pointer, out byte[] header, out byte[] keys)
        {
            header = M.Read(pointer, 104); var count = M.U64(header, 72); var capacity = M.U64(header, 80) >> 1;
            if (count > capacity || capacity > MaximumKeys || capacity != 0 && M.Ptr(header, 56) == IntPtr.Zero) throw Error("native curve key storage exceeds its bound");
            keys = M.Read(M.Ptr(header, 56), checked((int)capacity * 28));
            var result = new CurveData { Caches = M.Slice(header, 0, 56), Keys = M.Slice(keys, 0, checked((int)count * 28)), Capacity = (int)capacity,
                KeyCount = (int)count, KeyMemoryLabel = M.I32(header, 64), KeyStorageFlags = (int)(M.U64(header, 80) & 1),
                UnusedKeys = M.Slice(keys, checked((int)count * 28), checked((int)(capacity - count) * 28)),
                StoragePadding = M.Slice(header, 68, 4).Concat(M.Slice(header, 100, 4)).ToArray(),
                PreInfinity = M.I32(header, 88), PostInfinity = M.I32(header, 92), RotationOrder = M.I32(header, 96) };
            if (!header.SequenceEqual(M.Read(pointer, 104)) || !keys.SequenceEqual(M.Read(M.Ptr(header, 56), keys.Length))) throw Error("native curve changed while its fields were captured");
            return result;
        }

        private static NativeLive ReadNative(string key, int kind, IntPtr pointer, Func<object, string?> bindAsset)
        {
            if (kind == 0) {
                var header = M.Read(pointer, 160); var sheet = ResidentShaderPropertySheet.Capture(pointer, bindAsset);
                var names = M.Read(M.Ptr(header, 48), checked((int)(M.U64(header, 72) >> 1) * 4));
                var descriptors = M.Read(M.Ptr(header, 80), checked((int)(M.U64(header, 104) >> 1) * 4));
                var values = M.Read(M.Ptr(header, 112), checked((int)(M.U64(header, 136) >> 1)));
                var storage = ReadSheetStorage(sheet, header, names, descriptors, values);
                return new NativeLive(new Native { Key = key, Kind = kind, References = M.I32(header, 12), Sheet = sheet, SheetStorage = storage }, pointer, () => {
                    if (!header.SequenceEqual(M.Read(pointer, 160)) || !names.SequenceEqual(M.Read(M.Ptr(header, 48), names.Length))
                        || !descriptors.SequenceEqual(M.Read(M.Ptr(header, 80), descriptors.Length)) || !values.SequenceEqual(M.Read(M.Ptr(header, 112), values.Length))) throw Error("native property block changed during observation");
                });
            }
            if (kind == 1) {
                var row = new Native { Key = key, Kind = kind, Curve = ReadCurve(pointer, out var header, out var keys) };
                return new NativeLive(row, pointer, () => {
                    if (!header.SequenceEqual(M.Read(pointer, 104)) || !keys.SequenceEqual(M.Read(M.Ptr(header, 56), keys.Length))) throw Error("native animation curve changed during observation");
                });
            }
            if (kind == 2) {
                var bytes = M.Read(pointer, 168);
                var row = new Native { Key = key, Kind = kind, Gradient = new GradientData { Keys = M.Slice(bytes, 0, 128), ColorTimes = M.Slice(bytes, 128, 16),
                    AlphaTimes = M.Slice(bytes, 144, 16), ColorCount = bytes[160], AlphaCount = bytes[161], StoragePadding = M.Slice(bytes, 162, 2), Mode = M.I32(bytes, 164) } };
                return new NativeLive(row, pointer, () => { if (!bytes.SequenceEqual(M.Read(pointer, 168))) throw Error("native gradient changed during observation"); });
            }
            throw Error("native wrapper kind is unknown");
        }

        private static IntPtr CreateNativeOwner(Native row, Func<string, UnityEngine.Object> resolveAsset, Func<object, string?> bindAsset, Action admitted)
        {
            admitted(); IntPtr pointer;
            if (row.Kind == 0) pointer = Function<CreateSimple>(0x661700, "4883ec388b1572310201")();
            else if (row.Kind == 2) pointer = Function<CreateSimple>(0x85a0f0, "4883ec28b9a8000000e8")();
            else if (row.Kind == 1) {
                var header = Marshal.AllocHGlobal(32);
                try {
                    var empty = new byte[32]; Buffer.BlockCopy(BitConverter.GetBytes(1), 0, empty, 8, 4); Buffer.BlockCopy(BitConverter.GetBytes(1UL), 0, empty, 24, 8); Marshal.Copy(empty, 0, header, 32);
                    pointer = Function<CreateCurve>(0x73f880, "48895c2408574883ec308b15")(header);
                } finally { Marshal.FreeHGlobal(header); }
            } else throw Error("native wrapper allocation type is unknown");
            if (pointer == IntPtr.Zero) throw Error("native wrapper allocation failed");
            try {
                if (row.Kind == 0) {
                    using var sheet = ResidentShaderPropertySheet.Prepare(row.Sheet!, resolveAsset, bindAsset, admitted); sheet.CopyIntoUniqueSheet(pointer);
                    CanonicalizeSheetStorage(pointer, admitted);
                } else if (row.Kind == 1) {
                    var curve = row.Curve!; var count = curve.Keys.Length / 28;
                    RequireCanonicalCurveStorage(curve);
                    // The wrapper constructor owns a label-0x4a empty array.
                    // Embedded curves inherit their actual component label;
                    // select it before allocation, never relabel live storage.
                    M.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(curve.KeyMemoryLabel));
                    if (count != 0) M.Reserve(IntPtr.Add(pointer, 56), count, 28, 4, admitted);
                    M.Write(M.Ptr(IntPtr.Add(pointer, 56)), curve.Keys); M.Write(IntPtr.Add(pointer, 72), BitConverter.GetBytes((ulong)count));
                    M.Write(pointer, curve.Caches); M.Write(IntPtr.Add(pointer, 88), BitConverter.GetBytes(curve.PreInfinity)
                        .Concat(BitConverter.GetBytes(curve.PostInfinity)).Concat(BitConverter.GetBytes(curve.RotationOrder)).ToArray());
                    M.Write(IntPtr.Add(pointer, 68), new byte[4]); M.Write(IntPtr.Add(pointer, 100), new byte[4]);
                } else {
                    var gradient = row.Gradient!;
                    M.Write(pointer, gradient.Keys.Concat(gradient.ColorTimes).Concat(gradient.AlphaTimes)
                        .Concat(new[] { (byte)gradient.ColorCount, (byte)gradient.AlphaCount, (byte)0, (byte)0 }).Concat(BitConverter.GetBytes(gradient.Mode)).ToArray());
                }
                admitted(); return pointer;
            } catch { Destroy(row.Kind, pointer); throw; }
        }
        private static void Destroy(int kind, IntPtr pointer)
        {
            if (kind == 0) Function<DestroyNative>(0x661910, "4883ec284885c97447")(pointer);
            else if (kind == 1) Function<DestroyNative>(0x73f930, "4885c9742f534883ec20")(pointer);
            else if (kind == 2) Function<DestroyNative>(0x8597d0, "4885c9741f534883ec20")(pointer);
            else throw Error("native wrapper destruction type is unknown");
        }
        private static T Function<T>(int rva, string prefix) where T : Delegate
        {
            var pointer = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, rva);
            if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix) throw Error("native wrapper ABI prefix differs at " + rva.ToString("x", CultureInfo.InvariantCulture));
            return (T)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }

        internal static Wrapper DescribeWrapper(Type type, string key, string native)
        {
            var kind = Kind(type); if (kind < 0 || type.IsAbstract || type.ContainsGenericParameters || type.Assembly.IsDynamic) throw Error("actual wrapper type cannot be restored");
            RequirePointerLayout(kind); var pointer = PointerFields[kind];
            return new Wrapper { Key = key, Native = native, Kind = kind, Type = ResidentManagedTypeCatalog.DescribeType(type), Fields = CanonicalCarryGraphCodec.Fields(type).Select(field => {
                var storage = field.Module == pointer.Module && field.MetadataToken == pointer.MetadataToken ? "native-owner"
                    : field.FieldType == typeof(IntPtr) || field.FieldType == typeof(UIntPtr) || field.FieldType.IsPointer || field.FieldType.IsByRef ? "unsupported-pointer" : "D";
                return new ManagedField { Name = field.Name, Token = field.MetadataToken, Attributes = (int)field.Attributes, Storage = storage,
                    Root = storage == "D" ? key + "/field/" + field.DeclaringType!.Module.ModuleVersionId.ToString("N") + "/" + field.MetadataToken.ToString("x8") : "",
                    DeclaringType = ResidentManagedTypeCatalog.DescribeType(field.DeclaringType!), ValueType = ResidentManagedTypeCatalog.DescribeType(field.FieldType) };
            }).ToArray() };
        }
        private static FieldSlot[] MakeSlots(Wrapper row, object owner)
        {
            var fields = CanonicalCarryGraphCodec.Fields(owner.GetType());
            return row.Fields.Where(field => field.Storage == "D").Select(field => new FieldSlot(field, fields.Single(actual => actual.MetadataToken == field.Token
                && actual.DeclaringType == ResidentManagedTypeCatalog.ResolveType(field.DeclaringType)), owner)).ToArray();
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 2 || data.NativeAbi != Abi || data.Wrappers == null || data.Wrappers.Length > MaximumWrappers
                || data.Natives == null || data.Natives.Length > data.Wrappers.Length || data.CoverageGaps == null || data.CoverageGaps.Length > MaximumWrappers * 16
                || !data.CoverageGaps.SequenceEqual(data.CoverageGaps.Distinct().OrderBy(gap => gap, StringComparer.Ordinal))
                || data.CoverageGaps.Any(gap => string.IsNullOrEmpty(gap) || gap.Length > 16384 || gap.IndexOf('\0') >= 0)) throw Error("wrapper manifest shape differs");
            var groups = new Dictionary<string, Native>(StringComparer.Ordinal);
            for (var index = 0; index < data.Natives.Length; index++) {
                var row = data.Natives[index]; if (row == null || row.Key != NativeKey(index) || row.Kind < 0 || row.Kind >= Bases.Length || row.References < 1 || row.References > MaximumWrappers) throw Error("native owner row differs");
                if (row.Kind == 0) {
                    if (row.Sheet == null || row.SheetStorage == null || row.Curve != null || row.Gradient != null) throw Error("property-block native payload differs"); ResidentShaderPropertySheet.Validate(row.Sheet);
                    ValidateSheetStorage(row.Sheet, row.SheetStorage);
                    foreach (var gap in SheetStorageGaps(row.SheetStorage, row.Key)) if (!data.CoverageGaps.Contains(gap)) throw Error("property-sheet storage dependency lacks its coverage gap");
                    foreach (var gap in row.Sheet.CoverageGaps) if (!data.CoverageGaps.Contains("wrapper-sheet:" + row.Key + "/" + gap)) throw Error("property-sheet native gap is omitted");
                } else if (row.Kind == 1) {
                    if (row.Sheet != null || row.SheetStorage != null || row.Curve == null || row.Gradient != null) throw Error("curve native payload differs");
                    var curve = row.Curve;
                    ValidateCurve(curve);
                    foreach (var gap in CurveStorageGaps(curve, row.Key)) if (!data.CoverageGaps.Contains(gap)) throw Error("curve storage dependency lacks its coverage gap");
                } else {
                    if (row.Sheet != null || row.SheetStorage != null || row.Curve != null || row.Gradient == null) throw Error("gradient native payload differs");
                    var gradient = row.Gradient;
                    if (gradient.Keys == null || gradient.Keys.Length != 128 || gradient.ColorTimes == null || gradient.ColorTimes.Length != 16 || gradient.AlphaTimes == null || gradient.AlphaTimes.Length != 16
                        || gradient.StoragePadding == null || gradient.StoragePadding.Length != 2 || gradient.ColorCount < 0 || gradient.ColorCount > 8
                        || gradient.AlphaCount < 0 || gradient.AlphaCount > 8 || gradient.Mode < 0 || gradient.Mode > 1) throw Error("gradient native field shape differs");
                }
                groups.Add(row.Key, row);
            }
            var counts = groups.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
            for (var index = 0; index < data.Wrappers.Length; index++) {
                var row = data.Wrappers[index]; if (row == null || row.Key != WrapperKey(index) || row.Native == null || row.Type == null) throw Error("managed wrapper row differs");
                var type = ResidentManagedTypeCatalog.ResolveType(row.Type);
                if (JsonConvert.SerializeObject(row) != JsonConvert.SerializeObject(DescribeWrapper(type, row.Key, row.Native))) throw Error("managed wrapper type or complete field ownership differs");
                if (row.Native.Length != 0) { if (!groups.TryGetValue(row.Native, out var native) || native.Kind != row.Kind) throw Error("wrapper native owner type or identity differs"); counts[row.Native]++; }
                foreach (var field in row.Fields.Where(field => field.Storage == "unsupported-pointer"))
                    if (!data.CoverageGaps.Contains("managed-wrapper-pointer-field:" + row.Key + "/" + field.Name)) throw Error("wrapper pointer field lacks its native coverage gap");
            }
            foreach (var row in data.Natives) {
                var count = counts[row.Key]; if (count < 1 || row.References < count) throw Error("native owner is orphaned or under-referenced");
                if (row.Kind == 0 && row.References != count && !data.CoverageGaps.Contains("property-sheet-shared-native-references:" + row.Key)) throw Error("shared sheet dependency lacks its Canon gap");
                if (row.Kind != 0 && (row.References != count || count != 1 && !data.CoverageGaps.Contains("unowned-native-wrapper-alias:" + row.Key))) throw Error("native finalizer ownership is not established");
            }
        }
        internal static void RequireCompleteCoverage(Data data)
        { Validate(data); if (data.CoverageGaps.Length != 0) throw Error("coverage gaps: " + string.Join(";", data.CoverageGaps.Take(16))); }
        internal static void ValidateCurve(CurveData curve)
        {
            if (curve == null || curve.Caches == null || curve.Caches.Length != 56 || curve.Keys == null || curve.Keys.Length % 28 != 0 || curve.Keys.Length / 28 > MaximumKeys
                || curve.KeyCount != curve.Keys.Length / 28 || curve.Capacity < curve.KeyCount || curve.Capacity > MaximumKeys
                || curve.KeyStorageFlags < 0 || curve.KeyStorageFlags > 1 || curve.UnusedKeys == null || curve.UnusedKeys.Length != (curve.Capacity - curve.KeyCount) * 28
                || curve.StoragePadding == null || curve.StoragePadding.Length != 8 || curve.PreInfinity < 0 || curve.PreInfinity > 3
                || curve.PostInfinity < 0 || curve.PostInfinity > 3 || curve.RotationOrder < 0 || curve.RotationOrder > 5) throw Error("curve native field shape differs");
            for (var offset = 0; offset < 56; offset += 28) {
                var index = M.I32(curve.Caches, offset);
                if (index < -1 || index > Math.Max(0, curve.KeyCount - 1)) throw Error("curve interpolation cache references a key outside its native owner");
            }
        }
        private static IEnumerable<string> CurveStorageGaps(CurveData curve, string key)
        {
            if (curve.Capacity != curve.KeyCount) yield return "curve-storage-capacity:" + key;
            if (curve.KeyStorageFlags != 0 && curve.Capacity != 0) yield return "curve-borrowed-key-storage:" + key;
        }
        internal static void NormalizeCurveStorage(CurveData curve)
        {
            RequireCanonicalizableCurveStorage(curve); curve.Capacity = curve.KeyCount; curve.KeyStorageFlags = curve.KeyCount == 0 ? 1 : 0;
            curve.UnusedKeys = Array.Empty<byte>(); curve.StoragePadding = new byte[8];
        }
        internal static void RequireCanonicalizableCurveStorage(CurveData curve)
        {
            ValidateCurve(curve);
            if (curve.KeyStorageFlags != 0 && curve.Capacity != 0) throw Error("borrowed curve storage has no native owner/alias binding");
        }
        internal static void RequireCanonicalCurveStorage(CurveData curve)
        {
            ValidateCurve(curve);
            if (curve.Capacity != curve.KeyCount || curve.KeyStorageFlags != (curve.KeyCount == 0 ? 1 : 0)
                || curve.UnusedKeys.Length != 0 || curve.StoragePadding.Any(value => value != 0)) throw Error("curve native storage is not canonical");
        }
        internal static void RequireCanonicalState(Data data)
        {
            RequireCompleteCoverage(data);
            foreach (var native in data.Natives) {
                if (native.Kind == 0) RequireCanonicalSheetStorage(native.Sheet!, native.SheetStorage!);
                if (native.Kind == 1) {
                    RequireCanonicalCurveStorage(native.Curve!);
                    if (native.Curve!.KeyMemoryLabel != 0x4a) throw Error("wrapper curve allocator category is not canonical");
                }
                if (native.Kind == 2 && native.Gradient!.StoragePadding.Any(value => value != 0)) throw Error("gradient native storage is not canonical");
            }
        }
        private static void RequireNativeEqual(Native expected, Native actual)
        { if (JsonConvert.SerializeObject(expected) != JsonConvert.SerializeObject(actual)) throw Error("complete native wrapper readback differs: " + expected.Key); }
        private static int Kind(Type type) { for (var index = 0; index < Bases.Length; index++) if (Bases[index].IsAssignableFrom(type)) return index; return -1; }
        private static void RequirePointerLayout(int kind)
        { var field = PointerFields[kind]; if (field.FieldType != typeof(IntPtr) || field.IsStatic || field.DeclaringType != Bases[kind]) throw Error("native wrapper pointer ABI differs"); }
        private static string WrapperKey(int index) => Prefix + "object/" + index.ToString("D6", CultureInfo.InvariantCulture);
        private static string NativeKey(int index) => Prefix + "native/" + index.ToString("D6", CultureInfo.InvariantCulture);
        private static Data Copy(Data source) => JsonConvert.DeserializeObject<Data>(JsonConvert.SerializeObject(source)) ?? throw Error("wrapper snapshot copy failed");
        private static bool SameSlot(object? first, object? second, Type type)
        {
            if (first == null || second == null) return first == null && second == null;
            if (!type.IsValueType) return ReferenceEquals(first, second);
            if (type == typeof(float)) return BitConverter.ToInt32(BitConverter.GetBytes((float)first), 0) == BitConverter.ToInt32(BitConverter.GetBytes((float)second), 0);
            if (type == typeof(double)) return BitConverter.DoubleToInt64Bits((double)first) == BitConverter.DoubleToInt64Bits((double)second);
            if (type.IsPrimitive || type.IsEnum || type == typeof(decimal)) return ResidentValueAccess.Scalar(first).Equals(ResidentValueAccess.Scalar(second));
            return CanonicalCarryGraphCodec.Fields(type).All(field => SameSlot(ResidentValueAccess.ReadField(first, field), ResidentValueAccess.ReadField(second, field), field.FieldType));
        }
        private sealed class References : IEqualityComparer<object>
        { internal static readonly References Instance = new References(); public new bool Equals(object? a, object? b) => ReferenceEquals(a, b); public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value); }
        private static InvalidDataException Error(string detail) => new InvalidDataException("Resident managed native wrapper " + detail + ".");
    }
}
