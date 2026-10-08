"""Locate exact font glyph, UV and usage differences in P0 candidate diagnostics."""
import argparse
import collections
import json
from pathlib import Path


def compare(left, right):
    pair = [json.loads(path.read_text(encoding="utf-8-sig")) for path in (left, right)]
    report = {"left": str(left), "right": str(right), "fonts": [], "phaseP0Passed": False}
    fonts = []
    for candidate in pair:
        values = candidate.get("fontCacheDetails", [])
        mapped = {item["font"]: item for item in values}
        if len(mapped) != len(values) or not mapped:
            raise ValueError("font diagnostics absent or names ambiguous")
        fonts.append(mapped)
    for name in sorted(fonts[0].keys() | fonts[1].keys()):
        a, b = (values.get(name) for values in fonts)
        item = {"font": name, "presenceEqual": a is not None and b is not None, "tables": {}}
        if a is not None and b is not None:
            item["atlasEqual"] = a.get("atlas") == b.get("atlas")
            item["atlasAvailable"] = a.get("atlas") is not None and b.get("atlas") is not None
            for table in ("ascii", "unicode"):
                aa, bb = a[table], b[table]
                changes = []
                for index in range(max(len(aa), len(bb))):
                    x = aa[index] if index < len(aa) else None
                    y = bb[index] if index < len(bb) else None
                    if x is None or y is None:
                        changes.append({"slot": index, "field": "presence", "left": x, "right": y})
                        continue
                    for field in sorted(x.keys() | y.keys()):
                        if x.get(field) != y.get(field):
                            changes.append({"slot": index, "index": x.get("index"), "field": field,
                                            "left": x.get(field), "right": y.get(field)})
                item["tables"][table] = {"leftCount": len(aa), "rightCount": len(bb),
                                         "differenceCounts": dict(collections.Counter(x["field"] for x in changes)),
                                         "differences": changes}
                # Retain all ordered differences above. A separate keyed view
                # distinguishes insertion order from a change to a glyph itself.
                maps = [{(x["index"], x["size"], x["style"]): x for x in values} for values in (aa, bb)]
                if all(len(mapped) == len(values) for mapped, values in zip(maps, (aa, bb))):
                    keyed = []
                    for key in sorted(maps[0].keys() | maps[1].keys()):
                        x, y = maps[0].get(key), maps[1].get(key)
                        if x is None or y is None:
                            keyed.append({"glyph": key, "field": "presence", "left": x, "right": y})
                        else:
                            keyed.extend({"glyph": key, "field": field, "left": x.get(field), "right": y.get(field)}
                                         for field in sorted(x.keys() | y.keys()) if x.get(field) != y.get(field))
                    item["tables"][table]["keyedDifferenceCounts"] = dict(collections.Counter(x["field"] for x in keyed))
                    item["tables"][table]["keyedDifferences"] = keyed
        report["fonts"].append(item)
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("left", type=Path)
    parser.add_argument("right", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = compare(args.left, args.right)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    for font in report["fonts"]:
        print(json.dumps({"font": font["font"], "atlasAvailable": font.get("atlasAvailable"),
                          "tables": {name: {key: value for key, value in table.items() if key not in ("differences", "keyedDifferences")}
                                     for name, table in font["tables"].items()}}))
