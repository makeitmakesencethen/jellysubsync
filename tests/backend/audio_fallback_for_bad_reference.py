#!/usr/bin/env python3
"""A reference subtitle that is not the same cut becomes an audio alignment, not a refusal.

The refusal was right that a subtitle reference cannot be trusted for a huge shift, and wrong to give up there: the
film's own audio cannot be a wrong cut. So when the shift a subtitle reference demands goes past the ceiling, that
track is discarded as a ruler (so the file's other subtitles do not repeat the same measurement), the subtitle is
aligned against the audio instead, and the result is written. The cost is one audio analysis per file, cached like
every other audio path, and it is paid only by files whose reference is bad.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

old = """            if (usedSubtitleReference
                && measured is { } fromReference
                && Math.Abs(fromReference.ShiftMs) > referenceCeilingMs)
            {
                var detail = $"aligned to the reference subtitle {referenceSpec} at {fromReference.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a reference-derived shift ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: {detail}, over the {referenceCeilingMs / 1000.0:0.#} s limit for a "
                    + $"subtitle reference — the reference track is probably not the same cut; nothing written, "
                    + $"source untouched, file={video.Path}");
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Refused";
                job.Error = $"refused: the alignment came from the reference subtitle {referenceSpec} and moved this "
                    + $"subtitle by {fromReference.ShiftMs} ms, more than the {referenceCeilingMs / 1000.0:0.#} s a "
                    + "subtitle reference is trusted for — a shift this size usually means that track is from a "
                    + "different cut. Nothing was written. Raise \\"Maximum shift from a subtitle reference\\" if the "
                    + "track really is the same cut, or sync this subtitle against the audio instead.";
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }"""
new = """            if (usedSubtitleReference
                && measured is { } fromReference
                && Math.Abs(fromReference.ShiftMs) > referenceCeilingMs)
            {
                // The measurement is right and the conclusion was incomplete: a subtitle reference cannot be trusted
                // for a shift this size (it is very likely from a different cut), but the film's own audio cannot be
                // a wrong cut at all. Instead of refusing the subtitle because of the track it happened to be
                // aligned against, drop that track as a ruler and align this subtitle against the audio. The track
                // is discarded so the file's other subtitles do not repeat the same measurement, and the audio
                // analysis is paid once per file, cached like every other audio path.
                var detail = $"aligned to the reference subtitle {referenceSpec} at {fromReference.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: the reference subtitle is not the same cut ({Detail}) - aligning against the audio instead",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id}: the reference subtitle {referenceSpec} is not the same cut ({detail}, over the "
                    + $"{referenceCeilingMs / 1000.0:0.#} s limit for a subtitle reference) \\u2014 discarding that "
                    + $"track as a ruler and aligning against the audio instead, file={video.Path}");

                if (referenceSpec is not null)
                {
                    ReferenceStore.Discard(videoPath, referenceSpec);
                }

                var audioReference = await PrepareAudioReferenceAsync(
                    "the reference subtitle is not the same cut as the video").ConfigureAwait(false);
                var audioArgs = BuildFfSubSyncArgs(
                    config, audioReference, subtitleInputPath, tempOutput, tempDir, serializeSpeech, null);
                var audioExit = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, audioArgs, tempDir, null, cancellationToken).ConfigureAwait(false);

                if (audioExit == 0 && File.Exists(tempOutput))
                {
                    if (speechKey is not null && serializeSpeech)
                    {
                        SpeechCache.Harvest(audioReference, speechKey);
                        SpeechCache.DropLink(speechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    usedSubtitleReference = false;
                    referenceSpec = null;
                    referenceStream = null;
                    audioFallback = true;
                    measured = MeasureSyncChange(subtitleInputPath, tempOutput);
                    PluginLog.Info(
                        $"[{job.Id}] reference: method=audio why=the reference subtitle was not the same cut "
                        + $"(it demanded {fromReference.ShiftMs} ms)");
                }
                else
                {
                    _logger.LogWarning(
                        "Sync job {JobId}: the audio alignment after the bad reference did not produce a subtitle (exit {Code}) - nothing written",
                        job.Id,
                        audioExit);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: {detail}, over the {referenceCeilingMs / 1000.0:0.#} s limit for a "
                        + "subtitle reference, and the audio alignment that replaced it produced nothing; nothing "
                        + $"written, source untouched, file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: the subtitle was aligned against the file's own subtitle track {referenceSpec}, "
                        + $"which demanded a {fromReference.ShiftMs} ms shift — that track is not the same cut — and "
                        + "aligning against the audio instead produced nothing. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }"""
assert old in t, 'refusal block not found'
t = t.replace(old, new, 1)

old = """        var usedSubtitleReference = false;
        var stretchDropped = false;"""
new = """        var usedSubtitleReference = false;
        var stretchDropped = false;
        var audioFallback = false;"""
assert old in t
t = t.replace(old, new, 1)

# the outcome says what happened
old = """                if (stretchDropped)
                {"""
new = """                if (audioFallback)
                {
                    job.Outcome = "the file's own subtitle track is not the same cut, so this was aligned against the "
                        + "audio" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \\u00b7 " + job.Outcome);
                }
                else if (stretchDropped)
                {"""
assert old in t
t = t.replace(old, new, 1)

svc.write_text(t)
print('a bad reference now becomes an audio alignment instead of a refusal')
