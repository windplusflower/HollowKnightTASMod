using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // An explicitly registered List's mutation epoch is a relational identity.
    // Bind the list to epoch zero and every retained enumerator to its signed
    // modular distance from that epoch; valid/invalid and wrap behavior survive.
    // No list content, index, current item or alias is excluded. This reads only.
    internal sealed class LoadingListEpochIdentity<T>
    {
        private static readonly FieldInfo ListVersion = typeof(List<T>).GetField("_version", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("List._version");
        private static readonly FieldInfo EnumeratorList =
            typeof(List<T>.Enumerator).GetField("list", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? typeof(List<T>.Enumerator).GetField("_list", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("List.Enumerator.list");
        private readonly List<T> list;

        internal LoadingListEpochIdentity(List<T> list) { this.list = list; }

        internal string? Field(object owner, FieldInfo field, object? value)
        {
            if (ReferenceEquals(owner, list) && field.DeclaringType == typeof(List<T>) && field.Name == ListVersion.Name)
                return "list-epoch:origin";
            if (owner is List<T>.Enumerator && (field.Name == "version" || field.Name == "_version")
                && ReferenceEquals(EnumeratorList.GetValue(owner), list) && value is int captured)
            {
                var current = (int)ListVersion.GetValue(list)!;
                return "list-epoch:delta=" + unchecked(captured - current).ToString(CultureInfo.InvariantCulture);
            }
            return null;
        }
    }
}
