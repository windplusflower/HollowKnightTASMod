using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Newtonsoft.Json;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentMonoNativeState
    {
        // This is ObjectGUIState/IDList, not the global 648B GUIState. Empty
        // storage and absent storage have identical logical semantics. Native
        // pointers, capacity and the GUI allocator seed remain target-local.
        internal sealed class GuiData
        {
            [JsonProperty(Required=Required.Always)] public int Version = 1;
            [JsonProperty(Required=Required.Always)] public string State = "empty";
            [JsonProperty(Required=Required.Always)] public string[] RequiredGaps = Array.Empty<string>();
        }
        internal sealed class GuiIdle
        {
            internal bool Present, EternalPresent, CurrentObject, DomainReloading;
            internal int Depth, Keyboard, HotControl, UniqueId;
        }
        private sealed class GuiContext
        {
            internal GuiIdle Value = new GuiIdle();
            internal IntPtr Pointer, Eternal;
            internal byte[] Keyboard = Array.Empty<byte>(), Object = Array.Empty<byte>(), Depth = Array.Empty<byte>(), EternalBytes = Array.Empty<byte>();
            internal void RequireUnchanged()
            {
                if (ReadGuiPointer()!=Pointer) throw Error("GUI context identity changed during Mono capture");
                if (Pointer == IntPtr.Zero) return;
                if (!M.Read(Pointer,4).SequenceEqual(Keyboard) || !M.Read(IntPtr.Add(Pointer,152),8).SequenceEqual(Object)
                    || !M.Read(IntPtr.Add(Pointer,624),4).SequenceEqual(Depth) || M.Ptr(IntPtr.Add(Pointer,528))!=Eternal
                    || Eternal!=IntPtr.Zero && !M.Read(Eternal,12).SequenceEqual(EternalBytes))
                    throw Error("GUI idle/focus witness changed during Mono capture");
            }
        }
        private sealed class GuiIdDiagnostic
        {
            public int Hint, Value;
            public byte Keyboard;
            public uint[] Rect = Array.Empty<uint>();
        }
        private sealed class GuiDiagnostic
        {
            public string Owner = "";
            public bool PhysicalPresence, GlobalPresence, EternalPresence, CurrentObject, DomainReloading, HasKeyboard;
            public int Depth, Keyboard, HotControl, UniqueId, Index, Label;
            public ulong Count, Capacity;
            public bool Borrowed, Truncated;
            public int[] Controls = Array.Empty<int>();
            public GuiIdDiagnostic[] Ids = Array.Empty<GuiIdDiagnostic>();
            public string[] Reasons = Array.Empty<string>();
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr GuiStatePointer();
        private static IntPtr ReadGuiPointer()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
            // The complete fixed function only reads gStackPtr/display stack/
            // gGUIState. Unlike GetObjectGUIState it never allocates or calls OnGUI.
            ResidentLoadedAssetIndex.RequireCode(engine,0x28eb60,51,"bc08aa69615c3bf1d4e44cd124a57d2d2ab24c2c22f2aeeb0937735d7034bac1");
            var getter = (GuiStatePointer)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine,0x28eb60),typeof(GuiStatePointer));
            return getter();
        }
        private static GuiContext ReadGuiContext()
        {
            var context = new GuiContext {Pointer = ReadGuiPointer()};
            if (context.Pointer == IntPtr.Zero) return context;
            context.Value.Present = true;
            context.Keyboard = M.Read(context.Pointer,4);
            context.Object = M.Read(IntPtr.Add(context.Pointer,152),8);
            context.Depth = M.Read(IntPtr.Add(context.Pointer,624),4);
            context.Eternal = M.Ptr(IntPtr.Add(context.Pointer,528));
            context.Value.Keyboard = M.I32(context.Keyboard,0);
            context.Value.CurrentObject = M.Ptr(context.Object,0)!=IntPtr.Zero;
            context.Value.Depth = M.I32(context.Depth,0);
            context.Value.EternalPresent = context.Eternal!=IntPtr.Zero;
            if (context.Eternal!=IntPtr.Zero) {
                context.EternalBytes = M.Read(context.Eternal,12);
                if (context.EternalBytes[8]>1 || context.EternalBytes[9]>1) throw Error("GUI eternal flag ABI differs");
                context.Value.UniqueId = M.I32(context.EternalBytes,0);
                context.Value.HotControl = M.I32(context.EternalBytes,4);
                context.Value.DomainReloading = context.EternalBytes[9]!=0;
            }
            return context;
        }
        internal static GuiData ProjectGuiState(byte[]? header,GuiIdle idle)
        {
            if (idle==null) throw Error("GUI idle witness is absent");
            var reasons = new List<string>();
            if (idle.Depth!=0 || idle.CurrentObject) reasons.Add("mono/gui-dispatch-active");
            if (idle.Present && !idle.EternalPresent) reasons.Add("mono/gui-eternal-context-unbound");
            if (idle.Keyboard!=0 || idle.HotControl!=0) reasons.Add("mono/gui-keyboard-or-hotcontrol-uncovered");
            if (idle.DomainReloading) reasons.Add("mono/gui-domain-reloading");
            if (header!=null) {
                if (header.Length!=72 || header[28]>1) throw Error("ObjectGUIState IDList ABI differs");
                ulong count = M.U64(header,48), capacity = M.U64(header,56)>>1;
                if (count>capacity || capacity!=0 && M.Ptr(header,32)==IntPtr.Zero || M.I32(header,40)!=74)
                    throw Error("ObjectGUIState IDList storage extent or label differs");
                int status = M.I32(header,0);
                if (status<0 || status>3) throw Error("ObjectGUIState search enum differs");
                if (count!=0) reasons.Add("mono/gui-control-id-aliases-uncovered");
                if (M.I32(header,64)!=0) reasons.Add("mono/gui-control-id-cursor-active");
                int sentinel = status==0 ? 0 : -1;
                if (header[28]!=0 || status>1 || Enumerable.Range(1,4).Any(index=>M.I32(header,index*4)!=sentinel)
                    || M.I32(header,20)!=-1 || M.I32(header,24)!=-1)
                    reasons.Add("mono/gui-control-id-focus-reference-uncovered");
            }
            var gaps = reasons.Distinct(StringComparer.Ordinal).OrderBy(value=>value,StringComparer.Ordinal).ToArray();
            return new GuiData {State=gaps.Length==0 ? "empty" : "uncovered",RequiredGaps=gaps};
        }
        private static void ValidateGui(GuiData value)
        {
            if (value==null || value.Version!=1 || value.State!="empty" && value.State!="uncovered"
                || value.RequiredGaps==null || value.RequiredGaps.Length>8
                || value.RequiredGaps.Any(gap=>string.IsNullOrEmpty(gap) || gap.Length>256)
                || !value.RequiredGaps.SequenceEqual(value.RequiredGaps.Distinct(StringComparer.Ordinal).OrderBy(gap=>gap,StringComparer.Ordinal))
                || (value.State=="empty")!=(value.RequiredGaps.Length==0)) throw Error("typed GUI semantic proof differs");
        }
        private static GuiData CaptureGuiState(string owner,IntPtr pointer,GuiContext context,List<GuiDiagnostic> diagnostics,ref int remainingBytes)
        {
            byte[]? header = pointer==IntPtr.Zero ? null : M.Read(pointer,72);
            GuiData data;
            try { data = ProjectGuiState(header,context.Value); }
            catch (Exception error) {
                if (ResidentFailureEvidence.Collecting) ResidentFailureEvidence.RecordDomain("MonoGui",new {
                    Owner=owner,PhysicalPresence=header!=null,Reason=error.GetType().Name+": "+error.Message,
                    Controls=header==null ? Array.Empty<int>() : Enumerable.Range(0,7).Select(index=>M.I32(header,index*4)).ToArray(),
                    Count=header==null ? 0UL : M.U64(header,48),Index=header==null ? 0 : M.I32(header,64)
                });
                throw;
            }
            if (ResidentFailureEvidence.Collecting && data.RequiredGaps.Length!=0 && diagnostics.Count<16) {
                try {
                    var row = new GuiDiagnostic {Owner=owner,PhysicalPresence=header!=null,GlobalPresence=context.Value.Present,
                        EternalPresence=context.Value.EternalPresent,CurrentObject=context.Value.CurrentObject,Depth=context.Value.Depth,
                        Keyboard=context.Value.Keyboard,HotControl=context.Value.HotControl,UniqueId=context.Value.UniqueId,
                        DomainReloading=context.Value.DomainReloading,Reasons=data.RequiredGaps};
                    if (header!=null) {
                        row.Controls = Enumerable.Range(0,7).Select(index=>M.I32(header,index*4)).ToArray();
                        row.HasKeyboard = header[28]!=0;
                        row.Index = M.I32(header,64);
                        row.Label = M.I32(header,40);
                        row.Count = M.U64(header,48);
                        row.Capacity = M.U64(header,56)>>1;
                        row.Borrowed = (M.U64(header,56)&1)!=0;
                        int count = (int)Math.Min(row.Count,(ulong)(remainingBytes/28));
                        var entries = M.Read(M.Ptr(header,32),checked(count*28));
                        remainingBytes -= entries.Length;
                        row.Truncated = (ulong)count!=row.Count;
                        row.Ids = Enumerable.Range(0,count).Select(index=>new GuiIdDiagnostic {
                            Hint=M.I32(entries,index*28),Value=M.I32(entries,index*28+4),Keyboard=entries[index*28+8],
                            Rect=Enumerable.Range(0,4).Select(slot=>M.U32(entries,index*28+12+slot*4)).ToArray()
                        }).ToArray();
                    }
                    diagnostics.Add(row);
                } catch (Exception error) { ResidentFailureEvidence.Mark("GUI IDList diagnostic failure "+error.GetType().Name); }
            }
            if (header!=null && !M.Read(pointer,72).SequenceEqual(header)) throw Error("ObjectGUIState changed during Mono capture");
            return data;
        }
    }
}
