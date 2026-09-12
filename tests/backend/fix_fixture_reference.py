#!/usr/bin/env python3
"""Build the fixture's reference from the track the plugin actually used, not from its cache.

The plugin writes its reference per run and removes it with the run (that lifetime is a fix in its own right), so a
fixture that waits for the run to finish finds nothing. The probe run's log names the track it chose and the cue
count, and the same text is in the container: take it from there, and check the cue count against the log so a
wrong track cannot pass silently.
"""
import pathlib

p = pathlib.Path('tests/backend/reference_framerate.py')
t = p.read_text()

old = '''REF_CACHE = pathlib.Path("/opt/data/jf12test/cache/subsync/ref")


def cached_reference_text():
    """The reference the plugin actually uses for this file: the newest one under its cache.

    Picking a track myself got this wrong (the plugin chose s:1 while the sidebar of tracks suggested s:3), and a
    fixture built from the wrong track measures a span ratio that is not the PAL pair under test.
    """
    files = [p for p in REF_CACHE.rglob("*.ref.srt")]
    if not files:
        raise SystemExit("no reference in the plugin's cache - run any sync first")
    newest = max(files, key=lambda p: p.stat().st_mtime)
    return newest, newest.read_text(encoding="utf-8", errors="replace")
'''
new = '''TRACK_RE = re.compile(r"reference: method=\\w+ cues=(\\d+) track=s:(\\d+)")


def reference_from_probe(log_lines):
    """The container track the plugin chose as this file's reference, taken from its own log line.

    Picking a track by hand got this wrong before (the plugin chose s:1 where the stream list suggested s:3), and the
    cache cannot be used: the plugin removes the reference with the run it belongs to. The probe's log names the track
    and the cue count, and the same text comes out of the container - the cue count is checked so a wrong track fails
    loudly instead of quietly measuring a span ratio that is not the PAL pair under test.
    """
    for line in log_lines:
        m = TRACK_RE.search(line)
        if m:
            cues, track = int(m.group(1)), int(m.group(2))
            text = embedded_text(track)
            got = text.count("-->")
            if got != cues:
                raise SystemExit("track s:%d has %d cues but the plugin's reference had %d"
                                 % (track, got, cues))
            return track, text
    raise SystemExit("the probe run did not say which track it used as the reference: %s"
                     % " / ".join(l[:120] for l in log_lines[:4]))
'''
assert old in t
t = t.replace(old, new, 1)

t = t.replace("import json\nimport os", "import json\nimport os\nimport re", 1)

old = '''    ref_file, reference = cached_reference_text()
    ref_cues = cue_starts_from_text(reference)
    BACKUP.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    SIDECAR.write_text(rescale_srt(reference, PAL), encoding="utf-8")
    print("reference: %s, %d cues; the sidecar is the same text at %.5fx" % (ref_file, len(ref_cues), PAL))'''
new = '''    track, reference = reference_from_probe(probe.get("log", []))
    ref_cues = cue_starts_from_text(reference)
    BACKUP.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    SIDECAR.write_text(rescale_srt(reference, PAL), encoding="utf-8")
    print("reference: the plugin's own track s:%d, %d cues; the sidecar is the same text at %.5fx"
          % (track, len(ref_cues), PAL))'''
assert old in t
t = t.replace(old, new, 1)

t = t.replace('results = {"item": item_id, "reference": str(ref_file), "reference_cues": len(ref_cues), "pal": PAL,',
              'results = {"item": item_id, "reference_track": track, "reference_cues": len(ref_cues), "pal": PAL,', 1)
p.write_text(t)
print('the fixture takes the reference from the track the plugin used')
