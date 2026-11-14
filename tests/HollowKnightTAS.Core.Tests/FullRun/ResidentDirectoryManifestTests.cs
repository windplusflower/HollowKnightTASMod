using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class ResidentDirectoryManifestTests
{
    [TestMethod]
    public void RootOrderUsesTheActualSceneListAndRejectsAForeignBacklinkBeforeWriting()
    {
        var api = new Api(); var scene = Marshal.AllocHGlobal(248);
        var tr = Enumerable.Range(0, 3).Select(_ => Marshal.AllocHGlobal(184)).ToArray();
        var sentinel = IntPtr.Add(scene, 176);
        var nodes = tr.Select(p => IntPtr.Add(p, 152)).ToArray();
        try {
            Marshal.WriteInt32(scene, 8, 27);
            for (var i = 0; i < nodes.Length; i++) {
                Marshal.WriteIntPtr(nodes[i], 0, i == 0 ? sentinel : nodes[i - 1]);
                Marshal.WriteIntPtr(nodes[i], 8, i == nodes.Length - 1 ? sentinel : nodes[i + 1]);
                Marshal.WriteIntPtr(nodes[i], 16, tr[i]); Marshal.WriteIntPtr(nodes[i], 24, scene);
            }
            Marshal.WriteIntPtr(sentinel, 0, nodes[2]); Marshal.WriteIntPtr(sentinel, 8, nodes[0]);
            var reversed = tr.Reverse().ToArray();
            Marshal.WriteIntPtr(nodes[1], 24, IntPtr.Zero);
            api.Reject(() => api.ReorderRoots(tr, reversed));
            Assert.AreEqual(nodes[0], Marshal.ReadIntPtr(sentinel, 8));
            Marshal.WriteIntPtr(nodes[1], 24, scene);
            api.ReorderRoots(tr, reversed);
            Assert.AreEqual(nodes[2], Marshal.ReadIntPtr(sentinel, 8)); Assert.AreEqual(nodes[0], Marshal.ReadIntPtr(sentinel, 0));
            Assert.AreEqual(nodes[2], Marshal.ReadIntPtr(nodes[1], 0)); Assert.AreEqual(nodes[0], Marshal.ReadIntPtr(nodes[1], 8));
            api.ReorderRoots(reversed, tr);
            Assert.AreEqual(nodes[0], Marshal.ReadIntPtr(sentinel, 8)); Assert.AreEqual(nodes[2], Marshal.ReadIntPtr(sentinel, 0));
        } finally { foreach (var pointer in tr) Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(scene); }
    }
    [TestMethod]
    public void BinaryWireRebuildsNestedPathsTypeOrdinalsAndExecutionDependencies()
    {
        var api = new Api(); var source = api.Tree(); api.Dependency(source,0,1,"current-loader");
        var wire = source.GetType().Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentDirectoryWire",true)!;
        var encode = wire.GetMethod("Encode",All)!; var decode = wire.GetMethod("Decode",All)!;
        var bytes = (byte[])encode.Invoke(null,new[] {source})!;
        var restored = decode.Invoke(null,new object[] {bytes})!; api.Same(source,restored);
        CollectionAssert.AreEqual(bytes,(byte[])encode.Invoke(null,new[] {restored})!);
        void RejectWire(byte[] corrupt) {
            var wrapped = Assert.ThrowsExactly<TargetInvocationException>(() => decode.Invoke(null,new object[]{corrupt}));
            Assert.IsInstanceOfType<InvalidDataException>(wrapped.InnerException);
        }
        RejectWire(bytes.Take(bytes.Length-1).ToArray());
        RejectWire(bytes.Concat(new byte[]{0}).ToArray());
        var forged = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes(int.MaxValue),0,forged,4,4);
        RejectWire(forged);
    }

    [TestMethod]
    public void CompleteTreeManifestPreservesDuplicateNamesComponentOrdinalsAndIndependentCopy()
    {
        var api = new Api(); var fixture = api.Tree(); api.Validate(fixture);
        var owners = (Array)Get(fixture, "Objects");
        Assert.AreNotEqual(Get(owners.GetValue(1)!, "Key"), Get(owners.GetValue(2)!, "Key"));
        var components = (Array)Get(owners.GetValue(0)!, "Components");
        Assert.AreNotEqual(Get(components.GetValue(1)!, "Key"), Get(components.GetValue(2)!, "Key"));
        var copy = fixture.GetType().GetMethod("Copy", All)!.Invoke(fixture, null)!;
        Set(owners.GetValue(1)!, "Name", "changed after sealing");
        api.RejectTree(fixture); api.Validate(copy);
        Assert.AreEqual("same/[0]", Get(((Array)Get(copy, "Objects")).GetValue(1)!, "Name"));
    }

    [TestMethod]
    public void TreeManifestRejectsUnpairedParentsSiblingGapsTypeOrdinalsAndForeignModuleTokens()
    {
        var api = new Api();
        void Reject(Action<object, Array> mutate) {
            var fixture = api.Tree(); var owners = (Array)Get(fixture, "Objects"); mutate(fixture, owners); api.RejectTree(fixture);
        }
        Reject((_, owners) => Set(owners.GetValue(1)!, "Parent", "missing native owner"));
        Reject((_, owners) => Set(owners.GetValue(2)!, "SiblingIndex", 3));
        Reject((_, owners) => Set(owners.GetValue(2)!, "Ordinal", 0));
        Reject((_, owners) => Set(((Array)Get(owners.GetValue(0)!, "Components")).GetValue(2)!, "Ordinal", 0));
        Reject((_, owners) => Set(owners.GetValue(2)!, "Key", Get(owners.GetValue(1)!, "Key")));
        var type = api.NativeType(typeof(UnityEngine.Mesh));
        Set(type, "Module", "11111111-1111-1111-1111-111111111111");
        api.Reject(() => type.GetType().GetMethod("Resolve", All)!.Invoke(type, new object?[] { true, null }));
    }

    [TestMethod]
    public void AssetManifestUsesCapturedProvenanceInsteadOfDuplicateDisplayNamesAndRejectsAliasCollapse()
    {
        var api = new Api(); var first = api.Asset(typeof(UnityEngine.Material), "same display name", "sharedassets0.assets", 123, -1);
        var second = api.Asset(typeof(UnityEngine.Material), "same display name", "sharedassets0.assets", 124, -1);
        var dynamic = api.Asset(typeof(UnityEngine.Mesh), "same display name", "", 0, 0);
        Assert.AreNotEqual(Get(first, "Binding"), Get(second, "Binding"));
        var source = api.Assets(first, second, dynamic); api.ValidateAssets(source);
        Set(second, "Binding", Get(first, "Binding")); api.Reject(() => api.ValidateAssets(api.Assets(first, second, dynamic)));
        Set(second, "Binding", api.SerializedKey(Get(second, "Type"), "sharedassets0.assets", 124));
        Set(dynamic, "DynamicOrdinal", 2); api.Reject(() => api.ValidateAssets(api.Assets(first, second, dynamic)));
    }

    [TestMethod]
    public void ExecutionManifestProtectsActualAnchorAncestorsAndKeepsCompoundGameComponents()
    {
        var api = new Api(); var fixture = api.Tree(); api.Execution(fixture, 0, 1, "current-driver"); api.Validate(fixture);
        var owners = (Array)Get(fixture, "Objects"); var root = owners.GetValue(0)!; var components = (Array)Get(root, "Components");
        var anchor = components.GetValue(1)!;
        StringAssert.StartsWith((string)Get(anchor, "Key"), "resident-execution/");
        Assert.AreEqual(2, Convert.ToInt32(Get(components.GetValue(0)!, "Ownership")), "The protected Transform is an execution dependency.");
        Assert.AreEqual(0, Convert.ToInt32(Get(components.GetValue(2)!, "Ownership")), "A gameplay component on the same GameObject remains game state.");
        CollectionAssert.AreEquivalent(new[] { (string)Get(root, "Key") }, api.Protected(fixture));

        var other = api.Tree(); api.Execution(other, 1, 0, "current-driver-transform"); api.Validate(other);
        var children = (Array)Get(other, "Objects");
        CollectionAssert.AreEquivalent(new[] { (string)Get(children.GetValue(0)!, "Key"), (string)Get(children.GetValue(1)!, "Key") }, api.Protected(other));
    }

    [TestMethod]
    public void ExecutionManifestRejectsOmittedDependenciesRolesAndDuplicateStableBindings()
    {
        var api = new Api();
        var fixture = api.Tree(); api.Execution(fixture, 0, 1, "current-driver");
        var root = ((Array)Get(fixture, "Objects")).GetValue(0)!; var components = (Array)Get(root, "Components");
        api.SetOwnership(components.GetValue(0)!, 0); api.RejectTree(fixture);
        fixture = api.Tree(); api.Execution(fixture, 0, 1, "current-driver");
        var entries = (Array)Get(Get(fixture, "Execution"), "Entries"); Set(entries.GetValue(0)!, "Role", "another-process-role"); api.RejectTree(fixture);
        fixture = api.Tree(); api.Execution(fixture, 0, 1, "current-driver"); api.Execution(fixture, 0, 2, "current-driver"); api.RejectTree(fixture);
    }

    [TestMethod]
    public void ProtectedObjectGuardRejectsExecutionWritesButAllowsCompoundGameMetadataChanges()
    {
        var api = new Api(); var source = api.Tree(); api.Execution(source, 0, 1, "current-driver");
        var copy = source.GetType().GetMethod("Copy", All)!.Invoke(source, null)!;
        var first = ((Array)Get(source, "Objects")).GetValue(0)!; var second = ((Array)Get(copy, "Objects")).GetValue(0)!;
        var components = (Array)Get(second, "Components"); Set(components.GetValue(2)!, "HideFlags", 7);
        api.ProtectedEqual(first, second);
        Set(second, "ActiveSelf", false); api.Reject(() => api.ProtectedEqual(first, second)); Set(second, "ActiveSelf", true);
        Set(components.GetValue(1)!, "HideFlags", 7); api.Reject(() => api.ProtectedEqual(first, second));
    }

    [TestMethod]
    public void ExecutionHostDependencyProtectsStructureWhileKeepingGameplayComponentFields()
    {
        var api = new Api(); var source = api.Tree(); api.Dependency(source, 0, 1, "current-loader-native-owner"); api.Validate(source);
        var owner = ((Array)Get(source, "Objects")).GetValue(0)!; var components = (Array)Get(owner, "Components");
        Assert.AreEqual(2, Convert.ToInt32(Get(components.GetValue(0)!, "Ownership")));
        Assert.AreEqual(0, Convert.ToInt32(Get(components.GetValue(1)!, "Ownership")), "The loader's ordinary component fields remain in the unified gameplay graph.");
        Assert.AreEqual(0, Convert.ToInt32(Get(components.GetValue(2)!, "Ownership")));
        Assert.AreEqual(0, ((Array)Get(Get(source, "Execution"), "Entries")).Length);
        CollectionAssert.AreEquivalent(new[] { (string)Get(owner, "Key") }, api.Protected(source));
        var copy = source.GetType().GetMethod("Copy", All)!.Invoke(source, null)!; api.Same(source, copy);
        var row = ((Array)Get(Get(copy, "Execution"), "Dependencies")).GetValue(0)!;
        Set(row, "Role", "another-loader"); api.Validate(copy); api.Reject(() => api.Same(source, copy));
        Assert.AreEqual("current-loader-native-owner", Get(((Array)Get(Get(source, "Execution"), "Dependencies")).GetValue(0)!, "Role"));
    }

    [TestMethod]
    public void ExecutionHostDependencyRejectsMissingTransformsHostReorderAndInventedRoles()
    {
        var api = new Api();
        void Reject(Action<object, object> mutate) {
            var source = api.Tree(); api.Dependency(source, 0, 1, "current-loader");
            mutate(source, ((Array)Get(Get(source, "Execution"), "Dependencies")).GetValue(0)!); api.RejectTree(source);
        }
        Reject((_, entry) => Set(entry, "ComponentOrder", 2));
        Reject((_, entry) => Set(entry, "ComponentKey", "missing-host"));
        Reject((_, entry) => Set(entry, "ObjectKey", "missing-gameobject"));
        Reject((_, entry) => Set(entry, "Role", ""));
        Reject((source, _) => api.SetOwnership(((Array)Get(((Array)Get(source, "Objects")).GetValue(0)!, "Components")).GetValue(0)!, 0));
        var dependencyOnly = api.Tree(); api.Dependency(dependencyOnly, 1, -1, "current-loader-go"); api.Validate(dependencyOnly);
        Assert.AreEqual(2, api.Protected(dependencyOnly).Length, "A dependency also protects its real ancestors.");
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static object Get(object owner, string name) => owner.GetType().GetField(name, All)!.GetValue(owner)!;
    private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, All)!.SetValue(owner, value);
    private sealed class Api
    {
        private readonly Type directory, assets, execution;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            directory = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentObjectDirectory", true)!;
            assets = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentAssetBindings", true)!;
            execution = assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentExecutionOwnership", true)!;
        }
        private object Invoke(Type type, string name, params object[] values) => type.GetMethod(name, All)!.Invoke(null, values)!;
        private object Row(string name, params (string Name, object Value)[] values)
        {
            var row = Activator.CreateInstance(directory.GetNestedType(name, All)!, true)!;
            foreach (var field in values) Set(row, field.Name, field.Value);
            return row;
        }
        private Array Rows(string name, params object[] values)
        {
            var result = Array.CreateInstance(directory.GetNestedType(name, All)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index);
            return result;
        }
        internal object NativeType(Type type) => Invoke(directory.GetNestedType("TypeRecord", All)!, "Capture", type);
        internal object Tree()
        {
            var sceneKey = (string)Invoke(directory, "SceneKey", "", "persistent", -1, 0);
            var scene = Row("SceneRecord", ("Key", sceneKey), ("Name", "persistent"), ("BuildIndex", -1), ("Unlisted", true));
            var transform = NativeType(typeof(UnityEngine.Transform)); var behaviour = NativeType(typeof(PlayMakerFSM));
            object Owner(string parent, string name, int ordinal, int sibling, bool actions)
            {
                var key = (string)Invoke(directory, "ObjectKey", sceneKey, parent, name, ordinal);
                object Component(object type, int number, int order) => Row("ComponentRecord", ("Type", type), ("Ordinal", number), ("Order", order),
                    ("Key", Invoke(directory, "ComponentKey", key, type, number)));
                var components = actions ? Rows("ComponentRecord", Component(transform, 0, 0), Component(behaviour, 0, 1), Component(behaviour, 1, 2))
                    : Rows("ComponentRecord", Component(transform, 0, 0));
                return Row("ObjectRecord", ("Key", key), ("Scene", sceneKey), ("Parent", parent), ("Name", name), ("Ordinal", ordinal),
                    ("SiblingIndex", sibling), ("Tag", "Untagged"), ("ActiveSelf", true), ("Components", components));
            }
            var root = Owner("", "Root/go/1:X中", 0, 0, true); var key = (string)Get(root, "Key");
            return Row("Snapshot", ("Scenes", Rows("SceneRecord", scene)), ("Objects", Rows("ObjectRecord", root,
                Owner(key, "same/[0]", 0, 0, false), Owner(key, "same/[0]", 1, 1, false))));
        }
        internal void Validate(object source) => Invoke(directory, "Validate", source);
        internal void Same(object source, object target) => Invoke(directory, "RequireSame", source, target, true);
        internal void ReorderRoots(IntPtr[] current, IntPtr[] desired)
            => directory.Assembly.GetType("HollowKnightTAS.Runtime.FullRun.ResidentSceneRootOrder", true)!
                .GetMethod("Reorder", All)!.Invoke(null, new object[] { 27, current, desired, (Action)(() => { }) });
        internal void RejectTree(object source) => Reject(() => Validate(source));
        internal void Reject(Action action)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(action);
            Assert.IsInstanceOfType<InvalidDataException>(error.GetBaseException());
        }
        internal string SerializedKey(object type, string path, long id) => (string)Invoke(assets, "SerializedKey", type, path, id);
        internal object Asset(Type type, string name, string path, long id, int ordinal)
        {
            var native = NativeType(type); var result = Activator.CreateInstance(assets.GetNestedType("AssetRecord", All)!, true)!;
            Set(result, "Type", native); Set(result, "Name", name); Set(result, "SourcePath", path); Set(result, "LocalIdentifier", id);
            Set(result, "DynamicOrdinal", ordinal); Set(result, "Kind", ordinal < 0 ? "serialized" : "dynamic");
            Set(result, "Binding", ordinal < 0 ? SerializedKey(native, path, id) : Invoke(assets, "DynamicKey", native, ordinal));
            return result;
        }
        internal Array Assets(params object[] values)
        {
            values = values.OrderBy(value => (string)Get(value, "Binding"), StringComparer.Ordinal).ToArray();
            var result = Array.CreateInstance(assets.GetNestedType("AssetRecord", All)!, values.Length);
            for (var index = 0; index < values.Length; index++) result.SetValue(values[index], index);
            return result;
        }
        internal void ValidateAssets(Array manifest) => Invoke(assets, "ValidateManifest", manifest);
        internal void SetOwnership(object component, int value)
            => Set(component, "Ownership", Enum.ToObject(component.GetType().GetField("Ownership", All)!.FieldType, value));
        internal void Execution(object fixture, int ownerIndex, int componentIndex, string role)
        {
            var owners = (Array)Get(fixture, "Objects"); var owner = owners.GetValue(ownerIndex)!;
            var component = ((Array)Get(owner, "Components")).GetValue(componentIndex)!;
            SetOwnership(component, 1); Set(component, "ExecutionRole", role); Set(component, "Key", Invoke(execution, "Binding", Get(component, "Type"), role));
            var key = (string)Get(owner, "Key");
            while (key.Length != 0) {
                var row = owners.Cast<object>().Single(value => (string)Get(value, "Key") == key);
                var transform = ((Array)Get(row, "Components")).GetValue(0)!;
                if (Convert.ToInt32(Get(transform, "Ownership")) == 0) SetOwnership(transform, 2);
                key = (string)Get(row, "Parent");
            }
            Set(fixture, "Execution", Invoke(execution, "CreateManifest", owners));
        }
        internal string[] Protected(object fixture) => ((System.Collections.Generic.IEnumerable<string>)Invoke(execution, "ProtectedKeys", fixture)).ToArray();
        internal void Dependency(object fixture, int ownerIndex, int componentIndex, string role)
        {
            var owners = (Array)Get(fixture, "Objects"); var owner = owners.GetValue(ownerIndex)!; var type = execution.GetNestedType("DependencyEntry", All)!;
            var entry = Activator.CreateInstance(type, true)!; Set(entry, "Role", role); Set(entry, "ObjectKey", Get(owner, "Key"));
            if (componentIndex >= 0) {
                var component = ((Array)Get(owner, "Components")).GetValue(componentIndex)!;
                Set(entry, "ComponentKey", Get(component, "Key")); Set(entry, "Type", Get(component, "Type")); Set(entry, "ComponentOrder", componentIndex);
            }
            var entries = Array.CreateInstance(type, 1); entries.SetValue(entry, 0); Set(Get(fixture, "Execution"), "Dependencies", entries);
            var key = (string)Get(owner, "Key");
            while (key.Length != 0) {
                var current = owners.Cast<object>().Single(value => (string)Get(value, "Key") == key);
                SetOwnership(((Array)Get(current, "Components")).GetValue(0)!, 2); key = (string)Get(current, "Parent");
            }
        }
        internal void ProtectedEqual(object expected, object actual)
            => execution.GetNestedType("TopologyGuard", All)!.GetMethod("RequireProtectedObject", All)!.Invoke(null, new[] { expected, actual });
    }
}
