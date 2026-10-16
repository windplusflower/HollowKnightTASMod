using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalJsonEvidenceTests
{
    [TestMethod]
    public void Complete_large_evidence_matches_original_UTF8_bytes_and_reports_cost_without_deferring_writes()
    {
        var sample = new CanonicalS0ObservationSnapshot();
        for (var index = 0; index < 10236; index++) {
            var key = "root/" + index.ToString("D5") + "/" + new string('p', 480);
            sample.Fields.Add(key, "f32:00-00-00-80");
            if (index < 6100) sample.FieldOwnership.Add(key, "S0");
        }
        sample.Dependencies = new[] { "中文\n\"\\\t𐀀", "raw NaN payload remains f32:01-00-c0-7f" };
        var directory = Path.Combine(Path.GetTempPath(), "hktas-json-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try {
            var original = Path.Combine(directory, "original.json"); var streamed = Path.Combine(directory, "streamed.json");
            var timer = Stopwatch.StartNew();
            File.WriteAllText(original, JsonConvert.SerializeObject(sample)); var old = timer.Elapsed.TotalMilliseconds;
            timer.Restart(); CanonicalJsonEvidence.Write(streamed, sample); var next = timer.Elapsed.TotalMilliseconds;
            CollectionAssert.AreEqual(File.ReadAllBytes(original), File.ReadAllBytes(streamed));
            Assert.IsTrue(new FileInfo(streamed).Length > 8_000_000);
            Console.WriteLine("complete JSON evidence (.NET8 only): bytes=" + new FileInfo(streamed).Length + ";originalMs=" + old + ";streamedMs=" + next);
            File.WriteAllText(streamed, "old file content that must be truncated");
            CanonicalJsonEvidence.Write(streamed, new { Empty = true });
            Assert.AreEqual(JsonConvert.SerializeObject(new { Empty = true }), File.ReadAllText(streamed));
        } finally { foreach (var path in Directory.GetFiles(directory)) File.Delete(path); Directory.Delete(directory); }
    }
}
