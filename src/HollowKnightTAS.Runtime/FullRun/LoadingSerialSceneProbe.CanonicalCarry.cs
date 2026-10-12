using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingSerialSceneProbe
    {
        // Canon runs before observedBoundary releases Begin's parked loop.
        // The ordinary checkpoint getter still requires a completed Canon;
        // this separate reader cannot release or advance the original loader.
        internal static GameManager.SceneLoadInfo AddCanonicalLoadInfo(IDictionary<string, object> roots)
        {
            var self = instance;
            if (self == null || self.canonical == null || !self.awaitingBoundary || !self.sourceUnloaded || self.source.isLoaded
                || self.transition == null || self.checkpointLoadInfo == null || !self.checkpointTransitionFinished
                || self.checkpointTransitionRoutine == null || USceneManager.GetActiveScene().name != StagingName)
                throw new InvalidDataException("Canonical SceneLoadInfo is outside the original parked unloaded boundary.");
            var info = self.checkpointLoadInfo;
            var load = self.transition; var manager = GameManager.instance;
            if (manager == null || info.GetType() != typeof(GameManager.SceneLoadInfo) || load.GetType() != typeof(SceneLoad))
                throw new InvalidDataException("Canonical scene loader owner or exact load-info type differs.");
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            object? Read(object owner, Type type, string field) => (type.GetField(field, flags)
                ?? throw new InvalidDataException("Canonical scene loader reference field is absent: " + field)).GetValue(owner);
            var routine = self.checkpointTransitionRoutine; var closure = Closure(routine);
            if (routine.GetType().FullName != "GameManager+<BeginSceneTransitionRoutine>d__174"
                || closure.GetType().FullName != "GameManager+<>c__DisplayClass174_0"
                || !ReferenceEquals(Read(manager, typeof(GameManager), "sceneLoad"), load)
                || !ReferenceEquals(Read(load, typeof(SceneLoad), "runner"), manager)
                || !ReferenceEquals(Read(routine, routine.GetType(), "<>4__this"), manager)
                || !ReferenceEquals(Read(routine, routine.GetType(), "info"), info)
                || !ReferenceEquals(Read(closure, closure.GetType(), "<>4__this"), manager)
                || !ReferenceEquals(Read(closure, closure.GetType(), "info"), info)
                || !Equals(Read(load, typeof(SceneLoad), "targetSceneName"), info.SceneName))
                throw new InvalidDataException("Canonical scene loader/iterator/closure do not share their original actual load info.");
            var target = USceneManager.GetSceneByName(info.SceneName);
            if (target.IsValid() && target.isLoaded || string.IsNullOrEmpty(info.SceneName) || info.SceneName.Length > 256
                || info.SceneName.IndexOf('\0') >= 0 || float.IsNaN(info.EntryDelay) || float.IsInfinity(info.EntryDelay)
                || !Enum.IsDefined(typeof(GameManager.SceneLoadVisualizations), info.Visualization)
                || info.HeroLeaveDirection.HasValue && !Enum.IsDefined(typeof(GlobalEnums.GatePosition), info.HeroLeaveDirection.Value))
                throw new InvalidDataException("Canonical scene entry parameters or target activation differ.");
            roots.Add("D/SceneLoadInfo", info);
            roots.Add(CanonicalSceneLoadCarry.Root, CanonicalSceneLoadCarry.Capture(self.sourceName, info.SceneName, CaptureParkedLoadState(self)));
            return info;
        }
        internal static Action PrepareCanonicalLoadScalars(CanonicalSceneLoadCarry.Data data)
        {
            // Recheck the actual original ownership and parked phase. Do not
            // run Begin, an iterator, or any SceneLoad entry callback.
            AddCanonicalLoadInfo(new Dictionary<string, object>(StringComparer.Ordinal));
            var self = instance!; var load = self.transition!; var routine = self.checkpointTransitionRoutine!;
            var info = self.checkpointLoadInfo!; var sourceBefore = self.sourceName;
            var apply = PrepareParkedLoadState(CanonicalSceneLoadCarry.Decode(data), self);
            var source = data.SourceScene;
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Canonical SceneLoad preparation was already consumed.");
                if (!ReferenceEquals(instance, self) || !self.awaitingBoundary || !self.sourceUnloaded || self.source.isLoaded
                    || !ReferenceEquals(self.transition, load) || !ReferenceEquals(self.checkpointTransitionRoutine, routine)
                    || !ReferenceEquals(self.checkpointLoadInfo, info) || self.sourceName != sourceBefore)
                    throw new InvalidOperationException("Canonical SceneLoad preparation changed before scalar application.");
                consumed = true; apply(); self.sourceName = source;
            };
        }
        internal static CanonicalSceneLoadCallbackBindings ReadCanonicalCallbackBindings()
        {
            AddCanonicalLoadInfo(new Dictionary<string, object>(StringComparer.Ordinal));
            var self = instance!;
            return new CanonicalSceneLoadCallbackBindings(self.transition!, GameManager.instance, self.checkpointLoadInfo!,
                Closure(self.checkpointTransitionRoutine!), self.sourceName);
        }
    }
}
