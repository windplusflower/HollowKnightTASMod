#!/usr/bin/env python3
"""Compare source and target Lean S0 K3 behavioral trajectory traces."""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any


def load_trace(path: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    header: dict[str, Any] | None = None
    frames: list[dict[str, Any]] = []
    with path.open("r", encoding="utf-8") as fh:
        for line_no, raw in enumerate(fh, start=1):
            text = raw.strip()
            if not text:
                continue
            obj = json.loads(text)
            rtype = obj.get("recordType")
            if rtype == "header":
                if header is not None:
                    raise ValueError(f"{path}:{line_no}: duplicate header record")
                header = obj
            elif rtype == "frame":
                frames.append(obj)
            else:
                raise ValueError(f"{path}:{line_no}: unknown recordType '{rtype}'")
    if header is None:
        raise ValueError(f"{path}: missing header record")
    return header, frames


def compare_values(
    path: str,
    src: Any,
    tgt: Any,
    tol: float,
) -> tuple[str, Any, Any] | None:
    if type(src) is not type(tgt) and not (
        isinstance(src, (int, float))
        and not isinstance(src, bool)
        and isinstance(tgt, (int, float))
        and not isinstance(tgt, bool)
    ):
        return (path, src, tgt)

    if isinstance(src, dict):
        src_keys = sorted(src.keys())
        tgt_keys = sorted(tgt.keys())
        if src_keys != tgt_keys:
            return (f"{path}.<keys>", src_keys, tgt_keys)
        for k in src_keys:
            diff = compare_values(f"{path}.{k}", src[k], tgt[k], tol)
            if diff is not None:
                return diff
        return None

    if isinstance(src, list):
        if len(src) != len(tgt):
            return (f"{path}.length", len(src), len(tgt))
        for idx, (sv, tv) in enumerate(zip(src, tgt)):
            diff = compare_values(f"{path}[{idx}]", sv, tv, tol)
            if diff is not None:
                return diff
        return None

    if isinstance(src, float) or isinstance(tgt, float):
        sf = float(src)
        tf = float(tgt)
        if math.isnan(sf) or math.isnan(tf) or math.isinf(sf) or math.isinf(tf):
            return (path, src, tgt)
        if abs(sf - tf) > tol:
            return (path, src, tgt)
        return None

    if src != tgt:
        return (path, src, tgt)
    return None


def compare_k3_traces(
    source_path: Path,
    target_path: Path,
    float_tolerance: float = 0.0,
) -> dict[str, Any]:
    src_hdr, src_frames = load_trace(source_path)
    tgt_hdr, tgt_frames = load_trace(target_path)

    if src_hdr.get("schema") != "lean-s0-k3-trace-v1" or tgt_hdr.get("schema") != "lean-s0-k3-trace-v1":
        return {
            "passed": False,
            "reason": "schema_mismatch",
            "firstDivergence": {
                "movieFrame": None,
                "field": "header.schema",
                "source": src_hdr.get("schema"),
                "target": tgt_hdr.get("schema"),
            },
        }

    src_meta = src_hdr.get("metadata", {})
    tgt_meta = tgt_hdr.get("metadata", {})
    for key in ("ordinal", "targetScene", "entryGate", "startMovie", "endMovie", "rngSeed", "rngSha256"):
        if src_meta.get(key) != tgt_meta.get(key):
            return {
                "passed": False,
                "reason": "header_metadata_mismatch",
                "firstDivergence": {
                    "movieFrame": None,
                    "field": f"header.metadata.{key}",
                    "source": src_meta.get(key),
                    "target": tgt_meta.get(key),
                },
            }

    start_movie = int(src_meta["startMovie"])
    end_movie = int(src_meta["endMovie"])
    expected_count = end_movie - start_movie
    if expected_count <= 0:
        return {
            "passed": False,
            "reason": "invalid_frame_window",
            "firstDivergence": {
                "movieFrame": None,
                "field": "header.window",
                "source": f"{start_movie}..{end_movie}",
                "target": None,
            },
        }

    if len(src_frames) != expected_count or len(tgt_frames) != expected_count:
        return {
            "passed": False,
            "reason": "frame_count_mismatch",
            "expectedFrames": expected_count,
            "sourceFrames": len(src_frames),
            "targetFrames": len(tgt_frames),
            "firstDivergence": {
                "movieFrame": None,
                "field": "frameCount",
                "source": len(src_frames),
                "target": len(tgt_frames),
            },
        }

    behavioral_keys = ("movieFrame", "scene", "gameState", "rngSha256", "hero", "player", "enemies", "grimmchild")
    for idx, (sf, tf) in enumerate(zip(src_frames, tgt_frames)):
        expected_mf = start_movie + idx
        if sf.get("movieFrame") != expected_mf or tf.get("movieFrame") != expected_mf:
            return {
                "passed": False,
                "reason": "non_sequential_movie_frame",
                "firstDivergence": {
                    "movieFrame": expected_mf,
                    "field": "movieFrame",
                    "source": sf.get("movieFrame"),
                    "target": tf.get("movieFrame"),
                },
            }

        for key in behavioral_keys:
            diff = compare_values(key, sf.get(key), tf.get(key), float_tolerance)
            if diff is not None:
                field_path, s_val, t_val = diff
                return {
                    "passed": False,
                    "reason": "trajectory_divergence",
                    "comparedFrames": idx + 1,
                    "expectedFrames": expected_count,
                    "firstDivergence": {
                        "movieFrame": expected_mf,
                        "field": field_path,
                        "source": s_val,
                        "target": t_val,
                    },
                }

    return {
        "passed": True,
        "reason": "exact_match",
        "startMovie": start_movie,
        "endMovie": end_movie,
        "comparedFrames": expected_count,
        "floatTolerance": float_tolerance,
        "rngSeed": src_meta.get("rngSeed"),
        "rngSha256": src_meta.get("rngSha256"),
        "targetScene": src_meta.get("targetScene"),
        "entryGate": src_meta.get("entryGate"),
        "packetBytes": src_meta.get("packetBytes"),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Compare Lean S0 K3 behavioral traces.")
    parser.add_argument("--source", required=True, type=Path, help="Source lean-s0-k3.jsonl path")
    parser.add_argument("--target", required=True, type=Path, help="Target lean-s0-k3.jsonl path")
    parser.add_argument("--output", required=False, type=Path, help="Optional output JSON report path")
    parser.add_argument("--float-tolerance", type=float, default=0.0, help="Numeric comparison tolerance")
    args = parser.parse_args(argv)

    report = compare_k3_traces(args.source, args.target, float_tolerance=args.float_tolerance)
    encoded = json.dumps(report, indent=2, ensure_ascii=False)
    if args.output is not None:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded + "\n", encoding="utf-8")
    print(encoded)
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    sys.exit(main())
