using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAudioState
    {
        // Failure-only inventory. None of these DTOs enter Data, equality or admission.
        private sealed class ChannelDiagnostic
        {
            public string Alias = "", Failure = "", ClipIdentity = "", Callback = "", Group = "";
            public bool WeakOwner, LiveInstance, HasFMOD, HasDSP;
            public SoundHandleDiagnostic? SoundHandle;
            public VoiceReverb Reverb = new VoiceReverb();
            public ResidentAudioBackendControl.Data? Backend;
            public string BackendFailure = "";
            public ResidentAudioBackendControl.DelayDiagnostic? BackendDelay;
            public bool[] ListRegistered = Array.Empty<bool>();
            public byte[][] ParameterScalars = Array.Empty<byte[]>();
            public byte[] VolumePitchPanBits = Array.Empty<byte>(), PauseBytes = Array.Empty<byte>(), AttenuationBits = Array.Empty<byte>(), Flags = Array.Empty<byte>();
        }
        private sealed class SoundHandleDiagnostic
        {
            public int LoadState;
            public uint Mode, Flags;
            public bool HasFMODSound, HasFMODFSB;
            public string LoadStateMeaning = "unknown", Streaming = "unknown", SampleClipHandleAlias = "unknown";
            public ulong ResourceOffset, ResourceSize;
        }
        private static SoundHandleDiagnostic DecodeSoundHandleDiagnostic(byte[] body)
        {
            if (body.Length != 208) throw Error("audio diagnostic sound handle extent differs");
            // PDB StreamedResource is 56B: source string40, offset uint64, size uint64.
            // A nonempty resource is not by itself proof of FMOD streaming mode.
            int loadState = M.I32(body,128);
            return new SoundHandleDiagnostic {
                LoadState = loadState, LoadStateMeaning = loadState == 3 ? "ready" : "unknown",
                Mode = BitConverter.ToUInt32(body,132), Flags = BitConverter.ToUInt32(body,144),
                HasFMODSound = M.Ptr(body,120) != IntPtr.Zero, HasFMODFSB = M.Ptr(body,112) != IntPtr.Zero,
                ResourceOffset = M.U64(body,192), ResourceSize = M.U64(body,200)
            };
        }
        private sealed class ChannelUseDiagnostic
        {
            public string Owner = "", Role = "", Alias = "", GroupRelativeToSource = "", Failure = "";
        }
        private sealed class AudioChannelDiagnostics
        {
            public string Note = "Main-thread buffered parameters only; position_pcm is a buffered request, not the live playback cursor.", Failure = "";
            public bool Truncated;
            public List<ChannelDiagnostic> Channels = new List<ChannelDiagnostic>();
            public List<ChannelUseDiagnostic> Uses = new List<ChannelUseDiagnostic>();
            public ResidentAudioPositionProbe.Data PositionProbe = new ResidentAudioPositionProbe.Data();
        }
        private sealed class DiagnosticMemory
        {
            private readonly Dictionary<IntPtr,byte[]> reads = new Dictionary<IntPtr,byte[]>();
            private int total;
            internal byte[] Read(IntPtr pointer,int count)
            {
                if (reads.TryGetValue(pointer,out var bytes) && bytes.Length >= count) return M.Slice(bytes,0,count);
                total = checked(total+count);
                if (count < 0 || total > 4*1024*1024 || reads.Count >= 32768) throw Error("audio diagnostic read bound exceeded");
                bytes = M.Read(pointer,count); reads[pointer] = bytes; return bytes;
            }
        }
        private static ChannelDiagnostic DecodeChannelDiagnostic(byte[] body,string alias)
        {
            if (body.Length != 360) throw Error("audio diagnostic instance extent differs");
            var result = new ChannelDiagnostic { Alias = alias, WeakOwner = true, LiveInstance = true,
                HasFMOD = M.Ptr(body,192) != IntPtr.Zero, HasDSP = M.Ptr(body,336) != IntPtr.Zero,
                // Exclude callback/group pointers, all alignment padding and backend objects.
                ParameterScalars = new[] {M.Slice(body,8,60),M.Slice(body,88,40),M.Slice(body,152,2),new[] {body[154],body[155],body[156],(byte)(body[160]&0x3f)}},
                Reverb = new VoiceReverb {Direct=M.I32(body,128),Room=M.I32(body,132),Flags=BitConverter.ToUInt32(body,136),HasConnectionPoint=M.Ptr(body,144)!=IntPtr.Zero},
                VolumePitchPanBits = M.Slice(body,280,48), PauseBytes = M.Slice(body,328,3),
                AttenuationBits = M.Slice(body,344,4), Flags = M.Slice(body,348,4),
                ListRegistered = new[] {208,232,256}.Select(offset => M.Ptr(body,offset) != IntPtr.Zero && M.Ptr(body,offset+8) != IntPtr.Zero).ToArray()
            };
            return result;
        }
        private static string DiagnosticCallback(IntPtr callback,IntPtr engine,int engineSize)
        {
            if (callback == IntPtr.Zero) return "null";
            long rva = callback.ToInt64()-engine.ToInt64();
            return rva >= 0 && rva < engineSize ? "UnityPlayer+0x"+rva.ToString("x") : "unknown-present";
        }
        private static string DiagnosticGroup(IntPtr group,IntPtr dry,IntPtr wet)
            => group == IntPtr.Zero ? "null" : group == dry && group == wet ? "source-dry-and-wet" : group == dry ? "source-dry" : group == wet ? "source-wet" : "other-present";
        private static void AttachBackendDiagnostic(Reader reader,ChannelDiagnostic row,string alias)
        {
            if(reader.BackendReceipts.TryGetValue(alias,out var backend)) row.Backend=backend;
            if(reader.BackendFailures.TryGetValue(alias,out var failure)) row.BackendFailure=failure;
            if(reader.BackendDelays.TryGetValue(alias,out var delay)) row.BackendDelay=delay;
        }
        private static void RecordChannelDiagnostics(Reader reader,Data data)
        {
            var evidence = new AudioChannelDiagnostics();
            var positions = new Dictionary<string,ResidentAudioPositionProbe.Candidate>(StringComparer.Ordinal);
            try {
                var memory = new DiagnosticMemory();
                var engine = LoadingEngineClockProbe.RequirePinnedEngine();
                var aliases = reader.DiagnosticAliases.ToDictionary(pair => pair.Value,pair => pair.Key,StringComparer.Ordinal);
                var bodies = new Dictionary<string,byte[]>(StringComparer.Ordinal);
                var instances = new Dictionary<string,long>(StringComparer.Ordinal);
                foreach (var source in data.Sources) {
                    byte[]? sourceBody = null;
                    IEnumerable<(Channel Channel,string Role)> Uses() {
                        yield return (source.Main,"main");
                        for (int i=0;i<source.Cached.Length;i++) yield return (source.Cached[i],"cached/"+i);
                        for (int i=0;i<source.Providers.Length;i++) yield return (source.Providers[i],"provider/"+i);
                        for (int i=0;i<source.OneShots.Length;i++) yield return (source.OneShots[i],"one-shot/"+i);
                    }
                    foreach (var use in Uses()) {
                        if (!use.Channel.WeakOwner) continue;
                        if (evidence.Uses.Count >= 8192 || evidence.Channels.Count >= 4096) { evidence.Truncated = true; break; }
                        var association = new ChannelUseDiagnostic {Owner=source.Owner,Role=use.Role,Alias=use.Channel.Alias}; evidence.Uses.Add(association);
                        try {
                            if (!bodies.ContainsKey(use.Channel.Alias)) {
                                var row = new ChannelDiagnostic {Alias=use.Channel.Alias,WeakOwner=true}; evidence.Channels.Add(row);
                                var weak = memory.Read(aliases[use.Channel.Alias],24); var instance = M.Ptr(weak,16);
                                if (instance == IntPtr.Zero) { bodies.Add(use.Channel.Alias,Array.Empty<byte>()); continue; }
                                var body = memory.Read(instance,360); bodies.Add(use.Channel.Alias,body);
                                instances.Add(use.Channel.Alias,instance.ToInt64());
                                var decoded = DecodeChannelDiagnostic(body,use.Channel.Alias);
                                evidence.Channels[evidence.Channels.Count-1] = row = decoded;
                                AttachBackendDiagnostic(reader,row,use.Channel.Alias);
                                row.Callback = DiagnosticCallback(M.Ptr(body,72),engine.BaseAddress,engine.ModuleMemorySize);
                                row.Group = M.Ptr(body,80) == IntPtr.Zero ? "null" : "present";
                                try {
                                    var handleWeak = M.Ptr(body,200);
                                    var handle = handleWeak == IntPtr.Zero ? IntPtr.Zero : M.Ptr(memory.Read(handleWeak,24),16);
                                    if (handle == IntPtr.Zero) row.ClipIdentity = handleWeak == IntPtr.Zero ? "sound-handle-null" : "sound-handle-expired";
                                    else {
                                        var handleBody = memory.Read(handle,208);
                                        row.SoundHandle = DecodeSoundHandleDiagnostic(handleBody);
                                        var clipWeak = M.Ptr(handleBody,136);
                                        var clip = clipWeak == IntPtr.Zero ? IntPtr.Zero : M.Ptr(memory.Read(clipWeak,24),16);
                                        if (clip == IntPtr.Zero) row.ClipIdentity = clipWeak == IntPtr.Zero ? "sample-clip-null" : "sample-clip-expired";
                                        else {
                                            int id = M.I32(memory.Read(clip,16),8);
                                            var registered = ResidentLoadedAssetIndex.RegisteredPointer(id,out var verify);
                                            if (registered != clip || !ResidentNativeAssetReference.IsFamily(clip,typeof(AudioClip))) throw Error("channel parent SampleClip is not a registered AudioClip");
                                            verify(); row.ClipIdentity = "registered-AudioClip-instance/"+id;
                                            var sampleHandleWeak = M.Ptr(memory.Read(clip,88),80);
                                            var sampleHandle = sampleHandleWeak == IntPtr.Zero ? IntPtr.Zero : M.Ptr(memory.Read(sampleHandleWeak,24),16);
                                            row.SoundHandle.SampleClipHandleAlias = sampleHandleWeak == IntPtr.Zero ? "null"
                                                : sampleHandle == IntPtr.Zero ? "expired" : sampleHandle == handle ? "same-instance" : "different-instance";
                                        }
                                    }
                                } catch (Exception error) { row.Failure = "clip: "+error.GetType().Name+": "+error.Message; }
                            }
                            var existing = bodies[use.Channel.Alias];
                            if (existing.Length != 0) {
                                if ((use.Role=="main" || use.Role.StartsWith("one-shot/",StringComparison.Ordinal)) && !positions.ContainsKey(use.Channel.Alias))
                                    positions.Add(use.Channel.Alias,new ResidentAudioPositionProbe.Candidate {Alias=use.Channel.Alias,Weak=aliases[use.Channel.Alias].ToInt64(),Instance=instances[use.Channel.Alias]});
                                if (sourceBody == null) sourceBody = memory.Read(M.Owner(reader.Directory.Resolve(source.Owner)),1016);
                                association.GroupRelativeToSource = DiagnosticGroup(M.Ptr(existing,80),M.Ptr(sourceBody,896),M.Ptr(sourceBody,904));
                            }
                        } catch (Exception error) { association.Failure = error.GetType().Name+": "+error.Message; }
                    }
                    if (evidence.Truncated) break;
                }
            } catch (Exception error) { evidence.Failure = error.GetType().Name+": "+error.Message; }
            try { evidence.PositionProbe = ResidentAudioPositionProbe.Capture(positions.Values.ToArray()); }
            catch (Exception error) { evidence.PositionProbe.Reason = "PCM observation unavailable: "+error.GetType().Name+": "+error.Message; }
            try { ResidentFailureEvidence.RecordDomain("AudioChannels",evidence); }
            catch (Exception error) { ResidentFailureEvidence.Mark("audio channel diagnostics unavailable "+error.GetType().Name); }
        }
    }
}

