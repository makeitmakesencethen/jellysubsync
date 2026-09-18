#!/usr/bin/env python3
"""Queue a large file ahead of a probe, so the probe's own run sits *Queued* with tasks outstanding.

The priority-4 defect ("History shows queued jobs as red/Failed") is only visible while a run is
genuinely pending, and on this rig the fixtures are 120 s cuts: a three-file selection finished between
two polls of the History tab, which is why the first reproduction attempt measured nothing. What makes a
run pending is the server's own FIFO - work queued ahead of it - so this script queues one enormous file
into the slow-storage library and leaves it running, and the probe's small batch waits behind it.

The synthetic item is a sparse 2 GiB Matroska file plus a `.subsync-scratch.json` beside it, describing
the item and stream ids to pass to `/SubSync/Sync`. EnqueueSync does no filesystem access at all (it
validates from Jellyfin's cached metadata), so an id that exists only in that file is enough for the
server to accept the job and put it in the queue; the job's own extraction then fails on the missing
stream, which does not matter - the file was queued to make the *next* run wait, and it is removed as
soon as it has done that.

    python3 tests/backend/priority_scratch.py start     # build and queue the obstruction
    python3 tests/backend/priority_scratch.py list      # what is queued/running right now
    python3 tests/backend/priority_scratch.py stop      # kill everything and remove the scratch files

The directory is hardlinked into /opt/data/jf12test/media-slow/, which the test server only reads under
the slow-storage shim (start-server.sh with SLOW=1). Without the shim the file is read at full speed and
the obstruction is short-lived; the probe tolerates either.
"""
import json
import os
import pathlib
import subprocess
import sys
import time
import urllib.request

BASE = 'http://127.0.0.1:8096'
# The admin token comes from the GUI probes' own ids.json: this script is part of that harness.
def _ids():
    here = pathlib.Path(__file__).resolve()
    return json.loads((here.parents[1] / 'gui' / 'ids.json').read_text())

SCRATCH = pathlib.Path(os.environ.get('SCRATCH', '/opt/data/tmp/scratch'))
SLOW_LIB = pathlib.Path('/opt/data/jf12test/media-slow')
SOURCE_EPISODE = os.environ.get('SOURCE_EPISODE', '/opt/data/jf12test/media/Helikopterrånet S01E01.mkv')
FFMPEG = os.environ.get('FFMPEG', '/opt/data/bin/ffmpeg')
# The episode is ~50 minutes; looping it is how a longer obstruction is made without re-encoding.
LOOP_COUNT = os.environ.get('SCRATCH_LOOPS', '3')


SLOW_LIB_ID = os.environ.get('SCRATCH_LIBRARY_ID', '712677157c17ee05cd0d3616c4749b6a')


def refresh(library_id, wait_seconds=0):
    """Ask Jellyfin to re-scan a library, and give it a moment to finish."""
    api('Items/%s/Refresh?Recursive=true&ImageRefreshMode=Default&MetadataRefreshMode=Default' % library_id,
        {}, method='POST')
    if wait_seconds:
        time.sleep(wait_seconds)


def find_item(name):
    """The library item Jellyfin created for a scratch directory, by name."""
    uid = api('Users/Me')['Id']
    listing = api('Users/%s/Items?ParentId=%s&Recursive=true&IncludeItemTypes=Movie&Fields=Path,MediaSources'
                  % (uid, SLOW_LIB_ID))
    for item in listing.get('Items', []):
        if item.get('Name') == name:
            return item
        if name in (item.get('Path') or ''):
            return item
    return None


def api(path, body=None, method=None):
    url = BASE + '/' + path.lstrip('/')
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method or ('POST' if data else 'GET'))
    req.add_header('Authorization', 'MediaBrowser Token="%s"' % _ids()['admin_token'])
    if data:
        req.add_header('Content-Type', 'application/json')
    with urllib.request.urlopen(req, timeout=60) as answer:
        raw = answer.read().decode('utf-8', 'replace')
    return json.loads(raw) if raw.strip().startswith(('{', '[')) else raw


def start():
    """Create a big file, let Jellyfin index it, and queue a sync of it.

    EnqueueSync has no filesystem access (validated from Jellyfin's cached metadata), but it *does* ask the
    library manager for the item - so an item id that exists only in a JSON file beside the media is not
    enough: the 404 the first attempt earned was the server looking the id up and not finding an item.
    The file therefore goes into the slow-storage library, the library is refreshed, and the id Jellyfin
    assigned is what gets queued.
    """
    SCRATCH.mkdir(parents=True, exist_ok=True)
    stamp = time.strftime('%H%M%S')
    name = 'priority-scratch-%s' % stamp
    directory = SCRATCH / name
    directory.mkdir()
    media = directory / 'Big.mkv'

    # A real Matroska file, not a sparse stub: EnqueueSync reads Jellyfin's cached metadata and refuses an
    # item with no subtitle stream ("Subtitle stream index N not found"), so the obstruction has to carry a
    # subtitle track the queue will accept. Copying the episode's first subtitle stream and looping it to a
    # long duration gives an extraction pass that reads a lot - which is what the obstruction is for.
    source = pathlib.Path(SOURCE_EPISODE)
    if not source.exists():
        raise SystemExit('source episode missing: %s (set SOURCE_EPISODE)' % source)
    minutes = int(os.environ.get('SCRATCH_MINUTES', '40'))
    argv = [FFMPEG, '-y', '-v', 'error',
            '-stream_loop', str(LOOP_COUNT), '-i', str(source),
            '-map', '0:v:0', '-map', '0:a:0', '-map', '0:s:0',
            '-t', str(minutes * 60),
            '-c', 'copy', str(media)]
    built = subprocess.run(argv, capture_output=True, text=True)
    if built.returncode != 0 or not media.exists() or media.stat().st_size == 0:
        raise SystemExit('ffmpeg could not build the obstruction:\n%s' % built.stderr[-1500:])

    # Into the library Jellyfin watches: a symlink keeps one copy of the data.
    SLOW_LIB.mkdir(parents=True, exist_ok=True)
    link = SLOW_LIB / name
    if link.exists():
        link.unlink()
    os.symlink(directory, link)

    refresh(SLOW_LIB_ID, wait_seconds=int(os.environ.get('SCRATCH_SCAN_WAIT', '35')))
    item = find_item(name)
    if item is None:
        raise SystemExit('the library scan did not index %s; is the file inside %s?' % (name, SLOW_LIB))

    media_sources = item.get('MediaSources') or []
    streams = (media_sources[0].get('MediaStreams') if media_sources else None) or []
    subtitles = [s for s in streams if s.get('Type') == 'Subtitle']
    if subtitles:
        # the item has real subtitle streams (it should not: the file is empty), take the first
        stream_index = subtitles[0]['Index']
    else:
        # No stream to point at. The queue validates the stream index against the media source and refuses
        # anything it cannot find, so this file cannot be queued through /SubSync/Sync at all. Recorded
        # rather than worked around: the probe's obstruction has to come from real media.
        stream_index = None

    spec = {
        'label': name,
        'path': str(directory),
        'media': str(media),
        'itemId': item['Id'],
        'streamIndex': stream_index,
        'createdAt': time.time(),
        'itemName': item['Name'],
    }
    spec_file = SCRATCH / (name + '.json')

    if stream_index is None:
        spec['queued'] = None
        spec['note'] = 'no subtitle stream in this file: nothing queued'
        spec_file.write_text(json.dumps(spec, indent=1))
        print(json.dumps({'queued': None, 'label': name, 'itemId': item['Id'],
                          'note': spec['note'], 'spec': str(spec_file)}, indent=1))
        return

    job = api('SubSync/Sync', {
        'itemId': item['Id'],
        'subtitleIndex': stream_index,
        'label': name,
    })
    spec['queued'] = job
    spec_file.write_text(json.dumps(spec, indent=1))
    print(json.dumps({'queued': job.get('Id') if isinstance(job, dict) else job,
                      'label': name, 'itemId': item['Id'], 'streamIndex': stream_index,
                      'spec': str(spec_file)}, indent=1))


def list_jobs():
    active = api('SubSync/Active')
    print(json.dumps(active, indent=1))


def stop():
    try:
        print(json.dumps(api('SubSync/Kill', {'all': True}), indent=1))
    except Exception as error:  # nothing running is not a failure
        print('kill: %s' % error)
    removed = []
    for spec_file in sorted(SCRATCH.glob('priority-scratch-*.json')):
        spec = json.loads(spec_file.read_text())
        directory = pathlib.Path(spec['path'])
        link = SLOW_LIB / spec['label']
        for victim in (link, directory):
            try:
                if victim.is_symlink() or victim.is_file():
                    victim.unlink()
                elif victim.is_dir():
                    import shutil
                    shutil.rmtree(victim)
                removed.append(str(victim))
            except FileNotFoundError:
                pass
        spec_file.unlink()
    print(json.dumps({'removed': removed}, indent=1))


if __name__ == '__main__':
    action = sys.argv[1] if len(sys.argv) > 1 else 'start'
    {'start': start, 'list': list_jobs, 'stop': stop}[action]()
