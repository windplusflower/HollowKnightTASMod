"""Classify existing lossless K3 evidence; never changes comparison or D data."""
import argparse
import base64
import collections
import gzip
import hashlib
import json
import re
from pathlib import Path


def read_json(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def samples(path):
    return [row for line in path.read_text(encoding='utf-8-sig').splitlines()
            if (row := json.loads(line)).get('kind') == 'sample']


def inventory(row):
    value = row['inventory']
    raw = gzip.decompress(base64.b64decode(value['gzipBase64']))
    assert len(raw) == value['bytes'] and len(raw) <= 16 * 1024 * 1024
    assert hashlib.sha256(raw).hexdigest() == value['sha256']
    result = json.loads(raw)
    assert result['format'] == 'sorted-utf16-prefix-root-names-v1'
    names, previous = [], b''
    for entry in result['names']:
        assert entry['prefix'] * 2 <= len(previous)
        previous = previous[:entry['prefix'] * 2] + entry['suffix'].encode('utf-16-le')
        names.append(previous.decode('utf-16-le'))
    assert names == sorted(names) and len(names) == len(set(names))
    return result, names


def descriptive_key(name):
    # A lookup between two documented diagnostic naming formats, NOT a new
    # equality rule. Preserve component/state/action ordinals and report any
    # collision instead of matching different source owners by position.
    name = name.replace('DontDestroyOnLoad:', 'DontDestroyOnLoad/', 1).replace('|', ':', 1)
    match = re.search(r':(?=[A-Za-z_\.0-9+]+\[\d+\](?:/|$))', name)
    if not match:
        return None
    return re.sub(r'\[\d+\]', '', name[:match.start()]) + name[match.start():]


def category(name):
    if '/TMP SubMesh [perpetua_tmpro Material]' in name:
        return 'eight-original-HUD-TMP-submesh-children'
    if '/Charms' in name and '/action[' in name:
        return 'thirteen-original-Charms-FSM-actions'
    if '/SoulOrb_fill' in name and '|iTweenFSMEvents' in name:
        return 'SoulOrb-original-iTween-events-component'
    if name.startswith('asset:UnityEngine.Mesh:'):
        return 'anonymous-native-Mesh-assets-unresolved-owner'
    if name.startswith('asset:UnityEngine.Material:'):
        return 'dynamic-TMP-Material-asset-unresolved-owner'
    return 'unclassified'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('evidence')
    args = parser.parse_args()
    path = Path(args.evidence)
    source = samples(path / 'source/run-01/actual-d-source-suffix-oracle/loading-gameplay-oracle.jsonl')
    target = samples(path / 'target/run-01/actual-d-target-suffix-oracle/loading-gameplay-oracle.jsonl')
    assert len(source) == len(target) == 3
    boundary = read_json(path / 'source/run-01/boundary-digest-2.json')
    index = collections.defaultdict(list)
    for row in boundary['Roots']:
        key = descriptive_key(row['id'])
        if key:
            index[key].append(row)
    report = dict(diagnosticOnly=True, comparisonPassed=False, equalityRulesUnchanged=True,
                  nativeAnonymousAssetOwnerIdentityNotInferred=True, samples=[])
    for left, right in zip(source, target):
        assert (left['movieFrame'], left['nativeFrame']) == (right['movieFrame'], right['nativeFrame'])
        _, a = inventory(left)
        _, b = inventory(right)
        only_a, only_b = sorted(set(a) - set(b)), sorted(set(b) - set(a))
        evidence = []
        for name in only_a:
            lookup = name
            if name.endswith('/native-hierarchy') and '/TMP SubMesh ' in name:
                lookup = name.removesuffix('/native-hierarchy') + '|TMPro.TMP_SubMesh[0]'
            key = descriptive_key(lookup)
            candidates = index.get(key, []) if key else []
            evidence.append(dict(identity=name, category=category(name),
                                 boundaryLookupCount=len(candidates),
                                 existedAtSourceD=len(candidates) == 1,
                                 boundaryMatches=[dict(identity=row['id'], type=row['type']) for row in candidates]))
        report['samples'].append(dict(movieFrame=left['movieFrame'], nativeFrame=left['nativeFrame'],
            sourceRootCount=left['state']['rootCount'], targetRootCount=right['state']['rootCount'],
            sourceBehaviours=left['state']['totalBehaviours'], targetBehaviours=right['state']['totalBehaviours'],
            blackSource=sum('Spell Fluke Black(Clone)' in name for name in a),
            blackTarget=sum('Spell Fluke Black(Clone)' in name for name in b),
            particleManagerRandomEqual=left['state']['particleManagerRandom'] == right['state']['particleManagerRandom'],
            gameplayRngEqual=left['state']['gameplayRng'] == right['state']['gameplayRng'],
            nativeSchedulerEqual=left['state']['nativeScheduler'] == right['state']['nativeScheduler'],
            sourceOnly=only_a, targetOnly=only_b,
            categories=dict(collections.Counter(category(name) for name in only_a)), sourceBoundaryEvidence=evidence))
    destination = path / 'boss-context-short-window-analysis.json'
    destination.write_text(json.dumps(report, indent=2), encoding='utf-8')
    first = report['samples'][0]
    print(json.dumps(dict(samples=3, sourceOnly=len(first['sourceOnly']), targetOnly=len(first['targetOnly']),
                          categories=first['categories'],
                          exactUniqueBoundaryMatches=sum(row['existedAtSourceD'] for row in first['sourceBoundaryEvidence']))))


if __name__ == '__main__':
    main()
