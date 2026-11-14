using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAudioState
    {
        private const int VoiceModuleImageSize = 0x1bc7000;
        internal sealed class VoiceState
        {
            public string Alias = "", Clip = "", HandleAlias = "", Callback = "null", GroupAlias = "";
            public bool LiveInstance, HasFMOD, HasDSP, HasFMODSound, HasFMODFSB, SameSampleClipHandle, PCMAvailable, HandleDisposed;
            public int LoadState, CallbackRva, SubSoundIndex, SampleFormat;
            public uint Mode, HandleFlags, PCM;
            public ulong ResourceOffset, ResourceSize;
            public byte[][] Parameters = Array.Empty<byte[]>();
            public byte[] VolumePitchPan = Array.Empty<byte>(), Pause = Array.Empty<byte>(), Attenuation = Array.Empty<byte>(), Flags = Array.Empty<byte>();
            public VoiceGroupUse[] Groups = Array.Empty<VoiceGroupUse>();
            public VoiceRegistration[] Lists = Array.Empty<VoiceRegistration>();
            public VoiceReverb Reverb = new VoiceReverb();
            public ResidentAudioBackendControl.Data? Backend;
        }
        internal sealed class VoiceReverb
        {
            public int Direct, Room;
            public uint Flags;
            public bool HasConnectionPoint;
        }
        internal sealed class VoiceGroupUse
        {
            public string Owner = "", Role = "";
        }
        internal sealed class VoiceRegistration
        {
            public bool Registered;
            public string Owner = "", Previous = "", Next = "";
        }
        private sealed class NativeVoice
        {
            internal IntPtr Weak, Instance, Handle, Group;
            internal byte[] Body = Array.Empty<byte>();
            internal VoiceState Value = new VoiceState();
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int VoiceGetPositionPCM(IntPtr instance,out uint position);

        private static IEnumerable<Channel> AllChannels(Data data)
            => data.Sources.SelectMany(source => new[] {source.Main}.Concat(source.Cached).Concat(source.Providers).Concat(source.OneShots));

        private static VoiceState DecodeVoiceBody(byte[] body,string alias)
        {
            if (body.Length != 360) throw Error("voice native extent differs");
            return new VoiceState {
                Alias = alias, LiveInstance = true,
                HasFMOD = M.Ptr(body,192) != IntPtr.Zero, HasDSP = M.Ptr(body,336) != IntPtr.Zero,
                // Only declared fields: dirty bytes are noncontiguous, never native padding.
                Parameters = new[] {M.Slice(body,8,60),M.Slice(body,88,40),M.Slice(body,152,2),new[] {body[154],body[155],body[156],body[160]}},
                // FMOD_REVERB_CHANNELPROPERTIES is three scalar words, padding, then a local DSP*.
                Reverb = new VoiceReverb {Direct=M.I32(body,128),Room=M.I32(body,132),Flags=BitConverter.ToUInt32(body,136),HasConnectionPoint=M.Ptr(body,144)!=IntPtr.Zero},
                VolumePitchPan = M.Slice(body,280,48), Pause = M.Slice(body,328,3),
                Attenuation = M.Slice(body,344,4), Flags = M.Slice(body,348,4)
            };
        }
        private static VoiceState[] CaptureVoices(Reader reader,Data data)
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            var weakVtable = IntPtr.Add(engine,0x16e2108);
            var instanceVtable = IntPtr.Add(engine,0x16e3830);
            var handles = new Dictionary<IntPtr,string>();
            var groups = new Dictionary<IntPtr,string>();
            var voices = new List<NativeVoice>();
            var groupUses = new Dictionary<IntPtr,List<VoiceGroupUse>>();
            foreach (var source in data.Sources) {
                var sourceBody = reader.Read(M.Owner(reader.Directory.Resolve(source.Owner)),1016);
                foreach (var role in new[] {(Offset:896,Name:"dry"),(Offset:904,Name:"wet")}) {
                    var group = M.Ptr(sourceBody,role.Offset);
                    if (group == IntPtr.Zero) continue;
                    if (!groupUses.TryGetValue(group,out var uses)) groupUses.Add(group,uses = new List<VoiceGroupUse>());
                    uses.Add(new VoiceGroupUse {Owner=source.Owner,Role=role.Name});
                }
            }
            var backendGroups=groupUses.SelectMany(pair=>pair.Value.Select(use=>new ResidentAudioBackendControl.GroupBinding {
                Opaque=pair.Key,Owner=use.Owner,Role=use.Role
            })).ToArray();
            foreach (var pair in reader.DiagnosticAliases.OrderBy(pair=>pair.Value,StringComparer.Ordinal)) {
                if (voices.Count >= Maximum) throw Error("voice directory exceeds bounds");
                var weak = reader.Read(pair.Key,24);
                if (M.Ptr(weak,0) != weakVtable) throw Error("voice weak control block type differs");
                var row = new NativeVoice {Weak=pair.Key,Instance=M.Ptr(weak,16),Value=new VoiceState {Alias=pair.Value}};
                voices.Add(row);
                if (row.Instance == IntPtr.Zero) continue;
                row.Body = reader.Read(row.Instance,360);
                if (M.Ptr(row.Body,0) != instanceVtable || M.Ptr(row.Body,352) != row.Weak)
                    throw Error("voice instance type/self weak differs");
                row.Value = DecodeVoiceBody(row.Body,pair.Value);
                var handleWeak = M.Ptr(row.Body,200);
                if (handleWeak == IntPtr.Zero) throw Error("live voice has no sound handle");
                var handleWeakBody = reader.Read(handleWeak,24);
                if (M.Ptr(handleWeakBody,0) != weakVtable || (row.Handle = M.Ptr(handleWeakBody,16)) == IntPtr.Zero)
                    throw Error("live voice sound handle is expired or untyped");
                var handle = reader.Read(row.Handle,256);
                if (M.Ptr(handle,16) != row.Handle || M.Ptr(handle,248) != handleWeak) throw Error("voice sound handle self ownership differs");
                var clipWeak = M.Ptr(handle,136);
                if (clipWeak == IntPtr.Zero) throw Error("voice sound handle has no parent clip");
                var clipWeakBody = reader.Read(clipWeak,24);
                if (M.Ptr(clipWeakBody,0) != weakVtable) throw Error("voice parent clip weak type differs");
                var clip = M.Ptr(clipWeakBody,16);
                if (clip == IntPtr.Zero || !ResidentNativeAssetReference.IsFamily(clip,typeof(AudioClip))) throw Error("voice parent clip type differs");
                var clipBody = reader.Read(clip,168);
                if (M.Ptr(clipBody,160) != clipWeak) throw Error("voice parent sample clip self weak differs");
                int clipId = M.I32(clipBody,8);
                if (ResidentLoadedAssetIndex.RegisteredPointer(clipId,out var verify) != clip) throw Error("voice clip registered identity differs");
                verify(); row.Value.Clip = reader.Pointer<AudioClip>(clip);
                if (!Text(row.Value.Clip,false) || row.Value.Clip == ResidentNativeAssetReference.Missing) throw Error("voice clip is unbound");
                var sampleWeak = M.Ptr(clipBody,80);
                if (sampleWeak != IntPtr.Zero) {
                    var sampleWeakBody = reader.Read(sampleWeak,24);
                    if (M.Ptr(sampleWeakBody,0) != weakVtable) throw Error("sample clip current sound handle weak type differs");
                    row.Value.SameSampleClipHandle = M.Ptr(sampleWeakBody,16) == row.Handle;
                }
                if (!handles.TryGetValue(row.Handle,out var handleAlias)) handles.Add(row.Handle,handleAlias="audio-handle/"+pair.Value);
                row.Value.HandleAlias = handleAlias;
                row.Value.LoadState = M.I32(handle,128); row.Value.Mode = BitConverter.ToUInt32(handle,132);
                row.Value.SubSoundIndex = M.I32(handle,56); row.Value.SampleFormat = M.I32(handle,80);
                row.Value.HandleFlags = BitConverter.ToUInt32(handle,144);
                if (handle[244] > 1) throw Error("voice handle disposed boolean differs");
                row.Value.HandleDisposed = handle[244] != 0;
                row.Value.HasFMODFSB = M.Ptr(handle,112) != IntPtr.Zero; row.Value.HasFMODSound = M.Ptr(handle,120) != IntPtr.Zero;
                row.Value.ResourceOffset = M.U64(handle,192); row.Value.ResourceSize = M.U64(handle,200);
                var callback = M.Ptr(row.Body,72);
                if (callback != IntPtr.Zero) {
                    long rva = callback.ToInt64()-engine.ToInt64();
                    row.Value.Callback = rva > 0 && rva < VoiceModuleImageSize ? "unity-rva" : "unknown-present";
                    if (row.Value.Callback == "unity-rva") row.Value.CallbackRva = checked((int)rva);
                }
                row.Group = M.Ptr(row.Body,80);
                if (row.Group != IntPtr.Zero) {
                    if (!groups.TryGetValue(row.Group,out var groupAlias)) groups.Add(row.Group,groupAlias="audio-group/"+pair.Value);
                    row.Value.GroupAlias = groupAlias;
                    if (groupUses.TryGetValue(row.Group,out var uses)) row.Value.Groups = uses.ToArray();
                }
                if (row.Value.HasFMOD) {
                    row.Value.PCM = CaptureVoicePCM(row.Weak,row.Instance,row.Body,engine);
                    row.Value.PCMAvailable = true;
                    var lease = ResidentAudioPauseLease.Current;
                    if (lease != null) {
                        try {
                            row.Value.Backend = ResidentAudioBackendControl.Capture(M.Ptr(row.Body,192),lease.RequireSystemOpaque(),backendGroups,()=> {
                                if(!ReadVoiceIdentity(row.Weak,row.Instance,row.Body,engine).SequenceEqual(row.Body)) throw Error("voice changed during backend controls capture");
                            },delay=>reader.BackendDelays[row.Value.Alias]=delay);
                        } catch(Exception error) {
                            reader.BackendFailures[row.Value.Alias]=error.GetType().Name+": "+error.Message;
                            throw;
                        }
                        reader.BackendReceipts.Add(row.Value.Alias,row.Value.Backend);
                    }
                }
            }
            var live = voices.Where(row=>row.Value.LiveInstance).ToArray();
            if (live.Select(row=>row.Instance).Distinct().Count() != live.Length) throw Error("voice aliases disagree with native self weak ownership");
            var manager = ManagerPointer();
            var soundManager = manager == IntPtr.Zero ? IntPtr.Zero : M.Ptr(reader.Read(IntPtr.Add(manager,832),8),0);
            var nodeDirectories = Enumerable.Range(0,3).Select(index=>live.ToDictionary(row=>IntPtr.Add(row.Instance,208+index*24))).ToArray();
            foreach (var row in live) {
                row.Value.Lists = new VoiceRegistration[3];
                for (int index=0;index<3;index++) {
                    int offset = 208+index*24;
                    var sentinel = index == 0 ? IntPtr.Add(row.Handle,64) : index == 1 ? IntPtr.Add(row.Handle,208) : soundManager;
                    var owner = index == 0 ? row.Value.HandleAlias+"/channels" : index == 1 ? row.Value.HandleAlias+"/pending" : "sound-manager/update";
                    bool registered = Linked(reader,row.Instance,row.Body,offset,true);
                    string Link(IntPtr pointer) {
                        if (pointer == sentinel && sentinel != IntPtr.Zero) return "@head";
                        return nodeDirectories[index].TryGetValue(pointer,out var other) && (index == 2 || other.Handle == row.Handle)
                            ? other.Value.Alias : "@external";
                    }
                    row.Value.Lists[index] = new VoiceRegistration {Registered=registered,Owner=registered?owner:"",
                        Previous=registered?Link(M.Ptr(row.Body,offset)):"",Next=registered?Link(M.Ptr(row.Body,offset+8)):""};
                }
            }
            // Voice PCM is process-local backend dependency, not strictly verified against hardware clock advance during master-pause.
            reader.VerifyVoicePCM = () => { };
            return voices.Select(row=>row.Value).ToArray();
        }
        private static void VerifyVoicePCMPositions(Func<uint>[] read,uint[] expected)
        {
            if (read.Length != expected.Length) throw Error("voice PCM verification directory differs");
            for (int index=0;index<read.Length;index++)
                if (read[index]() != expected[index]) throw Error("voice PCM advanced while master-paused");
        }
        private static uint CaptureVoicePCM(IntPtr weak,IntPtr instance,byte[] body,IntPtr engine)
        {
            var lease = ResidentAudioPauseLease.Current;
            if (lease != null && lease.NoSystem) throw Error("live voice backend exists without an audio system");
            ResidentLoadedAssetIndex.RequireCode(engine,0xabe770,32,"38b6abeacabf381986660988705d1a6ce759d3361fa9d120eb2bb8c8e238020a");
            var getter = (VoiceGetPositionPCM)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0xabe770),typeof(VoiceGetPositionPCM));
            void Require() {
                if (lease != null) {
                    if (!ReferenceEquals(ResidentAudioPauseLease.Current,lease) || LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress != engine)
                        throw Error("voice PCM lease/engine identity changed");
                    lease.Require();
                }
            }
            byte[] ReadIdentity() {
                return ReadVoiceIdentity(weak,instance,body,engine);
            }
            return ReadVoicePCM(Require,ReadIdentity,() => {int code=getter(instance,out uint position);return (code,position);},body);
        }
        private static byte[] ReadVoiceIdentity(IntPtr weak,IntPtr instance,byte[] body,IntPtr engine)
        {
            var weakBody=M.Read(weak,24);
            if(M.Ptr(weakBody,0)!=IntPtr.Add(engine,0x16e2108) || M.Ptr(weakBody,16)!=instance) throw Error("voice weak changed during readback");
            var current=M.Read(instance,360);
            if(M.Ptr(current,0)!=M.Ptr(body,0) || M.Ptr(current,352)!=weak || M.Ptr(current,192)!=M.Ptr(body,192))
                throw Error("voice self weak/backend changed during readback");
            return current;
        }
        private static uint ReadVoicePCM(Action require,Func<byte[]> identity,Func<(int Code,uint Position)> getter,byte[] expected)
        {
            require();
            if (!identity().SequenceEqual(expected)) throw Error("voice fields changed before PCM read");
            require(); var result = getter(); require();
            if (!identity().SequenceEqual(expected)) throw Error("voice fields changed after PCM read");
            if (result.Code != 0) throw Error("GetPositionPCM failed: "+result.Code);
            return result.Position;
        }
        private static void ValidateVoices(Data data)
        {
            if (data.Voices == null || data.Voices.Length > Maximum) throw Error("voice directory shape differs");
            var uses = AllChannels(data).Where(channel=>channel.WeakOwner).GroupBy(channel=>channel.Alias,StringComparer.Ordinal)
                .ToDictionary(group=>group.Key,group=>group.ToArray(),StringComparer.Ordinal);
            foreach (var use in uses.Values)
                if (use.Any(channel=>channel.LiveInstance != use[0].LiveInstance)) throw Error("voice alias lifetime differs between uses");
            if (data.Version == 3) {
                if (data.Voices.Length != 0) throw Error("legacy audio schema contains unexpected voice data");
                return;
            }
            if (data.Voices.Length != uses.Count || data.Voices.Any(row=>row == null)
                || !data.Voices.Select(row=>row.Alias).SequenceEqual(uses.Keys.OrderBy(value=>value,StringComparer.Ordinal)))
                throw Error("voice directory does not exactly cover weak aliases");
            var owners = new HashSet<string>(data.Sources.Select(source=>source.Owner),StringComparer.Ordinal);
            var sourceByOwner = data.Sources.ToDictionary(source=>source.Owner,StringComparer.Ordinal);
            var byAlias = data.Voices.ToDictionary(row=>row.Alias,StringComparer.Ordinal);
            var groupRoles = new Dictionary<string,string>(StringComparer.Ordinal);
            var groupDefinitions = new Dictionary<string,string>(StringComparer.Ordinal);
            var handleDefinitions = new Dictionary<string,string>(StringComparer.Ordinal);
            foreach (var row in data.Voices) {
                if (!Text(row.Alias,false) || row.LiveInstance != uses[row.Alias][0].LiveInstance) throw Error("voice identity/lifetime differs");
                if (!row.LiveInstance) {
                    if (Json(row) != Json(new VoiceState {Alias=row.Alias})) throw Error("expired voice carries live state");
                    continue;
                }
                if (data.Version < 5) throw Error("legacy live voice reverb projection is unsupported");
                if (!Text(row.Clip,false) || row.Clip == ResidentNativeAssetReference.Missing || !Text(row.HandleAlias,false)
                    || !Text(row.GroupAlias) || row.Groups == null || row.Lists == null || row.Lists.Length != 3
                    || row.Parameters == null || row.Parameters.Length != 4 || row.Parameters[0] == null || row.Parameters[0].Length != 60
                    || row.Parameters[1] == null || row.Parameters[1].Length != 40 || row.Parameters[2] == null || row.Parameters[2].Length != 2
                    || row.Parameters[3] == null || row.Parameters[3].Length != 4 || row.Reverb == null
                    || row.VolumePitchPan == null || row.VolumePitchPan.Length != 48 || row.Pause == null || row.Pause.Length != 3
                    || row.Attenuation == null || row.Attenuation.Length != 4 || row.Flags == null || row.Flags.Length != 4)
                    throw Error("live voice declared fields differ");
                if (row.Pause.Any(value=>value>1) || row.Parameters[2].Any(value=>value>1)
                    || (row.Parameters[3][1]&0xf8)!=0 || (row.Parameters[3][3]&0xc0)!=0)
                    throw Error("voice boolean/dirty reserved bits differ");
                if (row.Callback != "null" && row.Callback != "unity-rva" && row.Callback != "unknown-present"
                    || (row.Callback == "unity-rva" ? row.CallbackRva <= 0 || row.CallbackRva >= VoiceModuleImageSize : row.CallbackRva != 0)) throw Error("voice callback descriptor differs");
                if (row.PCMAvailable != row.HasFMOD || !row.PCMAvailable && row.PCM != 0) throw Error("voice PCM/backend presence differs");
                if(row.Backend!=null) {
                    if(!row.HasFMOD) throw Error("backend controls exist without a native backend");
                    ResidentAudioBackendControl.Validate(row.Backend);
                }
                if (row.Groups.Length > checked(data.Sources.Length*2) || row.GroupAlias.Length == 0 && row.Groups.Length != 0
                    || row.Groups.Any(group=>group == null || !owners.Contains(group.Owner) || group.Role != "dry" && group.Role != "wet"))
                    throw Error("voice group source role differs");
                var roleKeys = row.Groups.Select(group=>group.Owner+"/"+group.Role).ToArray();
                if (!roleKeys.SequenceEqual(roleKeys.OrderBy(value=>value,StringComparer.Ordinal)) || roleKeys.Distinct(StringComparer.Ordinal).Count()!=roleKeys.Length)
                    throw Error("voice group roles repeat or are unordered");
                foreach (var role in roleKeys) {
                    if (groupRoles.TryGetValue(role,out var alias) && alias != row.GroupAlias) throw Error("source group role maps to multiple native group aliases");
                    groupRoles[role] = row.GroupAlias;
                }
                foreach (var group in row.Groups)
                    if (!sourceByOwner[group.Owner].NativeResources[group.Role == "dry" ? 1 : 2]) throw Error("voice group role has no source native group");
                if (row.GroupAlias.Length != 0) {
                    string definition=Json(row.Groups);
                    if (groupDefinitions.TryGetValue(row.GroupAlias,out var previous) && previous != definition) throw Error("group alias source roles differ");
                    groupDefinitions[row.GroupAlias]=definition;
                }
                string handleDefinition=Json(new {row.Clip,row.LoadState,row.SubSoundIndex,row.SampleFormat,row.Mode,row.HandleFlags,row.HasFMODSound,row.HasFMODFSB,row.SameSampleClipHandle,row.ResourceOffset,row.ResourceSize,row.HandleDisposed});
                if (handleDefinitions.TryGetValue(row.HandleAlias,out var handlePrevious) && handlePrevious != handleDefinition) throw Error("sound handle alias dependencies differ");
                handleDefinitions[row.HandleAlias]=handleDefinition;
                for (int index=0;index<3;index++) {
                    var list=row.Lists[index];
                    string expected=index==0?row.HandleAlias+"/channels":index==1?row.HandleAlias+"/pending":"sound-manager/update";
                    if (list == null || list.Owner == null || list.Previous == null || list.Next == null
                        || (list.Registered ? list.Owner!=expected || list.Previous.Length==0 || list.Next.Length==0 : list.Owner.Length+list.Previous.Length+list.Next.Length!=0))
                        throw Error("voice list typed owner differs");
                    if (!list.Registered) continue;
                    void Link(string alias,bool previous) {
                        if (alias == "@head" || alias == "@external") return;
                        if (!byAlias.TryGetValue(alias,out var other) || !other.LiveInstance || other.Lists == null || other.Lists.Length!=3)
                            throw Error("voice list refers to an unknown voice");
                        var linked=other.Lists[index];
                        if (linked == null || !linked.Registered || linked.Owner!=expected || (previous?linked.Next:linked.Previous)!=row.Alias)
                            throw Error("voice list reciprocal typed ownership differs");
                    }
                    Link(list.Previous,true); Link(list.Next,false);
                }
            }
            var actualGroupDefinitions=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var row in data.Voices.Where(row=>row.LiveInstance && row.Backend!=null)) {
                var body=row.Backend!;string definition=Json(body.GroupUses);
                foreach(var use in body.GroupUses) {
                    if(!sourceByOwner.TryGetValue(use.Owner,out var owner) || !owner.NativeResources[use.Role=="dry"?1:2]) throw Error("backend group has no actual source role");
                    string key=use.Owner+"/"+use.Role;
                    if(actualGroupDefinitions.TryGetValue(key,out var previous) && previous!=definition) throw Error("backend source group role aliases differ");
                    actualGroupDefinitions[key]=definition;
                    if(groupRoles.TryGetValue(key,out var alias) && groupDefinitions[alias]!=definition) throw Error("requested/backend group source alias mappings disagree");
                }
            }
            for (int index=0;index<3;index++) {
                foreach (var list in data.Voices.Where(row=>row.LiveInstance && row.Lists[index].Registered).GroupBy(row=>row.Lists[index].Owner)) {
                    var members=list.ToDictionary(row=>row.Alias,StringComparer.Ordinal);
                    if (members.Values.Any(row=>row.Lists[index].Previous=="@external" || row.Lists[index].Next=="@external")) continue;
                    var first=members.Values.Where(row=>row.Lists[index].Previous=="@head").ToArray();
                    if (first.Length!=1) throw Error("voice owner list has no unique head");
                    var visited=new HashSet<string>(StringComparer.Ordinal);string current=first[0].Alias;
                    while(current!="@head") {
                        if (!members.TryGetValue(current,out var member) || !visited.Add(current)) throw Error("voice typed owner list is cyclic");
                        current=member.Lists[index].Next;
                    }
                    if (visited.Count!=members.Count) throw Error("voice typed owner list omits a member");
                }
            }
        }
        private static IEnumerable<string> VoiceCoverageGaps(Data data)
        {
            if (data.Version < 4 && AllChannels(data).Any(channel=>channel.WeakOwner)) yield return "audio-legacy-weak-without-typed-voice";
            foreach (var row in data.Voices ?? Array.Empty<VoiceState>()) {
                if (!row.LiveInstance) continue;
                if (!row.HasFMOD || !row.PCMAvailable) yield return "audio-live-voice-PCM-unavailable";
                if(row.HasFMOD && row.Backend==null) yield return "audio-live-voice-backend-control-body-missing";
                if(row.Backend!=null) foreach(var gap in ResidentAudioBackendControl.RequiredGaps(row.Backend).Where(g=>!g.StartsWith("audio-backend-delay-",StringComparison.Ordinal))) yield return gap;
                if (row.HasDSP) yield return "audio-live-voice-DSP-unsupported";
                if (row.Reverb.HasConnectionPoint) yield return "audio-live-voice-reverb-connection-point-unsupported";
                if (row.Callback != "null") yield return "audio-live-voice-callback-unsupported";
                if (row.GroupAlias.Length != 0 && row.Groups.Length == 0) yield return "audio-live-voice-group-owner-unknown";
                if (row.LoadState != 3) yield return "audio-live-voice-handle-not-ready";
                if (row.HandleDisposed) yield return "audio-live-voice-handle-disposed";
                if (!row.SameSampleClipHandle) yield return "audio-live-voice-handle-not-current-sampleclip";
                if (row.Lists.Any(list=>list.Registered && (list.Previous=="@external" || list.Next=="@external"))) yield return "audio-voice-owner-list-has-unobserved-members";
            }
        }
    }
}
