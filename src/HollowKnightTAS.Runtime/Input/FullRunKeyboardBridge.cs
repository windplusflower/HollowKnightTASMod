using System;
using System.Collections.Generic;
using System.Linq;
using HollowKnightTAS.Core.Movie;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Input
{
    // Only explicit sequence keys are owned. Hooks are removed with the TAS input lease.
    public sealed class FullRunKeyboardBridge : IDisposable
    {
        private readonly List<Hook> hooks = new List<Hook>();
        private readonly CustomKeyState state = new CustomKeyState();
        private HashSet<short> keys = new HashSet<short>();
        private readonly Func<bool> active;
        public FullRunKeyboardBridge(Func<bool> active) { this.active = active; }
        public void Configure(MovieV2Document movie)
        {
            keys = new HashSet<short>(CustomKeyInput.Keys(movie));
            if (keys.Count == 0 || hooks.Count != 0) return;
            try
            {
                Install("GetKey", state.Held);
                Install("GetKeyDown", state.Down);
                Install("GetKeyUp", state.Up);
                Modding.Logger.LogDebug("[HKTAS] Custom keyboard hooks installed: " + string.Join(",", keys));
            }
            catch { Dispose(); throw; }
        }
        private void Install(string name, Func<short, bool> read)
        {
            var method = typeof(UnityEngine.Input).GetMethod(name, new[] { typeof(KeyCode) })
                ?? throw new MissingMethodException("UnityEngine.Input." + name);
            hooks.Add(new Hook(method, (Func<Func<KeyCode, bool>, KeyCode, bool>)((original, key) =>
                keys.Contains((short)key) ? active() && read((short)key) : original(key))));
            var stringMethod = typeof(UnityEngine.Input).GetMethod(name, new[] { typeof(string) })
                ?? throw new MissingMethodException("UnityEngine.Input." + name + "(string)");
            hooks.Add(new Hook(stringMethod, (Func<Func<string, bool>, string, bool>)((original, nameValue) =>
            {
                var key = ParseName(nameValue);
                return key.HasValue && keys.Contains(key.Value) ? active() && read(key.Value) : original(nameValue);
            })));
        }
        private static short? ParseName(string name)
        {
            if (name == null) return null;
            var normalized = name.Replace(" ", "");
            switch (normalized.ToLowerInvariant())
            {
                case "leftctrl": return 306; case "rightctrl": return 305;
                case "up": return 273; case "down": return 274;
                case "right": return 275; case "left": return 276;
            }
            if (normalized.Length == 1)
            {
                var code = (short)char.ToLowerInvariant(normalized[0]);
                if (CustomKeyInput.Names.ContainsKey(code)) return code;
            }
            foreach (var item in CustomKeyInput.Names)
                if (string.Equals(item.Value, normalized, StringComparison.OrdinalIgnoreCase)) return item.Key;
            if (normalized.StartsWith("[") && normalized.EndsWith("]"))
            {
                var pad = normalized.Substring(1, normalized.Length - 2);
                if (pad.Length == 1 && pad[0] >= '0' && pad[0] <= '9') return (short)(256 + pad[0] - '0');
                switch (pad) { case ".": return 266; case "/": return 267; case "*": return 268;
                    case "-": return 269; case "+": return 270; case "enter": return 271; case "equals": return 272; }
            }
            return null;
        }
        public void Prepare(IEnumerable<GameInputSample> samples) => state.Prepare(samples);
        public void Complete() => state.Complete();
        internal short[] CapturePrevious() => state.CapturePrevious();
        internal short[] CaptureCurrent() => state.CaptureCurrent();
        internal void Restore(short[] previous, short[] current)
        {
            if (previous.Concat(current).Any(key => !keys.Contains(key)))
                throw new System.IO.InvalidDataException("Checkpoint keyboard keys differ from the Movie.");
            state.Restore(previous, current);
        }
        public void Dispose()
        {
            for (var i = hooks.Count - 1; i >= 0; i--) hooks[i].Dispose();
            hooks.Clear();
        }
    }
}
