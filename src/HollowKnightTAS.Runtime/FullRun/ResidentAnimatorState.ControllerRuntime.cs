using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class ControllerRuntime
        {
            public bool HasConstant;
            public byte[] AnimationFlags = Array.Empty<byte>();
            public int Layers, PlayableInstances;
            public int[] SynchronizedLayers = Array.Empty<int>();
            public int[] StateCounts = Array.Empty<int>();
            // Each ValueConstant is (ID, type, bank index), without pointers.
            public byte[] Parameters = Array.Empty<byte>();
            public ControllerInput? Input;
            public ControllerWorkspace? Workspace;
        }
        internal sealed class ControllerInput
        {
            public byte[] Speed = Array.Empty<byte>(), GotoStates = Array.Empty<byte>();
            public int BehaviourPlayer, LayerMixer;
        }
        internal sealed class ControllerWorkspace
        {
            public byte[] TriggerReset = Array.Empty<byte>(), TimingWeights = Array.Empty<byte>();
            public MachineWork[] Machines = Array.Empty<MachineWork>();
        }
        internal sealed class MachineWork
        {
            public int MaxBlendedClips, ValueConstants, TriggerSlot, PlayableInstance;
            public int MaxTreeNodes;
            public BlendLayer[]? Blends;
            public bool HasBlendWorkspace;
            public byte[] Messages = Array.Empty<byte>(), OutputFlags = Array.Empty<byte>();
        }
        // -1 null, -2 an observed pointer outside the implemented binding set.
        // Unknown addresses are never dereferenced or copied into a packet.
        private const int UnknownEdge = -2;
        private static int Edge(IntPtr actual, IntPtr expected)
            => actual == IntPtr.Zero ? -1 : actual == expected ? 0 : UnknownEdge;
        private static int TableEdge(IntPtr actual, IntPtr first, int count)
        {
            if (actual == IntPtr.Zero) return -1;
            if (first == IntPtr.Zero) return UnknownEdge;
            var delta = checked(actual.ToInt64() - first.ToInt64());
            return delta >= 0 && delta % 16 == 0 && delta / 16 < count ? (int)(delta / 16) : UnknownEdge;
        }
        private static int TriggerCount(byte[] parameters)
        {
            int count = 0;
            // CreateControllerWorkspace tests exactly types 4 and 9 in the
            // 12-byte ValueConstant array when it allocates the reset bank.
            for (var offset = 4; offset < parameters.Length; offset += 12)
                if (M.U32(parameters, offset) == 4 || M.U32(parameters, offset) == 9) count++;
            return count;
        }
        private static void ValidateRuntime(ControllerRuntime value)
        {
            if (value == null || value.Layers < 0 || value.Layers > MaximumEntries || value.PlayableInstances < 0 || value.PlayableInstances > MaximumEntries
                || value.SynchronizedLayers == null || value.SynchronizedLayers.Length > MaximumEntries || value.SynchronizedLayers.Any(count => count < 0 || count > MaximumEntries)
                || value.StateCounts == null || value.StateCounts.Length != value.SynchronizedLayers.Length || value.StateCounts.Any(count => count < 0 || count > MaximumEntries))
                throw Error("controller runtime counts differ");
            ValidateAnimationFlags(value.AnimationFlags);
            BankSize(value.Parameters, 12);
            if (!value.HasConstant && (value.Workspace != null || value.Input != null || value.Layers != 0 || value.SynchronizedLayers.Length != 0 || value.Parameters.Length != 0))
                throw Error("controller runtime has no backing constant layout");
            bool ValidEdge(int edge, int slots) => edge >= UnknownEdge && edge < slots;
            var input = value.Input;
            if (input != null) {
                if (input.Speed == null || input.Speed.Length != 4 || input.GotoStates == null || input.GotoStates.Length != value.Layers * 21
                    || !ValidEdge(input.BehaviourPlayer, 1) || !ValidEdge(input.LayerMixer, 1)) throw Error("controller input layout differs");

            }
            var work = value.Workspace;
            if (work == null) return;
            var maxSync = value.SynchronizedLayers.Length == 0 ? 0 : value.SynchronizedLayers.Max();
            if (work.TriggerReset == null || work.TriggerReset.Length != TriggerCount(value.Parameters)
                || work.TimingWeights == null || work.TimingWeights.Length != maxSync * 4 || work.Machines == null || work.Machines.Length != value.SynchronizedLayers.Length)
                throw Error("controller workspace bank/constant layout differs");
            for (var index = 0; index < work.Machines.Length; index++) {
                var row = work.Machines[index];
                if (row == null || row.MaxBlendedClips < 0 || row.MaxBlendedClips > MaximumEntries || row.Messages == null || row.Messages.Length != 12
                    || row.OutputFlags == null || row.OutputFlags.Length != 4
                    || !ValidEdge(row.ValueConstants, 1) || !ValidEdge(row.TriggerSlot, 1) || !ValidEdge(row.PlayableInstance, value.PlayableInstances))
                    throw Error("controller machine workspace layout differs");
                ValidateBlend(row, value.SynchronizedLayers[index]);
            }
        }
        private static byte[] DirectArray(MemoryReader reader, IntPtr pointer, int count, int stride)
        {
            if ((pointer == IntPtr.Zero) != (count == 0)) throw Error("native workspace count/allocations differ");
            return reader.ReadOwned(pointer, checked(count * stride));
        }
        // Source, if supplied, contributes only scalar contents and local
        // binding choices. All pointers, indices and extents below are read
        // from the validated target graph.
        private static ControllerRuntime ReadRuntime(IntPtr owner, byte[] player, MemoryReader reader, ControllerRuntime? source = null, List<PendingWrite>? writes = null)
        {
            var constant = M.Ptr(player, 296); var input = M.Ptr(player, 304); var workspace = M.Ptr(player, 320);
            var firstInstance = M.Ptr(player, 440);
            var value = new ControllerRuntime { HasConstant = constant != IntPtr.Zero, PlayableInstances = Count(M.U32(player, 448)), AnimationFlags = M.Slice(player,256,5) };
            if (source != null) {
                PrepareAnimationFlags(source.AnimationFlags,value.AnimationFlags,IntPtr.Add(owner,256),writes!);
                value.AnimationFlags = (byte[])source.AnimationFlags.Clone();
            }
            if (firstInstance == IntPtr.Zero && value.PlayableInstances != 0) throw Error("playable instance allocation missing");
            if (constant == IntPtr.Zero) {
                if (input != IntPtr.Zero || workspace != IntPtr.Zero) throw Error("native controller runtime lost its constant owner");
                if (source != null && Json(value) != Json(source)) throw Error("target constant ownership differs");
                return value;
            }
            var header = reader.Read(constant, 48); value.Layers = Count(M.U32(header, 0)); var machines = Count(M.U32(header, 16));
            var machineOffsets = reader.Bank(constant, header, 16, 8); var machineTable = Relative(constant, header, 24);
            value.SynchronizedLayers = new int[machines];
            value.StateCounts = new int[machines];
            for (int i = 0; i < machines; i++) {
                var machine = Relative(machineTable, machineOffsets, i * 8);
                if (machine == IntPtr.Zero) throw Error("controller constant machine absent");
                var definition = reader.Read(machine,56);
                value.StateCounts[i] = Count(M.U32(definition,0)); value.SynchronizedLayers[i] = Count(M.U32(definition,52));
            }
            var values = Relative(constant, header, 32);
            if (values != IntPtr.Zero) value.Parameters = reader.Bank(values, reader.Read(values, 16), 0, 12);
            var memory = M.Ptr(player, 312);
            var valueDeps = new[] { memory == IntPtr.Zero ? IntPtr.Zero : Relative(memory, reader.Read(memory, 48), 32), M.Ptr(player, 400), Relative(constant, header, 40) };
            if (source != null && (!source.HasConstant || value.Layers != source.Layers || value.PlayableInstances != source.PlayableInstances
                || !value.SynchronizedLayers.SequenceEqual(source.SynchronizedLayers) || !value.StateCounts.SequenceEqual(source.StateCounts) || !value.Parameters.SequenceEqual(source.Parameters)
                || (input != IntPtr.Zero) != (source.Input != null) || (workspace != IntPtr.Zero) != (source.Workspace != null)))
                throw Error("target controller input/workspace schema differs");
            byte[] Bits(IntPtr address, byte[] before, byte[]? desired)
            {
                if (desired == null) return before;
                writes!.Add(new PendingWrite(address, before, desired)); return desired;
            }
            int Link(IntPtr location, IntPtr actual, IntPtr dependency, int? desired)
            {
                var observed = Edge(actual, dependency);
                if (!desired.HasValue) return observed;
                if (observed == UnknownEdge || desired == UnknownEdge || desired == 0 && dependency == IntPtr.Zero) throw Error("native workspace dependency has no target binding");
                Bits(location, BitConverter.GetBytes(actual.ToInt64()), BitConverter.GetBytes(desired == -1 ? 0L : dependency.ToInt64()));
                return desired.Value;
            }
            if (input != IntPtr.Zero) {
                var bytes = reader.ReadOwned(input, 32); var desired = source?.Input;
                var result = new ControllerInput { Speed = Bits(input, M.Slice(bytes, 0, 4), desired?.Speed),
                    BehaviourPlayer = Link(IntPtr.Add(input, 8), M.Ptr(bytes, 8), IntPtr.Add(owner, 368), desired?.BehaviourPlayer),
                    LayerMixer = Link(IntPtr.Add(input, 16), M.Ptr(bytes, 16), M.Ptr(player, 408), desired?.LayerMixer),
                    GotoStates = new byte[value.Layers * 21] };
                var states = M.Ptr(bytes, 24); var block = DirectArray(reader, states, value.Layers, 24);
                for (int i = 0; i < value.Layers; i++) {
                    var loaded = Bits(IntPtr.Add(states, i * 24), M.Slice(block, i * 24, 21), desired == null ? null : M.Slice(desired.GotoStates, i * 21, 21));
                    Buffer.BlockCopy(loaded, 0, result.GotoStates, i * 21, 21);
                }
                value.Input = result;
            }
            if (workspace != IntPtr.Zero) {
                var bytes = reader.ReadOwned(workspace, 40);
                if (M.U32(bytes, 24) != machines) throw Error("workspace disagrees with controller machine count");
                var triggers = M.Ptr(bytes, 16); var weights = M.Ptr(bytes, 32);
                var work = new ControllerWorkspace { Machines = new MachineWork[machines],
                    TriggerReset = Bits(triggers, DirectArray(reader, triggers, TriggerCount(value.Parameters), 1), source?.Workspace?.TriggerReset),
                    TimingWeights = Bits(weights, DirectArray(reader, weights, machines == 0 ? 0 : value.SynchronizedLayers.Max(), 4), source?.Workspace?.TimingWeights) };
                var workSlots = DirectArray(reader, M.Ptr(bytes, 0), machines, 8);
                var outputSlots = DirectArray(reader, M.Ptr(bytes, 8), machines, 8);
                for (int i = 0; i < machines; i++) {
                    var machine = M.Ptr(workSlots, i * 8); var machineBytes = reader.ReadOwned(machine, 32);
                    var output = M.Ptr(outputSlots, i * 8); var outputBytes = reader.ReadOwned(output, 32);
                    var desired = source?.Workspace?.Machines[i]; var edge = TableEdge(M.Ptr(outputBytes, 16), firstInstance, value.PlayableInstances);
                    if (desired != null) {
                        if (edge == UnknownEdge || desired.PlayableInstance == UnknownEdge) throw Error("output instance binding unresolved");
                        var target = desired.PlayableInstance == -1 ? IntPtr.Zero : IntPtr.Add(firstInstance, checked(desired.PlayableInstance * 16));
                        Bits(IntPtr.Add(output, 16), M.Slice(outputBytes, 16, 8), BitConverter.GetBytes(target.ToInt64())); edge = desired.PlayableInstance;
                    }
                    var entry = new MachineWork { MaxBlendedClips = Count(M.U32(machineBytes, 24)), HasBlendWorkspace = M.Ptr(machineBytes, 0) != IntPtr.Zero,
                        ValueConstants = Link(IntPtr.Add(machine, 8), M.Ptr(machineBytes, 8), values, desired?.ValueConstants),
                        TriggerSlot = Link(IntPtr.Add(machine, 16), M.Ptr(machineBytes, 16), IntPtr.Add(workspace, 16), desired?.TriggerSlot),
                        PlayableInstance = edge, Messages = Bits(output, M.Slice(outputBytes, 0, 12), desired?.Messages),
                        OutputFlags = Bits(IntPtr.Add(output, 24), M.Slice(outputBytes, 24, 4), desired?.OutputFlags) };
                    if (entry.HasBlendWorkspace) {
                        entry.MaxTreeNodes = MaxTreeNodes(reader, Relative(machineTable, machineOffsets, i * 8), value.SynchronizedLayers[i]);
                        if (desired != null && (desired.MaxTreeNodes != entry.MaxTreeNodes || desired.MaxBlendedClips != entry.MaxBlendedClips || desired.Blends == null))
                            throw Error("target nested blend workspace topology differs");
                        entry.Blends = ReadBlends(reader, M.Ptr(machineBytes, 0), value.SynchronizedLayers[i], entry.MaxTreeNodes, entry.MaxBlendedClips,
                            values, valueDeps, desired?.Blends, Bits, Link);
                    }
                    work.Machines[i] = entry;
                }
                value.Workspace = work;
            }
            ValidateRuntime(value);
            if (source != null && Json(value) != Json(source)) throw Error("controller runtime target machine layout differs");
            return value;
        }
        private static void PrepareRuntime(ControllerRuntime source, IntPtr player, List<PendingWrite> writes)
        {
            ValidateRuntime(source); Reject(RuntimeScalarGaps(source)); Reject(RuntimeScalarGaps(CaptureRuntime(player)));
            var reader = new MemoryReader(); var bytes = reader.Read(player, 456);
            ReadRuntime(player, bytes, reader, source, writes); reader.Verify();
        }
        internal static ControllerRuntime CaptureRuntime(IntPtr player)
        {
            var reader = new MemoryReader(); var bytes = reader.Read(player, 456);
            var saved = ReadRuntime(player, bytes, reader); reader.Verify(); return saved;
        }
        // Exact CreateValueArray jump table (RVA 0x135b04). GetInteger
        // independently verifies tag 3 and indexes the bank at ValueArray+72.
        private static int ValueBank(uint type)
        {
            switch (type) {
                case 6: return 0; case 7: return 1; case 8: return 2; case 1: return 3;
                case 3: return 4; case 4: case 9: return 5; default: return -1;
            }
        }
        internal static void ValidateControllerDomains(ControllerRuntime schema, ControllerMemory? memory)
        {
            if (memory == null) return;
            ValidateMemory(memory); ValidateRuntime(schema);
            if (!schema.HasConstant || memory.Machines.Length != schema.StateCounts.Length || memory.LayerWeights.Length != schema.Layers * 4
                || memory.Machines.Where((state,i) => state.SynchronizedWeights.Length != schema.SynchronizedLayers[i] * 4).Any())
                throw Error("controller memory and constant allocation directories differ");
            var counts = new int[6];
            for (var i = 0; i < schema.Parameters.Length; i += 12) {
                var bank = ValueBank(M.U32(schema.Parameters,i+4));
                if (bank >= 0) counts[bank]++;
            }
            if (memory.ParameterBanks == null) {
                if (counts.Any(n => n != 0)) throw Error("typed parameter schema lacks live value storage");
                return;
            }
            for (int i = 0; i < 6; i++)
                if (memory.ParameterBanks[i].Length != counts[i] * ParameterStrides[i]) throw Error("typed controller parameter bank extent differs");
            for (var i = 0; i < schema.Parameters.Length; i += 12) {
                var bank = ValueBank(M.U32(schema.Parameters,i+4));
                if (bank >= 0 && M.U32(schema.Parameters,i+8) >= counts[bank]) throw Error("controller parameter slot lies outside its declared value bank");
            }
        }
        private static bool KnownStateIndices(Playable player)
        {
            if (player.Memory == null) return true;
            for (int machine = 0; machine < player.Memory.Machines.Length; machine++) {
                var state = player.Memory.Machines[machine].State;
                for (int offset = 0; offset < 16; offset += 4) {
                    var index = M.U32(state,offset);
                    if (index != uint.MaxValue && index >= player.Runtime.StateCounts[machine]) return false;
                }
            }
            return true;
        }
    }
}
