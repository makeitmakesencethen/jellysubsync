#!/usr/bin/env python3
"""Wait for one S28 test level to drain, then print that level's numbers (zero-token, self-terminating).

Armed per level with --since (the level's start time) and --files (a path substring). Prints nothing
until the level has produced its valid audio walks and the queue has gone quiet, then prints the
analyser output for that window and exits -- so a background run costs no tokens while waiting.

Usage: s28-watch-level.py --level 1 --files "Outsiders - S10E0" [--since "YYYY-MM-DD HH:MM:SS"]
"""
import argparse, datetime, re, subprocess, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
LOG = Path('/subsync-logs/subsync.log')
QUIET_S = 240          # no new log bytes for this long == the level is done
POLL_S = 45
MAX_WAIT_S = 3 * 3600


def stamp():
    return datetime.datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--level', required=True)
    ap.add_argument('--files', required=True)
    ap.add_argument('--since', default=None)
    ap.add_argument('--expect', type=int, default=5, help='valid audio walks to wait for')
    args = ap.parse_args()

    since = args.since or stamp()
    print(f"S28 level {args.level}: watching for {args.expect} fresh audio walks of '{args.files}' "
          f"from {since}Z", flush=True)

    started = time.time()
    out = ''
    last_size, last_change = LOG.stat().st_size, time.time()
    while time.time() - started < MAX_WAIT_S:
        time.sleep(POLL_S)
        size = LOG.stat().st_size
        if size != last_size:
            last_size, last_change = size, time.time()

        out = subprocess.run([sys.executable, str(HERE / 's28-server-levels.py'),
                              '--files', args.files, '--since', since],
                             capture_output=True, text=True).stdout
        valid = sum(1 for l in out.splitlines() if 'limit=' in l and ' min ' in l)  # per-run detail lines
        quiet = time.time() - last_change
        if valid >= args.expect and (quiet >= QUIET_S or 'running 0' in out):
            print(f"\n=== level {args.level} drained after {int(time.time()-started)}s "
                  f"({valid} runs)\n{out}", flush=True)
            return 0
        if quiet >= QUIET_S and valid:
            print(f"\n=== level {args.level} went quiet with only {valid}/{args.expect} runs\n{out}",
                  flush=True)
            return 0
    print(f"=== level {args.level}: gave up after {MAX_WAIT_S}s\n{out}", flush=True)
    return 1


if __name__ == '__main__':
    sys.exit(main())
