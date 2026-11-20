using System;
using System.IO;
using System.Reflection;
using GlobalEnums;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class CanonicalSceneMappingBindings
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private static readonly FieldInfo Slot = Required(typeof(GameManager), "OnFinishedEnteringScene");
        private static readonly Type Closure = typeof(global::SceneManager).Assembly.GetType(CanonicalSceneMappingCarry.ClosureType, true)!;
        private static readonly FieldInfo ClosureOwner = Required(Closure, "<>4__this"), ClosureSelf = Required(Closure, "temp");
        private static readonly FieldInfo GhostManager = Required(typeof(global::SceneManager), "gm"), GhostPlayer = Required(typeof(global::SceneManager), "pd");
        private static readonly FieldInfo MapZoneField = Required(typeof(global::SceneManager), "mapZone"), ManualField = Required(typeof(global::SceneManager), "manualMapTrigger");
        private readonly GameManager manager;
        private readonly PlayerData player;
        private readonly PlayerData currentPlayer;
        private readonly object? originalSlot;
        private readonly GameManager.EnterSceneEvent[] callbacks;
        private readonly global::SceneManager[] ghosts;
        private readonly int[] beforeZones;
        private readonly bool[] beforeManual;
        internal CanonicalSceneMappingCarry.Data Data { get; }
        internal PlayerData OwnedPlayer => player;
        internal bool HasDetachedPlayer => callbacks.Length != 0 && !ReferenceEquals(player, currentPlayer);
        private static FieldInfo Required(Type type, string name) => type.GetField(name, Instance)
            ?? throw new InvalidDataException("Scene entry mapping original field is absent: " + type.FullName + "/" + name);

        internal static GameManager.EnterSceneEvent? RetainOriginalTitleCallback()
        {
            var callback = Slot.GetValue(GameManager.instance) as GameManager.EnterSceneEvent;
            var chain = callback?.GetInvocationList() ?? Array.Empty<Delegate>();
            if (chain.Length > 1) throw new InvalidDataException("Title callback retention has multiple owners.");
            if (chain.Length == 0) return null;
            var item = chain[0]; var target = item.Target;
            if (item.Method.MetadataToken != CanonicalSceneMappingCarry.HandlerToken
                || item.Method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                || target?.GetType() != Closure || !ReferenceEquals(ClosureSelf.GetValue(target), item)
                || !(ClosureOwner.GetValue(target) is global::SceneManager owner) || owner == null
                || owner.gameObject.scene.name != "Menu_Title")
                throw new InvalidDataException("Retained callback is not the actual original live title SceneManager.");
            return callback;
        }
        internal CanonicalSceneMappingBindings(GameManager.EnterSceneEvent? retainedTitleCallback = null, bool useCurrentEmptySlot = false)
        {
            manager = GameManager.instance ?? throw new InvalidDataException("Scene entry mapping GameManager is absent.");
            currentPlayer = PlayerData.instance ?? throw new InvalidDataException("Scene entry mapping current PlayerData is absent.");
            player = currentPlayer;
            if (Slot.FieldType != typeof(GameManager.EnterSceneEvent) || ClosureOwner.FieldType != typeof(global::SceneManager)
                || ClosureSelf.FieldType != typeof(GameManager.EnterSceneEvent) || GhostManager.FieldType != typeof(GameManager)
                || GhostPlayer.FieldType != typeof(PlayerData) || MapZoneField.FieldType != typeof(MapZone) || ManualField.FieldType != typeof(bool))
                throw new InvalidDataException("Scene entry mapping original field types differ.");
            originalSlot = Slot.GetValue(manager);
            var selected = retainedTitleCallback ?? originalSlot as GameManager.EnterSceneEvent;
            if (useCurrentEmptySlot) {
                if (originalSlot != null) throw new InvalidDataException("Empty source mapping requires the target's actual original event slot to be empty.");
                // The diagnostic job retains the original title callback for
                // source boundaries that still need it. Validate that exact
                // retired binding, but do not turn a tool-only backup into a
                // live world root after its original one-shot unsubscribe.
                if (retainedTitleCallback != null) new CanonicalSceneMappingBindings(retainedTitleCallback).Guard();
                selected = null;
                Modding.Logger.Log("[HKTAS] Source empty mapping uses actual empty target event slot;retainedTitleBackupIsNotWorldRoot=true;liveFieldsWritten=false.");
            }
            var chain = selected?.GetInvocationList() ?? Array.Empty<Delegate>();
            if (chain.Length > 1) throw new InvalidDataException("Multiple scene entry mapping callbacks are not registered.");
            callbacks = new GameManager.EnterSceneEvent[chain.Length]; ghosts = new global::SceneManager[chain.Length];
            beforeZones = new int[chain.Length]; beforeManual = new bool[chain.Length];
            Data = new CanonicalSceneMappingCarry.Data { Items = new CanonicalSceneMappingCarry.Mapping[chain.Length] };
            for (var index = 0; index < chain.Length; index++) {
                var callback = chain[index]; var method = callback.Method; var target = callback.Target;
                if (callback.GetType() != typeof(GameManager.EnterSceneEvent) || method.IsStatic || method.ReturnType != typeof(void)
                    || method.GetParameters().Length != 0 || method.MetadataToken != CanonicalSceneMappingCarry.HandlerToken
                    || method.Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                    || method.DeclaringType != Closure || method.Name != CanonicalSceneMappingCarry.HandlerName
                    || ReferenceEquals(target, null) || target!.GetType() != Closure || !ReferenceEquals(ClosureSelf.GetValue(target), callback))
                    throw new InvalidDataException("Unregistered scene entry mapping delegate, target or self alias.");
                var owner = ClosureOwner.GetValue(target) as global::SceneManager;
                if (ReferenceEquals(owner, null) || owner!.GetType() != typeof(global::SceneManager) || owner != null || owner!.GetInstanceID() != 0)
                    throw new InvalidDataException("Scene entry mapping original SceneManager is not a zero-id fake-null owner.");
                if (!ReferenceEquals(GhostManager.GetValue(owner), manager))
                    throw new InvalidDataException("Scene entry mapping original GameManager alias differs.");
                var ownedPlayer = GhostPlayer.GetValue(owner);
                if (ReferenceEquals(ownedPlayer, null) || ownedPlayer!.GetType() != typeof(PlayerData))
                    throw new InvalidDataException("Scene entry mapping original owned PlayerData is absent or has an unregistered type.");
                player = (PlayerData)ownedPlayer;
                var zone = (MapZone)MapZoneField.GetValue(owner);
                if (!Enum.IsDefined(typeof(MapZone), zone)) throw new InvalidDataException("Scene entry mapping original map zone is invalid.");
                callbacks[index] = (GameManager.EnterSceneEvent)callback; ghosts[index] = owner;
                Data.Items[index] = new CanonicalSceneMappingCarry.Mapping { Order = index, MethodModuleMvid = method.Module.ModuleVersionId.ToString("D"),
                    MethodDeclaringType = method.DeclaringType!.FullName!, MethodName = method.Name, MethodToken = method.MetadataToken,
                    MapZone = (int)zone, ManualMapTrigger = (bool)ManualField.GetValue(owner), UsesCurrentPlayerData = ReferenceEquals(player, currentPlayer) };
                beforeZones[index] = Data.Items[index].MapZone; beforeManual[index] = Data.Items[index].ManualMapTrigger;
            }
            CanonicalSceneMappingCarry.Validate(Data); Guard();
        }
        internal void Guard()
        {
            if (!ReferenceEquals(GameManager.instance, manager) || !ReferenceEquals(PlayerData.instance, currentPlayer)
                || !ReferenceEquals(Slot.GetValue(manager), originalSlot)) throw new InvalidOperationException("Scene entry mapping preparation owners or slot changed.");
            for (var index = 0; index < callbacks.Length; index++) {
                var target = callbacks[index].Target; var ghost = ghosts[index];
                if (!ReferenceEquals(ClosureOwner.GetValue(target), ghost) || !ReferenceEquals(ClosureSelf.GetValue(target), callbacks[index])
                    || ghost != null || ghost!.GetInstanceID() != 0 || !ReferenceEquals(GhostManager.GetValue(ghost), manager)
                    || !ReferenceEquals(GhostPlayer.GetValue(ghost), player) || (int)(MapZone)MapZoneField.GetValue(ghost) != beforeZones[index]
                    || (bool)ManualField.GetValue(ghost) != beforeManual[index]) throw new InvalidOperationException("Scene entry mapping original aliases or flags changed.");
            }
        }
        internal Action PrepareApply(CanonicalSceneMappingCarry.Data input, PlayerData? ownedPlayer)
        {
            var saved = CanonicalSceneMappingCarry.Copy(input);
            if (saved.Items.Length > callbacks.Length) throw new InvalidDataException("Scene entry mapping requires an original prepared callback binding.");
            if (saved.Items.Length != 0 && (ReferenceEquals(ownedPlayer, null) || ownedPlayer!.GetType() != typeof(PlayerData)
                || ReferenceEquals(ownedPlayer, currentPlayer) != saved.Items[0].UsesCurrentPlayerData))
                throw new InvalidDataException("Saved scene entry mapping current/detached PlayerData root alias differs.");
            foreach (var item in saved.Items)
                if (!Enum.IsDefined(typeof(MapZone), (MapZone)item.MapZone)) throw new InvalidDataException("Saved scene entry mapping zone is invalid.");
            Guard(); var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Scene entry mapping preparation was already consumed.");
                Guard(); consumed = true;
                for (var index = 0; index < saved.Items.Length; index++) {
                    GhostPlayer.SetValue(ghosts[index], ownedPlayer);
                    MapZoneField.SetValue(ghosts[index], (MapZone)saved.Items[index].MapZone);
                    ManualField.SetValue(ghosts[index], saved.Items[index].ManualMapTrigger);
                    if ((MapZone)MapZoneField.GetValue(ghosts[index]) != (MapZone)saved.Items[index].MapZone
                        || (bool)ManualField.GetValue(ghosts[index]) != saved.Items[index].ManualMapTrigger)
                        throw new InvalidOperationException("Scene entry mapping ghost flags readback differs.");
                    if (!ReferenceEquals(GhostPlayer.GetValue(ghosts[index]), ownedPlayer))
                        throw new InvalidOperationException("Scene entry mapping original owned PlayerData alias readback differs.");
                }
                var expected = saved.Items.Length == 0 ? null : callbacks[0];
                Slot.SetValue(manager, expected);
                if (!ReferenceEquals(Slot.GetValue(manager), expected)) throw new InvalidOperationException("Scene entry mapping original callback slot readback differs.");
            };
        }
        internal void RequireAppliedSource(CanonicalSceneMappingCarry.Data source, PlayerData? ownedPlayer)
        {
            var saved = CanonicalSceneMappingCarry.Copy(source);
            if (saved.Items.Length > callbacks.Length || !ReferenceEquals(GameManager.instance, manager)
                || !ReferenceEquals(PlayerData.instance, currentPlayer)
                || !ReferenceEquals(Slot.GetValue(manager), saved.Items.Length == 0 ? null : callbacks[0]))
                throw new InvalidOperationException("Applied mapping original owners or source slot differ.");
            for (var index = 0; index < saved.Items.Length; index++) {
                var target = callbacks[index].Target; var ghost = ghosts[index];
                if (!ReferenceEquals(ClosureOwner.GetValue(target), ghost) || !ReferenceEquals(ClosureSelf.GetValue(target), callbacks[index])
                    || ghost != null || ghost!.GetInstanceID() != 0 || !ReferenceEquals(GhostManager.GetValue(ghost), manager)
                    || !ReferenceEquals(GhostPlayer.GetValue(ghost), ownedPlayer)
                    || (int)(MapZone)MapZoneField.GetValue(ghost) != saved.Items[index].MapZone
                    || (bool)ManualField.GetValue(ghost) != saved.Items[index].ManualMapTrigger)
                    throw new InvalidOperationException("Applied mapping source values or original ghost aliases differ.");
            }
        }
    }
}
