#!/usr/bin/env python3
"""What the extractor decides about the storage, from the plugin's own log.

Run one small job on a given item and print the interesting lines: the storage probe's verdict, the
extraction lane's pass (bytes, reads, ms, ms per read), and whether the walk had to correct a wrong
"reads are cheap" verdict from its own reads.

    python3 probe_verdict.py [--item <id>] [--tasks 2]
"""
import argparse
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402

LOG = "/opt/data/jf12test/data/data/subsync/logs/subsync.log"


def lines_since(mark):
    with open(LOG, encoding="utf-8", errors="replace") as fh:
        return [l.rstrip() for i, l in enumerate(fh) if i >= mark]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--item", default="1c0799b1407a2e7714b9e6e0bffbd6e2")
    ap.add_argument("--tasks", type=int, default=2)
    ap.add_argument("--timeout", type=int, default=1800)
    args = ap.parse_args()

    tracks = drive.tracks(args.item)
    if not tracks:
        raise SystemExit("no tracks for " + args.item)
    tasks = [{"ItemId": args.item, "SubtitleIndex": t["Index"]} for t in tracks[: args.tasks]]

    ss.clear_cache()
    mark = sum(1 for _ in open(LOG, encoding="utf-8", errors="replace"))
    st, batch, _ = ss.post("/SubSync/Batch", {"Label": "probe verdict", "Tasks": tasks})
    if st != 200:
        raise SystemExit("batch POST failed: %s %s" % (st, batch))
    ss.wait_batch(batch.get("Id"), timeout=args.timeout)

    fresh = lines_since(mark)
    keep = [l for l in fresh if any(k in l for k in (
        "extract: storage", "extract lane:", "switching to", "reads measured", "extract: method=",
        "ffsubsync exit", "job "))]
    print("\n".join(l[:190] for l in keep[-24:]))


if __name__ == "__main__":
    main()
