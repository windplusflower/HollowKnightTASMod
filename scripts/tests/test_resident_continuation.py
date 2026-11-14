"""Synthetic protocol tests; no game observation or qualification artifact is produced."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("resident_compare", Path(__file__).resolve().parents[1] / "Compare-ResidentContinuation.py")
compare = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compare)
NAMES = "admission assetManifest assetValues clock componentNative directory input managedGraph objectNative process rng scheduler staticRoots typeManifest".split()
PACKET = b"fixture-source-packet"


def sha(data):
    return hashlib.sha256(data).hexdigest()


def tagged(value):
    raw = value.encode("utf-8")
    return struct.pack("<i", len(raw)) + raw


def keys(values):
    values = sorted(values, key=lambda s: s.encode("utf-16-be"))
    return sha(tagged("resident-keys-v1") + struct.pack("<i", len(values)) + b"".join(map(tagged, values)))


def groups():
    result = []
    for name in NAMES:
        data = ("abstract " + name).encode()
        directory = keys(["a", "猫/" + name])
        result.append(dict(name=name, bytes=len(data), sha256=sha(data), expectedCount=2, expectedKeysSha256=directory,
                           observedCount=2, observedKeysSha256=directory, gaps=[]))
    return result


def world(items):
    data = bytearray(tagged("resident-world-v1") + struct.pack("<i", len(items)))
    for g in items:
        data += tagged(g["name"]) + struct.pack("<i", g["bytes"]) + tagged(g["sha256"])
        for prefix in ("expected", "observed"):
            data += struct.pack("<i", g[prefix + "Count"]) + tagged(g[prefix + "KeysSha256"])
        data += struct.pack("<i", len(g["gaps"])) + b"".join(map(tagged, g["gaps"]))
    return sha(data)


def line(value):
    return (json.dumps(value, separators=(",", ":"), ensure_ascii=False) + "\n").encode()


def records(role, schedule=(False, True, True, False, True, True)):
    pid = 11 if role == "source" else 12
    ticks = 638900000000000000 + pid
    n, m, consumed = 20, 10, 17
    header = dict(kind="header", schema="resident-continuation-trace-v1", traceId=f"{pid:032x}", role=role, processId=pid,
                  processStartUtcTicks=ticks, processNonce=sha(f"resident-process-v1\n{pid}\n{ticks}".encode()), packetSchema="resident-boundary-v2",
                  packetSha256=sha(PACKET), packetBytes=len(PACKET), movieSha256=sha(b"movie"), saveSha256=sha(b"save"), buildSha256=sha(b"build"),
                  boundaryOrdinal=2, sourceScene="Source", targetScene="Target", startNative=n, startMovie=m, startConsumed=consumed,
                  endMovie=m + sum(schedule), movieLength=m + sum(schedule), consumedSemantics="current-frame-sample-index-before-next-prepare-v1", groups=list(NAMES))
    result = [header]
    previous_ready = False
    for ready in schedule:
        if previous_ready:
            consumed = 0
        for phase in ("before", "after"):
            if phase == "after":
                n += 1
                m += ready
                consumed += int(ready) * 2
            contents = groups()
            result.append(dict(kind="sample", sequence=len(result) - 1, phase=phase, completedNative=n, completedMovie=m,
                               consumedSamples=consumed, inputReady=ready, boundary="input" if ready else "load", scene="Target" if ready else "",
                               k=m - 10 if phase == "after" and ready and m <= 13 else 0, samplesComplete=True, worldSha256=world(contents), groups=contents))
        previous_ready = ready
    result.append(dict(kind="footer", schema=header["schema"], traceId=header["traceId"], headerSha256="", bodySha256="",
                       rows=len(result)-1, pairs=len(schedule), loadingFrames=schedule.count(False), inputFrames=sum(schedule), kCount=min(3, sum(schedule)),
                       samplesComplete=True, endNative=n, endMovie=m, endConsumed=consumed, movieLength=m))
    return result


def write(path, rows):
    footer = rows[-1]
    body = b"".join(map(line, rows[:-1]))
    footer["headerSha256"] = sha(line(rows[0]))
    footer["bodySha256"] = sha(body)
    path.write_bytes(body + line(footer))


class ResidentContinuationTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.source, self.target = self.root / "source", self.root / "target"
        self.header, self.se, self.te = self.root / "ipc", self.root / "source-end", self.root / "target-end"
        self.source_rows, self.target_rows = records("source"), records("target")
        self.store_pair()
        self.receipts()

    def store_pair(self):
        write(self.source, self.source_rows)
        write(self.target, self.target_rows)

    def receipts(self):
        h = self.source_rows[0]
        self.header.write_bytes(line(dict(schema=h["packetSchema"], sha256=h["packetSha256"], length=str(h["packetBytes"]), sourceOrdinal=str(h["boundaryOrdinal"]),
                                          nativeFrame=str(h["startNative"]), movieFrame=str(h["startMovie"]), consumedSamples=str(h["startConsumed"]),
                                          sourceScene=h["sourceScene"], targetScene=h["targetScene"])))
        for rows, path in ((self.source_rows, self.se), (self.target_rows, self.te)):
            f = rows[-1]
            path.write_bytes(line(dict(nativeFrame=str(f["endNative"]), movieFrame=str(f["endMovie"]), sceneName=rows[-2]["scene"],
                                       paused="true", faultCode="0", mismatchCount="0", error="")))

    def run_pair(self):
        return compare.compare(self.source, self.target, sha(PACKET), len(PACKET), self.source_rows[0]["endMovie"], self.header, self.se, self.te)

    def test_independent_complete_abstract_world(self):
        result = self.run_pair()
        for flag in ("tracesVerified", "k3ExactPassed", "loadingExactPassed", "fullSuffixExactPassed", "completeStateCoverage"):
            self.assertTrue(result[flag], flag)
        self.assertFalse(result["restoreQualified"])
        self.assertEqual(12, result["comparedSamplePositions"])
        self.assertNotEqual(result["sourceSeal"], result["targetSeal"])

    def test_late_world_difference_and_loading_difference_have_distinct_scopes(self):
        last = self.target_rows[-2]
        last["groups"][0]["sha256"] = sha(b"other")
        last["worldSha256"] = world(last["groups"])
        self.store_pair()
        report = self.run_pair()
        self.assertTrue(report["k3ExactPassed"] and report["loadingExactPassed"] and report["completeStateCoverage"])
        self.assertFalse(report["fullSuffixExactPassed"])
        self.assertEqual(["admission"], report["differences"][0]["differingGroups"])
        self.target_rows[1]["scene"] = "other"
        self.store_pair()
        report = self.run_pair()
        self.assertFalse(report["k3ExactPassed"] or report["loadingExactPassed"] or report["fullSuffixExactPassed"])

    def test_identical_hashes_with_gaps_or_missing_owner_cannot_pass(self):
        for rows in (self.source_rows, self.target_rows):
            row = rows[1]
            row["groups"][0]["gaps"] = ["unobserved producer"]
            row["groups"][1]["observedKeysSha256"] = keys(["x", "y"])
            row["worldSha256"] = world(row["groups"])
            row["samplesComplete"] = rows[-1]["samplesComplete"] = False
        self.store_pair()
        report = self.run_pair()
        self.assertFalse(any(report[k] for k in ("k3ExactPassed", "loadingExactPassed", "fullSuffixExactPassed", "completeStateCoverage")))
        self.assertEqual(1, report["incompleteSamplePositions"])
        self.target_rows[1]["samplesComplete"] = True
        self.store_pair()
        with self.assertRaisesRegex(ValueError, "claimed sample coverage"):
            self.run_pair()

    def test_resealed_missing_duplicate_reordered_or_fake_k_frame_rejects(self):
        mutations = [lambda r: r.pop(3), lambda r: r.insert(3, copy.deepcopy(r[2])), lambda r: r[2].update(sequence=0),
                     lambda r: r[2].update(phase="before"), lambda r: r[2].update(consumedSamples=18), lambda r: r[2].update(completedNative=22),
                     lambda r: r[4].update(completedMovie=10), lambda r: r[4].update(k=2), lambda r: r[4].update(boundary="changed"),
                     lambda r: r[4].update(inputReady=1), lambda r: r[5].update(consumedSamples=19), lambda r: r[-1].update(rows=13), lambda r: r[-1].update(endMovie=15)]
        original = self.target_rows
        for mutate in mutations:
            with self.subTest(mutate=mutate):
                self.target_rows = copy.deepcopy(original)
                mutate(self.target_rows)
                self.store_pair()
                with self.assertRaises(ValueError):
                    self.run_pair()

    def test_legacy_projection_omitted_group_or_wrong_world_digest_rejects(self):
        mutations = [lambda r: r[0].update(schema="hktas-loading-oracle-v1"), lambda r: r[3].update(projection="old subset"),
                     lambda r: r[3]["groups"].pop(), lambda r: r[3]["groups"].__setitem__(1, r[3]["groups"][0]),
                     lambda r: r[3].update(worldSha256="0" * 64), lambda r: r[3]["groups"][0].update(expectedCount=0),
                     lambda r: r[3]["groups"][0].update(bytes=0, sha256=sha(b"")), lambda r: r[-1].update(samplesComplete=1)]
        original = self.target_rows
        for mutate in mutations:
            self.target_rows = copy.deepcopy(original)
            mutate(self.target_rows)
            self.store_pair()
            with self.assertRaises(ValueError):
                self.run_pair()

    def test_missing_seal_trailing_data_duplicate_field_and_byte_tampering_reject(self):
        original = self.target.read_bytes()
        for broken in (original[:-1], b"\n".join(original.split(b"\n")[:-2]) + b"\n", original + b"\n", original.replace(b'"k":1', b'"k":1,"k":1', 1),
                       original.replace(b'"scene":"Target"', b'"scene":"Altered"', 1)):
            self.target.write_bytes(broken)
            with self.assertRaises(ValueError):
                self.run_pair()

    def test_boundary_and_endpoints_bound_to_real_ipc_contract(self):
        for path, key, value in ((self.header, "nativeFrame", "21"), (self.header, "sourceOrdinal", "3"), (self.header, "consumedSamples", "18"),
                                 (self.header, "sha256", "0" * 64), (self.header, "sourceScene", "other"), (self.te, "nativeFrame", "27"),
                                 (self.te, "paused", "false"), (self.te, "mismatchCount", "1"), (self.se, "sceneName", "Other")):
            self.receipts()
            data = json.loads(path.read_bytes())
            data[key] = value
            path.write_bytes(line(data))
            with self.assertRaises(ValueError):
                self.run_pair()

    def test_role_process_nonce_and_source_package_movie_save_build_must_agree(self):
        original = self.target_rows
        for key, value in (("role", "source"), ("traceId", self.source_rows[0]["traceId"]), ("packetSha256", sha(b"bad")), ("movieSha256", sha(b"bad")),
                           ("saveSha256", sha(b"bad")), ("buildSha256", sha(b"bad")), ("targetScene", "another"), ("boundaryOrdinal", 4)):
            self.target_rows = copy.deepcopy(original)
            self.target_rows[0][key] = value
            self.store_pair()
            with self.assertRaises(ValueError):
                self.run_pair()
        self.target_rows = copy.deepcopy(original)
        for key in ("processId", "processStartUtcTicks", "processNonce"):
            self.target_rows[0][key] = self.source_rows[0][key]
        self.store_pair()
        with self.assertRaisesRegex(ValueError, "independent"):
            self.run_pair()

    def test_short_suffix_and_absent_loading_or_k_do_not_grant_missing_scopes(self):
        for rows in (self.source_rows, self.target_rows):
            rows[-1]["movieLength"] = rows[0]["movieLength"] = 15
        self.store_pair()
        report = self.run_pair()
        self.assertTrue(report["k3ExactPassed"] and report["loadingExactPassed"])
        self.assertFalse(report["fullSuffixExactPassed"] or report["completeStateCoverage"])
        self.source_rows, self.target_rows = records("source", (True, True)), records("target", (True, True))
        self.store_pair()
        self.receipts()
        report = self.run_pair()
        self.assertFalse(report["k3ExactPassed"] or report["loadingExactPassed"])


if __name__ == "__main__":
    unittest.main()
