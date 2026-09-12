#!/usr/bin/env python3
"""Checks for the stretch verification: unit cases in the harness, source checks in the suite."""
import pathlib

checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()

# unit cases, next to the arbiter's
anchor = 'Check("an unusable duration leaves the decision to the pair rule"'
idx = c.index(anchor)
line_end = c.index(");", idx) + 2
addition = '''

// A stretch is a claim about the whole timeline, and only the film's audio can test it: a differently cut subtitle
// produces the same span ratio as one from another framerate. A correct stretch leaves the audio alignment almost
// nothing to do; one applied to the wrong kind of difference still wants a large shift.
Check("a stretch the audio agrees with holds", SubSyncService.StretchHoldsAgainstAudio(1.0, 1200, 3000));
Check("a stretch the audio still wants 40 s of does not hold", !SubSyncService.StretchHoldsAgainstAudio(1.0, 40000, 3000));
Check("a stretch the audio wants rescaled again does not hold", !SubSyncService.StretchHoldsAgainstAudio(1.02, 500, 3000));
Check("the room grows with the runtime, up to a point", SubSyncService.StretchHoldsAgainstAudio(1.0, 12000, 4200));'''
c = c[:line_end] + addition + c[line_end:]

# source checks
anchor = "    report('the queued job carries the reason the interface shows',"
new = """    report('a stretch is tested against the film\\'s audio, and only when something was stretched',
           'private async Task<(string? Path, bool Dropped, string Input)> VerifyStretchAgainstAudioAsync(' in service_source
           and 'internal static bool StretchHoldsAgainstAudio(double ratio, long shiftMs, double videoSeconds)' in service_source
           and 'if (engineInput != subtitleInputPath)\\n            {\\n                var (verifiedPath, dropped, verifiedInput) = await VerifyStretchAgainstAudioAsync(' in service_source
           and 'the stretch does NOT hold against the audio' in service_source
           and 'aligned with offsets only' in service_source
           and 'the audio confirms the stretch' in service_source
           and '--no-fix-framerate' in service_source
           and 'a differently cut subtitle looks the same as a framerate mismatch in the spans' in service_source)
    report('the queued job carries the reason the interface shows',"""
assert anchor in c
c = c.replace(anchor, new, 1)
checks.write_text(c)
print('checks added for the stretch verification')
