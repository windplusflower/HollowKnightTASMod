using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // This is the D-value transfer stage, not complete Material Canon.
        // It reconstructs saved maps as well as the live property sheet: public
        // SetFloat/SetColor only modify saved keys which already exist and
        // therefore cannot replace the source's actual serialized directory.
        // GPU buffers, shared-data alias groups, display lists, texture stacks
        // and shader-global caches remain the full adapter's explicit gaps.
        internal static PreparedMaterialValues PrepareMaterialValues(Snapshot source, Material target,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Func<bool> admitted)
            => new PreparedMaterialValues(source, target, resolve, bind, admitted);

        internal sealed class PreparedMaterialValues : IDisposable
        {
            private readonly Snapshot source;
            private readonly Material target;
            private readonly Func<string,NativeObject> resolve;
            private readonly Func<object, string?> bind;
            private readonly Func<bool> admitted;
            private readonly MaterialValueFunctions functions;
            private readonly BorrowedSavedMaterialSheet saved;
            private readonly ResidentShaderPropertySheet.Prepared properties;
            private bool attempted, applied, disposed;

            internal PreparedMaterialValues(Snapshot source, Material target, Func<string, NativeObject> resolve,
                Func<object, string?> bind, Func<bool> admitted)
            {
                if (source == null || source.Kind != "Material" || source.Material == null || source.Type.Resolve() != typeof(Material)
                    || resolve == null || bind == null || admitted == null) throw Error("material value transfer lacks its exact type or bindings");
                ResidentAssetPipeline.ValidateMaterial(source.Material);
                var data = source.Material;
                if (!data.HasRuntimeSheet || data.RuntimeProperties == null || data.ShaderBinding.Length == 0)
                    throw Error("material value transfer requires an existing runtime sheet and loaded shader");
                ResidentShaderPropertySheet.RequireCompleteCoverage(data.RuntimeProperties);
                this.source = source.Copy(); this.target = target; this.bind = bind; this.admitted = admitted; Require();
                this.resolve = resolve;
                _ = ResidentNativeAssetReference.ResolveTyped(data.ShaderBinding,typeof(Shader),resolve);
                _ = ResidentNativeAssetReference.ResolveTyped(data.RuntimeShaderBinding,typeof(Shader),resolve);
                _ = ResidentNativeAssetReference.ResolveTyped(data.OwnerBinding,typeof(NativeObject),resolve);
                functions = new MaterialValueFunctions();
                var keys = data.SavedProperties.Select(value => Shader.PropertyToID(value.Name)).ToArray();
                var values = data.SavedProperties.Select(value => {
                    if (value.Kind != 2) return (byte[])value.Value.Clone();
                    var bytes = new byte[20]; Buffer.BlockCopy(value.Value, 0, bytes, 0, 16);
                    var texture = ResidentNativeAssetReference.ResolveId(value.TextureBinding,typeof(Texture),resolve);
                    Buffer.BlockCopy(BitConverter.GetBytes(texture), 0, bytes, 16, 4); return bytes;
                }).ToArray();
                saved = new BorrowedSavedMaterialSheet(data.SavedProperties.Select(value => value.Kind).ToArray(), keys, values);
                try { properties = ResidentShaderPropertySheet.Prepare(data.RuntimeProperties, resolve, bind, Require); }
                catch { saved.Dispose(); throw; }
            }
            internal void Apply()
            {
                Require(); if (attempted) throw Error("material value application is single use"); attempted = true;
                var data = source.Material!; var pointer = ResidentNativeMemory.Owner(target);
                functions.ResetWithShader(pointer, ResidentNativeAssetReference.ResolveTyped(data.ShaderBinding,typeof(Shader),resolve));
                functions.CopySaved(IntPtr.Add(pointer, 72), saved.Pointer);
                target.shaderKeywords = data.Keywords;
                target.enableInstancing = data.EnableInstancing; target.doubleSidedGI = data.DoubleSidedGI;
                target.globalIlluminationFlags = (MaterialGlobalIlluminationFlags)data.GlobalIlluminationFlags;
                target.renderQueue = data.RenderQueue;
                var shared = functions.GetWritableShared(pointer, 3);
                if (shared == IntPtr.Zero || BitConverter.ToInt32(Read(IntPtr.Add(shared, 12), 4), 0) != 1)
                    throw Error("material native COW did not return uniquely owned shared data");
                ResetMaterialTagDirectories(shared);
                foreach (var pass in data.DisabledPasses) target.SetShaderPassEnabled(pass, false);
                foreach (var tag in data.OverrideTags) target.SetOverrideTag(tag.Name, tag.Value);
                shared = functions.GetWritableShared(pointer, 3);
                properties.CopyIntoUniqueSheet(IntPtr.Add(shared, 24));
                ResidentNativeMemory.Write(IntPtr.Add(shared, 16), BitConverter.GetBytes(ResidentNativeAssetReference.ResolveTyped(data.RuntimeShaderBinding,typeof(Shader),resolve).ToInt64()));
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 64), BitConverter.GetBytes(ResidentNativeAssetReference.ResolveId(data.OwnerBinding,typeof(NativeObject),resolve)));
                functions.CopyString(IntPtr.Add(pointer, 128), data.SerializedKeywords);
                // Property validity is semantic; historical derived hashes are
                // deliberately invalidated for the later common cache stage.
                var flags = (BitConverter.ToUInt32(Read(IntPtr.Add(shared, 236), 4), 0) & ~7U) | (data.RuntimeFlags & 3U) | 4U;
                ResidentNativeMemory.Write(IntPtr.Add(shared, 236), BitConverter.GetBytes(flags));
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 176), new byte[] { 1, 1 });
                ResidentNativeMemory.Write(IntPtr.Add(shared, 576), new byte[] { 1, 1 });
                target.name = source.Name; target.hideFlags = (HideFlags)source.HideFlags;
                applied = true; Verify();
            }
            internal void Verify()
            {
                Require(); if (!applied) throw Error("material value verification precedes application");
                var observed = new MaterialState(); CaptureMaterialNative(target, observed, bind);
                if (target.name != source.Name || (int)target.hideFlags != source.HideFlags
                    || !EncodeMaterialValues(source.Material!).SequenceEqual(EncodeMaterialValues(observed)))
                    throw Error("material saved/runtime values or metadata failed independent readback");
            }
            private void Require()
            {
                if (disposed || !admitted() || !ResidentObjectDirectory.IsLive(target) || target.GetType() != typeof(Material))
                    throw Error("material value transfer lost its admitted exact-type owner");
            }
            public void Dispose()
            {
                if (disposed) return; disposed = true; properties.Dispose(); saved.Dispose();
            }
        }

        internal static byte[] EncodeMaterialValues(MaterialState state)
        {
            ResidentAssetPipeline.ValidateMaterial(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(state.ShaderBinding); writer.Write(state.RuntimeShaderBinding); writer.Write(state.OwnerBinding); writer.Write(state.SerializedKeywords);
                writer.Write(state.RenderQueue); writer.Write(state.GlobalIlluminationFlags); writer.Write(state.EnableInstancing); writer.Write(state.DoubleSidedGI);
                writer.Write(state.Keywords.Length); foreach (var item in state.Keywords) writer.Write(item);
                writer.Write(state.DisabledPasses.Length); foreach (var item in state.DisabledPasses) writer.Write(item);
                writer.Write(state.OverrideTags.Length); foreach (var item in state.OverrideTags) { writer.Write(item.Name); writer.Write(item.Value); }
                writer.Write(state.SavedProperties.Length);
                foreach (var item in state.SavedProperties) { writer.Write(item.Kind); writer.Write(item.Name); writer.Write(item.TextureBinding); WriteBytes(writer, item.Value); }
                writer.Write(state.HasRuntimeSheet); writer.Write(state.RuntimeFlags & 3U);
                if (state.RuntimeProperties != null) writer.Write(Newtonsoft.Json.JsonConvert.SerializeObject(state.RuntimeProperties));
                return stream.ToArray();
            }
        }
        private static void ResetMaterialTagDirectories(IntPtr shared)
        {
            var data = Read(shared, 312);
            var count = BitConverter.ToUInt64(data, 264); var capacity = BitConverter.ToUInt64(data, 272) >> 1;
            if (count > capacity || capacity > 1048576 || (count != 0 && Pointer(data, 248) == IntPtr.Zero))
                throw Error("target material disabled-pass allocation is invalid");
            var first = Pointer(data, 288); var last = Pointer(data, 296); var capacityEnd = Pointer(data, 304);
            if (last.ToInt64() < first.ToInt64() || capacityEnd.ToInt64() < last.ToInt64() || (last.ToInt64() - first.ToInt64()) % 8 != 0
                || (first == IntPtr.Zero && (last != IntPtr.Zero || capacityEnd != IntPtr.Zero))) throw Error("target material tag-vector allocation is invalid");
            // Both containers own only trivial ShaderTagID values. Keeping the
            // existing target allocation and setting size/end cannot leak or
            // retain native object references. Capacity Canon is a later step.
            ResidentNativeMemory.Write(IntPtr.Add(shared, 264), BitConverter.GetBytes(0UL));
            ResidentNativeMemory.Write(IntPtr.Add(shared, 296), BitConverter.GetBytes(first.ToInt64()));
        }

        // Borrowed, read-only std::map inputs for UnityPropertySheet::operator=.
        // The engine deep-copies these nodes into its own allocator. No pointer
        // from this buffer is installed into a live Material or later freed by
        // Unity. Trees retain complete red/black invariants for the copy's
        // resulting target tree, including non-perfect tree sizes.
        internal sealed class BorrowedSavedMaterialSheet : IDisposable
        {
            private IntPtr storage;
            internal IntPtr Pointer => storage != IntPtr.Zero ? storage : throw Error("borrowed saved-material input is disposed");
            internal BorrowedSavedMaterialSheet(int[] kinds, int[] keys, byte[][] values)
            {
                if (kinds == null || keys == null || values == null || kinds.Length != keys.Length || keys.Length != values.Length || keys.Length > 12288)
                    throw Error("borrowed saved-material directory shape differs");
                for (var index = 0; index < keys.Length; index++)
                    if (kinds[index] < 0 || kinds[index] > 2 || values[index] == null || values[index].Length != (kinds[index] == 0 ? 4 : kinds[index] == 1 ? 16 : 20))
                        throw Error("borrowed saved-material value layout differs");
                var bytes = 48;
                var maps = new[] { 2, 0, 1 }.Select(kind => Enumerable.Range(0, keys.Length).Where(index => kinds[index] == kind).OrderBy(index => keys[index]).ToArray()).ToArray();
                for (var map = 0; map < maps.Length; map++) {
                    var rows = maps[map]; if (rows.Length > 4096 || rows.Select(index => keys[index]).Distinct().Count() != rows.Length)
                        throw Error("borrowed saved-material keys repeat or exceed their bound");
                    bytes = checked(bytes + (rows.Length + 1) * (map == 0 ? 56 : map == 1 ? 40 : 48));
                }
                storage = Marshal.AllocHGlobal(bytes);
                try {
                    var output = new byte[bytes]; var cursor = 48;
                    void PointerAt(int offset, int target) => Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(storage, target).ToInt64()), 0, output, offset, 8);
                    for (var map = 0; map < maps.Length; map++) {
                        var rows = maps[map]; var stride = map == 0 ? 56 : map == 1 ? 40 : 48; var sentinel = cursor; cursor += stride;
                        PointerAt(map * 16, sentinel); Buffer.BlockCopy(BitConverter.GetBytes((ulong)rows.Length), 0, output, map * 16 + 8, 8);
                        output[sentinel + 24] = 1; output[sentinel + 25] = 1;
                        var positions = new int[rows.Length];
                        for (var index = 0; index < rows.Length; index++) { positions[index] = cursor; cursor += stride; }
                        var redLevel = 0; for (var remainder = rows.Length - 1; remainder >= 0; remainder = remainder / 2 - 1) redLevel++;
                        int Build(int first, int last, int parent, int level) {
                            if (last < first) return sentinel;
                            var middle = first + (last - first) / 2; var position = positions[middle]; var row = rows[middle];
                            PointerAt(position, Build(first, middle - 1, position, level + 1)); PointerAt(position + 8, parent);
                            PointerAt(position + 16, Build(middle + 1, last, position, level + 1)); output[position + 24] = (byte)(level == redLevel ? 0 : 1);
                            Buffer.BlockCopy(BitConverter.GetBytes(keys[row]), 0, output, position + 28, 4); Buffer.BlockCopy(values[row], 0, output, position + 32, values[row].Length);
                            return position;
                        }
                        PointerAt(sentinel, rows.Length == 0 ? sentinel : positions[0]); PointerAt(sentinel + 8, Build(0, rows.Length - 1, sentinel, 0));
                        PointerAt(sentinel + 16, rows.Length == 0 ? sentinel : positions[rows.Length - 1]);
                    }
                    if (cursor != bytes) throw Error("borrowed saved-material allocation accounting differs");
                    Marshal.Copy(output, 0, storage, output.Length);
                } catch { Dispose(); throw; }
            }
            public void Dispose() { var old = storage; storage = IntPtr.Zero; if (old != IntPtr.Zero) Marshal.FreeHGlobal(old); }
        }
        private sealed class MaterialValueFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Binary(IntPtr destination, IntPtr source);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Writable(IntPtr owner, int flags);
            internal readonly Binary ResetWithShader, CopySaved;
            internal readonly Writable GetWritableShared;
            private readonly Binary stringAssign;
            internal MaterialValueFunctions()
            {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                ResetWithShader = Bind<Binary>(engine.BaseAddress, 0x7b1d70, 546, "810dcb8fcbe5fae202407c01436d5d593d683f0f9c108fb91ec4dcbc909ddace");
                CopySaved = Bind<Binary>(engine.BaseAddress, 0x7a8d00, 489, "a420ba7382364975137250729098b2195a1023d224ea0d4ffedf5716257c4092");
                GetWritableShared = Bind<Writable>(engine.BaseAddress, 0x25bb00, 64, "5cad48c1104d2e99e17105b8ed8966059990074b448d4892817474970ca9b1e6");
                stringAssign = Bind<Binary>(engine.BaseAddress, 0x8ec60, 121, "886bc9382c00af9058d9e56a0803173780e0bbc30b4f002560cd6b5d336578ae");
            }
            internal void CopyString(IntPtr target, string text)
            {
                var chars = new UTF8Encoding(false, true).GetBytes(text + "\0");
                if (chars.Length > 16385) throw Error("material serialized keyword string exceeds native bounds");
                var input = Marshal.AllocHGlobal(40 + chars.Length);
                try {
                    var bytes = new byte[40 + chars.Length]; Buffer.BlockCopy(chars, 0, bytes, 40, chars.Length);
                    Buffer.BlockCopy(BitConverter.GetBytes(IntPtr.Add(input, 40).ToInt64()), 0, bytes, 0, 8);
                    // cap=0 tells native assign to retain a borrowed external
                    // pointer. A positive cap is essential to force deep copy.
                    Buffer.BlockCopy(BitConverter.GetBytes((ulong)chars.Length), 0, bytes, 8, 8);
                    Buffer.BlockCopy(BitConverter.GetBytes((ulong)chars.Length - 1), 0, bytes, 24, 8);
                    Marshal.Copy(bytes, 0, input, bytes.Length); stringAssign(target, input);
                    if (ResidentNativeMemory.String(target) != text) throw Error("material serialized keywords failed independent readback");
                } finally { Marshal.FreeHGlobal(input); }
            }
            private static T Bind<T>(IntPtr module, int rva, int extent, string expected) where T : Delegate
            {
                var address = IntPtr.Add(module, rva);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(Read(address, extent))).Replace("-", "").ToLowerInvariant() != expected)
                        throw Error("pinned Material native entry differs: " + rva.ToString("x"));
                return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }
        }
    }
}
