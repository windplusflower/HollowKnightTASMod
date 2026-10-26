"""Explicit diagnostic derivation; never migrates a user sequence in place."""
import argparse
import hashlib
import json
from pathlib import Path
import zipfile

OLD = "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v13-experimental"
NEW = "hktas-unity-input-playerloop-serial-scene-canonical-boundary-2026-v14-experimental"

def sha(data):
    return hashlib.sha256(data).hexdigest()

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("destination")
    parser.add_argument('--source-boundary-ordinal', type=int, required=True, choices=range(2, 65))
    parser.add_argument('--target-profile', choices=('v14', 'v15'), default='v14')
    args = parser.parse_args()
    new_profile = NEW if args.target_profile == 'v14' else NEW.replace('-v14-', '-v15-')
    source, destination = Path(args.source).resolve(), Path(args.destination).resolve()
    owned = Path(__file__).resolve().parent.parent / "artifacts/scene-checkpoints/p3"
    if not destination.is_relative_to(owned) or destination == source or destination.exists():
        raise ValueError("Choose a fresh diagnostic fixture under the owned P3 directory")
    original_pack = source.read_bytes()
    with zipfile.ZipFile(source) as archive:
        original_movie = archive.read("movie.hktas")
        header_line, body = original_movie.split(b"\n", 1)
        header = json.loads(header_line)
        if header["nativeProfileId"] != OLD:
            raise ValueError("Source is not the explicit v13 diagnostic source")
        if sum(json.loads(line)["repeatCount"] for line in body.splitlines() if line.strip()) != 3150:
            raise ValueError("Expected the unchanged 3150 input frames")
        new_header_line = header_line.replace(OLD.encode(), new_profile.encode())
        if json.loads(new_header_line) != dict(header, nativeProfileId=new_profile):
            raise ValueError("Header replacement changed unrelated metadata")
        derived_movie = new_header_line + b"\n" + body
        manifest = json.loads(archive.read("manifest.json"))
        if manifest["MovieSha256"] != sha(original_movie):
            raise ValueError("Source movie binding differs")
        manifest["MovieSha256"] = sha(derived_movie)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(destination, "x") as output:
            for entry in archive.infolist():
                data = archive.read(entry.filename)
                if entry.filename == "movie.hktas":
                    data = derived_movie
                elif entry.filename == "manifest.json":
                    data = json.dumps(manifest, separators=(",", ":"), ensure_ascii=False).encode()
                output.writestr(entry, data)
    if source.read_bytes() != original_pack:
        raise ValueError("Original sequence changed")
    evidence = dict(schema="canonical-retired-ui-derived-diagnostic-v1" if args.target_profile == 'v14' else "canonical-boss-context-derived-diagnostic-v1", diagnosticOnly=True,
        newlyRecorded=False, authoritativeNewProfileExpectations=False, frames=3150, sourceBoundaryOrdinal=args.source_boundary_ordinal,
        source=str(source), destination=str(destination), originalProfile=OLD, derivedProfile=new_profile,
        originalPackSha256=sha(original_pack), derivedPackSha256=sha(destination.read_bytes()),
        originalMovieSha256=sha(original_movie), derivedMovieSha256=sha(derived_movie),
        unchangedInputBodySha256=sha(body), initialSavesUnchanged=True,
        changedHeaderFields=["nativeProfileId"], changedPackageFields=["MovieSha256"],
        oldExpectations="retained only as additional comparison, not a newly recorded baseline")
    destination.with_suffix(".derived.json").write_text(json.dumps(evidence, indent=2), encoding="utf-8")
    print(json.dumps(evidence))

if __name__ == "__main__":
    main()

