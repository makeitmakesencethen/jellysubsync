#!/usr/bin/env python3
"""B14 integration probe: does stopping the server leave anything behind?

The harness checks in tests/run_checks.py drive the teardown path directly (a tracked child process is killed and
counted, a lane that will not finish does not hold the wait open). This asks the harder question of a real server:
with a run in flight and child processes alive, does stopping Jellyfin actually stop them, and does it say so?

It queues real work on the rig, waits until the plugin is reading a file, records the child processes that exist at
that moment, sends the server SIGTERM the way a service stop does, and then checks that:

  * the plugin's own log carries the teardown line with numbers (processes asked to stop, processes that exited,
    how many lanes finished, how many entries were still tracked);
  * nothing is left running for this rig - no ffmpeg or ffsubsync process holding a media file from it.

Needs a rig on 127.0.0.1:8096 with an admin token in tests/gui/ids.json, and it is the probe that stops the rig (the
scenario run that started it will notice):

    python3 tests/rig/run_scenario.py --scenario smoke --keep-rig
    python3 tests/backend/b14_teardown_probe.py
"""
import json
import os
import pathlib
import re
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO = pathlib.Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:8096'
IDS = REPO / 'tests' / 'gui' / 'ids.json'
LOG = pathlib.Path('/opt/data/jf12test/data/data/subsync/logs/subsync.log')

failures = 0


def report(name, ok, detail=''):
    global failures
    if not ok:
        failures += 1
    print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail else ''))


def token():
    return json.loads(IDS.read_text(encoding='utf-8'))['admin_token']


def call(path, method='GET', body=None):
    data = json.dumps(body).encode('utf-8') if body is not None else None
    request = urllib.request.Request(
        BASE + path, data=data, method=method,
        headers={'Authorization': f'MediaBrowser Token="{token()}"', 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = response.read().decode('utf-8-sig')
    except urllib.error.HTTPError as error:
        payload = error.read().decode('utf-8-sig')
    try:
        return json.loads(payload) if payload.strip() else None
    except ValueError:
        return payload


def log_text():
    try:
        return LOG.read_text(encoding='utf-8', errors='replace')
    except OSError:
        return ''


def child_processes():
    """Processes that look like this rig's work: a media tool with a file from the rig in its command line."""
    out = subprocess.run(['ps', '-eo', 'pid,args'], capture_output=True, text=True).stdout.splitlines()
    found = []
    for line in out:
        if 'b14_teardown_probe' in line or 'ps -eo' in line:
            continue
        if ('ffmpeg' in line or 'ffsubsync' in line) and ('jf12test' in line or 'SubSync' in line):
            found.append(line.strip()[:160])
    return found


def server_pid():
    out = subprocess.run(['pgrep', '-f', 'jellyfin[.]dll'], capture_output=True, text=True).stdout.split()
    return int(out[0]) if out else None


def main():
    ids = json.loads(IDS.read_text(encoding='utf-8'))
    movie = ids['movie']

    before = log_text()

    # Children left by an earlier run (a rig that was killed with -9 keeps them) would be counted as this run's and
    # would make the result say the wrong thing. They are cleared first, and how many there were is reported.
    leftovers = child_processes()
    for line in leftovers:
        try:
            os.kill(int(line.split()[0]), signal.SIGKILL)
        except (ValueError, ProcessLookupError, PermissionError):
            pass
    if leftovers:
        time.sleep(2)

    item = call(f'/Items?Ids={movie}&Fields=MediaStreams,Path')
    streams = []
    path = ''
    if isinstance(item, dict):
        for entry in (item.get('Items') or []):
            path = entry.get('Path') or ''
            streams = [s for s in (entry.get('MediaStreams') or []) if s.get('Type') == 'Subtitle']
    if not streams:
        report('B14: the rig has an item with subtitle tracks to run', False, f'item {movie}')
        return finish()

    # Queue several tasks so the plugin is genuinely busy, then wait until a child process of the run exists: the
    # moment that matters for this probe is the one where something is alive to be left behind.
    tasks = [{'itemId': movie, 'subtitleIndex': s.get('Index')} for s in streams[:3]]
    call('/SubSync/Batch', 'POST', {'label': 'b14-teardown', 'tasks': tasks})
    alive = []
    deadline = time.time() + 180
    while time.time() < deadline:
        alive = child_processes()
        if alive:
            break
        time.sleep(1)

    started = bool(alive)
    if not started:
        fresh = log_text()[len(before):]
        started = bool(re.search(r'(queued:|extract lane:|alignment:|method=)', fresh))

    report('B14: the run had work in flight when the server was stopped',
           started,
           (alive[0] if alive else 'no child process seen; the log shows the run started'))

    pid = server_pid()
    if pid is None:
        report('B14: the server process was found to stop', False, 'no jellyfin process')
        return finish()

    os.kill(pid, signal.SIGTERM)
    gone = False
    for _ in range(30):
        time.sleep(1)
        if server_pid() is None:
            gone = True
            break

    report('B14: the server stopped on the signal', gone, f'pid {pid}')

    after = log_text()[len(before):]
    teardown = [line for line in after.splitlines() if 'teardown:' in line]
    numbers = re.search(
        r'tracked=(\d+) stopped=(\d+) killedDirect=(\d+) exitedDirect=(\d+) aliveAfter=(\d+) '
        r'tasksDone=(\d+)/(\d+) trackedLeft=(\d+)',
        teardown[-1] if teardown else '')
    report('B14: the plugin log records what teardown stopped, with numbers',
           bool(teardown) and numbers is not None,
           teardown[-1] if teardown else 'no teardown line was written')

    leftover = child_processes()
    report('B14: nothing of this rig is left running after the server stopped',
           not leftover,
           '; '.join(leftover) if leftover else 'no ffmpeg/ffsubsync process holds a file from this rig')

    if numbers:
        report('B14: no process this service started is alive after teardown',
               int(numbers.group(5)) == 0,
               f'aliveAfter={numbers.group(5)}')
        if alive:
            # What matters is the property, not which call did the killing: a child stopped by its own runner's
            # cancellation callback is stopped all the same, and the counts say how each one ended.
            report('B14: a run that had child processes alive was tracked, and they are gone',
                   int(numbers.group(1)) >= 1 and int(numbers.group(2)) >= 1,
                   f'{len(alive)} alive before the signal; tracked={numbers.group(1)} '
                   f'stopped={numbers.group(2)} (killedDirect={numbers.group(3)})')

    return finish()


def finish():
    print('ALL PASS' if failures == 0 else f'{failures} FAILURE(S)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
