using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Backend controls are separate from Unity's requested parameter cache.
    // A coherent receipt is an observation, never live voice restore coverage.
    internal static class ResidentAudioBackendControl
    {
        private const int Bytes = 664, HeaderBytes = 64, FieldCount = 15;
        internal const int Valid = 1, NotApplicable = 2, Unsupported = 3, ErrorStatus = 4;
        internal sealed class GroupBinding
        {
            internal IntPtr Opaque;
            internal string Owner = "", Role = "";
        }
        internal sealed class GroupUse
        {
            public string Owner = "", Role = "";
        }
        internal sealed class Field
        {
            public int Status, Error;
            public uint[] Values = new uint[8];
        }
        internal sealed class Data
        {
            [JsonProperty(Required=Required.Always)] public int Version = 2;
            public bool Coherent;
            public int Result, RealCount;
            public uint Mode;
            public Field[] Fields = Array.Empty<Field>();
            public GroupUse[] GroupUses = Array.Empty<GroupUse>();
            public int DelayKind;
        }
        // Failure evidence only: clocks and creation timestamps are process-local witnesses,
        // not portable D and never independently compared as playback state.
        internal sealed class DelayDiagnostic
        {
            public int Version = 2, Result, NativeStatus, NativeError;
            public bool NativeCoherent;
            public uint Classification, WitnessFlags;
            public ulong FirstClock, SecondClock;
            public uint[] RawDelay = new uint[8];
        }
        private sealed class GroupDirectory
        {
            internal IntPtr[] Pointers = Array.Empty<IntPtr>();
            internal GroupUse[][] Uses = Array.Empty<GroupUse[]>();
        }
        internal delegate int NativeQuery(IntPtr voice,IntPtr system,IntPtr groups,uint count,IntPtr output,uint bytes);
        [DllImport("HollowKnightTAS.ClockBridge.dll",CallingConvention=CallingConvention.Cdecl,ExactSpelling=true,
            EntryPoint="HktasClockBridge_QueryResidentAudioVoiceControlV2")]
        private static extern int Query(IntPtr voice,IntPtr system,IntPtr groups,uint count,IntPtr output,uint bytes);
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident Audio backend: "+message);
        private static bool Text(string value) => !string.IsNullOrEmpty(value) && value.Length<=262144 && value.IndexOf('\0')<0;
        private static GroupDirectory Groups(GroupBinding[] bindings)
        {
            if (bindings == null || bindings.Length>262144) throw Error("actual source group directory exceeds bounds");
            var aliases = new Dictionary<string,IntPtr>(StringComparer.Ordinal);
            foreach (var binding in bindings) {
                if (binding == null || binding.Opaque==IntPtr.Zero || !Text(binding.Owner) || binding.Role!="dry" && binding.Role!="wet")
                    throw Error("actual source group binding is absent or has an unknown role");
                string key=binding.Owner+"/"+binding.Role;
                if (aliases.TryGetValue(key,out var previous) && previous!=binding.Opaque)
                    throw Error("source group role maps to different native groups");
                aliases[key]=binding.Opaque;
            }
            var groups=bindings.GroupBy(binding=>binding.Opaque).ToArray();
            return new GroupDirectory {
                Pointers=groups.Select(group=>group.Key).ToArray(),
                Uses=groups.Select(group=>group.Select(binding=>new GroupUse {Owner=binding.Owner,Role=binding.Role})
                    .GroupBy(use=>use.Owner+"/"+use.Role,StringComparer.Ordinal).Select(uses=>uses.First())
                    .OrderBy(use=>use.Owner+"/"+use.Role,StringComparer.Ordinal).ToArray()).ToArray()
            };
        }
        internal static Data Capture(IntPtr opaqueChannel,IntPtr opaqueSystem,GroupBinding[] groups,Action requireVoiceIdentity,Action<DelayDiagnostic> observeDelay)
        {
            if (requireVoiceIdentity == null) throw new ArgumentNullException(nameof(requireVoiceIdentity));
            var lease=ResidentAudioPauseLease.Current ?? throw Error("backend read requires current master own-pause lease");
            if (lease.NoSystem) throw Error("backend voice cannot be observed in NoSystem mode");
            var engine=LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            void Require()
            {
                if (!ReferenceEquals(ResidentAudioPauseLease.Current,lease) || LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress!=engine)
                    throw Error("backend query changed lease or fixed engine identity");
                lease.Require();
                if (lease.RequireSystemOpaque()!=opaqueSystem) throw Error("backend system differs from the current master lease");
                requireVoiceIdentity();
            }
            return CaptureCore(opaqueChannel,opaqueSystem,groups,Require,Query,observeDelay);
        }
        internal static Data CaptureCore(IntPtr opaqueChannel,IntPtr opaqueSystem,GroupBinding[] groups,Action require,NativeQuery query,Action<DelayDiagnostic> observeDelay)
        {
            if (opaqueChannel==IntPtr.Zero || opaqueSystem==IntPtr.Zero || require==null || query==null || observeDelay==null) throw Error("backend query requires an actual voice/system and guards");
            var directory=Groups(groups);
            var bytes=new byte[Bytes];
            var input=GCHandle.Alloc(directory.Pointers,GCHandleType.Pinned);
            var output=GCHandle.Alloc(bytes,GCHandleType.Pinned);
            bool delivered=false;
            try {
                require();
                int count=query(opaqueChannel,opaqueSystem,input.AddrOfPinnedObject(),checked((uint)directory.Pointers.Length),output.AddrOfPinnedObject(),Bytes);
                delivered=count==Bytes;
                require();
                if (count!=Bytes) throw Error("native control receipt was not delivered: "+count);
            } catch {
                if(delivered) {
                    try {ObserveDelay(DecodeDelayDiagnostic(bytes),observeDelay);}
                    catch(Exception) { /* Preserve the original query/identity failure. */ }
                }
                throw;
            } finally {
                output.Free();
                input.Free();
            }
            return DecodeCore(bytes,directory,observeDelay);
        }
        internal static Data Decode(byte[] bytes,GroupBinding[] groups) => DecodeCore(bytes,Groups(groups),_=>{});
        internal static DelayDiagnostic DecodeDelayDiagnostic(byte[] bytes)
        {
            if (bytes==null || bytes.Length!=Bytes || BitConverter.ToUInt32(bytes,0)!=0x31435641u
                || BitConverter.ToUInt32(bytes,4)!=2 || BitConverter.ToUInt32(bytes,8)!=Bytes
                || BitConverter.ToUInt32(bytes,12)>1 || BitConverter.ToUInt32(bytes,28)!=FieldCount
                || BitConverter.ToUInt32(bytes,32)>2 || (BitConverter.ToUInt32(bytes,36)&~15u)!=0
                || BitConverter.ToUInt32(bytes,56)!=0 || BitConverter.ToUInt32(bytes,60)!=0)
                throw Error("native control receipt header/version/size differs");
            int offset=HeaderBytes+12*40;
            return new DelayDiagnostic {
                NativeCoherent=BitConverter.ToUInt32(bytes,12)!=0,Result=BitConverter.ToInt32(bytes,16),
                Classification=BitConverter.ToUInt32(bytes,32),WitnessFlags=BitConverter.ToUInt32(bytes,36),
                FirstClock=BitConverter.ToUInt64(bytes,40),SecondClock=BitConverter.ToUInt64(bytes,48),
                NativeStatus=BitConverter.ToInt32(bytes,offset),NativeError=BitConverter.ToInt32(bytes,offset+4),
                RawDelay=Enumerable.Range(0,8).Select(index=>BitConverter.ToUInt32(bytes,offset+8+index*4)).ToArray()
            };
        }
        private static Data DecodeCore(byte[] bytes,GroupDirectory groups,Action<DelayDiagnostic> observeDelay)
        {
            var delay=DecodeDelayDiagnostic(bytes);
            ObserveDelay(delay,observeDelay);
            var data=new Data {
                Coherent=BitConverter.ToUInt32(bytes,12)!=0,Result=BitConverter.ToInt32(bytes,16),
                Mode=BitConverter.ToUInt32(bytes,20),RealCount=BitConverter.ToInt32(bytes,24),Fields=new Field[FieldCount]
            };
            for (int index=0;index<FieldCount;index++) {
                int offset=HeaderBytes+index*40;
                data.Fields[index]=new Field {
                    Status=BitConverter.ToInt32(bytes,offset),Error=BitConverter.ToInt32(bytes,offset+4),
                    Values=Enumerable.Range(0,8).Select(slot=>BitConverter.ToUInt32(bytes,offset+8+slot*4)).ToArray()
                };
            }
            bool inactive=delay.NativeCoherent && delay.Result==0 && data.RealCount==1 && delay.Classification==1 && delay.WitnessFlags==15
                && delay.SecondClock>=delay.FirstClock && delay.NativeStatus==Valid && delay.NativeError==0;
            ulong start=(ulong)delay.RawDelay[1] | (ulong)delay.RawDelay[2]<<32;
            bool rawInactive=delay.RawDelay[0]==0 && delay.RawDelay.Skip(3).All(value=>value==0) && start<=delay.FirstClock && start<=delay.SecondClock;
            if(inactive && !rawInactive) throw Error("native inactive-delay witness contradicts its raw timing");
            data.DelayKind=inactive?1:delay.Classification==2?2:0;
            // This is a semantic inactive/no-qualified-delay projection, not a setter.
            // Raw stamps and both clocks remain only in the diagnostic observed above.
            var portableDelay=data.Fields[12];
            Array.Clear(portableDelay.Values,0,portableDelay.Values.Length);
            if(!inactive && portableDelay.Status==Valid) portableDelay.Status=Unsupported;
            var group=data.Fields[14];
            if (group.Values.Skip(1).Any(value=>value!=0)
                || group.Status!=Valid && group.Status!=Unsupported && group.Values[0]!=0
                || group.Status==Unsupported && group.Values[0]!=0 && group.Values[0]!=uint.MaxValue)
                throw Error("native group field contains an unexpected index payload");
            if (group.Status==Valid) {
                uint index=group.Values[0];
                if (index>=groups.Uses.Length) throw Error("native group index is outside the actual source directory");
                data.GroupUses=groups.Uses[index];
            }
            // Native group indexes belong only to this pinned call. Portable D
            // names the actual source owner+role aliases and contains no index.
            Array.Clear(group.Values,0,group.Values.Length);
            Validate(data);
            return data;
        }
        private static void ObserveDelay(DelayDiagnostic delay,Action<DelayDiagnostic> observe)
        {
            try {observe(delay);}
            catch(Exception error) {
                try {ResidentFailureEvidence.Mark("audio delay diagnostic failure "+error.GetType().Name);}
                catch(Exception) { }
            }
        }
        internal static void Validate(Data data)
        {
            if (data==null || data.Version!=2 || data.Fields==null || data.Fields.Length!=FieldCount || data.GroupUses==null || data.DelayKind<0 || data.DelayKind>2
                || data.GroupUses.Length>262144 || data.Coherent && (data.Result!=0 || data.RealCount!=1))
                throw Error("typed backend body is absent or has an inconsistent receipt");
            int[] used={1,1,1,1,4,2,1,1,7,2,6,2,0,3,0};
            for (int index=0;index<FieldCount;index++) {
                var field=data.Fields[index];
                if (field==null || field.Values==null || field.Values.Length!=8 || field.Status<0 || field.Status>4
                    || (field.Status==Valid || field.Status==NotApplicable || field.Status==0) && field.Error!=0
                    || field.Status==ErrorStatus && field.Error==0 || field.Values.Skip(used[index]).Any(value=>value!=0))
                    throw Error("backend field status/error/extent differs: "+index);
                if ((field.Status==0 || field.Status==NotApplicable || field.Status==ErrorStatus) && field.Values.Any(value=>value!=0))
                    throw Error("unavailable backend field carries invented values: "+index);
                if (field.Status==NotApplicable && (index<8 || index>11 || (data.Mode&0x18u)!=8u))
                    throw Error("backend NA is not an actual 2D spatial field");
                if (index>=8 && index<=11 && field.Status==Valid && (data.Mode&0x18u)!=0x10u)
                    throw Error("3D backend controls have no actual 3D mode");
            }
            if (data.DelayKind==1 ? !data.Coherent || data.Fields[12].Status!=Valid || data.Fields[12].Error!=0 : data.Fields[12].Status==Valid)
                throw Error("portable delay kind has no complete inactive witness");
            if (data.Fields[0].Status==Valid && data.Fields[0].Values[0]!=data.Mode
                || data.Fields[3].Status==Valid && data.Fields[3].Values[0]>1
                || data.Fields[7].Status==Valid && data.Fields[7].Values[0]>1
                || data.Fields[8].Status==Valid && data.Fields[8].Values[6]>1
                || data.Fields[12].Status==Valid && data.Fields[12].Values[7]>1
                || data.Fields[4].Values[3]>1 || data.Fields[4].Status==Valid && data.Fields[4].Values[3]!=0)
                throw Error("backend mode or named boolean semantics differ");
            string? previous=null;
            foreach (var use in data.GroupUses) {
                if (use==null || !Text(use.Owner) || use.Role!="dry" && use.Role!="wet") throw Error("portable group owner/role differs");
                string key=use.Owner+"/"+use.Role;
                if (previous!=null && StringComparer.Ordinal.Compare(previous,key)>=0) throw Error("portable group aliases repeat or are unordered");
                previous=key;
            }
            if ((data.Fields[14].Status==Valid)!=(data.GroupUses.Length!=0)) throw Error("portable group aliases disagree with actual group match");
        }
        internal static string[] RequiredGaps(Data data)
        {
            Validate(data);
            var gaps=new List<string>();
            if (!data.Coherent) gaps.Add("audio-backend-control-receipt-not-coherent");
            if (data.DelayKind!=1) gaps.Add(data.DelayKind==2?"audio-backend-delay-scheduled-unsupported":"audio-backend-delay-witness-unqualified");
            for (int index=0;index<FieldCount;index++)
                if (data.Fields[index].Status!=Valid && data.Fields[index].Status!=NotApplicable)
                    gaps.Add("audio-backend-control-field-"+index+"-status-"+data.Fields[index].Status+"-error-"+data.Fields[index].Error);
            return gaps.ToArray();
        }
        internal static void RequireComplete(Data? data)
        {
            if (data==null) throw Error("typed backend body is absent");
            var gaps=RequiredGaps(data);
            if (gaps.Length!=0) throw Error(string.Join(",",gaps));
        }
    }
}
