using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class ResidentContinuationTraceTests
{
    private const BindingFlags All = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Names = "admission assetManifest assetValues clock componentNative directory globalNative input managedGraph objectNative process rng scheduler staticRoots typeManifest".Split(' ');

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "hktas-resident-trace-test-" + Guid.NewGuid().ToString("N"));
        private readonly Type api, identity, group;
        internal Fixture()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            api = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentContinuationTrace", true)!;
            identity = api.GetNestedType("Identity", All)!; group = api.GetNestedType("Group", All)!; Directory.CreateDirectory(root);
        }
        internal string PathFor(string name) => Path.Combine(root, name + ".jsonl");
        internal static void Set(object owner, string key, object value) => owner.GetType().GetField(key, All)!.SetValue(owner, value);
        internal object Identity(long end = 14)
        {
            var result = Activator.CreateInstance(identity, true)!;
            Set(result, "Role", "source"); Set(result, "PacketSha256", new string('a', 64)); Set(result, "PacketBytes", 10);
            Set(result, "MovieSha256", new string('b', 64)); Set(result, "SaveSha256", new string('c', 64)); Set(result, "BuildSha256", new string('d', 64));
            Set(result, "BoundaryOrdinal", 2); Set(result, "SourceScene", "Source"); Set(result, "TargetScene", "Target");
            Set(result, "StartNative", 20L); Set(result, "StartMovie", 10L); Set(result, "StartConsumed", 17L); Set(result, "EndMovie", end); Set(result, "MovieLength", 14L);
            return result;
        }
        internal IDisposable Open(string name, object? data = null) => (IDisposable)api.GetMethod("Open", All)!.Invoke(null, new[] { PathFor(name), data ?? Identity() })!;
        internal object Group(string name, string[]? expected = null, string[]? observed = null, string[]? gaps = null)
            => group.GetMethod("Capture", All)!.Invoke(null, new object[] { name, Encoding.UTF8.GetBytes("abstract " + name),
                expected ?? new[] { "a", "猫/" + name }, observed ?? new[] { "猫/" + name, "a" }, gaps ?? Array.Empty<string>() })!;
        internal Array Groups() { var result = Array.CreateInstance(group, Names.Length); for (var i = 0; i < Names.Length; i++) result.SetValue(Group(Names[i]), i); return result; }
        internal object Missing(string name) => group.GetMethod("Missing", All)!.Invoke(null, new object[] { name, "unknown producer" })!;
        internal void Sample(object writer, string phase, long native, long movie, long consumed, bool ready, Array groups, string? boundary = null)
            => api.GetMethod("Sample", All)!.Invoke(writer, new object[] { phase, native, movie, consumed, ready, boundary ?? (ready ? "input" : "load"), ready ? "Target" : "", groups });
        internal void Seal(object writer, long native, long movie, long consumed) => api.GetMethod("Seal", All)!.Invoke(writer, new object[] { native, movie, consumed });
        public void Dispose() { foreach (var path in Directory.GetFiles(root)) File.Delete(path); Directory.Delete(root); }
    }
    private static void Reject(Action action)
    {
        var error = Assert.ThrowsExactly<TargetInvocationException>(action); Assert.IsInstanceOfType<InvalidDataException>(error.InnerException);
    }
    private static JsonElement Parse(string line) { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }
    private static string Digest(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    [TestMethod]
    public void SealedTraceContainsAllNativePairsLoadingSamplesKMarkersAndTheExactWireDigest()
    {
        using var fixture = new Fixture(); var identity = fixture.Identity(); using var writer = fixture.Open("complete", identity);
        Fixture.Set(identity, "EndMovie", 12L); Fixture.Set(identity, "Role", "target");
        var groups = fixture.Groups(); long native = 20, movie = 10, consumed = 17; var previousReady = false;
        foreach (var ready in new[] { false, true, true, false, true, true }) {
            if (previousReady) consumed = 0;
            fixture.Sample(writer, "before", native, movie, consumed, ready, groups);
            native++; if (ready) { movie++; consumed += 2; }
            fixture.Sample(writer, "after", native, movie, consumed, ready, groups);
            previousReady = ready;
        }
        var path = fixture.PathFor("complete"); Assert.IsFalse(File.Exists(path)); fixture.Seal(writer, native, movie, consumed);
        var lines = File.ReadAllLines(path, Encoding.UTF8); Assert.AreEqual(14, lines.Length);
        var records = lines.Select(Parse).ToArray(); var header = records[0]; var footer = records[^1];
        Assert.AreEqual("source", header.GetProperty("role").GetString()); Assert.AreEqual(14L, header.GetProperty("endMovie").GetInt64());
        Assert.AreEqual(Digest(lines[0] + "\n"), footer.GetProperty("headerSha256").GetString());
        Assert.AreEqual(Digest(string.Join("\n", lines.Take(13)) + "\n"), footer.GetProperty("bodySha256").GetString());
        Assert.AreEqual(2, footer.GetProperty("loadingFrames").GetInt32()); Assert.AreEqual(6, footer.GetProperty("pairs").GetInt32());
        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, records.Skip(1).Take(12).Select(row => row.GetProperty("k").GetInt32()).Where(k => k != 0).ToArray());
        foreach (var row in records.Skip(1).Take(12)) {
            // Independent Python fixture includes the UTF8 key "猫" and hashes the whole group proof protocol.
            Assert.AreEqual("aa13cea79de763af4c79fc5c885410a5297d8a4e637d0fc450ca94c480a52f6d", row.GetProperty("worldSha256").GetString());
            Assert.IsTrue(row.GetProperty("samplesComplete").GetBoolean());
        }
        Reject(() => fixture.Open("complete")); Reject(() => fixture.Seal(writer, native, movie, consumed));
    }

    [TestMethod]
    public void MissingDuplicateOutOfOrderOrInputConsumingLoadingFramePermanentlyFaultsTheSink()
    {
        using var fixture = new Fixture(); var groups = fixture.Groups();
        var scripts = new Action<IDisposable>[] {
            w => fixture.Sample(w, "after", 21, 10, 17, false, groups),
            w => fixture.Sample(w, "before", 21, 10, 17, false, groups),
            w => { fixture.Sample(w, "before", 20, 10, 17, false, groups); fixture.Sample(w, "before", 20, 10, 17, false, groups); },
            w => { fixture.Sample(w, "before", 20, 10, 17, false, groups); fixture.Sample(w, "after", 21, 10, 18, false, groups); },
            w => { fixture.Sample(w, "before", 20, 10, 17, false, groups); fixture.Sample(w, "after", 21, 11, 19, true, groups); },
            w => { fixture.Sample(w, "before", 20, 10, 17, true, groups); fixture.Sample(w, "after", 21, 11, 19, true, groups, "other"); },
            w => { fixture.Sample(w, "before", 20, 10, 17, true, groups); fixture.Sample(w, "after", 21, 11, 19, true, groups); fixture.Sample(w, "before", 21, 11, 19, true, groups); },
            w => { fixture.Sample(w, "before", 20, 10, 17, true, groups); fixture.Seal(w, 20, 10, 17); },
            w => { fixture.Sample(w, "before", 20, 10, 17, true, groups); fixture.Sample(w, "after", 21, 11, 19, true, groups); fixture.Seal(w, 21, 11, 19); }
        };
        for (var i = 0; i < scripts.Length; i++) {
            using var writer = fixture.Open("bad" + i); Reject(() => scripts[i](writer));
            Reject(() => fixture.Sample(writer, "before", 20, 10, 17, true, groups)); Assert.IsFalse(File.Exists(fixture.PathFor("bad" + i)));
        }
    }

    [TestMethod]
    public void IncompleteAndDuplicateDomainsCannotBeSubstitutedForTheWholeWorld()
    {
        using var fixture = new Fixture(); var groups = fixture.Groups();
        Reject(() => fixture.Group("legacy-projection")); Reject(() => fixture.Group("directory", new[] { "a", "a" }));
        Reject(() => fixture.Group("directory", observed: new[] { "a", "a" })); Reject(() => fixture.Group("directory", gaps: new[] { "x", "x" }));
        var missing = Array.CreateInstance(groups.GetType().GetElementType()!, groups.Length - 1); Array.Copy(groups, missing, missing.Length);
        using var first = fixture.Open("missing"); Reject(() => fixture.Sample(first, "before", 20, 10, 17, false, missing));
        groups.SetValue(groups.GetValue(0), 1); using var second = fixture.Open("duplicate");
        Reject(() => fixture.Sample(second, "before", 20, 10, 17, false, groups));
    }

    [TestMethod]
    public void MissingPayloadAndMismatchedOwnerSetsRemainExplicitInTheSealedDiagnostic()
    {
        using var fixture = new Fixture(); using var writer = fixture.Open("gap", fixture.Identity(11)); var groups = fixture.Groups();
        groups.SetValue(fixture.Missing("admission"), 0);
        groups.SetValue(fixture.Group("assetManifest", new[] { "a" }, new[] { "b" }), 1);
        fixture.Sample(writer, "before", 20, 10, 17, true, groups); fixture.Sample(writer, "after", 21, 11, 19, true, groups); fixture.Seal(writer, 21, 11, 19);
        var records = File.ReadAllLines(fixture.PathFor("gap")).Select(Parse).ToArray(); var row = records[1];
        Assert.IsFalse(row.GetProperty("samplesComplete").GetBoolean()); Assert.IsFalse(records[^1].GetProperty("samplesComplete").GetBoolean());
        var proofs = row.GetProperty("groups"); Assert.AreEqual(0, proofs[0].GetProperty("bytes").GetInt32());
        Assert.AreEqual("unknown producer", proofs[0].GetProperty("gaps")[0].GetString());
        Assert.AreNotEqual(proofs[1].GetProperty("expectedKeysSha256").GetString(), proofs[1].GetProperty("observedKeysSha256").GetString());
    }
}
