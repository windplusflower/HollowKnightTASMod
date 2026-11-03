using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using USceneManager = UnityEngine.SceneManagement.SceneManager;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed partial class LoadingSerialSceneProbe
    {
        // The old policy was always absent, so this bridge already rejected
        // every caller. Keep that rejection explicit after removing its type.
        internal static GameManager.SceneLoadInfo AddCanonicalLoadInfo(IDictionary<string, object> roots)
            => throw new InvalidDataException("Legacy canonical SceneLoadInfo bridge is retired; use the resident boundary coordinator.");
        internal static Action PrepareCanonicalLoadScalars(CanonicalSceneLoadCarry.Data data)
        {
            // Recheck the actual original ownership and parked phase. Do not
            // run Begin, an iterator, or any SceneLoad entry callback.
            AddCanonicalLoadInfo(new Dictionary<string, object>(StringComparer.Ordinal));
            var self = instance!; var load = self.transition!; var routine = self.checkpointTransitionRoutine!;
            var info = self.checkpointLoadInfo!; var sourceBefore = self.sourceName;
            var apply = PrepareParkedLoadState(CanonicalSceneLoadCarry.Decode(data), self);
            var source = data.SourceScene;
            var consumed = false;
            return () => {
                if (consumed) throw new InvalidOperationException("Canonical SceneLoad preparation was already consumed.");
                if (!ReferenceEquals(instance, self) || !self.awaitingBoundary || !self.sourceUnloaded || self.source.isLoaded
                    || !ReferenceEquals(self.transition, load) || !ReferenceEquals(self.checkpointTransitionRoutine, routine)
                    || !ReferenceEquals(self.checkpointLoadInfo, info) || self.sourceName != sourceBefore)
                    throw new InvalidOperationException("Canonical SceneLoad preparation changed before scalar application.");
                consumed = true; apply(); self.sourceName = source;
            };
        }
        internal static CanonicalSceneLoadCallbackBindings ReadCanonicalCallbackBindings()
        {
            AddCanonicalLoadInfo(new Dictionary<string, object>(StringComparer.Ordinal));
            var self = instance!;
            return new CanonicalSceneLoadCallbackBindings(self.transition!, GameManager.instance, self.checkpointLoadInfo!,
                Closure(self.checkpointTransitionRoutine!), self.sourceName);
        }
    }
}
