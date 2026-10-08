using System;
using HollowKnightTAS.Core.Media;
using Unity.Collections;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Media
{
    /// <summary>Main-thread-only offline mixer and completed backbuffer reader.</summary>
    public sealed class UnityFrameCapture : IDisposable
    {
        private readonly VideoExportFormat format;
        private readonly VariableVideoTimeline timeline = new VariableVideoTimeline();
        private readonly AudioBlockReframer audioBlocks;
        private readonly Func<double> frameDuration;
        private bool recordingAudio;
        public int LastAudioSampleFrames { get; private set; }
        public float MaximumAudioPeak { get; private set; }
        public int DspBlockSampleFrames { get; }
        public double LastFrameDuration { get; private set; }

        public UnityFrameCapture(VideoExportFormat format, Func<double>? frameDuration = null)
        {
            this.format = format;
            // v2 supplies the native duration driving Unity's gameplay clock.
            // Both paths only observe their clock; video export never sets it.
            this.frameDuration = frameDuration ?? (() => Time.captureDeltaTime);
            ValidateConfiguration();
            AudioSettings.GetDSPBufferSize(out var blockSize, out _);
            DspBlockSampleFrames = blockSize;
            audioBlocks = new AudioBlockReframer(blockSize, format.Channels);
            if (!AudioRenderer.Start()) throw new InvalidOperationException("Unity offline audio capture could not start.");
            recordingAudio = true;
        }

        public void Capture(long frameIndex, out byte[] rgb, out byte[] pcm)
        {
            if (!recordingAudio) throw new ObjectDisposedException(nameof(UnityFrameCapture));
            ValidateConfiguration();
            LastAudioSampleFrames = AudioRenderer.GetSampleCountForCaptureFrame();
            LastFrameDuration = frameDuration();
            var expectedValues = timeline.Advance(LastFrameDuration, format.SampleRate, format.Channels);
            // Unity renders only complete DSP blocks, not arbitrary video-frame-sized buffers.
            // GetSampleCountForCaptureFrame rounds its accumulator down to whole blocks and
            // can legitimately return zero. Prefetch at most one block, retain the remainder,
            // and call Render once per game frame (including a zero-length drain).
            var renderValues = audioBlocks.RequiredRenderValues(expectedValues);
            using (var buffer = new NativeArray<float>(renderValues, Allocator.Temp))
            {
                if (!AudioRenderer.Render(buffer)) throw new InvalidOperationException("Unity offline audio render failed.");
                var floats = audioBlocks.Consume(buffer.ToArray(), expectedValues);
                foreach (var sample in floats) MaximumAudioPeak = Math.Max(MaximumAudioPeak, Math.Abs(sample));
                pcm = new byte[checked(floats.Length * sizeof(float))];
                Buffer.BlockCopy(floats, 0, pcm, 0, pcm.Length);
            }

            var texture = ScreenCapture.CaptureScreenshotAsTexture();
            if (texture == null) throw new InvalidOperationException("Completed game backbuffer is unavailable.");
            try
            {
                if (texture.width != format.Width || texture.height != format.Height)
                    throw new InvalidOperationException("Game backbuffer dimensions changed during export.");
                var pixels = texture.GetPixels32();
                rgb = new byte[format.VideoFrameBytes];
                for (var i = 0; i < pixels.Length; i++)
                {
                    rgb[i * 3] = pixels[i].r;
                    rgb[i * 3 + 1] = pixels[i].g;
                    rgb[i * 3 + 2] = pixels[i].b;
                }
            }
            finally { UnityEngine.Object.Destroy(texture); }
        }

        public void Dispose()
        {
            if (!recordingAudio) return;
            recordingAudio = false;
            if (!AudioRenderer.Stop()) throw new InvalidOperationException("Unity offline audio capture could not stop.");
        }

        private void ValidateConfiguration()
        {
            if (Screen.width != format.Width || Screen.height != format.Height)
                throw new InvalidOperationException("Export resolution must match the current game backbuffer.");
            if (AudioSettings.outputSampleRate != format.SampleRate
                || (format.Channels == 2 ? AudioSettings.speakerMode != AudioSpeakerMode.Stereo
                    : AudioSettings.speakerMode != AudioSpeakerMode.Mono))
                throw new InvalidOperationException("Export audio format must match Unity's output configuration.");
            var observedDuration = frameDuration();
            if (double.IsNaN(observedDuration) || double.IsInfinity(observedDuration)
                || observedDuration <= 0)
                throw new InvalidOperationException("Game clock reported an invalid frame duration: " + observedDuration);
        }
    }
}
