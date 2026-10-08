import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from loading_trace_windows import expected_gameplay_samples, gameplay_windows, validate_sample_scope

spec = importlib.util.spec_from_file_location("loading_state", Path(__file__).parents[1] / "Compare-LoadingState.py")
state = importlib.util.module_from_spec(spec)
spec.loader.exec_module(state)


class LoadingTraceWindowsTests(unittest.TestCase):
    def header(self):
        return dict(kind="header", schema="hktas-loading-oracle-v1", first=1400, end=4000,
                    coverage="registered-state-diagnostic-test-fixture",
                    samplingScope="bounded-transition-windows-v1",
                    gameplayWindows=[dict(first=1418, end=1424), dict(first=2196, end=2203),
                                     dict(first=2912, end=2919)], completeStateCoverage=False)

    def trace(self, directory, name, mutate=None):
        header = self.header()
        rows = [dict(kind="sample", sequence=i, movieFrame=frame, phase=phase,
                     scene="room", boundary="GameplayInput", state={"rng": "exact", "physics": "exact"})
                for i, (frame, phase) in enumerate(expected_gameplay_samples(header))]
        if mutate:
            mutate(header, rows)
        for i, row in enumerate(rows):
            row["sequence"] = i
        body = b"".join((json.dumps(row) + "\n").encode() for row in [header, *rows])
        footer = dict(kind="footer", success=True, first=1400, end=4000, rows=len(rows),
                      sha256=hashlib.sha256(body).hexdigest())
        path = Path(directory) / name
        path.write_bytes(body + (json.dumps(footer) + "\n").encode())
        return path

    def test_all_pairs_in_all_windows_are_required(self):
        pairs = expected_gameplay_samples(self.header())
        self.assertEqual(40, len(pairs))
        self.assertEqual([(1418, "before"), (1418, "after")], pairs[:2])
        self.assertEqual((2918, "after"), pairs[-1])

    def test_invalid_window_headers_are_rejected(self):
        for declared in (None, [], [{"first": 1399, "end": 1401}], [{"first": 3999, "end": 4001}],
                         [{"first": 1418, "end": 1418}], [{"first": 1418.0, "end": 1424}],
                         [{"first": 1418, "end": 1424}, {"first": 1423, "end": 1425}],
                         [{"first": 2912, "end": 2919}, {"first": 1418, "end": 1424}]):
            header = dict(self.header(), gameplayWindows=declared)
            with self.subTest(declared=declared), self.assertRaises(ValueError):
                gameplay_windows(header)
        with self.assertRaisesRegex(ValueError, "unknown sampling scope"):
            gameplay_windows(dict(self.header(), samplingScope="skip-anything"))

    def test_gap_samples_and_unannounced_windows_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "outside declared"):
            validate_sample_scope(self.header(), [{"movieFrame": 2000}])
        with self.assertRaisesRegex(ValueError, "require"):
            gameplay_windows(dict(self.header(), samplingScope="bounded-window"))

    def test_short_window_trace_does_not_grant_restore_or_phase_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            result = state.compare([self.trace(directory, "a"), self.trace(directory, "b")])
        self.assertTrue(result["comparisonPassed"])
        self.assertFalse(result["phaseP0Passed"])

    def test_missing_or_reordered_pair_rejects_even_with_valid_footer(self):
        with tempfile.TemporaryDirectory() as directory:
            for mutation in (lambda h, rows: rows.pop(14),
                             lambda h, rows: rows.__setitem__(slice(12, 14), rows[12:14][::-1])):
                with self.assertRaisesRegex(ValueError, "incomplete gameplay window"):
                    state.read_trace(self.trace(directory, "bad", mutation))

    def test_state_difference_in_later_window_is_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            a = self.trace(directory, "a")
            b = self.trace(directory, "b", lambda h, rows: rows[-2]["state"].update(physics="different"))
            result = state.compare([a, b])
        self.assertFalse(result["comparisonPassed"])
        difference = result["comparisons"][0]["firstDifferences"]["physics"]
        self.assertEqual(2918, difference["movieFrame"])


if __name__ == "__main__":
    unittest.main()
