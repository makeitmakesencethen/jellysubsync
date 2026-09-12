#!/usr/bin/env python3
"""Test a stretch against the film's own audio, and only when something was stretched.

A stretch is a claim about the whole timeline, and the other subtitle cannot check it: a subtitle from a different
cut and one from a different framerate produce the same span ratio. The film's audio can. After a stretch, ffsubsync
is pointed at the media file (the audio, default stream) with the *stretched* subtitle as input and told not to
rescale, so what it reports next is the residual rather than a second opinion about the ratio. A correct stretch
leaves almost nothing to do - below 3 s the engine suppresses its write entirely - while a stretch applied to a
differently cut subtitle still wants a large shift, because its error is not a uniform scale. When it does not hold,
the subtitle the user has is aligned against the audio instead, offsets only, and both the log and the outcome say
the stretch was dropped.

Cost is bounded by construction: it runs only when a stretch happened, and only the first subtitle of a file pays
for the audio analysis, which is cached per file like every other audio path in the plugin.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

# ---------------------------------------------------------------- 1. the decision
anchor = """    /// <summary>
    /// Whether the subtitle being synced is the one that sits off the file's own timeline.
    /// </summary>"""
decision = '''    /// <summary>
    /// Whether a stretch holds up against the film's own audio.
    /// </summary>
    /// <remarks>
    /// The audio is the film: a subtitle stretched by the right factor needs almost nothing further to line up with
    /// the speech, while a stretch applied to a subtitle from a different cut - which looks identical in the spans -
    /// still needs a large shift, because its error is not a uniform scale. Ten seconds, or half a percent of the
    /// runtime, is the room left for a genuine difference in intro or outro length.
    /// </remarks>
    /// <param name="ratio">Time ratio the audio alignment asked for after the stretch (1.0 = none).</param>
    /// <param name="shiftMs">Shift the audio alignment asked for after the stretch.</param>
    /// <param name="videoSeconds">The file's duration.</param>
    /// <returns>True when the stretch holds.</returns>
    internal static bool StretchHoldsAgainstAudio(double ratio, long shiftMs, double videoSeconds)
    {
        if (Math.Abs(ratio - 1.0) > 0.005)
        {
            return false;
        }

        var ceiling = Math.Max(10.0, videoSeconds * 0.005);
        return Math.Abs(shiftMs) <= ceiling * 1000.0;
    }

'''
assert anchor in t
t = t.replace(anchor, decision + anchor, 1)

# ---------------------------------------------------------------- 2. the verification
anchor = """    private async Task<int> RunProcessWithStderrCallbackAsync("""
helper = '''    /// <summary>
    /// Tests a stretch against the film's audio and says what to write.
    /// </summary>
    /// <remarks>
    /// Called only when a stretch happened. If it holds, the engine's own output - the stretched subtitle with that
    /// small residual applied - is the result, or the stretched copy when the engine suppressed a write it judged
    /// too small to save. If it does not hold, the subtitle the user has is aligned against the audio with offsets
    /// only. The caller is told which input that result came from, because the guards downstream compare the result
    /// with what the engine was given.
    /// </remarks>
    /// <param name="job">The job.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="ffsubsyncExe">The engine.</param>
    /// <param name="videoPath">The media file, which supplies the audio.</param>
    /// <param name="stretchedInput">The rescaled subtitle the alignment used.</param>
    /// <param name="originalInput">The subtitle the user has.</param>
    /// <param name="tempDir">The job's temporary directory.</param>
    /// <param name="videoSeconds">The file's duration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The path to use as the result, whether the stretch was dropped, and the input that result came from.</returns>
    private async Task<(string? Path, bool Dropped, string Input)> VerifyStretchAgainstAudioAsync(
        SyncJob job,
        Configuration.PluginConfiguration config,
        string ffsubsyncExe,
        string videoPath,
        string stretchedInput,
        string originalInput,
        string tempDir,
        double videoSeconds,
        CancellationToken cancellationToken)
    {
        var verifyOutput = Path.Combine(tempDir, "audio-check.srt");
        SafeDelete(verifyOutput);
        var args = new List<string>
        {
            videoPath,
            "-i", stretchedInput,
            "-o", verifyOutput,
            "--max-offset-seconds", config.MaxOffsetSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds", config.MaxSubtitleSeconds.ToString(CultureInfo.InvariantCulture),
            "--vad", AllowedVadMethods.Contains(config.VadMethod) ? config.VadMethod : "subs_then_webrtc",
            "--output-encoding", AllowedOutputEncodings.Contains(config.OutputEncoding) ? config.OutputEncoding : "utf-8",
            "--ffmpeg-path", ResolveFfmpegPath(),
            "--no-fix-framerate",
            "--skip-infer-framerate-ratio",
            "--log-dir-path", tempDir
        };

        _logger.LogInformation("Sync job {JobId}: testing the stretch against the film's audio", job.Id);
        PluginLog.Info(
            $"[{job.Id}] framerate: the subtitle was stretched, so the stretch is tested against the film's own "
            + "audio (offsets only) \\u2014 a differently cut subtitle looks the same as a framerate mismatch in the spans");

        var exitCode = await RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, args, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Sync job {JobId}: the audio check could not run (exit {Code}) - keeping the stretched result",
                job.Id,
                exitCode);
            PluginLog.Info($"[{job.Id}] framerate: the audio check could not run (exit={exitCode}) - keeping the stretched result");
            return (stretchedInput, false, stretchedInput);
        }

        var measured = File.Exists(verifyOutput) ? MeasureSyncChange(stretchedInput, verifyOutput) : null;
        var ratio = measured?.Ratio ?? 1.0;
        var shiftMs = measured?.ShiftMs ?? 0;
        if (StretchHoldsAgainstAudio(ratio, shiftMs, videoSeconds))
        {
            _logger.LogInformation(
                "Sync job {JobId}: the stretch holds - the audio asked for {Shift} ms more and no rescale",
                job.Id,
                shiftMs);
            PluginLog.Info(
                $"[{job.Id}] framerate: the audio confirms the stretch (a further {shiftMs} ms, no rescale)");
            return File.Exists(verifyOutput) ? (verifyOutput, false, stretchedInput) : (stretchedInput, false, stretchedInput);
        }

        // The stretch does not hold: align the subtitle the user has against the audio, offsets only.
        var fallbackOutput = Path.Combine(tempDir, "audio-fallback.srt");
        SafeDelete(fallbackOutput);
        var fallbackArgs = new List<string>(args);
        fallbackArgs[fallbackArgs.IndexOf("-i") + 1] = originalInput;
        fallbackArgs[fallbackArgs.IndexOf("-o") + 1] = fallbackOutput;

        _logger.LogWarning(
            "Sync job {JobId}: the stretch does not hold against the audio ({Ratio:0.0000}x, {Shift} ms) - aligning with offsets only",
            job.Id,
            ratio,
            shiftMs);
        PluginLog.Info(
            $"[{job.Id}] framerate: the stretch does NOT hold against the audio ({ratio:0.0000}x, {shiftMs} ms) "
            + "\\u2014 a different cut looks the same in the spans, so the subtitle is aligned with offsets only");

        var fallbackExit = await RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, fallbackArgs, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (fallbackExit == 0 && File.Exists(fallbackOutput))
        {
            return (fallbackOutput, true, originalInput);
        }

        return (null, true, originalInput);
    }

''' + anchor
assert anchor in t
t = t.replace(anchor, helper, 1)

# ---------------------------------------------------------------- 3. wire it in after the main run
old = """            if (!File.Exists(tempOutput) && engineInput != subtitleInputPath && File.Exists(engineInput))"""
new = """            // A stretch is a claim about the whole timeline, and only the film's audio can test it: another
            // subtitle shows the same few percent whether the subtitle is from a different framerate or from a
            // different cut. Runs only when something was stretched.
            if (engineInput != subtitleInputPath)
            {
                var (verifiedPath, dropped, verifiedInput) = await VerifyStretchAgainstAudioAsync(
                    job,
                    config,
                    ffsubsyncExe,
                    videoPath,
                    engineInput,
                    subtitleInputPath,
                    tempDir,
                    videoDuration.TotalSeconds,
                    cancellationToken).ConfigureAwait(false);
                stretchDropped = dropped;
                if (verifiedPath is not null)
                {
                    tempOutput = verifiedPath;
                    engineInput = verifiedInput;
                }
                else if (dropped)
                {
                    job.Outcome = "the stretch did not hold against the audio and the offset-only alignment produced nothing";
                }
            }

            if (!File.Exists(tempOutput) && engineInput != subtitleInputPath && File.Exists(engineInput))"""
assert old in t
t = t.replace(old, new, 1)

# a local flag for the outcome wording, and the wording itself
old = """        var usedSubtitleReference = false;"""
new = """        var usedSubtitleReference = false;
        var stretchDropped = false;"""
assert old in t
t = t.replace(old, new, 1)

old = """                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
                if (engineInput != subtitleInputPath)
                {"""
new = """                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
                if (stretchDropped)
                {
                    job.Outcome = "the stretch did not hold against the film's audio, so the subtitle was aligned with "
                        + "offsets only" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \\u00b7 " + job.Outcome);
                }
                else if (engineInput != subtitleInputPath)
                {"""
assert old in t
t = t.replace(old, new, 1)

svc.write_text(t)
print('verification wired in: runs only on a stretch, tests the audio, falls back to offsets only')
