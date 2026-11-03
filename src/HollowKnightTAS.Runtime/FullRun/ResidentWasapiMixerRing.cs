using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // The caller owns output identity and producer admission. This is the CPU
    // ring only; bytes already submitted to the WASAPI device are not here.
    internal static class ResidentWasapiMixerRing
    {
        private const int MaxBytes = 32 * 1024 * 1024;

        internal sealed class Data
        {
            public int RenderFrames, BlockAlign, Format, Channels;
            public int RingFrames, BlockFrames, BlockCount, ReadFrame, WriteFrame;
            public byte[] Bytes = Array.Empty<byte>();
            public bool HasClient, HasRenderClient, HasFeeder, HasMixer;
        }

        internal static Data Capture(IntPtr output, bool feeder, bool mixer, Func<IntPtr, int, byte[]> read)
        {
            if (read == null) throw new ArgumentNullException(nameof(read));
            if (IntPtr.Size != 8 || !BitConverter.IsLittleEndian)
                throw Error("pinned Win64 architecture differs");
            var render = Read(output, 1128, 32, read);
            var ring = Read(output, 1872, 28, read);
            var result = new Data {
                HasClient = BitConverter.ToInt64(render, 0) != 0,
                HasRenderClient = BitConverter.ToInt64(render, 8) != 0,
                RenderFrames = BitConverter.ToInt32(render, 16),
                BlockAlign = BitConverter.ToInt32(render, 20),
                Format = BitConverter.ToInt32(render, 24),
                Channels = BitConverter.ToInt32(render, 28),
                RingFrames = BitConverter.ToInt32(ring, 8),
                BlockFrames = BitConverter.ToInt32(ring, 12),
                BlockCount = BitConverter.ToInt32(ring, 16),
                ReadFrame = BitConverter.ToInt32(ring, 20),
                WriteFrame = BitConverter.ToInt32(ring, 24),
                HasFeeder = feeder, HasMixer = mixer
            };
            int length = ValidateShape(result);
            result.Bytes = Read(new IntPtr(BitConverter.ToInt64(ring, 0)), 0, length, read);
            Validate(result);
            return result;
        }

        internal static void Validate(Data data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            int length = ValidateShape(data);
            if (data.Bytes == null || data.Bytes.Length != length)
                throw Error("ring byte extent differs");
        }

        private static int ValidateShape(Data data)
        {
            if (data.RenderFrames <= 0 || data.BlockAlign <= 0 || data.Format < 0 || data.Channels <= 0
                || data.RingFrames <= 0 || data.BlockFrames <= 0 || data.BlockCount <= 0)
                throw Error("ring dimensions differ");
            // initEx sets ringFrames = blockFrames * blockCount. feederUpdate
            // multiplies frame cursors by blockAlign and wraps by ringFrames.
            if ((long)data.BlockFrames * data.BlockCount != data.RingFrames
                || data.ReadFrame < 0 || data.ReadFrame >= data.RingFrames
                || data.WriteFrame < 0 || data.WriteFrame >= data.RingFrames)
                throw Error("ring frame extent or cursor differs");
            long length = (long)data.RingFrames * data.BlockAlign;
            if (length <= 0 || length > MaxBytes) throw Error("ring exceeds byte budget");
            return (int)length;
        }

        private static byte[] Read(IntPtr owner, int offset, int length, Func<IntPtr, int, byte[]> read)
        {
            long start = owner.ToInt64();
            if (start <= 0 || offset < 0 || length <= 0 || length > MaxBytes
                || start > long.MaxValue - offset || start + offset > long.MaxValue - length)
                throw Error("native read address or extent differs");
            var bytes = read(new IntPtr(start + offset), length);
            if (bytes == null || bytes.Length != length) throw Error("native read length differs");
            // Keep the DTO independent of any reusable reader scratch buffer.
            return (byte[])bytes.Clone();
        }

        private static InvalidDataException Error(string message)
            => new InvalidDataException("ResidentWasapiMixerRing: " + message);
    }
}
