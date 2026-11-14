using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;
namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        internal sealed class FontCacheDiagnostic
        {
            public string Asset = "", Address = "", Reason = "", PackingIterator = "";
            public int FontId, MaterialId, TextureId, ConvertCase, CallbackDepth;
            public uint Width, Height, Margin, SubImageSize, SubImageIndex;
            public ulong FontDataBytes;
            public byte[] FontDataPrefix = Array.Empty<byte>();
            public string[] FontNames = Array.Empty<string>();
            public int[] FallbackIds = Array.Empty<int>(), IteratorKey = Array.Empty<int>();
            public byte[][] Ascii = Array.Empty<byte[]>(), Unicode = Array.Empty<byte[]>(), PackingRects = Array.Empty<byte[]>(), PackingKeys = Array.Empty<byte[]>();
            public FontFaceDiagnostic[] Faces = Array.Empty<FontFaceDiagnostic>();
        }
        internal sealed class FontFaceDiagnostic
        {
            public string Family = "", Address = "", NativeFamily = "", NativeStyle = "";
            public uint Style;
            public int NumFaces, FaceIndex, Flags, StyleFlags, GlyphCount;
            public byte[] Metrics = Array.Empty<byte>();
        }
        private static FontCacheDiagnostic CaptureFontCacheDiagnostic(IntPtr pointer, string asset, Func<IntPtr, int, byte[]> read)
        {
            const int MaxEntries = 4096, MaxBytes = 2 * 1024 * 1024;
            var result = new FontCacheDiagnostic
            {
                Asset = asset,
                Address = pointer.ToInt64().ToString("x")
            };
            int consumed = 0;
            byte[] Read(IntPtr address, int count)
            {
                if (count < 0 || count > MaxBytes - consumed || (count != 0 && address == IntPtr.Zero))
                {
                    throw new InvalidDataException("Font diagnostic byte or pointer bound");
                }
                consumed += count;
                var bytes = read(address, count);
                if (bytes.Length != count)
                {
                    throw new InvalidDataException("Font diagnostic short read");
                }
                return bytes;
            }
            string Text(IntPtr storage)
            {
                var header = Read(storage, 40);
                ulong size = M.U64(header, 24);
                if (size > 4096)
                {
                    throw new InvalidDataException("Font diagnostic string bound");
                }
                var dataPointer = M.Ptr(header, 0);
                if (dataPointer == IntPtr.Zero)
                {
                    if (size >= 16)
                    {
                        throw new InvalidDataException("Font diagnostic inline string");
                    }
                    dataPointer = IntPtr.Add(storage, 8);
                }
                else if (M.U64(header, 8) < size)
                {
                    throw new InvalidDataException("Font diagnostic string capacity");
                }
                var bytes = Read(dataPointer, (int)size + 1);
                if (bytes[bytes.Length - 1] != 0)
                {
                    throw new InvalidDataException("Font diagnostic string terminator");
                }
                return new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - 1);
            }
            string CString(IntPtr dataPointer)
            {
                if (dataPointer == IntPtr.Zero)
                {
                    return "";
                }
                var bytes = new List<byte>();
                for (int i = 0; i < 512; i++)
                {
                    byte value = Read(IntPtr.Add(dataPointer, i), 1)[0];
                    if (value == 0)
                    {
                        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
                    }
                    bytes.Add(value);
                }
                throw new InvalidDataException("Font diagnostic face string bound");
            }
            byte[] Dynamic(byte[] header, int offset, int stride, out int count)
            {
                ulong size = M.U64(header, offset + 16), capacity = M.U64(header, offset + 24) >> 1;
                if (size > capacity || capacity > MaxEntries)
                {
                    throw new InvalidDataException("Font diagnostic array extent");
                }
                count = (int)size;
                return Read(M.Ptr(header, offset), checked(count * stride));
            }
            byte[][] Records(byte[] bytes, int stride, int fields) => Enumerable.Range(0, bytes.Length / stride).Select(i => M.Slice(bytes, i * stride, fields)).ToArray();
            byte[] Vector(byte[] header, int offset, int stride)
            {
                long begin = M.Ptr(header, offset).ToInt64(), end = M.Ptr(header, offset + 8).ToInt64(), capacity = M.Ptr(header, offset + 16).ToInt64();
                if (end < begin || capacity < end || (end - begin) % stride != 0 || (capacity - begin) % stride != 0 || (capacity - begin) / stride > MaxEntries)
                {
                    throw new InvalidDataException("Font diagnostic vector extent");
                }
                return Read(new IntPtr(begin), checked((int)(end - begin)));
            }
            List<(IntPtr Pointer, byte[] Bytes)> Tree(IntPtr head, ulong count, int nodeSize)
            {
                if (count > MaxEntries || head == IntPtr.Zero)
                {
                    throw new InvalidDataException("Font diagnostic tree extent");
                }
                var root = Read(head, nodeSize);
                if (root[25] != 1)
                {
                    throw new InvalidDataException("Font diagnostic tree sentinel");
                }
                var pending = new Stack<(IntPtr Node, IntPtr Parent)>();
                pending.Push((M.Ptr(root, 8), head));
                var seen = new HashSet<IntPtr>();
                var nodes = new List<(IntPtr, byte[])>();
                while (pending.Count != 0)
                {
                    var item = pending.Pop();
                    if (item.Node == head)
                    {
                        continue;
                    }
                    if (item.Node == IntPtr.Zero || !seen.Add(item.Node) || (ulong)seen.Count > count)
                    {
                        throw new InvalidDataException("Font diagnostic cyclic tree or count");
                    }
                    var bytes = Read(item.Node, nodeSize);
                    if (bytes[25] != 0 || bytes[24] > 1 || M.Ptr(bytes, 8) != item.Parent)
                    {
                        throw new InvalidDataException("Font diagnostic tree links");
                    }
                    nodes.Add((item.Node, bytes));
                    pending.Push((M.Ptr(bytes, 0), item.Node));
                    pending.Push((M.Ptr(bytes, 16), item.Node));
                }
                if (nodes.Count != (long)count)
                {
                    throw new InvalidDataException("Font diagnostic tree count differs");
                }
                return nodes;
            }
            try
            {
                var font = Read(pointer, 80);
                result.FontId = M.I32(font, 8);
                result.MaterialId = M.I32(font, 64);
                result.TextureId = M.I32(font, 68);
                var implPointer = M.Ptr(font, 72);
                var impl = Read(implPointer, 320);
                result.ConvertCase = M.I32(impl, 52);
                if (M.Ptr(impl, 120) != pointer)
                {
                    throw new InvalidDataException("Font diagnostic owning Font differs");
                }
                result.Width = M.U32(impl, 272);
                result.Height = M.U32(impl, 276);
                result.Margin = M.U32(impl, 280);
                result.SubImageSize = M.U32(impl, 284);
                result.SubImageIndex = M.U32(impl, 288);
                result.CallbackDepth = M.I32(impl, 308);
                result.Ascii = Records(Dynamic(impl, 64, 60, out _), 60, 57);
                result.Unicode = Records(Vector(impl, 96, 60), 60, 57);
                result.PackingRects = Records(Vector(impl, 224, 16), 16, 16);
                var head = M.Ptr(impl, 248);
                var nodes = Tree(head, M.U64(impl, 256), 40);
                result.PackingKeys = nodes.Select(n => M.Slice(n.Bytes, 28, 8)).ToArray();
                var iterator = M.Ptr(impl, 264);
                result.PackingIterator = iterator == head ? "end" : iterator.ToInt64().ToString("x");
                if (iterator != head)
                {
                    var item = nodes.FirstOrDefault(n => n.Pointer == iterator);
                    if (item.Bytes == null)
                    {
                        throw new InvalidDataException("Font diagnostic packing iterator outside set");
                    }
                    result.IteratorKey = new[]
                    {
                        M.I32(item.Bytes,28),M.I32(item.Bytes,32)
                    };
                }
                result.FontDataBytes = M.U64(impl, 144);
                ulong dataCapacity = M.U64(impl, 152) >> 1;
                if (result.FontDataBytes > dataCapacity || dataCapacity > MaxBytes)
                {
                    throw new InvalidDataException("Font diagnostic fontData extent");
                }
                result.FontDataPrefix = Read(M.Ptr(impl, 128), (int)Math.Min(result.FontDataBytes, 256UL));
                _ = Dynamic(impl, 160, 40, out int nameCount);
                var namesPointer = M.Ptr(impl, 160);
                result.FontNames = Enumerable.Range(0, nameCount).Select(i => Text(IntPtr.Add(namesPointer, i * 40))).ToArray();
                var fallbacks = Dynamic(impl, 192, 4, out int fallbackCount);
                result.FallbackIds = Enumerable.Range(0, fallbackCount).Select(i => M.I32(fallbacks, i * 4)).ToArray();
                var dynamicPointer = M.Ptr(impl, 312);
                if (dynamicPointer != IntPtr.Zero)
                {
                    var dynamic = Read(dynamicPointer, 24);
                    var faces = Tree(M.Ptr(dynamic, 0), M.U64(dynamic, 8), 88);
                    var rows = new List<FontFaceDiagnostic>();
                    foreach (var node in faces)
                    {
                        var facePointer = M.Ptr(node.Bytes, 80);
                        var face = Read(facePointer, 216);
                        rows.Add(new FontFaceDiagnostic
                        {
                            Family = Text(IntPtr.Add(node.Pointer, 32)),
                            Style = M.U32(node.Bytes, 72),
                            Address = facePointer.ToInt64().ToString("x"),
                            NumFaces = M.I32(face, 0),
                            FaceIndex = M.I32(face, 4),
                            Flags = M.I32(face, 8),
                            StyleFlags = M.I32(face, 12),
                            GlyphCount = M.I32(face, 16),
                            NativeFamily = CString(M.Ptr(face, 24)),
                            NativeStyle = CString(M.Ptr(face, 32)),
                            Metrics = M.Slice(face, 88, 32)
                        });
                    }
                    result.Faces = rows.ToArray();
                }
            }
            catch (Exception error)
            {
                ResidentFailureEvidence.Mark("Font cache diagnostic unavailable " + error.GetType().Name);
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
