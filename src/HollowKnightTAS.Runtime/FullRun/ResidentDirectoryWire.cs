using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using D = HollowKnightTAS.Runtime.FullRun.ResidentObjectDirectory;
using E = HollowKnightTAS.Runtime.FullRun.ResidentExecutionOwnership;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Parent indices and the actual type table reconstruct the full paths.
    // The source manifest must validate before redundant keys are omitted.
    internal static class ResidentDirectoryWire
    {
        private const int Magic = 0x32445248, Limit = 32 * 1024 * 1024;
        internal static byte[] Encode(D.Snapshot value)
        {
            D.Validate(value);
            using var w = new ResidentMetadataWire.Writer(Limit); var b = w.Out;
            var types = new Dictionary<string, int>(StringComparer.Ordinal); var typeIds = new Dictionary<D.TypeRecord,int>(); var list = new List<D.TypeRecord>();
            var scenes = new Dictionary<string, int>(StringComparer.Ordinal); var objects = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in value.Objects) foreach (var component in row.Components) {
                if (typeIds.ContainsKey(component.Type)) continue;
                var key = component.Type.Key;
                if (!types.TryGetValue(key,out var id)) { types.Add(key,id=list.Count); list.Add(component.Type); }
                typeIds.Add(component.Type,id);
            }
            void Type(D.TypeRecord t) {
                w.Text(t.Assembly); w.Text(t.Module); w.Text(t.Name); b.Write(t.Token); b.Write(t.Arguments.Length);
                foreach (var arg in t.Arguments) Type(arg);
            }
            b.Write(Magic); b.Write(list.Count); foreach (var type in list) Type(type);
            b.Write(value.Scenes.Length);
            foreach (var scene in value.Scenes) {
                scenes.Add(scene.Key, scenes.Count); w.Text(scene.Path); w.Text(scene.Name); b.Write(scene.BuildIndex); b.Write(scene.Ordinal); b.Write(scene.Unlisted);
            }
            b.Write(value.Objects.Length);
            foreach (var row in value.Objects) {
                b.Write(scenes[row.Scene]); b.Write(row.Parent.Length == 0 ? -1 : objects[row.Parent]); objects.Add(row.Key,objects.Count);
                w.Text(row.Name); w.Text(row.Tag); b.Write(row.Ordinal); b.Write(row.SiblingIndex); b.Write(row.Layer); b.Write(row.HideFlags);
                b.Write(row.ActiveSelf); b.Write(row.IsStatic); b.Write(row.Components.Length);
                foreach (var c in row.Components) {
                    b.Write(typeIds[c.Type]); b.Write(c.Ordinal); b.Write(c.HideFlags); b.Write((byte)c.Ownership); w.Text(c.ExecutionRole);
                }
            }
            b.Write(value.Execution.Dependencies.Length);
            foreach (var d in value.Execution.Dependencies) { w.Text(d.Role); b.Write(objects[d.ObjectKey]); b.Write(d.ComponentOrder); }
            return w.Finish();
        }

        internal static D.Snapshot Decode(byte[] bytes)
        {
            try {
                using var r = new ResidentMetadataWire.Reader(bytes, Limit); var b = r.In;
                if (b.ReadInt32() != Magic) throw Error("unknown schema");
                int Index(int count) { var n = b.ReadInt32(); return n >= 0 && n < count ? n : throw Error("foreign index"); }
                int nodes = 0; long expanded = 0;
                void Charge(long chars) { if (chars > 262144 || (expanded += chars) > 64L * 1024 * 1024) throw Error("expanded identity budget exceeded"); }
                D.TypeRecord Type(int depth,out int keyBound) {
                    if (depth > 32 || ++nodes > D.MaximumComponents * 2) throw Error("type shape exceeds its bound");
                    var t = new D.TypeRecord { Assembly = r.Text(), Module = r.Text(), Name = r.Text(), Token = b.ReadInt32(),
                        Arguments = new D.TypeRecord[r.Count(64,20)] };
                    long length = (long)t.Assembly.Length + t.Module.Length + t.Name.Length + 48;
                    for (var j = 0; j < t.Arguments.Length; j++) {
                        t.Arguments[j] = Type(depth + 1,out var childSize); length += childSize + 8;
                    }
                    if (length > 262144) throw Error("type identity exceeds its bound");
                    keyBound = (int)length; return t;
                }
                var types = new D.TypeRecord[r.Count(D.MaximumComponents,20)]; var seen = new HashSet<string>(StringComparer.Ordinal);
                var typeKeys = new string[types.Length];
                for (var i = 0; i < types.Length; i++) { var t = types[i] = Type(0,out var bound); Charge(bound); t.Validate();
                    if (!seen.Add(typeKeys[i]=t.Key)) throw Error("duplicate type"); }
                var used = new bool[types.Length]; var nextType = 0;
                var result = new D.Snapshot { Scenes = new D.SceneRecord[r.Count(256,17)] };
                for (var i = 0; i < result.Scenes.Length; i++) {
                    var s = result.Scenes[i] = new D.SceneRecord { Path=r.Text(), Name=r.Text(), BuildIndex=b.ReadInt32(), Ordinal=b.ReadInt32(), Unlisted=r.Bit() };
                    Charge((long)s.Path.Length + s.Name.Length + 64); s.Key = D.SceneKey(s.Path,s.Name,s.BuildIndex,s.Ordinal);
                }
                result.Objects = new D.ObjectRecord[r.Count(D.MaximumObjects,55)]; var total = 0;
                for (var i = 0; i < result.Objects.Length; i++) {
                    var scene = result.Scenes[Index(result.Scenes.Length)]; var p = b.ReadInt32();
                    if (p < -1 || p >= i) throw Error("parent did not precede child");
                    var row = result.Objects[i] = new D.ObjectRecord { Scene=scene.Key, Parent=p<0 ? "" : result.Objects[p].Key, Name=r.Text(), Tag=r.Text(),
                        Ordinal=b.ReadInt32(), SiblingIndex=b.ReadInt32(), Layer=b.ReadInt32(), HideFlags=b.ReadInt32(), ActiveSelf=r.Bit(), IsStatic=r.Bit() };
                    Charge((long)(p<0 ? row.Scene : row.Parent).Length + row.Name.Length + 32);
                    row.Key = D.ObjectKey(row.Scene,row.Parent,row.Name,row.Ordinal);
                    row.Components = new D.ComponentRecord[r.Count(D.MaximumComponents - total,17)]; total += row.Components.Length;
                    for (var j = 0; j < row.Components.Length; j++) {
                        var ti = Index(types.Length);
                        if (!used[ti]) { if (ti != nextType++) throw Error("noncanonical type order"); used[ti] = true; }
                        var c = row.Components[j] = new D.ComponentRecord { Type=types[ti], Order=j, Ordinal=b.ReadInt32(),
                            HideFlags=b.ReadInt32(), Ownership=(ResidentComponentOwnership)b.ReadByte(), ExecutionRole=r.Text() };
                        Charge((long)row.Key.Length + typeKeys[ti].Length + c.ExecutionRole.Length + 40);
                        c.Key = c.Ownership == ResidentComponentOwnership.Execution ? E.Binding(c.Type,c.ExecutionRole) : D.ComponentKeyFromTypeKey(row.Key,typeKeys[ti],c.Ordinal);
                    }
                }
                if (nextType != types.Length) throw Error("unreferenced type");
                result.Execution = E.CreateManifest(result.Objects);
                result.Execution.Dependencies = new E.DependencyEntry[r.Count(D.MaximumObjects,12)];
                for (var i = 0; i < result.Execution.Dependencies.Length; i++) {
                    var role = r.Text(); var row = result.Objects[Index(result.Objects.Length)]; var slot = b.ReadInt32();
                    if (slot < -1 || slot >= row.Components.Length) throw Error("foreign execution dependency slot");
                    var c = slot == -1 ? null : row.Components[slot];
                    result.Execution.Dependencies[i] = new E.DependencyEntry { ObjectKey=row.Key, Role=role, ComponentOrder=slot, Type=c?.Type, ComponentKey=c?.Key ?? "" };
                }
                r.End(); D.Validate(result); return result;
            } catch (Exception e) when (e is EndOfStreamException || e is DecoderFallbackException) {
                throw new InvalidDataException("Malformed resident directory.",e);
            }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident directory wire: " + reason);
    }
}
