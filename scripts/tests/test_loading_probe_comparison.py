import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("loading_probe", Path(__file__).parents[1] / "Compare-LoadingProbe.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class LoadingProbeComparisonTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)

    def write(self, name, mutate=None, success=True):
        path = Path(self.directory.name) / name
        header = dict(kind="header", schema="hktas-loading-probe-v1", first=3, end=5,
                      completeStateCoverage=False)
        rows = [dict(kind="sample", sequence=i, movieFrame=3+i//2,
                     phase=("before", "after")[i % 2], scene="room", boundary="GameplayInput",
                     state={"rng": "abc", "time": "f32:12345678"}) for i in range(4)]
        if mutate:
            mutate(rows)
        body = b"".join((json.dumps(r) + "\n").encode() for r in [header, *rows])
        footer = dict(kind="footer", success=success, rows=len(rows), first=3, end=5,
                      sha256=hashlib.sha256(body).hexdigest())
        path.write_bytes(body + (json.dumps(footer) + "\n").encode())
        return path

    def test_equal_subset_does_not_claim_p0_pass(self):
        result = probe.compare([self.write("a"), self.write("b")])
        self.assertTrue(result["comparisonPassed"])
        self.assertFalse(result["phaseP0Passed"])

    def test_first_difference_keeps_raw_bits(self):
        b = self.write("b", lambda rs: rs[2]["state"].update(time="f32:12345679"))
        result = probe.compare([self.write("a"), b])
        difference = result["differences"][0]["firstDifferences"][0]
        self.assertFalse(result["comparisonPassed"])
        self.assertEqual((4, "before", "time"), (difference["movieFrame"], difference["phase"], difference["field"]))

    def test_missing_pair_rejected_even_with_valid_digest(self):
        with self.assertRaisesRegex(ValueError, "missing, repeated"):
            probe.read_trace(self.write("a", lambda rs: rs.pop()))

    def test_duplicate_sequence_rejected(self):
        with self.assertRaisesRegex(ValueError, "sequence"):
            probe.read_trace(self.write("a", lambda rs: rs[2].update(sequence=1)))

    def test_budget_failure_rejected(self):
        with self.assertRaisesRegex(ValueError, "did not complete"):
            probe.read_trace(self.write("a", success=False))

    def test_tampered_trace_rejected(self):
        p = self.write("a")
        p.write_bytes(p.read_bytes().replace(b"12345678", b"12345679"))
        with self.assertRaisesRegex(ValueError, "digest"):
            probe.read_trace(p)

    def test_truncated_trace_rejected(self):
        p = self.write("a")
        p.write_bytes(p.read_bytes()[:-1])
        with self.assertRaisesRegex(ValueError, "incomplete"):
            probe.read_trace(p)


if __name__ == "__main__":
    unittest.main()
