"""Compare bounded P0 diagnostics, never treat this subset as a complete savestate oracle."""
import argparse
import hashlib
import json
from pathlib import Path
from loading_trace_windows import expected_gameplay_samples, validate_sample_scope


def read_trace(path):
    lines = Path(path).read_bytes().splitlines(keepends=True)
    if len(lines) < 2 or not lines[-1].endswith(b"\n"):
        raise ValueError(f"{path}: incomplete trace")
    records = [json.loads(line) for line in lines]
    header, footer = records[0], records[-1]
    if header.get("kind") != "header" or header.get("schema") not in ("hktas-loading-probe-v1", "hktas-loading-oracle-v1"):
        raise ValueError(f"{path}: unknown header")
    if footer.get("kind") != "footer" or footer.get("success") is not True:
        raise ValueError(f"{path}: trace did not complete: {footer}")
    if hashlib.sha256(b"".join(lines[:-1])).hexdigest() != footer.get("sha256"):
        raise ValueError(f"{path}: digest mismatch")
    rows = records[1:-1]
    if len(rows) != footer.get("rows") or any(r.get("sequence") != i for i, r in enumerate(rows)):
        raise ValueError(f"{path}: row count or sequence mismatch")
    if any(r.get("kind") != "sample" or r.get("phase") not in ("before", "after", "loading", "event") for r in rows):
        raise ValueError(f"{path}: unknown sample phase")
    first, end = header["first"], header["end"]
    if first < 0 or end <= first or end - first > 10000:
        raise ValueError(f"{path}: invalid window")
    if footer.get("first") != first or footer.get("end") != end:
        raise ValueError(f"{path}: footer window mismatch")
    validate_sample_scope(header, rows)
    gameplay = [r for r in rows if r["phase"] in ("before", "after")]
    expected = expected_gameplay_samples(header)
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
            # Raw nativeFrame includes pre-input resource-wait loops and is a
            # process cursor. The oracle records controlledNativeFrame anchored
            # at StartInputAt; compare that state field without rebasing at K.
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
        left_events = [r for r in traces[0][1] if r["phase"] == "event" and r["movieFrame"] >= lower]
        right_events = [r for r in rows if r["phase"] == "event" and r["movieFrame"] >= lower]
        mismatched_event_samples = 0
        if events_match:
            for left, right in zip(left_events, right_events):
                lstate = left["state"]
                rstate = right["state"]
                keys = sorted(k for k in lstate.keys() | rstate.keys() if lstate.get(k) != rstate.get(k))
                mismatched_event_samples += bool(keys)
                for key in keys:
                    event_key = "event:" + key
                    if event_key not in first_by_field:
                        first_by_field[event_key] = dict(movieFrame=left["movieFrame"], phase="event",
                                                        boundary=left["boundary"], field=key,
                                                        expected=lstate.get(key), actual=rstate.get(key))
        left_loading = [r for r in traces[0][1] if r["phase"] == "loading" and r["movieFrame"] >= lower]
        right_loading = [r for r in rows if r["phase"] == "loading" and r["movieFrame"] >= lower]
        loading_order = lambda rs: [(r["movieFrame"], r["boundary"], r["scene"]) for r in rs]
        loading_match = loading_order(left_loading) == loading_order(right_loading)
        mismatched_loading_samples = 0
        if loading_match:
            for left, right in zip(left_loading, right_loading):
                lstate = left["state"]
                rstate = right["state"]
                keys = sorted(k for k in lstate.keys() | rstate.keys() if lstate.get(k) != rstate.get(k))
                mismatched_loading_samples += bool(keys)
                for key in keys:
                    loading_key = "loading:" + key
                    if loading_key not in first_by_field:
                        first_by_field[loading_key] = dict(movieFrame=left["movieFrame"], phase="loading",
                                                          boundary=left["boundary"], field=key,
                                                          expected=lstate.get(key), actual=rstate.get(key))
        comparisons.append(dict(path=str(path), comparedSamples=len(base),
                                mismatchedSamples=mismatched_samples, lifecycleMatch=events_match,
                                comparedEventSamples=len(left_events),
                                mismatchedEventSamples=mismatched_event_samples if events_match else None,
                                loadingOrderMatch=loading_match, comparedLoadingSamples=len(left_loading),
                                actualLoadingSamples=len(right_loading),
                                mismatchedLoadingSamples=mismatched_loading_samples if loading_match else None))
        differences.append(dict(path=str(path), firstDifferences=list(first_by_field.values())))
    return dict(schema="hktas-loading-probe-comparison-v1", first=lower, end=header["end"],
                comparisonPassed=all(c["mismatchedSamples"] == 0 and c["lifecycleMatch"]
                                     and c["mismatchedEventSamples"] == 0 and c["loadingOrderMatch"]
                                     and c["mismatchedLoadingSamples"] == 0 for c in comparisons),
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
