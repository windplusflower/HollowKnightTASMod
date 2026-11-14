using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentPresentationTypes
    {
        internal sealed class SceneLightService
        {
            public int Vtable, Handle;
            public string Registration = "", Unsupported = "";
            public uint Interests;
            public byte[][] Fields = Array.Empty<byte[]>();
        }
        private static readonly int[] SceneLightOffsets = { 80, 88, 104, 144, 152, 168, 176 };
        private static readonly int[] SceneLightSizes = { 1, 16, 1, 1, 16, 1, 34 };

        private static SceneLightService CaptureSceneLightService(IntPtr owner, int vtable, IntPtr dispatch,
            Func<IntPtr, int, byte[]> read, Func<IntPtr, string> text)
        {
            var result = new SceneLightService { Vtable = vtable };
            try
            {
                if (vtable != 0x1621248) throw Error("unknown Light listener service");
                var bytes = read(owner, 248);
                int handle = M.I32(bytes, 8);
                if (handle < 0 || handle >= 64) throw Error("SceneLights Transform handle differs");
                ulong bit = 1UL << handle;
                var registry = read(dispatch, 2744);
                if ((M.U64(registry, 128) & bit) == 0 || (M.U64(registry, 2736) & bit) != 0)
                    throw Error("SceneLights Transform registration is not live");
                result.Handle = handle;
                result.Registration = text(IntPtr.Add(dispatch, 136 + handle * 40));
                for (int interest = 0; interest < 11; interest++)
                    if ((M.U64(registry, 40 + interest * 8) & bit) != 0) result.Interests |= 1U << interest;
                if (result.Registration.Length == 0 || result.Registration.Length > 16384 || !result.Registration.EndsWith(".m_TCSHandle", StringComparison.Ordinal)
                    || result.Interests != 0x407) throw Error("SceneLights Transform name or interest metadata differs");
                uint mask = M.U32(bytes, 24);
                ulong slots = ((ulong)mask >> 3) + 1;
                if ((mask & 7) != 0 || slots > 4096 || (slots & (slots - 1)) != 0 || M.U32(bytes, 28) != 0)
                    throw Error("SceneLights map is nonempty or layout unsupported");
                var map = read(M.Ptr(bytes, 16), checked((int)slots * 104));
                for (int at = 0; at < map.Length; at += 104)
                    if (M.U32(map, at) < 0xfffffffeU) throw Error("SceneLights map retains a live entry");
                foreach (var array in new[] { (48, 96, 88), (112, 24, 9), (216, 4, 74) })
                {
                    ulong count = M.U64(bytes, array.Item1 + 16), capacity = M.U64(bytes, array.Item1 + 24) >> 1;
                    if (count != 0 || capacity > 100000 || M.I32(bytes, array.Item1 + 8) != array.Item3
                        || capacity != 0 && M.Ptr(bytes, array.Item1) == IntPtr.Zero)
                        throw Error("SceneLights cache or empty-return array is nonempty or malformed");
                }
                result.Fields = SceneLightOffsets.Select((offset, index) => M.Slice(bytes, offset, SceneLightSizes[index])).ToArray();
                ValidateSceneLightService(result);
            }
            catch (Exception error)
            {
                result.Unsupported = error.Message.Length <= 512 ? error.Message : error.Message.Substring(0, 512);
            }
            return result;
        }
        private static void ValidateSceneLightService(SceneLightService service)
        {
            if (service == null || service.Unsupported == null || service.Unsupported.Length > 512 || service.Registration == null)
                throw Error("SceneLights service schema differs");
            if (service.Unsupported.Length != 0) return;
            if (service.Vtable != 0x1621248 || service.Handle < 0 || service.Handle >= 64 || service.Interests != 0x407
                || service.Registration.Length == 0 || service.Registration.Length > 16384 || !service.Registration.EndsWith(".m_TCSHandle", StringComparison.Ordinal)
                || service.Fields == null || service.Fields.Length != SceneLightSizes.Length
                || service.Fields.Where((field, index) => field == null || field.Length != SceneLightSizes[index]).Any())
                throw Error("SceneLights typed service payload differs");
            foreach (int field in new[] { 0, 2, 3, 5 }) if (service.Fields[field][0] > 1) throw Error("SceneLights cache flag differs");
            if (service.Fields[6][32] > 1 || service.Fields[6][33] > 1) throw Error("SceneLights dirty or realtime flag differs");
        }
        private static void RequireLightServiceDependencies(LightRegistry source, LightRegistry target)
        {
            if (source.ListenerVtables.Length == 0 && target.ListenerVtables.Length == 0) return;
            if (!source.ListenerVtables.SequenceEqual(target.ListenerVtables) || source.Services == null || target.Services == null
                || source.Services.Length != source.ListenerVtables.Length || target.Services.Length != target.ListenerVtables.Length)
                throw Error("SceneLights local service directory differs");
            for (int index = 0; index < source.Services.Length; index++)
            {
                var saved = source.Services[index]; var actual = target.Services[index];
                ValidateSceneLightService(saved); ValidateSceneLightService(actual);
                if (saved.Unsupported.Length != 0 || actual.Unsupported.Length != 0 || saved.Vtable != actual.Vtable
                    || saved.Handle != actual.Handle || saved.Registration != actual.Registration || saved.Interests != actual.Interests)
                    throw Error("SceneLights source and target logical registration differs or is unsupported");
            }
        }
        private static void ApplySceneLightFields(SceneLightService source, SceneLightService target, IntPtr owner, Action admitted)
        {
            RequireLightServiceDependencies(new LightRegistry { ListenerVtables = new[] { source.Vtable }, Services = new[] { source } },
                new LightRegistry { ListenerVtables = new[] { target.Vtable }, Services = new[] { target } });
            // Empty allocation capacities, tombstones and native handles remain
            // target-local. Only consumer-visible POD flags/hashes are restored.
            for (int index = 0; index < SceneLightOffsets.Length; index++)
            {
                admitted(); M.Write(IntPtr.Add(owner, SceneLightOffsets[index]), source.Fields[index]);
            }
        }
    }
}
