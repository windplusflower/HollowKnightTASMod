using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Semantic gameplay context, deliberately distinct from a retired Unity UI.
    // Admission and the versioned three-path policy own conversion; this class
    // alone never reads or changes a static callback slot.
    internal sealed class CanonicalBossChallengeContext
    {
        public int Level = 0;
        public CanonicalBossStatueContext Statue = null!;
        public Action? CachedCompletion;

        internal void Setup(BossSceneController self) => Setup(self, CanonicalBossChallengeOperations.Original);
        internal void Complete() => Complete(CanonicalBossChallengeOperations.Original);

        internal void Setup(object self, CanonicalBossChallengeOperations operations)
        {
            // Original b0 allocates its secondary closure before its first API.
            var returning = operations.NewReturnContext(self);
            operations.SetLevel(self, Level);
            operations.SetDreamReturn(self, "DREAM RETURN");
            var completion = CachedCompletion;
            if (completion == null) CachedCompletion = completion = Complete;
            operations.AddCompletion(self, completion);
            operations.AddReturn(self, returning);
        }

        internal void Complete(CanonicalBossChallengeOperations operations)
        {
            var key = Statue.CompletionKey(operations);
            var value = operations.GetCompletion(operations.GameManager(), key);
            switch (Level) {
                case 0: value.completedTier1 = true; break;
                case 1: value.completedTier2 = true; break;
                case 2: value.completedTier3 = true; break;
            }
            operations.SetCompletion(operations.GameManager(), key, value);
            // Evaluation order is material: fetch GM/playerData BEFORE the
            // second fresh UsingDreamVersion path, just as the original IL.
            var player = operations.PlayerData(operations.GameManager());
            operations.SetString(player, Statue.CompletionKey(operations), "currentBossStatueCompletionKey");
            player = operations.PlayerData(operations.GameManager());
            operations.SetInt(player, Level, "bossStatueTargetLevel");
        }
    }

    internal sealed class CanonicalBossStatueContext
    {
        public string? StatueStatePD = null, DreamStatueStatePD = null;
        public bool IsAlwaysUnlocked = false;
        public BossScene? BossScene = null;

        internal string? CompletionKey(CanonicalBossChallengeOperations operations)
        {
            var state = State(operations);
            return state.usingAltVersion ? DreamStatueStatePD : StatueStatePD;
        }

        private BossStatue.Completion State(CanonicalBossChallengeOperations operations)
        {
            if (string.IsNullOrEmpty(StatueStatePD)) return operations.None();
            var state = operations.GetCompletion(operations.GameManager(), StatueStatePD);
            // Do not simplify this to usingAltVersion: original unlock tests
            // and their hooks still run, even though isUnlocked stays local.
            if (!state.isUnlocked && operations.IsLive(BossScene) && (operations.IsUnlocked(BossScene!) || IsAlwaysUnlocked))
                state.isUnlocked = true;
            return state;
        }
    }

    // Operations are a test seam, not context state or payload data. The sole
    // production implementation calls original APIs and preserves fresh reads.
    internal abstract class CanonicalBossChallengeOperations
    {
        internal static readonly CanonicalBossChallengeOperations Original = new OriginalOperations();
        internal abstract object GameManager();
        internal abstract object PlayerData(object manager);
        internal abstract BossStatue.Completion None();
        internal abstract BossStatue.Completion GetCompletion(object manager, string? key);
        internal abstract void SetCompletion(object manager, string? key, BossStatue.Completion value);
        internal abstract bool IsLive(BossScene? scene);
        internal abstract bool IsUnlocked(BossScene scene);
        internal abstract void SetString(object player, string? value, string field);
        internal abstract void SetInt(object player, int value, string field);
        internal abstract object NewReturnContext(object self);
        internal abstract void SetLevel(object self, int value);
        internal abstract void SetDreamReturn(object self, string value);
        internal abstract void AddCompletion(object self, Action callback);
        internal abstract void AddReturn(object self, object returning);

        private sealed class OriginalOperations : CanonicalBossChallengeOperations
        {
            internal override object GameManager() => global::GameManager.instance;
            internal override object PlayerData(object manager) => ((global::GameManager)manager).playerData;
            internal override BossStatue.Completion None() => BossStatue.Completion.None;
            internal override BossStatue.Completion GetCompletion(object manager, string? key)
                => ((global::GameManager)manager).GetPlayerDataVariable<BossStatue.Completion>(key!);
            internal override void SetCompletion(object manager, string? key, BossStatue.Completion value)
                => ((global::GameManager)manager).SetPlayerDataVariable(key!, value);
            internal override bool IsLive(BossScene? scene) => scene;
            internal override bool IsUnlocked(BossScene scene) => scene.IsUnlocked(BossSceneCheckSource.Statue);
            internal override void SetString(object player, string? value, string field)
                => CanonicalBossChallengeOriginal.SetString((global::PlayerData)player, value!, field);
            internal override void SetInt(object player, int value, string field)
                => CanonicalBossChallengeOriginal.SetInt((global::PlayerData)player, value, field);
            internal override object NewReturnContext(object self) => CanonicalBossChallengeOriginal.NewReturn(self);
            internal override void SetLevel(object self, int value) => ((BossSceneController)self).BossLevel = value;
            internal override void SetDreamReturn(object self, string value) => ((BossSceneController)self).DreamReturnEvent = value;
            internal override void AddCompletion(object self, Action callback) => ((BossSceneController)self).OnBossesDead += callback;
            internal override void AddReturn(object self, object returning)
                => ((BossSceneController)self).OnBossSceneComplete += CanonicalBossChallengeOriginal.ReturnCallback(returning);
        }
    }

    internal static class CanonicalBossChallengeOriginal
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static readonly Type Primary = typeof(BossChallengeUI).GetNestedType("<>c__DisplayClass33_0", BindingFlags.NonPublic)!;
        internal static readonly Type Secondary = typeof(BossChallengeUI).GetNestedType("<>c__DisplayClass33_1", BindingFlags.NonPublic)!;
        private static readonly FieldInfo Self = Secondary.GetField("self", Flags)!;
        private static readonly ConstructorInfo Constructor = Secondary.GetConstructor(Flags, null, Type.EmptyTypes, null)!;
        private static readonly MethodInfo Return = Secondary.GetMethod("<LoadBoss>b__2", Flags)!;
        internal static readonly Action<PlayerData, string, string> SetString = OriginalCall<Action<PlayerData, string, string>>("SetStringSwappedArgs", typeof(string));
        internal static readonly Action<PlayerData, int, string> SetInt = OriginalCall<Action<PlayerData, int, string>>("SetIntSwappedArgs", typeof(int));

        private static T OriginalCall<T>(string name, Type value) where T : Delegate
        {
            var parameters = new[] { typeof(PlayerData), value, typeof(string) };
            var method = typeof(PlayerData).GetMethod(name, Flags, null, new[] { value, typeof(string) }, null)
                ?? throw new InvalidDataException("Original challenge PlayerData API is absent: " + name);
            var bridge = new DynamicMethod("CanonicalBoss_" + name, typeof(void), parameters, typeof(CanonicalBossChallengeOriginal).Module, true);
            var il = bridge.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, method); il.Emit(OpCodes.Ret);
            return (T)bridge.CreateDelegate(typeof(T));
        }

        internal static void Validate()
        {
            if (typeof(BossChallengeUI).Module.ModuleVersionId.ToString("D") != CanonicalSceneMappingCarry.ModuleMvid
                || Primary == null || Secondary == null || Self == null || Constructor == null || Return == null
                || !Primary.GetFields(Flags | BindingFlags.DeclaredOnly).OrderBy(field => field.MetadataToken)
                    .Select(field => field.Name + ":" + field.FieldType.FullName).SequenceEqual(new[] {
                        "level:System.Int32", "<>4__this:BossChallengeUI", "<>9__1:System.Action" })
                || Primary.GetMethod("<LoadBoss>b__0", Flags)?.MetadataToken != 100666692
                || Primary.GetMethod("<LoadBoss>b__1", Flags)?.MetadataToken != 100666693
                || Return.MetadataToken != 100666695 || Return.ReturnType != typeof(void) || Return.GetParameters().Length != 0
                || !Secondary.GetFields(Flags | BindingFlags.DeclaredOnly).Select(field => field.Name + ":" + field.FieldType.FullName)
                    .SequenceEqual(new[] { "self:BossSceneController" }))
                throw new InvalidDataException("Original challenge return closure layout differs.");
            // These ScriptableObject classes declare no Unity load callbacks.
            // Resource loading must not silently introduce gameplay producers.
            foreach (var type in new[] { typeof(BossSequence), typeof(BossScene) })
            {
                if (type.TypeInitializer != null || typeof(UnityEngine.ISerializationCallbackReceiver).IsAssignableFrom(type))
                    throw new InvalidDataException("Boss asset declares unqualified static/serialization callbacks: " + type.FullName);
                foreach (var name in new[] { "Awake", "OnEnable", "OnDisable", "OnDestroy", "OnValidate", "Reset" })
                    if (type.GetMethod(name, Flags | BindingFlags.DeclaredOnly) != null)
                        throw new InvalidDataException("Boss asset declares an unqualified lifecycle callback: " + type.FullName + "/" + name);
            }
            foreach (var type in new[] { typeof(BossSceneController), typeof(BossSequenceController), typeof(BossChallengeUI), typeof(BossStatue) })
                RequireStaticInventory(type);
        }
        internal static void RequireStaticInventory(Type type)
        {
            var initializer = type.TypeInitializer;
            var expected = type == typeof(BossSceneController) ? "2A" : type == typeof(BossChallengeUI) ? "15-80-40-17-00-0A-2A" : null;
            if (initializer == null ? expected != null : expected == null
                || BitConverter.ToString(initializer.GetMethodBody()!.GetILAsByteArray()!) != expected)
                throw new InvalidDataException("Challenge static initializer differs: " + type.FullName);
            if (type == typeof(BossChallengeUI)) {
                var field = type.Module.ResolveField(0x0a001740);
                if (field == null || field.DeclaringType != type || !field.IsStatic || field.Name != "lastSelectedButton" || field.FieldType != typeof(int))
                    throw new InvalidDataException("Original challenge UI scalar initializer target differs.");
            }
            if (type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Any(field => field.IsDefined(typeof(ThreadStaticAttribute), false)))
                throw new InvalidDataException("Challenge static inventory has a thread-local field.");
        }

        internal static object NewReturn(object self)
        {
            var result = Constructor.Invoke(Array.Empty<object>());
            Self.SetValue(result, self);
            return result;
        }
        internal static Action ReturnCallback(object returning)
        {
            if (returning.GetType() != Secondary) throw new InvalidDataException("Original challenge return target differs.");
            return (Action)Delegate.CreateDelegate(typeof(Action), returning, Return);
        }
    }
}
