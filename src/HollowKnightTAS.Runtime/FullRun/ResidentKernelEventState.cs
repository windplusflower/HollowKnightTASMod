using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HollowKnightTAS.Runtime.FullRun
{
    // NtQueryEvent reads EVENT_BASIC_INFORMATION without waiting on (and thus
    // consuming) an auto-reset event. Its native declaration is documented in
    // the Windows NT Event/Semaphore specification, section 2.6. The installed
    // Windows API/8-byte ABI is independently exercised by the offline tests.
    // This is an observation only. It does not freeze external request writers,
    // establish protocol quiescence, transfer handles, or signal/reset events.
    internal static class ResidentKernelEventState
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInformation
        {
            internal int Type;
            internal int State;
        }
        [DllImport("ntdll.dll", ExactSpelling = true)]
        private static extern int NtQueryEvent(IntPtr handle, int informationClass,
            out BasicInformation information, uint length, out uint returned);
        [DllImport("kernelbase.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CompareObjectHandles(IntPtr first, IntPtr second);

        internal sealed class Data
        {
            public EventResetMode ResetMode;
            public bool Signaled;
        }
        internal sealed class Live
        {
            private readonly EventWaitHandle owner;
            private readonly SafeWaitHandle handle;
            private readonly EventResetMode resetMode;
            private readonly bool signaled;
            private readonly Action admitted;
            internal Data Snapshot => new Data { ResetMode = resetMode, Signaled = signaled };

            internal Live(EventWaitHandle owner, EventResetMode mode, Action admitted)
            {
                this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted));
                if (mode != EventResetMode.AutoReset && mode != EventResetMode.ManualReset) throw Error("event reset mode is invalid");
                resetMode = mode; admitted(); handle = owner.SafeWaitHandle;
                signaled = Read(owner, handle, resetMode, admitted); RequireUnchanged();
            }
            internal void RequireUnchanged()
            {
                if (Read(owner, handle, resetMode, admitted) != signaled)
                    throw Error("event signal changed during the read-only observation");
            }
        }
        internal static Live Capture(EventWaitHandle owner, EventResetMode mode, Action admitted)
            => new Live(owner, mode, admitted);

        internal sealed class Alias
        {
            private readonly EventWaitHandle first, second;
            private readonly SafeWaitHandle firstHandle, secondHandle;
            private readonly Action admitted;
            internal Alias(EventWaitHandle first, EventWaitHandle second, Action admitted)
            {
                this.first = first ?? throw new ArgumentNullException(nameof(first)); this.second = second ?? throw new ArgumentNullException(nameof(second));
                this.admitted = admitted ?? throw new ArgumentNullException(nameof(admitted)); admitted();
                firstHandle = first.SafeWaitHandle; secondHandle = second.SafeWaitHandle; RequireUnchanged();
            }
            internal void RequireUnchanged()
            {
                admitted(); RequireHandles(); bool retainedFirst = false, retainedSecond = false;
                try {
                    firstHandle.DangerousAddRef(ref retainedFirst); secondHandle.DangerousAddRef(ref retainedSecond);
                    RequireHandles();
                    if (!CompareObjectHandles(firstHandle.DangerousGetHandle(), secondHandle.DangerousGetHandle()))
                        throw Error("producer/consumer handles do not refer to the same kernel object");
                    admitted(); RequireHandles();
                } finally {
                    if (retainedSecond) secondHandle.DangerousRelease(); if (retainedFirst) firstHandle.DangerousRelease();
                }
            }
            private void RequireHandles()
            {
                if (firstHandle.IsClosed || firstHandle.IsInvalid || secondHandle.IsClosed || secondHandle.IsInvalid
                    || !ReferenceEquals(first.SafeWaitHandle, firstHandle) || !ReferenceEquals(second.SafeWaitHandle, secondHandle))
                    throw Error("a producer/consumer kernel handle was closed, invalidated or replaced");
            }
        }
        internal static Alias RequireSameObject(EventWaitHandle first, EventWaitHandle second, Action admitted) => new Alias(first, second, admitted);

        private static bool Read(EventWaitHandle owner, SafeWaitHandle handle, EventResetMode mode, Action admitted)
        {
            admitted();
            if (handle == null || handle.IsClosed || handle.IsInvalid || !ReferenceEquals(owner.SafeWaitHandle, handle))
                throw Error("event handle was closed, invalidated or replaced");
            var retained = false;
            try {
                // Retain the exact kernel handle during the query; a concurrent
                // Dispose cannot close/reuse its numeric value beneath us.
                handle.DangerousAddRef(ref retained);
                var status = NtQueryEvent(handle.DangerousGetHandle(), 0, out var info, 8, out var returned);
                if (status != 0 || returned != 8 || info.Type < 0 || info.Type > 1 || info.State < 0 || info.State > 1)
                    throw Error("native event query failed or ABI differs: status=0x" + unchecked((uint)status).ToString("x8"));
                // NT NotificationEvent=0 is manual; SynchronizationEvent=1 is
                // auto. EventResetMode uses the reverse numeric ordering.
                if (info.Type != (mode == EventResetMode.ManualReset ? 0 : 1)) throw Error("actual native reset mode differs");
                if (handle.IsClosed || !ReferenceEquals(owner.SafeWaitHandle, handle)) throw Error("event handle changed during the native query");
                admitted(); return info.State != 0;
            }
            finally { if (retained) handle.DangerousRelease(); }
        }
        private static InvalidDataException Error(string message) => new InvalidDataException("Resident kernel event: " + message);
    }
}
