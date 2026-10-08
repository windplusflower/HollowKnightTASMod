using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HollowKnightTAS.Core.Movie
{
    // Unity KeyCode numbers are stable; keep Core independent of Unity assemblies.
    public static class CustomKeyInput
    {
        public static readonly IReadOnlyDictionary<short, string> Names = BuildNames();
        private static IReadOnlyDictionary<short, string> BuildNames()
        {
            var keys = new SortedDictionary<short, string>();
            for (short i = 97; i <= 122; i++) keys[i] = ((char)i).ToString().ToUpperInvariant();
            for (short i = 48; i <= 57; i++) keys[i] = "Alpha" + (char)i;
            for (short i = 282; i <= 296; i++) keys[i] = "F" + (i - 281);
            for (short i = 256; i <= 265; i++) keys[i] = "Keypad" + (i - 256);
            var codes = new short[] {8,9,13,27,32,39,44,45,46,47,59,61,91,92,93,96,127,266,267,268,269,270,271,272,273,274,275,276,277,278,279,280,281,300,301,302,303,304,305,306,307,308};
            var names = new[] {"Backspace","Tab","Return","Escape","Space","Quote","Comma","Minus","Period","Slash","Semicolon","Equals","LeftBracket","Backslash","RightBracket","BackQuote","Delete","KeypadPeriod","KeypadDivide","KeypadMultiply","KeypadMinus","KeypadPlus","KeypadEnter","KeypadEquals","UpArrow","DownArrow","RightArrow","LeftArrow","Insert","Home","End","PageUp","PageDown","Numlock","CapsLock","ScrollLock","RightShift","LeftShift","RightControl","LeftControl","RightAlt","LeftAlt"};
            for (var i = 0; i < codes.Length; i++) keys[codes[i]] = names[i];
            return new System.Collections.ObjectModel.ReadOnlyDictionary<short, string>(keys);
        }
        public static string Action(short key) => "Key:" + key.ToString(CultureInfo.InvariantCulture);
        public static bool TryAction(string action, out short key)
        {
            key = 0;
            return action.StartsWith("Key:", StringComparison.Ordinal)
                && short.TryParse(action.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out key) && Names.ContainsKey(key);
        }
        public static IEnumerable<short> Keys(MovieV2Document movie) => movie.Header.CustomKeys;
        // Declared keys plus every key that the runs actually sample.
        public static MovieV2Header WithUsedKeys(MovieV2Header header, IEnumerable<short> declared, IEnumerable<NativeFrameRun> runs)
            => header.WithCustomKeys(declared.Concat(runs.SelectMany(r => r.Samples)
                .Where(s => s.Channel == GameInputChannel.CustomKey).Select(s => s.Values[0])).Distinct().OrderBy(k => k).ToArray());
        public static GameInputSample Sample(short key, bool held) => new GameInputSample(GameInputChannel.CustomKey,
            new short[] { key, held ? short.MaxValue : (short)0 }, null);
        public static void Validate(IReadOnlyList<GameInputSample> samples)
        {
            var seen = new HashSet<short>();
            foreach (var sample in samples.Where(s => s.Channel == GameInputChannel.CustomKey))
                if (sample.Values.Count != 2 || !Names.ContainsKey(sample.Values[0])
                    || (sample.Values[1] != 0 && sample.Values[1] != short.MaxValue)
                    || sample.Mouse != null || sample.PressedMask != 0 || sample.ReleasedMask != 0
                    || !seen.Add(sample.Values[0]))
                    throw new InvalidDataException("Invalid or duplicate custom key sample.");
        }
    }
}
