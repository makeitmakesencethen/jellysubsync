#!/usr/bin/env python3
"""The framerate option has to work when the reference is a subtitle, not the audio.

Builds the case the option exists for, on a file whose alignment reference is its own embedded text track: the
external subtitle's span is a PAL pair (0.95904) away from that reference, which is what a Swedish film with a
25 fps subtitle on a 23.976 fps release looks like. A subtitle reference cannot fix that by itself - it is
another subtitle, so the engine has no frame rate to read and fits a pure shift of about half the film's drift,
which the reference ceiling then refuses (measured on the user's file: 111.9 s on a 96-minute film). The plugin
now rescales the target onto the reference's time base before aligning, and this proves it:

  1. with the option on  -> the log shows the rescale, the job completes, and the written subtitle sits on the
     reference's timeline (its span matches the reference's);
  2. with the option off -> no rescale is attempted, so the option is what produces the correction.

    python3 reference_framerate.py
"""
import json
import os
import pathlib
import statistics
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402

PAL = 0.95904          # a 25 fps timing against a 23.976 fps reference
VIDEO = pathlib.Path(drive.FILE["heli_movie"])
SIDECAR = VIDEO.with_suffix(".swe.srt")
BACKUP = pathlib.Path("/tmp/reference_framerate_sidecar.bak")
RESULT = pathlib.Path(__file__).with_suffix(".json")


def cue_starts_from_text(text):
    tmp = pathlib.Path("/tmp/reference_framerate_ref.srt")
    tmp.write_text(text, encoding="utf-8")
    return cue_starts(tmp)


def cue_starts(path):
    out = []
    for line in pathlib.Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        if "-->" not in line:
            continue
        out.append(seconds(line.split("-->")[0].strip().split(" ")[0]))
    return out


def seconds(stamp):
    h, m, rest = stamp.split(":")
    s, _, ms = rest.replace(",", ".").partition(".")
    return int(h) * 3600 + int(m) * 60 + int(s) + (int(ms.ljust(3, "0")[:3]) / 1000.0)


def stamp(total):
    whole = int(total)
    return "%02d:%02d:%02d,%03d" % (whole // 3600, (whole % 3600) // 60, whole % 60,
                                    round((total - whole) * 1000))


def rescale_srt(src_text, factor):
    lines = []
    for line in src_text.splitlines():
        if "-->" in line:
            left, right = line.split("-->")
            lines.append("%s --> %s" % (stamp(seconds(left.strip().split(" ")[0]) * factor),
                                        stamp(seconds(right.strip().split(" ")[0]) * factor)))
        else:
            lines.append(line)
    return "\n".join(lines) + "\n"


def embedded_text(track):
    out = subprocess.run(["ffmpeg", "-v", "error", "-i", str(VIDEO), "-map", "0:s:%d" % track,
                          "-f", "srt", "-"], capture_output=True, text=True, check=True)
    return out.stdout


REF_CACHE = pathlib.Path("/opt/data/jf12test/cache/subsync/ref")


def cached_reference_text():
    """The reference the plugin actually uses for this file: the newest one under its cache.

    Picking a track myself got this wrong (the plugin chose s:1 while the sidebar of tracks suggested s:3), and a
    fixture built from the wrong track measures a span ratio that is not the PAL pair under test.
    """
    files = [p for p in REF_CACHE.rglob("*.ref.srt")]
    if not files:
        raise SystemExit("no reference in the plugin's cache - run any sync first")
    newest = max(files, key=lambda p: p.stat().st_mtime)
    return newest, newest.read_text(encoding="utf-8", errors="replace")


def run_case(item_id, index, enabled, label):
    cfg = ss.plugin_config()
    cfg["FixFramerate"] = enabled
    ss.set_plugin_config(cfg)
    run = drive.run_single(item_id, index, mode="copy", timeout=900, label=label)
    log = [l.split("INFO", 1)[-1].strip()[:200] for l in run.get("log", [])]
    written = None
    for cand in (run.get("output"), str(SIDECAR.parent / (SIDECAR.stem + ".SYNCED.srt"))):
        if cand and pathlib.Path(cand).exists():
            written = pathlib.Path(cand)
            break
    return {"status": run.get("status_job"), "phase": run.get("phase"), "error": run.get("error"),
            "rescale": any("rescaling it onto the reference's time base" in l for l in log),
            "not_a_pair": any("is not a framerate pair" in l for l in log),
            "refused": any("REFUSED" in l for l in log) or run.get("phase") == "Refused",
            "written": str(written) if written else None, "log": log}


def main():
    item_id = drive.ITEM["heli_movie"]
    tracks = drive.tracks(item_id)
    external = [t for t in tracks if t.get("IsExternal") or t.get("isExternal")]
    if not external:
        raise SystemExit("this fixture has no external subtitle to sync: %s" % json.dumps(tracks)[:300])
    target = external[0]

    # A probe run first: it refreshes the reference the plugin uses for this file, which is the only text the
    # sidecar may be built from for the span ratio to be the PAL pair under test.
    cfg0 = ss.plugin_config()
    cfg0["FixFramerate"] = False
    ss.set_plugin_config(cfg0)
    probe = drive.run_single(item_id, target["Index"], mode="copy", timeout=900, label="framerate-probe")
    print("probe: %s" % probe.get("status_job"))
    ref_file, reference = cached_reference_text()
    ref_cues = cue_starts_from_text(reference)
    BACKUP.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    SIDECAR.write_text(rescale_srt(reference, PAL), encoding="utf-8")
    print("reference: %s, %d cues; the sidecar is the same text at %.5fx" % (ref_file, len(ref_cues), PAL))

    was = ss.plugin_config().get("FixFramerate")
    results = {"item": item_id, "reference": str(ref_file), "reference_cues": len(ref_cues), "pal": PAL,
               "sidecar": str(SIDECAR)}
    try:
        for enabled, label in ((True, "framerate-on"), (False, "framerate-off")):
            for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
                stale.unlink()
            case = run_case(item_id, target["Index"], enabled, label)
            if case["written"]:
                got = cue_starts(case["written"])
                case["cues"] = len(got)
                case["span_ratio_vs_reference"] = round(
                    (got[-1] - got[0]) / (ref_cues[-1] - ref_cues[0]), 5) if len(got) > 2 else None
                diffs = [abs(a - b) for a, b in zip(got, ref_cues)]
                case["median_offset_s"] = round(statistics.median(diffs), 3) if diffs else None
            results[label] = case
    finally:
        cfg = ss.plugin_config()
        cfg["FixFramerate"] = was
        ss.set_plugin_config(cfg)
        SIDECAR.write_text(BACKUP.read_text(encoding="utf-8"), encoding="utf-8")
        for stale in SIDECAR.parent.glob(SIDECAR.stem + ".SYNCED.srt"):
            stale.unlink()

    on, off = results.get("framerate-on", {}), results.get("framerate-off", {})
    on_ok = (on.get("rescale") and on.get("status") == "Completed"
             and (on.get("span_ratio_vs_reference") or 0) > 1.0
             and abs((on.get("span_ratio_vs_reference") or 0) - 1.0) <= 0.01
             and (on.get("median_offset_s") or 99) <= 2.5)
    off_ok = (not off.get("rescale")) and (off.get("refused") or abs((off.get("span_ratio_vs_reference") or 0) - PAL) <= 0.02)
    results["verdict"] = {"on_rescaled_and_synced": bool(on_ok), "off_left_uncorrected": bool(off_ok),
                          "pass": bool(on_ok and off_ok)}
    RESULT.write_text(json.dumps(results, indent=2), encoding="utf-8")

    print("\noption ON : status=%s rescale=%s span ratio vs reference=%s (1.0 = corrected) "
          "median offset=%s s refused=%s"
          % (on.get("status"), on.get("rescale"), on.get("span_ratio_vs_reference"),
             on.get("median_offset_s"), on.get("refused")))
    print("option OFF: status=%s rescale=%s span ratio vs reference=%s refused=%s"
          % (off.get("status"), off.get("rescale"), off.get("span_ratio_vs_reference"), off.get("refused")))
    for l in on.get("log", [])[:8]:
        print("   on |", l)
    for l in off.get("log", [])[:6]:
        print("   off|", l)
    print("\nwrote", RESULT)
    print("verdict:", "PASS" if results["verdict"]["pass"] else "CHECK")


if __name__ == "__main__":
    main()
