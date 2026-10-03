using System;
using HollowKnightTAS.Core.Movie;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Movie
{
    [TestClass]
    public sealed class SessionAchievementsTests
    {
        [TestMethod]
        public void SessionsStartLockedAndDoNotShareChanges()
        {
            var first = new SessionAchievements();
            var second = new SessionAchievements();
            Assert.IsFalse(first.IsUnlocked("ENDING_A"));
            first.Unlock("ENDING_A");
            first.Unlock("ENDING_A");
            Assert.IsTrue(first.IsUnlocked("ENDING_A"));
            Assert.IsFalse(second.IsUnlocked("ENDING_A"));
            Assert.AreEqual(1, first.Capture().Length);
        }

        [TestMethod]
        public void CaptureIsAnOrderedCopyAndClearResetStayLocal()
        {
            var state = new SessionAchievements();
            state.Unlock("B"); state.Unlock("A");
            var copy = state.Capture();
            CollectionAssert.AreEqual(new[] { "A", "B" }, copy);
            copy[0] = "C";
            Assert.IsTrue(state.IsUnlocked("A"));
            state.Clear("A");
            CollectionAssert.AreEqual(new[] { "B" }, state.Capture());
            state.Reset();
            Assert.AreEqual(0, state.Capture().Length);
        }

        [TestMethod]
        public void InvalidOrUnboundedStateFails()
        {
            var state = new SessionAchievements();
            Assert.ThrowsExactly<ArgumentException>(() => state.Unlock(""));
            Assert.ThrowsExactly<ArgumentException>(() => state.IsUnlocked(new string('x', 257)));
            for (int i = 0; i < 4096; i++) state.Unlock(i.ToString());
            state.Unlock("0");
            Assert.ThrowsExactly<InvalidOperationException>(() => state.Unlock("overflow"));
            Assert.AreEqual(4096, state.Capture().Length);
        }
    }
}
