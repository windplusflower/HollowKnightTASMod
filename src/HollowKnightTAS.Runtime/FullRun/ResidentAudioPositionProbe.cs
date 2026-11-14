using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using M = HollowKnightTAS.Runtime.FullRun.ResidentNativeMemory;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Failure evidence only. This does not enter Audio Data, comparison or coverage.
    internal static class ResidentAudioPositionProbe
    {
        internal sealed class Candidate
        {
            internal string Alias = "";
            internal long Weak, Instance;
        }
        internal sealed class Sample
        {
            public bool Attempted;
            public int ReturnCode = -1;
            public uint Position;
            public ulong WallMilliseconds;
            public string Reason = "not-sampled";
            internal bool Succeeded => Attempted && ReturnCode == 0 && Reason.Length == 0;
        }
        internal sealed class Voice
        {
            public string Alias = "", Reason = "";
            public Sample First = new Sample(), Second = new Sample();
            public bool? SamePosition;
        }
        internal sealed class Data
        {
            public string Note = "Two read-only native PCM observations under master own-pause; equal positions do not prove every DSP or hardware state is frozen.", Reason = "not-sampled";
            public bool NoLease, NoSystem, OriginalPaused, Truncated;
            public int CandidateCount;
            public ulong ActualElapsedMilliseconds, WaitElapsedMilliseconds;
            public Voice[] Voices = Array.Empty<Voice>();
        }
        internal sealed class Backend
        {
            internal bool Available, NoSystem, OriginalPaused;
            internal long WeakVtable, InstanceVtable;
            internal Action Require = () => { };
            internal Func<long,long,long[]> ReadState = (_,__) => throw new InvalidOperationException();
            internal Func<long,(int Code,uint Position)> Query = _ => throw new InvalidOperationException();
            internal Func<ulong> Wall = () => M.WallMilliseconds();
            internal Action<int> Sleep = milliseconds => Thread.Sleep(milliseconds);
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetPositionPCM(IntPtr instance, out uint position);

        private static InvalidDataException Error(string text) => new InvalidDataException("Audio position evidence: " + text);
        private static string Failure(Exception error) => error.GetType().Name + ": " + error.Message;
        internal static Data Capture(Candidate[] candidates)
        {
            var lease = ResidentAudioPauseLease.Current;
            if (lease == null) return Run(candidates, new Backend());
            if (lease.NoSystem) return Run(candidates, new Backend {Available = true, NoSystem = true, OriginalPaused = lease.OriginalPaused, Require = lease.Require});
            try {
                lease.Require();
                var engine = LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress;
                ResidentLoadedAssetIndex.RequireCode(engine, 0xabe770, 32, "38b6abeacabf381986660988705d1a6ce759d3361fa9d120eb2bb8c8e238020a");
                var getter = (GetPositionPCM)Marshal.GetDelegateForFunctionPointer(IntPtr.Add(engine, 0xabe770), typeof(GetPositionPCM));
                var backend = new Backend {Available = true, OriginalPaused = lease.OriginalPaused,
                    WeakVtable = IntPtr.Add(engine, 0x16e2108).ToInt64(), InstanceVtable = IntPtr.Add(engine, 0x16e3830).ToInt64()};
                backend.Require = () => {
                    if (!ReferenceEquals(ResidentAudioPauseLease.Current, lease)) throw Error("master lease is no longer current");
                    lease.Require();
                    if (LoadingEngineClockProbe.RequirePinnedEngine().BaseAddress != engine) throw Error("engine identity changed");
                };
                backend.ReadState = (weak, instance) => ReadNativeState(weak, instance, backend.WeakVtable);
                backend.Query = instance => {int result = getter(new IntPtr(instance), out var position); return (result, position);};
                return Run(candidates, backend);
            } catch (Exception error) {
                var data = Run(candidates, new Backend());
                data.NoLease = false;
                data.Reason = "native-query-unavailable: " + Failure(error);
                foreach (var voice in data.Voices) voice.Reason = data.Reason;
                return data;
            }
        }
        private static long[] ReadNativeState(long weakAddress, long expectedInstance, long expectedWeakVtable)
        {
            var weak = M.Read(new IntPtr(weakAddress), 24);
            var state = new long[5];
            state[0] = M.Ptr(weak, 0).ToInt64();
            state[1] = M.Ptr(weak, 16).ToInt64();
            // Do not dereference an expired, replaced, or untyped weak target.
            if (state[0] != expectedWeakVtable || state[1] == 0 || state[1] != expectedInstance) return state;
            var body = M.Read(new IntPtr(state[1]), 360);
            state[2] = M.Ptr(body, 0).ToInt64();
            state[3] = M.Ptr(body, 352).ToInt64();
            state[4] = M.Ptr(body, 192).ToInt64(); // Encoded FMOD facade handle; never dereferenced.
            return state;
        }
        private static string InvalidState(Candidate candidate, long[] state, Backend backend)
        {
            if (state == null || state.Length != 5) return "native-state-shape-differs";
            if (state[0] != backend.WeakVtable) return "weak-vtable-differs";
            if (state[1] == 0) return "weak-expired";
            if (state[1] != candidate.Instance) return "weak-instance-changed";
            if (state[2] != backend.InstanceVtable) return "instance-vtable-differs";
            if (state[3] != candidate.Weak) return "instance-self-weak-differs";
            if (state[4] == 0) return "no-FMOD-backend-not-sampled";
            return "";
        }
        private static Sample Observe(Candidate candidate, Backend backend, long[]? expected, out long[]? stable)
        {
            stable = null;
            var result = new Sample();
            try {
                backend.Require();
                var before = backend.ReadState(candidate.Weak, candidate.Instance);
                result.Reason = InvalidState(candidate, before, backend);
                if (result.Reason.Length != 0) return result;
                if (expected != null && !expected.SequenceEqual(before)) {result.Reason = "native-identity-changed-before-query"; return result;}
                // Require master own-pause immediately around each native API operation.
                backend.Require();
                result.Attempted = true;
                var value = backend.Query(candidate.Instance);
                result.ReturnCode = value.Code;
                result.Position = value.Position;
                result.WallMilliseconds = backend.Wall();
                backend.Require();
                var after = backend.ReadState(candidate.Weak, candidate.Instance);
                result.Reason = InvalidState(candidate, after, backend);
                if (result.Reason.Length != 0) return result;
                if (!before.SequenceEqual(after)) {result.Reason = "native-identity-changed-during-query"; return result;}
                if (value.Code != 0) {result.Reason = "GetPositionPCM-error-" + value.Code; return result;}
                result.Reason = "";
                stable = after;
            } catch (Exception error) {result.Reason = Failure(error);}
            return result;
        }
        private static Data Run(Candidate[] candidates, Backend backend)
        {
            var selected = candidates.Take(32).ToArray();
            var data = new Data {CandidateCount = candidates.Length, Truncated = candidates.Length > 32,
                NoLease = !backend.Available, NoSystem = backend.NoSystem, OriginalPaused = backend.OriginalPaused,
                Voices = selected.Select(value => new Voice {Alias = value.Alias}).ToArray()};
            if (!backend.Available || backend.NoSystem) {
                data.Reason = backend.NoSystem ? "no-audio-system-not-sampled" : "no-current-master-lease-not-sampled";
                foreach (var voice in data.Voices) voice.Reason = data.Reason;
                return data;
            }
            if (selected.Length == 0) {data.Reason = "no-live-main-or-one-shot-candidates"; return data;}
            ulong start = backend.Wall();
            var states = new long[]?[selected.Length];
            try {
                backend.Require();
                for (int i = 0; i < selected.Length; i++) {
                    data.Voices[i].First = Observe(selected[i], backend, null, out states[i]);
                    if (!data.Voices[i].First.Succeeded) data.Voices[i].Reason = data.Voices[i].First.Reason;
                }
                if (!data.Voices.Any(voice => voice.First.Succeeded)) {data.Reason = "no-successful-first-round"; return data;}
                ulong waitStart = backend.Wall();
                for (int attempt = 0; attempt < 100; attempt++) {
                    backend.Require();
                    ulong now = backend.Wall();
                    if (now < waitStart) throw Error("wall-clock moved backwards");
                    if (now - waitStart >= 100) break;
                    backend.Sleep((int)Math.Min(5UL, 100UL - (now - waitStart)));
                }
                ulong waitEnd = backend.Wall();
                if (waitEnd < waitStart) throw Error("wall-clock moved backwards");
                data.WaitElapsedMilliseconds = waitEnd - waitStart;
                if (data.WaitElapsedMilliseconds < 100) throw Error("real wall-clock interval was not reached within the bounded wait");
                backend.Require();
                for (int i = 0; i < selected.Length; i++) {
                    if (!data.Voices[i].First.Succeeded) continue;
                    var second = Observe(selected[i], backend, states[i], out _);
                    data.Voices[i].Second = second;
                    if (second.Succeeded) data.Voices[i].SamePosition = data.Voices[i].First.Position == second.Position;
                    else data.Voices[i].Reason = second.Reason;
                }
                data.Reason = "two-round-native-PCM-observation-only";
            } catch (Exception error) {data.Reason = Failure(error);}
            finally {
                ulong end = backend.Wall();
                data.ActualElapsedMilliseconds = end >= start ? end - start : 0;
            }
            return data;
        }
    }
}
