#!/usr/bin/env python3
"""One command per scenario, one file of results: the register's evidence, produced here.

Why this exists
---------------
The register (`knowledge/FIX_PLAN.md`) had rows that could only be settled by the user running the
plugin on his server, so they were never settled. Everything those rows need is on this machine:

  * a real Jellyfin at `/opt/data/jf12test`, started by this script (the same command
    `tests/backend/start-server.sh` uses, with the ICU library path this host needs),
  * `tests/backend/slowread.so` - an `LD_PRELOAD` shim that puts a *real* per-read latency and a real
    per-byte cost on one media directory and nothing else, so a local NVMe can stand in for the share
    (fabji's own measurements: ~10 ms per read and ~11 MB/s - see `tests/backend/slowread-fabji.env`;
    S41 was one cold read of 231 ms),
  * libraries pointing at a fast volume (`media/`), the shimmed one (`media-slow/`), a RAM volume
    (`/dev/shm/s39-fast`) and the fixture set (`media-fixtures/`).

A scenario starts the server with a named plugin build and a named storage profile, queues a batch
through the plugin's own API, waits for the evidence, and asserts on the plugin log. Every assertion
that ran - passed or failed - is appended to `tests/rig/results.json` with the log lines it decided
on, so a long run survives an interruption and the report is built from the file, not from memory.

Usage
-----
    python3 tests/rig/run_scenario.py --list
    python3 tests/rig/run_scenario.py --scenario s41-thrash-tier --ms-per-call 231
    python3 tests/rig/run_scenario.py --scenario s41-thrash-tier --ms-per-call 231 --keep-rig

Every scenario prints one line per assertion and writes the same record to the results file. A run
whose assertions do not all hold exits 1: that is the point of the exercise - a scenario that cannot
fail proves nothing.

The caller's identity
---------------------
The rig poses as the server's own admin, using the access token out of the rig's `Devices` table
rather than an API key. Since 2.0.34 the plugin's item endpoints resolve who is calling and fail
closed without an identity, so a key would exercise a door no user walks through; a token is the
stricter instrument and the one that can disagree with a fix.
"""
from __future__ import annotations

import argparse
import json
import os
import pathlib
import re
import shutil
import subprocess
import urllib.error
import sys
import time

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / 'tests' / 'backend'))

import rig as riglib  # noqa: E402  (needs the path above)

RESULTS = pathlib.Path(__file__).resolve().parent / 'results.json'
JELLYFIN = pathlib.Path('/opt/data/jf12test')

# The small fixture both volumes get, so a scenario can queue a real job on each without a walk over
# gigabytes at 231 ms per read. It carries two embedded subrip tracks (eng, swe) - a text track is
# what the extractor's cue-indexed route needs to be exercised at all.
SMALL_FIXTURE = JELLYFIN / 'media' / 'Embedded Test (2026).mkv'

# The two volumes a scenario compares. They must sit on *different* filesystems, or the plugin's own
# volume rule (the device behind the longest mount point) makes one profile of them and there is only
# one verdict to read: `media/` and `media-slow/` are both on /dev/nvme0n1p2, while the library named
# "S39 Fast Storage" points at a tmpfs, which is what makes a fast volume here.
SLOW_VOLUME_DIR = JELLYFIN / 'media-slow'
FAST_VOLUME_DIR = pathlib.Path('/dev/shm/s39-fast')


def volume_key_of(path: str) -> str:
    """The volume key the plugin computes for a path - its own rule, mirrored, so a scenario can tell
    which volume a log line is about. See `Services/VolumeProfile.cs`: the device behind the longest
    mount point that contains the path."""
    full = str(pathlib.Path(path).resolve()) if pathlib.Path(path).exists() else str(path)
    longest, device = -1, '?'
    with open('/proc/self/mounts', encoding='utf-8') as handle:
        for line in handle:
            fields = line.split()
            if len(fields) < 2:
                continue
            mount_point = fields[1].replace('\\040', ' ')
            if not full.startswith(mount_point):
                continue
            if mount_point != '/' and len(full) > len(mount_point) and full[len(mount_point)] != '/':
                continue
            if len(mount_point) > longest:
                longest, device = len(mount_point), fields[0]
    return device


# The first-measurement line, in the shape the fix writes it:
#   volume <key> had nothing measured about it, so it was read 3 time(s) of 16 KB: median of 3 reads took
#   13,16 ms (slowest 231,13 ms) - the ceiling for that volume is 2 (<why>)
# and in the shape before it, so a scenario run against an older build parses the line and fails the
# assertions that are about the fix, rather than reporting that it saw nothing:
#   volume <key> had nothing measured about it, so it was read once: 16 KB took 231,13 ms - the ceiling ...
PROBE = re.compile(
    r'volume (\S+) had nothing measured about it, so it was read (\d+) time\(s\) of (\d+) KB: '
    r'median of (\d+) reads took ([\d.,]+) ms.*? - the ceiling for that volume is (\S+?)(?: \((.*)\))?$')
PROBE_LEGACY = re.compile(
    r'volume (\S+) had nothing measured about it, so it was read once: (\d+) KB took ([\d.,]+) ms'
    r' - the ceiling for that volume is (\S+?)(?: \((.*)\))?$')


def parse_probe(line: str) -> dict | None:
    """One first-measurement line as fields: volume, how many reads it stands on, the figure, the verdict."""
    m = PROBE.search(line)
    if m:
        return {'line': line, 'key': m.group(1), 'reads': int(m.group(2)), 'kb': int(m.group(3)),
                'ms': m.group(5), 'ceiling': m.group(6), 'why': (m.group(7) or '').strip()}
    m = PROBE_LEGACY.search(line)
    if m:
        # "read once": one read, and no median - the defect's own wording, kept parseable on purpose.
        return {'line': line, 'key': m.group(1), 'reads': 1, 'kb': int(m.group(2)),
                'ms': m.group(3), 'ceiling': m.group(4), 'why': (m.group(5) or '').strip()}
    return None


def log(message: str) -> None:
    print(message, flush=True)


# The fixture a *walk* needs: one embedded subtitle track and no other, because the plugin only measures a
# walk when the job fell back to the audio (`!usedSubtitleReference`). A second text track would serve as the
# reference and the engine run would never be counted as a walk of the volume.
WALK_FIXTURE = JELLYFIN / 'media-slow' / 'Slow Single Track (2026).mkv'

# A library of the rig's own, pointing at a directory on the overlay filesystem: it is a different device from
# the media on /opt/nvme (and from the tmpfs), so the plugin treats it as a volume of its own - which is what a
# ratio needs: one volume to be the reference, another to be judged against it.
WALK_LIBRARY_NAME = 'S39 Walk Storage'
WALK_VOLUME_DIR = pathlib.Path('/tmp/s39-fast')


def ensure_library(name: str, path: pathlib.Path) -> None:
    """Registers a media library in the rig, the way the rig's own libraries are stored.

    Jellyfin keeps a virtual folder as `data/root/default/<name>/` holding `options.xml` (with the paths), a
    `<slug>.mblink` naming the folder, and a `<type>.collection` marker - so a library is created by copying
    that shape, not by driving the UI. The rig is stopped when this runs.
    """
    options = JELLYFIN / 'data' / 'root' / 'default' / 'S39 Fast Storage' / 'options.xml'
    target = JELLYFIN / 'data' / 'root' / 'default' / name
    target.mkdir(parents=True, exist_ok=True)
    (target / 'options.xml').write_text(
        options.read_text().replace('/dev/shm/s39-fast', str(path)))
    slug = name.lower().replace(' ', '-')
    (target / f'{slug}.mblink').write_text(str(path))
    (target / 'movies.collection').write_text('')
    log(f'[rig] library "{name}" registered for {path}')


def prepare_walk_fixtures(count: int) -> None:
    """Copies the single-track fixture onto the walk volume, once per job the reference needs.

    Copies rather than hardlinks: a hardlink shares the inode, size and mtime of the media/ original, and the
    extractor keys its subtitle cache on exactly that, so a linked file would be answered from cache and the
    engine run would never happen. `count` files because a walk is one engine run per job, and a volume must
    have `MinSamplesForReference` (8) of them before it can set the bar for the others.
    """
    WALK_VOLUME_DIR.mkdir(parents=True, exist_ok=True)
    for index in range(1, count + 1):
        target = WALK_VOLUME_DIR / f'Walk Reference {index:02d} (2026).mkv'
        if not target.exists():
            shutil.copy2(WALK_FIXTURE, target)
    log(f'[rig] {count} walk fixture(s) on {WALK_VOLUME_DIR} '
        f'({len(list(WALK_VOLUME_DIR.glob("*.mkv")))} present)')


def prepare_fixtures() -> None:
    """Puts the small fixture on both scenario volumes, as a plain copy each time.

    A hardlink would share the inode, size and mtime of the `media/` copy, and the extractor keys its
    subtitle cache on exactly that - so the pass would answer from cache and read nothing, quietly
    turning the storage profile into decoration. Fresh copies have their own identity and are read.
    """
    target = SLOW_VOLUME_DIR / SMALL_FIXTURE.name
    stale = target.exists() and target.stat().st_ino == SMALL_FIXTURE.stat().st_ino
    if stale:
        target.unlink()  # the earlier hardlink: same cache identity as the media/ copy
    if not target.exists():
        shutil.copy2(SMALL_FIXTURE, target)
        log(f'[rig] copied {SMALL_FIXTURE.name} onto the slow volume'
            + (' (replaced a hardlink that would have been served from cache)' if stale else ''))
    FAST_VOLUME_DIR.mkdir(parents=True, exist_ok=True)
    fast_target = FAST_VOLUME_DIR / SMALL_FIXTURE.name
    if not fast_target.exists():
        shutil.copy2(SMALL_FIXTURE, fast_target)
        log(f'[rig] copied {SMALL_FIXTURE.name} onto the fast volume ({volume_key_of(str(fast_target))})')


def ensure_items(rig, path_contains: str, name_prefix: str, args, tries: int = 6) -> list:
    """Refreshes the library until the scenario's fixture shows up in it, and returns the items.

    A freshly linked or copied file is not in the library until a scan has run, and a scan takes what
    it takes - so a scenario waits for its fixture instead of assuming it, and says how many tries it
    spent when it never appears.
    """
    found = []
    for attempt in range(tries):
        found = [i for i in rig.items(path_contains) if (i.get('Name') or '').startswith(name_prefix)]
        if found:
            return found
        rig.refresh_library()
        time.sleep(10 if attempt == 0 else 15)
    return found


# --------------------------------------------------------------------------- scenarios


def scenario_smoke(rig, args, ctx):
    """The harness checking itself: the rig starts, sees both volumes, and reports the log path."""
    items = rig.items()
    slow = ensure_items(rig, 'media-slow', 'Embedded Test', args)
    fast = ensure_items(rig, str(FAST_VOLUME_DIR), 'Embedded Test', args)
    ctx['observations'] = [f'slow volume key: {volume_key_of(str(SLOW_VOLUME_DIR))}',
                           f'fast volume key: {volume_key_of(str(FAST_VOLUME_DIR))}']
    return [
        ('the rig lists items at all', bool(items), f'{len(items)} item(s)'),
        ('the shimmed volume has the scenario fixture', bool(slow),
         f'{len(slow)} on {SLOW_VOLUME_DIR} ({volume_key_of(str(SLOW_VOLUME_DIR))})'),
        ('the fast volume has the scenario fixture', bool(fast),
         f'{len(fast)} on {FAST_VOLUME_DIR} ({volume_key_of(str(FAST_VOLUME_DIR))})'),
        ('the two volumes are different volumes',
         volume_key_of(str(SLOW_VOLUME_DIR)) != volume_key_of(str(FAST_VOLUME_DIR)),
         f'{volume_key_of(str(SLOW_VOLUME_DIR))} vs {volume_key_of(str(FAST_VOLUME_DIR))}'),
        ('the plugin log is where the plugin writes', riglib.PLUGIN_LOG.exists(),
         str(riglib.PLUGIN_LOG)),
    ]


def _s41(rig, args, ctx, expect_slow_ceiling=None):
    """The S41 body, shared by both storage profiles.

    Sets the batch going, waits for a verdict from *both* volumes (waiting for the first one is how the
    first version of this scenario measured nothing: it stopped the rig two seconds after queueing, while
    the second job was still queued), then reads the probe and ceiling lines.
    """
    slow_items = ensure_items(rig, 'media-slow', 'Embedded Test', args)
    fast_items = ensure_items(rig, str(FAST_VOLUME_DIR), 'Embedded Test', args)
    found = [('both volumes have the scenario fixture', bool(slow_items and fast_items),
              f'slow={len(slow_items)} fast={len(fast_items)} after a library refresh')]
    if not slow_items or not fast_items:
        return found

    slow_key = volume_key_of(str(SLOW_VOLUME_DIR))
    fast_key = volume_key_of(str(FAST_VOLUME_DIR))

    # Only this run's lines are its evidence: reading from the start of a log that carries every earlier
    # run is how the first version "saw" probes it had not made.
    rig.log_lines()
    since = rig._log_offset
    batch = rig.queue_batch(slow_items[:1] + fast_items[:1], label='rig-s41')
    ctx['batch'] = batch

    wanted = {slow_key, fast_key}
    deadline = time.time() + float(args.timeout)
    started = time.time()
    lines, probed = [], {}
    while True:
        lines = rig.log_lines(since)
        probed = {p['key']: p for p in (parse_probe(ln) for ln in lines) if p}
        if wanted <= set(probed) or time.time() >= deadline:
            break
        time.sleep(3)

    slow, fast = probed.get(slow_key), probed.get(fast_key)
    ctx['observations'] = [v['line'] for v in probed.values()]
    ctx['waited_s'] = round(time.time() - started, 1)
    ctx['probed'] = probed

    samples = slow['reads'] if slow else 0

    assertions = found + [
        ('both volumes were probed in one run', wanted <= set(probed),
         f'probed {sorted(probed)} in {ctx["waited_s"]}s (wanted {sorted(wanted)})'),
        ('the slow volume\'s verdict names how many reads it stands on', bool(slow),
         f'S41 defect when the line says "read once" -> '
         f'{slow["line"] if slow else "(the slow volume was never probed)"}'),
        ('a verdict does not rest on a single read', samples >= 2,
         f'samples={samples} (one cold read decides at 1), '
         f'median={slow["ms"] if slow else "?"} ms, verdict={slow["ceiling"] if slow else "?"} '
         f'({slow["why"] if slow else "?"})'),
        ('the fast volume gets no ceiling', bool(fast) and fast['ceiling'] == 'none',
         fast['line'] if fast else '(the fast volume was never probed)'),
    ]
    if expect_slow_ceiling is not None:
        assertions.append(
            (f'the shimmed volume is allowed the walks its own state says ({expect_slow_ceiling})',
             bool(slow) and slow['ceiling'] == expect_slow_ceiling,
             f'{"ceiling=" + slow["ceiling"] + " because " + slow["why"] if slow else "no verdict"}'))
    return assertions


def scenario_s41_thrash_tier(rig, args, ctx):
    """S41: one cold read must not decide that a volume is thrashing.

    The field defect (2.0.37, 2026-09-14): the probe takes a *single* 16 KB cold read and whatever it
    returns becomes the volume's class, so a share that answered one read in 231 ms - a loaded moment,
    against a steady 13-46 ms - was held to one walk for a whole run while its own measurements say two
    is its best. The shim reproduces that cold read exactly.

    The assertions are what the fix has to make true: the verdict says how many reads it stands on, and
    a single read cannot reach the thrash tier. The run therefore ends in FAIL against 2.0.37, quoting
    the defect.
    """
    return _s41(rig, args, ctx)


def scenario_s41_cold_read(rig, args, ctx):
    """S41's acceptance, in the shape the field reported it.

    The shim answers the first read on a freshly opened handle in 231 ms and every later read in 13 ms -
    one cold read, then a share at rest - so the volume's own steady state is 13 ms and two walks is its
    best. The probe opens its own handle and reads once, so it is the read that gets the cold price, and
    the reads that follow in the same pass are warm: the field's shape, reproduced by construction rather
    than by a lucky ordering.
    A probe that takes that first read as the volume's class calls it thrashing and holds it to one
    walk for the whole run, which is what happened on 2026-09-14. A probe that samples more than once
    and takes the median lands on 13 ms and allows the two walks.

    Assertions: two walks for the shimmed volume, no ceiling on the fast one, and a verdict that says
    what it stands on. Against 2.0.37 this FAILS on the ceiling and on the sample count.
    """
    return _s41(rig, args, ctx, expect_slow_ceiling='2')


def scenario_s41_steady(rig, args, ctx):
    """S41's acceptance: on a share in its normal state (fabji's measured 10 ms/read, 11 MB/s), the
    shimmed volume must be allowed two walks and the fast volume none, in the same run."""
    return _s41(rig, args, ctx, expect_slow_ceiling='2')


def prepare_judged_fixtures(count: int) -> None:
    """Copies the single-track fixture onto the shimmed volume, once per job that must be held.

    The S39 line is a *hold* line - the scheduler only says why a volume is capped when it actually holds a
    walk back - so the judged volume needs `cap` walks running (2) plus one more being planned. Copies, not
    links: a link shares the cache identity of the media/ original and the pass would be answered from cache.
    """
    for index in range(1, count + 1):
        target = SLOW_VOLUME_DIR / f'Judged Clip {index:02d} (2026).mkv'
        if not target.exists():
            shutil.copy2(WALK_FIXTURE, target)
    log(f'[rig] {count} judged fixture(s) on {SLOW_VOLUME_DIR}')


# S31's fixture: the real 50-minute episode, without its embedded subtitle tracks, beside two external sidecars -
# the one to sync, and a sibling that is the same track from a different cut. The episode is hardlinked (no copy
# of 2,4 GB) and the stripped file is made once with a stream copy.
S31_SOURCE = JELLYFIN / 'media' / 'Helikopterrånet S01E01.mkv'
S31_DIR = JELLYFIN / 'media' / 'S31 Wrong Ruler'
S31_MEDIA = S31_DIR / 'S31 Episode (2026).mkv'
S31_TARGET = S31_DIR / 'S31 Episode (2026).eng.srt'
S31_RULER = S31_DIR / 'S31 Episode (2026).pol.srt'
S31_SCRATCH = pathlib.Path('/tmp/s31-fixture')
S31_SPAN_STRETCH = 1.02      # a different cut: inside the plugin's 3 % span check
S31_TARGET_SHIFT = 5.0            # the target's own misalignment, in seconds (other-cut variant)
S31_TARGET_SHIFT_CORRECT = 20.0   # the same, for the correct-ruler variant: big enough to reach the band


def _srt_stamp(seconds: float) -> str:
    hours = int(seconds // 3600)
    minutes = int((seconds % 3600) // 60)
    secs = int(seconds % 60)
    millis = int(round((seconds - int(seconds)) * 1000))
    return f'{hours:02d}:{minutes:02d}:{secs:02d},{millis:03d}'


def _srt_seconds(value: str) -> float:
    hours, minutes, rest = value.split(':')
    secs, millis = rest.replace('.', ',').split(',')
    return int(hours) * 3600 + int(minutes) * 60 + int(secs) + int(millis) / 1000.0


def _rerender(source: str, move) -> str:
    blocks = [b for b in re.split(r'\n\s*\n', source.strip()) if '-->' in b]
    out = []
    for block in blocks:
        lines = block.splitlines()
        start, end = lines[1].split(' --> ')
        moved = f'{_srt_stamp(move(_srt_seconds(start)))} --> {_srt_stamp(move(_srt_seconds(end)))}'
        out.append('\n'.join([lines[0], moved] + lines[2:]))
    return '\n\n'.join(out) + '\n'


def prepare_s31_fixtures(ruler_kind: str = 'other-cut') -> None:
    """Builds S31's shape and says what it is, because the shape is the whole experiment.

    * The media carries exactly two embedded subtitle tracks: the **target** (the episode's own track shifted
      +5 s - a plausible misalignment) and the **ruler**. Embedded rather than external sidecars, because that is
      the path the plugin takes when it picks "another text track" as the reference, and an external-sidecar
      target resolved to `ordinal=-1` and fell back to the audio (measured 2026-09-15), which would prove nothing.
    * `ruler_kind='other-cut'` is the Alex shape: the same track stretched 1,02x, so it passes the cue-count
      check, the 3 % span rule and the 30 s ceiling while no longer matching the film. `'correct'` is the film's
      own timeline: the cross-check must accept it, which is what makes the check able to disagree.

    The media is rebuilt whenever the requested ruler changes, because the ruler is a track inside it.
    """
    S31_DIR.mkdir(parents=True, exist_ok=True)
    S31_SCRATCH.mkdir(parents=True, exist_ok=True)
    mark = S31_SCRATCH / f'ruler-{ruler_kind}.mark'

    raw = S31_SCRATCH / 'source-track.srt'
    if not raw.exists() or raw.read_text(encoding='utf-8', errors='replace').count(' --> ') < 100:
        # Not `0:s:0`: the episode's first embedded track is a signs track with 8 cues (measured), and 8 cues over
        # 50 minutes is refused by the plugin's own signs check - a different refusal than the one under test.
        subprocess.run(['/usr/bin/ffmpeg', '-y', '-v', 'error', '-i', str(S31_SOURCE),
                        '-map', '0:4', str(raw)], check=True, capture_output=True)
    source = raw.read_text(encoding='utf-8', errors='replace')

    stale = S31_MEDIA.exists() and not mark.exists()
    if stale:
        S31_MEDIA.unlink()

    target = S31_SCRATCH / 'target.srt'
    ruler = S31_SCRATCH / 'ruler.srt'
    # A correct ruler still has to reach the cross-check band for the check to be exercised at all: its own
    # demand is the target's shift, so that variant uses a larger one (20 s > the 10 s band at the default
    # ceiling). The other-cut variant keeps 5 s, where the ruler's *own* demand lands at ~24 s.
    shift = S31_TARGET_SHIFT if ruler_kind != 'correct' else S31_TARGET_SHIFT_CORRECT
    target.write_text(_rerender(source, lambda x: x + shift), encoding='utf-8')
    ruler.write_text(_rerender(source, (lambda x: x) if ruler_kind == 'correct'
                               else (lambda x: x * S31_SPAN_STRETCH)), encoding='utf-8')

    if not S31_MEDIA.exists():
        subprocess.run(
            ['/usr/bin/ffmpeg', '-y', '-v', 'error', '-i', str(S31_SOURCE),
             '-i', str(target), '-i', str(ruler),
             '-map', '0:v', '-map', '0:a', '-map', '1', '-map', '2',
             '-c', 'copy', '-c:s', 'srt',
             '-metadata:s:s:0', 'language=eng', '-metadata:s:s:1', 'language=pol',
             '-metadata', 'title=S31 Wrong Ruler', str(S31_MEDIA)],
            check=True, capture_output=True)

    for other in S31_SCRATCH.glob('ruler-*.mark'):
        other.unlink()
    mark.write_text('ok')
    log(f"[rig] S31 fixture ready ({ruler_kind} ruler): {source.count(' --> ')} cue(s) per track - "
        f"target shifted +{shift:0.0f} s (eng), ruler "
        + ('the film\'s own timeline (pol)' if ruler_kind == 'correct'
           else f'stretched {S31_SPAN_STRETCH}x (pol)'))


def scenario_s31_wrong_ruler(rig, args, ctx):
    """Runs with `--s31-ruler correct` to prove the cross-check does not fire when the ruler is right."""
    """S31: a sibling subtitle from a different cut must not be trusted as a ruler.

    What the row is about, in the field's own shape: a track that passes every plausibility check the plugin has
    (cue count, span against the file, demanded shift) but is not this film's timeline. The plugin aligns the
    target onto it and writes the result as an ordinary success.

    Assertions, in the order the fix makes them true: the engine's score is in the log at all, the sync was not
    left standing on that ruler, the ruler was discarded, and the run says it aligned against the audio instead.
    Run against a released build first: every one of those fails and a sidecar is written anyway, which is the
    silent-wrongness this row exists for.
    """
    # Scanned first with the fixture absent, so an item left over from an earlier run (with its old subtitle
    # streams) is dropped rather than updated in place, then scanned again once the file exists.
    rig.refresh_library()
    time.sleep(15)
    prepare_s31_fixtures(getattr(args, 's31_ruler', 'other-cut'))
    rig.refresh_library()
    time.sleep(10)
    # Looked up by path, not by name: Jellyfin names the movie after its folder, so a name filter finds nothing.
    items = [i for i in rig.items(str(S31_DIR)) if str(S31_DIR) in (i.get('Path') or '')]
    if not items:
        rig.refresh_library()
        time.sleep(20)
        items = [i for i in rig.items(str(S31_DIR)) if str(S31_DIR) in (i.get('Path') or '')]
    if not items:
        return [('the S31 fixture is in the library', False, f'no item whose path contains {S31_DIR}')]

    # The audio path has to be able to succeed for the fallback to mean anything: clear the caches so the
    # reference is built and the audio analysed in this run.
    rig.post('/SubSync/SpeechCache/Clear')
    rig.log_lines()
    since = rig._log_offset
    # The target is an *embedded* track: the ruler has to be the file's other embedded track, which is the path
    # the plugin takes when it picks a sibling subtitle as the reference.
    batch = rig.queue_batch(items[:1], label='rig-s31', external=False)
    ctx['batch'] = batch
    lines, deadline = [], time.time() + float(args.timeout)
    while time.time() < deadline:
        lines = rig.log_lines(since)
        if any('job ' in ln and 'completed' in ln for ln in lines) or \
           any(ln.strip().endswith(('FAILED', 'UNVERIFIED')) for ln in lines):
            break
        time.sleep(5)
    rig.wait_batch(batch, timeout=60)

    text = '\n'.join(lines)
    scores = [ln for ln in lines if 'ffsubsync alignment: score=' in ln]
    cross = [ln for ln in lines if 'cross-check' in ln]
    disagree = [ln for ln in lines if 'disagree' in ln]
    confirm = [ln for ln in lines if 'confirmed by' in ln]
    discarded = [ln for ln in lines if 'discarded as a ruler' in ln]
    written = [ln for ln in lines if 'completed:' in ln]
    ctx['observations'] = scores + cross + disagree + confirm + discarded + written
    other_cut = getattr(args, 's31_ruler', 'other-cut') != 'correct'

    assertions = [
        ('the engine\'s alignment score is in the log', bool(scores),
         scores[0].split('INFO')[-1].strip()[:160] if scores else '(no score line: the build throws it away)'),
        ('a suspicious ruler is cross-checked against the film\'s own audio', bool(cross),
         cross[0].split('INFO')[-1].strip()[:150] if cross else '(no cross-check ran)'),
    ]
    if other_cut:
        assertions += [
            ('the wrong ruler and the audio disagree', bool(disagree),
             disagree[0].split('INFO')[-1].strip()[:190] if disagree else '(nothing disagreed)'),
            ('the wrong ruler is discarded', bool(discarded),
             discarded[0].split('INFO')[-1].strip()[:150] if discarded else '(the track was kept as a ruler)'),
            ('the wrong ruler\'s answer was not written', any('UNVERIFIED' in ln or 'nothing written' in ln
                                                           or 'method=audio' in ln for ln in lines),
             ' | '.join(ln.split('INFO')[-1].strip()[:110] for ln in lines
                        if 'UNVERIFIED' in ln or 'nothing written' in ln) or
             ('completed with the audio\'s answer' if written else '(nothing said either way)')),
        ]
    else:
        assertions += [
            ('a correct ruler is confirmed by the audio, not refused', bool(confirm),
             confirm[0].split('INFO')[-1].strip()[:180] if confirm else '(the audio never confirmed it)'),
            ('nothing was discarded when the ruler was right', not discarded,
             discarded[0].split('INFO')[-1].strip()[:150] if discarded else 'no discard, as expected'),
            ('a subtitle was produced', bool(written),
             written[0].split('INFO')[-1].strip()[:150] if written else '(no completion line)'),
        ]
    return assertions


# S43's fixture: one embedded text track, and it is 30 s out of sync with the film. A single text track forces
# the plugin to use the audio as the reference, so this is exactly the case where "the audio path" must be audio.
S43_DIR = JELLYFIN / 'media' / 'S43 Audio Is Audio'
S43_MEDIA = S43_DIR / 'S43 Episode (2026).mkv'
S43_TARGET_SHIFT = 30.0


def prepare_s43_fixtures() -> None:
    """Media whose only subtitle track is 30 s out of sync, so the audio is the only usable reference.

    The track is the episode's own (stream 4, in sync with the film) shifted by 30 s: the film's audio therefore
    says -30,00 s and the track itself says 0,00 s. With the configured VAD the engine reads that very track out
    of the container as its speech signal and answers ~0; with `webrtc` it reads the film and answers ~-30.
    """
    S43_DIR.mkdir(parents=True, exist_ok=True)
    S43_SCRATCH = pathlib.Path('/tmp/s43-fixture')
    S43_SCRATCH.mkdir(parents=True, exist_ok=True)
    raw = S43_SCRATCH / 'source.srt'
    if not raw.exists() or raw.read_text(encoding='utf-8', errors='replace').count(' --> ') < 100:
        subprocess.run(['/usr/bin/ffmpeg', '-y', '-v', 'error', '-i', str(S31_SOURCE),
                        '-map', '0:4', str(raw)], check=True, capture_output=True)
    target = S43_SCRATCH / 'target.srt'
    target.write_text(_rerender(raw.read_text(encoding='utf-8', errors='replace'),
                                lambda x: x + S43_TARGET_SHIFT), encoding='utf-8')
    if not S43_MEDIA.exists():
        subprocess.run(
            ['/usr/bin/ffmpeg', '-y', '-v', 'error', '-i', str(S31_SOURCE), '-i', str(target),
             '-map', '0:v', '-map', '0:a', '-map', '1',
             '-c', 'copy', '-c:s', 'srt', '-metadata:s:s:0', 'language=eng',
             '-metadata', 'title=S43 Audio Is Audio', str(S43_MEDIA)],
            check=True, capture_output=True)
    log(f'[rig] S43 fixture ready: one embedded track shifted +{S43_TARGET_SHIFT:0.0f} s, '
        f'so the film says -{S43_TARGET_SHIFT:0.0f} s and the track itself says 0')


def scenario_s43_audio_is_audio(rig, args, ctx):
    """S43: when the plugin says "the audio", the engine must be reading audio.

    The file carries one subtitle track, 30 s out of sync: the plugin has no sibling to use as a reference, so it
    aligns against the audio. With the configured `subs_then_webrtc` the engine reads that track - the one being
    synced - out of the container as its speech signal and answers ~0 s, i.e. "already in sync" for a subtitle
    that is 30 s out. With the audio VAD forced it answers ~-30 s, which is the film's own answer.

    Assertions: the log names the VAD the engine was given and why, and the alignment it reported is the film's
    answer rather than the track's. Run against a build without the rule and both fail.
    """
    rig.refresh_library()
    time.sleep(15)
    prepare_s43_fixtures()
    rig.refresh_library()
    time.sleep(10)
    items = [i for i in rig.items(str(S43_DIR)) if str(S43_DIR) in (i.get('Path') or '')]
    if not items:
        rig.refresh_library()
        time.sleep(20)
        items = [i for i in rig.items(str(S43_DIR)) if str(S43_DIR) in (i.get('Path') or '')]
    if not items:
        return [('the S43 fixture is in the library', False, f'no item whose path contains {S43_DIR}')]

    rig.post('/SubSync/SpeechCache/Clear')
    rig.log_lines()
    since = rig._log_offset
    batch = rig.queue_batch(items[:1], label='rig-s43', external=False)
    ctx['batch'] = batch
    lines, deadline = [], time.time() + float(args.timeout)
    while time.time() < deadline:
        lines = rig.log_lines(since)
        if any('alignment: score=' in ln for ln in lines) and any(
                'completed' in ln or 'UNVERIFIED' in ln or 'failed' in ln.lower() for ln in lines):
            break
        if any('already in sync' in ln for ln in lines):
            break
        time.sleep(4)
    rig.wait_batch(batch, timeout=60)

    vad = [ln for ln in lines if 'so the engine is given --vad' in ln]
    alignment = [ln for ln in lines if 'alignment: score=' in ln]
    outcome = [ln for ln in lines if 'completed' in ln or 'UNVERIFIED' in ln or 'already in sync' in ln]
    ctx['observations'] = vad + alignment + outcome

    offset = None
    for ln in alignment:
        m = re.search(r'offset=(-?[\d.]+) s', ln)
        if m:
            offset = float(m.group(1))
    truth = -S43_TARGET_SHIFT

    return [
        ('the log names the VAD the engine was given, and why', bool(vad),
         vad[0].split('INFO')[-1].strip()[:170] if vad else '(nothing says which VAD ran)'),
        (f'the engine answers the film ({truth:+.0f} s), not the track itself (0 s)',
         offset is not None and abs(offset - truth) <= 8.0,
         f'engine reported offset={offset} s across {len(alignment)} run(s): '
         + ' | '.join(ln.split('INFO')[-1].strip()[:90] for ln in alignment)),
        ('the job says what it did with that answer', bool(outcome),
         outcome[0].split('INFO')[-1].strip()[:170] if outcome else '(no outcome line)'),
    ]


# D3 + F10's fixture is not media: it is the settings themselves, driven through the same API the settings page
# uses. The rows are the audit's ten hostile saves, one at a time.
D3_ROWS = [
    ('ParallelWorkers', 99, 'stored in 1-64, and the response says so',
     lambda c: 1 <= c['ParallelWorkers'] <= 64, 'Parallel workers'),
    ('ExtractionTimeoutMinutes', 0, 'stored in 1-240, and the response says so',
     lambda c: 1 <= c['ExtractionTimeoutMinutes'] <= 240, 'Extraction timeout'),
    ('ExtractionTimeoutMinutes', 100000, 'stored in 1-240, and the response says so',
     lambda c: 1 <= c['ExtractionTimeoutMinutes'] <= 240, 'Extraction timeout'),
    ('MaxOffsetSeconds', -5, 'stored in 1-600, and the response says so',
     lambda c: 1 <= c['MaxOffsetSeconds'] <= 600, 'Max offset seconds'),
    ('MaxOffsetSeconds', 100000, 'stored in 1-600, and the response says so',
     lambda c: 1 <= c['MaxOffsetSeconds'] <= 600, 'Max offset seconds'),
    ('MaxSubtitleReferenceOffsetSeconds', -5, 'stored in 1-600, and the response says so',
     lambda c: 1 <= c['MaxSubtitleReferenceOffsetSeconds'] <= 600, 'Max shift from a subtitle reference'),
    ('OutputEncoding', 'not-an-encoding', 'stored as an encoding the engine accepts, and the response says so',
     lambda c: c['OutputEncoding'] in ('utf-8', 'utf-8-sig', 'utf-16', 'latin-1', 'windows-1252', 'cp1252'),
     'Output encoding'),
    ('VadMethod', 'not-a-vad', 'stored as a method the engine accepts, and the response says so',
     lambda c: c['VadMethod'] in ('webrtc', 'subs_then_webrtc', 'auditok', 'silero'), 'VAD method'),
    ('SyncLanguages', ['qq', 'zz', '!!!@#'], 'dropped, and the response says so',
     lambda c: list(c['SyncLanguages']) == [], 'language'),
    ('FfmpegPath', '/no/such/ffmpeg-xyz', 'not stored, and the response says so',
     lambda c: c['FfmpegPath'] == '', 'ffmpeg path'),
    ('FfSubSyncPath', '/no/such/binary-xyz', 'not stored, and the response says so',
     lambda c: c['FfSubSyncPath'] in ('', 'ffsubsync'), 'ffsubsync executable'),
]


def scenario_d3_settings(rig, args, ctx):
    """D3 + F10: every hostile save the audit drove through the page, driven through the API instead.

    One field at a time: read the stored configuration, change that one field to the value the audit typed, save
    it, read back what the server stored, and ask whether the answer said anything at all. Before the shared
    validation path, an out-of-range ceiling, a typo'd encoding, a missing binary path and a language tag that
    matches nothing were all stored exactly as typed, and the page answered "Saved." for every one of them.

    Run against a build without the validation path and most of these fail.
    """
    original = rig.get('/SubSync/Configuration')
    ctx['original'] = original
    assertions = []

    for field, value, label, ok, note_needle in D3_ROWS:
        body = dict(original)
        body[field] = value
        refused = None
        answered = None
        try:
            answered = rig.post('/SubSync/Configuration', body)
        except urllib.error.HTTPError as error:      # a refusal is a fine outcome: nothing half-stored
            refused = error.code

        if refused is not None:
            assertions.append((f'{field} = {value!r} is refused rather than stored as typed', 400 <= refused < 500,
                               f'HTTP {refused}'))
            continue

        stored = answered or {}
        # The save body stays exactly what it was; what was adjusted is reported on its own endpoint.
        try:
            notes = [str(n) for n in (rig.get('/SubSync/Settings/ValidationNotes') or [])]
        except urllib.error.HTTPError:
            notes = []
        value_ok = bool(ok(stored))
        note_ok = any(note_needle.lower() in n.lower() for n in notes)
        shown = f"{field}={value!r} stored as {stored.get(field)!r}"
        if note_ok:
            shown += ' | ' + next(n for n in notes if note_needle.lower() in n.lower())[:120]
        else:
            shown += ' | the response said nothing about it'
        assertions.append((f'{field} = {value!r} is {label}', value_ok and note_ok, shown))

    # And the configuration is still a usable one: the page's own values go back in without a single note.
    answered = rig.post('/SubSync/Configuration', original)
    try:
        notes = [str(n) for n in (rig.get('/SubSync/Settings/ValidationNotes') or [])]
    except urllib.error.HTTPError:
        notes = []
    assertions.append(('the settings the plugin shipped with are stored unchanged and without complaint',
                       not notes and answered.get('MaxOffsetSeconds') == original.get('MaxOffsetSeconds'),
                       f'{len(notes)} note(s) for the unmodified configuration'))
    return assertions


def scenario_s39_ratio(rig, args, ctx):
    """S39: a ceiling chosen between two numbers this machine measured, not from a constant.

    What the row owes: the judgement has to quote *both* throughputs - this volume's last walk and the best
    this machine has measured - instead of a threshold someone picked for one machine.

    The two volumes are genuinely different devices (the walk fixtures on the overlay filesystem, the judged
    ones on the shimmed share), because the plugin names a volume by the device behind its longest mount point.
    The reference needs 8 walks before it can set the bar for anything else, so the volume is given more than
    that (`--fast-files`, default 12): a walk taken while another volume is being read is discarded by design,
    and one lost sample must not cost the run its reference.
    """
    prepare_walk_fixtures(int(args.fast_files))
    prepare_judged_fixtures(3)
    rig.refresh_library()
    reference = ensure_items(rig, str(WALK_VOLUME_DIR), 'Walk Reference', args)
    judged = ensure_items(rig, 'media-slow', 'Judged Clip', args)
    fast_key = volume_key_of(str(WALK_VOLUME_DIR))
    slow_key = volume_key_of(str(SLOW_VOLUME_DIR))
    found = [('the walk volume holds the reference fixtures', len(reference) >= 8,
              f'{len(reference)} of {args.fast_files} on {WALK_VOLUME_DIR} ({fast_key})'),
             ('the judged volume holds its single-track files', len(judged) >= 3,
              f'{len(judged)} on {SLOW_VOLUME_DIR} ({slow_key})')]
    if len(reference) < 8 or len(judged) < 3:
        return found

    # 1) the reference: one batch of walks on the fast volume, alone on the machine.
    rig.log_lines()
    since = rig._log_offset
    batch = rig.queue_batch(reference, label='rig-s39-reference')
    ctx['reference_batch'] = batch
    walked, deadline = [], time.time() + float(args.timeout)
    while time.time() < deadline:
        lines = rig.log_lines(since)
        walked = [ln for ln in lines if 'this walk moved' in ln and 'not being used' not in ln]
        live = [t for t in (rig.batch(batch).get('Tasks') or [])
                if (t.get('Status') or '').lower() in ('queued', 'running')]
        if len(walked) >= 8 or not live:
            break
        time.sleep(5)
    ctx['observations'] = list(walked)

    # 2) the judged volume walks once, alone. It has to *have* a walk before it can be judged by one: with no
    #    walk of its own the ceiling falls back to the read tier (measured 2026-09-15 - the hold line then
    #    quoted "12,9 ms per read", the absolute behaviour, instead of the ratio the row is about).
    #
    #    The caches are cleared first, and that is the difference between a walk that measures this volume and
    #    one that measures nothing: with the audio analysis cached (it survives a restart) the engine runs on
    #    the cached speech file and never reads the media, so the same job "walked" at 280 MB/s with the shim
    #    in place and at 1,3 MB/s without it. Both are real plugin behaviour; the slow one is the one that
    #    needs the volume, and it is also the one a first run on a fresh file produces.
    cleared = rig.post('/SubSync/SpeechCache/Clear')
    ctx['cache_cleared'] = cleared
    log(f"[rig] caches cleared before the judged walk: {cleared.get('message', cleared)}")

    since = rig._log_offset
    first = rig.queue_batch(judged[:1], label='rig-s39-judged-walk')
    ctx['judged_walk_batch'] = first
    judged_lines, deadline = [], time.time() + min(float(args.timeout), 300.0)
    while time.time() < deadline:
        lines = rig.log_lines(since)
        judged_lines = [ln for ln in lines if 'this walk moved' in ln and str(SLOW_VOLUME_DIR) in ln]
        if judged_lines:
            break
        time.sleep(5)
    ctx['observations'] = list(walked) + list(judged_lines)

    # 3) now fill the volume's ceiling: two walks run (its cap) and a third is held, which is the only moment
    #    the scheduler states what the ceiling was decided from.
    since = rig._log_offset
    slow_batch = rig.queue_batch(judged[1:3] + judged[:1], label='rig-s39-judged')
    ctx['judged_batch'] = slow_batch
    held, deadline = [], time.time() + float(args.timeout)
    while time.time() < deadline:
        lines = rig.log_lines(since)
        held = [ln for ln in lines if 'walk ceiling: holding' in ln and slow_key in ln]
        if held:
            break
        time.sleep(5)
    ctx['observations'] = list(walked) + list(judged_lines) + list(held)
    ctx['held_lines'] = list(held)

    hold = next((ln for ln in held if slow_key in ln), held[0] if held else '')
    numbers = re.findall(r'([\d.,]+) MB/s', hold)
    return found + [
        (f'the reference volume measured itself with at least 8 walk(s)', len(walked) >= 8,
         f'{len(walked)} walk line(s) | {walked[0].split("INFO")[-1].strip()[:120] if walked else "(none)"}'),
        ('the judged volume walked, so it has a walk of its own to be judged by',
         bool(judged_lines),
         (judged_lines[-1].split('INFO')[-1].strip()[:170] if judged_lines else
          f'(no walk line mentioning {SLOW_VOLUME_DIR})')
         + f" | caches cleared first: {ctx.get('cache_cleared', {}).get('message', '(no answer)')[:90]}"),
        ('a walk was held on the judged volume, so the ceiling was stated',
         bool(hold), hold.split('INFO')[-1].strip()[:200] if hold else '(no hold line)'),
        ('the hold quotes both numbers this machine measured, not a constant',
         len(numbers) >= 2 and 'this machine has measured' in hold,
         f'numbers: {numbers}'),
        ('the ceiling it chose is the storage-bound one (2), not none and not one',
         re.search(r'holding \S+ at 2 concurrent', hold) is not None,
         f'cap in the line: {re.search(r"holding \S+ at (\S+) concurrent", hold).group(1) if re.search(r"holding \S+ at (\S+) concurrent", hold) else "?"}'),
    ]


SCENARIOS = {
    'smoke': dict(run=scenario_smoke, storage='any',
                  needs='nothing: it is the harness checking itself (2 volumes, fixtures, log)'),
    's41-thrash-tier': dict(run=scenario_s41_thrash_tier, storage='shim',
                            shim={'MS_PER_CALL': 231.0, 'MS_PER_16K': 0.0},
                            needs='231 ms per read: a share that answers one cold read slowly, from the field'),
    's41-cold-read': dict(run=scenario_s41_cold_read, storage='shim',
                          shim={'MS_PER_CALL': 13.0, 'MS_PER_16K': 0.0,
                                'FD_FIRST_MS': 231.0, 'FD_FIRST_READS': 1},
                          needs="the field shape: a share whose first read took 231 ms and whose steady state is 13 ms"),
    's31-wrong-ruler': dict(run=scenario_s31_wrong_ruler, storage='any',
                            needs='a sibling subtitle from a different cut: the ruler passes every check and is wrong'),
    'd3-settings': dict(run=scenario_d3_settings, storage='any',
                        needs='nothing: it drives the settings API, the same way the page does'),
    's43-audio-is-audio': dict(run=scenario_s43_audio_is_audio, storage='any',
                               needs='a file whose only subtitle track is out of sync: the audio is the only reference'),
    's39-ratio': dict(run=scenario_s39_ratio, storage='shim',
                      shim={'MS_PER_CALL': 0.0, 'MS_PER_16K': 12.8},
                      needs='8 walks on one volume, then a slow walk on another: the ratios must decide, not a constant'),
    's41-steady': dict(run=scenario_s41_steady, storage='shim',
                       shim={'MS_PER_CALL': 10.0, 'MS_PER_16K': 1.46},
                       needs="fabji's measured share (10 ms/read, 11 MB/s): two walks, and no ceiling on the fast one"),
}


# --------------------------------------------------------------------------- driver


def main() -> int:
    parser = argparse.ArgumentParser(description='Run one rig scenario and record its evidence.')
    parser.add_argument('--scenario', default=None, help='which scenario to run (--list for the set)')
    parser.add_argument('--list', action='store_true', help='print the scenarios and what each needs')
    parser.add_argument('--ms-per-call', type=float, default=None,
                        help="the shim's per-read charge in ms (default per scenario; S41's field number: 231)")
    parser.add_argument('--ms-per-16k', type=float, default=None,
                        help="the shim's per-byte charge in ms per 16 KB (11 MB/s = 1.46)")
    parser.add_argument('--no-shim', action='store_true', help='run with no storage profile at all')
    parser.add_argument('--plugin-version', default=None,
                        help='plugin version to install into the rig (default: the tree\'s meta.json)')
    parser.add_argument('--plugin-source', default=None,
                        help='directory holding the built plugin, instead of bin/Release/net10.0')
    parser.add_argument('--timeout', type=float, default=900.0, help='seconds to wait for the evidence')
    parser.add_argument('--settle', type=float, default=20.0, help='seconds after a refresh before queueing')
    parser.add_argument('--keep-rig', action='store_true', help='leave the server running for inspection')
    parser.add_argument('--s31-ruler', choices=('other-cut', 'correct'), default='other-cut',
                        help="S31's ruler: the same track from a different cut, or the film's own timeline")
    parser.add_argument('--fast-files', type=int, default=12,
                        help='walk fixtures (one engine run each): the reference needs 8 to count, so a few extra '
                             'survive the walks that are discarded for contention')
    parser.add_argument('--note', default='', help='a line to store beside this run')
    args = parser.parse_args()

    if args.list or not args.scenario:
        print('scenarios:')
        for name, spec in SCENARIOS.items():
            print(f'  {name:<20} {spec["needs"]}')
        print('\nresults: ' + str(RESULTS))
        return 0 if args.list else 2

    if args.scenario not in SCENARIOS:
        print(f'unknown scenario {args.scenario!r}; --list for the set', file=sys.stderr)
        return 2

    spec = SCENARIOS[args.scenario]
    shim = None if (args.no_shim or spec['storage'] == 'any') else dict(spec.get('shim') or {})
    if shim is not None:
        if args.ms_per_call is not None:
            shim['MS_PER_CALL'] = args.ms_per_call
        if args.ms_per_16k is not None:
            shim['MS_PER_16K'] = args.ms_per_16k

    prepare_fixtures()
    if args.scenario == 's39-ratio':
        ensure_library(WALK_LIBRARY_NAME, WALK_VOLUME_DIR)
    installed = riglib.install_plugin(
        version=args.plugin_version,
        source=pathlib.Path(args.plugin_source) if args.plugin_source else None)
    log(f'[rig] plugin build installed: {installed}')

    started = time.time()
    rig = riglib.Rig(shim=shim)
    record = {'scenario': args.scenario, 'when': time.strftime('%Y-%m-%d %H:%M:%S'),
              'plugin': installed.name, 'shim': shim, 'note': args.note,
              'repo_head': head_commit(), 'assertions': [], 'passed': False, 'observations': []}
    try:
        rig.start()
        rig.wait_ready()
        ctx = {}
        assertions = spec['run'](rig, args, ctx)
        record['assertions'] = [{'check': c, 'passed': bool(p), 'detail': str(d)}
                                for c, p, d in assertions]
        record['observations'] = list(ctx.get('observations', []))
        record['batch'] = ctx.get('batch')
    except Exception as exc:  # a scenario that cannot run is a failure, and says why
        record['assertions'] = [{'check': 'the scenario ran at all', 'passed': False,
                                 'detail': f'{type(exc).__name__}: {exc}'}]
    finally:
        tail = rig.log_text(rig._log_offset if rig._log_offset else 0)
        if tail:
            record['log_tail'] = tail.splitlines()[-40:]
        if not args.keep_rig:
            rig.stop()
        record['seconds'] = round(time.time() - started, 1)
        record['passed'] = all(a['passed'] for a in record['assertions']) and bool(record['assertions'])

    for item in record['assertions']:
        log(('  PASS  ' if item['passed'] else '  FAIL  ') + item['check'] +
            (f" - {item['detail']}" if item['detail'] else ''))
    RESULTS.parent.mkdir(parents=True, exist_ok=True)
    history = json.loads(RESULTS.read_text()) if RESULTS.exists() else []
    history.append(record)
    RESULTS.write_text(json.dumps(history, indent=2) + '\n')
    log(f"[rig] {record['scenario']}: {'PASSED' if record['passed'] else 'FAILED'} "
        f"in {record['seconds']}s ({len(record['assertions'])} assertion(s)) -> {RESULTS}")
    return 0 if record['passed'] else 1


def head_commit() -> str:
    try:
        return subprocess.run(['git', 'rev-parse', '--short', 'HEAD'], cwd=REPO, text=True,
                              capture_output=True, check=True).stdout.strip()
    except Exception:
        return '?'


if __name__ == '__main__':
    raise SystemExit(main())
