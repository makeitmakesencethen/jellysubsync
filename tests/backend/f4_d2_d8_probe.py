#!/usr/bin/env python3
"""Access and truth probe: who may see which run (F4), which engine counts as installed (D2), what can be synced (D8).

The harness checks drive the rules directly. This drives the endpoints, because the question F4 asks is about the API a
second account can reach, not about a predicate:

  * F4 - a second, non-administrator account signs in. It must not see the administrator's jobs or batches (the list
    must not contain them, and asking for one by id must answer like a job that does not exist), and a run of its own
    must come back without the server's output path - while the administrator reads the same run and does see it.
  * D2 - pointing the configured engine path at something that is not there must make the status report the engine as
    missing, name the path it looked at, and keep the resolved path in the answer. Restoring the setting restores the
    status.
  * D8 - the same series id is offered to the single-sync endpoint and to the batch endpoint; both must refuse it the
    same way, with the same title and detail.

Needs a rig on 127.0.0.1:8096 with an admin token in tests/gui/ids.json:

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim --keep-rig
    python3 tests/backend/f4_d2_d8_probe.py

The account it creates is deleted again, and the configuration it changes is written back.
"""
import json
import pathlib
import sys
import time
import urllib.error
import urllib.request

REPO = pathlib.Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:8096'
IDS = REPO / 'tests' / 'gui' / 'ids.json'

failures = 0


def report(name, ok, detail=''):
    global failures
    if not ok:
        failures += 1
    print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail else ''))


CLIENT = 'MediaBrowser Client="SubSync access probe", Device="probe", DeviceId="subsync-access-probe", Version="1.0.0"'


def call(path, method='GET', body=None, token=None, raw=False):
    """Returns (status, parsed body). A refusal is a result here, not an exception."""
    data = json.dumps(body).encode('utf-8') if body is not None else None
    headers = {'Content-Type': 'application/json', 'Authorization': CLIENT}
    if token:
        headers['Authorization'] = CLIENT + f', Token="{token}"'
    request = urllib.request.Request(BASE + path, data=data, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=60) as response:
            payload = response.read().decode('utf-8-sig')
            status = response.status
    except urllib.error.HTTPError as error:
        payload = error.read().decode('utf-8-sig')
        status = error.code
    if raw:
        return status, payload
    try:
        return status, (json.loads(payload) if payload.strip() else None)
    except ValueError:
        return status, payload


def as_dict(value):
    """The parsed body when it is an object, and an empty one otherwise (a body is not always JSON)."""
    return value if isinstance(value, dict) else {}


def as_list(value):
    """The parsed body when it is an array, and an empty one otherwise."""
    return value if isinstance(value, list) else []


def main():
    ids = json.loads(IDS.read_text(encoding='utf-8'))
    admin = ids['admin_token']
    movie = ids['movie']
    series = ids['series']

    # ---- F4: a second account, with no administrator permission ------------------------------------------------
    name = 'access-probe-' + str(int(time.time()))[-6:]
    status, created = call('/Users/New', 'POST', {'Name': name}, token=admin)
    if status != 200 or not isinstance(created, dict):
        report('F4: a second account could be created on the rig', False, f'HTTP {status} {created}')
        return finish()
    other_id = created['Id']
    call(f'/Users/{other_id}/Policy', 'POST',
         {'IsAdministrator': False, 'EnableAllFolders': True, 'EnableRemoteAccess': True}, token=admin)
    status, session = call('/Users/AuthenticateByName', 'POST',
                           {'Username': name, 'Pw': ''})
    other = as_dict(session).get('AccessToken')
    report('F4: the second account signed in without administrator rights',
           status == 200 and bool(other), f'HTTP {status}')

    # The rig is fast enough that a run finishes in about a second, so the queue is held down to one worker and the
    # administrator queues several tasks: that keeps work queued long enough to test what another account is told about
    # it, and copy mode is on so a finished run has an output path to compare between the two accounts.
    status, probe_config = call('/SubSync/Configuration', token=admin)
    probe_config = as_dict(probe_config)
    call('/SubSync/Configuration', 'POST', dict(probe_config, SyncModeCopy=True, ParallelWorkers=1), token=admin)

    status, admin_job = call('/SubSync/Sync', 'POST', {'itemId': movie, 'subtitleIndex': 0}, token=admin)
    admin_job = as_dict(admin_job)
    admin_id = admin_job.get('Id')
    report('F4: the administrator can queue a run', status == 200 and bool(admin_id), f'HTTP {status} body={admin_job}')

    # A second task behind the first one, so the queue still holds work for the administrator when the checks below ask
    # the other account about it (one worker runs them one at a time).
    status, queued = call('/SubSync/Batch', 'POST', {
        'label': 'access-probe',
        'tasks': [
            {'itemId': movie, 'subtitleIndex': 1},
            {'itemId': ids['ep2'], 'subtitleIndex': 0},
        ],
    }, token=admin)
    queued = as_dict(queued)
    admin_batch = queued.get('Id')
    report('F4: the administrator can queue a batch', status == 200 and bool(admin_batch), f'HTTP {status}')

    # what the administrator queues is not the second account's to see
    status, their_jobs = call('/SubSync/Jobs', token=other)
    listed = as_list(their_jobs)
    report('F4: the second account\'s run list does not contain the administrator\'s run',
           status == 200 and not any(j.get('Id') == admin_id for j in listed),
           f'HTTP {status}, {len(listed)} run(s): {[j.get("Id") for j in listed]}')

    status, body = call(f'/SubSync/Jobs/{admin_id}', token=other)
    report('F4: asking for the administrator\'s run by id answers like a run that does not exist',
           status == 404 and as_dict(body).get('status') == 404,
           f'HTTP {status} {str(body)[:120]}')

    status, their_batches = call('/SubSync/Batches', token=other)
    report('F4: the second account\'s batch list does not contain the administrator\'s run',
           status == 200 and not any(b.get('Id') == admin_batch for b in as_list(their_batches)),
           f'HTTP {status}, {len(as_list(their_batches))} batch(es)')

    status, batch_body = call(f'/SubSync/Batch/{admin_batch}', token=other)
    report('F4: asking for the administrator\'s batch by id answers like one that does not exist',
           status == 404, f'HTTP {status} {str(batch_body)[:120]}')

    # asking for the very work the administrator has queued is a conflict, not a hand-over
    status, conflict = call('/SubSync/Sync', 'POST', {'itemId': movie, 'subtitleIndex': 1}, token=other)
    conflict = as_dict(conflict)
    report('F4: another account asking for queued work is answered as a conflict, not handed the run',
           status == 409 and 'already queued by another account' in (conflict.get('detail') or '')
           and not conflict.get('OutputPath') and not conflict.get('OwnerId'),
           f'HTTP {status} {str(conflict)[:160]}')

    # wait for the administrator's own run to settle, so the second account can ask for the same track without meeting
    # the queue's duplicate rule (a finished run is not a duplicate)
    deadline = time.time() + 180
    while time.time() < deadline:
        _, settled = call(f'/SubSync/Jobs/{admin_id}', token=admin)
        _, batch_now = call(f'/SubSync/Batch/{admin_batch}', token=admin)
        batch_done = as_dict(batch_now).get('Status') in ('Completed', 'Failed', 'Cancelled')
        if as_dict(settled).get('Status') in ('Completed', 'Failed', 'Cancelled') and batch_done:
            break
        time.sleep(3)

    status, own_job = call('/SubSync/Sync', 'POST', {'itemId': movie, 'subtitleIndex': 0}, token=other)
    own_id = as_dict(own_job).get('Id')
    report('F4: the second account can queue its own run', status == 200 and bool(own_id), f'HTTP {status} body={own_job}')

    done = False
    deadline = time.time() + 240
    while own_id and time.time() < deadline:
        _, mine_now = call(f'/SubSync/Jobs/{own_id}', token=other)
        if as_dict(mine_now).get('Status') in ('Completed', 'Failed', 'Cancelled'):
            done = True
            break
        time.sleep(4)

    _, mine = call(f'/SubSync/Jobs/{own_id}', token=other) if own_id else (0, {})
    _, theirs = call(f'/SubSync/Jobs/{own_id}', token=admin) if own_id else (0, {})
    mine = as_dict(mine)
    theirs = as_dict(theirs)
    report('F4: the second account sees its own run',
           done and mine.get('Status') == 'Completed', f'status={mine.get("Status")}')
    report('F4: the second account\'s own run carries no server path',
           mine and not mine.get('OutputPath'), f'OutputPath={mine.get("OutputPath")!r}')
    report('F4: the administrator reading the same run does see where it was written',
           bool(theirs.get('OutputPath')),
           f'OutputPath={theirs.get("OutputPath")!r} status={theirs.get("Status")} error={theirs.get("Error")!r}')
    report('F4: and the two accounts were answered about the same run',
           mine.get('Id') == theirs.get('Id') == own_id, f'{mine.get("Id")} vs {theirs.get("Id")}')
    report('F4: the second account cannot read the administrator\'s other run either',
           call(f'/SubSync/Jobs/{admin_id}', token=other)[0] == 404)

    # ---- D2: the engine the status describes is the one that will run -----------------------------------------
    status, original = call('/SubSync/Configuration', token=admin)
    original = as_dict(original)
    before = as_dict(call('/SubSync/InstallationStatus', token=admin)[1])
    report('D2: the status names the engine that will run',
           bool(before.get('ResolvedBinaryPath')),
           f'resolved={before.get("ResolvedBinaryPath")!r} installed={before.get("IsInstalled")}')

    # A path the plugin accepts, which then stops existing: the engine was moved, uninstalled or its mount is gone.
    # This is the case the old status could not see, because it asked about the plugin's own managed binary while the
    # configured path is what a job runs.
    engine = pathlib.Path('/tmp/ffsubsync-probe-engine')
    engine.write_text('#!/bin/sh\nexit 0\n', encoding='utf-8')
    engine.chmod(0o755)
    save_status, saved = call('/SubSync/Configuration', 'POST', dict(original, FfSubSyncPath=str(engine)), token=admin)
    saved = as_dict(saved)
    stored_now = as_dict(call('/SubSync/Configuration', token=admin)[1]).get('FfSubSyncPath')
    resolved_now = as_dict(call('/SubSync/InstallationStatus', token=admin)[1]).get('ResolvedBinaryPath')
    report('D2: a configured engine path that exists is stored and is the engine the status resolves',
           stored_now == str(engine) and resolved_now == str(engine),
           f'HTTP {save_status} stored={stored_now!r} resolved={resolved_now!r}')

    engine.unlink()
    broken = as_dict(call('/SubSync/InstallationStatus', token=admin)[1])
    report('D2: the engine that was there and is not any more is reported as not installed',
           broken.get('IsInstalled') is False,
           f'installed={broken.get("IsInstalled")} resolved={broken.get("ResolvedBinaryPath")!r}')
    report('D2: the answer names the path it looked at',
           broken.get('ResolvedBinaryPath') == str(engine),
           f'resolved={broken.get("ResolvedBinaryPath")!r}')
    report('D2: and it says so in words, so the page can explain it',
           str(engine) in (broken.get('EngineNote') or ''),
           f'note={broken.get("EngineNote")!r}')

    call('/SubSync/Configuration', 'POST', dict(original), token=admin)
    restored = as_dict(call('/SubSync/InstallationStatus', token=admin)[1])
    report('D2: restoring the setting restores the status',
           restored.get('IsInstalled') is True,
           f'installed={restored.get("IsInstalled")} resolved={restored.get("ResolvedBinaryPath")!r}')

    # ---- D8: one refusal shape for an item that cannot be synced ----------------------------------------------
    status_sync, single = call('/SubSync/Sync', 'POST', {'itemId': series, 'subtitleIndex': 0}, token=admin)
    single = as_dict(single)
    status_batch, batch = call('/SubSync/Batch', 'POST',
                               {'label': 'access-probe', 'tasks': [{'itemId': series, 'subtitleIndex': 0}]}, token=admin)
    batch = as_dict(batch)
    report('D8: single sync refuses a series',
           status_sync == 400 and single.get('title') == 'Not a video',
           f'HTTP {status_sync} {str(single)[:160]}')
    report('D8: the batch endpoint refuses the very same request the same way',
           status_batch == status_sync
           and batch.get('title') == single.get('title') and batch.get('detail') == single.get('detail'),
           f'HTTP {status_batch} {str(batch)[:160]}')
    report('D8: the refusal names the item and says what it is',
           'is a series, not a video' in (batch.get('detail') or ''),
           f'detail={batch.get("detail")!r}')

    # leave nothing behind
    call('/SubSync/Configuration', 'POST', dict(probe_config), token=admin)
    call(f'/Users/{other_id}', 'DELETE', token=admin)
    return finish()


def finish():
    print('ALL PASS' if failures == 0 else f'{failures} FAILURE(S)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
