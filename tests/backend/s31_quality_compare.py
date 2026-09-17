#!/usr/bin/env python3
"""S31 head-to-head: autosubsync's shape-based quality score vs the plugin's current checks.

Both sides run against the S31 fixture's own 803 cues (extracted from the real 2.4 GB episode):

* the **shape score** is the plugin's C# `QualityOfFit` (a fresh C# reimplementation of
  oseiskar/autosubsync's `quality_of_fit.py`, MIT) run over a shift-score curve built from the
  target track and each ruler variant. Zero media reads: two subtitle files are enough.
* the **current checks** are the ones the plugin actually applies to a subtitle reference
  (cue-count parity, the 3 % span rule, `MaxSubtitleReferenceOffsetSeconds` = 30 s), plus the
  S31 audio cross-check that is the existing fix for the wrong-ruler case.

Usage: python3 tests/backend/s31_quality_compare.py [--out /tmp/s31-headtohead.json]
"""
import argparse
import bisect
import json
import math
import os
import subprocess
import sys

from _dotnet import find_dotnet
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from s31_quality_curves import (  # noqa: E402
    SOURCE, SPAN_STRETCH, TARGET_SHIFT, TARGET_SHIFT_CORRECT, cue_starts, shift_curve)

RUNNER = Path(__file__).parent / 'qfit_runner' / 'qfit_runner.csproj'
DOTNET = find_dotnet()
CEILING_S = 30.0     # MaxSubtitleReferenceOffsetSeconds default
SPAN_RULE = 3.0      # the 3 % span rule
MATCH_TOL = 0.30     # a cue counts as matched inside this many seconds


def best_match(tgt, rul):
    """Best global shift and how many target cues land on a ruler cue at it."""
    rs = sorted(rul)
    best, best_s = -1, None
    for step in range(-400, 401):
        shift = step / 10.0
        count = 0
        for t in tgt:
            want = t + shift
            i = bisect.bisect_left(rs, want)
            d = None
            if i < len(rs):
                d = abs(rs[i] - want)
            if i > 0:
                dd = abs(rs[i - 1] - want)
                d = dd if d is None else min(d, dd)
            if d is not None and d <= MATCH_TOL:
                count += 1
        if count > best:
            best, best_s = count, shift
    return best, best_s


def run_shape_score(variants, out_json):
    """Score the curves with the plugin's real C# QualityOfFit."""
    json.dump({'cues': None, 'variants': variants}, open(out_json, 'w'))
    res = subprocess.run(
        [DOTNET, 'run', '-c', 'Release', '--project', str(RUNNER), '--', out_json],
        capture_output=True, text=True,
        env={**os.environ, 'DOTNET_SYSTEM_GLOBALIZATION_INVARIANT': '1'})
    if res.returncode != 0:
        raise SystemExit(f'runner failed: {res.stdout[-800:]}\n{res.stderr[-800:]}')

    scores = {}
    for line in res.stdout.splitlines():
        cols = line.split()
        if len(cols) >= 7 and cols[0] in variants:
            scores[cols[0]] = {
                'peak': float(cols[1]), 'peak_shift_s': float(cols[2]),
                'monotonicity': float(cols[3]), 'prominence': float(cols[4]),
                'non_edgeness': float(cols[5]), 'quality': float(cols[6]),
                'verdict': 'TRUSTWORTHY' if 'TRUSTWORTHY' in line else 'REJECTED',
            }
        elif len(cols) >= 7 and cols[-1].startswith('('):
            pass
    return scores


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='/tmp/s31-headtohead.json')
    ap.add_argument('--sigma', type=float, default=0.5)
    args = ap.parse_args()

    source = cue_starts(SOURCE)
    if len(source) < 100:
        print(f'{SOURCE} holds {len(source)} cue(s) - rebuild the S31 fixture first', file=sys.stderr)
        return 1

    cases = {
        'other-cut': ([t + TARGET_SHIFT for t in source], [t * SPAN_STRETCH for t in source]),
        'correct': ([t + TARGET_SHIFT_CORRECT for t in source], list(source)),
    }

    curves = {}
    for name, (tgt, rul) in cases.items():
        shifts, scores = shift_curve(tgt, rul, sigma=args.sigma)
        curves[name] = {
            'shifts': shifts, 'scores': scores, 'peak': max(scores),
            'peak_shift': shifts[scores.index(max(scores))], 'cues': len(source),
        }

    shape = run_shape_score(curves, '/tmp/s31-curves-headtohead.json')

    report = {'sigma': args.sigma, 'cues': len(source), 'variants': {}}
    print(f'S31 head-to-head - {len(source)} cue(s) per track, score curve sigma={args.sigma} s')
    print()
    print(f'{"variant":<10} {"shape score":>12} {"threshold":>10} {"shape says":>12} | '
          f'{"parity":>7} {"span":>7} {"demand":>8} {"read-time says":>14}')
    for name, (tgt, rul) in cases.items():
        count, shift = best_match(tgt, rul)
        span_t, span_r = tgt[-1] - tgt[0], rul[-1] - rul[0]
        stretch = abs(span_r / span_t - 1) * 100
        parity_ok = len(tgt) == len(rul)
        span_ok = stretch < SPAN_RULE
        demand_ok = abs(shift) <= CEILING_S
        read_time = 'ACCEPTED' if (parity_ok and span_ok and demand_ok) else 'REFUSED'
        s = shape[name]
        print(f'{name:<10} {s["quality"]:>12.3f} {0.75:>10.2f} {s["verdict"]:>12} | '
              f'{"pass" if parity_ok else "FAIL":>7} {stretch:>6.2f}% {abs(shift):>7.1f}s '
              f'{read_time:>14}')
        report['variants'][name] = {
            'shape': s,
            'read_time': {'parity': parity_ok, 'span_stretch_pct': round(stretch, 3),
                          'span_ok': span_ok, 'ruler_demand_s': round(abs(shift), 3),
                          'demand_within_ceiling': demand_ok, 'verdict': read_time,
                          'match_at_best_shift': count, 'match_fraction': round(count / len(tgt), 4)},
        }

    Path(args.out).write_text(json.dumps(report, indent=2))
    print()
    print(f'wrote {args.out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
