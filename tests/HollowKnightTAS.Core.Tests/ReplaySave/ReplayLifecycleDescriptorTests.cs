using System.Collections.Generic;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Core.Movie;
using System.IO;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.ReplaySave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.ReplaySave
{
    [TestClass]
    public sealed class ReplayLifecycleDescriptorTests
    {
        [TestMethod]
        public void PrefixBindingRejectsPastEditsButAllowsFutureEdits()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var movie = new MovieParser().Parse(new StringReader(Encoding.UTF8.GetString(
                commit.Objects[commit.Descriptor.MovieObjectSha256])), "test.hktas").Document!;
            var log = new ReplayLifecycleLog(commit.Descriptor.BaselineObjectSha256,
                new[] { new ReplayLifecycleRecord(0, 2, MoviePrefixIdentity.ComputeSha256(movie, 3),
                    ReplayLifecycleKind.ReturnToMenu, 0, "", ReplayLifecycleOutcome.Completed, 10, "") });
            var replacement = new[] { new FrameRunCommand(1, TasAction.Dash, 0, 0, false, default) };
            log.VerifyInputPrefixes(movie);
            log.VerifyInputPrefixes(MovieTimelineEditor.Replace(movie, 3, 1, replacement).Movie);
            Assert.ThrowsExactly<InvalidDataException>(() => log.VerifyInputPrefixes(
                MovieTimelineEditor.Replace(movie, 2, 1, replacement).Movie));
            Assert.ThrowsExactly<InvalidDataException>(() => log.VerifyInputPrefixes(
                MovieTimelineEditor.Delete(movie, 0, 7).Movie));
        }

        [TestMethod]
        public void OldEntryRemainsV1AndLifecycleEntryPreservesReferenceWhenPinned()
        {
            var original = ReplaySaveTestFactory.Commit().Descriptor;
            var oldBytes = ReplaySaveEntryCodec.Serialize(original);
            Assert.IsFalse(Encoding.UTF8.GetString(oldBytes).Contains("lifecycleObjectSha256"));
            CollectionAssert.AreEqual(oldBytes, ReplaySaveEntryCodec.Serialize(ReplaySaveEntryCodec.Deserialize(oldBytes)));
            var updated = original.WithLifecycleObject(new string('a', 64));
            var loaded = ReplaySaveEntryCodec.Deserialize(ReplaySaveEntryCodec.Serialize(updated));
            Assert.AreEqual(2, loaded.SchemaVersion);
            Assert.AreEqual(updated.LifecycleObjectSha256, loaded.PinAsManual().LifecycleObjectSha256);
        }

        [TestMethod]
        public void CompleteLifecycleCommitReopensWithBothSlotObjects()
        {
            var original = ReplaySaveTestFactory.Commit();
            var movie = new MovieParser().Parse(new StringReader(Encoding.UTF8.GetString(
                original.Objects[original.Descriptor.MovieObjectSha256])), "test.hktas").Document!;
            var slot = new byte[] { 17, 31 };
            var slotHash = Sha256Utility.ComputeHex(slot);
            var moddedHash = Sha256Utility.ComputeHex(System.Array.Empty<byte>());
            var log = new ReplayLifecycleLog(original.Descriptor.BaselineObjectSha256,
                new[] { new ReplayLifecycleRecord(0, 0, MoviePrefixIdentity.ComputeSha256(movie, 1),
                    ReplayLifecycleKind.LoadSlot, 4, slotHash, ReplayLifecycleOutcome.Completed, 10, "", moddedHash) });
            var commit = original.WithLifecycle(log, new Dictionary<string, byte[]>
                { [slotHash] = slot, [moddedHash] = System.Array.Empty<byte>() });
            slot[0] = 0;
            Assert.AreEqual(1, original.Descriptor.SchemaVersion);
            Assert.AreEqual(2, commit.Descriptor.SchemaVersion);
            var directory = ReplaySaveTestFactory.TemporaryDirectory();
            try
            {
                var store = new ContentAddressedReplaySaveStore(directory);
                Assert.IsTrue(store.Commit(commit).Success);
                var loaded = new ContentAddressedReplaySaveStore(directory).Load(commit.Descriptor.ReplaySaveId);
                Assert.IsTrue(loaded.Success);
                Assert.IsNotNull(loaded.Package!.Lifecycle);
                CollectionAssert.AreEqual(log.Serialize(), loaded.Package.Lifecycle.Serialize());
                CollectionAssert.AreEqual(new byte[] { 17, 31 }, loaded.Package.Objects[slotHash]);
                Assert.AreEqual(0, loaded.Package.Objects[moddedHash].Length);
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void MissingOrMismatchedReferencesAreCorruptButIntactLogIsNotReady()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var slot = new byte[] { 9, 8, 7 };
            var slotHash = Sha256Utility.ComputeHex(slot);
            var movie = new MovieParser().Parse(new StringReader(Encoding.UTF8.GetString(
                commit.Objects[commit.Descriptor.MovieObjectSha256])), "test.hktas").Document!;
            var log = new ReplayLifecycleLog(commit.Descriptor.BaselineObjectSha256,
                new[] { new ReplayLifecycleRecord(0, 0, MoviePrefixIdentity.ComputeSha256(movie, 1), ReplayLifecycleKind.LoadSlot,
                    4, slotHash, ReplayLifecycleOutcome.Completed, 100, "") });
            var logBytes = log.Serialize();
            var hash = Sha256Utility.ComputeHex(logBytes);
            var descriptor = commit.Descriptor.WithLifecycleObject(hash);
            var objects = new Dictionary<string, byte[]>();
            foreach (var item in commit.Objects) objects.Add(item.Key, item.Value);
            bool Resolve(string id, out byte[]? bytes) => objects.TryGetValue(id, out bytes);
            Assert.AreEqual(ReplaySaveStatus.Corrupt, ReplaySaveValidator.Validate(descriptor, Resolve).Status);
            objects[hash] = logBytes;
            Assert.AreEqual(ReplaySaveStatus.Corrupt, ReplaySaveValidator.Validate(descriptor, Resolve).Status);
            objects[slotHash] = new byte[] { 0 };
            Assert.AreEqual(ReplaySaveStatus.Corrupt, ReplaySaveValidator.Validate(descriptor, Resolve).Status);
            objects[slotHash] = slot;
            var intact = ReplaySaveValidator.Validate(descriptor, Resolve);
            Assert.AreEqual(ReplaySaveStatus.Incompatible, intact.Status);
            StringAssert.Contains(intact.Detail, "lifecycle-settings-unknown");
            Assert.IsNull(intact.Package);

            var wrongManifest = ReplaySaveValidator.Validate(descriptor, Resolve, new string('f', 64));
            Assert.AreEqual(ReplaySaveStatus.Incompatible, wrongManifest.Status);
            StringAssert.Contains(wrongManifest.Detail, "lifecycle-settings-unknown");
            var target = objects[descriptor.SemanticSnapshotSha256];
            objects.Remove(descriptor.SemanticSnapshotSha256);
            var missingTarget = ReplaySaveValidator.Validate(descriptor, Resolve);
            Assert.AreEqual(ReplaySaveStatus.Corrupt, missingTarget.Status);
            StringAssert.Contains(missingTarget.Detail, "semantic-snapshot-missing");
            objects[descriptor.SemanticSnapshotSha256] = target;
            objects.Remove(descriptor.JournalSegmentObjectSha256s[0]);
            var missingJournal = ReplaySaveValidator.Validate(descriptor, Resolve);
            Assert.AreEqual(ReplaySaveStatus.JournalGap, missingJournal.Status);
            Assert.IsNull(missingJournal.Package);
        }
    }
}
