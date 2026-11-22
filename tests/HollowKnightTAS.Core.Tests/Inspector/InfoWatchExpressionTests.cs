using System;
using HollowKnightTAS.Core.Inspector;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.Inspector
{
    [TestClass]
    public sealed class InfoWatchExpressionTests
    {
        private static object? Eval(string expression) => InfoWatchExpression.Parse(expression).Evaluate(
            q => q.Root == "fsm" ? 33 : q.Read(new State()),
            key => key switch
            {
                "health" => 5,
                "maxHealth" => 10,
                "grounded" => true,
                "tft" => 16.6667d,
                "rng" => "1234 +5",
                "rngRolls" => 1234L,
                "rngDelta" => 5,
                _ => null
            });
        private sealed class State { public int geo = 100; public int[] values = { 4, 7 }; }
        [TestMethod]
        [DataRow("1 + 2 * 3", 7d)]
        [DataRow("(1 + 2) * -3", -9d)]
        [DataRow("health / maxHealth * 100", 50d)]
        [DataRow("tft * 1", 16.6667d)]
        [DataRow("rngRolls + rngDelta", 1239d)]
        [DataRow("player.geo - 12.5", 87.5d)]
        [DataRow("player.values[1] % 3 + 1e-2", 1.01d)]
        [DataRow("fsm(\"/Knight\", \"Spell Control\", \"MP Cost\") + 2", 35d)]
        [DataRow("component(\"/Enemy(Clone)\", \"Type\").geo / 4", 25d)]
        public void ComputesPrecedenceAndLegacyPaths(string expression, double expected) => Assert.AreEqual(expected, Eval(expression));
        [TestMethod]
        public void ShortCircuitsAndConcatenatesText()
        {
            Assert.AreEqual(false, Eval("false && (1 / 0 > 1)"));
            Assert.AreEqual(true, Eval("grounded || hero.missing > 0"));
            Assert.AreEqual("5 / 10", Eval("health + \" / \" + maxHealth"));
            Assert.AreEqual("1234 +5", Eval("rng"));
            Assert.AreEqual("ready", Eval("health <= 5 && !false ? \"ready\" : hero.missing"));
            Assert.AreEqual(true, Eval("player.geo == 100"));
            Assert.AreEqual(true, Eval("\"text\" != false"));
        }
        [TestMethod]
        [DataRow("1 / 0")]
        [DataRow("1 % 0")]
        [DataRow("1e308 * 10")]
        [DataRow("room + 1")]
        [DataRow("true + 1")]
        [DataRow("health && true")]
        public void RejectsUnavailableOrInvalidValues(string expression) => Assert.ThrowsExactly<InvalidOperationException>(() => Eval(expression));
        [TestMethod]
        [DataRow("hero.Mutate() + 1")]
        [DataRow("health = 5")]
        [DataRow("System.Environment.Exit(0)")]
        [DataRow("health +")]
        [DataRow("player.values[-1]")]
        [DataRow("1e999")]
        [DataRow("health; maxHealth")]
        public void RejectsMutationAndMalformedExpressions(string expression) => Assert.ThrowsExactly<FormatException>(() => InfoWatchExpression.Parse(expression));
        [TestMethod]
        public void BoundsLengthAndNesting()
        {
            Assert.ThrowsExactly<FormatException>(() => InfoWatchExpression.Parse(new string(' ', 512) + "1"));
            Assert.ThrowsExactly<FormatException>(() => InfoWatchExpression.Parse(new string('(', 40) + "1" + new string(')', 40)));
            Assert.ThrowsExactly<FormatException>(() => InfoWatchExpression.Parse(new string('!', 40) + "true"));
        }
    }
}
