#!/usr/bin/env python3
"""Framerate correction works and is on by default, with the option to disable it.

The option was opt-in because a subtitle from a longer cut looks numerically identical to one from a different
framerate. The user's call: a framerate mismatch is the common case in his library, so it works out of the box
and turning it off stays possible for the ambiguous one.

Also drops "(advanced)" from the label: it is no longer an advanced choice, it is the default.
"""
import pathlib

cfg = pathlib.Path('Jellyfin.Plugin.SubSync/Configuration/PluginConfiguration.cs')
t = cfg.read_text()

old_doc_start = t.index("    /// <remarks>\n    /// Off by default, deliberately.")
old_doc_end = t.index("    public bool FixFramerate { get; set; } = false;")
new_doc = '''    /// <remarks>
    /// On by default: a subtitle timed for a different framerate (PAL 25 against a 23.976 fps release - the
    /// common case in a European library) is stretched onto the video's timeline. That now works on both
    /// reference paths: on the audio path the engine does the rescale, and on the subtitle-reference path the
    /// plugin does it itself, because a reference subtitle has no frame rate for the engine to read (see
    /// RescaleOntoReferenceSpan) - before that, a PAL-timed subtitle aligned against a sibling subtitle came out
    /// as a pure shift of about half the film's drift and the reference ceiling refused it.
    ///
    /// The ambiguity this default accepts: ffsubsync infers a ratio from the reference duration against the
    /// subtitle's own span, so a subtitle that merely spans a few percent more - a release with a longer credits
    /// roll, or an extra scene - reads as a framerate mismatch as well. Measured with the bundled engine on a
    /// 45-minute file: a span 4.17% too long became a 0.960x scale, a -51.9 s shift and -104 s of drift. Turning
    /// this off means offsets only, bounded by <see cref="MaxOffsetSeconds"/>, and is the escape hatch for that
    /// case. Either way nothing is stretched silently: a result that is not a real framerate pair is refused
    /// rather than written.
    /// </remarks>
'''
t = t[:old_doc_start] + new_doc + t[old_doc_end:]
t = t.replace("    public bool FixFramerate { get; set; } = false;",
              "    public bool FixFramerate { get; set; } = true;", 1)
cfg.write_text(t)
print('config: FixFramerate defaults to true, remarks match')

page = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.html')
h = page.read_text()
old = '<span>Correct framerate mismatch (advanced)</span>'
assert old in h
h = h.replace(old, '<span>Correct framerate mismatch</span>', 1)

old_desc_start = h.index('<div class="fieldDescription checkboxFieldDescription">Let the sync stretch')
old_desc_end = h.index('</div>', old_desc_start)
new_desc = ('<div class="fieldDescription checkboxFieldDescription">On by default: a subtitle timed for a '
            'different framerate (PAL 25 on a 23.976 fps file) is stretched onto the video\'s timeline, whether '
            'the alignment uses the audio or another subtitle. Turn it off when a subtitle from a different cut '
            'gets stretched instead \u2014 that case looks the same in the numbers, and the stretch then leaves the '
            'end of the film minutes out of step. Nothing is stretched silently either way: a result that is not a '
            'real framerate pair is refused with the numbers.')
h = h[:old_desc_start] + new_desc + h[old_desc_end:]
page.write_text(h)
print('page: label loses "(advanced)", description states the new default')

checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()
for old, new in (
    ('FixFramerate { get; set; } = false', 'FixFramerate { get; set; } = true'),
    ('Correct framerate mismatch (advanced)', 'Correct framerate mismatch'),
):
    if old in c:
        c = c.replace(old, new)
        print('checks: updated expectation for %r' % old)
checks.write_text(c)
