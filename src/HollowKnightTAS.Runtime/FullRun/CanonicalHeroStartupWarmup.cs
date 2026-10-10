using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // R3 uses actual Unity activation and PlayerLoops. Never invoke Start or
    // advance an observed iterator manually, and never write lifecycle flags.
    internal sealed class CanonicalHeroStartupWarmup : IDisposable
    {
        private delegate void OriginalStart(HeroController hero);
        private readonly Hook startHook;
        private readonly Action<HeroController>? beforeHeroStart;
        private HeroController? hero;
        private GameObject? crack;
        private bool started, complete;
        private int firstFrame;
        private Vector3 position, scale;
        private Quaternion rotation;
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Type SetZIterator = typeof(SetZ).GetNestedTypes(BindingFlags.NonPublic)
            .Single(type => type.Name.StartsWith("<SetPosition>", StringComparison.Ordinal));
        private static readonly FieldInfo SetZOwner = SetZIterator.GetField("<>4__this", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
            byte[] bytes, UIntPtr size, out UIntPtr read);

        internal CanonicalHeroStartupWarmup(Action<HeroController>? beforeHeroStart = null)
        {
            this.beforeHeroStart = beforeHeroStart;
            if (HeroController.SilentInstance != null) throw new InvalidOperationException("Canonical Hero startup hook was installed too late.");
            var start = typeof(HeroController).GetMethod("Start", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("HeroController.Start");
            startHook = new Hook(start, (Action<OriginalStart, HeroController>)Start);
        }

        private void Start(OriginalStart original, HeroController value)
        {
            if (value == HeroController.SilentInstance) beforeHeroStart?.Invoke(value);
            original(value);
            if (value != HeroController.SilentInstance) return;
            if (hero != null) throw new InvalidOperationException("Canonical startup cannot bind a second Hero.");
            hero = value;
            // Returning to the title may destroy the previous Hero. A later
            // original pickup owns its own once-only lifecycle warmup.
            crack = null; started = false; complete = false;
        }

        // Called before the first source unload. The loader remains locked and
        // normal readiness suppresses Movie input during every warmup loop.
        internal bool Advance()
        {
            if (complete || hero == null) return true;
            if (!started)
            {
                var effect = hero.damageEffectFSM;
                crack = effect?.FsmVariables.FindFsmGameObject("Hit Crack")?.Value;
                if (effect == null || effect.FsmName != "Knight Damage" || crack == null || crack.name != "Hit Crack"
                    || crack.transform.parent != effect.transform || crack.transform.root != hero.transform || crack.activeSelf
                    || !crack.GetComponents<Component>().Select(component => component.GetType()).OrderBy(type => type.FullName, StringComparer.Ordinal)
                        .SequenceEqual(new[] { typeof(Transform), typeof(SpriteRenderer), typeof(Animator), typeof(DeactivateAfterDelay), typeof(RandomRotationRange) }
                            .OrderBy(type => type.FullName, StringComparer.Ordinal))
                    || crack.GetComponent<Animator>().keepAnimatorControllerStateOnDisable)
                    throw new InvalidOperationException("Canonical startup Hit Crack binding differs.");
                position = crack.transform.localPosition; rotation = crack.transform.localRotation; scale = crack.transform.localScale;
                started = true; firstFrame = Time.frameCount;
                crack.SetActive(true);
                if (!crack.activeInHierarchy) throw new InvalidOperationException("Canonical startup Hit Crack owner is inactive.");
                Modding.Logger.Log("[HKTAS] Canonical startup warming Hit Crack and waiting for Hero SetZ.");
                return false;
            }
            if (Time.frameCount - firstFrame > 120) throw new InvalidOperationException("Canonical Hero warmup exceeded 120 native frames.");
            var rotationScript = crack!.GetComponent<RandomRotationRange>();
            var pointer = (IntPtr)CachedPointer.GetValue(rotationScript)!;
            var lifecycle = new byte[5];
            LoadingEngineClockProbe.RequirePinnedEngine();
            if (pointer == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), lifecycle,
                new UIntPtr(5), out var read) || read.ToUInt64() != 5 || lifecycle.Any(value => value > 1) || lifecycle[3] != 0)
                throw new InvalidOperationException("Canonical Hero startup lifecycle layout differs.");
            if (lifecycle[0] != 1 || lifecycle[1] != 1) return false;
            if (crack.activeSelf) return false;
            var scheduler = new LoadingNativeSchedulerDigest();
            try
            {
                if (scheduler.CaptureWaits(iterator => iterator.GetType() == SetZIterator
                    && SetZOwner.GetValue(iterator) is SetZ owner && owner.gameObject == hero.gameObject, 1, "Startup Hero SetZ").Length != 0)
                    return false;
            }
            finally { scheduler.Release(); }
            crack.transform.localPosition = position; crack.transform.localRotation = rotation; crack.transform.localScale = scale;
            complete = true;
            Modding.Logger.Log("[HKTAS] Canonical startup warmup complete; actual Start/SetZ finished without Movie input.");
            return true;
        }

        internal bool CompleteFor(HeroController? value) => value == null || complete && value == hero;
        public void Dispose() => startHook.Dispose();
    }
}
