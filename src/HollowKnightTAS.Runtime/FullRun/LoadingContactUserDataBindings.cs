using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Pinned PhysicsContacts2D/Collision2D identity validation only. Addresses
    // are local lookup keys and never leave this helper. No native writes.
    internal static class LoadingContactUserDataBindings
    {
        internal sealed class ColliderBinding
        {
            internal string Id = "";
            internal int InstanceId;
        }
        internal sealed class Contact
        {
            internal string Binding = "";
            internal byte[] Bytes = Array.Empty<byte>();
            internal long OwnerA, OwnerB, Pointer;
        }
        internal static uint PairHash(int first, int second)
        {
            // Original bfd8f0, all arithmetic is exactly 32 bits.
            unchecked {
                var a = (uint)first * 0x1001u + 0x7ed55d16u;
                var b = (uint)second ^ (a >> 19) ^ a ^ 0xc761c23cu;
                var c = b * 0x21u + 0x165667b1u;
                return (c - 0x2c5d9b94u) ^ (c << 9);
            }
        }
        internal static int HashBytes(byte[] cache)
        {
            if (cache == null || cache.Length != 88) throw new InvalidDataException("Collision cache ABI differs.");
            var mask = U32(cache, 16);
            if ((mask & 31) != 0 || mask > 262143u * 32u) throw new InvalidDataException("Collision hash capacity differs.");
            var capacity = checked((int)(mask / 32 + 1));
            if ((capacity & (capacity - 1)) != 0 || U32(cache, 20) > capacity)
                throw new InvalidDataException("Collision hash mask/count differs.");
            return checked(capacity * 32);
        }
        internal static object Capture(byte[] cache, byte[] hash, long[] pointers, byte[][] objects,
            Contact[] contacts, Func<long, ColliderBinding?> collider, Func<long, string?> rigidbody,
            byte[][][]? manifolds = null, Func<long, string?>? fixture = null)
        {
            var hashBytes = HashBytes(cache);
            if (pointers.Length != objects.Length || pointers.Length > 262144
                || U32(cache, 20) != pointers.Length || U64(cache, 56) != (ulong)pointers.Length
                || U64(cache, 64) > 262144 || (ulong)pointers.Length > U64(cache, 64) >> 1
                || (pointers.Length != 0 && (I64(cache, 8) == 0 || I64(cache, 40) == 0)))
                throw new InvalidDataException("Collision hash/vector counts differ.");
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            var pointeeMissing = new SortedSet<string>(StringComparer.Ordinal);
            var indices = new Dictionary<long, int>();
            var pairs = new HashSet<Tuple<long, long>>();
            var counts = new int[pointers.Length];
            var records = new List<object>();
            var contactBindings = new List<object>();
            var mapped = new bool[pointers.Length];
            var manifoldRecords = new List<object>();
            var contactPointers = new Dictionary<long, Contact>();
            foreach (var contact in contacts) if (contact.Pointer != 0) {
                if (contactPointers.ContainsKey(contact.Pointer)) throw new InvalidDataException("Duplicate original contact pointer.");
                contactPointers.Add(contact.Pointer, contact);
            }
            if (manifolds != null && manifolds.Length != pointers.Length) throw new InvalidDataException("Manifold vector length differs.");
            for (var i = 0; i < pointers.Length; i++)
            {
                if (pointers[i] == 0 || indices.ContainsKey(pointers[i]) || objects[i].Length != 104)
                    throw new InvalidDataException("Collision vector pointer/ABI alias differs.");
                indices.Add(pointers[i], i);
                var value = objects[i]; var a = I64(value, 80); var b = I64(value, 88);
                if (a == 0 || b == 0 || !pairs.Add(Tuple.Create(a, b)) || I64(value, 40) != a || I64(value, 48) != b)
                    throw new InvalidDataException("Collision key/component pair differs.");
                var boundA = collider(a); var boundB = collider(b);
                if (boundA == null || boundB == null) missing.Add("collision-collider-key-unbound");
                else if (boundA.InstanceId > boundB.InstanceId) throw new InvalidDataException("Collision original Collider pair order differs.");
                var receiving = I64(value, 72);
                if (receiving != 0 && receiving != a && receiving != b) throw new InvalidDataException("Collision receiving Collider is foreign.");
                var bodyA = I64(value, 56); var bodyB = I64(value, 64);
                var rigidA = bodyA == 0 ? "null" : rigidbody(bodyA);
                var rigidB = bodyB == 0 ? "null" : rigidbody(bodyB);
                if (rigidA == null || rigidB == null) missing.Add("collision-rigidbody-unbound");
                for (var flag = 96; flag < 100; flag++) if (value[flag] > 1) throw new InvalidDataException("Collision bool ABI differs.");
                var manifoldCount = U64(value, 16); var encodedCapacity = U64(value, 24);
                if (manifoldCount > encodedCapacity >> 1 || encodedCapacity >> 1 > 262144
                    || (manifoldCount != 0 && I64(value, 0) == 0) || I32(value, 36) < 0)
                    throw new InvalidDataException("Collision manifold/count ABI differs.");
                if (manifoldCount != 0 && manifolds == null) pointeeMissing.Add("collision-active-manifold-content-not-covered");
                if (manifolds != null) {
                    if ((ulong)manifolds[i].Length != manifoldCount) throw new InvalidDataException("Manifold count differs.");
                    for (var j = 0; j < manifolds[i].Length; j++) {
                        var m = manifolds[i][j];
                        if (m.Length != 112 || m[0] > 1) throw new InvalidDataException("Manifold ABI differs.");
                        var cp = I64(m, 8); var fa = I64(m, 16); var fb = I64(m, 24);
                        var fixtureA = fixture?.Invoke(fa); var fixtureB = fixture?.Invoke(fb);
                        if (fixtureA == null || fixtureB == null) missing.Add("manifold-fixture-unbound");
                        Contact? live = null;
                        if (cp != 0) {
                            if (!contactPointers.TryGetValue(cp, out live) || I64(live.Bytes, 304) != pointers[i]
                                || I32(live.Bytes, 296) != j || value[97] != 0)
                                throw new InvalidDataException("Manifold contact/userdata/index reverse binding differs.");
                            var forward = live.OwnerA == a && live.OwnerB == b;
                            if (fa != I64(live.Bytes, forward ? 96 : 104) || fb != I64(live.Bytes, forward ? 104 : 96)
                                || I32(m, 32) != I32(live.Bytes, forward ? 112 : 116)
                                || I32(m, 36) != I32(live.Bytes, forward ? 116 : 112))
                                throw new InvalidDataException("Manifold original ordered fixture/child pair differs.");
                        } else pointeeMissing.Add("collision-retained-inactive-manifold-lifecycle-not-covered");
                        var count = I32(m, 44);
                        if (count < 0 || count > 2) throw new InvalidDataException("Manifold active point count differs.");
                        // Preserve all 112 bytes with local pointer slots rebound to stable
                        // targets. Padding/inactive bytes remain separate from initialized fields.
                        var raw = (byte[])m.Clone(); System.Array.Clear(raw, 8, 24);
                        var active = live != null && count != 0;
                        // PostSolve consumes current manifold count, while the
                        // callback producer writes only solver constraint count.
                        // That count can reduce from two to one. Preserve both
                        // slots without claiming their initialization is proved.
                        if (active) pointeeMissing.Add("collision-impulse-producer-initialization-not-qualified");
                        manifoldRecords.Add(new { collisionIndex = i, manifoldIndex = j, contact = live?.Binding,
                            fixtureA, fixtureB, childIndexA = I32(m, 32), childIndexB = I32(m, 36), enabled = m[0] != 0,
                            simulationId = I32(m, 40), pointCount = count, pointFieldsWithinManifoldCount = active,
                            impulseInitializationQualified = false,
                            normalHex = active ? Bits(m, 48, 8) : "",
                            pointsHex = active ? Bits(m, 56, count * 8) : "",
                            relativeVelocityHex = active ? Bits(m, 72, count * 8) : "",
                            separationsHex = active ? Bits(m, 88, count * 4) : "",
                            normalImpulsesHex = active ? Bits(m, 96, count * 4) : "",
                            tangentImpulsesHex = active ? Bits(m, 104, count * 4) : "",
                            pointerReboundRaw112Hex = Bits(raw, 0, 112), paddingHex = Bits(m, 1, 7), completeStateCoverage = false });
                    }
                }
                records.Add(new { vectorIndex = i, colliderA = boundA?.Id, colliderB = boundB?.Id,
                    rigidbodyA = rigidA, rigidbodyB = rigidB, receivingCollider = receiving == 0 ? null : collider(receiving)?.Id,
                    contactState = I32(value, 32), contactCount = I32(value, 36), enabled = value[96] != 0,
                    isTrigger = value[97] != 0, flagForRecreate = value[98] != 0, swappedReferences = value[99] != 0,
                    manifoldCount, manifoldEncodedCapacity = encodedCapacity,
                    completePointeeCoverage = false });
            }
            if (pointers.Length == 0) {
                if (hash.Length != 0) throw new InvalidDataException("Empty collision hash evidence differs.");
            } else {
                if (hash.Length != hashBytes) throw new InvalidDataException("Collision hash table ABI differs.");
                var used = 0; var mask = U32(cache, 16);
                for (var at = 0; at < hash.Length; at += 32) {
                    var code = U32(hash, at); if (code >= uint.MaxValue - 1) continue;
                    used++;
                    var index = I32(hash, at + 24);
                    if (index < 0 || index >= objects.Length || mapped[index]) throw new InvalidDataException("Collision hash index aliases or is foreign.");
                    mapped[index] = true;
                    var a = I64(hash, at + 8); var b = I64(hash, at + 16);
                    if (a != I64(objects[index], 80) || b != I64(objects[index], 88)) throw new InvalidDataException("Collision hash key/vector member differs.");
                    var ca = collider(a); var cb = collider(b);
                    if (ca == null || cb == null) { missing.Add("collision-hash-lookup-key-unbound"); continue; }
                    var expected = PairHash(ca.InstanceId, cb.InstanceId);
                    if (code != (expected & 0xfffffffcu) || Lookup(hash, mask, expected, a, b) != index)
                        throw new InvalidDataException("Collision original hash lookup differs.");
                }
                if (used != pointers.Length || mapped.Any(value => !value)) throw new InvalidDataException("Collision hash/vector membership differs.");
            }
            foreach (var contact in contacts) {
                if (contact.Bytes.Length != 312) throw new InvalidDataException("Contact userdata ABI differs.");
                var target = I64(contact.Bytes, 304);
                if (target == 0) { contactBindings.Add(new { contact = contact.Binding, collisionIndex = -1 }); continue; }
                if (!indices.TryGetValue(target, out var index)) throw new InvalidDataException("Contact userdata is outside the original collision vector.");
                var value = objects[index]; var a = I64(value, 80); var b = I64(value, 88);
                if (contact.OwnerA == 0 || contact.OwnerB == 0) missing.Add("contact-fixture-owner-unbound");
                else if (!((contact.OwnerA == a && contact.OwnerB == b) || (contact.OwnerA == b && contact.OwnerB == a)))
                    throw new InvalidDataException("Contact userdata Collider pair is foreign.");
                counts[index]++;
                if (value[97] == 0) {
                    var at = I32(contact.Bytes, 296);
                    if (manifolds == null) pointeeMissing.Add("contact-userdata-nontrigger-manifold-index-not-covered");
                    else if (at < 0 || at >= manifolds[index].Length || contact.Pointer == 0
                        || I64(manifolds[index][at], 8) != contact.Pointer)
                        throw new InvalidDataException("Contact original manifold forward index differs.");
                }
                else if (I32(contact.Bytes, 296) != -1) throw new InvalidDataException("Trigger contact original no-manifold index differs.");
                contactBindings.Add(new { contact = contact.Binding, collisionIndex = index });
            }
            for (var i = 0; i < counts.Length; i++)
                if (counts[i] != I32(objects[i], 36)) throw new InvalidDataException("Collision contact reference count differs.");
            return new { schema = "pinned-contact-userdata-bindings-v1", identityComplete = missing.Count == 0,
                unsupported = missing.ToArray(), collisions = records.ToArray(), contacts = contactBindings.ToArray(),
                pointeeUnsupported = pointeeMissing.ToArray(), manifolds = manifoldRecords.ToArray(),
                completeUserDataCoverage = false, completeStateCoverage = false,
                unknownRanges = "Collision2D dynamic storage/allocator/padding and active manifold content remain unqualified; original raw groups retained" };
        }
        private static int Lookup(byte[] table, uint mask, uint hash, long a, long b)
        {
            var at = hash & mask; uint step = 32;
            for (var probe = 0; probe < table.Length / 32; probe++) {
                var offset = (int)at; var code = U32(table, offset);
                if (code == (hash & 0xfffffffcu) && I64(table, offset + 8) == a && I64(table, offset + 16) == b)
                    return I32(table, offset + 24);
                if (code == uint.MaxValue) return -1;
                at = unchecked(at + step) & mask; step = unchecked(step + 32);
            }
            throw new InvalidDataException("Collision hash probe capacity exhausted.");
        }
        private static int I32(byte[] value, int at) => BitConverter.ToInt32(value, at);
        private static string Bits(byte[] value, int at, int count) => BitConverter.ToString(value, at, count).Replace("-", "").ToLowerInvariant();
        private static uint U32(byte[] value, int at) => BitConverter.ToUInt32(value, at);
        private static long I64(byte[] value, int at) => BitConverter.ToInt64(value, at);
        private static ulong U64(byte[] value, int at) => BitConverter.ToUInt64(value, at);
    }
}
