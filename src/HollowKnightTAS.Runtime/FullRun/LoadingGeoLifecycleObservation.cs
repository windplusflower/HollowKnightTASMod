using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Narrow read-only explanation of the original Geo HUD roots. In particular,
    // never use FsmState.Actions: that getter can deserialize an untouched state.
    internal static class LoadingGeoLifecycleObservation
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static object? Read(object owner, string name) => owner.GetType().GetField(name, Flags)?.GetValue(owner)
            ?? (owner.GetType().GetField(name, Flags) == null ? throw new InvalidOperationException("Geo observation field absent: " + name) : null);
        internal static object Capture()
        {
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras;
            var counter = cameras == null ? null : cameras.geoCounter;
            if (counter == null) return new { available = false, diagnosticOnly = true };
            var owners = new[] { counter.gameObject, counter.geoSprite, counter.geoTextMesh.gameObject,
                counter.addTextMesh.gameObject, counter.subTextMesh.gameObject };
            if (owners.Any(owner => owner == null) || owners.Distinct().Count() != 5
                || owners.Skip(1).Any(owner => owner.transform.parent != counter.transform))
                throw new InvalidOperationException("Original Geo observation owner directory differs.");
            object FsmObservation(PlayMakerFSM component)
            {
                var fsm = (Fsm)Read(component, "fsm")!;
                var states = (FsmState[]?)Read(fsm, "states");
                if (states == null || states.Length > 128) throw new InvalidOperationException("Geo FSM state directory exceeds its bound.");
                return new { name = Read(fsm, "name"), component.enabled, nativeLifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(component).Select(value => (int)value).ToArray(),
                    started = Read(fsm, "<Started>k__BackingField"), initialized = Read(fsm, "initialized"), preprocessed = Read(fsm, "preprocessed"),
                    activeState = Read(fsm, "activeState") is FsmState active ? Read(active, "name") : null,
                    states = states.Select(state => {
                        var actions = (FsmStateAction[]?)Read(state, "actions");
                        var data = Read(state, "actionData");
                        var names = data == null ? null : Read(data, "actionNames") as IList;
                        if ((actions?.Length ?? 0) > 256 || (names?.Count ?? 0) > 256) throw new InvalidOperationException("Geo action directory exceeds its bound.");
                        return new { name = Read(state, "name"), active = Read(state, "active"), finished = Read(state, "finished"),
                            rawActionsNull = actions == null, rawActions = actions?.Select(action => action?.GetType().FullName).ToArray(),
                            serializedActionNames = names?.Cast<string>().ToArray() };
                    }).ToArray() };
            }
            var player = Read(counter, "playerData");
            return new { available = true, diagnosticOnly = true, counter.enabled, nativeLifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(counter).Select(value => (int)value).ToArray(),
                playerDataPresent = player != null, playerDataIsCurrent = ReferenceEquals(player, PlayerData.instance),
                playerDataIsHero = ReferenceEquals(player, HeroController.SilentInstance?.playerData), counterCurrent = Read(counter, "counterCurrent"),
                owners = owners.Select(owner => {
                    var fsms = owner.GetComponents<PlayMakerFSM>();
                    if (fsms.Length > 4) throw new InvalidOperationException("Geo owner FSM count exceeds its bound.");
                    return new { owner.name, owner.activeSelf, owner.activeInHierarchy, fsms = fsms.Select(FsmObservation).ToArray() };
                }).ToArray() };
        }
    }
}
