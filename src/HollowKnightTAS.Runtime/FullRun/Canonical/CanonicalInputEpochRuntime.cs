using System;
using System.IO;
using System.Reflection;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalInputEpochRuntime
    {
        private static readonly FieldInfo Initial = Field("initialTime", typeof(float)), Current = Field("currentTime", typeof(float)),
            Last = Field("lastUpdateTime", typeof(float)), Tick = Field("currentTick", typeof(ulong));
        private static FieldInfo Field(string name, Type expected)
        {
            var field = typeof(InputManager).GetField(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (field == null || field.FieldType != expected) throw new InvalidDataException("Pinned InControl epoch layout differs: " + name);
            return field;
        }
        internal static CanonicalInputEpoch Capture(long? actualInputOrigin, long nativeFrame, CanonicalInputEpoch? origin = null)
        {
            var tick = (ulong)Tick.GetValue(null);
            var initial = (float)Initial.GetValue(null); var current = (float)Current.GetValue(null); var last = (float)Last.GetValue(null);
            var stamp = new LoadingNativeSchedulerDigest().CaptureManagerTimestamp();
            var result = CanonicalInputEpoch.Capture(actualInputOrigin, nativeFrame, tick, initial, current, last, stamp);
            if (origin != null) result.BindControlOrigin(origin);
            if ((ulong)Tick.GetValue(null) != tick || BitConverter.ToInt32(BitConverter.GetBytes((float)Initial.GetValue(null)), 0) != result.InitialTimeBits
                || BitConverter.ToInt32(BitConverter.GetBytes((float)Current.GetValue(null)), 0) != result.CurrentTimeBits
                || BitConverter.ToInt32(BitConverter.GetBytes((float)Last.GetValue(null)), 0) != result.LastUpdateTimeBits)
                throw new InvalidOperationException("InControl epoch changed during capture.");
            return result;
        }
    }
}
