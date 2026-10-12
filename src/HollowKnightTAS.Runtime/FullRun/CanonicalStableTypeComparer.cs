using System;
using System.Collections.Generic;

namespace HollowKnightTAS.Runtime.FullRun
{
    // SDK registration uses actual loaded RuntimeType instances. Retain their
    // reference equality while making dictionary buckets independent of the
    // current process's RuntimeType address. No native handle is carried.
    internal sealed class CanonicalStableTypeComparer : IEqualityComparer<Type>
    {
        internal static readonly CanonicalStableTypeComparer Instance = new CanonicalStableTypeComparer();
        public bool Equals(Type? left, Type? right) => ReferenceEquals(left, right);
        public int GetHashCode(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (type.Assembly.IsDynamic || type.FullName == null)
                throw new InvalidOperationException("Canonical SDK type keys require loaded module identities.");
            return HashIdentity(type.Module.ModuleVersionId, type.FullName);
        }
        internal static int HashIdentity(Guid moduleMvid, string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) throw new ArgumentException("Canonical SDK type name is absent.", nameof(fullName));
            var identity = moduleMvid.ToString("D") + "/" + fullName;
            uint hash = 2166136261;
            foreach (var character in identity) hash = unchecked((hash ^ character) * 16777619);
            return unchecked((int)hash);
        }
    }
}
