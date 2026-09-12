#!/usr/bin/env python3
"""F29, server side: the kill must actually stop what is running.

The page's half of F29 is `tests/gui/f29-confirm-probe.js` (first press asks, second kills). This is the
other half: with a job really running, `POST /SubSync/Kill` has to terminate it — no ffsubsync/ffmpeg child
left behind, and the server reporting nothing running afterwards.

    python3 f29_kill.py [--item <id>] [--tasks 6]
"""
import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402

SLOW_EPISODE = "1c0799b1407a2e7714b9e6e0bffbd6e2"


def engines():
    """Engine processes only — not this script, not the server, not the shell that started them."""
    out = subprocess.run(["ps", "-eo", "pid,args"], capture_output=True, text=True).stdout
    rows = []
    for line in out.splitlines():
        if "ffsubsync" in line or "ffprobe" in line or ("ffmpeg" in line and "grep" not in line):
            parts = line.strip().split(None, 1)
            if len(parts) == 2:
                rows.append((parts[0], parts[1][:110]))
    return rows


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--item", default=SLOW_EPISODE)
    ap.add_argument("--tasks", type=int, default=6)
    ap.add_argument("--timeout", type=int, default=900)
    args = ap.parse_args()

    tracks = drive.tracks(args.item)
    if not tracks:
        raise SystemExit("no tracks for " + args.item)
    tasks = [{"ItemId": args.item, "SubtitleIndex": t["Index"]} for t in tracks[: args.tasks]]

    before_engines = engines()
    st, batch, _ = ss.post("/SubSync/Batch", {"Label": "F29 kill", "Tasks": tasks})
    if st != 200:
        raise SystemExit("batch POST failed: %s %s" % (st, batch))

    running = 0
    deadline = time.time() + args.timeout
    while time.time() < deadline:
        st, active, _ = ss.get("/SubSync/Active")
        running = len(active.get("running") or [])
        if running:
            break
        time.sleep(2)

    engines_while_running = engines()
    started = time.time()
    st, kill, _ = ss.post("/SubSync/Kill", {})
    kill_response = kill if isinstance(kill, dict) else {"raw": str(kill)[:200]}

    gone_after = None
    for i in range(24):
        time.sleep(5)
        if not engines():
            gone_after = round(time.time() - started, 1)
            break

    st, active_after, _ = ss.get("/SubSync/Active")
    result = {
        "item": args.item,
        "tasks": len(tasks),
        "batch_id": batch.get("Id") if isinstance(batch, dict) else None,
        "running_when_killed": running,
        "kill_status": st,
        "kill_response": kill_response,
        "engines_before": before_engines,
        "engines_while_running": engines_while_running,
        "engines_gone_after_s": gone_after,
        "active_after": {"running": len(active_after.get("running") or []), "queued": active_after.get("queued")},
        "page_half": "tests/gui/f29-confirm3.json (first press asks, second press kills)",
    }
    with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "f29-kill.json"), "w") as fh:
        json.dump(result, fh, indent=2)
        fh.write("\n")
    print(json.dumps(result, indent=2))
    print("\nPASS" if (gone_after is not None and not result["active_after"]["running"]) else "\nCHECK")


if __name__ == "__main__":
    main()
