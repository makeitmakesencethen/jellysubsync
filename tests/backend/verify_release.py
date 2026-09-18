#!/usr/bin/env python3
"""Verify a published beta release the way an installer would: manifest, zip, checksum, packaged versions.

Downloads the beta catalog manifest, then the zip it points at, and checks
  * the manifest reports the version that was just cut,
  * the zip's MD5 equals the manifest's checksum (Jellyfin uses MD5 for installs),
  * the packaged meta.json carries the same version,
  * the packaged DLL carries the same assembly version,
  * the packaged DLL carries the structural refactor's markers (the classes and methods that only exist
    after the RunSyncJob split and the SubSyncService split, and the absence of the two members that were
    deleted), so a stale or pre-refactor artifact cannot pass as this release,
  * the bundled ffsubsync binary is inside the zip.

The Phase 2 partial-class split is deliberately not a marker here: a partial class is one class, so the
assembly is byte-for-byte what it would be with the code in one file - the split's own evidence is the
suite, the mutation drivers and the commit history, not the binary. The markers below are the ones the
binary can actually witness (Phase 1's real classes and the RunSyncJob methods).

Usage: python3 tests/backend/verify_release.py 2.0.61.0
"""
import hashlib
import io
import json
import re
import sys
import urllib.request
import zipfile

BASE = 'https://makeitmakesencethen.github.io/jellysubsync/beta'

# Names the refactored build must contain, and names the dead-code removals must have taken away.
# Metadata strings live UTF-8 in the assembly's #Strings heap, so a byte search is the right probe.
MARKERS_PRESENT = [
    # Phase 1: clusters that left SubSyncService as real classes
    'FfSubSyncEngine', 'SubSyncProcesses', 'AlignmentMetrics', 'MediaStreamMap', 'SyncedTargetNaming',
    # the RunSyncJob phases: the names the phases gave the code they pulled out
    'ResolveReferenceAsync', 'RunEngineAttemptAsync', 'WriteSyncedSubtitleAsync', 'PrepareAudioReferenceAsync',
    'DescribeCompletedSync', 'AnnounceCompletedAsync', 'MarkCancelled', 'FailJobAndRollBack', 'CleanUpAfterJob',
    # 2.0.62: the flag that carries "this end is the reader's own guess" as a fact rather than letting the
    # renderer infer it from a 2 000 ms length (F19). It is a member name, so it is UTF-8 metadata and this
    # byte search is the right probe. F21's fix is a try/finally around a stream capture and adds no name and
    # no string, so the binary cannot witness it - what the packaged assembly proves there is that it is the
    # same build the suite ran (assembly version + md5), and the behaviour is what the suite's F21 checks drive.
    'DurationGuessed',
]
MARKERS_ABSENT = ['RunCapturedAsync', 'LiveProcessCount']



def main() -> int:
    want = sys.argv[1] if len(sys.argv) > 1 else '2.0.43.0'
    failures = []

    manifest = json.load(urllib.request.urlopen(f'{BASE}/manifest.json', timeout=60))
    plugin = manifest[0]
    version = plugin['versions'][0]['version']
    checksum = plugin['versions'][0]['checksum']
    zip_url = plugin['versions'][0]['sourceUrl']
    print(f'manifest: {plugin["name"]} version={version} checksum={checksum}')
    print(f'          {zip_url}')
    if version != want:
        failures.append(f'manifest reports {version}, expected {want}')

    blob = urllib.request.urlopen(zip_url, timeout=300).read()
    digest = hashlib.md5(blob).hexdigest()
    print(f'zip: {len(blob)} bytes, md5={digest}')
    if digest != checksum:
        failures.append(f'zip md5 {digest} != manifest checksum {checksum}')

    with zipfile.ZipFile(io.BytesIO(blob)) as zf:
        names = zf.namelist()
        meta = json.loads(zf.read('meta.json').decode('utf-8'))
        dll = zf.read('Jellyfin.Plugin.SubSync.dll')
        bundled = [n for n in names if n.startswith('ffsubsync/linux-x64/')]
        print(f'packaged: meta.json version={meta["version"]} guid={meta["guid"]}')
        print(f'          dll={len(dll)} bytes, ffsubsync files={len(bundled)}, entries={len(names)}')
        if meta['version'] != want:
            failures.append(f'packaged meta.json says {meta["version"]}, expected {want}')
        for candidate in (want, want.removesuffix('.0')):
            if candidate.encode('utf-16-le') in dll:
                print(f'          dll carries {candidate}')
                break
        else:
            failures.append(f'dll does not carry {want}')
        if not any(n.endswith('ffsubsync') for n in bundled):
            failures.append('the bundled ffsubsync binary is missing from the zip')
        if 'Jellyfin.Plugin.SubSync.dll' not in names:
            failures.append('the plugin dll is missing from the zip')

        missing = [m for m in MARKERS_PRESENT if m.encode('utf-8') not in dll]
        still_there = [m for m in MARKERS_ABSENT if m.encode('utf-8') in dll]
        print(f'          refactor markers: {len(MARKERS_PRESENT) - len(missing)}/{len(MARKERS_PRESENT)} present, '
              f'{len(MARKERS_ABSENT) - len(still_there)}/{len(MARKERS_ABSENT)} removals confirmed')
        if missing:
            failures.append('the dll does not carry the refactor: ' + ', '.join(missing))
        if still_there:
            failures.append('the dll still carries removed code: ' + ', '.join(still_there))

    print()
    if failures:
        for f in failures:
            print('FAIL  ' + f)
        return 1
    print(f'OK  {want} is published, the zip matches its checksum, and the packaged versions agree')
    return 0


if __name__ == '__main__':
    sys.exit(main())
