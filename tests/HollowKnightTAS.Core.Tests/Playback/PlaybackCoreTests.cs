using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Input;
using HollowKnightTAS.Core.Ledger;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.Playback;
using HollowKnightTAS.Core.Recording;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Playback
{
    [TestClass]
    public sealed class PlaybackCoreTests
    {
        private const string Manifest =
            "77b19f22c6df1a8db00ebf1307106a63321af90054a433191acef1267a8c7285";
        private const string Baseline =
            "8b8c7995768198cdac8b44a563e875a50bb47efc70a25eab2e7afabccbd262b9";

        [TestMethod]
        public void InputBatches_PreserveContinuousEdgesAfterJournalFlush()
        {
            var header = ParseMovie("p0-recording.hktas").Header;
            var held = new[] { TasAction.Right, TasAction.Right, TasAction.Right,
                TasAction.None, TasAction.Jump, TasAction.Jump, TasAction.None };
            var samples = held.Select((value, index) => InputSample.FromHeld(
                (ulong)index, value, index == 0 ? TasAction.None : held[index - 1])).ToArray();
            var whole = new MovieCursor(new MovieDocument("whole", header,
                FrameRunEncoder.Encode(samples)));
            var journal = new ReplayJournal(header.BaselineId, header.BaselineSha256);
            for (var index = 0; index < held.Length; index++)
            {
                var batch = new MovieDocument("batch", header,
                    FrameRunEncoder.Encode(new[] { samples[index] }));
                var machine = new PlaybackStateMachine();
                var start = machine.StartReplay(batch, new PlaybackContext(
                    header.ManifestSha256, header.BaselineId, header.BaselineSha256, 0,
                    allowSceneTransitions: false,
                    initialHeld: journal.LastCommittedSample?.Held ?? TasAction.None));
                Assert.IsTrue(start.Success, start.Error);
                Assert.IsTrue(whole.MoveNext());
                var actual = start.FirstInput!.Value;
                Assert.AreEqual(whole.CurrentInput.Held, actual.Held);
                Assert.AreEqual(whole.CurrentInput.Pressed, actual.Pressed, "press at " + index);
                Assert.AreEqual(whole.CurrentInput.Released, actual.Released, "release at " + index);
                journal.Suspend();
                Assert.IsTrue(journal.Append(actual,
                    new TickStamp(actual.InputTick, 0, 0, 0, TickPhase.InControlCommitted)).Success);
                journal.AcknowledgePersistedThrough(index);
                Assert.AreEqual(0, journal.RetainedCount);
                Assert.AreEqual(held[index], journal.LastCommittedSample!.Value.Held);
            }
            Assert.IsFalse(whole.MoveNext());
        }

        [TestMethod]
        public void FrameRunEncodeExpand_RoundTripsEveryTickAndEdge()
        {
            var samples = BuildEdgeSamples();
            var commands = FrameRunEncoder.Encode(samples);
            var expanded = FrameRunEncoder.Expand(commands);

            Assert.HasCount(5, commands);
            Assert.HasCount(samples.Count, expanded);
            for (var index = 0; index < samples.Count; index++)
            {
                Assert.AreEqual((ulong)index, expanded[index].InputTick);
                Assert.AreEqual(samples[index].Held, expanded[index].Held);
                Assert.AreEqual(samples[index].Pressed, expanded[index].Pressed);
                Assert.AreEqual(samples[index].Released, expanded[index].Released);
                Assert.AreEqual(samples[index].AxisX, expanded[index].AxisX);
                Assert.AreEqual(samples[index].AxisY, expanded[index].AxisY);
            }
        }

        [TestMethod]
        public void MovieCursor_SchedulesEventsAtExactBoundaries()
        {
            var movie = ParseMovie("p0-recording.hktas");
            var cursor = new MovieCursor(movie);
            var events = new List<PlaybackEvent>();
            var inputs = new List<InputSample>();

            while (cursor.MoveNext())
            {
                events.AddRange(cursor.PendingEvents);
                inputs.Add(cursor.CurrentInput);
            }
            events.AddRange(cursor.PendingEvents);

            Assert.HasCount(175, inputs);
            CollectionAssert.AreEqual(
                new long[] { 0, 10, 55, 175 },
                events.Select(value => value.MovieTick).ToArray());
            CollectionAssert.AreEqual(
                new[]
                {
                    PlaybackEventKind.Marker,
                    PlaybackEventKind.Checkpoint,
                    PlaybackEventKind.Checkpoint,
                    PlaybackEventKind.Assertion
                },
                events.Select(value => value.Kind).ToArray());
            Assert.AreEqual(TasAction.None, inputs[9].Held);
            Assert.AreEqual(TasAction.Left, inputs[10].Held);
            Assert.AreEqual(TasAction.Left, inputs[54].Held);
            Assert.AreEqual(TasAction.None, inputs[55].Held);
            Assert.AreEqual(TasAction.Left, inputs[10].Pressed);
            Assert.AreEqual(TasAction.Left, inputs[55].Released);
            Assert.AreEqual(TasAction.None, cursor.CurrentInput.Held);
        }

        [TestMethod]
        public void PlaybackStateMachine_AdvancesRawTicksIndependentOfMovieOrigin()
        {
            var movie = ParseMovie("p0-recording.hktas");
            var machine = new PlaybackStateMachine();
            var start = machine.StartReplay(
                movie,
                new PlaybackContext(Manifest, "slot-2-gg-vengefly", Baseline, 3));

            Assert.IsTrue(start.Success, start.Error);
            Assert.AreEqual(PlaybackMode.Replaying, machine.Mode);
            Assert.AreEqual(0UL, start.FirstInput!.Value.InputTick);

            var rawTick = 91234UL;
            PlaybackTickResult? final = null;
            for (var movieTick = 0; movieTick < 175; movieTick++)
            {
                final = machine.Advance(
                    new TickStamp(
                        rawTick + (ulong)movieTick,
                        movieTick,
                        movieTick,
                        3,
                        TickPhase.InControlCommitted));
                Assert.IsTrue(final.Success, final.Error);
                Assert.AreEqual((ulong)movieTick, final.ConsumedInput.InputTick);
            }

            Assert.IsNotNull(final);
            Assert.IsTrue(final!.ReleaseBoundary);
            Assert.AreEqual(PlaybackMode.Stopping, machine.Mode);
            Assert.AreEqual(TasAction.None, final.NextInput.Held);
            Assert.AreEqual(TasAction.None, final.NextInput.Released);
            machine.CompleteCleanup(true, string.Empty);
            Assert.AreEqual(PlaybackMode.Idle, machine.Mode);
        }

        [TestMethod]
        public void PlaybackStateMachine_RejectsIllegalStartsAndContextMismatch()
        {
            var movie = ParseMovie("p0-recording.hktas");
            var machine = new PlaybackStateMachine();

            var manifestMismatchMachine = new PlaybackStateMachine();
            var manifestStart = manifestMismatchMachine.StartReplay(
                movie,
                new PlaybackContext(new string('0', 64), "slot-2-gg-vengefly", Baseline, 0));
            Assert.IsTrue(manifestStart.Success, manifestStart.Error);

            var baselineMismatchMachine = new PlaybackStateMachine();
            var baselineStart = baselineMismatchMachine.StartReplay(
                movie,
                new PlaybackContext(Manifest, "other", Baseline, 0));
            Assert.IsTrue(baselineStart.Success, baselineStart.Error);

            Assert.IsTrue(
                machine.StartRecording(
                    new RecordingContext("slot-2-gg-vengefly", Baseline))
                    .Success);
            Assert.IsFalse(
                machine.StartReplay(
                    movie,
                    new PlaybackContext(Manifest, "slot-2-gg-vengefly", Baseline, 0))
                    .Success);
            var stop = machine.Stop(PlaybackStopReason.Manual);
            Assert.IsTrue(stop.Success);
            machine.CompleteCleanup(true, string.Empty);
            Assert.AreEqual(PlaybackMode.Idle, machine.Mode);
        }

        [TestMethod]
        public void PlaybackStateMachine_SceneChangeAndCleanupFailureAreFailClosed()
        {
            var machine = new PlaybackStateMachine();
            var start = machine.StartReplay(
                ParseMovie("p0-recording.hktas"),
                new PlaybackContext(Manifest, "slot-2-gg-vengefly", Baseline, 0));
            Assert.IsTrue(start.Success);

            var tick = machine.Advance(
                new TickStamp(100, 0, 0, 1, TickPhase.InControlCommitted));
            Assert.IsFalse(tick.Success);
            Assert.AreEqual(PlaybackMode.Stopping, machine.Mode);
            Assert.AreEqual(PlaybackStopReason.SceneChanged, machine.StopReason);

            machine.CompleteCleanup(false, "restore mismatch");
            Assert.AreEqual(PlaybackMode.Faulted, machine.Mode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(machine.FaultMessage));
            Assert.IsTrue(machine.ResetFault());
            Assert.AreEqual(PlaybackMode.Idle, machine.Mode);
        }

        [TestMethod]
        public void ReplayJournal_AppendsCommitsAcknowledgesAndBoundsRetainedData()
        {
            var journal = new ReplayJournal("baseline", Baseline);
            for (var index = 0; index < 300; index++)
            {
                var sample = InputSample.FromHeld(
                    (ulong)(500 + index),
                    index < 100 ? TasAction.None : TasAction.Right,
                    index == 100 ? TasAction.None : index <= 100
                        ? TasAction.None
                        : TasAction.Right);
                var append = journal.Append(
                    sample,
                    new TickStamp(
                        (ulong)(500 + index),
                        index,
                        index,
                        0,
                        TickPhase.InControlCommitted));
                Assert.IsTrue(append.Success, append.Error);
                Assert.AreEqual(index, append.MovieTick);
            }

            var first = journal.CommitThrough(255);
            Assert.HasCount(256, first.Records);
            journal.AcknowledgePersistedThrough(255);
            Assert.AreEqual(44, journal.RetainedCount);
            Assert.AreEqual(255, journal.LastPersistedMovieTick);
            var second = journal.CommitThrough(299);
            Assert.HasCount(44, second.Records);
        }

        [TestMethod]
        public void ReplayJournal_SuspensionAllowsRawTickJumpButOtherGapsPoisonJournal()
        {
            var journal = new ReplayJournal("baseline", Baseline);
            Assert.IsTrue(journal.Append(Neutral(10), Stamp(10)).Success);
            journal.Suspend();
            Assert.IsTrue(journal.Append(Neutral(99), Stamp(99)).Success);

            var gap = journal.Append(Neutral(101), Stamp(101));
            Assert.IsFalse(gap.Success);
            Assert.IsTrue(journal.HasGap);
            Assert.IsFalse(journal.Append(Neutral(102), Stamp(102)).Success);
        }

        [TestMethod]
        public void ReplayJournal_RejectsWrongPhaseTickMismatchAndPersistenceFailure()
        {
            var wrongPhase = new ReplayJournal("baseline", Baseline);
            Assert.IsFalse(
                wrongPhase.Append(
                    Neutral(1),
                    new TickStamp(1, 0, 0, 0, TickPhase.LateUpdateEnd))
                    .Success);

            var mismatch = new ReplayJournal("baseline", Baseline);
            Assert.IsFalse(mismatch.Append(Neutral(1), Stamp(2)).Success);

            var persistence = new ReplayJournal("baseline", Baseline);
            Assert.IsTrue(persistence.Append(Neutral(1), Stamp(1)).Success);
            persistence.MarkPersistenceFailure("disk full");
            Assert.IsTrue(persistence.HasGap);
        }

        [TestMethod]
        public void InputRecorder_CreatesCanonicalMovieAndRejectsGap()
        {
            var recorder = new InputRecorder();
            foreach (var sample in BuildEdgeSamples())
            {
                Assert.IsTrue(recorder.Append(sample));
            }

            var movie = recorder.CreateMovie(Header(), "recorded.hktas");
            var text = new MovieCanonicalWriter().WriteToString(movie);
            StringAssert.Contains(text, "frames 2 hold=right");
            StringAssert.Contains(text, "frames 2 hold=jump x=2500 y=-5000");

            var gap = new InputRecorder();
            Assert.IsTrue(gap.Append(Neutral(1)));
            Assert.IsFalse(gap.Append(Neutral(3)));
            Assert.IsTrue(gap.HasGap);
            Assert.ThrowsExactly<InvalidOperationException>(
                () => gap.CreateMovie(Header()));
        }

        [TestMethod]
        public void RouteFixtures_ParseValidateAndEditOnlyInitialWait()
        {
            var original = ParseMovie("p0-recording.hktas");
            var edited = ParseMovie("p0-edited.hktas");
            var validator = new MovieValidator();
            Assert.IsTrue(
                validator.Validate(
                    original,
                    new MovieValidationContext(
                        1000,
                        MovieProtocolV1.DefaultSemanticPaths,
                        Manifest,
                        Baseline))
                    .Success);
            Assert.IsTrue(
                validator.Validate(
                    edited,
                    new MovieValidationContext(
                        1000,
                        MovieProtocolV1.DefaultSemanticPaths,
                        Manifest,
                        Baseline))
                    .Success);

            var originalRuns = original.Commands.OfType<FrameRunCommand>().ToArray();
            var editedRuns = edited.Commands.OfType<FrameRunCommand>().ToArray();
            Assert.HasCount(3, originalRuns);
            Assert.HasCount(3, editedRuns);
            Assert.AreEqual(10L, originalRuns[0].FrameCount);
            Assert.AreEqual(20L, editedRuns[0].FrameCount);
            Assert.AreEqual(originalRuns[1].FrameCount, editedRuns[1].FrameCount);
            Assert.AreEqual(originalRuns[2].FrameCount, editedRuns[2].FrameCount);
            Assert.AreEqual(120L, originalRuns[2].FrameCount);
        }

        private static List<InputSample> BuildEdgeSamples()
        {
            var held = new[]
            {
                TasAction.None,
                TasAction.Right,
                TasAction.Right,
                TasAction.None,
                TasAction.Jump,
                TasAction.Jump,
                TasAction.None
            };
            var axisX = new short[] { 0, 0, 0, 0, 2500, 2500, 0 };
            var axisY = new short[] { 0, 0, 0, 0, -5000, -5000, 0 };
            var result = new List<InputSample>();
            var previous = TasAction.None;
            for (var index = 0; index < held.Length; index++)
            {
                result.Add(
                    InputSample.FromHeld(
                        (ulong)index,
                        held[index],
                        previous,
                        axisX[index],
                        axisY[index]));
                previous = held[index];
            }

            return result;
        }

        private static InputSample Neutral(ulong tick)
        {
            return InputSample.FromHeld(tick, TasAction.None, TasAction.None);
        }

        private static TickStamp Stamp(ulong tick)
        {
            return new TickStamp(
                tick,
                checked((long)tick),
                checked((long)tick),
                0,
                TickPhase.InControlCommitted);
        }

        private static MovieDocument ParseMovie(string name)
        {
            var path = Path.Combine(
                AppContext.BaseDirectory,
                "fixtures",
                "movie",
                "generated",
                name);
            using var reader = new StreamReader(
                path,
                new UTF8Encoding(false, true),
                false);
            var result = new MovieParser().Parse(reader, path);
            Assert.IsTrue(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics));
            return result.Document!;
        }

        private static MovieHeader Header()
        {
            return new MovieHeader(
                1,
                "1.5.78.11833",
                "1.5.78.11833-77",
                Manifest,
                "slot-2-gg-vengefly",
                Baseline,
                "input");
        }
    }
}
