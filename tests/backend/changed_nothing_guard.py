#!/usr/bin/env python3
"""Measure "changed nothing" against the subtitle the user has, not against the engine's input.

After the plugin rescales a framerate-mismatched subtitle, the engine's input and its output are identical by
definition - so the guard that suppresses an unchanged sidecar concluded "the sync changed nothing (+0 ms
offset) - no sidecar written" while the user's own PAL-timed file was left as it was. The alignment guards keep
comparing what the engine was given, which is what they are about.
"""
import pathlib

p = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = p.read_text()
old = '''            if (measured is { IsNoChange: true } noChange)
            {
                _logger.LogInformation(
                    "Sync job {JobId}: the sync changed nothing ({Change}) \\u2014 no sidecar written",'''
new = '''            // "Changed nothing" is about the subtitle the user has, so it is measured against that: when the
            // plugin rescaled a framerate-mismatched subtitle, the engine's input and its output are identical by
            // definition, and comparing those two reported "+0 ms offset - no sidecar written" while the user's
            // own file was still PAL-timed. The alignment guards above keep using what the engine was given.
            var changedForUser = engineInput == subtitleInputPath
                ? measured
                : MeasureSyncChange(subtitleInputPath, tempOutput);

            if (changedForUser is { IsNoChange: true } noChange)
            {
                _logger.LogInformation(
                    "Sync job {JobId}: the sync changed nothing ({Change}) \\u2014 no sidecar written",'''
assert old in t, 'guard not found'
p.write_text(t.replace(old, new, 1))
print('patched: the changed-nothing guard measures against the user subtitle')
