using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // ScriptingGCHandle IDs are local Mono resources. Read borrows them; only
    // Apply replaces a Unity-owned handle, using the engine's own ABI.
    internal static class ResidentScriptingHandles
    {
        private const string MonoHash = "211e11bab370bc6acbd5b8f1af4f8a5536cd62df91c2ca8bdc4512e1a2d8834f";
        private static readonly Lazy<Runtime> Api = new Lazy<Runtime>(() => new Runtime());
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr HandleTarget(uint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint NewHandle(IntPtr value, int pinned);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FreeHandle(uint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void AcquireHandle(IntPtr storage, IntPtr value, int weakness);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ReleaseHandle(IntPtr storage);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void WriteBarrier(IntPtr owner, IntPtr storage, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Domain();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MethodObject(IntPtr domain, IntPtr method, IntPtr ownerClass);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr MetadataPointer(IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr TypeObject(IntPtr domain, IntPtr type);

        internal sealed class Capture
        {
            internal readonly object? Target;
            internal readonly int Weakness;
            internal readonly bool Allocated, CachePresent, CacheMatches;
            internal bool Present => Target != null;
            private readonly IntPtr storage;
            private readonly byte[] bytes;
            internal Capture(IntPtr storage, byte[] bytes, object? target, int weakness, bool allocated, bool cacheMatches)
            {
                this.storage = storage; this.bytes = bytes; Target = target; Weakness = weakness; Allocated = allocated;
                CachePresent = M.Ptr(bytes, 16) != IntPtr.Zero; CacheMatches = cacheMatches;
            }
            internal void RequireUnchanged()
            {
                Api.Value.Require();
                if (!M.Read(storage, 24).SequenceEqual(bytes) || Allocated && !ReferenceEquals(Target, Borrow(M.U64(bytes, 0))))
                    throw Error("captured native handle or target changed");
            }
        }

        internal static Capture Read(IntPtr storage)
        {
            var api = Api.Value; api.Require(); var bytes = M.Read(storage, 24); var token = M.U64(bytes, 0); var weakness = M.I32(bytes, 8);
            if (weakness < 0 || weakness > 2 || token != ulong.MaxValue && (token == 0 || token > uint.MaxValue)) throw Error("native handle schema differs");
            var allocated = token != ulong.MaxValue; var target = allocated ? Borrow(token) : null;
            var pointer = allocated ? api.Target((uint)token) : IntPtr.Zero;
            var cacheMatches = weakness == 2 && allocated ? M.Ptr(bytes, 16) == pointer : M.Ptr(bytes, 16) == IntPtr.Zero;
            var result = new Capture(storage, bytes, target, weakness, allocated, cacheMatches); result.RequireUnchanged(); return result;
        }
        private static object? Borrow(ulong token) => GCHandle.FromIntPtr(new IntPtr(unchecked((int)(uint)token))).Target;

        internal static void Apply(IntPtr storage, object? target, int weakness, Action admitted)
        {
            if (admitted == null || weakness < 0 || weakness > 2 || target != null && weakness == 0) throw Error("target handle mode or admission differs");
            admitted(); var api = Api.Value; api.Require(); _ = Read(storage);
            var temporary = target == null ? default(GCHandle) : GCHandle.Alloc(target, GCHandleType.Pinned);
            try {
                var pointer = target == null ? IntPtr.Zero : api.Target(unchecked((uint)GCHandle.ToIntPtr(temporary).ToInt64()));
                api.Release(storage); api.Barrier(IntPtr.Zero, IntPtr.Add(storage, 16), IntPtr.Zero);
                api.Acquire(storage, pointer, weakness);
                Verify(storage, target, weakness); admitted();
            } finally { if (temporary.IsAllocated) temporary.Free(); }
        }
        internal static void Apply(Capture source, IntPtr storage, object? target, Action admitted)
        {
            if (source == null || source.Allocated && !source.Present || !source.CacheMatches || source.Present != (target != null))
                throw Error("source handle requires an explicit collected-target/cache adapter");
            Apply(storage, target, source.Weakness, admitted);
        }
        internal static void Verify(IntPtr storage, object? target, int weakness)
        {
            var actual = Read(storage);
            if (actual.Weakness != weakness || actual.Allocated != (target != null) || !actual.CacheMatches || !ReferenceEquals(actual.Target, target))
                throw Error("target native handle/managed identity did not read back");
        }
        internal static void WriteObjectReference(IntPtr storage, object? target, Action admitted)
        {
            if (admitted == null) throw new ArgumentNullException(nameof(admitted)); admitted(); var api = Api.Value; api.Require();
            var temporary = target == null ? default(GCHandle) : GCHandle.Alloc(target, GCHandleType.Pinned);
            try {
                var pointer = target == null ? IntPtr.Zero : api.Target(unchecked((uint)GCHandle.ToIntPtr(temporary).ToInt64()));
                api.Barrier(IntPtr.Zero, storage, pointer);
                if (M.Ptr(storage) != pointer) throw Error("native scripting reference did not read back");
                admitted();
            } finally { if (temporary.IsAllocated) temporary.Free(); }
        }
        internal static void VerifyObjectReference(IntPtr storage, object? target)
        {
            var api = Api.Value; api.Require();
            var temporary = target == null ? default(GCHandle) : GCHandle.Alloc(target, GCHandleType.Pinned);
            try {
                var pointer = target == null ? IntPtr.Zero : api.Target(unchecked((uint)GCHandle.ToIntPtr(temporary).ToInt64()));
                if (M.Ptr(storage) != pointer) throw Error("native scripting reference/managed identity differs");
            } finally { if (temporary.IsAllocated) temporary.Free(); }
        }
        internal static void ClearInactiveCache(IntPtr storage, Action admitted)
        {
            var before = Read(storage);
            if (before.Allocated) throw Error("cannot clear a live native scripting cache");
            WriteObjectReference(IntPtr.Add(storage, 16), null, admitted); Verify(storage, null, before.Weakness);
        }

        // Reflection is produced by Mono itself and then read through a normal
        // GCHandle; no pointer reinterpretation, constructor or method invocation.
        internal static MethodInfo? ReadMethod(IntPtr method)
        {
            if (method == IntPtr.Zero) return null;
            var api = Api.Value; api.Require(); var reflected = api.MethodObject(api.Domain(), method, IntPtr.Zero);
            if (reflected == IntPtr.Zero) throw Error("Mono method reflection is absent");
            var temporary = api.NewHandle(reflected, 0);
            if (temporary == 0) throw Error("Mono temporary reflection handle is absent");
            try {
                var result = Borrow(temporary) as MethodInfo ?? throw Error("native method is not a managed MethodInfo");
                if (result.MethodHandle.Value != method) throw Error("native method reflection does not preserve its exact method pointer");
                return result;
            } finally { api.Free(temporary); }
        }
        internal static IntPtr MethodPointer(MethodInfo? method)
        {
            if (method == null) return IntPtr.Zero; Api.Value.Require();
            var pointer = method.MethodHandle.Value;
            if (pointer == IntPtr.Zero || !Equals(ReadMethod(pointer), method)) throw Error("managed method does not resolve its exact native method pointer");
            return pointer;
        }
        internal static Type? ReadClass(IntPtr nativeClass)
        {
            if (nativeClass == IntPtr.Zero) return null;
            var api = Api.Value; api.Require(); var nativeType = api.ClassType(nativeClass);
            if (nativeType == IntPtr.Zero || api.TypeClass(nativeType) != nativeClass) throw Error("native class/type identity differs");
            var reflected = api.TypeObject(api.Domain(), nativeType);
            if (reflected == IntPtr.Zero) throw Error("Mono class reflection is absent");
            var temporary = api.NewHandle(reflected, 0);
            if (temporary == 0) throw Error("Mono temporary type handle is absent");
            try {
                var result = Borrow(temporary) as Type ?? throw Error("native class is not a managed Type");
                if (api.TypeClass(api.ReflectionType(reflected)) != nativeClass) throw Error("native class reflection does not preserve its exact identity");
                return result;
            } finally { api.Free(temporary); }
        }
        internal static IntPtr ClassPointer(Type? type)
        {
            if (type == null) return IntPtr.Zero;
            var api = Api.Value; api.Require(); var temporary = GCHandle.Alloc(type, GCHandleType.Pinned);
            try {
                var reflected = api.Target(unchecked((uint)GCHandle.ToIntPtr(temporary).ToInt64()));
                var nativeType = api.ReflectionType(reflected);
                if (nativeType == IntPtr.Zero) throw Error("managed Type has no native Mono type");
                var nativeClass = api.TypeClass(nativeType);
                if (nativeClass == IntPtr.Zero || ReadClass(nativeClass) != type) throw Error("managed Type does not round-trip its native class");
                return nativeClass;
            } finally { temporary.Free(); }
        }

        private sealed class Runtime
        {
            private readonly IntPtr mono, engine;
            internal readonly HandleTarget Target;
            internal readonly AcquireHandle Acquire;
            internal readonly ReleaseHandle Release;
            internal readonly WriteBarrier Barrier;
            internal readonly NewHandle NewHandle;
            internal readonly FreeHandle Free;
            internal readonly Domain Domain;
            internal readonly MethodObject MethodObject;
            internal readonly MetadataPointer ClassType, TypeClass, ReflectionType;
            internal readonly TypeObject TypeObject;
            internal Runtime()
            {
                if (IntPtr.Size != 8 || Type.GetType("Mono.Runtime") == null) throw Error("requires pinned 64-bit Mono");
                engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(value => string.Equals(value.ModuleName, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1) throw Error("Mono runtime is absent or ambiguous"); mono = modules[0].BaseAddress;
                using (var hash = SHA256.Create()) using (var file = File.OpenRead(modules[0].FileName))
                    if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != MonoHash) throw Error("Mono binary identity differs");
                Target = Export<HandleTarget>("mono_gchandle_get_target"); Barrier = Export<WriteBarrier>("mono_gc_wbarrier_set_field");
                NewHandle = Export<NewHandle>("mono_gchandle_new"); Free = Export<FreeHandle>("mono_gchandle_free");
                Domain = Export<Domain>("mono_domain_get"); MethodObject = Export<MethodObject>("mono_method_get_object");
                ClassType = Export<MetadataPointer>("mono_class_get_type"); TypeClass = Export<MetadataPointer>("mono_class_from_mono_type");
                ReflectionType = Export<MetadataPointer>("mono_reflection_type_get_type"); TypeObject = Export<TypeObject>("mono_type_get_object");
                Acquire = Entry<AcquireHandle>(0x8b4620, "48895c2408574883ec2044894108");
                Release = Entry<ReleaseHandle>(0x8bbe30, "40534883ec20488339ff488bd9");
            }
            internal void Require()
            {
                if (GetModuleHandle("mono-2.0-bdwgc.dll") != mono || GetModuleHandle("UnityPlayer.dll") != engine) throw Error("loaded scripting engine changed");
            }
            private T Export<T>(string name) where T : class
            { var pointer = GetProcAddress(mono, name); return pointer == IntPtr.Zero ? throw Error("Mono export is absent: " + name) : (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)); }
            private T Entry<T>(int rva, string prefix) where T : class
            {
                var pointer = IntPtr.Add(engine, rva);
                if (BitConverter.ToString(M.Read(pointer, prefix.Length / 2)).Replace("-", "").ToLowerInvariant() != prefix) throw Error("native handle entry ABI differs");
                return (T)(object)Marshal.GetDelegateForFunctionPointer(pointer, typeof(T));
            }
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident scripting handles: " + reason + ".");
    }
}
