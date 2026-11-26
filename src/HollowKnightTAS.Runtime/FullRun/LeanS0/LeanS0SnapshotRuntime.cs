using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using GlobalEnums;
using HollowKnightTAS.Core.Movie;
using HollowKnightTAS.Runtime.Input;
using HollowKnightTAS.Runtime.Timing;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.SceneManagement;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class LeanS0CaptureContext
    {
        public int Ordinal { get; set; }
        public string SourceSceneName { get; set; } = string.Empty;
        public string TargetSceneName { get; set; } = string.Empty;
        public GameManager.SceneLoadInfo? LoadInfo { get; set; }
        public int RngSeed { get; set; }
        public string RngSha256 { get; set; } = string.Empty;
        public MovieV2Document Movie { get; set; } = null!;
        public string MovieId { get; set; } = string.Empty;
        public long MovieFrame { get; set; }
        public long NativeFrame { get; set; }
        public long ConsumedSamples { get; set; }
        public long SkippedLoadingFrames { get; set; }
        public int TimingRunIndex { get; set; }
        public long TimingRunStart { get; set; }
        public decimal ActiveFrameRate { get; set; }
        public decimal RecordingFrameRate { get; set; }
        public bool TitleReadySeen { get; set; }
        public bool FirstLevelActivationPending { get; set; }
        public string RandomSeedScene { get; set; } = string.Empty;
        public long LastGameUpdateNativeFrame { get; set; }
        public double GameUpdateDeltaSeconds { get; set; }
        public double GameUpdateTimeSeconds { get; set; }
        public double ExecutedMovieSeconds { get; set; }
        public long MovieInputNativeOrigin { get; set; }
        public CanonicalInputEpoch? MovieInputControlOrigin { get; set; }
        public NativeFullRunFrameClock Clock { get; set; } = null!;
        public FullRunActionSetAdapter Input { get; set; } = null!;
        public FullRunMouseBridge Mouse { get; set; } = null!;
        public Func<byte[]> ExportRandomBytes { get; set; } = null!;
        public Func<byte[]> ExportClockObserverBytes { get; set; } = null!;
        public string MoviePrefixSha256 { get; set; } = string.Empty;
    }

    internal static class LeanS0SnapshotRuntime
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo StaticVariablesField = typeof(StaticVariableList).GetField("variables", AnyStatic)
            ?? throw new MissingFieldException("StaticVariableList.variables");

        private static readonly FieldInfo BossCurrentDataField = typeof(BossSequenceController).GetField("currentData", AnyStatic)
            ?? throw new MissingFieldException("BossSequenceController.currentData");
        private static readonly FieldInfo BossCurrentSequenceField = typeof(BossSequenceController).GetField("currentSequence", AnyStatic)
            ?? throw new MissingFieldException("BossSequenceController.currentSequence");
        private static readonly FieldInfo BossIndexField = typeof(BossSequenceController).GetField("bossIndex", AnyStatic)
            ?? throw new MissingFieldException("BossSequenceController.bossIndex");
        private static readonly FieldInfo BossWasCompletedField = typeof(BossSequenceController).GetField("<WasCompleted>k__BackingField", AnyStatic)
            ?? throw new MissingFieldException("BossSequenceController.<WasCompleted>k__BackingField");
        private static readonly MethodInfo BossSetupBossSceneMethod = typeof(BossSequenceController).GetMethod("SetupBossScene", AnyStatic)
            ?? throw new MissingMethodException("BossSequenceController.SetupBossScene");
        private static readonly FieldInfo BossStatueCurrentSceneField = typeof(BossStatueLoadManager).GetField("currentBossScene", AnyStatic)
            ?? throw new MissingFieldException("BossStatueLoadManager.currentBossScene");
        private static readonly FieldInfo BossStatuePreviousSceneField = typeof(BossStatueLoadManager).GetField("previousBossScene", AnyStatic)
            ?? throw new MissingFieldException("BossStatueLoadManager.previousBossScene");
        private static readonly FieldInfo BossLastSelectedField = typeof(BossChallengeUI).GetField("lastSelectedButton", AnyStatic)
            ?? throw new MissingFieldException("BossChallengeUI.lastSelectedButton");
        private static readonly FieldInfo? BossChallengePrimaryLevelField = CanonicalBossChallengeOriginal.Primary?.GetField("level", AnyInstance);
        private static readonly FieldInfo? BossChallengePrimaryUiField = CanonicalBossChallengeOriginal.Primary?.GetField("<>4__this", AnyInstance);
        private static readonly FieldInfo? BossChallengePrimaryCachedField = CanonicalBossChallengeOriginal.Primary?.GetField("<>9__1", AnyInstance);
        private static readonly FieldInfo? BossChallengeUiStatueField = typeof(BossChallengeUI).GetField("bossStatue", AnyInstance);

        private static readonly FieldInfo TimeSlowedCountField = typeof(GameManager).GetField("timeSlowedCount", AnyInstance)
            ?? throw new MissingFieldException("GameManager.timeSlowedCount");

        private static readonly FieldInfo AudioCurrentMusicCueField = typeof(AudioManager).GetField("currentMusicCue", AnyInstance)
            ?? throw new MissingFieldException("AudioManager.currentMusicCue");
        private static readonly FieldInfo AudioCurrentAtmosCueField = typeof(AudioManager).GetField("currentAtmosCue", AnyInstance)
            ?? throw new MissingFieldException("AudioManager.currentAtmosCue");

        private static readonly FieldInfo CameraLockAreaField = typeof(CameraController).GetField("currentLockArea", AnyInstance)
            ?? throw new MissingFieldException("CameraController.currentLockArea");
        private static readonly FieldInfo[] CameraControllerScalarFields = CameraScalars(typeof(CameraController));
        private static readonly FieldInfo[] CameraTargetScalarFields = CameraScalars(typeof(CameraTarget));

        private static FieldInfo[] CameraScalars(Type type) => CheckpointScalarFields.Declared(type).Where(f =>
            (f.FieldType.IsPrimitive && f.FieldType != typeof(IntPtr) && f.FieldType != typeof(UIntPtr))
            || f.FieldType.IsEnum || f.FieldType == typeof(string)
            || f.FieldType == typeof(Vector2) || f.FieldType == typeof(Vector3)).ToArray();

        private static readonly FieldInfo HcExitedSuperDashing = HeroField("exitedSuperDashing");
        private static readonly FieldInfo HcExitedQuake = HeroField("exitedQuake");
        private static readonly FieldInfo HcEnterWithoutInput = HeroField("enterWithoutInput");
        private static readonly FieldInfo HcAttackCooldown = HeroField("attack_cooldown");
        private static readonly FieldInfo HcDashCooldownTimer = HeroField("dashCooldownTimer");
        private static readonly FieldInfo HcShadowDashTimer = HeroField("shadowDashTimer");
        private static readonly FieldInfo HcHitsSinceShielded = HeroField("hitsSinceShielded");
        private static readonly FieldInfo HcWieldingLantern = HeroField("wieldingLantern");
        private static readonly FieldInfo HcTakeNoDamage = HeroField("takeNoDamage");
        private static readonly FieldInfo HcBlockerFix = HeroField("blockerFix");
        private static readonly FieldInfo HcJumpedSteps = HeroField("jumped_steps");
        private static readonly FieldInfo HcCarefreeShieldEquipped = HeroField("carefreeShieldEquipped");
        private static readonly FieldInfo HcJoniBeam = HeroField("joniBeam");
        private static readonly FieldInfo HcNailChargeTime = HeroField("nailChargeTime");
        private static readonly FieldInfo HcPreventCastByDialogueEndTimer = HeroField("preventCastByDialogueEndTimer");
        private static readonly FieldInfo HcNailChargeTimeDefault = HeroField("NAIL_CHARGE_TIME_DEFAULT");
        private static readonly FieldInfo HcNailChargeTimeCharm = HeroField("NAIL_CHARGE_TIME_CHARM");

        private static readonly FieldInfo BounceVelocityField = BounceField("velocity");
        private static readonly FieldInfo BounceLastPosField = BounceField("lastPos");
        private static readonly FieldInfo BounceSpeedField = BounceField("speed");
        private static readonly FieldInfo BounceStepCounterField = BounceField("stepCounter");
        private static readonly FieldInfo BounceBouncingField = BounceField("bouncing");
        private static readonly FieldInfo? WaitTimerField = typeof(Wait).GetField("timer", AnyInstance);

        private static FieldInfo HeroField(string name)
            => typeof(HeroController).GetField(name, AnyInstance) ?? throw new MissingFieldException("HeroController." + name);

        private static FieldInfo BounceField(string name)
            => typeof(ObjectBounce).GetField(name, AnyInstance) ?? throw new MissingFieldException("ObjectBounce." + name);

        internal static LeanS0Packet Capture(LeanS0CaptureContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            PlayerData pd = PlayerData.instance ?? throw new InvalidOperationException("PlayerData.instance is null at S0.");
            SceneData sd = SceneData.instance ?? throw new InvalidOperationException("SceneData.instance is null at S0.");
            GameManager gm = GameManager.instance ?? throw new InvalidOperationException("GameManager.instance is null at S0.");
            HeroController hero = HeroController.SilentInstance ?? throw new InvalidOperationException("HeroController.SilentInstance is null at S0.");

            string entryGate = ctx.LoadInfo?.EntryGateName ?? string.Empty;

            var packet = new LeanS0Packet
            {
                Schema = LeanS0PacketCodec.SchemaVersion,
                BoundaryOrdinal = ctx.Ordinal,
                SourceSceneName = ctx.SourceSceneName,
                TargetSceneName = ctx.TargetSceneName,
                EntryGateName = entryGate,
                RngSeed = ctx.RngSeed,
                RngSha256 = ctx.RngSha256,
                CapturedAtUtc = "deterministic-s0",
                GameManagerProfileId = gm.profileID,
                C1_PlayerDataJson = JsonUtility.ToJson(pd),
                C2_SceneDataJson = JsonUtility.ToJson(sd),
                C3_BossSequence = CaptureBossSequence(pd),
                C4_StaticVariables = CaptureStaticVariables(),
                C5_PlayMakerGlobals = CapturePlayMakerGlobals(),
                C6_TransitionInfo = CaptureTransitionInfo(ctx.LoadInfo, ctx.TargetSceneName, entryGate),
                C7_HeroController = CaptureHeroController(hero),
                C8_KnightFsms = CaptureKnightFsms(hero),
                C9_CharmMinions = CaptureCharmMinions(hero),
                C10_Input = CaptureInputContainer(ctx.Input, ctx.Mouse),
                C11_Time = CaptureTimeState(gm),
                C12_ControlPlane = CaptureControlPlane(ctx),
                D3_MusicCue = CaptureAudioCues(gm),
                C13_Camera = CaptureCameraState(),
                C14_ModRandom = ModStartupCompatibility.CaptureModRandomState()
            };

            LeanS0PacketCodec.Validate(packet);
            return packet;
        }

        internal static GameManager.SceneLoadInfo ToGameSceneLoadInfo(LeanS0TransitionInfo dto)
        {
            if (dto == null) throw new ArgumentNullException(nameof(dto));
            return new GameManager.SceneLoadInfo
            {
                SceneName = dto.SceneName,
                EntryGateName = dto.EntryGateName,
                HeroLeaveDirection = dto.HeroLeaveDirection.HasValue ? (GatePosition?)dto.HeroLeaveDirection.Value : null,
                Visualization = (GameManager.SceneLoadVisualizations)dto.Visualization,
                EntryDelay = dto.EntryDelay,
                AlwaysUnloadUnusedAssets = dto.AlwaysUnloadUnusedAssets,
                PreventCameraFadeOut = dto.PreventCameraFadeOut,
                WaitForSceneTransitionCameraFade = dto.WaitForSceneTransitionCameraFade,
                IsFirstLevelForPlayer = dto.IsFirstLevelForPlayer,
                forceWaitFetch = dto.ForceWaitFetch
            };
        }

        internal static void ApplyAtTargetS0(
            LeanS0Packet packet,
            long targetNativeFrame,
            NativeFullRunFrameClock clock,
            FullRunActionSetAdapter input,
            FullRunMouseBridge mouse,
            MovieV2Document targetMovie,
            Action<byte[], long> importRandom,
            Action<byte[], long> importObserver,
            out CanonicalInputEpoch restoredOrigin)
        {
            if (packet == null) throw new ArgumentNullException(nameof(packet));
            LeanS0PacketCodec.Validate(packet);

            PlayerData pd = PlayerData.instance ?? throw new InvalidOperationException("Target PlayerData.instance is null.");
            SceneData sd = SceneData.instance ?? throw new InvalidOperationException("Target SceneData.instance is null.");
            GameManager gm = GameManager.instance ?? throw new InvalidOperationException("Target GameManager.instance is null.");
            HeroController hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Target HeroController.SilentInstance is null.");

            // C12 clock and FsmTime carry must run first in the exact HTC1/FsmTime sequence.
            ApplyControlPlaneClock(packet.C12_ControlPlane, targetNativeFrame, clock, importRandom, importObserver, out restoredOrigin);

            // C1 + C2
            JsonUtility.FromJsonOverwrite(packet.C1_PlayerDataJson, pd);
            if (pd.currentBossSequence != null && string.IsNullOrEmpty(pd.currentBossSequence.bossSequenceName))
            {
                pd.currentBossSequence = null;
            }
            JsonUtility.FromJsonOverwrite(packet.C2_SceneDataJson, sd);
            if (packet.GameManagerProfileId >= 1 && packet.GameManagerProfileId <= 4)
            {
                gm.profileID = packet.GameManagerProfileId;
            }

            // C3 BossSequence, BossChallengeUI & BossStatueLoadManager
            ApplyBossSequence(packet.C3_BossSequence, pd);

            // C4 StaticVariableList
            ApplyStaticVariables(packet.C4_StaticVariables);

            // C5 PlayMakerGlobals trimmed allow-list
            ApplyPlayMakerGlobals(packet.C5_PlayMakerGlobals);

            // C7 HeroController trimmed carry + CharmUpdate derived fields without resetting health
            ApplyHeroController(hero, pd, packet.C7_HeroController);

            // C8 Knight FSMs (Spore Cooldown / Dream Return)
            ApplyKnightFsms(hero, packet.C8_KnightFsms);

            // C9 Charm minions (Hatchlings count + Grimmchild Option B)
            ApplyCharmMinions(hero, packet.C9_CharmMinions);

            // Synchronize HUD displays & charm-derived state deterministically at S0
            LeanS0HudParity.SynchronizeAtTargetS0(hero, pd, packet.C5_PlayMakerGlobals);

            // C10 Input & Mouse previous-frame edge state
            ApplyInputContainer(packet.C10_Input, input, mouse, targetMovie);

            // C11 Time scale
            ApplyTimeState(gm, packet.C11_Time);

            // D3 Audio cues (best-effort informational sync, excluded from K3 verification)
            ApplyAudioCues(gm, packet.D3_MusicCue);

            // C13 Persistent GameCameras / CameraController / CameraTarget parity
            ApplyCameraState(packet.C13_Camera);

            // C14 Mod System.Random ordinals & live instance state parity
            ModStartupCompatibility.RestoreModRandomState(packet.C14_ModRandom ?? new LeanS0ModRandomState());
        }

        private static LeanS0BossSequenceState CaptureBossSequence(PlayerData pd)
        {
            var data = BossCurrentDataField.GetValue(null) as BossSequenceController.BossSequenceData;
            var seq = BossCurrentSequenceField.GetValue(null) as BossSequence;
            int index = (int)BossIndexField.GetValue(null)!;
            bool wasCompleted = (bool)BossWasCompletedField.GetValue(null)!;
            int lastSelected = (int)BossLastSelectedField.GetValue(null)!;
            var statueCurrent = BossStatueCurrentSceneField.GetValue(null) as BossScene;
            var statuePrevious = BossStatuePreviousSceneField.GetValue(null) as BossScene;

            var dto = new LeanS0BossSequenceState
            {
                IsInSequence = BossSequenceController.IsInSequence,
                WasCompleted = wasCompleted,
                BossIndex = index,
                LastSelectedButton = lastSelected,
                SequenceName = seq != null ? seq.name : string.Empty,
                HasCurrentData = data != null,
                PlayerDataAliasesCurrentData = data != null && pd.currentBossSequence != null && ReferenceEquals(pd.currentBossSequence, data),
                PlayerDataHasPreviousEquippedCharms = pd.currentBossSequence != null
                    ? pd.currentBossSequence.previousEquippedCharms != null
                    : (bool?)null,
                StatueLoadCurrentBossSceneName = statueCurrent != null ? statueCurrent.name : null,
                StatueLoadPreviousBossSceneName = statuePrevious != null ? statuePrevious.name : null
            };

            if (data != null)
            {
                dto.BindingsMask = (int)data.bindings;
                dto.Timer = data.timer;
                dto.KnightDamaged = data.knightDamaged;
                dto.PlayerDataKey = data.playerData ?? string.Empty;
                dto.HasPreviousEquippedCharms = data.previousEquippedCharms != null;
                dto.PreviousEquippedCharms = data.previousEquippedCharms != null
                    ? (int[])data.previousEquippedCharms.Clone()
                    : Array.Empty<int>();
                dto.WasOvercharmed = data.wasOvercharmed;
                if (string.IsNullOrEmpty(dto.SequenceName) && !string.IsNullOrEmpty(data.bossSequenceName))
                {
                    dto.SequenceName = data.bossSequenceName;
                }
                dto.PreviousCompletion = new LeanS0BossDoorCompletionDto
                {
                    CanUnlock = data.previousCompletion.canUnlock,
                    Unlocked = data.previousCompletion.unlocked,
                    Completed = data.previousCompletion.completed,
                    AllBindings = data.previousCompletion.allBindings,
                    NoHits = data.previousCompletion.noHits,
                    BoundNail = data.previousCompletion.boundNail,
                    BoundShell = data.previousCompletion.boundShell,
                    BoundCharms = data.previousCompletion.boundCharms,
                    BoundSoul = data.previousCompletion.boundSoul,
                    ViewedBossSceneCompletions = data.previousCompletion.viewedBossSceneCompletions != null
                        ? new List<string>(data.previousCompletion.viewedBossSceneCompletions)
                        : new List<string>()
                };
            }

            Delegate? setup = BossSceneController.SetupEvent;
            if (setup != null)
            {
                TryCaptureBossSetupDelegate(setup, dto, depth: 0);
            }

            return dto;
        }

        private static bool TryCaptureBossSetupDelegate(Delegate setup, LeanS0BossSequenceState dto, int depth)
        {
            if (depth > 3)
            {
                return false;
            }

            foreach (Delegate item in setup.GetInvocationList())
            {
                if (item == null)
                {
                    continue;
                }

                Type? decl = item.Method?.DeclaringType;
                if (item.Target is CanonicalBossChallengeContext ctx && ctx.Statue != null)
                {
                    dto.HasPendingSetup = true;
                    dto.PendingLevel = ctx.Level;
                    dto.PendingStatueSceneName = ctx.Statue.BossScene != null ? ctx.Statue.BossScene.sceneName ?? string.Empty : string.Empty;
                    dto.PendingStatueStatePd = ctx.Statue.StatueStatePD ?? string.Empty;
                    dto.PendingDreamStatueStatePd = ctx.Statue.DreamStatueStatePD ?? string.Empty;
                    dto.PendingStatueIsAlwaysUnlocked = ctx.Statue.IsAlwaysUnlocked;
                    dto.HasPendingCompletionCache = ctx.CachedCompletion != null;
                    return true;
                }

                if (item.Target != null
                    && item.Target.GetType() == CanonicalBossChallengeOriginal.Primary
                    && BossChallengePrimaryLevelField != null
                    && BossChallengePrimaryUiField != null
                    && BossChallengeUiStatueField != null)
                {
                    object closure = item.Target;
                    object? ui = BossChallengePrimaryUiField.GetValue(closure);
                    if (!ReferenceEquals(ui, null))
                    {
                        var statue = BossChallengeUiStatueField.GetValue(ui) as BossStatue;
                        if (!ReferenceEquals(statue, null))
                        {
                            BossScene? bossScene = statue.bossScene;
                            object? cached = BossChallengePrimaryCachedField?.GetValue(closure);
                            dto.HasPendingSetup = true;
                            dto.PendingLevel = (int)BossChallengePrimaryLevelField.GetValue(closure)!;
                            dto.PendingStatueSceneName = !ReferenceEquals(bossScene, null) ? bossScene.sceneName ?? string.Empty : string.Empty;
                            dto.PendingStatueStatePd = statue.statueStatePD ?? string.Empty;
                            dto.PendingDreamStatueStatePd = statue.dreamStatueStatePD ?? string.Empty;
                            dto.PendingStatueIsAlwaysUnlocked = statue.isAlwaysUnlocked;
                            dto.HasPendingCompletionCache = !ReferenceEquals(cached, null);
                            return true;
                        }
                    }
                }

                if (decl == typeof(BossSequenceController) || decl?.DeclaringType == typeof(BossSequenceController))
                {
                    dto.HasPendingSequenceSetup = true;
                    return true;
                }

                if (item.Target != null && depth < 3)
                {
                    foreach (FieldInfo field in item.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (typeof(Delegate).IsAssignableFrom(field.FieldType)
                            && field.GetValue(item.Target) is Delegate inner
                            && !ReferenceEquals(inner, item)
                            && TryCaptureBossSetupDelegate(inner, dto, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static void ApplyBossSequence(LeanS0BossSequenceState state, PlayerData pd)
        {
            BossLastSelectedField.SetValue(null, state.LastSelectedButton);
            BossIndexField.SetValue(null, state.BossIndex);
            BossWasCompletedField.SetValue(null, state.WasCompleted);
            BossSceneController.SetupEvent = null;
            ApplyBossStatueLoadManager(state);

            if (!state.HasCurrentData)
            {
                BossCurrentDataField.SetValue(null, null);
                BossCurrentSequenceField.SetValue(null, null);
                if (pd.currentBossSequence != null && state.PlayerDataHasPreviousEquippedCharms == false)
                {
                    pd.currentBossSequence.previousEquippedCharms = null;
                }
                RestorePendingBossSetup(state);
                return;
            }

            bool hasPreviousCharms = state.HasPreviousEquippedCharms ?? (
                ((BossSequenceController.ChallengeBindings)state.BindingsMask & BossSequenceController.ChallengeBindings.Charms) != 0
                || (state.PreviousEquippedCharms != null && state.PreviousEquippedCharms.Length > 0));
            int[]? previousEquippedCharms = hasPreviousCharms
                ? (state.PreviousEquippedCharms != null ? (int[])state.PreviousEquippedCharms.Clone() : Array.Empty<int>())
                : null;

            var data = new BossSequenceController.BossSequenceData
            {
                bindings = (BossSequenceController.ChallengeBindings)state.BindingsMask,
                timer = state.Timer,
                knightDamaged = state.KnightDamaged,
                playerData = state.PlayerDataKey,
                previousEquippedCharms = previousEquippedCharms,
                wasOvercharmed = state.WasOvercharmed,
                bossSequenceName = state.SequenceName,
                previousCompletion = new BossSequenceDoor.Completion
                {
                    canUnlock = state.PreviousCompletion.CanUnlock,
                    unlocked = state.PreviousCompletion.Unlocked,
                    completed = state.PreviousCompletion.Completed,
                    allBindings = state.PreviousCompletion.AllBindings,
                    noHits = state.PreviousCompletion.NoHits,
                    boundNail = state.PreviousCompletion.BoundNail,
                    boundShell = state.PreviousCompletion.BoundShell,
                    boundCharms = state.PreviousCompletion.BoundCharms,
                    boundSoul = state.PreviousCompletion.BoundSoul,
                    viewedBossSceneCompletions = state.PreviousCompletion.ViewedBossSceneCompletions != null
                        ? new List<string>(state.PreviousCompletion.ViewedBossSceneCompletions)
                        : new List<string>()
                }
            };

            BossSequence? seq = null;
            if (!string.IsNullOrEmpty(state.SequenceName))
            {
                seq = Resources.Load<BossSequence>("GG/" + state.SequenceName)
                    ?? Resources.FindObjectsOfTypeAll<BossSequence>().FirstOrDefault(s => s != null && s.name == state.SequenceName);
            }
            if (seq == null && state.IsInSequence)
            {
                throw new InvalidDataException($"Unable to resolve BossSequence asset '{state.SequenceName}'.");
            }

            BossCurrentDataField.SetValue(null, data);
            BossCurrentSequenceField.SetValue(null, seq);

            // Re-establish reference aliasing between PlayerData.currentBossSequence and
            // BossSequenceController.currentData so BossSceneController.Update() keeps both timers in sync.
            if (state.PlayerDataAliasesCurrentData == true
                || (!state.PlayerDataAliasesCurrentData.HasValue && pd.currentBossSequence != null))
            {
                pd.currentBossSequence = data;
            }
            else if (pd.currentBossSequence != null && state.PlayerDataHasPreviousEquippedCharms == false)
            {
                pd.currentBossSequence.previousEquippedCharms = null;
            }

            RestorePendingBossSetup(state);
        }

        private static void ApplyBossStatueLoadManager(LeanS0BossSequenceState state)
        {
            BossScene? current = ResolveBossSceneByName(state.StatueLoadCurrentBossSceneName);
            BossScene? previous = ResolveBossSceneByName(state.StatueLoadPreviousBossSceneName);
            BossStatueCurrentSceneField.SetValue(null, current);
            BossStatuePreviousSceneField.SetValue(null, previous);
        }

        private static BossScene? ResolveBossSceneByName(string? assetName)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                return null;
            }

            BossScene? found = Resources.FindObjectsOfTypeAll<BossScene>()
                .FirstOrDefault(s => s != null && string.Equals(s.name, assetName, StringComparison.Ordinal));
            if (found != null)
            {
                return found;
            }

            found = Resources.Load<BossScene>(assetName) ?? Resources.Load<BossScene>("GG/" + assetName);
            if (found != null)
            {
                return found;
            }

            Resources.LoadAll<BossSequence>("GG");
            return Resources.FindObjectsOfTypeAll<BossScene>()
                .FirstOrDefault(s => s != null && string.Equals(s.name, assetName, StringComparison.Ordinal));
        }

        private static void RestorePendingBossSetup(LeanS0BossSequenceState state)
        {
            if (state.HasPendingSetup)
            {
                BossScene? bossScene = null;
                if (!string.IsNullOrEmpty(state.PendingStatueSceneName))
                {
                    bossScene = Resources.FindObjectsOfTypeAll<BossScene>()
                        .FirstOrDefault(s => s != null && (string.Equals(s.sceneName, state.PendingStatueSceneName, StringComparison.Ordinal)
                            || string.Equals(s.name, state.PendingStatueSceneName, StringComparison.Ordinal)));
                }

                var context = new CanonicalBossChallengeContext
                {
                    Level = state.PendingLevel,
                    Statue = new CanonicalBossStatueContext
                    {
                        StatueStatePD = state.PendingStatueStatePd,
                        DreamStatueStatePD = state.PendingDreamStatueStatePd,
                        IsAlwaysUnlocked = state.PendingStatueIsAlwaysUnlocked,
                        BossScene = bossScene
                    }
                };
                if (state.HasPendingCompletionCache)
                {
                    context.CachedCompletion = context.Complete;
                }

                BossSceneController.SetupEvent = context.Setup;
            }
            else if (state.HasPendingSequenceSetup)
            {
                BossSetupBossSceneMethod.Invoke(null, null);
            }
        }

        private static Dictionary<string, LeanS0TypedValue> CaptureStaticVariables()
        {
            var result = new Dictionary<string, LeanS0TypedValue>(StringComparer.Ordinal);
            if (!(StaticVariablesField.GetValue(null) is Dictionary<string, object?> vars))
            {
                return result;
            }

            foreach (KeyValuePair<string, object?> kvp in vars.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                result[kvp.Key] = EncodeTypedValue(kvp.Key, kvp.Value);
            }
            return result;
        }

        private static void ApplyStaticVariables(Dictionary<string, LeanS0TypedValue> saved)
        {
            var dict = StaticVariablesField.GetValue(null) as Dictionary<string, object?>;
            if (dict == null)
            {
                dict = new Dictionary<string, object?>();
                StaticVariablesField.SetValue(null, dict);
            }

            dict.Clear();
            foreach (KeyValuePair<string, LeanS0TypedValue> kvp in saved)
            {
                dict[kvp.Key] = DecodeTypedValue(kvp.Key, kvp.Value);
            }
        }

        private static LeanS0TypedValue EncodeTypedValue(string key, object? value)
        {
            return value switch
            {
                null => new LeanS0TypedValue { Kind = "null", Value = null },
                bool b => new LeanS0TypedValue { Kind = "bool", Value = b ? "true" : "false" },
                int i => new LeanS0TypedValue { Kind = "int", Value = i.ToString(CultureInfo.InvariantCulture) },
                long l => new LeanS0TypedValue { Kind = "long", Value = l.ToString(CultureInfo.InvariantCulture) },
                float f => new LeanS0TypedValue { Kind = "float", Value = f.ToString("R", CultureInfo.InvariantCulture) },
                double d => new LeanS0TypedValue { Kind = "double", Value = d.ToString("R", CultureInfo.InvariantCulture) },
                string s => new LeanS0TypedValue { Kind = "string", Value = s },
                _ => throw new InvalidDataException($"Unsupported StaticVariableList value type for '{key}': {value.GetType().FullName}")
            };
        }

        private static object? DecodeTypedValue(string key, LeanS0TypedValue tv)
        {
            return tv.Kind switch
            {
                "null" => null,
                "bool" => bool.Parse(tv.Value ?? throw new InvalidDataException("Missing bool StaticVariable: " + key)),
                "int" => int.Parse(tv.Value ?? throw new InvalidDataException("Missing int StaticVariable: " + key), CultureInfo.InvariantCulture),
                "long" => long.Parse(tv.Value ?? throw new InvalidDataException("Missing long StaticVariable: " + key), CultureInfo.InvariantCulture),
                "float" => float.Parse(tv.Value ?? throw new InvalidDataException("Missing float StaticVariable: " + key), CultureInfo.InvariantCulture),
                "double" => double.Parse(tv.Value ?? throw new InvalidDataException("Missing double StaticVariable: " + key), CultureInfo.InvariantCulture),
                "string" => tv.Value ?? string.Empty,
                _ => throw new InvalidDataException($"Unsupported LeanS0TypedValue kind '{tv.Kind}' for '{key}'.")
            };
        }

        private static LeanS0PlayMakerGlobalsState CapturePlayMakerGlobals()
        {
            FsmBool? hudOut = PlayMakerGlobals.Instance?.Variables?.FindFsmBool("Is HUD Out");
            PlayMakerFSM? slideOut = FindHudFsm("Hud Canvas", "Slide Out");
            PlayMakerFSM? lowHealthFx = FindHudFsm("Hud Canvas/Health", "Low Health FX");
            FsmFloat? waitTime = lowHealthFx?.FsmVariables?.FindFsmFloat("Wait Time");
            var healthDisplayStates = new string[11];
            for (int i = 0; i < 11; i++)
            {
                PlayMakerFSM? hd = FindHudFsm($"Hud Canvas/Health/Health {i + 1}", "health_display");
                healthDisplayStates[i] = hd?.ActiveStateName ?? string.Empty;
            }
            return new LeanS0PlayMakerGlobalsState
            {
                IsHudOut = hudOut != null && hudOut.Value,
                LowHealthFxWaitTime = waitTime != null ? waitTime.Value : 0f,
                LowHealthFxActiveState = lowHealthFx?.ActiveStateName ?? string.Empty,
                SlideOutActiveState = slideOut?.ActiveStateName ?? string.Empty,
                HealthDisplayActiveStates = healthDisplayStates,
                HudCanvasLocalPosition = CaptureHudLocalPos("Hud Canvas"),
                HealthRootLocalPosition = CaptureHudLocalPos("Hud Canvas/Health"),
                SoulOrbLocalPosition = CaptureHudLocalPos("Hud Canvas/Soul Orb")
            };
        }

        private static float[] CaptureHudLocalPos(string relativePath)
        {
            Transform? t = GameCameras.instance?.hudCamera?.transform?.Find(relativePath);
            if (t == null) return Array.Empty<float>();
            Vector3 p = t.localPosition;
            return new[] { p.x, p.y, p.z };
        }

        private static void ApplyHudLocalPos(string relativePath, float[]? pos)
        {
            if (pos == null || pos.Length != 3) return;
            Transform? t = GameCameras.instance?.hudCamera?.transform?.Find(relativePath);
            if (t != null) t.localPosition = new Vector3(pos[0], pos[1], pos[2]);
        }

        private static void ApplyPlayMakerGlobals(LeanS0PlayMakerGlobalsState state)
        {
            FsmBool? hudOut = PlayMakerGlobals.Instance?.Variables?.FindFsmBool("Is HUD Out");
            if (hudOut != null)
            {
                hudOut.Value = state.IsHudOut;
            }

            // Clear any pre-existing Low Health iTweens before restoring HUD resting positions.
            Transform? healthTransform = GameCameras.instance?.hudCamera?.transform?.Find("Hud Canvas/Health");
            if (healthTransform != null)
            {
                LeanS0Gate.StopAndRemoveITweens(healthTransform.gameObject, restorePositionAnchor: true);
            }

            ApplyHudLocalPos("Hud Canvas", state.HudCanvasLocalPosition);
            ApplyHudLocalPos("Hud Canvas/Health", state.HealthRootLocalPosition);
            ApplyHudLocalPos("Hud Canvas/Soul Orb", state.SoulOrbLocalPosition);

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                PlayMakerFSM? slideOut = FindHudFsm("Hud Canvas", "Slide Out");
                if (slideOut != null
                    && !string.IsNullOrEmpty(state.SlideOutActiveState)
                    && slideOut.ActiveStateName != state.SlideOutActiveState)
                {
                    slideOut.SetState(state.SlideOutActiveState);
                }

                PlayMakerFSM? lowHealthFx = FindHudFsm("Hud Canvas/Health", "Low Health FX");
                if (lowHealthFx != null)
                {
                    FsmFloat? waitTime = lowHealthFx.FsmVariables?.FindFsmFloat("Wait Time");
                    if (waitTime != null)
                    {
                        waitTime.Value = state.LowHealthFxWaitTime;
                    }

                    if (!string.IsNullOrEmpty(state.LowHealthFxActiveState)
                        && lowHealthFx.ActiveStateName != state.LowHealthFxActiveState)
                    {
                        lowHealthFx.SetState(state.LowHealthFxActiveState);
                    }
                }

                if (state.HealthDisplayActiveStates != null)
                {
                    for (int i = 0; i < state.HealthDisplayActiveStates.Length && i < 11; i++)
                    {
                        string targetState = state.HealthDisplayActiveStates[i];
                        if (string.IsNullOrEmpty(targetState))
                        {
                            continue;
                        }

                        PlayMakerFSM? hd = FindHudFsm($"Hud Canvas/Health/Health {i + 1}", "health_display");
                        if (hd != null && hd.Fsm != null && hd.Fsm.Initialized
                            && hd.ActiveStateName != targetState
                            && hd.Fsm.GetState(targetState) != null)
                        {
                            hd.SetState(targetState);
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        private static Transform[] CollectCameraTransformChain(GameCameras cameras, CameraController controller, CameraTarget target)
        {
            var chain = new List<Transform>();
            foreach (Transform leaf in new[] { controller.transform, target.transform })
            {
                var branch = new List<Transform>();
                for (Transform? cur = leaf; cur != null; cur = cur.parent)
                {
                    branch.Add(cur);
                    if (cur == cameras.transform || branch.Count >= 8) break;
                }
                branch.Reverse();
                foreach (Transform t in branch)
                {
                    if (!chain.Contains(t)) chain.Add(t);
                }
            }
            return chain.ToArray();
        }

        private static LeanS0CameraState? CaptureCameraState()
        {
            GameCameras? cameras = GameCameras.instance;
            CameraController? controller = cameras?.cameraController;
            CameraTarget? target = cameras?.cameraTarget;
            if (cameras == null || controller == null || target == null) return null;

            Transform[] chain = CollectCameraTransformChain(cameras, controller, target);
            return new LeanS0CameraState
            {
                Controller = CheckpointScalarFields.Capture(controller, CameraControllerScalarFields),
                Target = CheckpointScalarFields.Capture(target, CameraTargetScalarFields),
                Transforms = chain.Select(t => new LeanS0CameraTransformPose
                {
                    Name = t.name,
                    LocalPosition = new[] { t.localPosition.x, t.localPosition.y, t.localPosition.z },
                    LocalRotation = new[] { t.localRotation.x, t.localRotation.y, t.localRotation.z, t.localRotation.w },
                    LocalScale = new[] { t.localScale.x, t.localScale.y, t.localScale.z }
                }).ToArray()
            };
        }

        private static void ApplyCameraState(LeanS0CameraState? state)
        {
            if (state == null) return;
            GameCameras? cameras = GameCameras.instance;
            CameraController? controller = cameras?.cameraController;
            CameraTarget? target = cameras?.cameraTarget;
            if (cameras == null || controller == null || target == null) return;

            controller.lockZoneList?.Clear();
            CameraLockAreaField.SetValue(controller, null);

            if (state.Controller != null && state.Controller.Count > 0)
            {
                CheckpointScalarFields.PrepareApply(controller, CameraControllerScalarFields, state.Controller)();
            }
            if (state.Target != null && state.Target.Count > 0)
            {
                CheckpointScalarFields.PrepareApply(target, CameraTargetScalarFields, state.Target)();
            }

            if (state.Transforms != null)
            {
                Transform[] chain = CollectCameraTransformChain(cameras, controller, target);
                for (int i = 0; i < state.Transforms.Length && i < chain.Length; i++)
                {
                    LeanS0CameraTransformPose pose = state.Transforms[i];
                    if (pose == null || chain[i].name != pose.Name) continue;
                    if (pose.LocalPosition.Length == 3)
                        chain[i].localPosition = new Vector3(pose.LocalPosition[0], pose.LocalPosition[1], pose.LocalPosition[2]);
                    if (pose.LocalRotation.Length == 4)
                        chain[i].localRotation = new Quaternion(pose.LocalRotation[0], pose.LocalRotation[1], pose.LocalRotation[2], pose.LocalRotation[3]);
                    if (pose.LocalScale.Length == 3)
                        chain[i].localScale = new Vector3(pose.LocalScale[0], pose.LocalScale[1], pose.LocalScale[2]);
                }
            }
        }

        private static PlayMakerFSM? FindHudFsm(string relativePath, string fsmName)
        {
            Transform? hudRoot = GameCameras.instance?.hudCamera?.transform;
            Transform? target = hudRoot?.Find(relativePath);
            if (target == null)
            {
                return null;
            }

            return target.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(f => f != null && string.Equals(f.FsmName, fsmName, StringComparison.Ordinal));
        }

        private static LeanS0TransitionInfo CaptureTransitionInfo(
            GameManager.SceneLoadInfo? loadInfo,
            string fallbackTargetScene,
            string fallbackEntryGate)
        {
            if (loadInfo == null)
            {
                return new LeanS0TransitionInfo
                {
                    SceneName = fallbackTargetScene,
                    EntryGateName = fallbackEntryGate
                };
            }

            return new LeanS0TransitionInfo
            {
                SceneName = string.IsNullOrEmpty(loadInfo.SceneName) ? fallbackTargetScene : loadInfo.SceneName,
                EntryGateName = loadInfo.EntryGateName ?? fallbackEntryGate,
                HeroLeaveDirection = loadInfo.HeroLeaveDirection.HasValue ? (int?)loadInfo.HeroLeaveDirection.Value : null,
                Visualization = (int)loadInfo.Visualization,
                EntryDelay = loadInfo.EntryDelay,
                AlwaysUnloadUnusedAssets = loadInfo.AlwaysUnloadUnusedAssets,
                PreventCameraFadeOut = loadInfo.PreventCameraFadeOut,
                WaitForSceneTransitionCameraFade = loadInfo.WaitForSceneTransitionCameraFade,
                IsFirstLevelForPlayer = loadInfo.IsFirstLevelForPlayer,
                ForceWaitFetch = loadInfo.forceWaitFetch
            };
        }

        private static LeanS0HeroControllerState CaptureHeroController(HeroController hero)
        {
            Vector3 pos = hero.transform.position;
            Rigidbody2D? rb = hero.GetComponent<Rigidbody2D>();
            return new LeanS0HeroControllerState
            {
                FacingRight = hero.cState != null && hero.cState.facingRight,
                ExitedSuperDashing = (bool)HcExitedSuperDashing.GetValue(hero)!,
                ExitedQuake = (bool)HcExitedQuake.GetValue(hero)!,
                EnterWithoutInput = (bool)HcEnterWithoutInput.GetValue(hero)!,
                AttackCooldown = (float)HcAttackCooldown.GetValue(hero)!,
                DashCooldownTimer = (float)HcDashCooldownTimer.GetValue(hero)!,
                ShadowDashTimer = (float)HcShadowDashTimer.GetValue(hero)!,
                ParryInvulnTimer = hero.parryInvulnTimer,
                PreventCastByDialogueEndTimer = (float)HcPreventCastByDialogueEndTimer.GetValue(hero)!,
                HitsSinceShielded = (int)HcHitsSinceShielded.GetValue(hero)!,
                WieldingLantern = (bool)HcWieldingLantern.GetValue(hero)!,
                TakeNoDamage = (bool)HcTakeNoDamage.GetValue(hero)!,
                BlockerFix = (bool)HcBlockerFix.GetValue(hero)!,
                JumpedSteps = (int)HcJumpedSteps.GetValue(hero)!,
                Position = new[] { pos.x, pos.y, pos.z },
                RbPosition = rb != null ? new[] { rb.position.x, rb.position.y } : null,
                RbVelocity = rb != null ? new[] { rb.velocity.x, rb.velocity.y } : null
            };
        }

        private static void ApplyHeroController(HeroController hero, PlayerData pd, LeanS0HeroControllerState state)
        {
            if (hero.cState != null)
            {
                if (state.FacingRight)
                {
                    hero.FaceRight();
                }
                else
                {
                    hero.FaceLeft();
                }
            }
            hero.transform.position = (state.Position != null && state.Position.Length == 3)
                ? new Vector3(state.Position[0], state.Position[1], state.Position[2])
                : new Vector3(-20000f, 20000f, 0f);
            Rigidbody2D? rb = hero.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.position = (state.RbPosition != null && state.RbPosition.Length == 2)
                    ? new Vector2(state.RbPosition[0], state.RbPosition[1])
                    : new Vector2(hero.transform.position.x, hero.transform.position.y);
                rb.velocity = (state.RbVelocity != null && state.RbVelocity.Length == 2)
                    ? new Vector2(state.RbVelocity[0], state.RbVelocity[1])
                    : Vector2.zero;
            }
            Physics2D.SyncTransforms();

            HcExitedSuperDashing.SetValue(hero, state.ExitedSuperDashing);
            HcExitedQuake.SetValue(hero, state.ExitedQuake);
            HcEnterWithoutInput.SetValue(hero, state.EnterWithoutInput);
            HcAttackCooldown.SetValue(hero, state.AttackCooldown);
            HcDashCooldownTimer.SetValue(hero, state.DashCooldownTimer);
            HcShadowDashTimer.SetValue(hero, state.ShadowDashTimer);
            hero.parryInvulnTimer = state.ParryInvulnTimer;
            HcPreventCastByDialogueEndTimer.SetValue(hero, Math.Max(0f, state.PreventCastByDialogueEndTimer));
            HcHitsSinceShielded.SetValue(hero, state.HitsSinceShielded);
            HcWieldingLantern.SetValue(hero, state.WieldingLantern);
            HcTakeNoDamage.SetValue(hero, state.TakeNoDamage);
            HcBlockerFix.SetValue(hero, state.BlockerFix);
            HcJumpedSteps.SetValue(hero, state.JumpedSteps);

            // Recompute derived HeroController charm fields without calling MaxHealth() (which resets health).
            float defaultCharge = (float)HcNailChargeTimeDefault.GetValue(hero)!;
            float charmCharge = (float)HcNailChargeTimeCharm.GetValue(hero)!;
            HcNailChargeTime.SetValue(hero, pd.GetBool("equippedCharm_26") ? charmCharge : defaultCharge);
            HcJoniBeam.SetValue(hero, pd.GetBool("equippedCharm_27"));
            HcCarefreeShieldEquipped.SetValue(hero, pd.GetBool("equippedCharm_40") && pd.GetInt("grimmChildLevel") == 5);
        }

        private static LeanS0KnightFsmsState CaptureKnightFsms(HeroController hero)
        {
            PlayMakerFSM[] fsms = hero.GetComponents<PlayMakerFSM>();
            PlayMakerFSM? spore = fsms.FirstOrDefault(f => f != null && f.FsmName == "Spore Cooldown");
            PlayMakerFSM? spellControl = fsms.FirstOrDefault(f => f != null && f.FsmName == "Spell Control");
            PlayMakerFSM? dreamReturn = fsms.FirstOrDefault(f => f != null && f.FsmName == "Dream Return");
            PlayMakerFSM? interp = fsms.FirstOrDefault(f => f != null && f.FsmName == "Control Interpolation");

            var state = new LeanS0KnightFsmsState
            {
                SporeCooldownActiveState = spore?.ActiveStateName ?? string.Empty,
                SporeCooldownPreviousState = spore?.Fsm?.PreviousActiveState?.Name ?? string.Empty,
                DreamReturnActiveState = dreamReturn?.ActiveStateName ?? string.Empty,
                DreamReturnPreviousState = dreamReturn?.Fsm?.PreviousActiveState?.Name ?? string.Empty,
                ControlInterpolationActiveState = interp?.ActiveStateName ?? string.Empty
            };

            Wait? sporeCooldownWait = spore?.Fsm?.GetState("Cooldown")?.Actions?.OfType<Wait>().FirstOrDefault();
            if (sporeCooldownWait != null && WaitTimerField != null)
            {
                state.SporeCooldownWaitTimer = (float)WaitTimerField.GetValue(sporeCooldownWait)!;
            }

            FsmBool? spellControlSporeBool = spellControl?.FsmVariables?.FindFsmBool("Spore Cooldown");
            if (spellControlSporeBool != null)
            {
                state.SpellControlSporeCooldown = spellControlSporeBool.Value;
            }

            if (dreamReturn?.FsmVariables != null)
            {
                foreach (FsmBool v in dreamReturn.FsmVariables.BoolVariables)
                    state.DreamReturnBools[v.Name] = v.Value;
                foreach (FsmFloat v in dreamReturn.FsmVariables.FloatVariables)
                    state.DreamReturnFloats[v.Name] = v.Value;
                foreach (FsmInt v in dreamReturn.FsmVariables.IntVariables)
                    state.DreamReturnInts[v.Name] = v.Value;
                foreach (FsmVector2 v in dreamReturn.FsmVariables.Vector2Variables)
                    state.DreamReturnVector2s[v.Name] = FromVec2(v.Value);
                foreach (FsmVector3 v in dreamReturn.FsmVariables.Vector3Variables)
                    state.DreamReturnVector3s[v.Name] = FromVec3(v.Value);
            }

            return state;
        }

        private static void ApplyKnightFsms(HeroController hero, LeanS0KnightFsmsState state)
        {
            PlayMakerFSM[] fsms = hero.GetComponents<PlayMakerFSM>();
            PlayMakerFSM? spore = fsms.FirstOrDefault(f => f != null && f.FsmName == "Spore Cooldown");
            if (spore != null)
            {
                if (!string.IsNullOrEmpty(state.SporeCooldownActiveState)
                    && spore.ActiveStateName != state.SporeCooldownActiveState)
                {
                    if (!LeanS0Gate.ResetFsmToState(spore, state.SporeCooldownActiveState))
                    {
                        spore.SetState(state.SporeCooldownActiveState);
                    }
                }

                if (state.SporeCooldownWaitTimer.HasValue && WaitTimerField != null)
                {
                    Wait? sporeCooldownWait = spore.Fsm?.GetState("Cooldown")?.Actions?.OfType<Wait>().FirstOrDefault();
                    if (sporeCooldownWait != null)
                    {
                        WaitTimerField.SetValue(sporeCooldownWait, state.SporeCooldownWaitTimer.Value);
                    }
                }
            }

            if (state.SpellControlSporeCooldown.HasValue)
            {
                PlayMakerFSM? spellControl = fsms.FirstOrDefault(f => f != null && f.FsmName == "Spell Control");
                FsmBool? spellControlSporeBool = spellControl?.FsmVariables?.FindFsmBool("Spore Cooldown");
                if (spellControlSporeBool != null)
                {
                    spellControlSporeBool.Value = state.SpellControlSporeCooldown.Value;
                }
            }

            PlayMakerFSM? dreamReturn = fsms.FirstOrDefault(f => f != null && f.FsmName == "Dream Return");
            if (dreamReturn != null)
            {
                if (dreamReturn.FsmVariables != null)
                {
                    foreach (FsmBool v in dreamReturn.FsmVariables.BoolVariables)
                    {
                        if (state.DreamReturnBools.TryGetValue(v.Name, out bool val))
                            v.Value = val;
                    }
                    foreach (FsmFloat v in dreamReturn.FsmVariables.FloatVariables)
                    {
                        if (state.DreamReturnFloats.TryGetValue(v.Name, out float val))
                            v.Value = val;
                    }
                    foreach (FsmInt v in dreamReturn.FsmVariables.IntVariables)
                    {
                        if (state.DreamReturnInts.TryGetValue(v.Name, out int val))
                            v.Value = val;
                    }
                    foreach (FsmVector2 v in dreamReturn.FsmVariables.Vector2Variables)
                    {
                        if (state.DreamReturnVector2s.TryGetValue(v.Name, out float[]? val))
                            v.Value = ToVec2(val);
                    }
                    foreach (FsmVector3 v in dreamReturn.FsmVariables.Vector3Variables)
                    {
                        if (state.DreamReturnVector3s.TryGetValue(v.Name, out float[]? val))
                            v.Value = ToVec3(val);
                    }
                }

                if (!string.IsNullOrEmpty(state.DreamReturnActiveState)
                    && dreamReturn.ActiveStateName != state.DreamReturnActiveState)
                {
                    dreamReturn.SetState(state.DreamReturnActiveState);
                }
            }
        }

        private static LeanS0CharmMinionsState CaptureCharmMinions(HeroController hero)
        {
            var result = new LeanS0CharmMinionsState();

            Transform? charmEffects = hero.transform.Find("Charm Effects");
            if (charmEffects != null)
            {
                PlayMakerFSM[] charmFsms = charmEffects.GetComponents<PlayMakerFSM>();
                PlayMakerFSM? hatchlingSpawn = charmFsms.FirstOrDefault(f => f != null && f.FsmName == "Hatchling Spawn");
                if (hatchlingSpawn != null)
                {
                    FsmInt? countVar = hatchlingSpawn.FsmVariables.FindFsmInt("Hatchlings");
                    FsmBool? dreamgateVar = hatchlingSpawn.FsmVariables.FindFsmBool("Dreamgate Entry");
                    result.HatchlingCount = countVar != null ? countVar.Value : 0;
                    result.HatchlingDreamgateEntry = dreamgateVar != null && dreamgateVar.Value;
                }

                PlayMakerFSM? whiteCharm = charmFsms.FirstOrDefault(f => f != null && f.FsmName == "White Charm");
                if (whiteCharm != null)
                {
                    result.WhiteCharmActiveState = whiteCharm.ActiveStateName ?? string.Empty;
                    Wait? waitAction = whiteCharm.Fsm?.ActiveState?.Actions?.OfType<Wait>().FirstOrDefault();
                    if (waitAction != null && WaitTimerField != null)
                    {
                        result.WhiteCharmWaitTimer = (float)WaitTimerField.GetValue(waitAction)!;
                    }
                }
            }

            GameObject? grimmchild = LeanS0Gate.FindGrimmchildInstance();
            if (grimmchild != null)
            {
                result.Grimmchild = CaptureGrimmchild(grimmchild);
            }

            return result;
        }

        private static void ApplyCharmMinions(HeroController hero, LeanS0CharmMinionsState state)
        {
            Transform? charmEffects = hero.transform.Find("Charm Effects");
            if (charmEffects != null)
            {
                PlayMakerFSM[] charmFsms = charmEffects.GetComponents<PlayMakerFSM>();
                PlayMakerFSM? hatchlingSpawn = charmFsms.FirstOrDefault(f => f != null && f.FsmName == "Hatchling Spawn");
                if (hatchlingSpawn != null)
                {
                    FsmInt? countVar = hatchlingSpawn.FsmVariables.FindFsmInt("Hatchlings");
                    if (countVar != null) countVar.Value = state.HatchlingCount;
                    FsmBool? dreamgateVar = hatchlingSpawn.FsmVariables.FindFsmBool("Dreamgate Entry");
                    if (dreamgateVar != null) dreamgateVar.Value = state.HatchlingDreamgateEntry;
                    LeanS0Gate.ResetFsmToState(hatchlingSpawn, "Count Remaining");
                }

                PlayMakerFSM? whiteCharm = charmFsms.FirstOrDefault(f => f != null && f.FsmName == "White Charm");
                if (whiteCharm != null && !string.IsNullOrEmpty(state.WhiteCharmActiveState))
                {
                    if (LeanS0Gate.ResetFsmToState(whiteCharm, state.WhiteCharmActiveState!)
                        && state.WhiteCharmWaitTimer.HasValue
                        && WaitTimerField != null)
                    {
                        Wait? waitAction = whiteCharm.Fsm?.ActiveState?.Actions?.OfType<Wait>().FirstOrDefault();
                        if (waitAction != null)
                        {
                            WaitTimerField.SetValue(waitAction, state.WhiteCharmWaitTimer.Value);
                        }
                    }
                }
            }

            GameObject? existing = LeanS0Gate.FindGrimmchildInstance();
            if (state.Grimmchild == null)
            {
                if (existing != null)
                {
                    ObjectPool.Recycle(existing);
                }
                return;
            }

            if (existing == null)
            {
                existing = SpawnGrimmchildFromCharmEffects(charmEffects, state.Grimmchild);
            }
            ApplyGrimmchildState(existing, state.Grimmchild);
        }

        private static GameObject SpawnGrimmchildFromCharmEffects(Transform? charmEffects, LeanS0GrimmchildState gc)
        {
            PlayMakerFSM? spawnFsm = charmEffects?.GetComponents<PlayMakerFSM>()
                .FirstOrDefault(f => f != null && f.FsmName == "Spawn Grimmchild");
            FsmState? spawnState = spawnFsm?.FsmStates?.FirstOrDefault(s => s != null && s.Name == "Spawn");
            SpawnObjectFromGlobalPool? spawner = spawnState?.Actions?.OfType<SpawnObjectFromGlobalPool>().FirstOrDefault();
            GameObject? prefab = spawner?.gameObject?.Value;
            if (prefab == null)
            {
                throw new InvalidDataException("Cannot spawn Grimmchild on target: Spawn Grimmchild prefab is unavailable.");
            }

            Vector3 pos = ToVec3(gc.LocalPosition);
            Quaternion rot = ToQuat(gc.LocalRotation);
            GameObject spawned = prefab.Spawn(pos, rot);
            UnityEngine.Object.DontDestroyOnLoad(spawned);

            FsmGameObject? childVar = spawnFsm!.FsmVariables.FindFsmGameObject("Child");
            if (childVar != null)
            {
                childVar.Value = spawned;
            }

            return spawned;
        }

        private static LeanS0GrimmchildState CaptureGrimmchild(GameObject go)
        {
            Transform tr = go.transform;
            Rigidbody2D? rb = go.GetComponent<Rigidbody2D>();
            ObjectBounce? bounce = go.GetComponent<ObjectBounce>();
            PlayMakerFSM? control = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f != null && f.FsmName == "Control");

            var dto = new LeanS0GrimmchildState
            {
                ActiveSelf = go.activeSelf,
                LocalPosition = FromVec3(tr.localPosition),
                LocalRotation = FromQuat(tr.localRotation),
                LocalScale = FromVec3(tr.localScale),
                RbPosition = rb != null ? FromVec2(rb.position) : new float[2],
                RbVelocity = rb != null ? FromVec2(rb.velocity) : new float[2]
            };

            if (bounce != null)
            {
                dto.BounceVelocity = FromVec2((Vector2)BounceVelocityField.GetValue(bounce)!);
                dto.BounceLastPos = FromVec2((Vector2)BounceLastPosField.GetValue(bounce)!);
                dto.BounceSpeed = (float)BounceSpeedField.GetValue(bounce)!;
                dto.BounceStepCounter = (int)BounceStepCounterField.GetValue(bounce)!;
                dto.Bouncing = (bool)BounceBouncingField.GetValue(bounce)!;
            }

            if (control != null)
            {
                dto.ControlActiveState = control.ActiveStateName ?? string.Empty;
                dto.ControlPreviousState = control.Fsm?.PreviousActiveState?.Name ?? string.Empty;

                foreach (FsmBool v in control.FsmVariables.BoolVariables)
                    dto.Bools[v.Name] = v.Value;
                foreach (FsmFloat v in control.FsmVariables.FloatVariables)
                    dto.Floats[v.Name] = v.Value;
                foreach (FsmInt v in control.FsmVariables.IntVariables)
                    dto.Ints[v.Name] = v.Value;
                foreach (FsmString v in control.FsmVariables.StringVariables)
                    dto.Strings[v.Name] = v.Value ?? string.Empty;
                foreach (FsmVector3 v in control.FsmVariables.Vector3Variables)
                    dto.Vector3s[v.Name] = FromVec3(v.Value);

                FsmState? activeState = control.Fsm?.ActiveState;
                if (activeState?.Actions != null)
                {
                    foreach (FsmStateAction action in activeState.Actions)
                    {
                        if (action is Wait wait)
                        {
                            FieldInfo? waitTimerField = typeof(Wait).GetField("timer", AnyInstance);
                            if (waitTimerField != null) dto.WaitTimer = (float)waitTimerField.GetValue(wait)!;
                        }
                        else if (action is GrimmChildFly fly)
                        {
                            FieldInfo? flyTimerField = typeof(GrimmChildFly).GetField("timer", AnyInstance);
                            if (flyTimerField != null) dto.FlyTimer = (float)flyTimerField.GetValue(fly)!;
                        }
                    }
                }
            }

            return dto;
        }

        private static void ApplyGrimmchildState(GameObject go, LeanS0GrimmchildState dto)
        {
            go.SetActive(dto.ActiveSelf);
            Transform tr = go.transform;
            tr.localPosition = ToVec3(dto.LocalPosition);
            tr.localRotation = ToQuat(dto.LocalRotation);
            tr.localScale = ToVec3(dto.LocalScale);

            Rigidbody2D? rb = go.GetComponent<Rigidbody2D>();
            if (rb != null)
            {
                rb.position = ToVec2(dto.RbPosition);
                rb.velocity = ToVec2(dto.RbVelocity);
            }

            ObjectBounce? bounce = go.GetComponent<ObjectBounce>();
            if (bounce != null)
            {
                BounceVelocityField.SetValue(bounce, ToVec2(dto.BounceVelocity));
                BounceLastPosField.SetValue(bounce, ToVec2(dto.BounceLastPos));
                BounceSpeedField.SetValue(bounce, dto.BounceSpeed);
                BounceStepCounterField.SetValue(bounce, dto.BounceStepCounter);
                BounceBouncingField.SetValue(bounce, dto.Bouncing);
            }

            PlayMakerFSM? control = go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f != null && f.FsmName == "Control");
            if (control != null)
            {
                foreach (KeyValuePair<string, bool> kvp in dto.Bools)
                {
                    FsmBool? v = control.FsmVariables.FindFsmBool(kvp.Key);
                    if (v != null) v.Value = kvp.Value;
                }
                foreach (KeyValuePair<string, float> kvp in dto.Floats)
                {
                    FsmFloat? v = control.FsmVariables.FindFsmFloat(kvp.Key);
                    if (v != null) v.Value = kvp.Value;
                }
                foreach (KeyValuePair<string, int> kvp in dto.Ints)
                {
                    FsmInt? v = control.FsmVariables.FindFsmInt(kvp.Key);
                    if (v != null) v.Value = kvp.Value;
                }
                foreach (KeyValuePair<string, string> kvp in dto.Strings)
                {
                    FsmString? v = control.FsmVariables.FindFsmString(kvp.Key);
                    if (v != null) v.Value = kvp.Value;
                }
                foreach (KeyValuePair<string, float[]> kvp in dto.Vector3s)
                {
                    FsmVector3? v = control.FsmVariables.FindFsmVector3(kvp.Key);
                    if (v != null) v.Value = ToVec3(kvp.Value);
                }

                if (!string.IsNullOrEmpty(dto.ControlActiveState) && control.ActiveStateName != dto.ControlActiveState)
                {
                    control.SetState(dto.ControlActiveState);
                }

                FsmState? activeState = control.Fsm?.ActiveState;
                if (activeState?.Actions != null)
                {
                    foreach (FsmStateAction action in activeState.Actions)
                    {
                        if (action is Wait wait)
                        {
                            FieldInfo? waitTimerField = typeof(Wait).GetField("timer", AnyInstance);
                            waitTimerField?.SetValue(wait, dto.WaitTimer);
                        }
                        else if (action is GrimmChildFly fly)
                        {
                            FieldInfo? flyTimerField = typeof(GrimmChildFly).GetField("timer", AnyInstance);
                            flyTimerField?.SetValue(fly, dto.FlyTimer);
                        }
                    }
                }
            }
        }

        private static LeanS0InputContainer CaptureInputContainer(FullRunActionSetAdapter input, FullRunMouseBridge mouse)
        {
            CheckpointInputState ci = input.CaptureCheckpoint();
            CheckpointMouseState cm = mouse.CaptureCheckpoint();

            return new LeanS0InputContainer
            {
                Input = new LeanS0InputStateDto
                {
                    MovieFrame = ci.MovieFrame,
                    Suspended = ci.Suspended,
                    AuthoredEdges = ci.AuthoredEdges.Select(e => (int)e).ToArray(),
                    KeyboardPrevious = (short[])ci.KeyboardPrevious.Clone(),
                    KeyboardCurrent = (short[])ci.KeyboardCurrent.Clone(),
                    Manager = new Dictionary<string, string>(ci.Manager, StringComparer.Ordinal),
                    Sets = ci.Sets.Select(s => new LeanS0ActionSetStateDto
                    {
                        Channel = (int)s.Channel,
                        RegistrationOrdinal = s.RegistrationOrdinal,
                        BindingActive = s.BindingActive,
                        Set = new Dictionary<string, string>(s.Set, StringComparer.Ordinal),
                        Actions = s.Actions.Select(a => new Dictionary<string, string>(a, StringComparer.Ordinal)).ToArray()
                    }).ToArray()
                },
                Mouse = new LeanS0MouseStateDto
                {
                    Enabled = cm.Enabled,
                    Suspended = cm.Suspended,
                    Width = cm.Width,
                    Height = cm.Height,
                    PreviousInControlButtons = cm.PreviousInControlButtons,
                    PreviousHkButtons = cm.PreviousHkButtons,
                    PreviousHkX = cm.PreviousHkX,
                    PreviousHkY = cm.PreviousHkY,
                    InControl = FromMouseValue(cm.InControl),
                    Hk = FromMouseValue(cm.Hk),
                    Modules = cm.Modules.Select(m => new LeanS0MouseModuleDto
                    {
                        Identity = m.Identity,
                        Fields = new Dictionary<string, string>(m.Fields, StringComparer.Ordinal)
                    }).ToArray()
                }
            };
        }

        private static void ApplyInputContainer(
            LeanS0InputContainer dto,
            FullRunActionSetAdapter input,
            FullRunMouseBridge mouse,
            MovieV2Document targetMovie)
        {
            var ci = new CheckpointInputState
            {
                MovieFrame = dto.Input.MovieFrame,
                Suspended = dto.Input.Suspended,
                AuthoredEdges = dto.Input.AuthoredEdges.Select(e => (GameInputChannel)e).ToArray(),
                KeyboardPrevious = (short[])dto.Input.KeyboardPrevious.Clone(),
                KeyboardCurrent = (short[])dto.Input.KeyboardCurrent.Clone(),
                Manager = new Dictionary<string, string>(dto.Input.Manager, StringComparer.Ordinal),
                Sets = dto.Input.Sets.Select(s => new CheckpointActionSetState
                {
                    Channel = (GameInputChannel)s.Channel,
                    RegistrationOrdinal = s.RegistrationOrdinal,
                    BindingActive = s.BindingActive,
                    Set = new Dictionary<string, string>(s.Set, StringComparer.Ordinal),
                    Actions = s.Actions.Select(a => new Dictionary<string, string>(a, StringComparer.Ordinal)).ToArray()
                }).ToArray()
            };

            var cm = new CheckpointMouseState
            {
                Enabled = dto.Mouse.Enabled,
                Suspended = dto.Mouse.Suspended,
                Width = dto.Mouse.Width,
                Height = dto.Mouse.Height,
                PreviousInControlButtons = dto.Mouse.PreviousInControlButtons,
                PreviousHkButtons = dto.Mouse.PreviousHkButtons,
                PreviousHkX = dto.Mouse.PreviousHkX,
                PreviousHkY = dto.Mouse.PreviousHkY,
                InControl = ToMouseValue(dto.Mouse.InControl),
                Hk = ToMouseValue(dto.Mouse.Hk),
                Modules = dto.Mouse.Modules.Select(m => new CheckpointMouseModule
                {
                    Identity = m.Identity,
                    Fields = new Dictionary<string, string>(m.Fields, StringComparer.Ordinal)
                }).ToArray()
            };

            input.ImportCheckpoint(ci, targetMovie);
            mouse.ImportCheckpoint(cm);
        }

        private static LeanS0MouseValueDto FromMouseValue(CheckpointMouseValue v) => new LeanS0MouseValueDto
        {
            X = v.X,
            Y = v.Y,
            DeltaX = v.DeltaX,
            DeltaY = v.DeltaY,
            Wheel = v.Wheel,
            Buttons = v.Buttons
        };

        private static CheckpointMouseValue ToMouseValue(LeanS0MouseValueDto v) => new CheckpointMouseValue
        {
            X = v.X,
            Y = v.Y,
            DeltaX = v.DeltaX,
            DeltaY = v.DeltaY,
            Wheel = v.Wheel,
            Buttons = v.Buttons
        };

        private static LeanS0TimeState CaptureTimeState(GameManager gm) => new LeanS0TimeState
        {
            TimeScale = Time.timeScale,
            TimeSlowedCount = (int)TimeSlowedCountField.GetValue(gm)!
        };

        private static void ApplyTimeState(GameManager gm, LeanS0TimeState state)
        {
            TimeController.SlowMotionTimeScale = 1f;
            TimeController.PauseTimeScale = 1f;
            TimeController.PlatformBackgroundTimeScale = 1f;
            TimeController.GenericTimeScale = state.TimeScale;
            if (Time.timeScale != state.TimeScale)
            {
                Time.timeScale = state.TimeScale;
            }
            TimeSlowedCountField.SetValue(gm, state.TimeSlowedCount);
        }

        private static LeanS0ControlPlaneState CaptureControlPlane(LeanS0CaptureContext ctx)
        {
            CanonicalInputEpoch epoch = CanonicalInputEpochRuntime.Capture(
                ctx.MovieInputNativeOrigin,
                ctx.NativeFrame,
                ctx.MovieInputControlOrigin);
            CanonicalFsmTimeCarry.Data fsmTime = CanonicalFsmTimeCarry.Capture();
            byte[] nativeClock = ctx.Clock.ExportContinuation();
            byte[] clockRandom = ctx.ExportRandomBytes();
            byte[] clockObserver = ctx.ExportClockObserverBytes();

            return new LeanS0ControlPlaneState
            {
                MovieId = !string.IsNullOrEmpty(ctx.MovieId) ? ctx.MovieId : ctx.Movie.Header.NativeProfileId,
                MoviePrefixSha256 = ctx.MoviePrefixSha256,
                MovieFrame = ctx.MovieFrame,
                NativeFrame = ctx.NativeFrame,
                ConsumedSamples = ctx.ConsumedSamples,
                SkippedLoadingFrames = ctx.SkippedLoadingFrames,
                TimingRunIndex = ctx.TimingRunIndex,
                TimingRunStart = ctx.TimingRunStart,
                ActiveFrameRate = ctx.ActiveFrameRate,
                RecordingFrameRate = ctx.RecordingFrameRate,
                TitleReadySeen = ctx.TitleReadySeen,
                FirstLevelActivationPending = ctx.FirstLevelActivationPending,
                RandomSeedScene = ctx.RandomSeedScene,
                LastGameUpdateNativeFrame = ctx.LastGameUpdateNativeFrame,
                GameUpdateDeltaSeconds = ctx.GameUpdateDeltaSeconds,
                GameUpdateTimeSeconds = ctx.GameUpdateTimeSeconds,
                ExecutedMovieSeconds = ctx.ExecutedMovieSeconds,
                MovieInputNativeOrigin = ctx.MovieInputNativeOrigin,
                NativeClockBase64 = Convert.ToBase64String(nativeClock),
                ClockRandomBase64 = Convert.ToBase64String(clockRandom),
                ClockObserverBase64 = Convert.ToBase64String(clockObserver),
                InputEpoch = new LeanS0InputEpochState
                {
                    Version = epoch.Version,
                    OriginNative = epoch.OriginNative,
                    SourceNative = epoch.SourceNative,
                    CurrentTick = epoch.CurrentTick,
                    SchedulerTimestamp = epoch.SchedulerTimestamp,
                    ControlOriginBound = epoch.ControlOriginBound,
                    OriginTick = epoch.OriginTick,
                    OriginSchedulerTimestamp = epoch.OriginSchedulerTimestamp,
                    InitialTimeBits = epoch.InitialTimeBits,
                    CurrentTimeBits = epoch.CurrentTimeBits,
                    LastUpdateTimeBits = epoch.LastUpdateTimeBits
                },
                FsmTime = new LeanS0FsmTimeState
                {
                    ModuleVersionId = fsmTime.ModuleVersionId,
                    FirstUpdateHasHappened = fsmTime.FirstUpdateHasHappened,
                    TotalEditorPlayerPausedTimeBits = fsmTime.TotalEditorPlayerPausedTimeBits,
                    RealtimeLastUpdateBits = fsmTime.RealtimeLastUpdateBits,
                    FrameCountLastUpdate = fsmTime.FrameCountLastUpdate
                }
            };
        }

        private static void ApplyControlPlaneClock(
            LeanS0ControlPlaneState c12,
            long targetNativeFrame,
            NativeFullRunFrameClock clock,
            Action<byte[], long> importRandom,
            Action<byte[], long> importObserver,
            out CanonicalInputEpoch restoredOrigin)
        {
            byte[] nativeClock = Convert.FromBase64String(c12.NativeClockBase64);
            byte[] clockRandom = Convert.FromBase64String(c12.ClockRandomBase64);
            byte[] clockObserver = Convert.FromBase64String(c12.ClockObserverBase64);

            var clockCarry = new CanonicalClockCarry
            {
                Native = nativeClock,
                Random = clockRandom,
                Observer = clockObserver
            };
            clockCarry.Validate(c12.NativeFrame);

            var fsmData = new CanonicalFsmTimeCarry.Data
            {
                Schema = 1,
                ModuleVersionId = c12.FsmTime.ModuleVersionId,
                FirstUpdateHasHappened = c12.FsmTime.FirstUpdateHasHappened,
                TotalEditorPlayerPausedTimeBits = c12.FsmTime.TotalEditorPlayerPausedTimeBits,
                RealtimeLastUpdateBits = c12.FsmTime.RealtimeLastUpdateBits,
                FrameCountLastUpdate = c12.FsmTime.FrameCountLastUpdate
            };

            bool imported = false;
            var fsmPrepared = CanonicalFsmTimeCarry.PrepareApply(
                fsmData,
                () => imported && clock.IsPaused && !clock.IsFinished && clock.CurrentFrameIndex == c12.NativeFrame);

            importRandom(clockCarry.Random, targetNativeFrame);
            clock.ImportContinuation(clockCarry.Native, targetNativeFrame);
            importObserver(clockCarry.Observer, c12.NativeFrame);
            imported = true;
            fsmPrepared.Apply();
            if (!fsmPrepared.ReadBackMatches)
            {
                throw new InvalidDataException("LeanS0 applied FsmTime readback differed.");
            }

            restoredOrigin = new CanonicalInputEpoch
            {
                Version = c12.InputEpoch.Version,
                OriginNative = c12.InputEpoch.OriginNative,
                SourceNative = c12.InputEpoch.SourceNative,
                CurrentTick = c12.InputEpoch.CurrentTick,
                SchedulerTimestamp = c12.InputEpoch.SchedulerTimestamp,
                ControlOriginBound = c12.InputEpoch.ControlOriginBound,
                OriginTick = c12.InputEpoch.OriginTick,
                OriginSchedulerTimestamp = c12.InputEpoch.OriginSchedulerTimestamp,
                InitialTimeBits = c12.InputEpoch.InitialTimeBits,
                CurrentTimeBits = c12.InputEpoch.CurrentTimeBits,
                LastUpdateTimeBits = c12.InputEpoch.LastUpdateTimeBits
            };
        }

        private static LeanS0AudioCueState CaptureAudioCues(GameManager gm)
        {
            AudioManager? am = gm.AudioManager;
            var music = am != null ? AudioCurrentMusicCueField.GetValue(am) as MusicCue : null;
            var atmos = am != null ? AudioCurrentAtmosCueField.GetValue(am) as AtmosCue : null;
            return new LeanS0AudioCueState
            {
                MusicCueName = music != null ? music.name : string.Empty,
                AtmosCueName = atmos != null ? atmos.name : string.Empty
            };
        }

        private static void ApplyAudioCues(GameManager gm, LeanS0AudioCueState state)
        {
            AudioManager? am = gm.AudioManager;
            if (am == null) return;
            if (!string.IsNullOrEmpty(state.MusicCueName))
            {
                MusicCue? cue = Resources.FindObjectsOfTypeAll<MusicCue>().FirstOrDefault(c => c != null && c.name == state.MusicCueName);
                if (cue != null) AudioCurrentMusicCueField.SetValue(am, cue);
            }
            if (!string.IsNullOrEmpty(state.AtmosCueName))
            {
                AtmosCue? cue = Resources.FindObjectsOfTypeAll<AtmosCue>().FirstOrDefault(c => c != null && c.name == state.AtmosCueName);
                if (cue != null) AudioCurrentAtmosCueField.SetValue(am, cue);
            }
        }

        private static float[] FromVec2(Vector2 v) => new[] { v.x, v.y };
        private static float[] FromVec3(Vector3 v) => new[] { v.x, v.y, v.z };
        private static float[] FromQuat(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        private static Vector2 ToVec2(float[] a) => new Vector2(a[0], a[1]);
        private static Vector3 ToVec3(float[] a) => new Vector3(a[0], a[1], a[2]);
        private static Quaternion ToQuat(float[] a) => new Quaternion(a[0], a[1], a[2], a[3]);
    }
}
