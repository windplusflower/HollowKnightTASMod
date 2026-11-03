using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Owner getters on Unity Mono anonymous/sharing parameters may abort
    // natively. Read the pinned MonoGenericParam/MonoGenericContainer ABI,
    // then validate its owner against already-loaded ECMA metadata.
    internal static class ResidentMonoGenericMetadata
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<Type, ResidentManagedTypeCatalog.TypeReference> Cache
            = new Dictionary<Type, ResidentManagedTypeCatalog.TypeReference>();
        private static readonly Lazy<NativeAbi> Native = new Lazy<NativeAbi>(() => new NativeAbi());
        internal sealed class UnownedParameterException : Exception
        {
            internal readonly bool Anonymous;
            internal UnownedParameterException(string detail, bool anonymous = false) : base(detail) { Anonymous = anonymous; }
        }
        internal static ResidentManagedTypeCatalog.TypeReference DescribeParameter(Type parameter)
        {
            if (parameter == null || !parameter.IsGenericParameter || parameter.Assembly.IsDynamic)
                throw new InvalidDataException("Resident generic parameter is not loaded metadata.");
            lock (Gate) {
                if (!Cache.TryGetValue(parameter, out var entry)) {
                    MemberInfo owner; int position;
                    if (Type.GetType("Mono.Runtime") != null) {
                        var abi = Native.Value;
                        var monoType = abi.Read(parameter.TypeHandle.Value, 16);
                        var code = monoType[10];
                        if (code != 0x13 && code != 0x1e) throw new InvalidDataException("Resident Mono parameter type code differs.");
                        var param = abi.Read(new IntPtr(BitConverter.ToInt64(monoType, 0)), 16);
                        var container = abi.Read(new IntPtr(BitConverter.ToInt64(param, 0)), 0x28);
                        var flags = BitConverter.ToUInt32(container, 0x20);
                        position = BitConverter.ToUInt16(param, 8);
                        var method = (flags & 0x20000000) != 0;
                        if (method != (code == 0x1e)) throw new InvalidDataException("Resident Mono parameter owner kind differs.");
                        // Anonymous containers own an image rather than a class
                        // or method. This is evidence of missing metadata owner,
                        // NOT proof of the absence of static storage.
                        if ((flags & 0x40000000) != 0)
                            throw new UnownedParameterException("anonymous-image-container:" + parameter.Module.ModuleVersionId.ToString("D")
                                + ":" + (method ? "mvar" : "var") + ":" + position + ":shared=" + ((flags & 0x80000000) != 0), anonymous: true);
                        var nativeOwner = new IntPtr(BitConverter.ToInt64(container, 0x18));
                        var klass = method ? new IntPtr(BitConverter.ToInt64(abi.Read(nativeOwner, 16), 8)) : nativeOwner;
                        var ownerType = abi.TypeFromClass(klass);
                        if (method) {
                            var token = BitConverter.ToInt32(abi.Read(nativeOwner, 8), 4);
                            owner = ownerType.Module.ResolveMethod(token);
                        } else owner = ownerType.IsGenericType ? ownerType.GetGenericTypeDefinition() : ownerType;
                    } else {
                        // CLR offline tests do not use Unity Mono's unsafe icall.
                        owner = (MemberInfo?)parameter.DeclaringMethod ?? parameter.DeclaringType
                            ?? throw new InvalidDataException("Resident CLR parameter has no owner.");
                        position = parameter.GenericParameterPosition;
                    }
                    var parameters = owner is MethodBase methodOwner ? methodOwner.GetGenericArguments() : ((Type)owner).GetGenericArguments();
                    if (position >= parameters.Length || !parameters[position].IsGenericParameter
                        || parameters[position] != parameter)
                        throw new UnownedParameterException("parameter-owner-roundtrip-differs:" + owner.Module.ModuleVersionId.ToString("D") + "/" + owner.MetadataToken + "/" + position);
                    entry = new ResidentManagedTypeCatalog.TypeReference { Kind = owner is MethodBase ? "mvar" : "var", Name = parameter.Name,
                        Assembly = owner.Module.Assembly.GetName().Name!, Module = owner.Module.ModuleVersionId.ToString("D"),
                        Token = owner.MetadataToken, Position = position };
                    Cache.Add(parameter, entry);
                }
                return new ResidentManagedTypeCatalog.TypeReference { Kind = entry.Kind, Name = entry.Name,
                    Assembly = entry.Assembly, Module = entry.Module, Token = entry.Token, Position = entry.Position };
            }
        }
        private sealed class NativeAbi
        {
            private const string Hash = "211e11bab370bc6acbd5b8f1af4f8a5536cd62df91c2ca8bdc4512e1a2d8834f";
            [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
            [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
            private readonly ConstructorInfo handleConstructor;
            internal NativeAbi()
            {
                if (IntPtr.Size != 8) throw new InvalidDataException("Resident generic metadata requires pinned x64 Mono.");
                var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                    .Where(value => string.Equals(value.ModuleName, "mono-2.0-bdwgc.dll", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (modules.Length != 1) throw new InvalidDataException("Resident generic metadata Mono module differs.");
                using (var hash = SHA256.Create()) using (var file = File.OpenRead(modules[0].FileName))
                    if (BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant() != Hash)
                        throw new InvalidDataException("Resident generic metadata Mono ABI differs.");
                var module = modules[0].BaseAddress;
                Check(module, 0x96c40, "0fbe410ac3"); // MonoType.type +0xa
                Check(module, 0x3ab20, "488d81b8000000c3"); // class.byval_arg +0xb8
                Check(module, 0x38190, "488b014c8bf18b4820"); // param.owner; container flags +0x20
                Check(module, 0x3584e, "0fb76908"); // param ordinal uint16 +8
                Check(module, 0x72ec0, "488b4108c3"); // method.class +8
                Check(module, 0x73b70, "8b4104c3"); // method.token +4
                handleConstructor = typeof(RuntimeTypeHandle).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null)
                    ?? throw new InvalidDataException("Resident generic metadata handle ABI differs.");
            }
            private void Check(IntPtr module, int rva, string expected)
            {
                if (BitConverter.ToString(Read(IntPtr.Add(module, rva), expected.Length / 2)).Replace("-", "").ToLowerInvariant() != expected)
                    throw new InvalidDataException("Resident generic metadata code ABI differs.");
            }
            internal byte[] Read(IntPtr address, int length)
            {
                var bytes = new byte[length];
                if (address == IntPtr.Zero || !ReadProcessMemory(GetCurrentProcess(), address, bytes, new UIntPtr((uint)length), out var read)
                    || read.ToUInt64() != (ulong)length) throw new InvalidDataException("Resident generic metadata memory is unreadable.");
                return bytes;
            }
            internal Type TypeFromClass(IntPtr klass)
            {
                _ = Read(klass, 0xc8);
                var handle = (RuntimeTypeHandle)handleConstructor.Invoke(new object[] { IntPtr.Add(klass, 0xb8) });
                return Type.GetTypeFromHandle(handle) ?? throw new InvalidDataException("Resident generic metadata owner class is absent.");
            }
        }
    }
}
