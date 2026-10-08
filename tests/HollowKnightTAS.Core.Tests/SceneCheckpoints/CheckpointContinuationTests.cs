using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Core.SceneCheckpoints;
using HollowKnightTAS.Runtime.FullRun;
using InControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints
{
    [TestClass]
    public sealed class CheckpointContinuationTests
    {
        [TestMethod]
        public void GameplayRenderAndSceneSnapshotWordsRetainAllBitsAndNoMutableAliases()
        {
            var words = new[] { 0, -1, int.MinValue, int.MaxValue, 11, 12, 13, 14, 21, 22, 23, 24 };
            var state = new CheckpointRandomState(words, true, true, 4, 3, -2147483647, 2345, "full-run-scene", "GG_Marmu");
            var bytes = state.Encode(); words[0] = 6;
            var decoded = CheckpointRandomState.Decode(bytes); var copy = decoded.CopyWords(); copy[0] = 7;
            CollectionAssert.AreEqual(bytes, decoded.Encode()); Assert.AreEqual(0, decoded.CopyWords()[0]);
            Assert.AreEqual(4, decoded.ResetCount); Assert.AreEqual(3, decoded.SceneEpoch);
            Assert.AreEqual(-2147483647, decoded.LastSeed); Assert.AreEqual(2345, decoded.AdvanceSequence);
            Assert.AreEqual("GG_Marmu", decoded.Scene);
        }
        [TestMethod]
        public void RandomContinuationRejectsCorruptFlagsUnknownSchemaTruncationAndEpoch()
        {
            var bytes = new CheckpointRandomState(new int[12], false, false, 0, 0, 0, 0, "", "").Encode();
            var version = (byte[])bytes.Clone(); version[0] = 2;
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointRandomState.Decode(version));
            var flags = (byte[])bytes.Clone(); flags[52] = 2;
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointRandomState.Decode(flags));
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointRandomState.Decode(bytes.Take(bytes.Length - 1).ToArray()));
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointRandomState.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
            Assert.ThrowsExactly<InvalidDataException>(() => new CheckpointRandomState(new int[12], false, false, 2, 3, 0, 0, "", ""));
        }
        [TestMethod]
        public void RestoredKeyboardRetainsHeldNewPressAndReleaseAcrossOneThousandFrames()
        {
            GameInputSample Held(short key) => new GameInputSample(GameInputChannel.CustomKey, new[] { key, (short)1 }, null);
            var source = new CustomKeyState(); source.Prepare(new[] { Held(97) }); source.Complete();
            source.Prepare(new[] { Held(97), Held(98) });
            var target = new CustomKeyState(); target.Restore(source.CapturePrevious(), source.CaptureCurrent());
            Assert.IsTrue(target.Held(97)); Assert.IsFalse(target.Down(97)); Assert.IsTrue(target.Down(98));
            for (var frame = 0; frame < 1000; frame++)
            {
                source.Complete(); target.Complete();
                var samples = frame % 3 == 0 ? new[] { Held(98) } : frame % 3 == 1 ? new[] { Held(97), Held(98) } : Array.Empty<GameInputSample>();
                source.Prepare(samples); target.Prepare(samples);
                foreach (short key in new short[] { 97, 98 })
                { Assert.AreEqual(source.Held(key), target.Held(key)); Assert.AreEqual(source.Down(key), target.Down(key)); Assert.AreEqual(source.Up(key), target.Up(key)); }
            }
            Assert.ThrowsExactly<InvalidDataException>(() => target.Restore(new short[] {97,97}, new short[] {98}));
        }
        [TestMethod]
        public void ActualInControlPrivateStateTransfersEdgesRepeatTimerAndTicksWithoutRunningUpdate()
        {
            #pragma warning disable SYSLIB0050
            var source = (OneAxisInputControl)FormatterServices.GetUninitializedObject(typeof(OneAxisInputControl));
            var target = (OneAxisInputControl)FormatterServices.GetUninitializedObject(typeof(OneAxisInputControl));
            #pragma warning restore SYSLIB0050
            var fields = CheckpointScalarFields.Declared(typeof(OneAxisInputControl));
            fields.Single(f => f.Name == "lastState").SetValue(source, new InputControlState { State = false, Value = 0, RawValue = 0 });
            fields.Single(f => f.Name == "thisState").SetValue(source, new InputControlState { State = true, Value = .75f, RawValue = 1 });
            fields.Single(f => f.Name == "nextState").SetValue(source, new InputControlState { State = true, Value = .75f, RawValue = 1 });
            fields.Single(f => f.Name == "nextRepeatTime").SetValue(source, 78.5f);
            fields.Single(f => f.Name == "pendingTick").SetValue(source, ulong.MaxValue - 1);
            fields.Single(f => f.Name == "enabled").SetValue(source, true);
            fields.Single(f => f.Name == "ownerEnabled").SetValue(source, true);
            var state = CheckpointScalarFields.Capture(source, fields);
            var apply = CheckpointScalarFields.PrepareApply(target, fields, state);
            Assert.IsFalse(target.WasPressed); apply();
            Assert.AreEqual(source.WasPressed, target.WasPressed); Assert.IsTrue(target.WasPressed);
            Assert.AreEqual(source.WasReleased, target.WasReleased); Assert.AreEqual(source.Value, target.Value);
            CollectionAssert.AreEqual(state.OrderBy(p => p.Key).ToArray(), CheckpointScalarFields.Capture(target, fields).OrderBy(p => p.Key).ToArray());
            var missing = new Dictionary<string, string>(state); missing.Remove(missing.Keys.First());
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointScalarFields.PrepareApply(target, fields, missing));
            var wrong = new Dictionary<string, string>(state); wrong[wrong.Keys.First()] = "bad";
            Assert.ThrowsExactly<InvalidDataException>(() => CheckpointScalarFields.PrepareApply(target, fields, wrong));
        }
    }
}
