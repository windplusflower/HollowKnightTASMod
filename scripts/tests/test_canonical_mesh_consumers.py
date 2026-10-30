"""A missing counterpart never proves a Mesh has no consumers."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "mesh_owners", Path(__file__).resolve().parents[1] / "Analyze-CanonicalMeshOwners.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MeshConsumerEvidenceTest(unittest.TestCase):
    def test_unpaired_mesh_keeps_real_sprite_and_native_consumers(self):
        asset = "asset:UnityEngine.Mesh:[10]"
        references = {asset: {("Shadow Recharge|tk2dSprite[0]", "mesh"),
                              ("Shadow Recharge/native-hierarchy", "sharedMesh")}}
        row = module.unmatched_consumers([asset], references)[0]
        self.assertTrue(row["hasObservedConsumers"])
        self.assertEqual(len(row["consumers"]), 2)

    def test_no_observed_reference_is_reported_separately(self):
        self.assertEqual(module.unmatched_consumers(["mesh"], {})[0],
                         {"asset": "mesh", "hasObservedConsumers": False, "consumers": []})


if __name__ == "__main__":
    unittest.main()
