import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("physics_comparator", Path(__file__).parents[1] / "scripts/Compare-LoadingPhysics.py")
comparator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparator)


class P0PhysicsComparatorTests(unittest.TestCase):
    def entry(self, kind, binding, value):
        return {"kind": kind, "binding": binding, "fields": [hashlib.sha256(value.encode()).hexdigest()]}

    def trace(self, root, name, entries):
        header = {"kind": "header", "schema": "hktas-loading-oracle-v1", "first": 10, "end": 11,
                  "nativePhysicsEntryDiagnostics": True, "coverage": "native-diagnostic",
                  "samplingScope": "serial-candidate-boundaries-only-diagnostic"}
        row = {"kind": "sample", "sequence": 0, "movieFrame": 10, "phase": "candidate", "scene": "Room",
               "boundary": "C", "state": {}, "nativePhysicsEntries": entries}
        data = b"".join((json.dumps(item) + "\n").encode() for item in [header, row])
        footer = {"kind": "footer", "success": True, "rows": 1, "first": 10, "end": 11,
                  "sha256": hashlib.sha256(data).hexdigest()}
        path = root / name
        path.write_bytes(data + (json.dumps(footer) + "\n").encode())
        return path

    def report(self, left, right):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            return comparator.compare([self.trace(root, "a", left), self.trace(root, "b", right)])

    def test_reordered_object_records_do_not_imply_changed_bound_geometry(self):
        entries = [self.entry("polygon-active", "grass10", "x10"), self.entry("polygon-active", "grass12", "x12")]
        report = self.report(entries, entries[::-1])
        item = report["comparisons"][0]
        self.assertFalse(item["firstBoundValueDifferences"])
        self.assertIn("polygon-active", item["firstRecordTraversalDifferences"])
        self.assertFalse(report["comparisonPassed"])

    def test_native_list_order_is_preserved_even_with_same_members(self):
        entries = [self.entry("body-fixture-order", "ground", "grass10"),
                   self.entry("body-fixture-order", "ground", "grass12")]
        report = self.report(entries, entries[::-1])
        self.assertIn("body-fixture-order", report["comparisons"][0]["firstNativeOrderDifferences"])
        self.assertFalse(report["comparisonPassed"])

    def test_value_difference_and_missing_object_are_reported(self):
        base = [self.entry("fixture", "a", "original"), self.entry("fixture", "b", "original")]
        for right in ([self.entry("fixture", "a", "changed"), base[1]], base[:1]):
            with self.subTest(right=right):
                report = self.report(base, right)
                self.assertIn("fixture", report["comparisons"][0]["firstBoundValueDifferences"])
                self.assertFalse(report["comparisonPassed"])

    def test_equal_diagnostics_do_not_qualify_complete_state_or_p0(self):
        base = [self.entry("fixture", "a", "value")]
        report = self.report(base, base)
        self.assertTrue(report["comparisonPassed"])
        self.assertFalse(report["completeStateCoverage"])
        self.assertFalse(report["phaseP0Passed"])


if __name__ == "__main__":
    unittest.main()
