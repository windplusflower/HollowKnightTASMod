using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text.Json;
using HollowKnightTAS.Runtime.FullRun;

// Only dependencies of the source-linked execution kernel; production admission
// is deliberately not exercised by this pure original-IL equivalence fixture.
namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalSceneMappingCarry { internal const string ModuleMvid = "2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"; }
    internal static class CanonicalBossSetupDiagnostic { internal static void ValidateAbi() { } }
}

internal sealed class FixtureFault : Exception { }
internal sealed class Operations : CanonicalBossChallengeOperations
{
    internal readonly List<string> Trace = new();
    internal readonly List<Action> CompletionCallbacks = new(), ReturnCallbacks = new();
    internal GameManager Manager = Uninitialized<GameManager>();
    internal BossSceneController Self = Uninitialized<BossSceneController>();
    internal BossStatue Statue = Uninitialized<BossStatue>();
    internal CanonicalBossChallengeContext Context = new();
    internal object Closure = null!;
    internal bool Live, Unlocked;
    internal int ThrowAt = -1, ReadCount;
    internal bool FlipSecond, MutateAfterWrite;
    internal BossStatue.Completion Stored;
    internal string? StoredKey, CompletionKey;
    internal int? CompletedLevel;

#pragma warning disable SYSLIB0050
    internal static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
    internal static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal static readonly Type Primary = typeof(BossChallengeUI).GetNestedType("<>c__DisplayClass33_0", BindingFlags.NonPublic)!;
    internal Operations(int level, string? key, bool unlocked, bool live, bool always, bool flip, bool mutate)
    {
        Manager.playerData = Uninitialized<PlayerData>();
        Live = live; Unlocked = unlocked; FlipSecond = flip; MutateAfterWrite = mutate;
        Statue.statueStatePD = key!; Statue.dreamStatueStatePD = "dream"; Statue.isAlwaysUnlocked = always;
        var ui = Uninitialized<BossChallengeUI>();
        typeof(BossChallengeUI).GetField("bossStatue", Flags)!.SetValue(ui, Statue);
#pragma warning disable SYSLIB0050
        Closure = FormatterServices.GetUninitializedObject(Primary);
#pragma warning restore SYSLIB0050
        Primary.GetField("<>4__this", Flags)!.SetValue(Closure, ui);
        Primary.GetField("level", Flags)!.SetValue(Closure, level);
        Context = new CanonicalBossChallengeContext { Level = level, Statue = new CanonicalBossStatueContext {
            StatueStatePD = key, DreamStatueStatePD = "dream", IsAlwaysUnlocked = always } };
    }
    private void Note(string value) { Trace.Add(value); if (Trace.Count - 1 == ThrowAt) throw new FixtureFault(); }
    internal override object GameManager() { Note("GM"); return Manager; }
    internal override object PlayerData(object manager) => ((GameManager)manager).playerData;
    internal override BossStatue.Completion None() { Note("None"); return BossStatue.Completion.None; }
    internal override BossStatue.Completion GetCompletion(object manager, string? key)
    {
        Note("Get:" + (key ?? "<null>"));
        ReadCount++;
        return new BossStatue.Completion { hasBeenSeen = true, seenTier3Unlock = true,
            isUnlocked = Unlocked, usingAltVersion = FlipSecond && ReadCount >= 3 };
    }
    internal override void SetCompletion(object manager, string? key, BossStatue.Completion value)
    {
        Note($"Set:{key}:{value.completedTier1}:{value.completedTier2}:{value.completedTier3}:{value.hasBeenSeen}:{value.seenTier3Unlock}");
        Stored = value; StoredKey = key;
        if (MutateAfterWrite) {
            Statue.statueStatePD = Context.Statue.StatueStatePD = "changed";
            Statue.dreamStatueStatePD = Context.Statue.DreamStatueStatePD = "changed-dream";
            Context.Level = 1;
            Primary.GetField("level", Flags)!.SetValue(Closure, 1);
        }
    }
    internal override bool IsLive(BossScene? scene) { Note("Live"); return Live; }
    internal override bool IsUnlocked(BossScene scene) { Note("Unlock:Statue"); return false; }
    internal override void SetString(object player, string? value, string field) { Note("String:" + field + ":" + (value ?? "<null>")); CompletionKey = value; }
    internal override void SetInt(object player, int value, string field) { Note("Int:" + field + ":" + value); CompletedLevel = value; }
    internal override object NewReturnContext(object self) => CanonicalBossChallengeOriginal.NewReturn(self);
    internal override void SetLevel(object self, int value) => Note("Level:" + value);
    internal override void SetDreamReturn(object self, string value) => Note("DreamReturn:" + value);
    internal override void AddCompletion(object self, Action callback) { Note("AddDead"); CompletionCallbacks.Add(callback); }
    internal override void AddReturn(object self, object returning) => AddOriginalReturn(CanonicalBossChallengeOriginal.ReturnCallback(returning));
    internal void AddOriginalReturn(Action callback) { Note("AddReturn"); ReturnCallbacks.Add(callback); }
    internal void DreamReturn() => Note("DoDreamReturn");
}

internal static class Probe
{
    internal static Operations Current = null!;
    internal static GameManager Manager() => (GameManager)Current.GameManager();
    internal static BossStatue.Completion None() => Current.None();
    internal static BossStatue.Completion Get(GameManager manager, string? key) => Current.GetCompletion(manager, key);
    internal static void Set(GameManager manager, string? key, BossStatue.Completion value) => Current.SetCompletion(manager, key, value);
    internal static bool Live(UnityEngine.Object? scene) => Current.IsLive(scene as BossScene);
    internal static bool Unlock(BossScene scene, BossSceneCheckSource source)
    { if (source != BossSceneCheckSource.Statue) throw new Exception("Wrong source"); return Current.IsUnlocked(scene); }
    internal static void String(PlayerData player, string? value, string field) => Current.SetString(player, value, field);
    internal static void Int(PlayerData player, int value, string field) => Current.SetInt(player, value, field);
    internal static void Level(BossSceneController self, int value) => Current.SetLevel(self, value);
    internal static void Dream(BossSceneController self, string value) => Current.SetDreamReturn(self, value);
    internal static void Dead(BossSceneController self, Action callback) => Current.AddCompletion(self, callback);
    internal static void Return(BossSceneController self, Action callback) => Current.AddOriginalReturn(callback);
    internal static void DoReturn(BossSceneController self) => Current.DreamReturn();
}

internal static class Program
{
    private static readonly BindingFlags Flags = Operations.Flags;
    private static DynamicMethod? State, Using;
    private static MethodInfo? Replace(MethodInfo method)
    {
        string? name = method.DeclaringType == typeof(GameManager) ? method.Name switch {
            "get_instance" => "Manager", "GetPlayerDataVariable" => "Get", "SetPlayerDataVariable" => "Set", _ => null }
            : method.DeclaringType == typeof(PlayerData) ? method.Name switch { "SetStringSwappedArgs" => "String", "SetIntSwappedArgs" => "Int", _ => null }
            : method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "op_Implicit" ? "Live"
            : method.DeclaringType == typeof(BossScene) && method.Name == "IsUnlocked" ? "Unlock"
            : method.DeclaringType == typeof(BossStatue.Completion) && method.Name == "get_None" ? "None"
            : method.DeclaringType == typeof(BossSceneController) ? method.Name switch {
                "set_BossLevel" => "Level", "set_DreamReturnEvent" => "Dream", "add_OnBossesDead" => "Dead",
                "add_OnBossSceneComplete" => "Return", "DoDreamReturn" => "DoReturn", _ => null } : null;
        if (method.DeclaringType == typeof(BossStatue)) {
            if (method.Name == "get_StatueState") return State;
            if (method.Name == "get_UsingDreamVersion") return Using;
        }
        return name == null ? null : typeof(Probe).GetMethod(name, Flags)!;
    }
    private static void Require(bool result, string detail) { if (!result) throw new Exception(detail); }
    private static string Run(Action action)
    {
        try { action(); return "ok"; }
        catch (TargetInvocationException error) when (error.InnerException is FixtureFault) { return "fault"; }
        catch (FixtureFault) { return "fault"; }
    }
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--font") { FontContract.Run(args[1]); return; }
        Require(typeof(BossChallengeUI).Module.ModuleVersionId.ToString("D") == CanonicalSceneMappingCarry.ModuleMvid, "Original module drift");
        CanonicalBossChallengeOriginal.Validate();
        State = OriginalIL.Copy(typeof(BossStatue).GetMethod("get_StatueState", Flags)!, Replace);
        Using = OriginalIL.Copy(typeof(BossStatue).GetMethod("get_UsingDreamVersion", Flags)!, Replace);
        var b0 = OriginalIL.Copy(Operations.Primary.GetMethod("<LoadBoss>b__0", Flags)!, Replace);
        var b1 = OriginalIL.Copy(Operations.Primary.GetMethod("<LoadBoss>b__1", Flags)!, Replace);
        var secondary = typeof(BossChallengeUI).GetNestedType("<>c__DisplayClass33_1", BindingFlags.NonPublic)!;
        var b2 = OriginalIL.Copy(secondary.GetMethod("<LoadBoss>b__2", Flags)!, Replace);
        int comparisons = 0, faults = 0;
        foreach (int level in new[] { 0, 1, 2, 3 })
        foreach (string? key in new string?[] { null, "", "normal" })
        foreach (bool unlocked in new[] { false, true })
        foreach (bool live in new[] { false, true })
        foreach (bool always in new[] { false, true })
        foreach (bool flip in new[] { false, true })
        foreach (bool mutate in new[] { false, true }) {
            var original = new Operations(level, key, unlocked, live, always, flip, mutate);
            var service = new Operations(level, key, unlocked, live, always, flip, mutate);
            Probe.Current = original;
            b1.Invoke(null, new[] { original.Closure });
            service.Context.Complete(service);
            Require(original.Trace.SequenceEqual(service.Trace), "b1 trace differs: " + string.Join(",", original.Trace) + " versus " + string.Join(",", service.Trace));
            Require(original.Stored.Equals(service.Stored) && original.StoredKey == service.StoredKey
                && original.CompletedLevel == service.CompletedLevel && original.CompletionKey == service.CompletionKey, "b1 effects differ");
            comparisons++;
            for (int at = 0; at < original.Trace.Count; at++) {
                var left = new Operations(level, key, unlocked, live, always, flip, mutate) { ThrowAt = at };
                var right = new Operations(level, key, unlocked, live, always, flip, mutate) { ThrowAt = at };
                Probe.Current = left;
                Require(Run(() => b1.Invoke(null, new[] { left.Closure })) == "fault"
                    && Run(() => right.Context.Complete(right)) == "fault" && left.Trace.SequenceEqual(right.Trace), "b1 exception prefix differs");
                faults++;
            }
        }
        int setups = 0, setupFaults = 0;
        foreach (int level in new[] { 0, 1, 2 })
        foreach (bool cached in new[] { false, true }) {
            var left = new Operations(level, "normal", false, true, false, false, false);
            var right = new Operations(level, "normal", false, true, false, false, false);
            if (cached) {
                Operations.Primary.GetField("<>9__1", Flags)!.SetValue(left.Closure,
                    Delegate.CreateDelegate(typeof(Action), left.Closure, Operations.Primary.GetMethod("<LoadBoss>b__1", Flags)!));
                right.Context.CachedCompletion = right.Context.Complete;
            }
            Probe.Current = left;
            for (int repeat = 0; repeat < 2; repeat++) {
                b0.Invoke(null, new[] { left.Closure, (object)left.Self });
                right.Context.Setup(right.Self, right);
            }
            Require(left.Trace.SequenceEqual(right.Trace) && left.CompletionCallbacks.Count == 2 && right.CompletionCallbacks.Count == 2, "b0 ordered subscriptions differ");
            Require(ReferenceEquals(left.CompletionCallbacks[0], left.CompletionCallbacks[1])
                && ReferenceEquals(right.CompletionCallbacks[0], right.CompletionCallbacks[1]), "b0 cache alias differs");
            foreach (var operations in new[] { left, right }) {
                Require(!ReferenceEquals(operations.ReturnCallbacks[0].Target, operations.ReturnCallbacks[1].Target), "Return closure incorrectly reused");
                foreach (var callback in operations.ReturnCallbacks) {
                    Require(callback.Method.MetadataToken == 100666695 && callback.Target!.GetType() == secondary
                        && ReferenceEquals(secondary.GetField("self", Flags)!.GetValue(callback.Target), operations.Self), "Original return alias differs");
                    Probe.Current = operations; b2.Invoke(null, new[] { callback.Target });
                }
                Action? list = operations.CompletionCallbacks[0] + operations.CompletionCallbacks[1];
                list -= operations.CompletionCallbacks[0];
                Require(list!.GetInvocationList().Length == 1 && list == operations.CompletionCallbacks[1], "Duplicate removal differs");
            }
            Require(left.Trace.SequenceEqual(right.Trace), "Return API effects differ");
            for (int at = 0; at < 4; at++) {
                var failLeft = new Operations(level, "normal", false, true, false, false, false) { ThrowAt = at };
                var failRight = new Operations(level, "normal", false, true, false, false, false) { ThrowAt = at };
                if (cached) {
                    Operations.Primary.GetField("<>9__1", Flags)!.SetValue(failLeft.Closure,
                        Delegate.CreateDelegate(typeof(Action), failLeft.Closure, Operations.Primary.GetMethod("<LoadBoss>b__1", Flags)!));
                    failRight.Context.CachedCompletion = failRight.Context.Complete;
                }
                Probe.Current = failLeft;
                Require(Run(() => b0.Invoke(null, new[] { failLeft.Closure, (object)failLeft.Self })) == "fault"
                    && Run(() => failRight.Context.Setup(failRight.Self, failRight)) == "fault"
                    && failLeft.Trace.SequenceEqual(failRight.Trace)
                    && (Operations.Primary.GetField("<>9__1", Flags)!.GetValue(failLeft.Closure) == null)
                        == (failRight.Context.CachedCompletion == null)
                    && failLeft.CompletionCallbacks.Count == failRight.CompletionCallbacks.Count
                    && failLeft.ReturnCallbacks.Count == failRight.ReturnCallbacks.Count, "b0 exception/cache prefix differs");
                setupFaults++;
            }
            setups++;
        }
        Console.WriteLine(JsonSerializer.Serialize(new { originalILComparisons = comparisons, exceptionPrefixes = faults,
            repeatedSetupCacheReturnCases = setups, setupExceptionPrefixes = setupFaults,
            originalReturnMethodPreserved = true, nativeCallsOrGameRun = false,
            productionAdmissionAndGraphNotValidated = true }));
    }
}
