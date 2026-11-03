using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        // TextMesh::GetMesh constructs one owned Mesh and MainThreadCleanup
        // destroys it directly (not a refcounted shared-data release). Reuse the
        // target slot as the source dynamic asset identity before allocation.
        // Generated geometry is then restored by the ordinary Mesh adapter.
        internal static IReadOnlyDictionary<string,NativeObject> PrepareOwnedAssets(Data saved,
            ResidentObjectDirectory directory,ResidentAssetBindings.AssetRecord[] manifest,ResidentMutationScope scope)
        {
            ValidateOwnedAssetSlots(saved,manifest);
            var result=new Dictionary<string,NativeObject>(StringComparer.Ordinal);
            var seen=new HashSet<IntPtr>();
            foreach(var row in saved.Rows.Where(row=>row.Kind=="text-mesh")) {
                if(!(directory.Resolve(row.Owner) is TextMesh owner) || owner.GetType()!=typeof(TextMesh)
                    || directory.Ownership.IsExecution(owner)) throw Error("TextMesh owned-asset target owner differs");
                var pointer=M.Ptr(IntPtr.Add(M.Owner(owner),136));
                if(row.Mesh.Length==0) {
                    if(pointer!=IntPtr.Zero) throw Error("target TextMesh owns a mesh absent in source; retirement is unsupported");
                    continue;
                }
                if(pointer==IntPtr.Zero) throw Error("target TextMesh lacks source owned mesh; ownership construction is unsupported");
                RequireUniqueTextMeshOwner(owner,pointer);
                if(!seen.Add(pointer)) throw Error("multiple TextMesh slots own one target mesh");
                var mesh=(Mesh)ResidentTargetAssetLookup.BindRegistered(M.I32(M.Read(IntPtr.Add(pointer,8),4),0),pointer,typeof(Mesh),scope);
                if(M.Owner(mesh)!=pointer || mesh.GetType()!=typeof(Mesh)) throw Error("owned TextMesh mesh lacks its unique exact wrapper");
                result.Add(row.Mesh,mesh);
            }
            ValidateOwnedAssetAdoptions(manifest,result);
            return new ReadOnlyDictionary<string,NativeObject>(result);
        }
        internal static void ValidateOwnedAssetSlots(Data saved,ResidentAssetBindings.AssetRecord[] manifest)
        {
            Validate(saved); ResidentAssetBindings.ValidateManifest(manifest);
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var row in saved.Rows.Where(row=>row.Kind=="text-mesh" && row.Mesh.Length!=0)) {
                if(!row.MeshOwned || !keys.Add(row.Mesh)) throw Error("source TextMesh mesh has no unique native owning slot");
                var records=manifest.Where(record=>record.Binding==row.Mesh).ToArray();
                if(records.Length!=1 || records[0].Kind!="dynamic" || records[0].Type.Resolve()!=typeof(Mesh))
                    throw Error("source owned TextMesh mesh is not an exact dynamic Mesh asset");
            }
        }
        internal static void ValidateOwnedAssetAdoptions(ResidentAssetBindings.AssetRecord[] manifest,IReadOnlyDictionary<string,NativeObject> adopted)
        {
            ResidentAssetBindings.ValidateManifest(manifest);
            if(adopted==null) throw new ArgumentNullException(nameof(adopted));
            var pointers=new HashSet<IntPtr>();
            foreach(var pair in adopted) {
                var records=manifest.Where(record=>record.Binding==pair.Key).ToArray();
                if(records.Length!=1 || records[0].Kind!="dynamic" || records[0].Type.Resolve()!=typeof(Mesh)
                    || !ResidentObjectDirectory.IsLive(pair.Value) || pair.Value.GetType()!=typeof(Mesh)
                    || !pointers.Add(M.Owner(pair.Value))) throw Error("owned dynamic asset adoption type, provenance, or unique identity differs");
            }
        }
        private static void RequireUniqueTextMeshOwner(Component expected,IntPtr mesh)
        {
            ResidentLoadedAssetIndex.RequireUniqueOwnerSlot(expected,136,mesh);
        }
    }
}
