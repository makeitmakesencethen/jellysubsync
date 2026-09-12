#!/usr/bin/env python3
"""Say the framerate option (and the two worst neighbours) in plain words.

The old text explained the engine's internals ("the engine infers a framerate ratio from the subtitle's own
span") and leaked a flag name ("Enable --gss"). What a user needs to decide is the trade-off, not the mechanics.
"""
import pathlib

page = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.html')
t = page.read_text()

edits = [
    # 1. framerate correction: the trade-off in three plain sentences, and what happens either way
    ('<div class="fieldDescription checkboxFieldDescription">Off by default, and that is the safe setting: '
     'the engine infers a framerate ratio from the subtitle\'s own span, so a release with a slightly longer '
     'runtime gets time-scaled and the whole file drifts. Only enable this for subtitles you know come from a '
     'different framerate.</div>',
     '<div class="fieldDescription checkboxFieldDescription">Let the sync stretch the timings to fix a '
     'framerate mismatch, e.g. a subtitle timed for PAL 25 fps on a 23.976 fps file. Leave it off when the '
     'subtitle is simply from a different cut: that looks identical to the engine, and stretching it leaves the '
     'end of the film minutes out of step. Either way nothing is stretched silently \u2014 a result that is not a '
     'real framerate pair is refused with the numbers.</div>'),

    # 2. golden-section: no flag name, and when it is worth using
    ('<div class="fieldDescription checkboxFieldDescription">Enable --gss to find the optimal framerate ratio '
     'between video and subtitles.</div>',
     '<div class="fieldDescription checkboxFieldDescription">Searches harder for the exact stretch factor. '
     'Slower, and only useful together with the option above.</div>'),

    # 3. speech detection: what it decides, not which component does it
    ('<div class="fieldDescription">The VAD method used by ffsubsync for speech detection.</div>',
     '<div class="fieldDescription">How the engine decides which parts of the audio are speech. The default '
     'works for both film and TV.</div>'),
]

for old, new in edits:
    if old not in t:
        raise SystemExit('not found, nothing changed: %r...' % old[:70])
    t = t.replace(old, new, 1)

page.write_text(t)
print('settings wording simplified in %d places' % len(edits))
for line in t.splitlines():
    if 'framerate pair is refused' in line or 'stretch factor' in line:
        print('  now:', ' '.join(line.strip().split())[:120])
