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
        public int Version = 19;
        public Dictionary<string, string> GameManager = null!, Hero = null!, HeroStates = null!;
        public Vector2[]? HeroPositionHistory;
        public Vector3 HeroLocalPosition, HeroLocalScale;
        public Quaternion HeroLocalRotation;
        public RigidbodyType2D HeroBodyType;
        public Vector2 HeroBodyVelocity;
        public float HeroBodyGravityScale;
        public bool HeroBodySimulated;
        public CheckpointCameraState Camera = null!;
        public CheckpointNailSlashState[] NailSlashes = null!;
        public CheckpointHeroSpellVariables SpellVariables = null!;
        public CheckpointPersistentFsmState DreamReturn = null!;
        public CheckpointHeroLightState HeroLight = null!;
        public CheckpointHeroInvulnerability Invulnerability = null!;
        public CheckpointSpatterState Spatters = null!;
        public CheckpointGeoState Geo = null!;
        public CheckpointProjectileState Projectiles = null!;
        public CheckpointPhysicsBroadphaseState Broadphase = null!;
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
            if (hero.transform.parent != null)
                throw new InvalidOperationException("Checkpoint Hero must have left its scene parent at C.");
            var body = hero.GetComponent<Rigidbody2D>() ?? throw new InvalidOperationException("Checkpoint Hero body is absent.");
            var physics = new LoadingRigidbodyObservation();
            var scheduler = new LoadingNativeSchedulerDigest();
            return new CheckpointManagedWorldData
            {
                GameManager = CheckpointScalarFields.Capture(manager, ManagerFields),
                Hero = CheckpointScalarFields.Capture(hero, HeroFields),
                HeroStates = CheckpointScalarFields.Capture(hero.cState, StateFields),
                HeroLocalPosition = hero.transform.localPosition,
                HeroLocalRotation = hero.transform.localRotation,
                HeroLocalScale = hero.transform.localScale,
                HeroBodyType = body.bodyType,
                HeroBodyVelocity = physics.CaptureCheckpointVelocity(body),
                HeroBodyGravityScale = body.gravityScale,
                HeroBodySimulated = body.simulated,
                Camera = CheckpointCameraState.Capture(),
                NailSlashes = CheckpointNailSlashState.Capture(hero),
                SpellVariables = CheckpointHeroSpellVariables.Capture(hero),
                DreamReturn = CheckpointPersistentFsmState.Capture(hero),
                HeroLight = CheckpointHeroLightState.Capture(hero),
                Invulnerability = CheckpointHeroInvulnerability.Capture(hero, scheduler),
                Spatters = CheckpointSpatterState.Capture(physics),
                Geo = CheckpointGeoState.Capture(physics),
                Projectiles = CheckpointProjectileState.Capture(physics, scheduler),
                Broadphase = CheckpointPhysicsBroadphaseState.Capture(),
                Boss = CheckpointBossContext.Capture(LoadingSerialSceneProbe.CheckpointBossBindings),
                HeroPositionHistory = PositionHistory.GetValue(hero) is Vector2[] history ? (Vector2[])history.Clone() : null
            };
        }
        internal Action PrepareApply(PlayerData playerData, SceneData sceneData)
        {
            if (Version != 19 || !Enum.IsDefined(typeof(RigidbodyType2D), HeroBodyType)
                || RemainingDomains == null || !RemainingDomains.SequenceEqual(new[] { "persistent-object-bindings",
                "fsm-actions-and-variables", "coroutine-scheduler", "physics-world", "mod-state", "animation-and-audio" })
                || HeroPositionHistory?.Length > 1024)
                throw new InvalidDataException("Unsupported managed world checkpoint coverage.");
            var manager = global::GameManager.instance ?? throw new InvalidOperationException("Restore GameManager is absent.");
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Restore Hero is absent.");
            var body = hero.GetComponent<Rigidbody2D>() ?? throw new InvalidOperationException("Restore Hero body is absent.");
            Modding.Logger.Log("[HKTAS] Checkpoint Hero body type: current=" + body.bodyType + " captured=" + HeroBodyType);
            if (hero.transform.parent != null || new[] { HeroLocalPosition.x, HeroLocalPosition.y, HeroLocalPosition.z,
                HeroLocalRotation.x, HeroLocalRotation.y, HeroLocalRotation.z, HeroLocalRotation.w,
                HeroLocalScale.x, HeroLocalScale.y, HeroLocalScale.z, HeroBodyVelocity.x, HeroBodyVelocity.y, HeroBodyGravityScale }
                .Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                throw new InvalidDataException("Checkpoint Hero transform or its binding is invalid.");
            var currentPlayer = PlayerData.instance;
            if (currentPlayer == null || manager.sceneData == null || hero.cState == null
                || !ReferenceEquals(manager.playerData, currentPlayer) || !ReferenceEquals(hero.playerData, currentPlayer))
                throw new InvalidOperationException("Restore managed world roots are not initialized or their aliases differ.");
            var applyManager = CheckpointScalarFields.PrepareApply(manager, ManagerFields, GameManager);
            var applyHero = CheckpointScalarFields.PrepareApply(hero, HeroFields, Hero);
            var applyStates = CheckpointScalarFields.PrepareApply(hero.cState, StateFields, HeroStates);
            var applySlashes = CheckpointNailSlashState.PrepareApply(hero, NailSlashes);
            var applySpellVariables = (SpellVariables ?? throw new InvalidDataException("Checkpoint Spell Control values are absent.")).PrepareApply(hero);
            var applyDreamReturn = (DreamReturn ?? throw new InvalidDataException("Checkpoint Dream Return state is absent.")).PrepareApply(hero);
            var applyHeroLight = (HeroLight ?? throw new InvalidDataException("Checkpoint HeroLight state is absent.")).PrepareApply(hero);
            var applyCamera = (Camera ?? throw new InvalidDataException("Checkpoint camera state is absent.")).PrepareApply();
            var applyPulse = (Invulnerability ?? throw new InvalidDataException("Checkpoint invulnerability state is absent.")).PreparePulseApply(hero);
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
                // Facing flags and the persistent native Transform are separate
                // state. FlipSprite multiplies the current scale, so restoring
                // only cState leaves later attack colliders facing differently.
                hero.transform.localPosition = HeroLocalPosition;
                hero.transform.localRotation = HeroLocalRotation;
                hero.transform.localScale = HeroLocalScale;
                // Preparation's native Kinematic body survives a direct transition.
                // Restore its actual captured mode; entering from above does
                // not reset it and therefore cannot reconstruct it implicitly.
                if (body.bodyType != HeroBodyType) body.bodyType = HeroBodyType;
                // Horizontal entry saves the current native gravity as the
                // previous value. A fresh preparation body can still have zero.
                // Restore the captured property before the original entry runs.
                body.gravityScale = HeroBodyGravityScale;
                body.simulated = HeroBodySimulated;
                body.velocity = HeroBodyVelocity;
                applyCamera();
                applySlashes();
                applySpellVariables();
                applyDreamReturn();
                applyHeroLight();
                applyPulse();
            };
        }
    }

    [Serializable]
    internal sealed class CheckpointNailSlashState
    {
        public string Binding = "", ObjectName = "";
        public Dictionary<string, string> Fields = null!;
        public Vector3 LocalPosition, LocalScale;
        public Quaternion LocalRotation;
        private static readonly FieldInfo[] ScalarFields = CheckpointScalarFields.Declared(typeof(NailSlash)).Where(f =>
            f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(string) || f.FieldType == typeof(Vector3)).ToArray();
        private static readonly FieldInfo Slashing = typeof(NailSlash).GetField("slashing", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly string[] Bindings = { "normalSlash", "alternateSlash", "upSlash", "downSlash", "wallSlash" };
        private static NailSlash Resolve(HeroController hero, string binding) =>
            (NailSlash)typeof(HeroController).GetField(binding)!.GetValue(hero);
        internal static CheckpointNailSlashState[] Capture(HeroController hero) => Bindings.Select(binding =>
        {
            var slash = Resolve(hero, binding);
            if (slash == null || slash.transform.root != hero.transform || (bool)Slashing.GetValue(slash))
                throw new InvalidOperationException("Checkpoint requires an initialized, quiescent Hero nail slash: " + binding);
            return new CheckpointNailSlashState { Binding = binding, ObjectName = slash.name,
                Fields = CheckpointScalarFields.Capture(slash, ScalarFields), LocalPosition = slash.transform.localPosition,
                LocalRotation = slash.transform.localRotation, LocalScale = slash.transform.localScale };
        }).ToArray();
        internal static Action PrepareApply(HeroController hero, CheckpointNailSlashState[] states)
        {
            if (states == null || !states.Select(s => s.Binding).SequenceEqual(Bindings))
                throw new InvalidDataException("Checkpoint Hero nail slash bindings differ.");
            var writes = states.Select(state =>
            {
                var slash = Resolve(hero, state.Binding);
                if (slash == null || slash.transform.root != hero.transform || slash.name != state.ObjectName
                    || (bool)Slashing.GetValue(slash) || state.Fields[typeof(NailSlash).FullName + "/slashing"] != "System.Boolean:False"
                    || new[] { state.LocalPosition.x, state.LocalPosition.y, state.LocalPosition.z,
                        state.LocalRotation.x, state.LocalRotation.y, state.LocalRotation.z, state.LocalRotation.w,
                        state.LocalScale.x, state.LocalScale.y, state.LocalScale.z }.Any(v => float.IsNaN(v) || float.IsInfinity(v)))
                    throw new InvalidDataException("Checkpoint Hero nail slash is active or its native binding differs.");
                var apply = CheckpointScalarFields.PrepareApply(slash, ScalarFields, state.Fields);
                var current = CheckpointScalarFields.Capture(slash, ScalarFields);
                var differences = state.Fields.Where(pair => current[pair.Key] != pair.Value)
                    .Select(pair => pair.Key + "=" + pair.Value).ToArray();
                Modding.Logger.Log("[HKTAS] Checkpoint nail slash differing fields " + state.Binding + ": " + string.Join(",", differences));
                return (Action)(() => { apply(); slash.transform.localPosition = state.LocalPosition;
                    slash.transform.localRotation = state.LocalRotation; slash.transform.localScale = state.LocalScale; });
            }).ToArray();
            return () => { foreach (var write in writes) write(); };
        }
        internal static string ScalarDigest(HeroController hero)
        {
            var values = Bindings.Select(binding =>
            {
                var slash = Resolve(hero, binding);
                return slash == null ? binding + ":missing" : binding + ":" + string.Join(";",
                    CheckpointScalarFields.Capture(slash, ScalarFields).Select(pair => pair.Key + "=" + pair.Value));
            });
            return HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(string.Join("|", values)));
        }
    }
}
