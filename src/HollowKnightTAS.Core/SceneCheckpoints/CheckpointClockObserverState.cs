using System;
using System.IO;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    // Fixed HCO1 data. The source getter is an independent anchor; it never
    // substitutes for the source observer's cached last-frame identity.
    public sealed class CheckpointClockObserverState
    {
        public const int EncodedBytes = 32;
        private const uint Magic = 0x314f4348; // HCO1
        public CheckpointClockObserverState(bool unityFrameObserved, bool sceneClockExclusionActive,
            int lastUnityFrameCount, int advanceSequence, int captureUnityFrameCount)
        {
            if (lastUnityFrameCount < 0 || captureUnityFrameCount < 0 || lastUnityFrameCount > captureUnityFrameCount
                || !unityFrameObserved && lastUnityFrameCount != 0 || advanceSequence < 0 || advanceSequence == int.MaxValue)
                throw new InvalidDataException("Invalid checkpoint clock observer identity.");
            UnityFrameObserved = unityFrameObserved; SceneClockExclusionActive = sceneClockExclusionActive;
            LastUnityFrameCount = lastUnityFrameCount; AdvanceSequence = advanceSequence;
            CaptureUnityFrameCount = captureUnityFrameCount;
        }
        public bool UnityFrameObserved { get; }
        public bool SceneClockExclusionActive { get; }
        public int LastUnityFrameCount { get; }
        public int AdvanceSequence { get; }
        public int CaptureUnityFrameCount { get; }

        public byte[] Encode()
        {
            using (var stream = new MemoryStream(EncodedBytes))
            using (var writer = new BinaryWriter(stream))
            {
                // 0 magic, 4 version, 8 length, 12 reserved; 16/17 raw flags,
                // 18 reserved; 20 cached frame, 24 sequence, 28 getter anchor.
                writer.Write(Magic); writer.Write(1); writer.Write(EncodedBytes); writer.Write(0);
                writer.Write((byte)(UnityFrameObserved ? 1 : 0)); writer.Write((byte)(SceneClockExclusionActive ? 1 : 0));
                writer.Write((ushort)0); writer.Write(LastUnityFrameCount); writer.Write(AdvanceSequence);
                writer.Write(CaptureUnityFrameCount); writer.Flush(); return stream.ToArray();
            }
        }
        public static CheckpointClockObserverState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length != EncodedBytes) throw new InvalidDataException("Invalid clock observer size.");
            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadInt32() != 1 || reader.ReadInt32() != EncodedBytes || reader.ReadInt32() != 0)
                    throw new InvalidDataException("Unknown clock observer schema or reserved bits.");
                var observed = reader.ReadByte(); var excluded = reader.ReadByte();
                if (observed > 1 || excluded > 1 || reader.ReadUInt16() != 0)
                    throw new InvalidDataException("Noncanonical clock observer flags.");
                return new CheckpointClockObserverState(observed == 1, excluded == 1,
                    reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            }
        }

        public void RequireReadback(CheckpointClockObserverState observed)
        {
            if (observed == null || UnityFrameObserved != observed.UnityFrameObserved
                || SceneClockExclusionActive != observed.SceneClockExclusionActive
                || LastUnityFrameCount != observed.LastUnityFrameCount || AdvanceSequence != observed.AdvanceSequence
                || CaptureUnityFrameCount != observed.CaptureUnityFrameCount)
                throw new InvalidDataException("Checkpoint clock observer readback differs.");
        }

        public void RequireNativeReadback(byte[] native, long expectedNative, int observedUnityFrame)
        {
            // HTC1 header is 104 bytes. TimeManager.frameCount is at 0xc8,
            // relative to its portable engine span starting at 0x30: 256.
            if (native == null || native.Length != 1128 || expectedNative < 0
                || BitConverter.ToUInt32(native, 0) != 0x31435448 || BitConverter.ToInt32(native, 4) != 1
                || BitConverter.ToInt32(native, 8) != native.Length || BitConverter.ToInt32(native, 12) != 0
                || BitConverter.ToUInt64(native, 80) != (ulong)expectedNative
                || BitConverter.ToInt32(native, 88) != AdvanceSequence
                || BitConverter.ToInt64(native, 256) != CaptureUnityFrameCount || observedUnityFrame != CaptureUnityFrameCount)
                throw new InvalidDataException("Checkpoint clock observer native/Unity boundary differs.");
        }

        public void RequireFrameNativeReadback(byte[] native, long expectedNative, int observedUnityFrame)
        {
            // RFC1 captures paused/resume, callback phase, and the complete
            // TimeManager body. It is only a witness and cannot be imported.
            if (native == null || native.Length != 1192 || expectedNative < 0 || BitConverter.ToUInt32(native, 0) != 0x31434652
                || BitConverter.ToInt32(native, 4) != 1 || BitConverter.ToInt32(native, 8) != native.Length
                || (BitConverter.ToInt32(native, 12) != 1 && BitConverter.ToInt32(native, 12) != 3) || BitConverter.ToUInt32(native, 1188) != 0
                || BitConverter.ToUInt64(native, 80) != (ulong)expectedNative || BitConverter.ToInt32(native, 88) != AdvanceSequence
                || BitConverter.ToInt64(native, 256) != CaptureUnityFrameCount || observedUnityFrame != CaptureUnityFrameCount)
                throw new InvalidDataException("Frame clock observer native/Unity readback differs.");
        }
    }
}
