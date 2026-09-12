#!/usr/bin/env python3
"""Rewrite the ceiling check for the new behaviour: the measured shift applied rigidly, then checked."""
import pathlib

p = pathlib.Path('tests/run_checks.py')
c = p.read_text()
old = """    report('a result at the offset ceiling gets one wide retry, checked against the audio again',
           'Math.Max(config.MaxOffsetSeconds * 4, 300)' in service
           and 'wide-allowance.srt' in service
           and 'wide-verify.srt' in service
           and 'retrying this subtitle with a {wideSeconds} s' in service
           and 'AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds)' in service
           and 'the alignment is not stable' in service
           and 'so the engine was clamped - the file is still written' not in service
           and 'wideAllowanceApplied' in service)"""
new = """    report('a result at the offset ceiling applies the measured shift rigidly and checks it against the audio',
           'private string? ShiftSrtBy(string inputPath, long shiftMs, string tempDir, SyncJob job)' in service
           and 'shifted-input.srt' in service
           and 'shifted-verify.srt' in service
           and "the engine's search is not widened: a wider window is how a wrong lock gets in" in service
           and 'AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds)' in service
           and 'applying that shift did not hold up' in service
           # the widened search is gone: it locked onto the wrong part of the audio on the user's file
           and 'wide-allowance.srt' not in service
           and 'Math.Max(config.MaxOffsetSeconds * 4, 300)' not in service
           and 'so the engine was clamped - the file is still written' not in service
           and 'wideAllowanceApplied' in service)"""
assert old in c, 'check not found'
p.write_text(c.replace(old, new, 1))
print('check rewritten for the rigid-shift design')
