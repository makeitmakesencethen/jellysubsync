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
/// The extraction lanes, the extraction chain and the extracted-text caches.
/// </summary>
/// <remarks>
/// Part of <see cref="SubSyncService"/>, split out of the single file as the C3 cluster of
/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,
/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this
/// code shares with the rest of the service is still the service's own fields, declared in the main
/// file.
/// </remarks>
public partial class SubSyncService
{
    /// <summary>
    /// True when a recent check of the extraction cache already said this track is not there yet (S7).
    /// </summary>
    /// <param name="key">The (file, ordinal) key the extraction cache is keyed by.</param>
    /// <returns>True when the answer is still trusted, so the cache does not have to be read again.</returns>
    private bool CacheSaysMissing(string key)
    {
        var now = DateTime.UtcNow;
        lock (_extractedMissGate)
        {
            return _extractedMissMemo.TryGetValue(key, out var at) && now - at < ExtractedMissTtl;
        }
    }

    /// <summary>Remembers that the extraction cache did not have a track, so the next pass need not read it.</summary>
    /// <param name="key">The (file, ordinal) key the extraction cache is keyed by.</param>
    private void RememberCacheMiss(string key)
    {
        var now = DateTime.UtcNow;
        lock (_extractedMissGate)
        {
            // A long run over many files must not grow this without end: the entries are worth two seconds each,
            // so clearing is cheaper than tracking them.
            if (_extractedMissMemo.Count > 2048)
            {
                _extractedMissMemo.Clear();
            }

            _extractedMissMemo[key] = now;
        }
    }

    /// <summary>
    /// True when this job's subtitle is already out of the media file, so the job has nothing to read
    /// and can start syncing straight away.
    /// </summary>
    /// <param name="job">Job being considered for a worker slot.</param>
    /// <returns>True when the subtitle text is available.</returns>
    private bool ExtractionReady(SyncJob job)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var context))
        {
            return false;
        }

        // A sidecar subtitle is already a file of its own: there is nothing to extract.
        if (context.Stream.IsExternal)
        {
            return true;
        }

        var path = context.Video?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var key = ExtractedKeyOf(path, context.Ordinal);
        if (_extractedReady.ContainsKey(key))
        {
            return true;
        }

        // The lane is reading this file right now, so the tracks it has not handed over yet are on
        // their way. Starting the job anyway is what made one file be read once per worker: measured
        // on the slow-storage profile, the lane read a 50-track episode (970.4 MB, 3 991 reads,
        // 473 472 ms) and four jobs then each started their own "Extracting N subtitles from the
        // Matroska index in one pass" over the same file, so the episode cost gigabytes of reads
        // instead of one pass. Waiting costs the job nothing - it could not have started syncing
        // before its subtitle existed - and the lane picks up the remaining ordinals the moment its
        // current pass ends. The escape hatch below still lets jobs extract for themselves when no
        // lane is running or the lane has gone quiet.
        if (_passInFlight.ContainsKey(path))
        {
            return false;
        }

        if (CacheSaysMissing(key))
        {
            return false;
        }

        if (SubtitleCache.TryGet(path, context.Ordinal.ToString(CultureInfo.InvariantCulture), out var text)
            && text.Length > 0)
        {
            _extractedReady[key] = 0;
            return true;
        }

        RememberCacheMiss(key);

        // The lane ran a pass over this file and came back without this track: a picture track with no
        // text, or an empty one. Holding the job back forever is what made the run look stuck (32
        // queued, one running, limit 4, because only one job per file was ever ready); letting it start
        // means it reports its own reason instead of sitting in the queue for ever.
        if (_extractTried.ContainsKey(key))
        {
            return true;
        }

        return false;
    }

    /// <summary>Key for "this track of this file has been extracted".</summary>
    /// <param name="videoPath">Media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <returns>Lookup key.</returns>
    private static string ExtractedKeyOf(string videoPath, int ordinal) => videoPath + "\u0000" + ordinal;

    /// <summary>Wakes the extraction lane, starting it if it is not running.</summary>
    private void WakeExtractor()
    {
        // Read through the settings source, not the plugin's in-memory copy: the page stores settings through
        // the API, and a hand-edited config.xml changes nothing else. Half the worker count, because a lane
        // reads a file while the workers drive the engine on files already read (F13).
        var configured = Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers;
        var wanted = Math.Clamp(configured / 2, 1, MaxExtractionLanes);

        lock (_queueLock)
        {
            _laneTasks.RemoveAll(task => task.IsCompleted);
            while (_laneTasks.Count < wanted)
            {
                _laneTasks.Add(Task.Run(ExtractLaneAsync));
            }
        }

        try { _extractWake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }


    /// <summary>True while at least one extraction lane is running.</summary>
    private bool LaneAlive
    {
        get
        {
            lock (_queueLock)
            {
                return _laneTasks.Exists(task => !task.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Keeps the queue's subtitles extracted.
    ///
    /// Pulls one media file at a time, extracts every queued subtitle of it in a single pass, stores
    /// each result as it arrives and lets the scheduler start those jobs. Runs for the life of the
    /// plugin and is signalled by the enqueue path and by the planner.
    /// </summary>
    private async Task ExtractLaneAsync()
    {
        PluginLog.Info("extract lane: started");
        while (!_disposing)
        {
            var work = NextFileToExtract();
            if (work is null)
            {
                try
                {
                    await _extractWake.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            var (videoPath, ordinals) = work.Value;
            if (!_passInFlight.TryAdd(videoPath, DateTime.UtcNow))
            {
                continue;
            }

            // This pass's own view of "everything is being stopped". A Kill cancels it; the token the
            // extractor receives is the only reason a `Kill` used to leave the reader going.
            var passStop = _laneStop;

            try
            {
                var results = new Dictionary<int, string>();
                var stats = new MkvExtractionStats();
                foreach (var ordinal in ordinals)
                {
                    _extractTried.TryRemove(ExtractedKeyOf(videoPath, ordinal), out _);
                }

                var ok = false;
                var reason = string.Empty;
                ok = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtractMany(
                        videoPath,
                        ordinals,
                        out results,
                        out reason,
                        out stats,
                        passStop.Token,
                        // Hand each subtitle over the moment the pass has read its last line, not when
                        // the whole file is done: that job starts syncing straight away, which is the
                        // difference between a language waiting for the other twenty-nine and it going
                        // as soon as it is out. The pass keeps reading for the rest.
                        (ordinal, text) =>
                        {
                            if (text.Length == 0)
                            {
                                return;
                            }

                            SubtitleCache.Store(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), text);
                            _extractedReady[ExtractedKeyOf(videoPath, ordinal)] = 0;
                            WakePump();
                        }),
                    passStop.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);

                // A cancelled pass is not a failed one. The Matroska reader reports cancellation as an
                // ordinary `false` ("cancelled"), and treating that as "this track holds no text" marks
                // every track the pass never reached as unextractable until the next restart — the jobs
                // for them then start, find nothing and fail. A Kill stops the pass; the tracks stay
                // queued and the next pass reads them.
                if (passStop.IsCancellationRequested)
                {
                    throw new OperationCanceledException(passStop.Token);
                }

                foreach (var pair in results)
                {
                    if (pair.Value.Length == 0)
                    {
                        continue;
                    }

                    // Already stored by the callback for most of them; this covers a track the pass
                    // produced without passing its last cue (an unindexed track, or a truncated file).
                    SubtitleCache.Store(videoPath, pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
                    _extractedReady[ExtractedKeyOf(videoPath, pair.Key)] = 0;
                }

                SubtitleCache.Prune();
                PluginLog.Info(
                    $"extract lane: {Path.GetFileName(videoPath)} -> {results.Count}/{ordinals.Count} subtitle(s), "
                    + $"{stats.BytesRead / 1e6:0.0} MB, {stats.ReadCalls} reads, {stats.TotalMs:0} ms, ok={ok}, "
                    + $"reason={reason} "
                    + $"prefetched={stats.PrefetchedRanges} ranges/{stats.PrefetchedBytes / 1e6:0.0} MB "
                    + $"unused={stats.PrefetchedUnusedRanges} ranges/{stats.PrefetchedUnusedBytes / 1e6:0.0} MB "
                    + $"bytesTwice={stats.BytesReadTwice / 1e6:0.00} MB memoryReads={stats.MemoryServedReads} "
                    + $"plan={stats.Route} expected={stats.PlanExpectedBytes / 1e6:0.00} MB/{stats.PlanExpectedCalls} reads "
                    + $"missed={stats.PlanMissed} indexedMisses={stats.IndexedMisses} walked={stats.WalkedClusters} "
                    + $"storage={stats.MeasuredMsPerRead:0.00} ms/read {stats.MeasuredMbPerSecond:0.0} MB/s "
                    + $"({DescribeExtraction(stats.Method)})");

                foreach (var ordinal in ordinals)
                {
                    if (!results.ContainsKey(ordinal) || results[ordinal].Length == 0)
                    {
                        // Nothing in this track to sync (a picture track, or an empty one). Recording it
                        // stops the lane from asking again on every pass.
                        _extractTried[ExtractedKeyOf(videoPath, ordinal)] = 0;
                    }
                }

                _lastPassFinishedUtc = DateTime.UtcNow;
                if (results.Count > 0)
                {
                    WakePump();
                }
            }
            catch (OperationCanceledException)
            {
                // A Kill stops this pass. Whatever the pass handed over before it stopped is already in
                // the subtitle cache and is reused; the rest stays queued and the next pass reads on,
                // because a cancelled pass records nothing about the tracks it never reached.
                PluginLog.Info(
                    $"extract lane: pass on {Path.GetFileName(videoPath)} stopped (killed by the user) "
                    + $"with {ordinals.Count} subtitle(s) still owed");
                _lastPassFinishedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extraction lane failed for {Video}", videoPath);
            }
            finally
            {
                _passInFlight.TryRemove(videoPath, out _);
            }
        }

        PluginLog.Info("extract lane: stopped");
    }

    /// <summary>
    /// Picks the next media file with queued subtitles that are not extracted yet, and the ordinals it
    /// still owes. Skips files a pass is already running on, and sidecar subtitles, which need nothing.
    /// </summary>
    /// <returns>The file and its missing ordinals, or null when there is nothing to do.</returns>
    private (string VideoPath, List<int> Ordinals)? NextFileToExtract()
    {
        const int MaxTracksPerPass = 48;
        var byFile = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        lock (_queueLock)
        {
            foreach (var job in _runOrder)
            {
                if (job.Status != SyncJobStatus.Queued
                    || !_jobContexts.TryGetValue(job.Id, out var context)
                    || context.Stream.IsExternal)
                {
                    continue;
                }

                var path = context.Video?.Path;
                if (string.IsNullOrEmpty(path) || _passInFlight.ContainsKey(path))
                {
                    continue;
                }

                if (!byFile.TryGetValue(path, out var wanted))
                {
                    wanted = new List<int>();
                    byFile[path] = wanted;
                }

                if (!wanted.Contains(context.Ordinal) && wanted.Count < MaxTracksPerPass)
                {
                    wanted.Add(context.Ordinal);
                }

                // The ruler comes out of the same pass. ffsubsync aligns each subtitle against another
                // subtitle track of the same file, and a job that finds the reference missing reads the
                // file again for it - measured on a real episode, four such passes of ~330 MB each for
                // one file. Asking for it here means one read produces both the languages and the ruler.
                foreach (var referenceOrdinal in ReferenceOrdinalsFor(path, context.Ordinal, context.Video))
                {
                    if (!wanted.Contains(referenceOrdinal) && wanted.Count < MaxTracksPerPass)
                    {
                        wanted.Add(referenceOrdinal);
                    }
                }
            }
        }

        foreach (var (path, wanted) in byFile)
        {
            var missing = wanted
                .Where(o => !_extractedReady.ContainsKey(ExtractedKeyOf(path, o))
                    && !_extractTried.ContainsKey(ExtractedKeyOf(path, o)))
                .ToList();
            if (missing.Count > 0)
            {
                return (path, missing);
            }
        }

        return null;
    }

    /// <summary>
    /// Track ordinals a job may align against, which the extraction lane wants produced as well.
    ///
    /// The choice itself belongs to the job (it must not align a track against that same track), so
    /// this asks the same picker the job path uses, with Jellyfin's stream list instead of the ffprobe
    /// list the job builds. The two agree on codec and order in practice; when they do not, the job
    /// extracts the reference for itself exactly as it did before, so a miss costs a read and never
    /// correctness. Memoised: the planner asks twice a second for every queued job.
    /// </summary>
    /// <param name="videoPath">Media file, part of the memo key.</param>
    /// <param name="targetOrdinal">Subtitle this job is fixing.</param>
    /// <param name="video">The library item.</param>
    /// <returns>Ordinals to extract, empty when the picker cannot decide.</returns>
    private IEnumerable<int> ReferenceOrdinalsFor(string videoPath, int targetOrdinal, Video? video)
    {
        var key = ExtractedKeyOf(videoPath, targetOrdinal) + "\u0000ref";
        if (_referenceOrdinals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var ordinals = new List<int>();
        try
        {
            if (video is null)
            {
                return ordinals;
            }

            var streams = video.GetMediaSources(true)
                .SelectMany(source => source.MediaStreams)
                .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !stream.IsExternal)
                .OrderBy(stream => stream.Index)
                .ToList();

            if (streams.Count > 0)
            {
                var spec = MediaStreamMap.SelectReferenceStream(
                    true,
                    streams.Select(stream => stream.Codec).ToList(),
                    targetOrdinal,
                    streams.Select(stream => stream.IsForced).ToList());
                var ordinal = MediaStreamMap.SubtitleStreamOrdinal(spec);
                if (ordinal >= 0)
                {
                    ordinals.Add(ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not work out the reference track for {Video}; the job will extract it", videoPath);
        }

        return _referenceOrdinals.GetOrAdd(key, ordinals);
    }

    private bool SpeechIsCached(SyncJob job)
    {
        try
        {
            if (!_jobContexts.TryGetValue(job.Id, out var ctx))
            {
                return false;
            }

            // The answer belongs to the media file, and the scheduler asks for it for every queued job
            // on every planning pass - while holding the queue lock the enqueue path waits on. Reading
            // it from storage each time cost tens of seconds per queued task on a share (240 tasks took
            // 105-424 s to queue, the slowest single task 32-40 s, almost all of it "log=" time spent
            // waiting for that lock). The answer cannot change except by a finished analysis, which is
            // itself a filesystem event of the same file, so a few seconds of trust is enough.
            var path = ctx.Video.Path;
            var now = DateTime.UtcNow;

            lock (_speechCachedGate)
            {
                if (_speechCachedMemo.TryGetValue(path, out var memo) && now - memo.At < SpeechCachedTtl)
                {
                    return memo.Cached;
                }
            }

            var key = SpeechCache.KeyFor(
                path,
                AudioReferenceVad + "|audio",
                EngineIdentity());
            var cached = SpeechCache.TryGet(key) is not null;

            lock (_speechCachedGate)
            {
                _speechCachedMemo[path] = (cached, now);
            }

            return cached;
        }
        catch (Exception)
        {
            return false;
        }
    }


    /// <summary>
    /// Reads the "done/total" counters out of an extraction progress line.
    ///
    /// The extractor reports "reading subtitle 128/326 · 1.3 MB, 341 reads · …"; the fraction is
    /// what lets the progress bar follow real work instead of standing still.
    /// </summary>
    /// <param name="line">Progress text from an extractor.</param>
    /// <returns>The completed fraction in 0..1, or null when the line has no counters.</returns>
    public static double? ExtractionFraction(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*/\s*(\d+)");
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0)
        {
            return null;
        }

        return Math.Clamp(done / total, 0.0, 1.0);
    }


    /// <summary>
    /// True when an extraction attempt that produced no subtitle was stopped rather than failing (B20).
    /// </summary>
    /// <remarks>
    /// A killed pass and a pass that genuinely could not read the file both come back as "no text", and the reader
    /// reports the first as the reason <c>cancelled</c>. Treating the two the same sends a cancelled attempt down
    /// every remaining engine - more minutes of engine work on a file the user just asked it to stop reading - and
    /// ends with the job marked failed, which names an action nobody took.
    /// </remarks>
    /// <param name="reason">The reason the attempt reported.</param>
    /// <param name="token">The job's cancellation token.</param>
    /// <returns>True when this attempt was cancelled.</returns>
    internal static bool IsCancelledExtraction(string reason, CancellationToken token)
        => token.IsCancellationRequested
            || string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the extraction chain when the attempt was cancelled, instead of trying the next engine (B20).
    /// </summary>
    /// <param name="reason">The reason the attempt reported.</param>
    /// <param name="token">The job's cancellation token.</param>
    /// <param name="where">Which attempt this was, for the log.</param>
    /// <exception cref="OperationCanceledException">Thrown when the attempt was cancelled.</exception>
    private void StopChainIfCancelled(string reason, CancellationToken token, string where)
    {
        if (!IsCancelledExtraction(reason, token))
        {
            return;
        }

        PluginLog.Info($"extract: {where} was stopped (killed by the user) — no fallback attempted");
        throw new OperationCanceledException(token);
    }


    /// <summary>
    /// Which queued subtitles of this file should be extracted along with this one.
    /// </summary>
    /// <remarks>
    /// Reading a file's clusters once and taking every requested subtitle out of them is what makes a
    /// multi-language episode affordable: the alternative visits the same clusters once per language.
    /// Only embedded tracks are listed (an external subtitle is already a file of its own), and the
    /// list is capped so one very large batch does not hold every track's text in memory at once.
    /// </remarks>
    /// <param name="videoPath">The media file being read.</param>
    /// <param name="primaryOrdinal">The ordinal of the subtitle this job is extracting.</param>
    /// <returns>Ordinals to extract in one pass, the primary first.</returns>
    private IReadOnlyList<int> SiblingOrdinals(string videoPath, int primaryOrdinal)
    {
        const int MaxTracksPerPass = 48;
        var wanted = new List<int> { primaryOrdinal };
        lock (_queueLock)
        {
            foreach (var other in _runOrder)
            {
                if (wanted.Count >= MaxTracksPerPass)
                {
                    break;
                }

                if (other.Status != SyncJobStatus.Queued
                    || !_jobContexts.TryGetValue(other.Id, out var context)
                    || context.Stream.IsExternal
                    || !string.Equals(context.Video.Path, videoPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!wanted.Contains(context.Ordinal))
                {
                    wanted.Add(context.Ordinal);
                }
            }
        }

        return wanted;
    }

    /// <summary>Looks up an already extracted track, taking it out so the text is freed after use.</summary>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT, when it was cached.</param>
    /// <returns>True when it was found.</returns>
    private bool TryTakeExtracted(string videoPath, int ordinal, out string text) =>
        _extractedText.TryRemove(ExtractedKey(videoPath, ordinal), out text!);

    /// <summary>Looks up an already extracted track without consuming it.</summary>
    /// <remarks>
    /// The reference track is not the job's own subtitle: two jobs of the same file may need the same
    /// text, so this one peeks instead of taking. Taking it (as the job's own extraction does) makes
    /// the second reader fall through to the container index for text this process already holds.
    /// </remarks>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT, when it was cached.</param>
    /// <returns>True when it was found.</returns>
    private bool TryPeekExtracted(string videoPath, int ordinal, out string text) =>
        _extractedText.TryGetValue(ExtractedKey(videoPath, ordinal), out text!);

    /// <summary>
    /// Reads the text of the track a job will align against, from the cheapest source that has it.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and ends without a whole-file ffmpeg read: this run's memory (the lane
    /// or a sibling job already produced the track), the extracted-subtitle cache (an earlier run
    /// did), then the container's own index. Anything that is not already text is therefore read
    /// through the index, never by demuxing the container with ffmpeg — that cost, per job, is what
    /// stops a bulk run finishing. Returning null is a legitimate answer: the caller then aligns
    /// against the audio instead.
    /// </remarks>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">0-based subtitle ordinal of the reference track.</param>
    /// <param name="job">Job whose phase is updated while reading.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SRT text, or null when no reader could produce it.</returns>
    private async Task<string?> TryReadReferenceTextAsync(
        string videoPath,
        int ordinal,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        if (TryPeekExtracted(videoPath, ordinal, out var inMemory) && !string.IsNullOrWhiteSpace(inMemory))
        {
            PluginLog.Info(
                $"reference: method=reused cues={SrtWriter.CountCues(inMemory)} file={videoPath} stream={ordinal}");
            return inMemory;
        }

        if (SubtitleCache.TryGet(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), out var onDisk)
            && !string.IsNullOrWhiteSpace(onDisk))
        {
            PluginLog.Info(
                $"reference: method=cache cues={SrtWriter.CountCues(onDisk)} file={videoPath} stream={ordinal}");
            return onDisk;
        }

        var reason = string.Empty;
        string? text = null;
        var stats = new MkvExtractionStats();

        try
        {
            if (MkvSubtitleExtractor.LooksLikeMatroska(videoPath))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var progress = new Action<string>(line =>
                {
                    job.Phase = "Reading the reference subtitle: " + line;
                });
                var ok = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtract(
                        videoPath,
                        ordinal,
                        out text,
                        out reason,
                        progress,
                        out stats,
                        null,
                        null,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
                watch.Stop();

                if (!ok || string.IsNullOrWhiteSpace(text))
                {
                    PluginLog.Warn(
                        $"reference: method=index-none ms={watch.ElapsedMilliseconds} stream={ordinal} "
                        + $"reason={reason} file={videoPath}");
                    return null;
                }

                PluginLog.Info(
                    $"reference: method={stats.Method} ms={watch.ElapsedMilliseconds} cues={SrtWriter.CountCues(text)} "
                    + $"bytesRead={stats.BytesRead} readCalls={stats.ReadCalls} file={videoPath} stream={ordinal}");
            }
            else if (Mp4SubtitleExtractor.LooksLikeMp4(videoPath))
            {
                if (!Mp4SubtitleExtractor.TryExtract(videoPath, ordinal, out var mp4Text, out reason)
                    || string.IsNullOrWhiteSpace(mp4Text))
                {
                    PluginLog.Warn(
                        $"reference: method=mp4-none stream={ordinal} reason={reason} file={videoPath}");
                    return null;
                }

                text = mp4Text;
                PluginLog.Info(
                    $"reference: method=mp4-sample-table cues={SrtWriter.CountCues(text)} file={videoPath} stream={ordinal}");
            }
            else
            {
                PluginLog.Warn($"reference: method=none stream={ordinal} reason=no index reader matched this container");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"reference: method=index-error stream={ordinal} reason={ex.Message} file={videoPath}");
            return null;
        }

        // Keep it for the other subtitles of this file (and the next run): the reference is read once.
        CacheExtracted(videoPath, ordinal, text!);
        SubtitleCache.Store(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), text!);
        return text;
    }

    /// <summary>Remembers an extracted track for the jobs that follow it.</summary>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT.</param>
    private void CacheExtracted(string videoPath, int ordinal, string text)
    {
        var key = ExtractedKey(videoPath, ordinal);
        _extractedText[key] = text;
        _extractedOrder.Enqueue(key);
        while (_extractedOrder.Count > ExtractedCacheLimit && _extractedOrder.TryDequeue(out var oldest))
        {
            _extractedText.TryRemove(oldest, out _);
        }
    }

    private static string ExtractedKey(string videoPath, int ordinal) => ordinal + "\u0000" + videoPath;

    /// <summary>
    /// Extracts an embedded subtitle using the cheapest applicable method, and reports which
    /// one worked:
    ///
    /// 1. the container's own index — Matroska <c>Cues</c>, MP4 <c>stbl</c> (kilobytes read);
    /// 2. ffmpeg, which demuxes the whole file (the only option for exotic codecs or files
    ///    without an index), guarded by a timeout so a stuck or very slow read fails with a
    ///    useful message instead of looking like a hang.
    ///
    /// Every skipped method logs why, so a slow path can always be traced back to its cause.
    /// </summary>
    /// <param name="videoPath">Media file.</param>
    /// <param name="subtitleOrdinal">0-based index among subtitle streams.</param>
    /// <param name="containerIndex">Real container stream index (for ffmpeg -map).</param>
    /// <param name="outputPath">Where to write the SRT.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="job">Job whose phase/progress is updated while extracting.</param>
    /// <param name="runTimeTicks">Total runtime from Jellyfin, used to turn ffmpeg's timestamps into progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The method that produced the subtitle ("matroska-cues", "mp4-sample-table" or "ffmpeg").</returns>
    private async Task<string> ExtractEmbeddedAsync(
        string videoPath,
        int subtitleOrdinal,
        int containerIndex,
        string outputPath,
        Configuration.PluginConfiguration config,
        SyncJob job,
        long? runTimeTicks,
        CancellationToken cancellationToken)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var skipped = new List<string>();

        if (MkvSubtitleExtractor.LooksLikeMatroska(videoPath))
        {
            job.Phase = "Extracting subtitle from the Matroska index";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Action<string>(line =>
            {
                job.Phase = "Extracting subtitle: " + line;

                // Advance the bar with the work actually done. Without this the whole extraction
                // sat at a frozen 5%, which made four independent workers look synchronised.
                if (ExtractionFraction(line) is { } fraction)
                {
                    job.Progress = 0.05 + (0.15 * fraction); // 5% → 20% is the extraction window
                }
            });
            // The extraction is synchronous, blocking IO on the media share, and it runs for tens of
            // seconds. On a pool thread it starves everything else the server is doing - including
            // the request that is still queueing the rest of the batch, which is why a 50-task batch
            // trickled in a few tasks every few seconds and the scheduler never saw a full queue. A
            // dedicated thread costs nothing here and leaves the pool for requests.
            var extractedText = string.Empty;
            var extractionReason = string.Empty;
            var extractionStats = new MkvExtractionStats();
            var extracted = false;

            // This track may already have been produced while the file was read for another language.
            if (TryTakeExtracted(videoPath, subtitleOrdinal, out var cachedText))
            {
                PluginLog.Info(
                    $"extract: method=reused cues={SrtWriter.CountCues(cachedText)} cacheLeft={_extractedText.Count} file={videoPath} stream={subtitleOrdinal}");
                await File.WriteAllTextAsync(outputPath, cachedText, utf8, cancellationToken).ConfigureAwait(false);
                return "matroska-cached";
            }

            // ...or while the file was read on a previous run. This is the whole answer to extraction
            // being slow: it is paid once per file, not once per run. The cache holds the subtitle as
            // it came out of the container, so the sync itself still runs (it depends on settings and
            // the engine), but the file is not read again.
            if (SubtitleCache.TryGet(videoPath, subtitleOrdinal.ToString(CultureInfo.InvariantCulture), out var diskText))
            {
                PluginLog.Info(
                    $"extract: method=cache cues={SrtWriter.CountCues(diskText)} file={videoPath} stream={subtitleOrdinal} "
                    + $"(no read: this file's subtitle was extracted on an earlier run)");
                await File.WriteAllTextAsync(outputPath, diskText, utf8, cancellationToken).ConfigureAwait(false);
                return "subtitle-cache";
            }

            // The extraction lane is what reads files: it produces every queued subtitle of a file in
            // one pass and stores each as it arrives, and the scheduler only starts a job once its
            // subtitle is there. Reaching this point with a cache miss means the lane is not running
            // (disposed, or it died), so the job extracts for itself - as a whole-file pass serving its
            // siblings if they are queued too, because that is cheaper than one pass per language.
            var wanted = SiblingOrdinals(videoPath, subtitleOrdinal);
            if (wanted.Count > 1)
            {
                job.Phase = "Extracting " + wanted.Count + " subtitles from the Matroska index in one pass";
                var many = new Dictionary<int, string>();
                var manyReason = string.Empty;
                var manyStats = new MkvExtractionStats();
                var manyOk = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtractMany(
                        videoPath,
                        wanted,
                        out many,
                        out manyReason,
                        out manyStats,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);

                foreach (var pair in many)
                {
                    CacheExtracted(videoPath, pair.Key, pair.Value);
                    SubtitleCache.Store(videoPath, pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
                }

                SubtitleCache.Prune();

                PluginLog.Info(
                    $"extract: method=shared-pass ms={watch.ElapsedMilliseconds} tracks={many.Count}/{wanted.Count} "
                    + $"cues={SrtWriter.CountCues(many.TryGetValue(subtitleOrdinal, out var own) ? own : string.Empty)} "
                    + $"bytesRead={manyStats.BytesRead} readCalls={manyStats.ReadCalls} "
                    + $"clusters={manyStats.ClustersVisited} blocks={manyStats.SubtitleBlocks} alsoBlocks={manyStats.AlsoBlocks} "
                    + $"blockOffsets={manyStats.BlockOffsets} "
                    + $"plan={manyStats.Route} expected={manyStats.PlanExpectedBytes} expectedCalls={manyStats.PlanExpectedCalls} "
                    + $"missed={manyStats.PlanMissed} missedTracks={string.Join(",", manyStats.MissedTracks)} "
                    + $"bytesTwice={manyStats.BytesReadTwice} "
                    + $"prefetched={manyStats.PrefetchedRanges} unusedPrefetch={manyStats.PrefetchedUnusedRanges} "
                    + $"memoryReads={manyStats.MemoryServedReads} msPerRead={manyStats.MeasuredMsPerRead:0.00} "
                    + $"mbPerSecond={manyStats.MeasuredMbPerSecond:0.0} ok={manyOk} reason={manyReason} file={videoPath}");

                // A pass the user killed is not a reason to start another one (B20): the chain used to carry on to
                // the per-track reader and then to ffmpeg, doing minutes of work on a file just stopped, and ending
                // with the job marked failed.
                if (!manyOk)
                {
                    StopChainIfCancelled(manyReason, cancellationToken, "shared pass");
                }

                // Which track a shared pass could not produce is something the log has to say (B2): the call
                // succeeds with a gap in it otherwise, and the gap may be the language someone is waiting for.
                if (manyStats.MissedTracks.Count > 0)
                {
                    PluginLog.Warn(
                        $"extract: shared pass produced no subtitle for track(s) "
                        + $"{string.Join(", ", manyStats.MissedTracks)} of {string.Join(", ", wanted)} "
                        + $"(served {many.Count}/{wanted.Count}) file={videoPath}");
                    _logger.LogWarning(
                        "The pass that read {Video} produced no subtitle for track(s) {Missed} of {Wanted}",
                        videoPath, string.Join(", ", manyStats.MissedTracks), string.Join(", ", wanted));
                    job.ExtractionNote = (job.ExtractionNote is null ? string.Empty : job.ExtractionNote + "; ")
                        + $"no subtitle for track(s) {string.Join(", ", manyStats.MissedTracks)}";
                }

                if (manyOk && many.TryGetValue(subtitleOrdinal, out var sharedText) && sharedText.Length > 0)
                {
                    // The line above is the whole record of this extraction: it was one pass over the
                    // file and it served every queued language. The generic line further down used to
                    // follow it with the same numbers under a different method name, which read like a
                    // second pass had run - two lines per pass, one of them a duplicate.
                    await File.WriteAllTextAsync(outputPath, sharedText, utf8, cancellationToken).ConfigureAwait(false);
                    return "matroska-shared";
                }
            }

            if (!extracted)
            {
                extractedText = string.Empty;
                extractionReason = string.Empty;
                extractionStats = new MkvExtractionStats();
                extracted = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtract(
                        videoPath,
                        subtitleOrdinal,
                        out extractedText,
                        out extractionReason,
                        progress,
                        out extractionStats,
                        null,
                        null,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
            }

            var srt = extractedText;
            var why = extractionReason;
            var stats = extractionStats;
            if (extracted)
            {
                SubtitleCache.Store(videoPath, subtitleOrdinal.ToString(CultureInfo.InvariantCulture), srt);
                await File.WriteAllTextAsync(outputPath, srt, utf8, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Extracted embedded subtitle in {Ms} ms ({Cues} cues, {Stats}) from {Video}",
                    watch.ElapsedMilliseconds, SrtWriter.CountCues(srt), stats, videoPath);
                PluginLog.Info($"extract: method={stats.Method} ms={watch.ElapsedMilliseconds} cues={SrtWriter.CountCues(srt)} bytesRead={stats.BytesRead} readCalls={stats.ReadCalls} clusters={stats.ClustersVisited} blocks={stats.SubtitleBlocks} file={videoPath}");
                return stats.Method;
            }

            // A cancelled read is not "no subtitle in this file" (B20): stop here instead of trying the next reader.
            StopChainIfCancelled(why, cancellationToken, "Matroska index pass");
            skipped.Add("matroska-index: " + why + " [" + stats + "]");
        }

        if (Mp4SubtitleExtractor.LooksLikeMp4(videoPath))
        {
            job.Phase = "Extracting subtitle with the MP4 sample table";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (Mp4SubtitleExtractor.TryExtract(videoPath, subtitleOrdinal, out var srt, out var why))
            {
                await File.WriteAllTextAsync(outputPath, srt, utf8, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Extracted embedded subtitle via the MP4 sample table in {Ms} ms ({Cues} cues) from {Video}",
                    watch.ElapsedMilliseconds, SrtWriter.CountCues(srt), videoPath);
                return "mp4-sample-table";
            }

            StopChainIfCancelled(why, cancellationToken, "MP4 sample-table pass");
            skipped.Add("mp4-sample-table: " + why);
        }

        if (skipped.Count == 0)
        {
            skipped.Add("no index reader matched this container");
        }

        double sizeMb = 0;
        try
        {
            sizeMb = new FileInfo(videoPath).Length / (1024.0 * 1024.0);
        }
        catch (Exception)
        {
            // Size is only used for the log line.
        }

        var timeout = TimeSpan.FromMinutes(Math.Clamp(config.ExtractionTimeoutMinutes, 1, 240));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var fallbackWatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Falling back to ffmpeg extraction for {Video} ({Size:0} MB, up to {Minutes} min) — indexed reads not usable: {Reasons}",
            videoPath, sizeMb, timeout.TotalMinutes,
            skipped.Count == 0 ? "no index reader matched this container" : string.Join("; ", skipped));

        // The single most useful line for a slow extraction: which reader refused the track and why.
        PluginLog.Warn(
            $"extract fallback to ffmpeg: file={videoPath} sizeMb={sizeMb:0.0} timeoutMinutes={timeout.TotalMinutes:0} reasons={string.Join("; ", skipped)}");

        // Say what is happening *before* the slow path starts: a whole-file ffmpeg read can
        // take minutes, and a frozen "Extracting subtitle" at 5% tells the user nothing.
        var durationSeconds = runTimeTicks.HasValue && runTimeTicks.Value > 0
            ? runTimeTicks.Value / (double)TimeSpan.TicksPerSecond
            : 0;
        job.Phase = $"Extracting subtitle with ffmpeg"
            + (sizeMb >= 1 ? $" — reading {sizeMb:0} MB" : string.Empty)
            + (durationSeconds > 0 ? $", up to {timeout.TotalMinutes:0} min" : string.Empty);
        _logger.LogInformation(
            "Extracting subtitle with ffmpeg (whole-file demux) for {Video}: {Size:0} MB, duration {Duration:0}s",
            videoPath, sizeMb, durationSeconds);

        try
        {
            await ExtractSubtitleWithProgressAsync(
                videoPath, containerIndex, outputPath, durationSeconds, job, timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "ffmpeg extraction finished in {Ms} ms for {Video}", fallbackWatch.ElapsedMilliseconds, videoPath);
            PluginLog.Info($"extract: method=ffmpeg ms={fallbackWatch.ElapsedMilliseconds} file={videoPath}");
            return "ffmpeg";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Subtitle extraction timed out after {timeout.TotalMinutes:0} minutes for a {sizeMb:0} MB file. "
                + $"Indexed extraction could not be used ({string.Join("; ", skipped)}), so ffmpeg had to read the whole file — "
                + "this usually means the media sits on a slow or busy mount, or the file needs remuxing to carry an index.");
        }
    }

    /// <summary>
    /// Runs the ffmpeg extraction while translating its reported timestamps into job
    /// progress (5% → 20%), so the UI shows movement instead of a stalled bar.
    /// </summary>
    /// <remarks>
    /// This is the last resort for a track no index reader could produce, and the one extraction whose output
    /// cannot be checked against anything else: whatever it writes is handed on as the subtitle. So what it
    /// wrote is checked instead (B8, <see cref="ExtractionOutputGuard"/>), and a run that failed, was killed or
    /// read a cut-short file leaves nothing behind - a partial subtitle is silently wrong in every way that
    /// matters (cues missing, the tail of the film untimed) and the engine cannot tell it from a complete one.
    /// </remarks>
    /// <param name="videoPath">Media file to demux.</param>
    /// <param name="streamIndex">Real container stream index to map.</param>
    /// <param name="outputPath">Where the SRT is written.</param>
    /// <param name="durationSeconds">Media duration, used to turn ffmpeg's progress into a fraction.</param>
    /// <param name="job">Job whose phase and progress are updated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many cues the verified extraction produced.</returns>
    internal async Task<int> ExtractSubtitleWithProgressAsync(
        string videoPath,
        int streamIndex,
        string outputPath,
        double durationSeconds,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegPath();
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-map", $"0:{streamIndex}",
            "-f", "srt",
            "-progress", "pipe:2",
            "-nostats",
            outputPath
        };

        // ffmpeg's own account of the input: the truncation markers live in these lines, and on a cut-short
        // container they are the only sign that the SRT is a prefix of the track (exit code 0).
        var stderrTail = new List<string>();
        var lastReported = -1.0;
        var exitCode = 0;
        var processEnded = false;

        try
        {
            exitCode = await _processes.RunProcessWithStderrCallbackAsync(
                ffmpegPath,
                args,
                null,
                line =>
                {
                    // Undiscriminating and bounded: the whole line goes to the guard, and a progress line is
                    // cheap to keep. Truncation is reported at the moment the file ends, so a tail is enough
                    // and a marker that repeats stays in it.
                    lock (stderrTail)
                    {
                        stderrTail.Add(line);
                        if (stderrTail.Count > ExtractionStderrTailLines)
                        {
                            stderrTail.RemoveAt(0);
                        }
                    }

                    if (durationSeconds <= 0)
                    {
                        return;
                    }

                    var seconds = ParseFfmpegProgressSeconds(line);
                    if (seconds < 0)
                    {
                        return;
                    }

                    var fraction = Math.Min(1.0, seconds / durationSeconds);
                    if (fraction - lastReported < 0.01)
                    {
                        return;
                    }

                    lastReported = fraction;
                    job.Progress = 0.05 + (0.15 * fraction); // 5% → 20% is the extraction window
                    job.Phase = $"Extracting subtitle with ffmpeg — {fraction * 100:0}% of the file read";
                },
                cancellationToken,
                jobId: job.Id).ConfigureAwait(false);
            processEnded = true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled or past the extraction timeout: ffmpeg was killed mid-write, so what is on disk is a
            // prefix of the subtitle. The caller turns this into a failure (or a kill); the file goes first.
            DiscardPartialExtraction(job, outputPath, "the extraction was stopped before it finished");
            throw;
        }
        finally
        {
            if (!processEnded)
            {
                DiscardPartialExtraction(job, outputPath, "the extraction did not run to completion");
            }
        }

        var verdict = ExtractionOutputGuard.Judge(exitCode, outputPath, stderrTail);
        if (!verdict.Accept)
        {
            PluginLog.Warn(
                $"extract: rejected file={videoPath} stream={streamIndex} exit={exitCode} "
                + $"reason={verdict.Reason}");
            throw new InvalidOperationException("ffmpeg subtitle extraction failed: " + verdict.Reason);
        }

        return verdict.Cues;
    }

    /// <summary>
    /// Throws away what a stopped extraction wrote, and says so in the plugin's own log.
    /// </summary>
    /// <remarks>
    /// Nothing downstream may see a partial extraction: the file is the engine's input, so a prefix of the
    /// track produces a confidently wrong offset rather than an error.
    /// </remarks>
    /// <param name="job">The job the extraction belonged to.</param>
    /// <param name="outputPath">Where the extraction was writing.</param>
    /// <param name="why">Why it is being discarded.</param>
    private static void DiscardPartialExtraction(SyncJob job, string outputPath, string why)
    {
        var note = ExtractionOutputGuard.Discard(outputPath);
        PluginLog.Warn($"[{job.Id}] {why} - {note}");
    }

    /// <summary>
    /// Reads the processed timestamp from one line of ffmpeg's <c>-progress</c> output
    /// (<c>out_time_us=…</c>), falling back to the human-readable <c>out_time=HH:MM:SS</c>.
    /// </summary>
    /// <remarks>
    /// <c>out_time_ms</c> is <b>microseconds</b> despite its name (B19): measured with ffmpeg 7.1 on this
    /// project's fixture, the same moment is printed as <c>out_time_us=33000000</c>,
    /// <c>out_time_ms=33000000</c> and <c>out_time=00:00:33.000000</c>. Dividing that field by 1 000 turned
    /// 33 seconds into 33 000, which the extraction window clamps to "100 % of the file read" - so a pass
    /// alternated between the true fraction and a full bar once per progress block, and the phase text a user
    /// reads could be the wrong one.
    /// </remarks>
    /// <param name="line">One progress line.</param>
    /// <returns>Seconds processed, or -1 when the line is not a progress line.</returns>
    internal static double ParseFfmpegProgressSeconds(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("out_time_us=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
        {
            return micros / 1_000_000.0;
        }

        // Misnamed by ffmpeg and microseconds in fact; see the remarks above for the measurement.
        if (trimmed.StartsWith("out_time_ms=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microsFromMs))
        {
            return microsFromMs / 1_000_000.0;
        }

        if (trimmed.StartsWith("out_time=", StringComparison.Ordinal))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"out_time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (match.Success)
            {
                return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                    + (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    /// <summary>Friendly name of an extraction method, for the UI phase and logs.</summary>
    private static string DescribeExtraction(string method) => method switch
    {
        "seekhead-cues" => "from the Matroska index (SeekHead)",
        "matroska-shared" => "from the pass that read this file for its other languages",
        "cue-index" => "from the Matroska cue index",
        "metadata-scan" => "by walking the file's block headers (this file's index does not point at its subtitle blocks)",
        "matroska-cues" => "from the Matroska index",
        "matroska-cached" => "from the pass that already read this file",
        "subtitle-cache" => "from the extracted-subtitle cache (no read this run)",
        "mp4-sample-table" => "with the MP4 sample table",
        "ffmpeg" => "with ffmpeg (whole-file read)",
        "cancelled" => "stopped by a kill",
        _ => "by a reader this build cannot name (" + method + ")"
    };

    private async Task ExtractSubtitle(string videoPath, int streamIndex, string outputPath, CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveFfmpegPath();

        // ArgumentList passes argv directly — no string-quoting/escaping layer
        // that can mangle paths into "Error opening output files: Invalid argument".
        //
        // streamIndex is the REAL container stream index discovered by probing
        // the file (ResolveContainerSubtitleIndexAsync) — mapped with plain
        // "-map 0:N". Never derive it from Jellyfin's MediaStream.Index or from
        // counting subtitle streams in Jellyfin's MediaStreams list: both have
        // been observed to disagree with the actual container (external sidecar
        // tracks and renumbered streams), producing "Failed to set value '0:N'
        // for option 'map': Invalid argument".
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-map", $"0:{streamIndex}",
            "-f", "srt",
            outputPath
        };

        _logger.LogInformation("Extracting subtitle: ffmpeg {Args}", string.Join(" ", args));

        var (exitCode, stderr) = await _processes.RunProcessArgumentListAsync(ffmpegPath, args, null, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg subtitle extraction failed: {FfmpegError(stderr)}");
        }
    }

    private static string FfmpegError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "exit code from ffmpeg (no stderr captured)";
        }

        // Last up-to-three non-empty lines usually contain the real reason.
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        var tail = string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 3)));
        return string.IsNullOrEmpty(tail) ? "unknown ffmpeg error" : tail;
    }

}
