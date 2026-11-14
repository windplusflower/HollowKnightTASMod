"""Inspect an unqualified HCCD carry graph without loading game code."""
import argparse
import collections
import hashlib
import json
import struct
from pathlib import Path


def inspect(path):
    raw = path.read_bytes()
    if not 0 < len(raw) <= 256 * 1024 * 1024:
        raise ValueError('Invalid carry payload size')
    data = memoryview(raw)
    cursor = 0

    def take(size):
        nonlocal cursor
        if size < 0 or cursor + size > len(data):
            raise ValueError('Truncated carry payload')
        result = data[cursor:cursor + size]
        cursor += size
        return result

    def integer():
        return struct.unpack('<i', take(4))[0]

    def count(maximum):
        value = integer()
        if not 0 <= value <= maximum:
            raise ValueError('Invalid count')
        return value

    if integer() != 0x44434348 or integer() != 1:
        raise ValueError('Unsupported carry wire format')
    strings = [bytes(take(count(256 * 1024 * 1024))).decode('utf-8') for _ in range(count(1000000))]
    if strings != sorted(set(strings)) or sum(len(s) for s in strings) > 256 * 1024 * 1024:
        raise ValueError('Noncanonical or unbounded string table')

    def text():
        value = integer()
        if not 0 <= value <= len(strings):
            raise ValueError('Invalid string identity')
        return None if value == 0 else strings[value - 1]

    schema = text()
    roots = [dict(binding=text(), node=integer()) for _ in range(count(100000))]
    node_count = count(100000)
    types = collections.Counter()
    node_types = [None] * node_count
    parents = {}
    values = 0

    def item(parent, field):
        nonlocal values
        values += 1
        if values > 1000000:
            raise ValueError('Value count exceeds its bound')
        kind = take(1)[0]
        text()
        text()
        reference = integer()
        if kind == 3 and reference not in parents:
            parents[reference] = parent, field

    for index in range(node_count):
        ordinal, kind_type = integer(), text()
        kind = take(1)[0]
        text()
        if ordinal != index + 1 or kind not in (0, 1, 2):
            raise ValueError('Invalid carry node')
        types[kind_type] += 1
        node_types[index] = kind_type
        fields = integer()
        if fields < -1 or fields > 1000000:
            raise ValueError('Invalid field count')
        for _ in range(max(0, fields)):
            field = text()
            item(ordinal, field)
        entries = integer()
        if entries < -1 or entries > 1000000:
            raise ValueError('Invalid item count')
        for offset in range(max(0, entries)):
            item(ordinal, f'[{offset}]')
    external = [dict(binding=text(), type=text()) for _ in range(count(100000))]
    issues = [text() for _ in range(count(1000000))]
    if cursor != len(raw):
        raise ValueError('Trailing carry payload bytes')
    root_names = {r['node']: r['binding'] for r in roots}

    def owner_path(node):
        parts = []
        visited = set()
        while node not in root_names:
            if node in visited or node not in parents or len(parts) > 128:
                return '<unbound>'
            visited.add(node)
            node, field = parents[node]
            parts.append(field)
        return root_names[node] + '/' + '/'.join(reversed(parts))

    grouped = collections.Counter()
    issue_examples = {}
    for issue in issues:
        node = None
        if issue.startswith('node['):
            end = issue.index(']')
            node = int(issue[5:end])
        category = issue.split(':', 1)[1] if ':' in issue else issue
        grouped[category] += 1
        if category not in issue_examples:
            issue_examples[category] = dict(issue=issue, ownerPath=owner_path(node) if node else None)
    return dict(version=1, diagnosticOnly=True, completeStateCoverage=False, restoreQualified=False,
                file=str(path), bytes=len(raw), sha256=hashlib.sha256(raw).hexdigest(), schema=schema,
                roots=roots, nodes=node_count, values=values, stringCount=len(strings),
                mostCommonTypes=types.most_common(20), externalCount=len(external), unsupportedCount=len(issues),
                issues=[dict(category=key, count=value, example=issue_examples[key]) for key, value in grouped.most_common()])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('payload', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args()
    result = inspect(args.payload)
    if args.output:
        if args.output.exists():
            raise ValueError('Refusing to overwrite evidence')
        args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: result[key] for key in ('bytes', 'nodes', 'values', 'externalCount', 'unsupportedCount', 'mostCommonTypes', 'issues')}, ensure_ascii=False))
