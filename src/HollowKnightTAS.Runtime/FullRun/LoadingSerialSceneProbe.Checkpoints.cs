using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingSerialSceneProbe
    {
        private IEnumerator? checkpointTransitionRoutine;
        private bool checkpointTransitionFinished;
        private Tuple<string, string>? checkpointBossBindings;
        internal static Tuple<string, string>? CheckpointBossBindings => instance?.checkpointBossBindings;
        private IEnumerator TrackCheckpointTransition(IEnumerator routine)
        {
            checkpointTransitionRoutine = routine;
            checkpointTransitionFinished = false;
            try { while (routine.MoveNext()) yield return routine.Current; }
            // The GameManager iterator only opens the activation gate and then
            // finishes, before serial unloading reaches C. Its closure remains
            // live through SceneLoad's event callbacks. Retain it until Finish.
            finally { checkpointTransitionFinished = true; (routine as IDisposable)?.Dispose(); }
        }
        private static FieldInfo[] CheckpointLoadFields(Type type) => CheckpointScalarFields.Declared(type).Where(field =>
            field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType == typeof(string)
            || Nullable.GetUnderlyingType(field.FieldType)?.IsPrimitive == true).ToArray();
        private static object Closure(IEnumerator routine) => CheckpointScalarFields.Declared(routine.GetType())
            .Single(field => field.Name == "<>8__1").GetValue(routine) ?? throw new InvalidOperationException("Scene transition closure is unavailable.");
        private static Array PhaseArray(SceneLoad load) => (Array)typeof(SceneLoad)
            .GetField("phaseInfos", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(load);
        internal static CheckpointSceneLoadState CaptureCheckpointLoadState()
        {
            GetCheckpointBoundary();
            return CaptureParkedLoadState(instance!);
        }
        private static CheckpointSceneLoadState CaptureParkedLoadState(LoadingSerialSceneProbe self)
        {
            var routine = self.checkpointTransitionRoutine ?? throw new InvalidOperationException("Scene transition iterator is unavailable.");
            if (!self.checkpointTransitionFinished) throw new InvalidOperationException("Scene transition activation iterator has not finished at C.");
            var closure = Closure(routine);
            return new CheckpointSceneLoadState
            {
                TransitionIteratorFinished = true,
                IteratorType = routine.GetType().FullName!, ClosureType = closure.GetType().FullName!,
                LoadFields = CheckpointScalarFields.Capture(self.transition, CheckpointLoadFields(typeof(SceneLoad))),
                IteratorFields = CheckpointScalarFields.Capture(routine, CheckpointLoadFields(routine.GetType())),
                ClosureFields = CheckpointScalarFields.Capture(closure, CheckpointLoadFields(closure.GetType())),
                Phases = PhaseArray(self.transition!).Cast<object>().Select(phase => CheckpointScalarFields.Capture(phase, CheckpointLoadFields(phase.GetType()))).ToArray()
            };
        }
        internal static Action PrepareCheckpointLoadState(CheckpointSceneLoadState state)
        {
            GetCheckpointBoundary(); return PrepareParkedLoadState(state, instance!);
        }
        private static Action PrepareParkedLoadState(CheckpointSceneLoadState state, LoadingSerialSceneProbe self)
        {
            var routine = self.checkpointTransitionRoutine ?? throw new InvalidOperationException("Restore scene transition iterator is unavailable.");
            var closure = Closure(routine); var phaseArray = PhaseArray(self.transition!); var phases = phaseArray.Cast<object>().ToArray();
            if (!state.TransitionIteratorFinished || !self.checkpointTransitionFinished
                || routine.GetType().FullName != state.IteratorType || closure.GetType().FullName != state.ClosureType || phases.Length != state.Phases.Length)
                throw new InvalidDataException("Scene load continuation types or phases differ.");
            var applications = new List<Action>
            {
                CheckpointScalarFields.PrepareApply(self.transition, CheckpointLoadFields(typeof(SceneLoad)), state.LoadFields),
                CheckpointScalarFields.PrepareApply(routine, CheckpointLoadFields(routine.GetType()), state.IteratorFields),
                CheckpointScalarFields.PrepareApply(closure, CheckpointLoadFields(closure.GetType()), state.ClosureFields)
            };
            for (var i = 0; i < phases.Length; i++)
            {
                var index = i; var apply = CheckpointScalarFields.PrepareApply(phases[i], CheckpointLoadFields(phases[i].GetType()), state.Phases[i]);
                applications.Add(() => { apply(); if (phases[index].GetType().IsValueType) phaseArray.SetValue(phases[index], index); });
            }
            return () => { foreach (var application in applications) application(); };
        }
    }
}
