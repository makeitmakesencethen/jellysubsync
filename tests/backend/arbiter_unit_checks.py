#!/usr/bin/env python3
"""Finish the arbiter: keep "the reference is off" from firing when neither side is off, and give it unit checks.

The C# harness is generated from tests/run_checks.py, so the arbiter's cases go there as Check(...) assertions -
that is the verification, and it runs with the normal suite. (The fixture route was dropped: the plugin writes its
reference per run and removes it with the run, so a fixture cannot make the plugin's own reference PAL-timed. The
decision itself needs no fixture.)
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

old = """            if (IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds))
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (Math.Abs(targetSpan / videoSeconds - 1.0) <= 0.03)
            {"""
new = """            var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
            if (IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds))
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (!referenceOnVideo)
            {"""
assert old in t
t = t.replace(old, new, 1)
svc.write_text(t)
print('the caller tells "the reference is off" from "neither side is off"')

checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()

old = """           and 'targetOnVideo && !referenceOnVideo' in service_source"""
new = """           and 'IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds)' in service_source"""
assert old in c
c = c.replace(old, new, 1)

# unit checks for the arbiter, next to the other timing-rule checks in the generated C# harness
anchor = """    string.Join(" ", SubSyncService.FramerateArgs(true, true)) == "--gss"""
idx = c.index(anchor)
line_end = c.index(");", idx) + 2
addition = """

// Which side is off the file's timeline decides whether a framerate rescale happens at all. A subtitle timed for
// PAL on a 23.976 fps file spans ~0.959 of the film; a reference taken from such a release does too. Rescaling the
// subtitle onto a mis-timed reference would move a correct subtitle off the video, and in a bulk run every
// subtitle of that file uses the same reference.
Check("a PAL-timed subtitle is the side to rescale", SubSyncService.IsTargetOffTheVideo(0.959 * 3000, 3000, 3000));
Check("a correct subtitle against a PAL-timed reference is left alone", !SubSyncService.IsTargetOffTheVideo(3000, 0.959 * 3000, 3000));
Check("two subtitles that both match the file are not rescaled", !SubSyncService.IsTargetOffTheVideo(3000, 2990, 3000));
Check("an unusable duration leaves the decision to the pair rule", SubSyncService.IsTargetOffTheVideo(3000, 0.959 * 3000, 30));"""
c = c[:line_end] + addition + c[line_end:]
checks.write_text(c)
print('the harness now checks the arbiter')
