using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Each list names actual fields whose fresh construction/reset is evidenced
    // by CanonicalVisualFsmTemplates.Restart and CanonicalBoundaryStaticPolicy.
    // New fields never inherit the ownership of a nearby field or their root.
    internal static class CanonicalS0FieldOwnership
    {
        private static readonly Dictionary<string, string[]> Contracts = new Dictionary<string, string[]>(StringComparer.Ordinal) {
            ["HutongGames.PlayMaker.Fsm"] = Words("name startState activeStateName initialized activeStateEntered <Started>k__BackingField <Finished>k__BackingField <SwitchedState>k__BackingField states activeState previousActiveState switchToState variables"),
            ["HutongGames.PlayMaker.FsmState"] = Words("name active finished activeActionIndex <StateTime>k__BackingField <loopCount>k__BackingField <maxLoopCount>k__BackingField actions activeActions _finishedActions activeAction fsm"),
            ["HutongGames.PlayMaker.FsmStateAction"] = Words("<DisplayName>k__BackingField <Entered>k__BackingField active autoName enabled finished isOpen name fsm fsmState fsmComponent owner"),
            ["HutongGames.PlayMaker.NamedVariable"] = Words("name networkSync showInInspector tooltip useVariable obj"),
            ["HutongGames.PlayMaker.FsmVariables"] = Words("arrayVariables boolVariables categories colorVariables enumVariables floatVariables gameObjectVariables intVariables materialVariables objectVariables quaternionVariables rectVariables stringVariables textureVariables variableCategoryIDs vector2Variables vector3Variables"),
            ["HutongGames.PlayMaker.Actions.Wait"] = Words("finishEvent realTime time timer"),
            ["HutongGames.PlayMaker.Actions.SetMaterialColor"] = Words("cachedGameObject component color everyFrame gameObject material materialIndex namedColor namedColorId"),
            ["HutongGames.PlayMaker.Actions.iTweenFsmAction"] = Words("finishEvent itweenEvents itweenID itweenType loopDontFinish realTime startEvent stopOnExit"),
            ["HutongGames.PlayMaker.Actions.iTweenScaleTo"] = Words("delay easeType gameObject id loopType speed time transformScale vectorScale"),
            ["HutongGames.PlayMaker.Actions.iTweenScaleBy"] = Words("delay easeType gameObject id loopType speed time vector"),
            ["UnityEngine.Vector3"] = Words("x y z"), ["UnityEngine.Vector2"] = Words("x y"),
            ["UnityEngine.Color"] = Words("r g b a"), ["UnityEngine.Quaternion"] = Words("x y z w")
        };
        private static string[] Words(string text) => text.Split(' ');
        internal static bool KnownOwner(Type type)
        {
            if (Contracts.ContainsKey(type.FullName ?? "")) return true;
            if (type.Namespace == "HutongGames.PlayMaker" && Words("FsmFloat FsmInt FsmBool FsmString FsmVector2 FsmVector3 FsmColor FsmQuaternion FsmGameObject FsmMaterial FsmTexture FsmObject").Contains(type.Name)) return true;
            if (!type.IsGenericType) return false;
            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(Dictionary<,>) || definition == typeof(List<>) || definition == typeof(Stack<>)
                || definition.FullName == "System.Collections.Generic.GenericEqualityComparer`1"
                    && type.GetGenericArguments()[0] == typeof(string)
                || definition.FullName == "System.Collections.Generic.ObjectEqualityComparer`1"
                    && type.GetGenericArguments()[0] == typeof(Type);
        }
        internal static CanonicalS0Observation.Ownership StaticRoot(string root)
        {
            var prefix = "static:PlayMaker:HutongGames.PlayMaker.";
            var names = new[] { "ActionData.ActionFieldsLookup", "ActionData.ActionHashCodeLookup", "ActionData.ActionTypeLookup", "ActionData.InitFields", "ActionData.UsedIndices", "ActionData.resaveActionData", "ReflectionUtils.assemblyNames", "ReflectionUtils.loadedAssemblies", "ReflectionUtils.typeLookup", "FsmExecutionStack.<MaxStackCount>k__BackingField", "FsmExecutionStack.fsmExecutionStack" };
            return names.Any(name => root == prefix + name) ? CanonicalS0Observation.Ownership.S0 : CanonicalS0Observation.Ownership.Unknown;
        }
        internal static CanonicalS0Observation.Ownership Classify(object owner, FieldInfo field)
        {
            var declaring = field.DeclaringType!; var type = declaring.FullName ?? "";
            if (type == "HutongGames.PlayMaker.FsmState" && field.Name == "<RealStartTime>k__BackingField"
                || type == "HutongGames.PlayMaker.Actions.Wait" && field.Name == "startTime") return CanonicalS0Observation.Ownership.Dependency;
            if (Contracts.TryGetValue(type, out var names) && names.Contains(field.Name, StringComparer.Ordinal)) return CanonicalS0Observation.Ownership.S0;
            if (type.StartsWith("HutongGames.PlayMaker.Fsm", StringComparison.Ordinal)
                && new[] { "FsmFloat", "FsmInt", "FsmBool", "FsmString", "FsmVector2", "FsmVector3", "FsmColor", "FsmQuaternion", "FsmGameObject", "FsmMaterial", "FsmTexture", "FsmObject" }.Contains(declaring.Name)
                && field.Name == "value") return CanonicalS0Observation.Ownership.S0;
            if (declaring.IsGenericType)
            {
                var definition = declaring.GetGenericTypeDefinition();
                var allowed = definition == typeof(Dictionary<,>) ? Words("_syncRoot buckets comparer count entries freeCount freeList keys values version")
                    : definition == typeof(List<>) ? Words("_items _size _syncRoot _version")
                    : definition == typeof(Stack<>) ? Words("_array _size _version _syncRoot") : Array.Empty<string>();
                if (allowed.Contains(field.Name, StringComparer.Ordinal)) return CanonicalS0Observation.Ownership.S0;
            }
            return CanonicalS0Observation.Ownership.Unknown;
        }
    }
}
