#!/usr/bin/env python3
"""Aggregate speed profile: the whole episode (50 tracks) in one batch, on the slow-storage
profile, for each ParallelWorkers setting. Reports wall clock, per-track outcomes and the
plugin's own extraction lines."""
import json
import sys
import time

import ss, drive

TARGET = sys.argv[1] if len(sys.argv) > 1 else "media-slow"
WORKERS = [int(x) for x in sys.argv[2].split(",")] if len(sys.argv) > 2 else [4]

if TARGET == "media-slow":
    ITEM = "1c0799b1407a2e7714b9e6e0bffbd6e2"
    LABELBASE = "slow-50tracks"
else:
    ITEM = drive.ITEM["ep1"]
    LABELBASE = "fast-50tracks"


def main():
    cfg = ss.plugin_config()
    for w in WORKERS:
        cfg["ParallelWorkers"] = w
        st, body = ss.set_plugin_config(cfg)
        cfg = ss.plugin_config()
        print("workers set to %s (stored %s)" % (w, cfg["ParallelWorkers"]), flush=True)
        ss.clear_cache()
        tracks = drive.tracks(ITEM)
        tasks = [{"ItemId": ITEM, "SubtitleIndex": t["Index"]} for t in tracks]
        label = "%s-w%d" % (LABELBASE, w)
        t0 = time.time()
        st, resp, post_ms = ss.post("/SubSync/Batch", {"Label": label, "Tasks": tasks})
        print("  batch post:", st, str(resp)[:200], "%.0f ms" % post_ms, flush=True)
        if st != 200:
            continue
        bid = resp.get("BatchId") or resp.get("batchId") or resp.get("Id")
        b = ss.wait_batch(bid, timeout=5400, poll=2.0)
        wall = time.time() - t0
        tasks_out = b.get("Tasks") or []
        counts = {}
        for t in tasks_out:
            counts[t.get("Status")] = counts.get(t.get("Status"), 0) + 1
        rec = {"worker_setting": w, "stored": cfg["ParallelWorkers"], "tracks": len(tasks),
               "batch_id": bid, "wall_s": wall, "post_ms": post_ms, "counts": counts,
               "mode": b.get("Mode"), "ok": b.get("Ok"), "failed": b.get("Failed"),
               "cancelled": b.get("Cancelled"),
               "fails": [{"i": t.get("SubtitleIndex"), "err": t.get("Error")}
                         for t in tasks_out if t.get("Status") == "Failed"],
               "outcomes": [t.get("Outcome") for t in tasks_out]}
        ss.record("aggregate-" + label, {k: v for k, v in rec.items() if k != "outcomes"})
        print("  RESULT %s: %.1f s wall, %s, mode=%s" % (label, wall, counts, b.get("Mode")), flush=True)
        with open(ss.WORK / ("agg-%s.json" % label), "w") as f:
            json.dump(rec, f, indent=1)


if __name__ == "__main__":
    main()
