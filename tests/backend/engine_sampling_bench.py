#!/usr/bin/env python3
"""Measure what sampling flags do to ffsubsync's audio alignment: cost and accuracy.

One command per flag set, one JSON file of evidence. Written for the repo scan of
2026-09-15 (candidates C1 multi-segment sampling, C2 piecewise alignment).

    python3 tests/backend/engine_sampling_bench.py \
        --media "/opt/data/jf12test/media-slow/Helikopterrånet S01E01.mkv" \
        --truth "/opt/data/jf12test/media-slow/Helikopterrånet S01E01.SYNCED.ukr.srt" \
        --shift -7500 [--second-half-shift 12500] [--shim] [--cases ...]

Accuracy is measured against the truth subtitle, not against what the engine says:
the input is the truth shifted by a known amount, so the right answer is known, and
the output is compared cue by cue with the truth. Cost is wall clock, which under the
shim is the shim's own sleeps - i.e. the bytes and round trips the engine asked for.
"""

import argparse
import json
import os
import re
import shlex
import subprocess
import sys
import time
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DEFAULT_ENGINE = "/opt/data/jf12test/data/data/subsync/venv/bin/ffsubsync"
SHIM = REPO / "tests/backend/slowread.so"
FFMPEG = "/opt/data/bin/ffmpeg"

CUE = re.compile(
    r"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2})[,.](\d{3})"
)


def ms(h, m, s, milli):
    return ((int(h) * 60 + int(m)) * 60 + int(s)) * 1000 + int(milli)


def read_cues(path):
    cues = []
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    for block in re.split(r"\r?\n\r?\n", text):
        match = CUE.search(block)
        if match:
            g = match.groups()
            cues.append((ms(*g[:4]), ms(*g[4:])))
    return cues


def write_shifted(path, cues, shift_ms, second_half_shift_ms):
    """Write cues with a (possibly piecewise) shift applied."""
    half = cues[0][0] + (cues[-1][0] - cues[0][0]) / 2.0
    out = []
    for i, (start, end) in enumerate(cues):
        delta = shift_ms if (second_half_shift_ms is None or start < half) else second_half_shift_ms
        out.append((start + delta, end + delta, i))
    out.sort(key=lambda c: c[0])
    lines = []
    for n, (start, end, _) in enumerate(out, 1):
        lines.append(
            "%d\n%s --> %s\nline %d\n"
            % (n, stamp(start), stamp(end), n)
        )
    Path(path).write_text("\n".join(lines), encoding="utf-8")
    return out


def stamp(ms_value):
    ms_value = max(0, int(ms_value))
    h, rest = divmod(ms_value, 3600000)
    m, rest = divmod(rest, 60000)
    s, milli = divmod(rest, 1000)
    return "%02d:%02d:%02d,%03d" % (h, m, s, milli)


def median(values):
    if not values:
        return None
    ordered = sorted(values)
    n = len(ordered)
    return ordered[n // 2] if n % 2 else (ordered[n // 2 - 1] + ordered[n // 2]) / 2.0


def compare(out_cues, truth_cues):
    """Median absolute per-cue displacement of out against truth, in ms."""
    n = min(len(out_cues), len(truth_cues))
    if n == 0:
        return None, None, None
    diffs = [out_cues[i][0] - truth_cues[i][0] for i in range(n)]
    half = n // 2
    return median([abs(d) for d in diffs]), median(diffs[:half]), median(diffs[half:])


CASES = {
    # name: extra engine arguments
    "whole": [],
    "ms8": ["--multi-segment-sync", "--segment-count", "8"],
    "ms8-p1": ["--multi-segment-sync", "--segment-count", "8", "--parallel-workers", "1"],
    "ms4-p1": ["--multi-segment-sync", "--segment-count", "4", "--parallel-workers", "1"],
    "ms16-p1": ["--multi-segment-sync", "--segment-count", "16", "--parallel-workers", "1"],
    "ms8-intro": ["--multi-segment-sync", "--segment-count", "8", "--skip-intro-outro", "--parallel-workers", "1"],
    "split6": ["--split-penalty", "6"],
    "split6-ms8": ["--split-penalty", "6", "--multi-segment-sync", "--segment-count", "8", "--parallel-workers", "1"],
    "split20": ["--split-penalty", "20"],
    "lowq": ["--skip-sync-on-low-quality"],
}

# One reader per job, like the plugin passes: the sampling count is the variable being measured.
for _segments in (2, 6, 10, 12, 16, 20, 24, 32, 48, 64):
    CASES["ms%d" % _segments] = [
        "--multi-segment-sync", "--segment-count", str(_segments), "--parallel-workers", "1"]

# Shorthand for suffixes in a "case+shorthand" spec.
ALIASES = {
    "nofix": ["--no-fix-framerate"],
    "gss": ["--gss"],
    "intro": ["--skip-intro-outro"],
    "p4": ["--parallel-workers", "4"],
    "split6": ["--split-penalty", "6"],
    "split20": ["--split-penalty", "20"],
    "lowq": ["--skip-sync-on-low-quality"],
}


def parse_case(spec):
    """Turn a case spec into (name, args).

    Forms: "whole" (a named set below), "whole+--no-fix-framerate" (a named set plus extra
    arguments), "custom=--multi-segment-sync --segment-count 12" (arguments only).
    """
    if "=" in spec:
        name, raw = spec.split("=", 1)
        return name, shlex.split(raw)
    if "+" in spec:
        name, raw = spec.split("+", 1)
        extras = []
        for token in shlex.split(raw):
            extras.extend(ALIASES.get(token, [token]))
        return name, CASES.get(name, []) + extras
    return spec, CASES[spec]


def run_case(engine, name, extra_args, media, work, shim_env, max_offset, timeout):
    args = [
        engine,
        media,
        "-i", str(work["input"]),
        "-o", str(work["out"]),
        "--max-offset-seconds", str(max_offset),
        "--max-subtitle-seconds", "10",
        "--vad", "webrtc",
        "--ffmpeg-path", FFMPEG,
    ] + extra_args
    env = dict(os.environ)
    env.update(shim_env)
    started = time.monotonic()
    try:
        proc = subprocess.run(args, capture_output=True, text=True, timeout=timeout, env=env)
        code, stderr, timed_out = proc.returncode, proc.stderr, False
    except subprocess.TimeoutExpired as exc:
        code, stderr, timed_out = -1, (exc.stderr or b"").decode("utf-8", "replace"), True
    elapsed = time.monotonic() - started

    result = {
        "case": name,
        "args": extra_args,
        "seconds": round(elapsed, 1),
        "exit": code,
        "timed_out": timed_out,
    }
    for label, pattern in (
        ("offset_seconds", r"offset seconds:\s*(-?[\d.]+)"),
        ("score", r"score:\s*(-?[\d.]+)"),
        ("scale", r"framerate scale factor:\s*([\d.]+)"),
    ):
        match = re.search(pattern, stderr)
        result[label] = float(match.group(1)) if match else None
    sampled = re.search(r"multi-segment sync: sampling (\d+) segment", stderr)
    result["segments_sampled"] = int(sampled.group(1)) if sampled else None
    result["splits"] = len(re.findall(r"^\s+\d+ cue\(s\) offset", stderr, re.M)) or None
    if result["splits"]:
        result["segment_offsets_s"] = [
            round(float(m), 3)
            for m in re.findall(r"^\s+\d+ cue\(s\) offset (-?[\d.]+)s", stderr, re.M)
        ]
    result["stderr_tail"] = stderr.strip().splitlines()[-4:]

    if work["out"].exists() and not timed_out:
        err, first, second = compare(read_cues(work["out"]), work["truth"])
        result["err_ms"] = None if err is None else round(err, 1)
        result["err_first_half_ms"] = None if first is None else round(first, 1)
        result["err_second_half_ms"] = None if second is None else round(second, 1)
    else:
        result["err_ms"] = result["err_first_half_ms"] = result["err_second_half_ms"] = None
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--media", required=True)
    parser.add_argument("--truth", required=True)
    parser.add_argument("--engine", default=DEFAULT_ENGINE)
    parser.add_argument("--shift", type=int, required=True, help="shift applied to the whole input (ms)")
    parser.add_argument("--second-half-shift", type=int, default=None)
    parser.add_argument("--cases", nargs="+", default=["whole", "ms8", "ms8-p1"])
    parser.add_argument("--shim", action="store_true")
    parser.add_argument("--ms-per-call", type=float, default=10.0)
    parser.add_argument("--ms-per-16k", type=float, default=1.46)
    parser.add_argument("--max-offset", type=int, default=60)
    parser.add_argument("--timeout", type=int, default=3600)
    parser.add_argument("--out", default="/tmp/sampling-bench.json")
    args = parser.parse_args()

    work_dir = Path("/tmp/sampling-bench")
    work_dir.mkdir(parents=True, exist_ok=True)
    truth = read_cues(args.truth)
    if not truth:
        sys.exit("no cues in %s" % args.truth)
    write_shifted(work_dir / "input.srt", truth, args.shift, args.second_half_shift)

    shim_env = {}
    if args.shim:
        if not SHIM.exists():
            sys.exit("shim not built: %s" % SHIM)
        slow_dir = str(Path(args.media).parent) + "/"
        shim_env = {
            "LD_PRELOAD": str(SHIM),
            "SLOWREAD_PREFIX": slow_dir,
            "SLOWREAD_MS_PER_CALL": str(args.ms_per_call),
            "SLOWREAD_MS_PER_16K": str(args.ms_per_16k),
        }

    print("media: %s" % args.media)
    print("truth: %s (%d cues)" % (args.truth, len(truth)))
    print("input: shift %+d ms%s" % (
        args.shift,
        "" if args.second_half_shift is None else ", second half %+d ms" % args.second_half_shift))
    print("shim: %s" % ("yes %s" % shim_env if shim_env else "no"))
    print()
    print("%-13s %8s %7s %9s %8s %8s %8s" % ("case", "sec", "exit", "err_ms", "1st", "2nd", "seg/split"))

    results = []
    for spec in args.cases:
        name, extra = parse_case(spec)
        work = {"input": work_dir / "input.srt", "out": work_dir / ("out-%s.srt" % re.sub(r"[^A-Za-z0-9]+", "-", spec)), "truth": truth}
        if work["out"].exists():
            work["out"].unlink()
        result = run_case(args.engine, name, extra, args.media, work, shim_env, args.max_offset, args.timeout)
        result["spec"] = spec
        results.append(result)
        print("%-13s %8.1f %7d %9s %8s %8s %s" % (
            name, result["seconds"], result["exit"], result["err_ms"],
            result["err_first_half_ms"], result["err_second_half_ms"],
            "%s/%s" % (result["segments_sampled"], result["splits"])))
        sys.stdout.flush()

    payload = {
        "media": args.media,
        "truth": args.truth,
        "shift_ms": args.shift,
        "second_half_shift_ms": args.second_half_shift,
        "shim": shim_env or None,
        "engine": args.engine,
        "when": time.strftime("%Y-%m-%d %H:%M:%S"),
        "results": results,
    }
    Path(args.out).write_text(json.dumps(payload, indent=2))
    print("\nwrote %s" % args.out)


if __name__ == "__main__":
    main()
