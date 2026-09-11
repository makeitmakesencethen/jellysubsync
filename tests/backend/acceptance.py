#!/usr/bin/env python3
"""Acceptance runs: a whole episode (A4) or a whole series (A5) must finish with zero failures.

What is measured for every run:

  * per-task outcome (Completed / Failed / Cancelled) and the wall clock;
  * **zero failures** — every failure printed with its message;
  * no half-written or empty output: every output path the run reports exists, is non-empty and
    holds cues (a 0-byte sidecar is S4, an empty one is a half-written file);
  * cue counts of the outputs against the cue counts the plugin extracted for that file's tracks —
    ffsubsync preserves the cue count, so an output whose count is not one of them lost or invented
    cues;
  * no orphaned scratch: nothing left under the plugin cache root (job scratch dirs, references,
    part files) and no ffsubsync/ffmpeg process alive afterwards;
  * the queue really is idle at the end (`/SubSync/Active`), and how many tracks completed.

Usage:
  python3 acceptance.py --scope episode --label A4-fast
  python3 acceptance.py --scope series  --label A5-fast
  python3 acceptance.py --scope episode --label A8-slow-w4 --no-clear
"""
import argparse
import json
import os
import pathlib
import re
import time

import ss, drive

CACHE = pathlib.Path("/opt/data/jf12test/cache/subsync")
EP_ONLY = ["ep1", "ep2"]


def cache_inventory():
    """Everything the plugin left under its cache root, by kind."""
    inv = {"job_dirs": [], "ref_files": [], "part_files": [], "extracted": 0, "speech": 0,
           "total_bytes": 0, "other": []}
    if not CACHE.exists():
        return inv
    for root, dirs, files in os.walk(CACHE):
        rel = os.path.relpath(root, CACHE)
        for name in files:
            path = pathlib.Path(root) / name
            inv["total_bytes"] += path.stat().st_size if path.exists() else 0
            if name.endswith(".part"):
                inv["part_files"].append(str(path))
            elif ".ref.srt" in name:
                inv["ref_files"].append(str(path))
            elif rel.startswith("extracted"):
                inv["extracted"] += 1
            elif rel.startswith("speech"):
                inv["speech"] += 1
            elif rel.split(os.sep)[0] not in (".", "ref", "extracted", "speech", "state"):
                inv["other"].append(str(path))
        # A job scratch dir is a top-level directory that is not one of the caches.
        for name in dirs:
            if rel == "." and name not in ("ref", "extracted", "speech", "state", "logs"):
                inv["job_dirs"].append(str(pathlib.Path(root) / name))
    return inv


def live_engine_processes():
    out = []
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        try:
            argv = open("/proc/%s/cmdline" % entry, "rb").read().decode("utf-8", "replace").split("\0")
        except OSError:
            continue
        line = " ".join(a for a in argv if a)
        if ("ffsubsync" in line or "ffmpeg" in line) and "/jellyfin.dll" not in line:
            out.append({"pid": int(entry), "cmd": line[:220]})
    return out


def cue_count(path):
    try:
        text = pathlib.Path(path).read_text(errors="replace")
    except OSError:
        return -1
    return len(re.findall(r"-->\s*\d\d:", text))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scope", default="episode", choices=["episode", "series", "season"])
    ap.add_argument("--item", default="", help="item id override")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--label", default="A4")
    ap.add_argument("--no-clear", action="store_true")
    ap.add_argument("--timeout", type=int, default=7200)
    args = ap.parse_args()

    cfg = ss.plugin_config()
    cfg["ParallelWorkers"] = args.workers
    ss.set_plugin_config(cfg)
    if not args.no_clear:
        ss.clear_cache()

    if args.scope == "episode":
        items = [args.item or drive.ITEM["heli_movie"]]
    elif args.scope == "season":
        items = [args.item or drive.ITEM["season"]]
    else:
        items = [args.item or drive.ITEM["series"]]

    tasks = []
    expansion = []
    if args.scope in ("series", "season"):
        # Expand server-side exactly the way the pages do (`Subtitles/Batch` with ExpandSeries), so the
        # batch carries the tasks a person would have queued from the series page. `/SubSync/Subtitles/{id}`
        # answers [] for a series id, and a batch built from it would silently contain nothing.
        st, body, ms = ss.post("/SubSync/Subtitles/Batch",
                               {"ItemIds": items, "ExpandSeries": True}, timeout=600)
        for entry in (body or {}).get("items", []):
            entry_tracks = entry.get("Tracks") or entry.get("tracks") or []
            expansion.append({"item": entry.get("Name") or entry.get("Id"),
                              "tracks": len(entry_tracks)})
            for t in entry_tracks:
                tasks.append({"ItemId": entry.get("Id"), "SubtitleIndex": t.get("Index")})
        print("expansion (%s ms): %s" % (round(ms), expansion), flush=True)
    else:
        for item_id in items:
            for t in drive.tracks(item_id):
                tasks.append({"ItemId": item_id, "SubtitleIndex": t["Index"]})

    print("scope=%s items=%s tasks=%d workers=%s cache=%s"
          % (args.scope, items, len(tasks), args.workers, "kept" if args.no_clear else "cleared"),
          flush=True)

    off = ss.bookmark()
    t0 = time.time()
    st, resp, _ = ss.post("/SubSync/Batch", {"Label": args.label, "Tasks": tasks})
    if st != 200:
        raise SystemExit("batch POST failed: %s %s" % (st, resp))
    bid = (resp or {}).get("Id")
    batch = ss.wait_batch(bid, timeout=args.timeout)
    wall = time.time() - t0
    plugin_log = ss.log_since(off)

    tasks_out = batch.get("Tasks") or []
    by_status = {}
    for t in tasks_out:
        by_status[t.get("Status")] = by_status.get(t.get("Status"), 0) + 1

    failures = [{"index": t.get("SubtitleIndex"), "status": t.get("Status"),
                 "error": (t.get("Error") or "")[:300], "outcome": (t.get("Outcome") or "")[:200]}
                for t in tasks_out if t.get("Status") == "Failed"]

    outputs = [t.get("OutputPath") for t in tasks_out if t.get("OutputPath")]
    empty_or_missing = []
    for p in outputs:
        path = pathlib.Path(p)
        if not path.exists():
            empty_or_missing.append({"path": p, "why": "missing"})
        elif path.stat().st_size == 0:
            empty_or_missing.append({"path": p, "why": "0 bytes"})
        elif cue_count(path) <= 0:
            empty_or_missing.append({"path": p, "why": "no cues", "size": path.stat().st_size})

    # The cue counts the plugin extracted from this file's own tracks: an output's count must be one
    # of them (ffsubsync changes timings, not the number of cues).
    source_cues = sorted({int(m) for m in re.findall(r"extract: method=\S+ cues=(\d+)", plugin_log)})
    output_cues = sorted({cue_count(p) for p in outputs if pathlib.Path(p).exists()})
    unmatched_cues = [c for c in output_cues if c not in source_cues]

    verification_failures = [l[-200:] for l in plugin_log.splitlines() if "verification failed" in l]
    parity_refusals = [l[-200:] for l in plugin_log.splitlines()
                       if "cue" in l and ("refus" in l or "mismatch" in l or "parity" in l)]

    time.sleep(3)
    inventory = cache_inventory()
    alive = live_engine_processes()
    st_a, active, _ = ss.get("/SubSync/Active")

    result = {
        "label": args.label,
        "scope": args.scope,
        "workers": args.workers,
        "cache": "kept" if args.no_clear else "cleared",
        "batch_id": bid,
        "tasks": len(tasks),
        "expansion": expansion,
        "statuses": by_status,
        "completed": by_status.get("Completed", 0),
        "failed": by_status.get("Failed", 0),
        "wall_s": round(wall, 1),
        "wall_per_task_s": round(wall / max(1, len(tasks)), 2),
        "failures": failures[:12],
        "outputs_reported": len(outputs),
        "empty_or_missing_outputs": empty_or_missing[:12],
        "empty_or_missing_count": len(empty_or_missing),
        "source_cue_counts": source_cues,
        "output_cue_counts": output_cues,
        "output_cues_not_from_source": unmatched_cues,
        "verification_failures": verification_failures[:5],
        "parity_refusals": parity_refusals[:5],
        "cache_after": {k: (len(v) if isinstance(v, list) else v) for k, v in inventory.items()},
        "cache_leftovers": inventory["part_files"][:6] + inventory["job_dirs"][:6],
        "engine_processes_alive": alive,
        "active_after": active if isinstance(active, str) else {"running": active.get("running"),
                                                                "queued": active.get("queued")},
    }

    out = pathlib.Path(__file__).with_name("acceptance-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("acceptance-%s" % args.label, {k: v for k, v in result.items()
                                             if k not in ("source_cue_counts", "output_cue_counts")})
    print(json.dumps(result, indent=2))
    print("written: %s" % out)


if __name__ == "__main__":
    main()
