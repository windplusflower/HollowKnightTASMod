using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    // EventData is shared mutable data, not a scratch object to clear. Sender
    // membership here is evidence for the existing D graph only; reconstructing
    // an outside state/action as a detached clone does not bind its identity.
    internal static class CanonicalEventDataCarry
    {
        private const string RootName = "R6/Fsm-EventData";
        private const int MaximumRoots = 10000;
        private const int MaximumArraySlots = 10000;
        private const int MaximumTotalSlots = 100000;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly FieldInfo EventDataSlot = Field(typeof(Fsm), "EventData", typeof(FsmEventData), Static);
        private static readonly FieldInfo States = Field(typeof(Fsm), "states", typeof(FsmState[]), Instance);
        private static readonly FieldInfo Actions = Field(typeof(FsmState), "actions", typeof(FsmStateAction[]), Instance);
        private static readonly FieldInfo[] SenderFields = {
            Field(typeof(FsmEventData), "SentByFsm", typeof(Fsm), Instance),
            Field(typeof(FsmEventData), "SentByState", typeof(FsmState), Instance),
            Field(typeof(FsmEventData), "SentByAction", typeof(FsmStateAction), Instance) };
        private static readonly FieldInfo[] UnityFields = {
            Field(typeof(FsmEventData), "SentByGameObject", typeof(UnityEngine.GameObject), Instance),
            Field(typeof(FsmEventData), "GameObjectData", typeof(UnityEngine.GameObject), Instance),
            Field(typeof(FsmEventData), "MaterialData", typeof(UnityEngine.Material), Instance),
            Field(typeof(FsmEventData), "TextureData", typeof(UnityEngine.Texture), Instance),
            Field(typeof(FsmEventData), "ObjectData", typeof(UnityEngine.Object), Instance) };

        internal static Action PrepareSourceReapply(CanonicalCarryGraphCodec.PreparedView source, Func<bool> admitted)
        {
            var owner = source.Root(RootName);
            var fields = CanonicalCarryGraphCodec.Fields(typeof(FsmEventData));
            if (owner.GetType() != typeof(FsmEventData) || !fields.Select(field => field.Name).SequenceEqual(new[] {
                "BoolData", "ColorData", "FloatData", "GameObjectData", "IntData", "MaterialData", "ObjectData", "QuaternionData", "RectData",
                "SentByAction", "SentByFsm", "SentByGameObject", "SentByState", "StringData", "TextureData", "Vector2Data", "Vector3Data" })
                || fields.Any(field => field.DeclaringType != typeof(FsmEventData) || field.IsInitOnly || field.IsStatic))
                throw new InvalidDataException("Complete original EventData source replay ABI differs.");
            // PreparedView.Field reads codec's independent source slot array,
            // never fields of the live root. Freeze after controlled external
            // rebinding, before original Geo retirement/activation can send.
            var values = fields.Select(field => source.Field(owner, field.DeclaringType!, field.Name)).ToArray();
            for (var i = 0; i < fields.Length; i++)
                if (values[i] == null ? fields[i].FieldType.IsValueType : !fields[i].FieldType.IsInstanceOfType(values[i]))
                    throw new InvalidDataException("EventData prepared source field type differs: " + fields[i].Name);
            void RequireOwner() {
                if (!ReferenceEquals(EventDataSlot.GetValue(null), owner))
                    throw new InvalidOperationException("EventData original static slot/owner changed during Geo derivation.");
            }
            RequireOwner(); var consumed = false;
            return () => {
                if (consumed || !admitted()) throw new InvalidOperationException("EventData source replay was consumed or lost pause.");
                consumed = true; RequireOwner();

                using var transaction = CanonicalFieldWriteTransaction.Begin(fields.Select(field => new CanonicalFieldWriteTransaction.Target(owner, field)));
                for (var i = 0; i < fields.Length; i++) fields[i].SetValue(owner, values[i]);
                for (var i = 0; i < fields.Length; i++)
                    if (!CanonicalFieldWriteTransaction.SameRawValue(fields[i].FieldType, values[i], fields[i].GetValue(owner)))
                        throw new InvalidOperationException("Complete EventData source field readback differs: " + fields[i].Name);
                RequireOwner(); if (!admitted()) throw new InvalidOperationException("EventData source replay lost its final pause.");
                transaction.Commit();
            };
        }

        internal static Audit Add(IDictionary<string, object> roots, Func<object, string?>? canonicalBindings = null)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            if (roots.Count > MaximumRoots) throw new InvalidDataException("EventData D root inventory exceeds its bound.");
            // Read only roots already designated as D before adding EventData.
            var fsmRoots = roots.Where(pair => pair.Value is Fsm).OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
            if (fsmRoots.Length == 0 || fsmRoots.Any(pair => pair.Value.GetType() != typeof(Fsm)))
                throw new InvalidDataException("Original EventData D FSM root registry is absent or has an unexpected type.");
            var original = EventDataSlot.GetValue(null);
            if (ReferenceEquals(original, null) || original!.GetType() != typeof(FsmEventData))
                throw new InvalidDataException("Original Fsm.EventData static slot is absent or has an unexpected type.");
            var data = (FsmEventData)original!;
            roots.Add(RootName, data);
            var senderValues = SenderFields.Select(field => field.GetValue(data)).ToArray();
            var senderAudits = SenderFields.Select((field, index) => new SenderAudit {
                Field = field.Name, Status = ReferenceEquals(senderValues[index], null) ? "absent" : "outside-D",
                RuntimeType = TypeName(senderValues[index]) }).ToArray();
            var audit = new Audit { Root = RootName, FsmRoots = fsmRoots.Length, Senders = senderAudits };

            foreach (var pair in fsmRoots)
            {
                Match(senderAudits[0], senderValues[0], pair.Value, pair.Key, -1, -1);
                var rawStates = States.GetValue(pair.Value);
                if (ReferenceEquals(rawStates, null)) { audit.NullStateArrays++; continue; }
                if (!(rawStates is FsmState[] states)) throw new InvalidDataException("Original EventData D states array type differs.");
                AddSlots(states.Length, true, audit);
                for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                {
                    var state = states[stateIndex];
                    if (ReferenceEquals(state, null)) { audit.NullStateEntries++; continue; }
                    Match(senderAudits[1], senderValues[1], state, pair.Key, stateIndex, -1);
                    // Actions is a lazy getter in PlayMaker. Its existing raw
                    // backing array can be null; that does not permit loading it.
                    var rawActions = Actions.GetValue(state);
                    if (ReferenceEquals(rawActions, null)) { audit.NullActionArrays++; continue; }
                    if (!(rawActions is FsmStateAction[] actions)) throw new InvalidDataException("Original EventData D actions array type differs.");
                    AddSlots(actions.Length, false, audit);
                    for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                    {
                        var action = actions[actionIndex];
                        if (ReferenceEquals(action, null)) { audit.NullActionEntries++; continue; }
                        Match(senderAudits[2], senderValues[2], action, pair.Key, stateIndex, actionIndex);
                    }
                }
            }

            for (var index = 0; index < senderAudits.Length; index++)
            {
                var sender = senderAudits[index];
                if (sender.Status != "outside-D") continue;
                var binding = canonicalBindings?.Invoke(senderValues[index]!);
                if (binding == null) continue;
                if (binding.Length == 0 || binding.Length > 16384 || binding.IndexOf('\0') >= 0)
                    throw new InvalidDataException("Original EventData canonical sender binding text differs.");
                sender.Status = "canonical-slot"; sender.Binding = binding;
            }
            var unsupported = senderAudits.Where(sender => sender.Status == "outside-D")
                .Select(sender => "R6/EventData/" + sender.Field + "-sender-outside-D-requires-binding").ToList();
            var unityValues = UnityFields.Select(field => field.GetValue(data)).ToArray();
            var unityAudits = new UnityAudit[UnityFields.Length];
            for (var index = 0; index < UnityFields.Length; index++)
            {
                var value = unityValues[index];
                var status = "CLR-null";
                if (!ReferenceEquals(value, null))
                {
                    if (!(value is UnityEngine.Object native)) throw new InvalidDataException("Original EventData Unity field type differs.");
                    // Original Unity null comparison checks the native object's
                    // lifetime. Do not access name, instance ID, or other game
                    // properties and do not replace a destroyed wrapper by null.
                    status = native == null ? "destroyed" : "live";
                    if (status == "destroyed")
                        unsupported.Add("R6/EventData/" + UnityFields[index].Name + "-destroyed-Unity-wrapper-requires-binding");
                }
                unityAudits[index] = new UnityAudit { Field = UnityFields[index].Name, Status = status, RuntimeType = TypeName(value) };
            }
            audit.UnityFields = unityAudits;
            audit.Unsupported = unsupported.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < SenderFields.Length; index++)
                if (!ReferenceEquals(SenderFields[index].GetValue(data), senderValues[index]))
                    throw new InvalidDataException("Original EventData sender changed during its carry audit: " + SenderFields[index].Name);
            for (var index = 0; index < UnityFields.Length; index++)
                if (!ReferenceEquals(UnityFields[index].GetValue(data), unityValues[index]))
                    throw new InvalidDataException("Original EventData Unity reference changed during its carry audit: " + UnityFields[index].Name);
            if (!ReferenceEquals(EventDataSlot.GetValue(null), data))
                throw new InvalidDataException("Original Fsm.EventData static slot changed during its carry audit.");
            return audit;
        }

        private static FieldInfo Field(Type owner, string name, Type type, BindingFlags flags)
        {
            var field = owner.GetField(name, flags);
            if (field == null || field.FieldType != type || field.IsLiteral || field.DeclaringType != owner)
                throw new InvalidDataException("Original EventData raw field layout differs: " + owner.FullName + "/" + name);
            return field;
        }

        private static void AddSlots(int count, bool states, Audit audit)
        {
            if (count > MaximumArraySlots || (long)audit.StateSlots + audit.ActionSlots + count > MaximumTotalSlots)
                throw new InvalidDataException("Original EventData D state/action array audit exceeds its bound.");
            if (states) audit.StateSlots += count; else audit.ActionSlots += count;
        }

        private static void Match(SenderAudit audit, object? sender, object candidate, string root, int stateIndex, int actionIndex)
        {
            if (ReferenceEquals(sender, null) || !ReferenceEquals(sender, candidate)) return;
            if (audit.MatchingLocations == 0)
            {
                audit.FsmRoot = root; audit.StateIndex = stateIndex; audit.ActionIndex = actionIndex;
            }
            audit.MatchingLocations++; audit.Status = "inside-D";
        }

        private static string TypeName(object? value)
        {
            if (ReferenceEquals(value, null)) return "";
            var type = value!.GetType();
            return type.FullName ?? type.Name;
        }

        internal sealed class Audit
        {
            public string Root { get; set; } = "";
            public int FsmRoots { get; set; }
            public int StateSlots { get; set; }
            public int ActionSlots { get; set; }
            public int NullStateArrays { get; set; }
            public int NullStateEntries { get; set; }
            public int NullActionArrays { get; set; }
            public int NullActionEntries { get; set; }
            public SenderAudit[] Senders { get; set; } = Array.Empty<SenderAudit>();
            public UnityAudit[] UnityFields { get; set; } = Array.Empty<UnityAudit>();
            public string[] Unsupported { get; set; } = Array.Empty<string>();
            public bool RestoreBindingQualified => false;
        }

        internal sealed class SenderAudit
        {
            public string Field { get; set; } = "";
            public string Status { get; set; } = "";
            public string RuntimeType { get; set; } = "";
            public string FsmRoot { get; set; } = "";
            public string Binding { get; set; } = "";
            public int StateIndex { get; set; } = -1;
            public int ActionIndex { get; set; } = -1;
            public int MatchingLocations { get; set; }
        }

        internal sealed class UnityAudit
        {
            public string Field { get; set; } = "";
            public string Status { get; set; } = "";
            public string RuntimeType { get; set; } = "";
        }
    }
}
