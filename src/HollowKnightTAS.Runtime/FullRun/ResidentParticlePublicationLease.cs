using System;
using System.IO;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Local admission only. Token, native thread IDs, wall time and the
    // monotonic publication counter must never become source gameplay D.
    internal sealed class ResidentParticlePublicationLease : IDisposable
    {
        [ThreadStatic] private static ResidentParticlePublicationLease? current;
        private readonly ulong token, started, publications, wallLimit;
        private readonly uint thread;
        private readonly Func<byte[]> status;
        private ResidentParticlePublicationLease(ulong token, ulong started, uint thread, Func<byte[]> status, ulong wallLimit = 9000)
        {
            this.token=token; this.started=started; this.thread=thread; this.status=status; this.wallLimit=wallLimit;
            publications=Read(status(),token,started,thread,ResidentNativeMemory.WallMilliseconds(),wallLimit);
        }
        internal static ResidentParticlePublicationLease Open(ulong token, ulong started, uint thread, Func<byte[]> status, ulong wallLimit = 9000)
        {
            if (current != null) throw Error("nested publication lease");
            return current=new ResidentParticlePublicationLease(token,started,thread,status,wallLimit);
        }
        internal static bool HasVerifiedPublicationFence()
        {
            var proof=current; if (proof == null) return false;
            proof.Require(); return true;
        }
        internal void Require()
        {
            if (!ReferenceEquals(current,this) || Read(status(),token,started,thread,ResidentNativeMemory.WallMilliseconds(),wallLimit) != publications)
                throw Error("publication counter or thread ownership changed inside the boundary");
        }
        internal static ulong Read(byte[] b,ulong token,ulong started,uint thread,ulong now,ulong wallLimit = 9000)
        {
            if (b == null || b.Length != 64 || BitConverter.ToUInt32(b,0) != 0x31465052 || BitConverter.ToUInt32(b,4) != 1
                || BitConverter.ToUInt32(b,8) != 64 || BitConverter.ToUInt32(b,12) != 1 || token == 0 || BitConverter.ToUInt64(b,16) != token
                || BitConverter.ToUInt64(b,24) != started || now < started || (wallLimit != 9000 && wallLimit != 120000 && wallLimit != 180000 && wallLimit != 360000 && wallLimit != 240000) || now-started >= wallLimit
                || BitConverter.ToUInt32(b,40) != 0 || BitConverter.ToUInt32(b,44) != 1 || BitConverter.ToInt32(b,48) != 0
                || thread == 0 || BitConverter.ToUInt32(b,52) != thread || BitConverter.ToUInt32(b,56) != thread || BitConverter.ToUInt32(b,60) != 1)
                throw Error("native registration, token, deadline, writers or thread identity differs");
            return BitConverter.ToUInt64(b,32);
        }
        public void Dispose()
        {
            if (!ReferenceEquals(current,this)) throw Error("foreign publication lease release");
            current=null;
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident particle fence: "+reason);
    }
}
