using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Diagnostic ownership, not a restore registry. All inputs are already-read
    // scalar identities; this catalog never reads a game object or invents D/S0.
    internal sealed class CanonicalCoverageCatalog
    {
        internal const int MaximumRecords = 65536, MaximumObjects = 50000;
        internal const int MaximumText = 16384, MaximumCharacters = 4 * 1024 * 1024;
        internal enum Domain { Unclassified, D, S0 }
        private readonly int recordLimit;
        private readonly Dictionary<string, Ownership> ownership = new Dictionary<string, Ownership>(StringComparer.Ordinal);
        private readonly Dictionary<string, ObjectData> objects = new Dictionary<string, ObjectData>(StringComparer.Ordinal);
        private readonly SortedDictionary<string, RecordData> records = new SortedDictionary<string, RecordData>(StringComparer.Ordinal);
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> observedLabels = new HashSet<string>(StringComparer.Ordinal);
        private int characters;

        internal CanonicalCoverageCatalog(int recordLimit = MaximumRecords)
        {
            if (recordLimit < 1 || recordLimit > MaximumRecords) throw new ArgumentOutOfRangeException(nameof(recordLimit));
            this.recordLimit = recordLimit;
        }

        internal void BeginSample()
        { ownership.Clear(); objects.Clear(); records.Clear(); names.Clear(); observedLabels.Clear(); characters = 0; }

        internal void BindRoot(string root, Domain domain, string evidence)
        {
            Check(root); Check(evidence);
            if (domain != Domain.D && domain != Domain.S0) throw new InvalidDataException("Coverage ownership must have an explicit D/S0 declaration.");
            if (ownership.TryGetValue(root, out var prior))
            {
                if (prior.Domain != domain || prior.Evidence != evidence) throw new InvalidDataException("Coverage root ownership changed: " + root);
                return;
            }
            if (ownership.Count >= MaximumRecords) throw new InvalidDataException("Coverage root declaration budget exceeded.");
            AddNames(root, evidence);
            ownership.Add(root, new Ownership(domain, evidence));
        }

        internal void InheritRoot(string child, string parent)
        {
            Check(child); Check(parent);
            if (ownership.TryGetValue(parent, out var declared)) BindRoot(child, declared.Domain, declared.Evidence);
        }

        internal void RegisterObject(string id, string type, string kind, int rawInstanceId,
            string sourceKind = "", string sourcePath = "", long localIdentifier = 0)
        {
            foreach (var value in new[] { id, type, kind }) Check(value);
            Check(sourceKind, true); Check(sourcePath, true);
            var entry = new ObjectData(id, type, kind, rawInstanceId, sourceKind, sourcePath, localIdentifier);
            if (objects.TryGetValue(id, out var prior))
            {
                if (!prior.Same(entry)) throw new InvalidDataException("Coverage object identity has conflicting metadata: " + id);
                return;
            }
            if (objects.Count >= MaximumObjects) throw new InvalidDataException("Coverage object budget exceeded.");
            AddNames(entry.Id, entry.Type, entry.Kind, entry.SourceKind, entry.SourcePath);
            objects.Add(id, entry);
        }

        internal void Record(string label, string root, string field, string? objectId = null)
        {
            Check(label); Check(root); Check(field);
            if (objectId != null && !objects.ContainsKey(objectId)) throw new InvalidDataException("Coverage occurrence has no observed object binding.");
            var declared = ownership.TryGetValue(root, out var bound) ? bound : new Ownership(Domain.Unclassified, "no-registered-canon-or-carry-domain");
            // NUL is prohibited in each input; separators retain unambiguous paths.
            var key = label + "\0" + root + "\0" + field + "\0" + (objectId ?? "");
            if (records.ContainsKey(key)) return;
            if (records.Count >= recordLimit) throw new InvalidDataException("Coverage occurrence budget exceeded.");
            AddNames(label, root, field, declared.Evidence);
            records.Add(key, new RecordData(label, root, field, objectId, declared.Domain, declared.Evidence));
            observedLabels.Add(label);
        }

        internal Snapshot Capture(IEnumerable<string> originalUnsupported)
        {
            // Existing observer omissions are cumulative. Never quietly lose a
            // prior label or pretend its current owning field is known.
            foreach (var label in originalUnsupported.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                if (!observedLabels.Contains(label)) Record(label, "<unattributed-observer-gap>", "$no-current-occurrence");
            var strings = names.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var indices = strings.Select((value, index) => new { value, index }).ToDictionary(value => value.value, value => value.index, StringComparer.Ordinal);
            var referenced = new HashSet<string>(records.Values.Where(value => value.ObjectId != null).Select(value => value.ObjectId!), StringComparer.Ordinal);
            var objectEntries = objects.Values.Where(value => referenced.Contains(value.Id)).OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
            var objectIndices = objectEntries.Select((value, index) => new { value.Id, index }).ToDictionary(value => value.Id, value => value.index, StringComparer.Ordinal);
            var result = new Snapshot {
                Names = strings,
                Objects = objectEntries.Select(value => new ObservedObject { Identity = indices[value.Id], Type = indices[value.Type],
                    Kind = indices[value.Kind], RawInstanceId = value.RawInstanceId, SourceKind = indices[value.SourceKind],
                    SourcePath = indices[value.SourcePath], LocalIdentifier = value.LocalIdentifier }).ToArray(),
                Records = records.Values.Select(value => new Occurrence { Label = indices[value.Label], Root = indices[value.Root],
                    Field = indices[value.Field], Object = value.ObjectId == null ? -1 : objectIndices[value.ObjectId],
                    Owner = value.Domain, Evidence = indices[value.Evidence] }).ToArray()
            };
            result.ReadSha256 = ReadHash(result);
            return result;
        }

        private void AddNames(params string[] values)
        {
            var fresh = values.Distinct(StringComparer.Ordinal).Where(value => !names.Contains(value)).ToArray();
            var added = fresh.Sum(value => (long)value.Length);
            if (characters + added > MaximumCharacters) throw new InvalidDataException("Coverage text budget exceeded.");
            foreach (var value in fresh) names.Add(value);
            characters = checked(characters + (int)added);
        }
        private static void Check(string value, bool empty = false)
        {
            if (value == null || (!empty && value.Length == 0) || value.Length > MaximumText || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Coverage identity text is absent, invalid or exceeds its bound.");
        }
        private static string ReadHash(Snapshot value)
        {
            // Includes raw instance diagnostics for same-world reader auditing.
            // This is deliberately not a portable-state qualification hash.
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, new UTF8Encoding(false, true), true))
            {
                writer.Write(value.Schema); writer.Write(value.Names.Length);
                foreach (var name in value.Names) writer.Write(name);
                writer.Write(value.Objects.Length);
                foreach (var item in value.Objects) { writer.Write(item.Identity); writer.Write(item.Type); writer.Write(item.Kind);
                    writer.Write(item.RawInstanceId); writer.Write(item.SourceKind); writer.Write(item.SourcePath); writer.Write(item.LocalIdentifier); }
                writer.Write(value.Records.Length);
                foreach (var item in value.Records) { writer.Write(item.Label); writer.Write(item.Root); writer.Write(item.Field);
                    writer.Write(item.Object); writer.Write((int)item.Owner); writer.Write(item.Evidence); }
            }
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Coverage snapshot exceeds its byte bound.");
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(bytes.ToArray())).Replace("-", "").ToLowerInvariant();
        }

        internal sealed class Snapshot
        {
            public int Schema = 1;
            public string Encoding = "interned-coverage-catalog-v1";
            public bool DiagnosticOnly = true, CompleteStateCoverage = false, RestoreQualified = false;
            public string[] Names = Array.Empty<string>();
            public ObservedObject[] Objects = Array.Empty<ObservedObject>();
            public Occurrence[] Records = Array.Empty<Occurrence>();
            public string ReadSha256 = "";
            internal Snapshot Copy() => new Snapshot { Names = (string[])Names.Clone(), Objects = Objects.Select(value => value.Copy()).ToArray(),
                Records = Records.Select(value => value.Copy()).ToArray(), ReadSha256 = ReadSha256 };
        }
        internal sealed class ObservedObject
        {
            public int Identity, Type, Kind, RawInstanceId, SourceKind, SourcePath;
            public long LocalIdentifier;
            internal ObservedObject Copy() => (ObservedObject)MemberwiseClone();
        }
        internal sealed class Occurrence
        {
            public int Label, Root, Field, Object, Evidence;
            public Domain Owner;
            internal Occurrence Copy() => (Occurrence)MemberwiseClone();
        }
        private sealed class Ownership
        {
            internal Ownership(Domain domain, string evidence) { Domain = domain; Evidence = evidence; }
            internal readonly Domain Domain; internal readonly string Evidence;
        }
        private sealed class ObjectData
        {
            internal ObjectData(string id, string type, string kind, int rawInstanceId, string sourceKind, string sourcePath, long localIdentifier)
            { Id = id; Type = type; Kind = kind; RawInstanceId = rawInstanceId; SourceKind = sourceKind; SourcePath = sourcePath; LocalIdentifier = localIdentifier; }
            internal readonly string Id, Type, Kind, SourceKind, SourcePath;
            internal readonly int RawInstanceId; internal readonly long LocalIdentifier;
            internal bool Same(ObjectData value) => Id == value.Id && Type == value.Type && Kind == value.Kind && RawInstanceId == value.RawInstanceId
                && SourceKind == value.SourceKind && SourcePath == value.SourcePath && LocalIdentifier == value.LocalIdentifier;
        }
        private sealed class RecordData
        {
            internal RecordData(string label, string root, string field, string? objectId, Domain domain, string evidence)
            { Label = label; Root = root; Field = field; ObjectId = objectId; Domain = domain; Evidence = evidence; }
            internal readonly string Label, Root, Field, Evidence; internal readonly string? ObjectId; internal readonly Domain Domain;
        }
    }
}
