#!/usr/bin/env python3
"""Verify a published beta release the way an installer would: manifest, zip, checksum, packaged versions.

Downloads the beta catalog manifest, then the zip it points at, and checks
  * the manifest reports the version that was just cut,
  * the zip's MD5 equals the manifest's checksum (Jellyfin uses MD5 for installs),
  * the packaged meta.json carries the same version,
  * the packaged DLL carries the same assembly version,
  * the bundled ffsubsync binary is inside the zip.

Usage: python3 tests/backend/verify_release.py 2.0.43.0
"""
import hashlib
import io
import json
import re
import sys
import urllib.request
import zipfile

BASE = 'https://makeitmakesencethen.github.io/jellysubsync/beta'


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

    print()
    if failures:
        for f in failures:
            print('FAIL  ' + f)
        return 1
    print(f'OK  {want} is published, the zip matches its checksum, and the packaged versions agree')
    return 0


if __name__ == '__main__':
    sys.exit(main())
