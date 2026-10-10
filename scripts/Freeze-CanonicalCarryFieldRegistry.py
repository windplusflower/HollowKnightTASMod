"""Freeze observed field layouts for the first, still-unqualified carry codec.

This does not classify native state or approve a snapshot. New runtime types
remain explicit unsupported records; changing this manifest is a spec change.
"""
import argparse
import hashlib
import json
from pathlib import Path


def freeze(source, output):
    document = json.loads(source.read_text(encoding='utf-8-sig'))
    if document.get('schema') != 1 or document.get('completeStateCoverage') is not False:
        raise ValueError('Expected a complete-schema diagnostic from the unqualified canonical observer')
    schema = document['fields']
    if schema['encoding'] != 'interned-field-schema-v2':
        raise ValueError('Unsupported field schema encoding')
    names = schema['names']
    layouts = {names[row['type']]: [dict(owner=names[f['owner']], name=f['field'], type=names[f['type']])
                for f in row['fields']] for row in schema['types']}
    selected = set()
    pending = [name for name, fields in layouts.items() if name.startswith('HutongGames.PlayMaker.')
               or any(f['owner'] == 'HutongGames.PlayMaker.FsmStateAction' for f in fields)]
    pending += ['PlayerData', 'SceneData']
    while pending:
        name = pending.pop()
        if name in selected:
            continue
        selected.add(name)
        if name.endswith('[]'):
            pending.append(name[:-2])
        # Native owners are external bindings, never field-graph allocations.
        if name.startswith('UnityEngine.') and name not in {
            'UnityEngine.Vector2', 'UnityEngine.Vector3', 'UnityEngine.Vector4', 'UnityEngine.Quaternion',
            'UnityEngine.Color', 'UnityEngine.Color32', 'UnityEngine.Rect', 'UnityEngine.Bounds',
            'UnityEngine.LayerMask', 'UnityEngine.Matrix4x4',
        }:
            continue
        for field in layouts.get(name, []):
            pending.append(field['type'])
    entries = [dict(name=name, fields=layouts.get(name)) for name in sorted(selected)]
    manifest = dict(version=1, diagnosticOnly=True, sourceSha256=hashlib.sha256(source.read_bytes()).hexdigest(),
                    sourceBoundary=document['ordinal'], types=entries)
    if output.exists():
        raise ValueError('Refusing to replace a frozen field registry')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(manifest, ensure_ascii=False, separators=(',', ':')) + '\n', encoding='utf-8')
    print(json.dumps(dict(types=len(entries), fields=sum(len(row['fields'] or []) for row in entries),
                          bytes=output.stat().st_size, output=str(output))))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--field-schema', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    freeze(args.field_schema, args.output)
