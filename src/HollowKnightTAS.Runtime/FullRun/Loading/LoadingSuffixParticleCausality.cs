using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class LoadingSuffixParticleCausality : IDisposable
    {
        private readonly MemoryStream output = new MemoryStream();
        private readonly Func<UnityEngine.Object, string> identity;
        private Dictionary<string, string> previous = new Dictionary<string, string>(StringComparer.Ordinal);
        private int sequence;
        internal LoadingSuffixParticleCausality(Func<UnityEngine.Object, string> identity) => this.identity = identity;
        internal void Observe(string phase, long movie, long native)
        {
            var systems = Resources.FindObjectsOfTypeAll<ParticleSystem>().Where(system => system != null
                && system.gameObject.scene.IsValid() && system.gameObject.scene.isLoaded).ToArray();
            if (systems.Length > 4096) throw new InvalidOperationException("Suffix particle seed owner inventory exceeded its bound.");
            var next = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var system in systems) next.Add(identity(system), JsonConvert.SerializeObject(new {
                seed = system.randomSeed, autoSeed = system.useAutoRandomSeed, playing = system.isPlaying,
                active = system.gameObject.activeInHierarchy, activeSelf = system.gameObject.activeSelf }));
            var changed = next.Where(item => !previous.TryGetValue(item.Key, out var value) || value != item.Value)
                .OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => new { owner = item.Key, state = JsonConvert.DeserializeObject(item.Value) }).ToArray();
            var removed = previous.Keys.Where(key => !next.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
            var bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { diagnosticOnly = true, sequence = sequence++,
                phase, movie, native, owners = next.Count, changed, removed, completeStateCoverage = false }) + "\n");
            if (sequence > 256 || output.Length + bytes.Length > 1024 * 1024)
                throw new InvalidOperationException("Suffix particle seed delta trace exceeded its bound.");
            output.Write(bytes, 0, bytes.Length); previous = next;
        }
        internal void Export(string directory)
        {
            using var file = new FileStream(Path.Combine(directory, "suffix-particle-seeds.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            output.WriteTo(file);
        }
        public void Dispose() => output.Dispose();
    }
}
