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
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ResidentStatus([Out] byte[] output, uint bytes);

        // An epoch is issued by the native frame callback, not synthesized by
        // managed code. Every load uses a fresh epoch; failure cannot be reset.
        internal ResidentBoundary OpenResidentBoundary(long expectedNative, bool restoring = true) => new ResidentBoundary(this, expectedNative, 0, restoring);
        internal ResidentBoundary OpenResidentFrameObservation(long expectedNative, int phase)
            => phase == 1 || phase == 3 ? new ResidentBoundary(this, expectedNative, phase) : throw new ArgumentOutOfRangeException(nameof(phase));
        internal sealed class ResidentBoundary : IDisposable
        {
            private readonly NativeFullRunFrameClock clock;
            private readonly ResidentRequire require;
            private readonly ResidentRequire beginDirectorRestoreMutation;
            private readonly ResidentRequire endDirectorRestoreMutation;
            private readonly ResidentEnd end;
            private readonly ResidentExport export;
            private readonly ResidentImport import;
            private readonly ResidentStatus particleStatus;
            private readonly ResidentStatus directorStatus;
            private readonly ResidentExport inputStatus;
            private readonly byte[] initialInput;
            private ulong started;
            private FullRun.ResidentDirectorFence.Witness directorWitness;
            private uint mainThread;
            private FullRun.ResidentParticlePublicationLease? particleLease;
            private readonly ResidentExport? frameClock;
            private readonly IntPtr module;
            private bool ended, failed, imported;
            internal ulong Token { get; }
            internal long NativeFrame { get; private set; }
            internal string BridgeSha256 { get; }
            internal ResidentBoundary(NativeFullRunFrameClock clock, long expectedNative, int phase, bool restoring = false)
            {
                this.clock = clock;
                if (expectedNative < 0 || clock.CurrentFrameIndex != expectedNative) throw Error("native cursor differs before admission");
                module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
                if (module == IntPtr.Zero) throw Error("native bridge is absent");
                T Load<T>(string name) where T : Delegate {
                    var address = GetProcAddress(module, "HktasClockBridge_" + name);
                    return address == IntPtr.Zero ? throw Error("native export is absent: " + name) : Marshal.GetDelegateForFunctionPointer<T>(address);
                }
                if (Load<NativeInt>("GetResidentBoundaryCapability")() != 4) throw Error("native boundary protocol differs");
                require = Load<ResidentRequire>("RequireResidentBoundary"); end = Load<ResidentEnd>("EndResidentBoundary");
                beginDirectorRestoreMutation = Load<ResidentRequire>("BeginResidentDirectorRestoreMutation");
                endDirectorRestoreMutation = Load<ResidentRequire>("EndResidentDirectorRestoreMutation");
                export = Load<ResidentExport>("ExportResidentContinuation"); import = Load<ResidentImport>("ImportResidentContinuation");
                particleStatus = Load<ResidentStatus>("CopyResidentParticleFenceStatus");
                directorStatus = Load<ResidentStatus>("CopyResidentDirectorFenceStatus");
                inputStatus = Load<ResidentExport>("CopyResidentInputFenceStatus");
                if (phase != 0) {
                    if (Load<NativeInt>("GetResidentFrameClockBytes")() != 1192) throw Error("read-only frame clock ABI differs");
                    frameClock = Load<ResidentExport>("ExportResidentFrameClock");
                }
                var path = new StringBuilder(32768); var count = GetModuleFileName(module, path, path.Capacity);
                if (count == 0 || count >= path.Capacity) throw Error("native bridge identity is unavailable");
                BridgeSha256 = Sha256Utility.ComputeHex(File.ReadAllBytes(path.ToString()));
                Token = phase == 0 ? Load<ResidentBegin>(restoring ? "BeginResidentBoundary" : "BeginResidentSourceBoundary")((ulong)expectedNative)
                    : Load<ResidentFrameBegin>("BeginResidentFrameObservation")((ulong)expectedNative, phase);
                NativeFrame = expectedNative;
                if (Token == 0) throw Error("native frame callback rejected boundary admission");
                try {
                    Require();
                    var receipt = new byte[64];
                    if (particleStatus(receipt, 64) != 64) throw Error("particle publication receipt is absent");
                    started = BitConverter.ToUInt64(receipt, 24);
                    mainThread = BitConverter.ToUInt32(receipt, 52);
                    initialInput = ReadInputFence();
                    _ = ReadDirectorFence(out directorWitness);
                    particleLease = FullRun.ResidentParticlePublicationLease.Open(Token,started,mainThread,() => {
                        Require(); var bytes = new byte[64];
                        if (particleStatus(bytes,64) != 64) throw Error("particle publication fence status is absent");
                        return bytes;
                    }, restoring ? 360000UL : 240000UL);
                } catch (Exception error) { FullRun.ResidentFailureEvidence.Record(error); Fault(); throw; }
            }
            internal void Require()
            {
                if (ended) throw Error("native boundary epoch ended");
                if (failed) throw Error("native boundary epoch failed");
                if (GetModuleHandle("HollowKnightTAS.ClockBridge.dll") != module) throw Error("native bridge module handle changed");
                if (clock.CurrentFrameIndex != NativeFrame) throw Error($"native frame changed: clock={clock.CurrentFrameIndex}, boundary={NativeFrame}");
                var req = require(Token, (ulong)NativeFrame);
                if (req != 1) throw Error($"require(Token={Token}, Frame={NativeFrame}) returned {req}");
            }
            internal byte[] Capture()
            {
                Require(); var bytes = new byte[frameClock == null ? ContinuationBytes : 1192];
                if ((frameClock ?? export)(Token, bytes, (uint)bytes.Length) != 1) throw Error("native clock capture failed"); Require(); return bytes;
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
            internal void BeginDirectorRestoreMutation()
            {
                Require();
                if (beginDirectorRestoreMutation(Token, (ulong)NativeFrame) != 1)
                    throw Error("native director restore mutation window open failed");
            }
            internal void EndDirectorRestoreMutation()
            {
                Require();
                if (endDirectorRestoreMutation(Token, (ulong)NativeFrame) != 1)
                    throw Error("native director restore mutation window seal failed");
                _ = ReadDirectorFence(out directorWitness);
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
