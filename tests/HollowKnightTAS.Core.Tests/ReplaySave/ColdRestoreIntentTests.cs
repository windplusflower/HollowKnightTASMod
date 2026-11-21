using System;
using System.IO;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.ReplaySave;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.ReplaySave
{
    [TestClass]
    public sealed class ColdRestoreIntentTests
    {
        [TestMethod]
        public void VersionFiveConsentBindsTargetAndCannotEnterOlderIntents()
        {
            var authorization = new ReplaySlotOverwriteAuthorization(Hash('1'), Hash('3'), Hash('7'), 250,
                new[] { new ReplaySlotFileIdentity(1, Hash('8'), "") }).Serialize();
            var intent = CreateIntent(lifecyclePlanObjectSha256: Hash('7'), rootPlan: true, slotAuthorization: authorization);
            var decoded = ColdRestoreIntentCodec.Deserialize(ColdRestoreIntentCodec.Serialize(intent));
            Assert.AreEqual(authorization, decoded.SlotOverwriteAuthorization);
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(schemaVersion: 4,
                lifecyclePlanObjectSha256: Hash('7'), rootPlan: true, slotAuthorization: authorization));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(targetMovieTick: 251,
                lifecyclePlanObjectSha256: Hash('7'), rootPlan: true, slotAuthorization: authorization));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(lifecyclePlanObjectSha256: Hash('6'),
                rootPlan: true, slotAuthorization: authorization));
        }

        [TestMethod]
        public void RootPlanIntentRoundTripsWithoutInventingCheckpointEvidence()
        {
            var intent = CreateIntent(lifecyclePlanObjectSha256: Hash('7'), rootPlan: true);
            var bytes = ColdRestoreIntentCodec.Serialize(intent);
            var decoded = ColdRestoreIntentCodec.Deserialize(bytes);
            Assert.IsTrue(decoded.IsRootPlanSource);
            Assert.AreEqual(string.Empty, decoded.ReplaySaveId);
            Assert.AreEqual(string.Empty, decoded.JournalHeadSha256);
            Assert.AreEqual(string.Empty, decoded.TargetSemanticSha256);
            Assert.IsFalse(decoded.RequiresExactTargetSemantic);
            CollectionAssert.AreEqual(bytes, ColdRestoreIntentCodec.Serialize(decoded));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(rootPlan: true));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(rootPlan: true,
                schemaVersion: 3, lifecyclePlanObjectSha256: Hash('7')));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(rootPlan: true,
                lifecyclePlanObjectSha256: Hash('7'), targetSemanticSha256: Hash('6')));
            var fakeJournal = Encoding.UTF8.GetString(bytes).Replace("\"journalHeadSha256\":\"\"",
                "\"journalHeadSha256\":\"" + Hash('5') + "\"");
            Assert.ThrowsExactly<InvalidDataException>(() => ColdRestoreIntentCodec.Deserialize(Encoding.UTF8.GetBytes(fakeJournal)));
        }

        [TestMethod]
        public void VersionThreeBindsPlanOnlyForBranchSeekAndKeepsOldSchemasReadable()
        {
            var intent = CreateIntent(schemaVersion: 3, lifecyclePlanObjectSha256: Hash('7'));
            var decoded = ColdRestoreIntentCodec.Deserialize(ColdRestoreIntentCodec.Serialize(intent));
            Assert.AreEqual(Hash('7'), decoded.LifecyclePlanObjectSha256);
            Assert.IsFalse(decoded.RequiresExactTargetSemantic);
            foreach (var version in new[] { 1, 2 })
            {
                var old = CreateIntent(schemaVersion: version);
                var oldBytes = ColdRestoreIntentCodec.Serialize(old);
                Assert.IsFalse(Encoding.UTF8.GetString(oldBytes).Contains("lifecyclePlanObjectSha256"));
                Assert.AreEqual(string.Empty, ColdRestoreIntentCodec.Deserialize(oldBytes).LifecyclePlanObjectSha256);
                Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(schemaVersion: version, lifecyclePlanObjectSha256: Hash('7')));
            }
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(operationKind: ColdRestoreOperationKind.RestoreReplaySave,
                targetSemanticSha256: Hash('6'), lifecyclePlanObjectSha256: Hash('7')));
        }

        [TestMethod]
        public void MenuSourceHasNoFabricatedJournalAndRetainsExactTargetBinding()
        {
            var menu = CreateIntent(operationKind: ColdRestoreOperationKind.RestoreReplaySave,
                targetSemanticSha256: Hash('6'), menuSource: true);
            Assert.IsTrue(menu.IsMenuSource);
            Assert.AreEqual(-1L, menu.SourceCommittedMovieTick);
            Assert.AreEqual(string.Empty, menu.SourceMovieObjectSha256);
            Assert.IsTrue(menu.RequiresExactTargetSemantic);
            var bytes = ColdRestoreIntentCodec.Serialize(menu);
            var decoded = ColdRestoreIntentCodec.Deserialize(bytes);
            Assert.IsTrue(decoded.IsMenuSource);
            CollectionAssert.AreEqual(bytes, ColdRestoreIntentCodec.Serialize(decoded));
            var enteredMenu = Encoding.UTF8.GetString(bytes).Replace(
                "\"sourceSceneEpoch\":0", "\"sourceSceneEpoch\":1");
            Assert.AreEqual(1, ColdRestoreIntentCodec.Deserialize(Encoding.UTF8.GetBytes(enteredMenu)).SourceSceneEpoch);
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(menuSource: true));
            Assert.ThrowsExactly<ArgumentException>(() => CreateIntent(schemaVersion: 1,
                menuSource: true, operationKind: ColdRestoreOperationKind.RestoreReplaySave,
                targetSemanticSha256: Hash('6')));
            var wrongTick = Encoding.UTF8.GetString(bytes).Replace(
                "\"sourceCommittedMovieTick\":-1", "\"sourceCommittedMovieTick\":0");
            Assert.ThrowsExactly<InvalidDataException>(() => ColdRestoreIntentCodec.Deserialize(
                Encoding.UTF8.GetBytes(wrongTick)));
        }

        [TestMethod]
        public void SchemaOneSourceJournalRoundTripsWithoutChangingIdentity()
        {
            var legacy = CreateIntent(schemaVersion: 1);
            var bytes = ColdRestoreIntentCodec.Serialize(legacy);
            var decoded = ColdRestoreIntentCodec.Deserialize(bytes);
            Assert.AreEqual(1, decoded.SchemaVersion);
            Assert.IsFalse(decoded.IsMenuSource);
            CollectionAssert.AreEqual(bytes, ColdRestoreIntentCodec.Serialize(decoded));
        }

        [TestMethod]
        public void IntentCodec_IsCanonicalAndStableAcrossRoundTrips()
        {
            var intent = CreateIntent();
            var bytes = ColdRestoreIntentCodec.Serialize(intent);
            var expectedHash = ColdRestoreIntentCodec.ComputeSha256(intent);

            for (var iteration = 0; iteration < 100; iteration++)
            {
                intent = ColdRestoreIntentCodec.Deserialize(bytes);
                CollectionAssert.AreEqual(
                    bytes,
                    ColdRestoreIntentCodec.Serialize(intent));
                Assert.AreEqual(
                    expectedHash,
                    ColdRestoreIntentCodec.ComputeSha256(intent));
            }
        }

        [TestMethod]
        public void IntentCodec_RejectsNonCanonicalAndUnknownFields()
        {
            var canonical = ColdRestoreIntentCodec.Serialize(CreateIntent());
            var text = Encoding.UTF8.GetString(canonical);
            Assert.ThrowsExactly<InvalidDataException>(
                () => ColdRestoreIntentCodec.Deserialize(
                    Encoding.UTF8.GetBytes(" " + text)));
            Assert.ThrowsExactly<InvalidDataException>(
                () => ColdRestoreIntentCodec.Deserialize(
                    Encoding.UTF8.GetBytes(
                        text.Substring(0, text.Length - 1)
                        + ",\"unknown\":\"field\"}")));
        }

        [TestMethod]
        public void IntentHash_ChangesWhenBoundTargetChanges()
        {
            var original = CreateIntent(targetMovieTick: 250);
            var changed = CreateIntent(targetMovieTick: 251);

            Assert.AreNotEqual(
                ColdRestoreIntentCodec.ComputeSha256(original),
                ColdRestoreIntentCodec.ComputeSha256(changed));
        }

        [TestMethod]
        public void TargetSemanticBinding_IsExactOnlyForSavedRestore()
        {
            var branch = CreateIntent();
            Assert.IsFalse(branch.RequiresExactTargetSemantic);
            Assert.AreEqual(string.Empty, branch.TargetSemanticSha256);

            Assert.ThrowsExactly<ArgumentException>(
                () => CreateIntent(targetSemanticSha256: Hash('6')));
            var saved = CreateIntent(
                operationKind:
                    ColdRestoreOperationKind.RestoreReplaySave,
                targetSemanticSha256: Hash('6'));
            Assert.IsTrue(saved.RequiresExactTargetSemantic);
            Assert.AreEqual(Hash('6'), saved.TargetSemanticSha256);
        }

        [TestMethod]
        public void ClaimValidation_FailsClosedForTimeAndBuildMismatch()
        {
            var intent = CreateIntent();
            var beforeCreation = ColdRestoreIntentValidator.ValidateForClaim(
                intent,
                intent.BuildFingerprint,
                intent.CreatedAtUtc.AddTicks(-1));
            Assert.AreEqual(
                ColdRestoreIntentValidationCode.NotYetValid,
                beforeCreation.Code);

            var expired = ColdRestoreIntentValidator.ValidateForClaim(
                intent,
                intent.BuildFingerprint,
                intent.ExpiresAtUtc);
            Assert.AreEqual(
                ColdRestoreIntentValidationCode.Expired,
                expired.Code);

            var mismatch = ColdRestoreIntentValidator.ValidateForClaim(
                intent,
                CreateBuildFingerprint('b'),
                intent.CreatedAtUtc);
            Assert.AreEqual(
                ColdRestoreIntentValidationCode.Ready,
                mismatch.Code);
            Assert.IsTrue(mismatch.Success, mismatch.Detail);

            var ready = ColdRestoreIntentValidator.ValidateForClaim(
                intent,
                intent.BuildFingerprint,
                intent.CreatedAtUtc);
            Assert.IsTrue(ready.Success, ready.Detail);
        }

        [TestMethod]
        public void StateMachine_OnlyAllowsDeclaredForwardPathAndTerminalFailure()
        {
            var path = new[]
            {
                ColdRestoreOperationState.Prepared,
                ColdRestoreOperationState.SourceQuiesced,
                ColdRestoreOperationState.SourceExited,
                ColdRestoreOperationState.Launching,
                ColdRestoreOperationState.NewSessionAttached,
                ColdRestoreOperationState.IntentClaimed,
                ColdRestoreOperationState.BaselineReady,
                ColdRestoreOperationState.ReplayingPrefix,
                ColdRestoreOperationState.PausedAtTarget,
                ColdRestoreOperationState.Completed
            };

            for (var index = 1; index < path.Length; index++)
            {
                Assert.IsTrue(
                    ColdRestoreOperationStateMachine.CanTransition(
                        path[index - 1],
                        path[index]));
            }

            Assert.IsFalse(
                ColdRestoreOperationStateMachine.CanTransition(
                    ColdRestoreOperationState.ReplayingPrefix,
                    ColdRestoreOperationState.BaselineReady));
            Assert.IsFalse(
                ColdRestoreOperationStateMachine.CanTransition(
                    ColdRestoreOperationState.Completed,
                    ColdRestoreOperationState.Prepared));
            Assert.IsTrue(
                ColdRestoreOperationStateMachine.CanTransition(
                    ColdRestoreOperationState.ReplayingPrefix,
                    ColdRestoreOperationState.Failed));
        }

        [TestMethod]
        public void Claim_IsCanonicalAuthenticatedAndBoundToIntentSession()
        {
            var intent = CreateIntent();
            var secret = Enumerable.Range(0, 32)
                .Select(value => (byte)value)
                .ToArray();
            var claim = ColdRestoreClaimAuthenticator.Issue(
                intent,
                "claim-0001",
                "companion-0001",
                intent.CreatedAtUtc,
                intent.CreatedAtUtc.AddMinutes(5),
                secret);
            var bytes = ColdRestoreClaimCodec.Serialize(claim);
            var decoded = ColdRestoreClaimCodec.Deserialize(bytes);
            CollectionAssert.AreEqual(
                bytes,
                ColdRestoreClaimCodec.Serialize(decoded));
            Assert.IsTrue(
                ColdRestoreClaimAuthenticator.Validate(
                    decoded,
                    intent,
                    "companion-0001",
                    intent.CreatedAtUtc,
                    secret).Success);

            var wrongSecret = Enumerable.Repeat((byte)0xff, 32).ToArray();
            Assert.AreEqual(
                ColdRestoreClaimValidationCode.InvalidMac,
                ColdRestoreClaimAuthenticator.Validate(
                    decoded,
                    intent,
                    "companion-0001",
                    intent.CreatedAtUtc,
                    wrongSecret).Code);
            Assert.AreEqual(
                ColdRestoreClaimValidationCode.CompanionMismatch,
                ColdRestoreClaimAuthenticator.Validate(
                    decoded,
                    intent,
                    "companion-0002",
                    intent.CreatedAtUtc,
                    secret).Code);
        }

        [TestMethod]
        public void OperationRecords_FormCanonicalHashBoundActorCheckedChain()
        {
            var intent = CreateIntent();
            var intentHash = ColdRestoreIntentCodec.ComputeSha256(intent);
            var genesis = new ColdRestoreOperationRecord(
                ColdRestoreOperationRecord.CurrentSchemaVersion,
                intent.OperationId,
                intentHash,
                "claim-0001",
                0,
                ColdRestoreOperationState.Prepared,
                ColdRestoreOperationRecord.GenesisPreviousSha256,
                intent.CreatedAtUtc,
                ColdRestoreActorRole.Companion,
                "companion-0001",
                string.Empty,
                5000,
                intent.CreatedAtUtc.AddMinutes(-10),
                "prepared");
            var next = genesis.CreateNext(
                ColdRestoreOperationState.SourceQuiesced,
                intent.CreatedAtUtc.AddSeconds(1),
                ColdRestoreActorRole.Runtime,
                "runtime-0001",
                intent.SourceSessionId,
                intent.SourceProcessId,
                intent.SourceProcessStartedAtUtc,
                "source-quiesced");

            Assert.AreEqual(1L, next.Sequence);
            Assert.AreEqual(
                ColdRestoreOperationRecordCodec.ComputeSha256(genesis),
                next.PreviousRecordSha256);
            CollectionAssert.AreEqual(
                ColdRestoreOperationRecordCodec.Serialize(next),
                ColdRestoreOperationRecordCodec.Serialize(
                    ColdRestoreOperationRecordCodec.Deserialize(
                        ColdRestoreOperationRecordCodec.Serialize(next))));
            Assert.ThrowsExactly<InvalidOperationException>(
                () => genesis.CreateNext(
                    ColdRestoreOperationState.SourceQuiesced,
                    intent.CreatedAtUtc.AddSeconds(1),
                    ColdRestoreActorRole.Companion,
                    "companion-0001",
                    string.Empty,
                    5000,
                    intent.CreatedAtUtc.AddMinutes(-10),
                    "wrong-actor"));
        }

        [TestMethod]
        public void RestoreProgress_SeparatesFunctionalAndColdEquivalence()
        {
            var functional = new ReplayRestoreProgress(
                new ReplayRestoreHandle("restore-0001"),
                ReplayRestorePhase.Paused,
                ReplaySaveStatus.Ready,
                10,
                10,
                11,
                false,
                1d,
                "functional");
            Assert.AreEqual(
                ReplayRestoreStrategy.FunctionalReplayRestore,
                functional.Strategy);
            Assert.AreEqual(
                ReplayRestoreEquivalenceClass.FunctionalOnly,
                functional.EquivalenceClass);
            Assert.AreEqual(string.Empty, functional.OperationId);

            var cold = new ReplayRestoreProgress(
                new ReplayRestoreHandle("operation-0001"),
                ReplayRestorePhase.BaselineReady,
                ReplaySaveStatus.Restoring,
                -1,
                10,
                -1,
                false,
                0d,
                "cold",
                strategy:
                    ReplayRestoreStrategy.VanillaEquivalentColdReplay,
                equivalenceClass:
                    ReplayRestoreEquivalenceClass.VanillaEquivalent,
                operationId: "operation-0001");
            Assert.AreEqual(
                ReplayRestoreStrategy.VanillaEquivalentColdReplay,
                cold.Strategy);
            Assert.AreEqual(
                ReplayRestoreEquivalenceClass.VanillaEquivalent,
                cold.EquivalenceClass);
            Assert.AreEqual("operation-0001", cold.OperationId);
            Assert.IsFalse(cold.IsTerminal);
        }

        private static ColdRestoreIntent CreateIntent(
            long targetMovieTick = 250,
            ColdRestoreOperationKind operationKind =
                ColdRestoreOperationKind.ApplyBranchAndSeek,
            string targetSemanticSha256 = "",
            int schemaVersion = ColdRestoreIntent.CurrentSchemaVersion,
            bool menuSource = false,
            string lifecyclePlanObjectSha256 = "", bool rootPlan = false, string slotAuthorization = "")
        {
            var created = new DateTimeOffset(
                2026,
                9,
                1,
                8,
                0,
                0,
                TimeSpan.Zero);
            return new ColdRestoreIntent(
                schemaVersion,
                "intent-0001",
                "operation-0001",
                operationKind,
                rootPlan ? string.Empty : "save-0001",
                "session-0001",
                4242,
                created.AddMinutes(-2),
                created,
                created.AddMinutes(15),
                menuSource ? -1 : 900,
                menuSource ? 0 : 3,
                Hash('1'),
                menuSource ? string.Empty : Hash('2'),
                Hash('3'),
                Hash('4'),
                rootPlan ? string.Empty : Hash('5'),
                targetMovieTick,
                targetSemanticSha256,
                "parent-operation-0001",
                "AiSdk",
                CreateBuildFingerprint('a'), lifecyclePlanObjectSha256, slotAuthorization);
        }

        private static ColdRestoreBuildFingerprint CreateBuildFingerprint(
            char start)
        {
            return new ColdRestoreBuildFingerprint(
                Hash(start),
                Hash(Next(start, 1)),
                Hash(Next(start, 2)),
                Hash(Next(start, 3)),
                Hash(Next(start, 4)),
                Hash(Next(start, 5)),
                Hash(Next(start, 6)),
                Hash(Next(start, 7)));
        }

        private static char Next(char value, int offset)
        {
            var digit = value - 'a' + offset;
            return (char)('a' + digit % 6);
        }

        private static string Hash(char value)
        {
            return new string(value, 64);
        }
    }
}
