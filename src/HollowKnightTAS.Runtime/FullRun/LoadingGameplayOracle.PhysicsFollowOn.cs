using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingGameplayOracle
    {
        private readonly bool physicsFollowOn;
        private LoadingContactImpulseProbe? contactImpulseProbe;
        internal void PhysicsBeforeFrame(long frame) => contactImpulseProbe?.BeforeFrame(frame);
        internal bool CompletedSuccessfully { get; private set; }

        internal static LoadingGameplayOracle CreatePhysicsFollowOn(string directory, string sessionDirectory,
            long inputOrigin, Func<long> movie, Func<long> native)
        {
            Directory.CreateDirectory(directory);
            var oracle = new LoadingGameplayOracle(directory, 1703, 1706, movie, native, () => "not-observed",
                inputNativeOrigin: inputOrigin, suffixCapture: true, identitySessionDirectory: sessionDirectory,
                physicsFollowOn: true);
            oracle.contactImpulseProbe = LoadingContactImpulseProbe.Create();
            return oracle;
        }

        private void SamplePhysicsFollowOn(string boundary)
        {
            try {
                var elapsed = Stopwatch.StartNew();
                unityIds.Clear(); sceneObjectOwners.Clear(); sceneRoots.Clear(); gameObjectPaths.Clear();
                observationIdentity.Refresh(); nativeValues.BeginSample();
                var bodies = Resources.FindObjectsOfTypeAll<Rigidbody2D>().Where(body => body != null && Loaded(body.gameObject)).ToArray();
                var colliders = Resources.FindObjectsOfTypeAll<Collider2D>().Where(collider => collider != null && Loaded(collider.gameObject)).ToArray();
                if (bodies.Length > 16000 || colliders.Length > 16000) throw new InvalidOperationException("Physics follow-on root budget exceeded.");
                var digest = nativePhysics!.Capture(bodies, colliders, UnityId, fields);
                var impulses = contactImpulseProbe?.Read(nativePhysics);
                Write(new { kind = "sample", movieFrame = movieFrame(), nativeFrame = nativeFrame(), phase = "after", boundary,
                    digest, nativePhysics = nativePhysics.Inventory, nativePhysicsGroups = nativePhysics.Groups,
                    nativeContactRawEvidence = nativePhysics.ContactRawEvidence, unsupported = nativePhysics.Unsupported,
                    contactImpulseCallbacks = impulses,
                    completeStateCoverage = false, captureMilliseconds = elapsed.ElapsedMilliseconds });
                gameplaySamples++;
            } catch (Exception error) { SealPhysicsFollowOn(false, error.GetType().Name + ": " + error.Message); throw; }
        }

        private void SealPhysicsFollowOn(bool success, string detail)
        {
            if (sealedTrace) return;
            sealedTrace = true;
            try {
                contactImpulseProbe?.Stop();
                Write(new { kind = "footer", success, detail, samples = gameplaySamples, first, end, completeStateCoverage = false });
                var bytes = buffer.ToArray();
                if (bytes.Length > 16 * 1024 * 1024) throw new InvalidOperationException("Physics follow-on output exceeds 16 MiB.");
                File.WriteAllBytes(Path.Combine(directory, "physics-follow-on.jsonl"), bytes);
                CompletedSuccessfully = success;
            } finally { fields.Dispose(); nativeValues.Dispose(); buffer.Dispose(); suffixRandomTimeline.Dispose(); }
            if (!success) throw new InvalidOperationException("Physics follow-on observer failed: " + detail);
        }
    }
}
