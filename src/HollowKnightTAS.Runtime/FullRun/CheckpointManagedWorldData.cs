using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    [Serializable]
    internal sealed class CheckpointManagedWorldData
    {
        public int Version = 1;
        public Dictionary<string, string> GameManager = null!, Hero = null!, HeroStates = null!;
        public Vector2[]? HeroPositionHistory;
        public CheckpointBossContext Boss = null!;
        // Explicit data coverage. Reference/native domains are not implied by
        // capturing their owner's scalar fields.
        public string[] RemainingDomains = { "persistent-object-bindings", "fsm-actions-and-variables",
            "coroutine-scheduler", "physics-world", "mod-state", "animation-and-audio" };
        private static FieldInfo[] Scalars(Type type) => CheckpointScalarFields.Declared(type).Where(field =>
            field.FieldType.IsPrimitive && field.FieldType != typeof(IntPtr) && field.FieldType != typeof(UIntPtr)
            || field.FieldType.IsEnum || field.FieldType == typeof(string)
            || field.FieldType == typeof(Vector2) || field.FieldType == typeof(Vector3)).ToArray();
        private static readonly FieldInfo[] ManagerFields = Scalars(typeof(global::GameManager));
        private static readonly FieldInfo[] HeroFields = Scalars(typeof(HeroController));
        private static readonly FieldInfo[] StateFields = CheckpointScalarFields.Declared(typeof(HeroControllerStates));
        private static readonly FieldInfo PositionHistory = typeof(HeroController).GetField("positionHistory", BindingFlags.Instance | BindingFlags.NonPublic)!;
        internal static CheckpointManagedWorldData Capture()
        {
            var manager = global::GameManager.instance ?? throw new InvalidOperationException("Checkpoint GameManager is absent.");
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Checkpoint Hero is absent.");
            return new CheckpointManagedWorldData
            {
                GameManager = CheckpointScalarFields.Capture(manager, ManagerFields),
                Hero = CheckpointScalarFields.Capture(hero, HeroFields),
                HeroStates = CheckpointScalarFields.Capture(hero.cState, StateFields),
                Boss = CheckpointBossContext.Capture(LoadingSerialSceneProbe.CheckpointBossBindings),
                HeroPositionHistory = PositionHistory.GetValue(hero) is Vector2[] history ? (Vector2[])history.Clone() : null
            };
        }
        internal Action PrepareApply(PlayerData playerData, SceneData sceneData)
        {
            if (Version != 1 || RemainingDomains == null || !RemainingDomains.SequenceEqual(new[] { "persistent-object-bindings",
                "fsm-actions-and-variables", "coroutine-scheduler", "physics-world", "mod-state", "animation-and-audio" })
                || HeroPositionHistory?.Length > 1024)
                throw new InvalidDataException("Unsupported managed world checkpoint coverage.");
            var manager = global::GameManager.instance ?? throw new InvalidOperationException("Restore GameManager is absent.");
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Restore Hero is absent.");
            var currentPlayer = PlayerData.instance;
            if (currentPlayer == null || manager.sceneData == null || hero.cState == null
                || !ReferenceEquals(manager.playerData, currentPlayer) || !ReferenceEquals(hero.playerData, currentPlayer))
                throw new InvalidOperationException("Restore managed world roots are not initialized or their aliases differ.");
            var applyManager = CheckpointScalarFields.PrepareApply(manager, ManagerFields, GameManager);
            var applyHero = CheckpointScalarFields.PrepareApply(hero, HeroFields, Hero);
            var applyStates = CheckpointScalarFields.PrepareApply(hero.cState, StateFields, HeroStates);
            var history = HeroPositionHistory == null ? null : (Vector2[])HeroPositionHistory.Clone();
            var playerWrites = CheckpointScalarFields.Declared(typeof(PlayerData)).Select(f => Tuple.Create(f, f.GetValue(playerData))).ToArray();
            var sceneWrites = CheckpointScalarFields.Declared(typeof(SceneData)).Select(f => Tuple.Create(f, f.GetValue(sceneData))).ToArray();
            return () =>
            {
                // Keep root instances bound to the newly initialized game's
                // consumers. Detached data never replaces the singleton roots.
                foreach (var write in playerWrites) write.Item1.SetValue(currentPlayer, write.Item2);
                foreach (var write in sceneWrites) write.Item1.SetValue(manager.sceneData, write.Item2);
                applyManager(); applyHero(); applyStates(); PositionHistory.SetValue(hero, history);
            };
        }
    }
}
