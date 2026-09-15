#!/usr/bin/env python3
"""D-series integration probe: one job per track (D9), one error shape (D10), where the page lives (D18).

The unit checks in tests/run_checks.py pin the rules (the duplicate predicate, the filter's body, the page's poll
rates). This drives the same things through the real API of a real Jellyfin with the plugin installed, because
"the queue refuses a second job for the same track" is a claim about a running server, not about a predicate:

  * D10 - every refusal answers as `{ status, title, detail }`. Two different refusals are asked for here (an empty
    batch and a batch naming an item this account cannot see) to show the shape does not depend on which check
    refused the request; the *thrown*-failure half of D10 is a unit check, which calls the filter directly.
  * D9  - queueing the same item and track twice produces one job, the second response says how many tasks were
    already queued, and the plugin's own log names the duplicate.
  * D18 - the two routes the web client uses for a plugin page both answer: the page itself, and the dashboard page
    that leads to it.

Needs a rig Jellyfin on 127.0.0.1:8096 with an admin token in tests/gui/ids.json (the rig writes both):

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim --keep-rig
    python3 tests/backend/d_series_probe.py

Anything it queues is cancelled at the end.
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


def token():
    return json.loads(IDS.read_text(encoding='utf-8'))['admin_token']


def call(path, method='GET', body=None, raw=False):
    """Returns (status, parsed body or text). A failure status is a result here, not an exception."""
    data = json.dumps(body).encode('utf-8') if body is not None else None
    request = urllib.request.Request(
        BASE + path, data=data, method=method,
        headers={'Authorization': f'MediaBrowser Token="{token()}"', 'Content-Type': 'application/json'})
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


def field(view, name):
    """Reads a field from a batch view. The server serialises PascalCase; the page accepts either, and so does this."""
    if not isinstance(view, dict):
        return None
    for key in (name, name[:1].lower() + name[1:]):
        if key in view:
            return view[key]
    return None


def wait_idle(seconds=30):
    """Waits until the rig is not running anything, so a later 'first request' really is the first."""
    deadline = __import__('time').time() + seconds
    while __import__('time').time() < deadline:
        _, batches = call('/SubSync/Batches')
        busy = [b for b in (batches or []) if field(b, 'Status') in ('Running', 'Queued')]
        if not busy:
            return True
        __import__('time').sleep(1)
    return False


def log_lines(count=400):
    try:
        return LOG.read_text(encoding='utf-8', errors='replace').splitlines()[-count:]
    except OSError:
        return []


def main():
    ids = json.loads(IDS.read_text(encoding='utf-8'))
    movie = ids['movie']

    # A rig left with a queued job from an earlier probe would make the first request a duplicate of that job, so
    # the queue is emptied first and the probe waits for it to happen.
    call('/SubSync/Kill', 'POST', {})
    wait_idle()

    # ---- D10: every refusal is the same body ------------------------------------------------------------------
    status, body = call('/SubSync/Batch', 'POST', {'label': 'd-probe', 'tasks': []})
    report('D10: an empty batch is refused with status, title and detail',
           status == 400 and isinstance(body, dict)
           and body.get('status') == 400 and bool(body.get('title')) and bool(body.get('detail')),
           f'HTTP {status} {str(body)[:160]}')

    phantom = 'ffffffff-ffff-ffff-ffff-ffffffffffff'
    status2, body2 = call('/SubSync/Batch', 'POST',
                          {'label': 'd-probe', 'tasks': [{'itemId': phantom, 'subtitleIndex': 3}]})
    report('D10: a refusal by a different check has the same body, so the page needs one reader',
           status2 in (403, 404) and isinstance(body2, dict)
           and body2.get('status') == status2 and bool(body2.get('title')) and bool(body2.get('detail')),
           f'HTTP {status2} {str(body2)[:160]}')
    report('D10: neither refusal is a bare string (the page reads a field, not a sentence)',
           isinstance(body, dict) and isinstance(body2, dict)
           and set(['status', 'title', 'detail']).issubset(set(body.keys()))
           and set(['status', 'title', 'detail']).issubset(set(body2.keys())),
           f'keys={sorted(body.keys()) if isinstance(body, dict) else type(body).__name__}')

    # ---- D9: the same item and track is one job ---------------------------------------------------------------
    status3, item = call(f'/Items?Ids={movie}&Fields=MediaStreams')
    streams = []
    if isinstance(item, dict):
        for entry in (item.get('Items') or []):
            streams = [s for s in (entry.get('MediaStreams') or []) if s.get('Type') == 'Subtitle']
    if not streams:
        report('D9: a subtitle track to queue was found in the rig library', False,
               f'no subtitle streams on item {movie}')
        return finish()
    index = streams[0].get('Index')

    first_status, first = call('/SubSync/Batch', 'POST',
                               {'label': 'd-probe', 'tasks': [{'itemId': movie, 'subtitleIndex': index}]})
    report('D9: the first request queues the task',
           first_status == 200 and isinstance(first, dict) and field(first, 'Total') == 1
           and (field(first, 'AlreadyQueuedCount') or 0) == 0,
           f'HTTP {first_status} total={field(first, "Total")} alreadyQueued={field(first, "AlreadyQueuedCount")}')

    before = len(log_lines(2000))
    second_status, second = call('/SubSync/Batch', 'POST',
                                 {'label': 'd-probe', 'tasks': [{'itemId': movie, 'subtitleIndex': index}]})
    report('D9: queueing the same item and track again queues nothing new and says so',
           second_status == 200 and isinstance(second, dict)
           and field(second, 'Total') == 0 and (field(second, 'AlreadyQueuedCount') or 0) == 1,
           f'HTTP {second_status} total={field(second, "Total")} alreadyQueued={field(second, "AlreadyQueuedCount")}')
    report('D9: the two requests are different batches, and the second one holds no job of its own',
           isinstance(first, dict) and isinstance(second, dict) and field(first, 'Id') != field(second, 'Id'))

    fresh = log_lines(2000)[before:]
    report('D9: the plugin log names the duplicate it refused to queue',
           any('queue duplicate: item=' in line and movie in line for line in fresh),
           next((line for line in reversed(fresh) if 'queue duplicate' in line), 'no duplicate line written'))

    jobs = call('/SubSync/Batch/' + str(field(first, 'Id')))[1] if isinstance(first, dict) else None
    report('D9: the first batch still owns exactly one task (the duplicate did not join it)',
           isinstance(jobs, dict) and field(jobs, 'Total') == 1,
           f'total={field(jobs, "Total")}')

    # leave the rig idle
    first_id = field(first, 'Id')
    if first_id:
        call(f'/SubSync/Batch/{first_id}/Cancel', 'POST', {})

    # ---- D18: both routes the client uses answer --------------------------------------------------------------
    page_status, page = call('/web/configurationpage?name=subsync-main', raw=True)
    page = page or ''
    report('D18: the plugin page is served at the route the client opens',
           page_status == 200 and 'subsyncMainPage' in page,
           f'HTTP {page_status}, {len(page)} bytes, marker={"subsyncMainPage" in page}')

    dash_status, dash = call('/web/configurationpage?name=SubSync', raw=True)
    dash = dash or ''
    report('D18: the dashboard page leads to that page',
           dash_status == 200 and 'configurationpage?name=subsync-main' in dash,
           f'HTTP {dash_status}, link={"configurationpage?name=subsync-main" in dash}')

    return finish()


def finish():
    print('ALL PASS' if failures == 0 else f'{failures} FAILURE(S)')
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
