using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using MonoMod.RuntimeDetour;
using MonoMod.Cil;
using Mono.Cecil.Cil;
using UnityEngine;
using UnityEngine.Video;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Protected full runs only. Opening, in-scene videos, travel and endings
    // share this factory. Decoder I/O cannot own gameplay completion events.
    internal sealed class DeterministicCinematics : IDisposable
    {
        private static readonly List<ClockedPlayer> players = new List<ClockedPlayer>();
        internal static string TraceState => string.Join(";", players.Select(p => p.Describe()));
        internal static string? Failure { get; private set; }
        internal static double FrameDuration { get; set; } = 0.02;
        private readonly Hook factory;
        private readonly ILHook polling;
        private static readonly FieldInfo PlayerVideo = typeof(CinematicPlayer).GetField("cinematicVideoPlayer",
            BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingFieldException("CinematicPlayer.cinematicVideoPlayer");

        public DeterministicCinematics()
        {
            Failure = null;
            factory = new Hook(typeof(CinematicVideoPlayer).GetMethod("Create",
                BindingFlags.Public | BindingFlags.Static),
                new Func<Func<CinematicVideoPlayerConfig, CinematicVideoPlayer>,
                    CinematicVideoPlayerConfig, CinematicVideoPlayer>(Create));
            try
            {
                polling = new ILHook(typeof(CinematicPlayer).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic), il =>
                {
                    var cursor = new ILCursor(il);
                    if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchCall(typeof(Time), "get_frameCount")))
                        throw new InvalidOperationException("Cinematic polling phase is unavailable.");
                    // Preserve branch targets attached to the original call.
                    cursor.Next.OpCode = OpCodes.Ldarg_0;
                    cursor.Next.Operand = null;
                    cursor.Index++;
                    cursor.EmitDelegate<Func<CinematicPlayer, int>>(owner =>
                        (PlayerVideo.GetValue(owner) as ClockedPlayer)?.PollPhase ?? 0);
                });
            }
            catch { factory.Dispose(); throw; }
        }

        private static CinematicVideoPlayer Create(
            Func<CinematicVideoPlayerConfig, CinematicVideoPlayer> original,
            CinematicVideoPlayerConfig config)
        {
            var player = original(config);
            try { return new ClockedPlayer(config, player); }
            catch (Exception error) { Failure = error.Message; player.Dispose(); throw; }
        }

        public void Dispose() { polling.Dispose(); factory.Dispose(); }

        private sealed class ClockedPlayer : CinematicVideoPlayer
        {
            private readonly CinematicVideoPlayer original;
            private readonly VideoPlayer video;
            private readonly CinematicPlaybackClock clock;
            internal int PollPhase { get; private set; }

            public ClockedPlayer(CinematicVideoPlayerConfig config, CinematicVideoPlayer original)
                : base(config)
            {
                this.original = original;
                video = config.MeshRenderer.GetComponent<VideoPlayer>();
                var clip = video == null ? null : video.clip;
                // The supported Windows factory uses an embedded-clip backend,
                // despite its XB1CinematicVideoPlayer class name.
                if (video == null || clip == null || clip.frameRate <= 0 || clip.frameCount == 0)
                    throw new InvalidOperationException("Cinematic has no deterministic clip duration.");
                var duration = clip.frameCount / clip.frameRate;
                clock = new CinematicPlaybackClock(duration);
                video.timeReference = VideoTimeReference.ExternalTime;
                video.externalReferenceTime = 0;
                video.prepareCompleted += Prepared;
                video.errorReceived += Error;
                players.Add(this);
                Modding.Logger.LogDebug("[HKTAS] Clocked cinematic: " + clip.name + " duration=" + duration);
            }

            // Vanilla's embedded backend also returns false during asynchronous
            // preparation. Preserve its loading and queued-Play contract.
            public override bool IsLoading => false;
            public override bool IsPlaying => clock.Playing;
            public override bool IsLooping { get => original.IsLooping; set => original.IsLooping = value; }
            public override float Volume { get => original.Volume; set => original.Volume = value; }
            public override float CurrentTime => (float)clock.Elapsed;
            internal string Describe() => video.clip.name + ":" + clock.Elapsed.ToString("R", CultureInfo.InvariantCulture)
                + ":" + clock.Playing + ":" + IsLooping;

            public override void Play()
            {
                if (clock.Playing) return;
                clock.Play(Time.frameCount);
                PollPhase = 0;
                video.externalReferenceTime = 0;
                original.Play();
            }

            public override void Stop()
            {
                clock.Stop();
                original.Stop();
            }

            public override void Update()
            {
                original.Update();
                // Retain vanilla's ten-update completion polling, anchored to
                // this clip rather than a global frame count shifted by loading.
                PollPhase = (PollPhase + 1) % 10;
                if (!clock.Playing) return;
                clock.Update(Time.frameCount, FrameDuration, IsLooping);
                if (!clock.Playing) { original.Stop(); return; }
                video.externalReferenceTime = clock.Elapsed;
            }

            private void Prepared(VideoPlayer source)
            {
                // Catch up presentation after I/O without restarting the
                // simulation's clip clock. Vanilla still owns video and audio.
                source.externalReferenceTime = clock.Elapsed;
                if (clock.Playing && clock.Elapsed > 0 && source.canSetTime) source.time = clock.Elapsed;
                // A completed Prepare may precede the first Play. Stopping here
                // would discard preparation and strand vanilla's queued Play.
            }

            private void Error(VideoPlayer source, string message) => Failure = "Video decode failed: " + message;

            public override void Dispose()
            {
                clock.Stop();
                players.Remove(this);
                if (video != null)
                {
                    video.prepareCompleted -= Prepared;
                    video.errorReceived -= Error;
                }
                original.Dispose();
            }
        }
    }
}
