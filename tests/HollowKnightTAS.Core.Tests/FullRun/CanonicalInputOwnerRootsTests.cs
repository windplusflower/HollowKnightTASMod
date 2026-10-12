using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HollowKnightTAS.Runtime.FullRun;
using InControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalInputOwnerRootsTests
{
    private static T Empty<T>() where T : class
    {
#pragma warning disable SYSLIB0050
        return (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
    }
    [TestMethod]
    public void Axes_and_directional_controls_bind_existing_objects_without_constructors()
    {
        var hero = Empty<HeroActions>(); var axis = Empty<PlayerTwoAxisAction>(); var left = Empty<OneAxisInputControl>();
        typeof(HeroActions).GetField("moveVector")!.SetValue(hero, axis);
        typeof(TwoAxisInputControl).GetField("<Left>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(axis, left);
        var roots = new Dictionary<string, object> { ["R6/InputActionSet/0"] = hero };
        Assert.AreEqual(2, CanonicalInputOwnerRoots.Add(roots));
        Assert.AreSame(axis, roots.Single(pair => pair.Key.EndsWith("/moveVector")).Value);
        Assert.AreSame(left, roots.Single(pair => pair.Key.EndsWith("/<Left>k__BackingField")).Value);
    }
    [TestMethod]
    public void Existing_action_root_and_shared_axis_have_one_canonical_identity()
    {
        var hero = Empty<HeroActions>(); var axis = Empty<PlayerTwoAxisAction>(); var left = Empty<OneAxisInputControl>();
        typeof(HeroActions).GetField("moveVector")!.SetValue(hero, axis);
        typeof(HeroActions).GetField("rightStick")!.SetValue(hero, axis);
        typeof(TwoAxisInputControl).GetField("<Left>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(axis, left);
        var roots = new Dictionary<string, object> { ["R6/InputActionSet/0"] = hero, ["R6/InputActionSet/0/action/left"] = left };
        Assert.AreEqual(1, CanonicalInputOwnerRoots.Add(roots));
        Assert.AreEqual(1, roots.Values.Count(value => ReferenceEquals(value, axis)));
        Assert.AreEqual(1, roots.Values.Count(value => ReferenceEquals(value, left)));
    }
    [TestMethod]
    public void Repeated_owner_alias_preserves_the_first_sorted_owner_path()
    {
        var hero = Empty<HeroActions>(); var axis = Empty<PlayerTwoAxisAction>();
        typeof(HeroActions).GetField("moveVector")!.SetValue(hero, axis);
        var roots = new Dictionary<string, object> { ["z-owner"] = hero, ["a-owner"] = hero };
        Assert.AreEqual(1, CanonicalInputOwnerRoots.Add(roots));
        StringAssert.StartsWith(roots.Single(pair => ReferenceEquals(pair.Value, axis)).Key, "R6/InputControl/a-owner/");
    }
}
