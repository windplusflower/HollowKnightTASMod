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
_COMPARE_SCRIPT = _VERIFY_DIR / "Compare-S0SemanticSnapshots.py"
_AUDIT_SCRIPT = _VERIFY_DIR / "Invoke-S0SemanticAudit.ps1"
_COMPARE_MOD = _load_module("compare_s0_semantic_snapshots", _COMPARE_SCRIPT)


def _make_snapshot(
    flat: dict[str, object],
    *,
    ordinal: int = 1,
    segment: str = "",
    source_scene: str = "GG_Workshop",
    target_scene: str = "GG_Vengefly_V",
    movie_frame: int = 100,
    native_frame: int = 150,
) -> dict[str, object]:
    return {
        "schema": "lean-s0-semantic-probe-v1",
        "meta": {
            "ordinal": ordinal,
            "segment": segment,
            "sourceScene": source_scene,
            "targetScene": target_scene,
            "entryGate": "door_dreamEnter",
            "movieFrame": movie_frame,
            "nativeFrame": native_frame,
        },
        "flat": flat,
    }


def _find_powershell() -> str:
    for candidate in ("pwsh", "powershell", "powershell.exe"):
        found = shutil.which(candidate)
        if found:
            return found
    raise RuntimeError("No PowerShell executable found on PATH")


class S0SemanticToolsTests(unittest.TestCase):
    def test_legacy_cli_backward_compatible(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            left_path = root / "left.json"
            right_path = root / "right.json"
            out_path = root / "diff.json"

            left_path.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "globalVars.intVariables[Foo]": 1,
                            "ddol/Knight:HeroController.health": 9,
                            "static/GameManager.sceneLoadCount": 2,
                        }
                    )
                ),
                encoding="utf-8",
            )
            right_path.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "globalVars.intVariables[Foo]": 1,
                            "ddol/Knight:HeroController.health": 8,
                            "static/GameManager.sceneLoadCount": 3,
                            "ddol/Pool:ObjectPool.pooledObjects.@count": 5,
                        },
                        movie_frame=220,
                        native_frame=270,
                    )
                ),
                encoding="utf-8",
            )

            proc = subprocess.run(
                [
                    sys.executable,
                    str(_COMPARE_SCRIPT),
                    "--left",
                    str(left_path),
                    "--right",
                    str(right_path),
                    "--output",
                    str(out_path),
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc.returncode, 0, msg=proc.stderr)
            report = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertEqual(report["totalFlatKeysLeft"], 3)
            self.assertEqual(report["totalFlatKeysRight"], 4)
            self.assertEqual(report["totalDiffCount"], 3)
            self.assertEqual(report["ignoredDiffCount"], 0)
            self.assertEqual(report["ignoredKeys"], [])
            self.assertEqual(
                report["diffCountByDomain"],
                {
                    "Knight.HeroController": 1,
                    "ObjectPool": 1,
                    "static": 1,
                },
            )
            self.assertEqual(len(report["diffs"]), 3)
            self.assertEqual(report["leftMeta"]["movieFrame"], 100)
            self.assertEqual(report["rightMeta"]["movieFrame"], 220)

    def test_ignore_known_time_fields_preserves_real_state_diffs(self) -> None:
        left_flat = {
            "ddol/_GameManager:GameManager.unityTime": "10.0",
            "ddol/_GameManager:GameManager.realtimeSinceStartup": "11.0",
            "ddol/_GameManager:GameManager.unscaledTime": "10.0",
            "ddol/_GameManager:GameManager.fixedTime": "9.98",
            "ddol/_GameManager:GameManager.timeSinceLevelLoad": "4.0",
            "static/HollowKnightTAS.Runtime.FullRun.RuntimeFullRunSession.LastGameUpdateNativeFrame": 100,
            "ddol/_GameManager:GameManager.startMenuTime": "1.2",
            "ddol/Knight:HeroController.nativeFrame": 100,
            "ddol/Knight:HeroController.frameCount": 100,
            "ddol/_GameManager:GameManager.<CurrentTick>k__BackingField": 500,
            "ddol/Knight:HeroController.CurrentTick": 500,
            # Equal time field must NOT appear in ignoredKeys
            "ddol/_GameManager:GameManager.unchanged.frameCount": 42,
            # Real state fields that must NOT be ignored
            "ddol/Pool:ObjectPool.pooledObjects.@count": 12,
            "ddol/Knight:HeroController.cState.onGround": True,
            "ddol/Knight:HeroController.dashCooldownTimer": "0.0",
            "static/GameManager.sceneLoadCount": 1,
        }
        right_flat = dict(left_flat)
        right_flat.update(
            {
                "ddol/_GameManager:GameManager.unityTime": "12.4",
                "ddol/_GameManager:GameManager.realtimeSinceStartup": "13.4",
                "ddol/_GameManager:GameManager.unscaledTime": "12.4",
                "ddol/_GameManager:GameManager.fixedTime": "12.38",
                "ddol/_GameManager:GameManager.timeSinceLevelLoad": "6.4",
                "static/HollowKnightTAS.Runtime.FullRun.RuntimeFullRunSession.LastGameUpdateNativeFrame": 220,
                "ddol/_GameManager:GameManager.startMenuTime": "3.6",
                "ddol/Knight:HeroController.nativeFrame": 220,
                "ddol/Knight:HeroController.frameCount": 220,
                "ddol/_GameManager:GameManager.<CurrentTick>k__BackingField": 620,
                "ddol/Knight:HeroController.CurrentTick": 620,
                "ddol/Pool:ObjectPool.pooledObjects.@count": 19,
                "ddol/Knight:HeroController.cState.onGround": False,
                "ddol/Knight:HeroController.dashCooldownTimer": "0.35",
                "static/GameManager.sceneLoadCount": 2,
            }
        )

        summary = _COMPARE_MOD.compare_snapshots(
            _make_snapshot(left_flat),
            _make_snapshot(right_flat),
            ignore_known_time_fields=True,
        )

        self.assertEqual(summary["ignoredDiffCount"], 11)
        self.assertNotIn("ddol/_GameManager:GameManager.unchanged.frameCount", summary["ignoredKeys"])
        self.assertEqual(summary["totalDiffCount"], 4)
        self.assertEqual(
            summary["diffCountByDomain"],
            {
                "Knight.HeroController": 2,
                "ObjectPool": 1,
                "static": 1,
            },
        )
        diff_paths = {d["path"] for d in summary["diffs"]}
        self.assertEqual(
            diff_paths,
            {
                "ddol/Pool:ObjectPool.pooledObjects.@count",
                "ddol/Knight:HeroController.cState.onGround",
                "ddol/Knight:HeroController.dashCooldownTimer",
                "static/GameManager.sceneLoadCount",
            },
        )

    def test_markdown_output_and_fail_on_diff_exit_codes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            left_path = root / "left.json"
            right_path = root / "right.json"
            out_path = root / "diff.json"
            md_path = root / "diff.md"

            left_flat = {f"ddol/Knight:HeroController.field_{i:03d}": i for i in range(125)}
            left_flat["ddol/_GameManager:GameManager.unityTime"] = "1.0"
            right_flat = {f"ddol/Knight:HeroController.field_{i:03d}": i + 1 for i in range(125)}
            right_flat["ddol/_GameManager:GameManager.unityTime"] = "3.4"

            left_path.write_text(json.dumps(_make_snapshot(left_flat, segment="clean")), encoding="utf-8")
            right_path.write_text(json.dumps(_make_snapshot(right_flat, segment="dirty")), encoding="utf-8")

            proc_fail = subprocess.run(
                [
                    sys.executable,
                    str(_COMPARE_SCRIPT),
                    "--left",
                    str(left_path),
                    "--right",
                    str(right_path),
                    "--output",
                    str(out_path),
                    "--markdown-output",
                    str(md_path),
                    "--ignore-known-time-fields",
                    "--fail-on-diff",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_fail.returncode, 1)
            md_text = md_path.read_text(encoding="utf-8")
            self.assertIn("**FAIL**", md_text)
            self.assertIn("showing 100 of 125", md_text)
            self.assertIn("| `Knight.HeroController` | 125 |", md_text)
            self.assertIn("Ignored Known Time/Frame Keys (showing 1 of 1)", md_text)
            # Only first 100 diff rows should be rendered in markdown detail tables
            rendered_field_rows = [line for line in md_text.splitlines() if "HeroController.field_" in line]
            self.assertEqual(len(rendered_field_rows), 100)

            # Time-only diff with --ignore-known-time-fields and --fail-on-diff should exit 0 (PASS)
            left_time = root / "left_time.json"
            right_time = root / "right_time.json"
            out_pass = root / "pass.json"
            md_pass = root / "pass.md"
            left_time.write_text(
                json.dumps(_make_snapshot({"ddol/_GameManager:GameManager.unityTime": "1.0", "ddol/Knight:HeroController.health": 9})),
                encoding="utf-8",
            )
            right_time.write_text(
                json.dumps(_make_snapshot({"ddol/_GameManager:GameManager.unityTime": "5.0", "ddol/Knight:HeroController.health": 9})),
                encoding="utf-8",
            )
            proc_pass = subprocess.run(
                [
                    sys.executable,
                    str(_COMPARE_SCRIPT),
                    "--left",
                    str(left_time),
                    "--right",
                    str(right_time),
                    "--output",
                    str(out_pass),
                    "--markdown-output",
                    str(md_pass),
                    "--ignore-known-time-fields",
                    "--fail-on-diff",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_pass.returncode, 0, msg=proc_pass.stderr)
            self.assertIn("**PASS**", md_pass.read_text(encoding="utf-8"))

    def test_invoke_s0_semantic_audit_compare_only_end_to_end(self) -> None:
        pwsh = _find_powershell()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_path = root / "clean-s0-semantic-boundary-01.json"
            dirty_time_only = root / "dirty-time-only.json"
            dirty_real_diff = root / "dirty-real-diff.json"

            clean_path.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "ddol/_GameManager:GameManager.unityTime": "10.0",
                            "static/RuntimeFullRunSession.LastGameUpdateNativeFrame": 100,
                            "ddol/Pool:ObjectPool.pooledObjects.@count": 4,
                        },
                        segment="clean",
                    )
                ),
                encoding="utf-8",
            )
            dirty_time_only.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "ddol/_GameManager:GameManager.unityTime": "12.4",
                            "static/RuntimeFullRunSession.LastGameUpdateNativeFrame": 220,
                            "ddol/Pool:ObjectPool.pooledObjects.@count": 4,
                        },
                        segment="dirty",
                    )
                ),
                encoding="utf-8",
            )
            dirty_real_diff.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "ddol/_GameManager:GameManager.unityTime": "12.4",
                            "static/RuntimeFullRunSession.LastGameUpdateNativeFrame": 220,
                            "ddol/Pool:ObjectPool.pooledObjects.@count": 8,
                        },
                        segment="dirty",
                    )
                ),
                encoding="utf-8",
            )

            out_pass = root / "audit-pass"
            proc_pass = subprocess.run(
                [
                    pwsh,
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(_AUDIT_SCRIPT),
                    "-CompareOnly",
                    "-LeftSnapshot",
                    str(clean_path),
                    "-RightSnapshot",
                    str(dirty_time_only),
                    "-OutputDirectory",
                    str(out_pass),
                    "-IgnoreKnownTimeFields",
                    "-FailOnDiff",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_pass.returncode, 0, msg=proc_pass.stdout + "\n" + proc_pass.stderr)
            pass_json = json.loads((out_pass / "semantic-diff.json").read_text(encoding="utf-8"))
            self.assertEqual(pass_json["totalDiffCount"], 0)
            self.assertEqual(pass_json["ignoredDiffCount"], 2)
            self.assertIn("**PASS**", (out_pass / "semantic-diff.md").read_text(encoding="utf-8"))

            out_fail = root / "audit-fail"
            proc_fail = subprocess.run(
                [
                    pwsh,
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(_AUDIT_SCRIPT),
                    "-CompareOnly",
                    "-LeftSnapshot",
                    str(clean_path),
                    "-RightSnapshot",
                    str(dirty_real_diff),
                    "-OutputDirectory",
                    str(out_fail),
                    "-IgnoreKnownTimeFields",
                    "-FailOnDiff",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_fail.returncode, 1, msg=proc_fail.stdout + "\n" + proc_fail.stderr)
            fail_json = json.loads((out_fail / "semantic-diff.json").read_text(encoding="utf-8"))
            self.assertEqual(fail_json["totalDiffCount"], 1)
            self.assertEqual(fail_json["diffCountByDomain"], {"ObjectPool": 1})
            self.assertIn("**FAIL**", (out_fail / "semantic-diff.md").read_text(encoding="utf-8"))

    def test_case_only_conflicting_keys_handled_by_compare_and_audit(self) -> None:
        pwsh = _find_powershell()
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            snap_dir = root / "snapshots"
            snap_dir.mkdir(parents=True, exist_ok=True)
            left_path = snap_dir / "baseline-s0-semantic-boundary-01.json"
            right_path = snap_dir / "s0-restore-01-s0-semantic-boundary-01.json"

            left_path.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "ddol/Knight:HeroController.JUMP_STEPS": 10,
                            "ddol/Knight:HeroController.jump_steps": 2,
                        },
                        ordinal=1,
                        segment="baseline",
                        movie_frame=100,
                        native_frame=150,
                    )
                ),
                encoding="utf-8",
            )
            right_path.write_text(
                json.dumps(
                    _make_snapshot(
                        {
                            "ddol/Knight:HeroController.JUMP_STEPS": 10,
                            "ddol/Knight:HeroController.jump_steps": 3,
                        },
                        ordinal=1,
                        segment="s0-restore-01",
                        movie_frame=101,
                        native_frame=250,
                    )
                ),
                encoding="utf-8",
            )

            # 1. Compare-S0SemanticSnapshots.py preserves both case-distinct keys
            summary = _COMPARE_MOD.compare_snapshots(
                json.loads(left_path.read_text(encoding="utf-8")),
                json.loads(right_path.read_text(encoding="utf-8")),
                ignore_known_time_fields=True,
            )
            self.assertEqual(summary["totalFlatKeysLeft"], 2)
            self.assertEqual(summary["totalFlatKeysRight"], 2)
            self.assertEqual(summary["totalDiffCount"], 1)
            self.assertEqual(summary["diffs"][0]["path"], "ddol/Knight:HeroController.jump_steps")
            self.assertEqual(summary["diffs"][0]["left"], 2)
            self.assertEqual(summary["diffs"][0]["right"], 3)

            # 2. Invoke-S0SemanticAudit.ps1 -CompareOnly succeeds on case-conflicting snapshots
            out_dir = root / "audit-case"
            proc_cmp = subprocess.run(
                [
                    pwsh,
                    "-NoProfile",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-File",
                    str(_AUDIT_SCRIPT),
                    "-CompareOnly",
                    "-LeftSnapshot",
                    str(left_path),
                    "-RightSnapshot",
                    str(right_path),
                    "-OutputDirectory",
                    str(out_dir),
                    "-IgnoreKnownTimeFields",
                ],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_cmp.returncode, 0, msg=proc_cmp.stdout + "\n" + proc_cmp.stderr)
            cmp_json = json.loads((out_dir / "semantic-diff.json").read_text(encoding="utf-8"))
            self.assertEqual(cmp_json["totalDiffCount"], 1)

            # 3. Read-SnapshotEntries inside Invoke-S0SemanticAudit.ps1 parses both JSONs without ConvertFrom-Json key collision
            ps_check = (
                "$tokens = $null; $errors = $null; "
                f"$ast = [System.Management.Automation.Language.Parser]::ParseFile('{_AUDIT_SCRIPT}', [ref]$tokens, [ref]$errors); "
                "if ($errors.Count -gt 0) { throw 'AST parse errors' }; "
                "$fn = $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Read-SnapshotEntries' }, $true) | Select-Object -First 1; "
                "if ($null -eq $fn) { throw 'Read-SnapshotEntries not found' }; "
                "Invoke-Expression $fn.Extent.Text; "
                f"$entries = @(Read-SnapshotEntries '{snap_dir}'); "
                "$entries | ConvertTo-Json -Compress"
            )
            proc_ps = subprocess.run(
                [pwsh, "-NoProfile", "-Command", ps_check],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(proc_ps.returncode, 0, msg=proc_ps.stdout + "\n" + proc_ps.stderr)
            entries = json.loads(proc_ps.stdout)
            self.assertEqual(len(entries), 2)
            self.assertEqual({e["segment"] for e in entries}, {"baseline", "s0-restore-01"})
            self.assertTrue(all(e["ordinal"] == 1 for e in entries))


if __name__ == "__main__":
    unittest.main()
