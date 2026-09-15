#!/usr/bin/env python3
"""P4 integration probe: does the running server keep the settings the page hands it, and say what it changed?

The unit checks in tests/run_checks.py prove what `SettingsValidation` does to a configuration object; this
drives the same three settings through the real API of a real Jellyfin with the plugin installed, the way the
page does, and reads the plugin's own log to see the scheduler pick the change up (F11, F12, F13):

  * F11 - a chosen encoding is stored exactly and comes back on a fresh read; one the engine cannot be given
    is *replaced and reported* instead of being stored as typed (the free-text field used to allow exactly
    that, and the page then said "Saved.").
  * F12 - a golden-section tick with framerate correction off is reported as doing nothing.
  * F13 - saving a configuration applies it to the running scheduler: the log carries
    `settings applied: workers=N lanes=M` with M recomputed from N (half of it, capped at the documented 3).

Needs a rig Jellyfin on 127.0.0.1:8096 with an admin token in tests/gui/ids.json (the rig writes both):

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim --keep-rig
    python3 tests/backend/p4_settings_probe.py

Everything it changes is restored at the end, and the original configuration is written back verbatim.
"""
import json
import pathlib
import sys
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


def call(path, method='GET', body=None):
    token = json.loads(IDS.read_text(encoding='utf-8'))['admin_token']
    data = json.dumps(body).encode('utf-8') if body is not None else None
    request = urllib.request.Request(
        BASE + path, data=data, method=method,
        headers={'Authorization': f'MediaBrowser Token="{token}"', 'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=30) as response:
        raw = response.read().decode('utf-8-sig')
    return json.loads(raw) if raw.strip() else None


def log_tail(lines=400):
    try:
        return LOG.read_text(encoding='utf-8', errors='replace').splitlines()[-lines:]
    except OSError:
        return []


def main():
    if not IDS.exists():
        print('SKIP  no rig token at tests/gui/ids.json (start the rig with --keep-rig)')
        return 0

    try:
        original = call('/SubSync/Configuration')
    except (urllib.error.URLError, OSError) as exc:
        print(f'SKIP  no rig Jellyfin on {BASE} ({exc})')
        return 0

    # ---- F11: a chosen encoding is stored as chosen and stays that way -------------------------------
    chosen = dict(original, OutputEncoding='latin-1')
    stored = call('/SubSync/Configuration', 'POST', chosen)
    report('F11: the API stores the encoding it was given',
           stored.get('OutputEncoding') == 'latin-1', f"stored={stored.get('OutputEncoding')}")
    report('F11: a fresh read returns the chosen encoding (retention across the page\'s reload)',
           call('/SubSync/Configuration').get('OutputEncoding') == 'latin-1')
    report('F11: nothing is reported when the value is one the engine accepts',
           not any('encoding' in note.lower() for note in (call('/SubSync/Settings/ValidationNotes') or [])),
           str(call('/SubSync/Settings/ValidationNotes')))

    # ---- F11: what the free-text field used to allow -------------------------------------------------
    typo = dict(original, OutputEncoding='utf8')
    stored = call('/SubSync/Configuration', 'POST', typo)
    notes = call('/SubSync/Settings/ValidationNotes') or []
    report('F11: an encoding the engine cannot be given is replaced with utf-8, not stored as typed',
           stored.get('OutputEncoding') == 'utf-8', f"stored={stored.get('OutputEncoding')}")
    report('F11: the substitution is reported to the page, which is what "Saved." used to hide',
           any(note.startswith('Output encoding') for note in notes), ' | '.join(notes))

    # ---- F12: a tick that does nothing says so -------------------------------------------------------
    inert = dict(original, OutputEncoding='latin-1', FixFramerate=False, UseGoldenSectionSearch=True)
    call('/SubSync/Configuration', 'POST', inert)
    notes = call('/SubSync/Settings/ValidationNotes') or []
    report('F12: a golden-section tick with framerate correction off is reported as doing nothing',
           any(note.startswith('Golden-section') for note in notes), ' | '.join(notes))
    report('F12: the tick itself is kept, so turning correction back on restores the setting',
           call('/SubSync/Configuration').get('UseGoldenSectionSearch') is True)

    usable = dict(original, OutputEncoding='latin-1', FixFramerate=True, UseGoldenSectionSearch=True)
    call('/SubSync/Configuration', 'POST', usable)
    report('F12: nothing is reported while the pair is usable',
           not any(note.startswith('Golden-section') for note in (call('/SubSync/Settings/ValidationNotes') or [])))

    # ---- F13: saving applies the settings to the running scheduler -----------------------------------
    before = len(log_tail())
    bumped = dict(original, OutputEncoding='latin-1', ParallelWorkers=64)
    call('/SubSync/Configuration', 'POST', bumped)
    lines = log_tail()
    applied = [line for line in lines if 'settings applied: workers=64' in line]
    report('F13: the save is applied to the running scheduler at once, with the lane width recomputed',
           bool(applied) and 'lanes=3' in applied[-1],
           applied[-1].strip() if applied else f'no "settings applied:" line in the last {len(lines)} log lines '
                                              f'(tail grew by {len(lines) - before})')
    report('F13: the worker count the page set is what the server reports back',
           call('/SubSync/Configuration').get('ParallelWorkers') == 64)

    # ---- put everything back ------------------------------------------------------------------------
    call('/SubSync/Configuration', 'POST', original)
    restored = call('/SubSync/Configuration')
    report('P4: the probe restored the configuration it found',
           restored.get('OutputEncoding') == original.get('OutputEncoding')
           and restored.get('ParallelWorkers') == original.get('ParallelWorkers')
           and restored.get('FixFramerate') == original.get('FixFramerate')
           and restored.get('UseGoldenSectionSearch') == original.get('UseGoldenSectionSearch'),
           f"workers={restored.get('ParallelWorkers')} encoding={restored.get('OutputEncoding')}")

    print(f'\n{len(applied)} "settings applied:" line(s) seen; log: {LOG}')
    print('FAILURE(S): ' + str(failures) if failures else 'ALL PASS')
    return 1 if failures else 0


if __name__ == '__main__':
    raise SystemExit(main())
