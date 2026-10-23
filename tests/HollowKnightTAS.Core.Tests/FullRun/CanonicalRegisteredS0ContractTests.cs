using System.IO;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    public sealed class CanonicalRegisteredS0ContractTests
    {
        private const string ClockPath = "light.HutongGames.PlayMaker.FsmState::<RealStartTime>k__BackingField";
        private static CanonicalS0ObservationSnapshot Snapshot(string clock = "f32:00-00-80-3F")
        {
            var value = new CanonicalS0ObservationSnapshot();
            value.Fields.Add("light.$type", "State");
            value.Fields.Add("light.alias", "ref:1");
            value.Fields.Add("light.value", "i32:7");
            value.Fields.Add(ClockPath, clock);
            value.FieldOwnership.Add("light.value", "S0");
            value.FieldOwnership.Add(ClockPath, "Dependency");
            value.Dependencies = new[] { ClockPath + ":D-clock-dependency-original-bits-retained" };
            value.RootOwnership.Add("light", "S0");
            value.RootOwnership.Add("carry", "D");
            return value;
        }

        [TestMethod]
        public void ClockRequiresOriginalBitsOnlyAfterBinding()
        {
            var source = Snapshot();
            var contract = CanonicalRegisteredS0Contract.Capture(source);
            var after = contract.Prepare(Snapshot("f32:00-00-00-40"));
            Assert.Throws<InvalidDataException>(() => after(Snapshot("f32:00-00-00-40")));
            after(source);
            var negativeZero = Snapshot("f32:00-00-00-80");
            var zeroAfter = CanonicalRegisteredS0Contract.Capture(negativeZero).Prepare(Snapshot("f32:00-00-00-00"));
            Assert.Throws<InvalidDataException>(() => zeroAfter(Snapshot("f32:00-00-00-00")));
        }

        [TestMethod]
        public void EveryNonClockDomainRejectsBeforeMutation()
        {
            var contract = CanonicalRegisteredS0Contract.Capture(Snapshot());
            foreach (var path in new[] { "light.value", "light.alias", "light.$type" }) {
                var target = Snapshot(); target.Fields[path] += "changed";
                Assert.Throws<InvalidDataException>(() => contract.Prepare(target));
            }
            var missing = Snapshot(); missing.Fields.Remove("light.value");
            Assert.Throws<InvalidDataException>(() => contract.Prepare(missing));
            var ownership = Snapshot(); ownership.FieldOwnership["light.value"] = "Dependency";
            Assert.Throws<InvalidDataException>(() => contract.Prepare(ownership));
        }

        [TestMethod]
        public void PreparedExpectationsAreFrozenAndDRootPresenceIsExcluded()
        {
            var contract = CanonicalRegisteredS0Contract.Capture(Snapshot());
            var target = Snapshot(); target.RootOwnership.Add("unrelated-carry", "D");
            var after = contract.Prepare(target);
            contract.CompleteSha256 = new string('0', 64); contract.Clock.Clear();
            after(target);
            target.Fields["light.alias"] = "ref:2";
            Assert.Throws<InvalidDataException>(() => after(target));
        }

        [TestMethod]
        public void UnknownUnreadInvariantAndNonfiniteAreClosed()
        {
            var unknown = Snapshot(); unknown.Unclassified = new[] { "unknown" };
            Assert.Throws<InvalidDataException>(() => CanonicalRegisteredS0Contract.Capture(unknown));
            var unread = Snapshot(); unread.Unread = new[] { "unread" };
            Assert.Throws<InvalidDataException>(() => CanonicalRegisteredS0Contract.Capture(unread));
            var invariant = Snapshot(); invariant.InvariantFailures = new[] { "changed" };
            Assert.Throws<InvalidDataException>(() => CanonicalRegisteredS0Contract.Capture(invariant));
            Assert.Throws<InvalidDataException>(() => CanonicalRegisteredS0Contract.Capture(Snapshot("f32:00-00-C0-7F")));
            var scope = Snapshot(); scope.Scope = "other";
            Assert.Throws<InvalidDataException>(() => CanonicalRegisteredS0Contract.Capture(scope));
        }
    }
}
