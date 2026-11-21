using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.ReplaySave;
using HollowKnightTAS.Core.Cryptography;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.ReplaySave
{
    [TestClass]
    public sealed class ReplayLifecycleTimelineEditorTests
    {
        private static FrameRunCommand Run(long n, TasAction action = TasAction.None) =>
            new FrameRunCommand(n, action, 0, 0, false, new MovieSourceSpan("edit", 1, 1, 1));

        private static MovieDocument Movie()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var parsed = new MovieParser().Parse(new StringReader(Encoding.UTF8.GetString(
                commit.Objects[commit.Descriptor.MovieObjectSha256])), "edit").Document!;
            return new MovieDocument("edit", parsed.Header, new[] { Run(10) });
        }

        private static ReplayLifecycleLog Log(MovieDocument movie) => new ReplayLifecycleLog(
            new string('b', 64), new[] {
                new ReplayLifecycleRecord(0, 2, MoviePrefixIdentity.ComputeSha256(movie, 3),
                    ReplayLifecycleKind.ReturnToMenu, 0, "", ReplayLifecycleOutcome.Completed, 20, ""),
                new ReplayLifecycleRecord(1, 4, MoviePrefixIdentity.ComputeSha256(movie, 5),
                    ReplayLifecycleKind.LoadSlot, 4, new string('a', 64), ReplayLifecycleOutcome.Completed, 100, "", "")
            });

        [TestMethod]
        public void RepeatedEditsMapCurrentBoundariesAndNeverReviveOldEvidence()
        {
            var movie = Movie();
            var log = Log(movie);
            var original = log.Serialize();
            var first = ReplayLifecycleTimelineEditor.Edit(movie, log, TimelineEditKind.Insert,
                0, 0, new[] { Run(2, TasAction.Right) });
            var second = ReplayLifecycleTimelineEditor.Edit(first, TimelineEditKind.Delete,
                0, 2, Array.Empty<FrameRunCommand>());
            CollectionAssert.AreEqual(new long[] { 2, 4 }, second.Operations.Select(x => x.AfterMovieTick).ToArray());
            Assert.IsTrue(second.Operations.All(x => x.RequiresReplay));
            CollectionAssert.AreEqual(original, log.Serialize());
            CollectionAssert.AreEqual(new long[] { 4, 6 }, first.Operations.Select(x => x.AfterMovieTick).ToArray());
            var cursor = new ReplayLifecycleCursor(second);
            Assert.AreEqual(ReplayLifecycleOutcome.Waiting, cursor.Claim(2, 0)!.Outcome);
        }

        [TestMethod]
        public void InsertMovesBoundaryAndRequiresReplayWithoutChangingSourceEvidence()
        {
            var movie = Movie();
            var log = Log(movie);
            var oldBytes = log.Serialize();
            var result = ReplayLifecycleTimelineEditor.Edit(movie, log, TimelineEditKind.Insert,
                3, 0, new[] { Run(2, TasAction.Attack) });
            CollectionAssert.AreEqual(new long[] { 4, 6 }, result.Operations.Select(x => x.AfterMovieTick).ToArray());
            Assert.IsTrue(result.Operations.All(x => x.RequiresReplay));
            Assert.AreSame(log.Records[1], result.Operations[1].Source);
            Assert.AreEqual("", result.Operations[1].Source.ModdedSlotObjectSha256);
            CollectionAssert.AreEqual(oldBytes, log.Serialize());
            Assert.AreEqual(10L, movie.Commands.OfType<FrameRunCommand>().Sum(x => x.FrameCount));
        }

        [TestMethod]
        public void DeletingAcrossActionsPreservesTheirOrderAndSlotDependencies()
        {
            var movie = Movie();
            var result = ReplayLifecycleTimelineEditor.Edit(movie, Log(movie), TimelineEditKind.Delete,
                2, 5, Array.Empty<FrameRunCommand>());
            CollectionAssert.AreEqual(new long[] { 1, 1 }, result.Operations.Select(x => x.AfterMovieTick).ToArray());
            CollectionAssert.AreEqual(new[] { 0, 1 }, result.Operations.Select(x => x.Source.Sequence).ToArray());
            Assert.AreEqual(result.Operations[0].InputPrefixSha256, result.Operations[1].InputPrefixSha256);
            Assert.AreEqual(new string('a', 64), result.Operations[1].Source.SlotObjectSha256);
            Assert.IsTrue(result.Operations.All(x => x.RequiresReplay));
        }

        [TestMethod]
        public void EditedPlanProducesCompletionEvidenceOnlyAfterNativeCompletion()
        {
            var movie = Movie();
            var log = Log(movie);
            var edit = ReplayLifecycleTimelineEditor.Edit(movie, log, TimelineEditKind.Delete,
                2, 5, Array.Empty<FrameRunCommand>());
            var cursor = new ReplayLifecycleCursor(edit);
            Assert.IsFalse(cursor.IsComplete);
            Assert.IsFalse(cursor.CanAdvanceInput(1));
            var request = cursor.Claim(1, 100)!;
            Assert.AreEqual(ReplayLifecycleOutcome.Waiting, request.Outcome);
            Assert.AreEqual(0L, request.NativeFrameCount);
            Assert.IsNull(cursor.Claim(1, 101));
            var first = cursor.Complete(0, 107);
            Assert.AreEqual(ReplayLifecycleOutcome.Completed, first.Outcome);
            Assert.AreEqual(7L, first.NativeFrameCount);
            Assert.AreEqual(1L, first.AfterMovieTick);
            Assert.AreEqual(edit.Operations[0].InputPrefixSha256, first.InputPrefixSha256);
            Assert.IsFalse(cursor.CanAdvanceInput(1));
            cursor.Claim(1, 107);
            var second = cursor.Complete(1, 118);
            Assert.AreEqual(11L, second.NativeFrameCount);
            Assert.IsTrue(cursor.IsComplete);
            var completed = new ReplayLifecycleLog(log.RootBaselineSha256, new[] { first, second });
            completed.VerifyInputPrefixes(edit.InputEdit.Movie);
            Assert.AreEqual(20L, log.Records[0].NativeFrameCount);
            Assert.AreEqual(100L, log.Records[1].NativeFrameCount);
        }

        [TestMethod]
        public void PortableExecutionPlanRoundTripsAndExecutesWithoutOldCompletionEvidence()
        {
            var commit = ReplaySaveTestFactory.Commit();
            var movie = Movie();
            var slotBytes = new byte[] { 1, 2, 3 };
            var slotHash = Sha256Utility.ComputeHex(slotBytes);
            var original = new ReplayLifecycleLog(commit.Descriptor.BaselineObjectSha256,
                new[] { new ReplayLifecycleRecord(0, 4, MoviePrefixIdentity.ComputeSha256(movie, 5),
                    ReplayLifecycleKind.LoadSlot, 4, slotHash, ReplayLifecycleOutcome.Completed, 99, "", "") });
            var edit = ReplayLifecycleTimelineEditor.Edit(movie, original, TimelineEditKind.Replace,
                0, 1, new[] { Run(1, TasAction.Left) });
            var plan = ReplayLifecycleExecutionPlan.FromEdit(new string('c', 64), edit,
                commit.Objects[commit.Descriptor.BaselineObjectSha256],
                new Dictionary<string, byte[]> { [slotHash] = slotBytes });
            var bytes = plan.Serialize();
            var decoded = ReplayLifecycleExecutionPlan.Deserialize(bytes, Sha256Utility.ComputeHex(bytes));
            var restoredRoot = decoded.ValidateSeek(movie.Header.ManifestSha256, 9);
            Assert.AreEqual(movie.Header.BaselineId, restoredRoot.BaselineId);
            Assert.ThrowsExactly<InvalidDataException>(() => decoded.ValidateSeek(movie.Header.ManifestSha256, -1));
            Assert.ThrowsExactly<InvalidDataException>(() => decoded.ValidateSeek(movie.Header.ManifestSha256, 10));
            var otherManifest = movie.Header.ManifestSha256 == new string('a', 64) ? new string('b', 64) : new string('a', 64);
            var crossManifestRoot = decoded.ValidateSeek(otherManifest, 0);
            Assert.AreEqual(movie.Header.BaselineId, crossManifestRoot.BaselineId);
            slotBytes[0] = 99;
            Assert.AreEqual((byte)1, decoded.CopySlotObjects()[slotHash][0]);
            Assert.AreEqual(ReplayLifecycleOutcome.Waiting, decoded.Requests[0].Outcome);
            Assert.AreEqual(0L, decoded.Requests[0].NativeFrameCount);
            var before = new ReplayLifecycleCursor(decoded, 3);
            Assert.IsTrue(before.IsComplete);
            var cursor = new ReplayLifecycleCursor(decoded, 4);
            Assert.IsFalse(cursor.IsComplete);
            cursor.Claim(4, 100);
            Assert.AreEqual(7L, cursor.Complete(0, 107).NativeFrameCount);
            Assert.IsTrue(cursor.IsComplete);
            var hash = Sha256Utility.ComputeHex(bytes);
            bytes[20] ^= 1;
            Assert.ThrowsExactly<InvalidDataException>(() => ReplayLifecycleExecutionPlan.Deserialize(bytes, hash));
        }

        [TestMethod]
        public void FutureEditPreservesEarlierEvidenceAndWrongSourceIsRejected()
        {
            var movie = Movie();
            var log = Log(movie);
            var future = ReplayLifecycleTimelineEditor.Edit(movie, log, TimelineEditKind.Replace,
                7, 1, new[] { Run(1, TasAction.Jump) });
            Assert.IsTrue(future.Operations.All(x => !x.RequiresReplay));
            var past = MovieTimelineEditor.Replace(movie, 0, 1, new[] { Run(1, TasAction.Left) }).Movie;
            Assert.ThrowsExactly<InvalidDataException>(() => ReplayLifecycleTimelineEditor.Edit(
                past, log, TimelineEditKind.Delete, 8, 1, Array.Empty<FrameRunCommand>()));
        }
    }
}
