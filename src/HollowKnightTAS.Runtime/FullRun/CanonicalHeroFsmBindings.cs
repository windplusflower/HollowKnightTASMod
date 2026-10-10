using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Core.SceneCheckpoints;
using HutongGames.PlayMaker;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class CanonicalHeroFsmBindings
    {
        private static readonly FieldInfo Raw = typeof(PlayMakerFSM).GetField("fsm", BindingFlags.Instance | BindingFlags.NonPublic)!;

        internal static int Validate(BoundaryObjectIdentity identities)
        {
            var hero = HeroController.SilentInstance ?? throw new InvalidOperationException("Canonical Hero FSM owner is absent.");
            var scenePrefix = hero.gameObject.scene.name + "/";
            var actual = hero.GetComponentsInChildren<PlayMakerFSM>(true).Select(component =>
            {
                var path = identities.Get(component);
                if (!path.StartsWith(scenePrefix, StringComparison.Ordinal) || !(Raw.GetValue(component) is Fsm fsm))
                    throw new InvalidOperationException("Canonical Hero FSM scene or raw configuration differs.");
                return new KeyValuePair<string, string>(path.Substring(scenePrefix.Length), fsm.Name);
            }).ToArray();
            CanonicalHeroFsmBindingRegistry.Validate(actual);
            return actual.Count(entry => !CanonicalHeroFsmBindingRegistry.IsCanonicalVisual(entry.Key, entry.Value));
        }
    }
}
