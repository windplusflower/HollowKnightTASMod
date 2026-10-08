using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    [Serializable]
    internal sealed class CheckpointBossContext
    {
        public BossSequenceController.BossSequenceData? SequenceData;
        public bool SequenceAliasesPlayerData;
        public string SequenceAsset = "", SetupKind = "None", SetupType = "", UiBinding = "", StatueBinding = "";
        public int SetupMethod;
        public Dictionary<string, string> SequenceScalars = null!, SetupScalars = new Dictionary<string, string>(), StatueScalars = new Dictionary<string, string>();
        private static readonly FieldInfo DataField = Field(typeof(BossSequenceController), "currentData", true);
        private static readonly FieldInfo SequenceField = Field(typeof(BossSequenceController), "currentSequence", true);
        private static readonly FieldInfo UiStatueField = Field(typeof(BossChallengeUI), "bossStatue");
        private static readonly FieldInfo[] SequenceFields = CheckpointScalarFields.Declared(typeof(BossSequenceController), true)
            .Where(field => field.FieldType.IsPrimitive || field.FieldType.IsEnum).ToArray();
        private static readonly FieldInfo[] StatueFields = CheckpointScalarFields.Declared(typeof(BossStatue)).Where(field =>
            field.Name == "statueStatePD" || field.Name == "dreamStatueStatePD").ToArray();
        private static FieldInfo Field(Type type, string name, bool isStatic = false) => type.GetField(name,
            BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance))
            ?? throw new MissingFieldException(type.FullName, name);
        private static string Binding(Component component)
        {
            var parts = new List<string>();
            for (var item = component.transform; item != null; item = item.parent) parts.Add(item.name);
            parts.Reverse(); return component.gameObject.scene.name + "/" + string.Join("/", parts);
        }
        private static Tuple<BossChallengeUI, BossStatue> StatueSetup(Delegate setup)
        {
            var type = setup.Method.DeclaringType!;
            if (!type.IsDefined(typeof(CompilerGeneratedAttribute), false) || type.DeclaringType != typeof(BossChallengeUI)
                || !setup.Method.Name.StartsWith("<LoadBoss>", StringComparison.Ordinal) || setup.Target?.GetType() != type)
                throw new InvalidDataException("Boss setup callback requires a registered checkpoint adapter.");
            var ui = (BossChallengeUI)CheckpointScalarFields.Declared(type).Single(field => field.FieldType == typeof(BossChallengeUI)).GetValue(setup.Target);
            if (Field(type, "<>9__1").GetValue(setup.Target) != null)
                throw new InvalidDataException("Boss statue setup was already invoked before C.");
            return Tuple.Create(ui, (BossStatue)UiStatueField.GetValue(ui));
        }
        // Capture symbolic component bindings while the source is still alive;
        // actual scalar state is read later at C, after destruction unwinds.
        internal static Tuple<string, string>? CaptureBindingsBeforeUnload()
        {
            var setup = BossSceneController.SetupEvent;
            if (setup == null || setup.GetInvocationList().Length != 1 || setup.Method.DeclaringType?.DeclaringType != typeof(BossChallengeUI)) return null;
            var owners = StatueSetup(setup); return Tuple.Create(Binding(owners.Item1), Binding(owners.Item2));
        }
        internal static CheckpointBossContext Capture(Tuple<string, string>? bindings)
        {
            var data = (BossSequenceController.BossSequenceData?)DataField.GetValue(null);
            var sequence = (BossSequence?)SequenceField.GetValue(null);
            var result = new CheckpointBossContext
            {
                SequenceData = data, SequenceAliasesPlayerData = data != null && ReferenceEquals(data, PlayerData.instance.currentBossSequence),
                SequenceAsset = sequence == null ? "" : sequence.name,
                SequenceScalars = CheckpointScalarFields.Capture(null, SequenceFields)
            };
            var setup = BossSceneController.SetupEvent;
            if (setup == null) return result;
            if (setup.GetInvocationList().Length != 1) throw new InvalidDataException("Multiple boss setup callbacks need an adapter.");
            if (setup.Method.DeclaringType?.DeclaringType == typeof(BossSequenceController))
            { result.SetupKind = "Sequence"; return result; }
            var owners = StatueSetup(setup);
            if (bindings == null) throw new InvalidDataException("Boss statue setup bindings were not frozen before unload.");
            result.SetupKind = "Statue"; result.SetupType = setup.Method.DeclaringType!.FullName!; result.SetupMethod = setup.Method.MetadataToken;
            result.UiBinding = bindings.Item1; result.StatueBinding = bindings.Item2;
            result.SetupScalars = CheckpointScalarFields.Capture(setup.Target, CheckpointScalarFields.Declared(setup.Target!.GetType()).Where(field => field.FieldType.IsPrimitive));
            result.StatueScalars = CheckpointScalarFields.Capture(owners.Item2, StatueFields);
            return result;
        }
        internal sealed class Targets
        {
            internal BossSequence? Sequence;
            internal BossStatue? Statue;
        }
        internal Targets BindBeforeUnload()
        {
            var targets = new Targets();
            if (SequenceAsset.Length != 0)
            {
                if (SequenceData == null || SequenceAsset != SequenceData.bossSequenceName || SequenceAsset.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new InvalidDataException("Invalid boss sequence asset identity.");
                targets.Sequence = Resources.Load<BossSequence>("GG/" + SequenceAsset);
                if (targets.Sequence == null || targets.Sequence.name != SequenceAsset) throw new InvalidDataException("Boss sequence asset cannot be rebound.");
            }
            if (SetupKind == "Statue")
            {
                var statues = Resources.FindObjectsOfTypeAll<BossStatue>().Where(value => value != null && value.gameObject.scene.IsValid() && Binding(value) == StatueBinding).ToArray();
                if (statues.Length != 1) throw new InvalidDataException("Boss statue source binding is absent or ambiguous: " + StatueBinding);
                targets.Statue = statues[0];
            }
            return targets;
        }
        internal Action PrepareApply(Targets targets, PlayerData player)
        {
            var applySequence = CheckpointScalarFields.PrepareApply(null, SequenceFields, SequenceScalars);
            var data = SequenceAliasesPlayerData ? player.currentBossSequence : SequenceData;
            if (SequenceAliasesPlayerData && (SequenceData == null || data == null)) throw new InvalidDataException("Boss sequence data alias is absent.");
            BossSceneController.SetupEventDelegate? setup = null; Action? applyStatue = null;
            if (SetupKind == "Statue")
            {
                var method = typeof(BossChallengeUI).Module.ResolveMethod(SetupMethod) as MethodInfo;
                var type = method?.DeclaringType;
                if (method == null || type?.FullName != SetupType || type.DeclaringType != typeof(BossChallengeUI)
                    || !type.IsDefined(typeof(CompilerGeneratedAttribute), false) || !method.Name.StartsWith("<LoadBoss>", StringComparison.Ordinal)
                    || ReferenceEquals(targets.Statue, null)) throw new InvalidDataException("Boss statue setup identity or binding differs.");
#pragma warning disable SYSLIB0050
                var closure = FormatterServices.GetUninitializedObject(type);
#pragma warning restore SYSLIB0050
                var members = CheckpointScalarFields.Declared(type);
                if (members.Count(field => field.FieldType == typeof(BossChallengeUI)) != 1
                    || members.Any(field => field.FieldType != typeof(BossChallengeUI) && !field.FieldType.IsPrimitive
                        && !(field.Name == "<>9__1" && field.FieldType == typeof(Action))))
                    throw new InvalidDataException("Boss statue callback closure layout differs.");
                CheckpointScalarFields.PrepareApply(closure, members.Where(field => field.FieldType.IsPrimitive), SetupScalars)();
                var level = (int)members.Single(field => field.Name == "level" && field.FieldType == typeof(int)).GetValue(closure);
                if (level < 0 || level > 2) throw new InvalidDataException("Boss statue challenge level is invalid.");
                setup = RebindStatueSetup(targets.Statue!, level);
                applyStatue = CheckpointScalarFields.PrepareApply(targets.Statue, StatueFields, StatueScalars);
            }
            else if (SetupKind != "None" && SetupKind != "Sequence") throw new InvalidDataException("Unknown boss setup adapter.");
            if (SetupKind == "Sequence" && (targets.Sequence == null || data == null)) throw new InvalidDataException("Boss sequence setup data is absent.");
            return () =>
            {
                DataField.SetValue(null, data); SequenceField.SetValue(null, targets.Sequence); applySequence();
                applyStatue?.Invoke();
                BossSceneController.SetupEvent = setup;
                if (SetupKind == "Sequence") typeof(BossSequenceController).GetMethod("SetupBossScene", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            };
        }
        // Registered adapter for the pinned BossChallengeUI.LoadBoss callback.
        // The transient challenge UI need not exist in the replacement world.
        // Its callback only reads the retained source statue and selected level;
        // reproduce those reads and the original game hooks, without UI actions.
        private static BossSceneController.SetupEventDelegate RebindStatueSetup(BossStatue statue, int level) => self =>
        {
            self.BossLevel = level; self.DreamReturnEvent = "DREAM RETURN";
            self.OnBossesDead += () =>
            {
                var field = statue.UsingDreamVersion ? statue.dreamStatueStatePD : statue.statueStatePD;
                var completion = GameManager.instance.GetPlayerDataVariable<BossStatue.Completion>(field);
                if (level == 0) completion.completedTier1 = true;
                else if (level == 1) completion.completedTier2 = true;
                else completion.completedTier3 = true;
                GameManager.instance.SetPlayerDataVariable(field, completion);
                GameManager.instance.playerData.SetString("currentBossStatueCompletionKey", statue.UsingDreamVersion ? statue.dreamStatueStatePD : statue.statueStatePD);
                GameManager.instance.playerData.SetInt("bossStatueTargetLevel", level);
            };
            self.OnBossSceneComplete += () => self.DoDreamReturn();
        };
    }
}
