#!/usr/bin/env python3
"""Report a rescaled result as a rescale, not as a half-file shift.

The fixture's log line for a corrected subtitle read "change=+55388 ms", because that measurement compares the
subtitle the user had (PAL-timed) with the corrected file - two differently scaled timelines, so their median
difference is about half the film's drift. The alignment had done nothing of the sort: the plugin rescaled the
subtitle onto the reference's timeline, exactly what framerate correction is for. A user reading that line would
think the job went wrong, so the outcome now says what happened.
"""
import pathlib

p = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = p.read_text()
old = """            var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
            if (job.OutputPath is not null)
            {
                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
            }"""
new = """            var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
            if (job.OutputPath is not null)
            {
                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
                if (engineInput != subtitleInputPath)
                {
                    // The subtitle the user had and the corrected file are on differently scaled timelines, so
                    // describing the difference between them reports about half the film's drift ("change=+55388 ms")
                    // for a correction that did what it was asked to. Say what was done instead: the factor, and the
                    // alignment's own change measured on the timeline the engine worked in.
                    var before = ParseSrtCueStarts(subtitleInputPath);
                    var after = ParseSrtCueStarts(engineInput);
                    var factor = before is { Count: > 2 } && after is { Count: > 2 }
                        ? (after[^1] - after[0]) / (before[^1] - before[0])
                        : 1.0;
                    var aligned = DescribeSyncChange(engineInput, job.OutputPath);
                    job.Outcome = $"stretched to {factor:0.#####}x onto the reference's timeline"
                        + (string.IsNullOrEmpty(aligned) ? string.Empty : ", " + aligned);
                }
            }"""
assert old in t, 'outcome block not found'
p.write_text(t.replace(old, new, 1))
print('a rescaled result reports the stretch and the alignment')
