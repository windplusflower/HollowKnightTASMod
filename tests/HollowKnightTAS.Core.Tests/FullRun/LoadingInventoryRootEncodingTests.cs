using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LoadingInventoryRootEncodingTests
    {
        [TestMethod]
        public void PrefixTablesPreserveDuplicateRecordsTypesAndEveryValue()
        {
            object[] roots = {
                new { id = "场景/🧵[0]/FSM/state[2]/action[1]", type = "Action", digest = "a", nodes = 3 },
                new { id = "场景/🧶[0]/FSM/state[2]/action[0]", type = "Action", digest = "b", nodes = 4 },
                new { id = "场景/🧵[0]/FSM/state[2]/action[1]", type = "Other", digest = "c", nodes = 5 },
                new { id = "global", type = (string?)null, digest = "d", nodes = 1 }
            };
            var encoded = new LoadingInventoryRootEncoding(roots);
            var names = new System.Collections.Generic.List<string>();
            var previous = "";
            foreach (var item in JArray.FromObject(encoded.Names))
            {
                previous = previous.Substring(0, (int)item["prefix"]!) + (string)item["suffix"]!;
                names.Add(previous);
            }
            var decoded = (JArray)encoded.Roots.DeepClone();
            foreach (JObject root in decoded)
            {
                root["id"] = names[(int)root["id"]!];
                if (root["type"]?.Type == JTokenType.Integer) root["type"] = encoded.Types[(int)root["type"]!];
            }
            var original = JArray.FromObject(roots, JsonSerializer.Create(new JsonSerializerSettings
                { NullValueHandling = NullValueHandling.Ignore }));
            Assert.IsTrue(JToken.DeepEquals(original, decoded));
            Assert.AreEqual(3, names.Count);
            Assert.AreEqual(4, decoded.Count);
        }

        [TestMethod]
        public void InvalidDiagnosticRootFailsRatherThanDroppingIt()
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new LoadingInventoryRootEncoding(
                new object[] { new { type = "missing-id" } }));
            Assert.ThrowsExactly<InvalidOperationException>(() => new LoadingInventoryRootEncoding(
                new object[] { new { id = "x", type = 42 } }));
        }
    }
}
