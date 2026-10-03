using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class PooledAudioLifetimeTests
    {
        [TestMethod]
        public void SourcePausePreservesRemainingVoiceAndSnapshotDoesNotAliasLiveState()
        {
            var clock = new PooledAudioLifetime(0);
            clock.Play(2, false, false);
            clock.Advance(0.5, 1, false);
            clock.SetPaused(true);
            var snapshot = clock.Capture();
            clock.Advance(100, 1, false);
            Assert.IsFalse(clock.IsPlaying);
            Assert.AreEqual(1.5, clock.Capture().Voices[0].Remaining);
            clock.SetPaused(false);
            clock.Advance(101.5, 1, false);
            Assert.IsFalse(clock.IsPlaying);
            Assert.AreEqual(0.5, snapshot.LastTime);
            Assert.IsTrue(snapshot.SourcePaused);
            Assert.AreEqual(1.5, snapshot.Voices[0].Remaining);
            Assert.AreEqual(0, clock.Capture().Voices.Length);
        }

        [TestMethod]
        public void PausedTasDoesNotExpireSoundAndOverlapWaitsForLongestVoice()
        {
            var clock = new PooledAudioLifetime(10);
            clock.Play(1, false, true);
            clock.Advance(10.5, 1, false);
            clock.Play(2, false, true);
            clock.Advance(10.5, 1, false);
            Assert.IsTrue(clock.IsPlaying);
            clock.Advance(11, 1, false);
            Assert.IsTrue(clock.IsPlaying);
            clock.Advance(12.5, 1, false);
            Assert.IsFalse(clock.IsPlaying);
        }

        [TestMethod]
        public void PitchAndListenerPauseChangeConsumedDurationWithoutWallClock()
        {
            var clock = new PooledAudioLifetime(0);
            clock.Play(1, false, true);
            clock.Advance(0.25, 2, false);
            clock.Advance(100, 2, true);
            Assert.IsTrue(clock.IsPlaying);
            clock.Advance(100.25, 2, false);
            Assert.IsFalse(clock.IsPlaying);
        }

        [TestMethod]
        public void RestartingMainClipPreservesOneShotsAndStopClearsLoops()
        {
            var clock = new PooledAudioLifetime(0);
            clock.Play(2, false, true);
            clock.Play(10, false, false);
            clock.Play(1, false, false);
            clock.Advance(1, 1, false);
            Assert.IsTrue(clock.IsPlaying);
            clock.Advance(2, 1, false);
            Assert.IsFalse(clock.IsPlaying);
            clock.Play(1, true, false);
            clock.Advance(1000, 1, false);
            Assert.IsTrue(clock.IsPlaying);
            clock.Stop();
            Assert.IsFalse(clock.IsPlaying);
        }
    }
}
