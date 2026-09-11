#!/usr/bin/env python3
"""S11b — a cancelled run must leave nothing of its own running.

Two measurements, both on the **slow profile** (the `slowread.so` shim, so every read of a file
under `/opt/data/jf12test/media-slow/` costs 12.8 ms per 16 KB and a whole-file read is minutes):

  --mode engine-cancel
      A sync of `Slow Single Track (2026).mkv` (one subtitle track, so the reference *is* the audio)
      makes ffsubsync spawn its own ffmpeg and read the container. The moment that child exists the
      batch is cancelled, then `/proc` is sampled for the children the cancel was supposed to reach.

  --mode lane-kill
      A batch of the 50-track slow episode makes the extraction lane read the whole 2.38 GB file.
      `POST /SubSync/Kill` is issued while the pass is in flight; what the pass does afterwards is
      read from the plugin log (a completed `extract lane: … ok=` line after the kill is a pass that
      was never interrupted).

Evidence: tests/backend/s11b-<mode>.json plus evidence.jsonl rows.
"""
import argparse
import json
import os
import pathlib
import re
import time

import ss, drive

MEDIA_SLOW = pathlib.Path("/opt/data/jf12test/media-slow")
SLOW_SINGLE = MEDIA_SLOW / "Slow Single Track (2026).mkv"
SLOW_EPISODE = MEDIA_SLOW / "Helikopterrånet S01E01.mkv"


def procs_with(fragment):
    """[(pid, ppid, cmdline)] for every process whose argv mentions `fragment`."""
    out = []
    for entry in os.listdir("/proc"):
        if not entry.isdigit():
            continue
        pid = int(entry)
        try:
            with open("/proc/%d/cmdline" % pid, "rb") as f:
                argv = f.read().decode("utf-8", "replace").split("\0")
            with open("/proc/%d/stat" % pid, "rb") as f:
                stat = f.read().decode("utf-8", "replace")
            ppid = int(stat.rsplit(")", 1)[1].split()[1])
        except (OSError, ValueError, IndexError):
            continue
        line = " ".join(a for a in argv if a)
        if fragment in line:
            out.append({"pid": pid, "ppid": ppid, "cmd": line[:400]})
    return out


def engine_children(root_fragment):
    """The engine's own children: ffmpeg/ffsubsync processes touching the media path."""
    kids = []
    for p in procs_with(root_fragment):
        cmd = p["cmd"]
        if "/jellyfin.dll" in cmd or "dotnet" in cmd.split()[0]:
            continue
        kids.append(p)
    return kids


def find_item(name_fragment):
    for it in ss.items(types=["Movie", "Episode"]):
        if name_fragment.lower() in (it.get("Name") or "").lower():
            return it
    return None


def mode_engine_cancel(args):
    item = find_item("Slow Single Track")
    if item is None:
        raise SystemExit("the 'Slow Single Track' item is not in the library yet: refresh it first")

    ss.clear_cache()
    tracks = drive.tracks(item["Id"])
    if not tracks:
        raise SystemExit("no tracks on %s" % item["Name"])

    plugin_off = ss.bookmark()
    t0 = time.time()
    st, resp, _ = ss.post("/SubSync/Batch",
                          {"Label": "S11b-engine", "Tasks": [{"ItemId": item["Id"],
                                                             "SubtitleIndex": tracks[0]["Index"]}]})
    bid = (resp or {}).get("Id") if isinstance(resp, dict) else None
    print("batch %s for %s track %s" % (bid, item["Name"], tracks[0]["Index"]), flush=True)

    # Wait for the engine's child to exist — that is the thing the cancel must reach.
    seen = []
    deadline = time.time() + args.wait
    while time.time() < deadline:
        kids = engine_children(str(SLOW_SINGLE))
        if kids:
            seen = kids
            print("engine children before cancel: %s" % json.dumps(kids)[:400], flush=True)
            break
        time.sleep(0.2)

    if not seen:
        print("no engine child appeared within %ss" % args.wait, flush=True)

    time.sleep(args.settle)
    st, body, _ = ss.post("/SubSync/Batch/%s/Cancel" % bid)
    cancel_at = time.time()
    print("cancel -> %s %s" % (st, json.dumps(body)[:200] if not isinstance(body, str) else body[:200]),
          flush=True)

    samples = []
    for _ in range(args.post):
        time.sleep(1.0)
        alive = engine_children(str(SLOW_SINGLE))
        samples.append({"t": round(time.time() - cancel_at, 1), "alive": alive})
        print("+%4.1fs alive=%d %s" % (time.time() - cancel_at, len(alive),
                                       json.dumps(alive)[:300]), flush=True)

    survivors = samples[-1]["alive"] if samples else []
    log = ss.log_since(plugin_off)
    result = {
        "mode": "engine-cancel",
        "file": str(SLOW_SINGLE),
        "item": item["Name"],
        "batch_id": bid,
        "engine_children_before_cancel": seen,
        "cancel_http": st,
        "samples": samples,
        "survivors_after_%ss" % args.post: survivors,
        "wall_s": round(time.time() - t0, 1),
        "log_lines": [l[-220:] for l in log.splitlines()
                      if re.search(r"cancel|kill|ffsubsync|extract:", l)][-14:],
    }
    out = pathlib.Path(__file__).with_name("s11b-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("S11b-%s" % args.label, {k: v for k, v in result.items() if k != "samples"})
    print(json.dumps({k: v for k, v in result.items()
                      if k not in ("samples", "log_lines")}, indent=2))
    print("written: %s" % out)


def mode_lane_kill(args):
    item = find_item("Helikopterrånet S01E01")
    # The slow copy, not the fast one.
    for it in ss.items(types=["Movie", "Episode"]):
        if it.get("Path") == str(SLOW_EPISODE):
            item = it
    ss.clear_cache()
    tracks = [t for t in drive.tracks(item["Id"]) if not t.get("IsExternal")][-args.tracks:]
    plugin_off = ss.bookmark()
    t0 = time.time()
    st, resp, _ = ss.post("/SubSync/Batch", {"Label": "S11b-lane",
                                             "Tasks": [{"ItemId": item["Id"],
                                                        "SubtitleIndex": t["Index"]} for t in tracks]})
    bid = (resp or {}).get("Id") if isinstance(resp, dict) else None
    print("batch %s (%d tasks) on %s" % (bid, len(tracks), item["Path"]), flush=True)

    # Wait until the lane is reading the file, then kill everything.
    started = ss.wait_for(r"extract lane:.*" + re.escape(os.path.basename(str(SLOW_EPISODE))),
                          offset=plugin_off, timeout=args.wait)
    print("lane line: %s" % (started or "(none)"), flush=True)
    time.sleep(args.settle)
    st, body, _ = ss.post("/SubSync/Kill")
    kill_at = time.time()
    print("Kill -> %s %s" % (st, json.dumps(body)[:200] if not isinstance(body, str) else body[:200]),
          flush=True)

    # Does the lane pass end by itself afterwards, and how long does the read continue?
    ended = None
    deadline = time.time() + args.post
    while time.time() < deadline:
        text = ss.log_since(plugin_off)
        for line in text.splitlines():
            if "extract lane:" in line and "->" in line:
                ended = line
                break
        if ended:
            break
        time.sleep(1.0)

    result = {
        "mode": "lane-kill",
        "file": str(SLOW_EPISODE),
        "batch_id": bid,
        "tasks": len(tracks),
        "lane_start_line": started,
        "kill_http": st,
        "lane_end_line": ended,
        "lane_stopped_after_kill": ended is not None,
        "seconds_from_kill_to_pass_end": round(time.time() - kill_at, 1) if ended else None,
        "wall_s": round(time.time() - t0, 1),
        "log_tail": [l[-200:] for l in ss.log_since(plugin_off).splitlines()][-12:],
    }
    out = pathlib.Path(__file__).with_name("s11b-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("S11b-%s" % args.label, {k: v for k, v in result.items() if k != "log_tail"})
    print(json.dumps({k: v for k, v in result.items() if k != "log_tail"}, indent=2))
    print("written: %s" % out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mode", default="engine-cancel", choices=["engine-cancel", "lane-kill"])
    ap.add_argument("--label", default="before")
    ap.add_argument("--tracks", type=int, default=8, help="lane-kill: tasks in the batch")
    ap.add_argument("--wait", type=int, default=180, help="seconds to wait for the engine child / lane")
    ap.add_argument("--settle", type=float, default=3.0, help="seconds between seeing the child and cancelling")
    ap.add_argument("--post", type=int, default=30, help="seconds to sample after the cancel")
    args = ap.parse_args()
    if args.mode == "engine-cancel":
        mode_engine_cancel(args)
    else:
        mode_lane_kill(args)


if __name__ == "__main__":
    main()
