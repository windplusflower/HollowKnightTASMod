using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Companion.Services
{
    // IList avoids WPF's enumerable snapshot. Binary search stays O(log runs),
    // regardless of the number of expanded frames. Rows have stable value identity.
    public sealed class VirtualInputRows : IList, IReadOnlyList<InputGridRow>
    {
        private readonly long[] ends;
        private readonly NativeFrameRun[]? native;
        private readonly FrameRunCommand[]? legacy;
        private readonly Dictionary<int, InputGridRow> cache = new();
        private readonly Queue<int> order = new();
        // WPF may still display a row after eviction; keep updating it until it is collected,
        // otherwise its stale arrow stays next to the new current row.
        private readonly List<WeakReference<InputGridRow>> evicted = new();
        private long current;
        private long previewStart, previewEnd;
        private string? previewAction;
        private bool previewHeld;
        public VirtualInputRows() { ends = Array.Empty<long>(); }
        public VirtualInputRows(MovieV2Document movie, long current)
        { native = movie.Runs.ToArray(); ends = Ends(native.Select(r => r.RepeatCount)); this.current = current; }
        public VirtualInputRows(MovieDocument movie, long current)
        { legacy = movie.Commands.OfType<FrameRunCommand>().ToArray(); ends = Ends(legacy.Select(r => r.FrameCount)); this.current = current; }
        private static long[] Ends(IEnumerable<long> lengths)
        { long sum = 0; return lengths.Select(n => sum = checked(sum + n)).ToArray(); }
        public int Count => ends.Length == 0 ? 0 : checked((int)ends[^1]);
        public int CachedCount => cache.Count;
        public InputGridRow this[int index]
        {
            get
            {
                if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
                if (cache.TryGetValue(index, out var row)) return row;
                var run = Array.BinarySearch(ends, (long)index + 1);
                if (run < 0) run = ~run;
                row = native != null ? new InputGridRow(new V2InputGridRow(index, native[run].Samples, current, native[run].FramesPerSecond, native[run].RngSeed))
                    : new InputGridRow(index, legacy![run], current);
                if (cache.Count >= 2048 && cache.Remove(order.Dequeue(), out var old))
                {
                    if (evicted.Count >= 4096) evicted.RemoveAll(r => !r.TryGetTarget(out _));
                    evicted.Add(new WeakReference<InputGridRow>(old));
                }
                cache.Add(index, row); order.Enqueue(index);
                ApplyPreview(row);
                return row;
            }
        }
        public void UpdateCurrent(long frame)
        { current = frame; foreach (var row in Live()) row.UpdateCurrent(frame); }
        public void Preview(long start, long end, string? action, bool held)
        {
            previewStart = Math.Min(start, end); previewEnd = Math.Max(start, end);
            previewAction = action; previewHeld = held;
            foreach (var row in Live()) ApplyPreview(row);
        }
        private List<InputGridRow> Live()
        {
            var rows = new List<InputGridRow>(cache.Values);
            evicted.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (var reference in evicted)
                if (reference.TryGetTarget(out var row)) rows.Add(row);
            return rows;
        }
        private void ApplyPreview(InputGridRow row) => row.PreviewAction(
            row.Tick >= previewStart && row.Tick <= previewEnd ? previewAction : null, previewHeld);
        public int IndexOf(object? value) => value is InputGridRow row && row.Tick >= 0 && row.Tick < Count ? (int)row.Tick : -1;
        public bool Contains(object? value) => IndexOf(value) >= 0;
        object? IList.this[int index] { get => this[index]; set => throw new NotSupportedException(); }
        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;
        public void CopyTo(Array array, int index) { for (int i = 0; i < Count; i++) array.SetValue(this[i], index + i); }
        public IEnumerator<InputGridRow> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public int Add(object? value) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
        public void Insert(int index, object? value) => throw new NotSupportedException();
        public void Remove(object? value) => throw new NotSupportedException();
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
