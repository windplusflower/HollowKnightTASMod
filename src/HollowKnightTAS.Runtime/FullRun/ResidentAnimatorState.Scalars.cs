using System;
using System.Collections.Generic;
using System.Linq;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        // Stable native bytes are observations, including dormant workspaces.
        // Shape/registration validation remains immediate; unsupported typed
        // values are derived hard gaps and never acquire permission to write.
        private static IEnumerable<string> BooleanRange(byte[] bytes,int first,int count,string field,int nativeOffset)
        {
            for (int i = 0; i < count; i++) if (bytes[first+i] > 1)
                yield return "noncanonical-animator-bool:"+field+":+"+(nativeOffset+i)+"="+bytes[first+i];
        }
        private static IEnumerable<string> NativeEnum(uint value,uint maximum,string field,int offset)
        {
            if (value > maximum) yield return "unproven-animator-enum:"+field+":+"+offset+"="+value;
        }
        private static IEnumerable<string> GenericScalarGaps(Generic value)
        {
            foreach (var gap in BooleanRange(new[] {value.ObjectCurve,value.IntCurve},0,2,"GenericBinding.isPPtrCurve/isIntCurve",17)) yield return gap;
            if (value.Custom != 0) yield return "unproven-custom-animation-binding="+value.Custom;
        }
        private static IEnumerable<string> HandleScalarGaps(Handles value)
            => value.Streams.Select(s => s.Binding).Concat(value.Properties.Select(p => p.Binding)).SelectMany(GenericScalarGaps);
        private static IEnumerable<string> AvatarNodeScalarGaps(AvatarNode node)
        {
            IEnumerable<string> gaps = Array.Empty<string>();
            switch (node.Kind) {
                case AvatarKind.MotionReference:
                    gaps = node.MotionReferenceValid != true ? Array.Empty<string>()
                        : node.Fields.Length != 1 || node.Fields[0].Length != 242
                            ? new[] {"motion-reference-live-field-shape"}
                            : BooleanRange(node.Fields[0],240,2,"MotionXReference",240);
                    break;
                case AvatarKind.Input: gaps = BooleanRange(node.Fields[0],16,5,"AvatarInput",16); break;
                case AvatarKind.Workspace: gaps = BooleanRange(node.Fields[0],48,3,"AvatarWorkspace",80); break;
                case AvatarKind.Human: gaps = BooleanRange(node.Fields[1],232,3,"Human",312); break;
                case AvatarKind.Memory:
                    // PDB declares unsigned char, not C++ bool. Constructor
                    // 12ad0f writes (1,0); wider flag values remain unproven.
                    gaps = BooleanRange(node.Fields[0],164,2,"AvatarMemory.byteFlags",164).Select(gap => gap.Replace("noncanonical-animator-bool:","unproven-animator-byte-flag:")); break;
                case AvatarKind.Values: gaps = BooleanRange(node.Fields[4],0,node.Fields[4].Length,"ValueArray.bool",0); break;
                case AvatarKind.Mask: gaps = node.Fields.SelectMany((bank,i) => BooleanRange(bank,0,bank.Length,"ValueArrayMask.bank"+i,0)); break;
                case AvatarKind.NodeState: gaps = BooleanRange(node.Fields[0],0,1,"AnimationNodeState.motionReadMask",24)
                    .Concat(BooleanRange(node.Fields[1],0,1,"AnimationNodeState.humanReadMask",48)); break;
            }
            foreach (var gap in gaps) yield return gap;
        }
        private static IEnumerable<string> AvatarInputScalarGaps(EvaluationInput input)
        {
            var offsets = new[] {0,24,40,41}; var names = new[] {"hasControllerParameters","additive","applyRootMotion","absoluteRootMotion"};
            for (int i = 0; i < 4; i++) foreach (var gap in BooleanRange(input.Flags,i,1,"EvaluationInput."+names[i],offsets[i])) yield return gap;
            foreach (var gap in NativeEnum(M.U32(input.Versions,4),1,"AnimationStreamSource",84)) yield return gap;
        }
        private static IEnumerable<string> MemoryScalarGaps(ControllerMemory memory)
        {
            foreach (var gap in NativeEnum(M.U32(StateMachineInputFirstEval(memory),0),2,"ControllerMemory.FirstEval/StateMachineInput.FirstEval",40)) yield return gap;
            foreach (var gap in BooleanRange(memory.Evaluation,4,1,"ControllerMemory.InPlayback",44)) yield return gap;
            foreach (var machine in memory.Machines) foreach (var gap in BooleanRange(machine.State,96,7,"StateMachineMemory",112)) yield return gap;
            if (memory.ParameterBanks != null) foreach (var gap in BooleanRange(memory.ParameterBanks[5],0,memory.ParameterBanks[5].Length,"ControllerMemory.boolParameters",0)) yield return gap;
        }
        private static IEnumerable<string> RuntimeScalarGaps(ControllerRuntime runtime)
        {
            foreach (var gap in AnimationFlagGaps(runtime.AnimationFlags)) yield return gap;
            for (int i = 0; i < runtime.Parameters.Length; i += 12)
                if (ValueBank(M.U32(runtime.Parameters,i+4)) < 0)
                    yield return "unproven-controller-parameter-type:"+(i/12)+"="+M.U32(runtime.Parameters,i+4);
            if (runtime.Input != null) for (int i = 20; i < runtime.Input.GotoStates.Length; i += 21)
                foreach (var gap in BooleanRange(runtime.Input.GotoStates,i,1,"GotoStateInfo["+(i/21)+"].FixedTransition",20)) yield return gap;
            var work = runtime.Workspace; if (work == null) yield break;
            foreach (var gap in BooleanRange(work.TriggerReset,0,work.TriggerReset.Length,"ControllerWorkspace.TriggerReset",0)) yield return gap;
            foreach (var machine in work.Machines) {
                foreach (var gap in BooleanRange(machine.OutputFlags,0,4,"StateMachineOutput",24)) yield return gap;
                // EvaluateStateMachine writes combined event bits (for example
                // Update|Move|IK); FireBehaviours intersects them with the stage
                // mask. Invalid128 is the separate constructor/reset sentinel.
                for (int i = 0; i < 12; i += 4) {
                    var message = M.U32(machine.Messages,i);
                    if (message != 128 && (message == 0 || (message & ~127U) != 0))
                        yield return "unproven-animator-enum:StateMachineMessage:+"+i+"="+message;
                }
                if (machine.Blends != null) foreach (var layer in machine.Blends)
                    for (int i = 12; i < layer.Nodes.Length; i += 21)
                        foreach (var gap in BooleanRange(layer.Nodes,i,1,"BlendTreeNodeOutput["+(i/21)+"].Mirror",12)) yield return gap;
            }
        }
        private static IEnumerable<string> GraphScalarGaps(Topology graph)
        {
            foreach (var gap in BooleanRange(new[] {graph.RecursingRaw},0,1,"PlayableGraph.Recursing",184)) yield return gap;
            if (graph.RecursingRaw != 0) yield return "native-playable-graph-is-recursing";
            foreach (var node in graph.Nodes) {
                if (node.Pose != null) foreach (var gap in BooleanRange(node.Pose.Flags,0,3,"AnimationPosePlayable",280)) yield return gap;
                if (node.Mixer != null) {
                    var subtype = node.Mixer.Subtype;
                    if (node.Vtable == 0x15d8aa8) {
                        foreach (var gap in BooleanRange(subtype,0,2,"AnimationBlendTreePlayable",360)) yield return gap;
                        int children = M.I32(subtype,6);
                        if (children < 0 || children > (node.Ports?.Inputs.Length ?? 0)) yield return "blend-tree-child-count-outside-inputs";
                    } else {
                        foreach (var gap in BooleanRange(subtype,4,1,"AnimationStateMachineMixerPlayable",364)) yield return gap;
                        int interrupted = M.I32(subtype,0);
                        if (interrupted < 0 || interrupted >= (node.Ports?.Inputs.Length ?? 0)) yield return "state-mixer-interrupted-pose-index-outside-inputs";
                    }
                }
                foreach (var gap in ClockScalarGaps(node.Clock)) yield return gap;
                if (AnimationFlagBytes(node.Vtable) != 0) foreach (var gap in AnimationFlagGaps(node.AnimationFlags)) yield return gap;
                foreach (var gap in NativeEnum(M.U32(node.Modes,8),2,"Playable.WrapMode",64)) yield return gap;
                foreach (var gap in NativeEnum(unchecked((uint)node.PlayerType),3,"DirectorPlayerType",24)) yield return gap;
            }
        }
        private static IEnumerable<string> ClockScalarGaps(byte[] clock)
            => NativeEnum(M.U32(clock,52),2,"Playable.LocalPlayState",132);
        private static IEnumerable<string> ScalarGaps(Data data)
        {
            foreach (var row in data.Rows) {
                foreach (var i in new[] {0,1,2}) foreach (var gap in BooleanRange(row.Fields[i],0,row.Fields[i].Length,"Animator",Offsets[i])) yield return gap;
                foreach (var gap in BooleanRange(row.Fields[4],144,4,"Animator",1144)) yield return gap;
                foreach (var gap in BooleanRange(row.Fields[4],152,5,"Animator",1152)) yield return gap;
                foreach (var gap in BooleanRange(row.Fields[7],12,2,"Animator",1484)) yield return gap;
                foreach (var gap in NativeEnum(M.U32(row.Fields[3],0),2,"Animator.CullingMode",396)) yield return gap;
                foreach (var gap in NativeEnum(M.U32(row.Fields[3],4),2,"Animator.UpdateMode",400)) yield return gap;
                foreach (var gap in NativeEnum(M.U32(row.Fields[7],0),2,"Animator.RecorderMode",1472)) yield return gap;
                foreach (var input in row.Inputs) foreach (var gap in AvatarInputScalarGaps(input)) yield return gap;
                foreach (var set in row.Datasets) foreach (var gap in BooleanRange(new[] {set.OwnsAvatar,set.Initialized},0,2,"AvatarDataSet",56)) yield return gap;
            }
            foreach (var node in data.AvatarNodes) foreach (var gap in AvatarNodeScalarGaps(node)) yield return gap;
            foreach (var player in data.Playables) {
                foreach (var gap in ClockScalarGaps(player.Clock)) yield return gap;
                if (player.Memory != null) foreach (var gap in MemoryScalarGaps(player.Memory)) yield return gap;
                foreach (var gap in RuntimeScalarGaps(player.Runtime)) yield return gap;
            }
            foreach (var memory in data.ExtraMemories) foreach (var gap in MemoryScalarGaps(memory)) yield return gap;
            foreach (var gap in BooleanRange(data.Registry.Queues.Flags,1,6,"DirectorManager",985)) yield return gap;
        }
    }
}
