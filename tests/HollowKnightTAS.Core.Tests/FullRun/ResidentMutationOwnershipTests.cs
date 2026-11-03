using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentMutationOwnershipTests
{
    // Pure managed wrapper fixtures. No Unity constructor/native getter, hook
    // installation, game process or Component.gameObject lookup is invoked.
    private sealed class CallbackOwner : MonoBehaviour { }
    private sealed class ScriptableOwner : ScriptableObject { }
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static T Wrapper<T>() where T : UnityEngine.Object
    {
        var value = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
        typeof(UnityEngine.Object).GetField("m_CachedPtr", All)!.SetValue(value, new IntPtr(1)); return value;
    }

    [TestMethod]
    public void CompoundGameObjectKeepsExecutionCallbacksOutsideGameSuppressionAndConstruction()
    {
        var api = new Api(); var anchor = Wrapper<CallbackOwner>(); var gameplay = Wrapper<CallbackOwner>(); var owner = Wrapper<GameObject>();
        var ownership = api.Ownership(_ => null);
        ((IDictionary)Get(ownership, "external")).Add(anchor, "resident-execution/current-driver");
        Add(Get(ownership, "protectedObjects"), owner);
        var scope = api.Scope(ownership); var stack = Get(scope, "construction");
        // Also seed the stale broad sets that caused the original bug. The
        // explicit execution guard must win over both ways of owning a node.
        Add(Get(scope, "objects"), owner); Add(Get(scope, "components"), anchor); Add(Get(scope, "components"), gameplay);
        api.Call(scope, "Enter");
        try {
            Assert.IsFalse(api.Owns(scope, owner)); Assert.IsFalse(api.Owns(scope, anchor)); Assert.IsTrue(api.Owns(scope, gameplay));
            var window = Activator.CreateInstance(api.ScopeType.GetNestedType("ConstructionWindow", All)!, All, null,
                new object[] { scope, owner, new HashSet<Type> { typeof(CallbackOwner) } }, null)!;
            stack.GetType().GetMethod("Push")!.Invoke(stack, new[] { window });
            Assert.IsFalse((bool)api.Call(scope, "Suppress", anchor, true), "Execution must bypass even a constructor window of the same actual type.");
            Assert.IsFalse((bool)api.Call(scope, "Suppress", anchor, false));
            Assert.IsTrue((bool)api.Call(scope, "Suppress", gameplay, true));
            Assert.IsTrue((bool)api.Call(scope, "Suppress", gameplay, false));
        } finally {
            stack.GetType().GetMethod("Clear")!.Invoke(stack, null);
            api.Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose();
        }
    }

    [TestMethod]
    public void ClassifierRetainsOnlyCurrentExecutionAnchorsAndRejectsTheirRoleChange()
    {
        var api = new Api(); var anchor = Wrapper<CallbackOwner>(); var active = true;
        var ownership = api.Ownership(value => ReferenceEquals(value, anchor) && active ? "current-driver" : null);
        for (var index = 0; index < 64; index++) Assert.IsNull(api.Call(ownership, "Classify", Wrapper<CallbackOwner>()));
        Assert.AreEqual(0, ((IDictionary)Get(ownership, "observedRoles")).Count);
        Assert.AreEqual("current-driver", api.Call(ownership, "Classify", anchor));
        Assert.AreEqual(1, ((IDictionary)Get(ownership, "observedRoles")).Count);
        active = false;
        var error = Assert.ThrowsExactly<TargetInvocationException>(() => api.Call(ownership, "Classify", anchor));
        Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
    }
    [TestMethod]
    public void ScriptableConstructionOwnsOnlyItsCreatedWrapperAndKeepsCallbacksSuppressed()
    {
        var api = new Api(); var asset = Wrapper<ScriptableOwner>(); var unrelated = Wrapper<ScriptableOwner>();
        var scope = api.Scope(api.Ownership(_ => null)); var stack = Get(scope, "assetConstruction");
        api.Call(scope, "Enter");
        try {
            Assert.IsFalse((bool)api.Call(scope, "Suppress", unrelated, false));
            var window = Activator.CreateInstance(api.ScopeType.GetNestedType("ScriptableConstructionWindow", All)!, All, null,
                new object[] { scope, typeof(ScriptableOwner) }, null)!;
            stack.GetType().GetMethod("Push")!.Invoke(stack, new[] { window });
            Assert.IsTrue((bool)api.Call(scope, "Suppress", asset, true));
            Assert.IsTrue((bool)api.Call(scope, "Suppress", asset, false));
            ((IDisposable)window).Dispose();
            Assert.IsTrue((bool)api.Call(scope, "Suppress", asset, false), "Owned OnEnable/OnDestroy remain suppressed through graph/native verification.");
            Assert.IsFalse((bool)api.Call(scope, "Suppress", unrelated, true));
            Assert.IsFalse((bool)api.Call(scope, "Suppress", unrelated, false));
        } finally {
            stack.GetType().GetMethod("Clear")!.Invoke(stack, null);
            api.Call(scope, "Complete", (Action)(() => { })); ((IDisposable)scope).Dispose();
        }
    }
    private static object Get(object owner, string field) => owner.GetType().GetField(field, All)!.GetValue(owner)!;
    private static void Add(object set, object value) => set.GetType().GetMethod("Add")!.Invoke(set, new[] { value });
    private sealed class Api
    {
        private readonly Type ownershipType;
        internal Type ScopeType { get; }
        internal Api()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root != null && !File.Exists(Path.Combine(root.FullName, "HollowKnightTAS.sln"))) root = root.Parent;
            Assert.IsNotNull(root);
            var runtime = Assembly.LoadFrom(Path.Combine(root.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            ownershipType = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentExecutionOwnership", true)!;
            ScopeType = runtime.GetType("HollowKnightTAS.Runtime.FullRun.ResidentMutationScope", true)!;
        }
        internal object Ownership(Func<Component, string?> classifier) => Activator.CreateInstance(ownershipType, All, null, new object[] { classifier }, null)!;
        internal object Scope(object ownership) => Activator.CreateInstance(ScopeType, All, null,
            new object[] { Array.Empty<GameObject>(), (Action)(() => { }), ownership }, null)!;
        internal object Call(object owner, string method, params object[] values) => owner.GetType().GetMethod(method, All)!.Invoke(owner, values)!;
        internal bool Owns(object scope, object owner)
            => (bool)ScopeType.GetMethod("Owns", All, null, new[] { owner is GameObject ? typeof(GameObject) : typeof(Component) }, null)!.Invoke(scope, new[] { owner })!;
    }
}
