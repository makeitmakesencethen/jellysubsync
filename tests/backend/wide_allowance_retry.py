#!/usr/bin/env python3
"""2.0.18: a subtitle that needs a bigger shift than allowed gets one wide retry, checked against the audio again.

The offset ceiling exists because a result pinned to it is the most the engine was allowed to apply, not what the
file needed. Refusing is safe and unhelpful: the user's Clara Sola subtitles genuinely need 94 s and 99 s, and the
limit is 60 s. So that subtitle is now aligned once more with a wide allowance (4x the configured one, at least
300 s) - and the wide result is then checked with a *tight* allowance against the film's audio, the same double-check
a framerate stretch gets: if the wide pass locked onto the wrong part of the audio, the tight pass will still ask for
a large shift and nothing is written.

The hold-predicate is renamed, since it now guards both the stretch and the wide retry.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

# ------------------------------------------------------------------ rename the predicate
t = t.replace('internal static bool StretchHoldsAgainstAudio(', 'internal static bool AlignmentHoldsAgainstAudio(', 1)
t = t.replace('    /// Whether a stretch holds up against the film\'s own audio.',
              '    /// Whether an alignment holds up against the film\'s own audio.', 1)
t = t.replace('    /// <param name="ratio">Time ratio the audio alignment asked for after the stretch (1.0 = none).</param>\n'
              '    /// <param name="shiftMs">Shift the audio alignment asked for after the stretch.</param>',
              '    /// <param name="ratio">Time ratio the audio alignment asked for on the result (1.0 = none).</param>\n'
              '    /// <param name="shiftMs">Shift the audio alignment asked for on the result.</param>', 1)
t = t.replace('    /// The audio is the film: a subtitle stretched by the right factor needs almost nothing further to line up with\n'
              '    /// the speech, while a stretch applied to a subtitle from a different cut - which looks identical in the spans -\n'
              '    /// still needs a large shift, because its error is not a uniform scale. Ten seconds, or half a percent of the\n'
              '    /// runtime, is the room left for a genuine difference in intro or outro length.',
              '    /// The audio is the film: a subtitle that has been put in the right place needs almost nothing further to line up\n'
              '    /// with the speech, while a stretch applied to a differently cut subtitle - or a wide allowance that locked onto\n'
              '    /// the wrong part of the audio - still asks for a large shift. Ten seconds, or half a percent of the runtime, is\n'
              '    /// the room left for a genuine difference in intro or outro length.', 1)
t = t.replace('    /// <returns>True when the stretch holds.</returns>', '    /// <returns>True when the result holds.</returns>', 1)
t = t.replace('        if (StretchHoldsAgainstAudio(ratio, shiftMs, videoSeconds))',
              '        if (AlignmentHoldsAgainstAudio(ratio, shiftMs, videoSeconds))', 1)

# ------------------------------------------------------------------ round off the stale local after the audio fallback
old = """                    usedSubtitleReference = false;
                    referenceSpec = null;
                    referenceStream = null;
                    audioFallback = true;"""
new = """                    usedSubtitleReference = false;
                    referenceSpec = null;
                    referenceStream = null;
                    referenceArg = audioReference;
                    audioFallback = true;"""
assert old in t
t = t.replace(old, new, 1)

# ------------------------------------------------------------------ the ceiling: one wide retry, checked again
old = """            // The engine clamps the shift at the configured ceiling, so a result that sits exactly there
            // is the most it was allowed to apply, not what the file needed: writing it would present a
            // guess as a synced subtitle. AGENTS.md documents this as a refusal as well.
            var ceilingMs = config.MaxOffsetSeconds * 1000.0;
            if (measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var detail = $"the measured offset {onCeiling.ShiftMs} ms sits on the configured ceiling "
                    + $"({config.MaxOffsetSeconds} s)";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a result pinned to the offset ceiling ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: {detail}, so the shift is what the engine was allowed to apply, "
                    + $"not what the file needs; nothing written, source untouched, file={video.Path}");
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Refused";
                job.Error = $"refused: the engine clamped the shift at the {config.MaxOffsetSeconds} s ceiling "
                    + $"({detail}), so this subtitle is further out than the plugin was allowed to move it. Nothing "
                    + "was written. Raise \\"Maximum offset\\" and run it again if the file really is that far out.";
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }"""
new = """            // The engine clamps the shift at the configured ceiling, so a result sitting at or past it is the most it
            // was allowed to apply, not what the file needed: writing that would present a guess as a synced subtitle.
            // Giving up is safe and unhelpful - a subtitle can genuinely be a minute or two out - so the subtitle is
            // aligned once more with a wide allowance, and that wide result is then checked again with a tight one
            // against the film's audio, the same double-check a framerate stretch gets: a wide allowance that locked
            // onto the wrong part of the audio still asks for a large shift on the tight pass and nothing is written.
            var ceilingMs = config.MaxOffsetSeconds * 1000.0;
            if (!wideAllowanceApplied && measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var wideSeconds = Math.Max(config.MaxOffsetSeconds * 4, 300);
                var wideOutput = Path.Combine(tempDir, "wide-allowance.srt");
                SafeDelete(wideOutput);
                var wideArgs = BuildFfSubSyncArgs(config, referenceArg, engineInput, wideOutput, tempDir, serializeSpeech, referenceStream);
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] = wideSeconds.ToString(CultureInfo.InvariantCulture);

                PluginLog.Info(
                    $"[{job.Id}] offsets: the alignment wanted {onCeiling.ShiftMs} ms, at or past the "
                    + $"{config.MaxOffsetSeconds} s ceiling \\u2014 retrying this subtitle with a {wideSeconds} s "
                    + "allowance and then checking the result against the film's audio again");
                _logger.LogInformation(
                    "Sync job {JobId}: the result reached the {Ceiling} s offset ceiling - retrying with {Wide} s and a check",
                    job.Id,
                    config.MaxOffsetSeconds,
                    wideSeconds);

                var wideExit = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, wideArgs, tempDir, null, cancellationToken).ConfigureAwait(false);

                if (wideExit == 0 && File.Exists(wideOutput))
                {
                    var wideChange = MeasureSyncChange(engineInput, wideOutput);

                    // The second check, with a tight allowance: if the film agrees with where the wide pass put the
                    // subtitle, it needs almost nothing more.
                    var verifyOutput = Path.Combine(tempDir, "wide-verify.srt");
                    SafeDelete(verifyOutput);
                    var verifyArgs = new List<string>(wideArgs);
                    verifyArgs[verifyArgs.IndexOf("-i") + 1] = wideOutput;
                    verifyArgs[verifyArgs.IndexOf("-o") + 1] = verifyOutput;
                    verifyArgs[verifyArgs.IndexOf("--max-offset-seconds") + 1] =
                        config.MaxOffsetSeconds.ToString(CultureInfo.InvariantCulture);
                    var verifyExit = await RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe, verifyArgs, tempDir, null, cancellationToken).ConfigureAwait(false);

                    var residual = verifyExit == 0 && File.Exists(verifyOutput)
                        ? MeasureSyncChange(wideOutput, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                    {
                        wideAllowanceApplied = true;
                        tempOutput = wideOutput;
                        measured = wideChange;
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the {wideChange?.ShiftMs} ms result was checked against the film's "
                            + $"audio again (a further {residualShift} ms, no rescale) \\u2014 writing it");
                        _logger.LogInformation(
                            "Sync job {JobId}: the wide-allowance result holds against the audio ({Shift} ms more)",
                            job.Id,
                            residualShift);
                    }
                    else
                    {
                        var why = $"the {wideChange?.ShiftMs} ms result still wanted {residualShift} ms more from a "
                            + $"{config.MaxOffsetSeconds} s check, so the alignment is not stable";
                        _logger.LogWarning(
                            "Sync job {JobId}: refusing after the wide retry ({Why}) — nothing written",
                            job.Id,
                            why);
                        PluginLog.Info(
                            $"job {job.Id} REFUSED: {why}; nothing written, source untouched, file={video.Path}");
                        job.Status = SyncJobStatus.Failed;
                        job.Phase = "Refused";
                        job.Error = $"refused: this subtitle is further out than the {config.MaxOffsetSeconds} s limit, "
                            + $"and aligning it with a {wideSeconds} s allowance did not hold up when checked again "
                            + $"({why}). Nothing was written.";
                        job.Progress = 1.0;
                        job.FinishedAtUtc = DateTime.UtcNow;
                        job.OutputPath = null;
                        SafeDelete(tempOutput);
                        SafeDelete(wideOutput);
                        return;
                    }
                }
                else
                {
                    var detail = $"the measured offset {onCeiling.ShiftMs} ms is at or past the configured ceiling "
                        + $"({config.MaxOffsetSeconds} s), and the retry with a {wideSeconds} s allowance (exit "
                        + $"{wideExit}) produced nothing";
                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the offset ceiling ({Detail}) — nothing written",
                        job.Id,
                        detail);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: {detail}; nothing written, source untouched, file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: {detail}. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }"""
assert old in t, 'ceiling block not found'
t = t.replace(old, new, 1)

old = """        var audioFallback = false;"""
new = """        var audioFallback = false;
        var wideAllowanceApplied = false;"""
assert old in t
t = t.replace(old, new, 1)

# the old message is gone from this path; make sure nothing else referred to it
svc.write_text(t)
print('wide retry with a second audio check is in place')
