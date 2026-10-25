using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One bounded source-boundary observation. Never invokes the pending callback,
    // a gameplay getter, or an asset factory, and never serializes the D packet.
    internal static class CanonicalBossSetupDiagnostic
    {
        internal const string Flag = "HKTAS_LOADING_BOSS_SETUP_DIAGNOSTIC";
        // The actual source branch uses this selector and capture call together;
        // tests can exercise its ordering without constructing a game world.
        internal static bool TryCapture<T>(bool phaseOne, bool actualD, bool source, int ordinal, int selected,
            bool observe, Action audit, Func<T> capture, out T result)
        {
            result = default!;
            if (!phaseOne || !actualD || !source || ordinal != selected) return false;
            if (observe) {
                try { audit(); }
                catch (Exception error) {
                    try { Modding.Logger.LogError("[HKTAS] Boss setup audit failed: " + error.GetType().Name + ":" + error.Message); }
                    catch { /* The original capture must still run once. */ }
                }
            }
            result = capture();
            return true;
        }
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static FieldInfo Field(Type type, string name) => type.GetField(name, All)
            ?? throw new InvalidDataException("Boss diagnostic raw field absent: " + type.FullName + "/" + name);
        private static object? Read(Type type, string name, object? owner = null) => Field(type, name).GetValue(owner);
        internal static object[] ValidateAbi()
        {
            var rows = new System.Collections.Generic.List<object>();
            void Check(Type declaring, string name, Type expected, bool isStatic = false, bool optional = false)
            {
                var field = declaring.GetField(name, All | BindingFlags.DeclaredOnly);
                if (field == null && !optional || field != null && (field.DeclaringType != declaring || field.FieldType != expected || field.IsStatic != isStatic))
                    throw new InvalidDataException("Boss diagnostic complete ABI preflight differs: " + declaring.FullName + "/" + name);
                rows.Add(new { declaringType = declaring.FullName, name, fieldType = expected.FullName, isStatic, optional, present = field != null });
            }
            var closure = typeof(BossChallengeUI).GetNestedType("<>c__DisplayClass33_0", BindingFlags.NonPublic)
                ?? throw new InvalidDataException("Original challenge closure type absent.");
            Check(closure, "level", typeof(int)); Check(closure, "<>4__this", typeof(BossChallengeUI)); Check(closure, "<>9__1", typeof(Action));
            Check(typeof(BossChallengeUI), "bossStatue", typeof(BossStatue));
            Check(typeof(BossStatue), "statueStatePD", typeof(string)); Check(typeof(BossStatue), "dreamStatueStatePD", typeof(string));
            Check(typeof(BossStatue), "isAlwaysUnlocked", typeof(bool)); Check(typeof(BossStatue), "bossScene", typeof(BossScene));
            Check(typeof(BossSceneController), "SetupEvent", typeof(BossSceneController.SetupEventDelegate), true);
            Check(typeof(BossSequenceController), "currentData", typeof(BossSequenceController.BossSequenceData), true);
            Check(typeof(BossSequenceController), "currentSequence", typeof(BossSequence), true);
            Check(typeof(BossSequenceController), "bossIndex", typeof(int), true); Check(typeof(BossSequenceController), "<WasCompleted>k__BackingField", typeof(bool), true);
            Check(typeof(PlayerData), "_instance", typeof(PlayerData), true); Check(typeof(PlayerData), "currentBossSequence", typeof(BossSequenceController.BossSequenceData));
            var data = typeof(BossSequenceController.BossSequenceData);
            Check(data, "bindings", typeof(BossSequenceController.ChallengeBindings)); Check(data, "timer", typeof(float));
            Check(data, "knightDamaged", typeof(bool)); Check(data, "playerData", typeof(string));
            Check(data, "previousCompletion", typeof(BossSequenceDoor.Completion)); Check(data, "previousEquippedCharms", typeof(int[]));
            Check(data, "wasOvercharmed", typeof(bool)); Check(data, "bossSequenceName", typeof(string));
            foreach (var name in new[] { "canUnlock", "unlocked", "completed", "allBindings", "noHits", "boundNail", "boundShell", "boundCharms", "boundSoul" })
                Check(typeof(BossSequenceDoor.Completion), name, typeof(bool));
            Check(typeof(BossSequenceDoor.Completion), "viewedBossSceneCompletions", typeof(System.Collections.Generic.List<string>));
            Check(typeof(UnityEngine.Object), "m_InstanceID", typeof(int), optional: true);
            return rows.ToArray();
        }
        private static string[] Fields(Type type) => type.GetFields(All | BindingFlags.DeclaredOnly)
            .Where(field => !field.IsStatic).OrderBy(field => field.MetadataToken)
            .Select(field => field.Name + ":" + field.FieldType.FullName).ToArray();
        private static object Method(Delegate callback) => new {
            module = callback.Method.Module.ModuleVersionId, token = callback.Method.MetadataToken,
            declaringType = callback.Method.DeclaringType?.FullName, name = callback.Method.Name,
            signature = callback.Method.ToString(), isStatic = callback.Method.IsStatic };
        private static object? Owner(UnityEngine.Object? value, BoundaryObjectIdentity identities)
        {
            if (ReferenceEquals(value, null)) return null;
            var cached = LoadingSerialSceneProbe.TryCachedSourceOwner(value!, out var provenance);
            try {
                var live = value != null;
                return new { type = value!.GetType().FullName, nativeLive = live,
                    identity = live ? identities.Get(value) : null, retiredSourceProvenance = provenance,
                    cachedIdentityAvailable = cached, nativeAddressEmitted = false };
            } catch (Exception error) { return new { type = value!.GetType().FullName, refused = true,
                reason = error.GetType().Name + ":" + error.Message, retiredSourceProvenance = provenance,
                cachedIdentityAvailable = cached, nativeAddressEmitted = false }; }
        }
        private static object Callback(Delegate callback, BoundaryObjectIdentity identities)
        {
            var target = callback.Target;
            if (target == null) return new { method = Method(callback), targetNull = true, acceptedShape = false };
            var type = target.GetType();
            if (type.DeclaringType != typeof(BossChallengeUI) || type.Name != "<>c__DisplayClass33_0"
                || callback.Method.Name != "<LoadBoss>b__0"
                || callback.Method.MetadataToken != 100666692
                || !Fields(type).SequenceEqual(new[] { "level:System.Int32", "<>4__this:BossChallengeUI", "<>9__1:System.Action" }))
                return new { method = Method(callback), targetType = type.FullName, fields = Fields(type), acceptedShape = false };
            var ui = Read(type, "<>4__this", target) as BossChallengeUI;
            var statue = ReferenceEquals(ui, null) ? null : Read(typeof(BossChallengeUI), "bossStatue", ui) as BossStatue;
            var nested = Read(type, "<>9__1", target) as Action;
            return new { method = Method(callback), targetType = type.FullName, acceptedShape = true,
                fields = Fields(type), level = Read(type, "level", target), ui = Owner(ui, identities),
                statue = Owner(statue, identities), uiStatueFieldIsExactOwner = !ReferenceEquals(ui, null) && !ReferenceEquals(statue, null),
                nested = nested == null ? null : new { count = nested.GetInvocationList().Length,
                    methods = nested.GetInvocationList().Take(8).Select(Method).ToArray(),
                    sameClosure = ReferenceEquals(nested.Target, target),
                    acceptedShape = nested.GetInvocationList().Length == 1 && ReferenceEquals(nested.Target, target)
                        && nested.Method.Name == "<LoadBoss>b__1" && nested.Method.MetadataToken == 100666693 },
                statueRaw = ReferenceEquals(statue, null) ? null : new {
                    statueStatePD = Read(typeof(BossStatue), "statueStatePD", statue),
                    dreamStatueStatePD = Read(typeof(BossStatue), "dreamStatueStatePD", statue),
                    isAlwaysUnlocked = Read(typeof(BossStatue), "isAlwaysUnlocked", statue),
                    bossScene = Owner(Read(typeof(BossStatue), "bossScene", statue) as UnityEngine.Object, identities) },
                fullUiAndStatueFieldsNotTraversed = true, gameplayGettersNotInvoked = true };
        }
        private static object Block(Func<object> read)
        {
            try { return read(); }
            catch (Exception error) { return new { refused = true, reason = error.GetType().Name + ":" + error.Message }; }
        }
        private static object Sequence(BoundaryObjectIdentity identities)
        {
            var data = Read(typeof(BossSequenceController), "currentData");
            var raw = Block(() => {
                if (data != null && (data.GetType() != typeof(BossSequenceController.BossSequenceData)
                    || !Fields(data.GetType()).SequenceEqual(new[] { "bindings:BossSequenceController+ChallengeBindings", "timer:System.Single",
                        "knightDamaged:System.Boolean", "playerData:System.String", "previousCompletion:BossSequenceDoor+Completion",
                        "previousEquippedCharms:System.Int32[]", "wasOvercharmed:System.Boolean", "bossSequenceName:System.String" })
                    || ((int[]?)Read(data.GetType(), "previousEquippedCharms", data))?.Length > 128))
                    throw new InvalidDataException("Boss sequence raw data diagnostic shape differs.");
                object? Value(FieldInfo field) {
                    var value = field.GetValue(data);
                    if (value is string text && text.Length > 512) throw new InvalidDataException("Boss sequence diagnostic text bound exceeded.");
                    if (field.Name != "previousCompletion") return value;
                    var completion = typeof(BossSequenceDoor.Completion);
                    var viewed = (System.Collections.Generic.List<string>?)Read(completion, "viewedBossSceneCompletions", value);
                    if (viewed != null && (viewed.Count > 256 || viewed.Any(text => text == null || text.Length > 512)))
                        throw new InvalidDataException("Boss sequence completion diagnostic bound exceeded.");
                    return completion.GetFields(BindingFlags.Public | BindingFlags.Instance).ToDictionary(item => item.Name,
                        item => item.Name == "viewedBossSceneCompletions" ? (object?)viewed?.ToArray() : item.GetValue(value));
                }
                return new { currentDataRaw = data == null ? null : data.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .ToDictionary(field => field.Name, Value) };
            });
            var player = Read(typeof(PlayerData), "_instance");
            return new { wasCompleted = Read(typeof(BossSequenceController), "<WasCompleted>k__BackingField"),
                bossIndex = Read(typeof(BossSequenceController), "bossIndex"), currentDataNull = data == null,
                currentDataType = data?.GetType().FullName, data = raw,
                playerDataPresent = player != null, aliasesPlayerData = player != null
                    && ReferenceEquals(data, Read(typeof(PlayerData), "currentBossSequence", player)),
                asset = Owner(Read(typeof(BossSequenceController), "currentSequence") as UnityEngine.Object, identities),
                onlyExactRawDataFieldsExpanded = true };
        }
        internal static void Write(int ordinal, BoundaryObjectIdentity identities, string directory)
        {
            if (Environment.GetEnvironmentVariable(Flag) != "1" || !CanonicalActualDExperiment.CaptureSource
                || ordinal != CanonicalActualDExperiment.SelectedOrdinal) return;
            CanonicalPhaseOneTrace.RequireContext(Environment.GetEnvironmentVariable);
            object result;
            try {
                ValidateAbi();
                if (typeof(BossChallengeUI).Module.ModuleVersionId != new Guid("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"))
                    throw new InvalidDataException("Boss setup diagnostic original module differs.");
                var setup = Read(typeof(BossSceneController), "SetupEvent") as Delegate;
                var calls = setup?.GetInvocationList() ?? Array.Empty<Delegate>();
                if (calls.Length > 8) throw new InvalidDataException("Boss setup invocation budget exceeded.");
                result = new { diagnosticOnly = true, ordinal, setupNull = setup == null,
                    invocationOrder = calls.Select(call => new { method = Method(call), targetType = call.Target?.GetType().FullName,
                        data = Block(() => Callback(call, identities)) }).ToArray(),
                    sequence = Block(() => Sequence(identities)),
                    sourcePacketWritten = false, completeStateCoverage = false, restoreQualified = false };
            } catch (Exception error) { result = new { diagnosticOnly = true, refused = true, reason = error.GetType().Name + ":" + error.Message }; }
            File.WriteAllText(Path.Combine(directory, "canonical-boss-setup-source-audit.json"), JsonConvert.SerializeObject(result));
        }
    }
}
