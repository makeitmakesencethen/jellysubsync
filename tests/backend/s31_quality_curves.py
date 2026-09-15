#!/usr/bin/env python3
"""Real shift-score curves from the S31 fixture, for the quality-of-fit head-to-head.

The curves are built the way a subtitle-ruler alignment sees the problem: for each candidate
global shift, count how many of the *target* subtitle's cues land on a cue of the *ruler* track.
That is the shape oseiskar/autosubsync's quality_of_fit.py scores (a peak rise, a peak fall and
two tails), and it is computed here from the fixture's own 803 cues, not from a synthetic array.

Usage: python3 s31_quality_curves.py [--out /tmp/s31-curves.json]
"""
import argparse
import json
import re
import sys
from pathlib import Path

FIXTURE = Path('/tmp/s31-fixture')
SOURCE = FIXTURE / 'source-track.srt'
TARGET_SHIFT = 5.0          # S31_TARGET_SHIFT: the target track is shifted +5 s
TARGET_SHIFT_CORRECT = 20.0  # S31_TARGET_SHIFT_CORRECT, used by the 'correct' ruler variant
SPAN_STRETCH = 1.02         # S31_SPAN_STRETCH: the other-cut ruler is the same track stretched

CUE = re.compile(r'(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->')


def cue_starts(path):
    text = Path(path).read_text(encoding='utf-8', errors='replace')
    out = []
    for m in CUE.finditer(text):
        h, mnt, s, ms = (int(g) for g in m.groups())
        out.append(h * 3600 + mnt * 60 + s + ms / 1000.0)
    return out


def shift_curve(target, ruler, lo=-40.0, hi=40.0, step=0.1, sigma=0.5):
    """Score per candidate shift, as a smooth correlation of the two tracks' cue times.

    autosubsync correlates per-frame *features* (a continuous function of the shift), so the curve
    it scores has a smooth bump at the alignment, not a plateau. Reproducing that shape here means
    a soft kernel rather than a binary match count: each target cue contributes
    exp(-(distance to the nearest ruler cue / sigma)^2), so a cue that nearly lines up still
    contributes and the peak has flanks to measure.
    """
    import bisect
    import math
    ruler_sorted = sorted(ruler)
    samples = []
    shifts = []
    s = lo
    while s <= hi + 1e-9:
        score = 0.0
        for t in target:
            want = t + s
            i = bisect.bisect_left(ruler_sorted, want)
            best = None
            if i < len(ruler_sorted):
                best = abs(ruler_sorted[i] - want)
            if i > 0:
                d = abs(ruler_sorted[i - 1] - want)
                best = d if best is None else min(best, d)
            if best is not None:
                score += math.exp(-((best / sigma) ** 2))
        shifts.append(round(s, 3))
        samples.append(round(score, 6))
        s += step
    return shifts, samples


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--out', default='/tmp/s31-curves.json')
    args = ap.parse_args()

    source = cue_starts(SOURCE)
    if len(source) < 100:
        print(f'source-track.srt has only {len(source)} cues - fixture missing?', file=sys.stderr)
        return 1

    target = [t + TARGET_SHIFT for t in source]
    target_correct = [t + TARGET_SHIFT_CORRECT for t in source]
    ruler_other_cut = [t * SPAN_STRETCH for t in source]
    ruler_correct = list(source)

    result = {'cues': len(source), 'variants': {}}
    for name, tgt, rul, demand in (
        ('other-cut', target, ruler_other_cut, -TARGET_SHIFT),
        ('correct', target_correct, ruler_correct, -TARGET_SHIFT_CORRECT),
    ):
        shifts, scores = shift_curve(tgt, rul)
        peak = max(scores)
        at = shifts[scores.index(peak)]
        result['variants'][name] = {
            'shifts': shifts,
            'scores': scores,
            'peak': peak,
            'peak_shift': at,
            'expected_demand_s': demand,
            'span_ruler_s': round(rul[-1] - rul[0], 3),
            'span_target_s': round(tgt[-1] - tgt[0], 3),
        }
        print(f'{name}: cue(s)={len(source)} peak={peak:.0f} at {at:+.1f} s '
              f'(expected {demand:+.1f} s) span ruler/target={rul[-1]-rul[0]:.1f}/{tgt[-1]-tgt[0]:.1f} s')

    Path(args.out).write_text(json.dumps(result))
    print(f'wrote {args.out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
