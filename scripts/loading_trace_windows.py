"""Validate a declared oracle scope without dropping any captured state values."""


def gameplay_windows(header):
    first, end = header["first"], header["end"]
    if type(first) is not int or type(end) is not int or not 0 <= first < end <= first + 10000:
        raise ValueError("invalid bounded window")
    scope = header.get("samplingScope")
    if scope in (None, "bounded-window", "serial-candidate-boundaries-only-diagnostic"):
        if header.get("gameplayWindows") is not None:
            raise ValueError("gameplay windows require the transition-window scope")
        return [(first, end)]
    if scope != "bounded-transition-windows-v1":
        raise ValueError("unknown sampling scope")
    declared = header.get("gameplayWindows")
    if not isinstance(declared, list) or not declared:
        raise ValueError("empty or missing gameplay windows")
    result = []
    prior_end = first
    for window in declared:
        if not isinstance(window, dict) or set(window) != {"first", "end"}:
            raise ValueError("invalid gameplay window declaration")
        start, stop = window["first"], window["end"]
        if type(start) is not int or type(stop) is not int or not prior_end <= start < stop <= end:
            raise ValueError("unordered, overlapping or out-of-range gameplay windows")
        result.append((start, stop))
        prior_end = stop
    return result


def expected_gameplay_samples(header):
    return [(frame, phase) for first, end in gameplay_windows(header)
            for frame in range(first, end) for phase in ("before", "after")]


def validate_sample_scope(header, rows):
    windows = gameplay_windows(header)
    for row in rows:
        frame = row.get("movieFrame")
        if type(frame) is not int or not any(first <= frame < end for first, end in windows):
            raise ValueError("sample outside declared gameplay windows")
