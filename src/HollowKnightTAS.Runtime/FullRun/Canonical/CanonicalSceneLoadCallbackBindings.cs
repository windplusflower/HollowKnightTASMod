using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalSceneLoadCallbackBindings
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly SceneLoad load;
        private readonly GameManager manager;
        private readonly GameManager.SceneLoadInfo info;
        private readonly object closure;
        private readonly FieldInfo[] slots;
        private readonly object?[] callbacks;
        private readonly FieldInfo ownerField, infoField, sourceField;
        private readonly string sourceBefore;
        private readonly CanonicalSceneLoadDiagnosticRegistry.Registration[] diagnostics;
        internal CanonicalSceneLoadCallbacks.Data Data { get; }
        internal CanonicalSceneLoadCallbackBindings(SceneLoad load, GameManager manager, GameManager.SceneLoadInfo info, object closure, string source)
        {
            this.load = load; this.manager = manager; this.info = info; this.closure = closure; sourceBefore = source;
            var type = closure.GetType();
            if (type.FullName != CanonicalSceneLoadCallbacks.ClosureType)
                throw new InvalidDataException("Original scene loader closure type or module differs.");
            var actualMvid = type.Module.ModuleVersionId.ToString("D");
            if (actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("SceneLoadCallbackBindings:Mvid:" + actualMvid,
                    "[HKTAS] SceneLoad closure ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            if (!CanonicalCarryGraphCodec.Fields(type).Select(field => field.Name).SequenceEqual(new[] { "<>4__this", "info", "lastSceneName" }))
                throw new InvalidDataException("Original scene loader closure complete layout differs.");
            FieldInfo Field(Type owner, string name) => owner.GetField(name, Instance) ?? throw new InvalidDataException("Scene loader raw callback field is absent: " + name);
            ownerField = Field(type, "<>4__this"); infoField = Field(type, "info"); sourceField = Field(type, "lastSceneName");
            slots = CanonicalSceneLoadCallbacks.Fields.Select(name => Field(typeof(SceneLoad), name)).ToArray();
            callbacks = slots.Select(slot => slot.GetValue(load)).ToArray();
            diagnostics = CanonicalSceneLoadDiagnosticRegistry.Read(load);
            var saved = new CanonicalSceneLoadCallbacks.Slot[slots.Length];
            for (var index = 0; index < slots.Length; index++) {
                saved[index] = new CanonicalSceneLoadCallbacks.Slot { Field = slots[index].Name };
                var chain = (callbacks[index] as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
                var observerCallbacks = diagnostics.Where(item => item.Callbacks.ContainsKey(slots[index].Name)).Select(item => item.Callbacks[slots[index].Name]).ToArray();
                var originalCount = CanonicalSceneLoadCallbacks.Tokens[index] == 0 ? 0 : 1;
                CanonicalSceneLoadCallbacks.RequireDiagnosticSuffix(chain, observerCallbacks, originalCount);
                if (originalCount == 0) continue;
                var callback = chain[0];
                if (!ReferenceEquals(callback.Target, closure) || callback.Method.IsStatic || callback.Method.ReturnType != typeof(void)
                    || callback.Method.GetParameters().Length != 0)
                    throw new InvalidDataException("Scene loader callback chain or target alias is unregistered: " + slots[index].Name + "; "
                        + (callbacks[index] is Delegate observed ? string.Join("; ", observed.GetInvocationList().Select(item =>
                            item.Method.DeclaringType?.FullName + "/" + item.Method.Name + "/token=" + item.Method.MetadataToken
                            + "/target=" + item.Target?.GetType().FullName + "/shared=" + ReferenceEquals(item.Target, closure))) : "not-delegate"));
                saved[index].Token = callback.Method.MetadataToken; saved[index].Method = callback.Method.Name;
                saved[index].ModuleMvid = callback.Method.Module.ModuleVersionId.ToString("D");
                saved[index].DeclaringType = callback.Method.DeclaringType!.FullName!; saved[index].SharedClosure = 0;
            }
            Data = CanonicalSceneLoadCallbacks.Copy(new CanonicalSceneLoadCallbacks.Data { SourceScene = source, Slots = saved });
            Guard();
        }
        internal void Guard()
            => RequireRetainedOwners(sourceBefore);
        private void RequireRetainedOwners(string expectedSource)
        {
            if (!ReferenceEquals(ownerField.GetValue(closure), manager) || !ReferenceEquals(infoField.GetValue(closure), info)
                || !Equals(sourceField.GetValue(closure), expectedSource) || slots.Where((slot, index) => !ReferenceEquals(slot.GetValue(load), callbacks[index])).Any())
                throw new InvalidOperationException("Original scene loader callback slots or shared closure fields changed.");
            var current = CanonicalSceneLoadDiagnosticRegistry.Read(load);
            if (current.Length != diagnostics.Length || current.Where((item, index) => !ReferenceEquals(item, diagnostics[index])).Any())
                throw new InvalidOperationException("Scene loader diagnostic listener registrations changed.");
        }
        internal void RequireAppliedSource(CanonicalSceneLoadCallbacks.Data source)
            => RequireRetainedOwners(CanonicalSceneLoadCallbacks.Copy(source).SourceScene);
        internal void RequireSource(CanonicalSceneLoadCallbacks.Data source, CanonicalSceneLoadCarry.Data continuation)
        {
            var saved = CanonicalSceneLoadCallbacks.Copy(source);
            if (saved.SourceScene != continuation.SourceScene || continuation.ClosureType != CanonicalSceneLoadCallbacks.ClosureType)
                throw new InvalidDataException("Scene loader callbacks disagree with the carried source/closure continuation.");
            Guard(); // Scalar preparation owns lastSceneName; callbacks are retained, never created or invoked here.
        }
    }
}
