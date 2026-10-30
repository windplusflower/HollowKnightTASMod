import copy
import importlib.util
from pathlib import Path
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location("active_k3", Path(__file__).parents[1] / "Compare-LoadingState.py")
state = importlib.util.module_from_spec(spec)
spec.loader.exec_module(state)


class CanonicalActiveK3Tests(unittest.TestCase):
    def trace(self):
        header = dict(nativeProfile=state.V15_PROFILE, samplingScope=state.SUFFIX_SCOPE,
                      first=2900, end=2903, coverage="fixture")
        rows = [dict(movieFrame=2900+i, nativeFrame=3000+i, phase="after", boundary="GameplayInput",
                     scene="GG_Workshop", sequence=i,
                     state={**{key: "exact" for key in state.ACTIVE_K3_GATES}, "time": "exact"},
                     inventory={"roots": [dict(id="hero", type="Hero", digest="raw", nodes=3)]}) for i in range(3)]
        return header, rows

    def compare(self, left, right, contract="auto"):
        with patch.object(state, "read_trace", side_effect=[left, right]):
            return state.compare([Path("source"), Path("target")], contract)

    def test_approved_history_keeps_raw_failures_but_passes_active_contract(self):
        a, b = self.trace(), self.trace()
        for i, row in enumerate(b[1]):
            row["state"].update({key: f"history-{i}" for key in state.PROCESS_HISTORY_PROJECTIONS if key != "rootInventory"})
            row["inventory"]["roots"][0].update(digest="shifted-shared-id", nodes=5)
        result = self.compare(a, b)
        self.assertTrue(result["comparisonPassed"])
        self.assertFalse(result["rawComparisonPassed"])
        self.assertEqual(20, len(result["comparisons"][0]["diagnosticProcessHistoryProjections"]))
        self.assertFalse(result["completeStateCoverage"])
        self.assertFalse(self.compare(a, b, "raw")["comparisonPassed"])

    def test_each_effective_gate_is_required_on_all_three_samples(self):
        for gate in state.ACTIVE_K3_GATES:
            for missing in (False, True):
                a, b = self.trace(), self.trace()
                if missing:
                    a[1][2]["state"].pop(gate)
                    b[1][2]["state"].pop(gate)
                else:
                    b[1][2]["state"][gate] = "different"
                with self.subTest(gate=gate, missing=missing):
                    self.assertFalse(self.compare(a, b)["comparisonPassed"])

    def test_root_identity_type_and_duplicate_changes_remain_blocking(self):
        for change in (dict(id="other"), dict(type="Other")):
            a, b = self.trace(), self.trace()
            b[1][1]["inventory"]["roots"][0].update(change)
            self.assertFalse(self.compare(a, b)["comparisonPassed"])
        a, b = self.trace(), self.trace()
        b[1][1]["inventory"]["roots"].append(copy.deepcopy(b[1][1]["inventory"]["roots"][0]))
        self.assertFalse(self.compare(a, b)["comparisonPassed"])

    def test_unknown_difference_header_and_native_cursor_still_fail(self):
        for mutate in (lambda b: b[1][0]["state"].update(unregistered="new"),
                       lambda b: b[0].update(observerModuleId="other"),
                       lambda b: b[1][1].update(nativeFrame=9999)):
            a, b = self.trace(), self.trace()
            mutate(b)
            self.assertFalse(self.compare(a, b)["comparisonPassed"])

    def test_contract_cannot_apply_to_another_profile_or_shorter_trace(self):
        a, b = self.trace(), self.trace()
        a[0]["nativeProfile"] = b[0]["nativeProfile"] = "v14"
        with self.assertRaises(ValueError):
            self.compare(a, b, state.ACTIVE_K3_CONTRACT)
        a, b = self.trace(), self.trace()
        b[1].pop()
        with self.assertRaises(ValueError):
            self.compare(a, b)


if __name__ == "__main__":
    unittest.main()
