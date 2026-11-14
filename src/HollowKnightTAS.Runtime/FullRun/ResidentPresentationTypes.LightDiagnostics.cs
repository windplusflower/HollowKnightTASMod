using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        // Failure evidence only: raw instance IDs and TCS handle are diagnostic
        // locators, never portable D, writable state or coverage qualification.
        internal sealed class LightServiceDiagnostic
        {
            public string Reason = "";
            public int ListenerIndex, Vtable = 0x1621248, TransformSystem, MapCount, DataCount, CookieCount;
            public bool Dirty, Realtime;
            public LightServiceEntry[] Map = Array.Empty<LightServiceEntry>(), Data = Array.Empty<LightServiceEntry>();
            public LightCookieEntry[] Cookies = Array.Empty<LightCookieEntry>();
        }
        internal sealed class LightServiceEntry
        {
            public int MapKey, LightId, CookieId;
            public string ResidentLight = "";
            public byte[] Value = Array.Empty<byte>();
        }
        internal sealed class LightCookieEntry
        {
            public int CookieId;
            public byte[] Value = Array.Empty<byte>();
        }
        private static LightServiceDiagnostic CaptureLightServiceDiagnostic(IntPtr pointer, int index, Func<IntPtr, int, byte[]> read, Func<int, string> resident)
        {
            const int MaxEntries = 4096, MaxBytes = 2 * 1024 * 1024;
            var result = new LightServiceDiagnostic
            {
                ListenerIndex = index
            };
            int consumed = 0;
            byte[] Read(IntPtr address, int length)
            {
                if (length < 0 || length > MaxBytes - consumed || (length != 0 && address == IntPtr.Zero))
                {
                    throw new InvalidDataException("SceneLights diagnostic byte or pointer bound");
                }
                consumed += length;
                var bytes = read(address, length);
                if (bytes.Length != length)
                {
                    throw new InvalidDataException("SceneLights diagnostic short read");
                }
                return bytes;
            }
            LightServiceEntry Entry(byte[] bytes, int at, int key)
            {
                var id = M.I32(bytes, at);
                return new LightServiceEntry
                {
                    MapKey = key,
                    LightId = id,
                    CookieId = M.I32(bytes, at + 4),
                    ResidentLight = resident(id),
                    Value = M.Slice(bytes, at, 96)
                };
            }
            byte[] ArrayBytes(byte[] header, int at, int stride, out int count)
            {
                ulong n = M.U64(header, at + 16), capacity = M.U64(header, at + 24) >> 1;
                if (n > capacity || capacity > MaxEntries)
                {
                    throw new InvalidDataException("SceneLights diagnostic array extent");
                }
                count = (int)n;
                return Read(M.Ptr(header, at), checked(count * stride));
            }
            try
            {
                var header = Read(pointer, 248);
                if (header[208] > 1 || header[209] > 1)
                {
                    throw new InvalidDataException("SceneLights diagnostic flags");
                }
                result.Dirty = header[208] != 0;
                result.Realtime = header[209] != 0;
                result.TransformSystem = M.I32(header, 8);
                uint mask = M.U32(header, 24), count = M.U32(header, 28);
                ulong slots = ((ulong)mask >> 3) + 1;
                if ((mask & 7) != 0 || slots > MaxEntries || (slots & (slots - 1)) != 0 || count > slots)
                {
                    throw new InvalidDataException("SceneLights diagnostic hash extent");
                }
                result.MapCount = (int)count;
                var table = Read(M.Ptr(header, 16), checked((int)slots * 104));
                var entries = new List<LightServiceEntry>();
                var keys = new HashSet<int>();
                for (int at = 0; at < table.Length; at += 104)
                {
                    if (M.U32(table, at) >= 0xfffffffeU)
                    {
                        continue;
                    }
                    int key = M.I32(table, at + 4);
                    if (!keys.Add(key))
                    {
                        throw new InvalidDataException("SceneLights diagnostic duplicate map key");
                    }
                    var entry = Entry(table, at + 8, key);
                    if (entry.LightId != key)
                    {
                        throw new InvalidDataException("SceneLights diagnostic map key and value ID differ");
                    }
                    entries.Add(entry);
                }
                result.Map = entries.ToArray();
                if (entries.Count != count)
                {
                    throw new InvalidDataException("SceneLights diagnostic map count differs");
                }
                var data = ArrayBytes(header, 48, 96, out result.DataCount);
                result.Data = Enumerable.Range(0, result.DataCount).Select(i => Entry(data, i * 96, 0)).ToArray();
                var cookies = ArrayBytes(header, 112, 24, out result.CookieCount);
                result.Cookies = Enumerable.Range(0, result.CookieCount).Select(i => new LightCookieEntry
                {
                    CookieId = M.I32(cookies, i * 24 + 16),
                    Value = M.Slice(cookies, i * 24, 24)
                }
                ).ToArray();
            }
            catch (Exception error)
            {
                ResidentFailureEvidence.Mark("SceneLights diagnostic unavailable " + error.GetType().Name);
                result.Reason = error.GetType().Name + ": " + error.Message;
                if (result.Reason.Length > 512)
                {
                    result.Reason = result.Reason.Substring(0, 512);
                }
            }
            return result;
        }
    }
}
