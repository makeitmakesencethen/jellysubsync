#!/usr/bin/env python3
"""Mine the plugin's own logs for how a subtitle ruler actually behaves in the field.

Answers the question the narrow gate turns on: of the runs that used a *subtitle* as the ruler, how
many demanded a shift big enough to be worth cross-checking, and of those, how many turned out to be
the same cut mis-shifted (the audio confirmed the ruler) against how many were a different cut (the
audio disagreed and the ruler was discarded).

Reads /subsync-logs/subsync.log* (the server's plugin log, read-only here).
"""
import collections
import re
import sys
from pathlib import Path

LOGS = sorted(Path('/subsync-logs').glob('subsync.log*'))
ALIGNED = re.compile(r'note: aligned to the reference subtitle (\S+) at (-?\d+) ms')
REF_SUB = re.compile(r'reference: method=subtitle')
REF_AUDIO = re.compile(r'reference: method=audio'
                       r'|ffsubsync start: .*reference=a:\d')
CONFIRMED = re.compile(r'is confirmed by the film\'s own audio')
DISAGREED = re.compile(r'and the film\'s own audio disagree')


def main():
    kinds = collections.Counter()
    buckets = collections.Counter()
    shifts = []
    decisions = collections.Counter()
    per_log = {}

    for path in LOGS:
        text = path.read_text(encoding='utf-8', errors='replace')
        local = collections.Counter()
        local['subtitle_rulers'] = len(REF_SUB.findall(text))
        local['audio_rulers'] = len(REF_AUDIO.findall(text))
        local['confirmed'] = len(CONFIRMED.findall(text))
        local['disagreed'] = len(DISAGREED.findall(text))
        per_log[path.name] = local
        kinds.update(local)

        for _, ms in ALIGNED.findall(text):
            shifts.append(abs(int(ms)))

    for ms in shifts:
        s = ms / 1000.0
        if s < 1:
            buckets['<1 s'] += 1
        elif s < 3:
            buckets['1-3 s'] += 1
        elif s < 10:
            buckets['3-10 s'] += 1
        elif s < 30:
            buckets['10-30 s'] += 1
        else:
            buckets['>30 s'] += 1

    print(f'logs: {", ".join(p.name for p in LOGS)}')
    for name, local in per_log.items():
        print(f'  {name:<18} subtitle-ruler runs={local["subtitle_rulers"]:<5} '
              f'audio-ruler starts={local["audio_rulers"]:<5} '
              f'confirmed={local["confirmed"]:<4} disagreed={local["disagreed"]}')
    print()
    print(f'runs whose *reference* was a subtitle track        : {kinds["subtitle_rulers"]}')
    print(f'runs whose reference was the audio                 : {kinds["audio_rulers"]}')
    print(f'subtitle-ruler runs that demanded a shift anyway    : {len(shifts)} '
          f'(the "check the result" note)')
    print('  |demanded shift| distribution:')
    for label in ('<1 s', '1-3 s', '3-10 s', '10-30 s', '>30 s'):
        n = buckets[label]
        print(f'    {label:<8} {n:>5}  ({n / max(1, len(shifts)) * 100:4.1f} % of noted runs)')
    print()
    print(f'cross-checks that CONFIRMED the ruler (same cut, mis-shifted): {kinds["confirmed"]} '
          f'(the ruler\'s own offset was 20 s or more in the ones examined)')
    print(f'cross-checks that DISCARDED the ruler (a different cut)      : {kinds["disagreed"]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
