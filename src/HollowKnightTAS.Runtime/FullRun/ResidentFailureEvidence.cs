using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentFailureEvidence
    {
        [ThreadStatic] private static List<string>? stamps;
        [ThreadStatic] private static ulong start;
        [ThreadStatic] private static FileStream? journal;
        internal static bool Collecting => stamps != null;
        internal static void BeginTiming()
        {
            EndTiming();
            var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
            if (string.IsNullOrEmpty(output)) return;
            stamps = new List<string>(24);
            start = ResidentNativeMemory.WallMilliseconds();
            try {
                if (!Path.IsPathRooted(output) || !Directory.Exists(output)) return;
                using var process = Process.GetCurrentProcess();
                // Keep only this process's latest transaction. Each short stage
                // record reaches the OS before a native watchdog can retire us.
                var path = Path.Combine(output,"resident-current-stages-" + process.Id + ".txt");
                journal = new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read,1);
                Mark("begin");
            } catch (Exception) { /* diagnostics must not alter admission */ }
        }
        internal static void EndTiming()
        {
            try { journal?.Dispose(); } catch (Exception) { }
            journal = null; stamps = null;
        }
        internal static void Mark(string stage)
        {
            if (stamps == null || stamps.Count >= 256) return;
            var line = (ResidentNativeMemory.WallMilliseconds() - start) + "ms " + stage;
            stamps.Add(line);
            if (journal == null) return;
            try {
                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                journal.Write(bytes,0,bytes.Length);
            } catch (Exception) { /* best effort */ }
        }
        internal static T Measure<T>(string stage,Func<T> action) { Mark(stage); return action(); }
        // Native retirement may stop the process before the Mod logger flushes.
        // Persist the first managed exception only on the failed path, before
        // closing the epoch. Never turn an evidence I/O error into the cause.
        internal static void Record(Exception failure)
        {
            try {
                var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output) || !Directory.Exists(output)) return;
                using var process = Process.GetCurrentProcess();
                var path = Path.Combine(output, "resident-first-fault-" + process.Id + ".txt");
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                Mark("fault"); var message = failure.ToString() + (stamps == null ? "" : Environment.NewLine + string.Join(Environment.NewLine,stamps));
                var bytes = Encoding.UTF8.GetBytes(message.Length > 32768 ? message.Substring(0, 32768) : message);
                file.Write(bytes, 0, bytes.Length); file.Flush(true);
            } catch { /* Best-effort diagnostic; always continue native retirement. */ }
        }
    }
}
