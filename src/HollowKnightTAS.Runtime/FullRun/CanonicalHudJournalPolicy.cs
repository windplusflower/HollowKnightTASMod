using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HutongGames.PlayMaker;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One explicit generated HUD directory, common to record/replay/restore.
    // Never enter Inventory states or call JournalEntryStats.Update (it writes PD).
    internal sealed class CanonicalHudJournalPolicy : IDisposable
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private readonly Hook buildHook, enterHook;
        private JournalList? journal;
        private Transform? inventory;
        private bool rebuilding, ready;
        private HashSet<int>? beforeOwners;
        private readonly Dictionary<int, MonoBehaviour> starts = new Dictionary<int, MonoBehaviour>();
        private readonly Dictionary<int, string> retiring = new Dictionary<int, string>();
        private readonly Dictionary<FsmState, Fsm> entered = new Dictionary<FsmState, Fsm>();
        private delegate void OriginalBuild(JournalList owner);
        private delegate void OriginalEnter(FsmState state);
        private static FieldInfo Field(Type type, string name) => type.GetField(name, Fields)
            ?? throw new MissingFieldException(type.FullName, name);
        private static readonly FieldInfo JournalPlayer = Field(typeof(JournalList), "pd");
        private static readonly FieldInfo CurrentList = Field(typeof(JournalList), "currentList");
        private static readonly FieldInfo StateFsm = Field(typeof(FsmState), "fsm");
        private static readonly FieldInfo FsmOwner = Field(typeof(Fsm), "owner");
        private static readonly FieldInfo StateClock = Field(typeof(FsmState), "<RealStartTime>k__BackingField");

        internal CanonicalHudJournalPolicy()
        {
            var build = typeof(JournalList).GetMethod("BuildEnemyList", Fields, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("JournalList.BuildEnemyList");
            var enter = typeof(FsmState).GetMethod("OnEnter", Fields, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("FsmState.OnEnter");
            if (build.ReturnType != typeof(void) || enter.ReturnType != typeof(void)
                || typeof(JournalList).Module.ModuleVersionId != typeof(GameManager).Module.ModuleVersionId)
                throw new InvalidOperationException("Original HUD builder ABI differs.");
            buildHook = new Hook(build, (Action<OriginalBuild, JournalList>)Build);
            enterHook = new Hook(enter, (Action<OriginalEnter, FsmState>)Enter);
        }
        private void Build(OriginalBuild original, JournalList owner)
        {
            if (!ReferenceEquals(owner, journal) || rebuilding || !ready) { original(owner); return; }
            // The original late Init can reach this method after Canon already
            // created its exact directory. Do not append a second copy.
            RequireDirectory(closed: false);
            var player = PlayerData.instance ?? throw new InvalidOperationException("Original late Journal init has no current PD.");
            JournalPlayer.SetValue(owner, player); owner.firstNewItem = -1; owner.itemCount = owner.list.Length - 1;
            foreach (var item in owner.listInv) Field(typeof(JournalEntryStats), "pd").SetValue(item.GetComponent<JournalEntryStats>(), player);
        }
        private void Enter(OriginalEnter original, FsmState state)
        {
            original(state);
            if (!rebuilding || beforeOwners == null) return;
            var fsm = (Fsm)StateFsm.GetValue(state);
            var owner = FsmOwner.GetValue(fsm) as MonoBehaviour;
            if (owner != null && !beforeOwners.Contains(owner.GetInstanceID())) {
                if (entered.Count >= 4096) throw new InvalidOperationException("HUD entered-state birth budget exceeded.");
                entered[state] = fsm;
            }
        }
        private void RequireClosed()
        {
            if (journal == null || inventory == null || journal.gameObject.activeInHierarchy)
                throw new InvalidOperationException("Canonical Journal requires its original closed Inventory owner.");
        }
        private void Bind()
        {
            var cameras = typeof(GameCameras).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null) as GameCameras
                ?? throw new InvalidOperationException("Original GameCameras owner is absent.");
            inventory = cameras.hudCamera.transform.Find("Inventory")
                ?? throw new InvalidOperationException("Original HUD Inventory directory is absent.");
            var list = inventory.Find("Journal/Enemy List")?.GetComponent<JournalList>()
                ?? throw new InvalidOperationException("Original Journal list owner is absent.");
            if (journal != null && !ReferenceEquals(journal, list)) throw new InvalidOperationException("Original Journal owner changed.");
            journal = list; RequireClosed();
        }
        internal object Reset()
        {
            Bind(); ready = false; starts.Clear(); entered.Clear(); retiring.Clear();
            var list = journal!.list;
            if (list == null || list.Length < 1 || list.Length > 512 || list.Any(item => item == null || item.GetComponent<JournalEntryStats>() == null))
                throw new InvalidOperationException("Original Journal prefab directory is incomplete.");
            var old = journal.transform.Cast<Transform>().Select(item => item.gameObject).ToArray();
            var recorded = journal.listInv;
            if (old.Length != 0 && (recorded == null || recorded.Length != list.Length || recorded.Any(item => item == null)
                || old.Length != recorded.Length || old.Any(item => !recorded.Any(value => ReferenceEquals(value, item)))))
                throw new InvalidOperationException("Journal has an unowned or duplicate generated child.");
            foreach (var item in old) {
                foreach (var component in item.GetComponentsInChildren<Component>(true))
                    retiring[component.GetInstanceID()] = "canonical-journal-owned-retirement/" + item.name + "/" + component.GetType().FullName;
                UnityEngine.Object.DestroyImmediate(item);
            }
            beforeOwners = new HashSet<int>(Resources.FindObjectsOfTypeAll<MonoBehaviour>().Where(owner => owner != null).Select(owner => owner.GetInstanceID()));
            if (beforeOwners.Count > 100000) throw new InvalidOperationException("HUD birth owner audit exceeded its bound.");
            journal.itemCount = -1; journal.listInv = null!; CurrentList.SetValue(journal, null);
            rebuilding = true;
            try { journal.BuildEnemyList(); }
            finally { rebuilding = false; beforeOwners = null; }
            ready = true; RequireDirectory();
            foreach (var owner in journal.GetComponentsInChildren<MonoBehaviour>(true)) {
                if (ReferenceEquals(owner, journal)) continue;
                starts.Add(owner.GetInstanceID(), owner);
            }
            foreach (var state in entered)
                if (!(FsmOwner.GetValue(state.Value) is MonoBehaviour owner) || !OwnsNewStart(owner.GetInstanceID()))
                    throw new InvalidOperationException("Journal construction entered a non-owned FSM.");
            DeriveFromCurrentPlayer();
            return new { generatedEntries = list.Length, removedOwnedEntries = old.Length, originalEnteredStates = entered.Count,
                actualNewOwners = starts.Count, completeStateCoverage = false, journalBindingQualified = false };
        }
        private void RequireDirectory(bool closed = true)
        {
            if (closed) RequireClosed();
            var list = journal!.listInv;
            if (!ready || list == null || list.Length != journal.list.Length || list.Any(item => item == null || item.transform.parent != journal.transform)
                || journal.transform.childCount != list.Length || list.Distinct().Count() != list.Length)
                throw new InvalidOperationException("Canonical Journal directory or original aliases changed.");
        }
        private void DeriveFromCurrentPlayer()
        {
            RequireDirectory(); var player = PlayerData.instance ?? throw new InvalidOperationException("Current source PlayerData is absent.");
            JournalPlayer.SetValue(journal, player);
            foreach (var item in journal!.listInv) {
                var stats = item.GetComponent<JournalEntryStats>();
                Field(typeof(JournalEntryStats), "pd").SetValue(stats, player);
                // These two OnEnable cache decisions are derived from the same
                // actual PD and original prefab render configuration, without
                // activating the parent or invoking Update/CountJournalEntries.
                var frame = (GameObject)Field(typeof(JournalEntryStats), "frame").GetValue(stats);
                var dot = (GameObject)Field(typeof(JournalEntryStats), "newDot").GetValue(stats);
                var framePrefab = (GameObject)Field(typeof(JournalEntryStats), "frameObject").GetValue(stats);
                var dotPrefab = (GameObject)Field(typeof(JournalEntryStats), "newDotObject").GetValue(stats);
                var frameVisible = player.GetInt(stats.GetPlayerDataKillsName()) <= 0;
                var dotVisible = player.GetBool(stats.GetPlayerDataNewDataName());
                Field(typeof(JournalEntryStats), "frameVisible").SetValue(stats, frameVisible);
                Field(typeof(JournalEntryStats), "dotVisible").SetValue(stats, dotVisible);
                frame.GetComponent<SpriteRenderer>().enabled = frameVisible || framePrefab.GetComponent<SpriteRenderer>().enabled;
                dot.GetComponent<SpriteRenderer>().enabled = dotVisible || dotPrefab.GetComponent<SpriteRenderer>().enabled;
            }
            journal.UpdateEnemyList();
            if (!ReferenceEquals(JournalPlayer.GetValue(journal), player)
                || journal.listInv.Any(item => !ReferenceEquals(Field(typeof(JournalEntryStats), "pd").GetValue(item.GetComponent<JournalEntryStats>()), player)))
                throw new InvalidOperationException("Journal actual PD consumer aliases differ.");
        }
        internal Action PrepareSourceDependencies(Func<bool> admission)
        {
            RequireDirectory();
            return () => { if (!admission()) throw new InvalidOperationException("HUD source derivation lost paused admission."); DeriveFromCurrentPlayer(); };
        }
        internal bool OwnsNewStart(int id) => starts.TryGetValue(id, out var owner) && owner != null && journal != null
            && owner.transform.IsChildOf(journal.transform);
        internal string? RetiringOwner(int id) => retiring.TryGetValue(id, out var value) ? value : null;
        internal void RebindEnteredClock(Action admission)
        {
            RequireDirectory(); var clock = CanonicalFsmTimeCarry.Capture();
            var bits = CanonicalFsmTimeCarry.ComputeRealtimeBits(clock, Time.realtimeSinceStartup);
            var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            foreach (var pair in entered) {
                admission();
                if (!(FsmOwner.GetValue(pair.Value) is MonoBehaviour owner) || !OwnsNewStart(owner.GetInstanceID())
                    || !ReferenceEquals(StateFsm.GetValue(pair.Key), pair.Value)) throw new InvalidOperationException("Original Journal state birth owner changed.");
                StateClock.SetValue(pair.Key, value);
            }
            admission();
            if (!CanonicalFsmTimeCarry.Equal(clock, CanonicalFsmTimeCarry.Capture()) || entered.Keys.Any(state => CanonicalFsmTimeCarry.Bits((float)StateClock.GetValue(state)) != bits))
                throw new InvalidOperationException("Journal entered clock derived readback differs.");
        }
        public void Dispose() { buildHook.Dispose(); enterHook.Dispose(); starts.Clear(); retiring.Clear(); entered.Clear(); }
    }
}
