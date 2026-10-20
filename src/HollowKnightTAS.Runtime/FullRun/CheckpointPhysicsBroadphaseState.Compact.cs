using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class CheckpointPhysicsBroadphaseState
    {
        // Free slots carry only the engine's valid next/height fields. All live
        // fields are bit-preserved, including internal user-data owner aliases.
        internal byte[] EncodeCompact()
        {
            Validate();
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x42504331); writer.Write(Version);
            foreach (var value in new[] { Root, Count, FreeHead, InsertionCount, ProxyCount, QueryProxy, WorldFlags, SimulationId }) writer.Write(value);
            writer.Write(Path); writer.Write(Step); writer.Write(Nodes.Length);
            var owners = Nodes.Where(n => n.Proxy != null).Select(n => n.Proxy!).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            writer.Write(owners.Length);
            foreach (var owner in owners) writer.Write(owner);
            int Owner(string? key) => key == null ? -1 : Array.BinarySearch(owners, key, StringComparer.Ordinal);
            foreach (var node in Nodes) {
                writer.Write(node.Parent); writer.Write(node.Height);
                if (node.Height == -1) continue;
                writer.Write(node.Child1); writer.Write(node.Child2); writer.Write(node.Bounds); writer.Write(Owner(node.UserData));
                if (node.Child1 != -1) continue;
                writer.Write(Owner(node.Proxy)); writer.Write(node.ProxyBounds); writer.Write(node.ProxyChild);
            }
            writer.Write(Moves.Length); foreach (var value in Moves) writer.Write(value);
            writer.Write(Pairs.Length); foreach (var value in Pairs) writer.Write(value);
            writer.Flush(); return stream.ToArray();
        }
        internal static CheckpointPhysicsBroadphaseState DecodeCompact(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 4 * 1024 * 1024) throw new InvalidDataException("Broadphase compact bounds differ.");
            using var stream = new MemoryStream(bytes, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (reader.ReadInt32() != 0x42504331) throw new InvalidDataException("Broadphase compact signature differs.");
            var result = new CheckpointPhysicsBroadphaseState { Version = reader.ReadInt32(), Root = reader.ReadInt32(), Count = reader.ReadInt32(),
                FreeHead = reader.ReadInt32(), InsertionCount = reader.ReadInt32(), ProxyCount = reader.ReadInt32(), QueryProxy = reader.ReadInt32(),
                WorldFlags = reader.ReadInt32(), SimulationId = reader.ReadInt32(), Path = reader.ReadUInt32(), Step = reader.ReadBytes(8) };
            var count = Bound(reader.ReadInt32(), MaximumNodes, "compact nodes");
            var ownerCount = Bound(reader.ReadInt32(), MaximumNodes, "compact owners");
            var owners = new string[ownerCount];
            for (var i = 0; i < owners.Length; i++) {
                owners[i] = reader.ReadString();
                if (owners[i].Length == 0 || owners[i].Length > 4096 || (i != 0 && StringComparer.Ordinal.Compare(owners[i - 1], owners[i]) >= 0))
                    throw new InvalidDataException("Broadphase compact owner order/identity differs.");
            }
            string? Owner() { var index = reader.ReadInt32(); if (index == -1) return null;
                if (index < 0 || index >= owners.Length) throw new InvalidDataException("Broadphase compact owner index differs."); return owners[index]; }
            result.Nodes = new Node[count];
            for (var i = 0; i < count; i++) {
                var node = new Node { Parent = reader.ReadInt32(), Height = reader.ReadInt32() }; result.Nodes[i] = node;
                if (node.Height == -1) continue;
                node.Child1 = reader.ReadInt32(); node.Child2 = reader.ReadInt32(); node.Bounds = reader.ReadBytes(16); node.UserData = Owner();
                if (node.Child1 != -1) continue;
                node.Proxy = Owner(); node.ProxyBounds = reader.ReadBytes(16); node.ProxyChild = reader.ReadInt32();
            }
            int[] Buffer(int maximum) { var length = Bound(reader.ReadInt32(), maximum, "compact buffer");
                var values = new int[length]; for (var i = 0; i < length; i++) values[i] = reader.ReadInt32(); return values; }
            result.Moves = Buffer(MaximumNodes * 2); result.Pairs = Buffer(MaximumNodes * 4);
            if (stream.Position != stream.Length) throw new InvalidDataException("Broadphase compact trailing bytes differ.");
            result.Validate();
            if (!result.EncodeCompact().SequenceEqual(bytes)) throw new InvalidDataException("Broadphase compact encoding differs.");
            return result;
        }
    }
}
