#!/usr/bin/env python3
"""Follow the design as it now stands: rigid shift, refinement rounds, and the check names that go with them."""
import pathlib

checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()
old = """    report('a result at the offset ceiling applies the measured shift rigidly and checks it against the audio',
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
new = """    report('a result at the offset ceiling shifts rigidly, refines against the audio, and checks every round',
           'private string? ShiftSrtBy(string inputPath, long shiftMs, string tempDir, SyncJob job)' in service
           and 'shifted-input.srt' in service
           and 'shifted-verify.srt' in service
           and "the engine's search is not widened: a wider window is how a wrong lock gets in" in service
           and 'for (var round = 1; round <= 3 && !accepted; round++)' in service
           and 'the check asked for {residualShift} ms more, so the subtitle is shifted ' in service
           and 'holds against the film\\'s audio' in service
           and 'shifting it by the measurement did not ' in service
           and 'AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds)' in service
           # a reference that has already been released: the check falls back to the film itself
           and 'the stored speech reference was no longer readable' in service
           # the widened search is gone: it locked onto the wrong part of the audio on the user's file
           and 'wide-allowance.srt' not in service
           and 'Math.Max(config.MaxOffsetSeconds * 4, 300)' not in service
           and 'so the engine was clamped - the file is still written' not in service
           and 'wideAllowanceApplied' in service)"""
assert old in c, 'check not found'
checks.write_text(c.replace(old, new, 1))
print('check follows the converging design')

# the test server's deploy: meta.json is not in the build output, so take it from the source
s = pathlib.Path('tests/backend/ship_2_0_19.sh')
t = s.read_text()
old = 'cp "$DLL" "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"'
new = ('cp "$DLL" "$JF/data/plugins/SubSync_2.0.15.0/"\n'
       '# `dotnet build` does not always leave meta.json beside the dll; the source copy is authoritative.\n'
       'if [ -f "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" ]; then\n'
       '  cp "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"\n'
       'else\n'
       '  cp "$REPO/Jellyfin.Plugin.SubSync/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"\n'
       'fi')
assert old in t
s.write_text(t.replace(old, new, 1))
print('deploy takes meta.json from wherever it exists')
