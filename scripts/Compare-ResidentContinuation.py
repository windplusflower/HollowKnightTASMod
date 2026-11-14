"""Verify two sealed Resident continuation traces against the source IPC receipt."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import re
import struct
import sys
import unicodedata

SCHEMA = "resident-continuation-trace-v1"
GROUPS = ("admission", "assetManifest", "assetValues", "clock", "componentNative", "directory", "input",
          "managedGraph", "objectNative", "process", "rng", "scheduler", "staticRoots", "typeManifest")
MAX_LINE = 1024 * 1024
MAX_FILE = 2 * 1024 * 1024 * 1024
MAX_LONG = (1 << 63) - 1
HEADER = "kind schema traceId role processId processStartUtcTicks processNonce packetSchema packetSha256 packetBytes movieSha256 saveSha256 buildSha256 boundaryOrdinal sourceScene targetScene startNative startMovie startConsumed endMovie movieLength consumedSemantics groups".split()
SAMPLE = "kind sequence phase completedNative completedMovie consumedSamples inputReady boundary scene k samplesComplete worldSha256 groups".split()
GROUP = "name bytes sha256 expectedCount expectedKeysSha256 observedCount observedKeysSha256 gaps".split()
FOOTER = "kind schema traceId headerSha256 bodySha256 rows pairs loadingFrames inputFrames kCount samplesComplete endNative endMovie endConsumed movieLength".split()
LOCAL_HEADER = {"traceId", "role", "processId", "processStartUtcTicks", "processNonce"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def fields(value, keys, what):
    require(type(value) is dict and set(value) == set(keys), f"{what} field set differs")


def integer(value, low, high, what):
    require(type(value) is int and low <= value <= high, f"invalid {what}")


def label(value, what, allow_empty=False):
    require(type(value) is str and (allow_empty or value.strip()) and len(value.encode("utf-8")) <= 16384
            and not any(unicodedata.category(c) == "Cc" for c in value), f"invalid {what}")


def digest(value, what):
    require(type(value) is str and re.fullmatch(r"[0-9a-f]{64}", value), f"invalid {what} SHA256")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, f"duplicate JSON field {key!r}")
        result[key] = value
    return result


def parse_line(raw):
    require(raw and len(raw) <= MAX_LINE and raw.endswith(b"\n") and not raw.endswith(b"\r\n"), "incomplete or oversized UTF8/LF record")
    def bad_constant(value):
        raise ValueError(f"invalid JSON number {value}")
    return json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object, parse_constant=bad_constant)


def number(value):
    return struct.pack("<i", value)


def text(value):
    payload = value.encode("utf-8")
    return number(len(payload)) + payload


EMPTY_KEYS = hashlib.sha256(text("resident-keys-v1") + number(0)).hexdigest()
EMPTY_BYTES = hashlib.sha256(b"").hexdigest()


def check_groups(groups):
    require(type(groups) is list and len(groups) == len(GROUPS), "missing or extra full-world group")
    world = hashlib.sha256(text("resident-world-v1") + number(len(groups)))
    missing = []
    for expected, group in zip(GROUPS, groups):
        fields(group, GROUP, "group")
        require(group["name"] == expected, f"unexpected, missing, duplicate or reordered group {expected}")
        integer(group["bytes"], 0, (1 << 31) - 1, "group byte length")
        digest(group["sha256"], "payload")
        require((group["bytes"] == 0) == (group["sha256"] == EMPTY_BYTES), "empty payload identity differs")
        for direction in ("expected", "observed"):
            count, keys = group[direction + "Count"], group[direction + "KeysSha256"]
            integer(count, 0, 1000000, "group key count")
            digest(keys, "group key directory")
            require((count == 0) == (keys == EMPTY_KEYS), "empty key directory identity differs")
        gaps = group["gaps"]
        require(type(gaps) is list and len(gaps) <= 4096, "invalid coverage gap list")
        for gap in gaps:
            label(gap, "coverage gap")
        require(gaps == sorted(set(gaps), key=lambda value: value.encode("utf-16-be")), "coverage gaps duplicated or unordered")
        require(group["bytes"] != 0 or gaps, "missing payload has no declared reason")
        if gaps or not group["bytes"] or group["expectedCount"] != group["observedCount"] or group["expectedKeysSha256"] != group["observedKeysSha256"]:
            missing.append(expected)
        world.update(text(expected) + number(group["bytes"]) + text(group["sha256"]))
        world.update(number(group["expectedCount"]) + text(group["expectedKeysSha256"]))
        world.update(number(group["observedCount"]) + text(group["observedKeysSha256"]) + number(len(gaps)))
        for gap in gaps:
            world.update(text(gap))
    return world.hexdigest(), missing


class Trace:
    def __init__(self, file, role):
        self.file = file
        self.size = 0
        raw = self.line()
        header = self.header = parse_line(raw)
        fields(header, HEADER, "header")
        require(header["kind"] == "header" and header["schema"] == SCHEMA and header["packetSchema"] == "resident-boundary-v2", "unknown trace or packet schema")
        require(header["role"] == role and header["groups"] == list(GROUPS), "trace role or full-world group protocol differs")
        require(header["consumedSemantics"] == "current-frame-sample-index-before-next-prepare-v1", "unknown input sample cursor semantics")
        require(type(header["traceId"]) is str and re.fullmatch(r"[0-9a-f]{32}", header["traceId"]), "invalid trace ID")
        integer(header["processId"], 1, (1 << 31) - 1, "process ID")
        integer(header["processStartUtcTicks"], 1, MAX_LONG, "process start")
        process_id = f"resident-process-v1\n{header['processId']}\n{header['processStartUtcTicks']}".encode("ascii")
        require(header["processNonce"] == hashlib.sha256(process_id).hexdigest(), "process identity receipt differs")
        for name in ("packetSha256", "movieSha256", "saveSha256", "buildSha256"):
            digest(header[name], name)
        integer(header["packetBytes"], 1, 256 * 1024 * 1024, "packet byte length")
        integer(header["boundaryOrdinal"], 1, (1 << 31) - 1, "boundary ordinal")
        for name in ("sourceScene", "targetScene"):
            label(header[name], name)
        for name in ("startNative", "startMovie", "startConsumed", "endMovie", "movieLength"):
            integer(header[name], 0, MAX_LONG, name)
        require(header["startMovie"] < header["endMovie"] <= header["movieLength"], "invalid declared Movie interval")
        self.hash = hashlib.sha256(raw)
        self.header_hash = self.hash.hexdigest()
        self.footer = None
        self.last_after_scene = None

    def line(self):
        raw = self.file.readline(MAX_LINE + 1)
        self.size += len(raw)
        require(self.size <= MAX_FILE, "trace exceeds the bounded file size")
        return raw

    def samples(self):
        h = self.header
        native, movie, consumed = h["startNative"], h["startMovie"], h["startConsumed"]
        next_before_consumed = consumed
        rows = loading = inputs = 0
        all_complete = True
        pending = None
        while True:
            raw = self.line()
            row = parse_line(raw)
            require(type(row) is dict, "non-object trace record")
            if row.get("kind") == "footer":
                fields(row, FOOTER, "footer")
                require(row["schema"] == SCHEMA and row["traceId"] == h["traceId"] and row["headerSha256"] == self.header_hash
                        and row["bodySha256"] == self.hash.hexdigest(), "footer seal differs")
                require(rows and pending is None and movie == h["endMovie"], "trace sealed at an incomplete pair or wrong endpoint")
                expected = dict(rows=rows, pairs=native - h["startNative"], loadingFrames=loading, inputFrames=inputs, kCount=min(3, inputs),
                                endNative=native, endMovie=movie, endConsumed=consumed, movieLength=h["movieLength"])
                for key, value in expected.items():
                    integer(row[key], 0, MAX_LONG, key)
                    require(row[key] == value, f"footer {key} differs from observed frames")
                require(row["samplesComplete"] is all_complete and not self.file.read(1), "footer coverage or end of file differs")
                self.footer = row
                return
            fields(row, SAMPLE, "sample")
            require(row["kind"] == "sample" and row["phase"] == ("before" if pending is None else "after"), "missing or reordered before/after sample")
            require(movie < h["endMovie"], "sample follows the requested Movie endpoint")
            for key in ("sequence", "completedNative", "completedMovie", "consumedSamples", "k"):
                integer(row[key], 0, MAX_LONG, key)
            require(row["sequence"] == rows and type(row["inputReady"]) is bool, "sequence or input admission differs")
            label(row["boundary"], "boundary")
            label(row["scene"], "scene", allow_empty=True)
            ready = row["inputReady"]
            marker = 0
            in_k3 = inputs < 3
            if pending is None:
                require((row["completedNative"], row["completedMovie"], row["consumedSamples"]) == (native, movie, next_before_consumed), "before native/Movie or prepared sample cursor differs")
                pending = (ready, row["boundary"], row["consumedSamples"])
            else:
                require(pending[:2] == (ready, row["boundary"]), "before/after input admission differs")
                require(row["completedNative"] == native + 1 and row["completedMovie"] == movie + int(ready), "native/Movie after cursor differs")
                require(row["consumedSamples"] >= pending[2] if ready else row["consumedSamples"] == pending[2], "input consumption within the native frame differs")
                native, movie, consumed = row["completedNative"], row["completedMovie"], row["consumedSamples"]
                next_before_consumed = 0 if ready else consumed
                if ready:
                    inputs += 1
                    marker = inputs if inputs <= 3 else 0
                else:
                    loading += 1
                pending = None
                self.last_after_scene = row["scene"]
            require(row["k"] == marker, "K marker is not the after point of its input-consuming native frame")
            world, missing = check_groups(row["groups"])
            require(row["worldSha256"] == world, "whole-world hash differs from complete group directory")
            require(row["samplesComplete"] is (not missing), "claimed sample coverage differs from group evidence")
            self.hash.update(raw)
            all_complete = all_complete and not missing
            rows += 1
            yield row, in_k3, missing


def result_template():
    return dict(schema="resident-continuation-comparison-v1", tracesVerified=False, k3ExactPassed=False, loadingExactPassed=False,
                fullSuffixExactPassed=False, completeStateCoverage=False, restoreQualified=False)


def read_receipt(path):
    with Path(path).open("rb") as file:
        raw = file.read(MAX_LINE + 1)
    require(0 < len(raw) <= MAX_LINE, "invalid IPC receipt size")
    data = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object)
    require(type(data) is dict, "invalid IPC receipt")
    return data


def receipt_integer(receipt, key):
    value = receipt.get(key)
    require(type(value) is str and re.fullmatch(r"0|[1-9][0-9]{0,18}", value), f"invalid IPC receipt integer {key}")
    parsed = int(value)
    integer(parsed, 0, MAX_LONG, key)
    return parsed


def check_boundary(receipt, header):
    require(receipt.get("schema") == header["packetSchema"] and receipt.get("sha256") == header["packetSha256"], "IPC source schema/packet identity differs")
    for actual, declared in (("length", "packetBytes"), ("sourceOrdinal", "boundaryOrdinal"), ("nativeFrame", "startNative"),
                             ("movieFrame", "startMovie"), ("consumedSamples", "startConsumed")):
        require(receipt_integer(receipt, actual) == header[declared], f"IPC source boundary {actual} differs")
    for key in ("sourceScene", "targetScene"):
        require(receipt.get(key) == header[key], f"IPC source boundary {key} differs")


def check_endpoint(receipt, trace):
    require(receipt.get("paused") == "true" and receipt.get("faultCode") == "0" and receipt.get("mismatchCount") == "0" and receipt.get("error") == "",
            "IPC endpoint did not park cleanly")
    require(receipt_integer(receipt, "nativeFrame") == trace.footer["endNative"] and receipt_integer(receipt, "movieFrame") == trace.footer["endMovie"],
            "IPC endpoint differs from the sealed native/Movie cursor")
    require(receipt.get("sceneName") == trace.last_after_scene, "IPC endpoint scene differs from the final after sample")


def compare(source, target, packet_sha256, packet_bytes, end_movie, source_header, source_endpoint, target_endpoint):
    digest(packet_sha256, "source IPC packet")
    integer(packet_bytes, 1, 256 * 1024 * 1024, "source IPC packet length")
    integer(end_movie, 1, MAX_LONG, "requested endpoint")
    source, target = Path(source), Path(target)
    require(not source.samefile(target), "source and target are the same file")
    report = result_template()
    with source.open("rb") as a, target.open("rb") as b:
        left, right = Trace(a, "source"), Trace(b, "target")
        h, other = left.header, right.header
        require(h["traceId"] != other["traceId"] and h["processNonce"] != other["processNonce"], "traces are not independent source and target processes")
        for key in set(HEADER) - LOCAL_HEADER:
            require(h[key] == other[key], f"source and target headers differ at {key}")
        require((h["packetSha256"], h["packetBytes"], h["endMovie"]) == (packet_sha256, packet_bytes, end_movie), "trace identity does not match the source IPC packet or requested endpoint")
        check_boundary(read_receipt(source_header), h)
        exact = dict(k3=True, loading=True, suffix=True)
        all_covered = same_timeline = True
        differences, gap_rows, compared = [], 0, 0
        for index, (s, t) in enumerate(itertools.zip_longest(left.samples(), right.samples())):
            sr, tr = s[0] if s else None, t[0] if t else None
            scopes = ["suffix"]
            if (s and s[1]) or (t and t[1]):
                scopes.append("k3")
            if (sr and not sr["inputReady"]) or (tr and not tr["inputReady"]):
                scopes.append("loading")
            missing = s is None or t is None
            gaps = [*(s[2] if s else []), *(t[2] if t else [])]
            unequal = sr != tr
            if missing or any(sr[key] != tr[key] for key in ("phase", "completedNative", "completedMovie", "consumedSamples", "inputReady", "k")):
                same_timeline = False
            if gaps or missing:
                gap_rows += 1
                all_covered = False
            if unequal or gaps or missing:
                for scope in scopes:
                    exact[scope] = False
                if len(differences) < 16:
                    keys = ["missing sample"] if missing else [key for key in SAMPLE if sr[key] != tr[key]]
                    domains = [] if missing else [x["name"] for x, y in zip(sr["groups"], tr["groups"]) if x != y]
                    differences.append(dict(sequence=index, fields=keys, differingGroups=domains, incompleteGroups=sorted(set(gaps))))
            compared += 1
        lf, rf = left.footer, right.footer
        require(lf is not None and rf is not None, "both traces must be independently sealed")
        check_endpoint(read_receipt(source_endpoint), left)
        check_endpoint(read_receipt(target_endpoint), right)
        full_window = end_movie == h["movieLength"]
        same_end = all(lf[k] == rf[k] for k in ("endNative", "endMovie", "endConsumed", "pairs", "loadingFrames", "inputFrames"))
        report.update(tracesVerified=True, k3ExactPassed=exact["k3"] and lf["kCount"] == rf["kCount"] == 3,
                      loadingExactPassed=exact["loading"] and lf["loadingFrames"] == rf["loadingFrames"] and lf["loadingFrames"] > 0,
                      fullSuffixExactPassed=exact["suffix"] and full_window and same_end,
                      completeStateCoverage=all_covered and same_timeline and full_window and same_end,
                      fullMovieWindow=full_window, sourcePairs=lf["pairs"], targetPairs=rf["pairs"], comparedSamplePositions=compared,
                      incompleteSamplePositions=gap_rows, differences=differences,
                      packetSha256=packet_sha256, packetBytes=packet_bytes, movieSha256=h["movieSha256"], saveSha256=h["saveSha256"],
                      buildSha256=h["buildSha256"], boundaryOrdinal=h["boundaryOrdinal"], startNative=h["startNative"], startMovie=h["startMovie"],
                      startConsumed=h["startConsumed"], sourceScene=h["sourceScene"], targetScene=h["targetScene"],
                      endMovie=end_movie, sourceEndNative=lf["endNative"], targetEndNative=rf["endNative"],
                      sourceEndConsumed=lf["endConsumed"], targetEndConsumed=rf["endConsumed"], sourceSeal=lf["bodySha256"], targetSeal=rf["bodySha256"])
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--target", type=Path, required=True)
    parser.add_argument("--packet-sha256", required=True)
    parser.add_argument("--packet-bytes", type=int, required=True)
    parser.add_argument("--end-movie", type=int, required=True)
    parser.add_argument("--source-header", type=Path, required=True)
    parser.add_argument("--source-endpoint", type=Path, required=True)
    parser.add_argument("--target-endpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        report = compare(args.source, args.target, args.packet_sha256, args.packet_bytes, args.end_movie,
                         args.source_header, args.source_endpoint, args.target_endpoint)
    except (ValueError, OSError, OverflowError, RecursionError) as error:
        report = result_template()
        report["error"] = str(error)
    try:
        with args.output.open("x", encoding="utf-8", newline="\n") as report_file:
            json.dump(report, report_file, ensure_ascii=False, indent=2)
            report_file.write("\n")
    except OSError as error:
        print(f"Could not create fresh comparison report: {error}", file=sys.stderr)
        return 2
    return 0 if all(report[key] for key in ("k3ExactPassed", "loadingExactPassed", "fullSuffixExactPassed", "completeStateCoverage")) else 1


if __name__ == "__main__":
    raise SystemExit(main())
