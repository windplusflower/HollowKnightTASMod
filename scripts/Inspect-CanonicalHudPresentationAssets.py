"""Bounded original level1 audit for eight HUD text leaves and six visual FSMs."""
import argparse
import hashlib
import json
import struct
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers import TypeTreeHelper as H
from UnityPy.streams import EndianBinaryReader

TEXTS = ('Border/Pane Name', 'Border/Pane Name L', 'Border/Pane Name R',
         'Charms/Confirm Action/Text', 'Charms/Details/Cost/Text Cost',
         'Charms/Equipped Charms/Text Equipped', 'Charms/Text Desc', 'Charms/Text Name')
FSMS = ('Charms/Cursor/TL/Sprite', 'Charms/Cursor/TR/Sprite',
        'Charms/Cursor/BR/Sprite', 'Charms/Cursor/BL/Sprite',
        'Charms/Confirm Action', 'Charms/Overcharm/Overcharm Set')
PREFIX = '_GameCameras/HudCamera/Inventory/'
CHILDREN = ('Charms/Confirm Action/ActionButtonIcon',
            'Charms/Confirm Action/ActionButtonIcon/Label',
            'Charms/Overcharm/Overcharm Set/break_glow', 'Charms/Overcharm/Overcharm Set/Wave')


def digest(value):
    return hashlib.sha256(value).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    data = Path(args.data)
    env = UnityPy.load(str(data / 'level1'))
    objects = {reader.path_id: reader for reader in env.objects}
    script_objects = {reader.path_id: reader for reader in UnityPy.load(str(data / 'globalgamemanagers.assets')).objects}
    generator = TypeTreeGenerator(next(iter(objects.values())).assets_file.unity_version)
    modules = ['mscorlib.dll', 'netstandard.dll', 'System.dll', 'System.Core.dll',
               'UnityEngine.dll', 'UnityEngine.UI.dll', 'PlayMaker.dll', 'Assembly-CSharp.dll']
    modules += [path.name for path in sorted((data / 'Managed').glob('UnityEngine.*Module.dll'))]
    for module in modules:
        generator.load_dll((data / 'Managed' / module).read_bytes())
    H.read_typetree_boost = None

    def normalize(node):
        if node.m_Children and node.m_Children[0].m_Type == 'Array' and node.m_Type in H.FUNCTION_READ_MAP:
            if node.m_Type != 'string' or node.m_Children[0].m_Children[1].m_Type == 'string':
                node.m_Type = 'vector'
        for child in node.m_Children:
            normalize(child)

    def ancestry(go):
        result = []
        while True:
            result.append(dict(name=go.m_Name, pathId=go.object_reader.path_id, activeSelf=bool(go.m_IsActive)))
            transform = go.m_Component[0].component.deref().read()
            if not transform.m_Father.path_id:
                return result
            go = transform.m_Father.deref().read().m_GameObject.deref().read()

    def component(reader):
        raw = reader.get_raw_data()
        result = dict(pathId=reader.path_id, type=reader.type.name, bytes=len(raw), rawSha256=digest(raw))
        if reader.type.name != 'MonoBehaviour':
            result['serialized'] = reader.read_typetree()
            return result
        script_file, script_path = struct.unpack_from('<iq', raw, 16)
        assert reader.assets_file.externals[script_file - 1].path == 'globalgamemanagers.assets'
        script_reader = script_objects[script_path]
        script = script_reader.read()
        fullname = (script.m_Namespace + '.' if script.m_Namespace else '') + script.m_ClassName
        nodes = generator.get_nodes_up(script.m_AssemblyName.removesuffix('.dll'), fullname)
        normalize(nodes)
        assert raw[12] in (0, 1) and raw[13:16] == b'\0\0\0'
        next(node for node in nodes.m_Children if node.m_Name == 'm_Enabled').m_MetaFlag |= 0x4000
        value = H.read_typetree(nodes, EndianBinaryReader(raw, '<'), byte_size=len(raw), assetsfile=reader.assets_file)
        assert value['m_Script'] == {'m_FileID': script_file, 'm_PathID': script_path}
        result.update(type=fullname, scriptPathId=script_path, scriptRawSha256=digest(script_reader.get_raw_data()), serialized=value)
        return result

    rows = []
    for reader in objects.values():
        if reader.type.name != 'GameObject':
            continue
        go = reader.read()
        chain = ancestry(go)
        path = '/'.join(entry['name'] for entry in reversed(chain))
        if not path.startswith(PREFIX) or path[len(PREFIX):] not in TEXTS + FSMS + CHILDREN:
            continue
        transform = go.m_Component[0].component.deref().read()
        rows.append(dict(path=path[len(PREFIX):], pathId=reader.path_id, chain=chain,
                         children=[dict(transformPathId=child.path_id,
                                        gameObjectPathId=child.deref().read().m_GameObject.path_id,
                                        name=child.deref().read().m_GameObject.deref().read().m_Name)
                                   for child in transform.m_Children],
                         components=[component(pair.component.deref()) for pair in go.m_Component]))
    assert len(rows) == len(TEXTS) + len(FSMS) + len(CHILDREN) and {row['path'] for row in rows} == set(TEXTS + FSMS + CHILDREN)
    owned = {}
    for row in rows:
        owned[row['pathId']] = row['path']
        for part in row['components']:
            owned[part['pathId']] = row['path'] + ':' + part['type']
    roots = [path for path in TEXTS + FSMS if not any(path.startswith(parent + '/') for parent in TEXTS + FSMS)]
    def root(path):
        matches = [value for value in roots if path == value or path.startswith(value + '/') or path.startswith(value + ':')]
        assert len(matches) == 1
        return matches[0]
    edges = []
    def pointers(value, path):
        if isinstance(value, dict):
            if set(value) == {'m_FileID', 'm_PathID'}:
                yield path, value
            else:
                for key, child in value.items():
                    yield from pointers(child, path + '/' + key)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                yield from pointers(child, path + '[' + str(index) + ']')
    for row in rows:
        for part in row['components']:
            for field, pointer in pointers(part['serialized'], part['type']):
                if pointer['m_FileID'] == 0 and pointer['m_PathID'] in owned:
                    target = owned[pointer['m_PathID']]
                    edges.append(dict(owner=row['path'], field=field, target=target,
                                      crossRoot=root(row['path']) != root(target)))
    report = dict(version=1, source='level1', sourceSha256=digest((data / 'level1').read_bytes()),
                  assemblySha256=digest((data / 'Managed/Assembly-CSharp.dll').read_bytes()),
                  sourceNativeLifecycleNotObserved=True, roots=sorted(roots), internalSerializedEdges=edges,
                  crossRootSerializedEdges=[edge for edge in edges if edge['crossRoot']],
                  rows=sorted(rows, key=lambda row: row['path']))
    Path(args.output).write_text(json.dumps(report, indent=2), encoding='utf-8')
    for row in report['rows']:
        print(json.dumps(dict(path=row['path'], pathId=row['pathId'], children=row['children'],
                              types=[part['type'] for part in row['components']]), ensure_ascii=False))
    print(json.dumps(dict(roots=len(roots), gameObjects=len(rows), components=sum(len(row['components']) for row in rows),
                          internalSerializedEdges=len(edges), crossRootSerializedEdges=report['crossRootSerializedEdges'])))


if __name__ == '__main__':
    main()
