"""Decode lossless P0 diagnostic tables without merging duplicate root records."""
import json
from pathlib import Path


def _index(value, table, label):
    if type(value) is not int or not 0 <= value < len(table):
        raise ValueError(f"invalid {label} index")
    return table[value]


def decode_roots(document):
    roots = document["roots"]
    if not isinstance(roots, list) or len(roots) > 100000:
        raise ValueError("invalid diagnostic root count")
    encoding = document.get("rootIdentityEncoding")
    if encoding is None:
        if any(not isinstance(root, dict) or not isinstance(root.get("id"), str) for root in roots):
            raise ValueError("invalid legacy diagnostic root")
        return [dict(root) for root in roots]
    if encoding != "sorted-utf16-prefix-root-names-v1":
        raise ValueError("unknown root identity encoding")
    names, previous = [], ""
    for item in document["rootIdentities"]:
        prefix, suffix = item["prefix"], item["suffix"]
        raw = previous.encode("utf-16-le")
        if type(prefix) is not int or not 0 <= prefix <= len(raw) // 2 or not isinstance(suffix, str):
            raise ValueError("invalid root prefix entry")
        previous = raw[:prefix * 2].decode("utf-16-le") + suffix
        names.append(previous)
    types = document["rootTypes"]
    if not isinstance(types, list) or any(not isinstance(value, str) for value in types):
        raise ValueError("invalid root type table")
    result = []
    for entry in roots:
        root = dict(entry)
        root["id"] = _index(root["id"], names, "root identity")
        if root.get("type") is not None:
            root["type"] = _index(root["type"], types, "root type")
        result.append(root)
    return result


def decode_field_schema(schema):
    if isinstance(schema, list):
        return schema
    if schema.get("encoding") != "interned-field-schema-v2":
        raise ValueError("unknown field schema encoding")
    names = schema["names"]
    if not isinstance(names, list) or any(not isinstance(value, str) for value in names):
        raise ValueError("invalid field schema names")
    return [{"type": _index(record["type"], names, "schema type"),
             "fields": [{"owner": _index(field["owner"], names, "field owner"),
                         "field": field["field"],
                         "type": _index(field["type"], names, "field type")}
                        for field in record["fields"]]}
            for record in schema["types"]]


def read_inventory(path):
    document = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    document["roots"] = decode_roots(document)
    if "fieldSchema" in document:
        document["fieldSchema"] = decode_field_schema(document["fieldSchema"])
    return document
