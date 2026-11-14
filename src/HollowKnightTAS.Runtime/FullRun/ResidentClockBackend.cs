using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HollowKnightTAS.Core.Cryptography;
using HollowKnightTAS.Runtime.Timing;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Production transport for the per-boundary clock protocol. The loader
    // witness and native frame-gate epoch must both remain admitted throughout.
    internal sealed class ResidentClockBackend : ResidentClockInputState.IBoundaryBackend, IDisposable
    {
        [ThreadStatic] private static ResidentClockBackend? currentClipBackend;
        private readonly int ownerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        internal static ulong RequireClipToken()
        {
            var current = currentClipBackend ?? throw Error("clip eligibility has no active backend scope");
            if (current.ownerThread != System.Threading.Thread.CurrentThread.ManagedThreadId || current.closed || current.failed)
                throw Error("clip eligibility backend scope is inactive or belongs to another thread");
            current.RequireBoundary();
            return current.Token;
        }
        private readonly NativeFullRunFrameClock.ResidentBoundary boundary;
        private readonly Action requireLoader;
        private readonly Action requirePayloadInitialized;
        private readonly Dictionary<string, MethodInfo> methods = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        private int stage;
        private bool closed, failed, producerFenceOpen;
        private readonly int observationPhase;
        public string Identity { get; }
        public bool IsFrameObservation => observationPhase == 1 || observationPhase == 3;
        public string[] CoverageGaps => Array.Empty<string>();
        public long NativeFrame => boundary.NativeFrame;
        internal ulong Token => boundary.Token;
        internal ResidentClockBackend(NativeFullRunFrameClock clock, Action requireLoader, int observationPhase = 0, bool restoring = false)
        {
            if (currentClipBackend != null) throw Error("nested clip eligibility backend scope");
            this.observationPhase = observationPhase;
            this.requireLoader = requireLoader ?? throw new ArgumentNullException(nameof(requireLoader)); requireLoader();
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(assembly => assembly.GetName().Name == "HollowKnightTAS.ClockPayload").ToArray();
            if (candidates.Length != 1) throw Error("actual clock payload assembly is absent or ambiguous");
            var payload = candidates[0]; var owner = payload.GetType("HollowKnightTAS.ClockPayload.ClockController", true)!;
            requirePayloadInitialized = ResidentStaticRoots.CreateInitializationGuard(owner);
            void Method(string name, Type returns, params Type[] parameters) {
                var method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
                if (method == null || method.ReturnType != returns || method.ContainsGenericParameters) throw Error("actual clock payload API differs: " + name);
                methods.Add(name, method);
            }
            Method("ExportResidentRandomState", typeof(byte[]), typeof(ulong));
            Method("ExportResidentClockObserver", typeof(byte[]), typeof(ulong));
            Method("ExportResidentParticleRandomState", typeof(uint[]), typeof(ulong));
            Method("ImportResidentRandomState", typeof(void), typeof(ulong), typeof(byte[]), typeof(long));
            Method("ImportResidentClockObserver", typeof(void), typeof(ulong), typeof(byte[]), typeof(long));
            Method("ImportResidentParticleRandomState", typeof(void), typeof(ulong), typeof(uint[]), typeof(long));
            Method("GetResidentRandomProducerAbi", typeof(int));
            Method("OpenResidentRandomProducerFence", typeof(void), typeof(ulong));
            Method("RequireResidentRandomProducerFence", typeof(void), typeof(ulong));
            Method("CloseResidentRandomProducerFence", typeof(void), typeof(ulong));
            Method("AbortResidentRandomProducerFence", typeof(void), typeof(ulong));
            if (observationPhase != 0) {
                Method("OpenResidentFrameProducerFence", typeof(void), typeof(ulong));
                Method("CloseResidentFrameProducerFence", typeof(void), typeof(ulong));
                Method("ExportResidentFrameRandomState", typeof(byte[]), typeof(ulong));
                Method("ExportResidentFrameClockObserver", typeof(byte[]), typeof(ulong));
                Method("ExportResidentFrameParticleRandomState", typeof(uint[]), typeof(ulong));
            }
            requirePayloadInitialized();
            if ((int)InvokePayload("GetResidentRandomProducerAbi")! != 1) throw Error("random producer protocol differs");
            var hash = Sha256Utility.ComputeHex(File.ReadAllBytes(payload.Location));
            boundary = observationPhase == 0 ? clock.OpenResidentBoundary(clock.CurrentFrameIndex, restoring)
                : clock.OpenResidentFrameObservation(clock.CurrentFrameIndex, observationPhase);
            Identity = "resident-clock-v3/" + boundary.BridgeSha256 + "/" + hash + "/" + payload.ManifestModule.ModuleVersionId.ToString("D");
            try {
                RequireNativeAndLoader();
                InvokePayload(observationPhase == 0 ? "OpenResidentRandomProducerFence" : "OpenResidentFrameProducerFence", Token); producerFenceOpen = true;
                RequireBoundary();
                currentClipBackend = this;
            } catch (Exception error) { ResidentFailureEvidence.Record(error); Fault(); boundary.Dispose(); throw; }
        }
        private void RequireNativeAndLoader()
        {
            if (closed || failed) throw Error("clock epoch is already closed or faulted");
            boundary.Require(); requireLoader(); boundary.Require();
        }
        public void RequireBoundary()
        {
            try {
                RequireNativeAndLoader();
                if (!producerFenceOpen) throw Error("random producer fence is not held");
                InvokePayload("RequireResidentRandomProducerFence", Token); RequireNativeAndLoader();
            } catch (Exception error) { ResidentFailureEvidence.Record(error); Fault(); throw; }
        }
        internal void RequirePayloadInitialized() { RequireBoundary(); requirePayloadInitialized(); RequireBoundary(); }
        // Open/close/abort have a different phase from the ordinary guarded
        // exports. In particular, checking the producer fence after closing it
        // would fault every otherwise valid native commit.
        private object? InvokePayload(string name, params object[] values)
        {
            requirePayloadInitialized();
            try { return methods[name].Invoke(null, values); }
            catch (TargetInvocationException error) when (error.InnerException != null) {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw;
            }
        }
        private object? Invoke(string name, params object[] values)
        {
            try { RequireBoundary(); var result = InvokePayload(name, values); RequireBoundary(); return result; }
            catch (Exception error) { ResidentFailureEvidence.Record(error); Fault(); throw; }
        }
        public byte[] CaptureNativeClock() { RequireBoundary(); var bytes = boundary.Capture(); RequireBoundary(); return bytes; }
        internal void BeginDirectorRestoreMutation() { RequireBoundary(); boundary.BeginDirectorRestoreMutation(); RequireBoundary(); }
        internal void EndDirectorRestoreMutation() { RequireBoundary(); boundary.EndDirectorRestoreMutation(); RequireBoundary(); }
        internal IntPtr RequireInputOwner() { RequireBoundary(); return boundary.RequireInputOwner(); }
        internal string DescribeInputFence() { RequireBoundary(); return boundary.DescribeInputFence(); }
        public byte[] CaptureRandom() => (byte[])Invoke(observationPhase == 0 ? "ExportResidentRandomState" : "ExportResidentFrameRandomState", Token)!;
        public byte[] CaptureObserver() => (byte[])Invoke(observationPhase == 0 ? "ExportResidentClockObserver" : "ExportResidentFrameClockObserver", Token)!;
        public uint[] CaptureParticles() => (uint[])Invoke(observationPhase == 0 ? "ExportResidentParticleRandomState" : "ExportResidentFrameParticleRandomState", Token)!;
        public void ImportRandom(byte[] bytes, long expectedTargetNative)
        {
            RequireStage(0, expectedTargetNative); stage = 1;
            Invoke("ImportResidentRandomState", Token, bytes, expectedTargetNative);
        }
        public void ImportNative(byte[] bytes, long expectedTargetNative)
        {
            RequireStage(1, expectedTargetNative); stage = 2;
            try { boundary.Apply(bytes, expectedTargetNative); RequireBoundary(); } catch (Exception error) { ResidentFailureEvidence.Record(error); Fault(); throw; }
        }
        public void ImportObserver(byte[] bytes, long sourceNative)
        { RequireStage(2, sourceNative); stage = 3; Invoke("ImportResidentClockObserver", Token, bytes, sourceNative); }
        public void ImportParticles(uint[] words, long sourceNative)
        { RequireStage(3, sourceNative); stage = 4; Invoke("ImportResidentParticleRandomState", Token, words, sourceNative); }
        private void RequireStage(int expected, long frame)
        { RequireBoundary(); if (observationPhase != 0 || stage != expected || frame != NativeFrame) throw Error("clock lease does not admit the requested import"); }
        internal void Complete()
        {
            try {
                RequireBoundary(); if (stage != 0 && stage != 4) throw Error("clock epoch contains a partial four-domain import");
                InvokePayload(observationPhase == 0 ? "CloseResidentRandomProducerFence" : "CloseResidentFrameProducerFence", Token); producerFenceOpen = false;
                RequireNativeAndLoader(); boundary.Complete(); closed = true;
            } catch (Exception error) { ResidentFailureEvidence.Record(error); Fault(); throw; }
        }
        internal void Fault()
        {
            if (closed || failed) return; failed = true;
            // Even if native commit fails after producer Close, no producer
            // may issue another request in this partially restored process.
            try { InvokePayload("AbortResidentRandomProducerFence", Token); }
            catch { /* Native retirement remains mandatory if payload abort fails. */ }
            finally { producerFenceOpen = false; boundary.Fault(); }
        }
        public void Dispose()
        {
            if (ownerThread != System.Threading.Thread.CurrentThread.ManagedThreadId) throw Error("backend disposal belongs to another thread");
            try { if (!closed) Fault(); boundary.Dispose(); }
            finally { if (ReferenceEquals(currentClipBackend,this)) currentClipBackend = null; }
        }
        private static InvalidOperationException Error(string message) => new InvalidOperationException("Resident clock backend: " + message);
    }
}
