#!/usr/bin/env python3
"""Two things, both found by the reference-path fixture:

1. On the subtitle-reference path the engine's span-based ratio inference has nothing to read: measured on the
   50-minute fixture it turned a 17.4 s shift into a 55.8 s one, which the reference ceiling then refused. The
   plugin decides the rescale on that path (it sees both spans and only accepts a real framerate pair), so the
   inference stays off there whether or not framerate correction is enabled.
2. The fixture has to be built from the reference the plugin actually uses; the newest cached reference is
   exactly that.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()
old = """        foreach (var flag in FramerateArgs(config.FixFramerate, config.UseGoldenSectionSearch))
        {
            args.Add(flag);
        }
"""
new = """        foreach (var flag in FramerateArgs(config.FixFramerate, config.UseGoldenSectionSearch))
        {
            args.Add(flag);
        }

        // When the alignment reference is itself a subtitle, the engine's span-based ratio inference has no frame
        // rate to read: it compares the reference's duration with the subtitle's span and rescales from that.
        // Measured on a 50-minute fixture whose subtitle was one PAL step off the reference, enabling framerate
        // correction this way turned a 17.4 s shift into a 55.8 s one, and the reference ceiling refused the file.
        // The plugin owns the rescale decision on this path - it can see both spans and only accepts a real
        // framerate pair (RescaleOntoReferenceSpan) - so the inference stays out of it either way.
        var referenceIsSubtitle = referenceStream is null
            && videoPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        if (referenceIsSubtitle && !args.Contains("--skip-infer-framerate-ratio"))
        {
            args.Add("--skip-infer-framerate-ratio");
        }
"""
assert old in t
svc.write_text(t.replace(old, new, 1))
print('the subtitle-reference path no longer lets the engine infer a ratio')

test = pathlib.Path('tests/backend/reference_framerate.py')
s = test.read_text()

old = '''def best_embedded_track():
    """The container track the plugin would use as a reference: the one with the most cues."""
    best, text = 0, embedded_text(0)
    for cand in range(1, 4):
        try:
            other = embedded_text(cand)
        except subprocess.CalledProcessError:
            break
        if other.count("-->") > text.count("-->"):
            best, text = cand, other
    return best, text
'''
new = '''REF_CACHE = pathlib.Path("/opt/data/jf12test/cache/subsync/ref")


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
assert old in s
s = s.replace(old, new, 1)

old = '''    # The sidelined subtitle: the reference's own text, timed as if it came from a PAL release.
    track, reference = best_embedded_track()
    ref_cues = cue_starts_from_text(reference)
    BACKUP.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    SIDECAR.write_text(rescale_srt(reference, PAL), encoding="utf-8")
    print("reference: embedded track s:%d, %d cues; the sidecar is the same text at %.5fx"
          % (track, len(ref_cues), PAL))'''
new = '''    # A probe run first: it refreshes the reference the plugin uses for this file, which is the only text the
    # sidecar may be built from for the span ratio to be the PAL pair under test.
    cfg0 = ss.plugin_config()
    cfg0["FixFramerate"] = False
    ss.set_plugin_config(cfg0)
    probe = drive.run_single(item_id, target["Index"], mode="copy", timeout=900, label="framerate-probe")
    print("probe: %s" % probe.get("status_job"))
    ref_file, reference = cached_reference_text()
    ref_cues = cue_starts_from_text(reference)
    BACKUP.write_text(SIDECAR.read_text(encoding="utf-8", errors="replace"), encoding="utf-8")
    SIDECAR.write_text(rescale_srt(reference, PAL), encoding="utf-8")
    print("reference: %s, %d cues; the sidecar is the same text at %.5fx" % (ref_file, len(ref_cues), PAL))'''
assert old in s
s = s.replace(old, new, 1)
s = s.replace('    results = {"item": item_id, "track": track, "reference_cues": len(ref_cues), "pal": PAL,',
              '    results = {"item": item_id, "reference": str(ref_file), "reference_cues": len(ref_cues), "pal": PAL,')
test.write_text(s)
print('the fixture is built from the reference the plugin uses')
