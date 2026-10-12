using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Register the exact existing diagnostic listeners at their own factories.
    // These keep their current process/log owners; they are not game-state D
    // and must never be mistaken for arbitrary extra gameplay callbacks.
    internal static class CanonicalSceneLoadDiagnosticRegistry
    {
        private static readonly Dictionary<SceneLoad, List<Registration>> records = new Dictionary<SceneLoad, List<Registration>>();
        internal sealed class Registration
        {
            internal readonly object Owner;
            internal readonly IReadOnlyDictionary<string, Delegate> Callbacks;
            internal Registration(object owner, Dictionary<string, Delegate> callbacks)
            { Owner = owner; Callbacks = new ReadOnlyDictionary<string, Delegate>(callbacks); }
        }
        internal static Action Register(SceneLoad load, object owner, Dictionary<string, Delegate> callbacks)
        {
            var type = owner.GetType();
            if (type.Assembly != typeof(CanonicalSceneLoadDiagnosticRegistry).Assembly
                || type != typeof(LoadingGameplayOracle) && type != typeof(LoadingBoundaryProbe)
                || !callbacks.Keys.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(new[] {
                    "ActivationComplete", "Complete", "FetchComplete", "Finish", "StartCalled", "WillActivate" })
                || callbacks.Values.Any(value => value == null || value.GetInvocationList().Length != 1))
                throw new InvalidDataException("Scene-load diagnostic registration is outside the known observer factories.");
            if (!records.TryGetValue(load, out var list)) {
                if (records.Count >= 64) throw new InvalidDataException("Scene-load diagnostic owner bound exceeded.");
                records.Add(load, list = new List<Registration>());
            }
            if (list.Count >= 2 || list.Any(item => ReferenceEquals(item.Owner, owner)))
                throw new InvalidDataException("Scene-load diagnostic observer membership is duplicated or unbounded.");
            var item = new Registration(owner, new Dictionary<string, Delegate>(callbacks, StringComparer.Ordinal)); list.Add(item);
            return () => { list.Remove(item); if (list.Count == 0) records.Remove(load); };
        }
        internal static Registration[] Read(SceneLoad load) => records.TryGetValue(load, out var list) ? list.ToArray() : Array.Empty<Registration>();
    }
}
