#!/usr/bin/env python3
"""S11 — the reference a bulk run aligns against.

Two defects in one reproducer:

1. **The reference directory is deleted while jobs of that file are still running.**
   `SubSyncService.PumpAsync` computes `stillQueued` from jobs whose status is `Queued`
   only, so the moment the batch's tail has nothing left in the queue the per-file
   reference tree goes — with up to `ParallelWorkers` jobs of the same media file still
   inside ffsubsync (which is reading that very file) or still about to write it.

2. **When the reference cannot be produced, the container is handed to ffsubsync.**
   `referenceStream` stays `s:N` and `referencePath` stays the video path, so ffsubsync
   demuxes the whole media file with its own ffmpeg — on this fixture 2.38 GB, one per
   job, which on the slow profile is minutes per job and never finishes.

This script watches both, with the plugin's and the server's own logs as the source:

  * every `ref/<key>/` directory is sampled while the batch runs — a disappearance while
    a job of that file is `Running` is defect 1, on the record;
  * the plugin log is scanned for `reference=s:` whose argv contains the media file
    (defect 2), for `Could not extract the reference` (the cause), and for
    `unable to read reference` (the same defect seen from ffsubsync's side);
  * the batch's outcome (completed / failed) and wall clock are recorded.

Usage:  python3 s11_reference.py [--tracks N] [--workers W] [--timeout S]
Writes tests/backend/s11-<label>.json plus evidence.jsonl rows.
"""
import argparse
import json
import os
import pathlib
import re
import subprocess
import threading
import time

import ss, drive

REF_ROOT = pathlib.Path("/opt/data/jf12test/cache/subsync/ref")
SERVER_LOG = pathlib.Path("/opt/data/jf12test/log/log_20260911.log")

ITEM = drive.ITEM["heli_movie"]           # the 2.38 GB, 50-track episode (as a film)
VIDEO = drive.FILE["heli_movie"]


def ref_dirs():
    try:
        return {p.name: sorted(f.name for f in p.glob("*")) for p in REF_ROOT.iterdir() if p.is_dir()}
    except FileNotFoundError:
        return {}


def running_jobs():
    st, active, _ = ss.get("/SubSync/Active")
    if st != 200 or not isinstance(active, dict):
        return []
    jobs = active.get("Jobs") or active.get("jobs") or []
    return [j for j in jobs if (j.get("Status") or j.get("status")) in ("Running", "Queued")]


class RefWatcher(threading.Thread):
    """Samples the reference tree and the running-job set together, once a second."""

    daemon = True

    def __init__(self):
        super().__init__()
        self.stop_flag = False
        self.samples = []
        self.disappearances = []

    def run(self):
        previous = ref_dirs()
        while not self.stop_flag:
            time.sleep(1.0)
            current = ref_dirs()
            jobs = running_jobs()
            self.samples.append({"t": round(time.time(), 2), "dirs": current, "jobs": len(jobs)})
            for name in previous:
                if name not in current:
                    self.disappearances.append({
                        "t": time.strftime("%H:%M:%S"), "key": name,
                        "files_before": previous[name], "jobs_running": len(jobs),
                        "job_states": [(j.get("Phase"), j.get("BatchIndex")) for j in jobs][:8],
                    })
            previous = current


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tracks", type=int, default=8)
    ap.add_argument("--indices", default="",
                    help="explicit comma-separated SubtitleIndex list, so a before/after pair "
                         "measures the same tracks even if the item's stream list shifts")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--timeout", type=int, default=3600)
    ap.add_argument("--label", default="before")
    ap.add_argument("--no-clear", action="store_true", help="keep the caches (warm run)")
    args = ap.parse_args()

    cfg = ss.plugin_config()
    before_workers = cfg.get("ParallelWorkers")
    cfg["ParallelWorkers"] = args.workers
    ss.set_plugin_config(cfg)
    if not args.no_clear:
        ss.clear_cache()

    titles = drive.tracks(ITEM)
    if args.indices:
        wanted = [int(x) for x in args.indices.split(",") if x.strip()]
        by_index = {t["Index"]: t for t in titles}
        picked = [by_index[i] for i in wanted if i in by_index]
        missing = [i for i in wanted if i not in by_index]
        if missing:
            print("WARNING: indices not in the item's track list: %s" % missing, flush=True)
    else:
        picked = [t for t in titles if not t.get("IsExternal")][-args.tracks:]
    tasks = [{"ItemId": ITEM, "SubtitleIndex": t["Index"]} for t in picked]

    print("reference tree before: %s" % ref_dirs(), flush=True)
    print("tracks: %s" % [(t["Index"], t.get("Language")) for t in picked], flush=True)
    print("workers=%s (was %s) cache=%s" % (args.workers, before_workers,
                                            "cleared" if not args.no_clear else "kept"), flush=True)

    plugin_off = ss.bookmark()
    server_off = SERVER_LOG.stat().st_size if SERVER_LOG.exists() else 0
    watcher = RefWatcher()
    watcher.start()

    t0 = time.time()
    st, resp, post_ms = ss.post("/SubSync/Batch", {"Label": "S11-%s" % args.label, "Tasks": tasks})
    print("batch POST -> %s %s" % (st, json.dumps(resp)[:200]), flush=True)
    bid = (resp or {}).get("BatchId") or (resp or {}).get("batchId") or (resp or {}).get("Id")
    batch = ss.wait_batch(bid, timeout=args.timeout) if bid else {}
    wall = time.time() - t0

    watcher.stop_flag = True
    watcher.join(timeout=5)

    time.sleep(2)
    plugin_log = ss.log_since(plugin_off)
    server_log = SERVER_LOG.read_text(errors="replace")[server_off:]

    demux = [l for l in plugin_log.splitlines()
             if re.search(r"reference=s:\d", l) and VIDEO in l]
    no_ref = [l for l in plugin_log.splitlines() if "unable to read reference" in l]
    could_not = [l for l in server_log.splitlines() if "Could not extract the reference subtitle" in l]
    refs_made = [l for l in plugin_log.splitlines() if "ffsubsync start" in l and ".ref.srt" in l]

    tasks_out = batch.get("Tasks") or []
    by_status = {}
    for t in tasks_out:
        by_status[t.get("Status")] = by_status.get(t.get("Status"), 0) + 1

    result = {
        "label": args.label,
        "workers": args.workers,
        "tracks": [t["Index"] for t in picked],
        "cache": "kept" if args.no_clear else "cleared",
        "batch_id": bid,
        "wall_s": round(wall, 1),
        "statuses": by_status,
        "completed": by_status.get("Completed", 0),
        "failed": by_status.get("Failed", 0),
        "ref_dir_disappearances": watcher.disappearances,
        "ref_dirs_at_end": ref_dirs(),
        "engine_demux_fallbacks": len(demux),
        "engine_demux_lines": [l[-260:] for l in demux][:6],
        "could_not_extract_reference": len(could_not),
        "could_not_extract_lines": [l[-200:] for l in could_not][:4],
        "unable_to_read_reference": len(no_ref),
        "reference_runs": len(refs_made),
        "failures": [{"index": t.get("SubtitleIndex"), "status": t.get("Status"),
                      "error": (t.get("Error") or "")[:200]} for t in tasks_out
                     if t.get("Status") == "Failed"],
    }

    out = pathlib.Path(__file__).with_name("s11-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("S11-%s" % args.label, {k: v for k, v in result.items()
                                      if not k.endswith("_lines")})

    print(json.dumps({k: v for k, v in result.items() if not k.endswith("_lines")}, indent=2))
    print("written: %s" % out)


if __name__ == "__main__":
    main()
