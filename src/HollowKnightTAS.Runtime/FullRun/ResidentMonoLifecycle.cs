using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Engine ABI, not a scene/type/path whitelist. Cross-process data contains
    // owner bindings and scalar values only. Every native address below is
    // resolved afresh from the target process and its pinned engine.
    internal static class ResidentMonoLifecycle
    {
        private const int MaximumOwners = 100000, MaximumCallbacks = 4096;
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw Error("Unity object pointer ABI is absent");
        private static readonly int[] NodeOffsets = { 128, 152, 176, 200, 256 };
        private static readonly int[] ManagerGetters = { 0x629ee0, 0x629f00, 0x629f10 };
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeOwner(IntPtr owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void NativeInsertBefore(IntPtr before, IntPtr node);

        internal sealed class Data
        {
            public string Owner = "", Type = "";
            public bool Enabled, BehaviourAdded, ActiveInHierarchy, AudioFilterPresent, GuiStatePresent;
            public byte[] Lifecycle = Array.Empty<byte>();
            public int ExecutionOrder, GuiDisplayMask, PendingStarts, OtherCallbacks, ActiveCoroutines;
            public Membership[] Memberships = Array.Empty<Membership>();
            public RegistryData Registry = new RegistryData();
        }
        internal sealed class Membership
        {
            public int Kind, ExecutionOrder, Lane, Ordinal;
        }
        internal sealed class RegistryData
        {
            public ListData[] Lists = Array.Empty<ListData>();
            public ImageListData[] Images = Array.Empty<ImageListData>();
        }
        internal sealed class ListData
        {
            // Kind 0/1/2: Update/Fixed/Late; 3: GUI; 4: OnRenderObject.
            // Lane 0 is the current list, lane 1 the pending-add list.
            public int Kind, ExecutionOrder, Lane;
            public string[] Owners = Array.Empty<string>();
        }
        internal sealed class ImageListData
        {
            public string Camera = "";
            public ImageEntry[] BeforeOpaque = Array.Empty<ImageEntry>(), AfterOpaque = Array.Empty<ImageEntry>();
            public int TransformsToLdr;
        }
        internal sealed class ImageEntry
        {
            public string Owner = "";
            public long CallbackRva;
            public byte[] Flags = Array.Empty<byte>();
            public int Event;
        }

        internal static Data Capture(MonoBehaviour owner, Func<UnityEngine.Object, string> identity)
            => Capture(new[] { owner }, identity)[0];

        internal static Data[] Capture(IEnumerable<MonoBehaviour> owners, Func<UnityEngine.Object, string> identity)
        {
            if (owners == null || identity == null) throw new ArgumentNullException();
            var selected = owners.ToArray();
            if (selected.Length > MaximumOwners || selected.Any(owner => owner == null)
                || selected.Distinct(References<MonoBehaviour>.Instance).Count() != selected.Length) throw Error("resident owner directory differs");
            var reader = new Reader(identity);
            var registry = reader.Registry(selected);
            ValidateRegistry(registry);
            var callbacks = reader.CallbackCounts();
            var result = selected.Select(owner => reader.Owner(owner, registry, callbacks)).ToArray();
            reader.RequireFrozen();
            return result;
        }

        internal static Plan Prepare(Data[] source, MonoBehaviour[] targets, ResidentMutationScope scope, Action admitted,
            Func<UnityEngine.Object, string> identity, Func<string, UnityEngine.Object> resolve)
            => new Plan(source, targets, scope, admitted, identity, resolve);

        internal sealed class Plan
        {
            private readonly Data[] source, before;
            private readonly MonoBehaviour[] targets;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly Func<UnityEngine.Object, string> identity;
            private readonly Func<string, UnityEngine.Object> resolve;
            private readonly HashSet<string> owned;
            private bool applied;

            internal Plan(Data[] source, MonoBehaviour[] targets, ResidentMutationScope scope, Action admitted,
                Func<UnityEngine.Object, string> identity, Func<string, UnityEngine.Object> resolve)
            {
                if (source == null || targets == null || source.Length != targets.Length || source.Length > MaximumOwners
                    || source.Any(value => value == null) || targets.Any(value => value == null)) throw Error("lifecycle plan directory differs");
                this.source = Clone(source); this.targets = (MonoBehaviour[])targets.Clone(); this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted)); this.identity = identity ?? throw new ArgumentNullException(nameof(identity));
                this.resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
                owned = new HashSet<string>(this.source.Select(value => value.Owner), StringComparer.Ordinal);
                if (owned.Count != source.Length) throw Error("lifecycle owner bindings are aliased");
                if (this.source.Length != 0 && this.source.Any(value => !ReferenceEquals(value.Registry, this.source[0].Registry)))
                    throw Error("resident batch did not preserve its shared complete registration directory");
                if (this.source.Length != 0) ValidateRegistry(this.source[0].Registry);
                admitted();
                for (var index = 0; index < source.Length; index++) {
                    Validate(this.source[index]);
                    if (!scope.Owns(targets[index]) || TypeIdentity(targets[index]) != source[index].Type
                        || identity(targets[index]) != source[index].Owner || !ReferenceEquals(resolve(source[index].Owner), targets[index]))
                        throw Error("actual restored Mono type/owner binding differs");
                }
                before = Capture(targets, identity);
                if (source.Length != 0) {
                    RequireForeignLists(this.source[0].Registry, before[0].Registry, owned);
                    RequireForeignImages(this.source[0].Registry, before[0].Registry, owned);
                    foreach (var list in this.source[0].Registry.Lists) foreach (var binding in list.Owners) {
                        var component = resolve(binding) as Component;
                        if (component == null || !SameOwnerBinding(identity(component), binding)) throw Error("registration neighbour cannot be resolved in the target process");
                    }
                }
            }

            // The scheduler owns complete callback/coroutine payloads and their
            // epoch conversion. It must reconcile after native registrations,
            // since AddToManager legitimately queues a Start even for an owner
            // whose copied DidStart will make that callback a later no-op.
            internal void Apply(Action reconcileScheduler)
            {
                if (applied || reconcileScheduler == null) throw Error("lifecycle application is reused or lacks scheduler reconciliation");
                scope.Require(); admitted();
                try {
                    RequireSame(before, Capture(targets, identity), true);
                    var engine = Engine();
                    var remove = Entry<NativeOwner>(engine, 0x8b05e0, "48895c241855488bec4883ec40");
                    var add = Entry<NativeOwner>(engine, 0x8ab340, "40534883ec4083792002");
                    var insert = Entry<NativeInsertBefore>(engine, 0x289bf0, "483bd174384c8b024d85c0");
                    for (var index = 0; index < targets.Length; index++) {
                        scope.Require(); admitted();
                        var target = targets[index]; var data = source[index]; var pointer = Native(target);
                        if (target.gameObject.activeInHierarchy != data.ActiveInHierarchy)
                            throw Error("restore object activity must be applied before lifecycle registration");
                        // Removal covers update, GUI, render, image-filter and
                        // DSP bypass registration. Managed OnDisable is scoped.
                        remove(pointer);
                        Write(pointer, 56, new[] { data.Enabled ? (byte)1 : (byte)0, data.BehaviourAdded ? (byte)1 : (byte)0 });
                        var flags = (byte[])data.Lifecycle.Clone(); flags[4] = 0;
                        Write(pointer, 292, flags);
                        if (data.Lifecycle[4] != 0) add(pointer);
                        // Unity may have visited Awake while registering this
                        // actual type. The complete source graph is restored by
                        // the caller; these are its exact source lifecycle bits.
                        Write(pointer, 292, data.Lifecycle);
                        Write(pointer, 288, BitConverter.GetBytes(data.GuiDisplayMask));
                    }
                    if (source.Length != 0) {
                        var reader = new Reader(identity);
                        var actual = reader.Registry(targets);
                        RequireForeignLists(source[0].Registry, actual, owned);
                        RequireForeignImages(source[0].Registry, actual, owned);
                        reader.RequireFrozen();
                        foreach (var list in source[0].Registry.Lists) {
                            if (!list.Owners.Any(owner => owned.Contains(owner))) continue;
                            IntPtr head;
                            try { head = reader.ListHead(list.Kind, list.ExecutionOrder, list.Lane); }
                            catch (InvalidOperationException ex) {
                                var ownedCount = list.Owners.Count(owner => owned.Contains(owner));
                                var firstOwned = list.Owners.FirstOrDefault(owner => owned.Contains(owner));
                                var firstInfo = "";
                                if (firstOwned != null && resolve(firstOwned) is Component comp) {
                                    var match = Array.FindIndex(targets, item => ReferenceEquals(item, comp));
                                    firstInfo = match >= 0
                                        ? $" first={firstOwned}(lc4={source[match].Lifecycle[4]},en={source[match].Enabled},act={source[match].ActiveInHierarchy})"
                                        : $" first={firstOwned}(notInTargets)";
                                }
                                throw Error($"{ex.Message} [key={Key(list)} total={list.Owners.Length} owned={ownedCount}{firstInfo}]");
                            }
                            for (var index = list.Owners.Length - 1; index >= 0; index--) {
                                var binding = list.Owners[index];
                                if (!owned.Contains(binding)) continue;
                                var component = resolve(binding) as Component ?? throw Error("owned registration binding became absent");
                                var destination = index + 1 == list.Owners.Length ? head
                                    : IntPtr.Add(Native(resolve(list.Owners[index + 1])), NodeOffsets[list.Kind]);
                                scope.Require(); admitted();
                                // Original native intrusive-list insertion uses
                                // current target pointers; no source pointer is
                                // serialized or written into this process.
                                insert(destination, IntPtr.Add(Native(component), NodeOffsets[list.Kind]));
                            }
                        }
                    }
                    scope.Require(); reconcileScheduler(); scope.Require(); admitted();
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("lifecycle verification precedes application");
                scope.Require(); admitted();
                try { RequireSame(source, Capture(targets, identity), true); }
                catch (Exception error) { scope.Fault(error); throw; }
            }
        }

        private sealed class Reader
        {
            private readonly IntPtr engine;
            private readonly Func<UnityEngine.Object, string> identity;
            private readonly Dictionary<IntPtr, Component> native = new Dictionary<IntPtr, Component>();
            private readonly List<Action> registrations = new List<Action>();
            private readonly Dictionary<string, IntPtr> heads = new Dictionary<string, IntPtr>(StringComparer.Ordinal);
            private readonly Dictionary<IntPtr, byte[]> frozen = new Dictionary<IntPtr, byte[]>();
            private readonly Dictionary<string, Membership>[] byOwner = NodeOffsets.Select(_ =>
                new Dictionary<string, Membership>(StringComparer.Ordinal)).ToArray();
            internal Reader(Func<UnityEngine.Object, string> identity)
            {
                engine = Engine(); this.identity = identity;
            }
            private Component RegisteredComponent(IntPtr pointer)
            {
                if (native.TryGetValue(pointer,out var found)) return found;
                if (pointer == IntPtr.Zero || native.Count >= MaximumOwners) throw Error("invalid registered component directory");
                var id = BitConverter.ToInt32(Read(IntPtr.Add(pointer,8),4),0);
                var value = ResidentLoadedAssetIndex.BorrowOptional(id,out var recheck);
                if (!(value is Component component) || Native(component) != pointer) throw Error("native registration has no matching live component");
                registrations.Add(recheck); native.Add(pointer,component); return component;
            }
            internal Data Owner(MonoBehaviour owner, RegistryData registry, Dictionary<int, int[]> callbacks)
            {
                var pointer = Native(owner); var bytes = Read(pointer, 312); Freeze(pointer, bytes);
                var binding = Bind(owner); var id = BitConverter.ToInt32(bytes, 8);
                if (id != owner.GetInstanceID() || bytes[56] > 1 || bytes[57] > 1) throw Error("native Mono owner/enabled ABI differs");
                var flags = bytes.Skip(292).Take(5).ToArray();
                if (flags.Any(value => value > 1) || flags[3] != 0) throw Error("resident capture overlaps native object destruction");
                var script = Pointer(bytes, 240); var executionOrder = script == IntPtr.Zero ? 0 : BitConverter.ToInt32(Read(IntPtr.Add(script, 68), 4), 0);
                var memberships = new List<Membership>();
                for (var kind = 0; kind < NodeOffsets.Length; kind++) {
                    var present = byOwner[kind].TryGetValue(binding, out var membership);
                    var previous = Pointer(bytes, NodeOffsets[kind]); var next = Pointer(bytes, NodeOffsets[kind] + 8);
                    if ((previous == IntPtr.Zero) != (next == IntPtr.Zero)
                        || present != (previous != IntPtr.Zero)) throw Error("native Mono registration/list membership differs");
                    if (Pointer(bytes, NodeOffsets[kind] + 16) != pointer) throw Error("native Mono list node has a foreign data owner");
                    if (present) memberships.Add(membership!);
                }
                if (memberships.Count != 0 && flags[4] == 0) throw Error("unregistered Mono retains native update nodes");
                callbacks.TryGetValue(id, out var counts);
                var data = new Data { Owner = binding, Type = TypeIdentity(owner), Enabled = bytes[56] != 0, BehaviourAdded = bytes[57] != 0,
                    Lifecycle = flags, ActiveInHierarchy = owner.gameObject.activeInHierarchy, ExecutionOrder = executionOrder,
                    GuiDisplayMask = BitConverter.ToInt32(bytes, 288), GuiStatePresent = Pointer(bytes, 280) != IntPtr.Zero,
                    AudioFilterPresent = Pointer(bytes, 304) != IntPtr.Zero, PendingStarts = counts?[0] ?? 0, OtherCallbacks = counts?[1] ?? 0,
                    ActiveCoroutines = WalkList(IntPtr.Add(pointer, 112), null).Count, Memberships = memberships.ToArray(), Registry = registry };
                Validate(data); return data;
            }
            internal RegistryData Registry(MonoBehaviour[] selected)
            {
                var lists = new List<ListData>();
                for (var kind = 0; kind < 3; kind++) {
                    var manager = Getter(ManagerGetters[kind]);
                    if (manager == IntPtr.Zero) throw Error("native behaviour manager is absent");
                    var managerBytes = Read(manager, 24); Freeze(manager, managerBytes);
                    var sentinel = Pointer(managerBytes, 8); var count = Count(managerBytes, 16, MaximumOwners);
                    var header = Read(sentinel, 32); Freeze(sentinel, header);
                    if (header[25] != 1) throw Error("native execution-order map sentinel differs");
                    var visited = new HashSet<IntPtr>();
                    void Walk(IntPtr node, int depth)
                    {
                        if (node == sentinel) return;
                        if (node == IntPtr.Zero || depth > 128 || !visited.Add(node) || visited.Count > count) throw Error("native execution-order map links differ");
                        var bytes = Read(node, 56); Freeze(node, bytes);
                        if (bytes[25] != 0) throw Error("native execution-order map node differs");
                        Walk(Pointer(bytes, 0), depth + 1);
                        var order = BitConverter.ToInt32(bytes, 32);
                        for (var lane = 0; lane < 2; lane++) {
                            var head = Pointer(bytes, 40 + lane * 8);
                            if (head == IntPtr.Zero) throw Error("native execution-order list is absent");
                            AddList(lists, kind, order, lane, head);
                        }
                        Walk(Pointer(bytes, 16), depth + 1);
                    }
                    Walk(Pointer(header, 8), 0);
                    if (visited.Count != count) throw Error("native execution-order map count differs");
                }
                var gui = Getter(0x28e9c0); var render = Getter(0x5d66f0);
                if (gui != IntPtr.Zero) AddList(lists, 3, 0, 0, gui);
                if (render != IntPtr.Zero) AddList(lists, 4, 0, 0, IntPtr.Add(render, 184));
                var cameras = selected.Select(owner => owner.GetComponent<Camera>()).Where(camera => camera != null).Distinct().ToArray();
                var images = cameras.Select(ReadImages).ToArray();
                foreach (var list in lists) {
                    var index = byOwner[list.Kind];
                    for (var ordinal = 0; ordinal < list.Owners.Length; ordinal++) {
                        var owner = list.Owners[ordinal];
                        if (index.ContainsKey(owner)) throw Error("duplicate native Mono registration for the same update kind");
                        index.Add(owner, new Membership { Kind = list.Kind, ExecutionOrder = list.ExecutionOrder, Lane = list.Lane, Ordinal = ordinal });
                    }
                }
                return new RegistryData { Lists = lists.OrderBy(list => list.Kind).ThenBy(list => list.ExecutionOrder).ThenBy(list => list.Lane).ToArray(), Images = images };
            }
            private void AddList(List<ListData> lists, int kind, int order, int lane, IntPtr head)
            {
                var key = Key(kind, order, lane); if (heads.ContainsKey(key)) throw Error("native registration lists are aliased");
                heads.Add(key, head);
                var nodes = WalkList(head, node => {
                    var bytes = Read(node, 24); Freeze(node, bytes); var owner = Pointer(bytes, 16);
                    if (IntPtr.Add(owner, NodeOffsets[kind]) != node)
                        throw Error("native update list contains an unbound component node");
                    return Bind(RegisteredComponent(owner));
                });
                if (nodes.Distinct(StringComparer.Ordinal).Count() != nodes.Count) throw Error("native list owner bindings are aliased");
                lists.Add(new ListData { Kind = kind, ExecutionOrder = order, Lane = lane, Owners = nodes.ToArray() });
            }
            internal IntPtr ListHead(int kind, int order, int lane) => heads.TryGetValue(Key(kind, order, lane), out var head)
                ? head : throw Error("source registration group has not been created by the original target registration");
            private List<string> WalkList(IntPtr head, Func<IntPtr, string>? owner)
            {
                var header = Read(head, 16); Freeze(head, header);
                var result = new List<string>(); var seen = new HashSet<IntPtr>(); var previous = head; var next = Pointer(header, 8);
                while (next != head) {
                    if (next == IntPtr.Zero || !seen.Add(next) || seen.Count > MaximumOwners) throw Error("native intrusive list is invalid");
                    var bytes = Read(next, 16); Freeze(next, bytes);
                    if (Pointer(bytes, 0) != previous) throw Error("native intrusive list predecessor differs");
                    result.Add(owner == null ? "" : owner(next)); previous = next; next = Pointer(bytes, 8);
                }
                if (Pointer(header, 0) != previous) throw Error("native intrusive list tail differs");
                return result;
            }
            private ImageListData ReadImages(Camera camera)
            {
                var cameraPointer = Native(camera); var renderLoopBytes = Read(IntPtr.Add(cameraPointer, 1336), 8); Freeze(IntPtr.Add(cameraPointer, 1336), renderLoopBytes);
                var renderLoop = Pointer(renderLoopBytes, 0);
                if (renderLoop == IntPtr.Zero) return new ImageListData { Camera = Bind(camera) };
                var pointer = IntPtr.Add(renderLoop, 8); var header = Read(pointer, 56); Freeze(pointer, header);
                ImageEntry[] Entries(int vector)
                {
                    var first = Pointer(header, vector); var end = Pointer(header, vector + 8); var capacity = Pointer(header, vector + 16);
                    var length = end.ToInt64() - first.ToInt64();
                    if (length < 0 || length % 24 != 0 || length > MaximumOwners * 24L || capacity.ToInt64() < end.ToInt64()) throw Error("native image filter vector differs");
                    var bytes = Read(first, checked((int)length)); if (bytes.Length != 0) Freeze(first, bytes);
                    return Enumerable.Range(0, checked((int)(length / 24))).Select(index => {
                        var offset = index * 24; var owner = Pointer(bytes, offset);
                        var component = RegisteredComponent(owner);
                        var callback = Pointer(bytes, offset + 8).ToInt64() - engine.ToInt64();
                        if (callback < 0 || callback >= LoadingEngineClockProbe.RequirePinnedEngine().ModuleMemorySize) throw Error("image filter callback is outside the pinned engine");
                        // Bytes 18..19 are native struct padding, not fields.
                        return new ImageEntry { Owner = Bind(component), CallbackRva = callback, Flags = bytes.Skip(offset + 16).Take(2).ToArray(), Event = BitConverter.ToInt32(bytes, offset + 20) };
                    }).ToArray();
                }
                return new ImageListData { Camera = Bind(camera), BeforeOpaque = Entries(0), AfterOpaque = Entries(24), TransformsToLdr = BitConverter.ToInt32(header, 48) };
            }
            internal Dictionary<int, int[]> CallbackCounts()
            {
                var managerPointer = IntPtr.Add(engine, 0x19d7a70); var global = Read(managerPointer, 8); Freeze(managerPointer, global);
                var manager = Pointer(global, 0); var state = Read(manager, 80); Freeze(manager, state);
                var head = Pointer(state, 48); var count = Count(state, 56, MaximumCallbacks); var header = Read(head, 32); Freeze(head, header);
                var result = new Dictionary<int, int[]>(); var seen = new HashSet<IntPtr>();
                void Walk(IntPtr node, int depth)
                {
                    if (node == head) return;
                    if (node == IntPtr.Zero || depth > 128 || !seen.Add(node) || seen.Count > count) throw Error("native callback tree differs");
                    var bytes = Read(node, 96); Freeze(node, bytes);
                    if (bytes[25] != 0) throw Error("native callback sentinel differs");
                    Walk(Pointer(bytes, 0), depth + 1);
                    var owner = BitConverter.ToInt32(bytes, 80);
                    if (!result.TryGetValue(owner, out var counts)) result.Add(owner, counts = new int[2]);
                    counts[Pointer(bytes, 64) == IntPtr.Add(engine, 0x8ac680) ? 0 : 1]++;
                    Walk(Pointer(bytes, 16), depth + 1);
                }
                Walk(Pointer(header, 8), 0); if (seen.Count != count) throw Error("native callback count differs"); return result;
            }
            private IntPtr Getter(int rva)
            {
                var code = Read(IntPtr.Add(engine, rva), 8);
                if (code[0] != 0x48 || code[1] != 0x8b || code[2] != 0x05 || code[7] != 0xc3) throw Error("native manager getter ABI differs");
                var global = IntPtr.Add(engine, checked(rva + 7 + BitConverter.ToInt32(code, 3))); var bytes = Read(global, 8); Freeze(global, bytes); return Pointer(bytes, 0);
            }
            private string Bind(UnityEngine.Object owner)
            {
                var binding = identity(owner);
                return !string.IsNullOrEmpty(binding) ? binding : throw Error("native registration owner has no resident/external identity");
            }
            private void Freeze(IntPtr address, byte[] bytes)
            {
                if (frozen.TryGetValue(address, out var previous)) {
                    var length = Math.Min(previous.Length, bytes.Length);
                    if (!previous.Take(length).SequenceEqual(bytes.Take(length))) throw Error("native registration changed during capture");
                    if (bytes.Length > previous.Length) frozen[address] = bytes;
                } else frozen.Add(address, bytes);
            }
            internal void RequireFrozen()
            {
                foreach (var check in registrations) check();
                foreach (var pair in frozen) if (!pair.Value.SequenceEqual(Read(pair.Key, pair.Value.Length))) throw Error("native lifecycle/registration capture changed before completion");
            }
        }

        private static void Validate(Data data)
        {
            if (data.Owner.Length == 0 || data.Type.Length == 0 || data.Lifecycle == null || data.Lifecycle.Length != 5
                || data.Lifecycle.Any(value => value > 1) || data.Lifecycle[3] != 0 || data.Registry == null || data.Memberships == null
                || data.PendingStarts < 0 || data.OtherCallbacks < 0 || data.ActiveCoroutines < 0
                || data.PendingStarts + data.OtherCallbacks > MaximumCallbacks || data.ActiveCoroutines > MaximumOwners)
                throw Error("source Mono lifecycle data is invalid");
            foreach (var membership in data.Memberships) {
                var matches = data.Registry.Lists.Where(list => list.Kind == membership.Kind && list.ExecutionOrder == membership.ExecutionOrder && list.Lane == membership.Lane).ToArray();
                if (matches.Length != 1 || membership.Ordinal < 0 || membership.Ordinal >= matches[0].Owners.Length
                    || matches[0].Owners[membership.Ordinal] != data.Owner) throw Error("source Mono membership does not match its complete list witness");
            }
        }
        private static void ValidateRegistry(RegistryData registry)
        {
            if (registry.Lists == null || registry.Images == null || registry.Lists.Length > MaximumOwners
                || registry.Lists.Any(list => list == null || list.Kind < 0 || list.Kind > 4 || list.Lane < 0 || list.Lane > 1
                    || list.Kind > 2 && (list.Lane != 0 || list.ExecutionOrder != 0) || list.Owners == null || list.Owners.Length > MaximumOwners
                    || list.Owners.Any(string.IsNullOrEmpty) || list.Owners.Distinct(StringComparer.Ordinal).Count() != list.Owners.Length)
                || registry.Lists.Select(Key).Distinct(StringComparer.Ordinal).Count() != registry.Lists.Length
                || !registry.Lists.Select(Key).SequenceEqual(registry.Lists.OrderBy(list => list.Kind).ThenBy(list => list.ExecutionOrder).ThenBy(list => list.Lane).Select(Key))
                || registry.Images.Any(image => image == null || string.IsNullOrEmpty(image.Camera) || image.BeforeOpaque == null || image.AfterOpaque == null
                    || image.BeforeOpaque.Length + image.AfterOpaque.Length > MaximumOwners || image.TransformsToLdr < 0
                    || image.BeforeOpaque.Concat(image.AfterOpaque).Any(entry => entry == null || string.IsNullOrEmpty(entry.Owner)
                        || entry.Flags == null || entry.Flags.Length != 2 || entry.Flags.Any(value => value > 1) || entry.CallbackRva < 0)))
                throw Error("source complete registration directory is invalid");
        }
        private static bool SameOwnerBinding(string left, string right) => ResidentExecutionOwnership.SameExecutionKey(left, right);
        private static bool SameOwnerList(string[] left, string[] right)
        {
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
                if (!SameOwnerBinding(left[index], right[index])) return false;
            return true;
        }
        private static void RequireForeignLists(RegistryData source, RegistryData target, HashSet<string> owned)
        {
            foreach (var list in source.Lists) {
                var other = target.Lists.SingleOrDefault(item => Key(item) == Key(list));
                var foreign = list.Owners.Where(owner => !owned.Contains(owner)).ToArray();
                if (other == null) { if (foreign.Length != 0) throw Error("foreign source registration group is absent"); continue; }
                var otherForeign = other.Owners.Where(owner => !owned.Contains(owner)).ToArray();
                if (!SameOwnerList(foreign, otherForeign))
                    throw Error($"foreign native registration order differs at {Key(list)}: src=[{string.Join(", ", foreign)}] vs tgt=[{string.Join(", ", otherForeign)}]");
            }
            foreach (var list in target.Lists.Where(list => source.Lists.All(item => Key(item) != Key(list))))
                if (list.Owners.Length != 0 && (list.Owners.Any(owner => !owned.Contains(owner)) || list.Kind < 3))
                    throw Error("target has an extra execution-order/foreign registration group");
        }
        private static void RequireForeignImages(RegistryData source, RegistryData target, HashSet<string> owned)
        {
            foreach (var sourceCamera in source.Images) {
                var targetCamera = target.Images.SingleOrDefault(item => item.Camera == sourceCamera.Camera) ?? throw Error("source image-filter camera is absent");
                if (!ImageEqual(sourceCamera.BeforeOpaque.Where(item => !owned.Contains(item.Owner)), targetCamera.BeforeOpaque.Where(item => !owned.Contains(item.Owner)))
                    || !ImageEqual(sourceCamera.AfterOpaque.Where(item => !owned.Contains(item.Owner)), targetCamera.AfterOpaque.Where(item => !owned.Contains(item.Owner))))
                    throw Error("foreign native image-filter order or flags differ");
            }
        }
        private static void RequireSame(Data[] source, Data[] target, bool scheduler)
        {
            if (source.Length != target.Length) throw Error("independent Mono readback owner count differs");
            for (var index = 0; index < source.Length; index++) {
                var a = source[index]; var b = target[index];
                if (a.Owner != b.Owner || a.Type != b.Type || a.Enabled != b.Enabled || a.BehaviourAdded != b.BehaviourAdded
                    || a.ActiveInHierarchy != b.ActiveInHierarchy || !a.Lifecycle.SequenceEqual(b.Lifecycle) || a.ExecutionOrder != b.ExecutionOrder
                    || a.GuiDisplayMask != b.GuiDisplayMask || a.GuiStatePresent != b.GuiStatePresent || a.AudioFilterPresent != b.AudioFilterPresent
                    || scheduler && (a.PendingStarts != b.PendingStarts || a.OtherCallbacks != b.OtherCallbacks || a.ActiveCoroutines != b.ActiveCoroutines)
                    || !a.Memberships.Select(MemberKey).SequenceEqual(b.Memberships.Select(MemberKey))) throw Error("complete Mono lifecycle/registration readback differs: " + a.Owner);
            }
            if (source.Length == 0) return;
            var before = source[0].Registry; var after = target[0].Registry;
            var beforeLists = before.Lists.Where(list => list.Owners.Length != 0).ToArray();
            var afterLists = after.Lists.Where(list => list.Owners.Length != 0).ToArray();
            if (!beforeLists.Select(Key).SequenceEqual(afterLists.Select(Key))
                || beforeLists.Where((list, index) => !SameOwnerList(list.Owners, afterLists[index].Owners)).Any()) throw Error("complete native update/GUI/render list readback differs");
            if (before.Images.Length != after.Images.Length) throw Error("native image-filter camera directory differs");
            foreach (var a in before.Images) {
                var b = after.Images.SingleOrDefault(item => item.Camera == a.Camera);
                if (b == null || a.TransformsToLdr != b.TransformsToLdr || !ImageEqual(a.BeforeOpaque, b.BeforeOpaque) || !ImageEqual(a.AfterOpaque, b.AfterOpaque))
                    throw Error("native image-filter registration readback differs");
            }
        }
        private static bool ImageEqual(IEnumerable<ImageEntry> left, IEnumerable<ImageEntry> right)
        {
            var a = left.ToArray(); var b = right.ToArray();
            return a.Length == b.Length && !a.Where((item, index) => !SameOwnerBinding(item.Owner, b[index].Owner) || item.CallbackRva != b[index].CallbackRva
                || item.Event != b[index].Event || !item.Flags.SequenceEqual(b[index].Flags)).Any();
        }
        private static Data[] Clone(Data[] values)
        {
            var registries = new Dictionary<RegistryData, RegistryData>(References<RegistryData>.Instance);
            return values.Select(value => {
                if (!registries.TryGetValue(value.Registry, out var registry)) {
                    ImageEntry CopyImage(ImageEntry item) => new ImageEntry { Owner = item.Owner, CallbackRva = item.CallbackRva, Event = item.Event, Flags = (byte[])item.Flags.Clone() };
                    registry = new RegistryData { Lists = value.Registry.Lists.Select(list => new ListData { Kind = list.Kind, ExecutionOrder = list.ExecutionOrder, Lane = list.Lane, Owners = (string[])list.Owners.Clone() }).ToArray(),
                        Images = value.Registry.Images.Select(list => new ImageListData { Camera = list.Camera, TransformsToLdr = list.TransformsToLdr,
                            BeforeOpaque = list.BeforeOpaque.Select(CopyImage).ToArray(), AfterOpaque = list.AfterOpaque.Select(CopyImage).ToArray() }).ToArray() };
                    registries.Add(value.Registry, registry);
                }
                return new Data { Owner = value.Owner, Type = value.Type, Enabled = value.Enabled, BehaviourAdded = value.BehaviourAdded,
                    ActiveInHierarchy = value.ActiveInHierarchy, AudioFilterPresent = value.AudioFilterPresent, GuiStatePresent = value.GuiStatePresent,
                    Lifecycle = (byte[])value.Lifecycle.Clone(), ExecutionOrder = value.ExecutionOrder, GuiDisplayMask = value.GuiDisplayMask,
                    PendingStarts = value.PendingStarts, OtherCallbacks = value.OtherCallbacks, ActiveCoroutines = value.ActiveCoroutines,
                    Memberships = value.Memberships.Select(item => new Membership { Kind = item.Kind, ExecutionOrder = item.ExecutionOrder, Lane = item.Lane, Ordinal = item.Ordinal }).ToArray(), Registry = registry };
            }).ToArray();
        }
        private static string TypeIdentity(MonoBehaviour owner) => owner.GetType().AssemblyQualifiedName + ":" + owner.GetType().Module.ModuleVersionId.ToString("D");
        private static string Key(int kind, int order, int lane) => kind + "/" + order + "/" + lane;
        private static string Key(ListData value) => Key(value.Kind, value.ExecutionOrder, value.Lane);
        private static string MemberKey(Membership value) => Key(value.Kind, value.ExecutionOrder, value.Lane) + "/" + value.Ordinal;
        private static IntPtr Engine() => LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
        private static IntPtr Native(UnityEngine.Object owner)
        {
            if (owner == null) throw Error("native owner is absent");
            var pointer = (IntPtr)CachedPointer.GetValue(owner)!;
            return pointer != IntPtr.Zero ? pointer : throw Error("native owner pointer is absent");
        }
        private static T Entry<T>(IntPtr engine, int rva, string prefix) where T : Delegate
        {
            var bytes = Enumerable.Range(0, prefix.Length / 2).Select(index => Convert.ToByte(prefix.Substring(index * 2, 2), 16)).ToArray();
            var pointer = IntPtr.Add(engine, rva);
            if (!bytes.SequenceEqual(Read(pointer, bytes.Length))) throw Error("pinned native registration entry differs");
            return (T)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
        }
        private static byte[] Read(IntPtr pointer, int length)
        {
            if (length == 0) return Array.Empty<byte>();
            var bytes = new byte[length];
            if (pointer == IntPtr.Zero || length < 0 || !ReadProcessMemory(GetCurrentProcess(), pointer, bytes, new UIntPtr((uint)length), out var read)
                || read.ToUInt64() != (ulong)length) throw Error("native registration read failed");
            return bytes;
        }
        private static void Write(IntPtr pointer, int offset, byte[] bytes)
        {
            if (!WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, offset), bytes, new UIntPtr((uint)bytes.Length), out var written)
                || written.ToUInt64() != (ulong)bytes.Length || !bytes.SequenceEqual(Read(IntPtr.Add(pointer, offset), bytes.Length))) throw Error("native lifecycle scalar write failed readback");
        }
        private static IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
        private static int Count(byte[] bytes, int offset, int maximum)
        {
            var count = BitConverter.ToInt64(bytes, offset);
            return count >= 0 && count <= maximum ? (int)count : throw Error("native registration count exceeds its bound");
        }
        private static InvalidOperationException Error(string message) => new InvalidOperationException("Resident Mono lifecycle: " + message);
        private sealed class References<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly References<T> Instance = new References<T>();
            public bool Equals(T? left, T? right) => ReferenceEquals(left, right);
            public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
