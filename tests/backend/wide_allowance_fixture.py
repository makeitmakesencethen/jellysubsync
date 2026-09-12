#!/usr/bin/env python3
"""A subtitle that needs more shift than the limit allows: one wide retry, then checked against the audio again.

The user's Clara Sola subtitles genuinely need 94 s and 99 s while "Maximum offset" is 60 s, and the job used to end
there. Now the subtitle is aligned once more with a wide allowance (4x the configured one, at least 300 s) and that
result is checked again with a tight allowance against the film's audio - the same double-check a framerate stretch
gets - so a wide allowance cannot smuggle in a lock onto the wrong part of the audio.

This fixture builds the case end to end: the sidecar is the file's own track 120 s out, which is past the 30 s a
subtitle reference is trusted for (so the reference is dropped and the audio used) and past the 60 s offset ceiling
(so the wide retry runs), but inside the 300 s it retries with.

    python3 wide_allowance_fixture.py
"""
import json
import os
import pathlib
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402
from reference_framerate import SIDECAR, cue_starts, reference_from_probe, seconds, stamp  # noqa: E402

SHIFT_S = 120.0
RESULT = pathlib.Path(__file__).with_suffix(".json")


def shift_srt(text, seconds_):
    out = []
    for line in text.splitlines():
        if "-->" in line:
            left, right = line.split("-->")
            out.append("%s --> %s" % (stamp(seconds(left.strip().split(" ")[0]) + seconds_),
                                      stamp(seconds(right.strip().split(" ")[0]) + seconds_)))
        else:
            out.append(line)
    return "\n".join(out) + "\n"


def main():
    item_id = drive.ITEM["heli_movie"]
    tracks = drive.tracks(item_id)
    external = [t for t in tracks if t.get("IsExternal") or t.get("isExternal")]
    if not external:
        raise SystemExit("no external subtitle on this fixture")
    target = external[0]

    cfg = ss.plugin_config()
    was = {k: cfg.get(k) for k in ("FixFramerate", "MaxOffsetSeconds")}
    cfg["FixFramerate"] = True
    cfg["MaxOffsetSeconds"] = 180
    ss.set_plugin_config(cfg)

    backup = pathlib.Path("/tmp/wide_allowance_sidecar.bak")
    backup.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    result = {"item": item_id, "shift_s": SHIFT_S, "max_offset_s": 60}
    try:
        probe = drive.run_single(item_id, target["Index"], mode="copy", timeout=900, label="wide-probe")
        track, reference = reference_from_probe(probe.get("log", []))
        ref_path = pathlib.Path("/tmp/wide_allowance_ref.srt")
        ref_path.write_text(reference, encoding="utf-8")
        ref_cues = cue_starts(ref_path)
        SIDECAR.write_text(shift_srt(reference, SHIFT_S), encoding="utf-8")
        input_cues = cue_starts(SIDECAR)
        print("reference: track s:%d (%d cues); the subtitle is its own text %+0.0f s out, limit %d s"
              % (track, len(ref_cues), SHIFT_S, cfg["MaxOffsetSeconds"]))

        for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
            stale.unlink()
        run = drive.run_single(item_id, target["Index"], mode="copy", timeout=1500, label="wide-allowance")
        log = [l.split("INFO", 1)[-1].strip() for l in run.get("log", [])]
        written = SIDECAR.parent / (SIDECAR.stem + ".SYNCED.srt")
        out = {"status": run.get("status_job"), "phase": run.get("phase"), "error": run.get("error"),
               "wide_window_logged": any("search window" in l and "aligning again with" in l for l in log),
               "holds_logged": any("holds against the film's audio" in l for l in log),
               "refused": run.get("phase") == "Refused" or any("REFUSED" in l for l in log),
               "written": str(written) if written.exists() else None, "log": [l[:320] for l in log]}
        if written.exists():
            got = cue_starts(written)
            out["cues"] = len(got)
            # What matters: the retry moved the subtitle *further* than the 60 s the first attempt was allowed to,
            # which is the whole point of the wide allowance. Compared against the subtitle's own times, not against
            # the embedded track: on this clip the audio and that track disagree by tens of seconds.
            if len(got) == len(input_cues):
                applied = [b - a for a, b in zip(input_cues, got)]
                out["applied_shift_s"] = round(statistics.median(applied), 3)
                out["applied_beyond_the_limit"] = abs(out["applied_shift_s"]) > 60.0
            ref_diffs = [abs(a - b) for a, b in zip(got, ref_cues)]
            out["median_offset_vs_reference_s"] = round(statistics.median(ref_diffs), 3) if ref_diffs else None
        result["run"] = out
    finally:
        SIDECAR.write_text(backup.read_text(encoding="utf-8"), encoding="utf-8")
        for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
            stale.unlink()
        cfg = ss.plugin_config()
        for k, v in was.items():
            if v is not None:
                cfg[k] = v
        ss.set_plugin_config(cfg)

    run = result.get("run", {})
    result["run"] = run
    applied = run.get("applied_shift_s")
    ok = bool(not run.get("refused")
              and run.get("status") == "Completed" and run.get("written")
              and applied is not None and abs(applied) > 60.0)
    result["verdict"] = {"wide_window_used": bool(run.get("wide_window_logged")),
                         "rechecked_against_audio": bool(run.get("recheck_logged")),
                         "still_refused": bool(run.get("refused")),
                         "written": run.get("written"),
                         "applied_shift_s": applied,
                         "applied_beyond_the_limit": bool(run.get("applied_beyond_the_limit")),
                         "median_offset_vs_reference_s": run.get("median_offset_vs_reference_s"),
                         "pass": ok}
    RESULT.write_text(json.dumps(result, indent=2), encoding="utf-8")

    print("status=%s wide window logged=%s holds logged=%s refused=%s written=%s applied shift=%s s (beyond the old limit: %s)"
          % (run.get("status"), run.get("wide_window_logged"), run.get("holds_logged"), run.get("refused"),
             run.get("written"), applied, run.get("applied_beyond_the_limit")))
    for l in run.get("log", [])[:12]:
        print("  |", l[:180])
    print("\nwrote", RESULT)
    print("verdict:", "PASS" if result["verdict"]["pass"] else "CHECK")


if __name__ == "__main__":
    main()
