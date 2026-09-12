#!/usr/bin/env python3
"""Prove the new queue wording renders as text, and that the shipped build carries it."""
import pathlib
import re
import subprocess

page = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.js').read_text()
html = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.html').read_text()

# pull the literals straight out of the source and let node render them
for label, pattern in (
    ('queued fallback', r"return '(Queued [^']*)';"),
    ('spinner markup', r'id="ss-spinner"'),
):
    if label == 'spinner markup':
        print('%-16s %s' % (label, 'present in the page markup: %s' % ('id="ss-spinner"' in html)))
        continue
    m = re.search(pattern, page)
    if not m:
        print('%-16s NOT FOUND' % label)
        continue
    literal = m.group(1)
    out = subprocess.run(['node', '-e', 'process.stdout.write("%s")' % literal], capture_output=True, text=True)
    print('%-16s source %-52r renders %r' % (label, literal, out.stdout))

# what the shipped dll contains (the web files are embedded resources)
dll = pathlib.Path('Jellyfin.Plugin.SubSync/bin/Release/net10.0/Jellyfin.Plugin.SubSync.dll').read_bytes()
for needle in (b'id="ss-spinner"', b'queuedReason', b'ss-spin .8s linear infinite',
               b'Reading subtitles from the video', b'Waiting for a free worker'):
    print('dll has %-38s %s' % (needle.decode(), needle in dll))
