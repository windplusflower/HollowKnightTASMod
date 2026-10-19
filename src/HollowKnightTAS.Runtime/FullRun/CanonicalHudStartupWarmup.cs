using System;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original HUD initialization is allowed to derive current PD. No state,
    // Start, iterator or gameplay action is invoked manually or suppressed.
    internal sealed class CanonicalHudStartupWarmup : IDisposable
    {
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private GameObject? hud;
        private HUDCamera? camera;
        private Transform? charms;
        private PlayMakerFSM[]? fsms;
        private bool complete;
        private int firstFrame;
        private readonly CanonicalGeoColdTemplate geoTemplate = new CanonicalGeoColdTemplate();
        private readonly CanonicalMarkerColdTemplate markerTemplate = new CanonicalMarkerColdTemplate();
        private CanonicalMapTextStartup? mapText;
        internal CanonicalGeoColdTemplate GeoTemplate => geoTemplate;
        // Startup only retains the actual serialized cold definition. The
        // three execution paths normalize the live hint at each unloaded B.
        internal CanonicalMarkerColdTemplate MarkerColdDefinition => markerTemplate;

        internal bool Advance()
        {
            if (complete) return true;
            if (hud == null)
            {
                var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras
                    ?? throw new InvalidOperationException("HUD startup has no original GameCameras.");
                hud = cameras.hudCamera.gameObject;
                camera = hud.GetComponent<HUDCamera>() ?? throw new InvalidOperationException("Original HUDCamera lifecycle owner is absent.");
                charms = hud.transform.Find("Inventory/Charms") ?? throw new InvalidOperationException("HUD startup Charms owner is absent.");
                var vignette = hud.GetComponentsInChildren<Transform>(true).Where(node => node.name == "Low Health Vignette").ToArray();
                if (vignette.Length != 1) throw new InvalidOperationException("HUD startup vignette owner is ambiguous.");
                var next = charms.Find("Equipped Charms/Next Dot") ?? throw new InvalidOperationException("HUD startup Next Dot is absent.");
                fsms = new[] { vignette[0].GetComponents<PlayMakerFSM>().Single(fsm => ((Fsm)Raw.GetValue(fsm)).Name == "Vignette Control"),
                    next.GetComponents<PlayMakerFSM>().Single(fsm => ((Fsm)Raw.GetValue(fsm)).Name == "Up and Down"),
                    next.GetComponents<PlayMakerFSM>().Single(fsm => ((Fsm)Raw.GetValue(fsm)).Name == "charm_unequip_shift"),
                    charms.GetComponents<PlayMakerFSM>().Single(fsm => ((Fsm)Raw.GetValue(fsm)).Name == "Initial Activation") };
                firstFrame = Time.frameCount;
                var marker = CanonicalMarkerDirectory.Current();
                geoTemplate.Capture(cameras.geoCounter, marker, incoming => markerTemplate.Capture(marker, incoming));
                Modding.Logger.Log("[HKTAS] Original Geo before HUD startup: " + Newtonsoft.Json.JsonConvert.SerializeObject(LoadingGeoLifecycleObservation.Capture()));
                Modding.Logger.Log("[HKTAS] Original Map before HUD startup: " + Newtonsoft.Json.JsonConvert.SerializeObject(LoadingMapLifecycleObservation.Capture()));
                if (!camera.enabled || !cameras.gameObject.activeInHierarchy)
                    throw new InvalidOperationException("Original HUD startup camera/ancestor is disabled.");
                mapText = new CanonicalMapTextStartup(hud.transform);
                // Already active target initialization is never toggled/restarted.
                if (!hud.activeSelf) hud.SetActive(true);
                if (!hud.activeInHierarchy) throw new InvalidOperationException("Original HUD startup ancestor is inactive.");
                Modding.Logger.Log("[HKTAS] Waiting for original HUD initialization and original MoveMenuToHUDCamera Invoke.");
                return false;
            }
            mapText!.ThrowIfFailed();
            if (Time.frameCount - firstFrame > 120) {
                if (!mapText!.Complete) mapText.RejectIncomplete();
                throw new InvalidOperationException("Original HUD startup exceeded 120 native frames.");
            }
            if (!hud.activeInHierarchy || camera == null || !camera.enabled)
                throw new InvalidOperationException("Original HUD startup camera was disabled before completion.");
            if (camera.IsInvoking("MoveMenuToHUDCamera") || charms!.gameObject.activeSelf
                || fsms!.Any(fsm => !((Fsm)Raw.GetValue(fsm)).Started)) return false;
            if (!mapText!.Complete) return false;
            foreach (var fsm in fsms!) {
                var flags = BoundaryMonoLifecycle.Read(fsm);
                if (flags[0] != 1 || flags[1] != 1) return false;
            }
            var activation = (Fsm)Raw.GetValue(fsms!.Last());
            if (activation.ActiveStateName != "State 2")
                throw new InvalidOperationException("Original Charms initialization did not complete its native Wait/disable state.");
            complete = true;
            Modding.Logger.Log("[HKTAS] Original Geo after HUD startup: " + Newtonsoft.Json.JsonConvert.SerializeObject(LoadingGeoLifecycleObservation.Capture()));
            Modding.Logger.Log("[HKTAS] Original Map after HUD startup: " + Newtonsoft.Json.JsonConvert.SerializeObject(LoadingMapLifecycleObservation.Capture()));
            Modding.Logger.Log("[HKTAS] Original HUD initialization completed; original PD derivation preserved and Invoke finished naturally.");
            return true;
        }
        public void Dispose() { mapText?.Dispose(); markerTemplate.Dispose(); geoTemplate.Dispose(); }
    }
}
