using System;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Exact scalar queue data and logical relations, with no process pointer
    // or GC handle. It is not a native queue import or a qualification claim.
    internal sealed class CanonicalSchedulerCarry
    {
        internal const string Root = "D/NativeScheduler";
        internal static readonly Type[] DataTypes = { typeof(CanonicalSchedulerCarry), typeof(Callback), typeof(Callback[]), typeof(Coroutine), typeof(Coroutine[]) };
        public int Version = 1;
        public long SourceNative;
        public uint Timestamp;
        public Callback[] Callbacks = Array.Empty<Callback>();
        public Coroutine[] Coroutines = Array.Empty<Coroutine>();
        internal sealed class Callback
        {
            public int Order, Rate, Mode;
            public string Owner = "", Call = "", Cleanup = "", Coroutine = "";
            public long WakeTimeBits, Frame;
            public byte Repeat;
            public uint TimestampAge;
            public bool UserDataPresent;
        }
        internal sealed class Coroutine
        {
            public string Identity = "", Family = "", WaitingFor = "", ContinueWhenFinished = "";
            public bool Done, EnumeratorBased, HasAsyncOperation;
        }
        internal void RequireEpoch(CanonicalInputEpoch epoch)
        {
            epoch.Validate(SourceNative);
            if (Timestamp != epoch.SchedulerTimestamp) throw new InvalidDataException("Canonical scheduler queue and input epoch timestamp differ.");
            Validate();
        }
        internal void Validate()
        {
            if (Version != 1 || SourceNative < 0 || Callbacks == null || Coroutines == null || Callbacks.Length > 4096 || Coroutines.Length > 4096
                || Coroutines.Any(item => item == null) || Coroutines.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() != Coroutines.Length)
                throw Invalid("queue bounds or identities");
            foreach (var coroutine in Coroutines) {
                Text(coroutine.Identity); Text(coroutine.Family); Text(coroutine.WaitingFor); Text(coroutine.ContinueWhenFinished);
                if (coroutine.Done || coroutine.HasAsyncOperation
                    || coroutine.Family != "serial-boundary" && coroutine.Family != "hero-invulnerability") throw Invalid("unregistered coroutine state");
                foreach (var relation in new[] { coroutine.WaitingFor, coroutine.ContinueWhenFinished })
                    if (relation != "none" && !Coroutines.Any(other => other.Identity == relation)) throw Invalid("foreign coroutine relation");
            }
            for (var index = 0; index < Callbacks.Length; index++) {
                var callback = Callbacks[index];
                if (callback == null || callback.Order != index || double.IsNaN(BitConverter.Int64BitsToDouble(callback.WakeTimeBits))
                    || double.IsInfinity(BitConverter.Int64BitsToDouble(callback.WakeTimeBits))) throw Invalid("callback order or deadline");
                Text(callback.Owner); Text(callback.Call); Text(callback.Cleanup);
                if (callback.Coroutine == null || callback.Coroutine.Length > 16384 || callback.Coroutine.IndexOf('\0') >= 0) throw Invalid("coroutine callback text");
                if (callback.Coroutine.Length == 0) {
                    if (callback.Call != "UnityPlayer:8ac680" || callback.Cleanup != "none" || callback.UserDataPresent || callback.Frame != -1
                        || callback.Rate != 0 || callback.Repeat != 0 || callback.Mode != 7 || callback.TimestampAge != 0)
                        throw Invalid("unregistered canonical start callback");
                }
                else if (!callback.UserDataPresent || callback.Call != "UnityPlayer:8a6c70" || callback.Cleanup != "UnityPlayer:8a6950"
                    || !Coroutines.Any(item => item.Identity == callback.Coroutine)) throw Invalid("foreign coroutine callback");
            }
        }
        private static void Text(string value)
        {
            if (value == null || value.Length == 0 || value.Length > 16384 || value.IndexOf('\0') >= 0) throw Invalid("descriptor text");
        }
        private static InvalidDataException Invalid(string detail) => new InvalidDataException("Canonical scheduler carry differs: " + detail);
    }
}
