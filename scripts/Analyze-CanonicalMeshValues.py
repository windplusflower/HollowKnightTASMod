"""Compare existing bounded independent Mesh projections by observed consumer identity."""
import argparse
import collections
import json
from pathlib import Path


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    audits = {}
    for role in ("source", "target"):
        folder = args.evidence / role / "run-01" / f"actual-d-{role}-suffix-oracle"
        audit = load(folder / "mesh-value-audit-1.json")
        if audit.get("refused") or not audit.get("existingProjectionOnly") or not audit.get("independentPerField"):
            raise ValueError("Mesh projection audit is incomplete")
        audits[role] = audit
    if (audits["source"]["movieFrame"], audits["source"]["nativeFrame"]) != (audits["target"]["movieFrame"], audits["target"]["nativeFrame"]):
        raise ValueError("Mesh projection samples do not describe the same logical frame")
    values = {role: {item["asset"]: item["value"] for item in audit["entries"]} for role, audit in audits.items()}
    correlation_file = load(args.evidence / "mesh-owner-correlation.json")
    correlation = correlation_file["samples"][0]
    if correlation["ambiguousSource"] or correlation["ambiguousTarget"]:
        raise ValueError("Consumer-to-Mesh mapping is ambiguous")
    pairs, fields = [], collections.Counter()
    for row in correlation["mapping"]:
        a, b = values["source"][row["source"]], values["target"][row["target"]]
        fa = {field["name"]: field for field in a["fields"]}
        fb = {field["name"]: field for field in b["fields"]}
        changed = [name for name in sorted(set(fa) | set(fb)) if fa.get(name) != fb.get(name)]
        ca = {(item["field"], item["index"]): item for item in a["channels"]}
        cb = {(item["field"], item["index"]): item for item in b["channels"]}
        changed_channels = [f"{name}[{index}]" for name, index in sorted(set(ca) | set(cb)) if ca.get((name, index)) != cb.get((name, index))]
        alias_equal = a["emptyArrayAliasGroups"] == b["emptyArrayAliasGroups"]
        flags_equal = a["hideFlags"] == b["hideFlags"]
        fields.update(changed)
        pairs.append({"source": row["source"], "target": row["target"], "originalDigestEqual": row["sameRawDigest"],
            "changedFields": changed, "changedChannels": changed_channels, "emptyArrayAliasesEqual": alias_equal,
            "hideFlagsEqual": flags_equal, "independentProjectionEqual": not changed and not changed_channels and alias_equal and flags_equal})
    extra = correlation["sourceWithoutMappedConsumer"]
    matches = {}
    for source in extra:
        value = values["source"][source]
        # Report each distinct object independently; equality is never used to merge identities.
        matches[source] = [target for target, candidate in values["target"].items() if value == candidate]
    output = {"diagnosticOnly": True, "formalHashUnchanged": True, "priorOneBasedRootIndexResultsInvalidated": True,
        "sameFrameConsumerProof": correlation_file.get("referenceSampleMatched", False), "movieFrame": audits["source"]["movieFrame"],
        "nativeFrame": audits["source"]["nativeFrame"], "sourceCount": len(values["source"]), "targetCount": len(values["target"]),
        "mappedPairs": len(pairs), "originalDigestDifferent": sum(not row["originalDigestEqual"] for row in pairs),
        "independentProjectionDifferent": sum(not row["independentProjectionEqual"] for row in pairs),
        "originalDifferentButIndependentEqual": sum(not row["originalDigestEqual"] and row["independentProjectionEqual"] for row in pairs),
        "changedFieldCounts": dict(sorted(fields.items())), "changedChannelPairs": sum(bool(row["changedChannels"]) for row in pairs),
        "changedEmptyAliasPairs": sum(not row["emptyArrayAliasesEqual"] for row in pairs),
        "changedHideFlagsPairs": sum(not row["hideFlagsEqual"] for row in pairs),
        "sourceUnmatchedEquivalentProjectionTargets": matches, "pairs": pairs}
    (args.evidence / "mesh-independent-value-comparison.json").write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({key: value for key, value in output.items() if key not in ("pairs", "sourceUnmatchedEquivalentProjectionTargets")}, ensure_ascii=False))
    print(json.dumps({key: {"count": len(value), "sample": value[:12]} for key, value in matches.items()}, ensure_ascii=False))


if __name__ == "__main__":
    main()
