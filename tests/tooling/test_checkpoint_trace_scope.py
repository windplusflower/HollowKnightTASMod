import hashlib
import importlib.util
import json
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("checkpoint_compare", Path(__file__).resolve().parents[2] / "scripts/Compare-CheckpointPrototype.py")
compare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compare)


class CheckpointTraceScopeTests(unittest.TestCase):
    def read(self, kind="rng-call", frame=7, continuous=True, first=3, end=9, outer_end=10):
        header = dict(kind="header", schema="hktas-loading-probe-v1", first=0, end=outer_end,
                      windows=[dict(first=0, end=3), dict(first=outer_end - 1, end=outer_end)],
                      continuousRandomCalls=continuous, randomFirst=first, randomEnd=end)
        row = dict(kind=kind, phase="before" if kind == "sample" else "rng-before", sequence=0, movieFrame=frame)
        body = b"".join((json.dumps(r) + "\n").encode() for r in (header, row))
        footer = dict(kind="footer", first=0, end=outer_end, rows=1, success=True, sha256=hashlib.sha256(body).hexdigest())
        data = body + (json.dumps(footer) + "\n").encode()
        class Evidence:
            def read_bytes(self):
                return data
            def __str__(self):
                return "in-memory trace"
        return compare.read_trace(Evidence())

    def test_declared_calls_can_cross_unsampled_state_gap(self):
        self.assertEqual(7, self.read()[2][0]["movieFrame"])
        self.assertEqual(7, self.read(kind="timer-call")[2][0]["movieFrame"])

    def test_state_rows_must_stay_in_short_windows(self):
        with self.assertRaisesRegex(ValueError, "outside the declared scope"):
            self.read(kind="sample")

    def test_opt_in_and_call_interval_are_required(self):
        for args in (dict(continuous=False), dict(frame=9), dict(frame=True)):
            with self.subTest(args=args), self.assertRaisesRegex(ValueError, "outside the declared scope"):
                self.read(**args)

    def test_invalid_continuous_scope_is_rejected(self):
        for args in (dict(end=300, outer_end=400), dict(first=9, end=3), dict(first=True), dict(continuous="true")):
            with self.subTest(args=args), self.assertRaisesRegex(ValueError, "invalid continuous"):
                self.read(**args)


if __name__ == "__main__":
    unittest.main()
