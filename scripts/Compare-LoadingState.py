"""Compare P0 state diagnostics exactly; this never grants full-state qualification."""
import argparse
import base64
import zlib
import collections
import hashlib
import json
from pathlib import Path
from loading_inventory import decode_roots
from loading_trace_windows import expected_gameplay_samples, validate_sample_scope

V15_PROFILE = "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v15-experimental"
ACTIVE_K3_CONTRACT = "v15-canonical-active-k3-equivalence-v1"
SUFFIX_SCOPE = "actual-d-suffix-all-loaded-three-after-v1"
ACTIVE_K3_GATES = (
    "gameplayRng", "particleManagerRandom", "nativeScheduler", "nativePhysics/body",
    "physicsBodies", "nativePhysics/contact-active-points",
    "nativePhysics/contact-control-initialized", "nativePhysics/polygon-active",
)
# User-approved 2026-10-30 process-history observations. These values remain
# in the original trace and raw comparison; no live state is ever rewritten.
PROCESS_HISTORY_PROJECTIONS = frozenset((
    "assetBindings", "assetValues", "fsmFields", "monoFields", "nativePhysics",
    "nativePhysics/body-fixture-order",
    "nativePhysics/broadphase", "nativePhysics/contact",
    "nativePhysics/contact-current-manifold", "nativePhysics/contact-inactive-points",
    "nativePhysics/contact-old-manifold", "nativePhysics/contact-toi-cache",
    "nativePhysics/polygon", "nativePhysics/polygon-unused-capacity",
    "nativePhysics/tree-free", "nativePhysics/tree-node", "nativePhysics/unsupported", "registeredStatics",
    "rootCount", "sessionAchievements", "unityHierarchy", "unsupported", "rootInventory",
))


def root_identities(row):
    # Preserve duplicates and concrete types as well as the complete identity set.
    # Exclude asset-value projections which are covered under PROCESS_HISTORY_PROJECTIONS.
    return collections.Counter((root["id"], root.get("type"))
                               for root in row["inventory"]["roots"]
                               if not root["id"].startswith("asset:") and not root["id"].startswith("detached-fsm"))


def decode_suffix_inventory(value):
    if not isinstance(value, dict) or value.get("encoding") != "gzip-base64-utf8-json-v1":
        raise ValueError("unknown suffix inventory encoding")
    if type(value.get("bytes")) is not int or not 0 < value["bytes"] <= 16*1024*1024:
        raise ValueError("suffix inventory byte budget exceeded")
    compressed = base64.b64decode(value["gzipBase64"], validate=True)
    decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
    content = decoder.decompress(compressed, 16*1024*1024 + 1)
    if len(content) != value["bytes"] or not decoder.eof or decoder.unused_data or decoder.unconsumed_tail:
        raise ValueError("suffix inventory length or compressed stream differs")
    if hashlib.sha256(content).hexdigest() != value["sha256"]:
        raise ValueError("suffix inventory digest differs")
    inventory = json.loads(content)
    if inventory.get("format") != "sorted-utf16-prefix-root-names-v1":
        raise ValueError("unknown suffix inventory root identity format")
    inventory["roots"] = decode_roots(dict(rootIdentityEncoding=inventory["format"], rootIdentities=inventory["names"], rootTypes=inventory["types"], roots=inventory["roots"]))
    return inventory


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
    if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1":
        for row in rows:
            row["inventory"] = decode_suffix_inventory(row.get("inventory"))
    validate_sample_scope(header, rows)
    if header.get("samplingScope") == "serial-candidate-boundaries-only-diagnostic":
        if not rows or any(row["phase"] != "candidate" for row in rows):
            raise ValueError(f"{path}: invalid candidate-only scope")
    else:
        gameplay = [(row["movieFrame"], row["phase"]) for row in rows if row["phase"] in ("before", "after")]
        expected = expected_gameplay_samples(header)
        if gameplay != expected:
            raise ValueError(f"{path}: incomplete gameplay window")
    return header, rows


def sample_key(row):
    return tuple(row[name] for name in ("movieFrame", "phase", "boundary", "scene"))


def compare(paths, contract="auto"):
    traces = [read_trace(path) for path in paths]
    header, base = traces[0]
    active = contract != "raw" and header.get("nativeProfile") == V15_PROFILE and header.get("samplingScope") == SUFFIX_SCOPE
    if contract not in ("auto", "raw", ACTIVE_K3_CONTRACT) or contract == ACTIVE_K3_CONTRACT and not active:
        raise ValueError("active K3 contract requires the exact v15 three-sample suffix scope")
    if active and (len(base) != 3 or any(len(rows) != 3 for _, rows in traces)):
        raise ValueError("active K3 contract requires all three original samples")
    comparisons = []
    for path, (other_header, other) in zip(paths[1:], traces[1:]):
        header_differences = sorted(key for key in header.keys() | other_header.keys()
                                    if header.get(key) != other_header.get(key))
        order_equal = [sample_key(row) for row in base] == [sample_key(row) for row in other]
        if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1":
            order_equal = order_equal and [row["nativeFrame"] for row in base] == [row["nativeFrame"] for row in other]
        first_by_field = {}
        blocking = {}
        gates = {key: True for key in ACTIVE_K3_GATES} if active else {}
        same_roots = True
        mismatch_count = 0
        active_mismatch_count = 0
        if not header_differences and order_equal:
            for left, right in zip(base, other):
                changed = sorted(key for key in left["state"].keys() | right["state"].keys()
                                 if left["state"].get(key) != right["state"].get(key))
                if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1" and left["inventory"] != right["inventory"]:
                    changed.append("rootInventory")
                mismatch_count += bool(changed)
                blocked = list(changed)
                if active:
                    blocked = [key for key in changed if key not in PROCESS_HISTORY_PROJECTIONS]
                    for key in ACTIVE_K3_GATES:
                        equal = key in left["state"] and left["state"].get(key) == right["state"].get(key)
                        gates[key] &= equal
                        if not equal and key not in blocked:
                            blocked.append(key)
                    roots_equal = root_identities(left) == root_identities(right)
                    same_roots &= roots_equal
                    if not roots_equal:
                        blocked.append("sameRootIdentitySets")
                active_mismatch_count += bool(blocked)
                for key in blocked:
                    blocking.setdefault(key, {"movieFrame": left["movieFrame"], "sequence": left["sequence"]})
                for key in changed:
                    first_by_field.setdefault(key, {
                        "movieFrame": left["movieFrame"], "phase": left["phase"],
                        "boundary": left["boundary"], "sequence": left["sequence"],
                        "expected": left["state"].get(key) if key != "rootInventory" else "see exact sample inventory",
                        "actual": right["state"].get(key) if key != "rootInventory" else "see exact sample inventory"})
        comparisons.append({"path": str(path), "headerDifferences": header_differences,
                            "sampleOrderEqual": order_equal, "comparedSamples": len(base) if order_equal else 0,
                            "mismatchedSamples": mismatch_count if order_equal else None,
                            "firstDifferences": first_by_field,
                            "blockingDifferences": blocking,
                            "activeMismatchedSamples": active_mismatch_count if order_equal else None,
                            "allThreeK3Equal": gates,
                            "sameRootIdentitySets": same_roots if active and order_equal and not header_differences else None,
                            "diagnosticProcessHistoryProjections": sorted(set(first_by_field) & PROCESS_HISTORY_PROJECTIONS) if active else []})
    return {"schema": "hktas-p0-state-comparison-v1", "first": header["first"], "end": header["end"],
            "coverage": header["coverage"], "samplingScope": header["samplingScope"],
            "phases": dict(collections.Counter(row["phase"] for row in base)),
            "comparisonContract": ACTIVE_K3_CONTRACT if active else "raw-exact-v1",
            "rawComparisonPassed": all(not item["headerDifferences"] and item["sampleOrderEqual"]
                                       and item["mismatchedSamples"] == 0 for item in comparisons),
            "comparisonPassed": all(not item["headerDifferences"] and item["sampleOrderEqual"]
                                    and item["activeMismatchedSamples"] == 0 for item in comparisons),
            "completeStateCoverage": False, "phaseP0Passed": False, "comparisons": comparisons}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("traces", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--contract", choices=("auto", "raw", ACTIVE_K3_CONTRACT), default="auto")
    args = parser.parse_args()
    if len(args.traces) < 2:
        parser.error("at least two independently collected traces are required")
    report = compare(args.traces, args.contract)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"comparisonPassed": report["comparisonPassed"],
                      "rawComparisonPassed": report["rawComparisonPassed"], "comparisonContract": report["comparisonContract"],
                      "comparedSamples": [item["comparedSamples"] for item in report["comparisons"]],
                      "firstDifferenceFields": [list(item["firstDifferences"]) for item in report["comparisons"]]}))
