using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Caller holds PersistentManager::m_Mutex and freezes its observation afterward.
    // readString must witness external UTF8 storage through that same observation.
    // No native filesystem methods are called.
    internal static class ResidentSerializedSourcePath
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr LowerBound(IntPtr map, out IntPtr row, IntPtr key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool Less(IntPtr map, IntPtr left, IntPtr right);
        internal static string Resolve(IntPtr engine, IntPtr manager, IntPtr pathStorage, string namespacePath,
            Func<IntPtr, int, byte[]> read, Func<IntPtr, string> readString)
        {
            try {
                Pin(engine, 0x8f4cc0, 175, "f1713afbd586e5bd6eee6ecbea56527ff3e3674a916dd40a432728d8de086805", read);
                Pin(engine, 0x8f5650, 171, "98565b87829297706a916c0a38acaf1171119034627ee6e98e0bb12ecd801522", read);
            } catch (InvalidDataException error) { throw new InvalidDataException(error.Message + "; namespacePath=" + namespacePath, error); }
            var lower = (LowerBound)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x8f4cc0), typeof(LowerBound));
            var less = (Less)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0x8f5650), typeof(Less));
            return ResolveObserved(engine, manager, pathStorage, namespacePath, read, readString,
                () => { lower(IntPtr.Add(manager, 96), out var row, pathStorage); return row; },
                (left, right) => less(IntPtr.Add(manager, 96), left, right));
        }
        // Injectable pure observation kernel permits offline container/routing tests.
        internal static string ResolveObserved(IntPtr engine, IntPtr manager, IntPtr pathStorage, string namespacePath,
            Func<IntPtr, int, byte[]> read, Func<IntPtr, string> readString, Func<IntPtr> lowerBound, Func<IntPtr, IntPtr, bool> less)
        {
            string candidate = namespacePath; long winner = -1;
            var utf8 = new UTF8Encoding(false, true);
            InvalidDataException Error(string reason) => new InvalidDataException("Resident serialized source path: " + reason
                + "; namespacePath=" + namespacePath + "; candidate=" + candidate + "; handlerVtableRva=0x" + winner.ToString("x"));
            byte[] Read(IntPtr address, int size) { var value = read(address, size); if (value == null || value.Length != size) throw Error("short observation"); return value; }
            IntPtr Pointer(byte[] bytes, int offset) => new IntPtr(BitConverter.ToInt64(bytes, offset));
            string Text(IntPtr storage) { Read(storage, 40); var value = readString(storage); try { if (value == null || value.IndexOf('\0') >= 0 || utf8.GetByteCount(value) > 16384) throw Error("invalid string observation"); } catch (EncoderFallbackException) { throw Error("invalid UTF8 string observation"); } return value; }
            if (engine == IntPtr.Zero || manager == IntPtr.Zero || pathStorage == IntPtr.Zero || namespacePath == null || Text(pathStorage) != namespacePath) throw Error("namespace storage differs");
            var map = Read(IntPtr.Add(manager, 96), 32); var first = Pointer(map, 8); var last = Pointer(map, 16); var capacity = Pointer(map, 24);
            long bytes = last.ToInt64() - first.ToInt64(), reserved = capacity.ToInt64() - first.ToInt64();
            if (bytes < 0 || bytes % 80 != 0 || reserved < bytes || reserved % 80 != 0 || reserved > 8000000 || (first == IntPtr.Zero && (last != IntPtr.Zero || capacity != IntPtr.Zero))) throw Error("remap vector bounds differ");
            var vector = bytes == 0 ? Array.Empty<byte>() : Read(first, (int)bytes);
            var strings = new List<Tuple<IntPtr, string>>();
            string Witness(IntPtr storage) { var value = Text(storage); strings.Add(Tuple.Create(storage, value)); return value; }
            // Witness every native comparator operand, including external string data via readString.
            for (int offset = 0; offset < bytes; offset += 80) { Witness(IntPtr.Add(first, offset)); Witness(IntPtr.Add(first, offset + 40)); }
            var selected = lowerBound(); long delta = selected.ToInt64() - first.ToInt64();
            if (delta < 0 || delta > bytes || delta % 80 != 0) throw Error("native lower_bound returned an invalid row");
            bool remapped = selected != last && !less(pathStorage, selected);
            if (remapped) candidate = Text(IntPtr.Add(selected, 40));
            if (lowerBound() != selected || Text(pathStorage) != namespacePath) throw Error("native remap lookup changed");
            // Both RemapToAbsolutePath fallback and FileCacherRead perform VFS ToAbsolute.
            // Unknown handlers of higher priority cannot be assumed to decline this path.
            var filesystem = Pointer(Read(IntPtr.Add(engine, 0x1a1e730), 8), 0);
            if (filesystem == IntPtr.Zero) throw Error("filesystem is absent");
            var defaults = Read(filesystem,32);
            var handlers = Read(IntPtr.Add(filesystem, 32), 32); var count = BitConverter.ToUInt64(handlers, 16); var max = BitConverter.ToUInt64(handlers, 24) >> 1;
            if (count > max || max > 4096) throw Error("filesystem handler count differs; offset=32; count=" + count + "; capacity=" + max + "; rawCapacity=" + BitConverter.ToUInt64(handlers,24));
            var entries = Pointer(handlers, 0); if (count != 0 && entries == IntPtr.Zero) throw Error("filesystem handler storage is absent; offset=32; count=" + count + "; capacity=" + max);
            var inventory = count == 0 ? Array.Empty<byte>() : Read(entries, checked((int)count * 8));
            var snapshots = new List<Tuple<IntPtr, byte[]>>();
            byte[] Snapshot(IntPtr address, int length) { var value = Read(address, length); snapshots.Add(Tuple.Create(address, value)); return value; }
            void Valid(string value) { try { if (value.IndexOf('\0') >= 0 || utf8.GetByteCount(value) > 16384) throw Error("invalid UTF8 path"); } catch (EncoderFallbackException) { throw Error("invalid UTF8 path"); } }
            void Slots(IntPtr table, params int[] pairs) { for (int index = 0; index < pairs.Length; index += 2)
                if (Pointer(Snapshot(IntPtr.Add(table, pairs[index]), 8), 0) != IntPtr.Add(engine, pairs[index + 1])) throw Error("filesystem predicate slot differs"); }
            bool Prefix(string path, string prefix) => path.StartsWith(prefix, StringComparison.Ordinal);
            bool Predicate(IntPtr handler, IntPtr table, string path, bool absolute) {
                winner = table.ToInt64() - engine.ToInt64();
                if (winner == 0x1699810) {
                    Slots(table, 0x58,0x80b6e0, 0x68,0x80b6e0, 0x168,0xb9920);
                    bool hit = Prefix(path, Witness(IntPtr.Add(handler, 8)));
                    var stores = Snapshot(IntPtr.Add(handler, 48), 32); ulong n = BitConverter.ToUInt64(stores,16), cap = BitConverter.ToUInt64(stores,24) >> 1;
                    var data = Pointer(stores,0); if (n > cap || cap > 4096 || (n != 0 && data == IntPtr.Zero)) throw Error("archive storage vector differs");
                    var rows = n == 0 ? Array.Empty<byte>() : Snapshot(data, checked((int)n * 8));
                    for (int item = 0; item < (int)n; item++) { var store = Pointer(rows,item*8); if (store == IntPtr.Zero) throw Error("archive storage is null"); hit |= Prefix(path,Witness(IntPtr.Add(store,2600))); }
                    return hit;
                }
                if (winner == 0x1699f48) { Slots(table,0x58,0x8145c0,0x68,0x8146d0,0x168,0xb9920); return Prefix(path,Witness(IntPtr.Add(handler,16))); }
                if (winner == 0x169a208) { Slots(table,0x58,0x816250,0x68,0x818a20,0x168,0xb9920); var mount = Witness(IntPtr.Add(handler,64)); return absolute ? Prefix(path,mount) : path.IndexOf(mount,StringComparison.Ordinal) >= 0; }
                throw Error("unsupported higher-priority filesystem handler");
            }
            bool Local(IntPtr table) { long rva = table.ToInt64()-engine.ToInt64(); return rva == 0x166cc28 || rva == 0x166d380; }
            bool NativeAbsolute(string path) { var raw = utf8.GetBytes(path); return raw.Length > 1 && (raw[1] == ':' || raw[0] == '/' || raw[0] == '\\'); }
            string Convert(string path, bool opening) {
                Valid(path);
                for (int index = (int)count-1; index >= -1; index--) {
                    // ToAbsolute fallback uses m_LocalFileSystem (+8), whereas
                    // GetHandlerForPath fallback uses m_DefaultFileSystem (+0).
                    var handler = index < 0 ? Pointer(defaults, opening ? 0 : 8) : Pointer(inventory,index*8); if (handler == IntPtr.Zero) throw Error("filesystem handler is null");
                    var table = Pointer(Snapshot(handler,8),0); winner = table.ToInt64()-engine.ToInt64();
                    if (!Local(table)) { if (index < 0) throw Error("fallback filesystem is not proven local disk; offset=" + (opening ? 0 : 8)); if (Predicate(handler,table,path,!opening)) throw Error(opening ? "source file is owned by virtual filesystem" : "path is absolute in virtual filesystem"); continue; }
                    Slots(table, 0x58,0xb9890,0x70,0x8057d0,0x08,0x4f1720,0x10,0x4f20b0,0x18,0x4f1e30,0x50,0x4f2770,0x60,0x8086c0,0x68,0x4f0d80,0x168,0xb9890);
                    if (opening || NativeAbsolute(path)) return path;
                    // PDB char[1040], read the complete storage and require a terminator.
                    var directory = Snapshot(IntPtr.Add(engine,0x1a1e740),1040); int end = Array.IndexOf(directory,(byte)0);
                    if (end < 0) throw Error("currentDirectory has no bounded terminator");
                    string basis; try { basis = utf8.GetString(directory,0,end); } catch (DecoderFallbackException) { throw Error("currentDirectory is not strict UTF8"); }
                    if (basis.Length == 0) return path;
                    if (path.Length == 0) return basis;
                    return basis.EndsWith("/",StringComparison.Ordinal) ? basis + (path.StartsWith("/",StringComparison.Ordinal) ? path.Substring(1) : path)
                        : basis + (path.StartsWith("/",StringComparison.Ordinal) ? "" : "/") + path;
                }
                throw Error("no proven disk handler wins");
            }
            if (!remapped) candidate = Convert(candidate,false); // RemapToAbsolutePath fallback.
            candidate = Convert(candidate,false); // FileCacherRead canonicalization.
            candidate = Convert(candidate,true); // FileSystemEntry independent CanHandle routing.
            Valid(candidate);
            // Strict file-handle identity belongs to the caller. Reject native root-relative
            // and device spellings here, rather than rewriting their native meaning.
            if (candidate.Length < 3 || !((candidate[0] >= 'A' && candidate[0] <= 'Z') || (candidate[0] >= 'a' && candidate[0] <= 'z'))
                || candidate[1] != ':' || (candidate[2] != '\\' && candidate[2] != '/')) throw Error("unsupported fully qualified Windows path");
            if (!Read(IntPtr.Add(manager, 96), 32).SequenceEqual(map) || (bytes != 0 && !Read(first, (int)bytes).SequenceEqual(vector))
                || lowerBound() != selected) throw Error("remap vector changed");
            if (strings.Any(item => Text(item.Item1) != item.Item2)
                || Pointer(Read(IntPtr.Add(engine, 0x1a1e730), 8), 0) != filesystem
                || !Read(IntPtr.Add(filesystem, 32), 32).SequenceEqual(handlers)
                || !Read(filesystem,32).SequenceEqual(defaults)
                || (count != 0 && !Read(entries, checked((int)count * 8)).SequenceEqual(inventory))
                || snapshots.Any(item => !Read(item.Item1,item.Item2.Length).SequenceEqual(item.Item2))) throw Error("path routing witnesses changed");
            return candidate;
        }
        private static void Pin(IntPtr engine, int rva, int size, string expected, Func<IntPtr, int, byte[]> read)
        {
            using (var sha = SHA256.Create()) {
                var bytes = read(IntPtr.Add(engine, rva), size);
                if (bytes.Length != size || BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != expected)
                    throw new InvalidDataException("Resident serialized source path: native comparator identity differs; rva=0x" + rva.ToString("x"));
            }
        }
    }
}
