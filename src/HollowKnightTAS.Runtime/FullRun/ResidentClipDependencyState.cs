using System;
using System.IO;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Conditional binding to an already equal target. This codec never calls a
    // setter: those setters can invalidate live Animator program allocations.
    internal static class ResidentClipDependencyState
    {
        private const int MaximumBytes = 32*1024*1024;
        private const int MaximumEvents = MaximumBytes/128;
        internal sealed class Event
        {
            public uint Time, Float;
            public int Integer, Options;
            public string Function = "", Text = "", Object = "";
        }
        internal sealed class CacheMetadata
        {
            public bool ConstantPresent;
            public uint ConstantBytes;
            public ulong[] CurveStorageBytes = Array.Empty<ulong>();
        }
        internal sealed class Data
        {
            public uint SampleRate, WrapMode;
            public int ObserverCount;
            public byte[] Flags = Array.Empty<byte>();
            public uint[] Settings = Array.Empty<uint>(), Bounds = Array.Empty<uint>();
            public string AdditiveClip = "";
            public CacheMetadata Cache = new CacheMetadata();
            public Event[] Events = Array.Empty<Event>();
            public string[] RequiredGaps = Array.Empty<string>();
            internal Data Copy() => Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(Newtonsoft.Json.JsonConvert.SerializeObject(this))!;
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident Clip dependency: " + message);
        internal static Data Capture(AnimationClip owner, Func<object,string?> bind)
        {
            LoadingEngineClockProbe.RequirePinnedEngine();
            var pointer = M.Owner(owner);
            if (ResidentNativeAssetReference.ResolveTyped(bind(owner) ?? throw Error("owner has no binding"),typeof(AnimationClip),
                key => owner) != pointer) throw Error("owner identity differs");
            var header = M.Read(pointer,544);
            var gaps = new System.Collections.Generic.List<string>();
            if (!ResidentAssetBindings.IsSharedBinding(bind(owner) ?? "")) gaps.Add("clip-program-without-serialized-origin");
            if (!ResidentClipEligibility.IsClean(owner)) gaps.Add("clip-curve-ledger-not-clean-or-not-held");
            var result = new Data {
                SampleRate = M.U32(header,192), WrapMode = M.U32(header,200),
                Flags = new[] {header[196],header[197],header[384],header[428],header[429]},
                Settings = Enumerable.Range(0,6).Select(index => M.U32(header,392+index*4)).ToArray(),
                Bounds = Enumerable.Range(0,6).Select(index => M.U32(header,520+index*4)).ToArray(),
                AdditiveClip = ResidentNativeAssetReference.BindId(M.I32(header,388),typeof(AnimationClip),bind)
            };
            result.Cache.ConstantPresent = M.Ptr(header,432) != IntPtr.Zero;
            result.Cache.ConstantBytes = M.U32(header,440);
            result.Cache.CurveStorageBytes = new ulong[6];
            for (int index = 0; index < 6; index++) {
                int offset = 208+index*24;
                long start = M.Ptr(header,offset).ToInt64();
                long end = M.Ptr(header,offset+8).ToInt64();
                long vectorCapacity = M.Ptr(header,offset+16).ToInt64();
                if (start < 0 || end < start || vectorCapacity < end || vectorCapacity-start > 32*1024*1024)
                    throw Error("curve vector extent differs");
                result.Cache.CurveStorageBytes[index] = checked((ulong)(end-start));
            }
            result.Flags = result.Flags.Concat(header.Skip(416).Take(11)).ToArray();
            ulong count = M.U64(header,368), capacity = M.U64(header,376)>>1;
            if (count > MaximumEvents || capacity < count || count != 0 && M.Ptr(header,352) == IntPtr.Zero)
                throw Error("event array extent differs");
            var events = M.Read(M.Ptr(header,352),checked((int)count*128));
            result.Events = new Event[(int)count];
            long totalBytes = events.Length;
            for (int index = 0; index < result.Events.Length; index++) {
                int offset = index*128;
                var address = IntPtr.Add(M.Ptr(header,352),offset);
                result.Events[index] = new Event {
                    Time = M.U32(events,offset), Float = M.U32(events,offset+92), Integer = M.I32(events,offset+96),
                    Options = M.I32(events,offset+100), Function = M.String(IntPtr.Add(address,8)),
                    Text = M.String(IntPtr.Add(address,48)),
                    Object = ResidentNativeAssetReference.BindId(M.I32(events,offset+88),typeof(UnityEngine.Object),bind)
                };
                totalBytes += System.Text.Encoding.UTF8.GetByteCount(result.Events[index].Function)
                    + System.Text.Encoding.UTF8.GetByteCount(result.Events[index].Text);
                if (totalBytes > 32*1024*1024) throw Error("event strings exceed total byte bound");
                if (M.Ptr(events,offset+104) != IntPtr.Zero || M.Ptr(events,offset+112) != IntPtr.Zero || M.Ptr(events,offset+120) != IntPtr.Zero)
                    gaps.Add("clip-event-runtime-sender-not-covered");
            }
            // Motion UserList+88 is covered by Animator UsersView, but the
            // separate AnimationAsset observer array and legacy state list are
            // not implied by that coverage. Keep unknown live edges explicit.
            ulong observers = M.U64(header,72);
            if ((M.U64(header,80)>>1) < observers || observers > (ulong)ResidentAnimatorState.MaximumClipObservers || observers != 0 && M.Ptr(header,56) == IntPtr.Zero)
                throw Error("AnimationAsset observer extent differs");
            result.ObserverCount = (int)observers;
            var root = IntPtr.Add(pointer,176);
            if (M.Ptr(header,176) != root || M.Ptr(header,184) != root)
                gaps.Add("clip-legacy-animationstate-list-not-covered");
            result.RequiredGaps = gaps.Distinct(StringComparer.Ordinal).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
            Validate(result);
            return result;
        }
        internal static void Validate(Data data)
        {
            if (data == null || data.ObserverCount < 0 || data.ObserverCount > ResidentAnimatorState.MaximumClipObservers || data.Cache == null || data.Cache.CurveStorageBytes == null || data.Cache.CurveStorageBytes.Length != 6
                || data.Cache.CurveStorageBytes.Any(value=>value>32*1024*1024)
                || data.Cache.ConstantPresent != (data.Cache.ConstantBytes != 0) || data.Cache.ConstantBytes > 32*1024*1024
                || data.Cache.ConstantPresent && data.Cache.ConstantBytes < 2400 || data.Flags == null || data.Flags.Length != 16 || data.Flags.Any(value=>value>1)
                || data.Settings == null || data.Settings.Length != 6 || data.Bounds == null || data.Bounds.Length != 6
                || data.Events == null || data.Events.Length > MaximumEvents || data.AdditiveClip == null
                || data.RequiredGaps == null || data.RequiredGaps.Length > 16 || data.RequiredGaps.Any(string.IsNullOrEmpty))
                throw Error("typed payload extent or flags differ");
            if (data.WrapMode != 0 && data.WrapMode != 1 && data.WrapMode != 2 && data.WrapMode != 4 && data.WrapMode != 8)
                throw Error("wrap mode is unknown");
            long totalBytes = data.Events.Length*128L;
            foreach (var row in data.Events) {
                if (row == null || row.Function == null || row.Text == null || row.Object == null
                    || row.Function.Length > 16384 || row.Text.Length > 16384 || row.Options < 0 || row.Options > 1)
                    throw Error("event payload differs");
                totalBytes += System.Text.Encoding.UTF8.GetByteCount(row.Function) + System.Text.Encoding.UTF8.GetByteCount(row.Text);
                if (totalBytes > 32*1024*1024) throw Error("event strings exceed total byte bound");
            }
        }
        internal static void RequireComplete(Data data)
        {
            Validate(data);
            if (data.RequiredGaps.Length != 0) throw Error(string.Join(",",data.RequiredGaps));
        }
        internal static void RequireEquivalent(Data source,Data target)
        {
            if (ResidentAssetDataWire.Values(PortableValues(source)) != ResidentAssetDataWire.Values(PortableValues(target)))
                throw Error("target mutable clip payload differs; changed clip state restoration is not supported");
        }
        internal static Data PortableValues(Data data)
        {
            RequireComplete(data);
            // Allocation presence and local storage history are not portable D.
            // Live graph aliases are checked by Animator ClipConstants/Bindings.
            var values = data.Copy();
            values.Cache = new CacheMetadata();
            values.ObserverCount = 0;
            return values;
        }
        internal static void Preflight(Data source,AnimationClip target,Func<string,UnityEngine.Object> resolve,Func<object,string?> bind,Action admitted)
        {
            admitted();
            foreach (var key in source.Events.Select(row=>row.Object).Concat(new[] {source.AdditiveClip}).Where(key=>key.Length!=0)) {
                var owner = resolve(key);
                if (bind(owner) != key) throw Error("target reference alias differs");
            }
            RequireEquivalent(source,Capture(target,bind));
            admitted();
        }
    }
}
