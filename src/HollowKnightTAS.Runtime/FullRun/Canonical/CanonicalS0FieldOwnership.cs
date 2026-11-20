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
            ["HutongGames.PlayMaker.Fsm"] = Words("updateHelperSetDirty dataVersion owner usedInTemplate name startState states events globalTransitions variables description docUrl showStateLabel maxLoopCount watermark password locked manualUpdate keepDelayedEventsOnStateExit preprocessed host rootFsm subFsmList setDirty activeStateEntered ExposedEvents myLog RestartOnEnable <Started>k__BackingField EnableDebugFlow EnableBreakpoints StepFrame delayedEvents updateEvents removeEvents editorFlags <EventTarget>k__BackingField initialized <Finished>k__BackingField activeStateName activeState switchToState previousActiveState <LastTransition>k__BackingField StateChanged <IsModifiedPrefabInstance>k__BackingField editState <SwitchedState>k__BackingField mouseEvents handleLevelLoaded handleTriggerEnter2D handleTriggerExit2D handleTriggerStay2D handleCollisionEnter2D handleCollisionExit2D handleCollisionStay2D handleTriggerEnter handleTriggerExit handleTriggerStay handleCollisionEnter handleCollisionExit handleCollisionStay handleParticleCollision handleControllerColliderHit handleJointBreak handleJointBreak2D handleOnGUI handleFixedUpdate handleLateUpdate handleApplicationEvents handleUiEvents handleLegacyNetworking <CollisionInfo>k__BackingField <TriggerCollider>k__BackingField <Collision2DInfo>k__BackingField <TriggerCollider2D>k__BackingField <JointBreakForce>k__BackingField <BrokenJoint2D>k__BackingField <ParticleCollisionGO>k__BackingField <TriggerName>k__BackingField <CollisionName>k__BackingField <Trigger2dName>k__BackingField <Collision2dName>k__BackingField <ControllerCollider>k__BackingField <RaycastHitInfo>k__BackingField handleAnimatorMove handleAnimatorIK"),
            ["HutongGames.PlayMaker.FsmState"] = Words("name active finished activeActionIndex <StateTime>k__BackingField <loopCount>k__BackingField <maxLoopCount>k__BackingField actions activeActions _finishedActions activeAction fsm description colorIndex position isBreakpoint isSequence hideUnused transitions actionData"),
            ["HutongGames.PlayMaker.FsmTransition"] = Words("fsmEvent toState linkStyle linkConstraint colorIndex toFsmState"),
            ["HutongGames.PlayMaker.FsmOwnerDefault"] = Words("ownerOption gameObject"),
            ["HutongGames.PlayMaker.ActionData"] = Words("actionNames customNames actionEnabled actionIsOpen actionStartIndex actionHashCodes unityObjectParams fsmGameObjectParams fsmOwnerDefaultParams animationCurveParams functionCallParams fsmTemplateControlParams fsmEventTargetParams fsmPropertyParams layoutOptionParams fsmStringParams fsmObjectParams fsmVarParams fsmArrayParams fsmEnumParams fsmFloatParams fsmIntParams fsmBoolParams fsmVector2Params fsmVector3Params fsmColorParams fsmRectParams fsmQuaternionParams stringParams byteData byteDataAsArray arrayParamSizes arrayParamTypes customTypeSizes customTypeNames paramDataType paramName paramDataPos paramByteDataSize nextParamIndex"),
            ["HutongGames.PlayMaker.FsmStateAction"] = Words("<DisplayName>k__BackingField <Entered>k__BackingField active autoName enabled finished isOpen name fsm fsmState fsmComponent owner"),
            ["HutongGames.PlayMaker.NamedVariable"] = Words("name networkSync showInInspector tooltip useVariable obj"),
            ["HutongGames.PlayMaker.FsmVariables"] = Words("arrayVariables boolVariables categories colorVariables enumVariables floatVariables gameObjectVariables intVariables materialVariables objectVariables quaternionVariables rectVariables stringVariables textureVariables variableCategoryIDs vector2Variables vector3Variables"),
            ["HutongGames.PlayMaker.Actions.Wait"] = Words("finishEvent realTime time timer"),
            ["HutongGames.PlayMaker.Actions.SetMaterialColor"] = Words("cachedGameObject component color everyFrame gameObject material materialIndex namedColor namedColorId"),
            ["HutongGames.PlayMaker.Actions.iTweenFsmAction"] = Words("finishEvent itweenEvents itweenID itweenType loopDontFinish realTime startEvent stopOnExit"),
            ["HutongGames.PlayMaker.Actions.iTweenScaleTo"] = Words("delay easeType gameObject id loopType speed time transformScale vectorScale"),
            ["HutongGames.PlayMaker.Actions.iTweenScaleBy"] = Words("delay easeType gameObject id loopType speed time vector"),
            ["HutongGames.PlayMaker.Actions.SetSpriteRenderer"] = Words("gameObject active"),
            ["HutongGames.PlayMaker.Actions.SetTextMeshProColor"] = Words("gameObject color everyFrame go textMesh"),
            ["HutongGames.PlayMaker.Actions.GetMaterialColor"] = Words("gameObject materialIndex material namedColor color fail"),
            ["HutongGames.PlayMaker.Actions.EaseColor"] = Words("fromValue toValue colorVariable finishInNextStep"),
            ["HutongGames.PlayMaker.Actions.EaseFsmAction"] = Words("time speed delay easeType reverse finishEvent realTime ease runningTime lastTime startTime deltaTime delayTime percentage fromFloats toFloats resultFloats finishAction start finished isRunning"),
            ["HutongGames.PlayMaker.Actions.SetColorValue"] = Words("colorVariable color everyFrame"),
            ["HutongGames.PlayMaker.Actions.BoolTest"] = Words("boolVariable isTrue isFalse everyFrame"),
            ["HutongGames.PlayMaker.Actions.FloatCompare"] = Words("float1 float2 tolerance equal lessThan greaterThan everyFrame"),
            ["HutongGames.PlayMaker.Actions.GetColorRGBA"] = Words("color storeRed storeGreen storeBlue storeAlpha everyFrame"),
            ["HutongGames.PlayMaker.Actions.GetOwner"] = Words("storeGameObject"),
            ["HutongGames.PlayMaker.Actions.SetMeshRenderer"] = Words("gameObject active"),
            ["HutongGames.PlayMaker.FsmObject"] = Words("typeName value objectType"),
            ["HutongGames.PlayMaker.FsmGameObject"] = Words("value OnChange"),
            ["HutongGames.PlayMaker.FsmRect"] = Words("value"),
            ["HutongGames.PlayMaker.FsmVar"] = Words("variableName objectType useVariable namedVar namedVarType enumType enumValue _objectType type floatValue intValue boolValue stringValue vector4Value objectReference arrayValue vector2 vector3 rect"),
            ["HutongGames.PlayMaker.FsmArray"] = Words("type objectTypeName objectType floatValues intValues boolValues stringValues vector4Values objectReferences sourceArray values"),
            ["HutongGames.PlayMaker.FsmEnum"] = Words("enumName intValue value parsedIntValue enumType"),
            ["UnityEngine.Vector3"] = Words("x y z"), ["UnityEngine.Vector2"] = Words("x y"),
            ["UnityEngine.Vector4"] = Words("x y z w"), ["UnityEngine.Rect"] = Words("m_XMin m_YMin m_Width m_Height"),
            ["UnityEngine.RaycastHit"] = Words("m_Point m_Normal m_FaceID m_Distance m_UV m_Collider"),
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
                || definition.FullName == "HutongGames.PlayMaker.Actions.ComponentAction`1" && type.GetGenericArguments()[0] == typeof(UnityEngine.Renderer)
                || definition.FullName == "System.Collections.Generic.Dictionary`2+Entry"
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
                if (definition.FullName == "System.Collections.Generic.Dictionary`2+Entry") allowed = Words("hashCode next key value");
                if (definition.FullName == "HutongGames.PlayMaker.Actions.ComponentAction`1" && declaring.GetGenericArguments()[0] == typeof(UnityEngine.Renderer))
                    allowed = Words("cachedGameObject component");
                if (allowed.Contains(field.Name, StringComparer.Ordinal)) return CanonicalS0Observation.Ownership.S0;
            }
            return CanonicalS0Observation.Ownership.Unknown;
        }
    }
}
