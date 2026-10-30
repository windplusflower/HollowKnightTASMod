using System;
using System.Collections.Generic;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBoundaryHeroFsmPolicy
    {
        private static readonly FieldInfo FsmField = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly FieldInfo FsmGameObjectValueField = typeof(FsmGameObject).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo FsmObjectValueField = typeof(FsmObject).GetField("value", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo FsmGameObjectObjField = typeof(FsmGameObject).GetField("obj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo FsmObjectObjField = typeof(FsmObject).GetField("obj", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo FsmVarNamedVarField = typeof(FsmVar).GetField("namedVar", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo FsmVarObjRefField = typeof(FsmVar).GetField("objectReference", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        private static readonly FieldInfo ActionDataUnityObjectParamsField = typeof(ActionData).GetField("unityObjectParams", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo ActionDataFsmGameObjectParamsField = typeof(ActionData).GetField("fsmGameObjectParams", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo ActionDataFsmObjectParamsField = typeof(ActionData).GetField("fsmObjectParams", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        private static readonly FieldInfo ActionDataFsmVarParamsField = typeof(ActionData).GetField("fsmVarParams", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

        private static readonly Dictionary<Type, FieldInfo[]> ActionFieldsCache = new();

        internal sealed class HeroFsmResetAudit
        {
            public int ScannedFsms { get; set; }
            public int ClearedActionFields { get; set; }
            public int ClearedActionFsmGameObjects { get; set; }
            public int ClearedActionFsmObjects { get; set; }
            public int ClearedActionDataUnityObjects { get; set; }
            public int ClearedActionDataFsmGameObjects { get; set; }
            public int ClearedActionDataFsmObjects { get; set; }
            public int ClearedVariableFsmGameObjects { get; set; }
            public int ClearedVariableFsmObjects { get; set; }
            public int ClearedGlobalVariables { get; set; }
            public List<string> ClearedSlots { get; } = new();
        }

        private static bool IsDestroyedUnityObject(object? obj)
        {
            return obj is UnityEngine.Object u && !ReferenceEquals(u, null) && u == null;
        }

        private static bool CleanFsmGameObject(FsmGameObject? fg)
        {
            if (fg == null) return false;
            var changed = false;
            var val = FsmGameObjectValueField.GetValue(fg);
            if (IsDestroyedUnityObject(val))
            {
                FsmGameObjectValueField.SetValue(fg, null);
                changed = true;
            }
            var objVal = FsmGameObjectObjField.GetValue(fg);
            if (IsDestroyedUnityObject(objVal))
            {
                FsmGameObjectObjField.SetValue(fg, null);
                changed = true;
            }
            return changed;
        }

        private static bool CleanFsmObject(FsmObject? fo)
        {
            if (fo == null) return false;
            var changed = false;
            var val = FsmObjectValueField.GetValue(fo);
            if (IsDestroyedUnityObject(val))
            {
                FsmObjectValueField.SetValue(fo, null);
                changed = true;
            }
            var objVal = FsmObjectObjField.GetValue(fo);
            if (IsDestroyedUnityObject(objVal))
            {
                FsmObjectObjField.SetValue(fo, null);
                changed = true;
            }
            return changed;
        }

        private static bool CleanFsmVar(FsmVar? fv)
        {
            if (fv == null) return false;
            var changed = false;
            var objRef = FsmVarObjRefField.GetValue(fv);
            if (IsDestroyedUnityObject(objRef))
            {
                FsmVarObjRefField.SetValue(fv, null);
                changed = true;
            }
            var named = FsmVarNamedVarField.GetValue(fv);
            if (named is FsmGameObject fg && CleanFsmGameObject(fg)) changed = true;
            else if (named is FsmObject fo && CleanFsmObject(fo)) changed = true;
            return changed;
        }

        private static FieldInfo[] GetActionFields(Type type)
        {
            lock (ActionFieldsCache)
            {
                if (ActionFieldsCache.TryGetValue(type, out var cached)) return cached;
                var list = new List<FieldInfo>();
                for (var cur = type; cur != null && cur != typeof(object); cur = cur.BaseType)
                {
                    var fields = cur.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    foreach (var f in fields)
                    {
                        if (f.IsLiteral) continue;
                        var ft = f.FieldType;
                        if (typeof(UnityEngine.Object).IsAssignableFrom(ft)
                            || typeof(FsmGameObject).IsAssignableFrom(ft)
                            || typeof(FsmObject).IsAssignableFrom(ft)
                            || typeof(FsmVar).IsAssignableFrom(ft)
                            || ft == typeof(FsmGameObject[])
                            || ft == typeof(FsmObject[])
                            || ft == typeof(FsmVar[])
                            || ft == typeof(UnityEngine.Object[]))
                        {
                            list.Add(f);
                        }
                    }
                }
                var arr = list.ToArray();
                ActionFieldsCache[type] = arr;
                return arr;
            }
        }

        internal static HeroFsmResetAudit ResetDestroyedWrappers(BoundaryObjectIdentity identities)
        {
            var audit = new HeroFsmResetAudit();
            var heroFsms = CanonicalHeroFsmBindings.Snapshot(identities);
            audit.ScannedFsms = heroFsms.Count;

            foreach (var pair in heroFsms)
            {
                var fsmPath = pair.Key;
                var component = pair.Value;
                if (FsmField.GetValue(component) is not Fsm fsm) continue;

                // 1. Clean Variables
                if (fsm.Variables != null)
                {
                    var goVars = fsm.Variables.GameObjectVariables;
                    if (goVars != null)
                    {
                        foreach (var fg in goVars)
                        {
                            if (CleanFsmGameObject(fg))
                            {
                                audit.ClearedVariableFsmGameObjects++;
                                audit.ClearedSlots.Add("FsmVar:" + fsmPath + "/" + fg.Name);
                            }
                        }
                    }

                    var objVars = fsm.Variables.ObjectVariables;
                    if (objVars != null)
                    {
                        foreach (var fo in objVars)
                        {
                            if (CleanFsmObject(fo))
                            {
                                audit.ClearedVariableFsmObjects++;
                                audit.ClearedSlots.Add("FsmObjectVar:" + fsmPath + "/" + fo.Name);
                            }
                        }
                    }
                }

                // 2. Clean States
                var states = fsm.States;
                if (states != null)
                {
                    foreach (var state in states)
                    {
                        if (state == null) continue;

                        // 2a. Clean Actions
                        var actions = state.Actions;
                        if (actions != null)
                        {
                            for (var aIdx = 0; aIdx < actions.Length; aIdx++)
                            {
                                var action = actions[aIdx];
                                if (action == null) continue;

                                var fields = GetActionFields(action.GetType());
                                foreach (var field in fields)
                                {
                                    var ft = field.FieldType;
                                    if (typeof(UnityEngine.Object).IsAssignableFrom(ft))
                                    {
                                        var val = field.GetValue(action);
                                        if (IsDestroyedUnityObject(val))
                                        {
                                            field.SetValue(action, null);
                                            audit.ClearedActionFields++;
                                            audit.ClearedSlots.Add("ActionField:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                        }
                                    }
                                    else if (typeof(FsmGameObject).IsAssignableFrom(ft))
                                    {
                                        if (CleanFsmGameObject(field.GetValue(action) as FsmGameObject))
                                        {
                                            audit.ClearedActionFsmGameObjects++;
                                            audit.ClearedSlots.Add("ActionFsmGO:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                        }
                                    }
                                    else if (typeof(FsmObject).IsAssignableFrom(ft))
                                    {
                                        if (CleanFsmObject(field.GetValue(action) as FsmObject))
                                        {
                                            audit.ClearedActionFsmObjects++;
                                            audit.ClearedSlots.Add("ActionFsmObj:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                        }
                                    }
                                    else if (typeof(FsmVar).IsAssignableFrom(ft))
                                    {
                                        if (CleanFsmVar(field.GetValue(action) as FsmVar))
                                        {
                                            audit.ClearedSlots.Add("ActionFsmVar:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                        }
                                    }
                                    else if (ft == typeof(FsmGameObject[]))
                                    {
                                        if (field.GetValue(action) is FsmGameObject[] arr)
                                        {
                                            foreach (var item in arr)
                                            {
                                                if (CleanFsmGameObject(item))
                                                {
                                                    audit.ClearedActionFsmGameObjects++;
                                                    audit.ClearedSlots.Add("ActionFsmGO[]:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                                }
                                            }
                                        }
                                    }
                                    else if (ft == typeof(FsmObject[]))
                                    {
                                        if (field.GetValue(action) is FsmObject[] arr)
                                        {
                                            foreach (var item in arr)
                                            {
                                                if (CleanFsmObject(item))
                                                {
                                                    audit.ClearedActionFsmObjects++;
                                                    audit.ClearedSlots.Add("ActionFsmObj[]:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                                }
                                            }
                                        }
                                    }
                                    else if (ft == typeof(FsmVar[]))
                                    {
                                        if (field.GetValue(action) is FsmVar[] arr)
                                        {
                                            foreach (var item in arr)
                                            {
                                                if (CleanFsmVar(item))
                                                {
                                                    audit.ClearedSlots.Add("ActionFsmVar[]:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name);
                                                }
                                            }
                                        }
                                    }
                                    else if (ft == typeof(UnityEngine.Object[]))
                                    {
                                        if (field.GetValue(action) is UnityEngine.Object[] arr)
                                        {
                                            for (var i = 0; i < arr.Length; i++)
                                            {
                                                if (IsDestroyedUnityObject(arr[i]))
                                                {
                                                    arr[i] = null!;
                                                    audit.ClearedActionFields++;
                                                    audit.ClearedSlots.Add("ActionField[]:" + fsmPath + "/" + state.Name + "/action[" + aIdx + "]." + field.Name + "[" + i + "]");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // 2b. Clean ActionData
                        var actionData = state.ActionData;
                        if (actionData != null)
                        {
                            var unityObjs = ActionDataUnityObjectParamsField.GetValue(actionData) as List<UnityEngine.Object>;
                            if (unityObjs != null)
                            {
                                for (var i = 0; i < unityObjs.Count; i++)
                                {
                                    if (IsDestroyedUnityObject(unityObjs[i]))
                                    {
                                        unityObjs[i] = null!;
                                        audit.ClearedActionDataUnityObjects++;
                                        audit.ClearedSlots.Add("ActionDataUnityObj:" + fsmPath + "/" + state.Name + "/param[" + i + "]");
                                    }
                                }
                            }

                            var fsmGos = ActionDataFsmGameObjectParamsField.GetValue(actionData) as List<FsmGameObject>;
                            if (fsmGos != null)
                            {
                                for (var i = 0; i < fsmGos.Count; i++)
                                {
                                    if (CleanFsmGameObject(fsmGos[i]))
                                    {
                                        audit.ClearedActionDataFsmGameObjects++;
                                        audit.ClearedSlots.Add("ActionDataFsmGO:" + fsmPath + "/" + state.Name + "/param[" + i + "]");
                                    }
                                }
                            }

                            var fsmObjs = ActionDataFsmObjectParamsField.GetValue(actionData) as List<FsmObject>;
                            if (fsmObjs != null)
                            {
                                for (var i = 0; i < fsmObjs.Count; i++)
                                {
                                    if (CleanFsmObject(fsmObjs[i]))
                                    {
                                        audit.ClearedActionDataFsmObjects++;
                                        audit.ClearedSlots.Add("ActionDataFsmObj:" + fsmPath + "/" + state.Name + "/param[" + i + "]");
                                    }
                                }
                            }

                            var fsmVars = ActionDataFsmVarParamsField.GetValue(actionData) as List<FsmVar>;
                            if (fsmVars != null)
                            {
                                for (var i = 0; i < fsmVars.Count; i++)
                                {
                                    if (CleanFsmVar(fsmVars[i]))
                                    {
                                        audit.ClearedSlots.Add("ActionDataFsmVar:" + fsmPath + "/" + state.Name + "/param[" + i + "]");
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 3. Clean PlayMakerGlobals
            try
            {
                var globals = typeof(PlayMakerGlobals).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
                if (globals != null)
                {
                    var vars = typeof(PlayMakerGlobals).GetField("variables", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(globals) as FsmVariables;
                    if (vars != null)
                    {
                        if (vars.GameObjectVariables != null)
                        {
                            foreach (var fg in vars.GameObjectVariables)
                            {
                                if (CleanFsmGameObject(fg))
                                {
                                    audit.ClearedGlobalVariables++;
                                    audit.ClearedSlots.Add("GlobalFsmVar:" + fg.Name);
                                }
                            }
                        }
                        if (vars.ObjectVariables != null)
                        {
                            foreach (var fo in vars.ObjectVariables)
                            {
                                if (CleanFsmObject(fo))
                                {
                                    audit.ClearedGlobalVariables++;
                                    audit.ClearedSlots.Add("GlobalFsmObjectVar:" + fo.Name);
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // PlayMakerGlobals cleanup is best-effort defense in depth
            }

            return audit;
        }
    }
}
