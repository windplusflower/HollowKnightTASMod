using System;
using System.Collections;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Canonical boundary reset for HUD Area Title banner.
    // In cold state (e.g. at boundary B), Area Title is inactive, its PlayMakerFSM
    // actions are unexpanded (null), and no TMP SubMesh objects or dynamic meshes exist.
    // Native component lifecycle flags (enabled/active) are preserved without
    // unparking or re-enabling, avoiding unregistered DelayedStartCall native callbacks.
    internal static class CanonicalAreaTitleBoundaryPolicy
    {
        private static readonly FieldInfo FsmField = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("PlayMakerFSM.fsm");
        private static readonly FieldInfo StatesField = typeof(Fsm).GetField("states", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.states");
        private static readonly FieldInfo ActionsField = typeof(FsmState).GetField("actions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.actions");
        private static readonly FieldInfo ActiveActionField = typeof(FsmState).GetField("activeAction", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.activeAction");
        private static readonly FieldInfo ActiveActionIndexField = typeof(FsmState).GetField("activeActionIndex", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.activeActionIndex");
        private static readonly FieldInfo StateTimeField = typeof(FsmState).GetField("<StateTime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.<StateTime>k__BackingField");
        private static readonly FieldInfo RealStartTimeField = typeof(FsmState).GetField("<RealStartTime>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.<RealStartTime>k__BackingField");
        private static readonly FieldInfo LoopCountField = typeof(FsmState).GetField("<loopCount>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.<loopCount>k__BackingField");
        private static readonly FieldInfo ActiveStateBoolField = typeof(FsmState).GetField("active", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.active");
        private static readonly FieldInfo FinishedStateBoolField = typeof(FsmState).GetField("finished", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("FsmState.finished");
        private static readonly FieldInfo ActiveActionsListField = typeof(FsmState).GetField("activeActions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo FinishedActionsListField = typeof(FsmState).GetField("_finishedActions", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo ActiveStateField = typeof(Fsm).GetField("activeState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.activeState");
        private static readonly FieldInfo ActiveStateNameField = typeof(Fsm).GetField("activeStateName", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.activeStateName");
        private static readonly FieldInfo PreviousActiveStateField = typeof(Fsm).GetField("previousActiveState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.previousActiveState");
        private static readonly FieldInfo SwitchToStateField = typeof(Fsm).GetField("switchToState", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.switchToState");
        private static readonly FieldInfo StartedField = typeof(Fsm).GetField("<Started>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.<Started>k__BackingField");
        private static readonly FieldInfo FinishedField = typeof(Fsm).GetField("<Finished>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.<Finished>k__BackingField");
        private static readonly FieldInfo ActiveStateEnteredField = typeof(Fsm).GetField("activeStateEntered", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException("Fsm.activeStateEntered");
        private static readonly FieldInfo DelayedEventsField = typeof(Fsm).GetField("delayedEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo UpdateEventsField = typeof(Fsm).GetField("updateEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo RemoveEventsField = typeof(Fsm).GetField("removeEvents", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        private static readonly FieldInfo SubMeshMeshField = typeof(TMPro.TMP_SubMesh).GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TextMeshField = typeof(TMPro.TMP_Text).GetField("m_mesh", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TextInfoField = typeof(TMPro.TMP_Text).GetField("m_textInfo", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo TextField = typeof(TMPro.TMP_Text).GetField("m_text", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo MaterialReferencesField = typeof(TMPro.TMP_Text).GetField("m_materialReferences", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo SubTextObjectsField = typeof(TMPro.TextMeshPro).GetField("m_subTextObjects", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static void Reset()
        {
            GameObject? go = null;
            var instance = AreaTitle.instance;
            if (instance != null) go = instance.gameObject;
            if (go == null)
            {
                var cameras = GameCameras.instance;
                var holder = cameras?.hudCamera?.transform.Find("Area Title Holder");
                go = holder?.Find("Area Title")?.gameObject;
            }
            if (go == null) return;

            // 1. Destroy dynamically generated TMP SubMesh child objects and their dynamic meshes
            var subMeshes = go.GetComponentsInChildren<TMPro.TMP_SubMesh>(true);
            foreach (var subMesh in subMeshes)
            {
                if (subMesh != null)
                {
                    if (SubMeshMeshField?.GetValue(subMesh) is Mesh m)
                    {
                        UnityEngine.Object.DestroyImmediate(m);
                    }
                    if (subMesh.gameObject != null)
                    {
                        UnityEngine.Object.DestroyImmediate(subMesh.gameObject);
                    }
                }
            }

            // 2. Clean TextMeshPro components back to cold pristine state without invoking tmp.mesh getter
            foreach (var tmp in go.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            {
                if (tmp == null) continue;

                var baseMesh = TextMeshField?.GetValue(tmp) as Mesh;

                // Destroy any dynamic submesh meshes still referenced in meshInfo
                if (TextInfoField?.GetValue(tmp) is TMPro.TMP_TextInfo textInfo && textInfo.meshInfo != null)
                {
                    for (var i = 1; i < textInfo.meshInfo.Length; i++)
                    {
                        var m = textInfo.meshInfo[i].mesh;
                        if (m != null && !ReferenceEquals(m, baseMesh))
                        {
                            UnityEngine.Object.DestroyImmediate(m);
                        }
                    }
                }

                // Clear sub-text objects array without reallocating
                if (SubTextObjectsField?.GetValue(tmp) is TMPro.TMP_SubMesh[] subArr)
                {
                    Array.Clear(subArr, 0, subArr.Length);
                }

                // Title Small Main/Sub/Super are modified when area title is shown.
                // Reset them back to cold pristine state using parameterless TMP_TextInfo to avoid triggering get_mesh.
                if (tmp.gameObject.name != "Title Large Main")
                {
                    TextInfoField?.SetValue(tmp, new TMPro.TMP_TextInfo());
                    TextField?.SetValue(tmp, string.Empty);
                }
            }

            // 3. Reset Area Title PlayMakerFSM back to cold unexpanded state without toggling component.enabled
            var fsmComponent = go.GetComponent<PlayMakerFSM>();
            if (fsmComponent != null)
            {
                var currentFsm = (Fsm)FsmField.GetValue(fsmComponent);
                if (currentFsm != null)
                {
                    ActiveStateField.SetValue(currentFsm, null);
                    ActiveStateNameField.SetValue(currentFsm, "");
                    PreviousActiveStateField.SetValue(currentFsm, null);
                    SwitchToStateField.SetValue(currentFsm, null);
                    StartedField.SetValue(currentFsm, false);
                    FinishedField.SetValue(currentFsm, false);
                    ActiveStateEnteredField.SetValue(currentFsm, false);
                    (DelayedEventsField?.GetValue(currentFsm) as IList)?.Clear();
                    (UpdateEventsField?.GetValue(currentFsm) as IList)?.Clear();
                    (RemoveEventsField?.GetValue(currentFsm) as IList)?.Clear();

                    if (StatesField.GetValue(currentFsm) is FsmState[] states)
                    {
                        foreach (var s in states)
                        {
                            ActionsField.SetValue(s, null);
                            ActiveActionField.SetValue(s, null);
                            ActiveActionIndexField.SetValue(s, -1);
                            StateTimeField.SetValue(s, 0f);
                            RealStartTimeField.SetValue(s, 0f);
                            LoopCountField.SetValue(s, 0);
                            ActiveStateBoolField.SetValue(s, false);
                            FinishedStateBoolField.SetValue(s, false);
                            (ActiveActionsListField?.GetValue(s) as IList)?.Clear();
                            (FinishedActionsListField?.GetValue(s) as IList)?.Clear();
                        }
                    }
                }
            }

            // 4. Ensure banner GameObject is returned to cold inactive state
            if (go.activeSelf) go.SetActive(false);
        }
    }
}
