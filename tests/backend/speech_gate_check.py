#!/usr/bin/env python3
"""Two subtitles of one file must not each analyse its audio.

Queues the same track twice for a file whose reference is the audio (no sibling text track to align
against) and reports what the plugin log says about the analysis: one `method=audio` (the job that does the
file's analysis) and the others on the speech cache.

    python3 speech_gate_check.py [--item <id>] [--tasks 2]
"""
import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import drive  # noqa: E402
import ss  # noqa: E402

LOG = "/opt/data/jf12test/data/data/subsync/logs/subsync.log"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--item", default="1c0799b1407a2e7714b9e6e0bffbd6e2")
    ap.add_argument("--tasks", type=int, default=2)
    ap.add_argument("--timeout", type=int, default=2400)
    args = ap.parse_args()

    tracks = drive.tracks(args.item)
    if not tracks:
        raise SystemExit("no tracks for " + args.item)
    # The same track, twice: both jobs need this file's audio, which is the case that used to analyse it twice.
    tasks = [{"ItemId": args.item, "SubtitleIndex": tracks[0]["Index"]} for _ in range(args.tasks)]

    mark = sum(1 for _ in open(LOG, encoding="utf-8", errors="replace"))
    st, batch, _ = ss.post("/SubSync/Batch", {"Label": "speech gate", "Tasks": tasks})
    if st != 200:
        raise SystemExit("batch POST failed: %s %s" % (st, batch))
    ss.wait_batch(batch.get("Id"), timeout=args.timeout)

    fresh = [l for i, l in enumerate(open(LOG, encoding="utf-8", errors="replace")) if i >= mark]
    audio = [l for l in fresh if "reference: method=audio" in l]
    fromcache = [l for l in fresh if "reference: method=speech-cache" in l]
    starts = [l for l in fresh if "ffsubsync start" in l]
    exits = [int(m) for l in fresh for m in re.findall(r"ffsubsync exit=0 after (\d+) ms", l)]

    print("jobs: %d" % len(tasks))
    print("method=audio lines: %d" % len(audio))
    print("method=speech-cache lines: %d" % len(fromcache))
    print("ffsubsync runs: %d, durations ms: %s" % (len(starts), exits))
    print("waiting messages: %d" % len([l for l in fresh if "harvested by another job" in l]))
    print("\nverdict:", "PASS" if len(audio) <= 1 and len(starts) <= len(tasks) else "CHECK")
    for l in (audio + fromcache)[:4]:
        print("  ", l.split("INFO", 1)[-1].strip()[:180])


if __name__ == "__main__":
    main()
