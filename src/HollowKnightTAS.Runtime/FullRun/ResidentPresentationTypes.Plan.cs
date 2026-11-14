using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        internal sealed class Plan
        {
            private readonly Data source;
            private readonly ResidentObjectDirectory directory;
            private readonly ResidentMutationScope scope;
            private readonly Func<string,UnityEngine.Object> resolve;
            private readonly Func<object,string?> bind;
            private readonly Action admitted;
            private bool applied;
            internal Plan(Data saved,ResidentObjectDirectory directory,ResidentMutationScope scope,
                Func<string,UnityEngine.Object> resolve,Func<object,string?> bind,Action admitted)
            {
                RequireCompleteCoverage(saved);
                source=Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Newtonsoft.Json.JsonConvert.SerializeObject(saved)) ?? throw Error("snapshot clone failed");
                this.directory=directory; this.scope=scope; this.resolve=resolve; this.bind=bind; this.admitted=admitted;
                var actual=Capture(directory,bind); RequireCompleteCoverage(actual);
                if (source.Lights.Present) RequireLightServiceDependencies(source.Lights,actual.Lights);
                if(!source.Rows.Select(row=>row.Owner+"/"+row.Kind).SequenceEqual(actual.Rows.Select(row=>row.Owner+"/"+row.Kind)) || source.Sorting.Present!=actual.Sorting.Present) throw Error("actual type directory differs");
                foreach(var row in source.Rows) {
                    if(!(resolve(row.Owner) is Component owner) || Kind(owner.GetType())!=row.Kind || !scope.Owns(owner)) throw Error("target owner is outside mutation scope");
                    if(row.Kind=="text-mesh") {
                        if (row.Font.Length != 0 && (!(resolve(row.Font) is Font font) || font.dynamic != row.FontCacheDependency))
                            throw Error("target TextMesh Font differs from its validated asset dependency kind");
                        var slot=M.Ptr(IntPtr.Add(M.Owner(owner),136));
                        if(row.Mesh.Length==0 ? slot!=IntPtr.Zero : slot!=M.Owner(resolve(row.Mesh)))
                            throw Error("TextMesh owned mesh was not adopted before asset preparation");
                    }
                }
            }
            internal void Apply()
            {
                if(applied) throw Error("plan already applied"); scope.Require(); admitted();
                foreach(var row in source.Rows) {
                    var owner=resolve(row.Owner); var pointer=M.Owner(owner); var ranges=Ranges(row.Kind);
                    for(var index=0;index<ranges.Length;index++) M.Write(IntPtr.Add(pointer,ranges[index][0]),row.Scalars[index]);
                    if(row.Kind=="light") ApplyLightShared(row,pointer,resolve,admitted);
                    if(row.Kind=="text-mesh") {
                        // Mesh cache identity is already adopted from its native
                        // owning slot before assets are allocated. Do not invoke
                        // SetText/SetFont, which would regenerate geometry.
                        M.WriteString(IntPtr.Add(pointer,56),row.Text,admitted);
                        // TextMesh::SetFont (0xc69260) writes this PPtr and
                        // calls ApplyToMesh. The owned Mesh is already restored
                        // independently, so retain its captured geometry here.
                        M.Write(IntPtr.Add(pointer,96),BitConverter.GetBytes(ResidentNativeAssetReference.ResolveId(row.Font,typeof(Font),resolve)));
                    }
                }
                if(source.Sorting.Present) {
                    var manager=SortingManager(); if(manager==IntPtr.Zero) throw Error("sorting manager disappeared");
                    var owners=source.Sorting.Owners.Select(key=>M.Owner(resolve(key))).ToArray();
                    WriteArray(manager,8,owners.SelectMany(pointer=>BitConverter.GetBytes(pointer.ToInt64())).ToArray(),owners.Length);
                    WriteArray(IntPtr.Add(manager,32),28,source.Sorting.Values,source.Sorting.Values.Length/28);
                    var root=IntPtr.Add(manager,64);
                    // Every actual node is detached; unknown/nonresident nodes
                    // were rejected before any write by complete capture.
                    foreach(var row in source.Rows.Where(row=>row.Kind=="sorting-group")) {
                        var pointer=M.Owner(resolve(row.Owner)); M.Write(IntPtr.Add(pointer,88),new byte[16]);
                        M.Write(IntPtr.Add(pointer,104),BitConverter.GetBytes(pointer.ToInt64()));
                    }
                    var nodes=source.Sorting.Dirty.Select(key=>IntPtr.Add(M.Owner(resolve(key)),88)).ToArray();
                    M.Write(root,BitConverter.GetBytes((nodes.Length==0?root:nodes[nodes.Length-1]).ToInt64())
                        .Concat(BitConverter.GetBytes((nodes.Length==0?root:nodes[0]).ToInt64())).ToArray());
                    for(var index=0;index<nodes.Length;index++)
                        M.Write(nodes[index],BitConverter.GetBytes((index==0?root:nodes[index-1]).ToInt64())
                            .Concat(BitConverter.GetBytes((index+1==nodes.Length?root:nodes[index+1]).ToInt64())).ToArray());
                }
                ApplyLightRegistry(source,directory,resolve,admitted);
                applied=true; scope.Require(); admitted();
            }
            private void WriteArray(IntPtr header,int stride,byte[] bytes,int count)
            {
                // POD pointer/data arrays have no element destructors. Preserve
                // target label/storage, reserve through the engine, publish size
                // only after every value was written.
                M.Write(IntPtr.Add(header,16),BitConverter.GetBytes(0UL));
                M.Reserve(header,count,stride,stride==8?8:4,admitted);
                var body=M.Read(header,32);
                if(bytes.Length!=0) M.Write(M.Ptr(body,0),bytes);
                M.Write(IntPtr.Add(header,16),BitConverter.GetBytes((ulong)count));
            }
            internal void Verify()
            {
                if(!applied) throw Error("plan has not applied"); scope.Require(); admitted();
                RequireEqual(source,Capture(directory,bind)); admitted();
            }
        }
    }
}
