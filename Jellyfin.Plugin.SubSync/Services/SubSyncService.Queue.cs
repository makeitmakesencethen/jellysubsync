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
/// The queue surface: enqueue, batch, cancel, kill, job lookup.
/// </summary>
/// <remarks>
/// Part of <see cref="SubSyncService"/>, split out of the single file as the C1 cluster of
/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,
/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this
/// code shares with the rest of the service is still the service's own fields, declared in the main
/// file.
/// </remarks>
public partial class SubSyncService
{
    /// <summary>
    /// Starts a sync job for the given item and subtitle stream index.
    /// Automatically ensures ffsubsync is available before running.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="subtitleIndex">The subtitle stream index within the first media source.</param>
    /// <param name="mode">Multi-subtitle mode (normal | parallel | fast).</param>
    /// <param name="ownerId">The account whose request this is, when one made it (F4).</param>
    /// <returns>The created sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found or ffsubsync is unavailable.</exception>
    public SyncJob StartSync(Guid itemId, int subtitleIndex, string? mode = null, Guid? ownerId = null)
    {
        return EnqueueSync(
            itemId,
            subtitleIndex,
            label: null,
            batchId: null,
            batchLabel: null,
            batchIndex: -1,
            mode: mode,
            ownerId: ownerId);
    }

    /// <summary>
    /// Creates and queues a sync job. Validation (item is a video, subtitle
    /// stream exists) happens NOW so bad requests fail immediately; execution
    /// happens later via the single background pump.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="subtitleIndex">The subtitle stream index within the first media source.</param>
    /// <param name="label">Optional human label (subtitle/track title).</param>
    /// <param name="batchId">Batch this job belongs to, if any.</param>
    /// <param name="batchLabel">Batch scope label, if any.</param>
    /// <param name="batchIndex">0-based position inside the batch.</param>
    /// <param name="mode">Multi-subtitle mode (normal | parallel | fast).</param>
    /// <param name="ownerId">The account whose request this is, when one made it (F4).</param>
    /// <returns>The queued sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found.</exception>
    public SyncJob EnqueueSync(Guid itemId, int subtitleIndex, string? label, string? batchId, string? batchLabel, int batchIndex, string? mode = null, Guid? ownerId = null)
    {
        return EnqueueSyncTimed(itemId, subtitleIndex, label, batchId, batchLabel, batchIndex, mode, ownerId).Job;
    }

    /// <summary>
    /// Queues one task and reports where the time went.
    /// </summary>
    /// <remarks>
    /// Queueing has to be fast: the scheduler can only start what is already in the queue, and a
    /// batch that trickles in one task every few seconds therefore looks exactly like a plugin that
    /// refuses to run more than one job at a time. The parts are timed so the slow one can be named
    /// instead of guessed at.
    /// </remarks>
    /// <param name="itemId">Media item.</param>
    /// <param name="subtitleIndex">Subtitle stream index.</param>
    /// <param name="label">Display label.</param>
    /// <param name="batchId">Batch this task belongs to.</param>
    /// <param name="batchLabel">Batch label.</param>
    /// <param name="batchIndex">Position within the batch.</param>
    /// <param name="mode">Requested mode.</param>
    /// <param name="ownerId">The account whose request this is, when one made it (F4).</param>
    /// <returns>
    /// The queued job, the cost of each part, and whether the queue already held a job for this item and track
    /// (in which case that job is the one returned - D9).
    /// </returns>
    internal (SyncJob Job, string Timing, bool Duplicate) EnqueueSyncTimed(
        Guid itemId,
        int subtitleIndex,
        string? label,
        string? batchId,
        string? batchLabel,
        int batchIndex,
        string? mode = null,
        Guid? ownerId = null)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var phase = System.Diagnostics.Stopwatch.StartNew();
        long itemMs = 0, sourcesMs = 0, settingsMs = 0, logMs = 0;
        long stateMs = 0;        // the two dictionary writes: what the enqueue records about the job
        long logWriteMs = 0;    // one line to the plugin log, which serialises every writer
        long lockMs = 0;        // the queue lock, also held by the pump while it plans
        long wakeMs = 0;        // waking the pump: starts extraction lanes, takes the lock twice more
        long settingsReadMs = 0; // the settings file, which is stat-ed to notice a hand-edited change
        long jellyfinLogMs = 0;  // Jellyfin's own log sink, whose writes also serialise
        long duplicateCheckMs = 0; // the duplicate job check, which is the one lock wait on the enqueue path
        long settingsUnaccountedMs = 0; // time inside the settings phase that neither of its two calls spent
        if (subtitleIndex < 0)
        {
            throw new ArgumentException("Subtitle index must be non-negative.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            throw new InvalidOperationException($"Item {itemId} is not a video.");
        }

        // No filesystem access in the enqueue path. This check used to stat the media file per task,
        // and with a job already reading that share each stat took seconds: a 50-task batch was
        // enqueued over a minute, so the scheduler only ever saw a handful of queued jobs and could
        // not fill its workers ("starting 1 of 8"). A missing file is caught when the job runs, with
        // the same clear message.
        // Validation therefore works from Jellyfin's cached metadata only.
        itemMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var mediaSources = video.GetMediaSources(true);
        if (mediaSources.Count == 0)
        {
            throw new InvalidOperationException("No media sources found for the video.");
        }

        var source = mediaSources[0];
        var subtitleStream = source.MediaStreams
            .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && s.Index == subtitleIndex);

        if (subtitleStream is null)
        {
            throw new InvalidOperationException($"Subtitle stream index {subtitleIndex} not found.");
        }

        // The listing says why a bitmap track cannot be synced (S5); queueing one says the same thing rather than
        // failing later inside the engine, where the message is about a subtitle it could not read.
        var imageRefusal = LanguageSupport.ImageBasedRefusal(subtitleStream.Codec);
        if (imageRefusal is not null)
        {
            throw new InvalidOperationException(imageRefusal);
        }

        // Embedded extraction never trusts Jellyfin's stream numbering: the real container
        // stream index is resolved at run time by probing the file with ffmpeg (see
        // ResolveContainerSubtitleIndexAsync). What the rest of the service means by "the
        // subtitle's ordinal" is its position among the file's embedded subtitle tracks - the
        // numbering the extraction lane, the subtitle cache and ffmpeg's own 0:s:N speak. That is
        // not Jellyfin's MediaStream.Index, which counts every stream in the file, video and audio
        // included, so the two differ by however many streams sit in front of the subtitles.
        // Handing the index to code that wanted an ordinal refused five jobs on a real run
        // ("subtitle ordinal 11 out of range (11 tracks)"), and on files where the index happened
        // to land inside the range it made the lane read a neighbouring track instead.
        var subtitleOrdinal = MediaStreamMap.EmbeddedSubtitleOrdinal(source.MediaStreams, subtitleStream);

        sourcesMs = phase.ElapsedMilliseconds;
        phase.Restart();

        var settingsWatch = System.Diagnostics.Stopwatch.StartNew();
        var config = Services.SettingsSource.Current() ?? new Configuration.PluginConfiguration();
        settingsReadMs = settingsWatch.ElapsedMilliseconds;

        var jellyfinLogWatch = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation(
            "Queued sync: item {ItemId} subtitle stream {SubtitleIndex} — output mode: {Mode}",
            itemId, subtitleIndex, config.SyncModeCopy ? "copy (.SYNCED.srt)" : "replace original in place");
        jellyfinLogMs = jellyfinLogWatch.ElapsedMilliseconds;

        // The settings phase ends here, before the duplicate check's critical section: that wait belongs to
        // `queueLock`, and leaving it inside `settings` is what made a lock wait look like a storage read (S7).
        settingsMs = phase.ElapsedMilliseconds;
        phase.Restart();

        // One job per item and track (D9): asking twice used to queue two jobs, so the same subtitle was read,
        // synced and written twice while the second waited for the first one's file gate. The queued job is
        // returned instead, and the caller can say "already queued" instead of implying a second run exists.
        SyncJob? alreadyQueued;
        lock (_queueLock)
        {
            alreadyQueued = FindDuplicate(_runOrder, itemId, subtitleIndex);
        }

        if (alreadyQueued is not null)
        {
            total.Stop();

            // B15: written after the lock is released, not inside it. The plugin log serialises every writer on
            // its own gate, so a line written while the queue lock is held keeps the enqueue path - the very thing
            // this lock exists to keep short - waiting for another thread's log write. The same shape was measured
            // in the cancel paths (S40) and fixed the same way there.
            PluginLog.Info(
                $"queue duplicate: item={itemId} stream={subtitleIndex} existing={alreadyQueued.Id} "
                + $"status={alreadyQueued.Status} batch={alreadyQueued.BatchId ?? "(standalone)"}");

            // A job another account queued is not this caller's to be handed (F4): returning it would give out an
            // identifier its owner cannot read - the run list does not contain it and asking for it answers 404 - so
            // the page would attach to a run it can never follow. Plugins queued by the plugin itself have no owner
            // and are treated as everyone's, which is how a sweep's run is reported to whoever asks next.
            if (alreadyQueued.OwnerId is not null && alreadyQueued.OwnerId != ownerId)
            {
                throw new SyncQueueConflictException(
                    $"That subtitle track is already queued by another account (run {alreadyQueued.Id}). It is "
                    + "processed once; ask again when it has finished.");
            }

            return (alreadyQueued, $"totalMs={total.ElapsedMilliseconds} duplicate=1", true);
        }

        // The duplicate check's wait on the queue lock is reported on its own, not folded into `settings` (S7):
        // a lock wait read as a storage read is how a burst's cost got attributed to the wrong thing.
        duplicateCheckMs = phase.ElapsedMilliseconds;
        phase.Restart();

        var job = new SyncJob
        {
            OwnerId = ownerId,
            ItemId = itemId,
            SubtitleIndex = subtitleIndex,
            BatchId = batchId,
            BatchLabel = batchLabel,
            BatchIndex = batchIndex,
            Label = label,
            Mode = NormalizeMode(mode ?? config.MultiSyncMode)
        };
        phase.Restart();

        _jobs[job.Id] = job;
        _jobContexts[job.Id] = (video, subtitleStream, subtitleOrdinal, config);
        stateMs = phase.ElapsedMilliseconds;
        phase.Restart();

        var queuedLine = $"queued: job={job.Id} item={itemId} stream={subtitleIndex} ordinal={subtitleOrdinal} "
            + $"mode={job.Mode} batch={batchId ?? "(standalone)"} language={subtitleStream.Language ?? "und"} "
            + $"external={subtitleStream.IsExternal} forced={subtitleStream.IsForced} "
            + $"codec={subtitleStream.Codec} video={video.Path}";
        PluginLog.Info(queuedLine);
        logWriteMs = phase.ElapsedMilliseconds;
        phase.Restart();

        lock (_queueLock)
        {
            _runOrder.Add(job);
        }

        lockMs = phase.ElapsedMilliseconds;
        phase.Restart();

        WakePump();
        wakeMs = phase.ElapsedMilliseconds;
        logMs = stateMs + logWriteMs + lockMs + wakeMs;
        total.Stop();

        var timing = $"item={itemMs} ms, sources={sourcesMs} ms, settings={settingsMs} ms, "
            + $"log={logMs} ms, total={total.ElapsedMilliseconds} ms";

        // The settings phase contains two calls and nothing else, so whatever it measures beyond them is time this
        // thread was not running - a contended host or a GC pause - and it is named rather than left looking like
        // storage work (S7: the worst enqueue of a 27-task burst reported `settings=270 ms` with both of its calls
        // at 0 ms, which is a scheduling pause, not a read).
        settingsUnaccountedMs = Math.Max(0, settingsMs - settingsReadMs - jellyfinLogMs);

        // Anything beyond a few milliseconds here is worth naming: a slow enqueue is invisible in
        // every other view and looks exactly like a scheduler that will not parallelise.
        //
        // The breakdown is what S40 needs: the phase the field reported as `log` is four different things -
        // two dictionary writes, one line to the plugin log, the queue lock, and waking the pump (which
        // takes the same lock twice more) - and 8-21 s per queued item has to be attributed to one of them
        // before anything is changed. The plugin log serialises every writer on one gate, the queue lock is
        // held by the pump while it plans, and waking the pump starts extraction lanes: all three are
        // candidates that the aggregate cannot tell apart.
        if (total.ElapsedMilliseconds > EnqueueTraceMs)
        {
            PluginLog.Info(
                $"enqueue slow: {timing} stream={subtitleIndex} breakdown: state={stateMs} ms, "
                + $"logWrite={logWriteMs} ms, queueLock={lockMs} ms, wakePump={wakeMs} ms, "
                + $"settingsRead={settingsReadMs} ms, jellyfinLog={jellyfinLogMs} ms, "
                + $"duplicateCheck={duplicateCheckMs} ms, settingsUnaccounted={settingsUnaccountedMs} ms "
                + $"settingsStats={Services.SettingsSource.Stats} settingsReads={Services.SettingsSource.Reads} "
                + $"video={video.Path}");
        }

        return (job, timing, false);
    }

    /// <summary>
    /// Enqueues a batch of tasks as one FIFO unit. Tasks that fail validation
    /// are recorded as failed jobs inside the batch instead of aborting it.
    /// </summary>
    /// <param name="label">Scope label shown in history (e.g. "Series · Season 2").</param>
    /// <param name="tasks">The task list (item, subtitle index, display title).</param>
    /// <param name="mode">Multi-subtitle mode for the whole batch (normal | parallel | fast).</param>
    /// <param name="ownerId">The account whose request this is, when one made it (F4).</param>
    /// <returns>What the bulk enqueue created, and which requested tasks were already queued (D9).</returns>
    public BatchCreation CreateBatch(
        string label,
        IReadOnlyList<(Guid ItemId, int SubtitleIndex, string? Title)> tasks,
        string? mode = null,
        Guid? ownerId = null)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var resolvedMode = NormalizeMode(mode ?? Services.SettingsSource.Current()?.MultiSyncMode);
        var jobs = new List<SyncJob>(tasks.Count);
        var alreadyQueued = new List<SyncJob>();
        var batchWatch = System.Diagnostics.Stopwatch.StartNew();
        var slowestMs = 0L;
        var slowestIndex = -1;
        var slowestDetail = string.Empty;
        var previousEnd = 0L;

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var taskWatch = System.Diagnostics.Stopwatch.StartNew();
            // Time spent between the previous task and this one, inside this loop. If a queue fills
            // slowly but every task's own parts are fast, the cost is here, not in the task.
            var gapMs = taskWatch.ElapsedMilliseconds - previousEnd;
            var detail = string.Empty;
            try
            {
                var queued = EnqueueSyncTimed(
                    task.ItemId,
                    task.SubtitleIndex,
                    task.Title,
                    batchId,
                    label,
                    i,
                    resolvedMode,
                    ownerId);
                detail = queued.Timing;
                if (queued.Duplicate)
                {
                    // Already queued or running: not counted as a task of this batch, because the job belongs to
                    // the batch that queued it and counting it here would show the same work twice (D9).
                    alreadyQueued.Add(queued.Job);
                }
                else
                {
                    jobs.Add(queued.Job);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                _logger.LogWarning("Batch {BatchId} task {Index} failed validation: {Error}", batchId, i, ex.Message);
                var failed = new SyncJob
                {
                    OwnerId = ownerId,
                    ItemId = task.ItemId,
                    SubtitleIndex = task.SubtitleIndex,
                    BatchId = batchId,
                    BatchLabel = label,
                    BatchIndex = i,
                    Label = task.Title,
                    Mode = resolvedMode,
                    Status = SyncJobStatus.Failed,
                    Error = ex.Message,
                    FinishedAtUtc = DateTime.UtcNow
                };
                _jobs[failed.Id] = failed;
                jobs.Add(failed);
            }

            taskWatch.Stop();
            previousEnd = taskWatch.ElapsedMilliseconds;
            if (taskWatch.ElapsedMilliseconds > slowestMs)
            {
                slowestMs = taskWatch.ElapsedMilliseconds;
                slowestIndex = i;
                slowestDetail = $"gap={gapMs} ms; {detail}";
            }
        }

        batchWatch.Stop();

        // How long it took to get the whole batch into the queue, and which part of the slowest task
        // was slow. This matters more than it looks: while a batch is still being enqueued the
        // scheduler only sees the first few tasks, so a slow enqueue looks exactly like "the plugin
        // refuses to run more than one at a time".
        PluginLog.Info(
            $"batch {batchId} queued: tasks={tasks.Count} mode={resolvedMode} label='{label}' "
            + $"totalMs={batchWatch.ElapsedMilliseconds} slowestTaskMs={slowestMs} (index {slowestIndex}: {slowestDetail})");

        return new BatchCreation { BatchId = batchId, Jobs = jobs, AlreadyQueued = alreadyQueued };
    }

    /// <summary>
    /// Cancels all queued (not yet started) jobs of a batch. A running job is
    /// allowed to finish.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    public void CancelBatch(string batchId)
    {
        var queuedCancelled = 0;
        List<SyncJob> cancelledHere;
        var cancelHold = System.Diagnostics.Stopwatch.StartNew();
        lock (_queueLock)
        {
            // Only the status changes while the lock is held. A log line per cancelled job used to be written
            // here - Jellyfin's file sink and the plugin log both, once per job - so cancelling a large batch
            // held the lock the enqueue path waits on for as long as those writes took (S40).
            cancelledHere = _runOrder
                .Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Queued)
                .ToList();
            foreach (var job in cancelledHere)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
            }

            queuedCancelled += cancelledHere.Count;
        }

        cancelHold.Stop();
        LogLockHold("cancel-batch", cancelHold.ElapsedMilliseconds);
        foreach (var job in cancelledHere)
        {
            _logger.LogInformation("Cancelled queued job {JobId} of batch {BatchId}", job.Id, batchId);
            LogPluginCancellation(job);
        }

        // A cancel that leaves the batch running is the complaint it always produces ("I pressed it and
        // it kept going"). The jobs of this batch that are already running are stopped too, and the
        // plugin log records which phases they were in, because a kill that silently misses a job in
        // "Analyzing speech" is indistinguishable from a kill that never arrived.
        var stopped = 0;
        var phases = new List<string>();
        foreach (var job in _jobs.Values.Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Running))
        {
            phases.Add($"{job.Id[..8]}={job.Phase}");
            if (_jobCancellation.TryGetValue(job.Id, out var cts))
            {
                try
                {
                    cts.Cancel();
                    stopped++;
                    LogPluginCancellation(job);
                }
                catch (ObjectDisposedException)
                {
                    // Finished between the check and the cancel.
                }
            }
        }

        if (queuedCancelled > 0 || stopped > 0)
        {
            PluginLog.Info(
                $"cancel batch {batchId}: {queuedCancelled} queued cancelled, {stopped} running stopped"
                + (phases.Count == 0 ? string.Empty : $" (phases: {string.Join(", ", phases)})"));
        }
    }


    /// <summary>
    /// Kills everything: queued jobs (any batch) are cancelled and every running job's
    /// ffsubsync/ffmpeg processes are terminated. Backs the UI's Kill action, which is
    /// what the Cancel button turns into while processes are still running.
    /// </summary>
    /// <returns>How many jobs were dropped from the queue and how many runs were killed.</returns>
    public (int QueuedCancelled, int RunningKilled) KillAll()
    {
        var queuedCancelled = 0;
        List<SyncJob> cancelledAll;
        var cancelAllHold = System.Diagnostics.Stopwatch.StartNew();
        lock (_queueLock)
        {
            // Statuses only, for the reason the batch cancel gives: one log write per job inside this lock is
            // the enqueue path's wait (S40).
            cancelledAll = _runOrder.Where(j => j.Status == SyncJobStatus.Queued).ToList();
            foreach (var job in cancelledAll)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.Phase = "Cancelled";
            }

            queuedCancelled += cancelledAll.Count;
        }

        cancelAllHold.Stop();
        LogLockHold("cancel-all", cancelAllHold.ElapsedMilliseconds);
        foreach (var job in cancelledAll)
        {
            LogPluginCancellation(job);
        }

        var runningKilled = 0;
        foreach (var kvp in _jobCancellation)
        {
            try
            {
                kvp.Value.Cancel();
                runningKilled++;
                if (_jobs.TryGetValue(kvp.Key, out var killedJob))
                {
                    LogPluginCancellation(killedJob);
                }
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the check and the cancel.
            }
        }

        // Cancelling a token only helps processes that poll it. Kill the trees directly as
        // well: ffsubsync spawns ffmpeg, and both must be gone before the file handles are
        // released and the job is really finished.
        // The extraction lane reads a whole file in one pass and is not a job, so it needs its own
        // stop: measured before this, a kill left the lane reading 805.6 MB afterwards while
        // /SubSync/Active reported nothing running. The source is replaced immediately so the next
        // pass is not born cancelled.
        try
        {
            var stopping = _laneStop;
            _laneStop = new CancellationTokenSource();
            stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already took it.
        }

        // What is reported is what actually exited, not what was asked to (B16): the count used to be the
        // larger of "process trees killed" and "run tokens cancelled", which is a number nobody measured - the
        // interface said "N stopped" for processes that were still alive. Each killed process is awaited on its
        // own handle with a deadline, so this is a wait rather than a poll. Shared with teardown (B14).
        var (processesAskedToStop, processesStopped) = _processes.KillChildProcesses();
        var survivors = _processes.LiveCount();
        _logger.LogInformation(
            "Kill requested: {Queued} queued task(s) cancelled, {Running} run token(s) cancelled, {Killed} process tree(s) killed, {Survivors} still alive",
            queuedCancelled, runningKilled, processesStopped, survivors);

        // The plugin's own log, so a kill is verifiable from the one file that gets handed over for
        // debugging. Which phases the jobs were in matters: a job killed while extracting a subtitle
        // and a job killed while analysing speech fail in the same place otherwise.
        var runningPhases = _jobs.Values
            .Where(j => j.Status == SyncJobStatus.Running)
            .Select(j => $"{j.Id[..8]}={j.Phase} ({j.Label ?? j.BatchLabel ?? j.ItemId.ToString()[..8]})")
            .ToList();
        PluginLog.Info(
            $"KILL requested: {queuedCancelled} queued cancelled, {runningKilled} run token(s), "
            + $"{processesStopped} of {processesAskedToStop} process tree(s) stopped, {survivors} survivor(s)"
            + (runningPhases.Count == 0 ? string.Empty : " \u00b7 still reporting: " + string.Join(", ", runningPhases)));

        // The second number is processes that really exited - measured, not estimated from the tokens that
        // were cancelled or the trees that were signalled (B16).
        return (queuedCancelled, processesStopped);
    }

    /// <summary>
    /// Gets a summary of what is executing right now, so the UI can tell the user whether
    /// anything is still running after a cancel.
    /// </summary>
    /// <returns>Running jobs and the queued count.</returns>
    public (IReadOnlyList<SyncJob> Running, int Queued) GetActive()
    {
        var running = _jobs.Values.Where(j => j.Status == SyncJobStatus.Running).OrderBy(j => j.CreatedAtUtc).ToList();
        var queued = _jobs.Values.Count(j => j.Status == SyncJobStatus.Queued);
        return (running, queued);
    }

    /// <summary>
    /// Gets all tracked jobs belonging to a batch, ordered by batch position.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    /// <returns>Ordered jobs of the batch.</returns>
    public IEnumerable<SyncJob> GetBatchJobs(string batchId)
    {
        return _jobs.Values
            .Where(j => j.BatchId == batchId)
            .OrderBy(j => j.BatchIndex);
    }

    /// <summary>
    /// Gets all batch ids seen so far, newest first.
    /// </summary>
    /// <returns>Distinct batch ids with their newest job creation time.</returns>
    public IEnumerable<(string BatchId, DateTime CreatedAt)> GetBatchIds()
    {
        return _jobs.Values
            .Where(j => j.BatchId is not null)
            .GroupBy(j => j.BatchId!)
            .Select(g => (BatchId: g.Key, CreatedAt: g.Min(j => j.CreatedAtUtc)))
            .OrderByDescending(g => g.CreatedAt);
    }


    /// <summary>
    /// Gets a sync job by its ID.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The sync job, or null if not found.</returns>
    public SyncJob? GetJob(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    /// <summary>
    /// Gets all sync jobs.
    /// </summary>
    /// <returns>All tracked sync jobs.</returns>
    public IEnumerable<SyncJob> GetAllJobs()
    {
        return _jobs.Values;
    }


    /// <summary>
    /// The outcome of a bulk enqueue (D9): what was created, and what the queue already held.
    /// </summary>
    public sealed class BatchCreation
    {
        /// <summary>Gets the batch identifier, set whether or not any task was new.</summary>
        public string BatchId { get; init; } = string.Empty;

        /// <summary>Gets the jobs this call created (validation failures appear as failed rows).</summary>
        public IReadOnlyList<SyncJob> Jobs { get; init; } = Array.Empty<SyncJob>();

        /// <summary>
        /// Gets the jobs the queue already held for the requested item and track, which were therefore not
        /// queued a second time.
        /// </summary>
        public IReadOnlyList<SyncJob> AlreadyQueued { get; init; } = Array.Empty<SyncJob>();
    }


    /// <summary>
    /// Finds the queued or running job for the same item and subtitle track, if there is one (D9).
    /// </summary>
    /// <remarks>
    /// A second job for the same track is not a second opinion: it reads the same subtitle, runs the same
    /// alignment and writes the same output, while the first job's file gate makes it wait. Only live jobs
    /// count - a finished job is history, and syncing the same track again later is a legitimate request.
    /// </remarks>
    /// <param name="jobs">Jobs in queue order.</param>
    /// <param name="itemId">Media item.</param>
    /// <param name="subtitleIndex">Subtitle stream index.</param>
    /// <returns>The job already queued for that track, or null.</returns>
    internal static SyncJob? FindDuplicate(IEnumerable<SyncJob> jobs, Guid itemId, int subtitleIndex)
        => jobs.FirstOrDefault(job => job.ItemId == itemId
            && job.SubtitleIndex == subtitleIndex
            && job.Status is SyncJobStatus.Queued or SyncJobStatus.Running);


    /// <summary>
    /// Stops exactly the jobs it is given (F2).
    /// </summary>
    /// <remarks>
    /// A scoped stop cancels the work it was told to cancel and nothing else: no lane is stopped, and no child process
    /// is killed directly, because either would take down a run belonging to somebody the caller never named. A
    /// cancelled job's own runner kills its child through the token it registered, which is why the scoped path is
    /// still a real stop rather than just a label.
    /// </remarks>
    /// <param name="targets">Jobs to stop.</param>
    /// <returns>How many queued jobs were cancelled and how many running jobs were asked to stop.</returns>
    public (int QueuedCancelled, int RunningKilled) KillJobs(IEnumerable<SyncJob> targets)
    {
        var queuedCancelled = 0;
        var runningKilled = 0;
        foreach (var job in targets.ToList())
        {
            if (job.Status == SyncJobStatus.Queued)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.Phase = "Cancelled";
                queuedCancelled++;
                LogPluginCancellation(job);
                continue;
            }

            if (job.Status != SyncJobStatus.Running || !_jobCancellation.TryGetValue(job.Id, out var cts))
            {
                continue;
            }

            try
            {
                cts.Cancel();
                runningKilled++;
                LogPluginCancellation(job);
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the check and the cancel.
            }
        }

        PluginLog.Info($"kill: scope={queuedCancelled + runningKilled} queued={queuedCancelled} running={runningKilled}");
        return (queuedCancelled, runningKilled);
    }

    /// <summary>
    /// Says whether anything is running right now, and what (F6).
    /// </summary>
    /// <remarks>
    /// A cache clear cannot know which cached file a running job is reading: the reference subtitles are handed to the
    /// engine as arguments and the audio and feature caches are keyed by a hash, so "delete everything except what is
    /// in use" is not a question this plugin can answer from the outside. The question it can answer is whether
    /// anything is using the cache at all, which is what this reports.
    /// </remarks>
    /// <returns>How many jobs are running, and how many are queued.</returns>
    public (int Running, int Queued) ActiveJobCounts()
        => (_jobs.Values.Count(job => job.Status == SyncJobStatus.Running),
            _jobs.Values.Count(job => job.Status == SyncJobStatus.Queued));


    /// <summary>
    /// Waits, for a bounded time, for the extraction lanes and the pump to finish (B14).
    /// </summary>
    /// <param name="milliseconds">Deadline in milliseconds.</param>
    /// <returns>How many of those tasks had completed by the deadline.</returns>
    internal int WaitForLanes(int milliseconds)
        => WaitForTasks(
            _pumpTask is null ? _laneTasks : _laneTasks.Concat(new[] { _pumpTask }),
            milliseconds);


    /// <summary>
    /// Picks the finished jobs a cleanup pass removes: everything past <paramref name="maxAge"/>, plus the
    /// oldest beyond <paramref name="maxJobs"/> when a run really has produced that many (B17).
    /// </summary>
    /// <param name="jobs">Every tracked job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="maxAge">How long a finished job is kept.</param>
    /// <param name="maxJobs">Most finished jobs to keep at all.</param>
    /// <param name="isHistoryOnly">Answers whether a row is a restored history entry rather than a job.</param>
    /// <returns>Keys to remove.</returns>
    internal static List<string> JobsToEvict(
        IReadOnlyDictionary<string, SyncJob> jobs,
        DateTime now,
        TimeSpan maxAge,
        int maxJobs,
        Func<string, bool> isHistoryOnly)
    {
        var finished = jobs
            .Where(pair => !isHistoryOnly(pair.Key))
            .Where(pair => pair.Value.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
            .OrderBy(pair => pair.Value.FinishedAtUtc ?? pair.Value.CreatedAtUtc)
            .ToList();

        var cutoff = now - maxAge;
        var toRemove = finished
            .Where(pair => (pair.Value.FinishedAtUtc ?? pair.Value.CreatedAtUtc) < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        var kept = finished.Count - toRemove.Count;
        if (kept > maxJobs)
        {
            var over = kept - maxJobs;
            toRemove.AddRange(finished
                .Where(pair => !toRemove.Contains(pair.Key))
                .Take(over)
                .Select(pair => pair.Key));
        }

        return toRemove;
    }


    /// <summary>
    /// Checks if a completed sync job exists for the given item and subtitle index.
    /// </summary>
    private bool HasCompletedSync(Guid itemId, int subtitleIndex)
    {
        return _jobs.Values.Any(j =>
            j.ItemId == itemId &&
            j.SubtitleIndex == subtitleIndex &&
            j.Status == SyncJobStatus.Completed);
    }


    // The per-job cancellation line (S23). Cancelling used to leave only the batch-level count, so the
    // only way to reconcile a cancelled batch from the plugin log was to take that number on trust.
    // Same shape as the completed and failed lines, so every job in a batch has one terminal line of its
    // own whatever happened to it.
    private static void LogPluginCancellation(SyncJob job)
        => PluginLog.Info(
            $"job {job.Id} cancelled: mode={job.Mode} item={job.ItemId} stream={job.SubtitleIndex}");

}
