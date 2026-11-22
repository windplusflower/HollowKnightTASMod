import csv
import json
import sys
import tempfile
import unittest
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path

_MOD_PATH = Path(__file__).resolve().parents[1] / "Compare-S0Acceptance.py"
_SPEC = spec_from_file_location("compare_s0_acceptance", _MOD_PATH)
assert _SPEC and _SPEC.loader
_MOD = module_from_spec(_SPEC)
sys.modules[_SPEC.name] = _MOD
_SPEC.loader.exec_module(_MOD)


def _scene_for_frame(mf: int) -> str:
    if mf < 10:
        return "Menu_Title"
    if mf < 25:
        return "GG_Workshop"
    if mf < 27:
        return "GG_Transition_Short"
    return "GG_Vengefly_V"


def _make_row(
    segment: str,
    mf: int,
    *,
    scene: str | None = None,
    rng_sha: str | None = None,
    tft_ms: float | None = None,
) -> dict[str, object]:
    sc = scene if scene is not None else _scene_for_frame(mf)
    rng = rng_sha if rng_sha is not None else f"sha-{mf:04d}-abcdef012345"
    tft = tft_ms if tft_ms is not None else (mf % 5) * 2.5
    time_double = 100.0 + mf * 0.02
    fixed_double = time_double - (tft / 1000.0)
    return {
        "segment": segment,
        "native": mf + 50,
        "movie": mf,
        "scene": sc,
        "rngSha256": rng,
        "rngRolls": str(mf * 3),
        "rngDelta": "3",
        "timeDouble": repr(time_double),
        "fixedTimeDouble": repr(fixed_double),
        "tftMs": repr(tft),
    }


def _write_csv(path: Path, rows: list[dict[str, object]]) -> None:
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


def _write_runs(path: Path, runs: list[dict[str, object]], baseline_error: str = "") -> None:
    path.write_text(
        json.dumps(
            {
                "suite": "unit",
                "baselineError": baseline_error,
                "capturedCount": len(runs),
                "qualifiedCount": len(runs),
                "runs": runs,
            },
            indent=2,
        ),
        encoding="utf-8",
    )


class CompareS0AcceptanceTests(unittest.TestCase):
    def test_exact_match_passes_with_short_scene_and_deterministic_seed(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            rows.extend(_make_row("s0-restore-1:cp1", mf) for mf in range(10, 40))
            rows.extend(_make_row("s0-restore-2:cp2", mf) for mf in range(25, 40))
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                        "Error": "",
                        "ElapsedMs": 120.0,
                    },
                    {
                        "Index": 2,
                        "SegmentName": "s0-restore-2:cp2",
                        "CheckpointId": "cp2",
                        "TransitionId": 2,
                        "ResumeMovieFrame": 25,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                        "Error": "",
                        "ElapsedMs": 95.0,
                    },
                ],
            )

            report1 = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=20261122,
            )
            report2 = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=20261122,
            )

            self.assertTrue(report1["passed"])
            self.assertEqual("ok", report1["reason"])
            self.assertEqual(report1["sampledFrames"], report2["sampledFrames"])

            short_samples = [s for s in report1["sampledFrames"] if s["scene"] == "GG_Transition_Short"]
            self.assertEqual(2, len(short_samples))
            self.assertEqual(["short_scene_0", "short_scene_1"], [s["stage"] for s in short_samples])

    def test_scene_transition_frame_shift_by_one_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            for mf in range(10, 40):
                # Shift transition from 25 -> 26 by one frame
                scene = "GG_Workshop" if mf == 25 else None
                rows.append(_make_row("s0-restore-1:cp1", mf, scene=scene))
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                    }
                ],
            )

            report = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=7,
            )
            self.assertFalse(report["passed"])
            self.assertEqual("scene_transition_mismatch", report["reason"])
            diffs = report["s0Results"][0]["transitionCheck"]["diffs"]
            self.assertTrue(len(diffs) >= 1)
            self.assertEqual(25, diffs[0]["baseline"]["movieFrame"])
            self.assertEqual(26, diffs[0]["s0"]["movieFrame"])

    def test_mid_scene_rng_mismatch_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            _write_csv(trace, rows)
            base_rows = _MOD.load_trace_segments(trace)["baseline"]
            base_intervals = _MOD.extract_scene_intervals(base_rows)
            sampled = _MOD.sample_scene_frames(base_intervals, seed=99)
            mid_sample = next(s for s in sampled if s.scene == "GG_Workshop" and s.stage == "mid")

            for mf in range(10, 40):
                bad_rng = "sha-diverged-rng" if mf == mid_sample.movie_frame else None
                rows.append(_make_row("s0-restore-1:cp1", mf, rng_sha=bad_rng))
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                    }
                ],
            )

            report = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=99,
            )
            self.assertFalse(report["passed"])
            self.assertEqual("sample_rng_mismatch", report["reason"])
            failures = report["s0Results"][0]["sampleFailures"]
            self.assertEqual(1, len(failures))
            self.assertEqual(mid_sample.movie_frame, failures[0]["movieFrame"])
            self.assertFalse(failures[0]["rngMatch"])
            self.assertTrue(failures[0]["tftMatch"])

    def test_late_scene_tft_mismatch_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            _write_csv(trace, rows)
            base_rows = _MOD.load_trace_segments(trace)["baseline"]
            base_intervals = _MOD.extract_scene_intervals(base_rows)
            sampled = _MOD.sample_scene_frames(base_intervals, seed=1234)
            late_sample = next(s for s in sampled if s.scene == "GG_Vengefly_V" and s.stage == "late")

            for mf in range(10, 40):
                bad_tft = 19.999 if mf == late_sample.movie_frame else None
                rows.append(_make_row("s0-restore-1:cp1", mf, tft_ms=bad_tft))
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                    }
                ],
            )

            report = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=1234,
            )
            self.assertFalse(report["passed"])
            self.assertEqual("sample_tft_mismatch", report["reason"])
            failures = report["s0Results"][0]["sampleFailures"]
            self.assertEqual(1, len(failures))
            self.assertEqual(late_sample.movie_frame, failures[0]["movieFrame"])
            self.assertTrue(failures[0]["rngMatch"])
            self.assertFalse(failures[0]["tftMatch"])

    def test_failed_s0_restore_produces_decision_report(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"
            out_dir = tmp_dir / "out"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": False,
                        "Error": "LeanS0 target boundary mismatch",
                    }
                ],
            )

            code = _MOD.main(
                [
                    "--trace",
                    str(trace),
                    "--runs",
                    str(runs),
                    "--suite",
                    "tas_fail_test",
                    "--seed",
                    "42",
                    "--out-dir",
                    str(out_dir),
                ]
            )
            self.assertEqual(1, code)
            summary = (out_dir / "summary.md").read_text(encoding="utf-8")
            self.assertIn("本实机验收未通过不代表代码必然存在问题", summary)
            self.assertIn("LeanS0 target boundary mismatch", summary)

    def test_fallback_segment_lookup_after_prior_restore_failure(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "trace.csv"
            runs = tmp_dir / "s0-runs.json"

            rows = [_make_row("baseline", mf) for mf in range(40)]
            # Simulate Runtime incrementing segment counter to 1 for cp2 after cp1 failed before restore-apply
            rows.extend(_make_row("s0-restore-1:cp2", mf) for mf in range(25, 40))
            _write_csv(trace, rows)
            _write_runs(
                runs,
                [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 40,
                        "Succeeded": False,
                        "Error": "StartRestore failed",
                    },
                    {
                        "Index": 2,
                        "SegmentName": "s0-restore-2:cp2",
                        "CheckpointId": "cp2",
                        "TransitionId": 2,
                        "ResumeMovieFrame": 25,
                        "TargetMovieFrame": 40,
                        "Succeeded": True,
                    },
                ],
            )

            report = _MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs,
                suite_name="unit",
                seed=11,
            )
            self.assertFalse(report["passed"])
            self.assertFalse(report["s0Results"][0]["passed"])
            self.assertTrue(report["s0Results"][1]["passed"])
            self.assertEqual("s0-restore-1:cp2", report["s0Results"][1]["resolvedSegmentName"])


if __name__ == "__main__":
    unittest.main()
