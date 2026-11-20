using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read-only runtime-state inventory. These public views are not claimed to
    // close native animation/controller, module configuration or resource state.
    internal static class LoadingNativeComponentObservation
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(
            IntPtr process, IntPtr address, [Out] byte[] buffer, UIntPtr size, out UIntPtr read);
        private static bool engineValidated;
        private static readonly FieldInfo Pointer = typeof(UnityEngine.Object)
            .GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");

        internal static bool ParticleNeedsSync(ParticleSystem particles)
        {
            if (!engineValidated) { LoadingEngineClockProbe.RequirePinnedEngine(); engineValidated = true; }
            var pointer = (IntPtr)Pointer.GetValue(particles)!;
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("ParticleSystem native pointer is unavailable.");
            var flag = new byte[1];
            // Pinned PDB ParticleSystem::m_NeedsSync is at +108. The public
            // GetPlaybackState first clears this flag and can finalize work;
            // never invoke its mutating branch from a diagnostic observer.
            if (!ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 108), flag, (UIntPtr)1, out var read)
                || read.ToUInt64() != 1 || flag[0] > 1)
                throw new InvalidOperationException("P0 particle synchronization flag read failed.");
            return flag[0] != 0;
        }

        internal static object? Capture(Component component, Action<string> omit)
        {
            // Shared resource getters do not instantiate renderer materials or
            // meshes. Avoid bounds/culling queries and physical audio cursors:
            // they can depend on lazy native caches or the live DSP clock.
            if (component is Renderer renderer) return CaptureRenderer(renderer);
            if (component is MeshFilter meshFilter) return new { meshFilter.sharedMesh };
            if (component is CanvasGroup group)
                return new { group.alpha, group.interactable, group.blocksRaycasts, group.ignoreParentGroups };
            if (component is Canvas canvas)
                return new { canvas.renderMode, canvas.worldCamera, canvas.planeDistance, canvas.pixelPerfect,
                    canvas.overridePixelPerfect, canvas.scaleFactor, canvas.referencePixelsPerUnit,
                    canvas.sortingLayerID, canvas.sortingOrder, canvas.overrideSorting, canvas.targetDisplay,
                    canvas.additionalShaderChannels };
            if (component is CanvasRenderer uiRenderer)
            {
                if (uiRenderer.materialCount > 64 || uiRenderer.popMaterialCount > 64)
                    throw new InvalidOperationException("P0 CanvasRenderer material budget exceeded.");
                return new { uiRenderer.cull, uiRenderer.hasPopInstruction, color = uiRenderer.GetColor(),
                    materials = Enumerable.Range(0, uiRenderer.materialCount).Select(uiRenderer.GetMaterial).ToArray(),
                    popMaterials = Enumerable.Range(0, uiRenderer.popMaterialCount).Select(uiRenderer.GetPopMaterial).ToArray() };
            }
            if (component is Camera camera)
                return new { camera.clearFlags, camera.backgroundColor, camera.cullingMask, camera.depth,
                    camera.orthographic, camera.orthographicSize, camera.fieldOfView, camera.nearClipPlane,
                    camera.farClipPlane, camera.rect, camera.depthTextureMode, camera.renderingPath,
                    camera.useOcclusionCulling, camera.allowHDR, camera.allowMSAA, camera.eventMask,
                    camera.targetTexture, camera.targetDisplay };
            if (component is Light light)
                return new { light.type, light.color, light.intensity, light.range, light.spotAngle,
                    light.bounceIntensity, light.shadows, light.shadowStrength, light.shadowBias,
                    light.shadowNormalBias, light.shadowNearPlane, light.cookie, light.cookieSize,
                    light.cullingMask, light.renderMode };
            if (component is TextMesh text)
                return new { text.text, text.font, text.fontSize, text.fontStyle, text.color,
                    text.characterSize, text.anchor, text.alignment, text.richText, text.lineSpacing,
                    text.offsetZ, text.tabSize };
            if (component is UnityEngine.Rendering.SortingGroup sorting)
                return new { sorting.sortingLayerID, sorting.sortingOrder };
            if (component is AudioSource audio)
                return new { audio.clip, audio.outputAudioMixerGroup, audio.volume, audio.pitch, audio.panStereo,
                    audio.spatialBlend, audio.mute, audio.loop, audio.playOnAwake, audio.ignoreListenerPause,
                    audio.ignoreListenerVolume, audio.spatialize, audio.spatializePostEffects, audio.priority,
                    audio.dopplerLevel, audio.spread, audio.minDistance, audio.maxDistance, audio.rolloffMode,
                    audio.bypassEffects, audio.bypassListenerEffects, audio.bypassReverbZones,
                    audio.reverbZoneMix, audio.velocityUpdateMode };
            if (component is AudioListener) return new { AudioListener.volume, AudioListener.pause };
            if (component is Animator animator)
            {
                object? running = null;
                if (animator.isInitialized)
                {
                    if (animator.layerCount > 64 || animator.parameterCount > 512)
                        throw new InvalidOperationException("P0 Animator observation budget exceeded.");
                    var layers = Enumerable.Range(0, animator.layerCount).Select(index => new
                    {
                        index, weight = animator.GetLayerWeight(index),
                        current = animator.GetCurrentAnimatorStateInfo(index),
                        next = animator.GetNextAnimatorStateInfo(index),
                        transition = animator.GetAnimatorTransitionInfo(index),
                        inTransition = animator.IsInTransition(index),
                        currentClips = animator.GetCurrentAnimatorClipInfo(index)
                            .Select(clip => new { clip = clip.clip, clip.weight }).ToArray(),
                        nextClips = animator.GetNextAnimatorClipInfo(index)
                            .Select(clip => new { clip = clip.clip, clip.weight }).ToArray()
                    }).ToArray();
                    var parameters = animator.parameters.Select(parameter => new
                    {
                        parameter.name, parameter.type, parameter.defaultBool, parameter.defaultFloat, parameter.defaultInt,
                        value = Parameter(animator, parameter, omit)
                    }).ToArray();
                    running = new { layers, parameters };
                    omit("native-animator-hidden-controller-state");
                }
                return new { animator.isInitialized, animator.runtimeAnimatorController, animator.avatar,
                    animator.speed, animator.applyRootMotion, animator.updateMode, animator.cullingMode,
                    animator.fireEvents, animator.keepAnimatorControllerStateOnDisable, running };
            }
            if (component is ParticleSystem particles)
            {
                if (ParticleNeedsSync(particles))
                {
                    omit("native-particle-pending-sync-not-observed");
                    return new { needsSync = true };
                }
                var count = particles.particleCount;
                if (count > 65536) throw new InvalidOperationException("P0 particle observation budget exceeded.");
                var values = new ParticleSystem.Particle[count];
                if (particles.GetParticles(values) != count)
                    throw new InvalidOperationException("P0 particle count changed while copying state.");
                var custom1 = new List<Vector4>(); var custom2 = new List<Vector4>();
                particles.GetCustomParticleData(custom1, ParticleSystemCustomData.Custom1);
                particles.GetCustomParticleData(custom2, ParticleSystemCustomData.Custom2);
                if (custom1.Count > 65536 || custom2.Count > 65536)
                    throw new InvalidOperationException("P0 custom particle observation budget exceeded.");
                // With m_NeedsSync proved false, GetPlaybackState takes only
                // its copy branch. GetTrails allocates returned lists.
                return new { particles.isPlaying, particles.isPaused, particles.isStopped, particles.isEmitting,
                    particles.time, particles.randomSeed, particles.useAutoRandomSeed,
                    playback = particles.GetPlaybackState(), values, custom1, custom2,
                    trails = particles.GetTrails() };
            }
            return null;
        }

        private static object CaptureRenderer(Renderer renderer)
        {
            object? specific = null;
            if (renderer is SpriteRenderer sprite)
                specific = new { sprite.sprite, sprite.color, sprite.flipX, sprite.flipY, sprite.drawMode,
                    sprite.size, sprite.tileMode, sprite.adaptiveModeThreshold, sprite.spriteSortPoint,
                    sprite.maskInteraction };
            else if (renderer is ParticleSystemRenderer particle)
                specific = new { particle.renderMode, particle.sortMode, particle.alignment, particle.sortingFudge,
                    particle.minParticleSize, particle.maxParticleSize, particle.normalDirection,
                    particle.lengthScale, particle.velocityScale, particle.cameraVelocityScale,
                    particle.mesh, particle.meshCount, particle.trailMaterial };
            else if (renderer is MeshRenderer mesh)
                specific = new { mesh.additionalVertexStreams };
            return new { renderer.enabled, renderer.forceRenderingOff, renderer.sharedMaterials, renderer.shadowCastingMode, renderer.receiveShadows,
                renderer.lightProbeUsage, renderer.reflectionProbeUsage, renderer.sortingLayerID,
                renderer.sortingOrder, renderer.lightmapIndex, renderer.lightmapScaleOffset,
                renderer.realtimeLightmapIndex, renderer.realtimeLightmapScaleOffset, renderer.probeAnchor,
                renderer.lightProbeProxyVolumeOverride, renderer.motionVectorGenerationMode,
                renderer.allowOcclusionWhenDynamic, specific };
        }

        private static object Parameter(Animator animator, AnimatorControllerParameter parameter, Action<string> omit)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float: return animator.GetFloat(parameter.nameHash);
                case AnimatorControllerParameterType.Int: return animator.GetInteger(parameter.nameHash);
                case AnimatorControllerParameterType.Bool: return animator.GetBool(parameter.nameHash);
                case AnimatorControllerParameterType.Trigger:
                    omit("native-animator-trigger-latch"); return "unobserved-trigger";
                default: throw new InvalidOperationException("Unknown Animator parameter type.");
            }
        }
    }
}
