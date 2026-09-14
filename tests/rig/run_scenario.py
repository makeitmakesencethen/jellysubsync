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


# "volume <key> had nothing measured about it, so it was read once: 16 KB took 231,13 ms - the ceiling
# for that volume is 1 (<why>)". One line carries the whole verdict, so a scenario parses it into fields
# rather than guessing at token positions - the first version of this read the volume key out of
# `line.split()[1]`, which is the timestamp, and so matched nothing at all.
PROBE = re.compile(
    r'volume (\S+) had nothing measured about it, so it was read once: (\d+) KB took ([\d.,]+) ms'
    r' - the ceiling for that volume is (\S+?)(?: \((.*)\))?$')


def log(message: str) -> None:
    print(message, flush=True)


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
        probed = {m.group(1): {'line': ln, 'kb': m.group(2), 'ms': m.group(3),
                               'ceiling': m.group(4), 'why': (m.group(5) or '').strip()}
                  for ln in lines for m in [PROBE.search(ln)] if m}
        if wanted <= set(probed) or time.time() >= deadline:
            break
        time.sleep(3)

    slow, fast = probed.get(slow_key), probed.get(fast_key)
    ctx['observations'] = [v['line'] for v in probed.values()]
    ctx['waited_s'] = round(time.time() - started, 1)
    ctx['probed'] = probed

    samples = re.search(r'(?:median of|from) (\d+) read', slow['line']) if slow else None

    assertions = found + [
        ('both volumes were probed in one run', wanted <= set(probed),
         f'probed {sorted(probed)} in {ctx["waited_s"]}s (wanted {sorted(wanted)})'),
        ('the slow volume\'s verdict names how many reads it stands on', bool(samples),
         f'S41 defect when absent -> {slow["line"] if slow else "(the slow volume was never probed)"}'),
        ('a verdict does not rest on a single read', bool(samples) and int(samples.group(1)) >= 2,
         f'samples={samples.group(1) if samples else "0 (one cold read)"}, '
         f'verdict={slow["ceiling"] + " (" + slow["why"] + ")" if slow else "?"}'),
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


def scenario_s41_steady(rig, args, ctx):
    """S41's acceptance: on a share in its normal state (fabji's measured 10 ms/read, 11 MB/s), the
    shimmed volume must be allowed two walks and the fast volume none, in the same run."""
    return _s41(rig, args, ctx, expect_slow_ceiling='2')


SCENARIOS = {
    'smoke': dict(run=scenario_smoke, storage='any',
                  needs='nothing: it is the harness checking itself (2 volumes, fixtures, log)'),
    's41-thrash-tier': dict(run=scenario_s41_thrash_tier, storage='shim', ms_per_call=231.0, ms_per_16k=0.0,
                            needs='231 ms per read: a share that answers one cold read slowly, from the field'),
    's41-steady': dict(run=scenario_s41_steady, storage='shim', ms_per_call=10.0, ms_per_16k=1.46,
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
    shim = None if (args.no_shim or spec['storage'] == 'any') else {
        'MS_PER_CALL': spec.get('ms_per_call', 231.0) if args.ms_per_call is None else args.ms_per_call,
        'MS_PER_16K': spec.get('ms_per_16k', 0.0) if args.ms_per_16k is None else args.ms_per_16k}

    prepare_fixtures()
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
