using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class SequencePackageTests
{
    private string root = null!;
    [TestInitialize] public void Setup() => Directory.CreateDirectory(root = Path.Combine(Path.GetTempPath(), "hktas-package-" + Guid.NewGuid().ToString("N")));
    [TestCleanup] public void Cleanup()
    {
        if (!Path.GetFullPath(root).StartsWith(Path.Combine(Path.GetTempPath(), "hktas-package-"), StringComparison.OrdinalIgnoreCase)) throw new Exception();
        Directory.Delete(root, true);
    }
    private static InitialSaveSnapshot Snapshot() => new(new Dictionary<string, byte[]>
    {
        ["user1.dat"] = new byte[] { 1, 2, 3 }, ["user1.modded.json"] = new byte[] { 4 }, ["user4.dat"] = new byte[] { 5 }
    });

    [TestMethod] public async Task PackageRoundTripAndRepeatedSavePreserveInitialFiles()
    {
        var path = Path.Combine(root, "sequence.hktaspack");
        var snapshot = Snapshot();
        await SequencePackage.WriteAsync(path, "movie 中文", snapshot);
        var loaded = SequencePackage.Read(path);
        Assert.AreEqual("movie 中文", loaded.Movie);
        Assert.AreEqual(snapshot.Id, loaded.InitialSaves!.Id);
        await SequencePackage.WriteAsync(path, "edited future input", loaded.InitialSaves);
        Assert.AreEqual(snapshot.Id, SequencePackage.Read(path).InitialSaves!.Id);
        CollectionAssert.AreEqual(new[] { "user1.dat", "user1.modded.json", "user4.dat" }, loaded.InitialSaves.Hashes.Keys.ToArray());
    }

    [TestMethod] public void ShadowSeedIsIndependentOfRealSavesAndRuntimeChanges()
    {
        var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
        File.WriteAllBytes(Path.Combine(original, "user2.dat"), new byte[] { 99 });
        var seed = Snapshot();
        var session = ProtectedSaveSession.Prepare("first", original, Path.Combine(root, "first"), seed);
        Assert.IsTrue(session.OriginalSha256.ContainsKey("user2.dat"));
        Assert.IsFalse(session.Descriptor.OriginalFileSha256.ContainsKey("user2.dat"));
        Assert.IsFalse(File.Exists(Path.Combine(root, "first", "user2.dat")));
        File.WriteAllBytes(Path.Combine(root, "first", "user1.dat"), new byte[] { 88 });
        var next = ProtectedSaveSession.Prepare("second", original, Path.Combine(root, "second"), seed);
        Assert.AreEqual(seed.Hashes["user1.dat"], Sha256Utility.ComputeFileHex(Path.Combine(next.Descriptor.ShadowRoot, "user1.dat")));
        CollectionAssert.AreEqual(new byte[] { 99 }, File.ReadAllBytes(Path.Combine(original, "user2.dat")));
    }

    [TestMethod] public void NewSessionCapturesImmutableOriginBeforeShadowWrites()
    {
        var original = Path.Combine(root, "original"); Directory.CreateDirectory(original);
        File.WriteAllBytes(Path.Combine(original, "user3.dat"), new byte[] { 7 });
        var session = ProtectedSaveSession.Prepare("new", original, Path.Combine(root, "shadow"));
        File.WriteAllBytes(Path.Combine(session.Descriptor.ShadowRoot, "user3.dat"), new byte[] { 8 });
        Assert.AreEqual(Sha256Utility.ComputeHex(new byte[] { 7 }), session.InitialSaves.Hashes["user3.dat"]);
    }

    [TestMethod] public async Task EmptySlotsRemainEmptyEvenWhenLocalSavesExist()
    {
        var seed = new InitialSaveSnapshot(new Dictionary<string, byte[]>());
        var path = Path.Combine(root, "empty.hktaspack");
        await SequencePackage.WriteAsync(path, "empty", seed);
        Assert.AreEqual(0, SequencePackage.Read(path).InitialSaves!.Hashes.Count);
    }

    [TestMethod] public async Task CorruptMissingDuplicateAndUndeclaredEntriesAreRejected()
    {
        var expected = Snapshot();
        foreach (var fault in new[] { "corrupt", "missing", "duplicate", "undeclared", "escape" })
        {
            var path = Path.Combine(root, fault + ".hktaspack");
            await SequencePackage.WriteAsync(path, "movie", expected);
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            {
                if (fault == "missing" || fault == "corrupt") archive.GetEntry("initial-saves/user1.dat")!.Delete();
                if (fault != "missing")
                {
                    var entryName = fault switch
                    {
                        "escape" => "../user1.dat",
                        "undeclared" => "undeclared.txt",
                        _ => "initial-saves/user1.dat"
                    };
                    using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
                    writer.Write("bad");
                }
            }

            if (fault == "missing" || fault == "duplicate")
            {
                Assert.Throws<InvalidDataException>(() => SequencePackage.Read(path), fault);
            }
            else
            {
                var loaded = SequencePackage.Read(path);
                Assert.AreEqual("movie", loaded.Movie, fault);
                Assert.IsNotNull(loaded.InitialSaves, fault);
                if (fault == "corrupt")
                {
                    Assert.AreEqual(
                        Sha256Utility.ComputeHex(Encoding.UTF8.GetBytes("bad")),
                        loaded.InitialSaves.Hashes["user1.dat"]);
                    Assert.AreEqual(expected.Hashes["user1.modded.json"], loaded.InitialSaves.Hashes["user1.modded.json"]);
                    Assert.AreEqual(expected.Hashes["user4.dat"], loaded.InitialSaves.Hashes["user4.dat"]);
                }
                else
                {
                    Assert.AreEqual(expected.Id, loaded.InitialSaves.Id, fault);
                    CollectionAssert.AreEqual(expected.Hashes.Keys.ToArray(), loaded.InitialSaves.Hashes.Keys.ToArray());
                }
            }
        }
    }

    [TestMethod] public async Task LegacyTextStaysUnboundAndFailedSaveDoesNotReplaceFile()
    {
        var path = Path.Combine(root, "legacy.hktas");
        await SequencePackage.WriteAsync(path, "legacy", null);
        Assert.IsNull(SequencePackage.Read(path).InitialSaves);
        await Assert.ThrowsAsync<InvalidDataException>(() => SequencePackage.WriteAsync(path, "bad", Snapshot()));
        Assert.AreEqual("legacy", File.ReadAllText(path));
    }
}
