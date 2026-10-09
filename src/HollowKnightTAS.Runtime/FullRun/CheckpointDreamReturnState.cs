using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One persistent, initialized FSM. Preserve its running state and action
    // lists without SetState/OnEnter, which would repeat completed game effects.
    [Serializable]
    internal sealed class CheckpointDreamReturnState
    {
        public Dictionary<string, string> Fields = null!;
        public string? Active, Previous, SwitchTo;
        public CheckpointHeroSpellVariables Variables = null!;
        public StateData[] States = null!;
        [Serializable] internal sealed class StateData
        {
            public string Name = "";
            public Dictionary<string, string> Fields = null!;
            public ActionData[]? Actions;
            public int ActiveAction = -1;
            public int[]? ActiveActions, FinishedActions;
        }
        [Serializable] internal sealed class ActionData
        {
            public string Type = "";
            public Dictionary<string, string> Fields = null!;
            public string[] VariableFields = null!;
            public Dictionary<string, string>[] VariableValues = null!;
        }
        private static FieldInfo Field(Type type, string name) => type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Dream Return field binding is absent: " + type.FullName + "/" + name);
        private static FieldInfo[] Scalars(Type type)
        {
            var result = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                result.AddRange(CheckpointScalarFields.Declared(current).Where(f => f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr)
                    && f.FieldType != typeof(UIntPtr) || f.FieldType.IsEnum || f.FieldType == typeof(string)
                    || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3)));
            return result.ToArray();
        }
        private static readonly FieldInfo RawFsm = Field(typeof(PlayMakerFSM), "fsm");
        private static readonly FieldInfo StateArray = Field(typeof(Fsm), "states");
        private static readonly FieldInfo ActionArray = Field(typeof(FsmState), "actions");
        private static readonly FieldInfo[] FsmFields = Scalars(typeof(Fsm)), StateFields = Scalars(typeof(FsmState));
        private static PlayMakerFSM Bind(HeroController hero) => hero.GetComponents<PlayMakerFSM>().Single(f => f.FsmName == "Dream Return");
        private static void RequireNoDelayedEvents(Fsm fsm)
        {
            if (((IList)Field(typeof(Fsm), "delayedEvents").GetValue(fsm)).Count != 0)
                throw new InvalidOperationException("Dream Return delayed events require a scheduler adapter.");
        }
        private static FieldInfo[] NamedValues(Type type)
        {
            var fields = new List<FieldInfo>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
                fields.AddRange(CheckpointScalarFields.Declared(current).Where(f => typeof(NamedVariable).IsAssignableFrom(f.FieldType)
                    && f.FieldType != typeof(FsmGameObject) && f.FieldType != typeof(FsmObject) && f.FieldType != typeof(FsmMaterial)
                    && f.FieldType != typeof(FsmTexture) && f.FieldType != typeof(FsmArray) && f.FieldType != typeof(FsmEnum)));
            return fields.ToArray();
        }
        private static string Identity(FieldInfo field) => field.DeclaringType!.FullName + "/" + field.Name;
        private static Dictionary<string, string> VariableValue(object? variable)
        {
            if (variable == null) return new Dictionary<string, string> { ["null"] = "true" };
            if (((NamedVariable)variable).CastVariable != null) throw new InvalidDataException("Dream action cast variables require an adapter.");
            return CheckpointScalarFields.Capture(variable, new[] { Field(variable.GetType(), "value") });
        }
        private static int[]? ActionIndices(object? list, FsmStateAction[] actions)
        {
            if (list == null) return null;
            return ((IEnumerable<FsmStateAction>)list).Select(action =>
            {
                var index = Array.FindIndex(actions, candidate => ReferenceEquals(candidate, action));
                if (index < 0) throw new InvalidDataException("Dream action list contains an unbound action.");
                return index;
            }).ToArray();
        }
        internal static CheckpointDreamReturnState Capture(HeroController hero)
        {
            var component = Bind(hero); var fsm = (Fsm)RawFsm.GetValue(component);
            RequireNoDelayedEvents(fsm);
            var states = (FsmState[])StateArray.GetValue(fsm);
            if (states.Length > 128) throw new InvalidDataException("Dream state layout exceeds its bound.");
            var result = new CheckpointDreamReturnState { Fields = CheckpointScalarFields.Capture(fsm, FsmFields),
                Active = ((FsmState?)Field(typeof(Fsm), "activeState").GetValue(fsm))?.Name,
                Previous = ((FsmState?)Field(typeof(Fsm), "previousActiveState").GetValue(fsm))?.Name,
                SwitchTo = ((FsmState?)Field(typeof(Fsm), "switchToState").GetValue(fsm))?.Name,
                Variables = CheckpointHeroSpellVariables.Capture(component),
                States = states.Select(state =>
                {
                    var actions = (FsmStateAction[]?)ActionArray.GetValue(state);
                    if (actions?.Length > 128) throw new InvalidDataException("Dream action layout exceeds its bound.");
                    var active = Field(typeof(FsmState), "activeAction").GetValue(state);
                    return new StateData { Name = state.Name, Fields = CheckpointScalarFields.Capture(state, StateFields),
                        ActiveAction = active == null ? -1 : Array.FindIndex(actions!, a => ReferenceEquals(a, active)),
                        ActiveActions = actions == null ? null : ActionIndices(Field(typeof(FsmState), "activeActions").GetValue(state), actions),
                        FinishedActions = actions == null ? null : ActionIndices(Field(typeof(FsmState), "_finishedActions").GetValue(state), actions),
                        Actions = actions?.Select(action => new ActionData { Type = action.GetType().FullName,
                            Fields = CheckpointScalarFields.Capture(action, Scalars(action.GetType())),
                            VariableFields = NamedValues(action.GetType()).Select(Identity).ToArray(),
                            VariableValues = NamedValues(action.GetType()).Select(field => VariableValue(field.GetValue(action))).ToArray() }).ToArray() };
                }).ToArray() };
            Modding.Logger.Log("[HKTAS] Captured Dream Return C state=" + result.Active + " entered=" + result.Fields[typeof(Fsm).FullName + "/activeStateEntered"]
                + " activeActions=" + string.Join(",", result.States.Where(s => s.Name == result.Active).SelectMany(s => s.Actions?.Select(a => a.Type) ?? Array.Empty<string>())));
            return result;
        }
        internal Action PrepareApply(HeroController hero)
        {
            var component = Bind(hero); var fsm = (Fsm)RawFsm.GetValue(component); RequireNoDelayedEvents(fsm);
            var states = (FsmState[])StateArray.GetValue(fsm);
            if (States == null || States.Length > 128 || !states.Select(s => s.Name).SequenceEqual(States.Select(s => s.Name)))
                throw new InvalidDataException("Dream Return state layout differs.");
            FsmState? State(string? name) => name == null ? null : states.Single(s => s.Name == name);
            var active = State(Active); var previous = State(Previous); var switchTo = State(SwitchTo);
            var writes = new List<Action> { CheckpointScalarFields.PrepareApply(fsm, FsmFields, Fields), Variables.PrepareApply(component, false) };
            for (var index = 0; index < states.Length; index++)
            {
                var state = states[index]; var data = States[index];
                writes.Add(CheckpointScalarFields.PrepareApply(state, StateFields, data.Fields));
                if (data.Actions == null)
                {
                    if (state == active) throw new InvalidDataException("Dream active state lacks loaded actions.");
                    continue;
                }
                // Normal action-data factory initializes managed action objects
                // and prefab bindings; it never runs OnEnter or a game frame.
                var actions = state.Actions;
                if (data.Actions.Length > 128 || !actions.Select(a => a.GetType().FullName).SequenceEqual(data.Actions.Select(a => a.Type)))
                    throw new InvalidDataException("Dream Return action layout differs: " + state.Name);
                for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                {
                    var action = actions[actionIndex]; var saved = data.Actions[actionIndex];
                    var variables = NamedValues(action.GetType());
                    if (!variables.Select(Identity).SequenceEqual(saved.VariableFields) || variables.Length != saved.VariableValues.Length)
                        throw new InvalidDataException("Dream Return action value layout differs.");
                    writes.Add(CheckpointScalarFields.PrepareApply(action, Scalars(action.GetType()), saved.Fields));
                    for (var v = 0; v < variables.Length; v++)
                    {
                        var variable = variables[v].GetValue(action); var value = saved.VariableValues[v];
                        if (value.ContainsKey("null")) { if (variable != null || value.Count != 1 || value["null"] != "true") throw new InvalidDataException("Dream action null binding differs."); }
                        else
                        {
                            if (variable == null || ((NamedVariable)variable).CastVariable != null) throw new InvalidDataException("Dream action variable binding differs.");
                            writes.Add(CheckpointScalarFields.PrepareApply(variable, new[] { Field(variable.GetType(), "value") }, value));
                        }
                    }
                }
                FsmStateAction? ActionAt(int i) => i == -1 ? null : i >= 0 && i < actions.Length ? actions[i]
                    : throw new InvalidDataException("Dream action ordinal is outside its bound.");
                var currentAction = ActionAt(data.ActiveAction);
                var activeActions = data.ActiveActions?.Select(i => ActionAt(i) ?? throw new InvalidDataException("Null active action.")).ToList();
                var finishedActions = data.FinishedActions?.Select(i => ActionAt(i) ?? throw new InvalidDataException("Null finished action.")).ToList();
                writes.Add(() => { Field(typeof(FsmState), "activeAction").SetValue(state, currentAction);
                    Field(typeof(FsmState), "activeActions").SetValue(state, activeActions); Field(typeof(FsmState), "_finishedActions").SetValue(state, finishedActions); });
            }
            Modding.Logger.Log("[HKTAS] Applying Dream Return C state=" + Active + " current=" + component.ActiveStateName);
            return () => { foreach (var write in writes) write(); Field(typeof(Fsm), "activeState").SetValue(fsm, active);
                Field(typeof(Fsm), "previousActiveState").SetValue(fsm, previous); Field(typeof(Fsm), "switchToState").SetValue(fsm, switchTo); };
        }
    }
}
