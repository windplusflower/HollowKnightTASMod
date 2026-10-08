import importlib.util
import hashlib
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("state_comparator", Path(__file__).parents[1] / "scripts/Compare-LoadingState.py")
comparator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparator)


class P0StateComparatorTests(unittest.TestCase):
    def trace(self, root, name, *, state="bits:00000000", native=20, complete=True, corrupt=False,
              duplicate=False, profile="experimental", coverage="bounded", candidate_only=False):
        header = {"kind": "header", "schema": "hktas-loading-oracle-v1", "first": 10, "end": 11,
                  "nativeProfile": profile, "coverage": coverage,
                  "samplingScope": "serial-candidate-boundaries-only-diagnostic" if candidate_only else "bounded-window"}
        phases = ["candidate"] if candidate_only else ["before", "candidate", "after"]
        rows = [{"kind": "sample", "sequence": i, "phase": phase, "boundary": "C" if phase == "candidate" else "GameplayInput",
                 "movieFrame": 10, "nativeFrame": native, "scene": "Room", "state": {"value": state}}
                for i, phase in enumerate(phases)]
        if duplicate:
            rows[-1]["sequence"] = 0
        data = b"".join((json.dumps(row) + "\n").encode() for row in [header] + rows)
        footer = {"kind": "footer", "success": complete, "rows": len(rows), "first": 10, "end": 11,
                  "sha256": "corrupt" if corrupt else hashlib.sha256(data).hexdigest()}
        path = root / name
        path.write_bytes(data + (json.dumps(footer) + "\n").encode())
        return path

    def test_candidate_samples_are_compared_and_never_qualify_p0(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = comparator.compare([self.trace(root, "a"), self.trace(root, "b", native=999)])
            self.assertTrue(report["comparisonPassed"])
            self.assertEqual(report["comparisons"][0]["comparedSamples"], 3)
            self.assertFalse(report["completeStateCoverage"])
            self.assertFalse(report["phaseP0Passed"])

    def test_raw_bit_difference_fails_without_tolerance(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = comparator.compare([self.trace(root, "a"), self.trace(root, "b", state="bits:00000001")])
            self.assertFalse(report["comparisonPassed"])
            self.assertEqual(report["comparisons"][0]["mismatchedSamples"], 3)

    def test_profile_and_coverage_changes_cannot_compare_as_equal(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = comparator.compare([self.trace(root, "a"), self.trace(root, "b", profile="other", coverage="reduced")])
            self.assertFalse(report["comparisonPassed"])
            self.assertEqual(report["comparisons"][0]["headerDifferences"], ["coverage", "nativeProfile"])

    def test_incomplete_corrupt_and_duplicate_traces_are_rejected(self):
        for options in ({"complete": False}, {"corrupt": True}, {"duplicate": True}):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                with self.assertRaises(ValueError):
                    comparator.read_trace(self.trace(Path(directory), "trace", **options))

    def test_candidate_only_diagnostic_scope_remains_partial(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = comparator.compare([self.trace(root, "a", candidate_only=True), self.trace(root, "b", candidate_only=True)])
            self.assertTrue(report["comparisonPassed"])
            self.assertEqual(report["phases"], {"candidate": 1})
            self.assertFalse(report["phaseP0Passed"])


if __name__ == "__main__":
    unittest.main()
