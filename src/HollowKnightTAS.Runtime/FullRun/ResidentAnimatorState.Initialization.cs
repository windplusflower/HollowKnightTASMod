using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static partial class ResidentAnimatorState
    {
        // Pinned PDB primary vftables and complete object extents:
        // AnimationPlayable 272, AnimationMixerPlayable 360,
        // AnimationLayerMixerPlayable 368, AnimatorControllerPlayable 456.
        // Playable (vftable16a23b8) is only192 bytes: never read its +256.
        private static int AnimationFlagBytes(int vtable)
            => vtable == 0x15d91f8 || vtable == 0x15d89d8 || vtable == 0x15d9380 || vtable == PlayableVtable
                || vtable == 0x15d8aa8 || vtable == 0x15d8b80 || vtable == 0x15d9608 || vtable == 0x15d97a8 ? 5 : 0;
        // Native factories reserve no custom-job bytes for these complete
        // object types. Their nonnull job-data slot points exactly one past
        // the object; a reflection descriptor would give it another meaning.
        private static int EmptyInlineJobDataSize(int vtable)
        {
            switch (vtable)
            {
                case 0x15d9380: return 368; // LayerMixer
                case 0x15d97a8: return 368; // StateMachineMixer
                case 0x15d8aa8: return 384; // BlendTree
                case 0x15d9608: return 288; // Pose
                case 0x15d8b80: return 512; // Clip
                case PlayableVtable: return 456; // Controller
                default: return 0;
            }
        }

        private static int? CaptureEmptyInlineJobDataOffset(int vtable, IntPtr owner, IntPtr reflection, IntPtr data)
        {
            int size = EmptyInlineJobDataSize(vtable);
            return size != 0 && reflection == IntPtr.Zero && data == IntPtr.Add(owner, size) ? size : (int?)null;
        }

        private static bool KnownEmptyInlineJobData(Node node)
            => node.EmptyInlineJobDataOffset.HasValue && node.JobData && !node.JobReflection
                && node.EmptyInlineJobDataOffset.Value == EmptyInlineJobDataSize(node.Vtable)
                && node.EmptyInlineJobDataOffset.Value != 0;

        private static void ValidateAnimationFlags(byte[] flags)
        {
            if (flags == null || flags.Length != 5) throw Error("AnimationPlayable initialization flag extent differs");
        }
        private static IEnumerable<string> AnimationFlagGaps(byte[] flags)
        {
            var names = new[] {"NeedsBindingRebuilded","NeedsPreProcess","NeedsAllocateBindings","ChildsNeedsAllocateBindings","BindingsAllocated"};
            for (int i = 0; i < 5; i++) {
                foreach (var gap in BooleanRange(flags,i,1,"AnimationPlayable."+names[i],256+i)) yield return gap;
                // Allocation/rebinding requests require their native owner
                // transaction, not a scalar reset or a replay of flag bits.
                if ((i == 0 || i == 2 || i == 3) && flags[i] != 0)
                    yield return "animation-playable-pending-binding-lifecycle:"+names[i]+":+"+(256+i)+"="+flags[i];
            }
        }
        private static void PrepareAnimationFlags(byte[] source,byte[] before,IntPtr address,List<PendingWrite> writes)
        {
            ValidateAnimationFlags(source); ValidateAnimationFlags(before);
            Reject(AnimationFlagGaps(source).Concat(AnimationFlagGaps(before)));
            // Allocated backing is a read-only dependency of this operation.
            // Only its real allocation owner may change it. PreProcess+257
            // is D (UpdateGraph1171ec clears it;11749e propagates its result).
            if (source[4] != before[4]) throw Error("AnimationPlayable allocated-binding owner differs");
            writes.Add(new PendingWrite(address,before,source));
        }
        private static byte[] StateMachineInputFirstEval(ControllerMemory owner)
        {
            ValidateMemory(owner);
            // UpdateGraph11738c reads ControllerMemory+40;117392 stores it
            // into stack StateMachineInput+48;117468 passes that stack object
            // to EvaluateStateMachine. There is no persisted ControllerInput
            // +48 field: ControllerInput is32 bytes. This alias is captured,
            // restored and read back solely through ControllerMemory.Evaluation.
            return owner.Evaluation.Take(4).ToArray();
        }
    }
}
