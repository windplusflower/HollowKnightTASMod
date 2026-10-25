using System;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text;
using HollowKnightTAS.Core.Cryptography;
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
        public void ActualSuffixPayloadRetainsIndependentFieldsWithGlobalDigestAndMetadata()
        {
            var independent = new[] { new { field = "activeState", owner = "Fsm", digest = new string('a', 64) },
                new { field = "states", owner = "Fsm", digest = new string('b', 64) } };
            var encoded = new LoadingInventoryRootEncoding(new object[] { new { id = "fixed-marker", type = "Fsm",
                digest = "unchanged-global-shared-graph", fieldDigests = independent } });
            var payload = JObject.FromObject(encoded.EncodeSuffix(new { markerIncomingReferences = new[] { "physical-owner/slot" } }));
            using var input = new MemoryStream(Convert.FromBase64String((string)payload["gzipBase64"]!));
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream(); gzip.CopyTo(output);
            var bytes = output.ToArray();
            Assert.AreEqual((int)payload["bytes"]!, bytes.Length);
            Assert.AreEqual((string)payload["sha256"]!, Sha256Utility.ComputeHex(bytes));
            var artifact = JObject.Parse(Encoding.UTF8.GetString(bytes));
            Assert.AreEqual(LoadingInventoryRootEncoding.Format, (string)artifact["format"]!);
            Assert.AreEqual(0, (int)artifact["roots"]![0]!["id"]!);
            Assert.AreEqual("unchanged-global-shared-graph", (string)artifact["roots"]![0]!["digest"]!);
            Assert.IsTrue(JToken.DeepEquals(JArray.FromObject(independent), artifact["roots"]![0]!["fieldDigests"]));
            Assert.AreEqual("physical-owner/slot", (string)artifact["markerIncomingReferences"]![0]!);
            Assert.ThrowsExactly<InvalidOperationException>(() => encoded.EncodeSuffix(new { roots = "overlap" }));
        }

        [TestMethod]
        public void StreamingSuffixIsByteIdenticalAndDoesNotReparentReusableTokens()
        {
            var metadata = new { text = "中文🧵\n\"\\\t", negativeZero = -0.0d,
                number = double.NaN, nested = new { empty = (string?)null, values = new[] { -1, 0, 3 } } };
            var encoded = new LoadingInventoryRootEncoding(new object[] {
                new { id = "场景/🧵[0]", type = "类型", digest = "\"\\\n", bits = -0.0f },
                new { id = "场景/🧵[0]", type = "类型", digest = "duplicate", flags = new[] { true, false } } });
            var legacy = new JObject { ["format"] = LoadingInventoryRootEncoding.Format,
                ["names"] = JArray.FromObject(encoded.Names), ["types"] = JArray.FromObject(encoded.Types), ["roots"] = encoded.Roots.DeepClone() };
            foreach (var property in JObject.FromObject(metadata).Properties()) legacy.Add(property.Name, property.Value.DeepClone());
            var expected = Encoding.UTF8.GetBytes(legacy.ToString(Formatting.None));
            var before = encoded.Roots.ToString(Formatting.None);
            for (var pass = 0; pass < 2; pass++) {
                var payload = JObject.FromObject(encoded.EncodeSuffix(metadata));
                using var input = new MemoryStream(Convert.FromBase64String((string)payload["gzipBase64"]!));
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(); gzip.CopyTo(output);
                CollectionAssert.AreEqual(expected, output.ToArray());
                Assert.AreEqual(Sha256Utility.ComputeHex(expected), (string)payload["sha256"]!);
                Assert.IsNull(encoded.Roots.Parent);
                Assert.AreEqual(before, encoded.Roots.ToString(Formatting.None));
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => encoded.EncodeSuffix(new { huge = new string('x', 16 * 1024 * 1024) }));
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
