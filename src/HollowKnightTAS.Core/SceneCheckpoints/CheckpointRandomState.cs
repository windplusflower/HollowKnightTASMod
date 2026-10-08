using System;
using System.IO;
using System.Linq;
using System.Text;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    // Shared with the early clock payload, which loads before the Core assembly.
    public sealed class CheckpointRandomState
    {
        private readonly int[] words;
        public CheckpointRandomState(int[] words, bool renderInitialized, bool snapshotAvailable,
            int resetCount, int sceneEpoch, int lastSeed, int advanceSequence, string boundary, string scene)
        {
            if (words == null || words.Length != 12 || resetCount < 0 || sceneEpoch < 0 || sceneEpoch > resetCount
                || advanceSequence < 0 || advanceSequence == int.MaxValue || boundary == null || scene == null
                || boundary.Length > 1024 || scene.Length > 1024)
                throw new InvalidDataException("Invalid checkpoint random/clock state.");
            this.words = (int[])words.Clone(); RenderInitialized = renderInitialized; SnapshotAvailable = snapshotAvailable;
            ResetCount = resetCount; SceneEpoch = sceneEpoch; LastSeed = lastSeed; AdvanceSequence = advanceSequence;
            Boundary = boundary; Scene = scene;
        }
        public bool RenderInitialized { get; }
        public bool SnapshotAvailable { get; }
        public int ResetCount { get; }
        public int SceneEpoch { get; }
        public int LastSeed { get; }
        public int AdvanceSequence { get; }
        public string Boundary { get; }
        public string Scene { get; }
        public int[] CopyWords() => (int[])words.Clone(); // gameplay, render, last scene seed snapshot
        public byte[] Encode()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(1); foreach (var word in words) writer.Write(word);
                writer.Write(RenderInitialized); writer.Write(SnapshotAvailable);
                writer.Write(ResetCount); writer.Write(SceneEpoch); writer.Write(LastSeed); writer.Write(AdvanceSequence);
                writer.Write(Boundary); writer.Write(Scene); writer.Flush(); return stream.ToArray();
            }
        }
        public static CheckpointRandomState Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 72 || bytes.Length > 8192) throw new InvalidDataException("Invalid random continuation size.");
            try
            {
                using (var stream = new MemoryStream(bytes, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    if (reader.ReadInt32() != 1) throw new InvalidDataException("Unsupported random continuation schema.");
                    var words = Enumerable.Range(0, 12).Select(_ => reader.ReadInt32()).ToArray();
                    var render = reader.ReadByte(); var snapshot = reader.ReadByte();
                    if (render > 1 || snapshot > 1) throw new InvalidDataException("Noncanonical random continuation flags.");
                    var result = new CheckpointRandomState(words, render == 1, snapshot == 1,
                        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadString(), reader.ReadString());
                    if (stream.Position != stream.Length || !result.Encode().SequenceEqual(bytes)) throw new InvalidDataException("Noncanonical random continuation.");
                    return result;
                }
            }
            catch (EndOfStreamException exception) { throw new InvalidDataException("Truncated random continuation.", exception); }
        }
    }
}
