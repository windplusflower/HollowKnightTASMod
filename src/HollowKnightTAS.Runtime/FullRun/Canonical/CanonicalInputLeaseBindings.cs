using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using InControl;

namespace HollowKnightTAS.Runtime.FullRun
{
    // Source objects are retained by both the SDK and the TAS lease. Validate
    // the proposed SDK graph against those actual target objects, not against
    // equal-looking newly allocated sources. Never calls Add/ClearBindings.
    internal static class CanonicalInputLeaseBindings
    {
        internal static int Validate(CanonicalCarryGraphCodec.PreparedView view, string setBinding,
            PlayerActionSet set, object channel, PlayerAction[] actions, BindingSource[] injected,
            BindingSource[][] originals)
        {
            if (actions == null || injected == null || originals == null || actions.Length == 0 || actions.Length > 1024
                || injected.Length != actions.Length || originals.Length != actions.Length || !ReferenceEquals(view.Root(setBinding), set))
                throw Invalid("lease shape or set identity");
            var seenActions = new HashSet<object>(new References());
            var seenSources = new HashSet<object>(new References());
            for (var index = 0; index < actions.Length; index++)
            {
                var action = actions[index]; var source = injected[index];
                if (ReferenceEquals(action, null) || ReferenceEquals(source, null) || originals[index] == null || originals[index].Length > 256
                    || !seenActions.Add(action) || !seenSources.Add(source)) throw Invalid("duplicated or absent retained slot");
                var ordinal = index.ToString(CultureInfo.InvariantCulture);
                if (!ReferenceEquals(view.Root("R6/InputTAS-source/" + setBinding + "/" + ordinal), source)
                    || !ReferenceEquals(view.Field(action, typeof(PlayerAction), "<Owner>k__BackingField"), set)
                    || !Equals(view.Field(source, source.GetType(), "channel"), channel)
                    || !Equals(view.Field(source, source.GetType(), "index"), index)
                    || !ReferenceEquals(view.Field(source, typeof(BindingSource), "<BoundTo>k__BackingField"), action))
                    throw Invalid("injected source ownership/channel/index");
                if (!(view.Field(source, source.GetType(), "value") is float value) || float.IsNaN(value)
                    || float.IsInfinity(value) || value < -1f || value > 1f) throw Invalid("injected source value");
                var regular = view.Field(action, typeof(PlayerAction), "regularBindings");
                var visible = view.Field(action, typeof(PlayerAction), "visibleBindings");
                CheckList(regular, source); CheckList(visible, source);
                var unfiltered = view.Field(action, typeof(PlayerAction), "unfilteredBindings");
                var bindings = view.Field(action, typeof(PlayerAction), "bindings");
                var wrapperType = typeof(System.Collections.ObjectModel.ReadOnlyCollection<BindingSource>);
                if (unfiltered == null || bindings == null
                    || !ReferenceEquals(view.Field(unfiltered, wrapperType, "list"), regular)
                    || !ReferenceEquals(view.Field(bindings, wrapperType, "list"), visible))
                    throw Invalid("readonly SDK view alias");
                for (var slot = 0; slot < originals[index].Length; slot++)
                {
                    var original = originals[index][slot];
                    if (ReferenceEquals(original, null) || !ReferenceEquals(view.Root("R6/InputOriginal-source/" + setBinding + "/" + ordinal
                        + "/" + slot.ToString(CultureInfo.InvariantCulture)), original)
                        || view.Field(original, typeof(BindingSource), "<BoundTo>k__BackingField") != null)
                        throw Invalid("retained original source identity or detached owner");
                }
            }
            return actions.Length;

            void CheckList(object? owner, BindingSource source)
            {
                if (owner == null || owner.GetType() != typeof(List<BindingSource>)
                    || !Equals(view.Field(owner, typeof(List<BindingSource>), "_size"), 1)
                    || !(view.Field(owner, typeof(List<BindingSource>), "_items") is BindingSource[] items)
                    || items.Length < 1 || !ReferenceEquals(items[0], source))
                    throw Invalid("SDK binding list does not retain its injected source");
                for (var index = 1; index < items.Length; index++)
                    if (!ReferenceEquals(items[index], null)) throw Invalid("SDK binding list has an unregistered trailing source");
            }
        }
        private static InvalidDataException Invalid(string detail) => new InvalidDataException("Canonical input lease binding differs: " + detail);
        private sealed class References : IEqualityComparer<object>
        {
            public new bool Equals(object? first, object? second) => ReferenceEquals(first, second);
            public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
        }
    }
}
