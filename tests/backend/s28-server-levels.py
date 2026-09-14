#!/usr/bin/env python3
"""S28 server test: per-file / per-level throughput of concurrent audio-ruler walks.

Reads the plugin log (default /subsync-logs/subsync.log) and reports, for a fixed file set,
the engine wall clock of every audio-ruler run, grouped by the worker limit in effect,
plus the level wall clock and aggregate throughput.

Usage: s28-server-levels.py [--log PATH] [--files A B C D] [--all]
Validity guard: a run counts only if it logged reference=a:0 and cachedSpeech=False.
"""
import argparse, datetime, re, sys, collections

DEFAULT_FILES = [
    "Outsiders - S01E01.H.265.mkv", "Outsiders - S01E02.H.265.mkv",
    "Outsiders - S01E03.H.265.mkv", "Outsiders - S01E04.H.265.mkv",
]
TS = re.compile(r'^(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+)Z')
DISPATCH = re.compile(r'dispatch: starting (\d+), running (\d+), limit (\d+)')
START = re.compile(r'ffsubsync start: .*?cachedSpeech=(True|False) reference=(\S+)')
END = re.compile(r'^(completed|UNVERIFIED|REFUSED|FAILED|FAIL)\b(.*)$')
FILEFIELD = re.compile(r'\bfile=(.+?)(?: · |$)')
OUTFIELD = re.compile(r'\boutput=(\S+)')


def parse_ts(line):
    m = TS.match(line)
    return datetime.datetime.strptime(m.group(1), '%Y-%m-%d %H:%M:%S.%f') if m else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--log', default='/subsync-logs/subsync.log')
    ap.add_argument('--files', nargs='*', default=DEFAULT_FILES)
    ap.add_argument('--all', action='store_true', help='ignore the file filter')
    ap.add_argument('--since', default=None,
                    help="only runs starting at/after this UTC time, 'YYYY-MM-DD HH:MM:SS'")
    args = ap.parse_args()

    since = (datetime.datetime.strptime(args.since, '%Y-%m-%d %H:%M:%S')
             if args.since else None)
    lines = open(args.log, errors='replace').read().splitlines()
    dispatch, jobs = [], collections.defaultdict(dict)

    for line in lines:
        t = parse_ts(line)
        if t is None:
            continue
        d = DISPATCH.search(line)
        if d:
            dispatch.append((t, int(d.group(3)), int(d.group(2))))
            continue
        jm = re.search(r'\bjob ([0-9a-f]{32})\b', line) or re.search(r'\[([0-9a-f]{32})\]', line)
        if not jm:
            continue
        jid, rest = jm.group(1), line[jm.end():]
        s = START.search(rest)
        if s:
            # keep the FIRST start of this job: a job that lands on the search window is retried once
            # with a doubled window, and that second run reads a cached analysis - timing it would
            # measure the cheap run instead of the job's real cost.
            jobs[jid].setdefault('t0', t)
            jobs[jid]['runs'] = jobs[jid].get('runs', 0) + 1
            if 'cached' not in jobs[jid] or (jobs[jid]['cached'] and s.group(1) == 'False'):
                jobs[jid]['cached'] = (s.group(1) == 'True')
            jobs[jid].setdefault('ref', s.group(2))
            jobs[jid].setdefault('limit', limit_at(dispatch, t))
            continue
        e = END.match(rest.strip())
        if e and 't0' in jobs[jid]:
            fm, om = FILEFIELD.search(rest), OUTFIELD.search(rest)
            jobs[jid].update(t1=t, outcome=e.group(1),
                             file=fm.group(1) if fm else None, out=om.group(1) if om else None)

    rows = []
    for jid, j in jobs.items():
        if 't0' not in j or 't1' not in j:
            continue
        name = (j.get('file') or j.get('out') or '')
        if not args.all and not any(f in name for f in args.files):
            continue
        if since and j['t0'] < since:
            continue
        rows.append(dict(jid=jid, name=name, dur=(j['t1'] - j['t0']).total_seconds(),
                         ref=j.get('ref'), cached=j.get('cached'), limit=j.get('limit'),
                         outcome=j.get('outcome'), runs=j.get('runs', 1), t0=j['t0'], t1=j['t1']))

    if not rows:
        print("no matching engine runs in the log yet")
        return 0

    print(f"{'limit':>5} {'files':>5} {'per-file s (min/med/max)':>28} {'level wall s':>13} "
          f"{'agg files/h':>12}  validity")
    by_limit = collections.defaultdict(list)
    for r in rows:
        by_limit[r['limit'][0] if r['limit'] else None].append(r)

    for lim in sorted(by_limit, key=lambda x: (x is None, x)):
        lvl = by_limit[lim]
        durs = sorted(r['dur'] for r in lvl)
        wall = (max(r['t1'] for r in lvl) - min(r['t0'] for r in lvl)).total_seconds()
        bad = [r for r in lvl if r['ref'] != 'a:0' or r['cached']]
        med = durs[len(durs) // 2]
        print(f"{str(lim):>5} {len(lvl):>5} {min(durs):>9.1f} {med:>8.1f} {max(durs):>8.1f} "
              f"{wall:>13.1f} {len(lvl) * 3600 / wall:>12.2f}  "
              f"{'OK' if not bad else 'VOID: ' + str(len(bad)) + ' not a fresh audio walk'}")

    print()
    for lim in sorted(by_limit, key=lambda x: (x is None, x)):
        for r in sorted(by_limit[lim], key=lambda r: r['t0']):
            flag = '' if (r['ref'] == 'a:0' and not r['cached']) else '  <-- INVALID (not audio/fresh)'
            rr = '' if r.get('runs', 1) == 1 else f" [{r['runs']} engine runs]"
            print(f"  limit={lim} {r['dur']/60:6.1f} min  {r['outcome']:10} "
                  f"{r['name'][:100]}{rr}{flag}")

    complete = {lim: len(v) for lim, v in by_limit.items()}
    if len(complete) < 2:
        print("\nonly one worker limit present -- need the same file set at 1, 2 and 4")
        return 0
    walls = {lim: (max(r['t1'] for r in v) - min(r['t0'] for r in v)).total_seconds()
             for lim, v in by_limit.items()}
    lo, hi = min(walls.values()), max(walls.values())   # values, not keys: min() on a dict returns a key
    print(f"\nlevel wall clock across limits: " +
          ", ".join(f"limit {k}: {v/60:.1f} min" for k, v in sorted(walls.items())))
    if hi <= lo * 1.25:
        print("VERDICT: wall clock is flat as concurrency rises -> QUEUEING regime. "
              "A cap costs no aggregate throughput and cuts per-file latency.")
    else:
        print("VERDICT: wall clock falls as concurrency rises -> BANDWIDTH regime. "
              "A cap costs aggregate throughput; per-file latency is bought with it.")
    return 0


def limit_at(dispatch, t):
    cur = (None, None)
    for dt, lim, run in dispatch:
        if dt <= t:
            cur = (lim, run)
        else:
            break
    return cur


if __name__ == '__main__':
    sys.exit(main())
