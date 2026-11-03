using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned RTTI/PDB: NavMeshSettings56 has one PPtr<NavMeshData> at 48;
    // StreamingManager64 has one std::list<PPtr<StreamingController>> at 48.
    // Observe their complete registration, including order. Do not invoke a
    // setter or replace a service owner behind the navigation/streaming backend.
    internal static class ResidentSceneServiceSettings
    {
        private const int Navigation = 0x197c8b0, Stream = 0x194d0c0;
        private const int NavData = 0x197cbb0, Controller = 0x194abf0;
        internal sealed class Data
        {
            public int Version = 1;
            public Row[] Rows = Array.Empty<Row>();
        }
        internal sealed class Row
        {
            public string Manager = "";
            public int Type;
            public string[] References = Array.Empty<string>();
            public byte[] Padding = Array.Empty<byte>();
        }
        internal static bool OwnsManager(ResidentNativeGlobalObjects.NativeClass c) =>
            c.ObjectDerived && c.GlobalManager && !c.Abstract && !c.Stripped && c.Namespace == "" &&
            (c.Rva == Navigation && c.Name == "NavMeshSettings" && c.Module == "AI" && c.Size == 56 ||
             c.Rva == Stream && c.Name == "StreamingManager" && c.Module == "Streaming" && c.Size == 64);

        internal sealed class Live
        {
            internal Data Snapshot = null!;
            internal readonly List<Tuple<IntPtr, byte[]>> Witness = new List<Tuple<IntPtr, byte[]>>();
            internal readonly List<Tuple<IntPtr, byte[]>> Padding = new List<Tuple<IntPtr, byte[]>>();
            internal Action Admitted = null!;
            internal byte[] Read(IntPtr addr, int size)
            { var value = M.Read(addr, size); Witness.Add(Tuple.Create(addr, value)); return value; }
            internal void RequireUnchanged()
            {
                Admitted();
                foreach (var item in Witness)
                    if (!M.Read(item.Item1, item.Item2.Length).SequenceEqual(item.Item2)) throw Error("registration changed during observation");
                Admitted();
            }
            internal void Canon(ResidentMutationScope scope)
            {
                RequireUnchanged(); scope.Require();
                scope.Mutate(() => {
                    foreach (var item in Padding.Where(x => x.Item2.Any(b => b != 0))) {
                        Admitted();
                        if (!M.Read(item.Item1, item.Item2.Length).SequenceEqual(item.Item2)) throw Error("native storage changed before Canon");
                        M.Write(item.Item1, new byte[item.Item2.Length]);
                        if (M.Read(item.Item1, item.Item2.Length).Any(b => b != 0)) throw Error("storage Canon readback differs");
                    }
                });
            }
        }
        internal static Live Capture(ResidentNativeGlobalObjects.CaptureResult globals, ResidentNativeAssetDirectory.Live assets, ResidentNativeGlobalObjects.Data projected, Action admitted)
        {
            admitted(); var live = new Live { Admitted = admitted }; var records = new List<Row>();
            var types = globals.Snapshot.Classes.Where(OwnsManager).ToDictionary(c => c.Rva);
            var nativeOnly = assets.OriginalRows.ToDictionary(p => p.Value.Item1, p => p.Key);
            string Reference(int id, int type)
            {
                if (id == 0) return "";
                if (!globals.RegisteredRows.TryGetValue(id, out var target) || target.TypeRva != type)
                    throw Error("service PPtr has no registered owner of its declared type");
                if (target.Binding.Length != 0) return target.Binding;
                return nativeOnly.TryGetValue(id, out var key) ? key : throw Error("service PPtr has no portable binding");
            }
            foreach (var pair in globals.RegisteredRows.Where(p => types.ContainsKey(p.Value.TypeRva)).OrderBy(p => p.Value.Binding, StringComparer.Ordinal)) {
                var actual = pair.Value;
                if (actual.Kind != "global" || !globals.ManagerPointers.TryGetValue(actual.Binding, out var owner) ||
                    owner != globals.RegisteredPointers[pair.Key]) throw Error("actual registered manager has another owner");
                var row = new Row { Manager = actual.Binding, Type = actual.TypeRva };
                var pad = new List<byte>();
                void Padding(IntPtr address, byte[] bytes)
                { live.Padding.Add(Tuple.Create(address, bytes)); pad.AddRange(bytes); }
                if (row.Type == Navigation) {
                    var body = live.Read(IntPtr.Add(owner, 48), 8);
                    row.References = new[] { Reference(M.I32(body, 0), NavData) };
                    Padding(IntPtr.Add(owner, 52), M.Slice(body, 4, 4));
                } else {
                    var list = live.Read(IntPtr.Add(owner, 48), 16);
                    row.References = ReadControllerList(M.Ptr(list, 0), M.U64(list, 8), live.Read,
                        id => Reference(id, Controller), Padding, admitted);
                }
                row.Padding = pad.ToArray(); records.Add(row);
            }
            live.Snapshot = new Data { Rows = records.ToArray() }; Validate(projected, live.Snapshot);
            live.RequireUnchanged(); return live;
        }
        internal static string[] ReadControllerList(IntPtr head, ulong count, Func<IntPtr,int,byte[]> read,
            Func<int,string> bind, Action<IntPtr,byte[]> padding, Action admitted)
        {
            if (head == IntPtr.Zero || count > ResidentObjectDirectory.MaximumComponents) throw Error("streaming controller list header differs");
            var first = read(head, 24); var previous = head; var current = M.Ptr(first, 0);
            // The sentinel owns a node allocation but no constructed PPtr.
            padding(IntPtr.Add(head, 16), M.Slice(first, 16, 8));
            var result = new string[(int)count]; var seen = new HashSet<IntPtr>();
            for (var index = 0; index < result.Length; index++) {
                if ((index & 127) == 0) admitted();
                if (current == head || current == IntPtr.Zero || !seen.Add(current)) throw Error("short or cyclic streaming registration");
                var node = read(current, 24);
                if (M.Ptr(node, 8) != previous) throw Error("streaming registration back-link differs");
                result[index] = bind(M.I32(node, 16)); padding(IntPtr.Add(current, 20), M.Slice(node, 20, 4));
                previous = current; current = M.Ptr(node, 0);
            }
            if (current != head || M.Ptr(first, 8) != previous) throw Error("streaming registration tail/count differs");
            return result;
        }
        internal static void Validate(ResidentNativeGlobalObjects.Data registry, Data data)
        {
            if (data == null || data.Version != 1 || data.Rows == null || data.Rows.Length > registry.Rows.Length) throw Error("service manifest differs");
            var types = registry.Classes.Where(OwnsManager).ToDictionary(t => t.Rva);
            var expected = registry.Rows.Where(row => types.ContainsKey(row.TypeRva)).OrderBy(r => r.Binding, StringComparer.Ordinal).ToArray();
            if (expected.Length != data.Rows.Length) throw Error("service settings coverage differs");
            var targets = registry.Rows.Where(r => r.Binding.Length != 0).ToDictionary(r => r.Binding, r => r.TypeRva, StringComparer.Ordinal);
            for (var i = 0; i < expected.Length; i++) {
                var r = data.Rows[i]; var e = expected[i];
                if (r == null || e.Kind != "global" || e.Binding.Length == 0 || r.Manager != e.Binding || r.Type != e.TypeRva ||
                    r.References == null || r.References.Length > ResidentObjectDirectory.MaximumComponents ||
                    r.Type == Navigation && r.References.Length != 1 || r.Padding == null ||
                    r.Padding.Length != (r.Type == Navigation ? 4 : 8 + 4*r.References.Length) ||
                    r.References.Any(k => k == null || k.Length != 0 && (!targets.TryGetValue(k, out var actual) || actual != (r.Type == Navigation ? NavData : Controller))))
                    throw Error("typed service reference, native storage or actual owner differs");
            }
        }
        internal static byte[] Dependencies(Data data)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(data.Rows.Length);
            foreach (var row in data.Rows) {
                writer.Write(row.Manager); writer.Write(row.Type); writer.Write(row.References.Length);
                foreach (var reference in row.References) writer.Write(reference);
            }
            writer.Flush(); return stream.ToArray();
        }
        internal static byte[] EncodeS0(Data data)
        {
            if (data.Rows.Any(row => row.Padding.Any(b => b != 0))) throw Error("noncanonical service storage");
            return Dependencies(data);
        }
        internal static void RequireMatching(Data source, Data target)
        { if (!EncodeS0(source).SequenceEqual(EncodeS0(target))) throw Error("active navigation/streaming registration dependencies differ"); }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident scene services: " + message);
    }
}
