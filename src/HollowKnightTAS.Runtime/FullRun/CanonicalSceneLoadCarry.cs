using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalSceneLoadCarry
    {
        internal const string Root = "D/SceneLoad-continuation";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Packet), typeof(Packet[]) };
        internal sealed class Data
        {
            public int Version = 1;
            public string SourceScene = "", TargetScene = "", IteratorType = "", ClosureType = "";
            public bool TransitionIteratorFinished;
            public Packet[] Packets = Array.Empty<Packet>();
        }
        internal sealed class Packet { public string Group = ""; public string[] Fields = Array.Empty<string>(), Values = Array.Empty<string>(); }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var owner = view.Root(Root); var type = typeof(Data);
            var result = new Data { Version = (int)view.Field(owner, type, nameof(Data.Version))!,
                SourceScene = (string)view.Field(owner, type, nameof(Data.SourceScene))!, TargetScene = (string)view.Field(owner, type, nameof(Data.TargetScene))!,
                IteratorType = (string)view.Field(owner, type, nameof(Data.IteratorType))!, ClosureType = (string)view.Field(owner, type, nameof(Data.ClosureType))!,
                TransitionIteratorFinished = (bool)view.Field(owner, type, nameof(Data.TransitionIteratorFinished))!,
                Packets = (Packet[])view.Field(owner, type, nameof(Data.Packets))! };
            Validate(result);
            if (!Equals(view.Field(view.Root("D/SceneLoadInfo"), typeof(GameManager.SceneLoadInfo), "SceneName"), result.TargetScene))
                throw new InvalidDataException("Canonical source SceneLoadInfo and continuation target disagree.");
            return Capture(result.SourceScene, result.TargetScene, Decode(result));
        }
        private static Packet Pack(string group, IReadOnlyDictionary<string, string> values)
        {
            var pairs = values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            return new Packet { Group = group, Fields = pairs.Select(pair => pair.Key).ToArray(), Values = pairs.Select(pair => pair.Value).ToArray() };
        }
        internal static Data Capture(string source, string target, CheckpointSceneLoadState state)
        {
            var data = new Data { SourceScene = source, TargetScene = target, IteratorType = state.IteratorType, ClosureType = state.ClosureType,
                TransitionIteratorFinished = state.TransitionIteratorFinished,
                Packets = new[] { Pack("load", state.LoadFields), Pack("iterator", state.IteratorFields), Pack("closure", state.ClosureFields) }
                    .Concat(state.Phases.Select((values, index) => Pack("phase/" + index, values))).ToArray() };
            Validate(data); return data;
        }
        internal static CheckpointSceneLoadState Decode(Data data)
        {
            Validate(data);
            Dictionary<string, string> Fields(Packet packet) => packet.Fields.Select((key, index) => new { key, value = packet.Values[index] })
                .ToDictionary(pair => pair.key, pair => pair.value, StringComparer.Ordinal);
            return new CheckpointSceneLoadState { TransitionIteratorFinished = true, IteratorType = data.IteratorType, ClosureType = data.ClosureType,
                LoadFields = Fields(data.Packets[0]), IteratorFields = Fields(data.Packets[1]), ClosureFields = Fields(data.Packets[2]),
                Phases = data.Packets.Skip(3).Select(Fields).ToArray() };
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.Version != 1 || !data.TransitionIteratorFinished || data.Packets == null || data.Packets.Length != 11
                || data.IteratorType != "GameManager+<BeginSceneTransitionRoutine>d__174" || data.ClosureType != "GameManager+<>c__DisplayClass174_0")
                throw new InvalidDataException("Canonical SceneLoad continuation owner, phase count or type differs.");
            foreach (var name in new[] { data.SourceScene, data.TargetScene })
                if (name == null || name.Length == 0 || name.Length > 256 || name.IndexOf('\0') >= 0)
                    throw new InvalidDataException("Canonical SceneLoad scene identity differs.");
            for (var index = 0; index < data.Packets.Length; index++) {
                var packet = data.Packets[index]; var expected = index == 0 ? "load" : index == 1 ? "iterator" : index == 2 ? "closure" : "phase/" + (index - 3);
                if (packet == null || packet.Group != expected || packet.Fields == null || packet.Values == null
                    || packet.Fields.Length == 0 || packet.Fields.Length > 64 || packet.Fields.Length != packet.Values.Length
                    || !packet.Fields.SequenceEqual(packet.Fields.Distinct().OrderBy(field => field, StringComparer.Ordinal)))
                    throw new InvalidDataException("Canonical SceneLoad scalar packet layout differs.");
                foreach (var value in packet.Fields.Concat(packet.Values))
                    if (value == null || value.Length > 16384 || value.IndexOf('\0') >= 0)
                        throw new InvalidDataException("Canonical SceneLoad scalar packet text exceeds its bound.");
            }
            var target = Array.IndexOf(data.Packets[0].Fields, "SceneLoad/targetSceneName");
            var source = Array.IndexOf(data.Packets[2].Fields, data.ClosureType + "/lastSceneName");
            if (target < 0 || data.Packets[0].Values[target] != "string:" + data.TargetScene
                || source < 0 || data.Packets[2].Values[source] != "string:" + data.SourceScene)
                throw new InvalidDataException("Canonical SceneLoad closure/target scene aliases differ.");
        }
    }
}
