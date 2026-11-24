using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public static class SystemRandomStateAccessor
    {
        public const int SeedArrayLength = 56;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private sealed class FieldTriple
        {
            public FieldTriple(FieldInfo inext, FieldInfo inextp, FieldInfo seedArray)
            {
                Inext = inext;
                Inextp = inextp;
                SeedArray = seedArray;
            }

            public FieldInfo Inext { get; }
            public FieldInfo Inextp { get; }
            public FieldInfo SeedArray { get; }
        }

        private static readonly object CacheSync = new object();
        private static readonly Dictionary<Type, FieldTriple?> FieldCache = new Dictionary<Type, FieldTriple?>();

        private static readonly FieldInfo? MonoInextField = typeof(Random).GetField("inext", AnyInstance);
        private static readonly FieldInfo? MonoInextpField = typeof(Random).GetField("inextp", AnyInstance);
        private static readonly FieldInfo? MonoSeedArrayField = typeof(Random).GetField("SeedArray", AnyInstance);

        private static readonly FieldInfo? NetCoreImplField = typeof(Random).GetField("_impl", AnyInstance);

        public static bool TryReadState(Random? instance, out int inext, out int inextp, out int[] seedArray)
        {
            inext = 0;
            inextp = 0;
            seedArray = Array.Empty<int>();

            if (instance == null || !TryResolveTarget(instance, out object target, out _, out _, out FieldTriple fields))
            {
                return false;
            }

            object? inextObj = fields.Inext.GetValue(target);
            object? inextpObj = fields.Inextp.GetValue(target);
            int[]? array = fields.SeedArray.GetValue(target) as int[];
            if (!(inextObj is int i) || !(inextpObj is int ip) || array == null || array.Length != SeedArrayLength)
            {
                return false;
            }

            if (i < 0 || i >= SeedArrayLength || ip < 0 || ip >= SeedArrayLength)
            {
                return false;
            }

            inext = i;
            inextp = ip;
            seedArray = (int[])array.Clone();
            return true;
        }

        public static bool TryWriteState(Random? instance, int inext, int inextp, int[]? seedArray)
        {
            if (instance == null
                || seedArray == null
                || seedArray.Length != SeedArrayLength
                || inext < 0 || inext >= SeedArrayLength
                || inextp < 0 || inextp >= SeedArrayLength)
            {
                return false;
            }

            if (!TryResolveTarget(instance, out object target, out object? parentOwner, out FieldInfo? parentField, out FieldTriple fields))
            {
                return false;
            }

            int[]? existingArray = fields.SeedArray.GetValue(target) as int[];
            if (existingArray != null && existingArray.Length == SeedArrayLength)
            {
                Array.Copy(seedArray, 0, existingArray, 0, SeedArrayLength);
            }
            else
            {
                fields.SeedArray.SetValue(target, (int[])seedArray.Clone());
            }

            fields.Inext.SetValue(target, inext);
            fields.Inextp.SetValue(target, inextp);

            // .NET 8 CompatPrng is a value-type field (_prng) inside CompatSeedImpl/CompatDerivedImpl;
            // write the mutated boxed struct back into its owning reference object.
            if (parentOwner != null && parentField != null && parentField.FieldType.IsValueType)
            {
                parentField.SetValue(parentOwner, target);
            }

            return true;
        }

        private static bool TryResolveTarget(
            Random instance,
            out object target,
            out object? parentOwner,
            out FieldInfo? parentField,
            out FieldTriple fields)
        {
            parentOwner = null;
            parentField = null;

            if (MonoInextField != null
                && MonoInextpField != null
                && MonoSeedArrayField != null
                && MonoInextField.FieldType == typeof(int)
                && MonoInextpField.FieldType == typeof(int)
                && MonoSeedArrayField.FieldType == typeof(int[]))
            {
                target = instance;
                fields = new FieldTriple(MonoInextField, MonoInextpField, MonoSeedArrayField);
                return true;
            }

            if (NetCoreImplField != null)
            {
                object? impl = NetCoreImplField.GetValue(instance);
                if (impl != null)
                {
                    object candidate = impl;
                    FieldInfo? prngField = impl.GetType().GetField("_prng", AnyInstance);
                    if (prngField != null)
                    {
                        object? prng = prngField.GetValue(impl);
                        if (prng != null)
                        {
                            candidate = prng;
                            parentOwner = impl;
                            parentField = prngField;
                        }
                    }

                    FieldTriple? resolved = GetOrResolveFields(candidate.GetType());
                    if (resolved != null)
                    {
                        target = candidate;
                        fields = resolved;
                        return true;
                    }
                }
            }

            FieldTriple? direct = GetOrResolveFields(instance.GetType());
            if (direct != null)
            {
                target = instance;
                parentOwner = null;
                parentField = null;
                fields = direct;
                return true;
            }

            target = null!;
            parentOwner = null;
            parentField = null;
            fields = null!;
            return false;
        }

        private static FieldTriple? GetOrResolveFields(Type type)
        {
            lock (CacheSync)
            {
                if (FieldCache.TryGetValue(type, out FieldTriple? cached))
                {
                    return cached;
                }

                FieldTriple? resolved = ResolveFields(type);
                FieldCache[type] = resolved;
                return resolved;
            }
        }

        private static FieldTriple? ResolveFields(Type type)
        {
            FieldInfo? inext = FindIntField(type, "inext", "_inext");
            FieldInfo? inextp = FindIntField(type, "inextp", "_inextp");
            FieldInfo? seedArray = FindIntArrayField(type, "SeedArray", "_seedArray");
            if (inext == null || inextp == null || seedArray == null)
            {
                return null;
            }

            return new FieldTriple(inext, inextp, seedArray);
        }

        private static FieldInfo? FindIntField(Type type, string primaryName, string fallbackName)
        {
            FieldInfo? field = type.GetField(primaryName, AnyInstance) ?? type.GetField(fallbackName, AnyInstance);
            return field != null && field.FieldType == typeof(int) ? field : null;
        }

        private static FieldInfo? FindIntArrayField(Type type, string primaryName, string fallbackName)
        {
            FieldInfo? field = type.GetField(primaryName, AnyInstance) ?? type.GetField(fallbackName, AnyInstance);
            return field != null && field.FieldType == typeof(int[]) ? field : null;
        }
    }

    public sealed class ModRandomInstanceSnapshot
    {
        public ModRandomInstanceSnapshot(string callsiteKey, uint ordinal, int inext, int inextp, int[] seedArray)
        {
            if (string.IsNullOrEmpty(callsiteKey))
            {
                throw new InvalidDataException("Mod random callsiteKey cannot be empty.");
            }
            if (seedArray == null || seedArray.Length != SystemRandomStateAccessor.SeedArrayLength)
            {
                throw new InvalidDataException("Mod random SeedArray must have length 56.");
            }
            if (inext < 0 || inext >= SystemRandomStateAccessor.SeedArrayLength
                || inextp < 0 || inextp >= SystemRandomStateAccessor.SeedArrayLength)
            {
                throw new InvalidDataException("Mod random inext/inextp out of range [0, 55].");
            }

            CallsiteKey = callsiteKey;
            Ordinal = ordinal;
            Inext = inext;
            Inextp = inextp;
            SeedArray = (int[])seedArray.Clone();
        }

        public string CallsiteKey { get; }
        public uint Ordinal { get; }
        public int Inext { get; }
        public int Inextp { get; }
        public int[] SeedArray { get; }
    }

    public sealed class ModRandomStateSnapshot
    {
        private const int BinaryFormatVersion = 1;
        private const int MaxPayloadBytes = 16 * 1024 * 1024;

        public ModRandomStateSnapshot(
            Dictionary<string, uint> ordinals,
            ModRandomInstanceSnapshot[] instances)
        {
            Ordinals = ordinals ?? throw new ArgumentNullException(nameof(ordinals));
            Instances = instances ?? throw new ArgumentNullException(nameof(instances));
        }

        public Dictionary<string, uint> Ordinals { get; }
        public ModRandomInstanceSnapshot[] Instances { get; }

        public byte[] Encode()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
            {
                writer.Write(BinaryFormatVersion);
                writer.Write(Ordinals.Count);
                foreach (KeyValuePair<string, uint> kvp in Ordinals.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (string.IsNullOrEmpty(kvp.Key))
                    {
                        throw new InvalidDataException("Mod random ordinals key cannot be empty.");
                    }
                    writer.Write(kvp.Key);
                    writer.Write(kvp.Value);
                }

                writer.Write(Instances.Length);
                foreach (ModRandomInstanceSnapshot instance in Instances)
                {
                    if (instance == null)
                    {
                        throw new InvalidDataException("Mod random instance snapshot cannot be null.");
                    }
                    writer.Write(instance.CallsiteKey);
                    writer.Write(instance.Ordinal);
                    writer.Write(instance.Inext);
                    writer.Write(instance.Inextp);
                    for (int i = 0; i < SystemRandomStateAccessor.SeedArrayLength; i++)
                    {
                        writer.Write(instance.SeedArray[i]);
                    }
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static ModRandomStateSnapshot Decode(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > MaxPayloadBytes)
            {
                throw new InvalidDataException("Invalid mod random state payload size.");
            }

            try
            {
                using (var stream = new MemoryStream(bytes, writable: false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    int version = reader.ReadInt32();
                    if (version != BinaryFormatVersion)
                    {
                        throw new InvalidDataException($"Unsupported mod random state version: {version}.");
                    }

                    int ordinalCount = reader.ReadInt32();
                    if (ordinalCount < 0 || ordinalCount > 100000)
                    {
                        throw new InvalidDataException($"Invalid mod random ordinal count: {ordinalCount}.");
                    }

                    var ordinals = new Dictionary<string, uint>(ordinalCount, StringComparer.Ordinal);
                    for (int i = 0; i < ordinalCount; i++)
                    {
                        string key = reader.ReadString();
                        if (string.IsNullOrEmpty(key))
                        {
                            throw new InvalidDataException("Mod random ordinals key cannot be empty.");
                        }
                        uint value = reader.ReadUInt32();
                        ordinals[key] = value;
                    }

                    int instanceCount = reader.ReadInt32();
                    if (instanceCount < 0 || instanceCount > 100000)
                    {
                        throw new InvalidDataException($"Invalid mod random instance count: {instanceCount}.");
                    }

                    var instances = new ModRandomInstanceSnapshot[instanceCount];
                    for (int i = 0; i < instanceCount; i++)
                    {
                        string callsiteKey = reader.ReadString();
                        uint ordinal = reader.ReadUInt32();
                        int inext = reader.ReadInt32();
                        int inextp = reader.ReadInt32();
                        var seedArray = new int[SystemRandomStateAccessor.SeedArrayLength];
                        for (int j = 0; j < seedArray.Length; j++)
                        {
                            seedArray[j] = reader.ReadInt32();
                        }
                        instances[i] = new ModRandomInstanceSnapshot(callsiteKey, ordinal, inext, inextp, seedArray);
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Unexpected trailing bytes in mod random state payload.");
                    }

                    return new ModRandomStateSnapshot(ordinals, instances);
                }
            }
            catch (EndOfStreamException ex)
            {
                throw new InvalidDataException("Truncated mod random state payload.", ex);
            }
        }
    }

    public readonly struct ModRandomRestoreResult
    {
        public ModRandomRestoreResult(int ordinalCount, int snapshotInstanceCount, int restoredInstanceCount, int missingInstanceCount)
        {
            OrdinalCount = ordinalCount;
            SnapshotInstanceCount = snapshotInstanceCount;
            RestoredInstanceCount = restoredInstanceCount;
            MissingInstanceCount = missingInstanceCount;
        }

        public int OrdinalCount { get; }
        public int SnapshotInstanceCount { get; }
        public int RestoredInstanceCount { get; }
        public int MissingInstanceCount { get; }
    }

    public sealed class ModRandomTracker : IDisposable
    {
        private sealed class PendingRegistration
        {
            public PendingRegistration(string callsiteKey, uint ordinal)
            {
                CallsiteKey = callsiteKey;
                Ordinal = ordinal;
            }

            public string CallsiteKey { get; }
            public uint Ordinal { get; }
        }

        private sealed class TrackedInstance
        {
            public TrackedInstance(string callsiteKey, uint ordinal, WeakReference<Random> reference)
            {
                CallsiteKey = callsiteKey;
                Ordinal = ordinal;
                Reference = reference;
            }

            public string CallsiteKey { get; }
            public uint Ordinal { get; }
            public WeakReference<Random> Reference { get; }
        }

        private readonly struct InstanceKey : IEquatable<InstanceKey>
        {
            public InstanceKey(string callsiteKey, uint ordinal)
            {
                CallsiteKey = callsiteKey;
                Ordinal = ordinal;
            }

            public string CallsiteKey { get; }
            public uint Ordinal { get; }

            public bool Equals(InstanceKey other)
                => Ordinal == other.Ordinal && string.Equals(CallsiteKey, other.CallsiteKey, StringComparison.Ordinal);

            public override bool Equals(object? obj)
                => obj is InstanceKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((CallsiteKey?.GetHashCode() ?? 0) * 397) ^ (int)Ordinal;
                }
            }
        }

        private readonly object sync = new object();
        private readonly Dictionary<string, uint> ordinals = new Dictionary<string, uint>(StringComparer.Ordinal);
        private readonly List<TrackedInstance> tracked = new List<TrackedInstance>();
        private readonly ThreadLocal<PendingRegistration?> pendingRegistration = new ThreadLocal<PendingRegistration?>();
        private readonly int gameThreadId;

        public ModRandomTracker(int gameThreadId)
        {
            this.gameThreadId = gameThreadId;
        }

        public static int ComputeDeterministicSeed(string callsiteKey, uint ordinal)
        {
            if (callsiteKey == null)
            {
                throw new ArgumentNullException(nameof(callsiteKey));
            }

            unchecked
            {
                uint hash = 2166136261;
                foreach (var character in callsiteKey)
                {
                    hash = (hash ^ character) * 16777619;
                }
                hash = (hash ^ ordinal) * 16777619;
                return (int)(hash & 0x7fffffff);
            }
        }

        public int NextSeed(
            Func<bool> isBackgroundAllowed,
            Action<string> logWarning,
            params Assembly[] excludedAssemblies)
        {
            foreach (var frame in new StackTrace(false).GetFrames() ?? Array.Empty<StackFrame>())
            {
                var method = frame.GetMethod();
                var assembly = method?.DeclaringType?.Assembly;
                if (assembly == null
                    || assembly == typeof(ModRandomTracker).Assembly
                    || IsExcludedAssembly(assembly, excludedAssemblies)
                    || assembly.IsDynamic)
                {
                    continue;
                }

                string location;
                try
                {
                    location = assembly.Location;
                }
                catch
                {
                    continue;
                }

                if (location.IndexOf(Path.DirectorySeparatorChar + "Mods" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (Thread.CurrentThread.ManagedThreadId != gameThreadId)
                {
                    if (isBackgroundAllowed())
                    {
                        logWarning(
                            "[HKTAS] Background thread called new System.Random() during mod startup/preload ("
                            + method + "); falling back to Environment.TickCount.");
                        return Environment.TickCount;
                    }
                    throw new InvalidOperationException("TAS cannot order background mod random constructors: " + method);
                }

                var key = assembly.GetName().Name + ":" + method!.DeclaringType!.FullName
                    + ":" + method + ":" + frame.GetILOffset();
                return PrepareMainThreadSeed(key);
            }

            return Environment.TickCount;
        }

        public int PrepareMainThreadSeed(string callsiteKey)
        {
            if (string.IsNullOrEmpty(callsiteKey))
            {
                throw new ArgumentNullException(nameof(callsiteKey));
            }

            lock (sync)
            {
                ordinals.TryGetValue(callsiteKey, out var ordinal);
                ordinals[callsiteKey] = checked(ordinal + 1);
                pendingRegistration.Value = new PendingRegistration(callsiteKey, ordinal);
                return ComputeDeterministicSeed(callsiteKey, ordinal);
            }
        }

        public void RegisterConstructedInstance(Random? instance)
        {
            PendingRegistration? pending = pendingRegistration.Value;
            pendingRegistration.Value = null;
            if (pending == null || instance == null || Thread.CurrentThread.ManagedThreadId != gameThreadId)
            {
                return;
            }

            lock (sync)
            {
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedInstance existing = tracked[i];
                    if (!existing.Reference.TryGetTarget(out _)
                        || (existing.Ordinal == pending.Ordinal
                            && string.Equals(existing.CallsiteKey, pending.CallsiteKey, StringComparison.Ordinal)))
                    {
                        tracked.RemoveAt(i);
                    }
                }

                tracked.Add(new TrackedInstance(pending.CallsiteKey, pending.Ordinal, new WeakReference<Random>(instance)));
            }
        }

        public ModRandomStateSnapshot? CaptureState(Action<string>? logWarning = null)
        {
            lock (sync)
            {
                PruneDeadEntriesLocked();
                if (ordinals.Count == 0 && tracked.Count == 0)
                {
                    return null;
                }

                var instances = new List<ModRandomInstanceSnapshot>(tracked.Count);
                foreach (TrackedInstance entry in tracked)
                {
                    if (!entry.Reference.TryGetTarget(out Random? target) || target == null)
                    {
                        continue;
                    }

                    if (!SystemRandomStateAccessor.TryReadState(target, out int inext, out int inextp, out int[] seedArray))
                    {
                        logWarning?.Invoke(
                            $"[HKTAS] Unable to read System.Random internal state for '{entry.CallsiteKey}' ordinal={entry.Ordinal}.");
                        continue;
                    }

                    instances.Add(new ModRandomInstanceSnapshot(entry.CallsiteKey, entry.Ordinal, inext, inextp, seedArray));
                }

                if (ordinals.Count == 0 && instances.Count == 0)
                {
                    return null;
                }

                return new ModRandomStateSnapshot(
                    new Dictionary<string, uint>(ordinals, StringComparer.Ordinal),
                    instances.ToArray());
            }
        }

        public ModRandomRestoreResult RestoreState(ModRandomStateSnapshot snapshot, Action<string>? logDiagnostic = null)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            lock (sync)
            {
                ordinals.Clear();
                foreach (KeyValuePair<string, uint> entry in snapshot.Ordinals)
                {
                    ordinals[entry.Key] = entry.Value;
                }

                var aliveByKey = new Dictionary<InstanceKey, Random>(tracked.Count);
                for (int i = tracked.Count - 1; i >= 0; i--)
                {
                    TrackedInstance entry = tracked[i];
                    if (!entry.Reference.TryGetTarget(out Random? target) || target == null)
                    {
                        tracked.RemoveAt(i);
                        continue;
                    }

                    aliveByKey[new InstanceKey(entry.CallsiteKey, entry.Ordinal)] = target;
                }

                int restoredCount = 0;
                int missingCount = 0;
                foreach (ModRandomInstanceSnapshot inst in snapshot.Instances)
                {
                    var key = new InstanceKey(inst.CallsiteKey, inst.Ordinal);
                    if (!aliveByKey.TryGetValue(key, out Random? target))
                    {
                        missingCount++;
                        logDiagnostic?.Invoke(
                            $"[HKTAS] Mod System.Random instance not found at S0 restore (skipping): key='{inst.CallsiteKey}', ordinal={inst.Ordinal}.");
                        continue;
                    }

                    if (!SystemRandomStateAccessor.TryWriteState(target, inst.Inext, inst.Inextp, inst.SeedArray))
                    {
                        missingCount++;
                        logDiagnostic?.Invoke(
                            $"[HKTAS] Failed to write System.Random internal state at S0 restore: key='{inst.CallsiteKey}', ordinal={inst.Ordinal}.");
                        continue;
                    }

                    restoredCount++;
                }

                return new ModRandomRestoreResult(ordinals.Count, snapshot.Instances.Length, restoredCount, missingCount);
            }
        }

        public Dictionary<string, uint> SnapshotOrdinals()
        {
            lock (sync)
            {
                return new Dictionary<string, uint>(ordinals, StringComparer.Ordinal);
            }
        }

        public void RestoreOrdinals(IDictionary<string, uint> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            lock (sync)
            {
                ordinals.Clear();
                foreach (KeyValuePair<string, uint> entry in source)
                {
                    ordinals[entry.Key] = entry.Value;
                }
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                ordinals.Clear();
                tracked.Clear();
                pendingRegistration.Value = null;
            }
        }

        public void Dispose()
        {
            Clear();
            pendingRegistration.Dispose();
        }

        private void PruneDeadEntriesLocked()
        {
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                if (!tracked[i].Reference.TryGetTarget(out _))
                {
                    tracked.RemoveAt(i);
                }
            }
        }

        private static bool IsExcludedAssembly(Assembly assembly, Assembly[] excludedAssemblies)
        {
            string? name = assembly.GetName().Name;
            if (string.Equals(name, "HollowKnightTAS", StringComparison.Ordinal)
                || string.Equals(name, "HollowKnightTAS.ClockPayload", StringComparison.Ordinal)
                || string.Equals(name, "HollowKnightTAS.Core", StringComparison.Ordinal))
            {
                return true;
            }

            for (int i = 0; i < excludedAssemblies.Length; i++)
            {
                if (assembly == excludedAssemblies[i])
                {
                    return true;
                }
            }

            return false;
        }
    }
}
