#!/usr/bin/env python3
"""2.0.20, part 1: the offset ceiling is a search window, so give it room and stop re-inventing the shift.

The user's file needs ~112 s and the ceiling was 60 s. ffsubsync cannot see an answer outside its window, so with
60 s it returned the best wrong one (56 s, then 60 s) - and the rigid-shift machinery built on top of that number
applied a median displacement of a re-timed timeline as if it were a pure shift, which is wrong by construction.

Changes:
  * MaxOffsetSeconds default 60 -> 180 (the value that made the user's file sync was 150).
  * The rigid-shift path is gone (ShiftSrtBy, the refinement rounds, and the convergence logs).
  * A result that reaches the ceiling gets one more attempt with a wider window, and its result is accepted only when
    it does *not* reach the wider ceiling either - a definitive answer, not a clamp. That result then passes one
    alignment against the film's audio, and if that asks for a large shift the job refuses with both numbers.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

START = "            // The engine clamps the shift at the configured ceiling"
END = "            // Nothing destructive is ever written: a measured rescale that was not asked for (or that"
start = t.index(START)
end = t.index(END, start)
assert 'shifted-verify.srt' in t[start:end], 'that is not the offset-ceiling block'

new_block = '''            // This ceiling is ffsubsync's search window, not a safety limit: an answer outside it cannot be seen, and
            // the engine then returns the best wrong one - which is how a subtitle needing ~112 s came back as 56 s.
            // So a result that reaches the window gets one more alignment with a wider one, and that result is only
            // accepted when it is *not* pinned to the wider window either. A definitive answer, then one alignment
            // against the film's audio as a last check: anything a wide window matched wrongly shows up there as a
            // large remaining shift, and the job refuses with both numbers instead of writing it.
            var ceilingMs = config.MaxOffsetSeconds * 1000.0;
            if (!wideAllowanceApplied && measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var wideSeconds = Math.Max(config.MaxOffsetSeconds * 2, 300);
                var wideLimitMs = wideSeconds * 1000.0;
                var wideOutput = Path.Combine(tempDir, "wide-window.srt");
                SafeDelete(wideOutput);
                var wideArgs = BuildFfSubSyncArgs(
                    config, referenceArg, engineInput, wideOutput, tempDir, serializeSpeech, referenceStream);
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] =
                    wideSeconds.ToString(CultureInfo.InvariantCulture);

                _logger.LogInformation(
                    "Sync job {JobId}: the result reached the {Window} s search window - aligning again with {Wide} s",
                    job.Id,
                    config.MaxOffsetSeconds,
                    wideSeconds);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the alignment reached the {config.MaxOffsetSeconds} s search window "
                    + $"(it measured {onCeiling.ShiftMs} ms, which a window that size cannot be trusted to have found) "
                    + $"\\u2014 aligning again with {wideSeconds} s and checking the result against the film's audio");

                var wideErrors = new List<string>();
                var wideExit = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe,
                    wideArgs,
                    tempDir,
                    line =>
                    {
                        lock (wideErrors)
                        {
                            wideErrors.Add(line);
                            if (wideErrors.Count > 8)
                            {
                                wideErrors.RemoveAt(0);
                            }
                        }
                    },
                    cancellationToken).ConfigureAwait(false);

                var wideChange = wideExit == 0 && File.Exists(wideOutput)
                    ? MeasureSyncChange(engineInput, wideOutput)
                    : null;

                if (wideChange is { } wider && Math.Abs(wider.ShiftMs) < wideLimitMs - 500)
                {
                    // The wider window produced an answer inside itself: the engine is not clamped any more.
                    var verifyOutput = Path.Combine(tempDir, "wide-check.srt");
                    SafeDelete(verifyOutput);
                    var verifyArgs = BuildFfSubSyncArgs(
                        config, referenceArg, wideOutput, verifyOutput, tempDir, serializeSpeech, referenceStream);
                    // A check, not a search: the configured window and no rescaling.
                    verifyArgs[verifyArgs.IndexOf("--max-offset-seconds") + 1] =
                        config.MaxOffsetSeconds.ToString(CultureInfo.InvariantCulture);
                    foreach (var flag in FramerateArgs(false, false))
                    {
                        if (!verifyArgs.Contains(flag))
                        {
                            verifyArgs.Add(flag);
                        }
                    }

                    verifyArgs.Remove("--gss");

                    var verifyExit = await RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe, verifyArgs, tempDir, null, cancellationToken).ConfigureAwait(false);
                    var residual = verifyExit == 0 && File.Exists(verifyOutput)
                        ? MeasureSyncChange(wideOutput, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (verifyExit == 0
                        && AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                    {
                        wideAllowanceApplied = true;
                        tempOutput = wideOutput;
                        measured = wider;
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the {wider.ShiftMs} ms result holds against the film's audio "
                            + $"(a further {residualShift} ms, no rescale) \\u2014 writing it");
                        _logger.LogInformation(
                            "Sync job {JobId}: the wider-window result holds against the audio ({Shift} ms more)",
                            job.Id,
                            residualShift);
                    }
                    else
                    {
                        var why = verifyExit != 0
                            ? $"the check did not run (exit {verifyExit})"
                            : $"the film's audio still asked for {residualShift} ms more (ratio {residualRatio:0.0000})";
                        _logger.LogWarning(
                            "Sync job {JobId}: refusing after the wider window ({Why}) — nothing written",
                            job.Id,
                            why);
                        PluginLog.Info(
                            $"job {job.Id} REFUSED: this subtitle needed {onCeiling.ShiftMs} ms with a "
                            + $"{config.MaxOffsetSeconds} s window and {wider.ShiftMs} ms with {wideSeconds} s, and {why}; "
                            + $"nothing written, source untouched, file={video.Path}");
                        job.Status = SyncJobStatus.Failed;
                        job.Phase = "Refused";
                        job.Error = $"refused: this subtitle is further out than the {config.MaxOffsetSeconds} s search "
                            + $"window ({onCeiling.ShiftMs} ms reached it), the {wideSeconds} s window measured "
                            + $"{wider.ShiftMs} ms, and that did not hold up against the film's audio: {why}. Nothing "
                            + "was written.";
                        job.Progress = 1.0;
                        job.FinishedAtUtc = DateTime.UtcNow;
                        job.OutputPath = null;
                        SafeDelete(tempOutput);
                        return;
                    }
                }
                else
                {
                    string engineTail;
                    lock (wideErrors)
                    {
                        engineTail = wideErrors.Count == 0
                            ? string.Empty
                            : " · engine said: " + string.Join(" | ", wideErrors).Trim();
                    }

                    var detail = wideChange is { } stillClamped
                        ? $"the {wideSeconds} s window also reached its limit ({stillClamped.ShiftMs} ms)"
                        : $"the {wideSeconds} s window produced nothing (exit {wideExit}){engineTail}";
                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the wider window ({Detail}) — nothing written",
                        job.Id,
                        detail);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle is further out than the plugin is searching "
                        + $"({detail}); raise \\"Maximum offset\\" and run it again, or sync it by hand. Nothing "
                        + $"written, source untouched, file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: {detail}. Raise \\"Maximum offset\\" in the plugin settings (it is the search "
                        + "window the alignment may look in) and run it again. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }

'''
t = t[:start] + new_block + t[end:]

# the rigid-shift helper goes with it
h_start = t.index("    /// <summary>\n    /// Writes a copy of a subtitle with every timestamp moved by a fixed amount.")
h_end = t.index('    /// <summary>\n    /// Reads one SRT timestamp ("00:01:02,345") as milliseconds.')
removed = t[h_start:h_end]
assert 'ShiftSrtBy' in removed
t = t[:h_start] + t[h_end:]
print('removed the rigid-shift helper (%d lines) and rewrote the window handling' % removed.count("\n"))

svc.write_text(t)
