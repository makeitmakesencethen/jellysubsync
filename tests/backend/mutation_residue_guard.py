#!/usr/bin/env python3
"""Catch mutation-driver residue before it is mistaken for a result.

A mutation driver writes a deliberately broken line into the production source, rebuilds the harness,
runs it and puts the source back in a `finally`. A driver that is SIGKILLed never reaches that
`finally`, so the broken line stays in the tree: the *next* run then reports a false red, and a build
or a release made in that state ships the mutation.

Measured, one residue at a time (FIX_PLAN T1, 2026-09-17):

    P8-refusal  resident in Services/SubSyncService.JobPipeline.cs -> the job baseline fails exactly
                "P8: a wrong-cut ruler whose audio retry produces nothing refuses and says nothing was written"
    P10-accepted resident in Services/AlignmentMetrics.cs          -> the job baseline fails exactly
                "P10: a PAL-like rescale with correction on is written, and the outcome names the factor"

and those two together are the exact two-name red that was reported twice that night. For contrast,
`P8-stale` and `P8-discard` residue each fail that same P8 check *and* the S45 stale-output check, a
pair that was never seen - which is what lets a resident mutation be identified from its failure set.

Usage:

    python3 tests/backend/mutation_residue_guard.py             # is the production tree clean?
    python3 tests/backend/mutation_residue_guard.py --selftest  # plant each known residue, prove it shows

Exit 0 for "clean" or "detected and restored", 2 for residue found in the tree, 1 for a selftest
mismatch. `--selftest` rebuilds the harness twice, so it takes a couple of minutes.
"""

import argparse
import pathlib
import signal
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
PRODUCTION = 'Jellyfin.Plugin.SubSync'

# name -> (the check names a residue of that mutation must produce, in order)
#
# Measured one mutation per process, on a clean tree (2026-09-18). Do not take these sets from the
# driver's own sweep: its runs are sequential and a check can start failing for reasons unrelated to
# the mutation under test once the sequence has gone on a while (measured: `P3-cache-hit` fails only
# its own P3 check in a fresh process, and `P10-accepted` fails the accepted-twin check in a fresh
# process, while the same sweep reported P10 failing for fifteen late mutations, `P10-accepted`
# among them reported against the *refused* twin instead). So a set is a hint, not a fingerprint.
KNOWN_RESIDUES = {
    'P8-refusal': ['P8: a wrong-cut ruler whose audio retry produces nothing refuses and says nothing was written'],
    'P10-accepted': ['P10: a PAL-like rescale with correction on is written, and the outcome names the factor'],
}

sys.path.insert(0, str(REPO / 'tests' / 'backend'))
sys.path.insert(0, str(REPO / 'tests'))


def dirty_sources():
    """Production source files that differ from HEAD - a residue, or an edit in progress."""
    out = subprocess.run(['git', 'status', '--porcelain', '--', PRODUCTION],
                         cwd=REPO, capture_output=True, text=True).stdout
    return [line.strip() for line in out.splitlines() if line.strip()]


def check(quiet=False):
    """True when no production source differs from HEAD."""
    dirty = dirty_sources()
    if not dirty:
        if not quiet:
            print(f'clean: no file under {PRODUCTION}/ differs from HEAD (no mutation residue)')
        return True
    print('RESIDUE (or an edit in progress): the production source is not what HEAD holds:')
    for line in dirty:
        print('  ', line)
    print()
    print('A mutation driver that was killed mid-run leaves exactly this, and the next baseline then')
    print('reports a false red that reads like a real finding (FIX_PLAN T1). Identify it before trusting')
    print('any result: `git diff -- Jellyfin.Plugin.SubSync` shows the broken line, and the failure set')
    print('names the mutation (see KNOWN_RESIDUES in this file). Restore with:')
    print(f'    git checkout HEAD -- {PRODUCTION}')
    return False


def selftest():
    """Plant each known residue, prove the baseline shows it, restore - and leave the tree clean."""
    import mutation_job_checks as driver

    env = driver.environment()
    planted = {}

    def restore(*_args):
        for path, text in planted.items():
            path.write_text(text, encoding='utf-8')
        sys.exit(3)

    for sig in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(sig, restore)

    failures = 0
    try:
        for name, expected in KNOWN_RESIDUES.items():
            path = driver.FILES.get(name, driver.SERVICE)
            old, new = driver.MUTATIONS[name]
            original = path.read_text(encoding='utf-8')
            if old not in original:
                print(f'{name}: ANCHOR NOT FOUND in {path.name} - the mutation text moved')
                failures += 1
                continue
            planted[path] = original
            path.write_text(original.replace(old, new), encoding='utf-8')
            out, err = driver.run_harness(env)
            if out is None:
                print(f'{name}: the harness did not build: {err}')
                failures += 1
            else:
                seen = driver.failures(out)
                ok = seen == expected
                print(f'{name}: residue in {path.name} -> {len(seen)} characterization failure(s)')
                for line in seen:
                    print('   -', line)
                if not ok:
                    print(f'   EXPECTED exactly: {expected}')
                    failures += 1
            path.write_text(original, encoding='utf-8')
            planted.pop(path, None)
    finally:
        for path, text in planted.items():
            path.write_text(text, encoding='utf-8')

    left = dirty_sources()
    if left:
        print('the selftest changed the production source and did not put it back:')
        for line in left:
            print('  ', line)
        failures += 1
    if failures:
        print(f'\nSELFTEST FAILED ({failures} problem(s))')
        return 1
    print('\nselftest passed: each known residue is visible in the baseline, and the tree is clean')
    return 0


def main():
    parser = argparse.ArgumentParser(description='Catch mutation-driver residue before it is mistaken for a result.')
    parser.add_argument('--selftest', action='store_true',
                        help='plant each known residue, prove the baseline shows it, restore')
    args = parser.parse_args()
    if args.selftest:
        return selftest()
    return 0 if check() else 2


if __name__ == '__main__':
    sys.exit(main())
