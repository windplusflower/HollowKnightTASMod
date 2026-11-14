using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class ResidentDepthImage
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Callback();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr Begin(IntPtr resource, byte[]? bytes, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Read(IntPtr job, uint timeout, out IntPtr bytes, out uint count, out int result);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(IntPtr job);
        private static T Export<T>(string name) where T : Delegate
        {
            var module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
            var entry = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module, "HktasClockBridge_" + name);
            if (entry == IntPtr.Zero) throw new InvalidDataException("Depth image bridge export is absent: " + name);
            return (T)Marshal.GetDelegateForFunctionPointer(entry, typeof(T));
        }
        internal static byte[] Transfer(RenderTexture texture, byte[]? source = null)
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || !texture.IsCreated())
                throw new InvalidDataException("Depth image transfer requires an allocated D3D11 render texture.");
            var begin = Export<Begin>("BeginDepthImage"); var read = Export<Read>("ReadDepthImage");
            var release = Export<Release>("ReleaseDepthImage"); var callback = Export<Callback>("GetDepthImageRenderEvent")();
            var job = begin(texture.GetNativeDepthBufferPtr(), source, (uint)(source?.Length ?? 0));
            if (job == IntPtr.Zero) throw new InvalidDataException("Depth attachment did not provide a native Texture2D resource.");
            try {
                using (var commands = new CommandBuffer()) {
                    commands.IssuePluginEventAndData(callback, 1, job);
                    Modding.Logger.Log($"[HKTAS-DEPTH-T] before ExecCmd: {ResidentTransformState.DebugSummary()}");
                    Graphics.ExecuteCommandBuffer(commands);
                    Modding.Logger.Log($"[HKTAS-DEPTH-T] after ExecCmd: {ResidentTransformState.DebugSummary()}");
                    GL.Flush();
                    Modding.Logger.Log($"[HKTAS-DEPTH-T] after GL.Flush: {ResidentTransformState.DebugSummary()}");
                }
                var status = read(job, 5000, out var bytes, out var count, out var result);
                Modding.Logger.Log($"[HKTAS-DEPTH-T] after read: {ResidentTransformState.DebugSummary()}");
                if (status != 1 || count < 24 || count > ResidentNativeAssetState.MaximumBytes)
                    throw new InvalidDataException("Depth image transfer failed: status=" + status + "; HRESULT=" + result.ToString("x8"));
                var copy = new byte[count]; Marshal.Copy(bytes, copy, 0, copy.Length); return copy;
            } finally { release(job); }
        }
        internal static void Validate(byte[] bytes, int width, int height)
        {
            if (bytes == null || bytes.Length < 24 || bytes.Length > ResidentNativeAssetState.MaximumBytes
                || BitConverter.ToUInt32(bytes, 0) != 0x31504452 || BitConverter.ToUInt32(bytes, 4) != width
                || BitConverter.ToUInt32(bytes, 8) != height) throw new InvalidDataException("Depth image header differs from its render texture.");
            var mips = BitConverter.ToUInt32(bytes, 12); var slices = BitConverter.ToUInt32(bytes, 16);
            var format = BitConverter.ToUInt32(bytes, 20);
            var stride = format == 53 || format == 55 ? 2 : format == 44 || format == 45 || format == 39 || format == 40 ? 4
                : format == 19 || format == 20 ? 8 : 0;
            if (mips < 1 || mips > 16 || slices < 1 || slices > 2048 || stride == 0) throw new InvalidDataException("Depth image format or subresources differ.");
            long expected = 24;
            for (var mip = 0; mip < mips; mip++) expected += (long)Math.Max(1, width >> mip) * Math.Max(1, height >> mip) * stride * slices;
            if (expected != bytes.Length) throw new InvalidDataException("Depth image pixel extent differs.");
        }
    }
}
