#!/usr/bin/env python3
"""Extract the slot-4 prefix or package an observed authored combat route.

The output is an UNVERIFIED current-build candidate, not a completed P3B route.
All initial-save bytes and original native input samples are preserved. No game,
compiler, live-save directory, or gameplay mutation API is used.
"""

import argparse
import base64
import copy
import hashlib
import io
import json
from pathlib import Path
import sys
import zipfile

from cryptography.hazmat.primitives import padding
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

REPO = Path(__file__).resolve().parent.parent
DEFAULT_BASE = REPO / "artifacts/scene-checkpoints/p3/canonical-v15-fresh-record-20261031-314/run-01/serial-scenes-canonical-boundary-recording.hktaspack"
DEFAULT_OUTPUT = REPO / "artifacts/scene-checkpoints/p3b/p3b-slot4-workshop-prefix-candidate-20261102.hktaspack"
P1_DEFAULT_BASE = REPO / "artifacts/scene-checkpoints/p3b/p3b-godhome-first-victory-recording-20261102-02/run-01/serial-scenes-canonical-boundary-input-route-recording-unqualified.hktaspack"
P1_DEFAULT_OUTPUT = REPO / "artifacts/scene-checkpoints/p3b/p3b-p1-complete-authoring-candidate-20261102.hktaspack"
PREFIX_FRAMES = 1480  # Stop immediately before the original inventory input.
MAX_ROUTE_FRAMES = 10000  # Existing InputRouteRecording total-end limit; never split around it.
P1_PREFIX_FRAMES = 2781
P1_PREFIX_SHA256 = "bf287d3bd1c5016c1ac27b1fd665b31abc2ec646cac5d4a9c79b1843c2492877"
P1_INITIAL_SAVES_ID = "e67b5e8abe64112c26e777c49ec341cc2c18b69d73c4b7e2243910964b313848"
# Expected scene labels from p1-authoring-plan-20261102.json. These remain an
# admission contract: every transition must be observed, never generated here.
P1_SCENES = ("GG_Boss_Door_Entrance", "GG_Vengefly", "GG_Gruz_Mother", "GG_False_Knight",
             "GG_Mega_Moss_Charger", "GG_Hornet_1", "GG_Spa", "GG_Ghost_Gorb",
             "GG_Dung_Defender", "GG_Mage_Knight", "GG_Brooding_Mawlek",
             "GG_Engine", "GG_Nailmasters", "GG_End_Sequence")
P1_COMPLETION_SCHEMA = "hktas-p1-sequence-completion-observation-v1"


def trim_movie(rows, frames):
    selected, remaining = [], frames
    for original in rows[1:]:
        if remaining == 0:
            break
        if original["repeatCount"] <= 0:
            raise ValueError("Movie repeatCount must be positive")
        run = copy.deepcopy(original)
        run["repeatCount"] = min(run["repeatCount"], remaining)
        selected.append(run)
        remaining -= run["repeatCount"]
    if remaining:
        raise ValueError("Cut frame exceeds the supplied Movie")
    return [copy.deepcopy(rows[0])] + selected


def observed_p1_route(boundaries, endpoint):
    """Validate complete observed attempts without deleting any failed prefix."""
    route = [row for row in boundaries if row["movie"] > P1_PREFIX_FRAMES]
    if not route:
        raise ValueError("P1 requires observed post-prefix scene transitions")
    prefix = [row for row in boundaries if row["movie"] <= P1_PREFIX_FRAMES]
    if not prefix or prefix[-1]["target"] != "GG_Atrium":
        raise ValueError("P1 requires the observed native prefix arrival in Atrium")
    if route[-1]["native"] > endpoint["nativeFrame"]:
        raise ValueError("P1 scene evidence extends beyond the completed endpoint")
    previous = prefix[-1]
    scene, index = "GG_Atrium", -1
    failed, completed = [], []
    for row in route:
        if row["ordinal"] != previous["ordinal"] + 1 or row["movie"] < previous["movie"] or row["native"] <= previous["native"]:
            raise ValueError("P1 boundary ordinals and executed frames must be complete and ordered")
        if row["source"] != scene:
            raise ValueError("P1 observed scene chain is discontinuous")
        target = row["target"]
        if index == -1 and not completed and (scene, target) in (
                ("GG_Atrium", "GG_Workshop"), ("GG_Workshop", "GG_Atrium")):
            pass  # Preserve normal bench/loadout preparation and retry travel.
        elif scene == "GG_Atrium" and not completed and target == P1_SCENES[0]:
            index = 0
        elif index >= 0 and index + 1 < len(P1_SCENES) and target == P1_SCENES[index + 1]:
            index += 1
            if target == "GG_End_Sequence":
                completed.append(row)
        elif index >= 0 and target == "GG_Atrium":
            if scene != "GG_End_Sequence":
                failed.append(row)
            index = -1
        else:
            raise ValueError("Unexpected or missing P1 scene transition: " + scene + " -> " + target)
        scene, previous = target, row
    if len(completed) != 1 or scene not in ("GG_End_Sequence", "GG_Atrium"):
        raise ValueError("P1 requires one complete observed sequence through Spa, Engine and the final scene")
    if endpoint["scene"] != scene or endpoint["heroResources"]["health"] <= 0:
        raise ValueError("P1 endpoint must match the observed final scene and show a living Hero")
    return completed[0], failed, route


def observed_p1_completion(path, movie_bytes, initial_saves_id, command, route, endpoint):
    """Consume an existing direct read receipt; never infer static state from scenes.

    JSON schema v1 requires: schema, observationKind (direct-static or
    canonical-carry), sequence (sequence-tier1), authoringCommand,
    authoringMovieSha256, initialSavesId, before, after. Each observation has
    movieFrame, nativeFrame, scene, wasCompleted (boolean), bossIndex (integer).
    canonical-carry receipts also require carryVersion=2.
    The producer must read BossSequenceController's original static fields, or
    CanonicalBossChallengeCarry.Data Version 2; world.json does not supply them.
    This packager validates a supplied receipt, it is not its runtime producer.
    """
    raw = path.read_bytes()
    receipt = json.loads(raw.decode("utf-8-sig"))
    if receipt.get("schema") != P1_COMPLETION_SCHEMA:
        raise ValueError("P1 completion evidence requires schema " + P1_COMPLETION_SCHEMA)
    if receipt.get("observationKind") not in ("direct-static", "canonical-carry"):
        raise ValueError("P1 completion must be read directly from static fields or canonical carry")
    if receipt["observationKind"] == "canonical-carry" and (type(receipt.get("carryVersion")) is not int or receipt["carryVersion"] != 2):
        raise ValueError("P1 canonical-carry completion evidence requires Data Version 2")
    if receipt.get("sequence") != "sequence-tier1" or receipt.get("initialSavesId") != initial_saves_id:
        raise ValueError("P1 completion receipt must identify the original Tier1 sequence and initial saves")
    if type(receipt.get("authoringCommand")) is not int or receipt["authoringCommand"] != command:
        raise ValueError("P1 completion receipt must identify this completed authoring command")
    if receipt.get("authoringMovieSha256") != hashlib.sha256(movie_bytes).hexdigest():
        raise ValueError("P1 completion receipt belongs to a different authoring Movie")
    before, after = receipt.get("before"), receipt.get("after")
    for sample, scene, completed, index in ((before, "GG_Nailmasters", False, 11),
                                             (after, "GG_End_Sequence", True, 12)):
        if not isinstance(sample, dict) or sample.get("scene") != scene or sample.get("wasCompleted") is not completed:
            raise ValueError("P1 completion requires direct false-to-true observations around the original final transition")
        if type(sample.get("bossIndex")) is not int or sample["bossIndex"] != index:
            raise ValueError("P1 completion requires original bossIndex 11 before and 12 after the final transition")
        if any(type(sample.get(key)) is not int or sample[key] < 0 for key in ("movieFrame", "nativeFrame")):
            raise ValueError("P1 completion observations require nonnegative integer Movie/native anchors")
    final = next(row for row in route if row["target"] == "GG_End_Sequence")
    entry = route[route.index(final) - 1]
    if not (entry["target"] == "GG_Nailmasters"
            and entry["movie"] <= before["movieFrame"] <= final["movie"] <= after["movieFrame"] <= endpoint["movieFrame"]
            and entry["native"] <= before["nativeFrame"] < final["native"] <= after["nativeFrame"] <= endpoint["nativeFrame"]):
        raise ValueError("P1 completion observations must bracket the successful final transition within this endpoint")
    returns = [row for row in route if row["source"] == "GG_End_Sequence"]
    if returns and (after["movieFrame"] > returns[0]["movie"] or after["nativeFrame"] >= returns[0]["native"]):
        raise ValueError("P1 completed observation must precede the observed return from End Sequence")
    return {**receipt, "source": str(path.resolve()), "sha256": hashlib.sha256(raw).hexdigest()}


def package_observed_authoring(args, parser):
    """Package existing common-input actions without promoting them to recording."""
    prefix_frames = P1_PREFIX_FRAMES if args.sequence == "sequence-tier1" else PREFIX_FRAMES
    if args.end_frame is None or args.end_frame <= prefix_frames:
        parser.error("Authoring packaging requires --end-frame after the native prefix")
    if args.end_frame > MAX_ROUTE_FRAMES:
        parser.error("Authoring total --end-frame must be <=10000 under the current InputRouteRecording contract")
    if args.sequence == "sequence-tier4" and not args.victory_commands:
        parser.error("P4 authoring packaging requires --victory-commands BEFORE AFTER")
    if args.sequence == "sequence-tier1" and args.victory_commands:
        parser.error("--victory-commands is the P4 first-boss contract; P1 uses its complete scene chain")
    if args.sequence == "sequence-tier1" and args.completion_evidence is None:
        parser.error("P1 authoring packaging requires --completion-evidence from direct static/carry observations")
    if args.sequence == "sequence-tier4" and args.completion_evidence is not None:
        parser.error("--completion-evidence is the P1 final-sequence contract")
    if args.evidence_directory is None:
        parser.error("Authoring packaging requires --evidence-directory")
    evidence = args.evidence_directory.resolve()
    base, output = args.base.resolve(), args.output.resolve()
    sidecar = output.with_suffix(".route-plan.json")
    if base == output or output.exists() or sidecar.exists():
        parser.error("Refusing to replace the base or any existing output")
    stem = f"command-{args.authoring_command:04}"
    movie_path = evidence / f"{stem}-movie.hktas"
    done = json.loads((evidence / f"{stem}-done.json").read_text(encoding="utf-8-sig"))
    if not done["success"] or done["frame"] != args.end_frame:
        raise ValueError("Cut frame must equal the successfully completed authoring command")

    def observation(command):
        path = evidence / f"command-{command:04}-world.json"
        world = json.loads(path.read_text(encoding="utf-8-sig"))
        heroes = [obj for obj in world["objects"] if obj.get("hero")]
        if len(heroes) != 1:
            raise ValueError("Expected exactly one observed Hero")
        hero = heroes[0]
        return {
            "source": str(path), "movieFrame": world["movieFrame"],
            "nativeFrame": world["metadata"]["nativeFrame"],
            "scene": world["metadata"]["activeScene"]["name"],
            "heroResources": hero["hero"]["resources"], "charms": hero["hero"]["charms"],
            "enemies": [{"name": obj["name"], "health": obj["health"]}
                        for obj in world["objects"] if obj.get("health")],
        }

    endpoint = observation(args.authoring_command)
    if endpoint["movieFrame"] != args.end_frame or endpoint["nativeFrame"] != done["nativeFrame"]:
        raise ValueError("Endpoint observation and completion receipt disagree")
    before = after = None
    if args.sequence == "sequence-tier4":
        before = observation(args.victory_commands[0])
        after = observation(args.victory_commands[1])
        if before["scene"] != "GG_Crystal_Guardian_2" or after["scene"] != before["scene"]:
            raise ValueError("Victory observations must belong to the first P4 boss")
        guardian = [obj for obj in before["enemies"] if obj["name"] == "Zombie Beam Miner Rematch"]
        if len(guardian) != 1 or guardian[0]["health"][0]["hp"] <= 0:
            raise ValueError("The before observation must show the living Guardian")
        if any(obj["name"] == "Zombie Beam Miner Rematch" for obj in after["enemies"]):
            raise ValueError("The after observation must show the Guardian removed")
        if not before["movieFrame"] < after["movieFrame"] < endpoint["movieFrame"]:
            raise ValueError("Victory observations must precede the endpoint")
        if endpoint["scene"] != "GG_Lost_Kin" or endpoint["heroResources"]["health"] <= 0:
            raise ValueError("The endpoint must show a living Hero in the next P4 boss scene")
    progress_path = evidence / "canonical-boundary-progress.jsonl"
    if progress_path.exists():
        progress = [json.loads(line) for line in progress_path.read_text(encoding="utf-8-sig").splitlines() if line]
        boundaries = [{key: row[key] for key in ("ordinal", "source", "target", "movie", "native", "phase")}
                      for row in progress if row.get("phase") == "input-route-authoring-unqualified"
                      and row["movie"] <= args.end_frame]
    else:
        journals = [p for p in evidence.glob("serial-scenes-*.jsonl") if p.stem.removeprefix("serial-scenes-").isdigit()]
        if len(journals) != 1:
            raise ValueError("Expected one resident serial journal")
        progress_path = journals[0]
        progress = [json.loads(line) for line in progress_path.read_text(encoding="utf-8-sig").splitlines() if line]
        verified = {(r["ordinal"], r["data"]["movie"], r["data"]["native"]) for r in progress
                    if r["phase"] == "resident-transit-verified"}
        boundaries = []
        for row in progress:
            if row["phase"] != "native-empty-boundary" or row["data"]["movie"] > args.end_frame:
                continue
            data = row["data"]
            if (row["ordinal"], data["movie"], data["native"]) not in verified:
                raise ValueError("Recorded boundary lacks its resident transit receipt")
            boundaries.append(dict(ordinal=row["ordinal"], phase="resident-transit-verified",
                                   **{key: data[key] for key in ("source", "target", "movie", "native")}))
    p1_route = None
    if args.sequence == "sequence-tier1":
        victory, failed_attempts, p1_route = observed_p1_route(boundaries, endpoint)
    else:
        victories = [row for row in boundaries if row["source"] == "GG_Crystal_Guardian_2"
                     and row["target"] == "GG_Lost_Kin" and row["movie"] > after["movieFrame"]]
        if len(victories) != 1:
            raise ValueError("Expected one observed first-boss victory transition")
        victory = victories[0]
        failed_attempts = [row for row in boundaries if row["source"] == "GG_Crystal_Guardian_2"
                           and row["target"] == "GG_Atrium" and row["movie"] < before["movieFrame"]]
        if not failed_attempts:
            raise ValueError("Expected the preserved first failed attempt and normal retry")
    with zipfile.ZipFile(base) as source:
        infos = source.infolist()
        entries = {entry.filename: source.read(entry) for entry in infos}
    manifest = json.loads(entries["manifest.json"])
    base_rows = [json.loads(line) for line in entries["movie.hktas"].decode("utf-8-sig").splitlines() if line]
    movie_bytes = movie_path.read_bytes()
    rows = [json.loads(line) for line in movie_bytes.decode("utf-8-sig").splitlines() if line]
    selected = trim_movie(rows, args.end_frame)
    prefix = trim_movie(rows, prefix_frames)
    if selected[0] != base_rows[0] or prefix != trim_movie(base_rows, prefix_frames):
        raise ValueError("The original environment header and native menu prefix must be preserved")
    if args.sequence == "sequence-tier1":
        prefix_bytes = ("\n".join(json.dumps(row, ensure_ascii=False, separators=(",", ":"))
                                  for row in prefix) + "\n").encode("utf-8")
        if hashlib.sha256(prefix_bytes).hexdigest() != P1_PREFIX_SHA256 or any(run.get("authored") for run in prefix[1:]):
            raise ValueError("P1 must preserve the original recording02 native upper-platform prefix")
        if manifest["InitialSavesId"] != P1_INITIAL_SAVES_ID or sum(name.startswith("initial-saves/") for name in entries) != 20:
            raise ValueError("P1 requires all 20 original initial-save entries and their original identity")
        completion = observed_p1_completion(args.completion_evidence, movie_bytes, manifest["InitialSavesId"],
                                            args.authoring_command, p1_route, endpoint)
    authored_frames = sum(run["repeatCount"] for run in selected[1:] if run.get("authored") is True)
    if authored_frames != args.end_frame - prefix_frames:
        raise ValueError("All post-prefix frames must retain their authored marker")
    if any(run.get("fps") != 50 or len(run["samples"]) != 1
           for run in selected[1:] if run.get("authored") is True):
        raise ValueError("Expected the observed 50 FPS single-sample authored input format")
    compact = lambda value: json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    movie = ("\n".join(compact(row) for row in selected) + "\n").encode("utf-8")
    candidate_manifest = copy.deepcopy(manifest)
    candidate_manifest["MovieSha256"] = hashlib.sha256(movie).hexdigest()
    candidate_entries = dict(entries, **{"movie.hktas": movie, "manifest.json": compact(candidate_manifest).encode("utf-8")})
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as candidate:
        # Preserve base entry metadata so identical inputs produce identical bytes.
        for entry in infos:
            candidate.writestr(entry, candidate_entries[entry.filename])
    with zipfile.ZipFile(io.BytesIO(buffer.getvalue())) as candidate:
        assert all(candidate.read(name) == value for name, value in entries.items() if name.startswith("initial-saves/"))
        assert json.loads(candidate.read("manifest.json"))["Files"] == manifest["Files"]
        assert candidate.read("movie.hktas") == movie
    plan = {
        "status": "AUTHORED_CANDIDATE_NATIVE_RECORDING_REQUIRED", "candidateKind": "observed-normal-input-first-boss-victory",
        "restoreQualified": False, "completeStateCoverage": False, "completeP3BRoute": False,
        "nativeRecordingVerified": False, "coldReplayVerified": False,
        "basePackage": str(base), "sourceAuthoringMovie": str(movie_path), "candidatePackage": str(output),
        "frames": args.end_frame, "nativePrefixFrames": prefix_frames, "authoredFrames": authored_frames,
        "initialSavesId": manifest["InitialSavesId"], "initialSaveBytesPreserved": True,
        "initialSaveEntryCount": sum(name.startswith("initial-saves/") for name in entries),
        "environmentHeaderPreserved": True, "authoredMarkersPreserved": True,
        "nativeInputCallbackCountsKnownForAuthoredFrames": False,
        "observationMode": "protected interactive authoring; common Canon/preservation only; no full D/K3/target restoration",
        "boundaryEvidence": str(progress_path), "boundaries": boundaries,
        "failedAttemptReturns": failed_attempts, "victoryBoundary": victory,
        "victoryBefore": before, "victoryAfter": after, "endpoint": endpoint,
        "observedMovieFramesAfterVictoryBoundary": args.end_frame - victory["movie"],
        "evidenceScope": ["slot 4 original Workshop save", "normal charm menu removed overcharm",
                          "normal P4 entry without bindings", "first attempt death and normal retry retained",
                          "second attempt defeated Enraged Guardian through normal nail/spell inputs",
                          "ordinal 8 normal Boss-to-Boss transition to Lost Kin"],
        "notCovered": ["fresh native RecordInputs replay", "complete source D capture", "target restore",
                       "K3 comparison", "120-frame next-fight suffix", "rest room", "last boss completion",
                       "final cold-replay and restore matrix", "production Studio checkpoint integration"],
        "nextSteps": ["Run the candidate with RecordInputs using all original initial saves",
                      "Use the newly recorded native route for source/target qualification at the verified victory boundary",
                      "Complete the remaining P3B matrix before claiming qualification"],
        "reproductionArgv": [sys.executable, str(Path(__file__).resolve())] + sys.argv[1:],
    }
    if args.sequence == "sequence-tier1":
        plan.update(sequenceTier=1, candidateKind="observed-normal-input-p1-scene-route",
            nativePrefixSha256=P1_PREFIX_SHA256, orderedP1SceneContract=list(P1_SCENES),
            p1Boundaries=p1_route, finalSceneTransitionObserved=True,
            completionEvidence=completion, staticSequenceCompletionObserved=True, wasCompleted=True, bossIndex=12,
            evidenceScope=["original recording02 native upper-platform prefix",
                "normal-input P1 scene chain including Spa and Engine",
                "all executed attempts and their normal returns retained",
                "observed Nailmasters to End Sequence transition and living endpoint",
                "direct static/carry WasCompleted false-to-true and bossIndex 11-to-12 observations"],
            notCovered=["fresh native RecordInputs replay", "complete source D capture", "target restore",
                "K3 comparison",
                "final cold-replay and restore matrix", "production Studio checkpoint integration"],
            nextSteps=["Record this complete candidate natively with all original initial saves",
                "Select actual new recording ordinals for boss, rest-room and final-boundary acceptance",
                "Complete the required cold-replay and restore matrix before claiming qualification"])
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(buffer.getvalue())
    with sidecar.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(plan, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(compact({"package": str(output), "plan": str(sidecar), "frames": args.end_frame,
                   "authoredFrames": authored_frames, "victoryBoundary": victory,
                   "status": plan["status"], "restoreQualified": False}))


def decode_save(raw):
    # Same bounded NRBF string record and AES settings as SaveJsonDocument.cs
    # and scratch/decrypt_saves.py. Never deserialize arbitrary NRBF objects.
    header = bytes((0, 1, 0, 0, 0, 255, 255, 255, 255, 1, 0, 0, 0, 0, 0, 0, 0, 6, 1, 0, 0, 0))
    if not raw.startswith(header):
        raise ValueError("Expected the original encrypted HK save string record")
    cursor, length, shift = len(header), 0, 0
    while True:
        value = raw[cursor]
        cursor += 1
        length |= (value & 127) << shift
        if value < 128:
            break
        shift += 7
        if shift >= 35:
            raise ValueError("Invalid save string length")
    if cursor + length + 1 != len(raw) or raw[-1] != 11:
        raise ValueError("Invalid save string boundary")
    decryptor = Cipher(algorithms.AES(b"UKu52ePUBwetZ9wNX88o54dnfKRu0T1l"), modes.ECB()).decryptor()
    plain = decryptor.update(base64.b64decode(raw[cursor:cursor + length], validate=True)) + decryptor.finalize()
    unpadder = padding.PKCS7(128).unpadder()
    return json.loads(unpadder.update(plain) + unpadder.finalize())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, help="Defaults to the original P4 recording, or recording02 for P1")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--sequence", choices=("sequence-tier1", "sequence-tier4"), default="sequence-tier4",
                        help="Observed route contract: P4 first victory or complete P1 scene route")
    parser.add_argument("--authoring-command", type=int, help="Package this completed interactive command instead of extracting the prefix")
    parser.add_argument("--evidence-directory", type=Path)
    parser.add_argument("--end-frame", type=int)
    parser.add_argument("--victory-commands", type=int, nargs=2, metavar=("BEFORE", "AFTER"))
    parser.add_argument("--completion-evidence", type=Path,
                        help="P1 direct static/carry JSON receipt with schema " + P1_COMPLETION_SCHEMA)
    args = parser.parse_args()
    args.base = args.base or (P1_DEFAULT_BASE if args.sequence == "sequence-tier1" else DEFAULT_BASE)
    args.output = args.output or (P1_DEFAULT_OUTPUT if args.sequence == "sequence-tier1" else DEFAULT_OUTPUT)
    if args.sequence == "sequence-tier1" and args.authoring_command is None:
        parser.error("P1 packaging requires an observed --authoring-command; use the existing native P1 prefix for authoring")
    if args.authoring_command is not None:
        package_observed_authoring(args, parser)
        return
    base, output = args.base.resolve(), args.output.resolve()
    sidecar = output.with_suffix(".route-plan.json")
    if base == output or output.exists() or sidecar.exists():
        parser.error("Refusing to replace the base or any existing output")
    with zipfile.ZipFile(base) as source:
        entries = {entry.filename: source.read(entry) for entry in source.infolist()}
    manifest = json.loads(entries["manifest.json"])
    rows = [json.loads(line) for line in entries["movie.hktas"].decode("utf-8-sig").splitlines() if line.strip()]
    header, runs = rows[0], rows[1:]
    assert header["nativeProfileId"] == "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v15-experimental"
    selected, cursor, nonzero = [], 0, []
    for original in runs:
        if cursor == PREFIX_FRAMES:
            break
        run = copy.deepcopy(original)
        assert run["repeatCount"] > 0 and not run.get("authored", False)
        run["repeatCount"] = min(run["repeatCount"], PREFIX_FRAMES - cursor)
        active = sorted({i for sample in run["samples"] for i, value in enumerate(sample["values"]) if value})
        if active:
            nonzero.append({"startFrame": cursor, "frames": run["repeatCount"], "actionIndexes": active})
        selected.append(run)
        cursor += run["repeatCount"]
    assert cursor == PREFIX_FRAMES
    assert [(x["startFrame"], x["frames"], x["actionIndexes"]) for x in nonzero] == [
        (400, 1, [8, 20, 21]), (1102, 1, [3]), (1144, 1, [3]), (1186, 1, [3]), (1288, 1, [8, 20, 21])]
    compact = lambda value: json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    movie = ("\n".join(compact(row) for row in [header] + selected) + "\n").encode("utf-8")
    candidate_manifest = copy.deepcopy(manifest)
    candidate_manifest["MovieSha256"] = hashlib.sha256(movie).hexdigest()
    candidate_entries = dict(entries, **{"movie.hktas": movie, "manifest.json": compact(candidate_manifest).encode("utf-8")})
    fields = ("profileID", "respawnScene", "respawnMarkerName", "health", "maxHealth", "maxHealthBase", "MPCharge", "MPReserveMax",
              "nailDamage", "nailSmithUpgrades", "fireballLevel", "quakeLevel", "screamLevel", "hasDash", "hasShadowDash", "hasDoubleJump",
              "hasSuperDash", "hasWalljump", "hasCyclone", "hasUpwardSlash", "hasDashSlash", "equippedCharms", "charmSlots", "charmSlotsFilled",
              "overcharmed", "bossRushMode", "hasGodfinder", "godseekerUnlocked", "dreamGateScene", "currentBossSequence")
    slots = []
    for slot in range(1, 5):
        dat = decode_save(entries[f"initial-saves/user{slot}.dat"])["playerData"]
        exported = json.loads(entries[f"initial-saves/user{slot}.json"])["playerData"]
        slots.append({"slotFile": slot, "source": f"{base}!initial-saves/user{slot}.dat", "actualDat": {key: dat.get(key) for key in fields},
                      "jsonConflicts": {key: {"dat": dat.get(key), "json": exported.get(key)} for key in fields if dat.get(key) != exported.get(key)},
                      "pantheonUnlocked": {str(tier): dat[f"bossDoorStateTier{tier}"]["unlocked"] for tier in range(1, 6)}})
    assert slots[3]["actualDat"]["respawnScene"] == "GG_Workshop" and slots[3]["actualDat"]["nailDamage"] == 21
    assert all(slots[3]["actualDat"][key] == 2 for key in ("fireballLevel", "quakeLevel", "screamLevel"))
    plan = {
        "status": "UNVERIFIED", "restoreQualified": False, "completeP3BRoute": False, "combatInputsGenerated": False,
        "candidateKind": "historical-native-prefix-only", "currentBuildReplayVerified": False,
        "basePackage": str(base), "candidatePackage": str(output), "slotFile": 4, "frames": PREFIX_FRAMES,
        "initialSavesId": manifest["InitialSavesId"], "initialSaveBytesPreserved": True, "nativeSampleValuesAndOrderPreserved": True,
        "menuPrefix": nonzero, "prefixMeaning": "Submit; Down three separate times; Submit loads slot file 4; stop before Inventory at Movie 1480",
        "historicalTransitionEvidence": str(base.parent / "serial-scenes-32632.jsonl.transitions.jsonl"),
        "historicalTransition": {"ordinal": 1, "source": "Menu_Title", "target": "GG_Workshop", "entryGate": "", "engineFrame": 1451},
        "slotAudit": slots,
        "slot4Cautions": ["user4.json is stale: actual user4.dat starts in GG_Workshop, not Town",
                          "user4.dat internally stores profileID 2; select file slot 4 normally and observe runtime saveSlot; never rewrite it",
                          "Saved health is 7/9; equipment is overcharmed at 12/11. Use original bench/heal/charm UI inputs and verify resulting state",
                          "A stale currentBossSequence Tier 5 record exists in the save; do not assume it is a live sequence or clear it by debug writes"],
        "staticRoute": [
            {"scene": "GG_Workshop", "object": "RestBench (1)", "position": [11.180, 35.660], "source": "scene-objects.tsv:569700", "runtimePositionVerified": False},
            {"scene": "GG_Workshop", "object": "left1", "position": [2.0, 9.0], "target": "GG_Atrium", "entryGate": "Door_Workshop",
             "source": "installed level470: GameObject 778, TransitionPoint 13701; scene-objects.tsv:569428", "inputTiming": None},
            {"scene": "GG_Atrium", "object": "Door_Workshop", "position": [111.330, 13.760], "source": "scene-objects.tsv:534743"},
            {"scene": "GG_Atrium", "object": "GG_Challenge_Door (1)", "position": [98.270, 36.830], "playerData": "bossDoorStateTier1",
             "source": "installed level424: GameObject 1204, BossSequenceDoor 5681; scene-objects.tsv:535488", "status": "PREFERRED_IF_TIER1_ASSET_CONTRACT_ADMITTED"},
            {"scene": "GG_Atrium", "object": "GG_Challenge_Door (4)", "position": [148.450, 36.830], "playerData": "bossDoorStateTier4",
             "source": "installed level424: GameObject 1206, BossSequenceDoor 5680; scene-objects.tsv:535490", "status": "ALTERNATIVE_WITH_EXISTING_TIER4_CONTRACT"}],
        "guardianFacts": {"scene": "GG_Crystal_Guardian_2", "object": "Battle Scene/Zombie Beam Miner Rematch", "fsm": "Beam Miner",
                          "assetHp": 650, "assetHpScale": [0, 1250, 1250], "actualHpAndLevelNeedTelemetry": True,
                          "assetPosition": [36.429, 12.570], "firstAttack": "Aim 2 -> Beam Antic 2 (0.6s) -> Beam 2 (0.5s)",
                          "aimSamplesHeroPositionOnce": True, "laterChoices": "random ATTACK/JUMP/ROAR; jump destination random",
                          "source": "installed level429 HealthManager 1701; fsm-export/Gods_Glory/GG_Crystal_Guardian_2.unity/Zombie_Beam_Miner_Rematch__Beam_Miner__fsm_1644.md"},
        "telemetryRequired": ["First controllable Hero x/y/velocity/grounded/facing/health/actual capability fields after loading slot 4",
                              "Bench charm UI focus/selected ID and actual equipped/overcharmed values before and after normal menu changes",
                              "Workshop left1 traversal and Atrium platform/door approach Movie anchors, without assumed travel durations",
                              "Exact selected sequence, bindings, bossIndex and first Boss scene after normal challenge-menu confirmation",
                              "Each fight frame: Hero and boss position/velocity, Hero input/control/damage state, boss HP/invincible/Beam Miner state and aim/jump variables",
                              "Actual boss death, OnBossSceneComplete, next non-rest Boss scene, then 120 executed Movie frames"],
        "nextSteps": ["Use this original prefix and readonly telemetry; replace only future Movie inputs through the common normal input path",
                      "At the original bench, choose an observed legal non-overcharmed loadout; do not change initial-save bytes",
                      "Reach Atrium through left1 and select a real Pantheon; first-victory attack timing remains unset until observed",
                      "Record the completed native input route, then qualify actual source/target restoration and final repeat matrix"],
        "notCovered": ["first boss victory", "Boss-to-Boss transition", "120-frame next-fight suffix", "rest room", "last boss completion", "cold-replay/restore matrix"]}
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as candidate:
        for name, data in candidate_entries.items():
            candidate.writestr(name, data)
    with zipfile.ZipFile(io.BytesIO(buffer.getvalue())) as candidate:
        assert all(candidate.read(name) == value for name, value in entries.items() if name.startswith("initial-saves/"))
        assert json.loads(candidate.read("manifest.json"))["Files"] == manifest["Files"]
        parsed = [json.loads(line) for line in candidate.read("movie.hktas").decode().splitlines()]
        assert parsed == [header] + selected and sum(row["repeatCount"] for row in parsed[1:]) == PREFIX_FRAMES
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(buffer.getvalue())
    with sidecar.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(plan, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(compact({"package": str(output), "plan": str(sidecar), "frames": PREFIX_FRAMES, "status": "UNVERIFIED", "combatInputsGenerated": False}))


if __name__ == "__main__":
    main()
