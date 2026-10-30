"""Audit the complete B2 Mesh batch, retaining all raw failures and real consumers."""
import argparse
import importlib.util
import json
from pathlib import Path


def module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


strict = module("loading_state_comparison", "Compare-LoadingState.py")
mesh_owners = module("mesh_owners", "Analyze-CanonicalMeshOwners.py")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def analyze(evidence):
    traces, audits, batches, inventories = {}, {}, {}, {}
    for role in ("source", "target"):
        folder = evidence / role / "run-01" / f"actual-d-{role}-suffix-oracle"
        traces[role] = strict.read_trace(folder / "loading-gameplay-oracle.jsonl")
        audits[role] = load(folder / "mesh-value-audit-1.json")
        audit = audits[role]
        if audit.get("refused") or not all(audit.get(key) is True for key in
                ("existingProjectionOnly", "existingFieldWalkOnly", "independentPerField")):
            raise ValueError("Incomplete Mesh projection or consumer audit")
        if len(traces[role][1]) != 3:
            raise ValueError("Expected all three original K3 samples")
        header = tuple(audit.get(key) for key in ("movieFrame", "nativeFrame", "sampleOrdinal", "runtimeSha256"))
        first = traces[role][1][0]
        if None in header or header[:3] != (first["movieFrame"], first["nativeFrame"], first["sequence"]):
            raise ValueError("Mesh audit does not identify the first exact K3 sample")
        inventories[role] = {row["id"]: row for row in first["inventory"]["roots"]}
        path = folder / "visual-mesh-batch-audit-1.json"
        if path.exists():
            batches[role] = load(path)
            if tuple(batches[role].get(key) for key in ("movieFrame", "nativeFrame", "sampleOrdinal", "runtimeSha256")) != header:
                raise ValueError("Batch audit and Mesh projection identity differ")
    if any(audits["source"][key] != audits["target"][key] for key in
           ("movieFrame", "nativeFrame", "sampleOrdinal", "runtimeSha256")):
        raise ValueError("Independent Mesh audits have different frame/runtime identities")
    reference_maps = {}
    for role, audit in audits.items():
        by_asset = {}
        for row in audit["references"]:
            by_asset.setdefault(row["asset"], set()).add((row["root"], row["path"]))
        reference_maps[role] = by_asset
    paired, unmatched, mapping = [], [], {}
    target_slots = {}
    for asset, slots in reference_maps["target"].items():
        target_slots.setdefault(frozenset(slots), []).append(asset)
    used_targets = set()
    source_values = {row["asset"]: row["value"] for row in audits["source"]["entries"]}
    target_values = {row["asset"]: row["value"] for row in audits["target"]["entries"]}
    for asset, value in source_values.items():
        slots = reference_maps["source"].get(asset, set())
        matches = target_slots.get(frozenset(slots), []) if slots else []
        if len(matches) > 1:
            raise ValueError("Ambiguous complete Mesh consumer slot set")
        if not matches:
            unmatched.extend(mesh_owners.unmatched_consumers([asset], reference_maps["source"]))
            continue
        target = matches[0]
        if target in used_targets:
            raise ValueError("A target Mesh was paired more than once")
        used_targets.add(target)
        mapping[asset] = target
        left = {row["name"]: row for row in value["fields"]}
        right = {row["name"]: row for row in target_values[target]["fields"]}
        changes = sorted(key for key in left.keys() | right.keys() if left.get(key) != right.get(key))
        if changes or value["channels"] != target_values[target]["channels"] or value["emptyArrayAliasGroups"] != target_values[target]["emptyArrayAliasGroups"] or value["hideFlags"] != target_values[target]["hideFlags"]:
            paired.append(dict(source=asset, target=target, changedFields=changes,
                               consumers=mesh_owners.unmatched_consumers([asset], reference_maps["source"])[0]["consumers"]))
    owner_changes = []
    if len(batches) == 2:
        def owners(role):
            rows = batches[role]["batch"]["owners"]
            result = {(row["key"], row["type"]): row for row in rows}
            if len(result) != len(rows) or batches[role]["batch"]["ownerCount"] != 5 or not batches[role]["rngReadOnly"]:
                raise ValueError("Batch owner directory or read-only RNG guard differs")
            return result
        left, right = owners("source"), owners("target")
        if left.keys() != right.keys():
            raise ValueError("Batch component directory differs across processes")
        for key in sorted(left):
            a, b = left[key], right[key]
            fa, fb = ({field["name"]: field for field in row["fields"]} for row in (a, b))
            changed = [name for name in sorted(fa.keys() | fb.keys()) if fa.get(name) != fb.get(name)]
            owner_changes.append(dict(key=key[0], type=key[1], completeDigestEqual=a["completeDigest"] == b["completeDigest"],
                                      changedFields=changed, sourceUnsupported=a["unsupported"], targetUnsupported=b["unsupported"],
                                      sourceFilterMesh=a["filterMesh"], targetFilterMesh=b["filterMesh"],
                                      rendererMaterialsEqual=a["materials"] == b["materials"],
                                      poseBitsEqual=a["poseBits"] == b["poseBits"],
                                      activationEqual=all(a[name] == b[name] for name in ("enabled", "activeSelf", "activeInHierarchy")),
                                      rendererEnabledEqual=a["rendererEnabled"] == b["rendererEnabled"],
                                      componentLayoutEqual=a["components"] == b["components"]))
    native_comparison = None
    if len(batches) == 2:
        def native_slots(role):
            return sorted((mapping.get(row["asset"], "source-unpaired:" + row["asset"]) if role == "source" else row["asset"],
                           row["owner"], row["slot"])
                          for row in batches[role]["batch"]["nativeConsumers"]["rows"])
        a, b = native_slots("source"), native_slots("target")
        native_comparison = dict(completeSlotSetsEqual=a == b,
                                 sourceOnly=sorted(set(a) - set(b)), targetOnly=sorted(set(b) - set(a)),
                                 deferredConsumersStillRequireOriginalCallerProof=True)
    hero_details = {}
    for role, audit in audits.items():
        candidates = [row["value"]["heroSubmeshDetails"] for row in audit["entries"] if row["value"].get("heroSubmeshDetails") is not None]
        if len(candidates) == 1:
            hero_details[role] = candidates[0]
    descriptor_changes = None
    if len(hero_details) == 2:
        if len(hero_details["source"]) != len(hero_details["target"]):
            raise ValueError("Hero submesh directory differs")
        descriptor_changes = []
        for a, b in zip(hero_details["source"], hero_details["target"]):
            x, y = ({row["path"]: row for row in item["descriptor"]} for item in (a, b))
            descriptor_changes.append(dict(index=a["index"], indicesEqual=a["indices"] == b["indices"],
                differences=[dict(path=key, source=x.get(key), target=y.get(key))
                             for key in sorted(x.keys() | y.keys()) if x.get(key) != y.get(key)]))
    raw = strict.compare([evidence / role / "run-01" / f"actual-d-{role}-suffix-oracle/loading-gameplay-oracle.jsonl"
                          for role in ("source", "target")])
    exact_guards = {key: all(a["state"][key] == b["state"][key] for a, b in zip(traces["source"][1], traces["target"][1]))
                    for key in ("gameplayRng", "particleManagerRandom", "nativeScheduler")}
    return dict(diagnosticOnly=True, comparisonRulesUnchanged=True, rawComparison=raw,
                allThreeK3Retained=True, batchAuditCollected=len(batches) == 2,
                exactThreeK3Guards=exact_guards, heroSubmeshDescriptorComparison=descriptor_changes,
                pairedMeshCount=len(used_targets), pairedMeshValueDifferences=paired,
                sourceUnpairedMeshes=unmatched, targetUnpairedMeshes=sorted(set(target_values) - used_targets),
                ownerFieldComparison=owner_changes,
                nativeConsumerComparison=native_comparison,
                noConsumerClaimDisproved=any(row["hasObservedConsumers"] for row in unmatched),
                registeredVisualPolicyAdmitted=False, completeStateCoverage=False, restoreQualified=False)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("evidence", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    report = analyze(args.evidence)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(dict(pairedMeshValueDifferences=len(report["pairedMeshValueDifferences"]),
                          unpairedMeshes=len(report["sourceUnpairedMeshes"]),
                          noConsumerClaimDisproved=report["noConsumerClaimDisproved"],
                          batchAuditCollected=report["batchAuditCollected"],
                          rawComparisonPassed=report["rawComparison"]["comparisonPassed"])))
