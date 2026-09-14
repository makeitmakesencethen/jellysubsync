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
