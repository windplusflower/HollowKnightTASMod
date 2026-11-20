using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Threading;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalBoundaryStaticPolicy
    {
        private static int metadataResetActive;
        internal static MetadataAudit ResetActionMetadata()
        {
            if (Interlocked.CompareExchange(ref metadataResetActive, 1, 0) != 0)
                throw new InvalidOperationException("Canonical action metadata reset reentered.");
            try { return ResetActionMetadataAtBoundary(); }
            finally { Volatile.Write(ref metadataResetActive, 0); }
        }

        private static MetadataAudit ResetActionMetadataAtBoundary()
        {
            if (FsmExecutionStack.StackCount != 0 || PlayMakerFSM.NotMainThread)
                throw new InvalidOperationException("Canonical metadata reset overlaps original FSM execution/deserialization.");
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var fields = typeof(ActionData).GetFields(flags).Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (!fields.Select(field => field.Name).SequenceEqual(new[] {
                "ActionFieldsLookup", "ActionHashCodeLookup", "ActionTypeLookup", "InitFields", "UsedIndices", "resaveActionData" }))
                throw new InvalidOperationException("Canonical ActionData static field registry differs.");
            var definitions = new object[] { new Dictionary<Type, FieldInfo[]>(), new Dictionary<Type, int>(),
                new Dictionary<string, Type>(), new List<FieldInfo>(), new List<int>() };
            if (fields[5].FieldType != typeof(bool)) throw new InvalidOperationException("ActionData resave flag layout differs.");
            var reflection = typeof(ReflectionUtils).GetFields(flags).Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (!reflection.Select(field => field.Name).SequenceEqual(new[] { "assemblyNames", "loadedAssemblies", "typeLookup" })
                || reflection[0].FieldType != typeof(List<string>) || reflection[1].FieldType != typeof(Assembly[]))
                throw new InvalidOperationException("Canonical ReflectionUtils static field registry differs.");
            var owners = fields.Take(5).Select(field => field.GetValue(null)).Concat(new[] { reflection[2].GetValue(null) }).ToArray();
            var empty = definitions.Concat(new object[] { new Dictionary<string, Type>() }).ToArray();
            // Preflight every layout before any write. Preserve the original
            // readonly container identities, including aliases outside D.
            var layouts = owners.Select((owner, index) => ContainerFields(owner, empty[index])).ToArray();
            if (((Dictionary<Type, FieldInfo[]>)owners[0]!).Count > 10000 || ((Dictionary<Type, int>)owners[1]!).Count > 10000
                || ((Dictionary<string, Type>)owners[2]!).Count > 10000 || ((List<FieldInfo>)owners[3]!).Count > 10000
                || ((List<int>)owners[4]!).Count > 10000 || ((Dictionary<string, Type>)owners[5]!).Count > 10000
                || ((Dictionary<Type, FieldInfo[]>)owners[0]!).Any(pair => pair.Value == null || pair.Value.Length > 10000))
                throw new InvalidDataException("Original metadata cache audit exceeds its bound.");
            var oldFields = ((Dictionary<Type, FieldInfo[]>)owners[0]!).Select(pair =>
                new KeyValuePair<Type, FieldInfo[]>(pair.Key, (FieldInfo[])pair.Value.Clone())).ToArray();
            var oldHashes = ((Dictionary<Type, int>)owners[1]!).ToArray();
            var oldTypes = ((Dictionary<string, Type>)owners[2]!).ToArray();
            var oldGlobalTypes = ((Dictionary<string, Type>)owners[5]!).ToArray();
            var hashMethod = typeof(ActionData).GetMethod("GetActionTypeHashCode", flags, null, new[] { typeof(Type) }, null)
                ?? throw new InvalidDataException("Original action metadata hash method is absent.");
            if (hashMethod.ReturnType != typeof(int)) throw new InvalidDataException("Original action hash return type differs.");
            var originalAssemblies = AssemblyIdentities();
            var targets = layouts.SelectMany((layout, index) => layout.Select(field =>
                new CanonicalFieldWriteTransaction.Target(owners[index], field))).Concat(new[] {
                    new CanonicalFieldWriteTransaction.Target(null, fields[5]),
                    new CanonicalFieldWriteTransaction.Target(null, reflection[0]),
                    new CanonicalFieldWriteTransaction.Target(null, reflection[1]) });
            using var transaction = CanonicalFieldWriteTransaction.Begin(targets);
            void EmptyCaches()
            {
                for (var index = 0; index < owners.Length; index++) ResetContainer(owners[index]!, empty[index], layouts[index]);
                fields[5].SetValue(null, false);
                reflection[0].SetValue(null, null); reflection[1].SetValue(null, null);
                if (!Equals(fields[5].GetValue(null), false) || reflection[0].GetValue(null) != null || reflection[1].GetValue(null) != null)
                    throw new InvalidDataException("Canonical metadata scalar readback differs.");
            }
            EmptyCaches();
            // These original metadata readers instantiate no action and run
            // no event/lifecycle method. Reject cached overrides that cannot
            // be reproduced, then clear their audit writes again for S0.
            foreach (var pair in oldFields)
            {
                var rebuilt = ActionData.GetFields(pair.Key);
                if (rebuilt.Length != pair.Value.Length || !rebuilt.Zip(pair.Value, SameField).All(equal => equal))
                    throw new InvalidDataException("Original action fields changed after cache reset: " + pair.Key.FullName);
            }
            foreach (var pair in oldHashes)
                if (!Equals(hashMethod.Invoke(null, new object[] { pair.Key }), pair.Value))
                    throw new InvalidDataException("Original action hash changed after cache reset: " + pair.Key.FullName);
            foreach (var pair in oldTypes)
                if (ActionData.GetActionType(pair.Key) != pair.Value)
                    throw new InvalidDataException("Original action type changed after cache reset: " + pair.Key);
            foreach (var pair in oldGlobalTypes)
                if (ReflectionUtils.GetGlobalType(pair.Key) != pair.Value)
                    throw new InvalidDataException("Original global type changed after cache reset: " + pair.Key);
            if (!originalAssemblies.SequenceEqual(AssemblyIdentities()))
                throw new InvalidDataException("Original metadata recomputation changed the loaded assembly registry.");
            EmptyCaches();
            transaction.Commit();
            return new MetadataAudit { ActionFields = oldFields.Length, ActionHashes = oldHashes.Length,
                ActionTypes = oldTypes.Length, GlobalTypes = oldGlobalTypes.Length, RecomputedEqual = true,
                LoadedAssembliesEqual = true };
        }

        private static string[] AssemblyIdentities()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (assemblies.Length > 2000) throw new InvalidDataException("Loaded metadata assembly registry exceeds its bound.");
            return assemblies.Select(assembly => assembly.FullName + "|" + assembly.ManifestModule.ModuleVersionId.ToString("D"))
                .OrderBy(identity => identity, StringComparer.Ordinal).ToArray();
        }

        private static bool SameField(FieldInfo left, FieldInfo right) => left.DeclaringType == right.DeclaringType
            && left.FieldType == right.FieldType && left.Module == right.Module && left.MetadataToken == right.MetadataToken;

        private static FieldInfo[] ContainerFields(object? owner, object empty)
        {
            if (owner == null || owner.GetType() != empty.GetType())
                throw new InvalidDataException("Original canonical metadata container type differs.");
            var type = owner.GetType();
            var dictionary = type.GetGenericTypeDefinition() == typeof(Dictionary<,>);
            var fields = CanonicalCarryGraphCodec.Fields(type);
            var names = dictionary ? new[] { "_syncRoot", "buckets", "comparer", "count", "entries", "freeCount", "freeList", "keys", "values", "version" }
                : new[] { "_items", "_size", "_syncRoot", "_version" };
            if (!fields.Select(field => field.Name).SequenceEqual(names))
                throw new InvalidDataException("Pinned canonical metadata container field layout differs.");
            return fields;
        }

        private static void ResetContainer(object owner, object empty, FieldInfo[] fields)
        {
            foreach (var field in fields) field.SetValue(owner, field.GetValue(empty));
            foreach (var field in fields)
                if (!Equals(field.GetValue(owner), field.GetValue(empty)))
                    throw new InvalidDataException("Canonical metadata empty container readback differs.");
        }

        internal sealed class MetadataAudit
        {
            public int ActionFields { get; set; }
            public int ActionHashes { get; set; }
            public int ActionTypes { get; set; }
            public int GlobalTypes { get; set; }
            public bool RecomputedEqual { get; set; }
            public bool LoadedAssembliesEqual { get; set; }
        }

        internal static void ResetExecutionStack()
        {
            var fields = typeof(FsmExecutionStack).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Where(field => !field.IsLiteral).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray();
            if (fields.Length != 2 || fields[0].Name != "<MaxStackCount>k__BackingField" || fields[0].FieldType != typeof(int)
                || fields[1].Name != "fsmExecutionStack" || fields[1].FieldType != typeof(Stack<Fsm>)
                || !(fields[1].GetValue(null) is Stack<Fsm> stack) || stack.Count != 0)
                throw new InvalidOperationException("Canonical boundary is inside an FSM execution stack or its closed fields differ.");
            // The original stack is private; only Push/Pop use it. At B no FSM
            // action is executing. Discard its version, unused storage and
            // debug-only maximum depth, without touching any live FSM state.
            fields[1].SetValue(null, new Stack<Fsm>(256));
            fields[0].SetValue(null, 0);
            if (FsmExecutionStack.StackCount != 0 || FsmExecutionStack.MaxStackCount != 0)
                throw new InvalidOperationException("Canonical FSM execution stack readback differs.");
        }
    }
}
