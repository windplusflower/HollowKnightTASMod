using System.Collections.Generic;
using System.Linq;
namespace HollowKnightTAS.Core.Movie
{
    public sealed class CustomKeyState
    {
        private HashSet<short> previous = new HashSet<short>();
        private HashSet<short> current = new HashSet<short>();
        public void Prepare(IEnumerable<GameInputSample> samples)
        { current = new HashSet<short>(samples.Where(s => s.Channel == GameInputChannel.CustomKey && s.Values[1] != 0).Select(s => s.Values[0])); }
        public void Complete() { previous = new HashSet<short>(current); }
        public bool Held(short key) => current.Contains(key);
        public bool Down(short key) => current.Contains(key) && !previous.Contains(key);
        public bool Up(short key) => !current.Contains(key) && previous.Contains(key);
        public short[] CapturePrevious() => previous.OrderBy(key => key).ToArray();
        public short[] CaptureCurrent() => current.OrderBy(key => key).ToArray();
        public void Restore(IEnumerable<short> previousKeys, IEnumerable<short> currentKeys)
        {
            var prior = previousKeys.ToArray(); var next = currentKeys.ToArray();
            if (!prior.SequenceEqual(prior.Distinct().OrderBy(key => key))
                || !next.SequenceEqual(next.Distinct().OrderBy(key => key))
                || prior.Concat(next).Any(key => !CustomKeyInput.Names.ContainsKey(key)))
                throw new System.IO.InvalidDataException("Invalid checkpoint keyboard state.");
            previous = new HashSet<short>(prior); current = new HashSet<short>(next);
        }
    }
}
