#!/usr/bin/env python3
"""A7 — a hand-picked multi-episode selection.

The plan's A7 is "a hand-picked multi-episode selection from the menu page": not a whole series and not
a whole season, but the tracks a person ticked in the library view, spanning episodes. The API equivalent
is one batch whose tasks span two episodes, so this queues exactly that — every third track of each of
the two episodes of the test series (non-adjacent on purpose, so the run is not "the first N" and the
selection spans each file) — and reports the batch the way the other acceptance runs do.

    python3 a7_handpick.py [--per-episode 8] [--label A7-handpick] [--workers 4]
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--per-episode", type=int, default=8)
    ap.add_argument("--label", default="A7-handpick")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--timeout", type=int, default=1800)
    ap.add_argument("--no-clear", action="store_true")
    args = ap.parse_args()

    cfg = ss.plugin_config() or {}
    before_workers = cfg.get("ParallelWorkers")
    cfg["ParallelWorkers"] = args.workers
    ss.set_plugin_config(cfg)

    if not args.no_clear:
        ss.clear_cache()

    episodes = [drive.ITEM["ep1"], drive.ITEM["ep2"]]
    tasks, chosen = [], []
    for item_id in episodes:
        tracks = drive.tracks(item_id)
        if not tracks:
            raise SystemExit("no tracks for " + str(item_id))
        picked = tracks[::3][: args.per_episode]
        for track in picked:
            tasks.append({"ItemId": item_id, "SubtitleIndex": track["Index"]})
        chosen.append({"item": item_id, "tracks": [t["Index"] for t in picked]})

    t0 = time.time()
    status, resp, _ = ss.post("/SubSync/Batch", {"Label": args.label, "Tasks": tasks})
    if status != 200:
        raise SystemExit("batch POST failed: %s %s" % (status, resp))
    batch = ss.wait_batch(resp.get("Id"), timeout=args.timeout)
    wall = time.time() - t0

    by_status = {}
    for t in batch.get("Tasks") or []:
        by_status[t.get("Status")] = by_status.get(t.get("Status"), 0) + 1

    empty, notes = [], []
    for t in batch.get("Tasks") or []:
        out = t.get("OutputPath")
        if out and os.path.exists(out) and os.path.getsize(out) == 0:
            empty.append(out)
        if t.get("Status") in ("Completed", "Refused"):
            notes.append((t.get("Status"), (t.get("Message") or t.get("Note") or "")[:110]))

    result = {
        "label": args.label,
        "episodes": chosen,
        "tasks": len(tasks),
        "batch_id": resp.get("Id"),
        "wall_s": round(wall, 1),
        "wall_per_task": round(wall / max(1, len(tasks)), 2),
        "statuses": by_status,
        "completed": by_status.get("Completed", 0),
        "failed": by_status.get("Failed", 0),
        "refused": by_status.get("Refused", 0),
        "empty_outputs": empty,
        "notes": notes[:6],
        "workers": args.workers,
        "workers_before": before_workers,
        "cache": "kept" if args.no_clear else "cleared",
    }
    name = "acceptance-%s.json" % args.label
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), name), "w") as fh:
        json.dump(result, fh, indent=2)
        fh.write("\n")
    print(json.dumps(result, indent=2))
    print("written: tests/backend/" + name)


if __name__ == "__main__":
    main()
