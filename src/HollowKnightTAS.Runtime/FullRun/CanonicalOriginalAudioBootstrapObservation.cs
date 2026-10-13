using System;
using System.Collections;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Read the original coroutine completion markers. Never advance, cancel,
    // replace or recreate the routine or its native wait.
    internal static class CanonicalOriginalAudioBootstrapObservation
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Manager = typeof(GameManager).GetField("audioManager", Fields)
            ?? throw new MissingFieldException("GameManager.audioManager");
        private static readonly FieldInfo Music = typeof(AudioManager).GetField("applyMusicCueRoutine", Fields)
            ?? throw new MissingFieldException("AudioManager.applyMusicCueRoutine");
        private static readonly FieldInfo Atmos = typeof(AudioManager).GetField("applyAtmosCueRoutine", Fields)
            ?? throw new MissingFieldException("AudioManager.applyAtmosCueRoutine");

        internal static bool Complete(GameManager manager, out object observation)
        {
            var audio = Manager.GetValue(manager) as AudioManager
                ?? throw new InvalidOperationException("Original AudioManager is absent.");
            var music = State(audio, Music, "BeginApplyMusicCue");
            var atmos = State(audio, Atmos, "BeginApplyAtmosCue");
            observation = new { diagnosticOnly = true, music, atmos, applicationExecuted = false };
            return music.complete && atmos.complete;
        }
        private sealed class RoutineState
        {
            public string type = "null";
            public int? state;
            public bool complete;
        }
        private static RoutineState State(AudioManager audio, FieldInfo field, string method)
        {
            var routine = field.GetValue(audio);
            if (routine == null) return new RoutineState { complete = true };
            var type = routine.GetType();
            if (!(routine is IEnumerator) || type.DeclaringType != typeof(AudioManager)
                || !type.Name.StartsWith("<" + method + ">d__", StringComparison.Ordinal))
                throw new InvalidOperationException("Original audio routine family changed: " + field.Name);
            var cursor = type.GetField("<>1__state", Fields) ?? throw new MissingFieldException(type.FullName, "<>1__state");
            var owner = type.GetField("<>4__this", Fields) ?? throw new MissingFieldException(type.FullName, "<>4__this");
            if (cursor.FieldType != typeof(int) || !ReferenceEquals(owner.GetValue(routine), audio))
                throw new InvalidOperationException("Original audio routine owner or cursor layout changed.");
            var state = (int)cursor.GetValue(routine)!;
            return new RoutineState { type = type.FullName!, state = state, complete = state == -1 };
        }
    }
}
