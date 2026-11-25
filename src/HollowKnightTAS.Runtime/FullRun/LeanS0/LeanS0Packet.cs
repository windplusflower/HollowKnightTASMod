using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HollowKnightTAS.Core.Movie;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed class LeanS0Packet
    {
        public string Schema { get; set; } = LeanS0PacketCodec.SchemaVersion;
        public int BoundaryOrdinal { get; set; }
        public string SourceSceneName { get; set; } = string.Empty;
        public string TargetSceneName { get; set; } = string.Empty;
        public string EntryGateName { get; set; } = string.Empty;
        public int RngSeed { get; set; }
        public string RngSha256 { get; set; } = string.Empty;
        public string CapturedAtUtc { get; set; } = string.Empty;
        public int GameManagerProfileId { get; set; }

        public string C1_PlayerDataJson { get; set; } = string.Empty;
        public string C2_SceneDataJson { get; set; } = string.Empty;
        public LeanS0BossSequenceState C3_BossSequence { get; set; } = new LeanS0BossSequenceState();
        public Dictionary<string, LeanS0TypedValue> C4_StaticVariables { get; set; } = new Dictionary<string, LeanS0TypedValue>(StringComparer.Ordinal);
        public LeanS0PlayMakerGlobalsState C5_PlayMakerGlobals { get; set; } = new LeanS0PlayMakerGlobalsState();
        public LeanS0TransitionInfo C6_TransitionInfo { get; set; } = new LeanS0TransitionInfo();
        public LeanS0HeroControllerState C7_HeroController { get; set; } = new LeanS0HeroControllerState();
        public LeanS0KnightFsmsState C8_KnightFsms { get; set; } = new LeanS0KnightFsmsState();
        public LeanS0CharmMinionsState C9_CharmMinions { get; set; } = new LeanS0CharmMinionsState();
        public LeanS0InputContainer C10_Input { get; set; } = new LeanS0InputContainer();
        public LeanS0TimeState C11_Time { get; set; } = new LeanS0TimeState();
        public LeanS0ControlPlaneState C12_ControlPlane { get; set; } = new LeanS0ControlPlaneState();
        public LeanS0AudioCueState D3_MusicCue { get; set; } = new LeanS0AudioCueState();
        public LeanS0HeroHitCrackState? D4_HeroHitCrack { get; set; }
        public LeanS0HudTweenState[]? D5_HudTweens { get; set; }
        public LeanS0ObjectPoolWarmupState? D6_ObjectPoolWarmup { get; set; }
        public LeanS0HeroInactiveComponentState[]? D7_HeroInactiveLifecycles { get; set; }
        public LeanS0CameraState? C13_Camera { get; set; }
        public LeanS0ModRandomState? C14_ModRandom { get; set; }
    }

    public sealed class LeanS0TypedValue
    {
        public string Kind { get; set; } = "null";
        public string? Value { get; set; }
    }

    public sealed class LeanS0BossDoorCompletionDto
    {
        public bool CanUnlock { get; set; }
        public bool Unlocked { get; set; }
        public bool Completed { get; set; }
        public bool AllBindings { get; set; }
        public bool NoHits { get; set; }
        public bool BoundNail { get; set; }
        public bool BoundShell { get; set; }
        public bool BoundCharms { get; set; }
        public bool BoundSoul { get; set; }
        public List<string> ViewedBossSceneCompletions { get; set; } = new List<string>();
    }

    public sealed class LeanS0BossSequenceState
    {
        public bool IsInSequence { get; set; }
        public bool WasCompleted { get; set; }
        public int BossIndex { get; set; }
        public int LastSelectedButton { get; set; } = -1;
        public string SequenceName { get; set; } = string.Empty;
        public bool HasCurrentData { get; set; }
        public int BindingsMask { get; set; }
        public float Timer { get; set; }
        public bool KnightDamaged { get; set; }
        public string PlayerDataKey { get; set; } = string.Empty;
        public LeanS0BossDoorCompletionDto PreviousCompletion { get; set; } = new LeanS0BossDoorCompletionDto();
        public int[] PreviousEquippedCharms { get; set; } = Array.Empty<int>();
        public bool? HasPreviousEquippedCharms { get; set; }
        public bool? PlayerDataAliasesCurrentData { get; set; }
        public bool? PlayerDataHasPreviousEquippedCharms { get; set; }
        public string? StatueLoadCurrentBossSceneName { get; set; }
        public string? StatueLoadPreviousBossSceneName { get; set; }
        public bool WasOvercharmed { get; set; }
        public bool HasPendingSetup { get; set; }
        public int PendingLevel { get; set; }
        public string PendingStatueSceneName { get; set; } = string.Empty;
        public string PendingStatueStatePd { get; set; } = string.Empty;
        public string PendingDreamStatueStatePd { get; set; } = string.Empty;
        public bool PendingStatueIsAlwaysUnlocked { get; set; }
        public bool HasPendingCompletionCache { get; set; }
        public bool HasPendingSequenceSetup { get; set; }
    }

    public sealed class LeanS0PlayMakerGlobalsState
    {
        public bool IsHudOut { get; set; }
        public float LowHealthFxWaitTime { get; set; }
        public string LowHealthFxActiveState { get; set; } = string.Empty;
        public string SlideOutActiveState { get; set; } = string.Empty;
        public string[] HealthDisplayActiveStates { get; set; } = Array.Empty<string>();
        public float[] HudCanvasLocalPosition { get; set; } = Array.Empty<float>();
        public float[] HealthRootLocalPosition { get; set; } = Array.Empty<float>();
        public float[] SoulOrbLocalPosition { get; set; } = Array.Empty<float>();
    }

    public sealed class LeanS0CameraTransformPose
    {
        public string Name { get; set; } = string.Empty;
        public float[] LocalPosition { get; set; } = new float[3];
        public float[] LocalRotation { get; set; } = new[] { 0f, 0f, 0f, 1f };
        public float[] LocalScale { get; set; } = new[] { 1f, 1f, 1f };
    }

    public sealed class LeanS0CameraState
    {
        public Dictionary<string, string> Controller { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Target { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public LeanS0CameraTransformPose[] Transforms { get; set; } = Array.Empty<LeanS0CameraTransformPose>();
    }

    public sealed class LeanS0ModRandomState
    {
        public Dictionary<string, uint> Ordinals { get; set; } = new Dictionary<string, uint>(StringComparer.Ordinal);
        public LeanS0ModRandomInstanceState[] Instances { get; set; } = Array.Empty<LeanS0ModRandomInstanceState>();
    }

    public sealed class LeanS0ModRandomInstanceState
    {
        public string CallsiteKey { get; set; } = string.Empty;
        public uint Ordinal { get; set; }
        public int Inext { get; set; }
        public int Inextp { get; set; }
        public int[] SeedArray { get; set; } = new int[56];
    }

    public sealed class LeanS0TransitionInfo
    {
        public string SceneName { get; set; } = string.Empty;
        public string EntryGateName { get; set; } = string.Empty;
        public int? HeroLeaveDirection { get; set; }
        public int Visualization { get; set; }
        public float EntryDelay { get; set; }
        public bool AlwaysUnloadUnusedAssets { get; set; }
        public bool PreventCameraFadeOut { get; set; }
        public bool WaitForSceneTransitionCameraFade { get; set; } = true;
        public bool IsFirstLevelForPlayer { get; set; }
        public bool ForceWaitFetch { get; set; }
    }

    public sealed class LeanS0HeroControllerState
    {
        public bool FacingRight { get; set; }
        public bool ExitedSuperDashing { get; set; }
        public bool ExitedQuake { get; set; }
        public bool EnterWithoutInput { get; set; }
        public float AttackCooldown { get; set; }
        public float DashCooldownTimer { get; set; }
        public float ShadowDashTimer { get; set; }
        public float ParryInvulnTimer { get; set; }
        public float PreventCastByDialogueEndTimer { get; set; }
        public int HitsSinceShielded { get; set; }
        public bool WieldingLantern { get; set; }
        public bool TakeNoDamage { get; set; }
        public bool BlockerFix { get; set; }
        public int JumpedSteps { get; set; }
        public float[]? Position { get; set; }
        public float[]? RbPosition { get; set; }
        public float[]? RbVelocity { get; set; }
    }

    public sealed class LeanS0KnightFsmsState
    {
        public string SporeCooldownActiveState { get; set; } = string.Empty;
        public string SporeCooldownPreviousState { get; set; } = string.Empty;
        public float? SporeCooldownWaitTimer { get; set; }
        public bool? SpellControlSporeCooldown { get; set; }
        public string DreamReturnActiveState { get; set; } = string.Empty;
        public string DreamReturnPreviousState { get; set; } = string.Empty;
        public Dictionary<string, bool> DreamReturnBools { get; set; } = new Dictionary<string, bool>(StringComparer.Ordinal);
        public Dictionary<string, float> DreamReturnFloats { get; set; } = new Dictionary<string, float>(StringComparer.Ordinal);
        public Dictionary<string, int> DreamReturnInts { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, float[]> DreamReturnVector2s { get; set; } = new Dictionary<string, float[]>(StringComparer.Ordinal);
        public Dictionary<string, float[]> DreamReturnVector3s { get; set; } = new Dictionary<string, float[]>(StringComparer.Ordinal);
        public string ControlInterpolationActiveState { get; set; } = string.Empty;
    }

    public sealed class LeanS0GrimmchildState
    {
        public bool ActiveSelf { get; set; }
        public float[] LocalPosition { get; set; } = new float[3];
        public float[] LocalRotation { get; set; } = new float[4];
        public float[] LocalScale { get; set; } = new float[3];
        public float[] RbPosition { get; set; } = new float[2];
        public float[] RbVelocity { get; set; } = new float[2];
        public float[] BounceVelocity { get; set; } = new float[2];
        public float[] BounceLastPos { get; set; } = new float[2];
        public float BounceSpeed { get; set; }
        public int BounceStepCounter { get; set; }
        public bool Bouncing { get; set; }
        public string ControlActiveState { get; set; } = string.Empty;
        public string ControlPreviousState { get; set; } = string.Empty;
        public Dictionary<string, bool> Bools { get; set; } = new Dictionary<string, bool>(StringComparer.Ordinal);
        public Dictionary<string, float> Floats { get; set; } = new Dictionary<string, float>(StringComparer.Ordinal);
        public Dictionary<string, int> Ints { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public Dictionary<string, string> Strings { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, float[]> Vector3s { get; set; } = new Dictionary<string, float[]>(StringComparer.Ordinal);
        public float WaitTimer { get; set; }
        public float FlyTimer { get; set; }
    }

    public sealed class LeanS0CharmMinionsState
    {
        public int HatchlingCount { get; set; }
        public bool HatchlingDreamgateEntry { get; set; }
        public string? WhiteCharmActiveState { get; set; }
        public float? WhiteCharmWaitTimer { get; set; }
        public LeanS0GrimmchildState? Grimmchild { get; set; }
    }

    public sealed class LeanS0ActionSetStateDto
    {
        public int Channel { get; set; }
        public int RegistrationOrdinal { get; set; }
        public bool BindingActive { get; set; }
        public Dictionary<string, string> Set { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string>[] Actions { get; set; } = Array.Empty<Dictionary<string, string>>();
    }

    public sealed class LeanS0InputStateDto
    {
        public long MovieFrame { get; set; }
        public bool Suspended { get; set; }
        public int[] AuthoredEdges { get; set; } = Array.Empty<int>();
        public short[] KeyboardPrevious { get; set; } = Array.Empty<short>();
        public short[] KeyboardCurrent { get; set; } = Array.Empty<short>();
        public Dictionary<string, string> Manager { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public LeanS0ActionSetStateDto[] Sets { get; set; } = Array.Empty<LeanS0ActionSetStateDto>();
    }

    public sealed class LeanS0MouseValueDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public short DeltaX { get; set; }
        public short DeltaY { get; set; }
        public short Wheel { get; set; }
        public uint Buttons { get; set; }
    }

    public sealed class LeanS0MouseModuleDto
    {
        public string Identity { get; set; } = string.Empty;
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public sealed class LeanS0MouseStateDto
    {
        public bool Enabled { get; set; }
        public bool Suspended { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public LeanS0MouseValueDto InControl { get; set; } = new LeanS0MouseValueDto();
        public LeanS0MouseValueDto Hk { get; set; } = new LeanS0MouseValueDto();
        public uint PreviousInControlButtons { get; set; }
        public uint PreviousHkButtons { get; set; }
        public float PreviousHkX { get; set; }
        public float PreviousHkY { get; set; }
        public LeanS0MouseModuleDto[] Modules { get; set; } = Array.Empty<LeanS0MouseModuleDto>();
    }

    public sealed class LeanS0InputContainer
    {
        public LeanS0InputStateDto Input { get; set; } = new LeanS0InputStateDto();
        public LeanS0MouseStateDto Mouse { get; set; } = new LeanS0MouseStateDto();
    }

    public sealed class LeanS0TimeState
    {
        public float TimeScale { get; set; } = 1f;
        public int TimeSlowedCount { get; set; }
    }

    public sealed class LeanS0FsmTimeState
    {
        public string ModuleVersionId { get; set; } = string.Empty;
        public bool FirstUpdateHasHappened { get; set; }
        public uint TotalEditorPlayerPausedTimeBits { get; set; }
        public uint RealtimeLastUpdateBits { get; set; }
        public int FrameCountLastUpdate { get; set; }
    }

    public sealed class LeanS0InputEpochState
    {
        public int Version { get; set; } = 1;
        public long? OriginNative { get; set; }
        public long SourceNative { get; set; }
        public ulong CurrentTick { get; set; }
        public uint SchedulerTimestamp { get; set; }
        public bool ControlOriginBound { get; set; }
        public ulong OriginTick { get; set; }
        public uint OriginSchedulerTimestamp { get; set; }
        public int InitialTimeBits { get; set; }
        public int CurrentTimeBits { get; set; }
        public int LastUpdateTimeBits { get; set; }
    }

    public sealed class LeanS0ControlPlaneState
    {
        public string MovieId { get; set; } = string.Empty;
        public string MoviePrefixSha256 { get; set; } = string.Empty;
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
        public string NativeClockBase64 { get; set; } = string.Empty;
        public string ClockRandomBase64 { get; set; } = string.Empty;
        public string ClockObserverBase64 { get; set; } = string.Empty;
        public LeanS0InputEpochState InputEpoch { get; set; } = new LeanS0InputEpochState();
        public LeanS0FsmTimeState FsmTime { get; set; } = new LeanS0FsmTimeState();
    }

    public sealed class LeanS0AudioCueState
    {
        public string MusicCueName { get; set; } = string.Empty;
        public string AtmosCueName { get; set; } = string.Empty;
    }

    public sealed class LeanS0HeroHitCrackState
    {
        public Dictionary<string, string> RotationFields { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> DeactivationFields { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public byte[] Lifecycle { get; set; } = Array.Empty<byte>();
        public byte[] DeactivationLifecycle { get; set; } = Array.Empty<byte>();
        public bool RotationEnabled { get; set; }
        public bool DeactivationEnabled { get; set; }
        public float[] LocalPosition { get; set; } = new float[3];
        public float[] LocalRotation { get; set; } = new float[4];
        public float[] LocalScale { get; set; } = new float[3];
    }

    public sealed class LeanS0HudTweenState
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public int MatchIndex { get; set; }
        public bool Enabled { get; set; }
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public float LastRealTimeOffset { get; set; }
        public float[][] Vectors { get; set; } = Array.Empty<float[]>();
        public float[] LocalPosition { get; set; } = new float[3];
        public float[] LocalRotation { get; set; } = new float[4];
        public float[] LocalScale { get; set; } = new float[3];
        public byte[] Lifecycle { get; set; } = Array.Empty<byte>();
    }

    public sealed class LeanS0PooledPrefabState
    {
        public string PrefabKey { get; set; } = string.Empty;
        public int TotalCount { get; set; }
        public bool[] AwakenedSlots { get; set; } = Array.Empty<bool>();
    }

    public sealed class LeanS0ObjectPoolWarmupState
    {
        public LeanS0PooledPrefabState[] Entries { get; set; } = Array.Empty<LeanS0PooledPrefabState>();
    }

    public sealed class LeanS0HeroInactiveComponentState
    {
        public string RelativePath { get; set; } = string.Empty;
        public string TypeFullName { get; set; } = string.Empty;
        public int MatchIndex { get; set; }
        public bool Enabled { get; set; }
        public byte[] Lifecycle { get; set; } = Array.Empty<byte>();
        public Dictionary<string, string> Fields { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public float[] LocalRotation { get; set; } = new float[4];
        public float[] LocalScale { get; set; } = new float[3];
        public bool HasAudioPitch { get; set; }
        public float AudioPitch { get; set; }
    }

    public static class LeanS0PacketCodec
    {
        public const string SchemaVersion = "lean-s0-packet-v1";
        private static readonly byte[] MagicBytes = { (byte)'L', (byte)'N', (byte)'S', (byte)'0' };
        private const int BinaryFormatVersion = 1;
        private const int HeaderBytes = 4 + 4 + 4 + 32; // Magic(4) + Version(4) + CompressedLength(4) + Sha256(32)

        private static readonly JsonSerializerSettings StrictJsonSettings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
            DefaultValueHandling = DefaultValueHandling.Include,
            Formatting = Formatting.None
        };

        public static string SerializeJson(LeanS0Packet packet)
        {
            Validate(packet);
            return JsonConvert.SerializeObject(packet, StrictJsonSettings);
        }

        public static LeanS0Packet DeserializeJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                throw new InvalidDataException("LeanS0 JSON payload is empty.");
            }

            LeanS0Packet? packet;
            try
            {
                packet = JsonConvert.DeserializeObject<LeanS0Packet>(json, StrictJsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("LeanS0 JSON schema validation failed: " + ex.Message, ex);
            }

            if (packet == null)
            {
                throw new InvalidDataException("LeanS0 JSON deserialized to null.");
            }

            Validate(packet);
            return packet;
        }

        public static byte[] Encode(LeanS0Packet packet)
        {
            string json = SerializeJson(packet);
            byte[] utf8 = Encoding.UTF8.GetBytes(json);
            byte[] compressed;
            using (var ms = new MemoryStream())
            {
                using (var gzip = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
                {
                    gzip.Write(utf8, 0, utf8.Length);
                }
                compressed = ms.ToArray();
            }

            if (HeaderBytes + compressed.Length > LeanS0Mode.MaxPacketBytes)
            {
                throw new InvalidDataException(
                    $"LeanS0 encoded packet exceeds maximum size ({HeaderBytes + compressed.Length} > {LeanS0Mode.MaxPacketBytes}).");
            }

            byte[] sha256Bytes = ComputeSha256Bytes(compressed);
            byte[] output = new byte[HeaderBytes + compressed.Length];
            Buffer.BlockCopy(MagicBytes, 0, output, 0, 4);
            WriteInt32LittleEndian(output, 4, BinaryFormatVersion);
            WriteInt32LittleEndian(output, 8, compressed.Length);
            Buffer.BlockCopy(sha256Bytes, 0, output, 12, 32);
            Buffer.BlockCopy(compressed, 0, output, 44, compressed.Length);
            return output;
        }

        public static LeanS0Packet Decode(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (data.Length < HeaderBytes || data.Length > LeanS0Mode.MaxPacketBytes)
            {
                throw new InvalidDataException($"LeanS0 packet length out of bounds: {data.Length}.");
            }

            for (int i = 0; i < 4; i++)
            {
                if (data[i] != MagicBytes[i])
                {
                    throw new InvalidDataException("LeanS0 packet magic header mismatch.");
                }
            }

            int formatVersion = ReadInt32LittleEndian(data, 4);
            if (formatVersion != BinaryFormatVersion)
            {
                throw new InvalidDataException($"Unsupported LeanS0 binary format version: {formatVersion}.");
            }

            int compressedLength = ReadInt32LittleEndian(data, 8);
            if (compressedLength <= 0 || HeaderBytes + compressedLength != data.Length)
            {
                throw new InvalidDataException("LeanS0 packet compressed payload length mismatch.");
            }

            byte[] compressed = new byte[compressedLength];
            Buffer.BlockCopy(data, HeaderBytes, compressed, 0, compressedLength);
            byte[] actualSha256 = ComputeSha256Bytes(compressed);
            for (int i = 0; i < 32; i++)
            {
                if (actualSha256[i] != data[12 + i])
                {
                    throw new InvalidDataException("LeanS0 packet SHA-256 integrity check failed.");
                }
            }

            string json;
            using (var input = new MemoryStream(compressed))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var outStream = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                int totalDecompressed = 0;
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    totalDecompressed += read;
                    if (totalDecompressed > LeanS0Mode.MaxPacketBytes)
                    {
                        throw new InvalidDataException("LeanS0 decompressed JSON exceeds maximum allowed size.");
                    }
                    outStream.Write(buffer, 0, read);
                }
                json = Encoding.UTF8.GetString(outStream.ToArray());
            }

            return DeserializeJson(json);
        }

        public static void Validate(LeanS0Packet packet)
        {
            if (packet == null)
            {
                throw new InvalidDataException("LeanS0Packet is null.");
            }
            if (!string.Equals(packet.Schema, SchemaVersion, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unsupported LeanS0 packet schema: '{packet.Schema}'.");
            }
            if (packet.BoundaryOrdinal < 1)
            {
                throw new InvalidDataException($"LeanS0 BoundaryOrdinal must be >= 1, got {packet.BoundaryOrdinal}.");
            }
            if (string.IsNullOrWhiteSpace(packet.TargetSceneName))
            {
                throw new InvalidDataException("LeanS0 TargetSceneName is required.");
            }
            if (string.IsNullOrWhiteSpace(packet.C1_PlayerDataJson) || string.IsNullOrWhiteSpace(packet.C2_SceneDataJson))
            {
                throw new InvalidDataException("LeanS0 C1_PlayerDataJson and C2_SceneDataJson are required.");
            }
            if (packet.C3_BossSequence == null
                || packet.C4_StaticVariables == null
                || packet.C5_PlayMakerGlobals == null
                || packet.C6_TransitionInfo == null
                || packet.C7_HeroController == null
                || packet.C8_KnightFsms == null
                || packet.C9_CharmMinions == null
                || packet.C10_Input == null
                || packet.C10_Input.Input == null
                || packet.C10_Input.Mouse == null
                || packet.C11_Time == null
                || packet.C12_ControlPlane == null
                || packet.D3_MusicCue == null)
            {
                throw new InvalidDataException("LeanS0 packet is missing one or more required carry categories.");
            }
            if (string.IsNullOrWhiteSpace(packet.C6_TransitionInfo.SceneName))
            {
                throw new InvalidDataException("LeanS0 C6_TransitionInfo.SceneName is required.");
            }
            if (packet.C9_CharmMinions.Grimmchild != null)
            {
                LeanS0GrimmchildState gc = packet.C9_CharmMinions.Grimmchild;
                if (gc.LocalPosition?.Length != 3 || gc.LocalRotation?.Length != 4 || gc.LocalScale?.Length != 3
                    || gc.RbPosition?.Length != 2 || gc.RbVelocity?.Length != 2
                    || gc.BounceVelocity?.Length != 2 || gc.BounceLastPos?.Length != 2)
                {
                    throw new InvalidDataException("LeanS0 Grimmchild vector array dimensions are invalid.");
                }
            }
            if (packet.D4_HeroHitCrack != null)
            {
                LeanS0HeroHitCrackState hc = packet.D4_HeroHitCrack;
                if (hc.RotationFields == null || hc.DeactivationFields == null
                    || hc.Lifecycle?.Length != 2 || hc.DeactivationLifecycle?.Length != 2
                    || hc.LocalPosition?.Length != 3 || hc.LocalRotation?.Length != 4 || hc.LocalScale?.Length != 3)
                {
                    throw new InvalidDataException("LeanS0 D4_HeroHitCrack dimensions are invalid.");
                }
            }
            if (packet.D5_HudTweens != null)
            {
                foreach (LeanS0HudTweenState tween in packet.D5_HudTweens)
                {
                    if (tween == null || tween.Fields == null || tween.Vectors == null
                        || tween.Lifecycle?.Length != 2 || tween.MatchIndex < 0
                        || tween.LocalPosition?.Length != 3 || tween.LocalRotation?.Length != 4 || tween.LocalScale?.Length != 3
                        || tween.Vectors.Any(v => v == null || v.Length != 3))
                    {
                        throw new InvalidDataException("LeanS0 D5_HudTweens dimensions are invalid.");
                    }
                }
            }
            if (packet.D6_ObjectPoolWarmup != null)
            {
                if (packet.D6_ObjectPoolWarmup.Entries == null)
                {
                    throw new InvalidDataException("LeanS0 D6_ObjectPoolWarmup entries cannot be null.");
                }
                foreach (LeanS0PooledPrefabState entry in packet.D6_ObjectPoolWarmup.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.PrefabKey)
                        || entry.TotalCount < 0 || entry.AwakenedSlots == null
                        || entry.AwakenedSlots.Length > entry.TotalCount)
                    {
                        throw new InvalidDataException("LeanS0 D6_ObjectPoolWarmup entry is invalid.");
                    }
                }
            }
            if (packet.D7_HeroInactiveLifecycles != null)
            {
                foreach (LeanS0HeroInactiveComponentState comp in packet.D7_HeroInactiveLifecycles)
                {
                    if (comp == null || string.IsNullOrEmpty(comp.TypeFullName)
                        || comp.Fields == null || comp.MatchIndex < 0
                        || comp.Lifecycle?.Length != 2
                        || comp.LocalRotation?.Length != 4 || comp.LocalScale?.Length != 3)
                    {
                        throw new InvalidDataException("LeanS0 D7_HeroInactiveLifecycles entry is invalid.");
                    }
                }
            }
            if (packet.C14_ModRandom != null)
            {
                LeanS0ModRandomState modRandom = packet.C14_ModRandom;
                if (modRandom.Ordinals == null)
                {
                    throw new InvalidDataException("LeanS0 C14_ModRandom.Ordinals cannot be null.");
                }
                foreach (string key in modRandom.Ordinals.Keys)
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        throw new InvalidDataException("LeanS0 C14_ModRandom.Ordinals contains an empty callsite key.");
                    }
                }
                if (modRandom.Instances == null)
                {
                    throw new InvalidDataException("LeanS0 C14_ModRandom.Instances cannot be null.");
                }
                foreach (LeanS0ModRandomInstanceState inst in modRandom.Instances)
                {
                    if (inst == null || string.IsNullOrEmpty(inst.CallsiteKey)
                        || inst.SeedArray == null || inst.SeedArray.Length != 56
                        || inst.Inext < 0 || inst.Inext > 55
                        || inst.Inextp < 0 || inst.Inextp > 55)
                    {
                        throw new InvalidDataException("LeanS0 C14_ModRandom instance entry is invalid.");
                    }
                }
            }
            if ((packet.C7_HeroController.Position != null && packet.C7_HeroController.Position.Length != 3)
                || (packet.C7_HeroController.RbPosition != null && packet.C7_HeroController.RbPosition.Length != 2)
                || (packet.C7_HeroController.RbVelocity != null && packet.C7_HeroController.RbVelocity.Length != 2))
            {
                throw new InvalidDataException("LeanS0 C7_HeroController spatial array dimensions are invalid.");
            }
            if (packet.C12_ControlPlane.MovieFrame < 0 || packet.C12_ControlPlane.NativeFrame < 0
                || packet.C12_ControlPlane.ConsumedSamples < 0 || packet.C12_ControlPlane.SkippedLoadingFrames < 0)
            {
                throw new InvalidDataException("LeanS0 C12 control-plane cursors must be non-negative.");
            }
            if (!MovieProtocolV1.IsLowerSha256(packet.C12_ControlPlane.MoviePrefixSha256)
                || string.IsNullOrWhiteSpace(packet.C12_ControlPlane.NativeClockBase64)
                || string.IsNullOrWhiteSpace(packet.C12_ControlPlane.ClockRandomBase64)
                || string.IsNullOrWhiteSpace(packet.C12_ControlPlane.ClockObserverBase64))
            {
                throw new InvalidDataException("LeanS0 C12 control-plane hashes or serialized clock blobs are incomplete.");
            }
        }

        private static byte[] ComputeSha256Bytes(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private static int ReadInt32LittleEndian(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }
    }
}
