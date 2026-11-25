#!/usr/bin/env python3
"""Generate a deterministic Movie v2 differential fuzzing .hktaspack package.

Preserves the base package's Movie v2 header, optional prefix frames, and all
initial-saves/* entries (plus InitialSavesId) without touching user save files.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import io
import json
import os
import random
from pathlib import Path
import sys
import zipfile
from typing import Any

HERO_ACTIONS: tuple[str, ...] = (
    "left",
    "right",
    "up",
    "down",
    "rightStickLeft",
    "rightStickRight",
    "rightStickUp",
    "rightStickDown",
    "menuSubmit",
    "menuCancel",
    "jump",
    "evade",
    "dash",
    "superDash",
    "dreamNail",
    "attack",
    "cast",
    "focus",
    "quickMap",
    "quickCast",
    "textSpeedup",
    "skipCutscene",
    "openInventory",
    "paneRight",
    "paneLeft",
    "pause",
)
ACTION_INDEX: dict[str, int] = {name: idx for idx, name in enumerate(HERO_ACTIONS)}
ALL_PROFILES: tuple[str, ...] = ("explore", "combat", "chaotic")
CHAOTIC_POOL: tuple[str, ...] = (
    "left",
    "right",
    "up",
    "down",
    "jump",
    "evade",
    "dash",
    "superDash",
    "dreamNail",
    "attack",
    "cast",
    "focus",
    "quickCast",
)


def compact_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def trim_prefix_runs(runs: list[dict[str, Any]], prefix_frames: int) -> tuple[list[dict[str, Any]], int]:
    """Return the first `prefix_frames` runs and the final active hero bitmask."""
    if prefix_frames < 0:
        raise ValueError(f"prefix-frames must be non-negative: {prefix_frames}")
    if prefix_frames == 0:
        return [], 0

    selected: list[dict[str, Any]] = []
    remaining = prefix_frames
    for original in runs:
        if remaining == 0:
            break
        repeat = int(original["repeatCount"])
        if repeat <= 0:
            raise ValueError("Movie v2 run repeatCount must be positive")
        run = copy.deepcopy(original)
        run["repeatCount"] = min(repeat, remaining)
        selected.append(run)
        remaining -= run["repeatCount"]

    if remaining > 0:
        total_available = sum(int(r["repeatCount"]) for r in runs)
        raise ValueError(
            f"Requested --prefix-frames={prefix_frames} exceeds base movie length ({total_available} frames)."
        )

    last_mask = 0
    last_samples = selected[-1].get("samples") or []
    for sample in last_samples:
        if sample.get("channel") == "hero":
            values = sample.get("values") or []
            last_mask = sum(1 << idx for idx, val in enumerate(values) if val)
    return selected, last_mask


def actions_to_mask(actions: set[str]) -> int:
    mask = 0
    for name in actions:
        mask |= 1 << ACTION_INDEX[name]
    return mask


def generate_profile_blocks(profile: str, fuzz_frames: int, rng: random.Random) -> list[tuple[int, set[str]]]:
    """Generate deterministic (duration, action_set) blocks summing to `fuzz_frames`."""
    blocks: list[tuple[int, set[str]]] = []
    remaining = fuzz_frames

    while remaining > 0:
        actions: set[str] = set()
        if profile == "explore":
            r_dir = rng.random()
            if r_dir < 0.42:
                actions.add("right")
                duration = rng.randint(14, 90)
            elif r_dir < 0.84:
                actions.add("left")
                duration = rng.randint(14, 90)
            elif r_dir < 0.92:
                actions.add(rng.choice(("up", "down")))
                duration = rng.randint(6, 25)
            else:
                duration = rng.randint(4, 18)

            if actions & {"left", "right"}:
                if rng.random() < 0.45:
                    actions.add("jump")
                if rng.random() < 0.25:
                    actions.add("dash")
                elif rng.random() < 0.08:
                    actions.add("superDash")
                if rng.random() < 0.15:
                    actions.add("attack")
                if rng.random() < 0.08:
                    actions.add(rng.choice(("cast", "quickCast")))

        elif profile == "combat":
            r_dir = rng.random()
            if r_dir < 0.36:
                actions.add("left")
            elif r_dir < 0.72:
                actions.add("right")
            elif r_dir < 0.87:
                actions.add(rng.choice(("up", "down")))

            if rng.random() < 0.58:
                actions.add("attack")
            if rng.random() < 0.42:
                actions.add("jump")
            if rng.random() < 0.26:
                actions.add("dash")
            if rng.random() < 0.22:
                actions.add(rng.choice(("cast", "quickCast")))
            if rng.random() < 0.10 and not (actions & {"attack", "dash", "cast", "quickCast"}):
                actions.add("focus")
                duration = rng.randint(16, 48)
            else:
                if rng.random() < 0.05:
                    actions.add("dreamNail")
                duration = rng.randint(2, 22)

        elif profile == "chaotic":
            duration = rng.randint(1, 12)
            if rng.random() >= 0.25:
                k = rng.randint(1, 4)
                picked = rng.sample(CHAOTIC_POOL, k=k)
                actions.update(picked)
                # Avoid simultaneous opposite directions cancelling each other out.
                if "left" in actions and "right" in actions:
                    actions.remove(rng.choice(("left", "right")))
                if "up" in actions and "down" in actions:
                    actions.remove(rng.choice(("up", "down")))
        else:
            raise ValueError(f"Unknown fuzz profile: {profile}")

        count = min(duration, remaining)
        if blocks and blocks[-1][1] == actions:
            prev_count, prev_actions = blocks[-1]
            blocks[-1] = (prev_count + count, prev_actions)
        else:
            blocks.append((count, actions))
        remaining -= count

    return blocks


def encode_fuzz_runs(blocks: list[tuple[int, set[str]]], initial_active_mask: int) -> list[dict[str, Any]]:
    """Encode (duration, action_set) blocks into valid canonical-style Movie v2 runs."""
    runs: list[dict[str, Any]] = []
    previous_mask = initial_active_mask

    for count, active in blocks:
        if count <= 0:
            continue
        active_mask = actions_to_mask(active)
        values = [32767 if action in active else 0 for action in HERO_ACTIONS]
        pressed_mask = active_mask & ~previous_mask
        released_mask = previous_mask & ~active_mask

        def make_run(repeat: int, pressed: int, released: int) -> dict[str, Any]:
            return {
                "repeatCount": repeat,
                "fps": 50,
                "authored": True,
                "samples": [
                    {
                        "channel": "hero",
                        "values": list(values),
                        "pressedMask": pressed,
                        "releasedMask": released,
                        "mouse": None,
                    }
                ],
            }

        if pressed_mask != 0 or released_mask != 0:
            runs.append(make_run(1, pressed_mask, released_mask))
            if count > 1:
                runs.append(make_run(count - 1, 0, 0))
        else:
            if (
                runs
                and runs[-1]["samples"][0]["pressedMask"] == 0
                and runs[-1]["samples"][0]["releasedMask"] == 0
                and runs[-1]["samples"][0]["values"] == values
            ):
                runs[-1]["repeatCount"] += count
            else:
                runs.append(make_run(count, 0, 0))

        previous_mask = active_mask

    return runs


def build_fuzz_package(
    *,
    base_package: Path,
    out_package: Path,
    seed: int = 42,
    prefix_frames: int = 0,
    fuzz_frames: int = 1800,
    profile: str = "explore",
) -> dict[str, Any]:
    base_resolved = base_package.resolve()
    out_resolved = out_package.resolve()
    if base_resolved == out_resolved:
        raise ValueError("Refusing to overwrite --base-package in place; choose a different --out path.")
    if not base_resolved.is_file():
        raise FileNotFoundError(f"Base package not found: {base_resolved}")
    if prefix_frames < 0:
        raise ValueError(f"--prefix-frames must be >= 0 (got {prefix_frames})")
    if fuzz_frames < 0:
        raise ValueError(f"--fuzz-frames must be >= 0 (got {fuzz_frames})")
    if prefix_frames + fuzz_frames <= 0:
        raise ValueError("Total frames (--prefix-frames + --fuzz-frames) must be positive.")
    if profile not in ALL_PROFILES:
        raise ValueError(f"Unsupported profile '{profile}'. Expected one of {ALL_PROFILES}.")

    with zipfile.ZipFile(base_resolved, "r") as src_zip:
        manifest = json.loads(src_zip.read("manifest.json").decode("utf-8-sig"))
        if manifest.get("Format") != "HK-TAS-Sequence" or manifest.get("Slots") != [1, 2, 3, 4]:
            raise ValueError("Base package manifest.json is not a valid HK-TAS-Sequence v1 archive.")
        save_entries: dict[str, bytes] = {}
        for item in manifest.get("Files") or []:
            rel = "initial-saves/" + item["Name"]
            save_entries[rel] = src_zip.read(rel)

        movie_lines = [
            line
            for line in src_zip.read("movie.hktas").decode("utf-8-sig").splitlines()
            if line.strip()
        ]
        if not movie_lines:
            raise ValueError("Base package movie.hktas is empty.")

    header = json.loads(movie_lines[0])
    if (
        header.get("format") != "hktas"
        or header.get("version") != 2
        or header.get("actionSchemaId") != "hktas-full-run-actions-v2"
    ):
        raise ValueError("Base package movie.hktas must be a valid Movie v2 document.")

    base_runs = [json.loads(line) for line in movie_lines[1:]]
    prefix_runs, last_prefix_mask = trim_prefix_runs(base_runs, prefix_frames)

    rng = random.Random(seed)
    blocks = generate_profile_blocks(profile, fuzz_frames, rng) if fuzz_frames > 0 else []
    fuzz_runs = encode_fuzz_runs(blocks, last_prefix_mask)
    all_runs = prefix_runs + fuzz_runs

    total_frames = sum(int(r["repeatCount"]) for r in all_runs)
    if total_frames != prefix_frames + fuzz_frames:
        raise AssertionError(f"Frame count mismatch: {total_frames} != {prefix_frames + fuzz_frames}")

    movie_text = "\n".join([compact_json(header)] + [compact_json(r) for r in all_runs]) + "\n"
    movie_bytes = movie_text.encode("utf-8")
    movie_sha256 = hashlib.sha256(movie_bytes).hexdigest()

    out_manifest = copy.deepcopy(manifest)
    out_manifest["MovieSha256"] = movie_sha256
    out_manifest.pop("Timeline", None)
    out_manifest.pop("Checkpoints", None)
    manifest_bytes = compact_json(out_manifest).encode("utf-8")

    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=zipfile.ZIP_DEFLATED) as dst_zip:
        for rel_name in sorted(save_entries.keys()):
            dst_zip.writestr(rel_name, save_entries[rel_name])
        dst_zip.writestr("movie.hktas", movie_bytes)
        dst_zip.writestr("manifest.json", manifest_bytes)

    out_resolved.parent.mkdir(parents=True, exist_ok=True)
    tmp_path = out_resolved.with_name(f".{out_resolved.name}.{os.getpid()}.tmp")
    try:
        tmp_path.write_bytes(buffer.getvalue())
        os.replace(tmp_path, out_resolved)
    finally:
        if tmp_path.exists():
            tmp_path.unlink()

    return {
        "package": str(out_resolved),
        "basePackage": str(base_resolved),
        "profile": profile,
        "seed": seed,
        "prefixFrames": prefix_frames,
        "fuzzFrames": fuzz_frames,
        "totalFrames": total_frames,
        "runCount": len(all_runs),
        "movieSha256": movie_sha256,
        "initialSavesId": out_manifest["InitialSavesId"],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-package", required=True, type=Path, help="Base .hktaspack providing initial saves and optional prefix")
    parser.add_argument("--out", required=True, type=Path, help="Output .hktaspack path")
    parser.add_argument("--seed", type=int, default=42, help="Deterministic PRNG seed (default: 42)")
    parser.add_argument("--prefix-frames", type=int, default=0, help="Number of leading frames preserved from base package (default: 0)")
    parser.add_argument("--fuzz-frames", type=int, default=1800, help="Number of fuzz input frames appended after prefix (default: 1800)")
    parser.add_argument("--profile", choices=ALL_PROFILES, default="explore", help="Input fuzz profile (default: explore)")
    args = parser.parse_args(argv)

    summary = build_fuzz_package(
        base_package=args.base_package,
        out_package=args.out,
        seed=args.seed,
        prefix_frames=args.prefix_frames,
        fuzz_frames=args.fuzz_frames,
        profile=args.profile,
    )
    print(compact_json(summary))
    return 0


if __name__ == "__main__":
    sys.exit(main())
