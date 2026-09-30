"""Compare bounded P0 diagnostics, never treat this subset as a complete savestate oracle."""
import argparse
import hashlib
import json
from pathlib import Path


def read_trace(path):
    lines = Path(path).read_bytes().splitlines(keepends=True)
    if len(lines) < 2 or not lines[-1].endswith(b"\n"):
        raise ValueError(f"{path}: incomplete trace")
    records = [json.loads(line) for line in lines]
    header, footer = records[0], records[-1]
    if header.get("kind") != "header" or header.get("schema") != "hktas-loading-probe-v1":
        raise ValueError(f"{path}: unknown header")
    if footer.get("kind") != "footer" or footer.get("success") is not True:
        raise ValueError(f"{path}: trace did not complete: {footer}")
    if hashlib.sha256(b"".join(lines[:-1])).hexdigest() != footer.get("sha256"):
        raise ValueError(f"{path}: digest mismatch")
    rows = records[1:-1]
    if len(rows) != footer.get("rows") or any(r.get("sequence") != i for i, r in enumerate(rows)):
        raise ValueError(f"{path}: row count or sequence mismatch")
    first, end = header["first"], header["end"]
    if first < 0 or end <= first or end - first > 10000:
        raise ValueError(f"{path}: invalid window")
    if footer.get("first") != first or footer.get("end") != end:
        raise ValueError(f"{path}: footer window mismatch")
    gameplay = [r for r in rows if r["phase"] in ("before", "after")]
    expected = [(frame, phase) for frame in range(first, end) for phase in ("before", "after")]
    if [(r["movieFrame"], r["phase"]) for r in gameplay] != expected:
        raise ValueError(f"{path}: missing, repeated, or reordered Movie sample")
    return header, rows, gameplay


def compare(paths, first=None):
    traces = [read_trace(path) for path in paths]
    header = traces[0][0]
    if any(t[0] != header for t in traces[1:]):
        raise ValueError("trace headers differ")
    lower = header["first"] if first is None else first
    if not header["first"] <= lower < header["end"]:
        raise ValueError("comparison start outside captured window")
    base = [r for r in traces[0][2] if r["movieFrame"] >= lower]
    differences = []
    comparisons = []
    for path, (_, rows, gameplay) in zip(paths[1:], traces[1:]):
        other = [r for r in gameplay if r["movieFrame"] >= lower]
        first_by_field = {}
        mismatched_samples = 0
        for left, right in zip(base, other):
            lstate = dict(left["state"], scene=left["scene"], boundary=left["boundary"])
            rstate = dict(right["state"], scene=right["scene"], boundary=right["boundary"])
            keys = sorted(k for k in lstate.keys() | rstate.keys() if lstate.get(k) != rstate.get(k))
            mismatched_samples += bool(keys)
            for key in keys:
                if key not in first_by_field:
                    first_by_field[key] = dict(movieFrame=left["movieFrame"], phase=left["phase"],
                                               field=key, expected=lstate.get(key), actual=rstate.get(key))
        lifecycle = lambda rs: [(r["movieFrame"], r["boundary"], r["scene"]) for r in rs
                                 if r["phase"] == "event" and r["movieFrame"] >= lower]
        events_match = lifecycle(traces[0][1]) == lifecycle(rows)
        comparisons.append(dict(path=str(path), comparedSamples=len(base),
                                mismatchedSamples=mismatched_samples, lifecycleMatch=events_match))
        differences.append(dict(path=str(path), firstDifferences=list(first_by_field.values())))
    return dict(schema="hktas-loading-probe-comparison-v1", first=lower, end=header["end"],
                comparisonPassed=all(c["mismatchedSamples"] == 0 and c["lifecycleMatch"] for c in comparisons),
                completeStateCoverage=header.get("completeStateCoverage") is True,
                phaseP0Passed=False, comparisons=comparisons, differences=differences)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("traces", nargs="+")
    parser.add_argument("--first-movie-frame", type=int)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if len(args.traces) < 2:
        parser.error("at least two independent traces are required")
    result = compare(args.traces, args.first_movie_frame)
    Path(args.output).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: result[key] for key in ("comparisonPassed", "completeStateCoverage", "phaseP0Passed")}))
    return 0 if result["comparisonPassed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
