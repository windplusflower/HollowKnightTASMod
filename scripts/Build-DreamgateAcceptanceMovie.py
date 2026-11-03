#!/usr/bin/env python3
"""Build an UNVERIFIED authored Dreamgate input candidate, never a native recording.

Only this package's movie checksum is calculated. Initial save entries, their
manifest records and the movie header are copied from the selected base package.
Existing outputs are never replaced. Run the result through -RecordInputs before
using the resulting native recording for cold replay or snapshot acceptance.
"""

import argparse
import copy
import hashlib
import io
import json
from pathlib import Path
import zipfile


REPO = Path(__file__).resolve().parent.parent
DEFAULT_BASE = REPO / "artifacts/scene-checkpoints/p3b/p3b-godhome-sequence.hktaspack"
DEFAULT_OUTPUT = REPO / "artifacts/scene-checkpoints/p3c/p3c-dreamgate-native-route-bench-v2-candidate-20261102.hktaspack"
ACTIONS = (
    "left", "right", "up", "down",
    "rightStickLeft", "rightStickRight", "rightStickUp", "rightStickDown",
    "menuSubmit", "menuCancel", "jump", "evade", "dash", "superDash",
    "dreamNail", "attack", "cast", "focus", "quickMap", "quickCast",
    "textSpeedup", "skipCutscene", "openInventory", "paneRight", "paneLeft", "pause",
)
SUBMIT = ("menuSubmit", "textSpeedup", "skipCutscene")
# Native behavior: Down + Dream Nail SETS; Up + Dream Nail WARPS.
# All intervals are authored candidates; scene arrival and safe footing still
# require observation. Existing slot 3 provides Town -> Abyss_12 Dreamgate.
BLOCKS = (
    (400, (), "title wait"),
    (1, SUBMIT, "open save selection"),
    (700, (), "save selection wait"),
    (1, ("down",), "select slot 2"),
    (39, (), "release menu direction"),
    (1, ("down",), "select slot 3"),
    (39, (), "release menu direction"),
    (1, SUBMIT, "load original slot 3"),
    (450, (), "Town arrival wait"),
    # Bench Control.Resting listens for Jump/Attack/directions, not menuCancel.
    # Separate getting up from walking: short direction input can be consumed
    # entirely by the Get Off animation before Hero regains normal control.
    (1, ("jump",), "normal jump input gets up from the bench"),
    (99, (), "finish bench get-up or ordinary jump and recover control"),
    (30, ("right",), "walk clear of the bench interaction trigger"),
    (20, (), "settle before cross-scene warp; footing still requires observation"),
    (200, ("up", "dreamNail"), "warp Town to existing Abyss_12 gate"),
    (300, (), "finish Dream Return and resource debit"),
    (10, ("right",), "move gate location slightly"),
    (40, (), "settle before setting gate"),
    (150, ("down", "dreamNail"), "set moved gate in Abyss_12"),
    (100, (), "finish gate setting"),
    (10, ("left",), "move away from gate"),
    (40, (), "settle before same-scene warp"),
    (200, ("up", "dreamNail"), "first same-scene warp"),
    (300, (), "finish first same-scene return"),
    (10, ("right",), "move away again"),
    (40, (), "settle before reuse"),
    (200, ("up", "dreamNail"), "reuse gate after earlier restore boundary"),
    (818, (), "suffix to 4200"),
)


def compact(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def make_runs():
    previous = set()
    runs, plan = [], []
    frame = 0
    for count, actions, purpose in BLOCKS:
        active = set(actions)
        values = [32767 if action in active else 0 for action in ACTIONS]
        pressed = sum(1 << i for i, action in enumerate(ACTIONS) if action in active - previous)
        released = sum(1 << i for i, action in enumerate(ACTIONS) if action in previous - active)

        def append(repeat, down, up):
            # Authored input provides values per channel, not fabricated native
            # callback counts. The recording journal captures actual samples.
            runs.append({"repeatCount": repeat, "fps": 50, "authored": True,
                         "samples": [{"channel": "hero", "values": list(values),
                                      "pressedMask": down, "releasedMask": up, "mouse": None}]})

        append(1, pressed, released)
        if count > 1:
            append(count - 1, 0, 0)
        plan.append({"startFrame": frame, "frames": count, "inputs": list(actions), "purpose": purpose})
        frame += count
        previous = active
    assert frame == 4200
    return runs, plan


def validate_movie(movie, base_header):
    lines = movie.decode("utf-8-sig").splitlines()
    assert json.loads(lines[0]) == base_header
    runs = [json.loads(line) for line in lines[1:]]
    assert sum(run["repeatCount"] for run in runs) == 4200
    previous = 0
    for run in runs:
        assert run["authored"] is True and run["fps"] == 50 and "rngSeed" not in run
        assert run["repeatCount"] > 0 and len(run["samples"]) == 1
        sample = run["samples"][0]
        assert sample["channel"] == "hero" and sample["mouse"] is None
        assert len(sample["values"]) == len(ACTIONS)
        assert all(value in (0, 32767) for value in sample["values"])
        active = sum(1 << i for i, value in enumerate(sample["values"]) if value)
        assert sample["pressedMask"] == active & ~previous
        assert sample["releasedMask"] == previous & ~active
        if run["repeatCount"] > 1:
            assert sample["pressedMask"] == sample["releasedMask"] == 0
        previous = active
    return len(runs)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base", type=Path, default=DEFAULT_BASE)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    base, output = args.base.resolve(), args.output.resolve()
    sidecar = output.with_suffix(".route-plan.json")
    if output == base or output.exists() or sidecar.exists():
        parser.error("Refusing to replace the base, an existing package, or an existing route plan.")

    with zipfile.ZipFile(base) as package:
        manifest = json.loads(package.read("manifest.json"))
        header_line = package.read("movie.hktas").decode("utf-8-sig").splitlines()[0]
        header = json.loads(header_line)
        assert header["format"] == "hktas" and header["version"] == 2
        assert header["actionSchemaId"] == "hktas-full-run-actions-v2"
        assert header["mouseEnabled"] is False
        assert header["nativeProfileId"] == "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v15-experimental"
        saves = {"initial-saves/" + entry["Name"]: package.read("initial-saves/" + entry["Name"])
                 for entry in manifest["Files"]}
        player = json.loads(saves["initial-saves/user3.json"])["playerData"]
        assert player["respawnScene"] == "Town" and player["hasDreamGate"] and player["hasDreamNail"]
        assert player["dreamGateScene"] == "Abyss_12" and player["dreamOrbs"] >= 3

    runs, blocks = make_runs()
    movie = (header_line + "\n" + "\n".join(compact(run) for run in runs) + "\n").encode("utf-8")
    run_count = validate_movie(movie, header)
    candidate_manifest = copy.deepcopy(manifest)
    candidate_manifest["MovieSha256"] = hashlib.sha256(movie).hexdigest()
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as package:
        for name, content in saves.items():
            package.writestr(name, content)
        package.writestr("movie.hktas", movie)
        package.writestr("manifest.json", compact(candidate_manifest))
    # Encode/read validation checks the candidate only; no unrelated hashing.
    with zipfile.ZipFile(io.BytesIO(buffer.getvalue())) as package:
        assert package.read("movie.hktas") == movie
        assert all(package.read(name) == content for name, content in saves.items())
        checked_manifest = json.loads(package.read("manifest.json"))
        assert checked_manifest["Files"] == manifest["Files"]
        assert checked_manifest["InitialSavesId"] == manifest["InitialSavesId"]
        assert checked_manifest["Slots"] == manifest["Slots"]

    plan = {"status": "UNVERIFIED", "nativeRecording": False, "authoredInputCandidate": True,
            "candidateRevision": "bench-v2", "completeP3CRoute": False,
            "restoreQualified": False, "basePackage": str(base), "candidatePackage": str(output),
            "frames": 4200, "fps": 50, "slot": 3, "initialSavesId": manifest["InitialSavesId"],
            "initialSaveBytesPreserved": True, "headerPreserved": True,
            "expectedTransitions": [
                {"source": "Menu_Title", "target": "Town", "entryGate": "", "status": "UNVERIFIED"},
                {"source": "Town", "target": "Abyss_12", "entryGate": "dreamGate", "status": "UNVERIFIED"},
                {"source": "Abyss_12", "target": "Abyss_12", "entryGate": "dreamGate", "status": "UNVERIFIED"},
                {"source": "Abyss_12", "target": "Abyss_12", "entryGate": "dreamGate", "status": "UNVERIFIED"}],
            "blocks": blocks,
            "coverageIntent": {
                "crossSceneWarps": ["Town -> Abyss_12 using the original saved gate"],
                "sameSceneWarps": ["Abyss_12 -> Abyss_12 after moving the gate",
                                   "Abyss_12 -> Abyss_12 reusing the moved gate"],
                "expectedEssenceCost": 3,
                "observedDreamgateTransitions": 0},
            "notCovered": [
                "No live slot-3 bench exit, footing, gate setting or Dreamgate transition has been observed.",
                "Only one cross-scene direction is planned; the relocated gate is reused within Abyss_12, not reached from another scene.",
                "The original disallowed-condition behavior is not exercised by this input candidate.",
                "No native recording, cold replay, source D, target restore, K3 or three-cold/three-restore matrix has passed.",
                "Restoration ordinals and a later gate use after restoration must be selected from an actual transition trace."],
            "validation": {"encodedRuns": run_count, "authoredSamplesPerFrame": 1,
                           "expandedFrames": 4200, "transitionMasksChecked": True},
            "notes": ["Record through Invoke-LoadingProbe.ps1 -RecordInputs before cold replay or ActualD acceptance.",
                      "Do not use -CanonicalD0Route for recording: its recording branch requires the old 3150-frame route.",
                      "Bench exit uses normal jump, a recovery wait, then separate walking; menuCancel is not a bench get-up input.",
                      "Menu slot selection, safe footing, gate movement, resource debit and all transitions require live observation.",
                      "Expected transitions are intentions, not captured evidence; select restoration ordinals from the resulting trace."]}
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("xb") as stream:
        stream.write(buffer.getvalue())
    with sidecar.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(compact(plan) + "\n")
    print(compact({"package": str(output), "plan": str(sidecar), "frames": 4200,
                   "runs": run_count, "status": "UNVERIFIED", "authored": True,
                   "initialSaveBytesEqual": True}))


if __name__ == "__main__":
    main()
