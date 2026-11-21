using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using TMPro;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    // One original TMP manager, four exact collections, and only three Border owners.
    // Predict through the original List/Dictionary operations on private copies;
    // the actual manager is changed solely by original TMP OnEnable/OnDestroy.
    internal sealed class CanonicalHudBorderQueuePartition : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] QueueNames = { "m_GraphicQueueLookup", "m_GraphicRebuildQueue", "m_LayoutQueueLookup", "m_LayoutRebuildQueue" };
        private static readonly string[] ListFields = { "_items", "_size", "_syncRoot", "_version" };
        private static readonly string[] DictionaryFields = { "_syncRoot", "buckets", "comparer", "count", "entries", "freeCount", "freeList", "keys", "values", "version" };
        private readonly object manager;
        private readonly object[] original, predicted;
        private readonly HashSet<object> internalOwners = new HashSet<object>(new References());
        private readonly List<string> foreign = new List<string>();
        private readonly TextMeshPro[] oldTexts;
        private readonly LoadingFieldDigest fields;
        private object[]? digestQueues;
        private bool consumed;
        private readonly string beforeDigest;
        private readonly Camera.CameraCallback cameraCallbacks;
        private readonly string cameraTargetPath;
        private const string ManagerRoot = "hud-Border3/original-TMP-manager", CameraRoot = "hud-Border3/original-camera-pre-render";
        internal CanonicalHudBorderQueuePartition(TextMeshPro[] oldTexts)
        {
            var corlibMvid = typeof(Dictionary<int, int>).Module.ModuleVersionId;
            if (corlibMvid != new Guid("9577ac7a-62ef-4317-9789-031239ba8798"))
                CanonicalSceneMappingCarry.WarnOnce("HudBorderQueuePartition:CorlibMvid:" + corlibMvid,
                    "[HKTAS] mscorlib ModuleVersionId differs from pinned original (continuing): actual=" + corlibMvid.ToString("D"));
            if (oldTexts.Length != 3 || oldTexts.Any(text => text == null) || oldTexts.Distinct().Count() != 3)
                throw new InvalidDataException("Original Border queue owner directory differs.");
            this.oldTexts = oldTexts;
            var type = typeof(TMP_UpdateManager);
            if (!type.GetFields(Flags).Select(field => field.Name).OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(QueueNames))
                throw new InvalidDataException("Original TMP manager complete queue ABI differs.");
            manager = type.GetField("s_Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)
                ?? throw new InvalidOperationException("Original TMP manager is absent.");
            cameraCallbacks = Camera.onPreRender ?? throw new InvalidOperationException("Original TMP camera registration is absent.");
            var ownCallbacks = cameraCallbacks.GetInvocationList().Where(callback => ReferenceEquals(callback.Target, manager)).ToArray();
            if (ownCallbacks.Length != 1 || ownCallbacks[0].Method.DeclaringType != typeof(TMP_UpdateManager)
                || ownCallbacks[0].Method.Name != "OnCameraPreRender")
                throw new InvalidOperationException("Original TMP manager has a foreign or repeated camera callback.");
            if (ownCallbacks[0].Method.MetadataToken != 0x060025e9)
                CanonicalSceneMappingCarry.WarnOnce("HudBorderQueuePartition:PreRenderToken:" + ownCallbacks[0].Method.MetadataToken,
                    "[HKTAS] TMP_UpdateManager.OnCameraPreRender MetadataToken differs from pinned 0x060025e9 (continuing): actual=0x"
                    + ownCallbacks[0].Method.MetadataToken.ToString("x8"));
            cameraTargetPath = CameraRoot + ".target[" + Array.FindIndex(cameraCallbacks.GetInvocationList(), callback => ReferenceEquals(callback.Target, manager)) + "]";
            original = QueueNames.Select(name => type.GetField(name, Flags)!.GetValue(manager)).ToArray();
            if (original[0].GetType() != typeof(Dictionary<int, int>) || original[2].GetType() != typeof(Dictionary<int, int>)
                || original[1].GetType() != typeof(List<TMP_Text>) || original[3].GetType() != typeof(List<TMP_Text>))
                throw new InvalidDataException("Original TMP collection concrete types differ.");
            var oldIds = new HashSet<int>(oldTexts.Select(text => text.GetInstanceID()));
            foreach (var queue in original) {
                if (queue is Dictionary<int, int> lookup && lookup.Any(pair => pair.Key != pair.Value || !oldIds.Contains(pair.Key))
                    || queue is List<TMP_Text> list && list.Any(item => !oldTexts.Any(text => ReferenceEquals(item, text))))
                    throw new InvalidOperationException("Unrelated pending TMP work needs its original source continuation.");
            }
            internalOwners.Add(manager);
            var arrays = new Dictionary<object, object>(new References());
            object CopyCollection(object source)
            {
                var layout = CanonicalCarryGraphCodec.Fields(source.GetType());
                var expected = source is IList ? ListFields : DictionaryFields;
                if (!layout.Select(field => field.Name).SequenceEqual(expected))
                    throw new InvalidDataException("Pinned original TMP collection complete field ABI differs.");
                var copy = FormatterServices.GetUninitializedObject(source.GetType());
                internalOwners.Add(source);
                foreach (var field in layout) {
                    var value = field.GetValue(source);
                    if (field.Name == "_syncRoot" && value != null)
                        throw new InvalidOperationException("Original TMP queue has an unsupported monitor cache: " + source.GetType().FullName + "/" + field.Name);
                    if ((field.Name == "keys" || field.Name == "values") && value != null) {
                        internalOwners.Add(value);
                        value = CopyOriginalLazyCache(source, copy, field.Name, value);
                    }
                    if (value is Array array) {
                        if (array.Length > 4096) throw new InvalidDataException("Original TMP queue backing array exceeds its fixed bound.");
                        internalOwners.Add(array);
                        if (!arrays.TryGetValue(array, out var clone)) { clone = array.Length == 0 ? array : array.Clone(); arrays.Add(array, clone); }
                        value = clone;
                    }
                    field.SetValue(copy, value);
                }
                return copy;
            }
            predicted = original.Select(CopyCollection).ToArray();
            fields = new LoadingFieldDigest(value => {
                if (digestQueues != null) for (var index = 0; index < digestQueues.Length; index++)
                    if (ReferenceEquals(value, digestQueues[index])) return "original-TMP-queue[" + index + "]";
                return value is UnityEngine.Object native ? "border-TMP-queue-native:" + native.GetInstanceID() : null;
            }, type => type == typeof(MonoBehaviour) || type == typeof(UnityEngine.Object), sharedFieldReaders: true);
            beforeDigest = Digest(original);
            if (beforeDigest != Digest(predicted)) throw new InvalidOperationException("Original TMP queue private copies lost raw storage or aliases.");
        }
        internal static object CopyOriginalLazyCache(object source, object copy, string kind, object cache)
        {
            if (source.GetType() != typeof(Dictionary<int, int>) || copy.GetType() != source.GetType() || kind != "keys" && kind != "values")
                throw new InvalidOperationException("Foreign TMP collection cache copy input.");
            var type = source.GetType().GetNestedType(kind == "keys" ? "KeyCollection" : "ValueCollection")!.MakeGenericType(typeof(int), typeof(int));
            var fields = CanonicalCarryGraphCodec.Fields(cache.GetType());
            if (cache.GetType() != type || fields.Length != 1 || fields[0].Name != "dictionary" || !ReferenceEquals(fields[0].GetValue(cache), source))
                throw new InvalidOperationException("Original TMP collection lazy cache has a foreign dictionary owner: " + kind);
            var result = Activator.CreateInstance(type, new[] { copy })!;
            if (!ReferenceEquals(fields[0].GetValue(result), copy)) throw new InvalidOperationException("Original TMP cache copy constructor lost its exact dictionary owner.");
            return result;
        }
        internal static object?[][] CompleteQueueStorage(object[] queues)
            => queues.Select(queue => CanonicalCarryGraphCodec.Fields(queue.GetType()).Select(field => field.GetValue(queue)).ToArray()).ToArray();
        internal void ReadFiniteRoots(Action<object, string, bool> capture)
        { capture(manager, ManagerRoot, true); capture(cameraCallbacks, CameraRoot, true); }
        internal void ObserveReference(object value, string path, string root)
        {
            if (!Owns(value) || root == ManagerRoot) return;
            if (ReferenceEquals(value, manager) && root == CameraRoot && path == cameraTargetPath) return;
            if (foreign.Count >= 64) throw new InvalidOperationException("Border TMP queue incoming reference proof exceeds its bound.");
            foreign.Add(root + ":" + path);
        }
        internal bool Owns(object? value) => value != null && internalOwners.Contains(value);
        internal void ObserveSlot(object owner, FieldInfo? field, int index, object? value, string path)
        {
            if (value == null || !Owns(value) || internalOwners.Contains(owner)) return;
            if (ReferenceEquals(value, manager) && field != null && field.IsStatic && field.DeclaringType == typeof(TMP_UpdateManager) && field.Name == "s_Instance") return;
            if (ReferenceEquals(value, manager) && owner is Delegate callback && callback.Method.DeclaringType == typeof(TMP_UpdateManager)
                && callback.Method.Name == "OnCameraPreRender") {
                if (callback.Method.MetadataToken != 9705 + 0x06000000)
                    CanonicalSceneMappingCarry.WarnOnce("HudBorderQueuePartition:SlotPreRenderToken:" + callback.Method.MetadataToken,
                        "[HKTAS] TMP_UpdateManager.OnCameraPreRender MetadataToken differs from pinned 0x060025e9 (continuing): actual=0x"
                        + callback.Method.MetadataToken.ToString("x8"));
                return;
            }
            if (foreign.Count >= 64) throw new InvalidOperationException("Border TMP queue incoming proof exceeds its finite refusal bound.");
            foreign.Add(path);
        }
        internal void RequireIncomingClosed()
        {
            if (foreign.Count != 0) throw new InvalidOperationException("Original TMP queue has an unqualified external consumer: " + string.Join(";", foreign));
        }
        private string Digest(object[] queues)
        {
            // The general world reader intentionally summarizes dictionaries. This
            // guard instead reads every pinned field, full backing arrays and cache
            // back-reference under one shared graph, including unused capacity.
            var prior = digestQueues; digestQueues = queues;
            try {
                var raw = CompleteQueueStorage(queues);
                var result = fields.Capture(raw, "original-TMP-complete-raw-storage", expandRoot: true);
                if (result.Unsupported.Count != 0) throw new InvalidOperationException("Original TMP queue has unread raw storage: " + string.Join(";", result.Unsupported));
                return result.Sha256;
            } finally { digestQueues = prior; }
        }
        internal void RequireBeforeMutation()
        {
            RequireIncomingClosed();
            if (consumed || !ReferenceEquals(Camera.onPreRender, cameraCallbacks) || Digest(original) != beforeDigest)
                throw new InvalidOperationException("Original Border TMP queues changed after their complete incoming snapshot.");
        }
        internal Action ExpectOriginalEnableThenRetirement(TextMeshPro[] produced)
        {
            RequireIncomingClosed();
            if (consumed || produced.Length != 3 || produced.Any(text => text == null) || produced.Distinct().Count() != 3
                || produced.Any(text => oldTexts.Any(old => ReferenceEquals(text, old))))
                throw new InvalidOperationException("Original Border queue producer transition is consumed or aliased.");
            consumed = true;
            var graphics = (List<TMP_Text>)predicted[1]; var graphicsLookup = (Dictionary<int, int>)predicted[0];
            var layout = (List<TMP_Text>)predicted[3]; var layoutLookup = (Dictionary<int, int>)predicted[2];
            foreach (var text in produced) {
                var id = text.GetInstanceID();
                if (graphicsLookup.ContainsKey(id)) throw new InvalidOperationException("Original Border new queue ID was already registered.");
                graphicsLookup[id] = id; graphics.Add(text);
            }
            // Original active retirement calls both OnDisable and OnDestroy.
            foreach (var text in oldTexts) for (var callback = 0; callback < 2; callback++) {
                graphics.Remove(text); graphicsLookup.Remove(text.GetInstanceID());
                layout.Remove(text); layoutLookup.Remove(text.GetInstanceID());
            }
            var expected = Digest(predicted);
            return () => {
                RequireIncomingClosed();
                if (!ReferenceEquals(Camera.onPreRender, cameraCallbacks) || !ReferenceEquals(typeof(TMP_UpdateManager).GetField("s_Instance", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null), manager)
                    || original.Where((queue, index) => !ReferenceEquals(queue, typeof(TMP_UpdateManager).GetField(QueueNames[index], Flags)!.GetValue(manager))).Any()
                    || Digest(original) != expected)
                    throw new InvalidOperationException("Original Border TMP queue transition changed unrelated raw storage, order, versions, capacities or aliases.");
            };
        }
        public void Dispose() => fields.Dispose();
        private sealed class References : IEqualityComparer<object> {
            public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
