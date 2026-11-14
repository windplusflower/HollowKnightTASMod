using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        private static bool IsEmptyNativeArray(byte[] bytes, int offset)
        {
            var size = BitConverter.ToUInt64(bytes, offset + 16); var capacity = BitConverter.ToUInt64(bytes, offset + 24) >> 1;
            if (size > capacity || capacity > MaximumBytes || size != 0 && Pointer(bytes, offset) == IntPtr.Zero)
                throw Error("native asset array shape exceeds its bounds");
            return Pointer(bytes, offset) == IntPtr.Zero && size == 0 && BitConverter.ToUInt64(bytes, offset + 24) == 1;
        }
        private static string[] MaterialCoverageGaps(MaterialState state)
        {
            ResidentAssetPipeline.ValidateMaterial(state); var result = new System.Collections.Generic.List<string>(); var cache = state.Caches;
            if (cache == null) result.Add("material/native-cache-domain-not-captured");
            if (!state.HasRuntimeSheet || state.RuntimeProperties == null || state.ShaderBinding.Length == 0) result.Add("material/absent-runtime-shader-or-sheet");
            if (state.RuntimeBufferOverride) result.Add("material/external-constant-buffer");
            if (cache != null) {
                if (!cache.BuildStacksEmpty || !cache.RuntimeStacksEmpty) result.Add("material/virtual-texture-stack-state");
                if (state.HasRuntimeSheet && (cache.SharedReferences < 1 || cache.PropertySheetReferences < 1)) result.Add("material/invalid-native-reference-count");

            }
            if (state.RuntimeProperties != null) result.AddRange(state.RuntimeProperties.CoverageGaps);
            return result.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        }
        internal static void RequireMaterialCanonicalizable(MaterialState state)
        {
            var gaps = MaterialCoverageGaps(state);
            if (state.RuntimeProperties != null) ResidentShaderPropertySheet.RequireCompleteCoverage(state.RuntimeProperties);
            if (gaps.Length != 0) throw Error("Material cannot enter common Canon: " + string.Join(";", gaps));
        }
        private static void RequireMaterialComplete(MaterialState state)
        {
            RequireMaterialCanonicalizable(state);
            var cache = state.Caches!;
            if (!cache.MainTextureUncached || !cache.MainColorUncached || !cache.HashesEmpty || !cache.TextureUnionEmpty
                || !cache.BufferAbsent || !cache.DisplayListsEmpty || !cache.SmallIndexMatchesOwner || cache.SharedReferences != 1 || cache.PropertySheetReferences != 1
                || !state.TextureDirty || !state.ConstantBufferDirty || !state.RuntimeTextureDirty || !state.RuntimeConstantBufferDirty || !state.RuntimeStacksDirty
                || (state.RuntimeFlags & 4U) == 0) throw Error("Material cache normalization did not read back");
            var gaps = MaterialCoverageGaps(state);
            if (gaps.Length != 0) throw Error("Material complete native readback is not in S0: " + string.Join(";", gaps));
        }
        // D: complete saved/runtime properties, shader/owner references, queue,
        // keywords, pass/tag data and name/hideFlags.
        // S0: unique SharedMaterialData; empty owned GPU buffer, display lists,
        // texture-ID union and virtual-texture handles; invalidated hashes and
        // main-property IDs; all material cache dirties set.
        // Dependency: native property/tag/keyword IDs, local allocator storage,
        // shader pointers and smallMaterialIndex are rebuilt in this process.
        // Renderer components retain Material PPtr identities. Camera/cull/
        // RenderLoop cached submissions are a separate global adapter domain.
        // This substep neither clears that global gap nor certifies all assets.
        internal static ColdMaterialReceipt CanonColdMaterial(Snapshot source, Material owner, ResidentMutationScope scope,
            Func<bool> admitted, Func<string, NativeObject> resolve, Func<object, string?> bind)
        {
            if (scope == null || admitted == null || resolve == null || bind == null) throw Error("cold Material Canon lacks admission or reference dependencies");
            scope.Require(); if (!admitted()) throw Error("cold Material Canon is outside the boundary transaction");
            RequireColdMaterialDomain(source, owner); var before = new MaterialState(); CaptureMaterialNative(owner, before, bind);
            var values = EncodeMaterialValues(source.Material!);
            if (owner.name != source.Name || (int)owner.hideFlags != source.HideFlags || !values.SequenceEqual(EncodeMaterialValues(before)))
                throw Error("cold Material source values changed before canonicalization");
            var functions = new MaterialCanonFunctions(); var pointer = ResidentNativeMemory.Owner(owner); IntPtr replacement = IntPtr.Zero;
            scope.Mutate(() => {
                // Acquire is the engine's real local retained share. It may
                // realize derived caches; the subsequent COW/reset removes
                // those caches and the typed transfer restores original D.
                var acquired = functions.Acquire(pointer);
                if (acquired == IntPtr.Zero) throw Error("Material shared-data acquisition returned no retained owner");
                try {
                    functions.Unshare(pointer); replacement = ResidentNativeMemory.Ptr(IntPtr.Add(pointer, 184));
                    if (replacement == IntPtr.Zero || replacement == acquired || BitConverter.ToInt32(Read(IntPtr.Add(replacement, 12), 4), 0) != 1)
                        throw Error("Material COW did not replace its retained shared data with a unique local owner");
                } finally { functions.Release(IntPtr.Add(acquired, 8)); }
                using (var transfer = PrepareMaterialValues(source, owner, resolve, bind, admitted)) transfer.Apply();
                var current = ResidentNativeMemory.Ptr(IntPtr.Add(pointer, 184));
                if (current != replacement) throw Error("Material value transfer replaced the canonical shared-data epoch");
                ResidentNativeMemory.Write(IntPtr.Add(current, 224), new byte[12]);
                // Only the three declared bitfields are state. The upper bits
                // are C++ padding and retain target-local bytes.
                var oldFlags = BitConverter.ToUInt32(Read(IntPtr.Add(current, 236), 4), 0);
                ResidentNativeMemory.Write(IntPtr.Add(current, 236), BitConverter.GetBytes((oldFlags & ~7U) | (source.Material!.RuntimeFlags & 3U) | 4U));
                ResidentNativeMemory.Write(IntPtr.Add(current, 240), BitConverter.GetBytes(owner.GetInstanceID()));
                ResidentNativeMemory.Write(IntPtr.Add(current, 312), new byte[256]);
                ResidentNativeMemory.Write(IntPtr.Add(current, 576), new byte[] { 1, 1, 0 });
                ResidentNativeMemory.Write(IntPtr.Add(current, 680), new byte[] { 1 });
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 120), BitConverter.GetBytes(0xdeadbeefU).Concat(BitConverter.GetBytes(0xdeadbeefU)).ToArray());
                ResidentNativeMemory.Write(IntPtr.Add(pointer, 176), new byte[] { 1, 1 });
                scope.Require(); if (!admitted()) throw Error("cold Material Canon lost admission after cache reconstruction");
            });
            var receipt = new ColdMaterialReceipt(source, owner, replacement, values, admitted, bind); receipt.Verify(); return receipt;
        }
        internal static void RequireColdMaterialDomain(Snapshot source, Material owner)
        {
            if (source == null || source.Kind != "Material" || source.Material == null || source.Type.Resolve() != typeof(Material)
                || !ResidentObjectDirectory.IsLive(owner) || owner.GetType() != typeof(Material)) throw Error("cold Material Canon received another type or a dead wrapper");
            ResidentAssetPipeline.ValidateMaterial(source.Material);
            RequireMaterialCanonicalizable(source.Material);
            if (!source.Material.HasRuntimeSheet || source.Material.RuntimeProperties == null || source.Material.ShaderBinding.Length == 0
                || source.Material.RuntimeBufferOverride) throw Error("cold Material Canon requires ordinary loaded shader/property ownership");
            ResidentShaderPropertySheet.RequireCompleteCoverage(source.Material.RuntimeProperties);
            LoadingEngineClockProbe.RequirePinnedEngine();
            var native = Read(ResidentNativeMemory.Owner(owner), 224); RequireEmptyNativeArray(native, 192);
            var shared = Pointer(native, 184);
            if (shared == IntPtr.Zero) throw Error("cold Material has no SharedMaterialData");
            var state = Read(shared, 688);
            if (state[578] != 0) throw Error("cold Material has an external constant-buffer override");
            RequireEmptyNativeArray(state, 648);
        }
        internal sealed class ColdMaterialReceipt
        {
            private readonly Snapshot source;
            private readonly Material owner;
            private readonly IntPtr sharedEpoch;
            private readonly byte[] values;
            private readonly Func<bool> admitted;
            private readonly Func<object, string?> bind;
            internal ColdMaterialReceipt(Snapshot source, Material owner, IntPtr sharedEpoch, byte[] values, Func<bool> admitted, Func<object, string?> bind)
            { this.source = source.Copy(); this.owner = owner; this.sharedEpoch = sharedEpoch; this.values = (byte[])values.Clone(); this.admitted = admitted; this.bind = bind; }
            internal void Verify()
            {
                if (!admitted()) throw Error("cold Material readback lost its admitted boundary"); RequireColdMaterialDomain(source, owner);
                var native = Read(ResidentNativeMemory.Owner(owner), 224); var shared = Pointer(native, 184);
                if (shared != sharedEpoch || native[176] != 1 || native[177] != 1 || BitConverter.ToUInt32(native, 120) != 0xdeadbeefU
                    || BitConverter.ToUInt32(native, 124) != 0xdeadbeefU) throw Error("cold Material owner epoch, dirties or main-property cache differ");
                var state = Read(shared, 688);
                if (BitConverter.ToInt32(state, 12) != 1 || BitConverter.ToInt32(state, 240) != owner.GetInstanceID()
                    || (BitConverter.ToUInt32(state, 236) & 7U) != ((source.Material!.RuntimeFlags & 3U) | 4U)
                    || state.Skip(224).Take(12).Any(value => value != 0) || state.Skip(312).Take(256).Any(value => value != 0)
                    || Pointer(state, 568) != IntPtr.Zero || state[576] != 1 || state[577] != 1 || state[578] != 0 || state[680] != 1)
                    throw Error("cold Material shared cache, hash, GPU ownership or dependency binding differs");
                RequireEmptyNativeArray(state, 584); RequireEmptyNativeArray(state, 616); RequireEmptyNativeArray(state, 648);
                var observed = new MaterialState(); CaptureMaterialNative(owner, observed, bind);
                if (owner.name != source.Name || (int)owner.hideFlags != source.HideFlags || !values.SequenceEqual(EncodeMaterialValues(observed)))
                    throw Error("cold Material Canon changed retained source values");
            }
        }
        private sealed class MaterialCanonFunctions
        {
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr Retain(IntPtr owner);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Unary(IntPtr owner);
            internal readonly Retain Acquire;
            internal readonly Unary Unshare, Release;
            internal MaterialCanonFunctions()
            {
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                Acquire = Bind<Retain>(engine.BaseAddress, 0x7a9210, 366, "6101f0e636caa2d873ee22239359c3d0e4fdd0e241edd174449fcd3fb297a80f");
                Unshare = Bind<Unary>(engine.BaseAddress, 0x7b5260, 185, "ea5c718ccb298a88736a21c4a8fa6a215ee10983d7aaa85c86aa44973d58e26a");
                Release = Bind<Unary>(engine.BaseAddress, 0x2260a0, 84, "3c92f04a6ed96d59cb064326ae5d9baa985bb602845cd302a7406c50b825f897");
            }
            private static T Bind<T>(IntPtr module, int rva, int extent, string expected) where T : Delegate
            {
                var address = IntPtr.Add(module, rva);
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(Read(address, extent))).Replace("-", "").ToLowerInvariant() != expected)
                        throw Error("pinned Material Canon native entry differs: " + rva.ToString("x"));
                return (T)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
            }
        }
    }
}
