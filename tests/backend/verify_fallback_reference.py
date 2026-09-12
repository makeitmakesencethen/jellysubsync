#!/usr/bin/env python3
"""The check must survive the speech cache being gone.

The fixture's refusal says exactly what went wrong, thanks to the engine tail that went in with this release:

    REFUSED: … the check did not run (exit 1) · engine said: ERROR unable to read reference

The alignment that measured the shift ran against the file's audio through the speech cache, and by the time the
check ran, that cached reference (a link the plugin drops after harvesting) was gone - so the engine could not read
it. The check now falls back to the media file itself when its first attempt cannot read the reference: slower for
that one run, but correct, and only when the cache has already been released.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

old = """                var residual = verifyExit == 0 && File.Exists(verifyOutput)
                    ? MeasureSyncChange(shiftedInput, verifyOutput)
                    : null;
                var residualRatio = residual?.Ratio ?? 1.0;
                var residualShift = residual?.ShiftMs ?? 0;"""
new = """                if (verifyExit != 0 && speechKey is not null)
                {
                    // The reference the alignment used may already be gone (the plugin drops the speech cache link
                    // once it has harvested it), which the engine reports as "unable to read reference". The film
                    // itself is always readable, so the check falls back to it: one more decode, correct either way.
                    var directArgs = BuildFfSubSyncArgs(
                        config, videoPath, shiftedInput, verifyOutput, tempDir, serializeSpeech: false, referenceStream: null);
                    foreach (var flag in FramerateArgs(false, false))
                    {
                        if (!directArgs.Contains(flag))
                        {
                            directArgs.Add(flag);
                        }
                    }

                    directArgs.Remove("--gss");
                    SafeDelete(verifyOutput);
                    verifyExit = await RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe,
                        directArgs,
                        tempDir,
                        line =>
                        {
                            lock (verifyErrors)
                            {
                                verifyErrors.Add(line);
                                if (verifyErrors.Count > 8)
                                {
                                    verifyErrors.RemoveAt(0);
                                }
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    PluginLog.Info(
                        $"[{job.Id}] offsets: the stored speech reference was no longer readable, so the check ran "
                        + $"against the file itself instead (exit={verifyExit})");
                }

                var residual = verifyExit == 0 && File.Exists(verifyOutput)
                    ? MeasureSyncChange(shiftedInput, verifyOutput)
                    : null;
                var residualRatio = residual?.Ratio ?? 1.0;
                var residualShift = residual?.ShiftMs ?? 0;"""
assert old in t, 'verification block not found'
t = t.replace(old, new, 1)
svc.write_text(t)
print('the check falls back to the file itself when the cached reference is gone')
