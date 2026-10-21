using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace HollowKnightTAS.Runtime.FullRun
{
    // HKSHDT01: one process-owned 4096-byte diagnostic page, never D. Created
    // during session construction. Command threads only publish fixed numeric
    // stages/timestamps to mapped memory; no flush, file I/O or detached worker.
    // Each 32-byte record has matching ordinal fences. Count is published last.
    internal static class CanonicalSourceHeaderTiming
    {
        private static readonly object Sync = new object();
        private static MemoryMappedFile? mapping;
        private static MemoryMappedViewAccessor? page;
        private static string? active;
        private static int count;
        private static long origin;
        private static bool disabled;
        internal static void Initialize()
        {
            if (!CanonicalActualDExperiment.CaptureSource) return;
            lock (Sync) {
                if (mapping != null || disabled) return;
                try {
                    var directory = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                    if (string.IsNullOrEmpty(directory)) { disabled = true; return; }
                    var pid = Process.GetCurrentProcess().Id;
                    mapping = MemoryMappedFile.CreateFromFile(Path.Combine(directory, "actual-d-source-header-timing-" + pid + ".bin"),
                        FileMode.CreateNew, null, 4096, MemoryMappedFileAccess.ReadWrite);
                    page = mapping.CreateViewAccessor(0, 4096, MemoryMappedFileAccess.ReadWrite);
                    page.Write(0, 0x3130544448534b48L);
                    page.Write(8, 1); page.Write(12, 4096); page.Write(16, pid); page.Write(20, 24);
                    page.Write(24, Stopwatch.Frequency); page.Write(32, DateTime.UtcNow.Ticks);
                    origin = Stopwatch.GetTimestamp(); page.Write(48, origin);
                    Thread.MemoryBarrier(); page.Write(40, 0);
                } catch { disabled = true; }
            }
        }
        internal static void Begin(IReadOnlyDictionary<string, string> fields)
        {
            if (!fields.TryGetValue("phase", out var phase) || phase != "d-exp-source-info"
                || !fields.TryGetValue("requestId", out var id)) return;
            lock (Sync) { if (active != null) return; active = id; Write(1); }
        }
        internal static string? Request(IReadOnlyDictionary<string, string> fields)
        {
            lock (Sync) return fields.TryGetValue("requestId", out var id) && id == active ? id : null;
        }
        internal static void Mark(string? id, string stage)
        {
            lock (Sync) { if (id != null && id == active) Write(Stage(stage)); }
        }
        internal static void Lifecycle(int stage) { lock (Sync) Write(stage); }
        private static int Stage(string stage)
        {
            switch (stage) {
                case "ipc-command-enqueued": return 2;
                case "ipc-command-queue-rejected": return 3;
                case "command-dequeued-dispatch-enter": return 4;
                case "dispatcher-command-start": return 5;
                case "before-observation-queue": return 6;
                case "observation-command-start": return 7;
                case "before-seal-require-full-hash": return 8;
                case "after-seal-require-full-hash": return 9;
                case "before-reply-full-hash": return 10;
                case "after-reply-full-hash": return 11;
                case "before-reply-serialization": return 12;
                case "after-reply-serialization": return 13;
                case "reply-enqueued": return 14;
                case "before-pipe-write": return 15;
                case "after-pipe-write": return 16;
                case "pipe-write-failed": return 17;
                default: return 0;
            }
        }
        private static void Write(int stage)
        {
            if (page == null || disabled || stage <= 0 || count >= 24) return;
            try {
                var ordinal = count + 1; var offset = 128 + count * 32;
                page.Write(offset, ordinal); page.Write(offset + 4, stage);
                page.Write(offset + 8, Stopwatch.GetTimestamp() - origin); page.Write(offset + 16, DateTime.UtcNow.Ticks);
                page.Write(offset + 24, 0); Thread.MemoryBarrier(); page.Write(offset + 28, ordinal);
                Thread.MemoryBarrier(); page.Write(40, ordinal); count = ordinal;
            } catch { disabled = true; }
        }
        internal static void Close()
        {
            lock (Sync) {
                try { page?.Dispose(); mapping?.Dispose(); } catch { }
                page = null; mapping = null; disabled = true;
            }
        }
    }
}
