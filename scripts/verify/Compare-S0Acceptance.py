#!/usr/bin/env python3
"""Compare baseline full-run trace against per-S0 in-process manual restore traces."""

from __future__ import annotations

import argparse
import csv
import json
import math
import random
import secrets
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any


@dataclass(frozen=True)
class TraceRow:
    segment: str
    native: int
    movie: int
    scene: str
    rng_sha256: str
    rng_rolls: str
    rng_delta: str
    time_double: float
    fixed_time_double: float
    tft_ms: float
    tft_ms_raw: str


@dataclass(frozen=True)
class SceneTransition:
    movie_frame: int
    from_scene: str
    to_scene: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "movieFrame": self.movie_frame,
            "fromScene": self.from_scene,
            "toScene": self.to_scene,
        }


@dataclass(frozen=True)
class SceneInterval:
    scene_index: int
    scene: str
    start_movie: int
    end_movie: int
    frame_count: int

    def to_dict(self) -> dict[str, Any]:
        return {
            "sceneIndex": self.scene_index,
            "scene": self.scene,
            "startMovieFrame": self.start_movie,
            "endMovieFrame": self.end_movie,
            "frameCount": self.frame_count,
        }


@dataclass(frozen=True)
class SampledFrame:
    scene_index: int
    scene: str
    stage: str
    movie_frame: int
    rng_sha256: str
    rng_rolls: str
    rng_delta: str
    tft_ms: float
    tft_ms_raw: str

    def to_dict(self) -> dict[str, Any]:
        return {
            "sceneIndex": self.scene_index,
            "scene": self.scene,
            "stage": self.stage,
            "movieFrame": self.movie_frame,
            "rngSha256": self.rng_sha256,
            "rngRolls": self.rng_rolls,
            "rngDelta": self.rng_delta,
            "tftMs": self.tft_ms,
            "tftMsRaw": self.tft_ms_raw,
        }


def load_trace_segments(trace_path: Path) -> dict[str, list[TraceRow]]:
    segments: dict[str, list[TraceRow]] = {}
    with trace_path.open("r", encoding="utf-8", newline="") as fh:
        reader = csv.DictReader(fh)
        required = {
            "segment",
            "native",
            "movie",
            "scene",
            "rngSha256",
            "rngRolls",
            "rngDelta",
            "timeDouble",
            "fixedTimeDouble",
            "tftMs",
        }
        if reader.fieldnames is None or not required.issubset(set(reader.fieldnames)):
            raise ValueError(
                f"Trace CSV missing required headers. Found={reader.fieldnames}, required={sorted(required)}"
            )
        for row in reader:
            seg = row["segment"]
            tr = TraceRow(
                segment=seg,
                native=int(row["native"]),
                movie=int(row["movie"]),
                scene=row["scene"],
                rng_sha256=row["rngSha256"],
                rng_rolls=row["rngRolls"],
                rng_delta=row["rngDelta"],
                time_double=float(row["timeDouble"]),
                fixed_time_double=float(row["fixedTimeDouble"]),
                tft_ms=float(row["tftMs"]),
                tft_ms_raw=row["tftMs"],
            )
            segments.setdefault(seg, []).append(tr)
    for seg_rows in segments.values():
        seg_rows.sort(key=lambda r: r.movie)
    return segments


def build_frame_map(rows: list[TraceRow]) -> tuple[dict[int, TraceRow], list[int]]:
    by_movie: dict[int, TraceRow] = {}
    duplicates: list[int] = []
    for r in rows:
        if r.movie in by_movie:
            duplicates.append(r.movie)
        else:
            by_movie[r.movie] = r
    return by_movie, duplicates


def extract_transitions(rows: list[TraceRow]) -> list[SceneTransition]:
    transitions: list[SceneTransition] = []
    if not rows:
        return transitions
    prev_scene = rows[0].scene
    for r in rows[1:]:
        if r.scene != prev_scene:
            transitions.append(
                SceneTransition(
                    movie_frame=r.movie,
                    from_scene=prev_scene,
                    to_scene=r.scene,
                )
            )
            prev_scene = r.scene
    return transitions


def extract_scene_intervals(rows: list[TraceRow]) -> list[tuple[SceneInterval, list[TraceRow]]]:
    intervals: list[tuple[SceneInterval, list[TraceRow]]] = []
    if not rows:
        return intervals
    group: list[TraceRow] = [rows[0]]
    idx = 0
    for r in rows[1:]:
        if r.scene != group[0].scene:
            intervals.append(
                (
                    SceneInterval(
                        scene_index=idx,
                        scene=group[0].scene,
                        start_movie=group[0].movie,
                        end_movie=group[-1].movie,
                        frame_count=len(group),
                    ),
                    group,
                )
            )
            idx += 1
            group = [r]
        else:
            group.append(r)
    intervals.append(
        (
            SceneInterval(
                scene_index=idx,
                scene=group[0].scene,
                start_movie=group[0].movie,
                end_movie=group[-1].movie,
                frame_count=len(group),
            ),
            group,
        )
    )
    return intervals


def sample_scene_frames(
    intervals: list[tuple[SceneInterval, list[TraceRow]]],
    seed: int,
) -> list[SampledFrame]:
    rng = random.Random(seed)
    samples: list[SampledFrame] = []
    for interval, group in intervals:
        length = len(group)
        if length >= 3:
            t1 = length // 3
            t2 = (2 * length) // 3
            buckets = [
                ("early", group[0:t1]),
                ("mid", group[t1:t2]),
                ("late", group[t2:length]),
            ]
            for stage, bucket in buckets:
                chosen = rng.choice(bucket)
                samples.append(
                    SampledFrame(
                        scene_index=interval.scene_index,
                        scene=interval.scene,
                        stage=stage,
                        movie_frame=chosen.movie,
                        rng_sha256=chosen.rng_sha256,
                        rng_rolls=chosen.rng_rolls,
                        rng_delta=chosen.rng_delta,
                        tft_ms=chosen.tft_ms,
                        tft_ms_raw=chosen.tft_ms_raw,
                    )
                )
        else:
            for idx, chosen in enumerate(group):
                samples.append(
                    SampledFrame(
                        scene_index=interval.scene_index,
                        scene=interval.scene,
                        stage=f"short_scene_{idx}",
                        movie_frame=chosen.movie,
                        rng_sha256=chosen.rng_sha256,
                        rng_rolls=chosen.rng_rolls,
                        rng_delta=chosen.rng_delta,
                        tft_ms=chosen.tft_ms,
                        tft_ms_raw=chosen.tft_ms_raw,
                    )
                )
    return samples


def floats_equal_exact(a: float, b: float) -> bool:
    if math.isnan(a) or math.isnan(b) or math.isinf(a) or math.isinf(b):
        return False
    return a == b


def compare_s0_acceptance(
    *,
    trace_path: Path,
    checkpoints_path: Path | None,
    runs_path: Path | None,
    suite_name: str,
    seed: int,
) -> dict[str, Any]:
    runs_meta: dict[str, Any] = {}
    if runs_path is not None and runs_path.exists():
        runs_meta = json.loads(runs_path.read_text(encoding="utf-8"))

    checkpoints_meta: dict[str, Any] = {}
    if checkpoints_path is not None and checkpoints_path.exists():
        checkpoints_meta = json.loads(checkpoints_path.read_text(encoding="utf-8"))

    baseline_error = str(runs_meta.get("baselineError") or "").strip()
    if baseline_error:
        return {
            "suite": suite_name,
            "passed": False,
            "seed": seed,
            "reason": "baseline_execution_failed",
            "detail": baseline_error,
            "baseline": {},
            "baselineTransitions": [],
            "sceneIntervals": [],
            "sampledFrames": [],
            "s0Results": [],
        }

    if not trace_path.exists():
        return {
            "suite": suite_name,
            "passed": False,
            "seed": seed,
            "reason": "missing_trace_file",
            "detail": f"Acceptance trace CSV not found: {trace_path}",
            "baseline": {},
            "baselineTransitions": [],
            "sceneIntervals": [],
            "sampledFrames": [],
            "s0Results": [],
        }

    segments = load_trace_segments(trace_path)
    baseline_rows = segments.get("baseline", [])
    if not baseline_rows:
        return {
            "suite": suite_name,
            "passed": False,
            "seed": seed,
            "reason": "empty_baseline_segment",
            "detail": "No baseline frames were recorded in acceptance-frame-trace.csv.",
            "baseline": {},
            "baselineTransitions": [],
            "sceneIntervals": [],
            "sampledFrames": [],
            "s0Results": [],
        }

    base_map, base_dups = build_frame_map(baseline_rows)
    if base_dups:
        return {
            "suite": suite_name,
            "passed": False,
            "seed": seed,
            "reason": "duplicate_baseline_frames",
            "detail": f"Baseline trace has duplicate movie frames: {base_dups[:10]}",
            "baseline": {},
            "baselineTransitions": [],
            "sceneIntervals": [],
            "sampledFrames": [],
            "s0Results": [],
        }

    base_transitions = extract_transitions(baseline_rows)
    intervals_with_rows = extract_scene_intervals(baseline_rows)
    intervals = [iv for iv, _ in intervals_with_rows]
    sampled = sample_scene_frames(intervals_with_rows, seed)

    run_entries: list[dict[str, Any]] = list(runs_meta.get("runs") or [])
    captured_count = int(checkpoints_meta.get("capturedCount", runs_meta.get("capturedCount", len(run_entries))))
    qualified_count = int(checkpoints_meta.get("qualifiedCount", runs_meta.get("qualifiedCount", len(run_entries))))

    base_summary = {
        "frameCount": len(baseline_rows),
        "firstMovieFrame": baseline_rows[0].movie,
        "lastMovieFrame": baseline_rows[-1].movie,
        "transitionCount": len(base_transitions),
        "sceneIntervalCount": len(intervals),
        "sampledFrameCount": len(sampled),
        "capturedCheckpoints": captured_count,
        "qualifiedCheckpoints": qualified_count,
    }

    if not run_entries:
        return {
            "suite": suite_name,
            "passed": False,
            "seed": seed,
            "reason": "no_s0_checkpoints_tested",
            "detail": "No qualified S0 checkpoints were captured or tested during baseline.",
            "baseline": base_summary,
            "baselineTransitions": [t.to_dict() for t in base_transitions],
            "sceneIntervals": [iv.to_dict() for iv in intervals],
            "sampledFrames": [s.to_dict() for s in sampled],
            "s0Results": [],
        }

    s0_results: list[dict[str, Any]] = []
    all_passed = True
    first_failure_reason: str | None = None

    for run in run_entries:
        seg_name = str(run.get("SegmentName") or run.get("segmentName") or "")
        cp_id = str(run.get("CheckpointId") or run.get("checkpointId") or "")
        resume_frame = int(run.get("ResumeMovieFrame") if "ResumeMovieFrame" in run else run.get("resumeMovieFrame", -1))
        target_frame = int(run.get("TargetMovieFrame") if "TargetMovieFrame" in run else run.get("targetMovieFrame", -1))
        transition_id = int(run.get("TransitionId") if "TransitionId" in run else run.get("transitionId", -1))
        run_succeeded = bool(run.get("Succeeded") if "Succeeded" in run else run.get("succeeded", False))
        run_skipped = bool(run.get("Skipped") if "Skipped" in run else run.get("skipped", False))
        run_error = str(run.get("Error") or run.get("error") or "")
        elapsed_ms = float(run.get("ElapsedMs") if "ElapsedMs" in run else run.get("elapsedMs", 0.0))

        entry: dict[str, Any] = {
            "index": run.get("Index", run.get("index")),
            "segmentName": seg_name,
            "checkpointId": cp_id,
            "transitionId": transition_id,
            "resumeMovieFrame": resume_frame,
            "targetMovieFrame": target_frame,
            "elapsedMs": elapsed_ms,
            "runSucceeded": run_succeeded,
            "runSkipped": run_skipped,
            "runError": run_error,
            "passed": False,
            "reason": "",
            "startSceneCheck": None,
            "frameCoverageCheck": None,
            "transitionCheck": None,
            "sampleChecks": [],
            "sampleFailures": [],
        }

        if not run_succeeded:
            entry["reason"] = "s0_restore_execution_failed"
            all_passed = False
            first_failure_reason = first_failure_reason or "s0_restore_execution_failed"
            s0_results.append(entry)
            continue

        s0_rows = segments.get(seg_name, [])
        if not s0_rows and cp_id:
            suffix = f":{cp_id}"
            matched_keys = [k for k in segments.keys() if k.endswith(suffix)]
            if len(matched_keys) == 1:
                resolved_seg = matched_keys[0]
                s0_rows = segments[resolved_seg]
                entry["resolvedSegmentName"] = resolved_seg
        if not s0_rows:
            entry["reason"] = "missing_s0_trace_segment"
            entry["runError"] = f"No trace rows found for segment '{seg_name}'."
            all_passed = False
            first_failure_reason = first_failure_reason or "missing_s0_trace_segment"
            s0_results.append(entry)
            continue

        s0_map, s0_dups = build_frame_map(s0_rows)
        if s0_dups:
            entry["reason"] = "duplicate_s0_frames"
            entry["runError"] = f"Duplicate movie frames in '{seg_name}': {s0_dups[:10]}"
            all_passed = False
            first_failure_reason = first_failure_reason or "duplicate_s0_frames"
            s0_results.append(entry)
            continue

        s0_first = s0_rows[0].movie
        s0_last = s0_rows[-1].movie
        base_last = baseline_rows[-1].movie

        # 1. Verify frame coverage [resume_frame .. base_last]
        expected_frames = [r.movie for r in baseline_rows if r.movie >= resume_frame]
        actual_frames = [r.movie for r in s0_rows]
        missing_frames = sorted(set(expected_frames) - set(actual_frames))
        extra_frames = sorted(set(actual_frames) - set(expected_frames))
        coverage_ok = len(missing_frames) == 0 and len(extra_frames) == 0
        entry["frameCoverageCheck"] = {
            "passed": coverage_ok,
            "expectedFirst": expected_frames[0] if expected_frames else None,
            "expectedLast": expected_frames[-1] if expected_frames else None,
            "actualFirst": s0_first,
            "actualLast": s0_last,
            "missingCount": len(missing_frames),
            "extraCount": len(extra_frames),
            "missingPreview": missing_frames[:10],
            "extraPreview": extra_frames[:10],
        }

        # 2. Verify start frame scene matches baseline at resume_frame
        base_at_resume = base_map.get(resume_frame)
        s0_at_resume = s0_map.get(resume_frame)
        start_scene_ok = (
            base_at_resume is not None
            and s0_at_resume is not None
            and base_at_resume.scene == s0_at_resume.scene
        )
        entry["startSceneCheck"] = {
            "passed": start_scene_ok,
            "movieFrame": resume_frame,
            "baselineScene": base_at_resume.scene if base_at_resume else None,
            "s0Scene": s0_at_resume.scene if s0_at_resume else None,
        }

        # 3. Verify scene transition frames strictly match baseline after resume_frame
        max_common_movie = min(s0_last, base_last)
        expected_transitions = [
            t for t in base_transitions if resume_frame < t.movie_frame <= max_common_movie
        ]
        actual_transitions = extract_transitions([r for r in s0_rows if r.movie <= max_common_movie])
        transition_diffs: list[dict[str, Any]] = []
        max_len = max(len(expected_transitions), len(actual_transitions))
        for idx in range(max_len):
            exp = expected_transitions[idx] if idx < len(expected_transitions) else None
            act = actual_transitions[idx] if idx < len(actual_transitions) else None
            if exp != act:
                transition_diffs.append(
                    {
                        "transitionOrdinal": idx,
                        "baseline": exp.to_dict() if exp else None,
                        "s0": act.to_dict() if act else None,
                    }
                )
        transitions_ok = start_scene_ok and len(transition_diffs) == 0 and coverage_ok
        entry["transitionCheck"] = {
            "passed": len(transition_diffs) == 0 and start_scene_ok,
            "expectedCount": len(expected_transitions),
            "actualCount": len(actual_transitions),
            "expected": [t.to_dict() for t in expected_transitions],
            "actual": [t.to_dict() for t in actual_transitions],
            "diffs": transition_diffs,
        }

        # 4. Verify early/mid/late sampled frames for RNG and TFT
        applicable_samples = [s for s in sampled if s.movie_frame >= resume_frame]
        sample_checks: list[dict[str, Any]] = []
        sample_failures: list[dict[str, Any]] = []
        for s in applicable_samples:
            actual_row = s0_map.get(s.movie_frame)
            if actual_row is None:
                check = {
                    "sceneIndex": s.scene_index,
                    "scene": s.scene,
                    "stage": s.stage,
                    "movieFrame": s.movie_frame,
                    "passed": False,
                    "missing": True,
                    "sceneMatch": False,
                    "rngMatch": False,
                    "tftMatch": False,
                    "baselineRngSha256": s.rng_sha256,
                    "s0RngSha256": None,
                    "baselineTftMs": s.tft_ms,
                    "s0TftMs": None,
                    "tftAbsDiffMs": None,
                    "baselineRngRolls": s.rng_rolls,
                    "s0RngRolls": None,
                    "baselineRngDelta": s.rng_delta,
                    "s0RngDelta": None,
                }
                sample_checks.append(check)
                sample_failures.append(check)
                continue

            scene_match = actual_row.scene == s.scene
            rng_match = bool(s.rng_sha256) and (actual_row.rng_sha256 == s.rng_sha256)
            tft_match = floats_equal_exact(actual_row.tft_ms, s.tft_ms)
            abs_diff_ms = abs(actual_row.tft_ms - s.tft_ms)
            ok = scene_match and rng_match and tft_match
            check = {
                "sceneIndex": s.scene_index,
                "scene": s.scene,
                "s0Scene": actual_row.scene,
                "stage": s.stage,
                "movieFrame": s.movie_frame,
                "passed": ok,
                "missing": False,
                "sceneMatch": scene_match,
                "rngMatch": rng_match,
                "tftMatch": tft_match,
                "baselineRngSha256": s.rng_sha256,
                "s0RngSha256": actual_row.rng_sha256,
                "baselineTftMs": s.tft_ms,
                "s0TftMs": actual_row.tft_ms,
                "tftAbsDiffMs": abs_diff_ms,
                "baselineRngRolls": s.rng_rolls,
                "s0RngRolls": actual_row.rng_rolls,
                "baselineRngDelta": s.rng_delta,
                "s0RngDelta": actual_row.rng_delta,
            }
            sample_checks.append(check)
            if not ok:
                sample_failures.append(check)

        entry["sampleChecks"] = sample_checks
        entry["sampleFailures"] = sample_failures

        entry_passed = transitions_ok and len(sample_failures) == 0
        entry["passed"] = entry_passed
        if not entry_passed:
            all_passed = False
            if not coverage_ok:
                entry["reason"] = "frame_coverage_mismatch"
            elif not start_scene_ok or len(transition_diffs) > 0:
                entry["reason"] = "scene_transition_mismatch"
            else:
                has_rng = any(not f["rngMatch"] for f in sample_failures)
                has_tft = any(not f["tftMatch"] for f in sample_failures)
                if has_rng and has_tft:
                    entry["reason"] = "sample_rng_and_tft_mismatch"
                elif has_rng:
                    entry["reason"] = "sample_rng_mismatch"
                elif has_tft:
                    entry["reason"] = "sample_tft_mismatch"
                else:
                    entry["reason"] = "sample_scene_mismatch"
            first_failure_reason = first_failure_reason or entry["reason"]
        else:
            entry["reason"] = "ok"

        s0_results.append(entry)

    return {
        "suite": suite_name,
        "passed": all_passed,
        "seed": seed,
        "reason": "ok" if all_passed else (first_failure_reason or "acceptance_failed"),
        "detail": "" if all_passed else f"One or more S0 comparisons failed (first reason: {first_failure_reason}).",
        "baseline": base_summary,
        "baselineTransitions": [t.to_dict() for t in base_transitions],
        "sceneIntervals": [iv.to_dict() for iv in intervals],
        "sampledFrames": [s.to_dict() for s in sampled],
        "s0Results": s0_results,
    }


def render_markdown_summary(report: dict[str, Any]) -> str:
    lines: list[str] = []
    suite = report["suite"]
    passed = report["passed"]
    lines.append(f"# S0 实机验收对比报告 - `{suite}`")
    lines.append("")
    if not passed:
        lines.append("> [!IMPORTANT]")
        lines.append("> **注意：本实机验收未通过不代表代码必然存在问题。** 请结合下方具体差异原因（切场景帧偏差 / RNG 偏差 / TFT 偏差 / 回档执行异常）由用户决策后续处理方式。")
        lines.append("")

    base = report.get("baseline") or {}
    lines.append("## 1. 总览")
    lines.append("")
    lines.append("| 指标 | 值 |")
    lines.append("| --- | --- |")
    lines.append(f"| 验收套件 | `{suite}` |")
    lines.append(f"| 验收结论 | **{'PASS (全部一致)' if passed else 'FAIL (存在差异或执行失败)'}** |")
    lines.append(f"| 失败原因代码 | `{report.get('reason', '')}` |")
    lines.append(f"| 随机抽样 Seed | `{report.get('seed')}` |")
    if base:
        lines.append(f"| Baseline 帧范围 | `{base.get('firstMovieFrame')} .. {base.get('lastMovieFrame')}` (共 {base.get('frameCount')} 帧) |")
        lines.append(f"| Baseline 切场景次数 | {base.get('transitionCount')} |")
        lines.append(f"| Baseline 场景区间数 | {base.get('sceneIntervalCount')} |")
        lines.append(f"| 抽样对比帧总数 | {base.get('sampledFrameCount')} |")
        lines.append(f"| 捕获 / 合格 S0 数 | {base.get('capturedCheckpoints')} / {base.get('qualifiedCheckpoints')} |")
    lines.append("")

    if report.get("detail"):
        lines.append(f"**摘要说明**：{report['detail']}")
        lines.append("")

    transitions = report.get("baselineTransitions") or []
    lines.append("## 2. Baseline 切场景帧记录")
    lines.append("")
    if transitions:
        lines.append("| # | Movie 帧 | 源场景 -> 目标场景 |")
        lines.append("| --- | --- | --- |")
        for idx, t in enumerate(transitions, start=1):
            lines.append(f"| {idx} | `{t['movieFrame']}` | `{t['fromScene']}` -> `{t['toScene']}` |")
    else:
        lines.append("_未记录到任何切场景事件。_")
    lines.append("")

    sampled = report.get("sampledFrames") or []
    lines.append("## 3. 每场景前/中/后期随机抽样基准")
    lines.append("")
    if sampled:
        lines.append("| 场景序号 | 场景名 | 阶段 | Movie 帧 | RNG SHA-256 (前12位) | TFT (ms) |")
        lines.append("| --- | --- | --- | --- | --- | --- |")
        for s in sampled:
            lines.append(
                f"| {s['sceneIndex']} | `{s['scene']}` | `{s['stage']}` | `{s['movieFrame']}` | `{s['rngSha256'][:12]}` | `{s['tftMsRaw']}` |"
            )
    else:
        lines.append("_无抽样帧。_")
    lines.append("")

    s0_results = report.get("s0Results") or []
    lines.append("## 4. 每个 S0 手动回档对比结果")
    lines.append("")
    if s0_results:
        lines.append("| # | Checkpoint ID | 恢复帧 | 状态 | 原因 | 切场景匹配 | 抽样一致数 | 耗时 (ms) |")
        lines.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
        for r in s0_results:
            tc = r.get("transitionCheck") or {}
            t_ok = "PASS" if tc.get("passed") else "FAIL"
            checks = r.get("sampleChecks") or []
            ok_cnt = sum(1 for c in checks if c.get("passed"))
            lines.append(
                f"| {r.get('index')} | `{r.get('checkpointId')[:12]}` | `{r.get('resumeMovieFrame')}` | "
                f"**{'PASS' if r.get('passed') else 'FAIL'}** | `{r.get('reason')}` | "
                f"{t_ok} ({tc.get('actualCount', '-')}/{tc.get('expectedCount', '-')}) | "
                f"{ok_cnt}/{len(checks)} | {r.get('elapsedMs', 0):.0f} |"
            )
        lines.append("")

        failed = [r for r in s0_results if not r.get("passed")]
        if failed:
            lines.append("### 失败 S0 详细差异（供人工决策）")
            lines.append("")
            for r in failed:
                lines.append(f"#### S0 #{r.get('index')} `{r.get('checkpointId')}` (resumeFrame=`{r.get('resumeMovieFrame')}`)")
                if r.get("runError"):
                    lines.append(f"- 执行异常：`{r['runError']}`")
                fc = r.get("frameCoverageCheck")
                if fc and not fc.get("passed"):
                    lines.append(
                        f"- 帧覆盖偏差：预期 `[{fc.get('expectedFirst')}..{fc.get('expectedLast')}]`，"
                        f"实际 `[{fc.get('actualFirst')}..{fc.get('actualLast')}]`，"
                        f"缺失={fc.get('missingCount')}（预览 `{fc.get('missingPreview')}`），"
                        f"多余={fc.get('extraCount')}（预览 `{fc.get('extraPreview')}`）"
                    )
                sc = r.get("startSceneCheck")
                if sc and not sc.get("passed"):
                    lines.append(
                        f"- 起点场景不一致（frame=`{sc.get('movieFrame')}`）：Baseline=`{sc.get('baselineScene')}` vs S0=`{sc.get('s0Scene')}`"
                    )
                tc = r.get("transitionCheck")
                if tc and tc.get("diffs"):
                    lines.append("- 切场景帧差异：")
                    for d in tc["diffs"][:10]:
                        lines.append(f"  - 第 {d['transitionOrdinal']} 次切换：Baseline=`{d['baseline']}` vs S0=`{d['s0']}`")
                sf = r.get("sampleFailures") or []
                if sf:
                    lines.append("- 随机抽样帧差异：")
                    for f in sf[:20]:
                        lines.append(
                            f"  - 场景 `{f['scene']}` ({f['stage']}) frame=`{f['movieFrame']}`: "
                            f"sceneMatch={f['sceneMatch']} (s0=`{f.get('s0Scene')}`), "
                            f"rngMatch={f['rngMatch']} (base=`{str(f.get('baselineRngSha256'))[:12]}` vs s0=`{str(f.get('s0RngSha256'))[:12]}`), "
                            f"tftMatch={f['tftMatch']} (base=`{f.get('baselineTftMs')}` ms vs s0=`{f.get('s0TftMs')}` ms, diff=`{f.get('tftAbsDiffMs')}` ms)"
                        )
                lines.append("")
    else:
        lines.append("_无 S0 回档运行记录。_")

    return "\n".join(lines).rstrip() + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Compare S0 acceptance traces against baseline.")
    parser.add_argument("--trace", required=True, help="Path to acceptance-frame-trace.csv")
    parser.add_argument("--checkpoints", default="", help="Path to baseline-checkpoints.json")
    parser.add_argument("--runs", default="", help="Path to s0-runs.json")
    parser.add_argument("--suite", required=True, help="Suite name")
    parser.add_argument("--seed", type=int, default=0, help="Random seed (0 = generate random positive seed)")
    parser.add_argument("--out-dir", required=True, help="Output directory for report.json and summary.md")
    args = parser.parse_args(argv)

    seed = args.seed if args.seed > 0 else secrets.randbelow(2_147_483_646) + 1
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    report = compare_s0_acceptance(
        trace_path=Path(args.trace),
        checkpoints_path=Path(args.checkpoints) if args.checkpoints else None,
        runs_path=Path(args.runs) if args.runs else None,
        suite_name=args.suite,
        seed=seed,
    )
    summary_md = render_markdown_summary(report)

    (out_dir / "report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    (out_dir / "summary.md").write_text(summary_md, encoding="utf-8")
    print(f"Suite={args.suite} Passed={report['passed']} Reason={report['reason']} Seed={seed}")
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    sys.exit(main())
