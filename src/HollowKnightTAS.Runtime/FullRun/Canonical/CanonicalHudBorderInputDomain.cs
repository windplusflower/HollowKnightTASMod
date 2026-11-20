using System;
using System.Collections.Generic;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalHudBorderInputDomain
    {
        internal const string Root = "HudBorder/PreAwakeInputs";
        private readonly CanonicalHudPresentationColdTemplate template;
        private readonly CanonicalCarryGraphCodec codec = CanonicalCarryFieldRegistry.Create(hudBorderInputDomain: true);
        internal CanonicalHudBorderInputDomain(CanonicalHudPresentationColdTemplate template) { this.template = template; }
        internal byte[] Capture()
        {
            template.RequireSnapshotReady(); template.RequireFrozenAssets();
            var source = template.LastBorderInputs ?? throw new InvalidDataException("Original Border pre-Awake inputs are absent.");
            CanonicalHudBorderInputs.Validate(source);
            string? Bind(object value) {
                var asset = template.BindOriginalAsset(value);
                return asset == null ? null : CanonicalHudPresentationColdTemplate.AssetBindingPrefix + asset;
            }
            var graph = codec.Capture(new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = source }, Bind);
            if (graph.Unsupported.Length != 0) throw new InvalidDataException("Original Border logical inputs are not closed: " + string.Join(";", graph.Unsupported));
            return codec.Encode(graph);
        }
        internal CanonicalHudBorderInputs.Data Decode(byte[] bytes)
        {
            template.RequireFrozenAssets();
            var data = new CanonicalHudBorderInputs.Data();
            object Resolve(string binding) {
                if (!binding.StartsWith(CanonicalHudPresentationColdTemplate.AssetBindingPrefix, StringComparison.Ordinal))
                    throw new InvalidDataException("Border logical input contains a foreign asset or gameplay binding.");
                return template.ResolveOriginalAsset(binding.Substring(CanonicalHudPresentationColdTemplate.AssetBindingPrefix.Length));
            }
            var graph = codec.Decode(bytes);
            var plan = codec.PrepareOwnedGraph(graph, new SortedDictionary<string, object>(StringComparer.Ordinal) { [Root] = data }, Resolve, view => {
                var saved = new CanonicalHudBorderInputs.Data(); var root = view.Root(Root);
                foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(CanonicalHudBorderInputs.Data)))
                    field.SetValue(saved, view.Field(root, field.DeclaringType!, field.Name));
                CanonicalHudBorderInputs.Validate(saved);
            });
            plan.Apply(); plan.VerifyReadback(); CanonicalHudBorderInputs.Validate(data);
            return data;
        }
    }
}
