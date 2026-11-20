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
        private static RandomRotationRange Bind(HeroController hero)
        {
            var effect = hero.damageEffectFSM;
            var go = effect?.FsmVariables.FindFsmGameObject("Hit Crack")?.Value;
            if (effect == null || effect.FsmName != "Knight Damage" || go == null || go.name != "Hit Crack"
                || go.transform.parent != effect.transform || go.transform.root != hero.transform || go.activeSelf || go.activeInHierarchy
                || !go.GetComponents<Component>().Select(c => c.GetType()).OrderBy(t => t.FullName, StringComparer.Ordinal)
                    .SequenceEqual(new[] { typeof(Transform), typeof(SpriteRenderer), typeof(Animator), typeof(DeactivateAfterDelay), typeof(RandomRotationRange) }
                        .OrderBy(t => t.FullName, StringComparer.Ordinal))
                || go.GetComponent<Animator>().keepAnimatorControllerStateOnDisable)
                throw new InvalidDataException("Checkpoint requires the bound inactive Hero Hit Crack effect with a resetting animator.");
            return go.GetComponent<RandomRotationRange>();
        }
        private static IntPtr LifecycleAddress(MonoBehaviour component)
        {
            var pointer = (IntPtr)CachedPointer.GetValue(component)!;
            if (pointer == IntPtr.Zero) throw new InvalidDataException("Checkpoint Hit Crack native component is absent.");
            // Hash-pinned Unity/PDB MonoBehaviour (312 bytes): m_DidAwake
            // +292, m_DidStart +293, m_IsDestroying +295, m_AddedToManager +296.
            return IntPtr.Add(pointer, 292);
        }
        private static byte[] ReadLifecycle(MonoBehaviour component)
        {
            var bytes = new byte[5];
            if (!ReadProcessMemory(GetCurrentProcess(), LifecycleAddress(component), bytes, new UIntPtr(5), out var read) || read.ToUInt64() != 5
                || bytes.Any(b => b > 1) || bytes[1] > bytes[0] || bytes[3] != 0 || bytes[4] != 0)
                throw new InvalidDataException("Checkpoint Hit Crack lifecycle is destroying, scheduled, or invalid: " + component.GetType().FullName + "/" + string.Join(",", bytes));
            return bytes.Take(2).ToArray();
        }
        internal static CheckpointHeroHitCrackState Capture(HeroController hero)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var component = Bind(hero);
            var deactivation = component.GetComponent<DeactivateAfterDelay>();
            var life = ReadLifecycle(component);
            Modding.Logger.Log("[HKTAS] Captured Hit Crack lifecycle: awake=" + life[0] + " start=" + life[1]);
            return new CheckpointHeroHitCrackState { RotationFields = CheckpointScalarFields.Capture(component, RotationScalars),
                DeactivationFields = CheckpointScalarFields.Capture(deactivation, DeactivationScalars), Lifecycle = life,
                DeactivationLifecycle = ReadLifecycle(deactivation), RotationEnabled = component.enabled, DeactivationEnabled = deactivation.enabled,
                LocalPosition = component.transform.localPosition, LocalRotation = component.transform.localRotation, LocalScale = component.transform.localScale };
        }
        internal Action PrepareApply(HeroController hero)
        {
            // Validate once for this synchronous C transaction; all reads and
            // write verification below use the same loaded engine/bindings.
            LoadingEngineClockProbe.RequirePinnedEngine();
            var component = Bind(hero); var current = ReadLifecycle(component);
            var deactivationComponent = component.GetComponent<DeactivateAfterDelay>(); ReadLifecycle(deactivationComponent);
            if (Lifecycle == null || Lifecycle.Length != 2 || Lifecycle.Any(b => b > 1) || Lifecycle[1] > Lifecycle[0]
                || DeactivationLifecycle == null || DeactivationLifecycle.Length != 2 || DeactivationLifecycle.Any(b => b > 1)
                || DeactivationLifecycle[1] > DeactivationLifecycle[0] || RotationEnabled != component.enabled || DeactivationEnabled != deactivationComponent.enabled
                || new[] { LocalPosition.x, LocalPosition.y, LocalPosition.z, LocalRotation.x, LocalRotation.y, LocalRotation.z, LocalRotation.w,
                    LocalScale.x, LocalScale.y, LocalScale.z }.Any(f => float.IsNaN(f) || float.IsInfinity(f)))
                throw new InvalidDataException("Checkpoint Hit Crack lifecycle or pose is invalid.");
            var rotation = CheckpointScalarFields.PrepareApply(component, RotationScalars, RotationFields);
            var deactivation = CheckpointScalarFields.PrepareApply(deactivationComponent, DeactivationScalars, DeactivationFields);
            Modding.Logger.Log("[HKTAS] Applying Hit Crack lifecycle: current=" + string.Join(",", current) + " captured=" + string.Join(",", Lifecycle));
            return () => { rotation(); deactivation(); component.transform.localPosition = LocalPosition;
                component.transform.localRotation = LocalRotation; component.transform.localScale = LocalScale;
                // This inactive component has no pending Start/update node.
                // Restore the actual two flags, so its next original OnEnable
                // keeps the same first-Start semantics as the source process.
                if (!WriteProcessMemory(GetCurrentProcess(), LifecycleAddress(component), Lifecycle, new UIntPtr(2), out var written) || written.ToUInt64() != 2
                    || !ReadLifecycle(component).SequenceEqual(Lifecycle))
                    throw new InvalidDataException("Checkpoint Hit Crack lifecycle write verification differs.");
                if (!WriteProcessMemory(GetCurrentProcess(), LifecycleAddress(deactivationComponent), DeactivationLifecycle, new UIntPtr(2), out written) || written.ToUInt64() != 2
                    || !ReadLifecycle(deactivationComponent).SequenceEqual(DeactivationLifecycle))
                    throw new InvalidDataException("Checkpoint Hit Crack deactivation lifecycle write verification differs."); };
        }
    }
}
