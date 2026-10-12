using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using InControl;
using UnityEngine;
using UnityEngine.EventSystems;

namespace HollowKnightTAS.Runtime.Input
{
    public sealed partial class FullRunMouseBridge
    {
        internal void AddCanonicalRoots(IDictionary<string, object> roots)
        {
            var state = CaptureCheckpoint();
            var modules = hkModules.Where(item => item.Module != null).OrderBy(item => ModuleIdentity(item.Module), StringComparer.Ordinal).ToArray();
            for (var index = 0; index < modules.Length; index++)
                foreach (var pair in CheckpointScalarFields.Capture(modules[index].Module, new[] { CanonicalAllowMouse }))
                    state.Modules[index].Fields.Add(pair.Key, pair.Value);
            roots.Add(CanonicalMouseCarry.Root, CanonicalMouseCarry.Copy(state));
        }
        private static readonly FieldInfo CanonicalMouseProvider = typeof(InputManager).GetField("<MouseProvider>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Original mouse provider slot is absent.");
        private static readonly FieldInfo CanonicalOverride = typeof(BaseInputModule).GetField("m_InputOverride", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Original UI mouse override slot is absent.");
        private static readonly FieldInfo CanonicalAllowMouse = typeof(HollowKnightInputModule).GetField("allowMouseInput", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new MissingFieldException("Original HK mouse flag is absent.");

        internal Action PrepareCanonicalContinuation(CheckpointMouseState inputState)
        {
            var state = CanonicalMouseCarry.Copy(inputState);
            if (!installed || disposed || !replaying || checkpointMouseImported || input == null || input.IsNativeFrameActive
                || !canonicalMouseCaches || filteredProvider == null || originalProvider == null
                || state.Enabled != Enabled || state.Width != viewportWidth || state.Height != viewportHeight)
                throw new InvalidOperationException("Canonical mouse preparation is outside its original parked replay bridge or viewport.");
            var targetInput = input; var targetProvider = filteredProvider; var targetOriginal = originalProvider;
            var modules = hkModules.Where(item => item.Module != null).OrderBy(item => ModuleIdentity(item.Module), StringComparer.Ordinal).ToArray();
            if (!state.Modules.Select(item => item.Identity).SequenceEqual(modules.Select(item => ModuleIdentity(item.Module))))
                throw new InvalidDataException("Canonical mouse original module membership or order differs.");
            var fieldSet = CheckpointMouseFields.Concat(new[] { CanonicalAllowMouse }).ToArray();
            var applications = modules.Select((item, index) => CheckpointScalarFields.PrepareApply(item.Module, fieldSet, state.Modules[index].Fields)).ToArray();
            var beforeModuleFields = modules.Select(item => CheckpointScalarFields.Capture(item.Module, fieldSet)).ToArray();
            var overrides = modules.Select(item => CanonicalOverride.GetValue(item.Module)).ToArray();
            var priorSuspended = suspended; var priorInControl = inControlState; var priorHk = hkState;
            var priorInControlButtons = previousInControlButtons; var priorHkButtons = previousHkButtons;
            var priorPosition = previousHkPosition;
            var nextInControl = state.InControl.Create(); var nextHk = state.Hk.Create();
            var consumed = false;
            void Guard()
            {
                if (disposed || !installed || !replaying || checkpointMouseImported || !ReferenceEquals(input, targetInput) || targetInput.IsNativeFrameActive
                    || !ReferenceEquals(filteredProvider, targetProvider) || !ReferenceEquals(originalProvider, targetOriginal)
                    || !ReferenceEquals(CanonicalMouseProvider.GetValue(null), targetProvider) || state.Enabled != Enabled
                    || viewportWidth != state.Width || viewportHeight != state.Height || suspended != priorSuspended
                    || !ReferenceEquals(inControlState, priorInControl) || !ReferenceEquals(hkState, priorHk)
                    || previousInControlButtons != priorInControlButtons || previousHkButtons != priorHkButtons
                    || Bits(previousHkPosition.x) != Bits(priorPosition.x) || Bits(previousHkPosition.y) != Bits(priorPosition.y))
                    throw new InvalidOperationException("Canonical mouse preparation changed before application.");
                var current = hkModules.Where(item => item.Module != null).OrderBy(item => ModuleIdentity(item.Module), StringComparer.Ordinal).ToArray();
                if (current.Length != modules.Length || current.Where((item, index) => !ReferenceEquals(item.Module, modules[index].Module)
                    || !ReferenceEquals(CanonicalOverride.GetValue(item.Module), overrides[index])).Any())
                    throw new InvalidOperationException("Canonical mouse original modules or installed overrides changed.");
                for (var index = 0; index < modules.Length; index++)
                    if (!SameFields(beforeModuleFields[index], CheckpointScalarFields.Capture(modules[index].Module, fieldSet)))
                        throw new InvalidOperationException("Canonical mouse original module cache changed before application.");
            }
            Guard();
            return () => {
                if (consumed) throw new InvalidOperationException("Canonical mouse preparation was already consumed.");
                Guard(); consumed = true; checkpointMouseImported = true;
                // Restore source caches directly. SetFrameInputEnabled can
                // clear them, and must not run during the D transaction.
                suspended = state.Suspended; inControlState = nextInControl; hkState = nextHk;
                previousInControlButtons = state.PreviousInControlButtons; previousHkButtons = state.PreviousHkButtons;
                previousHkPosition = new Vector2(state.PreviousHkX, state.PreviousHkY);
                foreach (var apply in applications) apply();
                for (var index = 0; index < modules.Length; index++)
                    if (!SameFields(state.Modules[index].Fields, CheckpointScalarFields.Capture(modules[index].Module, fieldSet)))
                        throw new InvalidOperationException("Canonical mouse original module cache readback differs.");
            };
        }
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        private static bool SameFields(IReadOnlyDictionary<string, string> first, IReadOnlyDictionary<string, string> second)
            => first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }
}
