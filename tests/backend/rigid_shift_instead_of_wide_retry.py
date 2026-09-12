#!/usr/bin/env python3
"""Replace the wide-allowance retry with a rigid shift plus a normal-allowance verification.

The retry was wrong on the user's file. Widening the engine's search to 300 s let it lock onto a different part of
the audio (~56 s instead of the ~94 s the first pass measured), and the check I paired with it re-ran the *same*
configuration, so it agreed with itself. The user's result was wildly wrong from the first line.

What replaces it uses the engine the way it is trustworthy - normal allowance, no rescaling, the film as reference:

  1. take the shift the first pass already measured (that is the engine's own number for this subtitle),
  2. apply it rigidly to the subtitle (pure timestamp arithmetic, no search, so nothing can lock onto anything),
  3. align *that* against the film's audio with the normal allowance: a correct shift leaves almost nothing, and the
     engine's own output is the result; a wrong shift leaves a large residual, and the job refuses instead of
     writing a confidently wrong subtitle.

`--max-offset-seconds` is never enlarged, which also removes the 300 s value that made one of the user's retries
exit 1.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

START = "            // The engine clamps the shift at the configured ceiling"
END = "            // Nothing destructive is ever written: a measured rescale that was not asked for (or that"
start = t.index(START)
end = t.index(END, start)
block = t[start:end]
assert 'wideAllowanceApplied' in block and 'wide-allowance.srt' in block, 'that is not the retry block'

new_block = '''            // The engine clamps the shift at the configured ceiling, so a result sitting at or past it is the most it
            // was allowed to apply, not what the file needed - and writing it would present a guess as a synced
            // subtitle. Widening the engine's search instead is worse: tried on the user's file, a 300 s allowance let
            // the engine lock onto a different part of the audio (56 s where the first pass measured 94 s), and the
            // result was wrong from the first line. So the shift the first pass measured is applied rigidly - pure
            // timestamp arithmetic, nothing to lock onto - and then aligned against the film's audio with the normal
            // allowance: a correct shift leaves almost nothing, a wrong one does not, and the job refuses rather than
            // writing a confidently wrong subtitle.
            var ceilingMs = config.MaxOffsetSeconds * 1000.0;
            if (!wideAllowanceApplied && measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var shiftedInput = ShiftSrtBy(engineInput, onCeiling.ShiftMs, tempDir, job);
                if (shiftedInput is null)
                {
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: this subtitle is further out than the {config.MaxOffsetSeconds} s limit and "
                        + "the shifted copy of it could not be written for the check. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }

                var verifyOutput = Path.Combine(tempDir, "shifted-verify.srt");
                SafeDelete(verifyOutput);
                var verifyArgs = BuildFfSubSyncArgs(
                    config, referenceArg, shiftedInput, verifyOutput, tempDir, serializeSpeech, referenceStream);
                // Verification, not a search: no rescaling, and the configured allowance.
                foreach (var flag in FramerateArgs(false, false))
                {
                    if (!verifyArgs.Contains(flag))
                    {
                        verifyArgs.Add(flag);
                    }
                }

                verifyArgs.Remove("--gss");

                _logger.LogInformation(
                    "Sync job {JobId}: the result reached the {Ceiling} s ceiling - applying the measured {Shift} ms and checking it against the audio",
                    job.Id,
                    config.MaxOffsetSeconds,
                    onCeiling.ShiftMs);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the alignment wanted {onCeiling.ShiftMs} ms, at or past the "
                    + $"{config.MaxOffsetSeconds} s ceiling \\u2014 applying that shift and checking it against the "
                    + "film's audio (the engine's search is not widened: a wider window is how a wrong lock gets in)");

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

                var residual = verifyExit == 0 && File.Exists(verifyOutput)
                    ? MeasureSyncChange(shiftedInput, verifyOutput)
                    : null;
                var residualRatio = residual?.Ratio ?? 1.0;
                var residualShift = residual?.ShiftMs ?? 0;

                if (verifyExit == 0
                    && AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                {
                    wideAllowanceApplied = true;
                    tempOutput = File.Exists(verifyOutput) ? verifyOutput : shiftedInput;
                    measured = MeasureSyncChange(engineInput, tempOutput);
                    PluginLog.Info(
                        $"[{job.Id}] offsets: the {onCeiling.ShiftMs} ms shift holds against the film's audio "
                        + $"(a further {residualShift} ms, no rescale) \\u2014 writing it");
                    _logger.LogInformation(
                        "Sync job {JobId}: the applied shift holds against the audio ({Shift} ms more)",
                        job.Id,
                        residualShift);
                }
                else
                {
                    string engineTail;
                    lock (verifyErrors)
                    {
                        engineTail = verifyErrors.Count == 0
                            ? string.Empty
                            : " · engine said: " + string.Join(" | ", verifyErrors).Trim();
                    }

                    var why = verifyExit != 0
                        ? $"the check did not run (exit {verifyExit}){engineTail}"
                        : $"the shifted subtitle still needed {residualShift} ms more from the film's audio "
                            + $"(ratio {residualRatio:0.0000})";
                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the check ({Why}) — nothing written",
                        job.Id,
                        why);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle needs {onCeiling.ShiftMs} ms, past the "
                        + $"{config.MaxOffsetSeconds} s limit, and {why}; nothing written, source untouched, "
                        + $"file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: this subtitle is further out than the {config.MaxOffsetSeconds} s limit "
                        + $"(the alignment measured {onCeiling.ShiftMs} ms), and applying that shift did not hold up "
                        + $"against the film's audio: {why}. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }

'''
t = t[:start] + new_block + t[end:]

# the rigid shift helper, next to the rescale one
anchor = """    /// <summary>
    /// Reads one SRT timestamp ("00:01:02,345") as milliseconds.
    /// </summary>"""
helper = '''    /// <summary>
    /// Writes a copy of a subtitle with every timestamp moved by a fixed amount.
    /// </summary>
    /// <remarks>
    /// Used for a subtitle further out than the offset limit allows: the shift the engine already measured is applied
    /// here, rigidly, so that nothing in a search can lock onto the wrong part of the audio. The result is then
    /// aligned against the film's audio to see whether the shift was right.
    /// </remarks>
    /// <param name="inputPath">The subtitle to shift.</param>
    /// <param name="shiftMs">Milliseconds to add (negative moves the subtitles earlier).</param>
    /// <param name="tempDir">Directory for the copy.</param>
    /// <param name="job">The job, for the log.</param>
    /// <returns>The path of the shifted copy, or null when it could not be written.</returns>
    private string? ShiftSrtBy(string inputPath, long shiftMs, string tempDir, SyncJob job)
    {
        try
        {
            var shifted = Path.Combine(tempDir, "shifted-input.srt");
            using (var writer = new StreamWriter(shifted, false, new System.Text.UTF8Encoding(false)))
            {
                foreach (var line in File.ReadLines(inputPath))
                {
                    var arrow = line.IndexOf("-->", StringComparison.Ordinal);
                    if (arrow > 0
                        && TryParseSrtTime(line[..arrow].Trim(), out var startMs)
                        && TryParseSrtTime(line[(arrow + 3)..].Trim(), out var endMs))
                    {
                        writer.WriteLine(
                            $"{SrtWriter.FormatTime((long)Math.Round(startMs) + shiftMs)} --> "
                            + SrtWriter.FormatTime((long)Math.Round(endMs) + shiftMs));
                    }
                    else
                    {
                        writer.WriteLine(line);
                    }
                }
            }

            return shifted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not shift {Path} by {Shift} ms for the check", inputPath, shiftMs);
            PluginLog.Info($"[{job.Id}] offsets: could not write the shifted copy of the subtitle ({ex.Message})");
            return null;
        }
    }

''' + anchor
assert anchor in t
t = t.replace(anchor, helper, 1)

svc.write_text(t)
print('the retry is gone: the measured shift is applied rigidly and checked against the audio')
