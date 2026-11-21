using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Only control scalars and method/asset metadata, never a saved D packet.
    internal sealed class CanonicalPersistentSpriteAnimationDiagnostic : IDisposable
    {
        internal const string Flag = "HKTAS_LOADING_PERSISTENT_SPRITE_ANIMATION_AUDIT";
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static CanonicalPersistentSpriteAnimationDiagnostic? current;
        private readonly SortedDictionary<string, tk2dSprite> owners;
        private readonly List<object> records = new List<object>();
        private readonly string role;
        private bool disposed;
        private static object? Read(Type type, object? owner, string name) => (type.GetField(name, Fields)
            ?? throw new MissingFieldException(type.FullName, name)).GetValue(owner);
        private static string Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        private static object Method(MethodBase method) => new { module = method.Module.ModuleVersionId.ToString("D"),
            token = method.DeclaringType == null ? 0 : method.MetadataToken, owner = method.DeclaringType?.FullName, method.Name };
        private static object[] Callbacks(Delegate? value)
        {
            var calls = value?.GetInvocationList() ?? Array.Empty<Delegate>();
            if (calls.Length > 8) throw new InvalidDataException("Sprite diagnostic callback bound differs.");
            return calls.Select(call => new { method = Method(call.Method), targetType = call.Target?.GetType().FullName,
                targetIsNull = ReferenceEquals(call.Target, null), targetLive = call.Target is UnityEngine.Object native ? (bool?)(native != null) : null }).Cast<object>().ToArray();
        }
        private static object Collection(tk2dSpriteCollectionData? value) => value == null ? new { absent = true } : (object)new {
            value.name, value.spriteCollectionGUID, module = value.GetType().Module.ModuleVersionId.ToString("D") };
        private static readonly Dictionary<(tk2dSpriteAnimationClip clip, string fpsBits), string> ClipHashCache = new();
        internal static string? ClipHash(tk2dSpriteAnimationClip? clip)
        {
            if (clip == null) return null;
            var fpsBits = Bits(clip.fps);
            var cacheKey = (clip, fpsBits);
            if (ClipHashCache.TryGetValue(cacheKey, out var cached)) return cached;
            if (clip.frames == null || clip.frames.Length > 4096 || clip.frames.Any(frame => frame == null || frame.eventInfo?.Length > 1024))
                throw new InvalidDataException("Sprite diagnostic clip metadata bounds differ.");
            var text = JsonConvert.SerializeObject(new { clip.name, fps = fpsBits, clip.loopStart, wrapMode = (int)clip.wrapMode,
                frames = clip.frames.Select(frame => new { collection = frame.spriteCollection == null ? null : frame.spriteCollection.spriteCollectionGUID,
                    frame.spriteId, frame.triggerEvent, frame.eventInfo, frame.eventInt, eventFloat = Bits(frame.eventFloat) }).ToArray() });
            var hash = HollowKnightTAS.Core.Cryptography.Sha256Utility.ComputeHex(System.Text.Encoding.UTF8.GetBytes(text));
            ClipHashCache[cacheKey] = hash;
            return hash;
        }
        private object Snapshot(string name, tk2dSprite sprite)
        {
            var animators = sprite.GetComponents<tk2dSpriteAnimator>();
            if (animators.Length != 1 || animators[0].GetType() != typeof(tk2dSpriteAnimator))
                throw new InvalidDataException("Sprite diagnostic original animator directory differs.");
            var animator = animators[0]; var type = typeof(tk2dSpriteAnimator);
            var library = (tk2dSpriteAnimation?)Read(type, animator, "library");
            var clip = (tk2dSpriteAnimationClip?)Read(type, animator, "currentClip");
            var clips = library == null ? Array.Empty<tk2dSpriteAnimationClip>() : library.clips;
            if (clips == null || clips.Length > 4096 || clip?.frames?.Length > 4096)
                throw new InvalidDataException("Sprite diagnostic original clip bounds differ.");
            var matches = Enumerable.Range(0, clips.Length).Where(index => ReferenceEquals(clips[index], clip)).ToArray();
            var scale = (Vector3)Read(typeof(tk2dBaseSprite), sprite, "_scale")!;
            var color = (Color)Read(typeof(tk2dBaseSprite), sprite, "_color")!;
            return new { owner = name, frameCount = Time.frameCount, deltaBits = Bits(Time.deltaTime), sprite.enabled,
                active = sprite.gameObject.activeInHierarchy, spriteId = Read(typeof(tk2dBaseSprite), sprite, "_spriteId"),
                arrow = !CanonicalBorderArrowCarry.OwnerKeys.Contains(name, StringComparer.Ordinal) ? null : (object)new {
                    nativeSignature = CanonicalBorderArrowCarry.NativeSignature(name),
                    upAnimation = Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "upAnimation"),
                    downAnimation = Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "downAnimation"),
                    upDelayBits = Bits((float)Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "upDelay")!),
                    randomStartFrameSpriteMax = Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "randomStartFrameSpriteMax"),
                    timerBits = Bits((float)Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "timer")!),
                    animatingDown = Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "animatingDown"),
                    readyingAnimUp = Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "readyingAnimUp"),
                    animatorAlias = ReferenceEquals(Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "spriteAnimator"), animator),
                    rendererAlias = ReferenceEquals(Read(typeof(InvAnimateUpAndDown), CanonicalBorderArrowCarry.Animation(name), "meshRenderer"), sprite.GetComponent<MeshRenderer>()) },
                collection = Collection((tk2dSpriteCollectionData?)Read(typeof(tk2dBaseSprite), sprite, "collection")),
                collectionInst = Collection((tk2dSpriteCollectionData?)Read(typeof(tk2dBaseSprite), sprite, "collectionInst")),
                scale = new[] { Bits(scale.x), Bits(scale.y), Bits(scale.z) }, color = new[] { Bits(color.r), Bits(color.g), Bits(color.b), Bits(color.a) },
                animator = new { animator.enabled, animator.playAutomatically, library = library == null ? null : library.name,
                    libraryClipCount = clips.Length, clipIndices = matches, clipHash = ClipHash(clip), clip = clip == null ? null : new { clip.name, fpsBits = Bits(clip.fps),
                        wrapMode = (int)clip.wrapMode, clip.loopStart, frameCount = clip.frames?.Length ?? -1,
                        eventFrameCount = clip.frames?.Count(frame => frame.triggerEvent) ?? -1 },
                    clipTimeBits = Bits((float)Read(type, animator, "clipTime")!), clipFpsBits = Bits((float)Read(type, animator, "clipFps")!),
                    previousFrame = Read(type, animator, "previousFrame"), state = Convert.ToInt32(Read(type, animator, "state")),
                    globalState = Convert.ToInt32(Read(type, null, "globalState")), defaultClipId = Read(type, animator, "defaultClipId"),
                    spriteAlias = ReferenceEquals(Read(type, animator, "_sprite"), sprite),
                    complete = Callbacks(animator.AnimationCompleted), events = Callbacks(animator.AnimationEventTriggered) },
                fsms = sprite.GetComponents<PlayMakerFSM>().Select(owner => {
                    var raw = (Fsm?)Read(typeof(PlayMakerFSM), owner, "fsm");
                    var active = raw == null ? null : (FsmState?)Read(typeof(Fsm), raw, "activeState");
                    return new { owner.enabled, name = raw?.Name, started = raw?.Started, state = active?.Name };
                }).ToArray() };
        }
        private void RecordAll(string stage, long? movie = null, long? native = null)
        {
            records.Add(new { stage, movie, native, owners = owners.Select(pair => Snapshot(pair.Key, pair.Value)).ToArray() });
            if (records.Count > 32) throw new InvalidDataException("Sprite diagnostic record bound exceeded.");
        }
        private CanonicalPersistentSpriteAnimationDiagnostic(string role, long movie, long native)
        {
            this.role = role;
            var all = CanonicalPersistentSpriteDirectory.Resolve();
            owners = new SortedDictionary<string, tk2dSprite>(StringComparer.Ordinal);
            foreach (var key in CanonicalPersistentSpriteContinuation.Keys) owners.Add(key, all[key]);
            var actualMvid = typeof(tk2dSprite).Module.ModuleVersionId.ToString("D");
            if (actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("SpriteAnimDiag:Mvid:" + actualMvid,
                    "[HKTAS] tk2dSprite ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            RecordAll("B-before-D", movie, native);
        }
        internal static void Arm(string role, long movie, long native)
        {
            if (Environment.GetEnvironmentVariable(Flag) != "1") return;
            if (!CanonicalActualDExperiment.Enabled || !CanonicalPhaseOneTrace.Enabled || current != null)
                throw new InvalidOperationException("Sprite diagnostic requires one protected B-to-first-K3 context.");
            current = new CanonicalPersistentSpriteAnimationDiagnostic(role, movie, native);
        }
        internal static void DReadback(long movie, long native) => current?.RecordAll("D-readback", movie, native);
        internal static void SharedIdentity(string stage, Func<object> readMetadata)
        {
            if (current == null) return;
            var json = JsonConvert.SerializeObject(new { diagnosticOnly = true, role = current.role, stage, metadata = readMetadata(),
                sameProcessReferenceEqualsOnly = true, savedDBytesIncluded = false });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) throw new InvalidDataException("Persistent sprite shared identity report exceeds its bound.");
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "persistent-sprite-shared-identity-diagnostic.json"), json);
        }
        internal static void FirstK3(long movie, long native)
        {
            if (current == null) return;
            current.RecordAll("first-K3", movie, native); current.Finish(true);
        }
        internal static void Abort() => current?.Finish(false);
        private void Finish(bool complete)
        {
            if (disposed) return;
            disposed = true;
            var json = JsonConvert.SerializeObject(new { diagnosticOnly = true, complete, role, ownerCount = owners.Count, records,
                noAnimationHooksOrWrites = true, completeStateCoverage = false });
            if (System.Text.Encoding.UTF8.GetByteCount(json) > 512 * 1024) throw new InvalidDataException("Sprite diagnostic output bound exceeded.");
            File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("HKTAS_LOADING_PROBE_OUTPUT")!, "persistent-sprite-animation-diagnostic.json"), json);
            current = null;
        }
        public void Dispose() => Finish(false);
    }
}
