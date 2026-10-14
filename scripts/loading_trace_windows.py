"""Validate a declared oracle scope without dropping any captured state values."""


def gameplay_windows(header):
    first, end = header["first"], header["end"]
    if type(first) is not int or type(end) is not int or not 0 <= first < end <= first + 10000:
        raise ValueError("invalid bounded window")
    scope = header.get("samplingScope")
    if scope == "actual-d-suffix-all-loaded-three-after-v1" and end - first != 3:
        raise ValueError("actual D suffix requires exactly three Movie frames")
    if scope in (None, "bounded-window", "serial-candidate-boundaries-only-diagnostic", "actual-d-suffix-all-loaded-three-after-v1"):
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
    phases = ("after",) if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1" else ("before", "after")
    return [(frame, phase) for first, end in gameplay_windows(header)
            for frame in range(first, end) for phase in phases]


def validate_sample_scope(header, rows):
    windows = gameplay_windows(header)
    for index, row in enumerate(rows):
        frame = row.get("movieFrame")
        if type(frame) is not int or not any(first <= frame < end for first, end in windows):
            raise ValueError("sample outside declared gameplay windows")
        if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1" and row.get("state", {}).get("inventoryScope") != "all-loaded":
            raise ValueError("actual D suffix requires full inventory at every sample")
        if header.get("samplingScope") == "actual-d-suffix-all-loaded-three-after-v1":
            if row.get("phase") != "after" or row.get("suffixOffset") != index or row.get("movieFrame") != header["first"] + index:
                raise ValueError("actual D suffix has missing duplicate or reordered after points")
            if type(row.get("nativeFrame")) is not int or not isinstance(row.get("inventory"), dict) or not isinstance(row["inventory"].get("roots"), list) or not row["inventory"]["roots"]:
                raise ValueError("actual D suffix lacks native cursor or complete root inventory")
