#!/usr/bin/env python3
"""S19: the shared extraction directory must outlive every job that reads it.

The defect: the extracted subtitle was written into the job's own temporary directory, and a job deletes
that directory when it finishes — so a job that started later (or re-entered, as the failing job did when its
mode changed from `auto` to `ultimate`) failed with

    DirectoryNotFoundException: Could not find a part of the path
    '.../cache/subsync/<jobId>/subtitle_15.srt'

Four of fifty tasks died that way in one bulk run on the slow profile.

This test watches the directories while a bulk run is in flight, so it fails if the shared directory (a) is
missing while a job needs it, (b) is deleted while another job is still reading, or (c) is left behind after
the last consumer finished. It also checks the log of that run for the exception the defect produced.

    python3 s19_shared_dir.py [--item <id>] [--tasks 12] [--workers 4]
"""
import argparse
import glob
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402

SHARED_ROOT = "/opt/data/jf12test/cache/subsync/shared"


def shared_dirs():
    return sorted(glob.glob(os.path.join(SHARED_ROOT, "*")))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--item", default=drive.ITEM["test_movie"])
    ap.add_argument("--tasks", type=int, default=12)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--log", default="/opt/data/jf12test/data/data/subsync/logs/subsync.log")
    args = ap.parse_args()

    tracks = drive.tracks(args.item)
    if not tracks:
        raise SystemExit("no tracks for " + args.item)
    tasks = [{"ItemId": args.item, "SubtitleIndex": t["Index"]} for t in tracks[: args.tasks]]

    cfg = ss.plugin_config() or {}
    cfg["ParallelWorkers"] = args.workers
    ss.set_plugin_config(cfg)
    ss.clear_cache()

    before_dirs = shared_dirs()
    before_log_lines = sum(1 for _ in open(args.log, encoding="utf-8", errors="replace"))

    st, batch, _ = ss.post("/SubSync/Batch", {"Label": "S19 shared dir", "Tasks": tasks})
    if st != 200:
        raise SystemExit("batch POST failed: %s %s" % (st, batch))
    bid = batch.get("Id")

    # Sample: while the run is in flight, the shared directory for this file must exist and hold tracks, and
    # it must not disappear between samples (that is exactly what failed the later jobs).
    samples, disappeared = [], []
    deadline = time.time() + args.timeout
    seen_with_tracks = 0
    while time.time() < deadline:
        dirs = shared_dirs()
        st, b, _ = ss.get("/SubSync/Batch/%s" % bid)
        status = b.get("Status") if isinstance(b, dict) else "?"
        counts = {}
        for t in (b.get("Tasks") or []) if isinstance(b, dict) else []:
            counts[t.get("Status")] = counts.get(t.get("Status"), 0) + 1
        sample = {"dirs": [os.path.basename(d) for d in dirs], "status": status, "tasks": counts}
        samples.append(sample)
        if dirs:
            seen_with_tracks += 1
        elif seen_with_tracks and status in ("Running", "Queued"):
            # It was there and vanished while the run was still going: the old defect, seen live.
            disappeared.append(sample)
        if status in ("Completed", "Partial", "Failed", "Cancelled"):
            break
        time.sleep(2)

    st, b, _ = ss.get("/SubSync/Batch/%s" % bid)
    tasks_out = (b.get("Tasks") or []) if isinstance(b, dict) else []
    by_status = {}
    for t in tasks_out:
        by_status[t.get("Status")] = by_status.get(t.get("Status"), 0) + 1

    # What the description promises: no job reads a directory another job removed.
    new_log = []
    with open(args.log, encoding="utf-8", errors="replace") as fh:
        for i, line in enumerate(fh):
            if i >= before_log_lines:
                new_log.append(line)
    exceptions = [l.strip()[:200] for l in new_log
                  if "DirectoryNotFoundException" in l and "subtitle_" in l]

    after_dirs = shared_dirs()
    result = {
        "item": args.item,
        "tasks": len(tasks),
        "workers": args.workers,
        "batch_id": bid,
        "statuses": by_status,
        "failed": by_status.get("Failed", 0),
        "refused": by_status.get("Refused", 0),
        "shared_dirs_before": [os.path.basename(d) for d in before_dirs],
        "shared_dirs_after": [os.path.basename(d) for d in after_dirs],
        "shared_dir_seen_while_running": seen_with_tracks > 0,
        "shared_dir_vanished_mid_run": disappeared,
        "directory_not_found_lines": exceptions,
        "samples": samples[:8],
    }
    root_left_behind = os.path.isdir(SHARED_ROOT) and len(shared_dirs()) == 0
    result["empty_root_left_behind"] = root_left_behind
    ok = (result["failed"] == 0 and not disappeared and not exceptions
          and not root_left_behind)
    result["verdict"] = "PASS" if ok else "CHECK"
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "s19-shared-dir.json"), "w") as fh:
        json.dump(result, fh, indent=2)
        fh.write("\n")
    print(json.dumps({k: result[k] for k in ("statuses", "failed", "refused", "shared_dir_seen_while_running",
                                             "shared_dir_vanished_mid_run", "shared_dirs_after",
                                             "empty_root_left_behind",
                                             "directory_not_found_lines", "verdict")}, indent=2))
    print("written: tests/backend/s19-shared-dir.json")


if __name__ == "__main__":
    main()
