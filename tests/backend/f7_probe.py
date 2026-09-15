#!/usr/bin/env python3
"""F7 on the rig: scratch directories left behind are swept without anyone pressing a button.

A job that is killed, or a server that is stopped mid-run, leaves its scratch directory in the cache root. Before this
batch, the only thing that ever removed one was the operator-facing "clear the cache" action, so the directory sat there
until somebody happened to press it. The fix sweeps them on the maintenance pass and once when the plugin loads.

What this measures, on a real server:

1. Two scratch directories are planted while the server is stopped, plus one directory that is *not* a job id. Loading
   the plugin must remove the first two and keep the third (the id rule is a safety rule, not a broom).
2. A scratch directory is planted while the server is running and a job is queued. The maintenance pass must remove it
   without anyone clearing the cache.
3. The plugin's own log has to say what it removed, so an operator can see it happened.

Usage: python3 tests/backend/f7_probe.py
"""

import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO = pathlib.Path(__file__).resolve().parents[2]
# The scratch root is the plugin's temp path (Jellyfin's cache dir), not its data dir: job scratch directories live
# beside `ref/`, and the log lives under the data dir.
SCRATCH_ROOT = pathlib.Path('/opt/data/jf12test/cache/subsync')
LOG = pathlib.Path('/opt/data/jf12test/data/data/subsync/logs/subsync.log')
IDS = json.loads((REPO / 'tests/gui/ids.json').read_text(encoding='utf-8'))
BASE = 'http://127.0.0.1:8096'

results = []
failures = 0


def report(name, ok, detail=''):
    global failures
    results.append({'check': name, 'ok': bool(ok), 'detail': str(detail)[:400]})
    print(('  PASS  ' if ok else '  FAIL  ') + name + (f'   [{detail}]' if detail else ''))
    if not ok:
        failures += 1


def note(text):
    print('  NOTE  ' + text)


def call(path, method='GET', body=None, token=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, method=method)
    request.add_header('Authorization', f'MediaBrowser Token="{token or IDS["admin_token"]}"')
    request.add_header('Content-Type', 'application/json')
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            raw = response.read().decode('utf-8', 'replace')
            return response.status, (json.loads(raw) if raw.strip().startswith(('{', '[')) else raw)
    except urllib.error.HTTPError as error:
        raw = error.read().decode('utf-8', 'replace')
        try:
            return error.code, json.loads(raw)
        except json.JSONDecodeError:
            return error.code, raw


def plant(name):
    directory = SCRATCH_ROOT / name
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'scratch.txt').write_text('left behind by a job that never finished\n', encoding='utf-8')
    return directory


def rig_pids():
    found = subprocess.run(['pgrep', '-f', 'jellyfin.dll'], capture_output=True, text=True).stdout.split()
    return [int(pid) for pid in found]


def stop_rig():
    for pid in rig_pids():
        try:
            os.kill(pid, 15)
        except ProcessLookupError:
            continue
    deadline = time.time() + 45
    while time.time() < deadline and rig_pids():
        time.sleep(1)
    for pid in rig_pids():
        try:
            os.kill(pid, 9)
        except ProcessLookupError:
            continue


def main():
    if not (REPO / 'tests/rig/run_scenario.py').exists():
        print('the rig is missing')
        return 2

    if rig_pids():
        note('a server was already running; stopping it so the planted directories are seen at load time')
        stop_rig()

    guid_a = 'a1b2c3d4e5f60718293a4b5c6d7e8f90'
    guid_b = '0f1e2d3c4b5a69788796a5b4c3d2e1f0'
    guid_c = 'abcdef0123456789abcdef0123456789'

    for name in ('not-a-job-id', guid_a, guid_b, guid_c):
        shutil.rmtree(SCRATCH_ROOT / name, ignore_errors=True)

    # Directories left behind by earlier runs, before this fix existed: they are the defect's own evidence, so the count
    # is recorded and they are expected to go with the rest.
    already_there = sorted(path.name for path in SCRATCH_ROOT.iterdir()
                           if path.is_dir() and re.fullmatch(r'[0-9a-f]{32}', path.name))
    note(f'{len(already_there)} scratch director(ies) were already lying there from earlier runs: {already_there}')

    plant(guid_a)
    plant(guid_b)
    plant('not-a-job-id')

    log_offset = LOG.stat().st_size if LOG.exists() else 0
    note(f'scratch root: {SCRATCH_ROOT}')

    print('starting the rig (the plugin load is what should sweep them)...')
    started = subprocess.run(
        [sys.executable, 'tests/rig/run_scenario.py', '--scenario', 'smoke', '--keep-rig'],
        cwd=str(REPO), capture_output=True, text=True, timeout=900)
    if started.returncode != 0:
        print(started.stdout[-3000:])
        print(started.stderr[-2000:])
        report('F7: the rig starts with the planted directories in place', False, f'exit {started.returncode}')
        return 1

    report('F7: the rig starts with the planted directories in place', True, 'smoke run passed')
    survived = [name for name in (guid_a, guid_b, *already_there) if (SCRATCH_ROOT / name).exists()]
    report('F7: every scratch directory left by a dead job is gone once the plugin has loaded',
           not survived,
           f'{len(survived)} of {2 + len(already_there)} still there')
    report('F7: a directory that is not a job id is left alone',
           (SCRATCH_ROOT / 'not-a-job-id').exists(),
           'the id rule still holds while sweeping')

    log_text = LOG.read_text(encoding='utf-8', errors='replace')[log_offset:]
    removed = re.findall(r'startup: removed (\d+) orphaned job scratch director', log_text)
    report('F7: the plugin says what it removed at startup',
           bool(removed) and int(removed[0]) == 2 + len(already_there),
           f'log said {removed}, expected {2 + len(already_there)}')

    # Now the maintenance half: a directory that appears while the server is running goes with the next pass, which
    # happens while a job is being worked on.
    guid_c = 'abcdef0123456789abcdef0123456789'
    plant(guid_c)
    log_offset = LOG.stat().st_size
    status, queued = call('/SubSync/Sync', 'POST', {'itemId': IDS['movie'], 'subtitleIndex': 0})
    note(f'queued a job to drive the maintenance pass (HTTP {status})')

    gone_at = None
    deadline = time.time() + 180
    while time.time() < deadline:
        if not (SCRATCH_ROOT / guid_c).exists():
            gone_at = time.time()
            break
        time.sleep(3)

    maintenance_log = LOG.read_text(encoding='utf-8', errors='replace')[log_offset:]
    swept = re.findall(r'sweep: removed (\d+) orphaned job scratch director', maintenance_log)
    report('F7: a scratch directory that appears while the server runs is swept by maintenance, with no button pressed',
           gone_at is not None,
           f'swept after {round(gone_at - (time.time() - 180))}s' if gone_at else 'still there after 180 s')
    report('F7: the maintenance sweep is reported in the log too',
           bool(swept), f'log said {swept}')

    # The reference tree shares this directory and is not a job id, so it must still be there once the job has run (it
    # is created on first use, which is why this is checked here rather than at load time).
    report('F7: the reference cache beside the job directories is left alone',
           (SCRATCH_ROOT / 'ref').exists(),
           'only job scratch directories are candidates')

    stop_rig()
    shutil.rmtree(SCRATCH_ROOT / 'not-a-job-id', ignore_errors=True)
    shutil.rmtree(SCRATCH_ROOT / guid_c, ignore_errors=True)

    out = REPO / 'tests/backend/f7_results.json'
    out.write_text(json.dumps({'probe': 'f7', 'checks': results,
                               'failures': failures}, indent=2), encoding='utf-8')
    print(f'\n{out}')
    print('F7 PROBE: ' + ('ALL PASS' if failures == 0 else f'{failures} FAILURE(S)'))
    return 0 if failures == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
