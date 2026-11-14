using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Temporary normal audio-API control, never D or FMOD restore coverage.
    // Only master own-pause is proven. Mixer/PCM stability needs its own adapter.
    internal sealed class ResidentAudioPauseLease : IDisposable
    {
        internal sealed class Backend
        {
            internal readonly Func<long[]> ReadIdentity;
            internal readonly Func<long,bool> GetPaused;
            internal readonly Action<long,bool> SetPaused;
            internal Backend(Func<long[]> readIdentity,Func<long,bool> getPaused,Action<long,bool> setPaused)
            { ReadIdentity = readIdentity; GetPaused = getPaused; SetPaused = setPaused; }
        }
        [ThreadStatic] private static ResidentAudioPauseLease? current;
        internal static ResidentAudioPauseLease? Current => current;
        private readonly Backend backend;
        private readonly int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        // Process-local manager/system/master addresses. Never serialized.
        private readonly long[] identity;
        private readonly bool original;
        private bool restoreNeeded, completed, disposed;
        internal bool NoSystem => identity[1] == 0;
        internal bool OriginalPaused => original;
        internal IntPtr RequireSystemOpaque()
        {
            Require();
            if (NoSystem) throw Error("audio system is absent");
            var system = new IntPtr(identity[1]);
            Require();
            return system;
        }

        private ResidentAudioPauseLease(Backend backend)
        {
            this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
            identity = Identity();
            original = !NoSystem && backend.GetPaused(identity[2]);
        }
        internal static ResidentAudioPauseLease Begin() => Begin(NativeBackend());
        internal static ResidentAudioPauseLease Begin(Backend backend)
        {
            if (current != null) throw Error("nested master pause lease");
            var lease = new ResidentAudioPauseLease(backend);
            try {
                if (!lease.NoSystem) {
                    // The setter may change state even when its later result is
                    // an error. Compensate every attempted state-changing call.
                    lease.restoreNeeded = true;
                    backend.SetPaused(lease.identity[2],true);
                    lease.RequireState();
                } else lease.RequireIdentity();
                current = lease;
                ResidentFailureEvidence.Mark("audio master lease mode=" + (lease.NoSystem ? "NoSystem" : "MasterOwnPause") + ";original=" + lease.original);
                return lease;
            } catch {
                lease.Dispose();
                throw;
            }
        }
        private long[] Identity()
        {
            var value = backend.ReadIdentity();
            if (value == null || value.Length != 3 || value[0] == 0 && value[1] != 0
                || (value[1] == 0) != (value[2] == 0)) throw Error("manager/system/master identity shape differs");
            return (long[])value.Clone();
        }
        private void RequireIdentity()
        {
            if (!identity.SequenceEqual(Identity())) throw Error("manager/system/master identity changed");
        }
        private void RequireState()
        {
            RequireIdentity();
            if (!NoSystem && !backend.GetPaused(identity[2])) throw Error("master own-pause request was not retained");
            RequireIdentity();
        }
        internal void Require()
        {
            if (disposed || completed || !ReferenceEquals(current,this)) throw Error("master pause lease is not current");
            // No admission callback here: Coordinator.Admitted calls Require.
            RequireState();
        }
        internal void Complete()
        {
            Require();
            Restore();
            completed = true;
            if (ReferenceEquals(current,this)) current = null;
        }
        private void Restore()
        {
            RequireIdentity();
            if (!NoSystem && restoreNeeded) {
                backend.SetPaused(identity[2],original);
                if (backend.GetPaused(identity[2]) != original) throw Error("original master own-pause did not read back");
                RequireIdentity();
                restoreNeeded = false;
            }
            ResidentFailureEvidence.Mark("audio master lease restore=" + (NoSystem ? "NoSystemUnchanged" : "Confirmed") + ";original=" + original);
        }
        public void Dispose()
        {
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != thread) throw Error("master pause lease crossed threads");
            if (disposed) return;
            try {
                if (!completed) Restore();
            } catch (Exception error) {
                // Cleanup must precede process.Fault without masking the action
                // exception. The failed transaction never grants audio coverage.
                ResidentFailureEvidence.Mark("audio master lease cleanup failed " + error.GetType().Name);
            } finally {
                disposed = true;
                if (ReferenceEquals(current,this)) current = null;
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MasterGroup(IntPtr system,out IntPtr group);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPause(IntPtr group,out byte paused);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetPause(IntPtr group,[MarshalAs(UnmanagedType.I1)] bool paused,[MarshalAs(UnmanagedType.I1)] bool setOwnState);
        private static Backend NativeBackend()
        {
            var engine = LoadingEngineClockProbe.RequirePinnedEngine();
            void Code(int rva,string expected) {
                var actual = BitConverter.ToString(M.Read(IntPtr.Add(engine.BaseAddress,rva),expected.Length/2)).Replace("-","").ToLowerInvariant();
                if (actual != expected) throw Error("fixed master-pause API prefix differs");
            }
            void Pinned() {
                if (LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress != engine.BaseAddress) throw Error("master-pause engine identity changed");
            }
            Code(0x572d00,"4863c1488d0d264d4601488b04c1c3");
            Code(0x10a3c50,"40534883ec20488bda488d542440e81d");
            Code(0x10c1d20,"4885d27506b825000000c30fb681a900");
            Code(0x10c2390,"48895c24104889742420574883ec200f");
            var getMaster = (MasterGroup)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress,0x10a3c50),typeof(MasterGroup));
            var getPaused = (GetPause)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress,0x10c1d20),typeof(GetPause));
            var setPaused = (SetPause)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine.BaseAddress,0x10c2390),typeof(SetPause));
            return new Backend(() => {
                Pinned();
                var manager = M.Ptr(IntPtr.Add(engine.BaseAddress,0x19d7a48));
                var system = manager == IntPtr.Zero ? IntPtr.Zero : M.Ptr(IntPtr.Add(manager,0x148));
                IntPtr master = IntPtr.Zero;
                if (system != IntPtr.Zero) {
                    int result = getMaster(system,out master);
                    if (result != 0 || master == IntPtr.Zero) throw Error("master group acquisition failed code=" + result);
                }
                return new[] {manager.ToInt64(),system.ToInt64(),master.ToInt64()};
            }, master => {
                Pinned(); int result = getPaused(new IntPtr(master),out byte paused);
                if (result != 0 || paused > 1) throw Error("master own-pause getter failed code=" + result);
                return paused != 0;
            }, (master,paused) => {
                Pinned(); int result = setPaused(new IntPtr(master),paused,true);
                if (result != 0) throw Error("master own-pause setter failed code=" + result);
                // Native group success discards per-channel errors. No claim of
                // PCM quiescence follows from this control-state operation.
            });
        }
        private static InvalidDataException Error(string reason) => new InvalidDataException("Resident audio master lease: " + reason);
    }
}
