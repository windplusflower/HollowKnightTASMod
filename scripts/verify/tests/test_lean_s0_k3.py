import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from importlib.util import module_from_spec, spec_from_file_location

_MOD_PATH = Path(__file__).resolve().parents[1] / "Compare-LeanS0K3.py"
_SPEC = spec_from_file_location("compare_lean_s0_k3", _MOD_PATH)
assert _SPEC and _SPEC.loader
_MOD = module_from_spec(_SPEC)
_SPEC.loader.exec_module(_MOD)


def _write_trace(path: Path, frames: list[dict], *, rng_sha: str = "abc", start: int = 6706) -> None:
    lines = [
        json.dumps(
            {
                "recordType": "header",
                "schema": "lean-s0-k3-trace-v1",
                "metadata": {
                    "role": "source",
                    "ordinal": 8,
                    "targetScene": "GG_Lost_Kin",
                    "entryGate": "door_dreamEnter",
                    "startMovie": start,
                    "endMovie": start + len(frames),
                    "rngSeed": 42,
                    "rngSha256": rng_sha,
                    "packetBytes": 12345,
                },
            }
        )
    ]
    for f in frames:
        lines.append(json.dumps({"recordType": "frame", **f}))
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def _sample_frame(mf: int, x: float = 10.0) -> dict:
    return {
        "movieFrame": mf,
        "nativeFrame": mf + 200,
        "scene": "GG_Lost_Kin",
        "gameState": "PLAYING",
        "rngSha256": f"rng-{mf}",
        "hero": {"pos": [x, 5.0, 0.0], "vel": [0.0, 0.0], "facingRight": True},
        "player": {"health": 9, "geo": 100, "equippedCharms": [13, 25]},
        "enemies": [
            {
                "path": "GG_Lost_Kin/Lost Kin#0",
                "hp": 1200,
                "isDead": False,
                "pos": [30.0, 6.0],
                "fsmStates": {"Lost Kin": "Idle"},
            }
        ],
        "grimmchild": None,
    }


class CompareLeanS0K3Tests(unittest.TestCase):
    def test_exact_match_passes(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            p1 = Path(tmp) / "src.jsonl"
            p2 = Path(tmp) / "tgt.jsonl"
            frames = [_sample_frame(6706 + i, x=10.0 + i * 0.1) for i in range(32)]
            _write_trace(p1, frames)
            _write_trace(p2, frames)
            report = _MOD.compare_k3_traces(p1, p2)
            self.assertTrue(report["passed"])
            self.assertEqual(32, report["comparedFrames"])

    def test_injected_single_frame_divergence_is_detected(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            p1 = Path(tmp) / "src.jsonl"
            p2 = Path(tmp) / "tgt.jsonl"
            src_frames = [_sample_frame(6706 + i, x=10.0 + i) for i in range(32)]
            tgt_frames = [_sample_frame(6706 + i, x=10.0 + i) for i in range(32)]
            # Inject intentional 1-unit boss HP mismatch at movieFrame 6715
            tgt_frames[9]["enemies"][0]["hp"] = 1199
            _write_trace(p1, src_frames)
            _write_trace(p2, tgt_frames)

            report = _MOD.compare_k3_traces(p1, p2)
            self.assertFalse(report["passed"])
            self.assertEqual("trajectory_divergence", report["reason"])
            div = report["firstDivergence"]
            self.assertEqual(6715, div["movieFrame"])
            self.assertEqual("enemies[0].hp", div["field"])
            self.assertEqual(1200, div["source"])
            self.assertEqual(1199, div["target"])


if __name__ == "__main__":
    unittest.main()
