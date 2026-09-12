"""Fill the wave even when one episode contributes many subtitles, and let a file's later
subtitles run in parallel once its reference is extracted.

Two causes of "2/4 workers":

1. The candidate scan stopped after `limit * 4` jobs. With a 10-track episode that is two distinct
   media files, so a four-worker batch could only ever be two wide - the gate was the scan window,
   not the configured width.
2. Several subtitles of one file were only allowed to overlap when the file's *audio* analysis was
   cached. On the subtitle-reference path there is no such cache, so tracks 2..10 queued behind the
   first even though each only reads a small cached reference file.

The reference extraction is written to a temporary file and moved into place, so two tracks of the
same file can no longer produce a half-written reference.
"""
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new, label):
    global s
    assert old in s, 'NOT FOUND: ' + label
    s = s.replace(old, new, 1)


# --- 1. the scan window -------------------------------------------------------
rep("""            candidates.Add(candidate);
            if (candidates.Count >= policy.Limit * 4)
            {
                break; // enough to fill a wave several times over; keeps the scan bounded
            }""",
"""            candidates.Add(candidate);

            // The scan has to be wide enough to *find* a wave, not merely to hold one. A fixed
            // multiple of the limit is not: with ten-track episodes, limit*4 candidates covered
            // only two distinct media files, so a four-worker batch ran two wide and reported
            // "using 2/4 workers". The ceiling is only there to keep one scheduler pass bounded.
            if (candidates.Count >= Math.Min(20000, Math.Max(policy.Limit * 64, 512)))
            {
                break;
            }""", 'scan window')

# --- 2. same-file overlap once the reference is cached ------------------------
rep("""                        job => JobNeedsHeavyIo(job, headMode),
                        job => SpeechIsCached(job));""",
"""                        job => JobNeedsHeavyIo(job, headMode),
                        job => SpeechIsCached(job)
                            || (_jobContexts.TryGetValue(job.Id, out var shareContext)
                                && SpeechCache.ReferenceReady(shareContext.Video.Path)));""", 'share predicate')

# --- 3. write the reference atomically ---------------------------------------
rep("""                    var referenceTarget = SpeechCache.ReferencePath(videoPath, referenceStream!, referenceIdentity);
                    try
                    {
                        Directory.CreateDirectory(SpeechCache.Root);
                        await ExtractEmbeddedAsync(
                            videoPath,
                            referenceOrdinal,
                            -1,
                            referenceTarget,
                            config,
                            job,
                            video.RunTimeTicks,
                            cancellationToken,
                            allowFfmpegFallback: false).ConfigureAwait(false);
                        referencePath = referenceTarget;
                        referenceStream = null;""",
"""                    var referenceTarget = SpeechCache.ReferencePath(videoPath, referenceStream!, referenceIdentity);
                    var referencePart = referenceTarget + ".part";
                    try
                    {
                        Directory.CreateDirectory(SpeechCache.Root);

                        // Written to a temporary name and moved into place: two subtitles of the
                        // same file may extract the reference at the same time, and a reader must
                        // never see a half-written file.
                        await ExtractEmbeddedAsync(
                            videoPath,
                            referenceOrdinal,
                            -1,
                            referencePart,
                            config,
                            job,
                            video.RunTimeTicks,
                            cancellationToken,
                            allowFfmpegFallback: false).ConfigureAwait(false);
                        File.Move(referencePart, referenceTarget, overwrite: true);
                        SpeechCache.MarkReferenceReady(videoPath, referenceIdentity);
                        referencePath = referenceTarget;
                        referenceStream = null;""", 'atomic reference write')

rep("""                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Fall back to the previous behaviour: let ffsubsync pull the stream. Slow,
                        // but better than failing the sync.""",
"""                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        try { File.Delete(referencePart); } catch (IOException) { /* best effort */ }

                        // Fall back to the previous behaviour: let ffsubsync pull the stream. Slow,
                        // but better than failing the sync.""", 'part cleanup')

open(p, 'w').write(s)
print('service: scan window widened, same-file overlap allowed when the reference is cached')

# --- SpeechCache: the readiness marker ---------------------------------------
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/SpeechCache.cs'
s = open(p).read()
rep("""    /// <summary>Gets the cached reference subtitle for a media file, or null when absent.</summary>""",
"""    /// <summary>
    /// Records that a reference subtitle has been extracted for this media file.
    ///
    /// Used only for scheduling: several subtitles of one file may then run at the same time,
    /// because none of them has to demux the file any more. Without it they queued behind the
    /// first, which is why a ten-track episode used one worker at a time.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="engineVersion">Bundled/used ffsubsync version or path.</param>
    public static void MarkReferenceReady(string videoPath, string engineVersion)
    {
        try
        {
            File.WriteAllText(ReferenceReadyPath(videoPath, engineVersion), DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (IOException)
        {
            // Best effort: without the marker the file is simply scheduled one track at a time.
        }
    }

    /// <summary>Whether a reference subtitle has already been extracted for this media file.</summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <returns>True when the marker exists.</returns>
    public static bool ReferenceReady(string videoPath) =>
        File.Exists(ReferenceReadyPath(videoPath, "any"));

    private static string ReferenceReadyPath(string videoPath, string engineVersion) =>
        Path.Combine(Root, KeyFor(videoPath, "reference-ready", engineVersion) + ".ready");

    /// <summary>Gets the cached reference subtitle for a media file, or null when absent.</summary>""", 'readiness marker')

# .ready markers are tiny; keep them out of the size accounting but clear stale ones
rep("""        return int.TryParse""", """        return int.TryParse""", 'noop')

open(p, 'w').write(s)
print('SpeechCache: reference-ready marker added')
