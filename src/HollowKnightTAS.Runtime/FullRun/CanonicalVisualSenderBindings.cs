using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This catalog binds identities of objects already rebuilt by Canon. It
    // neither reconstructs their runtime fields nor qualifies their coverage.
    internal sealed class CanonicalVisualSenderBindings
    {
        private const int MaximumOwners = 3;
        private const int MaximumArraySlots = 10000;
        private const int MaximumTotalSlots = 10000;
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly FieldInfo RawFsm = Field(typeof(PlayMakerFSM), "fsm", typeof(Fsm));
        private static readonly FieldInfo FsmOwner = Field(typeof(Fsm), "owner", typeof(MonoBehaviour));
        private static readonly FieldInfo FsmName = Field(typeof(Fsm), "name", typeof(string));
        private static readonly FieldInfo FsmInitialized = Field(typeof(Fsm), "initialized", typeof(bool));
        private static readonly FieldInfo FsmStarted = Field(typeof(Fsm), "<Started>k__BackingField", typeof(bool));
        private static readonly FieldInfo States = Field(typeof(Fsm), "states", typeof(FsmState[]));
        private static readonly FieldInfo StateFsm = Field(typeof(FsmState), "fsm", typeof(Fsm));
        private static readonly FieldInfo StateName = Field(typeof(FsmState), "name", typeof(string));
        private static readonly FieldInfo Actions = Field(typeof(FsmState), "actions", typeof(FsmStateAction[]));
        private readonly CanonicalSlotBindingCatalog catalog = new CanonicalSlotBindingCatalog();
        private readonly List<Slot> slots = new List<Slot>();
        private readonly List<OwnerAudit> owners = new List<OwnerAudit>();
        private int arraySlots;

        private CanonicalVisualSenderBindings() { }

        internal static CanonicalVisualSenderBindings Create(IEnumerable<PlayMakerFSM> resetOwners, BoundaryObjectIdentity identities)
        {
            if (resetOwners == null) throw new ArgumentNullException(nameof(resetOwners));
            if (identities == null) throw new ArgumentNullException(nameof(identities));
            var result = new CanonicalVisualSenderBindings();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in resetOwners)
            {
                if (result.owners.Count == MaximumOwners)
                    throw new InvalidDataException("Canonical visual sender owner inventory exceeds its bound.");
                if (ReferenceEquals(component, null) || component == null || component.GetType() != typeof(PlayMakerFSM)
                    || !component.gameObject.scene.IsValid() || !component.gameObject.scene.isLoaded)
                    throw new InvalidDataException("Canonical visual sender component is not an original loaded owner.");
                var original = RawFsm.GetValue(component);
                if (ReferenceEquals(original, null) || original!.GetType() != typeof(Fsm))
                    throw new InvalidDataException("Canonical visual sender raw FSM type differs.");
                var fsm = (Fsm)original!;
                if (!ReferenceEquals(FsmOwner.GetValue(fsm), component)
                    || !Equals(FsmInitialized.GetValue(fsm), true) || !Equals(FsmStarted.GetValue(fsm), true))
                    throw new InvalidDataException("Canonical visual sender FSM is not initialized by its original owner.");
                var name = Text(FsmName.GetValue(fsm), "FSM name");
                if (name != "CameraShake" && name != "HeroLight Control" && name != "color_fader" || !names.Add(name))
                    throw new InvalidDataException("Canonical visual sender owner is outside the three original Canon FSMs or is repeated.");
                var ownerIdentity = Text(identities.Get(component), "owner identity");
                if (ownerIdentity.StartsWith("asset:", StringComparison.Ordinal))
                    throw new InvalidDataException("Canonical visual sender owner has no loaded identity.");
                var fsmKey = "FSM/" + ownerIdentity;
                result.Register(fsmKey, fsm, "FSM");
                var ownerAudit = new OwnerAudit { OwnerIdentity = ownerIdentity, FsmName = name, FsmBinding = fsmKey };
                result.owners.Add(ownerAudit);
                var rawStates = States.GetValue(fsm);
                if (!(rawStates is FsmState[] states))
                    throw new InvalidDataException("Canonical visual sender original states array is absent or has a different type.");
                result.CountSlots(states.Length);
                ownerAudit.StateSlots = states.Length;
                for (var stateIndex = 0; stateIndex < states.Length; stateIndex++)
                {
                    var state = states[stateIndex];
                    if (ReferenceEquals(state, null)) { ownerAudit.NullStateEntries++; continue; }
                    if (state.GetType() != typeof(FsmState) || !ReferenceEquals(StateFsm.GetValue(state), fsm))
                        throw new InvalidDataException("Canonical visual sender state does not belong to its raw FSM.");
                    var stateName = Text(StateName.GetValue(state), "state name");
                    var stateKey = "CanonicalFSMState/v1/" + ownerIdentity + "/state/"
                        + stateIndex.ToString(CultureInfo.InvariantCulture) + ":" + stateName;
                    result.Register(stateKey, state, "state"); ownerAudit.StatesRegistered++;
                    // Preserve lazy actions as absent. Never use state.Actions,
                    // load actions, initialize one, or invoke an event here.
                    var rawActions = Actions.GetValue(state);
                    if (ReferenceEquals(rawActions, null)) { ownerAudit.NullActionArrays++; continue; }
                    if (!(rawActions is FsmStateAction[] actions))
                        throw new InvalidDataException("Canonical visual sender original actions array type differs.");
                    result.CountSlots(actions.Length);
                    ownerAudit.ActionSlots += actions.Length;
                    for (var actionIndex = 0; actionIndex < actions.Length; actionIndex++)
                    {
                        var action = actions[actionIndex];
                        if (ReferenceEquals(action, null)) { ownerAudit.NullActionEntries++; continue; }
                        var typeIdentity = Text(CanonicalCarryGraphCodec.Identity(action.GetType()), "action type identity");
                        var actionKey = stateKey + "/action/" + actionIndex.ToString(CultureInfo.InvariantCulture) + ":" + typeIdentity;
                        result.Register(actionKey, action, "action"); ownerAudit.ActionsRegistered++;
                    }
                    if (!ReferenceEquals(Actions.GetValue(state), rawActions))
                        throw new InvalidDataException("Canonical visual sender raw actions changed during registration.");
                }
                if (!ReferenceEquals(RawFsm.GetValue(component), fsm) || !ReferenceEquals(FsmOwner.GetValue(fsm), component)
                    || !ReferenceEquals(States.GetValue(fsm), rawStates))
                    throw new InvalidDataException("Canonical visual sender original owner/state array changed during registration.");
            }
            if (result.owners.Count != MaximumOwners)
                throw new InvalidDataException("Canonical visual sender inventory must contain all three registered owners.");
            result.catalog.Seal();
            return result;
        }

        internal string? Bind(object value) => catalog.Bind(value);
        internal object Resolve(string key) => catalog.Resolve(key);

        internal Audit Snapshot() => new Audit {
            Count = catalog.Count, ArraySlotsRead = arraySlots,
            Owners = owners.OrderBy(owner => owner.OwnerIdentity, StringComparer.Ordinal).Select(owner => new OwnerAudit { OwnerIdentity = owner.OwnerIdentity, FsmName = owner.FsmName,
                FsmBinding = owner.FsmBinding, StateSlots = owner.StateSlots, ActionSlots = owner.ActionSlots,
                StatesRegistered = owner.StatesRegistered, ActionsRegistered = owner.ActionsRegistered,
                NullStateEntries = owner.NullStateEntries, NullActionArrays = owner.NullActionArrays, NullActionEntries = owner.NullActionEntries }).ToArray(),
            Slots = slots.OrderBy(slot => slot.Key, StringComparer.Ordinal)
                .Select(slot => new Slot { Key = slot.Key, Kind = slot.Kind, RuntimeType = slot.RuntimeType }).ToArray() };

        private void Register(string key, object value, string kind)
        {
            // The shared pure catalog rejects both multiple slots for the same
            // reference and collisions of keys with different object identities.
            catalog.Add(key, value);
            slots.Add(new Slot { Key = key, Kind = kind, RuntimeType = CanonicalCarryGraphCodec.Identity(value.GetType()) });
        }

        private void CountSlots(int count)
        {
            if (count > MaximumArraySlots || (long)arraySlots + count > MaximumTotalSlots)
                throw new InvalidDataException("Canonical visual sender state/action array inventory exceeds its bound.");
            arraySlots += count;
        }

        private static FieldInfo Field(Type owner, string name, Type type)
        {
            var field = owner.GetField(name, Instance);
            if (field == null || field.DeclaringType != owner || field.FieldType != type || field.IsLiteral)
                throw new InvalidDataException("Canonical visual sender raw field layout differs: " + owner.FullName + "/" + name);
            return field;
        }

        private static string Text(object? value, string label)
        {
            if (!(value is string text) || text.Length > 4096 || text.IndexOf('\0') >= 0)
                throw new InvalidDataException("Canonical visual sender " + label + " is absent or exceeds its text bound.");
            return text;
        }

        internal sealed class Audit
        {
            public int Count { get; set; }
            public int ArraySlotsRead { get; set; }
            public OwnerAudit[] Owners { get; set; } = Array.Empty<OwnerAudit>();
            public Slot[] Slots { get; set; } = Array.Empty<Slot>();
            public bool IdentityOnly => true;
            public bool CanonicalStateCoverageQualified => false;
            public bool RestoreBindingQualified => false;
        }

        internal sealed class OwnerAudit
        {
            public string OwnerIdentity { get; set; } = "";
            public string FsmName { get; set; } = "";
            public string FsmBinding { get; set; } = "";
            public int StateSlots { get; set; }
            public int ActionSlots { get; set; }
            public int StatesRegistered { get; set; }
            public int ActionsRegistered { get; set; }
            public int NullStateEntries { get; set; }
            public int NullActionArrays { get; set; }
            public int NullActionEntries { get; set; }
        }

        internal sealed class Slot
        {
            public string Key { get; set; } = "";
            public string Kind { get; set; } = "";
            public string RuntimeType { get; set; } = "";
        }
    }
}
