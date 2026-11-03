using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HollowKnightTAS.Core.Cryptography;

namespace HollowKnightTAS.Runtime.Timing
{
    public sealed partial class NativeFullRunFrameClock
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ResidentBegin(ulong expected);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong ResidentFrameBegin(ulong expected, int phase);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentRequire(ulong token, ulong expected);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentEnd(ulong token, int complete);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentExport(ulong token, [Out] byte[] output, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentImport(ulong token, [In] byte[] source, uint count, ulong expected);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentAudioStatus([Out] byte[] output, uint bytes);

        // An epoch is issued by the native frame callback, not synthesized by
        // managed code. Every load uses a fresh epoch; failure cannot be reset.
        internal ResidentBoundary OpenResidentBoundary(long expectedNative) => new ResidentBoundary(this, expectedNative, 0);
        internal ResidentBoundary OpenResidentFrameObservation(long expectedNative, int phase)
            => phase == 1 || phase == 3 ? new ResidentBoundary(this, expectedNative, phase) : throw new ArgumentOutOfRangeException(nameof(phase));
        internal sealed class ResidentBoundary : IDisposable
        {
            private readonly NativeFullRunFrameClock clock;
            private readonly ResidentRequire require;
            private readonly ResidentEnd end;
            private readonly ResidentExport export;
            private readonly ResidentImport import;
            private readonly ResidentAudioStatus audioStatus;
            private readonly ResidentAudioStatus particleStatus;
            private readonly ResidentAudioStatus directorStatus;
            private readonly ResidentExport inputStatus;
            private readonly byte[] initialInput;
            private ulong started;
            private FullRun.ResidentDirectorFence.Witness directorWitness;
            private uint mainThread;
            private FullRun.ResidentParticlePublicationLease? particleLease;
            private readonly ResidentExport audioWorkers;
            private readonly ResidentExport? frameClock;
            private readonly IntPtr module;
            private bool ended, failed, imported;
            internal ulong Token { get; }
            internal long NativeFrame { get; private set; }
            internal string BridgeSha256 { get; }
            internal ResidentBoundary(NativeFullRunFrameClock clock, long expectedNative, int phase)
            {
                this.clock = clock;
                if (expectedNative < 0 || clock.CurrentFrameIndex != expectedNative) throw Error("native cursor differs before admission");
                module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
                if (module == IntPtr.Zero) throw Error("native bridge is absent");
                T Load<T>(string name) where T : Delegate {
                    var address = GetProcAddress(module, "HktasClockBridge_" + name);
                    return address == IntPtr.Zero ? throw Error("native export is absent: " + name) : Marshal.GetDelegateForFunctionPointer<T>(address);
                }
                if (Load<NativeInt>("GetResidentBoundaryCapability")() != 2) throw Error("native boundary protocol differs");
                require = Load<ResidentRequire>("RequireResidentBoundary"); end = Load<ResidentEnd>("EndResidentBoundary");
                export = Load<ResidentExport>("ExportResidentContinuation"); import = Load<ResidentImport>("ImportResidentContinuation");
                audioStatus = Load<ResidentAudioStatus>("CopyResidentAudioFenceStatus");
                particleStatus = Load<ResidentAudioStatus>("CopyResidentParticleFenceStatus");
                directorStatus = Load<ResidentAudioStatus>("CopyResidentDirectorFenceStatus");
                inputStatus = Load<ResidentExport>("CopyResidentInputFenceStatus");
                audioWorkers = Load<ResidentExport>("CopyResidentAudioWorkers");
                if (phase != 0) {
                    if (Load<NativeInt>("GetResidentFrameClockBytes")() != 1192) throw Error("read-only frame clock ABI differs");
                    frameClock = Load<ResidentExport>("ExportResidentFrameClock");
                }
                var path = new StringBuilder(32768); var count = GetModuleFileName(module, path, path.Capacity);
                if (count == 0 || count >= path.Capacity) throw Error("native bridge identity is unavailable");
                BridgeSha256 = Sha256Utility.ComputeHex(File.ReadAllBytes(path.ToString()));
                Token = phase == 0 ? Load<ResidentBegin>("BeginResidentBoundary")((ulong)expectedNative)
                    : Load<ResidentFrameBegin>("BeginResidentFrameObservation")((ulong)expectedNative, phase);
                NativeFrame = expectedNative;
                if (Token == 0) throw Error("native frame callback rejected boundary admission");
                try {
                    Require(); var audio = FullRun.ResidentNativeAudioFenceState.DecodeStatus(CaptureAudioStatus());
                    if (audio.Epoch != Token || audio.MainThread != audio.CallerThread) throw Error("native epochs do not share their owner");
                    started = audio.StartedTick; mainThread = audio.MainThread;
                    initialInput = ReadInputFence();
                    _ = ReadDirectorFence(out directorWitness);
                    particleLease = FullRun.ResidentParticlePublicationLease.Open(Token,audio.StartedTick,audio.MainThread,() => {
                        Require(); var bytes = new byte[64];
                        if (particleStatus(bytes,64) != 64) throw Error("particle publication fence status is absent");
                        return bytes;
                    });
                } catch (Exception error) { FullRun.ResidentFailureEvidence.Record(error); Fault(); throw; }
            }
            internal void Require()
            {
                if (ended || failed || GetModuleHandle("HollowKnightTAS.ClockBridge.dll") != module || clock.CurrentFrameIndex != NativeFrame
                    || require(Token, (ulong)NativeFrame) != 1) throw Error("native boundary ownership, cursor, command or wall deadline changed");
            }
            internal byte[] Capture()
            {
                Require(); var bytes = new byte[frameClock == null ? ContinuationBytes : 1192];
                if ((frameClock ?? export)(Token, bytes, (uint)bytes.Length) != 1) throw Error("native clock capture failed"); Require(); return bytes;
            }
            internal byte[] CaptureAudioStatus()
            {
                Require(); var bytes = new byte[96];
                if (audioStatus(bytes, (uint)bytes.Length) != 1) throw Error("audio fence status changed during observation");
                Require(); return bytes;
            }
            private byte[] ReadInputFence()
            {
                Require();
                var bytes = new byte[96];
                if (inputStatus(Token, bytes, 96) != 96) throw Error("input producer receipt is absent");
                uint U32(int offset) => BitConverter.ToUInt32(bytes, offset);
                ulong U64(int offset) => BitConverter.ToUInt64(bytes, offset);
                if (!BitConverter.IsLittleEndian || U32(0) != 0x31464952 || U32(4) != 1 || U32(8) != 96 || U32(12) != 3
                    || U64(16) != Token || U64(24) != started || U64(40) != (ulong)GetModuleHandle("UnityPlayer.dll").ToInt64()
                    || U64(48) != U64(56) || U64(64) > U64(56) || U64(72) != U64(80)
                    || mainThread == 0 || U32(88) != mainThread || U32(92) != mainThread)
                    throw Error("input queue/dispatch receipt does not match the boundary owner");
                return bytes;
            }
            internal IntPtr RequireInputOwner()
            {
                if (!ReadInputFence().SequenceEqual(initialInput)) throw Error("input producers changed during the held epoch");
                return new IntPtr(BitConverter.ToInt64(initialInput, 32));
            }
            internal string DescribeInputFence()
            {
                var owner = RequireInputOwner();
                return "input producer receipt v1, state present=" + (owner != IntPtr.Zero)
                    + ", queue returned=" + BitConverter.ToUInt64(initialInput, 56)
                    + ", accepted=" + BitConverter.ToUInt64(initialInput, 64)
                    + ", send returned=" + BitConverter.ToUInt64(initialInput, 80);
            }
            private FullRun.ResidentDirectorFence.Data ReadDirectorFence(out FullRun.ResidentDirectorFence.Witness witness)
            {
                Require(); var bytes = new byte[FullRun.ResidentDirectorFence.Bytes];
                if (directorStatus(bytes,(uint)bytes.Length) != bytes.Length) throw Error("Director receipt is absent");
                return FullRun.ResidentDirectorFence.Read(bytes,Token,started,mainThread,out witness);
            }
            internal FullRun.ResidentDirectorFence.Data CaptureDirectorFence()
            {
                var data = ReadDirectorFence(out var witness);
                directorWitness.RequireSame(witness);
                return data;
            }
            internal byte[] CaptureAudioWorkers()
            {
                Require(); const int capacity = 256, stride = 64; var bytes = new byte[capacity * stride];
                // This export takes row capacity and returns row count, unlike
                // the clock byte transport with the same native parameter ABI.
                var count = audioWorkers(Token, bytes, capacity);
                if (count < 0 || count > capacity) throw Error("audio worker directory changed during observation");
                var result = new byte[count * stride]; Buffer.BlockCopy(bytes, 0, result, 0, result.Length);
                Require(); return result;
            }
            internal void Apply(byte[] bytes, long expectedNative)
            {
                Require();
                if (imported || expectedNative != NativeFrame || bytes == null || bytes.Length != ContinuationBytes)
                    throw Error("native import order or source size differs");
                imported = true;
                try {
                    if (import(Token, bytes, (uint)bytes.Length, (ulong)expectedNative) != 1) throw Error("native clock import failed");
                    NativeFrame = clock.CurrentFrameIndex; Require();
                } catch (Exception error) { FullRun.ResidentFailureEvidence.Record(error); Fault(); throw; }
            }
            internal void Complete()
            {
                Require(); (particleLease ?? throw Error("particle publication lease is absent")).Require();
                _ = CaptureDirectorFence();
                _ = RequireInputOwner();
                if (end(Token, 1) != 1) { Fault(); throw Error("native transaction commit failed"); }
                ended = true; particleLease.Dispose(); particleLease = null;
            }
            internal void Fault()
            {
                if (ended || failed) return;
                failed = true;
                try { end(Token, 0); } finally { particleLease?.Dispose(); particleLease = null; }
            }
            public void Dispose() { if (!ended) Fault(); }
            private static InvalidOperationException Error(string message) => new InvalidOperationException("Resident native clock: " + message);
        }
    }
}
