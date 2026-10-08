"""Compare P0 state diagnostics exactly; this never grants full-state qualification."""
import argparse
import collections
import hashlib
import json
from pathlib import Path


def read_trace(path):
    lines = path.read_bytes().splitlines(keepends=True)
    if len(lines) < 2 or not all(line.endswith(b"\n") for line in lines):
        raise ValueError(f"{path}: incomplete trace")
    records = [json.loads(line) for line in lines]
    header, footer = records[0], records[-1]
    if header.get("schema") != "hktas-loading-oracle-v1" or header.get("kind") != "header":
        raise ValueError(f"{path}: unknown header")
    rows = records[1:-1]
    if footer.get("kind") != "footer" or footer.get("success") is not True:
        raise ValueError(f"{path}: unsuccessful footer: {footer}")
    if hashlib.sha256(b"".join(lines[:-1])).hexdigest() != footer.get("sha256"):
        raise ValueError(f"{path}: footer digest mismatch")
    if footer.get("rows") != len(rows) or any(row.get("sequence") != i for i, row in enumerate(rows)):
        raise ValueError(f"{path}: missing, duplicate, or reordered sample")
    if (footer.get("first"), footer.get("end")) != (header["first"], header["end"]):
        raise ValueError(f"{path}: footer window differs")
    if not 0 <= header["first"] < header["end"] <= header["first"] + 10000:
        raise ValueError(f"{path}: invalid bounded window")
    if any(row.get("kind") != "sample" or row.get("phase") not in
           ("before", "after", "loading", "event", "candidate") for row in rows):
        raise ValueError(f"{path}: unknown sample")
    if header.get("samplingScope") == "serial-candidate-boundaries-only-diagnostic":
        if not rows or any(row["phase"] != "candidate" for row in rows):
            raise ValueError(f"{path}: invalid candidate-only scope")
    else:
        gameplay = [(row["movieFrame"], row["phase"]) for row in rows if row["phase"] in ("before", "after")]
        expected = [(frame, phase) for frame in range(header["first"], header["end"])
                    for phase in ("before", "after")]
        if gameplay != expected:
            raise ValueError(f"{path}: incomplete gameplay window")
    return header, rows


def sample_key(row):
    return tuple(row[name] for name in ("movieFrame", "phase", "boundary", "scene"))


def compare(paths):
    traces = [read_trace(path) for path in paths]
    header, base = traces[0]
    comparisons = []
    for path, (other_header, other) in zip(paths[1:], traces[1:]):
        header_differences = sorted(key for key in header.keys() | other_header.keys()
                                    if header.get(key) != other_header.get(key))
        order_equal = [sample_key(row) for row in base] == [sample_key(row) for row in other]
        first_by_field = {}
        mismatch_count = 0
        if not header_differences and order_equal:
            for left, right in zip(base, other):
                changed = sorted(key for key in left["state"].keys() | right["state"].keys()
                                 if left["state"].get(key) != right["state"].get(key))
                mismatch_count += bool(changed)
                for key in changed:
                    first_by_field.setdefault(key, {
                        "movieFrame": left["movieFrame"], "phase": left["phase"],
                        "boundary": left["boundary"], "sequence": left["sequence"],
                        "expected": left["state"].get(key), "actual": right["state"].get(key)})
        comparisons.append({"path": str(path), "headerDifferences": header_differences,
                            "sampleOrderEqual": order_equal, "comparedSamples": len(base) if order_equal else 0,
                            "mismatchedSamples": mismatch_count if order_equal else None,
                            "firstDifferences": first_by_field})
    return {"schema": "hktas-p0-state-comparison-v1", "first": header["first"], "end": header["end"],
            "coverage": header["coverage"], "samplingScope": header["samplingScope"],
            "phases": dict(collections.Counter(row["phase"] for row in base)),
            "comparisonPassed": all(not item["headerDifferences"] and item["sampleOrderEqual"]
                                    and item["mismatchedSamples"] == 0 for item in comparisons),
            "completeStateCoverage": False, "phaseP0Passed": False, "comparisons": comparisons}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("traces", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if len(args.traces) < 2:
        parser.error("at least two independently collected traces are required")
    report = compare(args.traces)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"comparisonPassed": report["comparisonPassed"],
                      "comparedSamples": [item["comparedSamples"] for item in report["comparisons"]],
                      "firstDifferenceFields": [list(item["firstDifferences"]) for item in report["comparisons"]]}))
