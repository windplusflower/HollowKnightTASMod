"""Freeze exact Hero FSM owner bindings from the four pure Phase 0 samples."""
import argparse
import collections
import json
import re
from pathlib import Path


def bindings(boundary):
    objects = {obj['id'] for obj in boundary['objects']}
    heroes = [name for name in objects if re.fullmatch(r'DontDestroyOnLoad/root\[\d+\]:Knight', name)]
    if len(heroes) != 1:
        raise ValueError('Expected exactly one original Hero root')
    hero = heroes[0]
    siblings = collections.defaultdict(list)
    for name in objects:
        if name.startswith(hero + '/'):
            parent, child = name.rsplit('/', 1)
            match = re.fullmatch(r'(.*)\[(\d+)\]', child)
            siblings[parent, match[1]].append((int(match[2]), name))
    ranks = {name: rank for rows in siblings.values() for rank, (_, name) in enumerate(sorted(rows))}

    def stable(name):
        if name == hero:
            return 'Knight[0]'
        parent, child = name.rsplit('/', 1)
        return stable(parent) + '/' + child[:child.rfind('[')] + f'[{ranks[name]}]'

    result = {}
    for fsm in boundary['fsms']:
        if fsm['id'].startswith(hero + ':') or fsm['id'].startswith(hero + '/'):
            owner, component = fsm['id'].rsplit(':', 1)
            key = stable(owner) + ':' + component
            if key in result:
                raise ValueError('Repeated Hero FSM identity: ' + key)
            result[key] = fsm['name']
    return dict(sorted(result.items()))


def generate(paths, output):
    frozen = None
    count = 0
    for path in paths:
        doc = json.loads(path.read_text(encoding='utf-8-sig'))
        if doc.get('schema') != 1 or not doc.get('diagnosticOnly') or not doc.get('boundaries'):
            raise ValueError('Invalid Phase 0 inventory: ' + str(path))
        for boundary in doc['boundaries']:
            current = bindings(boundary)
            if frozen is None:
                frozen = current
            if current != frozen:
                raise ValueError('Hero bindings changed in ' + str(path) + '/B' + str(boundary['ordinal']))
            count += 1
    if not frozen or count != 13 or len(frozen) != 160:
        raise ValueError('Expected all four pure samples, 13 boundaries and 160 exact Hero FSM bindings')
    rows = '\n'.join('            { ' + json.dumps(key) + ', ' + json.dumps(name) + ' },' for key, name in frozen.items())
    code = '''// Generated from all 13 pure Phase 0 boundaries. Bindings only;
// state/action/variable coverage is a separate carry codec requirement.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HollowKnightTAS.Core.SceneCheckpoints
{
    public static class CanonicalHeroFsmBindingRegistry
    {
        private static readonly Dictionary<string, string> Bindings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
''' + rows + '''
        };
        public const int Count = 160;
        public static bool IsCanonicalVisual(string path, string name) =>
            path != null && Bindings.TryGetValue(path, out var expected) && expected == name
            && path.StartsWith("Knight[0]/HeroLight[0]:PlayMakerFSM[", StringComparison.Ordinal)
            && (name == "HeroLight Control" || name == "color_fader");

        public static void Validate(IEnumerable<KeyValuePair<string, string>> actual)
        {
            if (actual == null) throw new ArgumentNullException(nameof(actual));
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in actual)
            {
                if (found.Count >= Count || entry.Key == null || entry.Value == null || found.ContainsKey(entry.Key)
                    || !Bindings.TryGetValue(entry.Key, out var expected) || expected != entry.Value)
                    throw new InvalidOperationException("Canonical Hero FSM binding differs: " + entry.Key);
                found.Add(entry.Key, entry.Value);
            }
            if (found.Count != Count)
                throw new InvalidOperationException("Canonical Hero FSM binding is missing: "
                    + Bindings.Keys.First(path => !found.ContainsKey(path)));
        }

        public static IReadOnlyDictionary<string, string> Snapshot() => new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(Bindings, StringComparer.Ordinal));
    }
}
'''
    if output.exists():
        raise ValueError('Choose a fresh registry file; do not overwrite a frozen contract')
    output.write_text(code, encoding='utf-8')
    print(json.dumps({'boundaries': count, 'bindings': len(frozen), 'output': str(output)}))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('inventory', nargs='+', type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    generate(args.inventory, args.output)
