using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Original PlayMaker AddSystemEvents slots; pinned independently of a saved graph.
    internal static class CanonicalSystemEventBindings
    {
        internal static IReadOnlyDictionary<string, string> Snapshot() => new Dictionary<string, string> {
            ["<ApplicationFocus>k__BackingField"] = "APPLICATION FOCUS",
            ["<ApplicationPause>k__BackingField"] = "APPLICATION PAUSE",
            ["<ApplicationQuit>k__BackingField"] = "APPLICATION QUIT",
            ["<BecameInvisible>k__BackingField"] = "BECAME INVISIBLE",
            ["<BecameVisible>k__BackingField"] = "BECAME VISIBLE",
            ["<CollisionEnter2D>k__BackingField"] = "COLLISION ENTER 2D",
            ["<CollisionEnter>k__BackingField"] = "COLLISION ENTER",
            ["<CollisionExit2D>k__BackingField"] = "COLLISION EXIT 2D",
            ["<CollisionExit>k__BackingField"] = "COLLISION EXIT",
            ["<CollisionStay2D>k__BackingField"] = "COLLISION STAY 2D",
            ["<CollisionStay>k__BackingField"] = "COLLISION STAY",
            ["<ConnectedToServer>k__BackingField"] = "CONNECTED TO SERVER",
            ["<ControllerColliderHit>k__BackingField"] = "CONTROLLER COLLIDER HIT",
            ["<Disable>k__BackingField"] = "DISABLE",
            ["<DisconnectedFromServer>k__BackingField"] = "DISCONNECTED FROM SERVER",
            ["<FailedToConnect>k__BackingField"] = "FAILED TO CONNECT",
            ["<FailedToConnectToMasterServer>k__BackingField"] = "FAILED TO CONNECT TO MASTER SERVER",
            ["<Finished>k__BackingField"] = "FINISHED",
            ["<JointBreak2D>k__BackingField"] = "JOINT BREAK 2D",
            ["<JointBreak>k__BackingField"] = "JOINT BREAK",
            ["<LevelLoaded>k__BackingField"] = "LEVEL LOADED",
            ["<MasterServerEvent>k__BackingField"] = "MASTER SERVER EVENT",
            ["<MouseDown>k__BackingField"] = "MOUSE DOWN",
            ["<MouseDrag>k__BackingField"] = "MOUSE DRAG",
            ["<MouseEnter>k__BackingField"] = "MOUSE ENTER",
            ["<MouseExit>k__BackingField"] = "MOUSE EXIT",
            ["<MouseOver>k__BackingField"] = "MOUSE OVER",
            ["<MouseUp>k__BackingField"] = "MOUSE UP",
            ["<MouseUpAsButton>k__BackingField"] = "MOUSE UP AS BUTTON",
            ["<NetworkInstantiate>k__BackingField"] = "NETWORK INSTANTIATE",
            ["<ParticleCollision>k__BackingField"] = "PARTICLE COLLISION",
            ["<PlayerConnected>k__BackingField"] = "PLAYER CONNECTED",
            ["<PlayerDisconnected>k__BackingField"] = "PLAYER DISCONNECTED",
            ["<ServerInitialized>k__BackingField"] = "SERVER INITIALIZED",
            ["<TriggerEnter2D>k__BackingField"] = "TRIGGER ENTER 2D",
            ["<TriggerEnter>k__BackingField"] = "TRIGGER ENTER",
            ["<TriggerExit2D>k__BackingField"] = "TRIGGER EXIT 2D",
            ["<TriggerExit>k__BackingField"] = "TRIGGER EXIT",
            ["<TriggerStay2D>k__BackingField"] = "TRIGGER STAY 2D",
            ["<TriggerStay>k__BackingField"] = "TRIGGER STAY",
            ["<UiBeginDrag>k__BackingField"] = "UI BEGIN DRAG",
            ["<UiBoolValueChanged>k__BackingField"] = "UI BOOL VALUE CHANGED",
            ["<UiClick>k__BackingField"] = "UI CLICK",
            ["<UiDrag>k__BackingField"] = "UI DRAG",
            ["<UiDrop>k__BackingField"] = "UI DROP",
            ["<UiEndDrag>k__BackingField"] = "UI END DRAG",
            ["<UiEndEdit>k__BackingField"] = "UI END EDIT",
            ["<UiFloatValueChanged>k__BackingField"] = "UI FLOAT VALUE CHANGED",
            ["<UiIntValueChanged>k__BackingField"] = "UI INT VALUE CHANGED",
            ["<UiPointerClick>k__BackingField"] = "UI POINTER CLICK",
            ["<UiPointerDown>k__BackingField"] = "UI POINTER DOWN",
            ["<UiPointerEnter>k__BackingField"] = "UI POINTER ENTER",
            ["<UiPointerExit>k__BackingField"] = "UI POINTER EXIT",
            ["<UiPointerUp>k__BackingField"] = "UI POINTER UP",
            ["<UiVector2ValueChanged>k__BackingField"] = "UI VECTOR2 VALUE CHANGED",
        };
        internal static string SchemaIdentity => string.Join("\n", Snapshot().OrderBy(pair => pair.Key, System.StringComparer.Ordinal)
            .Select(pair => pair.Key + "=" + pair.Value));
    }
}
