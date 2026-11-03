using System;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class BlendLayer
        {
            public int Constant, Values;
            public byte[] Duration = Array.Empty<byte>(), Nodes = Array.Empty<byte>();
            public byte[][] Scratch = Array.Empty<byte[]>();
        }
        private static readonly int[] BlendStrides = {4, 4, 4, 8};
        // The scratch arrays have no runtime capacity field. Unity's allocator
        // constructs them using MAX(nodeCount) over the machine's referenced
        // state/layer blend trees. A clip count is not a valid substitute.
        private static int MaxTreeNodes(MemoryReader reader, IntPtr machine, int layers)
        {
            var body = reader.Read(machine, 56);
            var states = reader.Bank(machine, body, 0, 8); var table = Relative(machine, body, 8);
            int max = 0;
            for (var slot = 0; slot < states.Length; slot += 8) {
                var state = Relative(table, states, slot);
                var header = reader.Read(state, 96);
                if (M.U32(header, 16) != layers) throw Error("state constant synchronized layer extent differs");
                var indices = reader.Bank(state, header, 16, 4);
                var trees = reader.Bank(state, header, 32, 8); var treeTable = Relative(state, header, 40);
                for (var index = 0; index < layers; index++) {
                    var treeIndex = M.I32(indices, index * 4);
                    if (treeIndex == -1) continue;
                    if (treeIndex < 0 || treeIndex >= trees.Length / 8) throw Error("state references an out-of-range blend tree");
                    var tree = Relative(treeTable, trees, treeIndex * 8);
                    if (tree != IntPtr.Zero) max = Math.Max(max, Count(M.U32(reader.Read(tree, 4), 0)));
                }
            }
            return max;
        }
        private static void ValidateBlend(MachineWork machine, int layers)
        {
            if (machine.MaxTreeNodes < 0 || machine.MaxTreeNodes > MaximumEntries || machine.HasBlendWorkspace != (machine.Blends != null))
                throw Error("blend workspace allocation/extent differs");
            if (machine.Blends == null) return;
            if (machine.Blends.Length != layers) throw Error("blend synchronized layer count differs");
            foreach (var layer in machine.Blends) {
                if (layer == null || layer.Constant < UnknownEdge || layer.Constant > 0 || layer.Values < UnknownEdge || layer.Values > 7 || layer.Values == 0
                    || layer.Duration == null || layer.Duration.Length != 4 || layer.Nodes == null || layer.Nodes.Length != machine.MaxBlendedClips * 21
                    || layer.Scratch == null || layer.Scratch.Length != 4 || layer.Scratch.Where((bank, i) => bank == null || bank.Length != machine.MaxTreeNodes * BlendStrides[i]).Any())
                    throw Error("blend input/output or scratch extent differs");

            }
        }
        private static int ValueMask(IntPtr value, IntPtr[] dependencies)
        {
            if (value == IntPtr.Zero) return -1;
            int mask = 0;
            for (int i = 0; i < dependencies.Length; i++) if (dependencies[i] == value) mask |= 1 << i;
            return mask == 0 ? UnknownEdge : mask;
        }
        private static BlendLayer[] ReadBlends(MemoryReader reader, IntPtr state, int layers, int maxTree, int maxClips,
            IntPtr valueConstant, IntPtr[] valueDeps, BlendLayer[]? desired,
            Func<IntPtr, byte[], byte[]?, byte[]> bits, Func<IntPtr, IntPtr, IntPtr, int?, int> link)
        {
            var header = reader.ReadOwned(state, 32);
            if (M.U32(header, 24) != layers || desired != null && desired.Length != layers) throw Error("state workspace layer extent differs");
            var inputs = DirectArray(reader, M.Ptr(header, 0), layers, 8);
            var outputs = DirectArray(reader, M.Ptr(header, 8), layers, 8);
            var scratch = DirectArray(reader, M.Ptr(header, 16), layers, 8);
            var result = new BlendLayer[layers];
            for (var i = 0; i < layers; i++) {
                var input = M.Ptr(inputs, i * 8); var inputBytes = reader.ReadOwned(input, 16);
                var output = M.Ptr(outputs, i * 8); var outputBytes = reader.ReadOwned(output, 16);
                var block = reader.ReadOwned(M.Ptr(scratch, i * 8), 32);
                var expected = desired?[i];
                if (M.U32(outputBytes, 8) != maxClips) throw Error("blend output clip count disagrees with machine allocation");
                var observed = ValueMask(M.Ptr(inputBytes, 8), valueDeps);
                if (expected != null) {
                    if (observed == UnknownEdge || expected.Values == UnknownEdge) throw Error("blend input values are outside the controller binding set");
                    var target = IntPtr.Zero;
                    if (expected.Values != -1)
                        for (int j = 0; j < valueDeps.Length; j++) if ((expected.Values & 1 << j) != 0) { target = valueDeps[j]; break; }
                    if (ValueMask(target, valueDeps) != expected.Values) throw Error("blend value bank target aliases differ");
                    bits(IntPtr.Add(input, 8), M.Slice(inputBytes, 8, 8), BitConverter.GetBytes(target.ToInt64())); observed = expected.Values;
                }
                var row = new BlendLayer { Constant = link(input, M.Ptr(inputBytes, 0), valueConstant, expected?.Constant), Values = observed,
                    Duration = bits(IntPtr.Add(output, 12), M.Slice(outputBytes, 12, 4), expected?.Duration), Scratch = new byte[4][],
                    Nodes = new byte[checked(maxClips * 21)] };
                var nodeArray = M.Ptr(outputBytes, 0);
                var nodes = DirectArray(reader, nodeArray, maxClips, 24);
                for (int n = 0; n < maxClips; n++) {
                    var first = bits(IntPtr.Add(nodeArray, n * 24), M.Slice(nodes, n * 24, 13), expected == null ? null : M.Slice(expected.Nodes, n * 21, 13));
                    var last = bits(IntPtr.Add(nodeArray, n * 24 + 16), M.Slice(nodes, n * 24 + 16, 8), expected == null ? null : M.Slice(expected.Nodes, n * 21 + 13, 8));
                    Buffer.BlockCopy(first, 0, row.Nodes, n * 21, 13); Buffer.BlockCopy(last, 0, row.Nodes, n * 21 + 13, 8);
                }
                for (int bank = 0; bank < 4; bank++) {
                    var pointer = M.Ptr(block, bank * 8);
                    row.Scratch[bank] = bits(pointer, DirectArray(reader, pointer, maxTree, BlendStrides[bank]), expected?.Scratch[bank]);
                }
                result[i] = row;
            }
            return result;
        }
    }
}
