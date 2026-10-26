"""Read-only, bounded serialized-origin proof for the admitted Marmu challenge.

No live Unity process, Resources search, generated D, or saved game is read.
"""
import argparse
import hashlib
import json
from pathlib import Path

import UnityPy
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator
from UnityPy.helpers import TypeTreeHelper as H
from UnityPy.streams import EndianBinaryReader


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--data', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    data = Path(args.data)
    files = {name: UnityPy.load(str(data / name)) for name in
             ('globalgamemanagers', 'globalgamemanagers.assets', 'resources.assets', 'level470')}
    objects = {name: {o.path_id: o for o in env.objects} for name, env in files.items()}
    resource_manager = objects['globalgamemanagers'][13]
    container = resource_manager.read_typetree()['m_Container']
    resources_external = [i + 1 for i, x in enumerate(resource_manager.assets_file.externals)
                          if x.path == 'resources.assets']
    assert resources_external == [5]
    rows = [(key, ptr) for key, ptr in container if key == 'gg/boss sequence tier 2']
    assert rows == [('gg/boss sequence tier 2', {'m_FileID': 5, 'm_PathID': 19562})]
    direct = [(key, ptr) for key, ptr in container
              if ptr == {'m_FileID': 5, 'm_PathID': 19472}]
    assert direct == []
    generator = TypeTreeGenerator(objects['resources.assets'][19562].assets_file.unity_version)
    for name in ('mscorlib.dll', 'netstandard.dll', 'System.dll', 'System.Core.dll',
                 'UnityEngine.dll', 'PlayMaker.dll', 'Assembly-CSharp.dll'):
        generator.load_dll((data / 'Managed' / name).read_bytes())
    for module in sorted((data / 'Managed').glob('UnityEngine.*Module.dll')):
        generator.load_dll(module.read_bytes())
    H.read_typetree_boost = None

    def normalize(node):
        if node.m_Children and node.m_Children[0].m_Type == 'Array' and node.m_Type in H.FUNCTION_READ_MAP:
            if node.m_Type != 'string' or node.m_Children[0].m_Children[1].m_Type == 'string':
                node.m_Type = 'vector'
        for child in node.m_Children:
            normalize(child)

    scripts = objects['globalgamemanagers.assets']

    def decode(file, path, expected):
        reader = objects[file][path]
        raw = reader.get_raw_data()
        # MonoBehaviour header in this pinned Unity ABI.
        import struct
        script_file, script_path = struct.unpack_from('<iq', raw, 16)
        assert reader.assets_file.externals[script_file - 1].path == 'globalgamemanagers.assets'
        script_reader = scripts[script_path]
        script = script_reader.read()
        assert script.m_ClassName == expected and script.m_Namespace == ''
        assert script.m_AssemblyName == 'Assembly-CSharp.dll'
        nodes = generator.get_nodes_up('Assembly-CSharp', expected)
        normalize(nodes)
        # The generator omits this native MonoBehaviour header alignment in
        # this Unity build. Validate the complete raw header independently and
        # supply the actual three-byte alignment, rather than emitting a bogus
        # script pointer and treating it as an admissible serialized value.
        enabled = next(x for x in nodes.m_Children if x.m_Name == 'm_Enabled')
        assert raw[13:16] == b'\0\0\0' and raw[12] == 1
        enabled.m_MetaFlag |= 0x4000
        value = H.read_typetree(nodes, EndianBinaryReader(raw, '<'), byte_size=len(raw), assetsfile=reader.assets_file)
        assert value['m_Script'] == {'m_FileID': script_file, 'm_PathID': script_path}
        return dict(file=file, pathId=path, rawBytes=len(raw), rawSha256=sha(raw),
                    scriptPathId=script_path, scriptRawSha256=sha(script_reader.get_raw_data()), serialized=value)

    sequence = decode('resources.assets', 19562, 'BossSequence')
    scene = decode('resources.assets', 19472, 'BossScene')
    statue = decode('level470', 13642, 'BossStatue')
    scenes = sequence['serialized']['bossScenes']
    indexes = [i for i, ptr in enumerate(scenes) if ptr == {'m_FileID': 0, 'm_PathID': 19472}]
    assert len(scenes) == 12 and len(indexes) == 1
    assert statue['serialized']['bossScene'] == {'m_FileID': 4, 'm_PathID': 19472}
    assert objects['level470'][13642].assets_file.externals[3].path == 'resources.assets'
    assert statue['serialized']['statueStatePD'] == 'statueStateMarmu'
    assert scene['serialized']['m_Name'] == 'Marmu Boss Scene'
    report = dict(version=1, resourcePath=rows[0][0], directResourcePaths=direct,
                  resourceManagerRawSha256=sha(resource_manager.get_raw_data()),
                  resourceManagerContainerCount=len(container), bossScenesIndex=indexes[0],
                  sequence=sequence, scene=scene, statue=statue,
                  files={name: sha((data / name).read_bytes()) for name in files},
                  assemblySha256=sha((data / 'Managed' / 'Assembly-CSharp.dll').read_bytes()),
                  liveIdentityOrLifecycleNotValidated=True)
    Path(args.output).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(dict(resourcePath=report['resourcePath'], bossScenesIndex=indexes[0],
                          bossScenesLength=len(scenes), sceneSha256=scene['rawSha256'], passed=True)))


if __name__ == '__main__':
    main()
