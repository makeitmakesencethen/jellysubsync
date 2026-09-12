#!/usr/bin/env python3
"""Say what round 1 is doing.

The converging design logs the refinement rounds ("the check asked for -29150 ms more … round 2") and the acceptance,
but not the first one - so the fixture's assertion on that line failed even though the run was correct (it converged
to -120006 ms and wrote the subtitle). One line per round 1 restores it and reads better in the log anyway.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()
old = """                for (var round = 1; round <= 3 && !accepted; round++)
                {
                    SafeDelete(verifyOutput);"""
new = """                for (var round = 1; round <= 3 && !accepted; round++)
                {
                    if (round == 1)
                    {
                        _logger.LogInformation(
                            "Sync job {JobId}: the result reached the {Ceiling} s ceiling - applying the measured {Shift} ms and checking it against the audio",
                            job.Id,
                            config.MaxOffsetSeconds,
                            onCeiling.ShiftMs);
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the alignment wanted {onCeiling.ShiftMs} ms, at or past the "
                            + $"{config.MaxOffsetSeconds} s ceiling \\u2014 applying that shift and checking it against the "
                            + "film's audio (the engine's search is not widened: a wider window is how a wrong lock gets in)");
                    }

                    SafeDelete(verifyOutput);"""
assert old in t, 'round loop not found'
svc.write_text(t.replace(old, new, 1))
print('round 1 announces itself again')
