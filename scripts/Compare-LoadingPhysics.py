"""Distinguish bound native-physics values from native traversal order, without dropping either."""
import argparse
import collections
import json
from pathlib import Path

from importlib.util import module_from_spec, spec_from_file_location

_spec = spec_from_file_location("loading_state", Path(__file__).with_name("Compare-LoadingState.py"))
_state = module_from_spec(_spec)
_spec.loader.exec_module(_state)

ORDER_KINDS = {"body-fixture-order", "body-contact-order", "world-body-order", "world-contact-order"}


def entries(row):
    result = collections.defaultdict(lambda: collections.defaultdict(list))
    traversal = collections.defaultdict(list)
    if not isinstance(row.get("nativePhysicsEntries"), list):
        raise ValueError("missing native physics entry diagnostics")
    for item in row["nativePhysicsEntries"]:
        kind, binding, fields = item["kind"], item["binding"], item["fields"]
        if not isinstance(kind, str) or not isinstance(binding, str) or not isinstance(fields, list):
            raise ValueError("invalid native physics entry")
        if any(not isinstance(value, str) or len(value) != 64 or
               any(char not in "0123456789abcdef" for char in value) for value in fields):
            raise ValueError("invalid native physics field digest")
        result[kind][binding].append(fields)
        traversal[kind].append(binding)
    return result, traversal


def compare(paths):
    traces = [_state.read_trace(path) for path in paths]
    header, base = traces[0]
    if not header.get("nativePhysicsEntryDiagnostics"):
        raise ValueError("native physics entry diagnostics were not enabled")
    comparisons = []
    for path, (other_header, other) in zip(paths[1:], traces[1:]):
        if header != other_header or [_state.sample_key(row) for row in base] != [
                _state.sample_key(row) for row in other]:
            raise ValueError("physics diagnostics have different headers or sample order")
        first_values, first_order, first_traversal = {}, {}, {}
        mismatch_counts = collections.Counter()
        for left, right in zip(base, other):
            lv, lt = entries(left)
            rv, rt = entries(right)
            location = {name: left[name] for name in ("sequence", "movieFrame", "phase", "boundary", "scene")}
            for kind in sorted(lv.keys() | rv.keys()):
                changed = [binding for binding in sorted(lv[kind].keys() | rv[kind].keys())
                           if lv[kind].get(binding) != rv[kind].get(binding)]
                if changed:
                    target = first_order if kind in ORDER_KINDS else first_values
                    binding = changed[0]
                    target.setdefault(kind, {**location, "changedBindings": len(changed),
                                             "binding": binding, "left": lv[kind].get(binding),
                                             "right": rv[kind].get(binding)})
                    mismatch_counts[kind] += 1
                if lt[kind] != rt[kind]:
                    first_traversal.setdefault(kind, {**location, "leftEntries": len(lt[kind]),
                                                     "rightEntries": len(rt[kind])})
        comparisons.append({"path": str(path), "comparedSamples": len(base),
                            "firstBoundValueDifferences": first_values,
                            "firstNativeOrderDifferences": first_order,
                            "firstRecordTraversalDifferences": first_traversal,
                            "mismatchedSamplesByKind": dict(mismatch_counts)})
    return {"schema": "hktas-p0-physics-entry-comparison-v1", "comparisons": comparisons,
            "comparisonPassed": all(not item["firstBoundValueDifferences"] and
                                    not item["firstNativeOrderDifferences"] and
                                    not item["firstRecordTraversalDifferences"] for item in comparisons),
            "completeStateCoverage": False, "phaseP0Passed": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("traces", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if len(args.traces) < 2:
        parser.error("at least two traces are required")
    report = compare(args.traces)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"comparisonPassed": report["comparisonPassed"],
                      "valueKinds": [list(item["firstBoundValueDifferences"]) for item in report["comparisons"]],
                      "orderKinds": [list(item["firstNativeOrderDifferences"]) for item in report["comparisons"]],
                      "traversalKinds": [list(item["firstRecordTraversalDifferences"]) for item in report["comparisons"]]}))
