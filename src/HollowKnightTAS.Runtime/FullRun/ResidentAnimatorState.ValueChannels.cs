using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        internal sealed class ValueIntegerChannels
        {
            public byte[] Schema = Array.Empty<byte>();
            public byte[] Ordinary = Array.Empty<byte>();
            public bool ControllerParameters;
            public string[] Objects = Array.Empty<string>();
        }

        private sealed class ValueChannelSchema
        {
            internal readonly byte[] Bytes;
            internal readonly int Ordinary;
            internal readonly int Objects;
            internal readonly bool Parameters;

            internal ValueChannelSchema(byte[] bytes, int ordinary, int objects, bool parameters)
            {
                BankSize(bytes, 12);
                if (ordinary < 0 || objects < 0 || ordinary > MaximumEntries - objects || parameters && objects != 0)
                    throw Error("integer channel schema extent differs");

                var indices = new HashSet<uint>();
                for (int offset = 0; offset < bytes.Length; offset += 12)
                {
                    if (M.U32(bytes, offset + 4) != 3) continue;
                    var index = M.U32(bytes, offset + 8);
                    if (index >= ordinary + objects || !indices.Add(index))
                        throw Error("integer channel schema index differs");
                }
                if (indices.Count != ordinary + objects)
                    throw Error("integer channel schema is not dense and complete");

                Bytes = bytes;
                Ordinary = ordinary;
                Objects = objects;
                Parameters = parameters;
            }

            internal bool Same(ValueChannelSchema other)
                => Ordinary == other.Ordinary && Objects == other.Objects && Parameters == other.Parameters
                    && Bytes.SequenceEqual(other.Bytes);
        }

        private static ValueChannelSchema? AnimationChannelSchema(Backing backing)
        {
            if (!backing.ControllerPresent || !backing.SetPresent || backing.SetDependency == null) return null;
            return new ValueChannelSchema(backing.ParameterSchema, backing.Tables[1].Length, backing.Tables[2].Length, false);
        }

        private static ValueChannelSchema ParameterChannelSchema(ControllerRuntime runtime)
        {
            int ordinary = 0;
            for (int offset = 0; offset < runtime.Parameters.Length; offset += 12)
            {
                if (M.U32(runtime.Parameters, offset + 4) == 3) ordinary++;
            }
            return new ValueChannelSchema(runtime.Parameters, ordinary, 0, true);
        }

        // All assignments follow typed owner edges. A shared native allocation
        // receives exactly one full schema/classification, even across domains.
        private static Dictionary<int, ValueChannelSchema> ValueChannelOwners(Data data)
        {
            var result = new Dictionary<int, ValueChannelSchema>();
            var nodes = data.AvatarNodes;

            void Assign(int index, ValueChannelSchema? schema)
            {
                if (index == -1 || schema == null) return;
                if (index < 0 || index >= nodes.Length || nodes[index].Kind != AvatarKind.Values)
                    throw Error("integer channel value owner edge differs");
                if (nodes[index].Extents.Length != 6 || nodes[index].Extents[4] != schema.Ordinary + schema.Objects)
                    throw Error("integer channel schema and native bank extent differ");
                if (result.TryGetValue(index, out var previous) && !previous.Same(schema))
                    throw Error("shared Values has conflicting integer channel schemas");
                result[index] = schema;
            }

            void State(int index, ValueChannelSchema? schema)
            {
                if (index == -1) return;
                if (index < 0 || index >= nodes.Length || nodes[index].Kind != AvatarKind.NodeState
                    || nodes[index].Edges.Length != NodeStateEdges.Length)
                    throw Error("integer channel NodeState owner differs");
                Assign(nodes[index].Edges[0], schema);
            }

            foreach (var row in data.Rows)
            {
                for (int domain = 0; domain < 2; domain++)
                {
                    var backing = row.Evaluation[domain].Backing;
                    var schema = AnimationChannelSchema(backing);
                    Assign(backing.DefaultValues, schema);
                    var output = row.Datasets[domain].Edges[2];
                    if (output != -1) Assign(nodes[output].Edges[0], schema);
                    Assign(row.Inputs[domain].Edges[3], schema);
                    Assign(row.Inputs[domain].Edges[4], schema);
                }
                if (row.Retained != null)
                    Assign(row.Retained.Backup, AnimationChannelSchema(row.Evaluation[0].Backing));
            }

            foreach (var graph in data.Graphs)
            {
                if (graph.Owner < 0 || graph.Owner >= data.Rows.Length)
                    throw Error("integer channel graph owner differs");
                var schema = AnimationChannelSchema(data.Rows[graph.Owner].Evaluation[0].Backing);
                foreach (var node in graph.Nodes)
                {
                    if (node.Pose != null) State(node.Pose.State, schema);
                    if (node.Mixer != null) State(node.Mixer.State, schema);
                    if (node.Layer != null)
                    {
                        foreach (var state in node.Layer.States) State(state, schema);
                        Assign(node.Layer.LastValues, schema);
                    }
                    if (node.Controller < 0) continue;
                    if (node.Controller >= data.Playables.Length)
                        throw Error("integer channel controller owner differs");
                    var reset = data.Playables[node.Controller].ResetValues;
                    if (node.AnimationFlags[4] != 1 && reset != -1)
                        throw Error("retired controller retains reset Values root");
                    Assign(reset, schema);
                }
            }

            foreach (var player in data.Playables)
            {
                if (player.SharedAvatarValues != -1)
                    Assign(player.SharedAvatarValues, ParameterChannelSchema(player.Runtime));
            }
            return result;
        }

        private static ValueIntegerChannels CaptureIntegerChannels(byte[] bytes, ValueChannelSchema schema, Func<int, string> bind)
        {
            if (bytes.Length != checked((schema.Ordinary + schema.Objects) * 4))
                throw Error("integer channel storage extent differs");
            var objects = new string[schema.Objects];
            for (int index = 0; index < objects.Length; index++)
                objects[index] = bind(M.I32(bytes, (schema.Ordinary + index) * 4));
            return new ValueIntegerChannels
            {
                Schema = (byte[])schema.Bytes.Clone(),
                ControllerParameters = schema.Parameters,
                Ordinary = M.Slice(bytes, 0, schema.Ordinary * 4),
                Objects = objects
            };
        }

        private static byte[] EncodeIntegerChannels(ValueIntegerChannels value, Func<string, int> resolve)
        {
            var bytes = new byte[checked(value.Ordinary.Length + value.Objects.Length * 4)];
            value.Ordinary.CopyTo(bytes, 0);
            for (int index = 0; index < value.Objects.Length; index++)
            {
                var key = value.Objects[index];
                if (key == Unbound) throw Error("integer channel Object binding is unresolved");
                int id = key.Length == 0 ? 0 : resolve(key);
                if (key.Length != 0 && id == 0) throw Error("integer channel Object binding resolved to null ID");
                BitConverter.GetBytes(id).CopyTo(bytes, value.Ordinary.Length + index * 4);
            }
            return bytes;
        }

        private static void ValidateValueChannels(Data data)
        {
            var owners = ValueChannelOwners(data);
            for (int index = 0; index < data.AvatarNodes.Length; index++)
            {
                var node = data.AvatarNodes[index];
                var value = node.Integers;
                // Legacy nonempty channels remain an explicit coverage gap.
                if (value == null) continue;
                if (node.Kind != AvatarKind.Values || !owners.TryGetValue(index, out var schema)
                    || value.Schema == null || value.Ordinary == null || !Keys(value.Objects)
                    || !value.Schema.SequenceEqual(schema.Bytes) || value.ControllerParameters != schema.Parameters
                    || value.Ordinary.Length != checked(schema.Ordinary * 4) || value.Objects.Length != schema.Objects)
                    throw Error("typed integer payload lacks matching owner schema");
            }
        }

        private static IEnumerable<string> ValueChannelGaps(Data data)
        {
            if (data.AvatarNodes.Any(node => node.Integers != null && node.Integers.Objects.Contains(Unbound)))
                yield return "avatar-integer-object-binding-unresolved";
        }

        private sealed partial class AvatarGraph
        {
            private readonly Dictionary<int, IntPtr> integerCells = new Dictionary<int, IntPtr>();
            private readonly Dictionary<int, byte[]> integerBytes = new Dictionary<int, byte[]>();

            internal void RegisterValueChannels(Data data, Func<int, string> bind)
            {
                foreach (var pair in ValueChannelOwners(data))
                    Nodes[pair.Key].Integers = CaptureIntegerChannels(integerBytes[pair.Key], pair.Value, bind);
            }

            private void PrepareValueIntegers(int index, AvatarNode source, AvatarNode target, List<PendingWrite> writes,
                HashSet<int> controllerValues, Func<string, int> resolve)
            {
                var desired = source.Integers;
                var before = target.Integers;
                if ((desired == null) != (before == null)) throw Error("integer channel classification lifetime differs");
                if (desired == null)
                {
                    if (source.Kind == AvatarKind.Values && source.Extents[4] != 0 && !controllerValues.Contains(index))
                        throw Error("integer channel payload is missing for a live bank");
                    return;
                }
                if (!desired.Schema.SequenceEqual(before!.Schema) || desired.ControllerParameters != before.ControllerParameters
                    || desired.Ordinary.Length != before.Ordinary.Length || desired.Objects.Length != before.Objects.Length)
                    throw Error("integer channel schema classification differs");
                if (controllerValues.Contains(index)) return;
                if (source.ReadOnly) throw Error("instance integer channels cannot have read-only owner");
                writes.Add(new PendingWrite(integerCells[index], integerBytes[index], EncodeIntegerChannels(desired, resolve)));
            }
        }
    }
}
