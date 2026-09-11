#!/usr/bin/env python3
"""Matrix section C — sources of subtitle text. Real runs against the live server."""
import json
import sys
import time

import ss, drive

FIX = {
    "ass": "18f77125ffc2d2333d5ad5174e0f2f3a",
    "bitmap": "9977d98d54e48c801cc91279dd1d4b8a",
    "empty": "6bd906b12da66c34b8f66695d9671e21",
    "extcopy": "810991dd105ad0097a92522366411fe0",
    "extreplace": "047ed66f844228413461abd3d47b19a6",
    "mixed": None,
    "mp4": None,
    "noidx": None,
    "single": None,
    "trunc": None,
}
for it in ss.items(types=["Movie"]):
    n = it["Name"]
    if n.startswith("MixedPatched"):
        FIX["mixed"] = it["Id"]
    elif n.startswith("Mp4 Test"):
        FIX["mp4"] = it["Id"]
    elif n.startswith("No Index"):
        FIX["noidx"] = it["Id"]
    elif n.startswith("Single Track"):
        FIX["single"] = it["Id"]
    elif n.startswith("Truncated"):
        FIX["trunc"] = it["Id"]

OUT = {}
start = time.time()


def row(name, fn):
    t0 = time.time()
    try:
        r = fn()
    except Exception as e:
        r = {"exception": "%s: %s" % (type(e).__name__, e)}
    r["row_ms"] = (time.time() - t0) * 1000.0
    OUT[name] = r
    print("### %s -> %s" % (name, json.dumps({k: v for k, v in r.items() if k != "log"})[:900]), flush=True)
    ss.record("row-" + name, {k: v for k, v in r.items() if k != "log"})


def clear():
    return ss.clear_cache()


if __name__ == "__main__":
    which = sys.argv[1:] or ["C21", "C22", "C23", "C24", "C25", "C26", "C27", "C28"]

    if "C21" in which:
        # 50-track episode: one track, cold cache (the reference case)
        clear()
        row("C21-mkv-50tracks-single-cold", lambda: drive.run_single(
            drive.ITEM["ep1"], 4, label="C21"))

    if "C22" in which:
        # D17: mixed cue index
        clear()
        row("C22-mixed-cold-run1", lambda: drive.run_single(FIX["mixed"], 2, label="C22a"))
        row("C22-mixed-warm-run2", lambda: drive.run_single(FIX["mixed"], 2, label="C22b"))
        clear()
        row("C22-mixed-cold-run3", lambda: drive.run_single(FIX["mixed"], 2, label="C22c"))

    if "C23" in which:
        clear()
        row("C23-noindex-walk", lambda: drive.run_single(FIX["noidx"], 2, label="C23a"))
        clear()
        row("C23-truncated", lambda: drive.run_single(FIX["trunc"], 2, label="C23b"))

    if "C24" in which:
        clear()
        row("C24-mp4", lambda: drive.run_single(FIX["mp4"], 2, label="C24"))

    if "C25" in which:
        clear()
        row("C25-external-copy", lambda: drive.run_single(FIX["extcopy"], 0, label="C25a"))
        clear()
        row("C25-external-replace", lambda: drive.run_single(FIX["extreplace"], 0, label="C25b"))

    if "C26" in which:
        clear()
        row("C26-bitmap-list", lambda: {"status": ss.get("/SubSync/Subtitles/%s" % FIX["bitmap"])[0],
                                        "tracks": ss.get("/SubSync/Subtitles/%s" % FIX["bitmap"])[1]})
        row("C26-bitmap-queue-index2", lambda: drive.run_single(FIX["bitmap"], 2, label="C26a"))
        clear()
        row("C26-empty-track", lambda: drive.run_single(FIX["empty"], 2, label="C26b"))
        clear()
        row("C26-ass-track", lambda: drive.run_single(FIX["ass"], 2, label="C26c"))
        clear()
        row("C26-forced-track", lambda: drive.run_single("d3c9918c3eb575b564631d6e90f8dbd5", 2, label="C26d"))

    if "C27" in which:
        clear()
        row("C27-single-track-audio", lambda: drive.run_single(FIX["single"], 2, label="C27"))

    if "C28" in which:
        clear()
        row("C28-reference-out-of-sync", lambda: drive.run_single(drive.ITEM["ep2"], 3, label="C28"))

    print("\n=== SECTION (%.1f s) ===" % (time.time() - start))
    print("orphans after runs:", drive.orphan_check())
