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
/// The read models, the access surface, the sweep and the batch history.
/// </summary>
/// <remarks>
/// Part of <see cref="SubSyncService"/>, split out of the single file as the C8 cluster of
/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,
/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this
/// code shares with the rest of the service is still the service's own fields, declared in the main
/// file.
/// </remarks>
public partial class SubSyncService
{
    /// <summary>
    /// Whether an account may act on an item: the same check the item-scoped API endpoints make before
    /// they read anything or queue anything.
    /// </summary>
    /// <remarks>
    /// Fails closed at every step. An account that cannot be resolved, an item that cannot be resolved, a
    /// user with no libraries and an item with no ancestors all deny.
    /// </remarks>
    /// <param name="userId">The calling account.</param>
    /// <param name="itemId">The item the request names.</param>
    /// <returns>True when the account may act on the item.</returns>
    public bool CanUserSeeItem(Guid userId, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        // The account's libraries. Read through the preference API rather than a named type: Jellyfin
        // moved its entity namespace between versions, and this plugin must not care which one it built
        // against. Anything unexpected here denies, because the alternative is allowing.
        IReadOnlyCollection<string> folders;
        try
        {
            var enabled = user.GetPreference(PreferenceKind.EnabledFolders);
            folders = enabled is null
                ? Array.Empty<string>()
                : enabled.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        }
        catch (Exception)
        {
            folders = Array.Empty<string>();
        }

        var allFolders = user.HasPermission(PermissionKind.EnableAllFolders);
        var ancestors = AncestorIds(item);
        var allowed = ItemAccess.Allows(allFolders, folders, ancestors);
        if (!allowed)
        {
            // One line, both sides of the comparison. This is what makes a refusal diagnosable in the field:
            // if a legitimate account is ever refused, the log says which folders the item was found in and
            // which libraries the account holds, so the check can be corrected rather than guessed at.
            PluginLog.Info(
                $"item {itemId} not visible to account {userId}: it sits under [{string.Join(", ", ancestors)}], "
                + $"and that account's libraries are [{string.Join(", ", folders)}] (all-folders={allFolders})");
        }

        return allowed;
    }

    /// <summary>
    /// Gets the first item in a request that the account may not act on, or null when it may act on all
    /// of them. The bulk endpoints refuse the whole request when this returns anything.
    /// </summary>
    /// <param name="userId">The calling account.</param>
    /// <param name="itemIds">Every item the request names.</param>
    /// <returns>The first denied item id, or null.</returns>
    public Guid? FirstItemNotVisibleTo(Guid userId, IEnumerable<Guid> itemIds)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            // The account itself cannot be resolved: refuse the request rather than each item, because
            // there is nothing to check any of them against.
            return itemIds.FirstOrDefault();
        }

        foreach (var itemId in itemIds)
        {
            if (!CanUserSeeItem(userId, itemId))
            {
                return itemId;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the ids of every folder an item sits under, itself excluded.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Folder ids, nearest first.</returns>
    private static IReadOnlyCollection<string> AncestorIds(BaseItem item)
    {
        var ids = new List<string>();
        for (var parent = item.GetParent(); parent is not null; parent = parent.GetParent())
        {
            ids.Add(parent.Id.ToString("N"));
        }

        return ids;
    }


    /// <summary>
    /// Lists the subtitle tracks of one item, as the UI offers them.
    /// </summary>
    /// <param name="itemId">Media item.</param>
    /// <returns>The selectable subtitle tracks, or null when the item is not a video.</returns>
    public List<SubtitleInfo>? ListSubtitles(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return null;
        }

        var mediaSources = video.GetMediaSources(true);
        if (mediaSources.Count == 0)
        {
            return new List<SubtitleInfo>();
        }

        var source = mediaSources[0];
        var languageFilter = Services.SettingsSource.Current()?.SyncLanguages ?? Array.Empty<string>();

        // Image-based tracks (PGS, VobSub, DVB, XSUB) can never be aligned — they are
        // left out entirely so they cannot be picked and fail. Tracks outside the
        // configured language filter are hidden too, so the UI only ever offers work
        // that can actually succeed.
        return source.MediaStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
            .Where(s => LanguageSupport.MatchesFilter(s.Language, languageFilter))
            .Select(s =>
            {
                return new SubtitleInfo
                {
                    Index = s.Index,
                    Title = s.DisplayTitle ?? s.Language ?? $"Track {s.Index}",
                    Language = s.Language ?? "und",
                    IsExternal = s.IsExternal,
                    IsForced = s.IsForced,
                    ExternalPath = s.Path,
                    HasSyncedVersion = HasCompletedSync(itemId, s.Index),

                    // A track that cannot be synced is listed and says why (S5), and a sidecar the plugin wrote itself
                    // is listed too (S12): hiding either one made the list disagree with what the queue accepts, so a
                    // file with three subtitles looked like a file with two and the third could be queued by index
                    // without ever having been shown.
                    UnsupportedReason = LanguageSupport.ImageBasedRefusal(s.Codec),
                    IsPluginOutput = MediaStreamMap.IsOwnSidecar(s)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Lists subtitle streams for many items in one call, optionally expanding series and
    /// seasons into their episodes.
    ///
    /// The library browser used to ask for one item (and then one episode) at a time, so a
    /// library-wide selection meant hundreds of sequential round-trips before anything could
    /// be queued. Reading the media streams is in-memory work, so doing it server-side for a
    /// whole chunk of items is roughly free.
    /// </summary>
    /// <param name="itemIds">Items to look up.</param>
    /// <param name="expandSeries">Whether to expand series/seasons into episodes.</param>
    /// <param name="maxEpisodesPerSeries">Safety cap on expansion per requested series.</param>
    /// <returns>One entry per movie/episode, with the episodes of a series carrying its id.</returns>
    public List<BulkSubtitleItem> ListSubtitlesBulk(
        IReadOnlyList<Guid> itemIds,
        bool expandSeries,
        int maxEpisodesPerSeries = 2000)
    {
        var result = new List<BulkSubtitleItem>();
        if (itemIds is null || itemIds.Count == 0)
        {
            return result;
        }

        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                continue;
            }

            if (item is Video video)
            {
                result.Add(new BulkSubtitleItem
                {
                    Id = video.Id,
                    Name = video.Name,
                    Tracks = ListSubtitles(video.Id) ?? new List<SubtitleInfo>()
                });
                continue;
            }

            if (!expandSeries)
            {
                continue;
            }

            if (item is not Folder folder)
            {
                continue;
            }

            IEnumerable<BaseItem> children;
            try
            {
                children = folder.GetRecursiveChildren();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not expand {ItemId} into episodes", itemId);
                continue;
            }

            var count = 0;
            foreach (var child in children.OfType<Video>())
            {
                if (count++ >= maxEpisodesPerSeries)
                {
                    break;
                }

                result.Add(new BulkSubtitleItem
                {
                    Id = child.Id,
                    SeriesId = itemId,
                    Name = child.Name,
                    Tracks = ListSubtitles(child.Id) ?? new List<SubtitleInfo>()
                });
            }
        }

        return result;
    }


    /// <summary>
    /// Formats the periodic progress line. Pure, so a check can pin the wording.
    /// </summary>
    /// <param name="queued">Jobs still queued.</param>
    /// <param name="running">Jobs running now.</param>
    /// <param name="filesLeft">Distinct media files still queued.</param>
    /// <param name="laneFile">The file the extraction lane is on, when it is on one.</param>
    /// <param name="laneElapsed">How long it has been on that file.</param>
    /// <returns>The log line.</returns>
    public static string DescribeProgress(int queued, int running, int filesLeft, string? laneFile, TimeSpan? laneElapsed)
        => $"queue: {queued} queued, {running} running, {filesLeft} file(s) left, "
           + (laneFile is null
               ? "lane idle"
               : $"lane currently on: {laneFile}, {(laneElapsed ?? TimeSpan.Zero).TotalMinutes:0.#} min elapsed on it");

    private static void LogPluginProgress(int queued, int running, int filesLeft, string? laneFile, TimeSpan? laneElapsed)
        => PluginLog.Info(DescribeProgress(queued, running, filesLeft, laneFile, laneElapsed));

    // S25: batches live in memory, and a restart used to lose the History tab entirely. The file is the
    // same shape the sweep already keeps; restoring it adds display rows only, and anything that was
    // still queued or running when the plugin stopped comes back as cancelled, because it was interrupted.
    private void RestoreBatchHistory()
    {
        try
        {
            var path = BatchHistory.DefaultPath;
            var entries = BatchHistory.Load(path);
            if (entries.Count == 0)
            {
                return;
            }

            var restoredJobs = 0;
            foreach (var entry in entries)
            {
                foreach (var stored in entry.Jobs)
                {
                    if (_jobs.ContainsKey(stored.Id))
                    {
                        continue;
                    }

                    var job = new SyncJob
                    {
                        Id = stored.Id,
                        ItemId = stored.ItemId,
                        SubtitleIndex = stored.SubtitleIndex,
                        Mode = SyncJobMode.Normalize(stored.Mode),
                        BatchId = stored.BatchId ?? entry.BatchId,
                        BatchIndex = stored.BatchIndex,
                        BatchLabel = stored.BatchLabel ?? entry.Label,
                        Label = stored.Label,
                        Outcome = stored.Outcome,
                        OutputPath = stored.OutputPath,
                        Error = stored.Error,
                        ExtractionNote = stored.ExtractionNote,
                        Phase = stored.Phase,
                        Progress = stored.Progress,
                        CreatedAtUtc = stored.CreatedAtUtc,
                        FinishedAtUtc = stored.FinishedAtUtc
                    };

                    var status = Enum.TryParse<SyncJobStatus>(stored.Status, true, out var parsed)
                        ? parsed
                        : SyncJobStatus.Completed;
                    if (status is SyncJobStatus.Queued or SyncJobStatus.Running)
                    {
                        status = SyncJobStatus.Cancelled;
                        job.Phase = "Cancelled";
                        job.Outcome = "interrupted by a plugin restart";
                        job.FinishedAtUtc ??= DateTime.UtcNow;
                    }

                    job.Status = status;
                    if (_jobs.TryAdd(job.Id, job))
                    {
                        _historyOnlyJobs.Add(job.Id);
                        restoredJobs++;
                    }
                }
            }

            if (restoredJobs > 0)
            {
                PluginLog.Info(BatchHistory.DescribeRestore(entries.Count, restoredJobs, path));
            }
        }
        catch (Exception ex)
        {
            // A history that cannot be read is not worth a failed start-up.
            _logger.LogDebug(ex, "Could not restore the batch history");
        }
    }

    private List<BatchHistoryEntry> SnapshotBatchHistory()
    {
        var entries = new List<BatchHistoryEntry>();
        foreach (var group in _jobs.Values.Where(j => !string.IsNullOrEmpty(j.BatchId)).GroupBy(j => j.BatchId!))
        {
            entries.Add(new BatchHistoryEntry
            {
                BatchId = group.Key,
                Label = group.Select(j => j.BatchLabel).FirstOrDefault(l => !string.IsNullOrEmpty(l)),
                CreatedUtc = group.Min(j => j.CreatedAtUtc),
                Jobs = group.OrderBy(j => j.BatchIndex).Select(ToHistoryJob).ToList()
            });
        }

        // A sync queued on its own - the detail page's "Sync Subtitles" - is a run of one, and nothing here
        // used to write it: the file held groups only, so the first restart after such a sync showed a history
        // that had never heard of it (G1). It is written under its own derived id (RunId), one job per entry,
        // and BatchHistory keeps a bound of its own for those so a run of them cannot push the batches out.
        foreach (var job in _jobs.Values.Where(j => string.IsNullOrEmpty(j.BatchId)))
        {
            entries.Add(new BatchHistoryEntry
            {
                BatchId = RunId.ForJob(job.Id),
                Label = job.Label,
                CreatedUtc = job.CreatedAtUtc,
                Jobs = new List<BatchHistoryJob> { ToHistoryJob(job) }
            });
        }

        return entries;
    }

    /// <summary>
    /// Maps a job to the shape the history file keeps.
    /// </summary>
    /// <param name="job">The job to record.</param>
    /// <returns>The record written to the history file.</returns>
    private static BatchHistoryJob ToHistoryJob(SyncJob job) => new()
    {
        Id = job.Id,
        ItemId = job.ItemId,
        SubtitleIndex = job.SubtitleIndex,
        Mode = job.Mode,
        BatchId = job.BatchId,
        BatchIndex = job.BatchIndex,
        BatchLabel = job.BatchLabel,
        Label = job.Label,
        Status = job.Status.ToString(),
        Outcome = job.Outcome,
        OutputPath = job.OutputPath,
        Error = job.Error,
        ExtractionNote = job.ExtractionNote,
        Phase = job.Phase,
        Progress = job.Progress,
        CreatedAtUtc = job.CreatedAtUtc,
        FinishedAtUtc = job.FinishedAtUtc
    };

    // Called from the pump's tick. The cheap count signature decides whether anything changed at all,
    // and a minute has to have passed since the last write, so a running batch costs one small write a
    // minute rather than one per completed job.
    private void MaybePersistBatchHistory()
    {
        var terminal = 0;
        foreach (var job in _jobs.Values)
        {
            if (job.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
            {
                terminal++;
            }
        }

        var signature = $"{_jobs.Count}|{terminal}";
        if (signature == _historySignature
            || DateTime.UtcNow - _historySavedUtc < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _historySignature = signature;
        _historySavedUtc = DateTime.UtcNow;
        BatchHistory.Save(BatchHistory.DefaultPath, SnapshotBatchHistory());
    }

    // Called once per pump tick (about every 750 ms) and throttled to once a minute. The pump keeps
    // ticking while an engine run is silent, so a run reports even in the hour it spends inside one file.
    private void MaybeLogProgress()
    {
        if (DateTime.UtcNow - _lastProgressLineUtc < TimeSpan.FromSeconds(ProgressLineSeconds))
        {
            return;
        }

        var queued = CountQueued();
        var running = _jobs.Values.Count(j => j.Status == SyncJobStatus.Running);
        if (queued == 0 && running == 0)
        {
            return;
        }

        _lastProgressLineUtc = DateTime.UtcNow;

        int filesLeft;
        lock (_queueLock)
        {
            filesLeft = _runOrder
                .Where(j => j.Status == SyncJobStatus.Queued)
                .Select(j => _jobContexts.TryGetValue(j.Id, out var c) ? c.Video?.Path : null)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        string? laneFile = null;
        TimeSpan? laneElapsed = null;
        var lane = _passInFlight.OrderBy(kvp => kvp.Value).FirstOrDefault();
        if (lane.Key is not null)
        {
            laneFile = Path.GetFileName(lane.Key);
            laneElapsed = DateTime.UtcNow - lane.Value;
        }

        LogPluginProgress(queued, running, filesLeft, laneFile, laneElapsed);
    }


    /// <summary>
    /// Applies a settings change to the running scheduler at once.
    /// </summary>
    /// <remarks>
    /// The settings page stores through the API and expects what it saved to be in force, so the scheduler is
    /// woken here rather than on the next run: the sync worker limit is read per planning pass, and the
    /// extraction lanes' width is recomputed when the extractor is woken (F13). An increase applies within a
    /// second; a decrease applies as the running extractions finish, because a lane that is mid-file is not
    /// stopped to satisfy a number.
    /// </remarks>
    public void ApplySettingsNow()
    {
        // S7: the settings cache is reset outright, so a save through the API is in force at once rather than
        // within the trust window the settings file is read under.
        Services.SettingsSource.Reset();
        WakePump();
        PluginLog.Info($"settings applied: workers={ConfiguredWorkerLimit} lanes={ConfiguredLaneLimit}");
    }

    /// <summary>
    /// Gets how many extraction lanes the current settings ask for, without starting any.
    /// </summary>
    /// <remarks>Half the worker count, bounded by <see cref="MaxExtractionLanes"/> - the number the UI states.</remarks>
    public int ConfiguredLaneLimit => Math.Clamp(
        (Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers) / 2, 1, MaxExtractionLanes);


    /// <summary>
    /// Gets how many jobs may run at once with the current settings. Reported to the UI so the
    /// effective parallelism is visible instead of inferred.
    /// </summary>
    public int EffectiveWorkerLimit =>
        NormalizeWorkers(Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers);

    /// <summary>
    /// The worker count exactly as configured, without falling back to the default.
    ///
    /// Exposed so the interface can show it beside the limit actually in force: a report of
    /// "4/4 workers" with 8 configured means one of the two is not what the other thinks, and
    /// that is only visible when both are stated.
    /// </summary>
    public int ConfiguredWorkerLimit => Services.SettingsSource.Current()?.ParallelWorkers ?? -1;


    /// <summary>
    /// Runs a full-library sweep: queues one sync job per external subtitle track
    /// that has no synced output yet, honoring the persistent skip/fail cache,
    /// then waits for the queued batch to finish (through the normal FIFO pump).
    /// </summary>
    /// <param name="progress">Sweep progress, 0..1.</param>
    /// <param name="cancellationToken">Cancels queued-but-unstarted tasks.</param>
    /// <returns>Counts describing the run.</returns>
    public async Task<SweepResult> SweepLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = new SweepResult();
        var config = Services.SettingsSource.Current();
        var failStreakLimit = Math.Max(1, config?.SweepFailStreakLimit ?? 3);
        var maxItems = Math.Max(1, config?.SweepMaxItemsPerRun ?? 500);

        var root = _libraryManager.RootFolder;
        var videos = root?.GetRecursiveChildren().OfType<Video>().ToList() ?? new List<Video>();
        result.ScannedItems = videos.Count;
        progress.Report(0.02);

        var tasks = new List<(Guid ItemId, int SubtitleIndex, string? Title)>();
        var seenTracks = new HashSet<(Guid, int)>();

        for (var i = 0; i < videos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = videos[i];

            if (tasks.Count >= maxItems)
            {
                break;
            }

            var tracks = ListSubtitles(video.Id);
            if (tracks is null)
            {
                continue;
            }

            foreach (var track in tracks)
            {
                if (tasks.Count >= maxItems)
                {
                    break;
                }

                if (!track.IsExternal || string.IsNullOrWhiteSpace(track.ExternalPath))
                {
                    continue; // sweep targets external subtitle files only
                }

                if (!seenTracks.Add((video.Id, track.Index)))
                {
                    continue;
                }

                if (track.HasSyncedVersion)
                {
                    result.SkippedOther++;
                    continue;
                }

                var path = track.ExternalPath;
                if (!File.Exists(path))
                {
                    result.SkippedOther++;
                    continue;
                }

                var hash = SweepState.HashFile(path);
                var entry = _sweepState.Value.Get(path);

                if (entry is not null
                    && string.Equals(entry.SourceHash, hash, StringComparison.Ordinal)
                    && entry.OutputPath is not null
                    && File.Exists(entry.OutputPath)
                    && entry.FailStreak == 0)
                {
                    // Same source content, synced output still on disk.
                    result.SkippedCached++;
                    continue;
                }

                if (entry is not null
                    && string.Equals(entry.SourceHash, hash, StringComparison.Ordinal)
                    && entry.FailStreak >= failStreakLimit)
                {
                    result.SkippedFailed++;
                    continue;
                }

                tasks.Add((video.Id, track.Index, $"{video.Name} — {track.Title}"));
            }

            if (i % 50 == 0 || i == videos.Count - 1)
            {
                progress.Report(0.02 + (0.08 * (i + 1) / Math.Max(1, videos.Count)));
            }
        }

        result.CandidatesEnqueued = tasks.Count;
        if (tasks.Count == 0)
        {
            progress.Report(1.0);
            return result;
        }

        var batchLabel = $"Library sweep — {DateTime.Now:yyyy-MM-dd HH:mm}";
        var created = CreateBatch(batchLabel, tasks);
        var batchId = created.Jobs.FirstOrDefault()?.BatchId ?? created.BatchId;
        if (created.Jobs.Count == 0)
        {
            // Every task was already queued or running (D9): there is nothing new for this sweep to run, which is
            // not a failure and must not be reported as one.
            if (created.AlreadyQueued.Count > 0)
            {
                PluginLog.Info($"sweep: nothing new — {created.AlreadyQueued.Count} task(s) were already queued");
                result.FailedOrCancelled = 0;
            }
            else
            {
                result.FailedOrCancelled = tasks.Count;
            }

            progress.Report(1.0);
            return result;
        }

        progress.Report(0.12);

        // Wait for the batch through the same FIFO pump used by the UI, so the
        // sweep never runs parallel to user-triggered syncs.
        while (!cancellationToken.IsCancellationRequested)
        {
            var jobs = GetBatchJobs(batchId).ToList();
            if (jobs.Count == 0)
            {
                break;
            }

            var done = jobs.Count(j => j.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled);
            result.Completed = jobs.Count(j => j.Status == SyncJobStatus.Completed);
            result.FailedOrCancelled = done - result.Completed;
            progress.Report(0.12 + (0.88 * done / Math.Max(1, jobs.Count)));

            if (done >= jobs.Count)
            {
                break;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CancelBatch(batchId);
        }

        // The sweep's records are written in batches; this is the one point the run is over either way, so
        // whatever is still in memory lands here - including on a canceled run, where everything already
        // synced still deserves to be skipped next time.
        _sweepState.Value.Flush();

        _logger.LogInformation(
            "Library sweep finished: {Scanned} items scanned, {Enqueued} queued, {Cached} skipped (synced), {FailedStreak} skipped (fail streak), {Ok} ok, {Bad} failed/cancelled",
            result.ScannedItems, result.CandidatesEnqueued, result.SkippedCached, result.SkippedFailed,
            result.Completed, result.FailedOrCancelled);

        return result;
    }

    private void RecordSweepOutcome(SyncJob job, bool ok, string? outputPath, string? error)
    {
        try
        {
            if (!_jobContexts.TryGetValue(job.Id, out var ctx))
            {
                return;
            }

            if (!ctx.Stream.IsExternal || string.IsNullOrWhiteSpace(ctx.Stream.Path))
            {
                return; // skip/fail cache tracks external subtitle files only
            }

            _sweepState.Value.Record(ctx.Stream.Path, ok, outputPath, error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update sweep cache for job {JobId}", job.Id);
        }
    }


    /// <summary>
    /// What a sync request's item is, and why it cannot be synced when it cannot (D8).
    /// </summary>
    /// <remarks>
    /// Both entry points ask this one question, so a request naming a series, a season or a folder is refused the same
    /// way whether it arrives at the single-sync endpoint or inside a batch. The batch used to accept whatever it was
    /// handed and fail one task at a time later, which is the same work refused twice with a worse report.
    /// </remarks>
    public sealed class SyncTarget
    {
        /// <summary>Gets the item the request named.</summary>
        public Guid ItemId { get; init; }

        /// <summary>Gets a value indicating whether the item exists at all.</summary>
        public bool Found { get; init; }

        /// <summary>Gets a value indicating whether the item is a video.</summary>
        public bool IsVideo { get; init; }

        /// <summary>Gets the reason this item cannot be synced, or null when it can.</summary>
        public string? Refusal { get; init; }

        /// <summary>Gets a value indicating whether this target can be synced.</summary>
        public bool CanSync => Refusal is null;
    }

    /// <summary>
    /// Resolves what a sync request's item is, and why it is not a target when it is not (D8).
    /// </summary>
    /// <param name="itemId">The item the request named.</param>
    /// <returns>The target, with the refusal filled in when it cannot be synced.</returns>
    public SyncTarget InspectSyncTarget(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        return ClassifySyncTarget(
            itemId,
            item?.GetType().Name,
            item is Video);
    }

    /// <summary>
    /// Decides whether a kind of item can be synced, and why not when it cannot (D8).
    /// </summary>
    /// <remarks>
    /// Split from <see cref="InspectSyncTarget"/> so the rule - not the library lookup - is what the checks drive: a
    /// missing item, a series, a season and a folder are each refused with their own sentence, and only a video passes.
    /// </remarks>
    /// <param name="itemId">The item the request named.</param>
    /// <param name="itemKind">The item's type name, or null when it does not exist.</param>
    /// <param name="isVideo">Whether the item is a video.</param>
    /// <returns>The target, with the refusal filled in when it cannot be synced.</returns>
    internal static SyncTarget ClassifySyncTarget(Guid itemId, string? itemKind, bool isVideo)
    {
        if (itemKind is null)
        {
            return new SyncTarget { ItemId = itemId, Refusal = "The item was not found or is not available to this account." };
        }

        if (!isVideo)
        {
            return new SyncTarget
            {
                ItemId = itemId,
                Found = true,
                Refusal = $"Item {itemId} is a {itemKind.ToLowerInvariant()}, not a video: pick the episodes themselves, "
                    + "or use the library sweep. Single sync refuses the same request."
            };
        }

        return new SyncTarget { ItemId = itemId, Found = true, IsVideo = true };
    }

    /// <summary>
    /// Says whether an account is an administrator, for deciding how much of the run list it may see (F4).
    /// </summary>
    /// <param name="userId">The account to resolve.</param>
    /// <returns>True only when Jellyfin says the account has the administrator permission.</returns>
    public bool IsAdministrator(Guid userId)
    {
        try
        {
            return _userManager?.GetUserById(userId)?.HasPermission(PermissionKind.IsAdministrator) ?? false;
        }
        catch (Exception ex)
        {
            // Fails closed: an account that cannot be resolved is not an administrator.
            _logger.LogDebug(ex, "Could not resolve whether {UserId} is an administrator", userId);
            return false;
        }
    }

}
