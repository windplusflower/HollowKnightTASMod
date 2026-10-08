import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from loading_inventory import decode_roots, decode_field_schema


class InventoryTests(unittest.TestCase):
    def test_utf16_prefix_and_duplicate_records_keep_order_and_fields(self):
        document = {"rootIdentityEncoding": "sorted-utf16-prefix-root-names-v1",
                    "rootIdentities": [{"prefix": 0, "suffix": "场景/🧵/first"},
                                       {"prefix": 6, "suffix": "second"}],
                    "rootTypes": ["A", "B"],
                    "roots": [{"id": 0, "type": 0, "digest": "a", "nodes": 2},
                              {"id": 1, "type": 0, "digest": "b", "nodes": 3},
                              {"id": 0, "type": 1, "digest": "c", "nodes": 4}]}
        roots = decode_roots(document)
        self.assertEqual([row["id"] for row in roots], ["场景/🧵/first", "场景/🧵/second", "场景/🧵/first"])
        self.assertEqual([(row["type"], row["digest"], row["nodes"]) for row in roots],
                         [("A", "a", 2), ("A", "b", 3), ("B", "c", 4)])
        self.assertEqual(document["roots"][0]["id"], 0)

    def test_bad_binding_indices_fail(self):
        document = {"rootIdentityEncoding": "sorted-utf16-prefix-root-names-v1",
                    "rootIdentities": [], "rootTypes": [], "roots": [{"id": 0}]}
        with self.assertRaises(ValueError):
            decode_roots(document)

    def test_field_schema_preserves_private_declaring_owners(self):
        schema = {"encoding": "interned-field-schema-v2", "names": ["Base", "Derived", "System.Int32"],
                  "types": [{"type": 1, "fields": [{"owner": 0, "field": "privateValue", "type": 2}]}]}
        self.assertEqual(decode_field_schema(schema), [{"type": "Derived", "fields": [
            {"owner": "Base", "field": "privateValue", "type": "System.Int32"}]}])


if __name__ == "__main__":
    unittest.main()
