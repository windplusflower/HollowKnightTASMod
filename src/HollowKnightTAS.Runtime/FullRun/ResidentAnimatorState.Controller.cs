using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        // ControllerMemory and StateMachineMemory use *field-relative*
        // OffsetPtrs. GenerateGraph allocates their children independently.
        // Neither an offset nor an allocation address belongs in a snapshot.
        internal sealed class ControllerMemory
        {
            public byte[] Evaluation = Array.Empty<byte>(), LayerWeights = Array.Empty<byte>();
            public Machine[] Machines = Array.Empty<Machine>();
            public byte[][]? ParameterBanks;
        }
        internal sealed class Machine
        {
            // Native [16,119): state indices, previous times, durations, speed
            // modifiers, transition progress and all seven transition flags.
            public byte[] State = Array.Empty<byte>(), SynchronizedWeights = Array.Empty<byte>();
        }
        private static readonly int[] ParameterStrides = { 16, 16, 16, 4, 4, 1 };
        private const int MaximumEntries = 100000;
        private const int MaximumBytes = 32 * 1024 * 1024;

        private static IntPtr Relative(IntPtr owner, byte[] header, int field)
        {
            var delta = BitConverter.ToInt64(header, field);
            return delta == 0 ? IntPtr.Zero : new IntPtr(checked(owner.ToInt64() + field + delta));
        }
        private static int Count(uint value)
        {
            if (value > MaximumEntries) throw Error("native element count exceeded");
            return (int)value;
        }
        private sealed class Segment
        {
            internal readonly IntPtr Address;
            internal readonly byte[] Before;
            internal Segment(IntPtr address, byte[] before) { Address = address; Before = before; }
        }
        private sealed class MemoryReader
        {
            private int total;
            private readonly List<Segment> witness = new List<Segment>();
            private readonly List<Segment> writableAllocations = new List<Segment>();
            internal int Mark() => witness.Count;
            private static void RequireDisjoint(IEnumerable<Segment> ranges)
            {
                long end = 0;
                foreach (var part in ranges.OrderBy(segment => segment.Address.ToInt64())) {
                    long start = part.Address.ToInt64();
                    if (start < end) throw Error("native controller allocations overlap");
                    end = checked(start + part.Before.Length);
                }
            }
            internal void OwnSince(int mark)
            {
                for (int i = mark; i < witness.Count; i++) writableAllocations.Add(witness[i]);
            }
            internal byte[] ReadOwned(IntPtr pointer, int count)
            {
                var mark = Mark(); var bytes = Read(pointer, count); OwnSince(mark); return bytes;
            }
            internal byte[] Read(IntPtr address, int length)
            {
                total = checked(total + length);
                if (total > MaximumBytes) throw Error("controller storage exceeds package limit");
                var value = M.Read(address, length);
                if (length != 0) witness.Add(new Segment(address, value));
                return value;
            }
            internal byte[] Bank(IntPtr owner, byte[] header, int countField, int stride)
            {
                int count = Count(M.U32(header, countField));
                var address = Relative(owner, header, countField + 8);
                if ((address == IntPtr.Zero) != (count == 0)) throw Error("native controller bank allocation/count disagree");
                return Read(address, checked(count * stride));
            }
            internal void Verify()
            {
                RequireDisjoint(writableAllocations);
                foreach (var span in witness)
                    if (!span.Before.SequenceEqual(M.Read(span.Address, span.Before.Length))) throw Error("controller changed during observation");
            }
        }
        private static ControllerMemory ReadMemory(IntPtr pointer, MemoryReader reader)
        {
            var mark = reader.Mark();
            var header = reader.Read(pointer, 48);
            var count = Count(M.U32(header, 0));
            var table = Relative(pointer, header, 8);
            var offsets = reader.Bank(pointer, header, 0, 8);
            var machines = new Machine[count];
            var unique = new HashSet<IntPtr>();
            for (var index = 0; index < count; index++) {
                var state = Relative(table, offsets, index * 8);
                if (state == IntPtr.Zero || !unique.Add(state)) throw Error("native state machine owner is missing or shared");
                var bytes = reader.Read(state, 120);
                machines[index] = new Machine { State = M.Slice(bytes, 16, 103), SynchronizedWeights = reader.Bank(state, bytes, 0, 4) };
            }
            var value = new ControllerMemory { Evaluation = M.Slice(header, 40, 5), Machines = machines, LayerWeights = reader.Bank(pointer, header, 16, 4) };
            var parameters = Relative(pointer, header, 32);
            if (parameters != IntPtr.Zero) {
                var bankHeader = reader.Read(parameters, 96);
                value.ParameterBanks = ParameterStrides.Select((stride, bank) => reader.Bank(parameters, bankHeader, bank * 16, stride)).ToArray();
            }
            reader.OwnSince(mark); ValidateMemory(value);
            return value;
        }
        internal static ControllerMemory CaptureMemory(IntPtr pointer)
        {
            var reader = new MemoryReader();
            var result = ReadMemory(pointer, reader);
            reader.Verify();
            return result;
        }
        private static void ValidateMemory(ControllerMemory value)
        {
            if (value == null || value.Evaluation == null || value.Evaluation.Length != 5
                || value.Machines == null || value.Machines.Length > MaximumEntries) throw Error("controller memory shape differs");
            long total = value.Evaluation.Length + BankSize(value.LayerWeights, 4);
            foreach (var machine in value.Machines) {
                if (machine == null || machine.State == null || machine.State.Length != 103)
                    throw Error("state machine layout/flags differ");
                total += 103 + BankSize(machine.SynchronizedWeights, 4);
            }
            if (value.ParameterBanks != null) {
                if (value.ParameterBanks.Length != 6) throw Error("parameter bank directory differs");
                for (var index = 0; index < 6; index++) total += BankSize(value.ParameterBanks[index], ParameterStrides[index]);

            }
            if (total > MaximumBytes) throw Error("controller state exceeds package limit");
        }
        private static int BankSize(byte[] bank, int stride)
        {
            if (bank == null || bank.Length % stride != 0 || bank.Length / stride > MaximumEntries) throw Error("controller bank extent differs");
            return bank.Length;
        }
        private sealed class PendingWrite
        {
            private readonly IntPtr address;
            private readonly byte[] before, value;
            internal PendingWrite(IntPtr address, byte[] before, byte[] value)
            {
                if (value.Length != before.Length) throw Error("target native allocation extent differs");
                this.address = address; this.before = before; this.value = (byte[])value.Clone();
            }
            internal void Apply()
            {
                if (!M.Read(address, before.Length).SequenceEqual(before)) throw Error("preflight native witness changed");
                if (!value.SequenceEqual(before)) M.Write(address, value);
            }
        }
        private static void PrepareMemory(ControllerMemory source, IntPtr target, List<PendingWrite> writes)
        {
            ValidateMemory(source);
            Reject(MemoryScalarGaps(source));
            var before = CaptureMemory(target); Reject(MemoryScalarGaps(before));
            if (source.Machines.Length != before.Machines.Length || (source.ParameterBanks == null) != (before.ParameterBanks == null))
                throw Error("target controller machine/parameter directory differs");
            var header = M.Read(target, 48);
            var table = Relative(target, header, 8);
            var offsets = M.Read(table, checked(source.Machines.Length * 8));
            writes.Add(new PendingWrite(IntPtr.Add(target, 40), before.Evaluation, source.Evaluation));
            writes.Add(new PendingWrite(Relative(target, header, 24), before.LayerWeights, source.LayerWeights));
            for (var index = 0; index < source.Machines.Length; index++) {
                var state = Relative(table, offsets, index * 8);
                var stateHeader = M.Read(state, 16);
                writes.Add(new PendingWrite(IntPtr.Add(state, 16), before.Machines[index].State, source.Machines[index].State));
                writes.Add(new PendingWrite(Relative(state, stateHeader, 8), before.Machines[index].SynchronizedWeights, source.Machines[index].SynchronizedWeights));
            }
            if (source.ParameterBanks != null) {
                var parameters = Relative(target, header, 32);
                var banks = M.Read(parameters, 96);
                for (var index = 0; index < 6; index++)
                    writes.Add(new PendingWrite(Relative(parameters, banks, index * 16 + 8), before.ParameterBanks![index], source.ParameterBanks[index]));
            }
        }
    }
}
