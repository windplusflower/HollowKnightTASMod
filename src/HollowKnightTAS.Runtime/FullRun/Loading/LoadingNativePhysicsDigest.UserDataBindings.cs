using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingNativePhysicsDigest
    {
        private object CaptureContactUserDataBindings(IntPtr cachePointer, byte[] cache,
            List<KeyValuePair<IntPtr, byte[]>> contacts)
        {
            var vector = Array(cache, 40, 8, "collision-vector-identity");
            var pointers = new long[vector.Length / 8]; var values = new byte[pointers.Length][];
            var manifolds = new byte[pointers.Length][][];
            var manifoldBytes = new byte[pointers.Length][];
            long totalManifolds = 0;
            for (var i = 0; i < pointers.Length; i++) {
                pointers[i] = BitConverter.ToInt64(vector, i * 8);
                if (pointers[i] == 0) throw new InvalidOperationException("Collision vector identity has a null member.");
                values[i] = Read(new IntPtr(pointers[i]), 104);
                var count = BitConverter.ToUInt64(values[i], 16);
                var capacity = BitConverter.ToUInt64(values[i], 24) >> 1;
                if (count > capacity || capacity > 131072 || (count != 0 && Pointer(values[i], 0) == IntPtr.Zero)
                    || count > 4096 || (totalManifolds += (long)count) > 4096)
                    throw new InvalidOperationException("Collision manifold observation exceeds its fixed bound.");
                manifoldBytes[i] = Array(values[i], 0, 112, "collision-manifolds");
                manifolds[i] = Enumerable.Range(0, (int)count)
                    .Select(at => manifoldBytes[i].Skip(at * 112).Take(112).ToArray()).ToArray();
            }
            var hash = pointers.Length == 0 ? System.Array.Empty<byte>() : Read(Pointer(cache, 8), LoadingContactUserDataBindings.HashBytes(cache));
            var inputs = contacts.Select(contact => {
                fixtureOwners.TryGetValue(Pointer(contact.Value, 96), out var a);
                fixtureOwners.TryGetValue(Pointer(contact.Value, 104), out var b);
                return new LoadingContactUserDataBindings.Contact { Binding = contactIds[contact.Key], Bytes = contact.Value,
                    OwnerA = a.ToInt64(), OwnerB = b.ToInt64(), Pointer = contact.Key.ToInt64() };
            }).ToArray();
            var result = LoadingContactUserDataBindings.Capture(cache, hash, pointers, values, inputs,
                pointer => colliderBindings.TryGetValue(new IntPtr(pointer), out var binding) ? binding : null,
                pointer => rigidbodyIds.TryGetValue(new IntPtr(pointer), out var identity) ? identity : null,
                manifolds, pointer => fixtureIds.TryGetValue(new IntPtr(pointer), out var identity) ? identity : null);
            if (!Read(cachePointer, 88).SequenceEqual(cache)
                || !Array(cache, 40, 8, "collision-vector-identity").SequenceEqual(vector)
                || (hash.Length != 0 && !Read(Pointer(cache, 8), hash.Length).SequenceEqual(hash))
                || pointers.Where((pointer, index) => !Read(new IntPtr(pointer), 104).SequenceEqual(values[index])).Any()
                || values.Where((value, index) => !Array(value, 0, 112, "collision-manifolds").SequenceEqual(manifoldBytes[index])).Any()
                || contacts.Any(contact => !Read(contact.Key, 312).SequenceEqual(contact.Value)))
                throw new InvalidOperationException("Contact userdata identity observation became stale.");
            return result;
        }
    }
}
