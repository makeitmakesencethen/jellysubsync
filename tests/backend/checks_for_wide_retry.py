#!/usr/bin/env python3
"""Update the checks for 2.0.18: renamed predicate, wide retry, and the ceiling no longer ending the job."""
import pathlib

p = pathlib.Path('tests/run_checks.py')
c = p.read_text()

# the predicate was renamed, since it now guards the stretch and the wide retry
n = c.count('StretchHoldsAgainstAudio')
c = c.replace('StretchHoldsAgainstAudio', 'AlignmentHoldsAgainstAudio')
print('renamed the predicate in %d check lines' % n)

# the harness cases read the new name; state what they cover now
c = c.replace('Check("a stretch the audio agrees with holds"', 'Check("a result the audio agrees with holds"', 1)
c = c.replace('Check("a stretch the audio still wants 40 s of does not hold"',
              'Check("a result the audio still wants 40 s of does not hold"', 1)
c = c.replace('Check("a stretch the audio wants rescaled again does not hold"',
              'Check("a result the audio wants rescaled again does not hold"', 1)

# the old ceiling refusal check: the ceiling is now one wide retry plus a second check
old = """    report('a result pinned to the offset ceiling is refused too, with the measured number',
           'refusing a result pinned to the offset ceiling' in service
           and 'so the engine was clamped - the file is still written' not in service)"""
new = """    report('a result at the offset ceiling gets one wide retry, checked against the audio again',
           'Math.Max(config.MaxOffsetSeconds * 4, 300)' in service
           and 'wide-allowance.srt' in service
           and 'wide-verify.srt' in service
           and 'retrying this subtitle with a {wideSeconds} s' in service
           and 'AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds)' in service
           and 'the alignment is not stable' in service
           and 'so the engine was clamped - the file is still written' not in service
           and 'wideAllowanceApplied' in service)"""
assert old in c, 'ceiling check not found'
c = c.replace(old, new, 1)

# name the source check for what it now covers as well
c = c.replace("report('a stretch is tested against the film\\'s audio, and only when something was stretched',",
              "report('a stretch is tested against the film\\'s audio, and only when something was stretched',", 1)

p.write_text(c)
print('checks updated')
