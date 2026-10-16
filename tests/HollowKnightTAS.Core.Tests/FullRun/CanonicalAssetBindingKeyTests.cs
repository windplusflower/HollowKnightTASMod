using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalAssetBindingKeyTests
{
    [TestMethod]
    public void Same_name_scene_identity_never_substitutes_for_original_asset_and_duplicate_candidates_reject()
    {
        const string resource = "asset:UnityEngine.GameObject:Audio Player Actor 2D";
        const string scene = "Town/Audio Player Actor 2D[0]";
        Assert.AreEqual(resource, CanonicalAssetBindingKey.UniqueOriginal(new[] { resource }, resource, value => value));
        foreach (var candidates in new[] { Array.Empty<string>(), new[] { scene }, new[] { resource, scene }, new[] { resource, resource } })
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalAssetBindingKey.UniqueOriginal(candidates, resource, value => value));
    }
    [TestMethod]
    public void Exact_type_assembly_and_full_name_are_retained_without_guessing_asset_name()
    {
        var result = CanonicalAssetBindingKey.Resolve("asset:System.String:original:name", new[] { typeof(string).Assembly }, type => type == typeof(string));
        Assert.AreSame(typeof(string), result.Item1);
        Assert.AreEqual("original:name", result.Item2);
        Assert.AreEqual("", CanonicalAssetBindingKey.Resolve("asset:System.String:", new[] { typeof(string).Assembly }, _ => true).Item2);
        foreach (var binding in new[] { "bad", "asset:", "asset::name", "Asset:System.String:name", "asset:system.string:name", "asset:Missing:name" })
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalAssetBindingKey.Resolve(binding, new[] { typeof(string).Assembly }, _ => true));
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalAssetBindingKey.Resolve("asset:System.String:name", new[] { typeof(string).Assembly }, _ => false));
    }
    [TestMethod]
    public void Duplicate_full_names_in_different_assemblies_are_rejected_and_forwarded_identity_is_shared()
    {
        static Type AssetType(string assembly) => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(assembly), AssemblyBuilderAccess.Run)
            .DefineDynamicModule(assembly).DefineType("Original.Asset", TypeAttributes.Public).CreateType()!;
        var first = AssetType("AssetBindingFirst"); var second = AssetType("AssetBindingSecond");
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalAssetBindingKey.Resolve("asset:Original.Asset:name",
            new[] { first.Assembly, second.Assembly }, _ => true));
        var result = CanonicalAssetBindingKey.Resolve("asset:Original.Asset:name", new[] { first.Assembly, first.Assembly }, _ => true);
        Assert.AreSame(first, result.Item1);
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalAssetBindingKey.Resolve("asset:System.String:name", new Assembly[2001], _ => true));
    }
}
