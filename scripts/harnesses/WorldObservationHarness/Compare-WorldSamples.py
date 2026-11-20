#!/usr/bin/env python3
"""Compare semantic multisets from paused world samples; never controls the game."""
from __future__ import annotations

import argparse
from collections import Counter
import copy
import hashlib
import json
import math
from pathlib import Path
import re
import sys


HASH_FIELDS = ("fixtureSha256", "runtimeAssemblySha256", "coreAssemblySha256")
SAMPLE_NAME = re.compile(r"sample-(\d+)-world\.json$")
EXCLUDED = [
    "Snapshot/envelope identity: snapshotId, sessionId, epoch, captureSequence, objectId, detailsId",
    "Unity identity: object id/parentId, component instanceId, scene.handle; references use scene/name/path/type",
    "Absolute timing: nativeFrame, unityFrameCount, metadata.time.time, metadata.time.fixedTime",
    "FSM currentState.realStartTime and raw action fields are outside this activeState/variables projection",
    "Rendering-only data: renderer bounds, screenPaths, camera/viewport state; terrain uses world-space geometry",
    "Unselected detail fields/components and all non-Envious objects except hero and static terrain",
]
SEMANTIC_RULES = [
    "HeroController.preventCastByDialogueEndTimer in hero.cooldowns: finite values <= 0 normalize to 0; positive/nonfinite values remain exact. Installed CanCast tests > 0; PreventCastByDialogueEnd sets 0.3; Update decrements by deltaTime.",
    "EnviousMarmu object's Control FSM, FsmGameObject Voice Player only: nonnull UnityEngine.GameObject reference named Audio Player Actor 2D(Clone) with null componentType compares name/type/componentType, retaining original resolution diagnostics. All 21 loaded states only use this variable in AudioPlayerOneShot.storePlayer (Warp/Warp Out 2). Null or other-name/type references remain exact.",
    "Collider geometry.attachedRigidbodyInstanceId is runtime identity only; body display/physics poses, definitions, bounds and worldPaths are retained.",
]


def read_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, separators=(",", ":"), allow_nan=False)


def digest(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def identity(obj):
    return {"scene": obj.get("scene", {}).get("name"), "path": obj.get("path"), "name": obj.get("name")}


def custom_object(obj):
    return any(c.get("assembly") == "EnviousMarmu" for c in obj.get("components", []))


def normalize(value, refs):
    if isinstance(value, list):
        return [normalize(item, refs) for item in value]
    if not isinstance(value, dict):
        return value
    # Only recognized Unity reference records lose IDs. A Mod field whose name
    # happens to be "id" is retained in its ordinary name/value field record.
    if "instanceId" in value and "type" in value and "name" in value:
        target = refs.get(value.get("objectId"))
        return {"reference": identity(target) if target else {"name": value.get("name"), "path": None, "scene": None},
                "type": value.get("type"), "componentType": value.get("componentType"),
                "scope": value.get("scope"), "resolvedInSample": target is not None}
    return {key: normalize(item, refs) for key, item in value.items()}


def multiset(values):
    # Sorting retains every repeated value, including identical sibling clones.
    return sorted(values, key=canonical)


def collider_projection(collider, refs):
    keys = ("componentIndex", "type", "enabled", "activeInHierarchy", "isTrigger", "usedByComposite",
            "layer", "layerName", "classification", "offset", "bounds", "definition", "worldPaths",
            "geometry", "geometryStatus", "geometryApproximation", "approximation", "omitted", "reason")
    result = normalize({key: collider[key] for key in keys if key in collider}, refs)
    if isinstance(result.get("geometry"), dict):
        result["geometry"].pop("attachedRigidbodyInstanceId", None)
    return result


def dialogue_timer_fields(hero):
    return [field for field in hero.get("cooldowns", {}).get("fields", [])
            if field.get("declaringType") == "HeroController" and field.get("name") == "preventCastByDialogueEndTimer"]


def expired_timer(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value <= 0


def voice_player_reference(fsm, variable):
    if fsm.get("name") != "Control" or variable.get("name") != "Voice Player" or variable.get("type") != "HutongGames.PlayMaker.FsmGameObject":
        return None
    reference = variable.get("raw", {}).get("value")
    if not isinstance(reference, dict) or reference.get("name") != "Audio Player Actor 2D(Clone)" or reference.get("type") != "UnityEngine.GameObject" or reference.get("componentType") is not None:
        return None
    return reference


def fsm_projection(fsm, refs, semantic):
    selected = {key: fsm.get(key) for key in
        ("componentIndex", "name", "enabled", "activeInHierarchy", "activeState", "initialized",
         "variables", "variableCount", "variablesAvailable", "variablesOmittedReason")}
    result = normalize(selected, refs)
    if semantic:
        for source, target in zip(fsm.get("variables", []), result.get("variables", [])):
            reference = voice_player_reference(fsm, source)
            if reference is not None:
                target["raw"]["value"] = {key: reference.get(key) for key in ("name", "type", "componentType")}
    return result


def object_projection(obj, refs, hero=False, semantic=False):
    result = identity(obj)
    for key in ("activeSelf", "activeInHierarchy", "layer", "layerName", "tag", "transform", "rigidbodies", "health"):
        result[key] = normalize(obj.get(key), refs)
    result["colliders"] = multiset([collider_projection(collider, refs) for collider in obj.get("colliders", [])])
    if hero:
        result["hero"] = normalize(obj.get("hero"), refs)
        if semantic:
            for field in dialogue_timer_fields(result["hero"]):
                if expired_timer(field.get("value")):
                    field["value"] = 0
    else:
        result["customComponents"] = multiset([
            {key: component.get(key) for key in ("index", "type", "assembly", "enabled")}
            for component in obj.get("components", []) if component.get("assembly") == "EnviousMarmu"])
        result["fsms"] = multiset([fsm_projection(fsm, refs, semantic) for fsm in obj.get("fsms", [])])
    return result


def field_values(component):
    return {field["name"]: field.get("value") for field in component.get("data", {}).get("fields", [])}


def load_sample(directory, frame):
    path = directory / f"sample-{frame}-world.json"
    world = read_json(path)
    if world.get("movieFrame") != frame:
        raise ValueError(f"{path}: movieFrame does not match filename")
    objects = world.get("objects")
    if not isinstance(objects, list) or not objects:
        raise ValueError(f"{path}: no objects")
    if world.get("total") != len(objects) or world.get("nextOffset") != -1:
        raise ValueError(f"{path}: incomplete pagination")
    metadata = world.get("metadata", {})
    if metadata.get("enumerationComplete") is not True or metadata.get("errors"):
        raise ValueError(f"{path}: enumeration incomplete or collector errors")
    details = []
    for detail_path in sorted(directory.glob(f"sample-{frame}-custom-*.json")):
        if not re.fullmatch(rf"sample-{frame}-custom-\d+\.json", detail_path.name):
            continue
        detail = read_json(detail_path)
        if detail.get("movieFrame") != frame or detail.get("found") is not True:
            raise ValueError(f"{detail_path}: detail is missing or from a different frame")
        if detail.get("nativeFrame") != world.get("nativeFrame"):
            raise ValueError(f"{detail_path}: native frame differs from its world sample")
        if not custom_object(detail.get("object", {})):
            raise ValueError(f"{detail_path}: no EnviousMarmu component")
        details.append(detail)
    return world, details


def project(world, details, semantic=False):
    objects = world["objects"]
    refs = {obj["id"]: obj for obj in objects}
    refs.update({detail["object"]["id"]: detail["object"] for detail in details})
    heroes = [obj for obj in objects if obj.get("kind") == "hero"]
    if len(heroes) != 1:
        raise ValueError(f"Expected one hero; found {len(heroes)}")
    if not heroes[0].get("transform") or not heroes[0].get("hero") or heroes[0].get("errors"):
        raise ValueError("Hero transform/state omitted")
    enemies = [obj for obj in objects if custom_object(obj)]
    for obj in enemies:
        if obj.get("errors"):
            raise ValueError(f"Collector errors for {obj.get('path')}")
        for fsm in obj.get("fsms", []):
            if not isinstance(fsm.get("variables"), list) or fsm.get("variablesAvailable") is False or fsm.get("variablesOmitted"):
                raise ValueError(f"FSM variables unavailable for {obj.get('path')}")
    custom_details = []
    for detail in details:
        components = []
        for component in detail["components"]:
            if component.get("assembly") != "EnviousMarmu":
                continue
            if component.get("error") or "data" not in component:
                raise ValueError(f"Custom detail unavailable for {detail['object'].get('path')}")
            components.append({"type": component["type"], "data": normalize(component["data"], refs)})
        custom_details.append({"object": object_projection(detail["object"], refs, semantic=semantic), "components": multiset(components)})
    terrain = []
    for obj in objects:
        bodies = obj.get("rigidbodies", [])
        if bodies and any(body.get("bodyType") != "Static" for body in bodies):
            continue
        colliders = [collider_projection(c, refs) for c in obj.get("colliders", [])
                     if c.get("classification") == "terrain"]
        if colliders:
            terrain.append({"object": identity(obj), "colliders": multiset(colliders)})
    timing = world.get("metadata", {}).get("time", {})
    return {"movieFrame": world["movieFrame"], "scene": world["metadata"].get("activeScene", {}).get("name"),
            "frameTiming": {key: timing.get(key) for key in ("deltaTime", "fixedDeltaTime", "timeScale")},
            "heroes": multiset([object_projection(obj, refs, True, semantic) for obj in heroes]),
            "marmuObjects": multiset([object_projection(obj, refs, semantic=semantic) for obj in enemies]),
            "customDetails": multiset(custom_details), "staticTerrain": multiset(terrain)}


def diagnostics(world, details):
    refs = {obj["id"]: obj for obj in world["objects"]}
    refs.update({detail["object"]["id"]: detail["object"] for detail in details})
    hero = next(obj for obj in world["objects"] if obj.get("kind") == "hero")
    timers = [{"field": field["name"], "rawValue": field.get("value"), "expired": expired_timer(field.get("value")),
               "semanticValue": 0 if expired_timer(field.get("value")) else field.get("value"),
               "reason": "CanCast only blocks while > 0; finite nonpositive values are already expired."}
              for field in dialogue_timer_fields(hero["hero"])]
    audio = []
    for scope, objects in (("world", world["objects"]), ("customDetails", [detail["object"] for detail in details])):
        for obj in objects:
            if not custom_object(obj):
                continue
            for fsm in obj.get("fsms", []):
                for variable in fsm.get("variables", []):
                    reference = voice_player_reference(fsm, variable)
                    if reference is None:
                        continue
                    target = refs.get(reference.get("objectId"))
                    audio.append({"scope": scope, "object": identity(obj), "fsm": fsm["name"], "variable": variable["name"],
                                  "rawReference": reference, "resolvedInSample": target is not None,
                                  "resolvedTarget": identity(target) if target else None,
                                  "resolvedComponents": sorted(c.get("type", "") for c in target.get("components", [])) if target else None,
                                  "reason": "Presentation audio pool reference; absence from active world enumeration does not by itself prove destruction."})
    return {"dialogueTimer": timers, "voicePlayerReferences": audio}


def first_differences(left, right, path="$", limit=20):
    results = []

    def walk(a, b, location):
        if len(results) >= limit or a == b:
            return
        if isinstance(a, dict) and isinstance(b, dict):
            for key in sorted(set(a) | set(b)):
                if key not in a or key not in b:
                    results.append({"path": location + "." + key, "baseline": a.get(key), "replay": b.get(key), "missingKey": True})
                else:
                    walk(a[key], b[key], location + "." + key)
                if len(results) >= limit:
                    break
        elif isinstance(a, list) and isinstance(b, list):
            if len(a) != len(b):
                results.append({"path": location + ".length", "baseline": len(a), "replay": len(b)})
            for i, (first, second) in enumerate(zip(a, b)):
                walk(first, second, f"{location}[{i}]")
                if len(results) >= limit:
                    break
        else:
            results.append({"path": location, "baseline": a, "replay": b})

    walk(left, right, path)
    return results[:limit]


def progress(world, details, previous):
    custom = [obj for obj in world["objects"] if custom_object(obj)]
    detail_by_id = {detail["object"]["id"]: detail for detail in details}
    nodes, managers, next_hp = [], [], {}
    for obj in custom:
        detail = detail_by_id.get(obj["id"])
        node_fields = manager_fields = None
        if detail:
            for component in detail["components"]:
                if component.get("type") == "EnviousMarmu.MarmuNode":
                    node_fields = field_values(component)
                elif component.get("type") == "EnviousMarmu.MarmuBattleManager":
                    manager_fields = field_values(component)
        if manager_fields is not None:
            managers.append({name: manager_fields.get(name) for name in ("_aliveCount", "_t", "_ended")})
        if not obj.get("health"):
            continue
        health = obj["health"][0]
        hp = health.get("hp")
        if isinstance(hp, (int, float)):
            next_hp[obj["id"]] = hp
        tier = node_fields.get("<Tier>k__BackingField", {}).get("name") if node_fields else None
        inferred = obj["name"].removeprefix("Marmu ") if obj["name"].startswith("Marmu ") else "Giant" if obj["name"] == "Ghost Warrior Marmu" else "Unknown"
        nodes.append({**identity(obj), "hp": hp, "isDead": health.get("isDead"), "isInvincible": health.get("isInvincible"),
                      "tier": tier or inferred, "tierSource": "customField" if tier else "objectNameInference"})
    hero = next(obj for obj in world["objects"] if obj.get("kind") == "hero")
    observed_drop = sum(max(0, old_hp - next_hp[oid]) for oid, old_hp in previous.items() if oid in next_hp)
    return {"heroResources": hero["hero"].get("resources"), "enemyCount": len(nodes),
            "aliveEnemyCount": sum(1 for n in nodes if not n["isDead"] and (n["hp"] or 0) > 0),
            "activeEnemyHpTotal": sum(max(0, n["hp"] or 0) for n in nodes if not n["isDead"]),
            "tiers": dict(sorted(Counter(n["tier"] for n in nodes).items())), "enemies": multiset(nodes), "managers": managers,
            "observedSameObjectHpDecreaseSincePreviousSample": observed_drop if previous else None,
            "disappearedEnemyObjectsSincePreviousSample": len(set(previous) - set(next_hp)),
            "newEnemyObjectsSincePreviousSample": len(set(next_hp) - set(previous)) if previous else len(next_hp)}, next_hp


def compare(baseline_dir, replay_dir):
    report = {"baseline": str(baseline_dir.resolve()), "replay": str(replay_dir.resolve()), "success": False,
              "semanticEqual": False, "rawEqual": False, "excludedFields": EXCLUDED, "semanticRules": SEMANTIC_RULES, "samples": [],
              "scope": "Exact semantic comparison at sampled paused movie frames, not a per-frame trace or RNG equivalence proof.",
              "damageScope": "Observed positive HP drops for the same object between adjacent samples; excludes unobserved damage before destruction and does not sum clone HP into cumulative damage."}
    try:
        left_report, right_report = read_json(baseline_dir / "report.json"), read_json(replay_dir / "report.json")
        if left_report.get("success") is not True or right_report.get("success") is not True:
            raise ValueError("Both acceptance reports must have success=true")
        report["hashes"] = {}
        for field in HASH_FIELDS:
            left, right = left_report.get(field), right_report.get(field)
            if not isinstance(left, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", left) or left.lower() != str(right).lower():
                raise ValueError(f"Missing, invalid or mismatched {field}")
            report["hashes"][field] = left.lower()
        frame_sets = [{int(match.group(1)) for path in directory.glob("sample-*-world.json")
                       if (match := SAMPLE_NAME.fullmatch(path.name))} for directory in (baseline_dir, replay_dir)]
        if not frame_sets[0] or frame_sets[0] != frame_sets[1]:
            raise ValueError(f"Nonempty identical sample frame sets required: {sorted(frame_sets[0])} vs {sorted(frame_sets[1])}")
        report["sampleFrames"] = sorted(frame_sets[0])
        previous_left, previous_right = {}, {}
        for frame in report["sampleFrames"]:
            left_world, left_details = load_sample(baseline_dir, frame)
            right_world, right_details = load_sample(replay_dir, frame)
            raw_left, raw_right = project(left_world, left_details), project(right_world, right_details)
            left, right = project(left_world, left_details, True), project(right_world, right_details, True)
            left_progress, previous_left = progress(left_world, left_details, previous_left)
            right_progress, previous_right = progress(right_world, right_details, previous_right)
            report["samples"].append({"movieFrame": frame, "semanticEqual": left == right,
                "rawEqual": raw_left == raw_right,
                "baselineRawProjectionSha256": digest(raw_left), "replayRawProjectionSha256": digest(raw_right),
                "baselineProjectionSha256": digest(left), "replayProjectionSha256": digest(right),
                "staticTerrainEqual": left["staticTerrain"] == right["staticTerrain"],
                "staticTerrainObjectCount": {"baseline": len(left["staticTerrain"]), "replay": len(right["staticTerrain"])},
                "customDetailCount": {"baseline": len(left_details), "replay": len(right_details)},
                "baselineProgress": left_progress, "replayProgress": right_progress,
                "diagnostics": {"baseline": diagnostics(left_world, left_details), "replay": diagnostics(right_world, right_details)},
                "rawDifferences": first_differences(raw_left, raw_right), "differences": first_differences(left, right)})
        report["semanticEqual"] = all(sample["semanticEqual"] for sample in report["samples"])
        report["rawEqual"] = all(sample["rawEqual"] for sample in report["samples"])
        report["success"] = report["semanticEqual"]
        report["differentSampleCount"] = sum(not sample["semanticEqual"] for sample in report["samples"])
        report["rawDifferentSampleCount"] = sum(not sample["rawEqual"] for sample in report["samples"])
    except (OSError, ValueError, KeyError, TypeError) as error:
        report["error"] = f"{type(error).__name__}: {error}"
    return report


def self_test():
    import tempfile
    root = Path(__file__).resolve().parents[2]
    source = root / "artifacts/world-observation/marmu-final"
    world = read_json(source / "boss-world.json")
    details = [read_json(path) for path in sorted(source.glob("boss-custom-*.json")) if re.fullmatch(r"boss-custom-\d+\.json", path.name)]
    frame = world["movieFrame"]
    cases = []
    with tempfile.TemporaryDirectory(prefix="hktas-world-compare-") as temporary:
        left, right = Path(temporary) / "left", Path(temporary) / "right"
        left.mkdir(); right.mkdir()
        base_report = {"success": True, **{field: "a" * 64 for field in HASH_FIELDS}}
        def save(directory, data, records, result=base_report):
            (directory / "report.json").write_text(json.dumps(result), encoding="utf-8")
            (directory / f"sample-{frame}-world.json").write_text(json.dumps(data), encoding="utf-8")
            for index, detail in enumerate(records):
                (directory / f"sample-{frame}-custom-{index}.json").write_text(json.dumps(detail), encoding="utf-8")
        save(left, world, details)
        renamed = json.dumps(world).replace(world["metadata"]["sessionId"], "another-session")
        renamed_details = [json.loads(json.dumps(detail).replace(world["metadata"]["sessionId"], "another-session")) for detail in details]
        right_world = json.loads(renamed)
        def reidentify(value):
            if isinstance(value, list):
                for item in value:
                    reidentify(item)
            elif isinstance(value, dict):
                if "instanceId" in value:
                    value["instanceId"] += 200
                if "handle" in value and "isLoaded" in value:
                    value["handle"] += 10
                for item in value.values():
                    reidentify(item)
        reidentify(right_world)
        reidentify(renamed_details)
        right_world["nativeFrame"] += 100
        right_world["metadata"]["time"]["time"] += 2
        right_world["metadata"]["time"]["fixedTime"] += 2
        for detail in renamed_details:
            detail["nativeFrame"] += 100
        save(right, right_world, renamed_details)
        cases.append(("session and absolute-time differences excluded", compare(left, right)["success"]))
        mutations = [
            ("hero health", lambda data: data["hero"]["resources"].__setitem__("health", data["hero"]["resources"]["health"] - 1), "hero"),
            ("enemy position", lambda data: data["transform"]["position"].__setitem__("x", data["transform"]["position"]["x"] + 1), "enemy"),
            ("enemy HP", lambda data: data["health"][0].__setitem__("hp", data["health"][0]["hp"] - 1), "enemy"),
            ("FSM active state", lambda data: data["fsms"][0].__setitem__("activeState", "DIFFERENT"), "enemy"),
            ("FSM variable", lambda data: data["fsms"][0]["variables"][0]["raw"].__setitem__("value", 123456), "enemy")]
        for name, mutation, kind in mutations:
            modified = copy.deepcopy(right_world)
            mutation(next(obj for obj in modified["objects"] if obj["kind"] == kind))
            save(right, modified, renamed_details)
            cases.append((name + " detected", not compare(left, right)["success"]))
        cloned = copy.deepcopy(right_world)
        cloned["objects"].append(copy.deepcopy(next(obj for obj in cloned["objects"] if obj["kind"] == "enemy")))
        cloned["total"] += 1
        save(right, cloned, renamed_details)
        cases.append(("identical duplicate clone retained", not compare(left, right)["success"]))
        modified_details = copy.deepcopy(renamed_details)
        fields = next(c for c in modified_details[0]["components"] if c.get("assembly") == "EnviousMarmu")["data"]["fields"]
        next(f for f in fields if f["name"] == "_targetHp")["value"] += 1
        save(right, right_world, modified_details)
        cases.append(("custom field difference detected", not compare(left, right)["success"]))
        terrain_change = copy.deepcopy(right_world)
        collider = next(c for obj in terrain_change["objects"] for c in obj.get("colliders", []) if c.get("classification") == "terrain")
        collider["worldPaths"][0]["points"][0]["x"] += 1
        save(right, terrain_change, renamed_details)
        cases.append(("terrain geometry difference detected", not compare(left, right)["success"]))
        incomplete = copy.deepcopy(right_world)
        incomplete["nextOffset"] = 64
        save(right, incomplete, renamed_details)
        cases.append(("incomplete pagination rejected", not compare(left, right)["success"]))
        timer_left, timer_right = copy.deepcopy(world), copy.deepcopy(right_world)
        left_timer = dialogue_timer_fields(next(obj for obj in timer_left["objects"] if obj["kind"] == "hero")["hero"])[0]
        right_timer = dialogue_timer_fields(next(obj for obj in timer_right["objects"] if obj["kind"] == "hero")["hero"])[0]
        left_timer["value"], right_timer["value"] = -1.0, -20.0
        save(left, timer_left, details); save(right, timer_right, renamed_details)
        timer_result = compare(left, right)
        cases.append(("expired dialogue timers equal with raw difference retained", timer_result["success"] and not timer_result["rawEqual"]))
        left_timer["value"], right_timer["value"] = 0.2, 0.3
        save(left, timer_left, details); save(right, timer_right, renamed_details)
        cases.append(("positive dialogue timer difference detected", not compare(left, right)["success"]))
        left_timer["value"], right_timer["value"] = -1.0, 0.01
        save(left, timer_left, details); save(right, timer_right, renamed_details)
        cases.append(("expired versus positive dialogue timer detected", not compare(left, right)["success"]))
        audio_left, audio_right = copy.deepcopy(world), copy.deepcopy(right_world)
        audio_object = {"id": "synthetic-audio", "name": "Audio Player Actor 2D(Clone)", "path": "Audio Player Actor 2D(Clone)",
                        "scene": {"name": "DontDestroyOnLoad"}, "components": []}
        audio_right["objects"].append(audio_object); audio_right["total"] += 1
        for data in (audio_left, audio_right):
            fsm = next(obj for obj in data["objects"] if obj["kind"] == "enemy")["fsms"][0]
            variable = next(v for v in fsm["variables"] if v["name"] == "Voice Player")
            variable["raw"]["value"] = {"objectId": "synthetic-audio", "instanceId": 1234, "name": "Audio Player Actor 2D(Clone)",
                                           "type": "UnityEngine.GameObject", "componentType": None}
        save(left, audio_left, details); save(right, audio_right, renamed_details)
        audio_result = compare(left, right)
        cases.append(("Voice Player active-list resolution differs but semantic identity matches", audio_result["success"] and not audio_result["rawEqual"]))
        audio_object["name"] = "Other Audio Object"
        for data in (audio_left, audio_right):
            fsm = next(obj for obj in data["objects"] if obj["kind"] == "enemy")["fsms"][0]
            next(v for v in fsm["variables"] if v["name"] == "Voice Player")["raw"]["value"]["name"] = "Other Audio Object"
        save(left, audio_left, details); save(right, audio_right, renamed_details)
        cases.append(("other object reference resolution differences retained", not compare(left, right)["success"]))
        actor_collider = copy.deepcopy(right_world)
        next(obj for obj in actor_collider["objects"] if obj["kind"] == "hero")["colliders"][0]["bounds"]["size"]["x"] += 0.1
        save(left, world, details); save(right, actor_collider, renamed_details)
        cases.append(("hero collider geometry difference detected", not compare(left, right)["success"]))
        save(right, right_world, renamed_details, {**base_report, "fixtureSha256": "b" * 64})
        cases.append(("different Movie hash rejected", not compare(left, right)["success"]))
    for name, passed in cases:
        print(f"{'PASS' if passed else 'FAIL'} {name}")
    return 0 if all(passed for _, passed in cases) else 2


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path, nargs="?")
    parser.add_argument("replay", type=Path, nargs="?")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--self-test", action="store_true", help="exercise comparator against archived Marmu schema and synthetic differences")
    args = parser.parse_args()
    if args.self_test:
        return self_test()
    if not args.baseline or not args.replay or not args.output:
        parser.error("baseline, replay and --output are required")
    result = compare(args.baseline, args.replay)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"World sample comparison {'PASS' if result['success'] else 'FAIL'}: {args.output}")
    if result.get("error"):
        print(result["error"], file=sys.stderr)
    return 0 if result["success"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
