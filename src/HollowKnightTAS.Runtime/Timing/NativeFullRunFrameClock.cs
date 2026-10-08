using System;
using System.Runtime.InteropServices;
using HollowKnightTAS.Core.FullRun;
using HollowKnightTAS.Core.Movie;

namespace HollowKnightTAS.Runtime.Timing
{
    public sealed class NativeFullRunFrameClock
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeInt();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate ulong NativeFrame();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeSetCallback(IntPtr callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeCompleted(ulong completed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeFault(int code);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeFrameRate(int numerator, int denominator);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int NativeCopyHash([Out] byte[] output, uint capacity);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);

        private readonly NativeFrame getFrame;
        private readonly NativeInt getMode;
        private readonly NativeSetCallback setCallback;
        private readonly NativeSetCallback setBeforeCallback;
        private readonly NativeInt reportMovieFrameCompleted;
        private readonly NativeInt requestPause;
        private readonly NativeInt finish;
        private NativeFrameRate setFrameRate = null!;
        private NativeFrame stepTicks = null!;
        private NativeFrame frequency = null!;
        public double StepSeconds => (double)stepTicks() / frequency();
        private readonly NativeFault fault;
        private readonly NativeCopyHash copyHash;
        private NativeCompleted? nativeCallback;
        private NativeCompleted? nativeBeforeCallback;
        private NativeCompleted? nativeObservationCallback;
        private NativeSetCallback setObservationCallback = null!;
        private NativeInt requestObservation = null!;
        private Action<long>? completedCallback;
        private Action<long>? beforeCallback;

        private NativeFullRunFrameClock(NativeFrame getFrame, NativeInt getMode,
            NativeSetCallback setCallback, NativeSetCallback setBeforeCallback,
            NativeInt reportMovieFrameCompleted,
            NativeInt requestPause, NativeInt finish, NativeFault fault,
            NativeCopyHash copyHash)
        {
            this.getFrame = getFrame;
            this.getMode = getMode;
            this.setCallback = setCallback;
            this.setBeforeCallback = setBeforeCallback;
            this.reportMovieFrameCompleted = reportMovieFrameCompleted;
            this.requestPause = requestPause;
            this.finish = finish;
            this.fault = fault;
            this.copyHash = copyHash;
        }

        public static bool TryFaultEarly(int code)
        {
            if (code <= 0) return false;
            var module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
            if (module == IntPtr.Zero) return false;
            var address = GetProcAddress(module, "HktasClockBridge_FaultFullRun");
            return address != IntPtr.Zero
                && Marshal.GetDelegateForFunctionPointer<NativeFault>(address)(code) == 1;
        }

        public static NativeFullRunFrameClock Attach(string token, string expectedProfileId)
        {
            if (!FullRunBootDescriptor.IsGateToken(token)
                || !string.Equals(token, Environment.GetEnvironmentVariable("HKTAS_BOOT_GATE_TOKEN"),
                    StringComparison.Ordinal))
                throw new InvalidOperationException("Native full-run gate token differs from the launch token.");
            if (expectedProfileId != MovieProtocolV2.NativeProfileId)
                throw new InvalidOperationException("Native full-run frame profile is unsupported.");
            var module = GetModuleHandle("HollowKnightTAS.ClockBridge.dll");
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Native clock bridge is missing.");
            T Load<T>(string export) where T : Delegate
            {
                var address = GetProcAddress(module, export);
                if (address == IntPtr.Zero)
                    throw new MissingMethodException("Native full-run export is missing: " + export);
                return Marshal.GetDelegateForFunctionPointer<T>(address);
            }
            if (Load<NativeInt>("HktasClockBridge_GetFullRunCapability")() != 2)
                throw new InvalidOperationException("Native full-run frame capability is unavailable.");
            var clock = new NativeFullRunFrameClock(
                Load<NativeFrame>("HktasClockBridge_GetCompletedPlayerLoops"),
                Load<NativeInt>("HktasClockBridge_GetFullRunMode"),
                Load<NativeSetCallback>("HktasClockBridge_SetFrameCompletedCallback"),
                Load<NativeSetCallback>("HktasClockBridge_SetBeforeFrameCallback"),
                Load<NativeInt>("HktasClockBridge_ReportMovieFrameCompleted"),
                Load<NativeInt>("HktasClockBridge_RequestFramePause"),
                Load<NativeInt>("HktasClockBridge_FinishFullRun"),
                Load<NativeFault>("HktasClockBridge_FaultFullRun"),
                Load<NativeCopyHash>("HktasClockBridge_CopyFullRunDescriptorHash"));
            clock.setFrameRate = Load<NativeFrameRate>("HktasClockBridge_SetFullRunFrameRateRatio");
            clock.setObservationCallback = Load<NativeSetCallback>("HktasClockBridge_SetObservationCallback");
            clock.requestObservation = Load<NativeInt>("HktasClockBridge_RequestObservation");
            clock.stepTicks = Load<NativeFrame>("HktasClockBridge_GetDeterministicClockStepTicks");
            clock.frequency = Load<NativeFrame>("HktasClockBridge_GetDeterministicClockFrequency");
            if (clock.CurrentFrameIndex < 0)
                throw new InvalidOperationException("Native full-run frame count is invalid.");
            return clock;
        }

        public long CurrentFrameIndex
        {
            get
            {
                var value = getFrame();
                if (value > long.MaxValue)
                    throw new InvalidOperationException("Native frame count overflowed.");
                return (long)value;
            }
        }

        public void SetFrameRate(decimal fps)
        {
            MovieFrameRate.ToRatio(fps, out var numerator, out var denominator);
            if (setFrameRate(numerator, denominator) != 1) throw new InvalidOperationException("Native frame rate rejected.");
            // Unity must derive gameplay time from the native QPC clock. A capture
            // override uses a separately latched step and can lag a rate change by
            // one frame. Preserve Unity's fixedDeltaTime and timeScale semantics.
            UnityEngine.Time.captureDeltaTime = 0f;
            UnityEngine.Application.targetFrameRate = (int)decimal.Ceiling(fps);
        }

        public bool IsPaused => getMode() == 0 || getMode() == 4;
        public bool IsFinished => getMode() == 4;

        public byte[] ReadArmedDescriptorHash()
        {
            var bytes = new byte[32];
            if (copyHash(bytes, (uint)bytes.Length) != 1)
                throw new InvalidOperationException("Native full-run bootstrap is not armed.");
            return bytes;
        }

        public void RegisterCompleted(Action<long> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (nativeCallback != null)
                throw new InvalidOperationException("Native completion callback is already installed.");
            completedCallback = callback;
            nativeCallback = completed =>
            {
                try
                {
                    if (completed > long.MaxValue) throw new OverflowException("Native frame count overflowed.");
                    completedCallback((long)completed);
                }
                catch
                {
                    fault(41);
                    requestPause();
                }
            };
            if (setCallback(Marshal.GetFunctionPointerForDelegate(nativeCallback)) != 1)
            {
                nativeCallback = null;
                completedCallback = null;
                throw new InvalidOperationException("Native completion callback registration failed.");
            }
        }

        public void RegisterBeforeFrame(Action<long> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (nativeBeforeCallback != null)
                throw new InvalidOperationException("Native before-frame callback is already installed.");
            beforeCallback = callback;
            nativeBeforeCallback = completed =>
            {
                try
                {
                    if (completed > long.MaxValue) throw new OverflowException("Native frame count overflowed.");
                    beforeCallback((long)completed);
                }
                catch
                {
                    fault(43);
                    requestPause();
                }
            };
            if (setBeforeCallback(Marshal.GetFunctionPointerForDelegate(nativeBeforeCallback)) != 1)
            {
                nativeBeforeCallback = null;
                beforeCallback = null;
                throw new InvalidOperationException("Native before-frame callback registration failed.");
            }
        }

        public void RequestPause()
        {
            if (requestPause() != 1)
                throw new InvalidOperationException("Native frame pause request was rejected.");
        }

        public void RegisterObservation(Action<long> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            if (nativeObservationCallback != null)
                throw new InvalidOperationException("Observation callback is already installed.");
            // The queue converts collector errors into request failures. Never fault the game
            // for an inspector failure; keep this delegate rooted for the process lifetime.
            nativeObservationCallback = frame =>
            {
                try { callback(checked((long)frame)); }
                catch { /* Request timeout reports unexpected observer failures. */ }
            };
            if (setObservationCallback(Marshal.GetFunctionPointerForDelegate(nativeObservationCallback)) != 1)
                throw new InvalidOperationException("Native observation callback registration failed.");
        }

        public bool RequestObservation() => requestObservation() == 1;

        public void ReportMovieFrameCompleted()
        {
            if (reportMovieFrameCompleted() != 1)
                throw new InvalidOperationException("Native Movie-frame completion was rejected.");
        }

        public void Finish()
        {
            if (finish() != 1)
                throw new InvalidOperationException("Native full-run finish request was rejected.");
        }

        public void Fault(int code)
        {
            if (code <= 0) throw new ArgumentOutOfRangeException(nameof(code));
            if (fault(code) != 1)
                throw new InvalidOperationException("Native full-run fault request was rejected.");
        }
    }
}
