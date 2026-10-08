"""Verify mixed-FPS StudioScenarioHarness real-game exports."""
import argparse
import json
import subprocess
from collections import Counter
from fractions import Fraction
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    parser.add_argument("--ffprobe", type=Path, required=True)
    parser.add_argument("--ffmpeg", type=Path, required=True)
    parser.add_argument("--movie", default="mixed.hktas")
    parser.add_argument("--video", default="mixed.mp4")
    args = parser.parse_args()
    root = args.output.resolve()
    states = json.loads((root / "states.json").read_text(encoding="utf-8-sig"))
    state = next(x["state"] for x in states if x["label"] == "export")
    assert state["videoExport.state"] == "Completed" and state["mismatchCount"] == "0"
    runs = [json.loads(line) for line in (root / args.movie).read_text(encoding="utf-8-sig").splitlines()[1:]]
    expected = Counter()
    movie_rates = []
    for run in runs:
        rate = Fraction(run.get("fps", 50), run.get("fpsDenominator", 1))
        expected[rate] += run["repeatCount"]
        movie_rates.extend([rate] * run["repeatCount"])
    path = root / args.video
    raw = subprocess.check_output([str(args.ffprobe), "-v", "error", "-show_streams", "-show_packets", "-of", "json", str(path)])
    (root / (path.stem + ".probe.json")).write_bytes(raw)
    data = json.loads(raw)
    video = next(s for s in data["streams"] if s["codec_type"] == "video")
    audio = next(s for s in data["streams"] if s["codec_type"] == "audio")
    packets = [p for p in data["packets"] if p["stream_index"] == video["index"]]
    assert len(packets) == int(state["videoExport.frames"])
    loading = len(packets) - sum(expected.values())
    assert loading >= 0
    expected[Fraction(50)] += loading
    time_base = Fraction(video["time_base"])
    # These neutral title-screen scenarios have a loading prefix followed by
    # the authored Movie. Match ordered cumulative timestamps: classifying
    # individual rounded packets cannot distinguish 100 from 99.999 FPS.
    rates = [Fraction(50)] * loading + movie_rates
    end = 0
    expected_end = Fraction(0)
    for packet, fps in zip(packets, rates, strict=True):
        assert int(packet["pts"]) == end, (packet, end)
        duration = int(packet["duration"])
        seconds = duration * time_base
        assert abs(seconds - 1 / fps) < Fraction(2, 1000000), (fps, seconds)
        end += duration
        expected_end += 1 / fps
        assert abs(end * time_base - expected_end) < Fraction(2, 1000000), (fps, end * time_base, expected_end)
    assert abs(float(end * time_base) - float(state["videoExport.durationSeconds"])) < 0.000002
    assert abs(float(video["duration"]) - float(audio["duration"])) < 0.001
    assert float(state["videoExport.maximumAudioPeak"]) > 0
    decoded = subprocess.run([str(args.ffmpeg), "-v", "error", "-xerror", "-i", str(path),
                              "-fps_mode:v", "passthrough", "-enc_time_base:v", "1:1000000",
                              "-f", "null", "-"], check=True, capture_output=True)
    assert not decoded.stderr, decoded.stderr.decode(errors="replace")
    result = dict(status="PASS", file=str(path), frames=len(packets), loading_frames=loading,
                  frame_rate_counts={str(k): v for k, v in expected.items()},
                  video_seconds=video["duration"], audio_seconds=audio["duration"], decode="PASS")
    (root / "media-verification.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
