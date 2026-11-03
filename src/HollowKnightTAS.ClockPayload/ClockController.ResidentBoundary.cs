using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HollowKnightTAS.Core.SceneCheckpoints;
using UnityEngine;

namespace HollowKnightTAS.ClockPayload
{
    public static partial class ClockController
    {
        private static ulong residentRandomToken, residentObserverToken, residentParticleToken;
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_RequireResidentBoundary(ulong token, ulong expectedFrame);
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_RequireResidentWritableBoundary(ulong token, ulong expectedFrame);
        [DllImport("HollowKnightTAS.ClockBridge.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int HktasClockBridge_ExportResidentContinuation(ulong token, [Out] byte[] bytes, uint size);

        internal static void RequireResidentClockBoundary(ulong token, bool importing)
        {
            if (token == 0 || !fullRunRenderIsolationEnabled || fullRunRenderEntered
                || GetCurrentThreadId() != (uint)StartupHookThreadId)
                throw new InvalidOperationException("Resident clock operation is outside its owned native boundary.");
            var frame = HktasClockBridge_GetCompletedPlayerLoops();
            if (HktasClockBridge_RequireResidentBoundary(token, frame) != 1
                || importing && HktasClockBridge_RequireResidentWritableBoundary(token, frame) != 1)
                throw new InvalidOperationException("Native resident epoch does not admit this clock operation.");
            RequireClockObserverHealth(importing);
        }
        private static byte[] ReadResidentNativeClock(ulong token)
        {
            RequireResidentClockBoundary(token, false); var bytes = new byte[1128];
            if (HktasClockBridge_ExportResidentContinuation(token, bytes, (uint)bytes.Length) != 1)
                throw new InvalidOperationException("Resident native clock readback failed.");
            return bytes;
        }
        public static byte[] ExportResidentRandomState(ulong token)
        { RequireResidentClockBoundary(token, false); var bytes = CapturePortableRandomState(); RequireResidentClockBoundary(token, false); return bytes; }
        public static byte[] ExportResidentClockObserver(ulong token)
        {
            RequireResidentClockBoundary(token, false); var state = CaptureClockObserver();
            var frame = HktasClockBridge_GetCompletedPlayerLoops();
            if (frame > long.MaxValue) throw new InvalidOperationException("Resident native frame overflowed.");
            state.RequireNativeReadback(ReadResidentNativeClock(token), (long)frame, Time.frameCount);
            state.RequireReadback(CaptureClockObserver()); RequireResidentClockBoundary(token, false); return state.Encode();
        }
        public static uint[] ExportResidentParticleRandomState(ulong token)
        {
            RequireResidentClockBoundary(token, false); var words = EarlyModWallClockBootstrap.CaptureParticleRandomState();
            if (words.Length != 4 || words.All(word => word == 0)) throw new InvalidOperationException("Resident particle RNG is absent.");
            RequireResidentClockBoundary(token, false); return words;
        }
        public static void ImportResidentRandomState(ulong token, byte[] bytes, long expectedNative)
        {
            RequireResidentClockBoundary(token, true);
            if (token <= residentRandomToken || expectedNative < 0 || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNative)
                throw new InvalidOperationException("Resident random import was repeated or its native cursor changed.");
            var state = CheckpointRandomState.Decode(bytes); residentRandomToken = token;
            ApplyPortableRandomState(state);
            if (!CapturePortableRandomState().SequenceEqual(bytes)) throw new InvalidDataException("Resident random words and observer fields did not read back exactly.");
            RequireResidentClockBoundary(token, true);
        }
        public static void ImportResidentClockObserver(ulong token, byte[] bytes, long expectedNative)
        {
            RequireResidentClockBoundary(token, true);
            if (token <= residentObserverToken || residentRandomToken != token || expectedNative < 0
                || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNative)
                throw new InvalidOperationException("Resident clock observer import order or native cursor differs.");
            var state = CheckpointClockObserverState.Decode(bytes);
            if (state.SceneClockExclusionActive || state.AdvanceSequence != Volatile.Read(ref deterministicClockAdvanceSequence))
                throw new InvalidDataException("Resident clock observer source sequence differs.");
            state.RequireNativeReadback(ReadResidentNativeClock(token), expectedNative, Time.frameCount);
            residentObserverToken = token;
            deterministicClockUnityFrameObserved = state.UnityFrameObserved;
            deterministicClockLastUnityFrameCount = state.LastUnityFrameCount;
            Volatile.Write(ref deterministicClockAdvanceSequence, state.AdvanceSequence);
            sceneClockExclusionActive = state.SceneClockExclusionActive;
            state.RequireReadback(CaptureClockObserver());
            state.RequireNativeReadback(ReadResidentNativeClock(token), expectedNative, Time.frameCount);
            RequireResidentClockBoundary(token, true);
        }
        public static void ImportResidentParticleRandomState(ulong token, uint[] words, long expectedNative)
        {
            RequireResidentClockBoundary(token, true);
            if (token <= residentParticleToken || residentObserverToken != token || residentRandomToken != token || expectedNative < 0
                || HktasClockBridge_GetCompletedPlayerLoops() != (ulong)expectedNative)
                throw new InvalidOperationException("Resident particle RNG import order or native cursor differs.");
            residentParticleToken = token; EarlyModWallClockBootstrap.RestoreResidentParticleRandomState(words, token);
            if (!EarlyModWallClockBootstrap.CaptureParticleRandomState().SequenceEqual(words)) throw new InvalidDataException("Resident particle RNG readback differs.");
            RequireResidentClockBoundary(token, true);
        }
    }
}
