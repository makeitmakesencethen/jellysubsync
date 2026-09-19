using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Extensions;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The sync job pipeline: the engine attempt, the reference, the write and the outcome.
/// </summary>
/// <remarks>
/// Part of <see cref="SubSyncService"/>, split out of the single file as the C5 cluster of
/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,
/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this
/// code shares with the rest of the service is still the service's own fields, declared in the main
/// file.
/// </remarks>
public partial class SubSyncService
{
    /// <summary>
    /// Releases a job's hold on its file's audio analysis, if it has one.
    /// </summary>
    /// <param name="job">The job that may be holding it.</param>
    /// <param name="videoPath">The media file.</param>
    private void ReleaseSpeechGate(SyncJob job, string videoPath)
    {
        if (!job.HoldsSpeechGate)
        {
            return;
        }

        job.HoldsSpeechGate = false;
        if (_speechGates.TryGetValue(videoPath, out var gate))
        {
            // Released only by the job that took it (the flag above), and never twice: the harvest path and
            // the job's finally both come through here.
            gate.Release();
        }
    }

    /// <summary>Joins a note with another, so callers do not repeat the separator.</summary>
    /// <param name="existing">Note so far, or null.</param>
    /// <param name="addition">Note to append.</param>
    /// <returns>The combined note.</returns>
    private static string Join(string? existing, string addition) =>
        string.IsNullOrEmpty(existing) ? addition : existing + " \u00b7 " + addition;


    /// <summary>
    /// Runs one job and guarantees that it leaves a terminal state behind (B6).
    /// </summary>
    /// <remarks>
    /// A job set to <c>Running</c> and left there is the whole of B6: it holds a worker slot and the run it
    /// belongs to never reports itself finished, with a restart as the only way out. The paths that produced it
    /// were an exception raised outside the job's own error handling (a media file that vanished, a fault
    /// before the job's <c>try</c> was reached) and any path that returned without settling its status. Neither
    /// can happen quietly any more: every exit passes <see cref="StuckJobPolicy.Settle"/>, which fails a job
    /// that is still running and says so in the plugin log, and a watchdog stops jobs that stop making progress
    /// (see <see cref="ReapStuckJobs"/>).
    /// </remarks>
    /// <param name="job">The job to run.</param>
    private async Task RunSyncJobWithContext(SyncJob job)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var ctx))
        {
            job.Status = SyncJobStatus.Failed;
            job.Error = "Job context was evicted before it started (server restarted?).";
            return;
        }

        using var cts = new CancellationTokenSource();
        _jobCancellation[job.Id] = cts;
        try
        {
            await RunSyncJob(job, ctx.Video, ctx.Stream, ctx.Ordinal, ctx.Config, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A job the watchdog already settled keeps the reason it was stopped for: "cancelled by user"
            // would name an action nobody took.
            if (job.Status == SyncJobStatus.Running)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.Phase = "Cancelled";
                job.Error = "Cancelled by user.";
                _logger.LogInformation("Job {JobId} cancelled by user", job.Id);
            }
        }
        catch (Exception ex)
        {
            // Anything the job's own handler did not catch - the failure used to end the task and leave the
            // job Running for good.
            _logger.LogError(ex, "Sync job {JobId} ended with an unhandled error", job.Id);
            PluginLog.Error($"job {job.Id} ended with an unhandled error: {ex.Message}", ex);
            if (job.Status is SyncJobStatus.Running or SyncJobStatus.Queued)
            {
                job.Status = SyncJobStatus.Failed;
                job.Error = ex.Message;
            }
        }
        finally
        {
            _jobCancellation.TryRemove(job.Id, out _);
            JobProcessRegistry.Forget(job.Id);

            // The phase is read before settling: it is the last thing the job reported, and it is the only
            // thing in the log that says where a job that never finished had got to.
            var phaseAtExit = job.Phase;
            if (StuckJobPolicy.Settle(
                    job, DateTime.UtcNow,
                    "the job ended without reaching a terminal state; the plugin log has the last phase it reported"))
            {
                PluginLog.Warn(
                    $"[{job.Id}] job ended while still running (phase '{phaseAtExit}') - marked failed so it "
                    + "cannot hold its worker slot or keep its run looking unfinished");
            }
        }

        // Sweep cache: successful syncs of external subtitle files are remembered
        // so repeat library sweeps skip unchanged content whose output still exists.
        if (job.Status == SyncJobStatus.Completed)
        {
            RecordSweepOutcome(job, ok: true, outputPath: job.OutputPath, error: null);
        }
    }

    /// <summary>
    /// Describes what a successful sync actually changed, by comparing cue
    /// timings of the original and the synced file: the applied offset in
    /// milliseconds (signed; + = subtitles moved later) and, when ffsubsync
    /// corrected a framerate mismatch, the fitted time ratio plus the total
    /// cumulative drift it fixed over the subtitle's runtime.
    /// </summary>
    /// <summary>
    /// States what the engine said about an alignment: its score and the offset it chose.
    /// </summary>
    /// <remarks>
    /// Logged for both paths on purpose. The score was being discarded, which is why a wrong ruler could pass
    /// without anything in the log hinting at it, and the two numbers side by side are what lets a field run be
    /// read after the fact: a subtitle reference that scores far below the audio reference of the same file is
    /// the pattern S31 is about.
    /// </remarks>
    /// <param name="jobId">The job the run belonged to.</param>
    /// <param name="reference">What the engine was aligned against, in words.</param>
    /// <param name="score">The score it printed, when it printed one.</param>
    /// <param name="offsetSeconds">The offset it printed, when it printed one.</param>
    private static void LogEngineAlignment(string jobId, string reference, double? score, double? offsetSeconds)
    {
        if (score is null && offsetSeconds is null)
        {
            return;
        }

        var scoreText = score is { } s ? $"{s:0.###}" : "(none)";
        var offsetText = offsetSeconds is { } o ? $"{o:0.000} s" : "(none)";
        PluginLog.Info(
            $"[{jobId}] ffsubsync alignment: score={scoreText} offset={offsetText} against {reference}"
            + (score is { } low && low < 0
                ? " - the engine itself calls a negative score an unsuccessful sync"
                : string.Empty));
    }

    /// <summary>
    /// The VAD to hand the engine for a reference: the audio VAD when the reference is the audio, and the
    /// configured method when the plugin supplied a subtitle it has already vetted.
    /// </summary>
    /// <param name="referenceSpec">The subtitle reference in use, or null when the reference is the audio.</param>
    /// <returns>The VAD to pass, or null to use the configured one.</returns>
    internal static string? VadForReference(string? referenceSpec)
        => referenceSpec is null ? AudioReferenceVad : null;

    /// <summary>Gets the VAD the engine is given when the plugin intends the audio to be the reference.</summary>
    internal static string AudioReferenceVadName => AudioReferenceVad;

    /// <summary>
    /// States that the engine is being given a VAD it did not get from the configuration, and why.
    /// </summary>
    /// <remarks>
    /// Logged because this is the difference between "the audio path" and "a path the engine chose a subtitle
    /// for": a field run has to be able to tell which signal produced an answer (S43).
    /// </remarks>
    /// <param name="jobId">The job the run belongs to.</param>
    /// <param name="reference">What the engine is aligned against, in words.</param>
    /// <param name="configured">The method the configuration names.</param>
    private static void LogVadOverride(string jobId, string reference, string? configured)
    {
        if (string.Equals(configured, AudioReferenceVad, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PluginLog.Info(
            $"[{jobId}] reference is {reference}, so the engine is given --vad {AudioReferenceVad}: with the "
            + $"configured '{configured ?? "subs_then_webrtc"}' it would read the video's own subtitle tracks as its "
            + "speech signal, and which track that is, is the engine's choice rather than the plugin's");
    }

    /// <summary>
    /// Reads the alignment score out of one line of the engine's output.
    /// </summary>
    /// <remarks>
    /// ffsubsync prints `score: 198713.000` and `offset seconds: 0.050` for every run, and that score is the only
    /// place the engine states how well the two timelines agreed. Measured on 2026-09-15: a real sibling
    /// subtitle scores ~198 700 against the same cut and a subtitle of another film ~2 900, but the *same track
    /// from a 2 % longer cut* scores 274 700, i.e. higher than the correct ruler, so the score alone is not a
    /// verdict - it is a number the log has to carry, and one signal beside the spread above. It is captured
    /// because it was being discarded, and because a field run cannot be read without it.
    /// </remarks>
    /// <param name="line">One line of the engine's stdout/stderr.</param>
    /// <param name="score">The score it states, when it states one.</param>
    /// <returns>True when the line carried a score.</returns>
    internal static bool TryParseEngineScore(string line, out double score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var marker = line.IndexOf("score:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var rest = line[(marker + "score:".Length)..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '-' or '+' or '.' or ','))
        {
            end++;
        }

        return end > 0 && double.TryParse(
            rest[..end].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out score);
    }

    /// <summary>
    /// Reads the offset the engine reported out of one line of its output.
    /// </summary>
    /// <param name="line">One line of the engine's output.</param>
    /// <param name="seconds">The offset it states, when it states one.</param>
    /// <returns>True when the line carried an offset.</returns>
    internal static bool TryParseEngineOffset(string line, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var marker = line.IndexOf("offset seconds:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var rest = line[(marker + "offset seconds:".Length)..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '-' or '+' or '.' or ','))
        {
            end++;
        }

        return end > 0 && double.TryParse(
            rest[..end].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    private async Task RunSyncJob(
        SyncJob job,
        Video video,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        int subtitleOrdinal,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        job.Status = SyncJobStatus.Running;
        job.StartedAtUtc = DateTime.UtcNow;
        job.Progress = 0.0;

        var videoPath = video.Path;
        var videoDir = Path.GetDirectoryName(videoPath) ?? ".";
        var videoNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var videoExt = Path.GetExtension(videoPath);
        var tempDir = Path.Combine(Plugin.Instance?.TempPath ?? Path.GetTempPath(), job.Id);

        // Stream of the media file ffsubsync should take its speech signal from. Embedded
        // inputs set this so the subtitle being fixed is not used as its own reference.

        // True when this job is aligned against a subtitle taken from a sibling track instead of
        // against the audio. Such a result is only ever as good as that track, so it is checked
        // before anything is written.
        var stretchDropped = false;
        var audioFallback = false;
        var wideAllowanceApplied = false;

        // Which track became the reference, for both log lines and the outcome text.

        // Duration of the video, used to judge whether a track is a full subtitle or just signs.
        var videoDurationForReference = video.RunTimeTicks is { } refTicks && refTicks > 0
            ? TimeSpan.FromTicks(refTicks)
            : TimeSpan.Zero;

        // Paths for the safe atomic-replace workflow
        // Filled in by the write step as it goes, and read by the failure path below: a write that throws
        // returns nothing, so the backup it created has to be published where the catch can still see it.
        var write = new SyncWriteOutcome();
        string? tempOutput = null;   // ffsubsync output in temp dir
        // Declared outside the try: the audio reference is prepared from four places, two of them outside
        // reference resolution, and the finally below drops the analysis link this object describes.
        var reference = new ReferenceResolution { Path = videoPath };
        string? changedDir = null;   // folder touched by this job (for the targeted library rescan)
        string? cuesNote = null;     // set when the subtitle looks like a signs/forced track

        // The job's own audio analysis, if it makes one. Declared here because the link's lifetime is the
        // job's lifetime: see the comment on the drop in the finally below (S46).

        try
        {
            // Everything that can throw lives inside this block, including the checks below: a job that
            // threw before reaching it skipped its own error handling and its cleanup, which is one of the
            // two ways a job stayed Running for good (B6).
            //
            // Step 0: Ensure ffsubsync is available
            job.Phase = "Preparing";
            job.Progress = 0.0;

            // Every job passes here, and a job about to run is the right place to notice that nothing has measured
            // its volume: the next planning pass can then judge that volume on a measurement instead of holding it.
            // The first attempt at this sat in the audio-reference branch, which jobs on a real server do not take -
            // 17 engine runs on 2026-09-14, several on the fast volume, and no probe ever fired (S38).
            ProbeVolumeIfUnmeasured(
                _jobContexts.TryGetValue(job.Id, out var probeContext) ? probeContext.Video.Path : null);

            Directory.CreateDirectory(tempDir);

            // The extracted subtitle is not the job's private business: every job of this file reads the same
            // tracks out of it, and it outlives the job that happened to extract it (see SharedExtractionStore).
            var sharedExtractDir = SharedExtractionStore.Acquire(video.Path, job.Id);

            // The file is checked here instead of when the task is queued: queueing must not touch the
            // media share (a stat per task slowed a 50-task batch to a minute while a job was reading the
            // same share, which starved the scheduler). One stat per job, at the point where it matters.
            if (!File.Exists(videoPath))
            {
                throw new FileNotFoundException($"The video file is no longer on disk: {videoPath}");
            }

            var ffsubsyncExe = ResolveFfSubSyncPath();

            if (!File.Exists(ffsubsyncExe) && ffsubsyncExe != "ffsubsync")
            {
                throw new InvalidOperationException($"ffsubsync not found at '{ffsubsyncExe}'. Install it from the plugin configuration page.");
            }

            // Step 1: Prepare subtitle input
            string subtitleInputPath;

            // Language filter applies to external files and embedded tracks alike;
            // ListSubtitles already hides what the filter excludes, so reaching this
            // point means a stale client queued the track.
            var allowedLanguages = Services.SettingsSource.Current()?.SyncLanguages ?? Array.Empty<string>();
            if (!LanguageSupport.MatchesFilter(subtitleStream.Language, allowedLanguages))
            {
                throw new InvalidOperationException(
                    $"This subtitle track is in {LanguageSupport.Label(subtitleStream.Language)}, which the configured language filter ({LanguageSupport.Describe(allowedLanguages)}) excludes.");
            }

            if (subtitleStream.IsExternal && !string.IsNullOrEmpty(subtitleStream.Path))
            {
                subtitleInputPath = subtitleStream.Path;

                // An external sidecar has no track of its own inside the file, but the file it sits next
                // to usually has one, and using it makes the alignment exact instead of an audio guess.
                // No sibling: the audio is the ruler, which for an external file is what the user asked
                // for and is the one case where an unverifiable result is still written (S8, settled
                // with the user on 2026-09-11).
                var siblings = video.GetMediaSources(true)
                    .SelectMany(source => source.MediaStreams)
                    .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle
                        && !stream.IsExternal)
                    .ToList();
                if (siblings.Count > 0)
                {
                    reference.Stream = MediaStreamMap.SelectReferenceStream(
                        false,
                        siblings.Select(stream => stream.Codec ?? string.Empty).ToList(),
                        -1,
                        siblings.Select(stream => stream.IsForced).ToList());
                    _logger.LogInformation(
                        "External sync of {Subtitle}: aligning against '{Reference}' of {Video} instead of the audio",
                        subtitleInputPath,
                        reference.Stream,
                        videoPath);
                }
            }
            else
            {
                // Only text subtitles can be aligned — image-based tracks (PGS,
                // DVD/VobSub, DVB, XSUB) cannot be converted to SRT text and made
                // ffmpeg fail with a cryptic exit code (e.g. 234) at extraction.
                if (LanguageSupport.IsImageBased(subtitleStream.Codec))
                {
                    throw new InvalidOperationException(
                        "This embedded subtitle track is image-based (PGS/DVD/VobSub) and can't be synchronized — only text subtitles can be aligned.");
                }

                job.Phase = "Extracting subtitle";
                job.Progress = 0.05;
                subtitleInputPath = Path.Combine(sharedExtractDir, $"subtitle_{job.SubtitleIndex}.srt");

                // Jellyfin's MediaStream.Index cannot be trusted as a container
                // stream index (observed values pointing past the file's real
                // stream count on files mixing embedded + external tracks), so
                // the real stream is located by probing the file with ffmpeg
                // and matching by position among embedded subtitle streams.
                var (containerIndex, subtitleStreamOrdinal, subtitleCodecs) = await MediaStreamMap.ResolveContainerSubtitleIndexAsync(video, subtitleStream, ResolveFfmpegPath(), _processes).ConfigureAwait(false);

                // Keep ffsubsync from using the very track we are fixing as its speech
                // signal (see SelectReferenceStream) — that would report every embedded
                // subtitle as already in sync.
                var embeddedSubtitleStreams = video.GetMediaSources(true)
                    .SelectMany(source => source.MediaStreams)
                    .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !stream.IsExternal)
                    .ToList();

                reference.Stream = MediaStreamMap.SelectReferenceStream(
                    true,
                    subtitleCodecs,
                    subtitleStreamOrdinal,
                    embeddedSubtitleStreams.Count == subtitleCodecs.Count
                        ? embeddedSubtitleStreams.Select(stream => stream.IsForced).ToList()
                        : null);
                _logger.LogInformation(
                    "Embedded sync of {Video}: deriving the speech signal from '{Reference}'",
                    videoPath, reference.Stream);

                var extractionWatch = System.Diagnostics.Stopwatch.StartNew();
                var extractionMethod = await ExtractEmbeddedAsync(
                    videoPath,
                    subtitleStreamOrdinal,
                    containerIndex,
                    subtitleInputPath,
                    config,
                    job,
                    video.RunTimeTicks,
                    cancellationToken).ConfigureAwait(false);
                extractionWatch.Stop();

                // Say which reader produced the subtitle and what it cost. The indexed reader touches
                // kilobytes and finishes in milliseconds; the ffmpeg fallback demuxes the whole file,
                // which on a NAS is the difference between a second and minutes per subtitle. Putting
                // it in the task result means the difference is visible without the server log.
                job.ExtractionNote = extractionMethod switch
                {
                    "ffmpeg" => $"demuxed with ffmpeg, {extractionWatch.ElapsedMilliseconds} ms",
                    "matroska-cached" => "reused from the pass that read this file for another subtitle",
                    // A cache hit reads nothing at all this run: saying "read through the container index" here
                    // described the reader that originally produced the text, not what this job did (nothing).
                    "subtitle-cache" => "served from the extracted-subtitle cache, no read this run",
                    _ => $"read through the container index ({extractionMethod}), {extractionWatch.ElapsedMilliseconds} ms"
                };

                // A kill during extraction must not turn into "try the next method".
                cancellationToken.ThrowIfCancellationRequested();
                job.Phase = "Extracted subtitle with " + DescribeExtraction(extractionMethod);
            }

            // Step 2: Run ffsubsync → temp output
            job.Phase = "Analyzing speech";
            job.Progress = 0.1;

            // A full episode subtitle carries hundreds of cues. A handful over a long video is a
            // forced/signs track, and nothing else in the output shows it: the synced sidecar looks
            // perfectly normal, it just contains two lines. Reported from real use after a two-cue
            // Norwegian track was synced while the same file also carried a full WebVTT track in that
            // language, so it is logged and stated in the outcome rather than left to be discovered.
            var inputCues = AlignmentMetrics.CountSubtitleCues(subtitleInputPath);
            var videoDuration = video.RunTimeTicks is { } ticks && ticks > 0
                ? TimeSpan.FromTicks(ticks)
                : TimeSpan.Zero;
            if (AlignmentMetrics.LooksLikeSignsTrack(inputCues, videoDuration))
            {
                cuesNote = $"only {inputCues} cue{(inputCues == 1 ? "" : "s")} in a "
                    + $"{(int)videoDuration.TotalMinutes}-minute file \u2014 looks like a forced/signs track, "
                    + "not the full subtitle";
                _logger.LogWarning("Sync job {JobId}: {Note}", job.Id, cuesNote);
            }

            tempOutput = Path.Combine(tempDir, "synced.srt");

            // "fast" mode: the speech analysis depends only on the media file, the VAD
            // method and the ffsubsync build — not on which subtitle is being synced —
            // so it is computed once and reused for the other subtitles of that file.

            // P3: which ruler this job is measured against - the file's own audio, or a sibling subtitle track
            // built here from text this process already has.
            await ResolveReferenceAsync(reference, job, videoPath, videoDurationForReference, cancellationToken)
                .ConfigureAwait(false);

            // The engine is given a rescaled copy when a frame rate difference stands between the two subtitles;
            // the measurement below still compares the original subtitle with the output, so the rescale is
            // visible to the guard rather than hidden from it.
            var engineInput = subtitleInputPath;
            var referenceArg = reference.Path!;
            if (reference.UsedSubtitleReference && config.FixFramerate)
            {
                engineInput = AlignmentMetrics.RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job, _logger) ?? engineInput;
            }

            // P5: the engine run - the first attempt and, when a cached speech analysis turned out to be
            // unusable, a second one from the audio. Everything it produces is consumed inside it except
            // reference.SerializeSpeech, which its retry can flip.
            var engineAttempt = await RunEngineAttemptAsync(
                job,
                config,
                ffsubsyncExe,
                videoPath,
                referenceArg,
                reference.Spec,
                reference.UsedSubtitleReference,
                engineInput,
                tempOutput,
                tempDir,
                reference.SerializeSpeech,
                reference.Stream,
                reference.UsingCachedSpeech,
                reference.SpeechKey,
                reference.Path,
                cancellationToken).ConfigureAwait(false);
            reference.SerializeSpeech = engineAttempt.SerializeSpeech;

            // P5-5: the engine's own answer for this run, in milliseconds. It is the fallback signal for every
            // guard below, and it exists precisely because the guards used to have only one: the measured change
            // between what the engine was given and what it wrote (MeasureSyncChange). That measurement needs at
            // least three cues and an unchanged cue count, so for a forced/signs track - or for the plugin's own
            // sparse output - it is null, and every guard keyed on it silently did nothing. Measured in the
            // field on 2026-09-18: job 3a8fbe3d… (Solsidan S09E01) took a −149,99 s answer from a sibling
            // subtitle ruler with a negative score, reported `change=unknown` and wrote a 98-byte sidecar over
            // the library's own; job 155dd60e… wrote 124 bytes the same way. Both are the same hole: the answer
            // was pinned at the ±150 s search window, the documented ceiling for a subtitle ruler is 30 s, and
            // nothing looked because nothing could be measured.
            long? engineShiftMs = engineAttempt.OffsetSeconds is { } engineOffset
                ? (long)Math.Round(engineOffset * 1000.0)
                : null;


            // A stretch is a claim about the whole timeline, and only the film's audio can test it: another
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

            if (!File.Exists(tempOutput) && engineInput != subtitleInputPath && File.Exists(engineInput))
            {
                // The engine suppressed its write because the subtitle it was handed - the copy the plugin
                // rescaled onto the reference's time base - needed no further shift. That copy is the fix the
                // user asked for: the rescaled subtitle becomes the result, instead of the run reporting
                // "already in sync" while the user's own PAL-timed file is left as it was. The checks below see
                // it exactly as they would an engine output, including the pair rule.
                File.Copy(engineInput, tempOutput, overwrite: true);
                PluginLog.Info(
                    $"[{job.Id}] framerate: the engine needed no further shift on the rescaled subtitle "
                    + "\u2014 that rescaled timing is the result");
                _logger.LogInformation(
                    "Sync job {JobId}: the subtitle was rescaled onto the reference's time base and aligned; using it as the output",
                    job.Id);
            }

            if (!File.Exists(tempOutput))
            {
                // ffsubsync suppresses writing when the detected shift is below
                // its threshold (default 3 s) — the subtitle is effectively
                // already in sync, so this is a success, not a failure.
                CompleteAlreadyInSync(job, cuesNote);
                return;
            }

            _logger.LogInformation("ffsubsync produced synced subtitle ({Size} bytes)", new FileInfo(tempOutput).Length);

            // ffsubsync writes an output file even when the timings come out identical, which produced
            // a ".SYNCED" sidecar with the same timing as its source: no benefit, one more subtitle
            // track in the library. Measured before anything is written, so nothing is touched.
            // Measured against what the engine was actually given. When the plugin rescaled a framerate-mismatched
            // subtitle itself (engineInput differs), comparing the original with the output made a correct fix look
            // like a growing shift - the median difference between two differently scaled timelines is about half
            // the file's drift, 55.4 s on the 50-minute fixture - and the reference ceiling refused a file that had
            // just been fixed. Comparing the engine's own input keeps that check about the alignment.
            var measured = AlignmentMetrics.MeasureSyncChange(engineInput, tempOutput);

            // A shift that came from a subtitle reference is only ever as good as that track: a
            // reference taken from a different cut drags every subtitle of the file onto it, and the
            // file that comes out looks exactly like an ordinary success. AGENTS.md has documented
            // MaxSubtitleReferenceOffsetSeconds as a refusal since the reference path was added, so
            // this is that refusal — with the measured numbers, and without touching anything.
            var referenceCeilingMs = Math.Max(1.0, Configuration.SettingsValidation.MaxSubtitleReferenceOffsetSecondsOf(config)) * 1000.0;

            // ...and a median is not enough to tell a ruler that fits from one that does not. Measured on
            // 2026-09-15: a subtitle aligned against the *same track from a 2 % longer cut* came out with a median
            // shift of -26,52 s - under the 30 s ceiling above, so nothing refused it - while its per-cue
            // displacement had an interquartile range of 27,76 s; the same file against its real sibling track
            // measured an IQR of 0,00 s. The spread is what separates them, and it is the only signal in the
            // plugin that sees this case: the engine's own score rated the wrong ruler *higher* (274 721 against
            // 198 713). So a subtitle ruler is also refused when its cues did not move together.
            var spreadCeilingMs = referenceCeilingMs * AlignmentMetrics.SubtitleReferenceSpreadFraction;
            var rulerSpreadTooWide = measured is { } spread && AlignmentMetrics.RulerSpreadTooWide(spread, referenceCeilingMs);

            // P5-5: the ruler's demand is judged on the measured change when there is one, and on the engine's
            // own reported answer when there is not. Before this, a subtitle whose change could not be measured
            // at all - a forced/signs track, or the plugin's own sparse output - was exempt from this ceiling
            // entirely, which is how a −149,99 s answer from a sibling ruler and a 98-byte sidecar reached the
            // library (job 3a8fbe3d…, 2026-09-18).
            var rulerDemandMs = measured?.ShiftMs ?? engineShiftMs;
            var rulerDemandFromEngine = measured is null && engineShiftMs is not null;
            if (reference.UsedSubtitleReference
                && rulerDemandMs is { } fromReference
                && (Math.Abs(fromReference) > referenceCeilingMs || rulerSpreadTooWide))
            {
                // The measurement is right and the conclusion was incomplete: a subtitle reference cannot be trusted
                // for a shift this size (it is very likely from a different cut), but the film's own audio cannot be
                // a wrong cut at all. Instead of refusing the subtitle because of the track it happened to be
                // aligned against, drop that track as a ruler and align this subtitle against the audio. The track
                // is discarded so the file's other subtitles do not repeat the same measurement, and the audio
                // analysis is paid once per file, cached like every other audio path.
                var detail = Math.Abs(fromReference) > referenceCeilingMs
                    ? $"aligned to the reference subtitle {reference.Spec} at {fromReference} ms"
                        + (rulerDemandFromEngine
                            ? " (the engine's own answer — the subtitle is too sparse for the change between it and "
                                + "its synced output to be measured, so the ruler is judged on what the engine said)"
                            : string.Empty)
                    : $"the cues did not move together against {reference.Spec}: a spread of "
                        + $"{measured!.Value.SpreadMs / 1000.0:0.00} s across the middle half of its cues "
                        + $"(range {measured.Value.RangeMs / 1000.0:0.00} s) while the median was "
                        + $"{measured.Value.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: the reference subtitle is not the same cut ({Detail}) - aligning against the audio instead",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id}: the reference subtitle {reference.Spec} is not the same cut ({detail}, over the "
                    + $"{referenceCeilingMs / 1000.0:0.#} s limit for a subtitle reference, itself under the "
                    + $"{spreadCeilingMs / 1000.0:0.#} s spread limit) \u2014 discarding that track as a ruler and "
                    + $"aligning against the audio instead, file={video.Path}");

                if (reference.Spec is not null)
                {
                    ReferenceStore.Discard(videoPath, reference.Spec);
                }

                var audioReference = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "the reference subtitle is not the same cut as the video", cancellationToken).ConfigureAwait(false);

                // S45: the audio retry writes to a path of its own. It used to be handed `tempOutput` - the file
                // the *discarded* ruler's run had just written - and the test afterwards was
                // `audioExit == 0 && File.Exists(tempOutput)`, so an audio run that exited 0 without writing
                // (the engine suppresses its write when the shift is under its threshold, which is exactly what
                // "this subtitle already matches the film" looks like) left that stale file in place: the job
                // then wrote the discarded ruler's answer and reported it as the audio's. With a path of its own,
                // "did the audio write this?" is answerable again, because nothing else can have. The wide-window
                // and cross-check retries below have always used their own paths for the same reason.
                var audioOutput = Path.Combine(tempDir, "audio-fallback.srt");
                SafeDelete(audioOutput);
                var audioArgs = BuildFfSubSyncArgs(
                    config, audioReference, subtitleInputPath, audioOutput, tempDir, reference.SerializeSpeech, null,
                    vadOverride: AudioReferenceVad);

                double? audioScore = null;
                double? audioOffsetSeconds = null;
                var audioExit = await _processes.RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, audioArgs, tempDir,
                    line =>
                    {
                        if (TryParseEngineScore(line, out var parsedScore))
                        {
                            audioScore = parsedScore;
                        }

                        if (TryParseEngineOffset(line, out var parsedOffset))
                        {
                            audioOffsetSeconds = parsedOffset;
                        }
                    },
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), "audio")).ConfigureAwait(false);
                LogEngineAlignment(job.Id, "the audio", audioScore, audioOffsetSeconds);

                if (audioExit == 0 && File.Exists(audioOutput))
                {
                    if (reference.SpeechKey is not null && reference.SerializeSpeech)
                    {
                        SpeechCache.Harvest(audioReference, reference.SpeechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    reference.UsedSubtitleReference = false;
                    reference.Spec = null;
                    reference.Stream = null;
                    referenceArg = audioReference;
                    audioFallback = true;
                    tempOutput = audioOutput;
                    measured = AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);
                    // P5-5: the answer that now matters is the audio run's, so the fallback signal moves with it.
                    // A leftover offset from the discarded ruler's run would put the window check and the audit
                    // below back on the answer that was just rejected.
                    engineShiftMs = audioOffsetSeconds is { } audioOffset
                        ? (long)Math.Round(audioOffset * 1000.0)
                        : null;
                    PluginLog.Info(
                        $"[{job.Id}] reference: method=audio why=the reference subtitle was not the same cut "
                        + $"(it demanded {fromReference} ms)");
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
                    RefuseJob(
                        job,
                        "Refused",
                        $"refused: the subtitle was aligned against the file's own subtitle track {reference.Spec}, "
                            + $"which demanded a {fromReference} ms shift — that track is not the same cut — and "
                            + "aligning against the audio instead produced nothing. Nothing was written.",
                        tempOutput);
                    return;
                }
            }

            // This ceiling is ffsubsync's search window, not a safety limit: an answer outside it cannot be seen, and
            // the engine then returns the best wrong one - which is how a subtitle needing ~112 s came back as 56 s.
            // So a result that reaches the window gets one more alignment with a wider one, and that result is only
            // accepted when it is *not* pinned to the wider window either. A definitive answer, then one alignment
            // against the film's audio as a last check: anything a wide window matched wrongly shows up there as a
            // large remaining shift, and the job refuses with both numbers instead of writing it.
            var ceilingMs = Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 1000.0;
            // P5-5: the same fallback as the ruler ceiling above - a result pinned at the search window has to be
            // retried wider even when the change could not be measured, because "the engine's answer sits exactly
            // on the edge of what it was allowed to look for" is a property of its own answer, not of the
            // comparison. Before this, a sparse subtitle whose answer was pinned at ±150 s was written as-is.
            var onCeilingShiftMs = measured?.ShiftMs ?? engineShiftMs;
            if (!wideAllowanceApplied && onCeilingShiftMs is { } onCeilingShift && Math.Abs(onCeilingShift) >= ceilingMs - 500)
            {
                var wideSeconds = Math.Max(Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 2, 300);
                var wideLimitMs = wideSeconds * 1000.0;
                var wideOutput = Path.Combine(tempDir, "wide-window.srt");
                SafeDelete(wideOutput);
                var wideArgs = BuildFfSubSyncArgs(
                    config, referenceArg, engineInput, wideOutput, tempDir, reference.SerializeSpeech, reference.Stream,
                    VadForReference(reference.Spec));
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] =
                    wideSeconds.ToString(CultureInfo.InvariantCulture);

                _logger.LogInformation(
                    "Sync job {JobId}: the result reached the {Window} s search window - aligning again with {Wide} s",
                    job.Id,
                    Configuration.SettingsValidation.MaxOffsetSecondsOf(config),
                    wideSeconds);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the alignment reached the {Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s search window "
                    + $"(it measured {onCeilingShift} ms, which a window that size cannot be trusted to have found"
                    + (measured is null && engineShiftMs is not null ? ", and this is the engine's own answer - the change could not be measured" : string.Empty)
                    + $") \u2014 aligning again with {wideSeconds} s and checking the result against the film's audio");

                var wideErrors = new List<string>();
                var wideExit = await _processes.RunProcessWithStderrCallbackAsync(
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
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), reference.Stream ?? "(default)")).ConfigureAwait(false);

                var wideChange = wideExit == 0 && File.Exists(wideOutput)
                    ? AlignmentMetrics.MeasureSyncChange(engineInput, wideOutput)
                    : null;

                if (wideChange is { } wider && Math.Abs(wider.ShiftMs) < wideLimitMs - 500)
                {
                    // The wider window produced an answer inside itself: the engine is not clamped any more.
                    var verifyOutput = Path.Combine(tempDir, "wide-check.srt");
                    SafeDelete(verifyOutput);
                    var verifyArgs = BuildFfSubSyncArgs(
                        config, referenceArg, wideOutput, verifyOutput, tempDir, reference.SerializeSpeech, reference.Stream,
                        VadForReference(reference.Spec));
                    // A check, not a search: the configured window and no rescaling.
                    verifyArgs[verifyArgs.IndexOf("--max-offset-seconds") + 1] =
                        Configuration.SettingsValidation.MaxOffsetSecondsOf(config).ToString(CultureInfo.InvariantCulture);
                    foreach (var flag in FramerateArgs(false, false))
                    {
                        if (!verifyArgs.Contains(flag))
                        {
                            verifyArgs.Add(flag);
                        }
                    }

                    verifyArgs.Remove("--gss");

                    var verifyExit = await _processes.RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe, verifyArgs, tempDir, null, cancellationToken,
                        new EngineWatch(job.Id, Path.GetFileName(videoPath), reference.Stream ?? "(default)")).ConfigureAwait(false);
                    var residual = verifyExit == 0 && File.Exists(verifyOutput)
                        ? AlignmentMetrics.MeasureSyncChange(wideOutput, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (verifyExit == 0
                        && AlignmentMetrics.AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                    {
                        wideAllowanceApplied = true;
                        tempOutput = wideOutput;
                        measured = wider;
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the {wider.ShiftMs} ms result holds against the film's audio "
                            + $"(a further {residualShift} ms, no rescale) \u2014 writing it");
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
                            $"job {job.Id} REFUSED: this subtitle needed {onCeilingShift} ms with a "
                            + $"{Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s window and {wider.ShiftMs} ms with {wideSeconds} s, and {why}; "
                            + $"nothing written, source untouched, file={video.Path}");
                        RefuseJob(
                            job,
                            "Refused",
                            $"refused: this subtitle is further out than the {Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s search "
                                + $"window ({onCeilingShift} ms reached it), the {wideSeconds} s window measured "
                                + $"{wider.ShiftMs} ms, and that did not hold up against the film's audio: {why}. Nothing "
                                + "was written.",
                            tempOutput);
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

                    // S22: the engine's own words decide which refusal this is. A run that could not open the
                    // reference it was handed is not a search-window problem, and the field's log shows this was
                    // 7 of 8 refusals - every one of them sending the user to "Maximum offset", a setting that
                    // cannot help a file that could not be read.
                    if (EngineCouldNotReadReference(engineTail))
                    {
                        _logger.LogWarning(
                            "Sync job {JobId}: refusing after the wider window - the engine could not read the reference it was handed ({Detail})",
                            job.Id,
                            detail);
                        PluginLog.Info(
                            $"job {job.Id} REFUSED: the engine could not read the reference it was handed "
                            + $"({referenceArg}){engineTail}; this is not a search-window problem, so \"Maximum offset\" "
                            + $"is not the setting to change; nothing written, source untouched, file={video.Path}");
                        RefuseJob(
                            job,
                            "Refused",
                            "refused: the engine could not read the reference it was aligned against "
                                + $"({referenceArg}){engineTail}. Nothing was written. Raising \"Maximum offset\" will "
                                + "not help this - the reference could not be opened, so the alignment had nothing to "
                                + "measure against.",
                            tempOutput);
                        return;
                    }

                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the wider window ({Detail}) — nothing written",
                        job.Id,
                        detail);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle is further out than the plugin is searching "
                        + $"({detail}); raise \"Maximum offset\" and run it again, or sync it by hand. Nothing "
                        + $"written, source untouched, file={video.Path}");
                    RefuseJob(
                        job,
                        "Refused",
                        $"refused: {detail}. Raise \"Maximum offset\" in the plugin settings (it is the search "
                            + "window the alignment may look in) and run it again. Nothing was written.",
                        tempOutput);
                    return;
                }
            }

            // Nothing destructive is ever written: a measured rescale that was not asked for (or that
            // is not a real framerate pair) means the engine moved the timeline, and the source
            // subtitle stays untouched while the job says exactly why. A *piecewise* answer is the one
            // exception, and only when the plugin asked for one (C2): see PiecewiseHolds.
            var piecewiseRequested = Configuration.SettingsValidation.SplitPenaltyOf(config) > 0;
            var structure = piecewiseRequested && measured is not null
                ? AlignmentMetrics.MeasureSegmentStructure(engineInput, tempOutput, AlignmentMetrics.EngineSampleMs)
                : null;
            var piecewiseHolds = structure is { } pieces
                && AlignmentMetrics.PiecewiseHolds(pieces, spreadCeilingMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 1000.0);
            if (piecewiseHolds)
            {
                _logger.LogInformation(
                    "Sync job {JobId}: the piecewise reading holds ({Segments} segment(s), {Spread} ms within a segment at most) - writing it",
                    job.Id,
                    structure!.Value.Segments,
                    structure.Value.MaxWithinSpreadMs);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the engine's answer is piecewise ({structure.Value.Segments} segment(s), at most "
                    + $"{structure.Value.MaxWithinSpreadMs} ms of spread inside one, largest step {structure.Value.LargestShiftMs} ms) "
                    + $"- the linear reading of it is {measured!.Value.Ratio:0.0000}x, which is a step and not a rescale");
            }

            if (measured is { } scaled
                && !piecewiseHolds
                && !AlignmentMetrics.IsRescaleAcceptable(scaled.Ratio, scaled.ShiftMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config), config.FixFramerate))
            {
                var span = videoDuration > TimeSpan.Zero
                    ? videoDuration.TotalSeconds
                    : (double?)null;
                var detail = span is null
                    ? scaled.Describe()
                    : $"{scaled.Describe()} over a {span.Value / 60.0:0.0}-minute file";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a rescaled result ({Detail}) \u2014 nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: measured {detail} \u2014 framerate correction is "
                    + (config.FixFramerate ? "on but this is not a framerate pair" : "off")
                    + $"; nothing written, source untouched, file={video.Path}");
                RefuseJob(
                    job,
                    "Refused",
                    $"refused: the engine rescaled the timings ({detail}) and nothing was written. "
                        + (config.FixFramerate
                            ? "This is not a framerate pair a release could really have."
                            : "Turn on \"Correct framerate mismatch\" only for subtitles from a different framerate."),
                    tempOutput);
                return;
            }

            var suspiciousMs = referenceCeilingMs * SuspiciousReferenceShiftFraction;
            var agreementMs = referenceCeilingMs * AlignmentMetrics.SubtitleReferenceAudioAgreementFraction;
            if (reference.UsedSubtitleReference
                && measured is { } fromReferenceNote
                && Math.Abs(fromReferenceNote.ShiftMs) > suspiciousMs)
            {
                // The plugin's own words for a shift this size are "usually means that track is not the same cut",
                // and until now it wrote the result anyway with that note on it. Instead, ask the one ruler that
                // cannot be a different cut - the film's own audio - and let the two answers decide. It costs one
                // audio analysis, cached per file like every other audio path, and only for shifts in this band.
                cuesNote = Join(cuesNote, $"aligned to the reference subtitle {reference.Spec} at {fromReferenceNote.ShiftMs} ms - "
                    + "worth checking, a shift this size usually means the reference track is not the same cut");
                _logger.LogInformation(
                    "Sync job {JobId}: aligned to the reference subtitle {Reference} at {Shift} ms",
                    job.Id,
                    reference.Spec,
                    fromReferenceNote.ShiftMs);
                PluginLog.Info(
                    $"[{job.Id}] note: aligned to the reference subtitle {reference.Spec} at {fromReferenceNote.ShiftMs} ms "
                    + "- check the result; a shift this size usually means that track is not the same cut");


                // The ruler's shape score is logged as context only. It was built as a gate in front of this
                // cross-check and rejected, on measurement rather than taste: a ruler that is the same cut as the
                // film but offset from it correlates with the target perfectly and scores exactly like a correct
                // ruler (0.833 on the S31 fixture at +20 s, which is right, and at +25 s, which is wrong), so a
                // score-based skip writes a wrong file in precisely the case this cross-check exists for. The
                // score assumes the peak is centred on the search window too, so a 0.95 bar is unreachable for any
                // ruler that asks for a real shift (0.833 is the ceiling). See docs/EVIDENCE_s31_shape_gate.md.
                var rulerShape = SubtitleRulerShape.Score(
                    engineInput,
                    referenceArg,
                    Configuration.SettingsValidation.MaxOffsetSecondsOf(config));
                PluginLog.Info(
                    $"[{job.Id}] ruler shape: "
                    + (rulerShape is { } shape ? shape.Describe() : "not measurable (the two tracks could not be read as subtitles)")
                    + " \u2014 context only; the audio cross-check runs either way");

                var crossCheckOutput = Path.Combine(tempDir, "audio-cross-check.srt");
                SafeDelete(crossCheckOutput);
                var crossReference = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "the reference subtitle asked for a shift worth checking", cancellationToken).ConfigureAwait(false);
                // `webrtc`, not the configured VAD: with the default `subs_then_webrtc` the engine takes the
                // video's *embedded subtitles* as the speech signal, and the ruler being cross-checked is one of
                // them - measured 2026-09-15, the "audio" run then returned the ruler's own answer (24 170 ms
                // against the film's real -0,08 s), i.e. it confirmed the very track it was meant to check.
                var crossArgs = BuildFfSubSyncArgs(
                    config, crossReference, subtitleInputPath, crossCheckOutput, tempDir, reference.SerializeSpeech, null,
                    vadOverride: AudioReferenceVad);
                double? crossScore = null;
                double? crossOffset = null;
                PluginLog.Info(
                    $"[{job.Id}] cross-check run: reference={crossReference} input={subtitleInputPath} "
                    + $"args={string.Join(' ', crossArgs)}");
                var crossExit = await _processes.RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, crossArgs, tempDir,
                    line =>
                    {
                        if (TryParseEngineScore(line, out var parsedCrossScore))
                        {
                            crossScore = parsedCrossScore;
                        }

                        if (TryParseEngineOffset(line, out var parsedCrossOffset))
                        {
                            crossOffset = parsedCrossOffset;
                        }
                    },
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), "audio")).ConfigureAwait(false);
                LogEngineAlignment(job.Id, "the audio (cross-check of a subtitle ruler)", crossScore, crossOffset);
                PluginLog.Info(
                    $"[{job.Id}] cross-check exit={crossExit}, output={crossCheckOutput} "
                    + $"exists={File.Exists(crossCheckOutput)}");

                var fromAudio = crossExit == 0 && File.Exists(crossCheckOutput)
                    ? AlignmentMetrics.MeasureSyncChange(subtitleInputPath, crossCheckOutput)
                    : null;
                if (fromAudio is { } audioChange)
                {
                    if (reference.SpeechKey is not null && reference.SerializeSpeech)
                    {
                        SpeechCache.Harvest(crossReference, reference.SpeechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    var disagreementMs = Math.Abs(audioChange.ShiftMs - fromReferenceNote.ShiftMs);
                    if (AlignmentMetrics.RulersDisagree(fromReferenceNote.ShiftMs, audioChange.ShiftMs, referenceCeilingMs))
                    {
                        // The two rulers disagree about this film, and only one of them can be a different cut.
                        File.Copy(crossCheckOutput, tempOutput, overwrite: true);
                        if (reference.Spec is not null)
                        {
                            ReferenceStore.Discard(videoPath, reference.Spec);
                        }

                        var wasSpec = reference.Spec;
                        reference.UsedSubtitleReference = false;
                        reference.Spec = null;
                        reference.Stream = null;
                        referenceArg = crossReference;
                        audioFallback = true;
                        measured = AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);
                        cuesNote = null;
                        _logger.LogWarning(
                            "Sync job {JobId}: the reference subtitle and the film's own audio disagree ({Ruler} ms against {Audio} ms) - writing the audio's answer",
                            job.Id,
                            fromReferenceNote.ShiftMs,
                            audioChange.ShiftMs);
                        PluginLog.Info(
                            $"[{job.Id}] the reference subtitle {wasSpec} and the film's own audio disagree "
                            + $"({fromReferenceNote.ShiftMs} ms against {audioChange.ShiftMs} ms, over the "
                            + $"{agreementMs / 1000.0:0.#} s they are allowed to differ) \u2014 that track is not this "
                            + $"film's timeline, so it is discarded as a ruler and the audio's answer is written, file={video.Path}");
                        PluginLog.Info(
                            $"[{job.Id}] reference: method=audio why=the reference subtitle and the film's own audio "
                            + $"disagreed by {disagreementMs} ms");
                    }
                    else
                    {
                        PluginLog.Info(
                            $"[{job.Id}] the reference subtitle's shift ({fromReferenceNote.ShiftMs} ms) is confirmed by "
                            + $"the film's own audio ({audioChange.ShiftMs} ms, within {agreementMs / 1000.0:0.#} s) "
                            + "- keeping the reference's answer");
                    }
                }
                else
                {
                    PluginLog.Info(
                        $"[{job.Id}] the audio cross-check of the reference subtitle produced no alignment "
                        + $"(exit {crossExit}): the shift stands as noted, nothing new is decided");
                }
            }

            // "Changed nothing" is about the subtitle the user has, so it is measured against that: when the
            // plugin rescaled a framerate-mismatched subtitle, the engine's input and its output are identical by
            // definition, and comparing those two reported "+0 ms offset - no sidecar written" while the user's
            // own file was still PAL-timed. The alignment guards above keep using what the engine was given.
            var changedForUser = engineInput == subtitleInputPath
                ? measured
                : AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);

            if (changedForUser is { IsNoChange: true } noChange)
            {
                CompleteAsNoChange(job, noChange, cuesNote, tempOutput);
                return;
            }

            // S8, settled with the user on 2026-09-11: a result whose only ruler was the audio cannot be
            // checked against anything. Measured on this project's own fixture, a subtitle that was
            // already in sync came back "+1780 ms offset" from the audio alone and was written as a
            // plain success. For a track *inside* the file that is a guess, so nothing is written and
            // the job says exactly that. An external sidecar has no other ruler to fall back on — the
            // audio IS its reference — so that path still writes, as it always has.
            if (!reference.UsedSubtitleReference && !subtitleStream.IsExternal)
            {
                var detail = measured is { } audioOnly ? audioOnly.Describe() : "no measurable change";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing to write an audio-only alignment ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} UNVERIFIED: the audio was the only ruler ({detail}) and this track has no "
                    + $"reference subtitle to check it against; nothing written, source untouched, file={video.Path}");
                RefuseJob(
                    job,
                    "Unverified \u2014 audio-only alignment",
                    $"unverified: nothing was written. This track sits inside the video file, so the only "
                        + $"reference for it was the film's audio ({detail}) and there was no subtitle track to "
                        + "check that answer against. An audio-only alignment has nothing to cross-check it, and "
                        + "on a short file it can be out by a second or more. Run it again from an external .srt "
                        + "beside the video: an external subtitle's reference is the audio the plugin is meant to "
                        + "use, and a synced sidecar is written for it.",
                    tempOutput);
                return;
            }

            // P14-P16: guard the engine's output, write the synced subtitle next to the media, and confirm
            // what was written. The two values the rest of the job needs come back; everything else (the
            // target path, the phase, the progress) it records on the job itself.
            await WriteSyncedSubtitleAsync(
                job,
                config,
                subtitleStream,
                videoDir,
                videoNameNoExt,
                videoPath,
                tempOutput,
                write).ConfigureAwait(false);
            changedDir = write.ChangedDir;

            // P17: describe what changed - the offset or rescale factor, the signs note, where the replaced
            // original was kept - and mark the job complete.
            DescribeCompletedSync(
                job,
                subtitleStream,
                subtitleInputPath,
                engineInput,
                write.BackupPath,
                cuesNote,
                audioFallback,
                stretchDropped);

            // P18: announce it (size probe, completion log, folder report, item refresh). Best-effort by
            // design: the subtitle is already on disk, so a library hiccup must not fail a finished job.
            await AnnounceCompletedAsync(job, video, changedDir).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkCancelled(job);
        }
        catch (Exception ex)
        {
            FailJobAndRollBack(job, ex, write.BackupPath);
        }
        finally
        {
            CleanUpAfterJob(job, video, tempDir, reference.SerializeSpeech, reference.SpeechKey);
        }
    }

    /// <summary>
    /// What one engine attempt produced: whether the attempt that mattered analysed the speech itself (the caller
    /// needs that to know which speech-cache entry the run belongs to), and the score and offset the engine itself
    /// printed.
    /// </summary>
    /// <remarks>
    /// The engine's own offset is carried out of here because it is the only alignment signal a job has when the
    /// change between the subtitle and its synced output cannot be measured (P5-5): <see
    /// cref="AlignmentMetrics.MeasureSyncChange"/> needs three cues and an unchanged cue count, so a forced/signs
    /// track - or the plugin's own sparse output - produces no measurement at all, and every guard that judged
    /// the ruler from that measurement was silently skipped until this value was available to them.
    /// </remarks>
    /// <param name="SerializeSpeech">Whether the attempt that mattered analysed the speech itself.</param>
    /// <param name="Score">The alignment score the engine printed, when it printed one.</param>
    /// <param name="OffsetSeconds">The offset the engine printed, when it printed one.</param>
    internal readonly record struct EngineAttempt(bool SerializeSpeech, double? Score, double? OffsetSeconds);

    /// <summary>
    /// Runs ffsubsync for this job's first attempt and, when a cached speech analysis turned out to be unusable,
    /// a second one from the audio. The stderr callbacks live inside this method on purpose: they mutate only
    /// locals here (the tail of what the engine printed, the score and offset it reported), which is what keeps
    /// the retry safe to read on its own.
    /// </summary>
    /// <returns>Whether the attempt that mattered analysed the speech itself, and the score and offset the engine
    /// printed.</returns>
    private async Task<EngineAttempt> RunEngineAttemptAsync(
        SyncJob job,
        PluginConfiguration config,
        string ffsubsyncExe,
        string videoPath,
        string referenceArg,
        string? referenceSpec,
        bool usedSubtitleReference,
        string engineInput,
        string tempOutput,
        string tempDir,
        bool serializeSpeech,
        string? referenceStream,
        bool usingCachedSpeech,
        string? speechKey,
        string referencePath,
        CancellationToken cancellationToken)
    {
        var args = BuildFfSubSyncArgs(
            config, referenceArg, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream,
            VadForReference(referenceSpec));

        _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);
        PluginLog.Info($"[{job.Id}] ffsubsync start: exe={ffsubsyncExe} cachedSpeech={usingCachedSpeech} reference={referenceStream ?? "(default)"} args={string.Join(' ', args)}");
        LogVadOverride(
            job.Id,
            referenceSpec is null ? "the audio" : $"the subtitle {referenceSpec}",
            config.VadMethod);

        // Parse ffsubsync stderr in real-time for progress updates.
        // tqdm format: " 42%|████▎     | 3000.0/6997.696 [00:27<00:34, 115.36it/s]"
        // Phase messages: "extracting speech...", "computing alignments...", "writing output..."
        var engineWatch = System.Diagnostics.Stopwatch.StartNew();
        // Keep the tail of stderr: "ffsubsync exited with code 1" on its own tells nobody anything,
        // and the reason (an unreadable reference, a subtitle with no text, a demux error) is
        // always in the last few lines ffsubsync printed.
        var engineErrors = new List<string>();
        double? engineScore = null;
        double? engineOffsetSeconds = null;
        var exitCode = await _processes.RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, args, tempDir,
            line =>
            {
                ParseFfSubSyncStderr(line, job);
                    if (TryParseEngineScore(line, out var parsedScore))
                    {
                        engineScore = parsedScore;
                    }

                    if (TryParseEngineOffset(line, out var parsedOffset))
                    {
                        engineOffsetSeconds = parsedOffset;
                    }

                    lock (engineErrors)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            engineErrors.Add(line.Trim());
                            if (engineErrors.Count > 6)
                            {
                                engineErrors.RemoveAt(0);
                            }
                        }
                    }
                },
            cancellationToken,
            new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);
        engineWatch.Stop();
        PluginLog.Info($"[{job.Id}] ffsubsync exit={exitCode} after {engineWatch.ElapsedMilliseconds} ms");
        LogEngineAlignment(
            job.Id,
            usedSubtitleReference ? $"reference subtitle {referenceSpec}" : "the audio",
            engineScore,
            engineOffsetSeconds);

        // A walk of the media measures its volume without taking any read to measure it, which is the only
        // signal that exists on a run whose extractions were all served from the subtitle cache. It only
        // counts when it is the volume being measured: a walk taken while another volume was being read
        // measures the moment as much as the storage, and believing it held a fast volume to two walks for
        // the rest of a mixed batch on 2026-09-14.
        if (!usedSubtitleReference)
        {
            var walked = MediaLengthOf(videoPath);
            if (walked > 0)
            {
                var walkedVolume = Services.VolumeProfiles.For(videoPath);
                var elsewhere = VolumesOtherThan(
                    MediaVolume.Of(videoPath),
                    _jobs.Values
                        .Where(j => j.Status == SyncJobStatus.Running)
                        .Select(j => MediaVolume.Of(
                            _jobContexts.TryGetValue(j.Id, out var other) ? other.Video.Path : null)));
                var walkedMbPerSec = walked / (engineWatch.ElapsedMilliseconds <= 0 ? 1.0 : engineWatch.ElapsedMilliseconds) / 1000.0;

                if (elsewhere > 0)
                {
                    PluginLog.Info(
                        $"[{job.Id}] this walk moved {walked / 1048576.0:0.0} MB of {videoPath} in "
                        + $"{engineWatch.ElapsedMilliseconds / 1000.0:0.0} s = {walkedMbPerSec:0.0} MB/s, but it is "
                        + $"not being used to judge that volume: {elsewhere} job(s) on another volume were being "
                        + "read at the same time, so this number belongs to the moment rather than to the storage");
                }
                else
                {
                    walkedVolume.ObserveWalk(walked, engineWatch.ElapsedMilliseconds);
                    var walkedCap = WalkCapForProfile(walkedVolume.MsPerCall(), walkedVolume.WalkBytesPerMs());
                    PluginLog.Info(
                        $"[{job.Id}] this walk moved {walked / 1048576.0:0.0} MB of {videoPath} in "
                        + $"{engineWatch.ElapsedMilliseconds / 1000.0:0.0} s = "
                        + $"{(walkedVolume.WalkBytesPerMs() ?? 0) / 1000.0:0.0} MB/s - "
                        + $"the ceiling for that volume is "
                        + $"{(walkedCap.Cap >= int.MaxValue ? "none" : walkedCap.Cap.ToString())} ({walkedCap.Why})");
                }
            }
        }

        if (exitCode != 0 && usingCachedSpeech && speechKey is not null)
        {
            // The cached speech file is unusable (deleted mid-run, truncated, or from
            // a different ffsubsync build). Drop it and redo the run from the audio.
            _logger.LogWarning(
                "Cached speech analysis failed for {Video} (exit code {Code}); falling back to a full audio run",
                videoPath, exitCode);
            var stale = SpeechCache.TryGet(speechKey);
            if (stale is not null)
            {
                try { File.Delete(stale); } catch (IOException) { /* retry below still works */ }
            }

            referencePath = SpeechCache.CreateReferenceLink(videoPath, speechKey);
            serializeSpeech = true;
            args = BuildFfSubSyncArgs(
                config, referenceArg, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream,
                VadForReference(referenceSpec));
            PluginLog.Info($"[{job.Id}] retrying ffsubsync from the audio: {string.Join(' ', args)}");
            lock (engineErrors)
            {
                engineErrors.Clear();
            }

            // The retry is the run that matters, so its own answer replaces the failed attempt's: a score or an
            // offset left over from a run that could not read the reference would describe a run nobody used.
            engineScore = null;
            engineOffsetSeconds = null;

            exitCode = await _processes.RunProcessWithStderrCallbackAsync(
                ffsubsyncExe, args, tempDir,
                line =>
                {
                    ParseFfSubSyncStderr(line, job);
                    if (TryParseEngineScore(line, out var retryScore))
                    {
                        engineScore = retryScore;
                    }

                    if (TryParseEngineOffset(line, out var retryOffset))
                    {
                        engineOffsetSeconds = retryOffset;
                    }

                    lock (engineErrors)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            engineErrors.Add(line.Trim());
                            if (engineErrors.Count > 6)
                            {
                                engineErrors.RemoveAt(0);
                            }
                        }
                    }
                },
                cancellationToken,
                new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);
            PluginLog.Info($"[{job.Id}] ffsubsync retry exit={exitCode}");
        }

        if (exitCode != 0)
        {
            string why;
            lock (engineErrors)
            {
                why = engineErrors.Count == 0 ? string.Empty : " Last output: " + string.Join(" | ", engineErrors);
            }

            throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.{why}");
        }

        if (speechKey is not null && serializeSpeech)
        {
            // Harvest covers the fallback where ffsubsync wrote the .npz next to the media file. The link
            // itself is not dropped here: the wider-window retry and the verification run later in this
            // same job are handed the same reference, and a retry pointed at a file the plugin deleted
            // under it cannot start (S46 - measured in the field, 7 of one run's 8 refusals). It is
            // dropped once, in this job's finally.
            SpeechCache.Harvest(referencePath, speechKey);
            SpeechCache.Prune();
        }

        ReleaseSpeechGate(job, videoPath);
        return new EngineAttempt(serializeSpeech, engineScore, engineOffsetSeconds);
    }

    /// <summary>
    /// Writes the engine's result next to the media and confirms it landed: a dot-separated sidecar in copy mode,
    /// an in-place replace with a backup in replace mode, or a new sidecar for an embedded track. The engine's
    /// output is checked before anything is copied, because copy mode used to leave a 0-byte sidecar in the
    /// library for a job that then failed.
    /// </summary>
    /// <param name="job">The job, whose output path and progress this sets.</param>
    /// <param name="config">The plugin settings, for the copy/replace choice.</param>
    /// <param name="subtitleStream">The track being synced, for its path and language.</param>
    /// <param name="videoDir">The media file's folder, where an embedded track's sidecar goes.</param>
    /// <param name="videoNameNoExt">The media file's name without extension, for that sidecar's name.</param>
    /// <param name="videoPath">The media file, named in the log line for an embedded track.</param>
    /// <param name="tempOutput">The engine's output in the job's temp directory.</param>
    /// <param name="outcome">Filled in as the write proceeds. It is written into rather than returned because the
    /// caller's failure path needs the backup path precisely when the write throws - and a throw returns nothing.</param>
    private async Task WriteSyncedSubtitleAsync(
        SyncJob job,
        PluginConfiguration config,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        string videoDir,
        string videoNameNoExt,
        string videoPath,
        string tempOutput,
        SyncWriteOutcome outcome)
    {
        // Step 2b: the result is checked *before* anything is written next to the media. The engine
        // writes an output file even when it holds nothing — an embedded track with no text is the
        // usual case — and copying it first left a 0-byte ".SYNCED.eng.srt" in the library for a job
        // that then failed: the next library scan logs `FfmpegException: ffprobe failed - streams and
        // format are both null` for it, and nothing ever removes it. Nothing is copied until the
        // engine's own output is known to hold subtitles.
        if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
        {
            throw new InvalidOperationException(
                "Subtitle verification failed — synced output is missing or empty. Nothing was written next to "
                + "the media: the engine produced no subtitles for this track.");
        }

        // Step 3: Save the synced subtitle (copy mode by default — original untouched)
        if (subtitleStream.IsExternal && !string.IsNullOrEmpty(subtitleStream.Path))
        {
            if (config.SyncModeCopy)
            {
                job.Phase = "Saving synced copy";
                job.Progress = 0.85;

                var original = subtitleStream.Path;
                var dir = Path.GetDirectoryName(original) ?? ".";
                var stem = Path.GetFileNameWithoutExtension(original);
                // Jellyfin recognises a sidecar only when it starts with the exact media
                // filename and continues with DOT-separated fields (see the media naming
                // docs: "Film.mkv" -> "Film.en.sdh.srt"). A hyphenated marker
                // ("...-SYNCED.srt") leaves Jellyfin unable to associate the file with the
                // video, so nothing appears in the interface. The marker is therefore a
                // field, not part of the name.
                var lang = string.IsNullOrWhiteSpace(subtitleStream.Language)
                    ? null
                    : subtitleStream.Language.Trim().ToLowerInvariant();

                // A stem the plugin already marked loses that marker first (S12): re-syncing its own output updates
                // that file, where appending a second marker wrote a name no player associates with the episode.
                var target = SyncedTargetNaming.SyncedTargetName(dir, stem, lang);

                SyncedTargetNaming.RequireWritable(dir);
                File.Copy(tempOutput, target, overwrite: true);
                job.OutputPath = target;
                outcome.ChangedDir = dir;
                _logger.LogInformation("Synced copy written: {Original} → {Target} (stem={Stem}, lang={Lang}, original untouched)", original, target, stem, lang ?? "(none)");
            }
            else
            {
                job.Phase = "Replacing subtitle";
                job.Progress = 0.85;

                SyncedTargetNaming.RequireWritable(Path.GetDirectoryName(subtitleStream.Path) ?? ".");

                // The backup path is chosen (and therefore known to the rollback below) *before*
                // the original is touched: the destructive copy is inside ReplaceExternalSubtitle,
                // and a failure there used to leave `backupPath` null, so the only rollback there is
                // was skipped for exactly the case that needs it.
                outcome.BackupPath = NextBackupPath(subtitleStream.Path);
                await ReplaceExternalSubtitle(subtitleStream.Path, outcome.BackupPath, tempOutput).ConfigureAwait(false);
                outcome.ChangedDir = Path.GetDirectoryName(subtitleStream.Path) ?? ".";

                job.OutputPath = subtitleStream.Path;
                _logger.LogInformation("Replaced external subtitle: {Path} (original kept at {Backup})", subtitleStream.Path, outcome.BackupPath);
            }
        }
        else
        {
            // EMBEDDED track: the subtitle was extracted earlier and synced to
            // tempOutput. The result is saved as a NEW external sidecar next
            // to the video. Video files are NEVER modified — no remuxing, no
            // container rewrite. Jellyfin discovers the sidecar via the
            // folder rescan below; the original embedded stream stays intact.
            job.Phase = "Saving synced subtitle";
            job.Progress = 0.75;

            var lang = string.IsNullOrWhiteSpace(subtitleStream.Language)
                ? null
                : subtitleStream.Language.Trim().ToLowerInvariant();
            // Dot-separated fields after the exact video filename, or Jellyfin will not
            // associate the sidecar with the episode and it never shows up.
            var target = lang is not null
                ? Path.Combine(videoDir, $"{videoNameNoExt}.SYNCED.{lang}.srt")
                : Path.Combine(videoDir, $"{videoNameNoExt}.SYNCED.srt");

            SyncedTargetNaming.RequireWritable(videoDir);
            File.Copy(tempOutput, target, overwrite: true);
            job.OutputPath = target;
            outcome.ChangedDir = videoDir;
            _logger.LogInformation(
                "Embedded subtitle synced as new external file: {Target} (video {Video} untouched)",
                target, videoPath);
        }

        // Step 4: Verify
        job.Phase = "Verifying";
        job.Progress = 0.95;

        if (string.IsNullOrEmpty(job.OutputPath) ||
            !File.Exists(job.OutputPath) ||
            new FileInfo(job.OutputPath).Length == 0)
        {
            // Whatever this job wrote is removed again if it does not hold subtitles: an empty sidecar
            // left in the library is reported by every later scan ("offline ... streams and format are
            // both null") and the user has no way to tell where it came from. The user's own file is
            // never touched here — replace mode has its own backup and rollback.
            if (!string.IsNullOrEmpty(job.OutputPath)
                && !string.Equals(job.OutputPath, subtitleStream.Path, StringComparison.Ordinal))
            {
                SafeDelete(job.OutputPath);
            }

            throw new InvalidOperationException("Subtitle verification failed — synced output is missing or empty.");
        }
    }

    /// <summary>
    /// What writing a synced subtitle records for the rest of the job: the folder the library monitor should be
    /// told about, and the backup of a replaced original that the failure path rolls back from.
    /// </summary>
    /// <remarks>
    /// It is filled as the write proceeds rather than returned, because the rollback needs the backup path exactly
    /// when the write throws. The first attempt at this extraction returned a tuple and lost both values on that
    /// path - the replace-mode original was then never restored from the kept backup. The P19 check caught it.
    /// </remarks>
    private sealed class SyncWriteOutcome
    {
        /// <summary>Gets or sets the folder the library monitor should be told about, if the job wrote one.</summary>
        public string? ChangedDir { get; set; }

        /// <summary>Gets or sets the kept original in replace mode, if there is one.</summary>
        public string? BackupPath { get; set; }
    }

    /// <summary>
    /// Builds the sentence a completed job carries: what changed (offset or rescale factor), a note when the
    /// subtitle looks like a signs track, and where a replaced original was kept. Also marks the job complete.
    /// </summary>
    /// <param name="job">The job, whose outcome, phase, status and progress this sets.</param>
    /// <param name="subtitleStream">The track being synced, for its path and whether it is external.</param>
    /// <param name="subtitleInputPath">The subtitle the user had, before any rescale.</param>
    /// <param name="engineInput">What the engine was actually given, when the input was rescaled.</param>
    /// <param name="backupPath">The kept original in replace mode, if there is one.</param>
    /// <param name="cuesNote">The signs/forced note, if the subtitle looked like one.</param>
    /// <param name="audioFallback">Whether a subtitle ruler was discarded and the audio used instead.</param>
    /// <param name="stretchDropped">Whether a framerate stretch was dropped after failing the audio check.</param>
    private void DescribeCompletedSync(
        SyncJob job,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        string subtitleInputPath,
        string engineInput,
        string? backupPath,
        string? cuesNote,
        bool audioFallback,
        bool stretchDropped)
    {
        // Step 5: Success — describe what changed (offset ms / framerate). The replace-mode
        // backup is deliberately NOT deleted: a sync that succeeds while being wrong used to
        // leave the user with no way back, and the copy costs a few kilobytes. It is named
        // *.bak.subsync, which is not a subtitle extension, so Jellyfin never shows it as a
        // second track, and the result says where it is.
        var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
        if (job.OutputPath is not null)
        {
            job.Outcome = AlignmentMetrics.DescribeSyncChange(outcomeInput, job.OutputPath);
            if (audioFallback)
            {
                job.Outcome = "the file's own subtitle track is not the same cut, so this was aligned against the "
                    + "audio" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \u00b7 " + job.Outcome);
            }
            else if (stretchDropped)
            {
                job.Outcome = "the stretch did not hold against the film's audio, so the subtitle was aligned with "
                    + "offsets only" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \u00b7 " + job.Outcome);
            }
            else if (engineInput != subtitleInputPath)
            {
                // The subtitle the user had and the corrected file are on differently scaled timelines, so
                // describing the difference between them reports about half the film's drift ("change=+55388 ms")
                // for a correction that did what it was asked to. Say what was done instead: the factor, and the
                // alignment's own change measured on the timeline the engine worked in.
                var before = AlignmentMetrics.ParseSrtCueStarts(subtitleInputPath);
                var after = AlignmentMetrics.ParseSrtCueStarts(engineInput);
                var factor = before is { Count: > 2 } && after is { Count: > 2 }
                    ? (after[^1] - after[0]) / (before[^1] - before[0])
                    : 1.0;
                var aligned = AlignmentMetrics.DescribeSyncChange(engineInput, job.OutputPath);
                job.Outcome = $"stretched to {factor:0.#####}x onto the reference's timeline"
                    + (string.IsNullOrEmpty(aligned) ? string.Empty : ", " + aligned);
            }
        }

        if (cuesNote is not null)
        {
            job.Outcome = string.IsNullOrEmpty(job.Outcome)
                ? cuesNote
                : job.Outcome + " \u00b7 " + cuesNote;
        }

        if (backupPath is not null)
        {
            // Worth saying plainly: the original file was overwritten in place.
            var kept = Path.GetFileName(backupPath);
            job.Outcome = string.IsNullOrEmpty(job.Outcome)
                ? "original replaced \u2014 kept at " + kept
                : job.Outcome + " \u00b7 original replaced, kept at " + kept;
            _logger.LogInformation("Original subtitle kept at {Backup} (replace mode)", backupPath);
        }

        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
    }

    /// <summary>
    /// Announces a finished job: probes the size of what was written, logs the completion in both logs, tells the
    /// library monitor about the folder, and refreshes the item so its subtitle list is re-read. Every step is
    /// best-effort - the subtitle is already on disk, so a library hiccup must not turn a finished job into a
    /// failure, which is why this block does not sit inside the job's own try.
    /// </summary>
    /// <param name="job">The job that was written.</param>
    /// <param name="video">The item the subtitle belongs to.</param>
    /// <param name="changedDir">The folder to report, if the job wrote one.</param>
    private async Task AnnounceCompletedAsync(SyncJob job, Video video, string? changedDir)
    {
        // Check what is about to be announced. On a flaky share a write can disappear between
        // the copy and this line, and "completed" would then point at a file that is not there.
        long? outputSize = null;
        if (!string.IsNullOrEmpty(job.OutputPath))
        {
            try
            {
                var written = new FileInfo(job.OutputPath);
                if (written.Exists)
                {
                    outputSize = written.Length;
                }
            }
            catch (IOException)
            {
                // Unreadable size is not a failure; the existence check below decides.
            }
        }

        _logger.LogInformation(
            "Sync job {JobId} completed \u2014 wrote: {Output} ({Size}, {Outcome})",
            job.Id,
            job.OutputPath ?? "(no output path set)",
            outputSize is null ? "size unreadable" : $"{outputSize} bytes",
            job.Outcome ?? "unknown");

        LogPluginCompletion(job, outputSize);

        if (changedDir is not null && outputSize is null)
        {
            _logger.LogWarning(
                "The synced subtitle {Output} is not on disk, so the library was not told anything changed. Check the share and its permissions.",
                job.OutputPath ?? "(none)");
            changedDir = null;
        }

        // Tell Jellyfin about the new file, then refresh the item so its stream list is re-read.
        // Both are best-effort: the subtitle is already on disk, so a library hiccup must never
        // turn a finished job into a failure (this block used to sit in the job's own try, where
        // an exception marked the job FAILED and rolled the result back).
        //
        // The folder report is never skipped - it is what makes Jellyfin discover the file, and
        // Jellyfin coalesces repeats itself. The item refresh re-probes the media file, so it runs
        // once per item instead of once per subtitle track (see LibraryRefreshGate).
        if (changedDir is not null)
        {
            try
            {
                _libraryMonitor.ReportFileSystemChanged(changedDir);
                _logger.LogInformation("Reported the change to the Jellyfin library monitor: {Dir}", changedDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The library monitor rejected the change report for {Dir}; the subtitle is written and will appear after the next library scan", changedDir);
            }
        }

        if (_refreshGate.ShouldRefresh(video.Id))
        {
            try
            {
                await _libraryManager.UpdateItemAsync(
                    video,
                    video.GetParent(),
                    ItemUpdateType.MetadataImport,
                    CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Refreshed item {ItemId} so its subtitle list is re-read", video.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refreshing item {ItemId} failed; the subtitle is written and appears after the next scan", video.Id);
            }
        }
        else
        {
            _logger.LogDebug("Skipped the item refresh for {ItemId}: another subtitle of the same item was synced moments ago", video.Id);
        }
    }

    /// <summary>
    /// What resolving a job's reference decides, for everything downstream of it: the path the engine is handed,
    /// what that path is, and the speech-cache facts of the audio analysis this job either found or made.
    /// </summary>
    /// <remarks>
    /// It exists because the audio-reference builder is called from four places - two inside reference resolution
    /// and two outside it (the wrong-cut fallback and the suspicious-reference cross-check) - and because the job's
    /// <c>finally</c> reads <see cref="SpeechKey"/> and <see cref="SerializeSpeech"/> to drop the analysis link.
    /// A closure could reach those locals; a method cannot, so they travel in an object the caller owns and
    /// declares before the job's <c>try</c>.
    /// </remarks>
    private sealed class ReferenceResolution
    {
        /// <summary>Gets or sets the path handed to the engine as its reference. Never the media file itself
        /// (S11): the container is never handed over, because the engine would demux the whole thing itself.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets what that reference is: a stream spec such as <c>s:0</c>, or null when the
        /// reference is the file's own audio.</summary>
        public string? Spec { get; set; }

        /// <summary>Gets or sets the engine's reference-stream argument, when the reference is a stream.</summary>
        public string? Stream { get; set; }

        /// <summary>Gets or sets a value indicating whether the reference is a sibling subtitle this run built or
        /// reused, rather than the audio.</summary>
        public bool UsedSubtitleReference { get; set; }

        /// <summary>Gets or sets the speech-cache key of the analysis this job found or produced, if any.</summary>
        public string? SpeechKey { get; set; }

        /// <summary>Gets or sets a value indicating whether this job's run writes the analysis for others to
        /// reuse.</summary>
        public bool SerializeSpeech { get; set; }

        /// <summary>Gets or sets a value indicating whether the engine is given a stored analysis rather than the
        /// media file.</summary>
        public bool UsingCachedSpeech { get; set; }
    }

    /// <summary>
    /// Prepares the file's own audio as the job's reference: reuses the stored analysis when one exists, otherwise
    /// waits for the file's gate and does the analysis this job's run will produce.
    /// </summary>
    /// <param name="reference">The resolution being built; the speech-cache facts are set here.</param>
    /// <param name="job">The job, for its phase label and the speech gate marker.</param>
    /// <param name="videoPath">The media file the analysis belongs to.</param>
    /// <param name="why">Why the audio is being used, for the log line.</param>
    /// <param name="cancellationToken">Cancels the wait for the file's gate.</param>
    /// <returns>The path the engine should be given.</returns>
    private async Task<string> PrepareAudioReferenceAsync(
        ReferenceResolution reference,
        SyncJob job,
        string videoPath,
        string why,
        CancellationToken cancellationToken)
    {
        {
            // Identity of everything that shapes the speech signal: the binary in use,
            // the bundled engine version and this plugin's own version. A path alone is
            // not enough — an upgraded bundled binary keeps its path.
            var engineIdentity = string.Join(
                "|",
                ResolveFfSubSyncPath(),
                _engine.BundledFfSubSyncVersion,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
            // The analysis this key names is the one the engine will produce, so the key names the VAD the
            // engine is actually given for the audio path - not the configured one, which it is not given.
            reference.SpeechKey = SpeechCache.KeyFor(
                videoPath,
                AudioReferenceVad + "|audio",
                engineIdentity);
            var cached = SpeechCache.TryGet(reference.SpeechKey);
            if (cached is not null)
            {
                reference.UsingCachedSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: true, audioReference: true);
                _logger.LogInformation("Reusing the stored audio analysis for {Video} ({Why})", videoPath, why);
                PluginLog.Info($"[{job.Id}] reference: method=speech-cache why={why}");
                return cached;
            }

            // The analysis is per *file*, not per subtitle: hold the file's gate so the first job does
            // it and the rest reuse the harvest. They wait here rather than starting a second analysis.
            var speechGate = _speechGates.GetOrAdd(videoPath, _ => new SemaphoreSlim(1, 1));

            // Cancellable, and that is the point: this wait can last as long as the file's audio analysis
            // (a feature film's is over an hour), so a job parked here has to be reachable by the user's
            // Kill and by the stall watchdog. Without the token neither could end it, and the job held its
            // worker slot until the server was restarted (B6).
            await speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            job.HoldsSpeechGate = true;

            var harvestedWhileWaiting = SpeechCache.TryGet(reference.SpeechKey);
            if (harvestedWhileWaiting is not null)
            {
                speechGate.Release();
                job.HoldsSpeechGate = false;
                reference.UsingCachedSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: true, audioReference: true);
                _logger.LogInformation("Reusing the audio analysis another job stored for {Video} ({Why})", videoPath, why);
                PluginLog.Info($"[{job.Id}] reference: method=speech-cache why={why} (harvested by another job of this file while this one waited)");
                return harvestedWhileWaiting;
            }

            reference.SerializeSpeech = true;
            job.Phase = SyncPhaseLabel(fromCache: false, audioReference: true);
            PluginLog.Info($"[{job.Id}] reference: method=audio why={why} (this job does the file's analysis; the others wait for it)");
            return SpeechCache.CreateReferenceLink(videoPath, reference.SpeechKey);
        }
    }

    /// <summary>
    /// Resolves what this job is aligned against: the file's own audio (analysed once and reused through the speech
    /// cache) or a sibling subtitle track built here from text this process already has. The container itself is
    /// never handed to the engine (S11) - that makes it demux the whole file, once per job.
    /// </summary>
    /// <param name="reference">The resolution to fill in.</param>
    /// <param name="job">The job, for its phase label and the extraction/language context.</param>
    /// <param name="videoPath">The media file.</param>
    /// <param name="videoDurationForReference">The file's duration, used to spot a signs/forced track.</param>
    /// <param name="cancellationToken">Cancels the per-file waits.</param>
    private async Task ResolveReferenceAsync(
        ReferenceResolution reference,
        SyncJob job,
        string videoPath,
        TimeSpan videoDurationForReference,
        CancellationToken cancellationToken)
    {
        // The reference this job is aligned against is one of exactly two things: the file's own
        // audio (analysed once, reused through the speech cache) or a sibling subtitle track that
        // our own reader produced. What it must never be is the media file itself: passing the
        // container to ffsubsync makes the engine demux the whole thing with its own ffmpeg, once
        // per job. Measured on this fixture — a 2.38 GB episode — two jobs sat in that demux for 7
        // and 17 minutes and never finished, and on a bulk run that is what stops the batch ever
        // reaching the end (S11).
        var usesAudioReference = reference.Stream is null
            || reference.Stream.StartsWith("a:", StringComparison.Ordinal);

        // The speech signal depends on the media file, the VAD method and the engine
        // build — never on the subtitle, its language or the mode. Analysing it is work
        // that happens anyway, so it is always kept: the other subtitles of that file and
        // any later run then skip the audio pass entirely. This is also the fallback whenever a
        // subtitle reference cannot be built — the plugin never refuses a job, it changes the
        // ruler it measures against.
        if (usesAudioReference)
        {
            reference.Path = await PrepareAudioReferenceAsync(reference, job, videoPath, "the audio is this job's own reference", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // The reference is a sibling subtitle track, and it is built here from text this
            // process already has. Letting ffsubsync pull the stream out of the video instead is
            // not: it demuxes the whole file. Measured on an 8.2 GB episode: 12.5 s and 8218 MB
            // read, repeated for every subtitle.
            var referenceIdentity = string.Join(
                "|",
                ResolveFfSubSyncPath(),
                _engine.BundledFfSubSyncVersion,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
            var referenceOrdinal = MediaStreamMap.SubtitleStreamOrdinal(reference.Stream);
            reference.Spec = reference.Stream;

            // The reference lives in this run's own directory and is shared with the other
            // subtitles of this file while they are still going to use it. It is deliberately
            // never carried over from an earlier run: a reference taken from a sibling subtitle
            // inherits that track's own error, and every other track of the file then inherits it
            // in turn.
            var referenceTarget = referenceOrdinal >= 0
                ? ReferenceStore.Reserve(videoPath, reference.Stream!, referenceIdentity)
                : null;

            // One job at a time builds this file's reference; the ones that follow reuse the file
            // it wrote. Two builders used to race on one shared "<target>.part", and the loser
            // either failed to write it or failed to move it into place — a race that ended in
            // "let ffsubsync demux the file instead", the very thing this block exists to avoid.
            var referenceGate = _referenceGates.GetOrAdd(videoPath, _ => new SemaphoreSlim(1, 1));
            string? referenceWhy = null;

            if (referenceTarget is not null)
            {
                await referenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(referenceTarget))
                    {
                        var reuseCues = ReferenceStore.CueCount(referenceTarget);
                        if (AlignmentMetrics.LooksLikeSignsTrack(reuseCues, videoDurationForReference))
                        {
                            // A reference with a handful of cues over a whole episode is a
                            // signs/forced track: it cannot align anything. Take it out of the
                            // running and let this job use the audio instead - the sync still
                            // happens, it just stops being built on a bad ruler.
                            _logger.LogWarning(
                                "The reference subtitle {Track} for {Video} holds only {Cues} cue(s) - a signs track, not usable as a reference; syncing against the audio instead",
                                reference.Spec,
                                videoPath,
                                reuseCues);
                            PluginLog.Info(
                                $"[{job.Id}] reference {reference.Spec} has only {reuseCues} cue(s) (a signs/forced track), "
                                + "so it is not usable as a ruler - falling back to the audio for this job");
                            ReferenceStore.Discard(videoPath, reference.Spec!);
                            referenceTarget = null;
                            referenceWhy = $"only {reuseCues} cue(s): a signs/forced track";
                        }
                        else
                        {
                            reference.Path = referenceTarget;
                            reference.Stream = null;
                            reference.UsedSubtitleReference = true;
                            job.Phase = SyncPhaseLabel(fromCache: true, audioReference: false);
                            _logger.LogInformation(
                                "Reusing this run's reference subtitle for {Video}: track {Reference}, {Cues} cues",
                                videoPath,
                                reference.Spec,
                                reuseCues);
                        }
                    }

                    if (referenceTarget is not null && !File.Exists(referenceTarget))
                    {
                        // The track's text, taken from wherever it already is: this run's memory,
                        // the extracted-subtitle cache, or the container's own index. Never a
                        // whole-file ffmpeg read — that is the cost this whole block exists to
                        // avoid.
                        var referenceText = await TryReadReferenceTextAsync(
                            videoPath, referenceOrdinal, job, cancellationToken).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(referenceText))
                        {
                            referenceWhy = "the track's text could not be read from the container index";
                        }
                        else
                        {
                            // Written under a name of this job's own and moved into place: the
                            // reference is handed to ffsubsync as a path, and a reader must never
                            // see a half-written file.
                            var referencePart = referenceTarget + "." + job.Id + ".part";
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(referenceTarget)!);
                                await File.WriteAllTextAsync(
                                    referencePart, referenceText, new System.Text.UTF8Encoding(false), cancellationToken)
                                    .ConfigureAwait(false);
                                File.Move(referencePart, referenceTarget, overwrite: true);
                                ReferenceStore.MarkReady(videoPath);
                                reference.Path = referenceTarget;
                                reference.Stream = null;
                                reference.UsedSubtitleReference = true;
                                job.Phase = SyncPhaseLabel(fromCache: false, audioReference: false);
                                _logger.LogInformation(
                                    "Built this run's reference subtitle for {Video} from track {Reference}: {Cues} cues (deleted once this file's subtitles are done)",
                                    videoPath,
                                    reference.Spec,
                                    SrtWriter.CountCues(referenceText));
                                PluginLog.Info(
                                    $"[{job.Id}] reference: method=subtitle cues={SrtWriter.CountCues(referenceText)} "
                                    + $"track={reference.Spec} file={videoPath}");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                try { File.Delete(referencePart); } catch (IOException) { /* best effort */ }
                                referenceWhy = ex.Message;
                                _logger.LogWarning(ex, "Could not write the reference subtitle for {Video}", videoPath);
                            }
                        }
                    }
                }
                finally
                {
                    referenceGate.Release();
                }
            }

            if (!reference.UsedSubtitleReference)
            {
                // No usable subtitle reference for this job. The audio is analysed instead —
                // through the speech cache, so that a file's other subtitles pay for it once —
                // and the container is never handed over: that demux is what left a bulk run
                // unable to finish.
                PluginLog.Info(
                    $"[{job.Id}] reference {reference.Spec ?? "(none)"} unusable ({referenceWhy ?? "not available"}) "
                    + "- aligning against the audio instead");
                _logger.LogWarning(
                    "No usable reference subtitle for {Video} ({Why}); syncing against the audio instead",
                    videoPath,
                    referenceWhy ?? "not available");
                reference.Spec = null;
                reference.Stream = null;
                reference.Path = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "no reference subtitle could be built for this job", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Ends a job the user killed: the cancellation is the outcome, not a failure, and the temp directory is
    /// removed by the cleanup that follows every run.
    /// </summary>
    /// <param name="job">The job that was killed.</param>
    private void MarkCancelled(SyncJob job)
    {
            _logger.LogInformation("Sync job {JobId} was killed by the user", job.Id);
            job.Status = SyncJobStatus.Cancelled;
            job.Phase = "Killed";
            job.Error = "Killed by the user.";
            job.FinishedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Ends a job that threw, after undoing an in-place replace. The backup is kept when the rollback itself
    /// fails - it is the user's last resort - and the take is taken from <paramref name="backupPath"/> rather than
    /// from the write step's return value, because a write that throws returns nothing.
    /// </summary>
    /// <param name="job">The job that failed.</param>
    /// <param name="ex">What it failed with.</param>
    /// <param name="backupPath">The kept original of a replaced subtitle, if any.</param>
    private void FailJobAndRollBack(SyncJob job, Exception ex, string? backupPath)
    {
            _logger.LogError(ex, "Subtitle sync failed for job {JobId}", job.Id);
            PluginLog.Error($"job {job.Id} failed: mode={job.Mode} item={job.ItemId} stream={job.SubtitleIndex}", ex);

            // ROLLBACK: if we created a backup but didn't complete successfully,
            // restore the original file from backup
            if (backupPath is not null && File.Exists(backupPath))
            {
                try
                {
                    // Determine what the original file was
                    var originalPath = backupPath.Substring(0, backupPath.Length - ".bak.subsync".Length);
                    _logger.LogWarning("Rolling back: restoring {Original} from backup {Backup}", originalPath, backupPath);
                    File.Copy(backupPath, originalPath, overwrite: true);
                    SafeDelete(backupPath);
                    _logger.LogInformation("Rollback complete: {Original} restored", originalPath);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "ROLLBACK FAILED for job {JobId}! Backup file preserved at {Backup}", job.Id, backupPath);
                    // Do NOT delete the backup — it's the user's last resort
                }
            }

            job.Status = SyncJobStatus.Failed;
            job.Error = ex.Message;
    }

    /// <summary>
    /// Releases everything a finished job held, in the order it was taken: the temp directory, the file's speech
    /// gate, the audio-analysis link (S46 - it has to outlive every run of this job, not just the first), and the
    /// shared extraction tree, which goes away only when the last job reading it is done. Every step is
    /// best-effort: a cleanup that fails must not change a job that already finished.
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="video">Its media item, for the per-file gate and the shared tree.</param>
    /// <param name="tempDir">The job's temp directory.</param>
    /// <param name="serializeSpeech">Whether this job's own analysis produced the cached speech.</param>
    /// <param name="speechKey">The speech-cache key of that analysis, if there is one.</param>
    private void CleanUpAfterJob(SyncJob job, Video video, string tempDir, bool serializeSpeech, string? speechKey)
    {
            // Clean up temp directory (contains ffsubsync output, extracted subs, etc.)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Non-critical
            }

            // The shared extraction directory goes away only when the last job reading it is done; this job
            // deleting it is exactly what used to fail the jobs that came after it.
            try
            {
                ReleaseSpeechGate(job, video.Path);
            }
            catch
            {
                // Non-critical: the next job of this file will do its own analysis.
            }

            // The audio-analysis symlink this job created outlives its engine runs and goes away here, with
            // the job that made it (S46). Dropping it as soon as the first run finished left the
            // wider-window retry and the verification run of the *same* job asking the engine for a file that
            // no longer existed, so a job whose answer had reached the search window refused instead of
            // being rescued: 7 of the 8 refusals in one field run were exactly this, all of them naming a
            // path under speech-cache/ that the plugin itself had deleted. The harvested .npz stays - that
            // is the artefact worth keeping - and Prune() only ever touches .npz and .ref.srt, so a link
            // that outlives its job is still cleared by the next prune or by the next job's own link.
            try
            {
                if (speechKey is not null && serializeSpeech)
                {
                    SpeechCache.DropLink(speechKey);
                }
            }
            catch
            {
                // Non-critical: the link is a temp artefact and the next prune clears it.
            }

            try
            {
                SharedExtractionStore.Release(video.Path, job.Id);
            }
            catch
            {
                // Non-critical: the next cleanup clears it.
            }
    }

    /// <summary>
    /// Ends a job as refused: nothing was written, so the output path is cleared and the engine's temporary output
    /// is removed.
    /// </summary>
    /// <remarks>
    /// Every refusal in a job's run goes through here. Before the extraction each of the five refusal sites wrote
    /// the same seven fields itself, and they had already drifted: one of them (the wrong-cut reference) left
    /// <c>FinishedAtUtc</c> set by the same lines as the others while another relied on the caller. The wording
    /// stays at the site, because it is built from the values that decided the refusal.
    /// </remarks>
    /// <param name="job">The job being refused.</param>
    /// <param name="phase">The phase to leave on the job.</param>
    /// <param name="error">The sentence the user is shown.</param>
    /// <param name="tempOutput">The engine's output in the job's temp directory, if any.</param>
    private void RefuseJob(SyncJob job, string phase, string error, string? tempOutput)
    {
        job.Status = SyncJobStatus.Failed;
        job.Phase = phase;
        job.Error = error;
        job.Progress = 1.0;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.OutputPath = null;
        SafeDelete(tempOutput);
    }

    /// <summary>
    /// Ends a job whose run wrote no subtitle at all because the engine suppressed its write: the subtitle is
    /// already in sync, so this is a success with nothing to write (P7's outcome).
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="cuesNote">The signs-track note, when there is one.</param>
    private void CompleteAlreadyInSync(SyncJob job, string? cuesNote)
    {
        job.Outcome = "already in sync (shift under 3 s) \u2014 no change needed"
            + (cuesNote is null ? string.Empty : " \u00b7 " + cuesNote);
        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
        _logger.LogInformation("Sync job {JobId}: subtitle already in sync \u2014 no output written", job.Id);
        LogPluginCompletion(job, null);
    }

    /// <summary>
    /// Ends a job whose engine output was measurably the same as its input: nothing changed for the user, so
    /// nothing is written (P12's outcome).
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="noChange">The measurement that says nothing moved.</param>
    /// <param name="cuesNote">The signs-track note, when there is one.</param>
    /// <param name="tempOutput">The engine's output, which is removed.</param>
    private void CompleteAsNoChange(SyncJob job, AlignmentMetrics.SyncChange noChange, string? cuesNote, string? tempOutput)
    {
        _logger.LogInformation(
            "Sync job {JobId}: the sync changed nothing ({Change}) \u2014 no sidecar written",
            job.Id,
            noChange.Describe());
        job.Outcome = $"already in sync ({noChange.Describe()}) \u2014 nothing written"
            + (cuesNote is null ? string.Empty : " \u00b7 " + cuesNote);
        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.OutputPath = null;
        SafeDelete(tempOutput);
        LogPluginCompletion(job, null);
    }

    /// <summary>
    /// Whether the engine's own words say it could not read the reference it was handed (S22).
    /// </summary>
    /// <remarks>
    /// A run that failed to open its reference is not a search-window problem, and refusing with "raise Maximum
    /// offset" sends the user to a setting that cannot help: measured in the field on 2026-09-15, 7 of the 8
    /// refusals in one run were this shape, every one of them naming a path under <c>state/speech-cache</c> that the
    /// engine could not open. The markers are the engine's own words, taken from that log.
    /// </remarks>
    /// <param name="engineTail">The tail of what the engine printed, as the refusal message already carries it.</param>
    /// <returns>True when the engine said it could not read the reference.</returns>
    internal static bool EngineCouldNotReadReference(string engineTail)
        => engineTail.Contains("unable to read reference", StringComparison.OrdinalIgnoreCase)
        || engineTail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
        || engineTail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// Safely replaces an external subtitle file with the synced version.
    /// Creates a backup first, then atomically renames the new file into place.
    /// </summary>
    private async Task ReplaceExternalSubtitle(string originalPath, string backupPath, string syncedTempPath)
    {
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException($"Original subtitle file not found: {originalPath}");
        }

        // 1. Copy original → backup (preserves original permissions/attrs)
        _logger.LogInformation("Backing up original subtitle: {Original} → {Backup}", originalPath, backupPath);
        File.Copy(originalPath, backupPath, overwrite: false);

        // 2. Copy synced temp → original (use Copy+Delete instead of cross-device Rename)
        _logger.LogInformation("Replacing subtitle with synced version: {Temp} → {Original}", syncedTempPath, originalPath);
        await Task.Run(() => File.Copy(syncedTempPath, originalPath, overwrite: true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the path a replace run keeps the user's original subtitle at.
    ///
    /// A name ending in <c>.bak.subsync</c> is deliberately not a subtitle extension, so Jellyfin
    /// never offers the backup as a second track. An earlier run's backup is never overwritten:
    /// each replace keeps the copy it made, so the chain of originals stays intact.
    /// </summary>
    /// <param name="originalPath">The subtitle that is about to be overwritten.</param>
    /// <returns>A path that does not exist yet.</returns>
    private static string NextBackupPath(string originalPath)
    {
        var first = originalPath + ".bak.subsync";
        if (!File.Exists(first))
        {
            return first;
        }

        for (var n = 2; n < 1000; n++)
        {
            var candidate = originalPath + ".bak" + n.ToString(CultureInfo.InvariantCulture) + ".subsync";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return originalPath + ".bak." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".subsync";
    }

    /// <summary>
    /// Deletes a file if it exists. Swallows all exceptions.
    /// </summary>
    private static void SafeDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* non-critical */ }
    }


    /// <summary>
    /// Parses a single stderr line from ffsubsync and updates job progress/phase.
    /// ffsubsync outputs tqdm progress bars and phase log lines.
    /// Progress mapping: speech extraction 10-55%, subtitle extraction 55-60%, alignment 60-75%.
    /// </summary>
    private void ParseFfSubSyncStderr(string line, SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Try to parse tqdm percentage
        var match = TqdmPercentRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            // Speech extraction phase: map 0-100% → 0.10-0.55
            job.Progress = 0.10 + (percent / 100.0) * 0.45;
            job.Phase = "Analyzing speech";
            return;
        }

        // Check for phase messages in log lines (lowercase to match stderr format)
        var lower = line.ToLowerInvariant();

        if (lower.Contains("extracting speech segments from subtitle"))
        {
            job.Phase = "Extracting subtitle speech";
            job.Progress = 0.55;
        }
        else if (lower.Contains("computing alignments"))
        {
            job.Phase = "Computing alignment";
            job.Progress = 0.60;
        }
        else if (lower.Contains("got score") && lower.Contains("for ratio"))
        {
            // Individual alignment iterations — nudge progress 0.60 → 0.75
            // Each iteration is ~1s; we just slowly creep up
            job.Phase = "Computing alignment";
            job.Progress = Math.Min(job.Progress + 0.01, 0.74);
        }
        else if (lower.Contains("writing output"))
        {
            job.Phase = "Writing output";
            job.Progress = 0.75;
        }
    }


    /// <summary>
    /// The ffsubsync flags that decide whether subtitle timings may be rescaled.
    /// </summary>
    /// <remarks>
    /// ffsubsync 0.5.1 corrects a framerate mismatch by default and infers the ratio from the ratio
    /// between the reference duration and the subtitle's own span, so a subtitle whose last cue sits a
    /// few percent outside the video is read as a framerate mismatch and the whole file is time-scaled
    /// to fit. Measured against the bundled engine with a 4.17% longer span: the default, and either
    /// opt-out flag on its own, all produced a 0.960x scale with a -51.9 s shift and -104 s of drift;
    /// only both flags together left the timings alone (ratio 1.0000x, offset only). Correction is
    /// therefore opt-in, and when it is on, the measured result still has to be a real framerate pair.
    /// </remarks>
    /// <param name="fixFramerate">Whether the user asked for framerate correction.</param>
    /// <param name="goldenSection">Whether the user asked for golden-section ratio search.</param>
    /// <returns>The flags to pass, possibly none.</returns>
    internal static IEnumerable<string> FramerateArgs(bool fixFramerate, bool goldenSection)
    {
        if (fixFramerate)
        {
            if (goldenSection)
            {
                yield return "--gss";
            }

            yield break;
        }

        yield return "--no-fix-framerate";
        yield return "--skip-infer-framerate-ratio";
    }

    private List<string> BuildFfSubSyncArgs(
        Configuration.PluginConfiguration config,
        string videoPath,
        string subtitleInput,
        string subtitleOutput,
        string? logDir = null,
        bool serializeSpeech = false,
        string? referenceStream = null,
        string? vadOverride = null)
    {
        // Validate config values to prevent argument injection
        var vadMethod = !string.IsNullOrWhiteSpace(vadOverride) && AllowedVadMethods.Contains(vadOverride)
            ? vadOverride!
            : AllowedVadMethods.Contains(config.VadMethod)
                ? config.VadMethod
                : "subs_then_webrtc";
        // The values the engine is given are the validated ones, so a hand-edited config.xml cannot put a
        // negative or absurd ceiling into argv (F10).
        var outputEncoding = Configuration.SettingsValidation.OutputEncodingOf(config);
        var maxOffsetSeconds = Configuration.SettingsValidation.MaxOffsetSecondsOf(config);
        var maxSubtitleSeconds = Configuration.SettingsValidation.MaxSubtitleSecondsOf(config);

        // ArgumentList passes argv directly — no string-quoting layer, so paths
        // with spaces/unicode can never split into extra arguments.
        var args = new List<string>
        {
            videoPath,
            "-i", subtitleInput,
            "-o", subtitleOutput,
            "--max-offset-seconds", maxOffsetSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds", maxSubtitleSeconds.ToString(CultureInfo.InvariantCulture),
            "--vad", vadMethod,
            "--output-encoding", outputEncoding,
            "--ffmpeg-path", ResolveFfmpegPath()
        };

        foreach (var flag in FramerateArgs(config.FixFramerate, config.UseGoldenSectionSearch))
        {
            args.Add(flag);
        }

        // When the alignment reference is itself a subtitle, the engine's span-based ratio inference has no frame
        // rate to read: it compares the reference's duration with the subtitle's span and rescales from that.
        // Measured on a 50-minute fixture whose subtitle was one PAL step off the reference, enabling framerate
        // correction this way turned a 17.4 s shift into a 55.8 s one, and the reference ceiling refused the file.
        // The plugin owns the rescale decision on this path - it can see both spans and only accepts a real
        // framerate pair (RescaleOntoReferenceSpan) - so the inference stays out of it either way.
        var referenceIsSubtitle = referenceStream is null
            && videoPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        if (referenceIsSubtitle && !args.Contains("--skip-infer-framerate-ratio"))
        {
            args.Add("--skip-infer-framerate-ratio");
        }

        if (!string.IsNullOrWhiteSpace(referenceStream))
        {
            args.Add("--reference-stream");
            args.Add(referenceStream);
        }

        args.AddRange(PiecewiseArgs(config));

        if (serializeSpeech)
        {
            // Writes the speech signal next to the reference path we passed in (a
            // symlink inside our cache dir), so later subtitles of the same file can
            // reuse it instead of analysing the audio again.
            args.Add("--serialize-speech");
        }

        if (!string.IsNullOrWhiteSpace(logDir))
        {
            args.Add("--log-dir-path");
            args.Add(logDir);
        }

        return args;
    }

    /// <summary>
    /// The engine arguments that allow a piecewise alignment, if the setting asks for one.
    /// </summary>
    /// <remarks>
    /// The alass idea, reached through this engine's own <c>--split-penalty</c>: the offset may change across
    /// the timeline, charged this many seconds of overlap per split. 0 - the default, and every release so
    /// far - leaves the engine's single global offset in place.
    /// </remarks>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The arguments to add, empty when a single global offset is wanted.</returns>
    public static List<string> PiecewiseArgs(PluginConfiguration config)
    {
        var penalty = Configuration.SettingsValidation.SplitPenaltyOf(config);
        return penalty > 0
            ? new List<string> { "--split-penalty", penalty.ToString("0.###", CultureInfo.InvariantCulture) }
            : new List<string>();
    }

    /// <summary>
    /// Runs a process and calls back with each stderr line in real-time.
    /// Used for ffsubsync to parse tqdm progress and phase messages.
    /// </summary>
    /// <summary>
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
            "--max-offset-seconds",
            Configuration.SettingsValidation.MaxOffsetSecondsOf(config).ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds",
            Configuration.SettingsValidation.MaxSubtitleSecondsOf(config).ToString(CultureInfo.InvariantCulture),
            // The reference of this run is the video, so this is an audio run: it is given the audio VAD (S43).
            "--vad", AudioReferenceVad,
            "--output-encoding", Configuration.SettingsValidation.OutputEncodingOf(config),
            "--ffmpeg-path", ResolveFfmpegPath(),
            "--no-fix-framerate",
            "--skip-infer-framerate-ratio",
            "--log-dir-path", tempDir
        };

        _logger.LogInformation("Sync job {JobId}: testing the stretch against the film's audio", job.Id);
        PluginLog.Info(
            $"[{job.Id}] framerate: the subtitle was stretched, so the stretch is tested against the film's own "
            + "audio (offsets only) \u2014 a differently cut subtitle looks the same as a framerate mismatch in the spans");

        var exitCode = await _processes.RunProcessWithStderrCallbackAsync(
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

        var measured = File.Exists(verifyOutput) ? AlignmentMetrics.MeasureSyncChange(stretchedInput, verifyOutput) : null;
        var ratio = measured?.Ratio ?? 1.0;
        var shiftMs = measured?.ShiftMs ?? 0;
        if (AlignmentMetrics.AlignmentHoldsAgainstAudio(ratio, shiftMs, videoSeconds))
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
            + "\u2014 a different cut looks the same in the spans, so the subtitle is aligned with offsets only");

        var fallbackExit = await _processes.RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, fallbackArgs, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (fallbackExit == 0 && File.Exists(fallbackOutput))
        {
            return (fallbackOutput, true, originalInput);
        }

        return (null, true, originalInput);
    }

}
