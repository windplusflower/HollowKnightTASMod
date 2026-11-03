using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    // MonoBehaviour and ScriptableObject share the engine's 312-byte native
    // implementation. This adapter owns its name and script binding/cache;
    // Component/Object, manager lists and coroutines have separate typed owners.
    internal static class ResidentMonoNativeState
    {
        private static readonly ResidentGraphPolicy Metadata = new ResidentGraphPolicy(_ => null);
        private static readonly int[] Nodes = { 128, 152, 176, 200, 256 };
        internal sealed class Data
        {
            public string Owner = "", Type = "", Name = "", Script = "";
            public int ReferenceVtable;
            public bool Scriptable, GuiState, AudioFilter, CacheMethodsMatch;
            public CacheData? Cache;
            public byte[] ScriptableFlags = Array.Empty<byte>();
            public int ScriptableDisplayMask;
            public int[] ScriptableNodes = Array.Empty<int>();
            public string[] CoverageGaps = Array.Empty<string>();
        }
        internal sealed class CacheData
        {
            public string Class = "";
            public int ReferenceCount, MethodCapacity, ScriptType, ExecutionOrder;
            public string[] Methods = Array.Empty<string>();
            public bool StateMachine, SharedAnimators, SerializationPresent, IdentifierMatches;
        }

        internal static Data Capture(NativeObject owner, Func<object, string?> bind)
            => CaptureMany(new[] { owner }, bind)[0];
        internal static Data[] CaptureMany(IEnumerable<NativeObject> selected, Func<object, string?> bind)
        {
            if (selected == null || bind == null) throw new ArgumentNullException();
            var owners = selected.ToArray();
            if (owners.Length > ResidentObjectDirectory.MaximumComponents) throw Error("owner directory exceeds bounds");
            if (owners.Length == 0) return Array.Empty<Data>();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var metadata = new BatchMetadata();
            var sharedCaches = new Dictionary<IntPtr,(CacheData data, Action verify)>();
            var loaded = new Dictionary<int,string>();
            string Script(int id)
            {
                if (id == 0) return "";
                if (loaded.TryGetValue(id,out var existing)) return existing;
                var ptr = ResidentLoadedAssetIndex.RegisteredPointer(id,out var check);
                if (M.Ptr(ptr) != IntPtr.Add(engine,0x16a2c30))
                    throw Error("script PPtr does not identify a native MonoScript");
                var key = ResidentNativeAssetReference.Bind(ptr,ResidentLoadedAssetIndex.MonoScriptClass,typeof(NativeObject),bind);
                check(); loaded.Add(id,key); return key;
            }
            var result = new List<Data>();
            foreach (var owner in owners) {
                if (!(owner is MonoBehaviour) && !(owner is ScriptableObject)) throw Error("owner does not have the native Mono implementation");
                var pointer = M.Owner(owner); var bytes = M.Read(pointer, 312);
                var scriptable = owner is ScriptableObject;
                if (scriptable != (M.Ptr(bytes, 48) == IntPtr.Zero) || bytes[56] > 1 || bytes[57] > 1) throw Error("native Mono base ABI differs");
                var cachePointer = M.Ptr(bytes, 240);
                CacheData? cache = null;
                if (cachePointer != IntPtr.Zero) {
                    if (!sharedCaches.TryGetValue(cachePointer, out var entry)) {
                        var captured = ReadScriptCache(cachePointer, metadata.Bind, out var verify);
                        sharedCaches.Add(cachePointer, entry = (captured, verify));
                    }
                    cache = entry.data;
                }
                var data = new Data {
                    Owner = bind(owner) ?? throw Error("native Mono owner is unbound"), Type = metadata.Bind(owner.GetType()),
                    Name = M.String(IntPtr.Add(pointer, 72)), Script = Script(M.I32(bytes, 232)), Scriptable = scriptable,
                    ReferenceVtable = checked((int)(M.Ptr(bytes, 224).ToInt64() - engine.ToInt64())),
                    Cache = cache, CacheMethodsMatch = M.Ptr(bytes, 248) == (cachePointer == IntPtr.Zero ? IntPtr.Zero : M.Ptr(IntPtr.Add(cachePointer, 16))),
                    GuiState = M.Ptr(bytes, 280) != IntPtr.Zero, AudioFilter = M.Ptr(bytes, 304) != IntPtr.Zero
                };
                if (cache != null && cache.Class != data.Type) throw Error("native cache class and managed owner type differ");
                if (scriptable) {
                    data.ScriptableFlags = new[] { bytes[56], bytes[57], bytes[292], bytes[293], bytes[294], bytes[295], bytes[296] };
                    data.ScriptableDisplayMask = M.I32(bytes, 288);
                    // The +112 list and every reachable native coroutine are
                    // owned by ResidentSchedulerState, including asset owners.
                    data.ScriptableNodes = Nodes.Select(offset => Detached(bytes, pointer, offset)).ToArray();
                }
                data.CoverageGaps = RequiredGaps(data); ValidateRow(data, metadata.Resolve);
                if (!M.Read(pointer, 312).SequenceEqual(bytes)) throw Error("native Mono changed during capture");
                result.Add(data);
            }
            foreach (var entry in sharedCaches.Values) entry.verify();
            metadata.Verify();
            if (LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress != engine) throw Error("engine changed during Mono capture");
            if (ResidentFailureEvidence.Collecting && owners.Length > 1)
                ResidentFailureEvidence.Mark("Mono native owners=" + owners.Length + ", distinct script caches=" + sharedCaches.Count);
            return result.ToArray();
        }
        private static int Detached(byte[] bytes, IntPtr owner, int offset)
        {
            var previous = M.Ptr(bytes, offset); var next = M.Ptr(bytes, offset + 8);
            if (M.Ptr(bytes, offset + 16) != owner) throw Error("native Mono list node owner differs");
            if (previous == IntPtr.Zero && next == IntPtr.Zero) return 0;
            if (previous == IntPtr.Add(owner, offset) && next == previous) return 1;
            return 2;
        }

        internal static CacheData? CaptureScriptCache(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            var data = ReadScriptCache(pointer, Meta, out var verify);
            verify(); ValidateCache(data); return data;
        }
        private static CacheData ReadScriptCache(IntPtr pointer, Func<object,string> bind, out Action verify)
        {
            var bytes = M.Read(pointer, 80); var klass = M.Ptr(bytes, 8);
            var count = M.U64(bytes, 32); var capacity = M.U64(bytes, 40) >> 1;
            if (count > 4096 || capacity > 4096 || count > capacity || (M.U64(bytes, 40) & 1) != 0 && count != 0)
                throw Error("native script method array shape differs");
            var methodBytes = M.Read(M.Ptr(bytes, 16), checked((int)count * 8));
            var methods = new string[(int)count];
            for (var index = 0; index < methods.Length; index++) {
                var method = ResidentScriptingHandles.ReadMethod(M.Ptr(methodBytes, index * 8)); methods[index] = method == null ? "" : bind(method);
            }
            var type = ResidentScriptingHandles.ReadClass(klass);
            var data = new CacheData { Class = type == null ? "" : bind(type), ReferenceCount = M.I32(bytes, 0),
                MethodCapacity = (int)capacity, Methods = methods, ScriptType = M.I32(bytes, 48), ExecutionOrder = M.I32(bytes, 68),
                StateMachine = bytes[64] != 0, SharedAnimators = bytes[65] != 0, SerializationPresent = M.Ptr(bytes, 72) != IntPtr.Zero,
                IdentifierMatches = M.U64(bytes, 56) == ~unchecked((ulong)klass.ToInt64()) };
            if (bytes[64] > 1 || bytes[65] > 1) throw Error("native script cache flag differs");
            verify = () => {
                if (!M.Read(pointer, 80).SequenceEqual(bytes) || !M.Read(M.Ptr(bytes, 16), methodBytes.Length).SequenceEqual(methodBytes))
                    throw Error("native script cache changed during capture");
            };
            return data;
        }
        internal static void ValidateCache(CacheData value)
            => ValidateCacheFields(value, ResolveMeta);
        private static void ValidateCacheFields(CacheData value, Func<string,object> resolve)
        {
            if (value == null || value.Class == null || value.Class.Length > 16384 || value.ReferenceCount < 1
                || value.Methods == null || value.Methods.Length > 4096 || value.MethodCapacity < value.Methods.Length || value.MethodCapacity > 4096
                || value.ScriptType < -7 || value.ScriptType > 3 || value.Methods.Any(method => method == null || method.Length > 16384))
                throw Error("native script cache manifest differs");
            if (value.Class.Length != 0 && resolve(value.Class) is not Type) throw Error("script cache class is not a type");
            foreach (var method in value.Methods) if (method.Length != 0 && resolve(method) is not MethodInfo) throw Error("script cache method is not a method");
        }
        internal static void RequireCacheEqual(CacheData? expected, CacheData? actual)
        {
            if (expected != null) ValidateCache(expected); if (actual != null) ValidateCache(actual);
            if (Json(expected) != Json(actual)) throw Error("native script cache type/methods/ownership differ");
        }
        internal static string[] RequiredGaps(Data value)
        {
            var gaps = new List<string>();
            if (value.Script == ResidentNativeAssetReference.Missing) gaps.Add("mono/unidentified-native-script");
            if (!value.CacheMethodsMatch) gaps.Add("mono/cached-methods-alias");
            if (value.Cache != null && !value.Cache.IdentifierMatches) gaps.Add("mono/cache-identifier-class-binding");
            if (value.Cache?.SerializationPresent == true) gaps.Add("mono/serialization-cache");
            if (value.GuiState) gaps.Add("mono/gui-state");
            if (value.AudioFilter) gaps.Add("mono/audio-filter");
            if (value.Scriptable && (value.ScriptableNodes.Any(value => value == 2)
                || value.ScriptableFlags.Length == 7 && (value.ScriptableFlags[1] != 0 || value.ScriptableFlags[6] != 0)))
                gaps.Add("scriptable/active-native-registration");
            return gaps.ToArray();
        }
        internal static void Validate(Data value)
            => ValidateRow(value, ResolveMeta);
        internal static void ValidateMany(Data[] rows, bool requireComplete = false)
        {
            var metadata = new BatchMetadata();
            foreach (var row in rows) {
                ValidateRow(row, metadata.Resolve);
                if (requireComplete && row.CoverageGaps.Length != 0)
                    throw Error("native Mono coverage is incomplete: " + string.Join(";", row.CoverageGaps));
            }
            metadata.Verify();
        }
        private static void ValidateRow(Data value, Func<string,object> resolve)
        {
            if (value == null || string.IsNullOrEmpty(value.Owner) || value.Owner.Length > 16384 || value.Type == null || value.Name == null
                || value.Name.IndexOf('\0') >= 0 || new System.Text.UTF8Encoding(false, true).GetByteCount(value.Name) > 16384 || value.Script == null
                || value.Script.Length > 16384 || value.ReferenceVtable <= 0 || value.ScriptableFlags == null || value.ScriptableNodes == null
                || value.CoverageGaps == null || value.ScriptableFlags.Length != (value.Scriptable ? 7 : 0)
                || value.ScriptableFlags.Any(flag => flag > 1) || value.ScriptableNodes.Length != (value.Scriptable ? 5 : 0)
                || value.ScriptableNodes.Any(kind => kind < 0 || kind > 2)) throw Error("native Mono manifest differs");
            var type = resolve(value.Type) as Type;
            if (type == null || (value.Scriptable ? !typeof(ScriptableObject).IsAssignableFrom(type) : !typeof(MonoBehaviour).IsAssignableFrom(type)))
                throw Error("native Mono actual type differs");
            if (value.Cache != null) { ValidateCacheFields(value.Cache, resolve); if (value.Cache.Class != value.Type) throw Error("script cache class binding differs"); }
            if (!value.CoverageGaps.SequenceEqual(RequiredGaps(value))) throw Error("native Mono coverage differs");
        }
        internal static void RequireComplete(Data value)
        { Validate(value); if (RequiredGaps(value).Length != 0) throw Error("native Mono coverage is incomplete: " + string.Join(";", RequiredGaps(value))); }
        internal static Data Copy(Data value) { Validate(value); return JsonConvert.DeserializeObject<Data>(Json(value)) ?? throw Error("native Mono copy failed"); }
        internal static void RequireEqual(Data expected, Data actual)
        { Validate(expected); Validate(actual); if (Json(expected) != Json(actual)) throw Error("native Mono independent readback differs"); }

        internal static void Canon(NativeObject owner, ResidentMutationScope scope, Action admitted)
        {
            RequireOwned(owner, scope); admitted(); var storage = IntPtr.Add(M.Owner(owner), 72); var name = M.String(storage);
            M.WriteString(storage, name, admitted); scope.Require();
        }
        internal static Plan Prepare(Data data, NativeObject owner, ResidentMutationScope scope,
            Func<string, NativeObject> resolve, Func<object, string?> bind, Action admitted) => new Plan(data, owner, scope, resolve, bind, admitted);
        internal sealed class Plan
        {
            private readonly Data source, before;
            private readonly NativeObject owner;
            private readonly ResidentMutationScope scope;
            private readonly Func<object, string?> bind;
            private readonly Action admitted;
            private readonly int scriptId;
            private bool applied;
            internal Plan(Data data, NativeObject owner, ResidentMutationScope scope, Func<string, NativeObject> resolve,
                Func<object, string?> bind, Action admitted)
            {
                source = Copy(data); RequireComplete(source); this.owner = owner; this.scope = scope; this.bind = bind; this.admitted = admitted;
                RequireOwned(owner, scope); admitted(); before = Capture(owner, bind); RequireComplete(before);
                if (source.Owner != before.Owner || source.Type != before.Type || source.ReferenceVtable != before.ReferenceVtable)
                    throw Error("native Mono target binding differs");
                // The native cache is shared by all instances of a real class.
                // Mutating its refcount or method table per component corrupts
                // unrelated owners; require its independent typed dependency.
                RequireCacheEqual(source.Cache, before.Cache);
                if (source.Script.Length != 0) {
                    var script = ResidentNativeAssetReference.Resolve(source.Script,ResidentLoadedAssetIndex.MonoScriptClass,typeof(NativeObject),resolve);
                    if (M.Ptr(script) != IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress,0x16a2c30)
                        || ResidentNativeAssetReference.Bind(script,ResidentLoadedAssetIndex.MonoScriptClass,typeof(NativeObject),bind) != source.Script)
                        throw Error("native MonoScript target binding differs");
                    scriptId = M.I32(M.Read(IntPtr.Add(script,8),4),0);
                }
            }
            internal void Apply()
            {
                if (applied) throw Error("native Mono plan is reused"); RequireOwned(owner, scope); admitted(); RequireEqual(before, Capture(owner, bind));
                try {
                    var pointer = M.Owner(owner); M.WriteString(IntPtr.Add(pointer, 72), source.Name, admitted);
                    M.Write(IntPtr.Add(pointer, 232), BitConverter.GetBytes(scriptId));
                    if (source.Scriptable) {
                        M.Write(IntPtr.Add(pointer, 56), source.ScriptableFlags.Take(2).ToArray());
                        M.Write(IntPtr.Add(pointer, 292), source.ScriptableFlags.Skip(2).ToArray());
                        M.Write(IntPtr.Add(pointer, 288), BitConverter.GetBytes(source.ScriptableDisplayMask));
                        for (var index = 0; index < Nodes.Length; index++) {
                            var node = IntPtr.Add(pointer, Nodes[index]); var link = source.ScriptableNodes[index] == 0 ? IntPtr.Zero : node;
                            M.Write(node, BitConverter.GetBytes(link.ToInt64())); M.Write(IntPtr.Add(node, 8), BitConverter.GetBytes(link.ToInt64()));
                        }
                    }
                    applied = true; Verify();
                } catch (Exception error) { scope.Fault(error); throw; }
            }
            internal void Verify()
            { if (!applied) throw Error("native Mono verification precedes application"); RequireOwned(owner, scope); admitted(); RequireEqual(source, Capture(owner, bind)); }
        }
        private static void RequireOwned(NativeObject owner, ResidentMutationScope scope)
        {
            scope.Require();
            if (owner is MonoBehaviour component ? !scope.Owns(component) : owner is ScriptableObject asset ? !scope.Owns(asset) : true)
                throw Error("native Mono mutation is outside its actual owned directory");
        }
        private static string Meta(object value) => Metadata.BindMetadata(value) ?? throw Error("native metadata is unbound");
        private static object ResolveMeta(string value) => Metadata.TryResolveMetadata(value, out var result) ? result : throw Error("native metadata is invalid");
        // One observation only. Distinct owners still have independent headers
        // and lifecycle data. Only immutable reflection identities are reused.
        private sealed class BatchMetadata
        {
            private readonly Dictionary<object,string> bound = new Dictionary<object,string>();
            private readonly Dictionary<string,object> resolved = new Dictionary<string,object>(StringComparer.Ordinal);
            internal string Bind(object value)
            {
                if (!bound.TryGetValue(value, out var key)) bound.Add(value, key = Meta(value));
                return key;
            }
            internal object Resolve(string key)
            {
                if (!resolved.TryGetValue(key, out var value)) resolved.Add(key, value = ResolveMeta(key));
                return value;
            }
            internal void Verify()
            {
                // Resolve anew to detect a module loaded during the traversal
                // that would make a previously unique binding ambiguous.
                foreach (var pair in resolved)
                    if (!Equals(ResolveMeta(pair.Key), pair.Value)) throw Error("metadata identity changed during Mono capture");
            }
        }
        private static string Json(object? value) => JsonConvert.SerializeObject(value);
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident Mono native state: " + reason);
    }
}
