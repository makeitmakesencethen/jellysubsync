#!/usr/bin/env python3
"""Measure the gate's shape score on synthetic tracks with realistic (irregular) cue spacing.

The check suite builds its fixture from a fixed pattern; a repeating pattern aliases into a comb of
equally good alignments and the score collapses. This script sweeps the spacing model to find one the
score treats the way it treats the real S31 episode (803 irregular cues), so the check mirrors reality.
"""
import bisect
import json
import math
import os
import random
import subprocess
import sys

RUNNER = 'tests/backend/qfit_runner/qfit_runner.csproj'
DOTNET = '/opt/data/.dotnet/dotnet'


def build(count=600, shift=0.0, stretch=1.0, mode='random', seed=42):
    rng = random.Random(seed)
    cues, cursor = [], 33000.0
    for _ in range(count):
        cues.append(cursor * stretch + shift)
        if mode == 'random':
            cursor += 700.0 + rng.random() * 2000.0
        elif mode == 'pattern':
            i = len(cues)
            cursor += 1000.0 + ((i * 37) % 100) / 100.0 * 1500.0 + ((i * 11) % 7) * 120.0
        else:
            cursor += 1000.0
    return cues


def curve(target, ruler, lo=-60.0, hi=60.0, step=0.1, sigma=0.5):
    rs = sorted(ruler)
    n = int(round((hi - lo) / step)) + 1
    out = []
    for i in range(n):
        s = lo + i * step
        total = 0.0
        for t in target:
            w = t + s
            j = bisect.bisect_left(rs, w)
            d = None
            if j < len(rs):
                d = abs(rs[j] - w)
            if j > 0:
                dd = abs(rs[j - 1] - w)
                d = dd if d is None else min(d, dd)
            total += math.exp(-((d / sigma) ** 2))
        out.append(round(total, 6))
    return out


def score(variants):
    json.dump({'variants': variants}, open('/tmp/gate-synth.json', 'w'))
    out = subprocess.run(
        [DOTNET, 'run', '-c', 'Release', '--project', RUNNER, '--', '/tmp/gate-synth.json'],
        capture_output=True, text=True, cwd='.',
        env={**os.environ, 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT': '1'})
    rows = {}
    for line in out.stdout.splitlines():
        cols = line.split()
        if len(cols) > 6 and cols[0] in variants:  # names carry no spaces, so cols[0] is the name
            rows[cols[0]] = (float(cols[3]), float(cols[4]), float(cols[5]), float(cols[6]))
    return rows


def main():
    for mode in ('regular', 'pattern', 'random'):
        variants = {}
        for name, (tgt, rul) in {
            f'{mode}_same': (build(shift=0, mode=mode), build(shift=0, mode=mode)),
            f'{mode}_offset25': (build(shift=0, mode=mode), build(shift=25, mode=mode)),
            f'{mode}_stretched': (build(shift=0, mode=mode), build(stretch=1.02, mode=mode)),
        }.items():
            c = curve(tgt, rul)
            variants[name] = {
                'shifts': [round(-60.0 + i * 0.1, 2) for i in range(len(c))],
                'scores': c, 'peak': max(c),
                'peak_shift': round(-60.0 + c.index(max(c)) * 0.1, 1),
            }
        rows = score(variants)
        print(f'--- spacing model: {mode} ---')
        for name, (mono, prom, edge, quality) in sorted(rows.items()):
            verdict = 'TRUSTED' if quality >= 0.75 else 'doubted'
            print(f'  {name:<24} mono={mono:.3f} prom={prom:.3f} edge={edge:.3f} '
                  f'quality={quality:.3f}  {verdict}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
