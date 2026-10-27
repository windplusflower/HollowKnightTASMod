using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Preserve the exact original Soul FSM objects. In particular, the live
    // callback is a continuation; it must never be completed or retired by C.
    internal sealed class CanonicalSoulOrbCarry
    {
        internal const string ContextRoot = "R2/SoulOrb/context", FsmRoot = "R2/SoulOrb/fsm";
        internal static void ValidateDomainRoots(CanonicalCarryGraphData graph)
        {
            if (graph.Roots == null || graph.Roots.Length != 151 || graph.Roots.Count(root => root.Binding == ContextRoot) != 1
                || graph.Roots.Count(root => root.Binding == FsmRoot) != 1
                || graph.Roots.Any(root => root.Binding != ContextRoot && root.Binding != FsmRoot
                    && !root.Binding.StartsWith(FsmRoot + "/state[", StringComparison.Ordinal)))
                throw new InvalidDataException("Soul carry domain contains an unknown or missing original root.");
        }
        internal const string SchemaIdentity = "original-soul-orb-28states121actions-live-callback-shared-global-bench-transition-v3:" + CanonicalHudPresentationDirectory.OriginalLevel1Sha256;
        internal const string BoundSoulLayout = "GGCheckBoundSoul/boundEvent=HutongGames.PlayMaker.FsmEvent;GGCheckBoundSoul/unboundEvent=HutongGames.PlayMaker.FsmEvent;HutongGames.PlayMaker.FsmStateAction/<DisplayName>k__BackingField=System.String;HutongGames.PlayMaker.FsmStateAction/<Entered>k__BackingField=System.Boolean;HutongGames.PlayMaker.FsmStateAction/active=System.Boolean;HutongGames.PlayMaker.FsmStateAction/autoName=System.Boolean;HutongGames.PlayMaker.FsmStateAction/enabled=System.Boolean;HutongGames.PlayMaker.FsmStateAction/finished=System.Boolean;HutongGames.PlayMaker.FsmStateAction/fsm=HutongGames.PlayMaker.Fsm;HutongGames.PlayMaker.FsmStateAction/fsmComponent=PlayMakerFSM;HutongGames.PlayMaker.FsmStateAction/fsmState=HutongGames.PlayMaker.FsmState;HutongGames.PlayMaker.FsmStateAction/isOpen=System.Boolean;HutongGames.PlayMaker.FsmStateAction/name=System.String;HutongGames.PlayMaker.FsmStateAction/owner=UnityEngine.GameObject";
        internal static Type BoundSoulType()
        {
            var type = typeof(GameManager).Assembly.GetType("GGCheckBoundSoul", true)!;
            var layout = string.Join(";", CanonicalCarryGraphCodec.Fields(type).Select(field => field.DeclaringType!.FullName + "/" + field.Name + "=" + field.FieldType.FullName));
            if (type.Module.ModuleVersionId.ToString("D") != "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f" || layout != BoundSoulLayout)
                throw new InvalidDataException("Original Soul bound action field layout differs.");
            return type;
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", Flags)!;
        private static readonly FieldInfo States = typeof(Fsm).GetField("states", Flags)!;
        private static readonly FieldInfo Actions = typeof(FsmState).GetField("actions", Flags)!;
        private static readonly FieldInfo Cached = typeof(iTweenFsmAction).GetField("itweenEvents", Flags)!;
        private static readonly FieldInfo ActionId = typeof(iTweenFsmAction).GetField("itweenID", Flags)!;
        private static readonly MethodInfo Producer = typeof(iTweenFsmAction).GetMethod("OnEnteriTween", Flags)!;
        private static readonly string[] Original = new[] {
            "Init|GetHero,HutongGames.PlayMaker.Actions.CallMethodProper,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.FindChild,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.ConvertIntToFloat,HutongGames.PlayMaker.Actions.FloatMultiply,HutongGames.PlayMaker.Actions.FloatAdd,HutongGames.PlayMaker.Actions.SetPosition,HutongGames.PlayMaker.Actions.IntCompare",
            "Idle|HutongGames.PlayMaker.Actions.SetBoolValue",
            "MP Drain|HutongGames.PlayMaker.Actions.SendEventByName,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.SendEventByName,HutongGames.PlayMaker.Actions.SetBoolValue,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.ConvertIntToFloat,HutongGames.PlayMaker.Actions.FloatMultiply,HutongGames.PlayMaker.Actions.FloatAdd,HutongGames.PlayMaker.Actions.SetPosition",
            "MP <= 1|HutongGames.PlayMaker.Actions.SetMeshRenderer,HutongGames.PlayMaker.Actions.SetPosition",
            "MP Gain|HutongGames.PlayMaker.Actions.SetMeshRenderer,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.ConvertIntToFloat,HutongGames.PlayMaker.Actions.FloatMultiply,HutongGames.PlayMaker.Actions.FloatAdd,HutongGames.PlayMaker.Actions.SetPosition,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.SetBoolValue",
            "MP Full|HutongGames.PlayMaker.Actions.PlayerDataBoolTest,GGCheckBoundSoul,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.SetBoolValue,HutongGames.PlayMaker.Actions.GetPlayerDataBool,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.ActivateGameObject",
            "Check Can Heal|HutongGames.PlayMaker.Actions.GetFsmBool,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare",
            "Can't Heal|HutongGames.PlayMaker.Actions.SetBoolValue,HutongGames.PlayMaker.Actions.SendEventByName",
            "MP Lose|HutongGames.PlayMaker.Actions.Tk2dPlayAnimation,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.SendEventByName,HutongGames.PlayMaker.Actions.SetBoolValue,HutongGames.PlayMaker.Actions.Tk2dPlayFrame,HutongGames.PlayMaker.Actions.ConvertIntToFloat,HutongGames.PlayMaker.Actions.FloatMultiply,HutongGames.PlayMaker.Actions.FloatAdd,HutongGames.PlayMaker.Actions.GetPosition,HutongGames.PlayMaker.Actions.SetVector3XYZ,HutongGames.PlayMaker.Actions.iTweenMoveTo",
            "Check MP|HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare",
            "Check Eyes|HutongGames.PlayMaker.Actions.IntCompare",
            "Over|HutongGames.PlayMaker.Actions.SendEventByName",
            "Under|HutongGames.PlayMaker.Actions.SendEventByName",
            "Under 2|HutongGames.PlayMaker.Actions.SendEventByName",
            "Check Eyes 2|HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.SendEventByName,HutongGames.PlayMaker.Actions.IntCompare",
            "Over 2|HutongGames.PlayMaker.Actions.SendEventByName",
            "Start Full|GGCheckBoundSoul,HutongGames.PlayMaker.Actions.ActivateGameObject",
            "Check Focus Prompt|HutongGames.PlayMaker.Actions.GetPlayerDataBool,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.PlayerDataBoolTest,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.SetPlayerDataBool,HutongGames.PlayMaker.Actions.SendEventByName",
            "Already Can Heal?|HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.SetBoolValue",
            "Orb Flash?|HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.GetPlayerDataInt,HutongGames.PlayMaker.Actions.IntCompare,HutongGames.PlayMaker.Actions.SendEventByName",
            "Set|HutongGames.PlayMaker.Actions.SetBoolValue",
            "Pause|HutongGames.PlayMaker.Actions.NextFrameEvent",
            "Can Heal 2|HutongGames.PlayMaker.Actions.SendEventByName,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.BoolTest,HutongGames.PlayMaker.Actions.PlayParticleEmitter,HutongGames.PlayMaker.Actions.AudioPlayerOneShotSingle,HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.SetScale,HutongGames.PlayMaker.Actions.SendMessage,HutongGames.PlayMaker.Actions.SetParent,SendEventToRegister,HutongGames.PlayMaker.Actions.SetBoolValue",
            "Binding?|GGCheckBoundSoul",
            "Bound|HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.ActivateGameObject",
            "Not Bound|HutongGames.PlayMaker.Actions.ActivateGameObject,HutongGames.PlayMaker.Actions.ActivateGameObject",
            "Bound Full|HutongGames.PlayMaker.Actions.ActivateGameObject",
            "Set?|HutongGames.PlayMaker.Actions.BoolTest",
        };
        internal sealed class Data
        {
            public bool Present, DonotFinish, IsLooping, Enabled;
            public int Id, Counter;
            public byte[] Lifecycle = Array.Empty<byte>();
            public iTweenFSMEvents? Callback;
            public iTweenFsmAction? Action;
            public FsmEvent? FinishEvent;
            public string[] SharedSlots = Array.Empty<string>(), SharedPaths = Array.Empty<string>();
        }
        internal sealed class SharedDirectory
        {
            internal readonly SortedDictionary<string, object> Roots = new SortedDictionary<string, object>(StringComparer.Ordinal);
            internal readonly Dictionary<object, string> Identities = new Dictionary<object, string>(new References());
            internal string[] Slots = Array.Empty<string>(), Paths = Array.Empty<string>();
        }
        internal static FsmGameObject[] GlobalGameObjects()
        {
            var instance = typeof(PlayMakerGlobals).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
                ?? throw new InvalidDataException("Original PlayMaker globals are absent.");
            var vars = typeof(PlayMakerGlobals).GetField("variables", Flags)!.GetValue(instance)
                ?? throw new InvalidDataException("Original PlayMaker global variables are absent.");
            return typeof(FsmVariables).GetField("gameObjectVariables", Flags)!.GetValue(vars) as FsmGameObject[]
                ?? throw new InvalidDataException("Original global gameObject variables are absent.");
        }
        internal static SharedDirectory Shared(LoadingGeoIncomingReferences incoming, string[]? requiredSlots = null)
        {
            var values = GlobalGameObjects();
            if (values.Length > 4096) throw new InvalidDataException("Original global variable directory exceeds its bound.");
            var nameField = typeof(NamedVariable).GetField("name", Flags)!;
            var result = new SharedDirectory(); var names = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Length; i++) {
                var value = values[i]; if (value == null) continue;
                var name = nameField.GetValue(value) as string;
                var key = "R2/SoulOrb/global-gameobject[" + i + "]";
                var descriptor = key + "\n" + name;
                if (requiredSlots == null ? !incoming.IsSoulMember(value) : !requiredSlots.Contains(descriptor, StringComparer.Ordinal)) continue;
                if (value.GetType() != typeof(FsmGameObject) || string.IsNullOrEmpty(name) || name!.Contains("\n")
                    || !incoming.Owns(value) || !names.Add(name) || values.Count(other => ReferenceEquals(other, value)) != 1
                    || values.Count(other => other != null && Equals(nameField.GetValue(other), name)) != 1)
                    throw new InvalidDataException("Soul shared original global variable identity or name is ambiguous.");
                result.Roots.Add(key, value); result.Identities.Add(value, key);
                result.Slots = result.Slots.Concat(new[] { descriptor }).ToArray();
            }
            if (requiredSlots != null && !result.Slots.SequenceEqual(requiredSlots)) throw new InvalidDataException("Soul source-selected global variables differ on target.");
            result.Paths = incoming.SoulSharedPaths(result.Identities);
            if (result.Roots.Count > 64 || result.Paths.Length > 4096) throw new InvalidDataException("Soul shared global directory exceeds its bound.");
            Modding.Logger.Log("[HKTAS] Soul global incoming index: " + incoming.SoulIndexStatistics() + ";selected=" + result.Roots.Count + ";selectedPaths=" + result.Paths.Length);
            return result;
        }
        private bool targetPrepared;
        private Action? transitionVerify;
        internal Action TransitionVerify => transitionVerify ?? throw new InvalidOperationException("Soul shared transition was not prepared.");
        internal static PlayMakerFSM Owner()
        {
            RequireAbi();
            var camera = GameCameras.instance;
            if (camera == null || camera.hudCamera == null) throw new InvalidDataException("Original Soul camera is absent.");
            var soul = camera.hudCamera.transform.Find("Hud Canvas/Soul Orb");
            if (soul == null) throw new InvalidDataException("Original Soul owner is absent.");
            var owners = soul.GetComponents<PlayMakerFSM>().Where(item => ((Fsm)Raw.GetValue(item)).Name == "Soul Orb Control").ToArray();
            if (owners.Length != 1) throw new InvalidDataException("Original Soul FSM identity differs.");
            return owners[0];
        }
        internal static SortedDictionary<string, object> ObjectRoots()
        {
            return ObjectRootsFromRaw((Fsm)Raw.GetValue(Owner()));
        }
        internal static SortedDictionary<string, object> ObjectRootsFromRaw(Fsm raw)
        {
            var states = States.GetValue(raw) as FsmState[];
            if (states == null || states.Length != Original.Length) throw new InvalidDataException("Original Soul state directory differs.");
            var roots = new SortedDictionary<string, object>(StringComparer.Ordinal) { [FsmRoot] = raw };
            for (var index = 0; index < states.Length; index++) {
                var state = states[index]; var actions = state == null ? null : Actions.GetValue(state) as FsmStateAction[];
                if (actions == null || state!.Name + "|" + string.Join(",", actions.Select(action => action?.GetType().FullName)) != Original[index])
                    throw new InvalidDataException("Original Soul complete action directory differs: " + index);
                roots.Add(FsmRoot + "/state[" + index + "]", state);
                for (var action = 0; action < actions.Length; action++) roots.Add(FsmRoot + "/state[" + index + "]/action[" + action + "]", actions[action]);
            }
            if (roots.Values.Distinct(new References()).Count() != 150) throw new InvalidDataException("Original Soul FSM/state/action aliases differ.");
            return roots;
        }
        private static iTweenMoveTo Tween(PlayMakerFSM owner)
        {
            var raw = (Fsm)Raw.GetValue(owner); var states = (FsmState[])States.GetValue(raw);
            var state = states.Single(item => item.Name == "MP Lose");
            var actions = (FsmStateAction[])Actions.GetValue(state);
            if (actions.Length != 11 || actions[10].GetType() != typeof(iTweenMoveTo)) throw new InvalidDataException("Original Soul MP Lose action11 differs.");
            return (iTweenMoveTo)actions[10];
        }
        private static Transform Liquid(PlayMakerFSM owner) => owner.transform.Find("SoulOrb_fill/Liquid")
            ?? throw new InvalidDataException("Original Soul Liquid identity is absent.");
        internal static Data Capture(bool targetDirectoryOnly = false)
        {
            var owner = Owner(); var roots = ObjectRoots(); var action = Tween(owner); var liquid = Liquid(owner);
            var events = liquid.GetComponents<iTweenFSMEvents>();
            var cached = Cached.GetValue(action) as iTweenFSMEvents;
            if (owner.ActiveStateName != "Idle" && owner.ActiveStateName != "MP Lose" || liquid.GetComponents<iTween>().Any(item => item != null))
                throw new InvalidDataException("Soul snapshot has unsupported original state or live Tween producer.");
            var present = targetDirectoryOnly ? events.Length == 1 : owner.ActiveStateName == "MP Lose";
            if (present ? events.Length != 1 || cached == null || events[0].GetType() != typeof(iTweenFSMEvents)
                    || !ReferenceEquals(events[0], cached) || !ReferenceEquals(cached.itweenFSMAction, action)
                    || !ReferenceEquals(action.Fsm, Raw.GetValue(owner)) || cached.itweenID != (int)ActionId.GetValue(action) || cached.islooping || !cached.donotfinish || action.Finished
                : events.Length != 0 || !ReferenceEquals(cached, null))
                throw new InvalidDataException("Soul original live/empty callback closure differs.");
            if (action.finishEvent == null || action.finishEvent.Name != "FINISHED" || action.stopOnExit.IsNone || !action.stopOnExit.Value
                || action.loopDontFinish.IsNone || !action.loopDontFinish.Value)
                throw new InvalidDataException("Soul original completion/exit contract differs.");
            byte[] lifecycle = Array.Empty<byte>();
            if (present) using (new LoadingEngineClockProbe.CheckpointValidation()) lifecycle = BoundaryMonoLifecycle.ObserveUnstartedUi(cached!);
            return new Data { Present = present, Callback = cached, Action = action, FinishEvent = action.finishEvent,
                Id = present ? cached!.itweenID : 0, Counter = iTweenFSMEvents.itweenIDCount, Lifecycle = lifecycle,
                DonotFinish = present && cached!.donotfinish, IsLooping = present && cached!.islooping, Enabled = present && cached!.enabled };
        }
        internal static void AddRoots(IDictionary<string, object> roots, bool targetDirectoryOnly, SharedDirectory shared)
        {
            foreach (var pair in ObjectRoots()) roots.Add(pair.Key, pair.Value);
            var data = Capture(targetDirectoryOnly); data.SharedSlots = shared.Slots; data.SharedPaths = shared.Paths;
            roots.Add(ContextRoot, data);
        }
        internal static IReadOnlyDictionary<object, string> CopiedMembers(CanonicalCarryGraphCodec codec)
        {
            var roots = ObjectRoots();
            // Stable roots retain identity, but an outside holder can still
            // have source-dependent alias state. Refuse it rather than assume
            // the target's old holder already matches the source.
            var copied = new Dictionary<object, string>(new References());
            var metadata = new CanonicalCarryReflectionBindings(new[] { typeof(object).Assembly, typeof(GameManager).Assembly,
                typeof(Fsm).Assembly, typeof(GameObject).Assembly, typeof(AudioClip).Assembly, typeof(Rigidbody2D).Assembly,
                typeof(RaycastHit).Assembly }, Array.Empty<FieldInfo>());
            string? External(object value) {
                if (value is UnityEngine.Object native) return "Soul/shared-native/" + native.GetInstanceID();
                if (value is FsmEvent shared) return "Soul/shared-event/" + shared.Name;
                if (value is Fsm other && !ReferenceEquals(other, roots[FsmRoot])) {
                    var owner = typeof(Fsm).GetField("owner", Flags)!.GetValue(other) as PlayMakerFSM;
                    if (owner == null || !ReferenceEquals(Raw.GetValue(owner), other)) throw new InvalidDataException("Soul copied closure reaches a foreign FSM.");
                    return "Soul/shared-fsm/" + owner.GetInstanceID();
                }
                var binding = metadata.Bind(value);
                // Parameter arrays are reconstructed by the main graph binding,
                // so their outside holders must still be included in the incoming proof.
                if (binding != null && value is Array && !copied.ContainsKey(value)) copied.Add(value, "reflection-parameter-array");
                return binding;
            }
            var graph = codec.Capture(roots, External, null, (value, id, path) => { if (!copied.ContainsKey(value)) copied.Add(value, "node[" + id + "]/" + path); });
            foreach (var callback in Liquid(Owner()).GetComponents<iTweenFSMEvents>()) if (!copied.ContainsKey(callback)) copied.Add(callback, "original-Soul-Liquid-callback");
            if (graph.Unsupported.Length != 0 || copied.Count > 4096)
                throw new InvalidDataException("Soul managed closure is unsupported or exceeds its finite bound: " + string.Join(";", graph.Unsupported));
            return copied;
        }
        internal static bool SourceRequiresCallback(CanonicalCarryGraphData source, string expectedSchema, string expectedBinding)
        {
            if (source.Version != 1 || source.Schema != expectedSchema || source.Unsupported.Length != 0)
                throw new InvalidDataException("Source Soul schema/coverage differs before its original producer.");
            CanonicalCarryGraphData.Node Node(string binding, Type type) {
                var root = source.Roots.SingleOrDefault(item => item.Binding == binding);
                if (root.Node <= 0 || root.Node > source.Nodes.Length) throw new InvalidDataException("Source Soul root is absent: " + binding);
                var node = source.Nodes[root.Node - 1];
                if (node.Id != root.Node || node.Type != CanonicalCarryGraphCodec.Identity(type) || node.Kind != "object" || node.Fields == null
                    || !node.Fields.Select(item => item.Name).SequenceEqual(CanonicalCarryGraphCodec.Fields(type).Select(field =>
                        CanonicalCarryGraphCodec.Identity(field.DeclaringType!) + "/" + field.Name)))
                    throw new InvalidDataException("Source Soul complete original root layout differs: " + binding);
                return node;
            }
            CanonicalCarryGraphData.Value Field(CanonicalCarryGraphData.Node node, Type type, string name)
                => node.Fields!.Single(item => item.Name == CanonicalCarryGraphCodec.Identity(type) + "/" + name).Value;
            var context = Node(ContextRoot, typeof(Data)); var fsm = Node(FsmRoot, typeof(Fsm));
            CanonicalCarryGraphData.Node ArrayNode(CanonicalCarryGraphData.Value reference, Type type, int count) {
                if (reference.Kind != "node" || reference.Reference < 1 || reference.Reference > source.Nodes.Length)
                    throw new InvalidDataException("Source Soul ordered array reference differs.");
                var array = source.Nodes[reference.Reference - 1];
                if (array.Type != CanonicalCarryGraphCodec.Identity(type) || array.Kind != "array" || array.Items == null || array.Items.Length != count)
                    throw new InvalidDataException("Source Soul ordered array layout differs.");
                return array;
            }
            var orderedStates = ArrayNode(Field(fsm, typeof(Fsm), "states"), typeof(FsmState[]), Original.Length);
            for (var state = 0; state < Original.Length; state++) {
                var stateNode = Node(FsmRoot + "/state[" + state + "]", typeof(FsmState));
                var name = Field(stateNode, typeof(FsmState), "name");
                if (orderedStates.Items![state].Kind != "node" || orderedStates.Items[state].Reference != stateNode.Id
                    || name.Kind != "scalar" || name.Type != CanonicalCarryGraphCodec.Identity(typeof(string)) || name.Text != Original[state].Split('|')[0])
                    throw new InvalidDataException("Source Soul original state names/order differ.");
                var names = Original[state].Substring(Original[state].IndexOf('|') + 1).Split(',');
                var orderedActions = ArrayNode(Field(stateNode, typeof(FsmState), "actions"), typeof(FsmStateAction[]), names.Length);
                for (var action = 0; action < names.Length; action++) {
                    var type = typeof(iTweenMoveTo).Assembly.GetType(names[action], false) ?? typeof(Fsm).Assembly.GetType(names[action], true)!;
                    var actionNode = Node(FsmRoot + "/state[" + state + "]/action[" + action + "]", type);
                    if (orderedActions.Items![action].Kind != "node" || orderedActions.Items[action].Reference != actionNode.Id)
                        throw new InvalidDataException("Source Soul original action order/root aliases differ.");
                }
            }
            var presence = Field(context, typeof(Data), "Present");
            if (presence.Kind != "scalar" || presence.Type != CanonicalCarryGraphCodec.Identity(typeof(bool)) || presence.Text != "True" && presence.Text != "False")
                throw new InvalidDataException("Source Soul presence scalar differs.");
            var present = presence.Text == "True";
            var active = Field(fsm, typeof(Fsm), "activeState");
            var expectedState = Node(FsmRoot + (present ? "/state[8]" : "/state[1]"), typeof(FsmState));
            if (active.Kind != "node" || active.Reference != expectedState.Id) throw new InvalidDataException("Source Soul active-state root alias differs.");
            var tween = Node(FsmRoot + "/state[8]/action[10]", typeof(iTweenMoveTo));
            bool Same(CanonicalCarryGraphData.Value a, CanonicalCarryGraphData.Value b) => a.Kind == b.Kind && a.Type == b.Type && a.Reference == b.Reference && a.Text == b.Text;
            var actionValue = Field(context, typeof(Data), "Action");
            if (actionValue.Kind != "node" || actionValue.Reference != tween.Id
                || !Same(Field(context, typeof(Data), "Callback"), Field(tween, typeof(iTweenFsmAction), "itweenEvents"))
                || !Same(Field(context, typeof(Data), "FinishEvent"), Field(tween, typeof(iTweenFsmAction), "finishEvent")))
                throw new InvalidDataException("Source Soul callback/action/completion aliases differ.");
            if (present) {
                var finished = Field(tween, typeof(FsmStateAction), "finished");
                var actionFsm = Field(tween, typeof(FsmStateAction), "fsm");
                if (finished.Kind != "scalar" || finished.Text != "False" || finished.Type != CanonicalCarryGraphCodec.Identity(typeof(bool))
                    || actionFsm.Kind != "node" || actionFsm.Reference != fsm.Id)
                    throw new InvalidDataException("Source Soul active action completion/FSM alias differs.");
                foreach (var pair in new[] { Tuple.Create("Enabled", "True"), Tuple.Create("DonotFinish", "True"), Tuple.Create("IsLooping", "False") }) {
                    var value = Field(context, typeof(Data), pair.Item1);
                    if (value.Kind != "scalar" || value.Type != CanonicalCarryGraphCodec.Identity(typeof(bool)) || value.Text != pair.Item2)
                        throw new InvalidDataException("Source Soul live callback flags differ.");
                }
                var callback = Field(context, typeof(Data), "Callback");
                if (callback.Kind != "external" || callback.Reference < 1 || callback.Reference > source.Externals.Length
                    || callback.Type != CanonicalCarryGraphCodec.Identity(typeof(iTweenFSMEvents))
                    || source.Externals[callback.Reference - 1].Type != callback.Type || source.Externals[callback.Reference - 1].Binding != expectedBinding)
                    throw new InvalidDataException("Source Soul original native callback binding differs.");
                var id = Field(context, typeof(Data), "Id"); var counter = Field(context, typeof(Data), "Counter");
                if (id.Kind != "scalar" || id.Type != CanonicalCarryGraphCodec.Identity(typeof(int)) || !Same(id, counter)
                    || !Same(id, Field(tween, typeof(iTweenFsmAction), "itweenID"))
                    || !int.TryParse(id.Text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    || parsed <= 0 || parsed.ToString(System.Globalization.CultureInfo.InvariantCulture) != id.Text)
                    throw new InvalidDataException("Source Soul original action/global counter values differ.");
            }
            return present;
        }
        internal static string[] SourceStrings(CanonicalCarryGraphData source, string fieldName)
        {
            var root = source.Roots.Single(item => item.Binding == ContextRoot);
            var node = source.Nodes[root.Node - 1];
            var value = node.Fields!.Single(item => item.Name == CanonicalCarryGraphCodec.Identity(typeof(Data)) + "/" + fieldName).Value;
            if (value.Kind != "node" || value.Reference < 1 || value.Reference > source.Nodes.Length)
                throw new InvalidDataException("Soul shared source directory reference differs.");
            var array = source.Nodes[value.Reference - 1];
            if (array.Kind != "array" || array.Type != CanonicalCarryGraphCodec.Identity(typeof(string[])) || array.Items == null || array.Items.Length > 4096
                || array.Items.Any(item => item.Kind != "scalar" || item.Type != CanonicalCarryGraphCodec.Identity(typeof(string)) || item.Text == null))
                throw new InvalidDataException("Soul shared source directory layout differs.");
            return array.Items.Select(item => item.Text!).ToArray();
        }
        internal void PrepareOriginalIdentity(CanonicalCarryGraphData source, string expectedSchema, BoundaryObjectIdentity identities, Func<bool> admitted, LoadingGeoIncomingReferences incoming)
        {
            if (targetPrepared || !admitted()) throw new InvalidOperationException("Soul original producer requires single-use parked admission.");
            targetPrepared = true; RequireAbi();
            var present = SourceRequiresCallback(source, expectedSchema, identities.Get(Liquid(Owner()).gameObject) + ":iTweenFSMEvents[0]");
            var shared = Shared(incoming, SourceStrings(source, "SharedSlots"));
            var sourcePaths = SourceStrings(source, "SharedPaths");
            if (!shared.Paths.SequenceEqual(sourcePaths)) {
                var sourceOnly = sourcePaths.Except(shared.Paths, StringComparer.Ordinal).ToArray();
                var targetOnly = shared.Paths.Except(sourcePaths, StringComparer.Ordinal).ToArray();
                string Describe(string[] paths) {
                    var categories = paths.GroupBy(path => path.Contains(":PlayMakerFSM[") ? "PlayMakerFSM" : path.Contains("asset:") ? "asset" : path.Contains("static:") ? "static" : "other")
                        .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.Key + "=" + group.Count());
                    var selected = new List<string>(); var bytes = 0;
                    foreach (var path in paths.Take(32)) {
                        var size = System.Text.Encoding.UTF8.GetByteCount(path);
                        if (bytes + size > 65536) break;
                        selected.Add(path); bytes += size;
                    }
                    return "categories=" + string.Join(",", categories) + ";sampleCount=" + selected.Count + ";sampleBytes=" + bytes + ";sample=" + string.Join(" | ", selected);
                }
                try { transitionVerify = incoming.ValidateSoulSharedTransition(shared.Identities, sourcePaths); }
                catch (InvalidDataException error) {
                    throw new InvalidDataException("Source/target original Soul global incoming identity directory differs before producer: sourceCount="
                        + sourcePaths.Length + ";targetCount=" + shared.Paths.Length + ";sourceOnlyCount=" + sourceOnly.Length
                        + ";targetOnlyCount=" + targetOnly.Length + ";sourceOnly[" + Describe(sourceOnly)
                        + "];targetOnly[" + Describe(targetOnly) + "];reason=" + error.Message, error);
                }
            }
            else transitionVerify = incoming.ValidateSoulSharedTransition(shared.Identities, sourcePaths);
            incoming.RequireSoulSharedPaths(shared.Identities, shared.Paths);
            var before = Capture();
            if (before.Present || Owner().ActiveStateName != "Idle") throw new InvalidOperationException("Soul target requires its original empty Idle callback owner.");
            if (!present) return;
            if (iTweenFSMEvents.itweenIDCount < 0 || iTweenFSMEvents.itweenIDCount == int.MaxValue)
                throw new InvalidOperationException("Soul original producer counter is outside the admitted non-overflow shape.");
            var owner = Owner(); var action = Tween(owner); var counter = iTweenFSMEvents.itweenIDCount;
            var finished = action.Finished;
            var random = UnityEngine.Random.state;
            var particleRandom = LoadingGameplayOracle.CaptureParticleRandomState();
            Producer.Invoke(action, new object[] { action.gameObject });
            if (!random.Equals(UnityEngine.Random.state) || !particleRandom.SequenceEqual(LoadingGameplayOracle.CaptureParticleRandomState()))
                throw new InvalidOperationException("Original Soul callback producer changed RNG; no RNG state is repaired.");
            var callback = Cached.GetValue(action) as iTweenFSMEvents;
            if (callback == null || !ReferenceEquals(callback.gameObject, Liquid(owner).gameObject)
                || !ReferenceEquals(callback.itweenFSMAction, action) || callback.itweenID != counter + 1
                || iTweenFSMEvents.itweenIDCount != counter + 1 || owner.ActiveStateName != "Idle" || action.Finished != finished)
                throw new InvalidOperationException("Original Soul callback-only producer readback differs.");
            if (!admitted()) throw new InvalidOperationException("Soul original producer lost parked admission.");
        }
        internal static Data FromPrepared(CanonicalCarryGraphCodec.PreparedView view)
        {
            var data = new Data(); var owner = view.Root(ContextRoot);
            foreach (var field in CanonicalCarryGraphCodec.Fields(typeof(Data)))
                field.SetValue(data, view.Field(owner, typeof(Data), field.Name));
            return data;
        }
        internal static Action PrepareApply(CanonicalCarryGraphCodec.PreparedView view, Func<bool> admitted, out Action verify)
        {
            var data = FromPrepared(view); var owner = Owner(); var raw = (Fsm)view.Root(FsmRoot);
            var action = (iTweenMoveTo)view.Root(FsmRoot + "/state[8]/action[10]");
            var state = view.Field(raw, typeof(Fsm), "activeState");
            if (!ReferenceEquals(raw, Raw.GetValue(owner)) || !ReferenceEquals(action, data.Action)
                || !ReferenceEquals(state, view.Root(FsmRoot + (data.Present ? "/state[8]" : "/state[1]")))
                || !ReferenceEquals(view.Field(action, typeof(iTweenFsmAction), "finishEvent"), data.FinishEvent)
                || data.FinishEvent == null || data.FinishEvent.Name != "FINISHED" || data.Counter < 0
                || !ReferenceEquals(view.Field(action, typeof(iTweenFsmAction), "itweenEvents"), data.Callback)
                || data.Present && ((int)view.Field(action, typeof(iTweenFsmAction), "itweenID")! != data.Id
                    || (bool)view.Field(action, typeof(FsmStateAction), "finished")! || data.Id != data.Counter
                    || data.IsLooping || !data.DonotFinish || !data.Enabled || data.Lifecycle.Length != 5)
                || !data.Present && (data.Callback != null || data.Id != 0 || data.Lifecycle.Length != 0 || data.Enabled || data.IsLooping || data.DonotFinish))
                throw new InvalidDataException("Prepared original Soul state/action/callback closure differs.");
            var callbacks = Liquid(owner).GetComponents<iTweenFSMEvents>();
            if (data.Present ? callbacks.Length != 1 || !ReferenceEquals(callbacks[0], data.Callback)
                    || !ReferenceEquals(data.Callback!.itweenFSMAction, action)
                : callbacks.Length != 0)
                throw new InvalidDataException("Original Soul producer did not bind the exact prepared native callback.");
            void Native() {
                if (!admitted() || !ReferenceEquals(Raw.GetValue(owner), raw)) throw new InvalidOperationException("Soul native source admission differs.");
                var current = Liquid(owner).GetComponents<iTweenFSMEvents>();
                if (data.Present ? current.Length != 1 || !ReferenceEquals(current[0], data.Callback)
                    || !data.Callback || data.Callback!.enabled != data.Enabled || !ReferenceEquals(data.Callback.itweenFSMAction, action)
                    : current.Length != 0) throw new InvalidOperationException("Soul original native callback identity changed.");
                if (data.Present) {
                    using var validation = new LoadingEngineClockProbe.CheckpointValidation();
                    if (!data.Lifecycle.SequenceEqual(BoundaryMonoLifecycle.ObserveUnstartedUi(data.Callback!)))
                        throw new InvalidOperationException("Soul original native callback lifecycle differs; native flags are never written.");
                }
            }
            Native(); var consumed = false;
            var beforeId = data.Callback == null ? 0 : data.Callback.itweenID;
            var beforeDontFinish = data.Callback != null && data.Callback.donotfinish;
            var beforeLooping = data.Callback != null && data.Callback.islooping;
            var beforeCounter = iTweenFSMEvents.itweenIDCount;
            verify = () => {
                Native();
                if (iTweenFSMEvents.itweenIDCount != data.Counter || owner.ActiveStateName != (data.Present ? "MP Lose" : "Idle")
                    || !ReferenceEquals(Tween(owner), action) || !ReferenceEquals(Cached.GetValue(action), data.Callback)
                    || !ReferenceEquals(action.finishEvent, data.FinishEvent)
                    || data.Present && (data.Callback!.itweenID != data.Id || data.Callback.donotfinish != data.DonotFinish
                        || data.Callback.islooping != data.IsLooping || (int)ActionId.GetValue(action) != data.Id || action.Finished))
                    throw new InvalidOperationException("Complete original Soul source continuation readback differs.");
            };
            var readback = verify;
            return () => {
                if (consumed) throw new InvalidOperationException("Soul source native values were already applied.");
                consumed = true; Native();
                if (iTweenFSMEvents.itweenIDCount != beforeCounter || data.Present && (data.Callback!.itweenID != beforeId
                    || data.Callback.donotfinish != beforeDontFinish || data.Callback.islooping != beforeLooping))
                    throw new InvalidOperationException("Soul original callback fields/counter changed after preparation.");
                if (data.Present) { data.Callback!.itweenID = data.Id; data.Callback.donotfinish = data.DonotFinish; data.Callback.islooping = data.IsLooping; }
                iTweenFSMEvents.itweenIDCount = data.Counter;
                readback();
            };
        }
        private static void RequireAbi()
        {
            if (typeof(iTweenFSMEvents).Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                || Producer == null || Producer.MetadataToken != 0x060045b7 || Producer.ReturnType != typeof(void)
                || typeof(iTweenFSMEvents).GetMethods(Flags | BindingFlags.DeclaredOnly).Any(method => new[] {
                    "Awake", "OnEnable", "Start", "OnDisable", "OnDestroy", "Update", "LateUpdate", "FixedUpdate" }.Contains(method.Name)))
                throw new InvalidDataException("Original Soul callback-only producer ABI differs.");
        }
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
