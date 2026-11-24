using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using InControl;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LeanS0EnvironmentParity
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<Type> HeroInactiveResetTypes = new HashSet<Type>
        {
            typeof(RandomRotationRange),
            typeof(RandomRotation),
            typeof(RandomScale),
            typeof(AudioSourcePitchRandomizer),
            typeof(RandomAudioStart),
            typeof(DeactivateAfterDelay)
        };

        private static readonly FieldInfo? PitchRandomizerAudioField = typeof(AudioSourcePitchRandomizer).GetField("audioSource", AnyInstance);
        private static readonly FieldInfo? RandomScaleDidScaleField = typeof(RandomScale).GetField("didScale", AnyInstance);
        private static readonly FieldInfo? RandomAudioStartTimeField = typeof(RandomAudioStart).GetField("time", AnyInstance);
        private static readonly FieldInfo? RandomAudioStartTimerField = typeof(RandomAudioStart).GetField("timer", AnyInstance);
        private static readonly FieldInfo? RandomAudioStartStartedField = typeof(RandomAudioStart).GetField("started", AnyInstance);
        private static readonly FieldInfo? DeactivateTimerField = typeof(DeactivateAfterDelay).GetField("timer", AnyInstance);
        private static readonly FieldInfo? DeactivateWorldPosField = typeof(DeactivateAfterDelay).GetField("worldPos", AnyInstance);
        private static readonly FieldInfo? DeactivateStartPosField = typeof(DeactivateAfterDelay).GetField("startPos", AnyInstance);
        private static readonly FieldInfo PoolObjectsField = typeof(ObjectPool).GetField("pooledObjects", AnyInstance)
            ?? throw new MissingFieldException("ObjectPool.pooledObjects");
        private static readonly FieldInfo SpawnedObjectsField = typeof(ObjectPool).GetField("spawnedObjects", AnyInstance)
            ?? throw new MissingFieldException("ObjectPool.spawnedObjects");
        private static readonly FieldInfo CachedPointerField = typeof(UnityEngine.Object).GetField("m_CachedPtr", AnyInstance)
            ?? throw new MissingFieldException("UnityEngine.Object.m_CachedPtr");
        private static readonly FieldInfo? UiManagerStartMenuTimeField =
            typeof(UIManager).GetField("startMenuTime", AnyInstance);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);

        private static bool nativeLifecycleUnsupportedWarned;

        private static void WarnNativeLifecycleUnsupportedOnce(string detail)
        {
            if (!nativeLifecycleUnsupportedWarned)
            {
                nativeLifecycleUnsupportedWarned = true;
                Modding.Logger.LogWarn("[HKTAS] " + detail);
            }
        }

        private static bool TryReadMonoLifecycle(MonoBehaviour component, out byte[] lifecycle)
        {
            lifecycle = new byte[] { 0, 0 };
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping MonoBehaviour native lifecycle read on unmatched UnityPlayer.dll.");
                return false;
            }
            IntPtr pointer = (IntPtr)CachedPointerField.GetValue(component)!;
            var bytes = new byte[5];
            if (pointer == IntPtr.Zero
                || !ReadProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), bytes, new UIntPtr(5), out var read)
                || read.ToUInt64() != 5 || bytes.Any(v => v > 1) || bytes[1] > bytes[0] || bytes[3] != 0)
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour native lifecycle layout differs for " + component.GetType().FullName + "; using fallback observation.");
                return false;
            }
            lifecycle = new[] { bytes[0], bytes[1] };
            return true;
        }

        private static void WriteMonoLifecycle(MonoBehaviour component, byte[] lifecycle)
        {
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping MonoBehaviour native lifecycle write on unmatched UnityPlayer.dll.");
                return;
            }
            if (lifecycle.Length != 2 || lifecycle.Any(v => v > 1) || lifecycle[1] > lifecycle[0] || component.isActiveAndEnabled)
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour lifecycle write preconditions differ for " + component.GetType().FullName + "; skipping write.");
                return;
            }
            IntPtr pointer = (IntPtr)CachedPointerField.GetValue(component)!;
            if (pointer == IntPtr.Zero
                || !WriteProcessMemory(GetCurrentProcess(), IntPtr.Add(pointer, 292), lifecycle, new UIntPtr(2), out var written)
                || written.ToUInt64() != 2
                || !TryReadMonoLifecycle(component, out var verify) || !verify.SequenceEqual(lifecycle))
            {
                WarnNativeLifecycleUnsupportedOnce("LeanS0 MonoBehaviour lifecycle write verification differed for " + component.GetType().FullName + ".");
            }
        }

        /// <summary>
        /// Resets pooled/spawned transient instances and inactive Hero effect lifecycles at an S0 boundary.
        /// </summary>
        internal static void ResetBoundaryEnvironment(LeanS0GateReceipt receipt, HeroController? hero)
        {
            if (receipt == null) throw new ArgumentNullException(nameof(receipt));
            receipt.ResetObjectPoolInstances = ResetObjectPoolToCleanInitialState(hero);
            receipt.ResetHeroInactiveLifecycles = ResetHeroInactiveRandomizerLifecycles(hero);
            if (hero != null)
            {
                var uiManager = UIManager.instance;
                if (uiManager != null && UiManagerStartMenuTimeField != null)
                {
                    UiManagerStartMenuTimeField.SetValue(uiManager, 0f);
                }
                if (LoadingEngineClockProbe.IsPinnedEngineMatched)
                {
                    try
                    {
                        var identities = new BoundaryObjectIdentity();
                        CheckpointPhysicsBroadphaseState.Canonicalize(identities);
                        Modding.Logger.Log("[HKTAS] LeanS0 broadphase canonicalized at S0 boundary.");
                    }
                    catch (Exception ex)
                    {
                        Modding.Logger.LogWarn("[HKTAS] LeanS0 broadphase canonicalize skipped: " + ex.Message);
                    }
                }
            }
        }

        /// <summary>
        /// Destroys all pooled/spawned transient instances at an S0 boundary (preserving explicit
        /// cross-scene charm companions like Grimmchild) and rebuilds initial startup pools cleanly
        /// so normal transitions and S0 restores enter every target scene identically.
        /// </summary>
        internal static int ResetObjectPoolToCleanInitialState(HeroController? hero)
        {
            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            int resetCount = 0;

            try
            {
                using (StartupObjectPoolRegistry.EnterRebuildScope())
                {
                    LoadingAudioLifetimePolicy.ResetForInProcessRestore();

                    ObjectPool? pool = ObjectPool.instance;
                    if (pool == null)
                    {
                        GameManager.instance?.DoDestroyPersonalPools();
                        return 0;
                    }

                    var preservedPrefabs = new HashSet<GameObject>();
                    if (SpawnedObjectsField.GetValue(pool) is Dictionary<GameObject, GameObject> spawned && spawned.Count > 0)
                    {
                        foreach (KeyValuePair<GameObject, GameObject> pair in spawned.ToList())
                        {
                            GameObject go = pair.Key;
                            if (go != null && go.name.StartsWith("Grimmchild", StringComparison.Ordinal))
                            {
                                if (pair.Value != null)
                                {
                                    preservedPrefabs.Add(pair.Value);
                                }
                                continue;
                            }

                            spawned.Remove(pair.Key);
                            if (go != null)
                            {
                                SilenceAndClearEffects(go);
                                UnityEngine.Object.DestroyImmediate(go);
                                resetCount++;
                            }
                        }
                    }

                    var pooledObjects = PoolObjectsField.GetValue(pool) as Dictionary<GameObject, List<GameObject>>;
                    if (pooledObjects != null)
                    {
                        foreach (List<GameObject> list in pooledObjects.Values)
                        {
                            if (list == null)
                            {
                                continue;
                            }

                            foreach (GameObject go in list)
                            {
                                if (go != null)
                                {
                                    SilenceAndClearEffects(go);
                                    UnityEngine.Object.DestroyImmediate(go);
                                    resetCount++;
                                }
                            }
                        }

                        pooledObjects.Clear();
                    }

                    // Clear stale PersonalObjectPool delegates from the unloaded scene before rebuilding startup pools.
                    GameManager.instance?.DoDestroyPersonalPools();

                    if (pooledObjects != null)
                    {
                        if (pool.startupPools != null)
                        {
                            foreach (ObjectPool.StartupPool sp in pool.startupPools)
                            {
                                if (sp?.prefab == null)
                                {
                                    continue;
                                }

                                if (sp.prefab.GetComponent<ActiveRecycler>() != null)
                                {
                                    // ActiveRecycler startup clones self-destroy after their initial animation;
                                    // keep an empty slot list so first spawn behaves identically across all runs.
                                    if (!pooledObjects.ContainsKey(sp.prefab))
                                    {
                                        pooledObjects[sp.prefab] = new List<GameObject>();
                                    }
                                    continue;
                                }

                                ObjectPool.CreatePool(sp.prefab, sp.size);
                            }
                        }

                        if (hero != null)
                        {
                            EnsureInitialHeroDeathPool(pooledObjects, hero.acidDeathPrefab);
                            EnsureInitialHeroDeathPool(pooledObjects, hero.spikeDeathPrefab);
                        }

                        foreach (GameObject preservedPrefab in preservedPrefabs)
                        {
                            if (preservedPrefab != null && !pooledObjects.ContainsKey(preservedPrefab))
                            {
                                pooledObjects[preservedPrefab] = new List<GameObject>();
                            }
                        }

                        foreach (StartupObjectPoolCatalogEntry<GameObject> entry in StartupObjectPoolRegistry.GetRegisteredEntries())
                        {
                            GameObject prefab = entry.Prefab;
                            if (prefab == null || pooledObjects.ContainsKey(prefab))
                            {
                                continue;
                            }

                            if (prefab.GetComponent<ActiveRecycler>() != null)
                            {
                                pooledObjects[prefab] = new List<GameObject>();
                                continue;
                            }

                            ObjectPool.CreatePool(prefab, entry.InitialPoolSize);
                        }
                    }
                }
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }

            if (resetCount > 0)
            {
                Modding.Logger.Log($"[HKTAS] Reset ObjectPool to clean initial state at S0: destroyed={resetCount}");
            }

            return resetCount;
        }

        private static void EnsureInitialHeroDeathPool(Dictionary<GameObject, List<GameObject>> pooledObjects, GameObject? prefab)
        {
            if (prefab == null || prefab.GetComponent<ActiveRecycler>() != null)
            {
                return;
            }

            if (!pooledObjects.TryGetValue(prefab, out List<GameObject>? list) || list == null)
            {
                ObjectPool.CreatePool(prefab, 1);
            }
            else if (list.Count < 1)
            {
                ObjectPool.CreatePool(prefab, 1 - list.Count);
            }
        }

        /// <summary>
        /// Resets inactive HeroController child randomizer/delay MonoBehaviours back to their
        /// unawakened initial state at S0 boundaries so prior-scene hits do not alter later scenes.
        /// </summary>
        internal static int ResetHeroInactiveRandomizerLifecycles(HeroController? hero)
        {
            if (hero == null)
            {
                return 0;
            }

            LoadingEngineClockProbe.RequirePinnedEngine();
            if (!LoadingEngineClockProbe.IsPinnedEngineMatched)
            {
                WarnNativeLifecycleUnsupportedOnce("Skipping ResetHeroInactiveRandomizerLifecycles on unmatched UnityPlayer.dll.");
                return 0;
            }

            MonoBehaviour[] behaviours = hero.GetComponentsInChildren<MonoBehaviour>(true);
            int resetCount = 0;

            foreach (MonoBehaviour mb in behaviours)
            {
                if (mb == null || mb.isActiveAndEnabled)
                {
                    continue;
                }

                GameObject? go = mb.gameObject;
                if (go == null || go.activeInHierarchy || (IntPtr)CachedPointerField.GetValue(mb)! == IntPtr.Zero)
                {
                    continue;
                }

                if (!HeroInactiveResetTypes.Contains(mb.GetType()))
                {
                    continue;
                }

                if (!TryReadMonoLifecycle(mb, out byte[] current) || (current[0] == 0 && current[1] == 0))
                {
                    continue;
                }

                if (mb is RandomScale)
                {
                    RandomScaleDidScaleField?.SetValue(mb, false);
                }
                else if (mb is AudioSourcePitchRandomizer)
                {
                    PitchRandomizerAudioField?.SetValue(mb, null);
                    AudioSource? audio = mb.GetComponent<AudioSource>();
                    if (audio != null)
                    {
                        audio.pitch = 1f;
                    }
                }
                else if (mb is RandomAudioStart)
                {
                    RandomAudioStartTimeField?.SetValue(mb, 0f);
                    RandomAudioStartTimerField?.SetValue(mb, 0f);
                    RandomAudioStartStartedField?.SetValue(mb, false);
                }
                else if (mb is DeactivateAfterDelay delay)
                {
                    if (delay.stayInPlace && DeactivateStartPosField?.GetValue(delay) is Vector3 startPos)
                    {
                        mb.transform.localPosition = startPos;
                    }
                    DeactivateTimerField?.SetValue(delay, 0f);
                    DeactivateWorldPosField?.SetValue(delay, Vector3.zero);
                    DeactivateStartPosField?.SetValue(delay, Vector3.zero);
                }

                WriteMonoLifecycle(mb, new byte[] { 0, 0 });
                resetCount++;
            }

            if (resetCount > 0)
            {
                Modding.Logger.Log($"[HKTAS] Reset inactive Hero effect lifecycles at S0: reset={resetCount}");
            }

            return resetCount;
        }

        private static void SilenceAndClearEffects(GameObject go)
        {
            foreach (AudioSource audio in go.GetComponentsInChildren<AudioSource>(true))
            {
                if (audio != null) audio.Stop();
            }
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private static readonly FieldInfo? OneAxisUpdateTickField = typeof(OneAxisInputControl).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? OneAxisPendingTickField = typeof(OneAxisInputControl).GetField("pendingTick", AnyInstance);
        private static readonly FieldInfo? OneAxisPendingCommitField = typeof(OneAxisInputControl).GetField("pendingCommit", AnyInstance);
        private static readonly FieldInfo? OneAxisNextRepeatTimeField = typeof(OneAxisInputControl).GetField("nextRepeatTime", AnyInstance);
        private static readonly FieldInfo? OneAxisWasRepeatedField = typeof(OneAxisInputControl).GetField("wasRepeated", AnyInstance);
        private static readonly FieldInfo? TwoAxisUpdateTickField = typeof(TwoAxisInputControl).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? InputControlZeroTickField = typeof(InputControl).GetField("zeroTick", AnyInstance);
        private static readonly FieldInfo? InputDeviceLastTickField = typeof(InputDevice).GetField("<LastInputTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetUpdateTickField = typeof(PlayerActionSet).GetField("<UpdateTick>k__BackingField", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetLastChangedField = typeof(PlayerActionSet).GetField("LastInputTypeChangedTick", AnyInstance);
        private static readonly FieldInfo? PlayerActionSetTwoAxisField = typeof(PlayerActionSet).GetField("twoAxisActions", AnyInstance);
        private static readonly FieldInfo? PlayerActionLastChangedField = typeof(PlayerAction).GetField("LastInputTypeChangedTick", AnyInstance);
        private static readonly FieldInfo? InputModuleDirectionField = typeof(HollowKnightInputModule).GetField("direction", AnyInstance);
        private static readonly FieldInfo? InputModuleNextMoveRepeatField = typeof(HollowKnightInputModule).GetField("nextMoveRepeatTime", AnyInstance);
        private static readonly FieldInfo? InputModuleLastVectorPressedField = typeof(HollowKnightInputModule).GetField("lastVectorPressedTime", AnyInstance);
        private static readonly FieldInfo? InputManagerDevicesField = typeof(InputManager).GetField("devices", AnyStatic);
        private static readonly FieldInfo? InputManagerActionSetsField = typeof(InputManager).GetField("playerActionSets", AnyStatic);

        /// <summary>
        /// Clamps/resets persistent InControl tick stamps that exceed <paramref name="targetTick"/>
        /// after rewinding InputManager.CurrentTick during an in-process reverse S0 restore.
        /// </summary>
        internal static void ResetInControlPersistentTicks(ulong targetTick)
        {
            foreach (HollowKnightInputModule mod in UnityEngine.Object.FindObjectsOfType<HollowKnightInputModule>())
            {
                if (mod == null) continue;
                ResetTwoAxisIfFuture(InputModuleDirectionField?.GetValue(mod) as TwoAxisInputControl, targetTick, forceReset: true);
                InputModuleNextMoveRepeatField?.SetValue(mod, 0f);
                InputModuleLastVectorPressedField?.SetValue(mod, 0f);
            }

            if (InputManagerDevicesField?.GetValue(null) is System.Collections.IList devices)
            {
                foreach (object? item in devices)
                {
                    if (item is InputDevice device) ResetDeviceTicks(device, targetTick);
                }
            }
            if (InputManager.ActiveDevice != null) ResetDeviceTicks(InputManager.ActiveDevice, targetTick);
            if (InputDevice.Null != null) ResetDeviceTicks(InputDevice.Null, targetTick);

            if (InputManagerActionSetsField?.GetValue(null) is System.Collections.IList sets)
            {
                foreach (object? item in sets)
                {
                    if (item is not PlayerActionSet set) continue;
                    ClampUlongField(PlayerActionSetUpdateTickField, set, targetTick, targetTick);
                    ClampUlongField(PlayerActionSetLastChangedField, set, targetTick, targetTick);
                    if (set.Actions != null)
                    {
                        foreach (PlayerAction action in set.Actions)
                        {
                            if (action == null) continue;
                            ResetOneAxisIfFuture(action, targetTick, forceReset: false);
                            ClampUlongField(PlayerActionLastChangedField, action, targetTick, targetTick);
                        }
                    }
                    if (PlayerActionSetTwoAxisField?.GetValue(set) is System.Collections.IList twoAxisList)
                    {
                        foreach (object? t in twoAxisList)
                        {
                            ResetTwoAxisIfFuture(t as TwoAxisInputControl, targetTick, forceReset: false);
                        }
                    }
                }
            }
        }

        private static void ResetDeviceTicks(InputDevice device, ulong targetTick)
        {
            ClampUlongField(InputDeviceLastTickField, device, targetTick, targetTick);
            ResetTwoAxisIfFuture(device.LeftStick, targetTick, forceReset: false);
            ResetTwoAxisIfFuture(device.RightStick, targetTick, forceReset: false);
            ResetTwoAxisIfFuture(device.DPad, targetTick, forceReset: false);
            if (device.Controls != null)
            {
                foreach (InputControl control in device.Controls)
                {
                    ResetOneAxisIfFuture(control, targetTick, forceReset: false);
                }
            }
        }

        private static void ResetTwoAxisIfFuture(TwoAxisInputControl? control, ulong targetTick, bool forceReset)
        {
            if (control == null) return;
            if (forceReset)
            {
                TwoAxisUpdateTickField?.SetValue(control, 0UL);
            }
            else
            {
                ClampUlongField(TwoAxisUpdateTickField, control, targetTick, 0UL);
            }
            ResetOneAxisIfFuture(control.Left, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Right, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Up, targetTick, forceReset);
            ResetOneAxisIfFuture(control.Down, targetTick, forceReset);
        }

        private static void ResetOneAxisIfFuture(OneAxisInputControl? control, ulong targetTick, bool forceReset)
        {
            if (control == null) return;
            bool reset = forceReset;
            if (OneAxisUpdateTickField != null && (ulong)OneAxisUpdateTickField.GetValue(control)! > targetTick)
            {
                OneAxisUpdateTickField.SetValue(control, 0UL);
                reset = true;
            }
            else if (forceReset)
            {
                OneAxisUpdateTickField?.SetValue(control, 0UL);
            }
            if (OneAxisPendingTickField != null && (forceReset || (ulong)OneAxisPendingTickField.GetValue(control)! > targetTick))
            {
                OneAxisPendingTickField.SetValue(control, 0UL);
                OneAxisPendingCommitField?.SetValue(control, false);
                reset = true;
            }
            if (reset)
            {
                OneAxisNextRepeatTimeField?.SetValue(control, 0f);
                OneAxisWasRepeatedField?.SetValue(control, false);
            }
            if (control is InputControl ic && InputControlZeroTickField != null
                && (forceReset || (ulong)InputControlZeroTickField.GetValue(ic)! > targetTick))
            {
                InputControlZeroTickField.SetValue(ic, 0UL);
            }
        }

        private static void ClampUlongField(FieldInfo? field, object target, ulong maxAllowed, ulong replacement)
        {
            if (field != null && (ulong)field.GetValue(target)! > maxAllowed)
            {
                field.SetValue(target, replacement);
            }
        }
    }
}
