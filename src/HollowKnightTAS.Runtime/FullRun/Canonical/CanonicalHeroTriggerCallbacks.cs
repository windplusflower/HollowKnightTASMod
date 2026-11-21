using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Import the real subscriptions; never call OnEnter or synthesize a collision.
    internal static class CanonicalHeroTriggerCallbacks
    {
        internal const string Root = "D/Hero-trigger-callbacks";
        internal const string SchemaIdentity = "Hero-original-Unity2DProxy-six-slots:Trigger2dEvent:15357,15358,15359:prepared-source-action-alias-v1";
        internal static readonly Type[] DataTypes = { typeof(Data), typeof(Slot), typeof(Slot[]), typeof(Item), typeof(Item[]) };
        internal sealed class Data { public Slot[] Slots = Array.Empty<Slot>(); }
        internal sealed class Slot { public string Owner = "", Field = ""; public Item[] Items = Array.Empty<Item>(); }
        internal sealed class Item
        {
            public string Fsm = "", StateName = "", Module = "", Method = "";
            public int State, Action, Token, Alias;
        }
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", Flags)!;
        private static readonly FieldInfo RawStates = typeof(Fsm).GetField("states", Flags)!;
        private static readonly FieldInfo RawActions = typeof(FsmState).GetField("actions", Flags)!;
        private static readonly string[] Names = { "OnCollisionEnter2dDelegates", "OnCollisionStay2dDelegates", "OnCollisionExit2dDelegates",
            "OnTriggerEnter2dDelegates", "OnTriggerStay2dDelegates", "OnTriggerExit2dDelegates" };
        private static FsmState[] States(Fsm fsm) => (FsmState[]?)RawStates.GetValue(fsm) ?? Array.Empty<FsmState>();
        private static FsmStateAction[] Actions(FsmState state) => (FsmStateAction[]?)RawActions.GetValue(state) ?? Array.Empty<FsmStateAction>();
        private static SortedDictionary<string, PlayMakerUnity2DProxy> Owners(BoundaryObjectIdentity identities)
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidDataException("Hero proxy owner is absent.");
            var prefix = hero.gameObject.scene.name + "/";
            var result = new SortedDictionary<string, PlayMakerUnity2DProxy>(StringComparer.Ordinal);
            foreach (var proxy in hero.GetComponentsInChildren<PlayMakerUnity2DProxy>(true)) {
                var path = identities.Get(proxy);
                if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Hero proxy scene differs.");
                result.Add(path.Substring(prefix.Length), proxy);
            }
            if (result.Count > 64) throw new InvalidDataException("Hero proxy owner bound exceeded.");
            return result;
        }
        private static FieldInfo Field(string name)
        {
            if (!Names.Contains(name)) throw new InvalidDataException("Unregistered Hero proxy slot.");
            var field = typeof(PlayMakerUnity2DProxy).GetField(name, Flags);
            if (field == null || !typeof(Delegate).IsAssignableFrom(field.FieldType))
                throw new InvalidDataException("Original Hero proxy slot layout differs.");
            var actualMvid = field.Module.ModuleVersionId.ToString("D");
            if (actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("HeroTrigger:FieldMvid:" + actualMvid,
                    "[HKTAS] PlayMakerUnity2DProxy ModuleVersionId differs from pinned original (continuing): actual=" + actualMvid);
            return field;
        }
        private static MethodInfo Method(string slot)
        {
            var ordinal = Array.IndexOf(Names, slot) - 3;
            if (ordinal < 0 || ordinal > 2) throw new InvalidDataException("Unregistered nonempty Hero collision callback.");
            var method = typeof(Trigger2dEvent).GetMethod(new[] { "DoTriggerEnter2D", "DoTriggerStay2D", "DoTriggerExit2D" }[ordinal], Flags)!;
            if (method == null)
                throw new InvalidDataException("Original Hero trigger method differs.");
            var actualMvid = method.Module.ModuleVersionId.ToString("D");
            if (method.MetadataToken != 0x06003bfd + ordinal || actualMvid != CanonicalSceneMappingCarry.ModuleMvid)
                CanonicalSceneMappingCarry.WarnOnce("HeroTrigger:Method:" + slot + ":" + actualMvid + ":" + method.MetadataToken,
                    "[HKTAS] Trigger2dEvent." + method.Name + " MVID/token differs from pinned original (continuing): mvid="
                    + actualMvid + ";token=0x" + method.MetadataToken.ToString("x8"));
            return method;
        }
        internal static Data Capture(BoundaryObjectIdentity identities)
        {
            var fsms = CanonicalHeroFsmBindings.Snapshot(identities);
            var aliases = new List<object>();
            var slots = new List<Slot>();
            foreach (var pair in Owners(identities)) foreach (var name in Names) {
                var callbacks = ((Delegate?)Field(name).GetValue(pair.Value))?.GetInvocationList() ?? Array.Empty<Delegate>();
                if (callbacks.Length > 256) throw new InvalidDataException("Hero trigger invocation bound exceeded.");
                var items = new List<Item>();
                foreach (var callback in callbacks) {
                    var method = Method(name);
                    if (callback.Method != method || !(callback.Target is Trigger2dEvent target))
                        throw new InvalidDataException("Unregistered original Hero trigger callback: " + callback.Method.DeclaringType?.FullName + "/" + callback.Method.Name);
                    var alias = aliases.FindIndex(value => ReferenceEquals(value, target));
                    if (alias < 0) { alias = aliases.Count; aliases.Add(target); }
                    Item? item = null;
                    foreach (var fsmPair in fsms) {
                        var states = States((Fsm)Raw.GetValue(fsmPair.Value));
                        for (var s = 0; s < states.Length; s++) for (var a = 0; a < Actions(states[s]).Length; a++)
                            if (ReferenceEquals(Actions(states[s])[a], target)) {
                                if (item != null) throw new InvalidDataException("Hero trigger action has multiple owner positions.");
                                item = new Item { Fsm = fsmPair.Key, State = s, Action = a, StateName = states[s].Name,
                                    Module = method.Module.ModuleVersionId.ToString("D"), Method = method.Name, Token = method.MetadataToken, Alias = alias };
                            }
                    }
                    items.Add(item ?? throw new InvalidDataException("Source Hero trigger callback has no actual Hero action binding."));
                }
                slots.Add(new Slot { Owner = pair.Key, Field = name, Items = items.ToArray() });
            }
            return new Data { Slots = slots.ToArray() };
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view) => new Data {
            Slots = (Slot[])view.Field(view.Root(Root), typeof(Data), nameof(Data.Slots))! };
        internal static Action PrepareApply(Data source, BoundaryObjectIdentity identities, Func<bool> admitted,
            CanonicalCarryGraphCodec.PreparedView view)
        {
            var owners = Owners(identities); var fsms = CanonicalHeroFsmBindings.Snapshot(identities);
            if (source.Slots == null || source.Slots.Length != owners.Count * Names.Length)
                throw new InvalidDataException("Source Hero proxy owner/slot directory differs.");
            var expectedKeys = owners.Keys.SelectMany(owner => Names.Select(name => owner + "/" + name)).ToArray();
            if (!expectedKeys.SequenceEqual(source.Slots.Select(slot => slot.Owner + "/" + slot.Field)))
                throw new InvalidDataException("Source Hero proxy ordered slot directory differs.");
            var aliases = new Dictionary<int, object>(); var guards = new List<Func<bool>>(); var writes = new List<Action>();
            foreach (var slot in source.Slots) {
                var owner = owners[slot.Owner]; var field = Field(slot.Field); var prior = field.GetValue(owner);
                if (slot.Items == null || slot.Items.Length > 256) throw new InvalidDataException("Source Hero trigger invocation bound differs.");
                Delegate? next = null;
                foreach (var item in slot.Items) {
                    var method = Method(slot.Field);
                    if (item == null || item.Method != method.Name || item.Alias < 0 || item.Alias >= 16384 || !fsms.TryGetValue(item.Fsm, out var fsmOwner))
                        throw new InvalidDataException("Source Hero trigger method or owner differs.");
                    if (item.Module != method.Module.ModuleVersionId.ToString("D") || item.Token != method.MetadataToken)
                        CanonicalSceneMappingCarry.WarnOnce("HeroTrigger:Prepared:" + item.Module + ":" + item.Token,
                            "[HKTAS] Prepared Hero trigger callback MVID/token differs from current runtime (continuing): saved="
                            + item.Module + "/0x" + item.Token.ToString("x8"));
                    var fsm = (Fsm)Raw.GetValue(fsmOwner);
                    if (!ReferenceEquals(view.Root("HeroFSM/" + item.Fsm), fsm)) throw new InvalidDataException("Prepared Hero trigger FSM alias differs.");
                    var states = (FsmState[])view.Field(fsm, typeof(Fsm), "states")!;
                    if (item.State < 0 || item.State >= states.Length || states[item.State].Name != item.StateName
                        || item.Action < 0 || item.Action >= Actions(states[item.State]).Length
                        || !(Actions(states[item.State])[item.Action] is Trigger2dEvent target))
                        throw new InvalidDataException("Prepared source Hero trigger action differs.");
                    if (!ReferenceEquals(typeof(Trigger2dEvent).GetField("_proxy", Flags)!.GetValue(target), owner)
                        || (int)target.trigger != Array.IndexOf(Names, slot.Field) - 3)
                        throw new InvalidDataException("Prepared Hero trigger proxy owner or trigger selector alias differs.");
                    if (aliases.TryGetValue(item.Alias, out var shared)) {
                        if (!ReferenceEquals(shared, target)) throw new InvalidDataException("Hero trigger shared target alias differs.");
                    } else {
                        if (aliases.Values.Any(value => ReferenceEquals(value, target))) throw new InvalidDataException("Hero trigger distinct target alias collapsed.");
                        aliases.Add(item.Alias, target);
                    }
                    guards.Add(() => fsmOwner != null && ReferenceEquals(Raw.GetValue(fsmOwner), fsm)
                        && ReferenceEquals(States(fsm), states) && ReferenceEquals(Actions(states[item.State])[item.Action], target));
                    next = Delegate.Combine(next, Delegate.CreateDelegate(field.FieldType, target, method));
                }
                guards.Add(() => owner != null && ReferenceEquals(field.GetValue(owner), prior));
                var installed = next;
                writes.Add(() => { field.SetValue(owner, installed);
                    if (!ReferenceEquals(field.GetValue(owner), installed)) throw new InvalidOperationException("Hero trigger slot raw readback differs."); });
            }
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Hero trigger prepared scope was already consumed.");
                consumed = true;
                if (!admitted() || guards.Any(guard => !guard()) || !owners.SequenceEqual(Owners(identities)))
                    throw new InvalidOperationException("Hero trigger actual owner/action/slot guard changed.");
                foreach (var write in writes) write();
                if (!admitted()) throw new InvalidOperationException("Hero trigger apply lost parked admission.");
            };
        }
    }
}
