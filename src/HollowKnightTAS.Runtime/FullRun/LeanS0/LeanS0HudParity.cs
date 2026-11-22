using System;
using System.Collections;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    /// <summary>
    /// Deterministic, side-effect-free synchronization of HUD elements and charm-driven
    /// visual/gameplay state at LeanS0 target boundaries.
    /// </summary>
    internal static class LeanS0HudParity
    {
        private const float LiquidBottomY = -10f;
        private const float LiquidBaseY = -0.589999974f;
        private const float LiquidYPerMp = 0.0171000008f;
        private const float BlueHealthBaseX = -10.32f;
        private const float BlueHealthStepX = 0.94f;
        private const float BlueHealthY = 7.68f;
        private const float JoniHealthY = 7.7f;
        private const float VesselSmallScale = 0.75f;
        private const float VesselLargeScale = 1f;
        private const int MinEyeOpenMp = 50;
        private const int BaseSpellCost = 33;
        private const int SpellTwisterCost = 24;

        private static readonly Color LiquidCantHealColor = new Color(0.43137255f, 0.43137255f, 0.43137255f, 1f);
        private static readonly Color EyesHiddenColor = new Color(1f, 1f, 1f, 0f);
        private static readonly Color FuryNailArtColor = new Color(1f, 0.17647058f, 0.17647058f, 1f);

        private static readonly FieldInfo? FsmDelayedEventsField =
            typeof(Fsm).GetField("delayedEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly string[] BasicSlashNames =
        {
            "Slash",
            "AltSlash",
            "DownSlash",
            "UpSlash",
            "WallSlash"
        };

        private static readonly string[] SizeModifierSlashNames =
        {
            "Slash",
            "AltSlash",
            "DownSlash",
            "UpSlash"
        };

        internal static (string AnimAppear, string AnimIdle, string AnimCrackAppear, string AnimCracked) ResolveHudFrameClips(
            bool bossRushMode,
            int permadeathMode)
        {
            if (bossRushMode)
            {
                return ("HUD Frame GG", "HUD Frame Idle GG", "HUD Frame CrackAppear", "HUD Frame Cracked");
            }

            if (permadeathMode > 0)
            {
                return ("HUD Frame St", "HUD Frame Idle St", "HUD Frame CrackAppear St", "HUD Frame Cracked St");
            }

            return ("HUD Frame", "HUD Frame Idle", "HUD Frame CrackAppear", "HUD Frame Cracked");
        }

        internal static string ResolveSlideOutSteadyState(bool isHudOut, string? capturedState)
        {
            if (isHudOut)
            {
                return "Out";
            }

            return capturedState switch
            {
                "In" or "Come In" => "In",
                _ => "Idle"
            };
        }

        internal static string ResolveHealthDisplaySteadyState(
            int healthNumber,
            int currentHp,
            int maxHp,
            bool boundShell,
            int boundMaxHp,
            string? capturedState)
        {
            bool isOverMax = healthNumber > maxHp;
            bool isBound = !isOverMax && boundShell && healthNumber > boundMaxHp;
            string desiredState = isOverMax
                ? "Inactive"
                : (isBound ? "Bound" : (currentHp >= healthNumber ? "Idle" : "Empty"));

            if (string.Equals(capturedState, desiredState, StringComparison.Ordinal))
            {
                return capturedState!;
            }

            return desiredState;
        }

        internal static string ResolveLowHealthFxSteadyState(
            int health,
            int healthBlue,
            bool equippedJoni,
            bool isHudOut,
            string? capturedState)
        {
            bool isLowHealth = health == 1 && healthBlue <= 0 && !equippedJoni;
            if (!isLowHealth || isHudOut)
            {
                return "Idle";
            }

            if (string.Equals(capturedState, "Wait", StringComparison.Ordinal))
            {
                return "Wait";
            }

            return "Low Health";
        }

        internal static float ComputeLiquidLocalY(int mp)
            => mp <= 1 ? LiquidBottomY : (LiquidBaseY + mp * LiquidYPerMp);

        internal static (
            bool Obtained,
            bool ShowRenderer,
            bool ShowBindingCap,
            string OrbState,
            string SizeState,
            string? ClipName,
            float ScaleXY) ResolveVesselSteadyState(
            int vesselIndex,
            int reserveMp,
            int reserveMax,
            bool boundSoul)
        {
            int clampedIndex = Math.Max(1, Math.Min(4, vesselIndex));
            int emptyAmount = (clampedIndex - 1) * 33;
            int fullAmount = clampedIndex * 33;
            int quarterAmount = emptyAmount + 8;
            int halfAmount = emptyAmount + 16;
            int threeQuarterAmount = emptyAmount + 24;

            bool obtained = reserveMax >= fullAmount;
            if (boundSoul)
            {
                return (
                    Obtained: obtained,
                    ShowRenderer: false,
                    ShowBindingCap: obtained,
                    OrbState: obtained ? "Obtained" : "Not obtained",
                    SizeState: "Bound",
                    ClipName: null,
                    ScaleXY: VesselSmallScale);
            }

            if (!obtained)
            {
                return (
                    Obtained: false,
                    ShowRenderer: false,
                    ShowBindingCap: false,
                    OrbState: "Not Obtained",
                    SizeState: "Start Small",
                    ClipName: null,
                    ScaleXY: VesselSmallScale);
            }

            int sizeFullThreshold = reserveMax == fullAmount ? fullAmount + 1 : fullAmount;
            bool isLarge = reserveMp > emptyAmount && reserveMp < sizeFullThreshold;
            string sizeState = isLarge ? "Start Large" : "Start Small";
            float scale = isLarge ? VesselLargeScale : VesselSmallScale;

            string orbState;
            string clipName;
            if (reserveMp >= fullAmount)
            {
                orbState = "Up To Full";
                clipName = "V_Full";
            }
            else if (reserveMp >= threeQuarterAmount)
            {
                orbState = "Up To 3Quarter";
                clipName = "V_UpTo3Quarter";
            }
            else if (reserveMp >= halfAmount)
            {
                orbState = "Up To Half";
                clipName = "V_UpToHalf";
            }
            else if (reserveMp >= quarterAmount)
            {
                orbState = "Up To Quarter";
                clipName = "V_UpToQuarter";
            }
            else
            {
                orbState = "Empty";
                clipName = "V_Empty";
            }

            return (
                Obtained: true,
                ShowRenderer: true,
                ShowBindingCap: false,
                OrbState: orbState,
                SizeState: sizeState,
                ClipName: clipName,
                ScaleXY: scale);
        }

        internal static bool ShouldActivateFury(bool equippedFury, int health, bool equippedJoni, int healthBlue)
            => equippedFury && health == 1 && !(equippedJoni && healthBlue > 0);

        internal static int ReadBoundValueOrDefault(bool isBoundActive, Func<int> readBoundValue)
            => isBoundActive ? readBoundValue() : 0;

        internal static (int FinalNailDamage, int BeamDamage) ComputeSlashAndBeamDamage(
            int rawNailDamage,
            bool boundNail,
            int boundNailDamage,
            bool equippedStrength,
            bool brokenStrength)
        {
            int baseNail = boundNail ? Math.Min(rawNailDamage, boundNailDamage) : rawNailDamage;
            int finalNail = equippedStrength && !brokenStrength
                ? Mathf.RoundToInt(baseNail * 1.5f)
                : baseNail;
            int beamDamage = Mathf.Clamp(Mathf.RoundToInt(finalNail * 0.5f), 1, 90);
            return (finalNail, beamDamage);
        }

        internal static int ComputeSharpShadowDamage(int rawNailDamage, bool equippedDashmaster)
            => Mathf.RoundToInt(rawNailDamage * (equippedDashmaster ? 1.5f : 1f));

        internal static int ComputeSpellCost(bool equippedSpellTwister)
            => equippedSpellTwister ? SpellTwisterCost : BaseSpellCost;

        internal static string ResolveBlockerHudClip(int blockerHits)
        {
            if (blockerHits >= 4)
            {
                return "UI Appear";
            }

            return blockerHits switch
            {
                3 => "UI Break 1",
                2 => "UI Break 2",
                1 => "UI Break 3",
                _ => "UI Break 4"
            };
        }

        internal static void SynchronizeAtTargetS0(
            HeroController hero,
            PlayerData pd,
            LeanS0PlayMakerGlobalsState? globalsState)
        {
            if (hero == null || pd == null)
            {
                return;
            }

            UnityEngine.Random.State savedRng = UnityEngine.Random.state;
            try
            {
                GameCameras? cameras = GameCameras.instance;
                Transform? hudCameraRoot = cameras?.hudCamera != null ? cameras.hudCamera.transform : null;
                Transform? hudCanvas = hudCameraRoot?.Find("Hud Canvas");

                SyncHudSlideOutAndFrame(hudCanvas, pd, globalsState);
                SyncHealthDisplays(hudCanvas, pd, globalsState);
                SyncJoniAndBlueHealth(hudCanvas, pd);
                SyncLowHealthAndOvercharmedAndHiveRegen(hero, hudCanvas, pd, globalsState);
                SyncSoulOrbAndVessels(hero, cameras, hudCanvas, pd, globalsState);
                SyncFuryAndCharmEffectsAndIcons(hero, cameras, hudCameraRoot, hudCanvas, pd);
            }
            finally
            {
                UnityEngine.Random.state = savedRng;
            }
        }

        private static void SyncHudSlideOutAndFrame(
            Transform? hudCanvas,
            PlayerData pd,
            LeanS0PlayMakerGlobalsState? globalsState)
        {
            if (hudCanvas == null)
            {
                return;
            }

            GameObject hudCanvasGo = hudCanvas.gameObject;
            StopAndDestroyITweens(hudCanvasGo);

            bool isHudOut = globalsState?.IsHudOut ?? false;
            hudCanvas.localScale = isHudOut ? new Vector3(0.75f, 0.75f, 0.75f) : Vector3.one;

            PlayMakerFSM? slideOut = FindFsm(hudCanvasGo, "Slide Out");
            if (slideOut != null && slideOut.Fsm != null && slideOut.Fsm.Initialized)
            {
                FsmVector3? startPosVar = slideOut.FsmVariables?.FindFsmVector3("Start Position");
                FsmVector3? offPosVar = slideOut.FsmVariables?.FindFsmVector3("Off position");
                if (startPosVar != null && offPosVar != null
                    && (offPosVar.Value - startPosVar.Value).sqrMagnitude > 1f)
                {
                    hudCanvas.position = isHudOut ? offPosVar.Value : startPosVar.Value;
                }

                string targetSlideState = ResolveSlideOutSteadyState(isHudOut, globalsState?.SlideOutActiveState);
                TransitionFsmSafe(slideOut, targetSlideState);

                FsmBool? globalIsHudOut = FsmVariables.GlobalVariables?.FindFsmBool("Is HUD Out");
                if (globalIsHudOut != null)
                {
                    globalIsHudOut.Value = isHudOut;
                }
            }

            Transform? hudFrameTr = hudCanvas.Find("Soul Orb/HUD_frame");
            if (hudFrameTr != null)
            {
                GameObject hudFrameGo = hudFrameTr.gameObject;
                bool bossRush = pd.GetBool("bossRushMode");
                int permadeath = pd.GetInt("permadeathMode");
                bool soulLimited = pd.GetBool("soulLimited");
                var clips = ResolveHudFrameClips(bossRush, permadeath);

                PlayMakerFSM? loadAnimFsm = FindFsm(hudFrameGo, "Load Animation");
                if (loadAnimFsm != null && loadAnimFsm.Fsm != null && loadAnimFsm.Fsm.Initialized)
                {
                    SetFsmString(loadAnimFsm, "Anim Appear", clips.AnimAppear);
                    SetFsmString(loadAnimFsm, "Anim Idle", clips.AnimIdle);
                    SetFsmString(loadAnimFsm, "Anim CrackAppear", clips.AnimCrackAppear);
                    SetFsmString(loadAnimFsm, "Anim Cracked", clips.AnimCracked);
                    SetFsmInt(loadAnimFsm, "PermaDeath Mode", permadeath);

                    TransitionFsmSafe(loadAnimFsm, soulLimited ? "Cracked" : "Normal");
                }

                SetMeshRendererEnabled(hudFrameGo, true);
                PlayTk2dClip(hudFrameGo, soulLimited ? clips.AnimCracked : clips.AnimIdle);
            }
        }

        private static void SyncHealthDisplays(
            Transform? hudCanvas,
            PlayerData pd,
            LeanS0PlayMakerGlobalsState? globalsState)
        {
            Transform? healthRoot = hudCanvas?.Find("Health");
            if (healthRoot == null)
            {
                return;
            }

            bool isHive = pd.GetBool("equippedCharm_29");
            bool isJoni = pd.GetBool("equippedCharm_27");
            int currentHp = pd.GetInt("health");
            int maxHp = pd.GetInt("maxHealth");
            bool boundShell = BossSequenceController.IsInSequence && BossSequenceController.BoundShell;
            int boundMaxHp = ReadBoundValueOrDefault(boundShell, () => BossSequenceController.BoundMaxHealth);
            string[]? capturedStates = globalsState?.HealthDisplayActiveStates;

            for (int i = 0; i < 11; i++)
            {
                int healthNumber = i + 1;
                Transform? healthSlot = healthRoot.Find($"Health {healthNumber}");
                if (healthSlot == null)
                {
                    continue;
                }

                GameObject selfGo = healthSlot.gameObject;
                StopAndDestroyITweens(selfGo);

                Transform? idleTr = healthSlot.Find("Idle");
                Transform? idleHiveTr = healthSlot.Find("Idle Hive");
                Transform? boundTr = healthSlot.Find("Idle Bound");
                Transform? maxUpTr = healthSlot.Find("Max Up");

                if (maxUpTr != null)
                {
                    maxUpTr.gameObject.SetActive(false);
                }

                GameObject? activeIdleGo = (isHive ? idleHiveTr : idleTr)?.gameObject;
                GameObject? otherIdleGo = (isHive ? idleTr : idleHiveTr)?.gameObject;
                GameObject? boundGo = boundTr?.gameObject;

                SetTk2dSpriteColor(selfGo, Color.white);

                if (healthNumber == 1 && !isJoni)
                {
                    ResetLocalY(idleTr, 0f);
                    ResetLocalY(idleHiveTr, 0f);
                    if (activeIdleGo != null)
                    {
                        PlayTk2dClip(activeIdleGo, isHive ? "Hive Health Idle" : "Health Idle");
                    }
                }

                string? capturedState = capturedStates != null && i < capturedStates.Length
                    ? capturedStates[i]
                    : null;
                string desiredState = ResolveHealthDisplaySteadyState(
                    healthNumber,
                    currentHp,
                    maxHp,
                    boundShell,
                    boundMaxHp,
                    capturedState);

                PlayMakerFSM? hd = FindFsm(selfGo, "health_display");
                if (hd != null && hd.Fsm != null && hd.Fsm.Initialized)
                {
                    SetFsmGameObject(hd, "Self", selfGo);
                    SetFsmGameObject(hd, "Parent", healthRoot.gameObject);
                    SetFsmGameObject(hd, "Max Up", maxUpTr?.gameObject);
                    SetFsmGameObject(hd, "Bound Sprite", boundGo);
                    SetFsmGameObject(hd, "Idle Sprite", activeIdleGo);
                    SetFsmGameObject(hd, "Idle Sprite Other", otherIdleGo);
                    SetFsmBool(hd, "Initialised", true);
                    SetFsmInt(hd, "Max HP", maxHp);
                    SetFsmInt(hd, "Current HP", currentHp);
                    SetFsmString(hd, "Anim Appear", isHive ? "Hive Health Appear" : "Health Appear");
                    SetFsmString(hd, "Anim Break", isHive ? "Hive Health Break" : "Health Break");
                    SetFsmString(hd, "Anim Empty", isHive ? "Hive Health Empty" : "Health Empty");
                    SetFsmString(hd, "Anim Refill", isHive ? "Hive Health Refill" : "Health Refill");

                    TransitionFsmSafe(hd, desiredState);
                }

                SetMeshRendererEnabled(otherIdleGo, false);
                switch (desiredState)
                {
                    case "Inactive":
                        SetMeshRendererEnabled(selfGo, false);
                        SetMeshRendererEnabled(activeIdleGo, false);
                        SetMeshRendererEnabled(boundGo, false);
                        break;

                    case "Bound":
                        SetMeshRendererEnabled(selfGo, false);
                        SetMeshRendererEnabled(activeIdleGo, false);
                        SetMeshRendererEnabled(boundGo, true);
                        PlayTk2dClip(selfGo, "Health Bound");
                        break;

                    case "Idle":
                        SetMeshRendererEnabled(selfGo, false);
                        SetMeshRendererEnabled(activeIdleGo, true);
                        SetMeshRendererEnabled(boundGo, false);
                        break;

                    case "Empty":
                        SetMeshRendererEnabled(selfGo, true);
                        SetMeshRendererEnabled(activeIdleGo, false);
                        SetMeshRendererEnabled(boundGo, false);
                        PlayTk2dClip(selfGo, isHive ? "Hive Health Empty" : "Health Empty");
                        break;
                }
            }
        }

        private static void SyncJoniAndBlueHealth(Transform? hudCanvas, PlayerData pd)
        {
            Transform? healthRoot = hudCanvas?.Find("Health");
            if (healthRoot == null)
            {
                return;
            }

            Transform? joniTr = healthRoot.Find("Joni Health");
            Transform? h1Tr = healthRoot.Find("Health 1");
            Transform? h1IdleTr = h1Tr?.Find("Idle");
            Transform? h1IdleHiveTr = h1Tr?.Find("Idle Hive");

            bool isJoni = pd.GetBool("equippedCharm_27");
            bool isHive = pd.GetBool("equippedCharm_29");
            int currentHp = pd.GetInt("health");
            int maxHp = pd.GetInt("maxHealth");
            int healthBlue = Math.Max(0, pd.GetInt("healthBlue"));
            int joniHealthBlue = pd.GetInt("joniHealthBlue");

            float h1IdleTargetY = isJoni ? 50f : 0f;
            ResetLocalY(h1IdleTr, h1IdleTargetY);
            ResetLocalY(h1IdleHiveTr, h1IdleTargetY);

            PlayMakerFSM? joniFsm = FindFsm(healthRoot.gameObject, "Joni Health");
            if (joniFsm != null && joniFsm.Fsm != null && joniFsm.Fsm.Initialized)
            {
                SetFsmGameObject(joniFsm, "Health 1", h1Tr?.gameObject);
                SetFsmGameObject(joniFsm, "Health 1 Idle", h1IdleTr?.gameObject);
                SetFsmGameObject(joniFsm, "Health 1 Idle Hive", h1IdleHiveTr?.gameObject);
                SetFsmGameObject(joniFsm, "Joni Health", joniTr?.gameObject);
                TransitionFsmSafe(joniFsm, "Idle");
            }

            if (h1Tr != null)
            {
                SetTk2dSpriteColor(h1Tr.gameObject, Color.white);
            }

            if (joniTr != null)
            {
                GameObject joniGo = joniTr.gameObject;
                SetTk2dSpriteColor(joniGo, Color.white);
                ResetLocalY(joniTr, JoniHealthY);

                Transform? joniIdleTr = joniTr.Find("Idle");
                Transform? joniMaxUpTr = joniTr.Find("Max Up");
                if (joniMaxUpTr != null)
                {
                    joniMaxUpTr.gameObject.SetActive(false);
                }

                string joniIdleClip = isHive ? "Hive Health Idle B" : "Blue Idle";
                string joniBreakClip = isHive ? "Hive Health Break B" : "Blue Break";
                string joniAppearClip = isHive ? "Hive Health Appear B" : "Blue Appear";

                PlayMakerFSM? joniDisplayFsm = FindFsm(joniGo, "blue_health_display");
                if (joniDisplayFsm != null && joniDisplayFsm.Fsm != null && joniDisplayFsm.Fsm.Initialized)
                {
                    SetFsmGameObject(joniDisplayFsm, "Self", joniGo);
                    SetFsmGameObject(joniDisplayFsm, "Parent", healthRoot.gameObject);
                    SetFsmGameObject(joniDisplayFsm, "Health 1", h1Tr?.gameObject);
                    SetFsmGameObject(joniDisplayFsm, "Idle Sprite", joniIdleTr?.gameObject);
                    SetFsmGameObject(joniDisplayFsm, "Max Up", joniMaxUpTr?.gameObject);
                    SetFsmString(joniDisplayFsm, "Anim Idle", joniIdleClip);
                    SetFsmString(joniDisplayFsm, "Anim Break", joniBreakClip);
                    SetFsmString(joniDisplayFsm, "Anim Appear", joniAppearClip);
                    SetFsmBool(joniDisplayFsm, "Appeared?", true);
                }

                bool showJoniSlot1 = isJoni && (currentHp >= 1 || joniHealthBlue > 0 || healthBlue > 0);
                if (showJoniSlot1)
                {
                    TransitionFsmSafe(joniDisplayFsm, "Idle");
                    SetMeshRendererEnabled(joniGo, true);
                    PlayTk2dClip(joniGo, joniIdleClip);
                }
                else
                {
                    TransitionFsmSafe(joniDisplayFsm, "Off");
                    SetMeshRendererEnabled(joniGo, false);
                }
            }

            for (int i = healthRoot.childCount - 1; i >= 0; i--)
            {
                Transform child = healthRoot.GetChild(i);
                if (child.name.StartsWith("Blue Health", StringComparison.Ordinal)
                    && !string.Equals(child.name, "Joni Health", StringComparison.Ordinal))
                {
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
                }
            }

            PlayMakerFSM? blueControlFsm = FindFsm(healthRoot.gameObject, "Blue Health Control");
            if (blueControlFsm != null && blueControlFsm.Fsm != null && blueControlFsm.Fsm.Initialized)
            {
                SetFsmBool(blueControlFsm, "Init", true);
                SetFsmInt(blueControlFsm, "Blue HP", healthBlue);
                SetFsmInt(blueControlFsm, "Max HP", maxHp);
                SetFsmInt(blueControlFsm, "Blues Added", healthBlue);
                SetFsmInt(blueControlFsm, "Blue HP counter", 0);
                SetFsmInt(blueControlFsm, "Joni HP Counter", 0);
                TransitionFsmSafe(blueControlFsm, "Idle");

                if (healthBlue > 0)
                {
                    ResolveBlueHealthPrefabs(blueControlFsm, out GameObject? normalPrefab, out GameObject? hivePrefab);
                    GameObject? prefabToUse = isJoni && isHive && hivePrefab != null ? hivePrefab : normalPrefab;
                    if (prefabToUse != null)
                    {
                        bool useHiveBlueClip = isJoni && isHive;
                        string cloneIdleClip = useHiveBlueClip ? "Hive Health Idle B" : "Blue Idle";
                        string cloneBreakClip = useHiveBlueClip ? "Hive Health Break B" : "Blue Break";
                        string cloneAppearClip = useHiveBlueClip ? "Hive Health Appear B" : "Blue Appear";

                        for (int b = 0; b < healthBlue; b++)
                        {
                            GameObject clone = UnityEngine.Object.Instantiate(prefabToUse, healthRoot);
                            clone.transform.localPosition = new Vector3(
                                BlueHealthBaseX + BlueHealthStepX * (maxHp + b),
                                BlueHealthY,
                                0f);
                            clone.transform.localScale = Vector3.one;
                            clone.SetActive(true);

                            SetTk2dSpriteColor(clone, Color.white);
                            SetMeshRendererEnabled(clone, true);
                            PlayTk2dClip(clone, cloneIdleClip);

                            PlayMakerFSM? cloneFsm = FindFsm(clone, "blue_health_display");
                            if (cloneFsm != null && cloneFsm.Fsm != null)
                            {
                                EnsureFsmStarted(cloneFsm);
                                SetFsmInt(cloneFsm, "Health Number", b + 1);
                                SetFsmBool(cloneFsm, "Start Idle", true);
                                SetFsmBool(cloneFsm, "Init", true);
                                SetFsmBool(cloneFsm, "Appeared?", true);
                                SetFsmGameObject(cloneFsm, "Self", clone);
                                SetFsmGameObject(cloneFsm, "Parent", healthRoot.gameObject);
                                SetFsmString(cloneFsm, "Anim Idle", cloneIdleClip);
                                SetFsmString(cloneFsm, "Anim Break", cloneBreakClip);
                                SetFsmString(cloneFsm, "Anim Appear", cloneAppearClip);
                                TransitionFsmSafe(cloneFsm, "Idle");
                            }
                        }
                    }
                }
            }
        }

        private static void SyncLowHealthAndOvercharmedAndHiveRegen(
            HeroController hero,
            Transform? hudCanvas,
            PlayerData pd,
            LeanS0PlayMakerGlobalsState? globalsState)
        {
            Transform? healthRoot = hudCanvas?.Find("Health");
            if (healthRoot == null)
            {
                return;
            }

            Transform? ocBackboardTr = healthRoot.Find("OC Backboard");
            bool overcharmed = pd.GetBool("overcharmed");
            if (ocBackboardTr != null)
            {
                ocBackboardTr.gameObject.SetActive(overcharmed);
            }

            int health = pd.GetInt("health");
            int healthBlue = pd.GetInt("healthBlue");
            bool equippedJoni = pd.GetBool("equippedCharm_27");
            bool isHudOut = globalsState?.IsHudOut ?? false;

            Transform? h1Tr = healthRoot.Find("Health 1");
            Transform? joniTr = healthRoot.Find("Joni Health");
            Transform? lowHealthParticlesTr = healthRoot.Find("Low Health Particles");
            Transform? lowHealthLightTr = healthRoot.Find("Low Health Light");
            Transform? lowHealthVignetteTr = healthRoot.Find("Low Health Vignette");
            Transform? lowHealthLeakTr = hero.transform.Find("Low Health Leak");

            PlayMakerFSM? lowHealthFx = FindFsm(healthRoot.gameObject, "Low Health FX");
            if (lowHealthFx != null && lowHealthFx.Fsm != null && lowHealthFx.Fsm.Initialized)
            {
                SetFsmFloat(lowHealthFx, "Wait Time", globalsState?.LowHealthFxWaitTime ?? 0f);
                SetFsmInt(lowHealthFx, "HP", health);
                SetFsmInt(lowHealthFx, "Blue HP", healthBlue);
                SetFsmGameObject(lowHealthFx, "Health 1", h1Tr?.gameObject);
                SetFsmGameObject(lowHealthFx, "Joni Health", joniTr?.gameObject);
                SetFsmGameObject(lowHealthFx, "OC Backboard", ocBackboardTr?.gameObject);
                SetFsmGameObject(lowHealthFx, "Low Health Particles", lowHealthParticlesTr?.gameObject);
                SetFsmGameObject(lowHealthFx, "Low Health Light", lowHealthLightTr?.gameObject);
                SetFsmGameObject(lowHealthFx, "Low Health Vignette", lowHealthVignetteTr?.gameObject);
                SetFsmGameObject(lowHealthFx, "Low Health Leak", lowHealthLeakTr?.gameObject);

                string targetLowHealthState = ResolveLowHealthFxSteadyState(
                    health,
                    healthBlue,
                    equippedJoni,
                    isHudOut,
                    globalsState?.LowHealthFxActiveState);

                TransitionFsmSafe(lowHealthFx, targetLowHealthState);

                if (string.Equals(targetLowHealthState, "Idle", StringComparison.Ordinal))
                {
                    StopParticles(lowHealthParticlesTr?.gameObject);
                    StopParticles(lowHealthLeakTr?.gameObject);

                    FsmVector3? h1InitialPos = lowHealthFx.FsmVariables?.FindFsmVector3("H1 Initial Pos");
                    if (h1Tr != null && h1InitialPos != null && h1InitialPos.Value != Vector3.zero)
                    {
                        h1Tr.localPosition = h1InitialPos.Value;
                    }

                    if (joniTr != null)
                    {
                        ResetLocalY(joniTr, JoniHealthY);
                    }

                    ResetFadedOverlay(lowHealthLightTr, "Fader", "Down");
                    ResetFadedOverlay(lowHealthVignetteTr, "Vignette Control", "Down");
                }
            }

            Transform? hiveBlobTr = healthRoot.Find("Hive Recovery Blob");
            PlayMakerFSM? hiveRegen = FindFsm(healthRoot.gameObject, "Hive Health Regen");
            if (hiveRegen != null && hiveRegen.Fsm != null && hiveRegen.Fsm.Initialized)
            {
                SetFsmGameObject(hiveRegen, "Recovery Blob", hiveBlobTr?.gameObject);
                SetMeshRendererEnabled(hiveBlobTr?.gameObject, false);

                bool isHive = pd.GetBool("equippedCharm_29");
                if (!isHive)
                {
                    TransitionFsmSafe(hiveRegen, "Inert");
                }
                else
                {
                    string active = hiveRegen.ActiveStateName;
                    if (string.IsNullOrEmpty(active)
                        || active is "Init" or "Pause" or "Check" or "Inert" or "Recover" or "Recover 1" or "Recover 2" or "Start Recovery")
                    {
                        TransitionFsmSafe(hiveRegen, "Idle");
                    }
                }
            }
            else if (hiveBlobTr != null)
            {
                SetMeshRendererEnabled(hiveBlobTr.gameObject, false);
            }
        }

        private static void SyncSoulOrbAndVessels(
            HeroController hero,
            GameCameras? cameras,
            Transform? hudCanvas,
            PlayerData pd,
            LeanS0PlayMakerGlobalsState? globalsState)
        {
            Transform? soulOrbTr = hudCanvas?.Find("Soul Orb");
            if (soulOrbTr == null)
            {
                return;
            }

            GameObject soulOrbGo = soulOrbTr.gameObject;
            StopAndDestroyITweens(soulOrbGo);

            int mp = pd.GetInt("MPCharge");
            int maxMp = pd.GetInt("maxMP");
            int focusMp = pd.GetInt("focusMP_amount");
            bool soulLimited = pd.GetBool("soulLimited");
            bool boundSoul = BossSequenceController.IsInSequence && BossSequenceController.BoundSoul;
            bool isHudOut = globalsState?.IsHudOut ?? false;

            Transform? fillTr = soulOrbTr.Find("SoulOrb_fill");
            Transform? liquidTr = soulOrbTr.Find("SoulOrb_fill/Liquid");
            Transform? eyesTr = soulOrbTr.Find("Eyes");
            Transform? orbFullTr = soulOrbTr.Find("Orb Full");
            Transform? bindingCapTr = soulOrbTr.Find("Binding Cap");
            Transform? bindingCapFullTr = soulOrbTr.Find("Binding Cap Full");
            Transform? canHealParticlesTr = soulOrbTr.Find("Can Heal Particles");
            Transform? whiteFlashTr = soulOrbTr.Find("White Flash");
            Transform? orbAnimTr = soulOrbTr.Find("Orb Anim");
            Transform? burstAnimTr = soulOrbTr.Find("Burst Anim");
            Transform? soulBurstTr = soulOrbTr.Find("Soul Orb Break Effect");
            Transform? vesselsTr = soulOrbTr.Find("Vessels");

            PlayMakerFSM? soulOrbFsm = cameras?.soulOrbFSM ?? FindFsm(soulOrbGo, "Soul Orb Control");
            float liquidY = ComputeLiquidLocalY(mp);
            bool canHeal = mp >= focusMp;
            bool isFull = maxMp > 0 && mp >= maxMp;
            bool showOrbFull = isFull && !soulLimited && !boundSoul;

            if (soulOrbFsm != null && soulOrbFsm.Fsm != null && soulOrbFsm.Fsm.Initialized)
            {
                SetFsmGameObject(soulOrbFsm, "Hero", hero.gameObject);
                SetFsmGameObject(soulOrbFsm, "Hero Effects", hero.transform.Find("Hero Effects")?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Soul Burst", soulBurstTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Vessel Folder", vesselsTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Fill", fillTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Get Flash", whiteFlashTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Can Heal Particles", canHealParticlesTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Eyes", eyesTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Liquid", liquidTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Orb Anim", orbAnimTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Orb Full", orbFullTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Burst Anim", burstAnimTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Binding Cap", bindingCapTr?.gameObject);
                SetFsmGameObject(soulOrbFsm, "Binding Cap Full", bindingCapFullTr?.gameObject);

                SetFsmInt(soulOrbFsm, "MP", mp);
                SetFsmInt(soulOrbFsm, "Max MP", maxMp);
                SetFsmInt(soulOrbFsm, "Focus MP amount", focusMp);
                SetFsmFloat(soulOrbFsm, "Liquid Y", liquidY);
                SetFsmBool(soulOrbFsm, "Setting", false);
                SetFsmBool(soulOrbFsm, "Can Focus Anim", canHeal);
                SetFsmBool(soulOrbFsm, "Orb Filled", showOrbFull);

                TransitionFsmSafe(soulOrbFsm, "Idle");
            }

            if (liquidTr != null)
            {
                GameObject liquidGo = liquidTr.gameObject;
                SetMeshRendererEnabled(liquidGo, !isHudOut);
                ResetLocalY(liquidTr, liquidY);

                tk2dSpriteAnimator? anim = liquidGo.GetComponent<tk2dSpriteAnimator>();
                if (anim != null && anim.DefaultClip != null)
                {
                    anim.PlayFromFrame(anim.DefaultClip, 0);
                }

                PlayMakerFSM? liquidControlFsm = FindFsm(liquidGo, "Liquid Control");
                if (liquidControlFsm != null && liquidControlFsm.Fsm != null && liquidControlFsm.Fsm.Initialized)
                {
                    TransitionFsmSafe(liquidControlFsm, "Idle");
                }

                SetTk2dSpriteColor(liquidGo, canHeal ? Color.white : LiquidCantHealColor);
            }

            if (bindingCapTr != null)
            {
                bindingCapTr.gameObject.SetActive(boundSoul);
            }

            if (bindingCapFullTr != null)
            {
                bool showBoundFull = boundSoul && isFull;
                bindingCapFullTr.gameObject.SetActive(showBoundFull);
                if (showBoundFull)
                {
                    PlayMakerFSM? boundCapFsm = FindFsm(bindingCapFullTr.gameObject, "FSM");
                    TransitionFsmSafe(boundCapFsm, "Play Full");
                }
            }

            if (orbFullTr != null)
            {
                orbFullTr.gameObject.SetActive(showOrbFull);
            }

            if (eyesTr != null)
            {
                GameObject eyesGo = eyesTr.gameObject;
                StopAndDestroyITweens(eyesGo);

                bool eyesOver = mp >= MinEyeOpenMp;
                PlayMakerFSM? eyesFsm = FindFsm(eyesGo, "Eyes Control");
                if (eyesFsm != null && eyesFsm.Fsm != null && eyesFsm.Fsm.Initialized)
                {
                    SetFsmInt(eyesFsm, "MP", mp);
                    TransitionFsmSafe(eyesFsm, eyesOver ? "Start Over" : "Start Under");
                }

                SetTk2dSpriteColor(eyesGo, eyesOver ? Color.white : EyesHiddenColor);
                eyesTr.localScale = eyesOver ? Vector3.one : new Vector3(1f, 0.4f, 1f);
            }

            if (vesselsTr != null)
            {
                PlayMakerFSM? updateVesselsFsm = FindFsm(vesselsTr.gameObject, "Update Vessels");
                if (updateVesselsFsm != null && updateVesselsFsm.Fsm != null && updateVesselsFsm.Fsm.Initialized)
                {
                    for (int v = 1; v <= 4; v++)
                    {
                        SetFsmGameObject(updateVesselsFsm, $"Vessel {v}", vesselsTr.Find($"Vessel {v}")?.gameObject);
                    }

                    TransitionFsmSafe(updateVesselsFsm, "Idle");
                }

                PlayMakerFSM? drainFsm = FindFsm(vesselsTr.gameObject, "Vessel Drain");
                if (drainFsm != null && drainFsm.Fsm != null && drainFsm.Fsm.Initialized)
                {
                    TransitionFsmSafe(drainFsm, "Idle");
                }

                for (int p = 1; p <= 4; p++)
                {
                    StopParticles(vesselsTr.Find($"Particle {p}")?.gameObject);
                }

                StopParticles(vesselsTr.Find("Particle Fill")?.gameObject);

                int reserveMax = pd.GetInt("MPReserveMax");
                int reserveMp = pd.GetInt("MPReserve");

                for (int v = 1; v <= 4; v++)
                {
                    Transform? vesselTr = vesselsTr.Find($"Vessel {v}");
                    if (vesselTr == null)
                    {
                        continue;
                    }

                    GameObject vesselGo = vesselTr.gameObject;
                    vesselGo.SetActive(true);
                    StopAndDestroyITweens(vesselGo);

                    Transform? bindingTr = vesselTr.Find("Binding");
                    Transform? flashTr = vesselTr.Find("Flash");

                    int emptyAmount = (v - 1) * 33;
                    int fullAmount = v * 33;
                    var resolved = ResolveVesselSteadyState(v, reserveMp, reserveMax, boundSoul);

                    PlayMakerFSM? orbFsm = FindFsm(vesselGo, "vessel_orb");
                    if (orbFsm != null && orbFsm.Fsm != null && orbFsm.Fsm.Initialized)
                    {
                        SetFsmGameObject(orbFsm, "Self", vesselGo);
                        SetFsmGameObject(orbFsm, "Binding Cap", bindingTr?.gameObject);
                        SetFsmGameObject(orbFsm, "Flash", flashTr?.gameObject);
                        SetFsmInt(orbFsm, "Empty Amount", emptyAmount);
                        SetFsmInt(orbFsm, "Full Amount", fullAmount);
                        SetFsmInt(orbFsm, "Quarter Amount", emptyAmount + 8);
                        SetFsmInt(orbFsm, "Half Amount", emptyAmount + 16);
                        SetFsmInt(orbFsm, "3Quarter Amount", emptyAmount + 24);
                        SetFsmInt(orbFsm, "Reserve MP Max", reserveMax);
                        SetFsmInt(orbFsm, "Reserve MP", reserveMp);
                        SetFsmBool(orbFsm, "Initialised", true);

                        TransitionFsmSafe(orbFsm, resolved.OrbState);
                    }

                    PlayMakerFSM? sizeFsm = FindFsm(vesselGo, "vessel_size");
                    if (sizeFsm != null && sizeFsm.Fsm != null && sizeFsm.Fsm.Initialized)
                    {
                        int sizeFullAmount = reserveMax == fullAmount ? fullAmount + 1 : fullAmount;
                        SetFsmInt(sizeFsm, "Empty Amount", emptyAmount);
                        SetFsmInt(sizeFsm, "Full Amount", sizeFullAmount);
                        SetFsmInt(sizeFsm, "MP Reserve Max", reserveMax);
                        SetFsmInt(sizeFsm, "MP Reserve", reserveMp);

                        TransitionFsmSafe(sizeFsm, resolved.SizeState);
                    }

                    vesselTr.localScale = new Vector3(resolved.ScaleXY, resolved.ScaleXY, 1f);
                    if (bindingTr != null)
                    {
                        bindingTr.gameObject.SetActive(resolved.ShowBindingCap);
                    }

                    SetMeshRendererEnabled(vesselGo, resolved.ShowRenderer);
                    if (resolved.ShowRenderer && resolved.ClipName != null)
                    {
                        PlayTk2dClip(vesselGo, resolved.ClipName);
                    }
                }
            }
        }

        private static void SyncFuryAndCharmEffectsAndIcons(
            HeroController hero,
            GameCameras? cameras,
            Transform? hudCameraRoot,
            Transform? hudCanvas,
            PlayerData pd)
        {
            Transform? charmEffectsTr = hero.transform.Find("Charm Effects");
            Transform? attacksTr = hero.transform.Find("Attacks");

            bool shouldFury = ShouldActivateFury(
                pd.GetBool("equippedCharm_6"),
                pd.GetInt("health"),
                pd.GetBool("equippedCharm_27"),
                pd.GetInt("healthBlue"));

            // Fury charm & attack multipliers/tint
            Transform? slashTr = attacksTr?.Find("Slash");
            Transform? altSlashTr = attacksTr?.Find("AltSlash");
            Transform? downSlashTr = attacksTr?.Find("DownSlash");
            Transform? upSlashTr = attacksTr?.Find("UpSlash");
            Transform? wallSlashTr = attacksTr?.Find("WallSlash");
            Transform? cycloneTr = attacksTr?.Find("Cyclone Slash");
            Transform? cycloneHitLTr = cycloneTr?.Find("Hits/Hit L");
            Transform? cycloneHitRTr = cycloneTr?.Find("Hits/Hit R");
            Transform? dashSlashTr = attacksTr?.Find("Dash Slash");
            Transform? greatSlashTr = attacksTr?.Find("Great Slash");
            Transform? sharpShadowTr = attacksTr?.Find("Sharp Shadow");

            foreach (string slashName in BasicSlashNames)
            {
                Transform? tr = attacksTr?.Find(slashName);
                if (tr == null)
                {
                    continue;
                }

                NailSlash? nailSlash = tr.GetComponent<NailSlash>();
                nailSlash?.SetFury(shouldFury);

                PlayMakerFSM? dmgFsm = FindFsm(tr.gameObject, "damages_enemy");
                if (dmgFsm != null)
                {
                    SetFsmFloat(dmgFsm, "Multiplier", shouldFury ? 1.75f : 1f);
                }
            }

            foreach (Transform? nailArtHitTr in new[] { cycloneHitLTr, cycloneHitRTr, dashSlashTr, greatSlashTr })
            {
                if (nailArtHitTr == null)
                {
                    continue;
                }

                PlayMakerFSM? artDmgFsm = FindFsm(nailArtHitTr.gameObject, "nailart_damage");
                if (artDmgFsm != null)
                {
                    SetFsmBool(artDmgFsm, "Fury", shouldFury);
                }
            }

            Color nailArtColor = shouldFury ? FuryNailArtColor : Color.white;
            foreach (Transform? nailArtSpriteTr in new[] { cycloneTr, dashSlashTr, greatSlashTr })
            {
                if (nailArtSpriteTr != null)
                {
                    SetTk2dSpriteColor(nailArtSpriteTr.gameObject, nailArtColor);
                }
            }

            Transform? furyObjTr = charmEffectsTr?.Find("Fury");
            Transform? furyVignetteTr = hudCameraRoot?.Find("fury_effects_v2");
            if (charmEffectsTr != null)
            {
                PlayMakerFSM? furyFsm = FindFsm(charmEffectsTr.gameObject, "Fury");
                if (furyFsm != null && furyFsm.Fsm != null && furyFsm.Fsm.Initialized)
                {
                    SetFsmGameObject(furyFsm, "Knight", hero.gameObject);
                    SetFsmGameObject(furyFsm, "Attacks", attacksTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Slash", slashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Alt Slash", altSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Down Slash", downSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Up Slash", upSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Wall Slash", wallSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Cyclone Slash", cycloneTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Cyclone Hit L", cycloneHitLTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Cyclone Hit R", cycloneHitRTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Dash Slash", dashSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Great Slash", greatSlashTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Fury Object", furyObjTr?.gameObject);
                    SetFsmGameObject(furyFsm, "Fury Vignette", furyVignetteTr?.gameObject);
                    SetFsmInt(furyFsm, "HP", pd.GetInt("health"));
                    SetFsmInt(furyFsm, "Blue Health", pd.GetInt("healthBlue"));
                    SetFsmBool(furyFsm, "Has Blue Health", pd.GetInt("healthBlue") > 0);
                    SetFsmBool(furyFsm, "Joni Equipped", pd.GetBool("equippedCharm_27"));

                    TransitionFsmSafe(furyFsm, shouldFury ? "Stay Furied" : "Idle");
                }
            }

            if (furyObjTr != null)
            {
                if (shouldFury)
                {
                    StartParticles(furyObjTr.gameObject);
                }
                else
                {
                    StopParticles(furyObjTr.gameObject);
                    StopAudioSources(furyObjTr.gameObject);
                    PlayMakerFSM? ctrlAudio = FindFsm(furyObjTr.gameObject, "Control Audio");
                    TransitionFsmSafe(ctrlAudio, "Stop");
                }
            }

            if (furyVignetteTr != null)
            {
                FadeGroup? fadeGroup = furyVignetteTr.GetComponent<FadeGroup>();
                if (fadeGroup != null)
                {
                    if (shouldFury)
                    {
                        fadeGroup.FadeUp();
                    }
                    else
                    {
                        fadeGroup.FadeDownFast();
                    }
                }
            }

            Transform? h1Tr = hudCanvas?.Find("Health/Health 1");
            if (h1Tr != null)
            {
                Transform? furyParticleTr = h1Tr.Find("Fury Particle");
                Transform? h1IdleTr = h1Tr.Find("Idle");
                PlayMakerFSM? h1FuryFsm = FindFsm(h1Tr.gameObject, "Fury Effects");
                if (h1FuryFsm != null && h1FuryFsm.Fsm != null && h1FuryFsm.Fsm.Initialized)
                {
                    SetFsmGameObject(h1FuryFsm, "Fury Particle", furyParticleTr?.gameObject);
                    SetFsmGameObject(h1FuryFsm, "Idle", h1IdleTr?.gameObject);
                    TransitionFsmSafe(h1FuryFsm, shouldFury ? "Fury" : "Idle");
                }

                if (shouldFury)
                {
                    StartParticles(furyParticleTr?.gameObject);
                    h1IdleTr?.GetComponent<SpriteFlash>()?.FlashingFury();
                }
                else
                {
                    StopParticles(furyParticleTr?.gameObject);
                    h1IdleTr?.GetComponent<SpriteFlash>()?.CancelFlash();
                }
            }

            // Longnail / Mark of Pride
            bool hasLongnail = pd.GetBool("equippedCharm_18");
            bool hasMantis = pd.GetBool("equippedCharm_13");
            foreach (string slashName in SizeModifierSlashNames)
            {
                NailSlash? nailSlash = attacksTr?.Find(slashName)?.GetComponent<NailSlash>();
                if (nailSlash != null)
                {
                    nailSlash.SetLongnail(hasLongnail);
                    nailSlash.SetMantis(hasMantis);
                }
            }

            if (charmEffectsTr != null)
            {
                PlayMakerFSM? slashSizeFsm = FindFsm(charmEffectsTr.gameObject, "Slash Size Modifiers");
                TransitionFsmSafe(slashSizeFsm, hasLongnail ? "Equipped 2" : "Unequipped 2");
            }

            // Nail damage & Strength & Nail Binding
            bool boundNail = BossSequenceController.IsInSequence && BossSequenceController.BoundNail;
            int boundNailDamage = ReadBoundValueOrDefault(boundNail, () => BossSequenceController.BoundNailDamage);
            var damageTuple = ComputeSlashAndBeamDamage(
                pd.GetInt("nailDamage"),
                boundNail,
                boundNailDamage,
                pd.GetBool("equippedCharm_25"),
                pd.GetBool("brokenCharm_25"));
            pd.SetInt("beamDamage", damageTuple.BeamDamage);

            foreach (Transform? slashDmgTr in new[] { slashTr, altSlashTr, downSlashTr, upSlashTr, wallSlashTr, cycloneHitLTr, cycloneHitRTr })
            {
                if (slashDmgTr == null)
                {
                    continue;
                }

                PlayMakerFSM? dmgFsm = FindFsm(slashDmgTr.gameObject, "damages_enemy");
                if (dmgFsm != null)
                {
                    SetFsmInt(dmgFsm, "damageDealt", damageTuple.FinalNailDamage);
                }
            }

            foreach (Transform? artDmgTr in new[] { dashSlashTr, greatSlashTr })
            {
                if (artDmgTr == null)
                {
                    continue;
                }

                PlayMakerFSM? dmgFsm = FindFsm(artDmgTr.gameObject, "damages_enemy");
                if (dmgFsm != null)
                {
                    SetFsmInt(dmgFsm, "damageDealt", damageTuple.FinalNailDamage * 2);
                }
            }

            if (attacksTr != null)
            {
                PlayMakerFSM? setSlashDmgFsm = FindFsm(attacksTr.gameObject, "Set Slash Damage");
                TransitionFsmSafe(setSlashDmgFsm, "Idle");
            }

            // Heavy Blow
            bool hasHeavyBlow = pd.GetBool("equippedCharm_15");
            foreach (Transform? recoilTr in new[] { slashTr, altSlashTr, downSlashTr, upSlashTr })
            {
                if (recoilTr != null)
                {
                    PlayMakerFSM? dmgFsm = FindFsm(recoilTr.gameObject, "damages_enemy");
                    if (dmgFsm != null)
                    {
                        SetFsmFloat(dmgFsm, "magnitudeMult", hasHeavyBlow ? 1.75f : 1f);
                    }
                }
            }

            if (greatSlashTr != null)
            {
                PlayMakerFSM? dmgFsm = FindFsm(greatSlashTr.gameObject, "damages_enemy");
                if (dmgFsm != null)
                {
                    SetFsmFloat(dmgFsm, "magnitudeMult", hasHeavyBlow ? 2f : 1.5f);
                }
            }

            foreach (Transform? cycloneHitTr in new[] { cycloneHitLTr, cycloneHitRTr })
            {
                if (cycloneHitTr != null)
                {
                    PlayMakerFSM? dmgFsm = FindFsm(cycloneHitTr.gameObject, "damages_enemy");
                    if (dmgFsm != null)
                    {
                        SetFsmFloat(dmgFsm, "magnitudeMult", hasHeavyBlow ? 2.5f : 1f);
                    }
                }
            }

            if (charmEffectsTr != null)
            {
                PlayMakerFSM? recoilFsm = FindFsm(charmEffectsTr.gameObject, "Enemy Recoil Up");
                TransitionFsmSafe(recoilFsm, hasHeavyBlow ? "Equipped" : "Unequipped");
            }

            // Sharp Shadow (+ Dashmaster)
            if (sharpShadowTr != null)
            {
                int sharpDmg = ComputeSharpShadowDamage(pd.GetInt("nailDamage"), pd.GetBool("equippedCharm_31"));
                PlayMakerFSM? sharpDmgFsm = FindFsm(sharpShadowTr.gameObject, "damages_enemy");
                if (sharpDmgFsm != null)
                {
                    SetFsmInt(sharpDmgFsm, "damageDealt", sharpDmg);
                }
            }

            if (attacksTr != null)
            {
                PlayMakerFSM? setSharpFsm = FindFsm(attacksTr.gameObject, "Set Sharp Shadow Damage");
                TransitionFsmSafe(setSharpFsm, "Idle");
            }

            // Spell Twister
            int spellCost = ComputeSpellCost(pd.GetBool("equippedCharm_33"));
            PlayMakerFSM? spellCtrl = FindFsm(hero.gameObject, "Spell Control");
            if (spellCtrl != null)
            {
                SetFsmInt(spellCtrl, "MP Cost", spellCost);
            }

            if (charmEffectsTr != null)
            {
                PlayMakerFSM? setSpellCostFsm = FindFsm(charmEffectsTr.gameObject, "Set Spell Cost");
                TransitionFsmSafe(setSpellCostFsm, "Idle");
            }

            // Godhome binding icons
            bool boundCharms = BossSequenceController.IsInSequence && BossSequenceController.BoundCharms;
            SyncBoundIcon(hudCanvas?.Find("Extras/Bound Nail Icon"), boundNail);
            SyncBoundIcon(hudCanvas?.Find("Extras/Bound Charms Icon"), boundCharms);

            // Baldur Shell HUD & shield FSM
            Transform? blockerHudTr = hudCanvas?.Find("Extras/Blocker HUD Icon");
            Transform? blockerShieldTr = charmEffectsTr?.Find("Blocker Shield");
            if (blockerHudTr != null)
            {
                FsmGameObject? blockerHudGlobal = FsmVariables.GlobalVariables?.FindFsmGameObject("Blocker HUD");
                if (blockerHudGlobal != null)
                {
                    blockerHudGlobal.Value = blockerHudTr.gameObject;
                }
            }

            bool blockerEquipped = pd.GetBool("equippedCharm_5");
            int blockerHits = pd.GetInt("blockerHits");
            if (!blockerEquipped)
            {
                SetMeshRendererEnabled(blockerHudTr?.gameObject, false);
                if (blockerShieldTr != null)
                {
                    PlayMakerFSM? shieldCtrl = FindFsm(blockerShieldTr.gameObject, "Control");
                    TransitionFsmSafe(shieldCtrl, "Unequipped");
                }
            }
            else
            {
                if (blockerShieldTr != null)
                {
                    PlayMakerFSM? shieldCtrl = FindFsm(blockerShieldTr.gameObject, "Control");
                    TransitionFsmSafe(shieldCtrl, "Equipped");
                    SetMeshRendererEnabled(blockerShieldTr.Find("Shell Anim")?.gameObject, false);
                }

                if (blockerHudTr != null)
                {
                    SetMeshRendererEnabled(blockerHudTr.gameObject, true);
                    PlayTk2dClip(blockerHudTr.gameObject, ResolveBlockerHudClip(blockerHits));
                }
            }

            // GeoCounter
            Transform? geoCounterTr = hudCanvas?.Find("Geo Counter");
            GeoCounter? geoCounter = cameras?.geoCounter ?? geoCounterTr?.GetComponent<GeoCounter>();
            if (geoCounter != null)
            {
                if (geoCounter.playerData == null)
                {
                    geoCounter.playerData = pd;
                }

                if (geoCounter.geoTextMesh != null)
                {
                    geoCounter.NewSceneRefresh();
                }
            }

            if (geoCounterTr != null)
            {
                PlayMakerFSM? geoCounterFsm = FindFsm(geoCounterTr.gameObject, "Geo Counter");
                TransitionFsmSafe(geoCounterFsm, "Idle");

                Transform? geoSpriteTr = geoCounterTr.Find("Geo Sprite");
                SetMeshRendererEnabled(geoSpriteTr?.gameObject, true);

                TransitionFsmSafe(FindFsm(geoCounterTr.Find("Add Text")?.gameObject, "Fader"), "Init");
                TransitionFsmSafe(FindFsm(geoCounterTr.Find("Subtract Text")?.gameObject, "Fader"), "Init");
            }
        }

        private static void SyncBoundIcon(Transform? iconTr, bool show)
        {
            if (iconTr == null)
            {
                return;
            }

            PlayMakerFSM? fsm = FindFsm(iconTr.gameObject, "Control Mesh Renderer");
            TransitionFsmSafe(fsm, show ? "Show" : "Hide");
            SetMeshRendererEnabled(iconTr.gameObject, show);
        }

        private static void ResolveBlueHealthPrefabs(
            PlayMakerFSM blueControlFsm,
            out GameObject? normalPrefab,
            out GameObject? hivePrefab)
        {
            normalPrefab = null;
            hivePrefab = null;

            GameObject? fsmNormalVar = blueControlFsm.FsmVariables?.FindFsmGameObject("Blue HP Object")?.Value;
            if (fsmNormalVar != null)
            {
                normalPrefab = fsmNormalVar;
            }

            FsmState[]? states = blueControlFsm.FsmStates;
            if (states == null)
            {
                return;
            }

            foreach (FsmState state in states)
            {
                if (state.Actions == null)
                {
                    continue;
                }

                foreach (FsmStateAction action in state.Actions)
                {
                    GameObject? candidate = action switch
                    {
                        SetGameObject setGo => setGo.gameObject?.Value,
                        CreateObject createGo => createGo.gameObject?.Value,
                        _ => null
                    };

                    if (candidate == null)
                    {
                        continue;
                    }

                    if (candidate.name.IndexOf("Hive", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hivePrefab ??= candidate;
                    }
                    else
                    {
                        normalPrefab ??= candidate;
                    }
                }
            }
        }

        private static void ResetFadedOverlay(Transform? target, string fsmName, string targetState)
        {
            if (target == null)
            {
                return;
            }

            StopAndDestroyITweens(target.gameObject);
            PlayMakerFSM? fsm = FindFsm(target.gameObject, fsmName);
            TransitionFsmSafe(fsm, targetState);

            FadeGroup? fadeGroup = target.GetComponent<FadeGroup>();
            fadeGroup?.FadeDownFast();

            SpriteRenderer? spriteRenderer = target.GetComponent<SpriteRenderer>();
            if (spriteRenderer != null)
            {
                Color c = spriteRenderer.color;
                c.a = 0f;
                spriteRenderer.color = c;
            }
        }

        private static void EnsureFsmStarted(PlayMakerFSM fsm)
        {
            if (fsm == null || fsm.Fsm == null)
            {
                return;
            }

            if (!fsm.Fsm.Initialized)
            {
                fsm.Fsm.Init(fsm);
            }

            if (!fsm.Fsm.Started)
            {
                fsm.Fsm.Start();
            }
        }

        private static void TransitionFsmSafe(PlayMakerFSM? fsm, string targetState)
        {
            if (fsm == null || fsm.Fsm == null || !fsm.Fsm.Initialized || string.IsNullOrEmpty(targetState))
            {
                return;
            }

            if (fsm.Fsm.GetState(targetState) == null)
            {
                return;
            }

            ClearDelayedEvents(fsm);
            if (!string.Equals(fsm.ActiveStateName, targetState, StringComparison.Ordinal))
            {
                fsm.SetState(targetState);
            }
        }

        private static void ClearDelayedEvents(PlayMakerFSM fsm)
        {
            if (FsmDelayedEventsField == null || fsm.Fsm == null)
            {
                return;
            }

            if (FsmDelayedEventsField.GetValue(fsm.Fsm) is IList delayedEvents)
            {
                delayedEvents.Clear();
            }
        }

        private static PlayMakerFSM? FindFsm(GameObject? go, string fsmName)
        {
            if (go == null)
            {
                return null;
            }

            foreach (PlayMakerFSM fsm in go.GetComponents<PlayMakerFSM>())
            {
                if (string.Equals(fsm.FsmName, fsmName, StringComparison.Ordinal))
                {
                    return fsm;
                }
            }

            return null;
        }

        private static void StopAndDestroyITweens(GameObject go)
        {
            iTween.Stop(go);
            foreach (iTween tween in go.GetComponents<iTween>())
            {
                UnityEngine.Object.DestroyImmediate(tween);
            }
        }

        private static void ResetLocalY(Transform? tr, float localY)
        {
            if (tr == null)
            {
                return;
            }

            Vector3 pos = tr.localPosition;
            tr.localPosition = new Vector3(pos.x, localY, pos.z);
        }

        private static void SetMeshRendererEnabled(GameObject? go, bool enabled)
        {
            if (go == null)
            {
                return;
            }

            MeshRenderer? renderer = go.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = enabled;
            }
        }

        private static void SetTk2dSpriteColor(GameObject? go, Color color)
        {
            if (go == null)
            {
                return;
            }

            tk2dSprite? sprite = go.GetComponent<tk2dSprite>();
            if (sprite != null)
            {
                sprite.color = color;
            }
        }

        private static void PlayTk2dClip(GameObject? go, string clipName)
        {
            if (go == null || string.IsNullOrEmpty(clipName))
            {
                return;
            }

            tk2dSpriteAnimator? anim = go.GetComponent<tk2dSpriteAnimator>();
            if (anim != null && anim.GetClipByName(clipName) != null)
            {
                anim.Play(clipName);
            }
        }

        private static void StartParticles(GameObject? go)
        {
            if (go == null)
            {
                return;
            }

            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                var emission = ps.emission;
                emission.enabled = true;
                if (!ps.isPlaying)
                {
                    ps.Play(withChildren: true);
                }
            }
        }

        private static void StopParticles(GameObject? go)
        {
            if (go == null)
            {
                return;
            }

            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(includeInactive: true))
            {
                var emission = ps.emission;
                emission.enabled = false;
                ps.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        private static void StopAudioSources(GameObject? go)
        {
            if (go == null)
            {
                return;
            }

            foreach (AudioSource audio in go.GetComponentsInChildren<AudioSource>(includeInactive: true))
            {
                audio.Stop();
            }
        }

        private static void SetFsmGameObject(PlayMakerFSM fsm, string name, GameObject? value)
        {
            FsmGameObject? v = fsm.FsmVariables?.FindFsmGameObject(name);
            if (v != null)
            {
                v.Value = value;
            }
        }

        private static void SetFsmBool(PlayMakerFSM fsm, string name, bool value)
        {
            FsmBool? v = fsm.FsmVariables?.FindFsmBool(name);
            if (v != null)
            {
                v.Value = value;
            }
        }

        private static void SetFsmInt(PlayMakerFSM fsm, string name, int value)
        {
            FsmInt? v = fsm.FsmVariables?.FindFsmInt(name);
            if (v != null)
            {
                v.Value = value;
            }
        }

        private static void SetFsmFloat(PlayMakerFSM fsm, string name, float value)
        {
            FsmFloat? v = fsm.FsmVariables?.FindFsmFloat(name);
            if (v != null)
            {
                v.Value = value;
            }
        }

        private static void SetFsmString(PlayMakerFSM fsm, string name, string value)
        {
            FsmString? v = fsm.FsmVariables?.FindFsmString(name);
            if (v != null)
            {
                v.Value = value;
            }
        }
    }
}
