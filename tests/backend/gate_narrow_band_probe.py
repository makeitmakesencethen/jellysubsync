#!/usr/bin/env python3
"""Does a narrower gate (very high score AND a small ruler offset) save anything, and is it safe?

Two questions, answered from the S31 fixture's own cue times:

1. Is the blind spot offset-dependent? A ruler that is the same cut but offset correlates perfectly at
   *any* offset, so if the score is blind at 25 s it is blind at 2 s too - measured here.
2. Can a "small offset" condition ever skip a pass that would otherwise run? The plugin only runs the
   audio cross-check when the ruler demands more than a third of the reference ceiling (10 s by
   default), so any band narrow enough to be called small sits below that trigger: it skips nothing.

Also reports what the score actually reaches on this fixture, against a 0.95 bar.
"""
import json
import os
import subprocess
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from s31_quality_curves import SOURCE, cue_starts, shift_curve  # noqa: E402

RUNNER = 'tests/backend/qfit_runner/qfit_runner.csproj'
DOTNET = '/opt/data/.dotnet/dotnet'
CEILING_S = 30.0        # MaxSubtitleReferenceOffsetSeconds default
SUSPICIOUS_S = CEILING_S / 3.0   # 10 s: below this the cross-check does not run today
WINDOW_S = 60.0         # the engine's search window (--max-offset-seconds), which the gate uses


def main():
    source = cue_starts(SOURCE)
    offsets = [2.0, 5.0, 10.0, 20.0, 25.0, 60.0]
    variants = {}
    for off in offsets:
        target = list(source)
        ruler = [t + off for t in source]
        shifts, scores = shift_curve(target, ruler, lo=-WINDOW_S, hi=WINDOW_S, sigma=0.5)
        name = f'offset_{int(off)}s'
        variants[name] = {'shifts': shifts, 'scores': scores, 'peak': max(scores),
                          'peak_shift': shifts[scores.index(max(scores))]}
    json.dump({'variants': variants}, open('/tmp/gate-offsets.json', 'w'))
    out = subprocess.run(
        [DOTNET, 'run', '-c', 'Release', '--project', RUNNER, '--', '/tmp/gate-offsets.json'],
        capture_output=True, text=True, cwd='.',
        env={**os.environ, 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT': '1'})

    scores = {}
    for line in out.stdout.splitlines():
        cols = line.split()
        if len(cols) > 6 and cols[0] in variants:
            scores[cols[0]] = (float(cols[6]), 'TRUSTWORTHY' in line)

    print(f'S31 fixture cues, window +/-{WINDOW_S:.0f} s, cross-check trigger: |demand| > {SUSPICIOUS_S:.0f} s')
    print()
    print(f'{"ruler offset":>13} {"score":>7} {"bar 0.75":>9} {"bar 0.95":>9} '
          f'{"cross-check today":>18} {"narrow gate skips":>18} {"written answer":>15}')
    for off in offsets:
        name = f'offset_{int(off)}s'
        quality, trusted = scores[name]
        runs_today = off > SUSPICIOUS_S
        narrow = quality >= 0.95 and off < 5.0
        print(f'{off:>11.0f} s {quality:>7.3f} {("trusted" if trusted else "doubted"):>9} '
              f'{("passes" if quality >= 0.95 else "fails"):>9} '
              f'{("RUNS" if runs_today else "does not run"):>18} '
              f'{("SKIPS a pass" if narrow and runs_today else ("skips nothing" if not runs_today else "does not fire")):>18} '
              f'{("wrong by " + format(off, "0.0f") + " s") if narrow else "-":>15}')
    print()
    print('Field cross-reference (the server\'s own plugin log, 2026-09-12..15):')
    print('  710 runs used a subtitle ruler; 8 runs (1.1 %) demanded a shift over 10 s - all in the')
    print('  10-30 s band, none under 10 s. A gate that only skips under a few seconds meets none of them.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
