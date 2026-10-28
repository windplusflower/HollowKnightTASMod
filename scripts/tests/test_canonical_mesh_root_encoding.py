"""The Mesh analyzer decodes the real runtime encoder's zero-based boundary IDs."""
import importlib.util
import base64
import gzip
import hashlib
import json
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("mesh_owners", ROOT / "scripts/Analyze-CanonicalMeshOwners.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MeshRootEncodingTest(unittest.TestCase):
    def test_production_encoder_first_and_last_zero_based_roots(self):
        completed = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                                    str(ROOT / "scripts/Test-CanonicalMeshRootEncoding.ps1")],
                                   check=True, capture_output=True, text=True, encoding="utf-8-sig")
        values = module.inventory(json.loads(completed.stdout))
        self.assertEqual(values["asset:UnityEngine.Mesh:[0]/asset-values"]["digest"], "first")
        self.assertEqual(values["asset:UnityEngine.Mesh:[9]/asset-values"]["digest"], "last")

        row = json.loads(completed.stdout)
        content = json.loads(gzip.decompress(base64.b64decode(row["inventory"]["gzipBase64"])))
        for invalid in (-1, len(content["names"])):
            content["roots"][0]["id"] = invalid
            raw = json.dumps(content, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
            changed = {"inventory": {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest(),
                                     "gzipBase64": base64.b64encode(gzip.compress(raw)).decode("ascii")}}
            with self.assertRaises(ValueError):
                module.inventory(changed)


if __name__ == "__main__":
    unittest.main()
