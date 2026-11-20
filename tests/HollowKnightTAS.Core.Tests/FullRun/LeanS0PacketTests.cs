using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class LeanS0PacketTests
    {
        private static LeanS0Packet CreateSamplePacket()
        {
            return new LeanS0Packet
            {
                BoundaryOrdinal = 8,
                SourceSceneName = "GG_Crystal_Guardian_2",
                TargetSceneName = "GG_Lost_Kin",
                EntryGateName = "door_dreamEnter",
                RngSeed = 123456789,
                RngSha256 = new string('a', 64),
                CapturedAtUtc = "2026-11-14T07:00:00Z",
                GameManagerProfileId = 3,
                C1_PlayerDataJson = "{\"health\":9,\"geo\":420}",
                C2_SceneDataJson = "{\"persistentBoolItems\":[]}",
                C3_BossSequence = new LeanS0BossSequenceState
                {
                    IsInSequence = true,
                    WasCompleted = false,
                    BossIndex = 7,
                    LastSelectedButton = 2,
                    SequenceName = "Boss Sequence Tier 4",
                    HasCurrentData = true,
                    BindingsMask = 0,
                    Timer = 210.5f,
                    KnightDamaged = true,
                    PlayerDataKey = "bossDoorStateTier4",
                    PreviousCompletion = new LeanS0BossDoorCompletionDto
                    {
                        CanUnlock = true,
                        Unlocked = true,
                        Completed = false,
                        ViewedBossSceneCompletions = new List<string> { "GG_Enraged_Guardian" }
                    },
                    PreviousEquippedCharms = new[] { 13, 21, 25, 32 },
                    WasOvercharmed = false,
                    HasPendingSequenceSetup = true
                },
                C4_StaticVariables = new Dictionary<string, LeanS0TypedValue>
                {
                    ["currentBossDoorPD"] = new LeanS0TypedValue { Kind = "string", Value = "bossDoorStateTier4" },
                    ["firstBossScene"] = new LeanS0TypedValue { Kind = "string", Value = "GG_Crystal_Guardian_2" }
                },
                C5_PlayMakerGlobals = new LeanS0PlayMakerGlobalsState
                {
                    IsHudOut = false,
                    LowHealthFxWaitTime = 0.15f,
                    LowHealthFxActiveState = "Idle",
                    SlideOutActiveState = "Idle"
                },
                C6_TransitionInfo = new LeanS0TransitionInfo
                {
                    SceneName = "GG_Lost_Kin",
                    EntryGateName = "door_dreamEnter",
                    HeroLeaveDirection = null,
                    Visualization = 4,
                    EntryDelay = 0f,
                    AlwaysUnloadUnusedAssets = false,
                    PreventCameraFadeOut = false,
                    IsFirstLevelForPlayer = false
                },
                C7_HeroController = new LeanS0HeroControllerState
                {
                    FacingRight = true,
                    AttackCooldown = 0.12f,
                    HitsSinceShielded = 2,
                    WieldingLantern = false,
                    BlockerFix = true,
                    JumpedSteps = 12
                },
                C8_KnightFsms = new LeanS0KnightFsmsState
                {
                    SporeCooldownActiveState = "Idle",
                    SporeCooldownPreviousState = "Init",
                    DreamReturnActiveState = "Idle",
                    DreamReturnPreviousState = "Check",
                    DreamReturnBools = new Dictionary<string, bool> { ["Dream Returning"] = true, ["Gate Returning"] = false },
                    DreamReturnFloats = new Dictionary<string, float> { ["Gate X"] = 12.5f },
                    DreamReturnInts = new Dictionary<string, int> { ["Decrement"] = -1 },
                    DreamReturnVector2s = new Dictionary<string, float[]> { ["Respawn Pos"] = new[] { 10f, 20f } },
                    DreamReturnVector3s = new Dictionary<string, float[]>(),
                    ControlInterpolationActiveState = "Check Pause"
                },
                C9_CharmMinions = new LeanS0CharmMinionsState
                {
                    HatchlingCount = 2,
                    HatchlingDreamgateEntry = false,
                    Grimmchild = new LeanS0GrimmchildState
                    {
                        ActiveSelf = true,
                        LocalPosition = new[] { 1.25f, 3.5f, 0f },
                        LocalRotation = new[] { 0f, 0f, 0f, 1f },
                        LocalScale = new[] { -1f, 1f, 1f },
                        RbPosition = new[] { 1.25f, 3.5f },
                        RbVelocity = new[] { 0.4f, -0.2f },
                        BounceVelocity = new[] { 0.4f, -0.2f },
                        BounceLastPos = new[] { 1.2f, 3.55f },
                        BounceSpeed = 0.447f,
                        BounceStepCounter = 3,
                        Bouncing = false,
                        ControlActiveState = "Follow",
                        ControlPreviousState = "Pause",
                        Floats = new Dictionary<string, float> { ["Attack Timer"] = 0.55f },
                        Ints = new Dictionary<string, int> { ["Grimm Level"] = 4 },
                        Bools = new Dictionary<string, bool> { ["Away"] = false },
                        Strings = new Dictionary<string, string> { ["Fly Anim"] = "Fly" },
                        Vector3s = new Dictionary<string, float[]> { ["Offset"] = new[] { -1.2f, 0.8f, 0f } },
                        WaitTimer = 0f,
                        FlyTimer = 0.1f
                    }
                },
                C10_Input = new LeanS0InputContainer
                {
                    Input = new LeanS0InputStateDto
                    {
                        MovieFrame = 6706,
                        AuthoredEdges = new[] { 1 },
                        KeyboardPrevious = new short[] { 32 },
                        KeyboardCurrent = Array.Empty<short>()
                    },
                    Mouse = new LeanS0MouseStateDto
                    {
                        Width = 1920,
                        Height = 1080
                    }
                },
                C11_Time = new LeanS0TimeState { TimeScale = 1f, TimeSlowedCount = 0 },
                C12_ControlPlane = new LeanS0ControlPlaneState
                {
                    MovieId = "p3b-test",
                    MoviePrefixSha256 = new string('b', 64),
                    MovieFrame = 6706,
                    NativeFrame = 6975,
                    ConsumedSamples = 6706,
                    SkippedLoadingFrames = 269,
                    ActiveFrameRate = 60m,
                    RecordingFrameRate = 60m,
                    NativeClockBase64 = Convert.ToBase64String(new byte[16]),
                    ClockRandomBase64 = Convert.ToBase64String(new byte[16]),
                    ClockObserverBase64 = Convert.ToBase64String(new byte[16])
                },
                D3_MusicCue = new LeanS0AudioCueState { MusicCueName = "GG_Main", AtmosCueName = "GG_Atmos" }
            };
        }

        [TestMethod]
        public void EncodeDecode_RoundTrip_PreservesAllFields()
        {
            LeanS0Packet original = CreateSamplePacket();
            byte[] encoded = LeanS0PacketCodec.Encode(original);
            LeanS0Packet decoded = LeanS0PacketCodec.Decode(encoded);

            Assert.AreEqual(LeanS0PacketCodec.SchemaVersion, decoded.Schema);
            Assert.AreEqual(8, decoded.BoundaryOrdinal);
            Assert.AreEqual("GG_Crystal_Guardian_2", decoded.SourceSceneName);
            Assert.AreEqual("GG_Lost_Kin", decoded.TargetSceneName);
            Assert.AreEqual("door_dreamEnter", decoded.EntryGateName);
            Assert.AreEqual(123456789, decoded.RngSeed);
            Assert.AreEqual(3, decoded.GameManagerProfileId);
            Assert.AreEqual(original.C1_PlayerDataJson, decoded.C1_PlayerDataJson);
            Assert.AreEqual(original.C3_BossSequence.SequenceName, decoded.C3_BossSequence.SequenceName);
            Assert.IsTrue(decoded.C3_BossSequence.HasPendingSequenceSetup);
            Assert.AreEqual(12, decoded.C7_HeroController.JumpedSteps);
            Assert.IsTrue(decoded.C8_KnightFsms.DreamReturnBools["Dream Returning"]);
            Assert.AreEqual(12.5f, decoded.C8_KnightFsms.DreamReturnFloats["Gate X"]);
            Assert.AreEqual(10f, decoded.C8_KnightFsms.DreamReturnVector2s["Respawn Pos"][0]);
            Assert.AreEqual(0.15f, decoded.C5_PlayMakerGlobals.LowHealthFxWaitTime);
            Assert.AreEqual("Idle", decoded.C5_PlayMakerGlobals.LowHealthFxActiveState);
            Assert.AreEqual("Idle", decoded.C5_PlayMakerGlobals.SlideOutActiveState);
            Assert.AreEqual("bossDoorStateTier4", decoded.C4_StaticVariables["currentBossDoorPD"].Value);
            Assert.AreEqual(2, decoded.C9_CharmMinions.HatchlingCount);
            Assert.IsNotNull(decoded.C9_CharmMinions.Grimmchild);
            Assert.AreEqual(0.55f, decoded.C9_CharmMinions.Grimmchild!.Floats["Attack Timer"]);
            Assert.AreEqual(6706, decoded.C12_ControlPlane.MovieFrame);
            Assert.AreEqual("GG_Main", decoded.D3_MusicCue.MusicCueName);
        }

        [TestMethod]
        public void DeserializeJson_RejectsUnknownField()
        {
            LeanS0Packet original = CreateSamplePacket();
            string json = LeanS0PacketCodec.SerializeJson(original);
            string mutatedJson = json.Substring(0, json.Length - 1) + ",\"UnexpectedField\":123}";

            Assert.ThrowsExactly<InvalidDataException>(() => LeanS0PacketCodec.DeserializeJson(mutatedJson));
        }

        [TestMethod]
        public void DeserializeJson_RejectsWrongSchemaVersion()
        {
            LeanS0Packet original = CreateSamplePacket();
            string json = LeanS0PacketCodec.SerializeJson(original)
                .Replace(LeanS0PacketCodec.SchemaVersion, "lean-s0-packet-v999");

            Assert.ThrowsExactly<InvalidDataException>(() => LeanS0PacketCodec.DeserializeJson(json));
        }

        [TestMethod]
        public void Decode_RejectsCorruptedChecksum()
        {
            byte[] encoded = LeanS0PacketCodec.Encode(CreateSamplePacket());
            encoded[encoded.Length - 1] ^= 0x5A;

            Assert.ThrowsExactly<InvalidDataException>(() => LeanS0PacketCodec.Decode(encoded));
        }

        [TestMethod]
        public void DeserializeJson_DefaultsMissingGameManagerProfileIdToZero()
        {
            LeanS0Packet original = CreateSamplePacket();
            string json = LeanS0PacketCodec.SerializeJson(original)
                .Replace("\"GameManagerProfileId\":3,", string.Empty, StringComparison.Ordinal);

            LeanS0Packet decoded = LeanS0PacketCodec.DeserializeJson(json);
            Assert.AreEqual(0, decoded.GameManagerProfileId);
        }
    }
}
