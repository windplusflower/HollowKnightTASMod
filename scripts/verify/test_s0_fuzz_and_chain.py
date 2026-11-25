import csv
import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
import zipfile
from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
_FUZZ_PATH = REPO / "scripts" / "verify" / "New-FuzzTasPackage.py"
_COMPARE_PATH = REPO / "scripts" / "verify" / "Compare-S0Acceptance.py"


def _load_module(name: str, path: Path):
    spec = spec_from_file_location(name, path)
    assert spec and spec.loader
    mod = module_from_spec(spec)
    sys.modules[spec.name] = mod
    spec.loader.exec_module(mod)
    return mod


_FUZZ_MOD = _load_module("new_fuzz_tas_package", _FUZZ_PATH)
_COMPARE_MOD = _load_module("compare_s0_acceptance_chain", _COMPARE_PATH)


def _compact(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))


def _expand_movie_frames(movie_text: str) -> list[dict[str, object]]:
    lines = [line for line in movie_text.splitlines() if line.strip()]
    frames: list[dict[str, object]] = []
    for line in lines[1:]:
        run = json.loads(line)
        repeat = int(run["repeatCount"])
        samples = run["samples"]
        for idx in range(repeat):
            # Only the first frame of a repeat block carries edge masks.
            if idx == 0:
                frames.append({"fps": run.get("fps", 50), "samples": samples})
            else:
                held_samples = []
                for s in samples:
                    s_copy = dict(s)
                    s_copy["pressedMask"] = 0
                    s_copy["releasedMask"] = 0
                    held_samples.append(s_copy)
                frames.append({"fps": run.get("fps", 50), "samples": held_samples})
    return frames


def _build_minimal_base_package(path: Path) -> tuple[dict[str, object], dict[str, bytes], str]:
    header = {
        "format": "hktas",
        "version": 2,
        "tickUnit": "input-playerloop",
        "actionSchemaId": "hktas-full-run-actions-v2",
        "nativeProfileId": "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v15-experimental",
        "mouseEnabled": False,
        "gameVersion": "1.5.78.11833",
        "apiVersion": "78",
        "modVersion": "0.1.0",
        "environmentSha256": "none",
        "viewportWidth": 1920,
        "viewportHeight": 1080,
    }
    actions = _FUZZ_MOD.HERO_ACTIONS
    submit_idx = actions.index("menuSubmit")
    right_idx = actions.index("right")
    jump_idx = actions.index("jump")

    def hero_sample(active_indices: tuple[int, ...], pressed: int, released: int) -> dict[str, object]:
        values = [32767 if i in active_indices else 0 for i in range(len(actions))]
        return {
            "channel": "hero",
            "values": values,
            "pressedMask": pressed,
            "releasedMask": released,
            "mouse": None,
        }

    runs = [
        {"repeatCount": 10, "fps": 50, "authored": True, "samples": []},
        {
            "repeatCount": 1,
            "fps": 50,
            "authored": True,
            "samples": [hero_sample((submit_idx,), 1 << submit_idx, 0)],
        },
        {
            "repeatCount": 1,
            "fps": 50,
            "authored": True,
            "samples": [hero_sample((), 0, 1 << submit_idx)],
        },
        {
            "repeatCount": 1,
            "fps": 50,
            "authored": True,
            "samples": [hero_sample((right_idx, jump_idx), (1 << right_idx) | (1 << jump_idx), 0)],
        },
        {
            "repeatCount": 17,
            "fps": 50,
            "authored": True,
            "samples": [hero_sample((right_idx, jump_idx), 0, 0)],
        },
    ]
    movie_text = "\n".join([_compact(header)] + [_compact(r) for r in runs]) + "\n"
    movie_bytes = movie_text.encode("utf-8")

    save_files = {
        "user1.dat": b"minimal-synthetic-save-slot-1",
    }
    save_hashes = {
        name: hashlib.sha256(content).hexdigest()
        for name, content in sorted(save_files.items())
    }
    initial_saves_id = hashlib.sha256(_compact(save_hashes).encode("utf-8")).hexdigest()
    manifest = {
        "Format": "HK-TAS-Sequence",
        "Version": 1,
        "Slots": [1, 2, 3, 4],
        "InitialSavesId": initial_saves_id,
        "MovieSha256": hashlib.sha256(movie_bytes).hexdigest(),
        "Files": [
            {
                "Name": name,
                "Length": len(save_files[name]),
                "Sha256": save_hashes[name],
            }
            for name in sorted(save_files.keys())
        ],
    }
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for name, content in save_files.items():
            zf.writestr(f"initial-saves/{name}", content)
        zf.writestr("movie.hktas", movie_bytes)
        zf.writestr("manifest.json", _compact(manifest))
    return manifest, save_files, movie_text


def _validate_with_cli(movie_path: Path) -> str:
    cli_dll = REPO / "src" / "HollowKnightTAS.Cli" / "bin" / "Release" / "net8.0" / "HollowKnightTAS.Cli.dll"
    if cli_dll.is_file():
        cmd = ["dotnet", str(cli_dll), "movie", "validate", str(movie_path)]
    else:
        cli_proj = REPO / "src" / "HollowKnightTAS.Cli" / "HollowKnightTAS.Cli.csproj"
        cmd = ["dotnet", "run", "--project", str(cli_proj), "-c", "Release", "--", "movie", "validate", str(movie_path)]
    proc = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        raise AssertionError(
            f"HollowKnightTAS.Cli movie validate failed (code={proc.returncode}):\n"
            f"stdout={proc.stdout}\nstderr={proc.stderr}"
        )
    return proc.stdout.strip()


def _make_trace_row(
    segment: str,
    mf: int,
    *,
    scene: str | None = None,
    rng_sha: str | None = None,
    tft_ms: float | None = None,
) -> dict[str, object]:
    if scene is None:
        if mf < 10:
            scene = "Menu_Title"
        elif mf < 25:
            scene = "Town"
        else:
            scene = "Crossroads_01"
    rng = rng_sha if rng_sha is not None else f"rng-{mf:04d}-deadbeef"
    tft = tft_ms if tft_ms is not None else (mf % 4) * 3.125
    time_double = 50.0 + mf * 0.02
    fixed_double = time_double - (tft / 1000.0)
    return {
        "segment": segment,
        "native": mf + 20,
        "movie": mf,
        "scene": scene,
        "rngSha256": rng,
        "rngRolls": str(mf * 2),
        "rngDelta": "2",
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


class FuzzPackageAndChainRestoreTests(unittest.TestCase):
    def test_fuzz_package_determinism_prefix_profiles_and_cli_validation(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            base_pkg = tmp_dir / "base.hktaspack"
            base_manifest, base_saves, base_movie_text = _build_minimal_base_package(base_pkg)
            base_expanded = _expand_movie_frames(base_movie_text)

            # 1. Same seed => identical movie.hktas & MovieSha256
            out_a = tmp_dir / "fuzz_explore_a.hktaspack"
            out_b = tmp_dir / "fuzz_explore_b.hktaspack"
            res_a = _FUZZ_MOD.build_fuzz_package(
                base_package=base_pkg,
                out_package=out_a,
                seed=777,
                prefix_frames=20,
                fuzz_frames=180,
                profile="explore",
            )
            res_b = _FUZZ_MOD.build_fuzz_package(
                base_package=base_pkg,
                out_package=out_b,
                seed=777,
                prefix_frames=20,
                fuzz_frames=180,
                profile="explore",
            )
            self.assertEqual(res_a["movieSha256"], res_b["movieSha256"])

            with zipfile.ZipFile(out_a, "r") as za, zipfile.ZipFile(out_b, "r") as zb:
                self.assertEqual(za.read("movie.hktas"), zb.read("movie.hktas"))

            # 2. Different seed => different movie.hktas & MovieSha256
            out_c = tmp_dir / "fuzz_explore_c.hktaspack"
            res_c = _FUZZ_MOD.build_fuzz_package(
                base_package=base_pkg,
                out_package=out_c,
                seed=778,
                prefix_frames=20,
                fuzz_frames=180,
                profile="explore",
            )
            self.assertNotEqual(res_a["movieSha256"], res_c["movieSha256"])

            # 3. Verify all 3 profiles (including prefix=0 and prefix=20), manifest SHA256, initial saves, and CLI validation
            for idx, (profile, prefix_frames, fuzz_frames) in enumerate(
                [("explore", 20, 200), ("combat", 15, 200), ("chaotic", 0, 200)],
                start=1,
            ):
                pkg_path = tmp_dir / f"fuzz_{profile}.hktaspack"
                rc = _FUZZ_MOD.main(
                    [
                        "--base-package",
                        str(base_pkg),
                        "--out",
                        str(pkg_path),
                        "--seed",
                        str(100 + idx),
                        "--prefix-frames",
                        str(prefix_frames),
                        "--fuzz-frames",
                        str(fuzz_frames),
                        "--profile",
                        profile,
                    ]
                )
                self.assertEqual(0, rc)
                self.assertTrue(pkg_path.is_file())

                with zipfile.ZipFile(pkg_path, "r") as zf:
                    manifest = json.loads(zf.read("manifest.json").decode("utf-8"))
                    movie_bytes = zf.read("movie.hktas")
                    self.assertEqual(
                        hashlib.sha256(movie_bytes).hexdigest(),
                        manifest["MovieSha256"],
                    )
                    self.assertEqual(base_manifest["InitialSavesId"], manifest["InitialSavesId"])
                    self.assertEqual(base_manifest["Files"], manifest["Files"])
                    for name, raw in base_saves.items():
                        self.assertEqual(raw, zf.read(f"initial-saves/{name}"))

                movie_text = movie_bytes.decode("utf-8")
                expanded = _expand_movie_frames(movie_text)
                self.assertEqual(prefix_frames + fuzz_frames, len(expanded))
                if prefix_frames > 0:
                    self.assertEqual(base_expanded[:prefix_frames], expanded[:prefix_frames])

                # Verify edge masks across every single frame in generated movie
                prev_mask = 0
                for frame_idx, frame in enumerate(expanded):
                    for sample in frame["samples"]:
                        if sample["channel"] != "hero":
                            continue
                        values = sample["values"]
                        self.assertEqual(len(_FUZZ_MOD.HERO_ACTIONS), len(values))
                        active_mask = sum(1 << i for i, v in enumerate(values) if v)
                        self.assertEqual(
                            active_mask & ~prev_mask,
                            sample["pressedMask"],
                            f"pressedMask mismatch at profile={profile} frame={frame_idx}",
                        )
                        self.assertEqual(
                            prev_mask & ~active_mask,
                            sample["releasedMask"],
                            f"releasedMask mismatch at profile={profile} frame={frame_idx}",
                        )
                        prev_mask = active_mask

                extracted_movie = tmp_dir / f"extracted_{profile}.hktas"
                extracted_movie.write_bytes(movie_bytes)
                cli_out = _validate_with_cli(extracted_movie)
                self.assertTrue(
                    cli_out.startswith("VALID ") and f"frames={prefix_frames + fuzz_frames}" in cli_out,
                    f"Unexpected CLI output: {cli_out}",
                )

    def test_compare_s0_acceptance_consumes_chain_restore_runs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "acceptance-frame-trace.csv"
            runs_json = tmp_dir / "s0-runs.json"

            rows = [_make_trace_row("baseline", mf) for mf in range(45)]
            # Stage B segments (1..2)
            rows.extend(_make_trace_row("s0-restore-1:cp1", mf) for mf in range(10, 45))
            rows.extend(_make_trace_row("s0-restore-2:cp2", mf) for mf in range(25, 45))
            # Chain Restore segments (3..5) alternating cp1 <-> cp2
            rows.extend(_make_trace_row("s0-restore-3:cp1", mf) for mf in range(10, 45))
            rows.extend(_make_trace_row("s0-restore-4:cp2", mf) for mf in range(25, 45))
            rows.extend(_make_trace_row("s0-restore-5:cp1", mf) for mf in range(10, 45))
            _write_trace_csv(trace, rows)

            runs_payload = {
                "suite": "fuzz_explore_42",
                "baselineError": "",
                "capturedCount": 2,
                "qualifiedCount": 2,
                "runs": [
                    {
                        "Index": 1,
                        "SegmentName": "s0-restore-1:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 45,
                        "Succeeded": True,
                        "ElapsedMs": 100.0,
                    },
                    {
                        "Index": 2,
                        "SegmentName": "s0-restore-2:cp2",
                        "CheckpointId": "cp2",
                        "TransitionId": 2,
                        "ResumeMovieFrame": 25,
                        "TargetMovieFrame": 45,
                        "Succeeded": True,
                        "ElapsedMs": 80.0,
                    },
                    {
                        "Index": 3,
                        "SegmentName": "s0-restore-3:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 45,
                        "Succeeded": True,
                        "ElapsedMs": 95.0,
                        "IsChainRestore": True,
                        "ChainRound": 1,
                    },
                    {
                        "Index": 4,
                        "SegmentName": "s0-restore-4:cp2",
                        "CheckpointId": "cp2",
                        "TransitionId": 2,
                        "ResumeMovieFrame": 25,
                        "TargetMovieFrame": 45,
                        "Succeeded": True,
                        "ElapsedMs": 75.0,
                        "IsChainRestore": True,
                        "ChainRound": 2,
                    },
                    {
                        "Index": 5,
                        "SegmentName": "s0-restore-5:cp1",
                        "CheckpointId": "cp1",
                        "TransitionId": 1,
                        "ResumeMovieFrame": 10,
                        "TargetMovieFrame": 45,
                        "Succeeded": True,
                        "ElapsedMs": 90.0,
                        "IsChainRestore": True,
                        "ChainRound": 3,
                    },
                ],
            }
            runs_json.write_text(json.dumps(runs_payload, indent=2), encoding="utf-8")

            report = _COMPARE_MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs_json,
                suite_name="fuzz_explore_42",
                seed=1337,
            )
            self.assertTrue(report["passed"])
            self.assertEqual("ok", report["reason"])
            self.assertEqual(5, len(report["s0Results"]))
            self.assertTrue(all(r["passed"] for r in report["s0Results"]))

    def test_compare_s0_acceptance_detects_chain_restore_divergence(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            tmp_dir = Path(tmp)
            trace = tmp_dir / "acceptance-frame-trace.csv"
            runs_json = tmp_dir / "s0-runs.json"

            rows = [_make_trace_row("baseline", mf) for mf in range(45)]
            rows.extend(_make_trace_row("s0-restore-1:cp1", mf) for mf in range(10, 45))
            rows.extend(_make_trace_row("s0-restore-2:cp2", mf) for mf in range(25, 45))
            # Chain round 1 diverges by 1 frame on scene transition 25 -> 26
            for mf in range(10, 45):
                scene = "Town" if mf == 25 else None
                rows.append(_make_trace_row("s0-restore-3:cp1", mf, scene=scene))
            _write_trace_csv(trace, rows)

            runs_json.write_text(
                json.dumps(
                    {
                        "suite": "chain_fail",
                        "baselineError": "",
                        "capturedCount": 2,
                        "qualifiedCount": 2,
                        "runs": [
                            {
                                "Index": 1,
                                "SegmentName": "s0-restore-1:cp1",
                                "CheckpointId": "cp1",
                                "TransitionId": 1,
                                "ResumeMovieFrame": 10,
                                "TargetMovieFrame": 45,
                                "Succeeded": True,
                            },
                            {
                                "Index": 2,
                                "SegmentName": "s0-restore-2:cp2",
                                "CheckpointId": "cp2",
                                "TransitionId": 2,
                                "ResumeMovieFrame": 25,
                                "TargetMovieFrame": 45,
                                "Succeeded": True,
                            },
                            {
                                "Index": 3,
                                "SegmentName": "s0-restore-3:cp1",
                                "CheckpointId": "cp1",
                                "TransitionId": 1,
                                "ResumeMovieFrame": 10,
                                "TargetMovieFrame": 45,
                                "Succeeded": True,
                                "IsChainRestore": True,
                                "ChainRound": 1,
                            },
                        ],
                    },
                    indent=2,
                ),
                encoding="utf-8",
            )

            report = _COMPARE_MOD.compare_s0_acceptance(
                trace_path=trace,
                checkpoints_path=None,
                runs_path=runs_json,
                suite_name="chain_fail",
                seed=1337,
            )
            self.assertFalse(report["passed"])
            self.assertEqual("scene_transition_mismatch", report["reason"])
            self.assertTrue(report["s0Results"][0]["passed"])
            self.assertTrue(report["s0Results"][1]["passed"])
            self.assertFalse(report["s0Results"][2]["passed"])
            self.assertEqual("scene_transition_mismatch", report["s0Results"][2]["reason"])


if __name__ == "__main__":
    unittest.main()
