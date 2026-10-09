using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This adapter mutates already-bound local value instances. It does not
    // restart an FSM or claim to restore its actions, references or scheduler.
    [Serializable]
    internal sealed class CheckpointHeroSpellVariables
    {
        public string FsmName = "", StateName = "";
        public string[] Identities = null!;
        public int[] Aliases = null!;
        public Dictionary<string, string>[] Values = null!;
        private static readonly string[] Groups = { "floatVariables", "intVariables", "boolVariables",
            "stringVariables", "vector2Variables", "vector3Variables", "quaternionVariables", "colorVariables", "rectVariables" };
        private static IEnumerable<NamedVariable> Variables(PlayMakerFSM fsm)
        {
            foreach (var group in Groups)
            {
                var array = (NamedVariable[]?)typeof(FsmVariables).GetField(group, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fsm.FsmVariables);
                if (array != null) foreach (var variable in array) yield return variable;
            }
        }
        private static FieldInfo Value(NamedVariable variable) => variable.GetType().GetField("value", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidDataException("Spell FSM variable has no raw value field.");
        private static string Identity(NamedVariable variable, int ordinal) => ordinal + ":" + variable.GetType().FullName + "/" + variable.Name;
        private static int[] AliasOrdinals(NamedVariable[] variables) => variables.Select(variable =>
            Array.FindIndex(variables, candidate => ReferenceEquals(candidate, variable))).ToArray();
        internal static CheckpointHeroSpellVariables Capture(HeroController hero)
        {
            var fsm = hero.spellControl;
            if (fsm == null || fsm.gameObject != hero.gameObject)
                throw new InvalidOperationException("Checkpoint Hero Spell Control binding is absent.");
            var variables = Variables(fsm).ToArray();
            if (variables.Length > 1024 || variables.Any(v => v.CastVariable != null))
                throw new InvalidDataException("Spell FSM variables exceed their bound or use an unsupported cast binding.");
            return new CheckpointHeroSpellVariables { FsmName = fsm.FsmName, StateName = fsm.ActiveStateName,
                Identities = variables.Select(Identity).ToArray(), Aliases = AliasOrdinals(variables),
                Values = variables.Select(v => CheckpointScalarFields.Capture(v, new[] { Value(v) })).ToArray() };
        }
        internal Action PrepareApply(HeroController hero)
        {
            var fsm = hero.spellControl;
            if (fsm == null || fsm.gameObject != hero.gameObject || fsm.FsmName != FsmName || fsm.ActiveStateName != StateName)
                throw new InvalidDataException("Checkpoint Spell Control binding or active state differs: " + fsm?.ActiveStateName + " / " + StateName);
            var variables = Variables(fsm).ToArray();
            if (Identities == null || Values == null || Values.Length != Identities.Length || variables.Length > 1024
                || variables.Any(v => v.CastVariable != null) || Aliases == null || !AliasOrdinals(variables).SequenceEqual(Aliases)
                || !variables.Select(Identity).SequenceEqual(Identities))
                throw new InvalidDataException("Checkpoint Spell Control variable layout differs.");
            var writes = variables.Select((variable, index) =>
            {
                var field = Value(variable);
                var current = CheckpointScalarFields.Capture(variable, new[] { field });
                if (Values[index].Any(pair => !current.TryGetValue(pair.Key, out var old) || old != pair.Value))
                    Modding.Logger.Log("[HKTAS] Checkpoint spell variable differs " + Identities[index]
                        + ": current=" + string.Join(",", current.Values) + " captured=" + string.Join(",", Values[index].Values));
                return CheckpointScalarFields.PrepareApply(variable, new[] { field }, Values[index]);
            }).ToArray();
            return () => { foreach (var write in writes) write(); };
        }
    }
}
