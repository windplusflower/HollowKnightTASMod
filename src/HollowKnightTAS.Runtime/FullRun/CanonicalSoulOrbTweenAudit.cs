using System;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Diagnostic only. A missing callback is not repaired or treated as inert.
    internal static class CanonicalSoulOrbTweenAudit
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static object? Observe()
        {
            if (Environment.GetEnvironmentVariable("HKTAS_LOADING_SOUL_ORB_TWEEN_AUDIT") != "1") return null;
            try {
                if (typeof(iTweenFSMEvents).Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid)
                    throw new InvalidOperationException("original-module");
                var hud = GameCameras.instance.hudCamera.transform;
                var soul = hud.Find("Hud Canvas/Soul Orb");
                if (soul == null) throw new InvalidOperationException("exact-original-soul-fill-path");
                var liquid = soul.Find("SoulOrb_fill/Liquid");
                if (liquid == null) throw new InvalidOperationException("exact-original-liquid-path");
                var fsms = soul.GetComponents<PlayMakerFSM>();
                var matches = fsms.Where(owner => ((Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(owner)).Name == "Soul Orb Control").ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("exact-original-soul-control");
                var owner = matches[0]; var raw = (Fsm)typeof(PlayMakerFSM).GetField("fsm", Flags)!.GetValue(owner);
                var states = (FsmState[])typeof(Fsm).GetField("states", Flags)!.GetValue(raw);
                if (states == null || states.Length > 128) throw new InvalidOperationException("bounded-original-states");
                var lose = states.Where(state => state.Name == "MP Lose").ToArray();
                if (lose.Length != 1) throw new InvalidOperationException("original-mp-lose-state");
                var actions = (FsmStateAction[])typeof(FsmState).GetField("actions", Flags)!.GetValue(lose[0]);
                if (actions == null || actions.Length != 11 || actions[10]?.GetType() != typeof(iTweenMoveTo))
                    throw new InvalidOperationException("original-action11-layout");
                var action = (iTweenFsmAction)actions[10];
                var cached = typeof(iTweenFsmAction).GetField("itweenEvents", Flags)!.GetValue(action) as iTweenFSMEvents;
                var id = (int)typeof(iTweenFsmAction).GetField("itweenID", Flags)!.GetValue(action);
                var events = liquid.GetComponents<iTweenFSMEvents>();
                if (events.Length > 4 || events.Any(item => item == null)) throw new InvalidOperationException("bounded-original-event-components");
                var rows = events.Select(item => new { live = true, item.enabled, active = item.gameObject.activeInHierarchy,
                    exactCachedEvent = ReferenceEquals(cached, item), exactActionBacklink = ReferenceEquals(item.itweenFSMAction, action),
                    item.itweenID, actionID = id, sameID = item.itweenID == id, item.donotfinish, item.islooping,
                    exactFsm = ReferenceEquals(action.Fsm, raw), action.Finished }).ToArray();
                return new { enabled = true, diagnosticOnly = true, refused = false, state = owner.ActiveStateName,
                    stateCount = states.Length, actionCount = actions.Length, cachedClrNull = ReferenceEquals(cached, null),
                    cachedLive = cached != null, tweenIdCount = iTweenFSMEvents.itweenIDCount,
                    liveOriginalProducers = liquid.GetComponents<iTween>().Count(item => item != null), events = rows,
                    callbacksRetired = false, completeStateCoverage = false };
            } catch (Exception error) {
                return new { enabled = true, diagnosticOnly = true, refused = true, reason = error.Message,
                    exceptionType = error.GetType().FullName, callbacksRetired = false, completeStateCoverage = false };
            }
        }
    }
}
