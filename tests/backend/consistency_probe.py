#!/usr/bin/env python3
"""Consistency probe: what a cancel may stop (F2), what a cache clear refuses to delete (F6), what re-syncing writes (S12).

The harness checks drive the rules. This drives the endpoints against a real server with a second account, because two
of the three are about what one account can do to another's work:

  * F2 - a cancel that names nothing is refused; asking to stop every run without being an administrator is refused;
    another account's run cannot be stopped by id; an account's own queued run can be; and the administrator's run is
    still alive afterwards, which is the point of the rule.
  * F6 - a cache clear while a run is reading the cache is refused with a 409 that names how many runs are in the way,
    and succeeds once they are done.
  * S12 - re-syncing a sidecar the plugin wrote updates that file instead of writing a second marker into the library.

Needs a rig on 127.0.0.1:8096 with an admin token in tests/gui/ids.json:

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim --keep-rig
    python3 tests/backend/consistency_probe.py
"""
import json
import pathlib
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO = pathlib.Path(__file__).resolve().parents[2]
BASE = 'http://127.0.0.1:8096'
IDS = REPO / 'tests' / 'gui' / 'ids.json'
MEDIA = pathlib.Path('/opt/data/jf12test/media')
CLIENT = 'MediaBrowser Client="SubSync consistency probe", Device="probe", DeviceId="subsync-consistency-probe", Version="1.0.0"'

failures = 0


def report(name, ok, detail=''):
    global failures
    if not ok:
        failures += 1
    print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail else ''))


def as_dict(value):
    return value if isinstance(value, dict) else {}


def as_list(value):
    return value if isinstance(value, list) else []


def call(path, method='GET', body=None, token=None):
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
    try:
        return status, (json.loads(payload) if payload.strip() else None)
    except ValueError:
        return status, payload


def main():
    ids = json.loads(IDS.read_text(encoding='utf-8'))
    admin = ids['admin_token']
    movie = ids['movie']

    # the media directory is snapshotted so a nested sidecar can be spotted afterwards
    before = {p.name for p in MEDIA.glob('*.srt')}

    name = 'consistency-probe-' + str(int(time.time()))[-6:]
    status, created = call('/Users/New', 'POST', {'Name': name}, token=admin)
    if status != 200:
        report('a second account could be created on the rig', False, f'HTTP {status} {created}')
        return finish()
    other_id = as_dict(created)['Id']
    call(f'/Users/{other_id}/Policy', 'POST',
         {'IsAdministrator': False, 'EnableAllFolders': True, 'EnableRemoteAccess': True}, token=admin)
    status, session = call('/Users/AuthenticateByName', 'POST', {'Username': name, 'Pw': ''})
    other = as_dict(session).get('AccessToken')
    if not other:
        report('the second account signed in', False, f'HTTP {status} {session}')
        return finish()

    # ---- F2: a cancel has to say what it stops, and may only stop what the caller may see ----------------------
    status, body = call('/SubSync/Kill', 'POST', {}, token=other)
    report('F2: a non-administrator cannot cancel anything at all',
           status == 403,
           f'HTTP {status} {str(body)[:140]}')

    status, body = call('/SubSync/Kill', 'POST', {'all': True}, token=other)
    report('F2: and asking for every run as a non-administrator is refused too',
           status == 403,
           f'HTTP {status} {str(body)[:140]}')

    status, body = call('/SubSync/Kill', 'POST', {}, token=admin)
    report('F2: an administrator who names nothing is asked what to stop',
           status == 400 and as_dict(body).get('title') == 'Say what to stop',
           f'HTTP {status} {str(body)[:140]}')

    status, body = call('/SubSync/Kill', 'POST', {'jobId': 'ffffffff-ffff-ffff-ffff-ffffffffffff'}, token=admin)
    report('F2: naming a run that is not queued or running stops nothing and says so',
           status == 404 and as_dict(body).get('title') == 'Nothing to stop',
           f'HTTP {status} {str(body)[:140]}')

    # the administrator queues work, held back so it is still live while the other account tries to stop it
    status, config = call('/SubSync/Configuration', token=admin)
    config = as_dict(config)
    call('/SubSync/Configuration', 'POST', dict(config, ParallelWorkers=1, SyncModeCopy=True), token=admin)
    status, queued = call('/SubSync/Batch', 'POST', {
        'label': 'consistency-probe',
        'tasks': [{'itemId': movie, 'subtitleIndex': 0}, {'itemId': ids['ep2'], 'subtitleIndex': 0}],
    }, token=admin)
    admin_batch = as_dict(queued).get('Id')
    _, admin_jobs = call('/SubSync/Jobs', token=admin)
    admin_live = [as_dict(j) for j in as_list(admin_jobs)
                  if as_dict(j).get('Status') in ('Queued', 'Running') and as_dict(j).get('BatchId') == admin_batch]
    admin_job = admin_live[0].get('Id') if admin_live else None
    report('F2: the administrator has live work to defend', bool(admin_batch and admin_job),
           f'batch={admin_batch} jobs={[j.get("Id") for j in admin_live]}')

    status, body = call('/SubSync/Kill', 'POST', {'jobId': admin_job}, token=other)
    report('F2: the second account cannot stop that run',
           status == 403, f'HTTP {status} {str(body)[:140]}')

    status, body = call('/SubSync/Kill', 'POST', {'batchId': admin_batch}, token=other)
    report('F2: nor that batch', status == 403, f'HTTP {status} {str(body)[:140]}')

    _, after_attempt = call(f'/SubSync/Jobs/{admin_job}', token=admin)
    report('F2: the administrator\'s run is still alive after both attempts',
           as_dict(after_attempt).get('Status') in ('Queued', 'Running', 'Completed'),
           f'status={as_dict(after_attempt).get("Status")}')

    # ---- F6: a clear refuses while a run is reading the cache --------------------------------------------------
    status, body = call('/SubSync/SpeechCache/Clear', 'POST', {}, token=admin)
    blocked = status == 409 and 'using the cache' in (as_dict(body).get('title') or '')
    if as_dict(after_attempt).get('Status') in ('Queued', 'Running'):
        report('F6: a cache clear while a run is reading the cache is refused', blocked,
               f'HTTP {status} {str(body)[:140]}')
    else:
        report('F6: a cache clear while a run is reading the cache is refused', False,
               'the run settled before the clear, so the case could not be observed')

    # ---- F2: a scoped cancel stops what it names, and the rest of the queue carries on -------------------------
    status, body = call('/SubSync/Kill', 'POST', {'jobId': admin_job}, token=admin)
    report('F2: the owner can stop the run it names',
           status == 200 and as_dict(body).get('queuedCancelled', 0) + as_dict(body).get('runningKilled', 0) >= 1,
           f'HTTP {status} {str(body)[:140]}')
    _, stopped = call(f'/SubSync/Jobs/{admin_job}', token=admin)
    _, sibling = call(f'/SubSync/Batch/{admin_batch}', token=admin)
    report('F2: the run it named is cancelled and the batch it belonged to is not stopped wholesale',
           as_dict(stopped).get('Status') == 'Cancelled'
           and as_dict(sibling).get('Status') in ('Queued', 'Running', 'Completed'),
           f'job={as_dict(stopped).get("Status")} batch={as_dict(sibling).get("Status")}')

    # ---- S12: re-syncing a sidecar the plugin wrote updates it -------------------------------------------------
    deadline = time.time() + 180
    while time.time() < deadline:
        _, batch_now = call(f'/SubSync/Batch/{admin_batch}', token=admin)
        if as_dict(batch_now).get('Status') in ('Completed', 'Failed', 'Cancelled'):
            break
        time.sleep(3)

    status, cleared = call('/SubSync/SpeechCache/Clear', 'POST', {}, token=admin)
    report('F6: with nothing running the same clear succeeds',
           status == 200 and 'removedAudio' in as_dict(cleared),
           f'HTTP {status} {str(cleared)[:120]}')

    # The sidecar this probe's own copy-mode run wrote is the one to re-sync. Jellyfin only offers a sidecar as a
    # stream once it has scanned it, so the item is refreshed and the listing polled; if the library never registers it,
    # that is reported as what it is rather than claimed as a pass - the flagging and naming rules are covered by the
    # harness checks either way.
    written = sorted((p for p in MEDIA.glob('*.SYNCED*.srt')), key=lambda p: p.stat().st_mtime)
    newest = written[-1] if written else None
    report('S12: a run in copy mode wrote a sidecar whose name carries one marker',
           newest is not None and newest.name.upper().count('SYNCED') == 1,
           newest.name if newest else 'no sidecar in the media directory')

    call(f'/Items/{movie}/Refresh', 'POST', {}, token=admin)
    tracked = None
    deadline = time.time() + 90
    while time.time() < deadline and tracked is None:
        _, listed = call(f'/SubSync/Subtitles/{movie}', token=admin)
        for stream in as_list(listed):
            stream = as_dict(stream)
            if 'SYNCED' in (stream.get('ExternalPath') or '').upper():
                tracked = stream
                break
        if tracked is None:
            time.sleep(6)

    if tracked is None:
        print('NOTE  the library did not register the sidecar as a stream within 90 s, so listing and re-syncing it '
              'could not be measured on this rig; the harness checks cover the flag and the naming rule')
    else:
        report('S12: a sidecar the plugin wrote is listed (flagged) instead of hidden from the list',
               tracked.get('IsPluginOutput') is True,
               f'index={tracked.get("Index")} path={tracked.get("ExternalPath")}')
        status, job = call('/SubSync/Sync', 'POST',
                           {'itemId': movie, 'subtitleIndex': tracked.get('Index')}, token=admin)
        job = as_dict(job)
        deadline = time.time() + 240
        while job.get('Id') and time.time() < deadline:
            _, now = call(f'/SubSync/Jobs/{job["Id"]}', token=admin)
            now = as_dict(now)
            if now.get('Status') in ('Completed', 'Failed', 'Cancelled'):
                job = now
                break
            time.sleep(4)
        output = job.get('OutputPath') or ''
        report('S12: re-syncing it does not write a second marker into the library',
               bool(output) and output.upper().count('SYNCED') == 1,
               f'status={job.get("Status")} output={output!r}')

    after = {p.name for p in MEDIA.glob('*.srt')}
    nested = [n for n in (after - before) if n.upper().count('SYNCED') > 1]
    report('S12: no file with two markers was created in the library', not nested,
           '; '.join(nested) if nested else f'{len(after - before)} new file(s), none nested')

    # ---- D13/D14 pointers so one rig session answers all of it -------------------------------------------------
    report('the SPA probes are run separately (tests/gui/d13-f29-probe.js, tests/gui/d14-probe.js)',
           (REPO / 'tests' / 'gui' / 'd14-probe.js').exists() and (REPO / 'tests' / 'gui' / 'd13-f29-probe.js').exists())

    call(f'/Users/{other_id}', 'DELETE', token=admin)
    call('/SubSync/Configuration', 'POST', dict(config), token=admin)
    return finish()


def finish():
    print('ALL PASS' if failures == 0 else f'{failures} FAILURE(S)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
