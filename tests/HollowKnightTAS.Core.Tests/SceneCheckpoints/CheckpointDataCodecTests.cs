using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HollowKnightTAS.Core.Tests.SceneCheckpoints
{
    [TestClass]
    public sealed class CheckpointDataCodecTests
    {
        [Serializable]
        public sealed class Data
        {
            public float Float;
            public double Double;
            public int Number;
            public string? Text;
            public List<int>? Items;
            public List<int>? Alias;
            public Dictionary<string, int>? Map;
            private int hidden = 17;
            public int ReadHidden() => hidden;
            public static int Effects;
            public Data() { Effects++; }
            public int Property => throw new InvalidOperationException("A live property must not be read.");
            [OnSerializing] internal void OnSerializing(StreamingContext context) { Effects++; }
            [OnDeserialized] internal void OnDeserialized(StreamingContext context) { Effects++; }
        }
        private static CheckpointDataCodec Codec() => new CheckpointDataCodec(new[] { typeof(Data) });
        [TestMethod]
        public void RawFloatBitsPrivateFieldsNullAliasesAndDataRoundTripWithoutLifecycleEffects()
        {
            var list = new List<int> { 1, 6, 40 };
            var value = new Data { Float = BitConverter.ToSingle(BitConverter.GetBytes(0x7fc01234U), 0),
                Double = BitConverter.ToDouble(BitConverter.GetBytes(0x8000000000000000UL), 0), Items = list, Alias = list,
                Map = new Dictionary<string, int> { ["门"] = 3 }, Number = -7 };
            Data.Effects = 0; var codec = Codec(); var bytes = codec.Encode(value); var decoded = codec.Decode<Data>(bytes);
            Assert.AreEqual(0, Data.Effects); Assert.AreEqual(0x7fc01234U, BitConverter.ToUInt32(BitConverter.GetBytes(decoded.Float), 0));
            Assert.AreEqual(0x8000000000000000UL, BitConverter.ToUInt64(BitConverter.GetBytes(decoded.Double), 0));
            Assert.AreSame(decoded.Items, decoded.Alias); Assert.AreEqual(17, decoded.ReadHidden()); Assert.IsNull(decoded.Text);
            CollectionAssert.AreEqual(bytes, codec.Encode(decoded));
            value.Items!.Add(99); value.Number = 100; Assert.AreEqual(3, decoded.Items!.Count); Assert.AreEqual(-7, decoded.Number);
        }
        [TestMethod]
        public void MissingExtraDuplicateFieldsWrongFloatEncodingAndCustomComparerAreRejected()
        {
            var codec = Codec(); var bytes = codec.Encode(new Data());
            var json = JObject.Parse(Encoding.UTF8.GetString(bytes)); json.Remove("Number");
            Assert.Throws<JsonSerializationException>(() => codec.Decode<Data>(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
            json = JObject.Parse(Encoding.UTF8.GetString(bytes)); json["injected"] = 1;
            Assert.Throws<JsonSerializationException>(() => codec.Decode<Data>(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
            var duplicate = Encoding.UTF8.GetString(bytes).Replace("\"Number\":0", "\"Number\":0,\"Number\":0");
            Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode<Data>(Encoding.UTF8.GetBytes(duplicate)));
            json = JObject.Parse(Encoding.UTF8.GetString(bytes)); json["Float"] = 0;
            Assert.ThrowsExactly<InvalidDataException>(() => codec.Decode<Data>(Encoding.UTF8.GetBytes(json.ToString(Formatting.None))));
            Assert.ThrowsExactly<InvalidDataException>(() => codec.Encode(new Data { Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) }));
        }
        [Serializable] private sealed class Nonportable { public IntPtr Address; }
        [TestMethod]
        public void ProcessAddressesAreRejectedInsteadOfBeingSerialized()
        {
            var codec = new CheckpointDataCodec(new[] { typeof(Nonportable) });
            Assert.ThrowsExactly<InvalidDataException>(() => codec.Encode(new Nonportable { Address = new IntPtr(123) }));
        }
        [TestMethod]
        public void InstalledPlayerDataSceneDataAndLoadInfoFieldsRoundTripAsDetachedData()
        {
            var codec = new CheckpointDataCodec(new[] { typeof(PlayerData), typeof(SceneData), typeof(GameManager.SceneLoadInfo) }, new[] { typeof(UnityEngine.Vector3) });
#pragma warning disable SYSLIB0050
            var player = (PlayerData)FormatterServices.GetUninitializedObject(typeof(PlayerData));
            var scene = (SceneData)FormatterServices.GetUninitializedObject(typeof(SceneData));
            var info = (GameManager.SceneLoadInfo)FormatterServices.GetUninitializedObject(typeof(GameManager.SceneLoadInfo));
#pragma warning restore SYSLIB0050
            var shared = new List<string> { "Crossroads_01", "GG_Atrium" };
            Set(player, "health", 7); Set(player, "scenesVisited", shared); Set(player, "scenesMapped", shared);
            Set(player, "hazardRespawnLocation", new UnityEngine.Vector3(BitConverter.ToSingle(BitConverter.GetBytes(0x80000000U), 0), 2.5f, 0));
            var mapField = typeof(PlayerData).GetField("mapZoneBools", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var map = (IDictionary)Activator.CreateInstance(mapField.FieldType)!;
            map.Add("Crossroads", Enum.ToObject(mapField.FieldType.GetGenericArguments()[1], 1)); mapField.SetValue(player, map);
            foreach (var field in typeof(SceneData).GetFields(BindingFlags.Instance | BindingFlags.Public))
                if (field.FieldType.IsGenericType) field.SetValue(scene, Activator.CreateInstance(field.FieldType));
            Set(info, "SceneName", "GG_Atrium"); Set(info, "EntryGateName", "door1"); Set(info, "EntryDelay", BitConverter.ToSingle(BitConverter.GetBytes(0x80000000U), 0));
            var playerBytes = codec.Encode(player); var sceneBytes = codec.Encode(scene); var infoBytes = codec.Encode(info);
            var copiedPlayer = codec.Decode<PlayerData>(playerBytes); var copiedScene = codec.Decode<SceneData>(sceneBytes); var copiedInfo = codec.Decode<GameManager.SceneLoadInfo>(infoBytes);
            CollectionAssert.AreEqual(playerBytes, codec.Encode(copiedPlayer)); CollectionAssert.AreEqual(sceneBytes, codec.Encode(copiedScene)); CollectionAssert.AreEqual(infoBytes, codec.Encode(copiedInfo));
            Assert.AreEqual(7, typeof(PlayerData).GetField("health")!.GetValue(copiedPlayer));
            Assert.AreSame(typeof(PlayerData).GetField("scenesVisited")!.GetValue(copiedPlayer), typeof(PlayerData).GetField("scenesMapped")!.GetValue(copiedPlayer));
        }
        private static void Set(object value, string name, object data) => value.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(value, data);
    }
}
