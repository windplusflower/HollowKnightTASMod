using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using NativeObject = UnityEngine.Object;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentNativeAssetState
    {
        // Runtime MonoScript often has only a UnityEngine.Object managed
        // wrapper. Its actual native vtable identifies this typed 248-byte
        // layout. The cache is a shared actual-class dependency, not a private
        // buffer that can be overwritten once per asset/component.
        internal sealed class MonoScriptState
        {
            public byte[] Script = Array.Empty<byte>(), PropertiesHash = new byte[16];
            public string ClassName = "", Namespace = "", AssemblyName = "";
            public int ExecutionOrder;
            public ResidentMonoNativeState.CacheData? Cache;
            internal MonoScriptState Copy() => new MonoScriptState { Script = (byte[])Script.Clone(), PropertiesHash = (byte[])PropertiesHash.Clone(),
                ClassName = ClassName, Namespace = Namespace, AssemblyName = AssemblyName, ExecutionOrder = ExecutionOrder,
                Cache = Cache == null ? null : new ResidentMonoNativeState.CacheData { Class = Cache.Class, ReferenceCount = Cache.ReferenceCount,
                    MethodCapacity = Cache.MethodCapacity, ScriptType = Cache.ScriptType, ExecutionOrder = Cache.ExecutionOrder,
                    Methods = (string[])Cache.Methods.Clone(), StateMachine = Cache.StateMachine, SharedAnimators = Cache.SharedAnimators,
                    SerializationPresent = Cache.SerializationPresent, IdentifierMatches = Cache.IdentifierMatches } };
        }
        private static bool IsNativeMonoScript(NativeObject owner)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            return ResidentNativeMemory.Ptr(ResidentNativeMemory.Owner(owner)).ToInt64() - engine.BaseAddress.ToInt64() == 0x16a2c30;
        }
        private static MonoScriptState CaptureMonoScript(NativeObject owner)
        {
            RequireAssetVtable(owner, 0x16a2c30); var pointer = ResidentNativeMemory.Owner(owner); var before = Read(pointer, 248);
            var result = new MonoScriptState { Script = ReadRawString(IntPtr.Add(pointer, 56)), ExecutionOrder = BitConverter.ToInt32(before, 96),
                PropertiesHash = before.Skip(104).Take(16).ToArray(), ClassName = ResidentNativeMemory.String(IntPtr.Add(pointer, 128)),
                Namespace = ResidentNativeMemory.String(IntPtr.Add(pointer, 168)), AssemblyName = ResidentNativeMemory.String(IntPtr.Add(pointer, 208)),
                Cache = ResidentMonoNativeState.CaptureScriptCache(Pointer(before, 120)) };
            if (!before.SequenceEqual(Read(pointer, 248))) throw Error("MonoScript native state changed during capture");
            ValidateMonoScript(result); return result;
        }
        internal static void ValidateMonoScript(MonoScriptState state)
        {
            if (state == null || state.Script == null || state.PropertiesHash == null || state.PropertiesHash.Length != 16
                || state.ClassName == null || state.Namespace == null || state.AssemblyName == null) throw Error("MonoScript typed state is absent");
            RequireBytes(state.Script.Length);
            foreach (var value in new[] { state.ClassName, state.Namespace, state.AssemblyName })
                if (value.IndexOf('\0') >= 0 || new UTF8Encoding(false, true).GetByteCount(value) > 16384) throw Error("MonoScript metadata string exceeds bounds");
            if (state.Cache != null) ResidentMonoNativeState.ValidateCache(state.Cache);
        }
        private static string[] MonoScriptGaps(MonoScriptState state)
        {
            ValidateMonoScript(state);
            return new[] { state.Cache?.SerializationPresent == true ? "monoscript/serialization-cache" : "",
                state.Cache != null && !state.Cache.IdentifierMatches ? "monoscript/cache-identifier-class-binding" : "" }.Where(value => value.Length != 0).ToArray();
        }
        internal static void RequireMonoScriptComplete(MonoScriptState state)
        { var gaps = MonoScriptGaps(state); if (gaps.Length != 0) throw Error("MonoScript native coverage is incomplete: " + string.Join(";", gaps)); }
        internal static byte[] EncodeMonoScriptValues(MonoScriptState state)
        {
            ValidateMonoScript(state);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                writer.Write(state.Script.Length); writer.Write(state.Script); writer.Write(state.PropertiesHash); writer.Write(state.ExecutionOrder);
                writer.Write(state.ClassName); writer.Write(state.Namespace); writer.Write(state.AssemblyName); writer.Flush(); return stream.ToArray();
            }
        }
        internal static void RequireMonoScriptEqual(MonoScriptState source, MonoScriptState actual)
        {
            if (!EncodeMonoScriptValues(source).SequenceEqual(EncodeMonoScriptValues(actual))) throw Error("MonoScript independent value readback differs");
            ResidentMonoNativeState.RequireCacheEqual(source.Cache, actual.Cache);
        }
        internal static void ApplyMonoScript(Snapshot source, NativeObject owner, Action admitted)
        {
            admitted();
            if (source == null || source.Kind != "MonoScript" || source.Script == null || !ResidentObjectDirectory.IsLive(owner)
                || owner.GetType() != source.Type.Resolve()) throw Error("MonoScript transfer has another owner or managed wrapper type");
            RequireMonoScriptComplete(source.Script); RequireAssetVtable(owner, 0x16a2c30);
            var before = CaptureMonoScript(owner); RequireMonoScriptComplete(before);
            ResidentMonoNativeState.RequireCacheEqual(source.Script.Cache, before.Cache);
            var pointer = ResidentNativeMemory.Owner(owner); var data = source.Script;
            owner.name = source.Name; owner.hideFlags = (HideFlags)source.HideFlags;
            WriteRawString(IntPtr.Add(pointer, 56), data.Script, admitted);
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 96), BitConverter.GetBytes(data.ExecutionOrder));
            ResidentNativeMemory.Write(IntPtr.Add(pointer, 104), data.PropertiesHash);
            ResidentNativeMemory.WriteString(IntPtr.Add(pointer, 128), data.ClassName, admitted);
            ResidentNativeMemory.WriteString(IntPtr.Add(pointer, 168), data.Namespace, admitted);
            ResidentNativeMemory.WriteString(IntPtr.Add(pointer, 208), data.AssemblyName, admitted);
            admitted(); RequireMonoScriptEqual(data, CaptureMonoScript(owner));
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr CreateScriptFromClass(IntPtr klass);
        internal static NativeObject BindOrCreateRuntimeMonoScript(Snapshot source, Action admitted, ResidentMutationScope scope)
        {
            admitted(); var data = source.Script ?? throw Error("runtime MonoScript source is absent"); RequireMonoScriptComplete(data);
            if (data.Cache == null || data.Cache.Class.Length == 0) throw Error("runtime MonoScript requires an actual-class cache dependency");
            var metadata = new ResidentGraphPolicy(_ => null);
            if (!metadata.TryResolveMetadata(data.Cache.Class, out var descriptor) || descriptor is not Type actualType)
                throw Error("runtime MonoScript class dependency cannot be resolved");
            scope.Require();
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var klass = ResidentScriptingHandles.ClassPointer(actualType);
            var family = ResidentLoadedAssetIndex.Family(0x194b370, out var frozen);
            var candidates = family.Where(member => {
                if (ResidentNativeMemory.Ptr(member.Pointer) != IntPtr.Add(engine, 0x16a2c30)) throw Error("runtime MonoScript native vtable differs");
                var cache = ResidentNativeMemory.Ptr(IntPtr.Add(member.Pointer, 120));
                return cache != IntPtr.Zero && ResidentNativeMemory.Ptr(IntPtr.Add(cache, 8)) == klass
                    && ResidentTargetAssetLookup.IsUnmapped(member.Id);
            }).ToArray();
            frozen();
            if (candidates.Length > 1) throw Error("runtime MonoScript actual-class dependency has multiple native owners");
            if (candidates.Length == 1) {
                var found = ResidentTargetAssetLookup.BindRegistered(candidates[0].Id, candidates[0].Pointer, source.Type.Resolve(), scope);
                var foundCache = CaptureMonoScript(found).Cache;
                if (foundCache == null || foundCache.Class != data.Cache.Class) throw Error("runtime MonoScript bound another actual class");
                return found;
            }
            var address = IntPtr.Add(LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress, 0x8a3ab0);
            using (var hash = SHA256.Create()) if (BitConverter.ToString(hash.ComputeHash(Read(address, 243))).Replace("-", "").ToLowerInvariant()
                != "ffccf84ea70e575528de1f9849ccadf051bc6ce0eff51d6421bf7dc9fe7feb0c") throw Error("native runtime MonoScript construction ABI differs");
            var native = ((CreateScriptFromClass)Marshal.GetDelegateForFunctionPointer(address, typeof(CreateScriptFromClass)))(ResidentScriptingHandles.ClassPointer(actualType));
            if (native == IntPtr.Zero) throw Error("native runtime MonoScript construction returned no owner");
            admitted();
            var id = ResidentNativeMemory.I32(Read(IntPtr.Add(native, 8), 4), 0);
            if (!ResidentTargetAssetLookup.IsUnmapped(id)) throw Error("native runtime MonoScript unexpectedly has serialized provenance");
            var created = ResidentTargetAssetLookup.BindRegistered(id, native, source.Type.Resolve(), scope);
            if (!IsNativeMonoScript(created)) throw Error("native runtime MonoScript type differs");
            var cache = CaptureMonoScript(created).Cache;
            if (cache == null || cache.Class != data.Cache.Class) throw Error("native runtime MonoScript bound another actual class");
            return created;
        }
    }
}
