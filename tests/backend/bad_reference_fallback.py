#!/usr/bin/env python3
"""A file whose own subtitle track is a bad ruler must still get synced, against the audio.

Builds the case the user's file hit: the plugin's alignment reference is the file's embedded text track, and that
track is not the same cut. A mis-timed ruler shows up as an alignment that demands a huge shift - here the subtitle
is the reference's own text 45 s out, above the 30 s a subtitle reference is trusted for and below the 60 s the
audio path accepts. Before this change the job refused; now the track is dropped as a ruler and the subtitle is
aligned against the film's audio.

    python3 bad_reference_fallback.py
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

SHIFT_S = 45.0
RESULT = pathlib.Path(__file__).with_suffix(".json")


def shift_srt(text, seconds_):
    lines = []
    for line in text.splitlines():
        if "-->" in line:
            left, right = line.split("-->")
            lines.append("%s --> %s" % (stamp(seconds(left.strip().split(" ")[0]) + seconds_),
                                        stamp(seconds(right.strip().split(" ")[0]) + seconds_)))
        else:
            lines.append(line)
    return "\n".join(lines) + "\n"


def main():
    item_id = drive.ITEM["heli_movie"]
    tracks = drive.tracks(item_id)
    external = [t for t in tracks if t.get("IsExternal") or t.get("isExternal")]
    if not external:
        raise SystemExit("no external subtitle on this fixture")
    target = external[0]

    cfg = ss.plugin_config()
    was = cfg.get("FixFramerate")
    cfg["FixFramerate"] = True
    ss.set_plugin_config(cfg)

    backup = pathlib.Path("/tmp/bad_reference_sidecar.bak")
    backup.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    result = {"item": item_id, "shift_s": SHIFT_S}
    try:
        probe = drive.run_single(item_id, target["Index"], mode="copy", timeout=900, label="bad-ruler-probe")
        track, reference = reference_from_probe(probe.get("log", []))
        ref_path = pathlib.Path("/tmp/bad_reference_ref.srt")
        ref_path.write_text(reference, encoding="utf-8")
        ref_cues = cue_starts(ref_path)
        SIDECAR.write_text(shift_srt(reference, SHIFT_S), encoding="utf-8")
        print("reference: track s:%d (%d cues); the subtitle is its own text %+0.0f s out"
              % (track, len(ref_cues), SHIFT_S))

        for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
            stale.unlink()
        run = drive.run_single(item_id, target["Index"], mode="copy", timeout=1200, label="bad-ruler")
        # Assertions run on the full lines: an earlier version truncated them to 220 characters first, which cut off
        # the end of the fallback message and made a working run report CHECK.
        log = [l.split("INFO", 1)[-1].strip() for l in run.get("log", [])]
        written = SIDECAR.parent / (SIDECAR.stem + ".SYNCED.srt")
        out = {"status": run.get("status_job"), "phase": run.get("phase"), "error": run.get("error"),
               "audio_fallback_logged": any("aligning against the audio instead" in l for l in log),
               "audio_method_logged": any("method=audio why=the reference subtitle was not the same cut" in l for l in log),
               "refused": run.get("phase") == "Refused" or any("REFUSED" in l for l in log),
               "written": str(written) if written.exists() else None, "log": [l[:300] for l in log]}
        if written.exists():
            got = cue_starts(written)
            diffs = [abs(a - b) for a, b in zip(got, ref_cues)]
            out["cues"] = len(got)
            out["median_offset_vs_reference_s"] = round(statistics.median(diffs), 3) if diffs else None
        result["run"] = out
    finally:
        SIDECAR.write_text(backup.read_text(encoding="utf-8"), encoding="utf-8")
        for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
            stale.unlink()
        cfg = ss.plugin_config()
        cfg["FixFramerate"] = was
        ss.set_plugin_config(cfg)

    run = result.get("run", {})
    # None-checks, not `or` defaults: a perfect result is an offset of 0.0 s, and `or 99` reads that as missing.
    offset = run.get("median_offset_vs_reference_s")
    ok = bool(run.get("audio_fallback_logged") and not run.get("refused")
              and run.get("status") == "Completed" and run.get("written")
              and offset is not None and offset <= 5.0)
    result["verdict"] = {"audio_fallback_used": bool(run.get("audio_fallback_logged")),
                         "still_refused": bool(run.get("refused")),
                         "written": run.get("written"),
                         "median_offset_vs_reference_s": run.get("median_offset_vs_reference_s"),
                         "pass": bool(ok)}
    RESULT.write_text(json.dumps(result, indent=2), encoding="utf-8")

    print("status=%s refused=%s audio fallback logged=%s method logged=%s written=%s median offset vs reference=%s s"
          % (run.get("status"), run.get("refused"), run.get("audio_fallback_logged"),
             run.get("audio_method_logged"), run.get("written"), run.get("median_offset_vs_reference_s")))
    for l in run.get("log", [])[:10]:
        print("  |", l[:190])
    print("\nwrote", RESULT)
    print("verdict:", "PASS" if result["verdict"]["pass"] else "CHECK")


def cue_starts_from_text(text):  # noqa: D103
    tmp = pathlib.Path("/tmp/bad_reference_ref.srt")
    tmp.write_text(text, encoding="utf-8")
    return cue_starts(tmp)


if __name__ == "__main__":
    main()
