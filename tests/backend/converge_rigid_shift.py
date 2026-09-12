#!/usr/bin/env python3
"""Let the rigid shift converge: apply, check, apply what the check asks for, check again (at most three times).

The fixture showed why one rigid shift is not enough, and it showed it usefully: the engine's first measurement was
-90856 ms for a subtitle that is -120000 ms out, so after applying its number the film's audio still wanted -29150 ms
more. The check refused - correct, since a 29 s residual is not a sync - but the answer was reachable.

So the check now refines: whatever the film's audio asks for after a rigid shift is applied on top of it (still
rigidly, still no widened search), up to three rounds. Each round is verified by the same normal-allowance alignment,
so a wrong lock - which does not converge - still ends in a refusal rather than a written subtitle.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

START = """                var verifyOutput = Path.Combine(tempDir, "shifted-verify.srt");"""
END = """                    SafeDelete(tempOutput);
                    return;
                }
            }
"""
start = t.index(START)
end = t.index(END, start) + len(END)
assert 'shifted-verify.srt' in t[start:end] and 'wideAllowanceApplied = true' in t[start:end], 'that is not the check block'

new_block = '''                var verifyOutput = Path.Combine(tempDir, "shifted-verify.srt");
                var totalShift = onCeiling.ShiftMs;
                var shifted = shiftedInput;
                var accepted = false;
                var lastWhy = string.Empty;

                // Three rounds at most: apply what the film's audio asks for, rigidly, and check again.
                for (var round = 1; round <= 3 && !accepted; round++)
                {
                    SafeDelete(verifyOutput);
                    var verifyArgs = BuildFfSubSyncArgs(
                        config, referenceArg, shifted, verifyOutput, tempDir, serializeSpeech, referenceStream);
                    // Verification, not a search: no rescaling, and the configured allowance.
                    foreach (var flag in FramerateArgs(false, false))
                    {
                        if (!verifyArgs.Contains(flag))
                        {
                            verifyArgs.Add(flag);
                        }
                    }

                    verifyArgs.Remove("--gss");

                    var verifyErrors = new List<string>();
                    var verifyExit = await RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe,
                        verifyArgs,
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

                    if (verifyExit != 0 && speechKey is not null)
                    {
                        // The reference the alignment used may already be gone (the plugin drops the speech cache link
                        // once it has harvested it), which the engine reports as "unable to read reference". The film
                        // itself is always readable, so the check falls back to it: one more decode, correct either way.
                        SafeDelete(verifyOutput);
                        var directArgs = BuildFfSubSyncArgs(
                            config, videoPath, shifted, verifyOutput, tempDir, serializeSpeech: false, referenceStream: null);
                        foreach (var flag in FramerateArgs(false, false))
                        {
                            if (!directArgs.Contains(flag))
                            {
                                directArgs.Add(flag);
                            }
                        }

                        directArgs.Remove("--gss");
                        verifyErrors.Clear();
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
                        ? MeasureSyncChange(shifted, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (verifyExit == 0
                        && AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                    {
                        accepted = true;
                        wideAllowanceApplied = true;
                        tempOutput = File.Exists(verifyOutput) ? verifyOutput : shifted;
                        measured = MeasureSyncChange(engineInput, tempOutput);
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the {totalShift} ms shift holds against the film's audio "
                            + $"(a further {residualShift} ms, no rescale) \\u2014 writing it");
                        _logger.LogInformation(
                            "Sync job {JobId}: the applied shift holds against the audio ({Shift} ms more)",
                            job.Id,
                            residualShift);
                        break;
                    }

                    if (verifyExit != 0)
                    {
                        lock (verifyErrors)
                        {
                            lastWhy = $"the check did not run (exit {verifyExit})"
                                + (verifyErrors.Count == 0 ? string.Empty : " · engine said: " + string.Join(" | ", verifyErrors).Trim());
                        }

                        break;
                    }

                    lastWhy = $"the film's audio still asked for {residualShift} ms more (ratio {residualRatio:0.0000})";
                    if (round == 3)
                    {
                        break;
                    }

                    // Apply what the check asked for on top of the rigid shift, and check again.
                    totalShift += residualShift;
                    var next = ShiftSrtBy(engineInput, totalShift, tempDir, job);
                    if (next is null)
                    {
                        lastWhy = "the refined copy of the subtitle could not be written";
                        break;
                    }

                    shifted = next;
                    PluginLog.Info(
                        $"[{job.Id}] offsets: the check asked for {residualShift} ms more, so the subtitle is shifted "
                        + $"by {totalShift} ms in total and checked again (round {round + 1})");
                }

                if (!accepted)
                {
                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the check ({Why}) — nothing written",
                        job.Id,
                        lastWhy);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle needs {onCeiling.ShiftMs} ms, past the "
                        + $"{config.MaxOffsetSeconds} s limit, and {lastWhy}; nothing written, source untouched, "
                        + $"file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: this subtitle is further out than the {config.MaxOffsetSeconds} s limit "
                        + $"(the alignment measured {onCeiling.ShiftMs} ms), and shifting it by the measurement did not "
                        + $"hold up against the film's audio: {lastWhy}. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }
'''
t = t[:start] + new_block + t[end:]
svc.write_text(t)
print('the check now refines the rigid shift, up to three rounds, each verified')
