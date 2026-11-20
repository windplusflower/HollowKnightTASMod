#!/usr/bin/env python3
"""Read-only Unity asset audit for the vanilla Hollow Knight Hero SetZ component."""

from __future__ import annotations

import argparse
import importlib.metadata
import json
import struct
import sys
from pathlib import Path

import UnityPy


def read_i32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<i", data, offset)[0]


def read_i64(data: bytes, offset: int) -> int:
    return struct.unpack_from("<q", data, offset)[0]


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def read_f32(data: bytes, offset: int) -> float:
    return struct.unpack_from("<f", data, offset)[0]


def to_f32(value: float) -> float:
    return struct.unpack("<f", struct.pack("<f", value))[0]


def canonical_hex(value: float) -> str:
    return f"{struct.unpack('<I', struct.pack('<f', value))[0]:08x}"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--resources", required=True)
    parser.add_argument("--global-managers-assets", required=True)
    args = parser.parse_args()

    resources_path = Path(args.resources).resolve()
    global_managers_path = Path(args.global_managers_assets).resolve()
    resources = UnityPy.load(str(resources_path))
    root_reader = next(
        item for item in resources.objects if item.path_id == 3895
    )
    root = root_reader.read()
    components = [pair.component for pair in root.m_Component]
    component_rows = [
        {
            "pathId": int(pointer.path_id),
            "type": pointer.deref().type.name,
        }
        for pointer in components
    ]

    set_z_reader = next(
        item for item in resources.objects if item.path_id == 22445
    )
    raw = bytes(set_z_reader.get_raw_data())
    script_file_id = read_i32(raw, 16)
    script_path_id = read_i64(raw, 20)
    resources_file = next(iter(resources.files.values()))
    script_external_path = resources_file.externals[script_file_id - 1].path

    global_managers = UnityPy.load(str(global_managers_path))
    script_reader = next(
        item
        for item in global_managers.objects
        if item.path_id == script_path_id
    )
    script = script_reader.read()

    z_value = read_f32(raw, 32)
    delay_value = read_f32(raw, 44)
    maximum = to_f32(to_f32(z_value) + to_f32(0.0009999))
    checks = {
        "rootNameKnight": root.m_Name == "Knight",
        "rootPathId": root_reader.path_id == 3895,
        "componentCount": len(components) == 28,
        "setZComponentPresent": any(
            row["pathId"] == 22445 and row["type"] == "MonoBehaviour"
            for row in component_rows
        ),
        "rawLength": len(raw) == 48,
        "gameObjectPointer": read_i32(raw, 0) == 0
        and read_i64(raw, 4) == 3895,
        "enabled": raw[12] == 1,
        "scriptPointer": script_file_id == 1 and script_path_id == 2138,
        "scriptExternal": script_external_path == "globalgamemanagers.assets",
        "scriptIdentity": script_reader.type.name == "MonoScript"
        and script.m_Name == "SetZ"
        and script.m_ClassName == "SetZ"
        and script.m_AssemblyName == "Assembly-CSharp.dll",
        "emptyMonoBehaviourName": read_i32(raw, 28) == 0,
        "zBits": read_u32(raw, 32) == 0x3B83126F,
        "dontRandomizeFalse": raw[36] == 0,
        "randomizeFromStartingValueFalse": raw[40] == 0,
        "delayBits": read_u32(raw, 44) == 0x3F000000,
        "maximumBits": canonical_hex(maximum) == "3ba3d634",
    }
    artifact = {
        "schemaVersion": 1,
        "verdict": "PASS" if all(checks.values()) else "FAIL",
        "unityPyVersion": importlib.metadata.version("UnityPy"),
        "resourcesPath": str(resources_path),
        "globalManagersAssetsPath": str(global_managers_path),
        "hero": {
            "name": root.m_Name,
            "pathId": int(root_reader.path_id),
            "componentCount": len(components),
            "components": component_rows,
        },
        "setZ": {
            "componentPathId": int(set_z_reader.path_id),
            "componentType": set_z_reader.type.name,
            "rawLength": len(raw),
            "rawHex": raw.hex(),
            "gameObjectFileId": read_i32(raw, 0),
            "gameObjectPathId": read_i64(raw, 4),
            "enabled": bool(raw[12]),
            "scriptFileId": script_file_id,
            "scriptPathId": script_path_id,
            "scriptExternalPath": script_external_path,
            "scriptName": script.m_Name,
            "scriptClassName": script.m_ClassName,
            "scriptAssemblyName": script.m_AssemblyName,
            "z": z_value,
            "zCanonicalHex": f"{read_u32(raw, 32):08x}",
            "dontRandomize": bool(raw[36]),
            "randomizeFromStartingValue": bool(raw[40]),
            "delayBeforeRandomizing": delay_value,
            "delayCanonicalHex": f"{read_u32(raw, 44):08x}",
            "randomRangeMaximum": maximum,
            "randomRangeMaximumCanonicalHex": canonical_hex(maximum),
        },
        "checks": checks,
    }
    print(json.dumps(artifact, ensure_ascii=False, indent=2))
    return 0 if artifact["verdict"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
