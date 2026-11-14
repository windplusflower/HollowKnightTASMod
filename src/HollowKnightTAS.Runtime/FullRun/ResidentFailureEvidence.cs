using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

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
        internal static void RecordDomain(string domain, object state)
        {
            if (!Collecting) return;
            try {
                // Native Mono rows repeat shared type metadata across thousands
                // of owners. Keep failing owners complete within the same cap.
                if (state is ResidentMonoNativeState.Data[] monoRows)
                    state = monoRows.Where(row => ResidentMonoNativeState.RequiredGaps(row).Length != 0).ToArray();
                var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output) || !Directory.Exists(output)) return;
                foreach (var character in domain) if (!char.IsLetterOrDigit(character)) return;
                using var process = Process.GetCurrentProcess();
                using var bytes = new BoundedEvidenceStream();
                using (var text = new StreamWriter(bytes, new UTF8Encoding(false), 4096, true))
                using (var json = new JsonTextWriter(text)) {
                    JsonSerializer.CreateDefault().Serialize(json, state);
                    json.Flush();
                }
                var path = Path.Combine(output, "resident-failed-domain-" + domain + "-" + process.Id + ".json");
                File.WriteAllBytes(path, bytes.ToArray());
                Mark("failed domain snapshot " + domain + " bytes=" + bytes.Length);
            } catch (Exception error) {
                Mark("failed domain snapshot unavailable " + domain + " " + error.GetType().Name);
            }
        }
        private sealed class BoundedEvidenceStream : MemoryStream
        {
            public override void Write(byte[] buffer, int offset, int count)
            {
                if (Length + count > 16 * 1024 * 1024) throw new IOException("failure domain evidence exceeds 16 MiB");
                base.Write(buffer, offset, count);
            }
        }
        // Native retirement may stop the process before the Mod logger flushes.
        // Persist the first managed exception only on the failed path, before
        // closing the epoch. Never turn an evidence I/O error into the cause.
        internal static void Record(Exception failure)
        {
            try {
                var output = Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT");
                if (string.IsNullOrEmpty(output) || !Path.IsPathRooted(output) || !Directory.Exists(output)) return;
                var cause = failure.GetBaseException();
                var summary = cause.GetType().Name + ": " + cause.Message;
                if (summary.Length > 2048) summary = summary.Substring(0,2048);
                Mark("fault " + summary);
                var firstLine = Encoding.UTF8.GetBytes(summary + Environment.NewLine);
                using var process = Process.GetCurrentProcess();
                var path = Path.Combine(output, "resident-first-fault-" + process.Id + ".txt");
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1);
                file.Write(firstLine,0,firstLine.Length);
                var message = failure.ToString() + (stamps == null ? "" : Environment.NewLine + string.Join(Environment.NewLine,stamps));
                var bytes = Encoding.UTF8.GetBytes(message.Length > 32768 ? message.Substring(0, 32768) : message);
                file.Write(bytes, 0, bytes.Length); file.Flush(true);
            } catch { /* Best-effort diagnostic; always continue native retirement. */ }
        }
    }
}
