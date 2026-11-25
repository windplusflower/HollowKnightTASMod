#!/usr/bin/env python3
"""Diff acceptance-rng-diag.jsonl (and optional acceptance-frame-trace.csv) to pinpoint first RNG/state divergence."""

from __future__ import annotations

import argparse
import csv
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

POSSIBLE_UNTRACKED_SOURCES = "PlayMaker.dll / Unity Native C++ / External Mod"


@dataclass
class SegmentDiag:
    name: str
    diag_starts: list[dict[str, Any]] = field(default_factory=list)
    hud_vessel_diags: list[dict[str, Any]] = field(default_factory=list)
    frame_starts: dict[int, dict[str, Any]] = field(default_factory=dict)
    frame_ends: dict[int, dict[str, Any]] = field(default_factory=dict)
    rng_calls_all: list[dict[str, Any]] = field(default_factory=list)
    rng_calls_by_frame: dict[int, list[dict[str, Any]]] = field(default_factory=dict)
    itween_by_frame: dict[int, list[dict[str, Any]]] = field(default_factory=dict)
    diag_frames: set[int] = field(default_factory=set)


def compact_stack(stack_val: str | None, max_frames: int = 6) -> str:
    if not stack_val:
        return ""
    text = str(stack_val).strip()
    if not text:
        return ""
    if "\n" not in text and " <- " in text:
        parts = [p.strip() for p in text.split(" <- ") if p.strip()]
        return " <- ".join(parts[:max_frames])

    frames: list[str] = []
    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        if line.startswith("at "):
            line = line[3:].strip()
        elif line.startswith("在 "):
            line = line[2:].strip()
        # Strip trailing " in <file>:line <n>"
        in_idx = line.find(" in ")
        if in_idx > 0:
            line = line[:in_idx].strip()
        if (
            "System.Environment.get_StackTrace" in line
            or "LoadingRandomCallTrace" in line
            or "RuntimeFullRunSession" in line
            or line.startswith("MonoMod.")
        ):
            continue
        paren = line.find("(")
        sig = line[:paren] if paren > 0 else line
        # Keep Class.Method when possible
        dots = sig.split(".")
        short = ".".join(dots[-2:]) if len(dots) >= 2 else sig
        frames.append(short)
        if len(frames) >= max_frames:
            break
    return " <- ".join(frames) if frames else text.splitlines()[0][:200]


def load_diag_jsonl(diag_path: Path) -> tuple[dict[str, SegmentDiag], list[str]]:
    segments: dict[str, SegmentDiag] = {}
    segment_order: list[str] = []

    with diag_path.open("r", encoding="utf-8-sig") as fh:
        for line_no, raw_line in enumerate(fh, start=1):
            line = raw_line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid JSON in {diag_path} at line {line_no}: {exc}") from exc

            seg_name = str(ev.get("segment") or "")
            if not seg_name:
                continue
            if seg_name not in segments:
                segments[seg_name] = SegmentDiag(name=seg_name)
                segment_order.append(seg_name)
            seg = segments[seg_name]
            ev_type = str(ev.get("eventType") or "")

            if ev_type == "diag-start":
                seg.diag_starts.append(ev)
                if "movieFrame" in ev and ev["movieFrame"] is not None:
                    seg.diag_frames.add(int(ev["movieFrame"]))
            elif ev_type == "hud-vessel-diag":
                seg.hud_vessel_diags.append(ev)
            elif ev_type == "frame-start":
                mf = int(ev["movieFrame"])
                seg.frame_starts.setdefault(mf, ev)
                seg.diag_frames.add(mf)
            elif ev_type == "frame-end":
                mf = int(ev["movieFrame"])
                seg.frame_ends[mf] = ev
                seg.diag_frames.add(mf)
            elif ev_type == "rng-call":
                mf = int(ev["movieFrame"])
                seg.rng_calls_all.append(ev)
                seg.rng_calls_by_frame.setdefault(mf, []).append(ev)
                seg.diag_frames.add(mf)
            elif ev_type == "itween-launch":
                mf = int(ev["movieFrame"])
                seg.itween_by_frame.setdefault(mf, []).append(ev)
                seg.diag_frames.add(mf)

    return segments, segment_order


def load_trace_rng_deltas(trace_path: Path) -> dict[tuple[str, int], dict[str, Any]]:
    result: dict[tuple[str, int], dict[str, Any]] = {}
    with trace_path.open("r", encoding="utf-8-sig", newline="") as fh:
        reader = csv.DictReader(fh)
        if reader.fieldnames is None:
            return result
        for row in reader:
            seg = row.get("segment") or ""
            movie_raw = row.get("movie")
            if not seg or movie_raw is None or movie_raw == "":
                continue
            mf = int(movie_raw)
            delta_raw = (row.get("rngDelta") or "").strip()
            rng_delta: int | None
            try:
                rng_delta = int(delta_raw)
            except ValueError:
                rng_delta = None
            result[(seg, mf)] = {
                "scene": row.get("scene") or "",
                "rngSha256": row.get("rngSha256") or "",
                "rngDelta": rng_delta,
                "rngDeltaRaw": delta_raw,
                "rngRolls": row.get("rngRolls") or "",
                "tftMs": row.get("tftMs") or "",
            }
    return result


def _summarize_rng_call(ev: dict[str, Any]) -> dict[str, Any]:
    return {
        "movieFrame": ev.get("movieFrame"),
        "idx": ev.get("idx"),
        "nativeFrame": ev.get("nativeFrame"),
        "frameInputEnabled": ev.get("frameInputEnabled"),
        "site": ev.get("site") or "",
        "owner": ev.get("owner") or "",
        "rngSha256": ev.get("rngSha256") or "",
        "stack": compact_stack(ev.get("stack")),
    }


def _find_call_global_index(seg: SegmentDiag, mf: int, idx_in_frame: int) -> int:
    frame_calls = seg.rng_calls_by_frame.get(mf, [])
    if idx_in_frame < len(frame_calls):
        target = frame_calls[idx_in_frame]
        for pos, item in enumerate(seg.rng_calls_all):
            if item is target:
                return pos
    if idx_in_frame > 0 and idx_in_frame - 1 < len(frame_calls):
        prev_target = frame_calls[idx_in_frame - 1]
        for pos, item in enumerate(seg.rng_calls_all):
            if item is prev_target:
                return pos + 1
    for pos, item in enumerate(seg.rng_calls_all):
        if int(item.get("movieFrame", -1)) > mf:
            return pos
    return len(seg.rng_calls_all)


def _slice_context(
    calls: list[dict[str, Any]],
    center_pos: int,
    has_item_at_center: bool,
    window: int = 3,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    before_start = max(0, center_pos - window)
    before = [_summarize_rng_call(c) for c in calls[before_start:center_pos]]
    after_start = center_pos + 1 if has_item_at_center else center_pos
    after_end = min(len(calls), after_start + window)
    after = [_summarize_rng_call(c) for c in calls[after_start:after_end]]
    return before, after


def _summarize_itween(ev: dict[str, Any]) -> dict[str, Any]:
    return {
        "target": ev.get("target") or "",
        "hasId": bool(ev.get("hasId", False)),
        "type": ev.get("type") or "",
        "method": ev.get("method") or "",
        "name": ev.get("name") or "",
        "frameInputEnabled": ev.get("frameInputEnabled"),
        "stack": compact_stack(ev.get("stack")),
    }


def diff_itween_for_frame(
    mf: int,
    base_events: list[dict[str, Any]],
    rest_events: list[dict[str, Any]],
) -> dict[str, Any]:
    b_norm = [_summarize_itween(e) for e in base_events]
    r_norm = [_summarize_itween(e) for e in rest_events]
    diffs: list[dict[str, Any]] = []
    max_len = max(len(b_norm), len(r_norm))
    compare_keys = ("target", "hasId", "type", "method", "name")
    for idx in range(max_len):
        be = b_norm[idx] if idx < len(b_norm) else None
        re = r_norm[idx] if idx < len(r_norm) else None
        if be is None or re is None:
            diffs.append({"index": idx, "kind": "count_mismatch", "baseline": be, "restore": re})
            continue
        changed = [k for k in compare_keys if be.get(k) != re.get(k)]
        if changed:
            diffs.append({"index": idx, "kind": "field_mismatch", "changedFields": changed, "baseline": be, "restore": re})
    return {
        "movieFrame": mf,
        "equal": len(diffs) == 0,
        "baselineCount": len(b_norm),
        "restoreCount": len(r_norm),
        "diffs": diffs,
        "baselineEvents": b_norm,
        "restoreEvents": r_norm,
    }


def diff_frame_end_for_frame(
    mf: int,
    base_end: dict[str, Any] | None,
    rest_end: dict[str, Any] | None,
) -> dict[str, Any] | None:
    if base_end is None and rest_end is None:
        return None
    if base_end is None or rest_end is None:
        return {
            "movieFrame": mf,
            "equal": False,
            "missingSide": "restore" if rest_end is None else "baseline",
            "fieldDiffs": {},
            "bouncesDiff": None,
        }

    tracked_fields = ("scene", "rngSha256", "hx", "hy", "bx", "by", "vx", "vy", "cState")
    field_diffs: dict[str, dict[str, Any]] = {}
    for key in tracked_fields:
        bv = base_end.get(key)
        rv = rest_end.get(key)
        if bv != rv:
            field_diffs[key] = {"baseline": bv, "restore": rv}

    b_bounces = list(base_end.get("bounces") or [])
    r_bounces = list(rest_end.get("bounces") or [])
    bounces_diff: dict[str, Any] | None = None
    if b_bounces != r_bounces:
        item_diffs: list[dict[str, Any]] = []
        for idx in range(max(len(b_bounces), len(r_bounces))):
            bb = b_bounces[idx] if idx < len(b_bounces) else None
            rb = r_bounces[idx] if idx < len(r_bounces) else None
            if bb != rb:
                item_diffs.append({"index": idx, "baseline": bb, "restore": rb})
        bounces_diff = {
            "baselineCount": len(b_bounces),
            "restoreCount": len(r_bounces),
            "diffs": item_diffs,
        }

    return {
        "movieFrame": mf,
        "equal": len(field_diffs) == 0 and bounces_diff is None,
        "missingSide": None,
        "fieldDiffs": field_diffs,
        "bouncesDiff": bounces_diff,
    }


def _normalize_hud_fsm(fsm: dict[str, Any]) -> dict[str, Any]:
    return {
        "fsmName": fsm.get("fsmName") or "",
        "activeStateName": fsm.get("activeStateName") or "",
        "previousActiveState": fsm.get("previousActiveState") or "",
        "initialized": bool(fsm.get("initialized", False)),
        "started": bool(fsm.get("started", False)),
        "intVars": dict(fsm.get("intVars") or {}),
        "boolVars": dict(fsm.get("boolVars") or {}),
    }


def diff_hud_vessel_diag(
    base_huds: list[dict[str, Any]],
    rest_huds: list[dict[str, Any]],
) -> dict[str, Any] | None:
    if not base_huds and not rest_huds:
        return None
    scalar_keys = (
        "mpCharge",
        "mpReserve",
        "mpReserveMax",
        "soulLimited",
        "inBossSequence",
        "boundSoul",
        "vesselScale",
    )
    entries: list[dict[str, Any]] = []
    any_diff = False
    for idx in range(max(len(base_huds), len(rest_huds))):
        bh = base_huds[idx] if idx < len(base_huds) else None
        rh = rest_huds[idx] if idx < len(rest_huds) else None
        if bh is None or rh is None:
            any_diff = True
            entries.append(
                {
                    "index": idx,
                    "equal": False,
                    "missingSide": "restore" if rh is None else "baseline",
                    "scalarDiffs": {},
                    "fsmDiffs": [],
                }
            )
            continue

        scalar_diffs: dict[str, dict[str, Any]] = {}
        for key in scalar_keys:
            if bh.get(key) != rh.get(key):
                scalar_diffs[key] = {"baseline": bh.get(key), "restore": rh.get(key)}

        b_fsms = [_normalize_hud_fsm(f) for f in (bh.get("fsms") or [])]
        r_fsms = [_normalize_hud_fsm(f) for f in (rh.get("fsms") or [])]
        fsm_diffs: list[dict[str, Any]] = []
        for f_idx in range(max(len(b_fsms), len(r_fsms))):
            bf = b_fsms[f_idx] if f_idx < len(b_fsms) else None
            rf = r_fsms[f_idx] if f_idx < len(r_fsms) else None
            if bf != rf:
                fsm_diffs.append({"fsmIndex": f_idx, "baseline": bf, "restore": rf})

        entry_equal = len(scalar_diffs) == 0 and len(fsm_diffs) == 0
        if not entry_equal:
            any_diff = True
        entries.append(
            {
                "index": idx,
                "equal": entry_equal,
                "missingSide": None,
                "scalarDiffs": scalar_diffs,
                "fsmDiffs": fsm_diffs,
            }
        )

    return {
        "equal": not any_diff,
        "baselineCount": len(base_huds),
        "restoreCount": len(rest_huds),
        "entries": entries,
    }


def detect_untracked_rng_alerts(
    segments: dict[str, SegmentDiag],
    segment_order: list[str],
    trace_map: dict[tuple[str, int], dict[str, Any]] | None,
) -> list[dict[str, Any]]:
    alerts: list[dict[str, Any]] = []
    for seg_name in segment_order:
        seg = segments[seg_name]
        for mf in sorted(seg.diag_frames):
            calls = seg.rng_calls_by_frame.get(mf, [])
            captured = len(calls)
            fs = seg.frame_starts.get(mf)
            fe = seg.frame_ends.get(mf)

            reasons: list[str] = []
            csv_delta: int | None = None
            untracked_delta: int | None = None

            if trace_map is not None:
                tr = trace_map.get((seg_name, mf))
                if tr is not None and tr.get("rngDelta") is not None:
                    csv_delta = int(tr["rngDelta"])
                    if csv_delta > captured:
                        reasons.append("csv_rng_delta_exceeds_captured_calls")
                        untracked_delta = csv_delta - captured

            fs_sha = (fs.get("rngSha256") or "") if fs else ""
            fe_sha = (fe.get("rngSha256") or "") if fe else ""
            last_sha = (calls[-1].get("rngSha256") or "") if calls else ""

            if fs is not None and fe is not None and fs_sha and fe_sha and fs_sha != fe_sha and captured == 0:
                reasons.append("frame_start_end_rng_changed_with_zero_calls")

            if fe is not None and captured > 0 and last_sha and fe_sha and last_sha != fe_sha:
                reasons.append("last_rng_call_sha_differs_from_frame_end")

            if reasons:
                alerts.append(
                    {
                        "segment": seg_name,
                        "movieFrame": mf,
                        "reasons": reasons,
                        "capturedCalls": captured,
                        "csvRngDelta": csv_delta,
                        "untrackedDelta": untracked_delta,
                        "frameStartRngSha256": fs_sha or None,
                        "lastCallRngSha256": last_sha or None,
                        "frameEndRngSha256": fe_sha or None,
                        "possibleSources": POSSIBLE_UNTRACKED_SOURCES,
                    }
                )
    return alerts


def analyze_rng_diag(
    *,
    diag_path: Path,
    trace_path: Path | None = None,
    baseline_segment: str = "baseline",
    restore_segment: str | None = None,
) -> dict[str, Any]:
    segments, segment_order = load_diag_jsonl(diag_path)
    trace_map = load_trace_rng_deltas(trace_path) if trace_path is not None and trace_path.exists() else None

    resolved_restore = restore_segment
    if not resolved_restore:
        resolved_restore = next((s for s in segment_order if s.startswith("s0-restore-")), None)

    base_seg = segments.get(baseline_segment)
    rest_seg = segments.get(resolved_restore) if resolved_restore else None

    first_rng_div: dict[str, Any] | None = None
    first_frame_end_div: dict[str, Any] | None = None
    first_itween_div: dict[str, Any] | None = None
    common_frames: list[int] = []

    if base_seg is not None and rest_seg is not None:
        common_frames = sorted(base_seg.diag_frames & rest_seg.diag_frames)

        for mf in common_frames:
            b_calls = base_seg.rng_calls_by_frame.get(mf, [])
            r_calls = rest_seg.rng_calls_by_frame.get(mf, [])
            common_len = min(len(b_calls), len(r_calls))

            div_idx: int | None = None
            div_kind: str | None = None
            div_detail = ""

            for idx in range(common_len):
                bc = b_calls[idx]
                rc = r_calls[idx]
                b_site = bc.get("site") or ""
                r_site = rc.get("site") or ""
                b_owner = bc.get("owner") or ""
                r_owner = rc.get("owner") or ""
                b_sha = bc.get("rngSha256") or ""
                r_sha = rc.get("rngSha256") or ""

                site_diff = b_site != r_site
                owner_diff = b_owner != r_owner
                sha_diff = b_sha != r_sha
                if site_diff or owner_diff or sha_diff:
                    div_idx = idx
                    if site_diff and owner_diff:
                        div_kind = "site_and_owner_mismatch"
                        div_detail = f"site/owner differ at call #{idx}: base=({b_site}, {b_owner}) vs restore=({r_site}, {r_owner})"
                    elif site_diff:
                        div_kind = "site_mismatch"
                        div_detail = f"site differs at call #{idx}: base='{b_site}' vs restore='{r_site}'"
                    elif owner_diff:
                        div_kind = "owner_mismatch"
                        div_detail = f"owner differs at call #{idx}: base='{b_owner}' vs restore='{r_owner}'"
                    else:
                        div_kind = "rng_sha_mismatch"
                        div_detail = f"rngSha256 differs after ({b_site}, {b_owner}) at call #{idx}"
                    break

            if div_idx is None and len(b_calls) != len(r_calls):
                div_idx = common_len
                if len(r_calls) > len(b_calls):
                    div_kind = "extra_restore_call"
                    div_detail = f"restore made extra rng-call at index #{div_idx} (base={len(b_calls)}, restore={len(r_calls)})"
                else:
                    div_kind = "missing_restore_call"
                    div_detail = f"restore missed rng-call at index #{div_idx} (base={len(b_calls)}, restore={len(r_calls)})"

            if div_idx is not None and div_kind is not None:
                b_has = div_idx < len(b_calls)
                r_has = div_idx < len(r_calls)
                b_pos = _find_call_global_index(base_seg, mf, div_idx)
                r_pos = _find_call_global_index(rest_seg, mf, div_idx)
                b_before, b_after = _slice_context(base_seg.rng_calls_all, b_pos, b_has)
                r_before, r_after = _slice_context(rest_seg.rng_calls_all, r_pos, r_has)

                b_call_summary = _summarize_rng_call(b_calls[div_idx]) if b_has else None
                r_call_summary = _summarize_rng_call(r_calls[div_idx]) if r_has else None

                first_rng_div = {
                    "movieFrame": mf,
                    "callIndexInFrame": div_idx,
                    "kind": div_kind,
                    "detail": div_detail,
                    "baselineCall": b_call_summary,
                    "restoreCall": r_call_summary,
                    "baselineStack": b_call_summary["stack"] if b_call_summary else "",
                    "restoreStack": r_call_summary["stack"] if r_call_summary else "",
                    "contextBefore": {"baseline": b_before, "restore": r_before},
                    "contextAfter": {"baseline": b_after, "restore": r_after},
                    "itweenDiff": diff_itween_for_frame(
                        mf,
                        base_seg.itween_by_frame.get(mf, []),
                        rest_seg.itween_by_frame.get(mf, []),
                    ),
                    "frameEndDiff": diff_frame_end_for_frame(
                        mf,
                        base_seg.frame_ends.get(mf),
                        rest_seg.frame_ends.get(mf),
                    ),
                }
                break

        for mf in common_frames:
            fe_diff = diff_frame_end_for_frame(mf, base_seg.frame_ends.get(mf), rest_seg.frame_ends.get(mf))
            if fe_diff is not None and not fe_diff["equal"]:
                first_frame_end_div = fe_diff
                break

        for mf in common_frames:
            itw_diff = diff_itween_for_frame(
                mf,
                base_seg.itween_by_frame.get(mf, []),
                rest_seg.itween_by_frame.get(mf, []),
            )
            if not itw_diff["equal"]:
                first_itween_div = itw_diff
                break

    hud_diff = (
        diff_hud_vessel_diag(base_seg.hud_vessel_diags, rest_seg.hud_vessel_diags)
        if base_seg is not None and rest_seg is not None
        else None
    )
    untracked_alerts = detect_untracked_rng_alerts(segments, segment_order, trace_map)

    return {
        "diagPath": str(diag_path),
        "tracePath": str(trace_path) if trace_path is not None else None,
        "baselineSegment": baseline_segment,
        "restoreSegment": resolved_restore,
        "availableSegments": segment_order,
        "baselinePresent": base_seg is not None,
        "restorePresent": rest_seg is not None,
        "firstRngDivergence": first_rng_div,
        "firstFrameEndDivergence": first_frame_end_div,
        "firstItweenDivergence": first_itween_div,
        "hudVesselDiagDiff": hud_diff,
        "untrackedRngAlerts": untracked_alerts,
        "summary": {
            "commonDiagFrameCount": len(common_frames),
            "baselineRngCallCount": len(base_seg.rng_calls_all) if base_seg else 0,
            "restoreRngCallCount": len(rest_seg.rng_calls_all) if rest_seg else 0,
            "hasRngDivergence": first_rng_div is not None,
            "hasFrameEndDivergence": first_frame_end_div is not None,
            "hasItweenDivergence": first_itween_div is not None,
            "hasHudVesselDiff": bool(hud_diff and not hud_diff.get("equal", True)),
            "untrackedRngAlertCount": len(untracked_alerts),
        },
    }


def render_divergence_markdown(report: dict[str, Any]) -> str:
    lines: list[str] = []
    lines.append("# S0 诊断 RNG / 状态分叉分析报告")
    lines.append("")
    summary = report.get("summary") or {}
    lines.append("## 1. 总览")
    lines.append("")
    lines.append("| 指标 | 值 |")
    lines.append("| --- | --- |")
    lines.append(f"| Baseline Segment | `{report.get('baselineSegment')}` (present={report.get('baselinePresent')}) |")
    lines.append(f"| Restore Segment | `{report.get('restoreSegment')}` (present={report.get('restorePresent')}) |")
    lines.append(f"| 公共诊断帧数 | {summary.get('commonDiagFrameCount', 0)} |")
    lines.append(f"| RNG 调用数 (Base / Restore) | {summary.get('baselineRngCallCount', 0)} / {summary.get('restoreRngCallCount', 0)} |")
    lines.append(f"| 存在 RNG 调用分叉 | `{summary.get('hasRngDivergence')}` |")
    lines.append(f"| 存在 Frame-End 状态分叉 | `{summary.get('hasFrameEndDivergence')}` |")
    lines.append(f"| 存在 iTween Launch 差异 | `{summary.get('hasItweenDivergence')}` |")
    lines.append(f"| 存在 HUD Vessel 差异 | `{summary.get('hasHudVesselDiff')}` |")
    lines.append(f"| 非 Assembly-CSharp 偷跑 RNG 告警数 | {summary.get('untrackedRngAlertCount', 0)} |")
    lines.append("")

    alerts = report.get("untrackedRngAlerts") or []
    lines.append("## 2. 非 `Assembly-CSharp` 偷跑 RNG 告警")
    lines.append("")
    if alerts:
        for alert in alerts:
            lines.append(
                f"- **Segment `{alert['segment']}` Frame `{alert['movieFrame']}`**: "
                f"reasons=`{', '.join(alert['reasons'])}`, "
                f"capturedCalls=`{alert['capturedCalls']}`, "
                f"csvRngDelta=`{alert.get('csvRngDelta')}`, "
                f"untrackedDelta=`{alert.get('untrackedDelta')}`, "
                f"可能来源: `{alert['possibleSources']}`"
            )
            lines.append(
                f"  - frameStart=`{str(alert.get('frameStartRngSha256'))[:12]}`, "
                f"lastCall=`{str(alert.get('lastCallRngSha256'))[:12]}`, "
                f"frameEnd=`{str(alert.get('frameEndRngSha256'))[:12]}`"
            )
    else:
        lines.append("_未检测到未捕获的 RNG 偷跑。_")
    lines.append("")

    div = report.get("firstRngDivergence")
    lines.append("## 3. 首个 `rng-call` 分叉点")
    lines.append("")
    if div:
        lines.append(f"- Movie 帧：`{div['movieFrame']}`，帧内调用索引：`#{div['callIndexInFrame']}`，类型：`{div['kind']}`")
        lines.append(f"- 说明：{div['detail']}")
        if div.get("baselineCall"):
            bc = div["baselineCall"]
            lines.append(f"- Baseline 调用：`{bc['site']}` | owner=`{bc['owner']}` | sha=`{bc['rngSha256'][:12]}` | stack=`{bc['stack']}`")
        else:
            lines.append("- Baseline 调用：`<缺失>`")
        if div.get("restoreCall"):
            rc = div["restoreCall"]
            lines.append(f"- Restore 调用：`{rc['site']}` | owner=`{rc['owner']}` | sha=`{rc['rngSha256'][:12]}` | stack=`{rc['stack']}`")
        else:
            lines.append("- Restore 调用：`<缺失>`")

        for label, key in [("前 3 条上下文 (Baseline)", ("contextBefore", "baseline")), ("后 3 条上下文 (Baseline)", ("contextAfter", "baseline")),
                           ("前 3 条上下文 (Restore)", ("contextBefore", "restore")), ("后 3 条上下文 (Restore)", ("contextAfter", "restore"))]:
            ctx = (div.get(key[0]) or {}).get(key[1]) or []
            if ctx:
                lines.append(f"- {label}：")
                for c in ctx:
                    lines.append(f"  - f={c['movieFrame']} #{c['idx']}: `{c['site']}` | `{c['owner']}` | `{str(c['rngSha256'])[:12]}`")

        itw = div.get("itweenDiff")
        if itw and not itw.get("equal", True):
            lines.append(f"- 同帧 `itween-launch` 差异（base={itw['baselineCount']} vs restore={itw['restoreCount']}）：")
            for d in itw["diffs"][:10]:
                lines.append(f"  - idx={d['index']} kind=`{d['kind']}` base=`{d.get('baseline')}` restore=`{d.get('restore')}`")

        fe = div.get("frameEndDiff")
        if fe and not fe.get("equal", True):
            lines.append("- 同帧 `frame-end` 状态差异：")
            for k, v in (fe.get("fieldDiffs") or {}).items():
                lines.append(f"  - `{k}`: base=`{v['baseline']}` vs restore=`{v['restore']}`")
            if fe.get("bouncesDiff"):
                bd = fe["bouncesDiff"]
                lines.append(f"  - `bounces`: baseCount={bd['baselineCount']} vs restoreCount={bd['restoreCount']}")
    else:
        lines.append("_在公共诊断帧的 `rng-call` 序列中未发现分叉。_")
    lines.append("")

    fe_first = report.get("firstFrameEndDivergence")
    lines.append("## 4. 首个 `frame-end` 状态分叉")
    lines.append("")
    if fe_first:
        lines.append(f"- Movie 帧：`{fe_first['movieFrame']}`")
        for k, v in (fe_first.get("fieldDiffs") or {}).items():
            lines.append(f"  - `{k}`: base=`{v['baseline']}` vs restore=`{v['restore']}`")
        if fe_first.get("bouncesDiff"):
            bd = fe_first["bouncesDiff"]
            lines.append(f"  - `bounces`: baseCount={bd['baselineCount']} vs restoreCount={bd['restoreCount']}")
    else:
        lines.append("_在公共诊断帧的 `frame-end` 中未发现差异。_")
    lines.append("")

    hud = report.get("hudVesselDiagDiff")
    lines.append("## 5. `hud-vessel-diag` 差异")
    lines.append("")
    if hud is None:
        lines.append("_未记录 `hud-vessel-diag` 事件。_")
    elif hud.get("equal"):
        lines.append("_`hud-vessel-diag` 完全一致。_")
    else:
        for entry in hud.get("entries") or []:
            if entry.get("equal"):
                continue
            lines.append(f"- 条目 #{entry['index']} (missingSide=`{entry.get('missingSide')}`):")
            for k, v in (entry.get("scalarDiffs") or {}).items():
                lines.append(f"  - `{k}`: base=`{v['baseline']}` vs restore=`{v['restore']}`")
            for fd in entry.get("fsmDiffs") or []:
                lines.append(f"  - FSM #{fd['fsmIndex']}: base=`{fd.get('baseline')}` vs restore=`{fd.get('restore')}`")
    lines.append("")

    return "\n".join(lines).rstrip() + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Diff S0 acceptance RNG diagnostic JSONL and trace CSV.")
    parser.add_argument("--diag", required=True, help="Path to acceptance-rng-diag.jsonl")
    parser.add_argument("--trace", default="", help="Optional path to acceptance-frame-trace.csv")
    parser.add_argument("--baseline-segment", default="baseline", help="Baseline segment name (default: baseline)")
    parser.add_argument("--restore-segment", default="", help="Restore segment name (default: first s0-restore-* segment)")
    parser.add_argument("--out-dir", required=True, help="Output directory for divergence-report.json and divergence-report.md")
    args = parser.parse_args(argv)

    diag_path = Path(args.diag)
    trace_path = Path(args.trace) if args.trace else None
    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    report = analyze_rng_diag(
        diag_path=diag_path,
        trace_path=trace_path,
        baseline_segment=args.baseline_segment,
        restore_segment=args.restore_segment or None,
    )
    md = render_divergence_markdown(report)

    (out_dir / "divergence-report.json").write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    (out_dir / "divergence-report.md").write_text(md, encoding="utf-8")
    summary = report["summary"]
    print(
        f"RestoreSegment={report['restoreSegment']} "
        f"HasRngDiv={summary['hasRngDivergence']} "
        f"HasFrameEndDiv={summary['hasFrameEndDivergence']} "
        f"UntrackedAlerts={summary['untrackedRngAlertCount']}"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
