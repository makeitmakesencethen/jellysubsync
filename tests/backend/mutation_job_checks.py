#!/usr/bin/env python3
"""Mutation verification for the RunSyncJob characterization checks (tests/job_checks.cs).

A characterization check that still passes against a deliberately broken version of the phase it covers is not
coverage. Each entry below breaks one production line the named check exists to protect, rebuilds the logictest
harness and records which of the new checks failed. The source is always put back, and the exit status is
non-zero if any mutation left every check green.

Run it after `python3 tests/run_checks.py` has built the harness (it reuses .tests-work, it does not wipe it):

    python3 tests/backend/mutation_job_checks.py            # every mutation
    python3 tests/backend/mutation_job_checks.py P7 P19     # named ones only
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = REPO / '.tests-work'
SERVICE = REPO / 'Jellyfin.Plugin.SubSync' / 'Services' / 'SubSyncService.cs'
LABELS = re.compile(r'FAIL  (P5|P7|P8|P9|P10|P12|P13|P14|P15|P17|P18|P19|S43|S45|RunSyncJob)')

# name -> (text to break, what it becomes)
MUTATIONS = {
    'P7': ('job.Outcome = "already in sync (shift under 3 s) \\u2014 no change needed"',
           'job.Outcome = "already in sync (shift under 4 s) \\u2014 no change needed"'),
    'P12': ('job.Outcome = $"already in sync ({noChange.Describe()}) \\u2014 nothing written"',
            'job.Outcome = $"already in sync ({noChange.Describe()}) \\u2014 nothing to write"'),
    'P13': ('job.Phase = "Unverified \\u2014 audio-only alignment";',
            'job.Phase = "Unverified - audio-only alignment";'),
    'P14': ('if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)',
            'if (!File.Exists(tempOutput))'),
    'P5': ('throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.{why}");',
           'throw new InvalidOperationException($"ffsubsync exited with status {exitCode}.{why}");'),
    'P15-copy': ('var target = SyncedTargetName(dir, stem, lang);',
                 'var target = Path.Combine(dir, "mutant-target.srt");'),
    'P17': ('if (LooksLikeSignsTrack(inputCues, videoDuration))',
            'if (false && LooksLikeSignsTrack(inputCues, videoDuration))'),
    'P15-replace': ('backupPath = NextBackupPath(subtitleStream.Path);',
                    'backupPath = subtitleStream.Path + ".bak-mutant";'),
    'P19': ('if (backupPath is not null && File.Exists(backupPath))',
            'if (false && File.Exists(backupPath))'),
    'P10-refused': ('&& !IsRescaleAcceptable(scaled.Ratio, scaled.ShiftMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config), config.FixFramerate))',
                    '&& false)'),
    'P10-accepted': ('return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L * 20;',
                     'return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L / 100;'),
    'P9-nothing': ('$"refused: {detail}. Raise \\"Maximum offset\\" in the plugin settings (it is the search "',
                   '$"refused: {detail}. Change the search window in the plugin settings (it is the search "'),
    'P9-clamped': ('? $"the {wideSeconds} s window also reached its limit ({stillClamped.ShiftMs} ms)"',
                   '? $"the wider window reached its limit too ({stillClamped.ShiftMs} ms)"'),
    'P9-verify': ('&& AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))',
                  '&& true)'),
    'P9-accepted': ('tempOutput = wideOutput;\n                        measured = wider;',
                    'tempOutput = Path.Combine(tempDir, "synced.srt");\n                        measured = wider;'),
    'P8-discard': ('&& (Math.Abs(fromReference.ShiftMs) > referenceCeilingMs || rulerSpreadTooWide))',
                   '&& (Math.Abs(fromReference.ShiftMs) > referenceCeilingMs * 100.0 || rulerSpreadTooWide))'),
    'P8-refusal': ('job.Error = $"refused: the subtitle was aligned against the file\'s own subtitle track {referenceSpec}, "',
                   'job.Error = $"refused: the subtitle was aligned against a subtitle track {referenceSpec}, "'),
    'S43-vad': ('referencePath = referenceTarget;\n                                    referenceStream = null;\n                                    usedSubtitleReference = true;\n                                    job.Phase = SyncPhaseLabel(fromCache: false, audioReference: false);',
                'referencePath = videoPath;\n                                    referenceStream = null;\n                                    usedSubtitleReference = true;\n                                    job.Phase = SyncPhaseLabel(fromCache: false, audioReference: false);'),
    'P8-stale': ('if (audioExit == 0 && File.Exists(audioOutput))',
                 'if (audioExit == 0 && File.Exists(tempOutput))'),
    'P18-catch': ('catch (Exception ex)\n                {\n                    _logger.LogWarning(ex, "Refreshing item {ItemId} failed; the subtitle is written and appears after the next scan", video.Id);',
                  'catch (InvalidOperationException ex)\n                {\n                    _logger.LogWarning(ex, "Refreshing item {ItemId} failed; the subtitle is written and appears after the next scan", video.Id);'),
}


def environment():
    """The environment the suite runs its harness in, with the stand-in engine on PATH."""
    env = dict(
        os.environ,
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
        LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu',
        TZ='UTC',
    )
    env['PATH'] = '/opt/data/.dotnet' + os.pathsep + env.get('PATH', '')
    sys.path.insert(0, str(REPO / 'tests'))
    import run_checks
    engine_dir, fake_ffmpeg = run_checks.prepare_fake_engine(str(REPO / '.tests-work' / 'fakes'))
    env['SUBSYNC_FAKE_ENGINE'] = engine_dir
    env['SUBSYNC_FAKE_FFMPEG'] = fake_ffmpeg
    env['PATH'] = engine_dir + os.pathsep + env['PATH']
    return env


def run_harness(env):
    """Rebuilds and runs the harness; returns (stdout, error).

    The suite creates its own scratch projects under .tests-work while it runs (the B13 probe, for one). A second
    build of logictest would glob their sources in and fail on a duplicate entry point, so they are dropped first -
    the suite recreates what it needs.
    """
    for child in WORK.iterdir():
        if child.is_dir() and any(child.glob('*.csproj')):
            shutil.rmtree(child, ignore_errors=True)
    build = subprocess.run(
        ['dotnet', 'build', '-c', 'Release', '--nologo', '-v', 'q'], cwd=WORK, capture_output=True, text=True, env=env)
    if build.returncode != 0:
        return None, (build.stdout + build.stderr)[-800:]
    run = subprocess.run(
        ['dotnet', str(WORK / 'bin' / 'Release' / 'net10.0' / 'logictest.dll')],
        cwd=WORK, capture_output=True, text=True, env=env, timeout=1800)
    return run.stdout + run.stderr, None


def failures(output):
    return [line.split('  ', 1)[1].split('  [')[0].strip() for line in output.splitlines() if LABELS.match(line)]


def main():
    if not (WORK / 'Program.cs').exists():
        print('run `python3 tests/run_checks.py` first: the harness is built from .tests-work/Program.cs')
        return 2

    env = environment()
    original = SERVICE.read_text(encoding='utf-8')
    wanted = sys.argv[1:] or list(MUTATIONS)

    baseline, error = run_harness(env)
    if baseline is None:
        print('baseline build failed:', error)
        return 2
    if failures(baseline):
        print('baseline is not green:')
        for line in failures(baseline):
            print(' ', line)
        return 2
    passing = len([l for l in baseline.splitlines() if l.startswith('PASS') and LABELS.match('FAIL' + l[4:])])
    print(f'baseline: {passing} characterization check(s) pass, 0 fail')

    results = {}
    try:
        for name in wanted:
            if name not in MUTATIONS:
                print(f'{name}: unknown mutation')
                continue
            old, new = MUTATIONS[name]
            if old not in original:
                results[name] = 'ANCHOR NOT FOUND'
                print(f'{name}: ANCHOR NOT FOUND (the source moved)')
                continue
            SERVICE.write_text(original.replace(old, new, 1), encoding='utf-8')
            out, error = run_harness(env)
            if out is None:
                results[name] = 'BUILD FAILED'
                print(f'{name}: the mutation does not compile')
            else:
                caught = failures(out)
                results[name] = caught
                state = 'CAUGHT' if caught else 'MISSED'
                print(f'{name}: {state} -> ' + '; '.join(f[:70] for f in caught))
    finally:
        SERVICE.write_text(original, encoding='utf-8')

    restored = SERVICE.read_text(encoding='utf-8') == original
    print(f'\nsource put back: {restored}')
    print(json.dumps(results, indent=1))
    missed = [name for name, value in results.items() if value == [] or value == 'ANCHOR NOT FOUND' or value == 'BUILD FAILED']
    if missed:
        print('NOT VERIFIED BY ANY CHECK: ' + ', '.join(missed))
    return 1 if (missed or not restored) else 0


if __name__ == '__main__':
    sys.exit(main())
