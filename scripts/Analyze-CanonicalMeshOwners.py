"""Compare existing anonymous Mesh evidence by observed consumer, never alter equality."""
import argparse
import base64
import collections
import gzip
import hashlib
import json
from pathlib import Path


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def inventory(row):
    envelope = row["inventory"]
    raw = gzip.decompress(base64.b64decode(envelope["gzipBase64"]))
    if len(raw) != envelope["bytes"] or hashlib.sha256(raw).hexdigest() != envelope["sha256"]:
        raise ValueError("inventory length/hash differs")
    data = json.loads(raw)
    names, previous = [], b""
    for item in data["names"]:
        previous = previous[:item["prefix"] * 2] + item["suffix"].encode("utf-16-le")
        names.append(previous.decode("utf-16-le"))
    result = {}
    for item in data["roots"]:
        if not isinstance(item["id"], int) or item["id"] < 0 or item["id"] >= len(names):
            raise ValueError("inventory root id is outside its zero-based name table")
        name = names[item["id"]]
        if name.startswith("asset:UnityEngine.Mesh:") and name.endswith("/asset-values") and name in result:
            raise ValueError("Mesh asset root identity is duplicated")
        result[name] = item
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("evidence", type=Path)
    args = parser.parse_args()
    samples, references, headers = {}, {}, {}
    for role in ("source", "target"):
        folder = args.evidence / role / "run-01" / f"actual-d-{role}-suffix-oracle"
        samples[role] = [json.loads(line) for line in (folder / "loading-gameplay-oracle.jsonl").read_text(encoding="utf-8-sig").splitlines() if json.loads(line).get("kind") == "sample"]
        paired = folder / "mesh-value-audit-1.json"
        candidate = load(paired) if paired.exists() else None
        if candidate and "references" in candidate:
            audit = {"refused": candidate.get("refused"), "existingFieldWalkOnly": candidate.get("existingFieldWalkOnly"), "entries": candidate["references"]}
            headers[role] = {key: candidate.get(key) for key in ("movieFrame", "nativeFrame", "sampleOrdinal", "runtimeSha256")}
        else:
            audit = load(folder / "mesh-reference-audit.json")
        if audit.get("refused") or not audit.get("existingFieldWalkOnly"):
            raise ValueError("Mesh owner audit is incomplete")
        refs = collections.defaultdict(set)
        for item in audit["entries"]:
            refs[item["asset"]].add((item["root"], item["path"]))
        references[role] = refs
    if len(samples["source"]) != 3 or len(samples["target"]) != 3:
        raise ValueError("expected original three samples")
    if headers and (len(headers) != 2 or headers["source"] != headers["target"] or None in headers["source"].values()):
        raise ValueError("paired Mesh audit sample/runtime identity differs")
    # A consumer pair is evidence of a relationship, not an equality rule.
    by_consumer = {}
    for role in ("source", "target"):
        reverse = collections.defaultdict(set)
        for asset, paths in references[role].items():
            for path in paths:
                reverse[path].add(asset)
        by_consumer[role] = reverse
    pairs = collections.defaultdict(set)
    for asset, paths in references["source"].items():
        for path in paths:
            for other in by_consumer["target"].get(path, ()):
                pairs[asset].add(other)
    pairs = {asset: sorted(others) for asset, others in pairs.items()}
    output = {"diagnosticOnly": True, "comparisonRulesUnchanged": True,
              "priorOneBasedRootIndexResultsInvalidated": True,
              "referenceSampleMatched": bool(headers), "referenceHeader": headers.get("source"), "samples": []}
    for left, right in zip(samples["source"], samples["target"]):
        a, b = inventory(left), inventory(right)
        meshes_a = {name.removesuffix("/asset-values"): item for name, item in a.items() if name.startswith("asset:UnityEngine.Mesh:") and name.endswith("/asset-values")}
        meshes_b = {name.removesuffix("/asset-values"): item for name, item in b.items() if name.startswith("asset:UnityEngine.Mesh:") and name.endswith("/asset-values")}
        mapping = []
        for asset, others in sorted(pairs.items()):
            for other in others:
                if asset not in meshes_a or other not in meshes_b:
                    continue
                mapping.append({"source": asset, "target": other,
                    "sourceConsumerCount": len(references["source"][asset]), "targetConsumerCount": len(references["target"][other]),
                    "sameConsumerSet": references["source"][asset] == references["target"][other],
                    "sourceDigest": meshes_a[asset]["digest"], "targetDigest": meshes_b[other]["digest"],
                    "sameRawDigest": meshes_a[asset]["digest"] == meshes_b[other]["digest"]})
        reverse = collections.Counter(row["target"] for row in mapping)
        source_mapped = {row["source"] for row in mapping}
        target_mapped = set(reverse)
        output["samples"].append({"movieFrame": left["movieFrame"], "sourceMeshCount": len(meshes_a), "targetMeshCount": len(meshes_b),
            "sameFrameReference": bool(headers) and left["movieFrame"] == headers["source"]["movieFrame"] and left["nativeFrame"] == headers["source"]["nativeFrame"],
            "mappedPairs": len(mapping), "sourceWithoutMappedConsumer": sorted(set(meshes_a) - source_mapped),
            "targetWithoutMappedConsumer": sorted(set(meshes_b) - target_mapped),
            "ambiguousSource": {asset: others for asset, others in pairs.items() if len(others) != 1},
            "ambiguousTarget": sorted(asset for asset, count in reverse.items() if count != 1),
            "sameConsumerSetAndRawDigest": sum(row["sameConsumerSet"] and row["sameRawDigest"] for row in mapping),
            "mapping": mapping})
    dest = args.evidence / "mesh-owner-correlation.json"
    dest.write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding="utf-8")
    for sample in output["samples"]:
        print(json.dumps({key: value for key, value in sample.items() if key != "mapping"}, ensure_ascii=False))


if __name__ == "__main__":
    main()
