using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Shader is not an immutable asset merely because its provenance is
        // equal. The source image is a dependency, name/maximum LOD are mutable
        // D, and compiled programs/keyword maps/selection caches have separate
        // native ownership. This capture closes native asset references without
        // running Shader.Find, property getters, compilation or a lazy PPtr load.
        // The remaining program graph is a mandatory gap, including after an
        // in-place reload, until its complete independent witness is implemented.
        internal sealed class ShaderState
        {
            public ResidentSerializedAssetImage.Data Image = new ResidentSerializedAssetImage.Data { CoverageGaps = new[] { "serialized-image/no-source-identifier" } };
            public string Name = "", ImplementationName = "", CustomEditor = "", Fallback = "";
            public bool HasImplementation, Baked, NeedsCreating, OldBlobFormat, NoProjector, HasLighting;
            public int MaximumLod, ActiveSubShader, RenderQueue, DisableBatching;
            public int[] DefaultSubShaders = new int[6];
            public string[] Dependencies = Array.Empty<string>();
            public ShaderProperty[] Properties = Array.Empty<ShaderProperty>();
            public ShaderTexture[] NonModifiableTextures = Array.Empty<ShaderTexture>();
            public ResidentShaderPropertySheet.Data? Defaults;
            internal ShaderState Copy() => new ShaderState { Image = Image.Copy(), Name = Name, ImplementationName = ImplementationName,
                CustomEditor = CustomEditor, Fallback = Fallback, HasImplementation = HasImplementation, Baked = Baked, NeedsCreating = NeedsCreating,
                OldBlobFormat = OldBlobFormat, NoProjector = NoProjector, HasLighting = HasLighting, MaximumLod = MaximumLod,
                ActiveSubShader = ActiveSubShader, RenderQueue = RenderQueue, DisableBatching = DisableBatching,
                DefaultSubShaders = (int[])DefaultSubShaders.Clone(), Dependencies = (string[])Dependencies.Clone(),
                Properties = Properties.Select(value => value.Copy()).ToArray(), NonModifiableTextures = NonModifiableTextures.Select(value => value.Copy()).ToArray(),
                Defaults = Defaults == null ? null : ResidentShaderPropertySheet.Copy(Defaults) };
        }
        internal sealed class ShaderProperty
        {
            public string Name = "", Description = "", DefaultTexture = "";
            public string[] Attributes = Array.Empty<string>();
            public int Type, TextureDimension;
            public uint Flags;
            public byte[] DefaultValue = new byte[16];
            internal ShaderProperty Copy() => new ShaderProperty { Name = Name, Description = Description, DefaultTexture = DefaultTexture,
                Attributes = (string[])Attributes.Clone(), Type = Type, TextureDimension = TextureDimension, Flags = Flags, DefaultValue = (byte[])DefaultValue.Clone() };
        }
        internal sealed class ShaderTexture
        {
            public string Name = "", Binding = "";
            internal ShaderTexture Copy() => new ShaderTexture { Name = Name, Binding = Binding };
        }
        private sealed class ShaderRead
        {
            private readonly List<Tuple<IntPtr, byte[]>> reads = new List<Tuple<IntPtr, byte[]>>();
            private long bytes;
            internal byte[] Read(IntPtr address, int length) {
                bytes = checked(bytes + length); RequireBytes(bytes);
                var value = M.Read(address, length); reads.Add(Tuple.Create(address, value)); return value;
            }
            internal string String(IntPtr address) {
                var value = M.String(address); Read(address, 40);
                // Retain a read of the pointed-to characters as well as the
                // string header, so an in-place same-length change is detected.
                var header = reads[reads.Count - 1].Item2;
                var pointer = M.Ptr(header, 0); var length = M.U64(header, 24);
                if (length > 16384) throw Error("shader string exceeds bounds");
                if (pointer != IntPtr.Zero) Read(pointer, checked((int)length + 1));
                if (M.String(address) != value) throw Error("shader string changed during capture");
                return value;
            }
            internal byte[] Array(IntPtr address, int stride, int limit) {
                var header = Read(address, 32); var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
                if (count > capacity || count > (ulong)limit || capacity > (ulong)MaximumBytes / (ulong)stride)
                    throw Error("shader native array exceeds bounds");
                return Read(M.Ptr(header, 0), checked((int)count * stride));
            }
            internal void Frozen() { foreach (var value in reads) if (!value.Item2.SequenceEqual(M.Read(value.Item1, value.Item2.Length))) throw Error("shader native state changed during capture"); }
        }
        private static ShaderState CaptureShader(Shader owner, Func<object, string?> bind)
        {
            RequireAssetVtable(owner, 0x17270d8); var pointer = M.Owner(owner); var observation = new ShaderRead();
            var header = observation.Read(pointer, 1040); var implementation = M.Ptr(header, 56);
            var result = new ShaderState { Image = ResidentSerializedAssetImage.Capture(owner), Name = observation.String(IntPtr.Add(pointer, 80)),
                HasImplementation = implementation != IntPtr.Zero, Baked = ShaderBool(header[176]), NeedsCreating = ShaderBool(header[177]), OldBlobFormat = ShaderBool(header[178]) };
            string Binding(int id, Type expected) {
                return ResidentNativeAssetReference.BindId(id,expected,bind);
            }
            var dependencies = ShaderVector(observation, header, 152, 4, 4096);
            result.Dependencies = Enumerable.Range(0, dependencies.Length / 4).Select(index => Binding(M.I32(dependencies, index * 4), typeof(Shader))).ToArray();
            result.NonModifiableTextures = ReadShaderTextures(observation, IntPtr.Add(pointer, 896), id => Binding(id, typeof(Texture)));
            var properties = M.Ptr(header, 192);
            if (properties != IntPtr.Zero) result.Properties = ReadShaderProperties(observation, properties);
            if (implementation != IntPtr.Zero) {
                var data = observation.Read(implementation, 328);
                result.DefaultSubShaders = Enumerable.Range(0, 6).Select(index => M.I32(data, 64 + index * 4)).ToArray();
                result.ActiveSubShader = M.I32(data, 88); result.RenderQueue = M.I32(data, 92); result.MaximumLod = M.I32(data, 96);
                result.NoProjector = ShaderBool(data[100]); result.DisableBatching = M.I32(data, 104); result.HasLighting = ShaderBool(data[108]);
                result.ImplementationName = observation.String(IntPtr.Add(implementation, 184)); result.CustomEditor = observation.String(IntPtr.Add(implementation, 224));
                result.Fallback = observation.String(IntPtr.Add(implementation, 264));
                var defaults = M.Ptr(data, 176); if (defaults != IntPtr.Zero) result.Defaults = ResidentShaderPropertySheet.Capture(defaults, bind);
            }
            observation.Frozen(); ValidateShader(result); return result;
        }
        private static bool ShaderBool(byte value) { if (value > 1) throw Error("shader native boolean differs"); return value != 0; }
        private static byte[] ShaderVector(ShaderRead observation, byte[] header, int offset, int stride, int limit)
        {
            var first = M.Ptr(header, offset); var last = M.Ptr(header, offset + 8); var end = M.Ptr(header, offset + 16);
            var size = last.ToInt64() - first.ToInt64(); var capacity = end.ToInt64() - first.ToInt64();
            if (size < 0 || size % stride != 0 || size / stride > limit || capacity < size || capacity % stride != 0 || capacity > MaximumBytes
                || first == IntPtr.Zero && (last != IntPtr.Zero || end != IntPtr.Zero)) throw Error("shader native vector exceeds bounds");
            return observation.Read(first, (int)size);
        }
        private static ShaderProperty[] ReadShaderProperties(ShaderRead observation, IntPtr properties)
        {
            var header = observation.Read(properties, 32); var bytes = observation.Array(properties, 184, 4096); var first = M.Ptr(header, 0);
            var result = new ShaderProperty[bytes.Length / 184];
            for (var index = 0; index < result.Length; index++) {
                var pointer = IntPtr.Add(first, index * 184); var offset = index * 184;
                var attributes = observation.Array(IntPtr.Add(pointer, 80), 40, 256); var attributePointer = M.Ptr(bytes, offset + 80);
                result[index] = new ShaderProperty { Name = observation.String(pointer), Description = observation.String(IntPtr.Add(pointer, 40)),
                    Type = M.I32(bytes, offset + 112), Flags = M.U32(bytes, offset + 116), DefaultValue = M.Slice(bytes, offset + 120, 16),
                    DefaultTexture = observation.String(IntPtr.Add(pointer, 136)), TextureDimension = M.I32(bytes, offset + 176),
                    Attributes = Enumerable.Range(0, attributes.Length / 40).Select(item => observation.String(IntPtr.Add(attributePointer, item * 40))).ToArray() };
            }
            return result;
        }
        private static ShaderTexture[] ReadShaderTextures(ShaderRead observation, IntPtr map, Func<int, string> bind)
        {
            var header = observation.Read(map, 16); var count = M.U64(header, 8); var sentinel = M.Ptr(header, 0);
            if (count > 4096 || sentinel == IntPtr.Zero) throw Error("shader texture map exceeds bounds");
            var head = observation.Read(sentinel, 32); if (head[25] != 1) throw Error("shader texture map sentinel differs");
            var nodes = new HashSet<IntPtr>(); var values = new List<ShaderTexture>();
            void Visit(IntPtr node, int depth) {
                if (node == sentinel) return;
                if (node == IntPtr.Zero || depth > 128 || (ulong)nodes.Count >= count || !nodes.Add(node)) throw Error("shader texture map graph differs");
                var row = observation.Read(node, 80); if (row[25] != 0) throw Error("shader texture map node differs");
                Visit(M.Ptr(row, 0), depth + 1);
                values.Add(new ShaderTexture { Name = observation.String(IntPtr.Add(node, 32)), Binding = bind(M.I32(row, 72)) });
                Visit(M.Ptr(row, 16), depth + 1);
            }
            Visit(M.Ptr(head, 8), 0);
            if ((ulong)nodes.Count != count) throw Error("shader texture map count differs");
            return values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray();
        }
        internal static void ValidateShader(ShaderState state)
        {
            if (state == null || state.Image == null || state.DefaultSubShaders == null || state.DefaultSubShaders.Length != 6
                || state.Dependencies == null || state.Dependencies.Length > 4096 || state.Properties == null || state.Properties.Length > 4096
                || state.NonModifiableTextures == null || state.NonModifiableTextures.Length > 4096) throw Error("shader typed state schema differs");
            ResidentSerializedAssetImage.Validate(state.Image);
            void String(string value) { if (value == null || value.Length > 16384 || value.IndexOf('\0') >= 0) throw Error("shader typed string differs"); }
            String(state.Name); String(state.ImplementationName); String(state.CustomEditor); String(state.Fallback);
            foreach (var binding in state.Dependencies) String(binding);
            foreach (var property in state.Properties) {
                if (property == null || property.Type < 0 || property.Type > 4 || property.DefaultValue == null || property.DefaultValue.Length != 16
                    || property.Attributes == null || property.Attributes.Length > 256) throw Error("shader property schema differs");
                String(property.Name); String(property.Description); String(property.DefaultTexture);
                foreach (var attribute in property.Attributes) String(attribute);
            }
            if (state.Properties.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != state.Properties.Length) throw Error("shader property names repeat");
            string? previous = null;
            foreach (var texture in state.NonModifiableTextures) {
                if (texture == null) throw Error("shader texture map row is absent"); String(texture.Name); String(texture.Binding);
                if (previous != null && StringComparer.Ordinal.Compare(previous, texture.Name) >= 0) throw Error("shader texture map order differs"); previous = texture.Name;
            }
            if (state.Defaults != null) ResidentShaderPropertySheet.Validate(state.Defaults);
            if (!state.HasImplementation && (state.Defaults != null || state.MaximumLod != 0 || state.ActiveSubShader != 0 || state.RenderQueue != 0
                || state.DisableBatching != 0 || state.NoProjector || state.HasLighting || state.ImplementationName.Length != 0 || state.CustomEditor.Length != 0
                || state.Fallback.Length != 0 || state.DefaultSubShaders.Any(value => value != 0))) throw Error("shader absent implementation contains native values");
        }
        internal static byte[] EncodeShaderValues(ShaderState state)
        {
            ValidateShader(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(state.Name); writer.Write(state.HasImplementation); writer.Write(state.MaximumLod);
                writer.Flush(); return stream.ToArray();
            }
        }
        internal static byte[] EncodeShaderObservedState(ShaderState state)
        {
            ValidateShader(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                void Strings(string[] values) { writer.Write(values.Length); foreach (var value in values) writer.Write(value); }
                var image = state.Image;
                writer.Write(image.Version); writer.Write(image.SourcePath); writer.Write(image.LocalIdentifier); writer.Write(image.NativeTypeRva);
                writer.Write(image.Length); writer.Write(image.Hash); writer.Write(image.TypeHash.Length); writer.Write(image.TypeHash); writer.Write(image.Endianness); Strings(image.CoverageGaps);
                writer.Write(EncodeShaderValues(state)); writer.Write(state.ImplementationName); writer.Write(state.CustomEditor); writer.Write(state.Fallback);
                writer.Write(state.Baked); writer.Write(state.NeedsCreating); writer.Write(state.OldBlobFormat); writer.Write(state.NoProjector); writer.Write(state.HasLighting);
                writer.Write(state.ActiveSubShader); writer.Write(state.RenderQueue); writer.Write(state.DisableBatching);
                foreach (var index in state.DefaultSubShaders) writer.Write(index); Strings(state.Dependencies);
                writer.Write(state.Properties.Length);
                foreach (var property in state.Properties) {
                    writer.Write(property.Name); writer.Write(property.Description); Strings(property.Attributes); writer.Write(property.Type); writer.Write(property.Flags);
                    writer.Write(property.DefaultValue); writer.Write(property.DefaultTexture); writer.Write(property.TextureDimension);
                }
                writer.Write(state.NonModifiableTextures.Length); foreach (var texture in state.NonModifiableTextures) { writer.Write(texture.Name); writer.Write(texture.Binding); }
                writer.Write(state.Defaults != null);
                if (state.Defaults != null) {
                    var sheet = state.Defaults; writer.Write(sheet.Version); writer.Write(sheet.AllowAddProperty); Strings(sheet.CoverageGaps); writer.Write(sheet.Properties.Length);
                    foreach (var property in sheet.Properties) {
                        writer.Write(property.Name); writer.Write(property.Kind); writer.Write(property.Elements); writer.Write(property.Flags);
                        writer.Write(property.Values.Length); writer.Write(property.Values); writer.Write(property.Textures.Length);
                        foreach (var texture in property.Textures) {
                            writer.Write(texture.Binding); writer.Write(texture.DefaultName); writer.Write(texture.ScaleOffset); writer.Write(texture.TexelSize); writer.Write(texture.HdrDecode);
                            writer.Write(texture.AssignedDimension); writer.Write(texture.RequiredDimension); writer.Write(texture.VrUsage); writer.Write(texture.BindMs);
                        }
                    }
                }
                writer.Flush(); return stream.ToArray();
            }
        }
        private static string[] ShaderCoverageGaps(ShaderState state)
        {
            ValidateShader(state);
            return new[] { "shader/native-program-definition-and-compiled-device-state", "shader/keyword-map-and-global-registrations",
                "shader/complete-cache-canon-and-independent-readback", state.HasImplementation ? "" : "shader/implementation-not-created" }
                .Concat(state.Image.CoverageGaps).Concat(state.Image.Hash.Length == 64 ? Array.Empty<string>() : new[] { "shader/serialized-definition-image" })
                .Concat(state.Defaults?.CoverageGaps ?? Array.Empty<string>()).Where(value => value.Length != 0).Distinct(StringComparer.Ordinal).ToArray();
        }
        internal static void RequireShaderComplete(ShaderState state)
        { throw Error("Shader native coverage is incomplete: " + string.Join(";", ShaderCoverageGaps(state))); }
    }
}
