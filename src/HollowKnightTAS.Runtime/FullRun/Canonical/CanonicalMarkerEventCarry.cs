using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This is a closed, purely managed empty-event graph. Native Unity types
    // and arbitrary listener/delegate reconstruction remain excluded.
    internal static class CanonicalMarkerEventCarry
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly FieldInfo EventField = typeof(UnityEngine.UI.MaskableGraphic).GetField("m_OnCullStateChanged", Flags)!;
        internal static Type[] Types()
        {
            var ui = typeof(UnityEngine.UI.Graphic).Assembly;
            var core = typeof(GameObject).Assembly;
            if (ui.ManifestModule.ModuleVersionId != new Guid("b4d135af-a1fd-407b-bcc7-280e400336cf")
                || core.ManifestModule.ModuleVersionId != new Guid("047294ce-45cf-4b9a-9441-e48d2b8435e4"))
                throw new InvalidDataException("Marker empty-event module ABI differs.");
            var eventType = typeof(UnityEngine.UI.MaskableGraphic.CullStateChangedEvent);
            var calls = core.GetType("UnityEngine.Events.InvokableCallList", true)!;
            var persistent = core.GetType("UnityEngine.Events.PersistentCallGroup", true)!;
            var invoke = core.GetType("UnityEngine.Events.BaseInvokableCall", true)!;
            var persistentCall = core.GetType("UnityEngine.Events.PersistentCall", true)!;
            RequireNames(eventType, "m_InvokeArray", "m_Calls", "m_CallsDirty", "m_PersistentCalls");
            RequireNames(calls, "m_ExecutingCalls", "m_NeedsUpdate", "m_PersistentCalls", "m_RuntimeCalls");
            RequireNames(persistent, "m_Calls");
            var invokeList = typeof(List<>).MakeGenericType(invoke);
            var persistentList = typeof(List<>).MakeGenericType(persistentCall);
            RequireNames(invokeList, "_items", "_size", "_syncRoot", "_version");
            RequireNames(persistentList, "_items", "_size", "_syncRoot", "_version");
            return new[] { eventType, calls, persistent, invokeList, persistentList, invoke.MakeArrayType(), persistentCall.MakeArrayType() };
        }
        private static void RequireNames(Type type, params string[] expected)
        {
            var actual = CanonicalCarryGraphCodec.Fields(type).Select(field => field.Name).OrderBy(value => value, StringComparer.Ordinal);
            if (!actual.SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal)))
                throw new InvalidDataException("Marker empty-event complete field ABI differs: " + type.FullName);
        }
        internal static void RequireEmpty(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            Types();
            foreach (var owner in directory.Values.OfType<TMPro.TextMeshPro>()) {
                var value = EventField.GetValue(owner);
                if (value == null || value.GetType() != typeof(UnityEngine.UI.MaskableGraphic.CullStateChangedEvent))
                    throw new InvalidDataException("Marker original cull event is absent or has a foreign type.");
                var calls = value.GetType().BaseType!.BaseType!.GetField("m_Calls", Flags)!.GetValue(value)
                    ?? throw new InvalidDataException("Marker original cull invocation container is absent.");
                foreach (var name in new[] { "m_PersistentCalls", "m_RuntimeCalls", "m_ExecutingCalls" })
                    if (!(calls.GetType().GetField(name, Flags)!.GetValue(calls) is IList list) || list.Count != 0)
                        throw new InvalidDataException("Marker nonempty original cull listeners require an explicit callback continuation.");
                var group = value.GetType().BaseType.BaseType.GetField("m_PersistentCalls", Flags)!.GetValue(value)
                    ?? throw new InvalidDataException("Marker original persistent callback group is absent.");
                if (!(group.GetType().GetField("m_Calls", Flags)!.GetValue(group) is IList persistent) || persistent.Count != 0)
                    throw new InvalidDataException("Marker nonempty original persistent calls require an explicit callback continuation.");
            }
        }
        internal static IReadOnlyDictionary<string, object> Directory(IReadOnlyDictionary<string, UnityEngine.Object> directory)
        {
            RequireEmpty(directory);
            return directory.Where(pair => pair.Value is TMPro.TextMeshPro).ToDictionary(
                pair => pair.Key + "/event/m_OnCullStateChanged", pair => EventField.GetValue(pair.Value)!, StringComparer.Ordinal);
        }
    }
}
