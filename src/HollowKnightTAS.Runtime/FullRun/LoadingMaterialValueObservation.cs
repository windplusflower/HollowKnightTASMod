using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Copy-only ShaderPropertySheet reader for the pinned Unity binary. Property
    // names are enumerated, never inferred from a component's expected writes.
    internal sealed class LoadingMaterialValueObservation
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GetPropertyName(ref int id);
        private readonly GetPropertyName getName;
        private Dictionary<int, Texture>? textures;
        private Dictionary<int, Texture>? textureResources;
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object)
            .GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");

        internal void BeginSample() { textures = null; textureResources = null; }
        internal string ReadPropertyName(int id) => Name(id);

        internal LoadingMaterialValueObservation()
        {
            var module = LoadingEngineClockProbe.RequirePinnedEngine();
            // PDB 0334be90-63dd-4644-bee1-fc8d56c840c2/1:
            // ShaderLab::FastPropertyName::GetName (read-only, uses engine lock).
            var address = IntPtr.Add(module.BaseAddress, 0x7d59e0);
            if (Hex(Read(address, 10)) != "48895c2408574883ec20")
                throw new InvalidOperationException("P0 material property name query mismatch.");
            getName = (GetPropertyName)Marshal.GetDelegateForFunctionPointer(address, typeof(GetPropertyName));
        }

        internal object? Capture(MaterialPropertyBlock block, IntPtr pointer)
        {
            if (block.isEmpty) throw new InvalidOperationException("P0 nonempty material block capture received an empty block.");
            return CaptureSheet(pointer);
        }

        internal object CaptureMaterial(Material material, Action<string> omit)
        {
            var pointer = (IntPtr)CachedPointer.GetValue(material)!;
            var bytes = Read(pointer, 192);
            var shared = Pointer(bytes, 184);
            object? runtime = null;
            if (shared != IntPtr.Zero)
            {
                var sharedBytes = Read(shared, 688);
                var properties = CaptureSheet(IntPtr.Add(shared, 24));
                if (properties == null) omit("asset-material-unsupported-property-sheet");
                runtime = new { properties, flags = BitConverter.ToUInt32(sharedBytes, 236) & 7U,
                    textureDirty = sharedBytes[576] != 0, constantBufferDirty = sharedBytes[577] != 0,
                    userBufferOverride = sharedBytes[578] != 0, stacksDirty = sharedBytes[680] != 0 };
            }
            // The saved maps can contain values not present in the active shader.
            // Inspect existing nodes; do not invoke Material::CheckMaterialProperties.
            return new { runtime,
                savedTextures = ReadSavedMap(IntPtr.Add(pointer, 72), 20, true),
                savedFloats = ReadSavedMap(IntPtr.Add(pointer, 88), 4, false),
                savedColors = ReadSavedMap(IntPtr.Add(pointer, 104), 16, false),
                textureDirty = bytes[176] != 0, constantBufferDirty = bytes[177] != 0 };
        }

        private object[] ReadSavedMap(IntPtr map, int valueBytes, bool texture)
        {
            var header = Read(map, 16);
            var count = Count(header, 8, 4096);
            var sentinel = Pointer(header, 0);
            if (sentinel == IntPtr.Zero) throw new InvalidOperationException("P0 material saved-map sentinel is missing.");
            var head = Read(sentinel, 32);
            if (head[25] != 1) throw new InvalidOperationException("P0 material saved-map sentinel layout mismatch.");
            var nodes = new List<object>();
            var seen = new HashSet<IntPtr>();
            void Visit(IntPtr node, int depth)
            {
                if (node == sentinel) return;
                if (node == IntPtr.Zero || depth > 32 || seen.Count >= count || !seen.Add(node))
                    throw new InvalidOperationException("P0 material saved-map graph is invalid.");
                // Matching PDB: the first 28 bytes are tree links/flags;
                // pair<FastPropertyName,T> starts at +28, its value at +32.
                var data = Read(node, 32 + valueBytes);
                if (data[25] != 0) throw new InvalidOperationException("P0 material saved-map node layout mismatch.");
                Visit(Pointer(data, 0), depth + 1);
                object value = texture ? (object)new { scaleOffset = Hex(data, 32, 16),
                    texture = TextureFromInstance(BitConverter.ToInt32(data, 48)) } : Hex(data, 32, valueBytes);
                nodes.Add(new { name = Name(BitConverter.ToInt32(data, 28)), value });
                Visit(Pointer(data, 16), depth + 1);
            }
            Visit(Pointer(head, 8), 0);
            if (nodes.Count != count) throw new InvalidOperationException("P0 material saved-map count mismatch.");
            return nodes.ToArray();
        }

        private Texture? TextureFromInstance(int instance)
        {
            if (instance == 0) return null;
            // PPtr resolution through a getter may load a resource. Bind only
            // textures that are already loaded; never request one from disk.
            textures ??= Resources.FindObjectsOfTypeAll<Texture>().Where(value => value != null)
                .GroupBy(value => value.GetInstanceID()).ToDictionary(group => group.Key, group => group.First());
            if (!textures.TryGetValue(instance, out var texture))
                return null;
            return texture;
        }

        private Texture? TextureFromResource(int resource)
        {
            if (resource == 0) return null;
            if (textureResources == null)
            {
                textureResources = new Dictionary<int, Texture>();
                foreach (var texture in Resources.FindObjectsOfTypeAll<Texture>().Where(value => value != null))
                {
                    var pointer = (IntPtr)CachedPointer.GetValue(texture)!;
                    // Texture::m_TexID is a renderer resource ID, not Object's
                    // InstanceID. The saved PPtr and runtime TexEnv use different IDs.
                    var id = BitConverter.ToInt32(Read(IntPtr.Add(pointer, 80), 4), 0);
                    if (id == 0) continue;
                    if (textureResources.TryGetValue(id, out var previous) && previous.GetInstanceID() != texture.GetInstanceID())
                        throw new InvalidOperationException("P0 loaded textures share an ambiguous renderer resource ID.");
                    textureResources[id] = texture;
                }
            }
            if (!textureResources.TryGetValue(resource, out var result))
                return null;
            return result;
        }

        private object? CaptureSheet(IntPtr pointer)
        {
            // Native GetFloat/GetVector/GetMatrix/GetTexture use these same
            // type ranges, names, descriptors and byte-buffer offsets.
            var header = Read(pointer, 160);
            var count = Count(header, 64, 4096);
            if (Count(header, 96, 4096) != count)
                throw new InvalidOperationException("P0 material property counts disagree with Unity.");
            var starts = new int[7];
            for (var i = 0; i < starts.Length; i++)
            {
                starts[i] = BitConverter.ToInt32(header, 16 + i * 4);
                if (starts[i] < 0 || starts[i] > count || (i > 0 && starts[i] < starts[i - 1]))
                    throw new InvalidOperationException("P0 material property type ranges are invalid.");
            }
            if (starts[0] != 0 || starts[6] != count)
                throw new InvalidOperationException("P0 material property type ranges are incomplete.");
            // Compute/constant buffers require separate resource bindings.
            if (starts[4] != count) return null;
            var names = Read(Pointer(header, 48), checked(count * 4));
            var descriptors = Read(Pointer(header, 80), checked(count * 4));
            var data = Read(Pointer(header, 112), Count(header, 128, 1024 * 1024));
            var properties = new List<object>();
            for (var kind = 0; kind < 4; kind++)
                for (var i = starts[kind]; i < starts[kind + 1]; i++)
                {
                    var id = BitConverter.ToInt32(names, i * 4);
                    var name = Name(id);
                    var descriptor = BitConverter.ToUInt32(descriptors, i * 4);
                    var offset = (int)(descriptor & 0xfffff);
                    var elements = (int)((descriptor >> 20) & 0x3ff);
                    var flags = descriptor >> 30;
                    if (elements < 1) return null;
                    var stride = kind == 0 ? 4 : kind == 1 ? 16 : kind == 2 ? 64 : 36;
                    var length = checked(elements * stride);
                    if (offset < 0 || offset > data.Length - length)
                        throw new InvalidOperationException("P0 material property data exceeds its buffer.");
                    object value;
                    if (kind < 3) value = Hex(data, offset, length);
                    else
                    {
                        if (elements != 1) return null;
                        var texture = TextureFromResource(BitConverter.ToInt32(data, offset));
                        value = new { texture,
                            assignedDimension = BitConverter.ToInt32(data, offset + 4),
                            requiredDimension = BitConverter.ToInt32(data, offset + 8),
                            vrUsage = BitConverter.ToInt32(data, offset + 12),
                            bindMs = BitConverter.ToUInt32(data, offset + 16),
                            defaultName = Name(BitConverter.ToInt32(data, offset + 20)),
                            scaleOffsetIndex = BitConverter.ToInt32(data, offset + 24),
                            texelSizeIndex = BitConverter.ToInt32(data, offset + 28),
                            hdrDecodeIndex = BitConverter.ToInt32(data, offset + 32) };
                    }
                    properties.Add(new { name, kind, elements, flags, value });
                }
            return new { properties, allowAddProperty = header[152] != 0 };
        }

        private string Name(int id)
        {
            if (id == -1) return "<invalid-property-name>";
            var pointer = getName(ref id);
            var bytes = new List<byte>();
            for (var i = 0; i < 256; i++)
            {
                var value = Read(IntPtr.Add(pointer, i), 1)[0];
                if (value == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(value);
            }
            throw new InvalidOperationException("P0 material property name exceeds its budget.");
        }

        private static int Count(byte[] data, int offset, int maximum)
        {
            var count = BitConverter.ToInt64(data, offset);
            if (count < 0 || count > maximum) throw new InvalidOperationException("P0 material array exceeds its budget.");
            return (int)count;
        }
        private static IntPtr Pointer(byte[] data, int offset) => new IntPtr(BitConverter.ToInt64(data, offset));
        private static string Hex(byte[] data) => Hex(data, 0, data.Length);
        private static string Hex(byte[] data, int offset, int length)
            => BitConverter.ToString(data, offset, length).Replace("-", "").ToLowerInvariant();
        private static byte[] Read(IntPtr address, int length)
        {
            if (length == 0) return Array.Empty<byte>();
            if (address == IntPtr.Zero || length < 0 || length > 1024 * 1024)
                throw new InvalidOperationException("P0 material memory range is invalid.");
            var data = new byte[length];
            if (!ReadProcessMemory(GetCurrentProcess(), address, data, new UIntPtr((uint)length), out var read)
                || read.ToUInt64() != (ulong)length) throw new InvalidOperationException("P0 material memory read failed.");
            return data;
        }
    }
}
