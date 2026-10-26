using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalHudPresentationDirectoryTests
{
    [TestMethod] public void ExactRecursiveClosureIncludesOriginalButtonLabelAndSeventhFsm()
    {
        CanonicalHudPresentationDirectory.ValidateAbi();
        var definitions = CanonicalHudPresentationDirectory.Definitions;
        Assert.AreEqual(18, definitions.Length);
        Assert.AreEqual(13, CanonicalHudPresentationDirectory.RootPaths.Length);
        Assert.AreEqual(92, definitions.Sum(node => node.Components.Length));
        Assert.AreEqual(40, definitions.Sum(node => node.Components.Count(type => typeof(MonoBehaviour).IsAssignableFrom(type))));
        Assert.AreEqual(9, definitions.Sum(node => node.Components.Count(type => type == typeof(TMPro.TextMeshPro))));
        Assert.AreEqual(7, definitions.Sum(node => node.Components.Count(type => type == typeof(PlayMakerFSM))));
        var icon = definitions.Single(node => node.Path == "Charms/Confirm Action/ActionButtonIcon");
        CollectionAssert.AreEqual(new[] { "Label" }, icon.Children);
        Assert.IsTrue(definitions.Any(node => node.Path == icon.Path + "/Label" && node.SerializedGameObject == 805));
        foreach (var type in definitions.SelectMany(node => node.Components).Where(type => typeof(MonoBehaviour).IsAssignableFrom(type)).Distinct())
            Assert.IsTrue(CanonicalHudPresentationDirectory.ManagedFields(type).Length > 0);
        StringAssert.Contains(CanonicalHudPresentationDirectory.SchemaIdentity, "40mono9TMP7FSM");
    }
    [TestMethod] public void ColdShapeRejectsMissingChildForeignComponentAndReorderedComponents()
    {
        foreach (var node in CanonicalHudPresentationDirectory.Definitions) {
            CanonicalHudPresentationDirectory.RequireShape(node.Path, node.Components, node.Children, false);
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path,
                node.Components.Reverse().ToArray(), node.Children, false));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path,
                node.Components.Concat(new[] { typeof(SpriteRenderer) }).ToArray(), node.Children, false));
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path,
                node.Components, node.Children.Concat(new[] { "foreign-child" }).ToArray(), false));
            if (node.Children.Length > 0) Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path,
                node.Components, Array.Empty<string>(), false));
        }
    }
    [TestMethod] public void GeneratedTmpNameIsOnlyProvisionalShapeAndCannotEnterColdDefinition()
    {
        foreach (var node in CanonicalHudPresentationDirectory.Definitions) {
            var children = node.Children.Concat(new[] { "TMP SubMesh [perpetua_tmpro Material]" }).ToArray();
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path, node.Components, children, false));
            if (node.Components.Contains(typeof(TMPro.TextMeshPro)))
                CanonicalHudPresentationDirectory.RequireShape(node.Path, node.Components, children, true);
            else Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireShape(node.Path, node.Components, children, true));
        }
    }
    [TestMethod] public void DefinitionMetadataCannotBeChangedThroughReturnedArrays()
    {
        var copy = CanonicalHudPresentationDirectory.Definitions;
        var node = copy[0];
        var parts = node.Components; parts[0] = typeof(SpriteRenderer);
        copy[0] = copy[1];
        Assert.AreEqual("Border/Pane Name", CanonicalHudPresentationDirectory.Definitions[0].Path);
        Assert.AreEqual(typeof(RectTransform), node.Components[0]);
        CanonicalHudPresentationDirectory.ValidateAbi();
    }
    [TestMethod] public void OriginalButtonLabelAndWaveAliasesMustSurviveFactoryRemapping()
    {
#pragma warning disable SYSLIB0050
        T Empty<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
        var button = Empty<MenuButtonIcon>(); var label = Empty<TMPro.TextMeshPro>(); var container = Empty<TMPro.TextContainer>();
        var wave = Empty<WaveEffectControl>(); var renderer = Empty<SpriteRenderer>();
        button.label = label; button.textContainer = container; wave.spriteRenderer = renderer;
        var directory = new Dictionary<string, UnityEngine.Object> {
            ["$/Charms/Confirm Action/ActionButtonIcon:MenuButtonIcon[0]"] = button,
            ["$/Charms/Confirm Action/ActionButtonIcon/Label:TMPro.TextMeshPro[0]"] = label,
            ["$/Charms/Confirm Action/ActionButtonIcon/Label:TMPro.TextContainer[0]"] = container,
            ["$/Charms/Overcharm/Overcharm Set/Wave:WaveEffectControl[0]"] = wave,
            ["$/Charms/Overcharm/Overcharm Set/Wave:UnityEngine.SpriteRenderer[0]"] = renderer };
        CanonicalHudPresentationDirectory.RequireOriginalInternalAliases(directory);
        button.label = Empty<TMPro.TextMeshPro>();
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireOriginalInternalAliases(directory));
        button.label = label; wave.spriteRenderer = Empty<SpriteRenderer>();
        Assert.ThrowsExactly<InvalidDataException>(() => CanonicalHudPresentationDirectory.RequireOriginalInternalAliases(directory));
    }
}
