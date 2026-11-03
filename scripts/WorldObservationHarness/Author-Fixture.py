"""Build a v2 candidate by retaining a prefix and appending real action samples.

Usage: python Author-Fixture.py SOURCE DEST --prefix N 50:- 1:up,quickCast
The source supplies the observed environment header. Generated inputs require
actual game execution; writing this file alone is not a compatibility result.
"""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

ACTIONS = ("left right up down rightStickLeft rightStickRight rightStickUp "
           "rightStickDown menuSubmit menuCancel jump evade dash superDash "
           "dreamNail attack cast focus quickMap quickCast textSpeedup "
           "skipCutscene openInventory paneRight paneLeft pause").split()
INDEX = {name.lower(): i for i, name in enumerate(ACTIONS)}


def sample(held, previous):
    return dict(channel="hero", values=[32767 if i in held else 0 for i in range(26)],
                pressedMask=sum(1 << i for i in held - previous),
                releasedMask=sum(1 << i for i in previous - held), mouse=None)


def build(source, prefix, segments):
    source = Path(source)
    if source.suffix.lower() == ".hktaspack":
        with zipfile.ZipFile(source) as package:
            content = package.read("movie.hktas").decode("utf-8-sig")
    else:
        content = source.read_text(encoding="utf-8-sig")
    rows = [json.loads(line) for line in content.splitlines() if line]
    header = rows[0]
    assert header["version"] == 2 and not header["mouseEnabled"]
    if prefix < 0:
        raise ValueError("Prefix must not be negative")
    runs = []
    remaining = prefix
    for row in rows[1:]:
        if remaining <= 0:
            break
        take = min(remaining, row["repeatCount"])
        runs.append(dict(row, repeatCount=take))
        remaining -= take
    if remaining:
        raise ValueError("Prefix exceeds source Movie")
    held = {i for i, v in enumerate(runs[-1]["samples"][-1]["values"]) if v} if runs else set()

    def append(count, samples):
        if not count:
            return
        if runs and runs[-1].get("authored") is True and runs[-1].get("fps") == 50 and runs[-1]["samples"] == samples:
            runs[-1]["repeatCount"] += count
        else:
            # An authored sample supplies channel state. It does not pretend
            # to know the native callback count; RecordInputs observes that.
            runs.append(dict(repeatCount=count, fps=50, authored=True, samples=samples))

    for segment in segments:
        count, names = segment.split(":", 1)
        count = int(count)
        if count < 1:
            raise ValueError("Each action segment must have positive length")
        next_held = set() if names == "-" else {INDEX[n.lower()] for n in names.split(",")}
        append(1, [sample(next_held, held)])
        append(count - 1, [sample(next_held, next_held)])
        held = next_held
    return [header] + runs


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("destination")
    parser.add_argument("--prefix", type=int, required=True)
    parser.add_argument("segments", nargs="*")
    args = parser.parse_intermixed_args()
    result = build(args.source, args.prefix, args.segments)
    destination = Path(args.destination)
    if destination.exists():
        parser.error("Refusing to overwrite an existing fixture")
    content = ("\n".join(json.dumps(x, ensure_ascii=False, separators=(",", ":")) for x in result) + "\n").encode("utf-8")
    if destination.suffix.lower() == ".hktaspack":
        if Path(args.source).suffix.lower() != ".hktaspack":
            parser.error("A package output requires a package source with original initial saves")
        with zipfile.ZipFile(args.source) as source, zipfile.ZipFile(destination, "x", zipfile.ZIP_DEFLATED) as output:
            manifest = json.loads(source.read("manifest.json"))
            manifest["MovieSha256"] = hashlib.sha256(content).hexdigest()
            for entry in source.infolist():
                data = content if entry.filename == "movie.hktas" else json.dumps(manifest, separators=(",", ":")).encode("utf-8") if entry.filename == "manifest.json" else source.read(entry)
                output.writestr(entry, data)
    else:
        destination.write_bytes(content)
    print(json.dumps({"path": str(Path(args.destination).resolve()), "frames": sum(x["repeatCount"] for x in result[1:])}))
