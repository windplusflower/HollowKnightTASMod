"""Compare declared short windows after real checkpoint application; never qualify full state."""
import argparse
import hashlib
import json
from pathlib import Path


def read_trace(path):
    lines = path.read_bytes().splitlines(keepends=True)
    if len(lines) < 2 or not lines[-1].endswith(b"\n"):
        raise ValueError(f"{path}: incomplete evidence file")
    records = [json.loads(line) for line in lines]
    header, footer = records[0], records[-1]
    if header.get("kind") != "header" or header.get("schema") != "hktas-loading-probe-v1":
        raise ValueError(f"{path}: unknown probe header")
    if footer.get("kind") != "footer" or hashlib.sha256(b"".join(lines[:-1])).hexdigest() != footer.get("sha256"):
        raise ValueError(f"{path}: evidence digest differs")
    rows = records[1:-1]
    if len(rows) != footer.get("rows") or any(row.get("sequence") != i for i, row in enumerate(rows)):
        raise ValueError(f"{path}: sample sequence differs")
    first, end = header["first"], header["end"]
    if type(first) is not int or type(end) is not int or not 0 <= first < end <= first + 10000:
        raise ValueError(f"{path}: invalid scope")
    windows = header.get("windows", [{"first": first, "end": end}])
    previous = first
    for window in windows:
        start, stop = window["first"], window["end"]
        if type(start) is not int or type(stop) is not int or not previous <= start < stop <= end:
            raise ValueError(f"{path}: invalid declared window")
        previous = stop
    if not windows or windows[0]["first"] != first or previous != end:
        raise ValueError(f"{path}: window endpoints differ")
    if footer.get("first") != first or footer.get("end") != end:
        raise ValueError(f"{path}: footer scope differs")
    continuous = header.get("continuousRandomCalls", False)
    call_first, call_end = header.get("randomFirst"), header.get("randomEnd")
    if type(continuous) is not bool or continuous and (type(call_first) is not int or type(call_end) is not int
            or not first <= call_first < call_end <= end or call_end - call_first > 256):
        raise ValueError(f"{path}: invalid continuous random-call scope")
    def in_scope(row):
        frame = row.get("movieFrame")
        if type(frame) is not int:
            return False
        if continuous and row.get("kind") in ("rng-call", "timer-call"):
            return call_first <= frame < call_end
        return any(w["first"] <= frame < w["end"] for w in windows)
    if any(row.get("kind") not in ("sample", "rng-call", "timer-call")
           or row.get("kind") == "sample" and row.get("phase") not in ("before", "after", "event", "loading")
           or not in_scope(row) for row in rows):
        raise ValueError(f"{path}: sample outside the declared scope")
    return header, footer, rows, windows


def compare(run):
    def document(name):
        return json.loads((run / (name + ".json")).read_text(encoding="utf-8-sig"))
    capture, at_k = document("capture-header"), document("restored-k")
    k = int(capture["resumeMovieFrame"])
    if at_k["checkpointId"] != capture["checkpointId"] or at_k["status"] != "PausedAtK" or at_k["stateApplied"] != "true" \
            or int(at_k["movieFrame"]) != k or at_k["nativeFrame"] != capture["resumeNativeFrame"] \
            or at_k["firstConsumedMovieFrame"] != "-1" or at_k["consumedMovieFrames"] != "0":
        raise ValueError("evidence does not show an applied checkpoint parked at K with zero input")
    paths = []
    for name in ("recovered-shadow-01", "recovered-shadow-02"):
        candidates = list((run / name).rglob("loading-boundary-probe.jsonl"))
        if len(candidates) != 1:
            raise ValueError(f"{name}: absent or ambiguous probe evidence")
        paths.append(candidates[0])
    source, replacement = [read_trace(path) for path in paths]
    if source[0] != replacement[0]:
        raise ValueError("source and replacement probe headers differ")
    expected = [(frame, phase) for w in source[3] for frame in range(max(k, w["first"]), w["end"])
                for phase in ("before", "after")]
    if not expected:
        raise ValueError("no declared state samples follow the restored checkpoint")
    rows = [[r for r in trace[2] if r["kind"] == "sample" and r["phase"] in ("before", "after") and r["movieFrame"] >= k]
            for trace in (source, replacement)]
    keys = [[(r["movieFrame"], r["phase"]) for r in trace] for trace in rows]
    if len(set(keys[0])) != len(keys[0]) or len(set(keys[1])) != len(keys[1]):
        raise ValueError("duplicate Movie samples")
    source_rows = dict(zip(keys[0], rows[0]))
    differences = []
    for key, right in zip(keys[1], rows[1]):
        if key not in source_rows:
            raise ValueError("replacement sample has no source counterpart")
        left = source_rows[key]
        ls = dict(left["state"], scene=left["scene"], boundary=left["boundary"], nativeFrame=left["nativeFrame"])
        rs = dict(right["state"], scene=right["scene"], boundary=right["boundary"], nativeFrame=right["nativeFrame"])
        for field in sorted(ls.keys() | rs.keys()):
            if ls.get(field) != rs.get(field):
                differences.append(dict(movieFrame=key[0], phase=key[1], field=field, expected=ls.get(field), actual=rs.get(field)))
    complete = keys[0] == expected and keys[1] == expected and source[1].get("success") is True and replacement[1].get("success") is True
    k_window_complete = all((frame, phase) in keys[0] and (frame, phase) in keys[1]
                            for frame in range(k, k + 3) for phase in ("before", "after"))
    suffix_passed = False
    if all((run / (name + ".json")).exists() for name in ("source-full-runtime", "restored-full-suffix", "restored-full-runtime")):
        source_end, suffix, target_end = [document(name) for name in ("source-full-runtime", "restored-full-suffix", "restored-full-runtime")]
        t = int(source_end["movieFrame"])
        suffix_passed = (suffix["checkpointId"] == capture["checkpointId"] and suffix["stateApplied"] == "true"
                         and int(suffix["movieFrame"]) == t and int(suffix["firstConsumedMovieFrame"]) == k
                         and int(suffix["consumedMovieFrames"]) == t - k and t > k
                         and all(s["faultCode"] == "0" and s["mismatchCount"] == "0" and s["error"] == "" for s in (source_end, target_end))
                         and all(source_end[field] == target_end[field] for field in ("movieFrame", "nativeFrame", "mode", "sceneName"))
                         and complete and k_window_complete and not differences)
    return dict(schema="hktas-checkpoint-short-window-comparison-v1", checkpointId=capture["checkpointId"], resumeMovieFrame=k,
                comparisonPassed=complete and not differences, completeStateCoverage=False, restoreQualified=False,
                fullSuffixPassed=suffix_passed, kWindowComplete=k_window_complete,
                expectedSamples=len(expected), sourceSamples=len(rows[0]), replacementSamples=len(rows[1]), completeSamples=complete,
                sourceFooter=source[1], replacementFooter=replacement[1], differenceCount=len(differences),
                firstDifference=differences[0] if differences else None, differences=differences)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--require-full-suffix", action="store_true",
                        help="Require the exact original suffix and the K-starting three-frame window.")
    args = parser.parse_args()
    result = compare(args.run)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in result.items() if k != "differences"}, ensure_ascii=False))
    return 0 if result["comparisonPassed"] and (not args.require_full_suffix or result["fullSuffixPassed"]) else 1


if __name__ == "__main__":
    raise SystemExit(main())
