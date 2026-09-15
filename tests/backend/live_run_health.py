#!/usr/bin/env python3
"""Health read-out of the plugin's own log while a real batch is running.

Reads /subsync-logs/subsync.log (read-only mount of fabji's server log) and reports what the current
2.0.43 build is doing: which version is installed, the queue's shape from the S24 line, per-job
completion timings, refusals and failures with their reasons, the new 2.0.43 lines (ruler shape, split
penalty, sweep state), extraction plan misses, and anything that looks like an exception.
"""
import collections
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

LOG = Path('/subsync-logs/subsync.log')
LEVEL = re.compile(r'^\d{4}-\d\d-\d\d (\d\d:\d\d:\d\d\.\d+Z)\s+(\w+)\s+(.*)$')
QUEUE = re.compile(r'queued across (\d+) media file\(s\)')
DISPATCH = re.compile(r'dispatch: starting (\d+), running (\d+), limit (\d+), queued (\d+)')
COMPLETED = re.compile(r'job (\w+) completed: mode=(\S+) output=(\S+) bytes=(\S+) change=([^\n]{0,60})')


def main() -> int:
    text = LOG.read_text(encoding='utf-8', errors='replace')
    lines = text.splitlines()
    print(f'log: {len(lines)} lines, last line at {LEVEL.match(lines[-1]).group(1) if LEVEL.match(lines[-1]) else "?"}')

    versions = collections.Counter(re.findall(r'plugins/SubSync_([0-9.]+)/', text))
    print(f'installed plugin build(s) seen: {dict(versions)}')

    levels = collections.Counter(m.group(2) for m in (LEVEL.match(l) for l in lines) if m)
    print(f'log levels: {dict(levels)}')

    starts = collections.Counter(re.findall(r'job (\w+) (completed|FAILED|UNVERIFIED|REFUSED|cancelled|stopped)', text))
    status = collections.Counter(k for _, k in starts.items() for _ in [0])
    by_status = collections.Counter(s for _, s in starts)
    print(f'terminal job lines: {dict(by_status)}')

    print()
    print('queue, as the plugin reports it:')
    for line in [l for l in lines if l.strip().endswith('runnin' ) or ' media file(s), ' in l][-3:]:
        print('  ' + line[line.index('dispatch'):][:170])
    for line in [l for l in lines if 'queue: ' in l][-3:]:
        print('  ' + line[line.index('queue: '):][:170])

    print()
    print('2.0.43 lines:')
    shape = [l for l in lines if 'ruler shape:' in l]
    split = [l for l in lines if '--split-penalty' in l]
    sweep = [l for l in lines if 'sweep' in l.lower() and 'cache' in l.lower()]
    print(f'  ruler shape (only for a suspicious subtitle ruler): {len(shape)}')
    for l in shape[-3:]:
        print('    ' + l[l.index('ruler shape:'):][:150])
    print(f'  runs with --split-penalty in the engine argv: {len(split)}')
    for l in split[-2:]:
        print('    ' + re.search(r'--split-penalty \S+', l).group(0))
    print(f'  sweep/state lines: {len(sweep)}')

    print()
    print('extraction:')
    miss = [l for l in lines if 'missed its own prediction' in l]
    past = [l for l in lines if 'past the fetch' in l]
    twice = [l for l in lines if 'bytesTwice=' in l and 'bytesTwice=0.00 MB' not in l]
    print(f'  plan misses (WARN): {len(miss)}')
    for l in miss[-3:]:
        print('    ' + l[:200])
    print(f'  reads past the fetch: {len(past)}')
    print(f'  passes with bytes read twice: {len(twice)}')

    print()
    print('failures and refusals:')
    for kind in ('FAILED', 'REFUSED', 'UNVERIFIED'):
        hits = [l for l in lines if f' {kind}' in l]
        print(f'  {kind}: {len(hits)}')
        for l in hits[-3:]:
            print('    ' + l[l.index('job '):][:190] if 'job ' in l else '    ' + l[:190])

    print()
    exc = [l for l in lines if 'Exception' in l or 'Traceback' in l]
    print(f'exception-shaped lines: {len(exc)}')
    for l in exc[-4:]:
        print('  ' + l[:190])

    print()
    times = [m.group(1) for m in (LEVEL.match(l) for l in lines) if m]
    if times:
        print(f'window: {times[0]} .. {times[-1]}')
    jobs = list(COMPLETED.finditer(text))
    if len(jobs) >= 2:
        print(f'completions with a written sidecar: '
              f'{sum(1 for j in jobs if j.group(3) != "(none)")}, nothing-written: '
              f'{sum(1 for j in jobs if j.group(3) == "(none)")}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
