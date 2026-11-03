using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentCameraCanvasState
    {
        // Capture this receipt BEFORE CanonColdMesh swaps SharedMeshData. Compose
        // Apply/Verify with ResidentRendererState.MeshConsumerRebind. Canvas UI
        // keeps independent retains, so assigning the Mesh's replacement alone
        // leaves these CPU consumers pointing at the old geometry allocation.
        internal static MeshConsumerRebind PrepareMeshConsumerRebind(ResidentObjectDirectory directory, Mesh mesh, ResidentMutationScope scope, Action admitted)
            => new MeshConsumerRebind(directory, mesh, scope, admitted);
        internal sealed class MeshConsumerRebind
        {
            private readonly ResidentObjectDirectory directory;
            private readonly Mesh mesh;
            private readonly ResidentMutationScope scope;
            private readonly Action admitted;
            private readonly IntPtr meshOwner, originalShared;
            private readonly List<RendererUse> renderers = new List<RendererUse>();
            private readonly List<InstructionUse> instructions = new List<InstructionUse>();
            private readonly DirtyGroup[] dirty;
            private bool applied;
            private sealed class RendererUse
            {
                internal CanvasRenderer Owner = null!;
                internal IntPtr Pointer;
                internal byte[][] Scalars = Array.Empty<byte[]>();
                internal byte[] Before = Array.Empty<byte>();
            }
            private sealed class InstructionUse
            {
                internal Canvas Owner = null!;
                internal IntPtr Canvas, Buffer, Pointer;
                internal int Index;
                internal byte[] Before = Array.Empty<byte>();
            }
            internal MeshConsumerRebind(ResidentObjectDirectory directory, Mesh mesh, ResidentMutationScope scope, Action admitted)
            {
                this.directory = directory ?? throw new ArgumentNullException(nameof(directory)); this.mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
                this.scope = scope ?? throw new ArgumentNullException(nameof(scope)); this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                scope.Require(); admitted(); meshOwner = M.Owner(mesh); originalShared = M.Ptr(IntPtr.Add(meshOwner, 56));
                var shared = M.Read(originalShared, 400);
                var meshes = ResidentLoadedAssetIndex.Family(ResidentLoadedAssetIndex.MeshClass,out var checkMeshes);
                var meshAliases = meshes.Where(owner => M.Ptr(IntPtr.Add(owner.Pointer,56)) == originalShared).ToArray();
                if (meshAliases.Length != 1 || meshAliases[0].Pointer != meshOwner || !ReferenceEquals(meshAliases[0].Borrow<Mesh>(),mesh)) throw Error("Mesh Canon needs a unified plan for aliased Mesh owners");
                var canvasRenderers = ResidentLoadedAssetIndex.Family(ResidentLoadedAssetIndex.CanvasRendererClass,out var checkRenderers);
                foreach (var member in canvasRenderers) {
                    var pointer = member.Pointer; var bytes = M.Read(pointer, 304);
                    if (M.Ptr(bytes, 72) != originalShared) continue;
                    var renderer = member.Borrow<CanvasRenderer>();
                    RequireOwned(renderer); renderers.Add(new RendererUse { Owner = renderer, Pointer = pointer, Before = bytes, Scalars = CaptureRanges(bytes, CanvasRendererRanges) });
                }
                var canvases = ResidentLoadedAssetIndex.Family(ResidentLoadedAssetIndex.CanvasClass,out var checkCanvases);
                foreach (var member in canvases) {
                    var native = member.Pointer; var header = M.Read(IntPtr.Add(native, 240), 32); var count = M.U64(header, 16); var capacity = M.U64(header, 24) >> 1;
                    if (count > capacity || capacity > Maximum || count * 720UL > MaximumBytes) throw Error("Canvas instruction array exceeds the consumer bound");
                    var buffer = M.Ptr(header, 0);
                    for (var index = 0; index < (int)count; index++) {
                        var pointer = IntPtr.Add(buffer, index * 720); var bytes = M.Read(pointer, 720); if (M.Ptr(bytes, 512) != originalShared) continue;
                        var canvas = member.Borrow<Canvas>(); RequireOwned(canvas);
                        if (!Function<FenceDone>(0x725ac0, "40534883ec30488bd9e86217000084c0742648833b007420")(IntPtr.Add(native, 424))) throw Error("Canvas Mesh consumer has an active batch job");
                        instructions.Add(new InstructionUse { Owner = canvas, Canvas = native, Buffer = buffer, Pointer = pointer, Index = index, Before = bytes });
                    }
                }
                var spriteAliases = ResidentLoadedAssetIndex.Family(ResidentLoadedAssetIndex.SpriteRendererClass,out var checkSprites).Count(owner => {
                    var cache = M.Ptr(IntPtr.Add(owner.Pointer, 584)); return cache != IntPtr.Zero && M.Ptr(IntPtr.Add(cache, 96)) == originalShared;
                });
                checkMeshes(); checkRenderers(); checkCanvases(); checkSprites();
                if (spriteAliases != 0) throw Error("Mesh Canon needs a unified plan for Sprite-owned shared geometry");
                if (M.I32(shared, 4) != 1 + renderers.Count + instructions.Count) throw Error("Mesh Canon has an unbound native SharedMeshData retain");
                var reader = new Reader(directory, directory.Bind); dirty = CanonicalDirty(reader); reader.RequireFrozen(); RequireIdentity(false);
            }
            private static DirtyGroup[] CanonicalDirty(Reader reader)
            {
                var pending = CaptureDirty(reader,out var empty);
                if (empty.Count != 0) throw Error("Mesh consumer verification requires canonical Canvas buckets");
                return pending;
            }
            private void RequireOwned(Component owner)
            {
                if (directory.Bind(owner) == null || !scope.Owns(owner) || directory.Ownership.IsExecution(owner)) throw Error("Mesh Canon would mutate an execution or nonresident Canvas consumer");
            }
            internal void Apply()
            {
                if (applied) throw Error("Canvas Mesh consumer refresh is single-use"); scope.Require(); admitted(); RequireIdentity(false);
                var replacement = M.Ptr(IntPtr.Add(meshOwner, 56));
                if (replacement == originalShared) throw Error("Canvas consumer refresh precedes the Mesh allocation swap");
                try {
                    foreach (var row in renderers) {
                        scope.Require(); admitted(); Function<Binary>(0x233510, "48895c2408488974241048897c24185541564157488d6c24")(row.Pointer, meshOwner);
                        ApplyRanges(row.Pointer, CanvasRendererRanges, row.Scalars);
                    }
                    var materials = Marshal.AllocHGlobal(32);
                    try {
                        var header = new byte[32]; System.Buffer.BlockCopy(BitConverter.GetBytes(8UL), 0, header, 16, 8); M.Write(materials, header);
                        foreach (var row in instructions) {
                            scope.Require(); admitted(); Function<SyncVertex>(0x231270, "48896c2420565741574883ec204c8974245033ff4c8bb200")(IntPtr.Add(row.Canvas, 424), row.Pointer, replacement, materials);
                            // Original instruction scalar/cache fields, material
                            // and texture IDs stay local; only the retained share
                            // is rebound by the real native retain/release helper.
                            M.Write(row.Pointer, M.Slice(row.Before, 0, 512)); M.Write(IntPtr.Add(row.Pointer, 520), M.Slice(row.Before, 520, 200));
                        }
                    } finally { Marshal.FreeHGlobal(materials); }
                    RestoreDirty(dirty, directory, directory.Bind, directory.Resolve, admitted); applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            {
                if (!applied) throw Error("Canvas Mesh consumer verification precedes refresh"); scope.Require(); admitted(); RequireIdentity(true);
                var reader = new Reader(directory, directory.Bind); if (Json(dirty) != Json(CanonicalDirty(reader))) throw Error("Mesh consumer refresh changed the UI dirty map"); reader.RequireFrozen(); admitted();
            }
            private void RequireIdentity(bool rebound)
            {
                if (M.Owner(mesh) != meshOwner) throw Error("Mesh native owner changed during Canvas consumer refresh");
                var expected = rebound ? M.Ptr(IntPtr.Add(meshOwner, 56)) : originalShared;
                foreach (var row in renderers) {
                    RequireOwned(row.Owner); var bytes = M.Read(row.Pointer, 304);
                    if (M.Owner(row.Owner) != row.Pointer || M.Ptr(bytes, 72) != expected
                        || !M.Slice(bytes, 56, 16).SequenceEqual(M.Slice(row.Before, 56, 16)) || !M.Slice(bytes, 80, 224).SequenceEqual(M.Slice(row.Before, 80, 224)))
                        throw Error("CanvasRenderer Mesh refresh changed retained state or failed to rebind its share");
                }
                foreach (var row in instructions) {
                    RequireOwned(row.Owner); var header = M.Read(IntPtr.Add(row.Canvas, 240), 32); var bytes = M.Read(row.Pointer, 720);
                    if (M.Owner(row.Owner) != row.Canvas || M.Ptr(header, 0) != row.Buffer || M.U64(header, 16) <= (ulong)row.Index || M.Ptr(bytes, 512) != expected
                        || !M.Slice(bytes, 0, 512).SequenceEqual(M.Slice(row.Before, 0, 512)) || !M.Slice(bytes, 520, 200).SequenceEqual(M.Slice(row.Before, 520, 200)))
                        throw Error("Canvas instruction Mesh refresh changed retained state or failed to rebind its share");
                }
            }
        }
    }
}
