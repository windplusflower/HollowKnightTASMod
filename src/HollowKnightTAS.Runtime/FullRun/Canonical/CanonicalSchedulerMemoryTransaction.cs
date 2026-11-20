using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only epoch/deadline scalars may differ. Links, callbacks and native
    // ownership are checked over the complete observed blocks before writes.
    internal sealed class CanonicalSchedulerMemoryTransaction
    {
        private readonly byte[][] before, after;
        private bool consumed;
        private CanonicalSchedulerMemoryTransaction(byte[][] before, byte[][] after)
        { this.before = before; this.after = after; }

        internal static CanonicalSchedulerMemoryTransaction Prepare(CanonicalSchedulerRestorePlan plan, byte[] manager, byte[][] nodes)
        {
            if (manager.Length != 80 || nodes.Length > 4096 || plan.Changes.Length != nodes.Length
                || nodes.Any(node => node.Length != 96)) throw new InvalidDataException("Scheduler native block sizes differ.");
            var before = new[] { manager }.Concat(nodes).Select(block => (byte[])block.Clone()).ToArray();
            var after = before.Select(block => (byte[])block.Clone()).ToArray();
            Buffer.BlockCopy(BitConverter.GetBytes(plan.Timestamp), 0, after[0], 72, 4);
            for (var index = 0; index < plan.Changes.Length; index++) {
                var change = plan.Changes[index];
                if (change.TargetOrder != index) throw new InvalidDataException("Scheduler native write order differs.");
                Buffer.BlockCopy(BitConverter.GetBytes(change.WakeTimeBits), 0, after[index + 1], 32, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(change.Frame), 0, after[index + 1], 40, 8);
                Buffer.BlockCopy(BitConverter.GetBytes(change.Timestamp), 0, after[index + 1], 88, 4);
            }
            return new CanonicalSchedulerMemoryTransaction(before, after);
        }

        internal void ValidateBefore(Func<int, int, byte[]> read, Action guard)
        {
            guard();
            for (var index = 0; index < before.Length; index++)
                if (!before[index].SequenceEqual(read(index, before[index].Length)))
                    throw new InvalidOperationException("Scheduler native preparation changed at block " + index + ".");
            guard();
        }
        internal void Apply(Func<int, int, byte[]> read, Action<int, int, byte[]> write, Action guard)
        {
            if (consumed) throw new InvalidOperationException("Scheduler native preparation was already consumed.");
            ValidateBefore(read, guard);
            // A write/readback failure cannot permit a second application or
            // continuation with a partially modified queue.
            consumed = true;
            for (var index = 1; index < after.Length; index++) {
                write(index, 32, Slice(after[index], 32, 16));
                write(index, 88, Slice(after[index], 88, 4));
            }
            write(0, 72, Slice(after[0], 72, 4));
            guard();
            for (var index = 0; index < after.Length; index++)
                if (!after[index].SequenceEqual(read(index, after[index].Length)))
                    throw new InvalidOperationException("Scheduler native application readback differs at block " + index + ".");
            guard();
        }
        private static byte[] Slice(byte[] bytes, int offset, int length)
        { var result = new byte[length]; Buffer.BlockCopy(bytes, offset, result, 0, length); return result; }
    }
}
