using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Anonymous copy-record numbers are compiler allocation details. Bind
    // their actual field schema, including nested arrays and generic arguments.
    internal sealed class LoadingObservationTypeIdentity : IDisposable
    {
        private readonly Dictionary<Type, string> identities = new Dictionary<Type, string>();
        private readonly SHA256 hash = SHA256.Create();

        internal string Bind(Type type)
        {
            if (identities.TryGetValue(type, out var existing)) return existing;
            var name = type.FullName ?? type.Name;
            string identity;
            if (type.Assembly == typeof(LoadingObservationTypeIdentity).Assembly
                && type.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                && type.IsDefined(typeof(CompilerGeneratedAttribute), false))
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
                if (fields.Length > 256) throw new InvalidOperationException("P0 copy-record type schema budget exceeded.");
                var schema = new StringBuilder();
                foreach (var field in fields)
                {
                    var valueType = Bind(field.FieldType);
                    schema.Append(field.Name.Length).Append(':').Append(field.Name)
                        .Append(valueType.Length).Append(':').Append(valueType).Append(';');
                }
                identity = "anonymous-copy-fields-v1:" + BitConverter.ToString(hash.ComputeHash(
                    Encoding.UTF8.GetBytes(schema.ToString()))).Replace("-", "").ToLowerInvariant();
            }
            else if (type.IsArray)
            {
                var element = type.GetElementType()!;
                var bound = Bind(element);
                identity = bound == (element.FullName ?? element.Name) ? name
                    : "copy-array-rank=" + type.GetArrayRank() + ":" + bound;
            }
            else if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                var arguments = type.GetGenericArguments();
                var bound = arguments.Select(Bind).ToArray();
                identity = arguments.Select((argument, index) => bound[index] == (argument.FullName ?? argument.Name)).All(value => value)
                    ? name : type.GetGenericTypeDefinition().FullName + "[" + string.Join(";", bound) + "]";
            }
            else identity = name;
            identities.Add(type, identity);
            return identity;
        }

        public void Dispose() { identities.Clear(); hash.Dispose(); }
    }
}
