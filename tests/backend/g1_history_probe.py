#!/usr/bin/env python3
"""G1, measured end to end: does a sync started the way a detail page starts one reach History?

The claim under test is the one the user reported - a single episode/movie synced from its detail page
never shows up in History. The detail page's dialog posts to `/SubSync/Sync` (`Web/subsync.js:785`), so
this probe does exactly that against the rig, waits for the job to reach a terminal state, and then asks
the same two questions the History tab asks:

  * `GET /SubSync/Batches` - the list the tab is drawn from;
  * `GET /SubSync/Batch/<id>` - the run's own view, opened by clicking the row;
  * and what the plugin wrote to `batch-history.json`, which is what a restart reads back.

It also queues a one-task *batch* in the same run, so "the single sync appears" cannot be satisfied by a
change that merely lists everything: the batch has to keep behaving as it did.

Usage: python3 tests/backend/g1_history_probe.py [--keep]
"""
import json
import pathlib
import sys
import time
import urllib.error
import urllib.request

BASE = 'http://127.0.0.1:8096'
REPO = pathlib.Path(__file__).resolve().parents[2]
IDS = json.loads((REPO / 'tests' / 'gui' / 'ids.json').read_text())
CLIENT = 'MediaBrowser Client="g1 probe", Device="probe", DeviceId="subsync-g1-probe", Version="1.0.0"'
HISTORY = pathlib.Path('/opt/data/jf12test/data/data/subsync/state/batch-history.json')

failures = 0


def report(name, ok, detail=''):
    global failures
    if not ok:
        failures += 1
    print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail else ''))


def call(path, method='GET', body=None, token=IDS['admin_token']):
    data = json.dumps(body).encode('utf-8') if body is not None else None
    headers = {'Content-Type': 'application/json', 'Authorization': CLIENT + f', Token="{token}"'}
    request = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = response.read().decode('utf-8-sig')
            status = response.status
    except urllib.error.HTTPError as error:
        payload = error.read().decode('utf-8-sig')
        status = error.code
    try:
        return status, json.loads(payload) if payload.strip() else None
    except ValueError:
        return status, payload


def main():
    movie = IDS['movie']
    status, tracks = call(f'/SubSync/Subtitles/{movie}')
    text_tracks = [t for t in (tracks or []) if isinstance(t.get('Index'), int) and not t.get('IsImageBased')]
    if not text_tracks:
        report('the movie has a text subtitle to sync', False, f'HTTP {status}')
        return 1
    # The detail page's own pick: an external subtitle that is not already the plugin's output. An embedded
    # track with no sibling text track is refused by the audio-only rule on this fixture ("unverified"),
    # which is the plugin working, and would make this probe report a failure that is not about G1.
    def rank(track):
        return (not track.get('IsExternal', False), track.get('HasSyncedVersion', False), track.get('Index'))

    track = sorted(text_tracks, key=rank)[0]
    print(f"movie track: index={track['Index']} lang={track.get('Language')} external={track.get('IsExternal')}")

    # 1. the detail page's own call
    status, job = call('/SubSync/Sync', 'POST', {'itemId': movie, 'subtitleIndex': track['Index']})
    report('POST /SubSync/Sync queues one job (the detail page\'s call)', status == 200 and bool(job and job.get('Id')),
           f'HTTP {status} id={(job or {}).get("Id")}')
    if status != 200:
        return 1
    job_id = job['Id']
    run_id = 'single:' + job_id
    print(f'job id {job_id} → run id {run_id}')

    # 2. the job runs to a terminal state (the tab is read after it finished)
    state = None
    for _ in range(120):
        _, view = call(f'/SubSync/Jobs/{job_id}')
        state = (view or {}).get('Status')
        if state in ('Completed', 'Failed', 'Cancelled'):
            break
        time.sleep(2)
    print(f'job status {state}  outcome={(view or {}).get("Outcome")}')
    report('the standalone job reached a terminal state (so its row has something to report)',
           state in ('Completed', 'Failed', 'Cancelled'), str(state))

    # 3. a one-task batch in the same run, as the control
    status, batch = call('/SubSync/Batch', 'POST',
                         {'label': 'G1 control batch', 'tasks': [{'itemId': movie, 'subtitleIndex': track['Index']}]})
    control_id = (batch or {}).get('Id') if isinstance(batch, dict) else None
    print(f'control batch {control_id} (HTTP {status})')

    # 4. what the History tab sees
    _, listed = call('/SubSync/Batches')
    listed = listed or []
    ids = [entry.get('Id') for entry in listed]
    single_row = next((e for e in listed if e.get('Id') == run_id), None)
    report('the single sync is listed by GET /SubSync/Batches', single_row is not None,
           f'{len(listed)} run(s): {ids[:6]}')
    if single_row:
        print('  single row: ' + json.dumps(single_row, sort_keys=True))
        report('its row carries the item as its label, and its own status and count',
               single_row.get('Label') == 'Helikopterrånet S01E01' and single_row.get('Total') == 1
               and single_row.get('Ok') + single_row.get('Failed') + single_row.get('Cancelled') == 1
               and single_row.get('Status') == state,
               f"label='{single_row.get('Label')}' total={single_row.get('Total')} ok={single_row.get('Ok')} "
               f"failed={single_row.get('Failed')} status={single_row.get('Status')}")

    # 5. the row a user clicks
    status, view = call(f'/SubSync/Batch/{run_id}')
    report('clicking the row opens the run (GET /SubSync/Batch/<run id>)',
           status == 200 and isinstance(view, dict) and len(view.get('Tasks') or []) == 1,
           f'HTTP {status} tasks={len((view or {}).get("Tasks") or [])}')
    if isinstance(view, dict):
        print('  run view: ' + json.dumps({k: view.get(k) for k in ('Id', 'Label', 'Status', 'Total', 'Ok',
                                                                    'FrontendStatus')}, sort_keys=True))

    # the control still behaves as a batch, and is listed under its own id
    report('the one-task batch is listed separately and unchanged',
           control_id is not None and control_id in ids,
           f'control {control_id} listed={control_id in ids}')

    # 6. what a restart will read. The plugin persists at most once a minute (MaybePersistBatchHistory),
    # so the file is polled rather than read once.
    stored_ids = []
    for _ in range(45):
        if HISTORY.exists():
            stored_ids = [e.get('BatchId') for e in json.loads(HISTORY.read_text())]
            if run_id in stored_ids:
                break
        time.sleep(2)
    report('the history file holds the single sync (so a restart keeps it)',
           run_id in stored_ids,
           f'{len(stored_ids)} run(s) on disk, newest: {stored_ids[-3:]}'
           if stored_ids else 'no history file')

    print(f'\n{"FAIL" if failures else "ALL PASS"} — {failures} failure(s)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
