import csv
import json
import shutil
import subprocess
import sys
import tempfile
import unittest
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path


def _load_module(name: str, path: Path):
    spec = spec_from_file_location(name, path)
    assert spec and spec.loader
    mod = module_from_spec(spec)
    sys.modules[spec.name] = mod
    spec.loader.exec_module(mod)
    return mod


_VERIFY_DIR = Path(__file__).resolve().parent
_COMPARE_MOD = _load_module("compare_s0_acceptance_phase2", _VERIFY_DIR / "Compare-S0Acceptance.py")
_DIFF_MOD = _load_module("diff_acceptance_rng_diag", _VERIFY_DIR / "Diff-AcceptanceRngDiag.py")


def _scene_for_frame(mf: int) -> str:
    if mf < 10:
        return "Menu_Title"
    if mf < 25:
        return "GG_Workshop"
    return "GG_Vengefly_V"


def _make_trace_row(
    segment: str,
    mf: int,
    *,
    scene: str | None = None,
    rng_sha: str | None = None,
    rng_delta: str = "3",
    tft_ms: float | None = None,
) -> dict[str, object]:
    sc = scene if scene is not None else _scene_for_frame(mf)
    rng = rng_sha if rng_sha is not None else f"sha-{mf:04d}-000000000000"
    tft = tft_ms if tft_ms is not None else (mf % 4) * 2.5
    time_double = 100.0 + mf * 0.02
    fixed_double = time_double - (tft / 1000.0)
    return {
        "segment": segment,
        "native": mf + 50,
        "movie": mf,
        "scene": sc,
        "rngSha256": rng,
        "rngRolls": str(mf * 3),
        "rngDelta": rng_delta,
        "timeDouble": repr(time_double),
        "fixedTimeDouble": repr(fixed_double),
        "tftMs": repr(tft),
    }


def _write_trace_csv(path: Path, rows: list[dict[str, object]]) -> None:
    fieldnames = [
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
    ]
    with path.open("w", encoding="utf-8", newline="") as fh:
        writer = csv.DictWriter(fh, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


def _write_runs_json(path: Path, runs: list[dict[str, object]]) -> None:
    path.write_text(
        json.dumps(
            {
                "suite": "unit",
                "baselineError": "",
                "capturedCount": len(runs),
                "qualifiedCount": len(runs),
                "runs": runs,
            },
            indent=2,
        ),
        encoding="utf-8",
    )


def _write_jsonl(path: Path, events: list[dict[str, object]]) -> None:
    path.write_text(
        "\n".join(json.dumps(ev) for ev in events) + "\n",
        encoding="utf-8",
    )


def _rng_call(
    segment: str,
    mf: int,
    idx: int,
    *,
    site: str = "UnityEngine.Random.Range(float,float)",
    owner: str = "Vengefly(Clone)",
    rng_sha: str | None = None,
    stack: str = "EnemyAI.Update <- PlayMakerFSM.Update",
) -> dict[str, object]:
    return {
        "segment": segment,
        "eventType": "rng-call",
        "idx": idx,
        "nativeFrame": mf + 50,
        "movieFrame": mf,
        "frameInputEnabled": True,
        "engineFrame": mf + 100,
        "site": site,
        "owner": owner,
        "rngSha256": rng_sha or f"sha-{mf}-{idx}",
        "stack": stack,
    }


def _frame_start(segment: str, mf: int, scene: str = "GG_Vengefly_V", rng_sha: str = "start-sha") -> dict[str, object]:
    return {
        "segment": segment,
        "eventType": "frame-start",
        "nativeFrame": mf + 50,
        "movieFrame": mf,
        "scene": scene,
        "rngSha256": rng_sha,
    }


def _frame_end(
    segment: str,
    mf: int,
    *,
    scene: str = "GG_Vengefly_V",
    rng_sha: str = "end-sha",
    hx: str = "10.0",
    hy: str = "5.0",
    vx: str = "0.0",
    vy: str = "0.0",
    c_state: str = "g=1,d=0",
    bounces: list[dict[str, object]] | None = None,
) -> dict[str, object]:
    return {
        "segment": segment,
        "eventType": "frame-end",
        "nativeFrame": mf + 50,
        "movieFrame": mf,
        "scene": scene,
        "rngSha256": rng_sha,
        "hx": hx,
        "hy": hy,
        "bx": hx,
        "by": hy,
        "vx": vx,
        "vy": vy,
        "cState": c_state,
        "bounces": bounces or [],
    }


class CompareFirstDivergenceTests(unittest.TestCase):
    def test_pass_sets_first_divergence_none_and_failures_record_rng_tft_scene(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"
            out_dir = tmp_dir / "out"

            base_rows = [_make_trace_row("baseline", mf) for mf in range(40)]
            _write_trace_csv(trace, base_rows)
            parsed_base = _COMPARE_MOD.load_trace_segments(trace)["baseline"]
            sampled = _COMPARE_MOD.sample_scene_frames(_COMPARE_MOD.extract_scene_intervals(parsed_base), seed=77)
            mid_workshop = next(s for s in sampled if s.scene == "GG_Workshop" and s.stage == "mid")
            late_vengefly = next(s for s in sampled if s.scene == "GG_Vengefly_V" and s.stage == "late")

            all_rows = list(base_rows)
            # cp_ok: exact match
            all_rows.extend(_make_trace_row("s0-restore-1:cp_ok", mf) for mf in range(10, 40))
            # cp_rng: diverges at frame 12 (earlier than mid sample if mid_workshop > 12) and at mid_workshop
            for mf in range(10, 40):
                bad_sha = "diverged-rng" if mf in (12, mid_workshop.movie_frame) else None
                delta = "9" if mf == 12 else "3"
                all_rows.append(_make_trace_row("s0-restore-2:cp_rng", mf, rng_sha=bad_sha, rng_delta=delta))
            # cp_tft: diverges at late_vengefly tft
            for mf in range(10, 40):
                bad_tft = 19.5 if mf == late_vengefly.movie_frame else None
                all_rows.append(_make_trace_row("s0-restore-3:cp_tft", mf, tft_ms=bad_tft))
            # cp_scene: shifts transition 25 -> 26
            for mf in range(10, 40):
                sc = "GG_Workshop" if mf == 25 else None
                all_rows.append(_make_trace_row("s0-restore-4:cp_scene", mf, scene=sc))
            # cp_both: rng and tft both diverge at frame 14 and at mid_workshop
            for mf in range(10, 40):
                bad_sha = "both-rng" if mf in (14, mid_workshop.movie_frame) else None
                bad_tft = 8.88 if mf in (14, mid_workshop.movie_frame) else None
                all_rows.append(_make_trace_row("s0-restore-5:cp_both", mf, rng_sha=bad_sha, tft_ms=bad_tft))

            _write_trace_csv(trace, all_rows)
            _write_runs_json(
                runs,
                [
                    {"Index": 1, "SegmentName": "s0-restore-1:cp_ok", "CheckpointId": "cp_ok", "TransitionId": 1, "ResumeMovieFrame": 10, "TargetMovieFrame": 40, "Succeeded": True},
                    {"Index": 2, "SegmentName": "s0-restore-2:cp_rng", "CheckpointId": "cp_rng", "TransitionId": 1, "ResumeMovieFrame": 10, "TargetMovieFrame": 40, "Succeeded": True},
                    {"Index": 3, "SegmentName": "s0-restore-3:cp_tft", "CheckpointId": "cp_tft", "TransitionId": 1, "ResumeMovieFrame": 10, "TargetMovieFrame": 40, "Succeeded": True},
                    {"Index": 4, "SegmentName": "s0-restore-4:cp_scene", "CheckpointId": "cp_scene", "TransitionId": 1, "ResumeMovieFrame": 10, "TargetMovieFrame": 40, "Succeeded": True},
                    {"Index": 5, "SegmentName": "s0-restore-5:cp_both", "CheckpointId": "cp_both", "TransitionId": 1, "ResumeMovieFrame": 10, "TargetMovieFrame": 40, "Succeeded": True},
                ],
            )

            exit_code = _COMPARE_MOD.main(
                ["--trace", str(trace), "--runs", str(runs), "--suite", "unit", "--seed", "77", "--out-dir", str(out_dir)]
            )
            self.assertEqual(1, exit_code)
            report = json.loads((out_dir / "report.json").read_text(encoding="utf-8"))
            summary = (out_dir / "summary.md").read_text(encoding="utf-8")

            r_ok, r_rng, r_tft, r_scene, r_both = report["s0Results"]
            self.assertTrue(r_ok["passed"])
            self.assertIsNone(r_ok["firstDivergence"])

            self.assertFalse(r_rng["passed"])
            self.assertEqual("sample_rng_mismatch", r_rng["reason"])
            self.assertEqual("rng", r_rng["firstDivergence"]["kind"])
            self.assertEqual(min(12, mid_workshop.movie_frame), r_rng["firstDivergence"]["movieFrame"])
            if r_rng["firstDivergence"]["movieFrame"] == 12:
                self.assertEqual("9", r_rng["firstDivergence"]["restoreRngDelta"])
                self.assertEqual("3", r_rng["firstDivergence"]["baselineRngDelta"])

            self.assertFalse(r_tft["passed"])
            self.assertEqual("sample_tft_mismatch", r_tft["reason"])
            self.assertEqual("tft", r_tft["firstDivergence"]["kind"])
            self.assertEqual(late_vengefly.movie_frame, r_tft["firstDivergence"]["movieFrame"])

            self.assertFalse(r_scene["passed"])
            self.assertEqual("scene_transition_mismatch", r_scene["reason"])
            self.assertEqual("scene", r_scene["firstDivergence"]["kind"])
            self.assertEqual(25, r_scene["firstDivergence"]["movieFrame"])
            self.assertEqual("GG_Vengefly_V", r_scene["firstDivergence"]["baselineScene"])
            self.assertEqual("GG_Workshop", r_scene["firstDivergence"]["scene"])

            self.assertFalse(r_both["passed"])
            self.assertEqual("rng_and_tft", r_both["firstDivergence"]["kind"])

            self.assertIn("首帧分叉", summary)
            self.assertIn("kind=`rng`", summary)
            self.assertIn("kind=`tft`", summary)
            self.assertIn("kind=`scene`", summary)


class DiffAcceptanceRngDiagTests(unittest.TestCase):
    def test_extra_and_missing_rng_call_with_context_and_frame_end_diff(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            diag_path = tmp_dir / "acceptance-rng-diag.jsonl"
            out_dir = tmp_dir / "diff_out"

            events = [
                # Baseline: frames 100..102, 2 calls per frame = 6 calls
                _frame_start("baseline", 100, rng_sha="s100"),
                _rng_call("baseline", 100, 1, rng_sha="sha-100-1"),
                _rng_call("baseline", 100, 2, rng_sha="sha-100-2"),
                _frame_end("baseline", 100, rng_sha="sha-100-2"),
                _frame_start("baseline", 101, rng_sha="sha-100-2"),
                _rng_call("baseline", 101, 1, rng_sha="sha-101-1"),
                _rng_call("baseline", 101, 2, rng_sha="sha-101-2"),
                {
                    "segment": "baseline",
                    "eventType": "itween-launch",
                    "movieFrame": 101,
                    "target": "Hud Canvas/Soul Orb/Vessels/Vessel 1",
                    "hasId": False,
                    "type": "ScaleTo",
                    "method": "Vector3",
                    "name": "pop",
                    "stack": "   at System.Environment.get_StackTrace()\n   at HutongGames.iTween.Launch()\n   at PlayMakerFSM.OnEnter()",
                },
                _frame_end(
                    "baseline",
                    101,
                    rng_sha="sha-101-2",
                    hx="10.0",
                    hy="5.0",
                    bounces=[{"n": "Debris(Clone)", "x": "1.0", "y": "2.0", "vx": "0.5", "vy": "1.0"}],
                ),
                _frame_start("baseline", 102, rng_sha="sha-101-2"),
                _rng_call("baseline", 102, 1, rng_sha="sha-102-1"),
                _frame_end("baseline", 102, rng_sha="sha-102-1"),
                # Restore: frame 100 matches; frame 101 has 1 extra call at index 2 + itween + frame-end diff
                _frame_start("s0-restore-1:cp1", 100, rng_sha="s100"),
                _rng_call("s0-restore-1:cp1", 100, 1, rng_sha="sha-100-1"),
                _rng_call("s0-restore-1:cp1", 100, 2, rng_sha="sha-100-2"),
                _frame_end("s0-restore-1:cp1", 100, rng_sha="sha-100-2"),
                _frame_start("s0-restore-1:cp1", 101, rng_sha="sha-100-2"),
                _rng_call("s0-restore-1:cp1", 101, 1, rng_sha="sha-101-1"),
                _rng_call("s0-restore-1:cp1", 101, 2, rng_sha="sha-101-2"),
                _rng_call("s0-restore-1:cp1", 101, 3, site="UnityEngine.Random.value", owner="ExtraFX", rng_sha="sha-101-3"),
                _frame_end(
                    "s0-restore-1:cp1",
                    101,
                    rng_sha="sha-101-3",
                    hx="10.25",
                    hy="5.0",
                    c_state="g=0,d=1",
                    bounces=[{"n": "Debris(Clone)", "x": "1.5", "y": "2.0", "vx": "0.5", "vy": "1.0"}],
                ),
            ]
            _write_jsonl(diag_path, events)

            exit_code = _DIFF_MOD.main(["--diag", str(diag_path), "--out-dir", str(out_dir)])
            self.assertEqual(0, exit_code)

            report = json.loads((out_dir / "divergence-report.json").read_text(encoding="utf-8"))
            self.assertEqual("s0-restore-1:cp1", report["restoreSegment"])
            div = report["firstRngDivergence"]
            self.assertIsNotNone(div)
            self.assertEqual(101, div["movieFrame"])
            self.assertEqual("extra_restore_call", div["kind"])
            self.assertEqual(2, div["callIndexInFrame"])
            self.assertIsNone(div["baselineCall"])
            self.assertEqual("ExtraFX", div["restoreCall"]["owner"])
            self.assertEqual(3, len(div["contextBefore"]["restore"]))
            self.assertEqual(1, len(div["contextAfter"]["baseline"]))
            self.assertFalse(div["itweenDiff"]["equal"])
            self.assertFalse(div["frameEndDiff"]["equal"])
            self.assertIn("hx", div["frameEndDiff"]["fieldDiffs"])
            self.assertIn("cState", div["frameEndDiff"]["fieldDiffs"])
            self.assertIsNotNone(div["frameEndDiff"]["bouncesDiff"])

            # Now swap baseline/restore roles to verify missing_restore_call
            rev_report = _DIFF_MOD.analyze_rng_diag(
                diag_path=diag_path,
                baseline_segment="s0-restore-1:cp1",
                restore_segment="baseline",
            )
            self.assertEqual("missing_restore_call", rev_report["firstRngDivergence"]["kind"])

    def test_site_owner_mismatch_and_hud_vessel_diff(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            diag_path = tmp_dir / "acceptance-rng-diag.jsonl"
            events = [
                {
                    "segment": "baseline",
                    "eventType": "hud-vessel-diag",
                    "mpCharge": 99,
                    "mpReserve": 33,
                    "mpReserveMax": 99,
                    "soulLimited": False,
                    "inBossSequence": True,
                    "boundSoul": False,
                    "vesselScale": "(1, 1, 1)",
                    "fsms": [
                        {
                            "fsmName": "Vessel Drain",
                            "activeStateName": "Idle",
                            "previousActiveState": "Init",
                            "initialized": True,
                            "started": True,
                            "intVars": {"MP": 33},
                            "boolVars": {"Active": True},
                            "states": [{"name": "Idle", "transitions": [], "actions": []}],
                        }
                    ],
                },
                {
                    "segment": "s0-restore-1:cp1",
                    "eventType": "hud-vessel-diag",
                    "mpCharge": 99,
                    "mpReserve": 0,
                    "mpReserveMax": 99,
                    "soulLimited": False,
                    "inBossSequence": True,
                    "boundSoul": False,
                    "vesselScale": "(1, 1, 1)",
                    "fsms": [
                        {
                            "fsmName": "Vessel Drain",
                            "activeStateName": "Idle",
                            "previousActiveState": "Init",
                            "initialized": True,
                            "started": True,
                            "intVars": {"MP": 33},
                            "boolVars": {"Active": True},
                            "states": None,
                        }
                    ],
                },
                _frame_start("baseline", 50, rng_sha="s50"),
                _rng_call("baseline", 50, 1, site="Random.Range(int,int)", owner="BossA", rng_sha="h1"),
                _frame_end("baseline", 50, rng_sha="h1"),
                _frame_start("s0-restore-1:cp1", 50, rng_sha="s50"),
                _rng_call("s0-restore-1:cp1", 50, 1, site="Random.value", owner="BossB", rng_sha="h2"),
                _frame_end("s0-restore-1:cp1", 50, rng_sha="h2"),
            ]
            _write_jsonl(diag_path, events)

            report = _DIFF_MOD.analyze_rng_diag(diag_path=diag_path)
            self.assertEqual("site_and_owner_mismatch", report["firstRngDivergence"]["kind"])
            self.assertFalse(report["hudVesselDiagDiff"]["equal"])
            self.assertIn("mpReserve", report["hudVesselDiagDiff"]["entries"][0]["scalarDiffs"])
            self.assertEqual([], report["hudVesselDiagDiff"]["entries"][0]["fsmDiffs"])

            # Verify owner-only mismatch
            events[6]["site"] = "Random.Range(int,int)"
            _write_jsonl(diag_path, events)
            report_owner = _DIFF_MOD.analyze_rng_diag(diag_path=diag_path)
            self.assertEqual("owner_mismatch", report_owner["firstRngDivergence"]["kind"])

    def test_untracked_rng_alerts_from_trace_and_frame_start_end_without_trace(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            diag_path = tmp_dir / "acceptance-rng-diag.jsonl"
            trace_path = tmp_dir / "acceptance-frame-trace.csv"

            # Frame 200: 0 rng-calls, frame-start != frame-end -> alert even without --trace
            # Frame 201: 1 rng-call ending in sha-201-1, frame-end is sha-201-after -> alert even without --trace
            # Frame 202: 1 rng-call ending in sha-202-1 == frame-end, but CSV rngDelta=4 -> untrackedDelta=3 with --trace
            events = [
                _frame_start("baseline", 200, rng_sha="sha-200-start"),
                _frame_end("baseline", 200, rng_sha="sha-200-end"),
                _frame_start("baseline", 201, rng_sha="sha-200-end"),
                _rng_call("baseline", 201, 1, rng_sha="sha-201-1"),
                _frame_end("baseline", 201, rng_sha="sha-201-after"),
                _frame_start("s0-restore-1:cp1", 202, rng_sha="sha-202-start"),
                _rng_call("s0-restore-1:cp1", 202, 1, rng_sha="sha-202-1"),
                _frame_end("s0-restore-1:cp1", 202, rng_sha="sha-202-1"),
            ]
            _write_jsonl(diag_path, events)

            # Without --trace: frames 200 and 201 must alert
            report_no_trace = _DIFF_MOD.analyze_rng_diag(diag_path=diag_path, trace_path=None)
            alerts_no_trace = report_no_trace["untrackedRngAlerts"]
            self.assertEqual([200, 201], [a["movieFrame"] for a in alerts_no_trace])
            self.assertIn("frame_start_end_rng_changed_with_zero_calls", alerts_no_trace[0]["reasons"])
            self.assertIn("last_rng_call_sha_differs_from_frame_end", alerts_no_trace[1]["reasons"])
            self.assertIn("PlayMaker.dll", alerts_no_trace[0]["possibleSources"])

            # With --trace (include an untraced frame 199 with rngDelta=5 to confirm no false positive)
            trace_rows = [
                _make_trace_row("baseline", 199, rng_delta="5"),
                _make_trace_row("baseline", 200, rng_delta="2"),
                _make_trace_row("baseline", 201, rng_delta="2"),
                _make_trace_row("s0-restore-1:cp1", 202, rng_delta="4"),
            ]
            _write_trace_csv(trace_path, trace_rows)

            report_with_trace = _DIFF_MOD.analyze_rng_diag(diag_path=diag_path, trace_path=trace_path)
            alerts_with_trace = report_with_trace["untrackedRngAlerts"]
            self.assertEqual([200, 201, 202], [a["movieFrame"] for a in alerts_with_trace])
            self.assertEqual(2, alerts_with_trace[0]["untrackedDelta"])
            self.assertEqual(1, alerts_with_trace[1]["untrackedDelta"])
            self.assertEqual(3, alerts_with_trace[2]["untrackedDelta"])
            self.assertEqual(4, alerts_with_trace[2]["csvRngDelta"])
            self.assertEqual(1, alerts_with_trace[2]["capturedCalls"])

    def test_ps1_diff_only_end_to_end_offline(self) -> None:
        pwsh = shutil.which("pwsh")
        if not pwsh:
            self.skipTest("pwsh not available on PATH")

        with tempfile.TemporaryDirectory() as tmp:
            acc_dir = Path(tmp)
            suite_dir = acc_dir / "unit_suite"
            suite_dir.mkdir(parents=True, exist_ok=True)
            out_dir = acc_dir / "diag_out"

            (acc_dir / "report.json").write_text(
                json.dumps(
                    {
                        "passed": False,
                        "suites": [
                            {
                                "suite": "unit_suite",
                                "passed": False,
                                "reportPath": str(suite_dir / "report.json"),
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            (suite_dir / "report.json").write_text(
                json.dumps(
                    {
                        "suite": "unit_suite",
                        "passed": False,
                        "s0Results": [
                            {
                                "checkpointId": "cp1",
                                "segmentName": "s0-restore-1:cp1",
                                "passed": False,
                                "firstDivergence": {
                                    "movieFrame": 100,
                                    "scene": "GG_Vengefly_V",
                                    "baselineScene": "GG_Vengefly_V",
                                    "kind": "rng",
                                },
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )
            _write_jsonl(
                suite_dir / "acceptance-rng-diag.jsonl",
                [
                    _frame_start("baseline", 100, rng_sha="s100"),
                    _rng_call("baseline", 100, 1, rng_sha="b1"),
                    _frame_end("baseline", 100, rng_sha="b1"),
                    _frame_start("s0-restore-1:cp1", 100, rng_sha="s100"),
                    _rng_call("s0-restore-1:cp1", 100, 1, rng_sha="r1"),
                    _frame_end("s0-restore-1:cp1", 100, rng_sha="r1"),
                ],
            )
            _write_trace_csv(
                suite_dir / "acceptance-frame-trace.csv",
                [
                    _make_trace_row("baseline", 100, rng_delta="1"),
                    _make_trace_row("s0-restore-1:cp1", 100, rng_delta="2"),
                ],
            )

            ps1_path = _VERIFY_DIR / "Invoke-S0DivergenceDiagnose.ps1"
            proc = subprocess.run(
                [
                    pwsh,
                    "-NoProfile",
                    "-File",
                    str(ps1_path),
                    "-AcceptanceDir",
                    str(acc_dir),
                    "-OutputDirectory",
                    str(out_dir),
                    "-DiffOnly",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, proc.returncode, msg=proc.stdout + "\n" + proc.stderr)
            self.assertTrue((out_dir / "divergence-report.json").is_file())
            self.assertTrue((out_dir / "divergence-report.md").is_file())
            diff_report = json.loads((out_dir / "divergence-report.json").read_text(encoding="utf-8"))
            self.assertEqual("rng_sha_mismatch", diff_report["firstRngDivergence"]["kind"])
            self.assertEqual(1, diff_report["summary"]["untrackedRngAlertCount"])


if __name__ == "__main__":
    unittest.main()
