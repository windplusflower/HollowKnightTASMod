using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    [Serializable]
    internal sealed class CheckpointHeroHitCrackState
    {
        public Dictionary<string, string> RotationFields = null!, DeactivationFields = null!;
        public byte[] Lifecycle = null!, DeactivationLifecycle = null!;
        public bool RotationEnabled, DeactivationEnabled;
        public Vector3 LocalPosition, LocalScale;
        public Quaternion LocalRotation;
        private static readonly FieldInfo CachedPointer = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo[] RotationScalars = CheckpointScalarFields.Declared(typeof(RandomRotationRange));
        private static readonly FieldInfo[] DeactivationScalars = CheckpointScalarFields.Declared(typeof(DeactivateAfterDelay)).Where(f => f.FieldType.IsPrimitive || f.FieldType == typeof(Vector3)).ToArray();
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
        private static bool unsupportedWarned;

        private static void WarnOnce(string message)
        {
            if (!unsupportedWarned)
            {
                unsupportedWarned = true;
                Modding.Logger.LogWarn("[HKTAS] " + message);
            }
        }

        private static bool TryBind(HeroController hero, out RandomRotationRange? rotation)
        {
            rotation = null;
            var effect = hero.damageEffectFSM;
            var go = effect?.FsmVariables.FindFsmGameObject("Hit Crack")?.Value;
            if (effect == null || effect.FsmName != "Knight Damage" || go == null || go.name != "Hit Crack"
                || go.transform.parent != effect.transform || go.transform.root != hero.transform || go.activeSelf || go.activeInHierarchy
                || !go.GetComponents<Component>().Select(c => c.GetType()).OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .SequenceEqual(new[] { typeof(Transform), typeof(SpriteRenderer), typeof(Animator), typeof(DeactivateAfterDelay), typeof(RandomRotationRange) }
                        .OrderBy(t => t.FullName, StringComparer.Ordinal))
                || go.GetComponent<Animator>().keepAnimatorControllerStateOnDisable)
            {
                WarnOnce("Hit Crack component binding differs from expected layout; skipping Hit Crack native lifecycle.");
                return false;
            }
            rotation = go.GetComponent<RandomRotationRange>();
            return rotation != null;
        }
        private static IntPtr LifecycleAddress(MonoBehaviour component)
        {
            var pointer = (IntPtr)CachedPointer.GetValue(component)!;
            if (pointer == IntPtr.Zero) return IntPtr.Zero;
            // Hash-pinned Unity/PDB MonoBehaviour (312 bytes): m_DidAwake
            // +292, m_DidStart +293, m_IsDestroying +295, m_AddedToManager +296.
            return IntPtr.Add(pointer, 292);
        }
        private static bool TryReadLifecycle(MonoBehaviour component, out byte[] lifecycle)
        {
            lifecycle = new byte[] { 0, 0 };
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnOnce("Skipping Hit Crack native lifecycle read on unmatched UnityPlayer.dll.");
                return false;
            }
            var address = LifecycleAddress(component);
            var bytes = new byte[5];
            if (address == IntPtr.Zero
                || !ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr(5), out var read) || read.ToUInt64() != 5
                || bytes.Any(b => b > 1) || bytes[1] > bytes[0] || bytes[3] != 0 || bytes[4] != 0)
            {
                WarnOnce("Hit Crack native lifecycle layout differs (" + component.GetType().FullName + "); using fallback observation.");
                return false;
            }
            lifecycle = bytes.Take(2).ToArray();
            return true;
        }
        internal static CheckpointHeroHitCrackState Capture(HeroController hero)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            if (!TryBind(hero, out var component) || component == null)
            {
                return new CheckpointHeroHitCrackState
                {
                    RotationFields = new Dictionary<string, string>(StringComparer.Ordinal),
                    DeactivationFields = new Dictionary<string, string>(StringComparer.Ordinal),
                    Lifecycle = new byte[] { 0, 0 },
                    DeactivationLifecycle = new byte[] { 0, 0 }
                };
            }
            var deactivation = component.GetComponent<DeactivateAfterDelay>();
            TryReadLifecycle(component, out var life);
            TryReadLifecycle(deactivation, out var deactivationLife);
            Modding.Logger.Log("[HKTAS] Captured Hit Crack lifecycle: awake=" + life[0] + " start=" + life[1]);
            return new CheckpointHeroHitCrackState { RotationFields = CheckpointScalarFields.Capture(component, RotationScalars),
                DeactivationFields = CheckpointScalarFields.Capture(deactivation, DeactivationScalars), Lifecycle = life,
                DeactivationLifecycle = deactivationLife, RotationEnabled = component.enabled, DeactivationEnabled = deactivation.enabled,
                LocalPosition = component.transform.localPosition, LocalRotation = component.transform.localRotation, LocalScale = component.transform.localScale };
        }
        internal Action PrepareApply(HeroController hero)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            if (!TryBind(hero, out var component) || component == null) return () => { };
            var deactivationComponent = component.GetComponent<DeactivateAfterDelay>();
            if (RotationEnabled != component.enabled || DeactivationEnabled != deactivationComponent.enabled
                || new[] { LocalPosition.x, LocalPosition.y, LocalPosition.z, LocalRotation.x, LocalRotation.y, LocalRotation.z, LocalRotation.w,
                    LocalScale.x, LocalScale.y, LocalScale.z }.Any(f => float.IsNaN(f) || float.IsInfinity(f)))
            {
                WarnOnce("Checkpoint Hit Crack pose or enabled state differs; skipping Hit Crack restore.");
                return () => { };
            }
            var rotation = CheckpointScalarFields.PrepareApply(component, RotationScalars, RotationFields);
            var deactivation = CheckpointScalarFields.PrepareApply(deactivationComponent, DeactivationScalars, DeactivationFields);
            byte[] current = new byte[] { 0, 0 };
            var capturedLifecycle = Lifecycle ?? Array.Empty<byte>();
            var capturedDeactivationLifecycle = DeactivationLifecycle ?? Array.Empty<byte>();
            var canWriteLifecycle = LoadingEngineClockProbe.IsPinnedEngineMatched
                && TryReadLifecycle(component, out current)
                && TryReadLifecycle(deactivationComponent, out _)
                && capturedLifecycle.Length == 2 && !capturedLifecycle.Any(b => b > 1) && capturedLifecycle[1] <= capturedLifecycle[0]
                && capturedDeactivationLifecycle.Length == 2 && !capturedDeactivationLifecycle.Any(b => b > 1) && capturedDeactivationLifecycle[1] <= capturedDeactivationLifecycle[0];
            if (!canWriteLifecycle)
            {
                WarnOnce("Skipping Hit Crack native lifecycle write on unmatched engine or invalid lifecycle data.");
            }
            else
            {
                Modding.Logger.Log("[HKTAS] Applying Hit Crack lifecycle: current=" + string.Join(",", current) + " captured=" + string.Join(",", capturedLifecycle));
            }
            return () => {
                rotation();
                deactivation();
                component.transform.localPosition = LocalPosition;
                component.transform.localRotation = LocalRotation;
                component.transform.localScale = LocalScale;
                if (!canWriteLifecycle) return;
                var rotAddr = LifecycleAddress(component);
                var deactAddr = LifecycleAddress(deactivationComponent);
                if (rotAddr == IntPtr.Zero || deactAddr == IntPtr.Zero) return;
                if (!WriteProcessMemory(GetCurrentProcess(), rotAddr, capturedLifecycle, new UIntPtr(2), out var written) || written.ToUInt64() != 2
                    || !TryReadLifecycle(component, out var rotVerify) || !rotVerify.SequenceEqual(capturedLifecycle))
                    Modding.Logger.LogWarn("[HKTAS] Checkpoint Hit Crack lifecycle write verification differed.");
                if (!WriteProcessMemory(GetCurrentProcess(), deactAddr, capturedDeactivationLifecycle, new UIntPtr(2), out written) || written.ToUInt64() != 2
                    || !TryReadLifecycle(deactivationComponent, out var deactVerify) || !deactVerify.SequenceEqual(capturedDeactivationLifecycle))
                    Modding.Logger.LogWarn("[HKTAS] Checkpoint Hit Crack deactivation lifecycle write verification differed.");
            };
        }
    }
}
