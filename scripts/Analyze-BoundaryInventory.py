"""Summarize explicit Phase 0 inventory files; never infer restore coverage."""
import argparse
import collections
import json
from pathlib import Path


def summarize(paths):
    samples = []
    types = collections.defaultdict(lambda: {"classifications": set(), "examples": set(), "pendingExamples": set(), "maxCount": 0})
    pools = collections.defaultdict(lambda: {"maxIdle": 0, "maxLive": 0, "boundariesWithLive": []})
    hero_fsms = collections.defaultdict(set)
    schedulers = collections.defaultdict(set)
    statics = collections.defaultdict(set)
    omissions = set()
    for path in paths:
        document = json.loads(Path(path).read_text(encoding="utf-8-sig"))
        if document.get("schema") != 1 or not document.get("diagnosticOnly"):
            raise ValueError(f"Unsupported inventory: {path}")
        if not document["boundaries"]:
            raise ValueError(f"No actual boundaries: {path}")
        for boundary in document["boundaries"]:
            label = f"{Path(path).parent.parent.name}/B{boundary['ordinal']}"
            counts = collections.Counter()
            for obj in boundary["objects"]:
                for component in obj["components"]:
                    name = component["type"]
                    entry = types[name]
                    entry["classifications"].add(component["classification"])
                    if len(entry["examples"]) < 3:
                        entry["examples"].add(obj["id"])
                    if component["classification"] == "Pending" and len(entry["pendingExamples"]) < 3:
                        entry["pendingExamples"].add(obj["id"])
                    counts[name] += 1
            for name, count in counts.items():
                types[name]["maxCount"] = max(types[name]["maxCount"], count)
            for pool in boundary["pools"]:
                for table in pool["tables"]:
                    entry = pools[table["prefabName"]]
                    entry["maxIdle"] = max(entry["maxIdle"], len(table["idle"]))
                    entry["maxLive"] = max(entry["maxLive"], len(table["live"]))
                    if table["live"]:
                        entry["boundariesWithLive"].append(label)
            for fsm in boundary["fsms"]:
                if fsm["classification"].startswith("D:HeroFSM"):
                    hero_fsms[fsm["name"]].add(fsm["id"])
            for row in boundary["nativeScheduler"]:
                if row["kind"] == "coroutine":
                    schedulers[row["enumeratorType"]].add(row["id"])
            for row in boundary["staticTables"]:
                statics[row["owner"]].add(row["field"])
            omissions.update(boundary["schedulerUnsupported"])
            samples.append({"file": str(path), "ordinal": boundary["ordinal"], "source": boundary["source"],
                            "target": boundary["target"], "movie": boundary["movie"], "native": boundary["native"],
                            "objects": len(boundary["objects"]), "components": sum(counts.values()),
                            "elapsedMilliseconds": boundary["elapsedMilliseconds"]})
    return {"schema": 1, "diagnosticOnly": True, "completeStateCoverage": False, "samples": samples,
            "componentTypes": {name: {key: sorted(value) if isinstance(value, set) else value
                                       for key, value in entry.items()} for name, entry in sorted(types.items())},
            "pools": dict(sorted(pools.items())), "heroFsms": {key: sorted(value) for key, value in sorted(hero_fsms.items())},
            "schedulerTypes": {key: sorted(value) for key, value in sorted(schedulers.items())},
            "staticFields": {key: sorted(value) for key, value in sorted(statics.items())},
            "schedulerUnsupported": sorted(omissions),
            "pendingComponentTypes": sorted(name for name, entry in types.items() if "Pending" in entry["classifications"])}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Choose a fresh output file; retain earlier observations.")
    result = summarize(args.inventory)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"boundaries": len(result["samples"]), "componentTypes": len(result["componentTypes"]),
                      "livePoolTypes": [key for key, value in result["pools"].items() if value["maxLive"]],
                      "pendingComponentTypes": len(result["pendingComponentTypes"])}, ensure_ascii=False))
