using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Native addresses stay in this process and are never serialized.
    internal sealed class LoadingContactImpulseProbe
    {
        private const int Stride = 112, Capacity = 512;
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_SetContactImpulseProbeFrame(int movie);
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_CopyContactImpulseProbe([Out] byte[] output, int bytes);
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_StopContactImpulseProbe();
        private readonly Dictionary<IntPtr, int> localContacts = new Dictionary<IntPtr, int>();
        private int delivered;
        private bool stopped;
        internal static LoadingContactImpulseProbe? Create()
        {
            if (Environment.GetEnvironmentVariable("HKTAS_CONTACT_IMPULSE_PROBE") != "1") return null;
            var probe = new LoadingContactImpulseProbe(); probe.BeforeFrame(-1); return probe;
        }
        internal void BeforeFrame(long movie)
        {
            if (!stopped && HktasClockBridge_SetContactImpulseProbeFrame(checked((int)movie)) != 1)
                throw new InvalidOperationException("Pinned contact impulse probe unavailable.");
        }
        internal object[] Read(LoadingNativePhysicsDigest physics)
        {
            var bytes = new byte[Stride * Capacity];
            var count = HktasClockBridge_CopyContactImpulseProbe(bytes, bytes.Length);
            if (count < delivered || count > Capacity) throw new InvalidDataException("Contact impulse probe incomplete/overflow/unpublished ABI.");
            var result = new List<object>();
            for (var i = delivered; i < count; i++) {
                var at = i * Stride;
                var contact = new IntPtr(BitConverter.ToInt64(bytes, at + 16));
                if (!localContacts.TryGetValue(contact, out var local)) { local = localContacts.Count; localContacts.Add(contact, local); }
                var manifold = BitConverter.ToInt32(bytes, at + 24);
                var impulse = BitConverter.ToInt32(bytes, at + 28);
                var unchanged = BitConverter.ToInt32(bytes, at + 80) == 1;
                if (BitConverter.ToInt32(bytes, at) != 1 || manifold < 0 || manifold > 2 || impulse < 0 || impulse > 2 || !unchanged)
                    throw new InvalidDataException("Contact impulse callback ABI or original input changed.");
                string Hex(int offset) => BitConverter.ToString(bytes, at + offset, 16).Replace("-", "").ToLowerInvariant();
                result.Add(new { movieFrame = BitConverter.ToInt32(bytes, at + 4), nativeFrame = BitConverter.ToUInt64(bytes, at + 8),
                    localContact = local, contact = physics.ContactProbeBinding(contact,
                        new IntPtr(BitConverter.ToInt64(bytes, at + 88)), new IntPtr(BitConverter.ToInt64(bytes, at + 96)),
                        BitConverter.ToInt32(bytes, at + 104), BitConverter.ToInt32(bytes, at + 108)), manifoldCount = manifold, impulseCount = impulse,
                    callbackImpulseHex = Hex(32), reportBeforeHex = Hex(48), reportAfterHex = Hex(64),
                    reportPresent = BitConverter.ToInt32(bytes, at + 84) == 1, originalInputUnchanged = unchanged,
                    uninitializedValueReproductionQualified = false, completeStateCoverage = false });
            }
            delivered = count; return result.ToArray();
        }
        internal void Stop()
        {
            if (stopped) return;
            if (HktasClockBridge_StopContactImpulseProbe() != 1) throw new InvalidOperationException("Original contact tail jump restoration failed.");
            stopped = true;
        }
    }
}
