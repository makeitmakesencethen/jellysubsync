#!/usr/bin/env python3
"""B7 — can a Kill stop the extraction lane's pass?

The lane calls the Matroska reader with `CancellationToken.None`, so the whole-file read it starts
cannot be interrupted: `POST /SubSync/Kill` cancels queued and running *jobs*, kills the engine's
process trees, and the lane keeps reading the file (tens of seconds on the fast profile, tens of
minutes under the slow-storage shim).

Measured, not argued: the plugin's own process I/O counters (`/proc/<jellyfin>/io`, read_bytes) are
sampled before and after the kill, and `/SubSync/Active` is read to show that no job is running — so
the lane is the only reader left. The lane's own summary line (`extract lane: … ok=…`) is captured if
the pass ends inside the window.

Usage: python3 lane_kill.py [--tracks N] [--post S] [--label before]
"""
import argparse
import json
import pathlib
import re
import time

import ss, drive

MEDIA_SLOW = pathlib.Path("/opt/data/jf12test/media-slow")


def jellyfin_pid():
    for entry in pathlib.Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            argv = (entry / "cmdline").read_bytes().decode("utf-8", "replace")
        except OSError:
            continue
        if "jellyfin.dll" in argv:
            return int(entry.name)
    return None


def counters(pid):
    """(rchar, read_bytes) for the server process.

    `rchar` is the one that matters here: the lane reads a file that is already in the page cache, so
    `read_bytes` (bytes fetched from storage) stays at 0 while `rchar` (bytes handed to read/pread)
    grows by megabytes. The first version of this script watched `read_bytes` and therefore saw no
    reading at all while the lane was reading 10.6 MB.
    """
    out = {}
    try:
        for line in pathlib.Path("/proc/%d/io" % pid).read_text().splitlines():
            key, _, value = line.partition(": ")
            out[key] = int(value)
    except (OSError, ValueError):
        pass
    return out.get("rchar"), out.get("read_bytes")


def slow_episode_item():
    for it in ss.items(types=["Movie", "Episode"]):
        if it.get("Path", "").startswith(str(MEDIA_SLOW)) and it.get("Path", "").endswith(".mkv"):
            return it
    return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--tracks", type=int, default=10)
    ap.add_argument("--post", type=int, default=60, help="seconds to sample after the kill")
    ap.add_argument("--grew-mb", type=int, default=20, help="wait until the lane has read this much")
    ap.add_argument("--wait", type=int, default=120, help="give up waiting for the lane after this long")
    ap.add_argument("--label", default="before")
    args = ap.parse_args()

    item = slow_episode_item()
    if item is None:
        raise SystemExit("no media-slow episode in the library")
    pid = jellyfin_pid()
    if pid is None:
        raise SystemExit("jellyfin is not running")

    ss.clear_cache()
    tracks = [t for t in drive.tracks(item["Id"]) if not t.get("IsExternal")][-args.tracks:]
    off = ss.bookmark()

    # The lane starts reading as soon as the first job is queued; give it a moment to get going and
    # then confirm it is reading by watching the process counters move.
    st, resp, _ = ss.post("/SubSync/Batch",
                          {"Label": "lane-kill", "Tasks": [{"ItemId": item["Id"],
                                                            "SubtitleIndex": t["Index"]} for t in tracks]})
    bid = (resp or {}).get("Id") if isinstance(resp, dict) else None
    print("batch %s, %d tracks on %s" % (bid, len(tracks), item["Path"]), flush=True)

    start_rchar, start_read = counters(pid)
    grew = False
    deadline = time.time() + args.wait
    while time.time() < deadline:
        time.sleep(1)
        now_rchar, _ = counters(pid)
        if now_rchar is not None and start_rchar is not None \
                and now_rchar - start_rchar > args.grew_mb * 1048576:
            grew = True
            break
    reading, reading_storage = counters(pid)
    print("before kill: rchar=+%s MB read_bytes=+%s MB"
          % (round(((reading or 0) - (start_rchar or 0)) / 1048576.0, 1),
             round(((reading_storage or 0) - (start_read or 0)) / 1048576.0, 1)), flush=True)

    st_k, kill_body, _ = ss.post("/SubSync/Kill")
    killed_at = time.time()
    print("Kill -> %s %s" % (st_k, json.dumps(kill_body)[:200] if not isinstance(kill_body, str) else kill_body[:200]),
          flush=True)

    samples = []
    lane_end = None
    for _ in range(args.post):
        time.sleep(1)
        now, _ = counters(pid)
        active = ss.get("/SubSync/Active")[1]
        samples.append({"t": round(time.time() - killed_at, 1), "read_bytes": now,
                        "mb_since_kill": round(((now or 0) - (reading or 0)) / 1048576.0, 1),
                        "active": active if isinstance(active, str) else
                                  {"running": active.get("running"), "queued": active.get("queued")}})
        for line in ss.log_since(off).splitlines():
            if "extract lane:" in line and "->" in line:
                lane_end = line[-260:]

    total_after = round(((samples[-1]["read_bytes"] or 0) - (reading or 0)) / 1048576.0, 1) if samples else None
    active_after_kill = samples[-1]["active"] if samples else None
    result = {
        "label": args.label,
        "item": item["Name"],
        "path": item["Path"],
        "batch_id": bid,
        "tasks": len(tracks),
        "lane_was_reading_before_kill": grew,
        "mb_read_by_server_before_kill": round(((reading or 0) - (start_rchar or 0)) / 1048576.0, 1),
        "kill_http": st_k,
        "kill_body": kill_body,
        "mb_read_after_kill": total_after,
        "samples": samples,
        "active_after_kill": active_after_kill,
        "lane_end_line_after_kill": lane_end,
        "verdict": ("a killed run keeps reading the file" if (total_after or 0) > 5
                    else "no reading detected after the kill"),
    }
    out = pathlib.Path(__file__).with_name("lane-kill-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("lane-kill-%s" % args.label, {k: v for k, v in result.items() if k != "samples"})
    print(json.dumps({k: v for k, v in result.items() if k != "samples"}, indent=2))
    print("written: %s" % out)


if __name__ == "__main__":
    main()
