#!/usr/bin/env python3
"""Matrix section A — modes × kinds (single, all tracks, series, season, hand-picked, bulk,
re-sync, duplicates)."""
import json
import os
import time

import ss, drive

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
    print("### %s -> %s" % (name, json.dumps({k: v for k, v in r.items() if k != "log"})[:800]), flush=True)
    ss.record("row-" + name, {k: v for k, v in r.items() if k != "log"})


def all_tracks_batch(item, label, timeout=5400):
    tracks = [t["Index"] for t in drive.tracks(item)]
    return drive.run_batch([{"ItemId": item, "SubtitleIndex": i} for i in tracks],
                           label=label, timeout=timeout)


def main():
    which = os.environ.get("ROWS_A", "all").split(",")

    if "A1" in which or "all" in which:
        ss.clear_cache()
        row("A1-single-copy-mode", lambda: drive.run_single(
            "3ba2f1803ba81a3a302e00b82abfa2cc", 2, label="A1"))   # Embedded Test, embedded track

    if "A2" in which or "all" in which:
        def a2():
            cfg = ss.plugin_config()
            cfg["SyncModeCopy"] = False
            ss.set_plugin_config(cfg)
            ss.clear_cache()
            item = "047ed66f844228413461abd3d47b19a6"   # External Replace, +7 s
            srt = "/opt/data/jf12test/media-fixtures/External Replace (2026).en.srt"
            before = (os.path.getsize(srt), ss.sha256(srt))
            r = drive.run_single(item, 0, label="A2-replace")
            after_files = sorted(os.path.basename(f) for f in
                                 os.listdir(os.path.dirname(srt)))
            restored = os.path.exists(srt)
            after = (os.path.getsize(srt), ss.sha256(srt)) if restored else None
            cfg = ss.plugin_config()
            cfg["SyncModeCopy"] = True
            ss.set_plugin_config(cfg)
            return {"job": r.get("status_job"), "outcome": r.get("outcome"), "output": r.get("output"),
                    "size_before": before[0], "size_after": after[0] if after else None,
                    "hash_changed": (after[1] != before[1]) if after else None,
                    "files_after": after_files}
        row("A2-single-replace-mode", a2)

    if "A3" in which or "all" in which:
        ss.clear_cache()
        row("A3-all-tracks-movie", lambda: all_tracks_batch("d3c9918c3eb575b564631d6e90f8dbd5", "A3-signs-3tracks"))

    if "A4" in which or "all" in which:
        ss.clear_cache()
        row("A4-all-tracks-episode", lambda: all_tracks_batch(drive.ITEM["ep1"], "A4-ep1-50tracks"))

    if "A5" in which or "all" in which:
        ss.clear_cache()
        row("A5-whole-series", lambda: all_tracks_batch(drive.ITEM["series"], "A5-series"))

    if "A6" in which or "all" in which:
        ss.clear_cache()
        row("A6-one-season", lambda: all_tracks_batch(drive.ITEM["season"], "A6-season"))

    if "A7" in which or "all" in which:
        ss.clear_cache()
        def a7():
            tasks = []
            for ep in (drive.ITEM["ep1"], drive.ITEM["ep2"]):
                for i in (4, 6, 44):
                    tasks.append({"ItemId": ep, "SubtitleIndex": i})
            return drive.run_batch(tasks, label="A7-handpicked")
        row("A7-handpicked", a7)

    if "A8" in which or "all" in which:
        def a8():
            cfg = ss.plugin_config()
            cfg["ParallelWorkers"] = 8
            ss.set_plugin_config(cfg)
            ss.clear_cache()
            r = all_tracks_batch(drive.ITEM["ep2"], "A8-ep2-50tracks-w8")
            cfg = ss.plugin_config()
            cfg["ParallelWorkers"] = 4
            ss.set_plugin_config(cfg)
            return r
        row("A8-bulk-workers8", a8)

    if "A9" in which or "all" in which:
        def a9():
            ss.clear_cache()
            first = drive.run_single(drive.ITEM["ep1"], 4, label="A9-first")
            second = drive.run_single(drive.ITEM["ep1"], 4, label="A9-second")
            return {"first": {k: first.get(k) for k in ("status_job", "outcome", "wall_ms")},
                    "second": {k: second.get(k) for k in ("status_job", "outcome", "wall_ms")},
                    "second_log": [l[-180:] for l in (second.get("log") or [])
                                   if "extract" in l or "already" in l][:6]}
        row("A9-resync", a9)

    if "A10" in which or "all" in which:
        def a10():
            st1, r1, _ = ss.post("/SubSync/Sync", {"itemId": drive.ITEM["ep2"], "subtitleIndex": 4})
            st2, r2, _ = ss.post("/SubSync/Sync", {"itemId": drive.ITEM["ep2"], "subtitleIndex": 4})
            st3, r3, _ = ss.post("/SubSync/Batch", {"Label": "dup-batch",
                                                    "Tasks": [{"ItemId": drive.ITEM["ep2"], "SubtitleIndex": 6}] * 3})
            return {"sync1": st1, "sync2": st2, "same_job": (r1.get("Id") == r2.get("Id")) if isinstance(r1, dict) and isinstance(r2, dict) else None,
                    "ids": [r1.get("Id") if isinstance(r1, dict) else r1, r2.get("Id") if isinstance(r2, dict) else r2],
                    "batch_3_identical": st3, "batch_body": r3 if not isinstance(r3, dict) else
                    {k: r3.get(k) for k in ("BatchId", "Total", "Mode")}}
        row("A10-duplicates", a10)

    print("\n=== A section done in %.1f s ===" % (time.time() - start))
    print("orphans:", drive.orphan_check())


if __name__ == "__main__":
    main()
