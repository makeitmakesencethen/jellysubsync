"""G2 reproduction, backend half: the page's own bulk path with a bitmap track.

The fixture `Bitmap` (9977d98d54e48c801cc91279dd1d4b8a) carries exactly one subtitle track: index 2,
"English - Default - DVDSUB" - an image-based track. The listing flags it (`UnsupportedReason`). This
script posts the same request the plugin page posts when the user picks that track (or asks for all of a
file's tracks), then reads the batch back and reports what the run says about the task.

Usage: python3 tests/backend/g2_bitmap_probe.py [--expect-refusal|--expect-none]
"""
import json
import pathlib
import sys
import urllib.request

BASE = 'http://127.0.0.1:8096'
IDS = json.loads((pathlib.Path(__file__).parent.parent / 'gui' / 'ids.json').read_text())
TOKEN = IDS['admin_token']
BITMAP_ITEM = '9977d98d54e48c801cc91279dd1d4b8a'
BITMAP_TRACK = 2
ABSENT = 'http://127.0.0.1:8096'  # placeholder, replaced below


def call(path, body=None, method='GET'):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    req.add_header('Authorization', 'MediaBrowser Token="%s"' % TOKEN)
    req.add_header('X-Emby-Token', TOKEN)
    if data:
        req.add_header('Content-Type', 'application/json')
    with urllib.request.urlopen(req, timeout=120) as r:
        raw = r.read().decode()
    return json.loads(raw) if raw.strip() else {}


def listing():
    res = call('/SubSync/Subtitles/Batch', {'itemIds': [BITMAP_ITEM], 'expandSeries': False}, 'POST')
    items = res.get('items') or res.get('Items') or []
    return (items[0].get('Tracks') or []) if items else []


def main():
    expect = sys.argv[1] if len(sys.argv) > 1 else '--expect-refusal'
    tracks = listing()
    print('listing: %d track(s)' % len(tracks))
    for t in tracks:
        print('   index=%s codec=%s title=%r unsupported=%s'
              % (t.get('Index'), t.get('Codec'),
                 t.get('Title'), 'yes' if (t.get('UnsupportedReason') or t.get('unsupportedReason')) else 'no'))

    # Exactly what the page sends for "sync this file's subtitles": one task per track it considers syncable.
    tasks = [{'itemId': BITMAP_ITEM, 'subtitleIndex': t['Index'],
              'title': 'Bitmap \u2014 ' + (t.get('Title') or ('track %s' % t['Index']))}
             for t in tracks]
    print('tasks the page would post: %d -> %s' % (len(tasks), [t['subtitleIndex'] for t in tasks]))

    view = call('/SubSync/Batch', {'label': 'G2 bitmap probe', 'tasks': tasks}, 'POST')
    batch_id = view.get('Id') or view.get('id')
    tasks_out = view.get('Tasks') or view.get('tasks') or []
    print('batch %s: total=%s ok=%s failed=%s' % (batch_id, view.get('Total'), view.get('Ok'), view.get('Failed')))
    for t in tasks_out:
        status = t.get('Status') or t.get('status')
        err = (t.get('Error') or t.get('error') or '')
        print('   task %s -> %s  %s' % (t.get('BatchIndex'), status, err[:110]))

    record = {
        'listing': [
            {'index': t.get('Index'), 'codec': t.get('Codec'), 'title': t.get('Title'),
             'unsupportedReason': t.get('UnsupportedReason') or t.get('unsupportedReason')}
            for t in tracks
        ],
        'tasksPosted': [t['subtitleIndex'] for t in tasks],
        'batchId': batch_id,
        'total': view.get('Total') or view.get('total'),
        'ok': view.get('Ok') or view.get('ok'),
        'failed': view.get('Failed') or view.get('failed'),
        'taskResults': [
            {'index': t.get('BatchIndex'), 'status': t.get('Status') or t.get('status'),
             'error': t.get('Error') or t.get('error')}
            for t in tasks_out
        ],
    }
    out = pathlib.Path(__file__).parent / ('g2-bitmap-%s.json' % expect.strip('-').replace('expect-', ''))
    out.write_text(json.dumps(record, indent=1))
    print('wrote', out)

    if expect == '--expect-refusal':
        bad = [t for t in record['taskResults'] if t['status'] == 'Failed']
        ok = len(record['tasksPosted']) > 0 and len(bad) > 0 and 'image subtitle format' in (bad[0]['error'] or '')
        print('REPRODUCED' if ok else 'NOT REPRODUCED',
              '- %d task(s) posted for a bitmap track, %d failed as an image format' % (len(record['tasksPosted']), len(bad)))
        return 0 if ok else 1

    ok = record['tasksPosted'] == [] or record['failed'] == 0
    print('NO BITMAP TASK' if ok else 'STILL QUEUING A BITMAP TRACK',
          '- posted %s, failed %s' % (record['tasksPosted'], record['failed']))
    return 0 if ok else 1


if __name__ == '__main__':
    raise SystemExit(main())
