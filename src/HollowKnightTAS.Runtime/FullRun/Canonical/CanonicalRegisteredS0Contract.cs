using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // This is a registered R2/R6 prerequisite, never a whole-world D1 claim.
    internal sealed class CanonicalRegisteredS0Contract
    {
        public int Schema = 1;
        public string Scope = "registered-R2-R6-field-contract-v1";
        public string NonClockSha256 = "", CompleteSha256 = "", OwnershipSha256 = "";
        public SortedDictionary<string, string> Clock = new SortedDictionary<string, string>(StringComparer.Ordinal);

        internal static CanonicalRegisteredS0Contract Capture(CanonicalS0ObservationSnapshot snapshot)
        {
            if (snapshot.Schema != 1 || snapshot.Scope != "registered-R2-R6-field-contract-v1"
                || snapshot.Unclassified.Length != 0 || snapshot.Unread.Length != 0 || snapshot.InvariantFailures.Length != 0)
                throw new InvalidDataException("Registered R2/R6 S0 reader has unresolved fields.");
            if (snapshot.RootOwnership.Any(x => x.Value != "S0" && x.Value != "D" && x.Value != "Dependency"))
                throw new InvalidDataException("Registered S0 root ownership is unknown.");
            var result = new CanonicalRegisteredS0Contract();
            foreach (var entry in snapshot.FieldOwnership) {
                if (entry.Value != "S0" && entry.Value != "Dependency")
                    throw new InvalidDataException("Registered S0 ownership is absent or unknown.");
                if (entry.Value == "Dependency") {
                    if (!snapshot.Fields.ContainsKey(entry.Key)) throw new InvalidDataException("Registered clock field is unread.");
                    if (!(entry.Key.EndsWith(".HutongGames.PlayMaker.FsmState::<RealStartTime>k__BackingField", StringComparison.Ordinal)
                        || entry.Key.EndsWith(".HutongGames.PlayMaker.Actions.Wait::startTime", StringComparison.Ordinal)))
                        throw new InvalidDataException("Unknown registered clock dependency.");
                    result.Clock.Add(entry.Key, snapshot.Fields[entry.Key]);
                }
            }
            result.CompleteSha256 = Hash(snapshot.Fields);
            result.NonClockSha256 = Hash(snapshot.Fields.Where(x => !result.Clock.ContainsKey(x.Key)));
            // D root presence and D values are intentionally outside this S0 contract.
            if (!snapshot.Dependencies.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(
                result.Clock.Keys.Select(x => x + ":D-clock-dependency-original-bits-retained").OrderBy(x => x, StringComparer.Ordinal)))
                throw new InvalidDataException("Registered S0 dependency declarations differ.");
            result.OwnershipSha256 = Hash(snapshot.RootOwnership.Where(x => x.Value != "D")
                .Select(x => new KeyValuePair<string,string>("root:" + x.Key, x.Value))
                .Concat(snapshot.FieldOwnership.Select(x => new KeyValuePair<string,string>("field:" + x.Key, x.Value))));
            result.Validate();
            return result;
        }

        internal void Validate()
        {
            if (Schema != 1 || Scope != "registered-R2-R6-field-contract-v1" || Clock == null || Clock.Count > 128)
                throw new InvalidDataException("Registered S0 contract schema or bounds differ.");
            foreach (var digest in new[] { NonClockSha256, CompleteSha256, OwnershipSha256 })
                if (digest == null || digest.Length != 64 || digest.Any(x => !(x >= '0' && x <= '9' || x >= 'A' && x <= 'F')))
                    throw new InvalidDataException("Registered S0 digest is malformed.");
            foreach (var entry in Clock) {
                var value = entry.Value;
                if (entry.Key.Length > 16384 || value == null || value.Length != 15 || !value.StartsWith("f32:", StringComparison.Ordinal))
                    throw new InvalidDataException("Registered S0 clock representation differs.");
                byte[] bytes;
                try { bytes = value.Substring(4).Split('-').Select(x => Convert.ToByte(x, 16)).ToArray(); }
                catch (Exception error) { throw new InvalidDataException("Registered S0 clock bits differ.", error); }
                if (bytes.Length != 4 || "f32:" + BitConverter.ToString(bytes) != value || float.IsNaN(BitConverter.ToSingle(bytes, 0)) || float.IsInfinity(BitConverter.ToSingle(bytes, 0)))
                    throw new InvalidDataException("Registered S0 clock is nonfinite.");
            }
        }

        internal Action<CanonicalS0ObservationSnapshot> Prepare(CanonicalS0ObservationSnapshot before)
        {
            Validate();
            // Freeze all accepted expectations before any target mutation.
            var nonClock = NonClockSha256; var complete = CompleteSha256; var owners = OwnershipSha256;
            var paths = Clock.Keys.ToArray();
            void Require(CanonicalS0ObservationSnapshot snapshot, bool afterClock)
            {
                var actual = Capture(snapshot);
                if (actual.NonClockSha256 != nonClock || actual.OwnershipSha256 != owners
                    || !actual.Clock.Keys.SequenceEqual(paths) || afterClock && actual.CompleteSha256 != complete)
                    throw new InvalidDataException("Registered R2/R6 S0 source contract differs " + (afterClock ? "after clock binding." : "before D writes."));
            }
            Require(before, false);
            return after => Require(after, true);
        }

        private static string Hash(IEnumerable<KeyValuePair<string,string>> entries)
        {
            using (var sha = SHA256.Create()) {
                foreach (var entry in entries.OrderBy(x => x.Key, StringComparer.Ordinal))
                    foreach (var value in new[] { entry.Key, entry.Value }) {
                        var bytes = Encoding.UTF8.GetBytes(value);
                        var length = BitConverter.GetBytes(bytes.Length);
                        sha.TransformBlock(length, 0, length.Length, length, 0);
                        sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                    }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha.Hash!).Replace("-", "");
            }
        }
    }
}
