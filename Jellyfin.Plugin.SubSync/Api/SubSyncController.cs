using System.ComponentModel.DataAnnotations;
using Jellyfin.Plugin.SubSync.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SubSync.Api;

/// <summary>
/// API controller for the SubSync plugin.
/// All endpoints require authentication via Jellyfin's middleware.
/// </summary>
[ApiController]
[Authorize]
[Route("SubSync")]
public class SubSyncController : ControllerBase
{
    private readonly SubSyncService _syncService;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncController"/> class.
    /// </summary>
    /// <param name="syncService">The SubSync service.</param>
    public SubSyncController(SubSyncService syncService)
    {
        _syncService = syncService;
    }

    /// <summary>
    /// Lists subtitle tracks for a given video item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>List of available subtitle tracks.</returns>
    [HttpGet("Subtitles/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<List<SubtitleInfo>> GetSubtitles(Guid itemId)
    {
        var subtitles = _syncService.ListSubtitles(itemId);
        if (subtitles is null)
        {
            return NotFound("Item not found or is not a video.");
        }

        return Ok(subtitles);
    }

    /// <summary>
    /// Starts a subtitle sync job.
    /// </summary>
    /// <param name="request">The sync request containing item ID and subtitle index.</param>
    /// <returns>The created sync job.</returns>
    [HttpPost("Sync")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SyncJob> SyncSubtitle([FromBody] SyncRequest request)
    {
        try
        {
            var job = _syncService.StartSync(request.ItemId, request.SubtitleIndex, request.Mode);
            return Ok(job);
        }
        catch (FileNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets the status of a sync job.
    /// </summary>
    /// <param name="jobId">The sync job ID.</param>
    /// <returns>The sync job status.</returns>
    [HttpGet("Jobs/{jobId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<SyncJob> GetJobStatus(string jobId)
    {
        var job = _syncService.GetJob(jobId);
        if (job is null)
        {
            return NotFound("Job not found.");
        }

        return Ok(job);
    }

    /// <summary>
    /// Lists all sync jobs.
    /// </summary>
    /// <returns>All sync jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<SyncJob>> GetAllJobs()
    {
        return Ok(_syncService.GetAllJobs());
    }

    /// <summary>
    /// Enqueues a whole batch of sync tasks. Jobs run one at a time on the
    /// server in FIFO order; invalid tasks are recorded as failed entries
    /// instead of aborting the batch.
    /// </summary>
    /// <param name="request">Batch label + task list.</param>
    /// <returns>The created batch view.</returns>
    [HttpPost("Batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<BatchView> CreateBatch([FromBody] BatchCreateRequest request)
    {
        if (request.Tasks is null || request.Tasks.Count == 0)
        {
            return BadRequest("Batch must contain at least one task.");
        }

        if (request.Tasks.Count > 1000)
        {
            return BadRequest("Batch is too large (max 1000 tasks).");
        }

        var tasks = request.Tasks
            .Select(t => (t.ItemId, t.SubtitleIndex, Title: t.Title))
            .ToList();

        var jobs = _syncService.CreateBatch(request.Label ?? string.Empty, tasks, request.Mode);
        var batchId = jobs[0].BatchId!;
        return Ok(BuildBatchView(batchId));
    }

    /// <summary>
    /// Gets the current view of a batch (tasks + aggregate progress).
    /// </summary>
    /// <param name="batchId">Batch identifier.</param>
    /// <returns>The batch view.</returns>
    [HttpGet("Batch/{batchId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BatchView> GetBatch(string batchId)
    {
        var view = BuildBatchView(batchId);
        return view is null ? NotFound("Batch not found.") : Ok(view);
    }

    /// <summary>
    /// Lists all batches, newest first (history).
    /// </summary>
    /// <returns>Batch summaries without per-task detail.</returns>
    [HttpGet("Batches")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<BatchSummary>> GetBatches()
    {
        var summaries = new List<BatchSummary>();
        foreach (var (batchId, _) in _syncService.GetBatchIds())
        {
            var view = BuildBatchView(batchId);
            if (view is not null)
            {
                summaries.Add(new BatchSummary
                {
                    Id = view.Id,
                    Label = view.Label,
                    Status = view.Status,
                    Total = view.Total,
                    Completed = view.Completed,
                    Ok = view.Ok,
                    Failed = view.Failed,
                    Cancelled = view.Cancelled,
                    CreatedAtUtc = view.CreatedAtUtc,
                    FinishedAtUtc = view.FinishedAtUtc
                });
            }
        }

        return Ok(summaries);
    }

    /// <summary>
    /// Cancels all not-yet-started tasks of a batch.
    /// </summary>
    /// <param name="batchId">Batch identifier.</param>
    /// <returns>The updated batch view.</returns>
    [HttpPost("Batch/{batchId}/Cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BatchView> CancelBatch(string batchId)
    {
        _syncService.CancelBatch(batchId);
        var view = BuildBatchView(batchId);
        return view is null ? NotFound("Batch not found.") : Ok(view);
    }

    private BatchView? BuildBatchView(string batchId)
    {
        var jobs = _syncService.GetBatchJobs(batchId).ToList();
        if (jobs.Count == 0)
        {
            return null;
        }

        var current = jobs.FirstOrDefault(j => j.Status == Services.SyncJobStatus.Running);
        var queued = jobs.Any(j => j.Status == Services.SyncJobStatus.Queued);
        var running = current is not null;

        string status;
        if (running)
        {
            status = "Running";
        }
        else if (queued)
        {
            status = "Queued";
        }
        else if (jobs.All(j => j.Status == Services.SyncJobStatus.Cancelled))
        {
            status = "Cancelled";
        }
        else if (jobs.Any(j => j.Status == Services.SyncJobStatus.Failed) && jobs.Any(j => j.Status == Services.SyncJobStatus.Completed))
        {
            status = "Partial";
        }
        else if (jobs.Any(j => j.Status == Services.SyncJobStatus.Failed))
        {
            status = "Failed";
        }
        else
        {
            status = "Completed";
        }

        return new BatchView
        {
            Id = batchId,
            Label = jobs.FirstOrDefault(j => j.BatchLabel is not null)?.BatchLabel ?? string.Empty,
            Status = status,
            Total = jobs.Count,
            Completed = jobs.Count(j => j.Status is Services.SyncJobStatus.Completed or Services.SyncJobStatus.Failed or Services.SyncJobStatus.Cancelled),
            Ok = jobs.Count(j => j.Status == Services.SyncJobStatus.Completed),
            Failed = jobs.Count(j => j.Status == Services.SyncJobStatus.Failed),
            Cancelled = jobs.Count(j => j.Status == Services.SyncJobStatus.Cancelled),
            CreatedAtUtc = jobs.Min(j => j.CreatedAtUtc),
            FinishedAtUtc = jobs.Max(j => j.FinishedAtUtc),
            Mode = Services.SyncJobMode.Normalize(jobs[0].Mode),
            PluginVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? string.Empty,
            WorkerSetting = _syncService.ConfiguredWorkerLimit,
            // The width that matters is the one the running jobs are using: if this batch's own
            // mode is non-parallel but jobs from an earlier batch are still running, reporting
            // "3/1 workers" describes neither of them.
            WorkerLimit = Services.SyncJobMode.IsParallel(Services.SyncJobMode.Normalize(
                jobs.FirstOrDefault(j => j.Status == Services.SyncJobStatus.Running)?.Mode ?? jobs[0].Mode))
                ? Math.Clamp(_syncService.EffectiveWorkerLimit, 1, Services.SubSyncService.MaxParallelWorkers)
                : 1,
            RunningTasks = jobs
                .Where(j => j.Status == Services.SyncJobStatus.Running)
                .OrderBy(j => j.BatchIndex)
                .Select(j => new BatchTask
                {
                    BatchIndex = j.BatchIndex,
                    ItemId = j.ItemId,
                    Title = j.Label,
                    Status = j.Status.ToString(),
                    Progress = j.Progress,
                    Phase = j.Phase,
                    Error = j.Error,
                    OutputPath = j.OutputPath,
                    Outcome = j.Outcome,
                    StartedAtUtc = j.StartedAtUtc
                })
                .ToList(),
            CurrentTask = current is null ? null : new BatchTask
            {
                BatchIndex = current.BatchIndex,
                ItemId = current.ItemId,
                Title = current.Label,
                Status = current.Status.ToString(),
                Progress = current.Progress,
                Phase = current.Phase,
                Error = current.Error,
                OutputPath = current.OutputPath,
                Outcome = current.Outcome,
                StartedAtUtc = current.StartedAtUtc
            },
            Tasks = jobs.OrderBy(j => j.BatchIndex).Select(j => new BatchTask
            {
                BatchIndex = j.BatchIndex,
                ItemId = j.ItemId,
                Title = j.Label,
                Status = j.Status.ToString(),
                Progress = j.Progress,
                Phase = j.Phase,
                Error = j.Error,
                OutputPath = j.OutputPath,
                Outcome = j.Outcome,
                ExtractionNote = j.ExtractionNote
            }).ToList()
        };
    }

    /// <summary>
    /// Gets the ffsubsync installation status.
    /// </summary>
    /// <returns>Detailed installation status.</returns>
    [HttpGet("InstallationStatus")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<FfSubSyncInstallationStatus>> GetInstallationStatus()
    {
        var status = await _syncService.GetInstallationStatusAsync().ConfigureAwait(false);
        return Ok(status);
    }

    /// <summary>
    /// Installs ffsubsync into the managed virtualenv.
    /// </summary>
    /// <returns>Installation result.</returns>
    [HttpPost("Install")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> InstallFfSubSync()
    {
        try
        {
            await _syncService.InstallFfSubSyncAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Ok(new { message = "ffsubsync installed successfully." });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("already in progress"))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
        catch (Exception ex)
        {
            return BadRequest($"Installation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Empties every cache this plugin keeps: the audio analysis ("fast" mode), the reference
    /// subtitles, and the scratch directories of jobs that are no longer running. Safe at any time -
    /// each entry is rebuilt the next time it is needed, so clearing only costs time.
    /// </summary>
    /// <returns>What was removed, and what is left.</returns>
    [HttpPost("SpeechCache/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ClearSpeechCache()
    {
        var removedAudio = Services.SpeechCache.Clear();
        Services.ReferenceStore.Clear();
        var removedScratch = _syncService.ClearStaleJobDirectories();

        return Ok(new
        {
            removed = removedAudio,
            removedAudio,
            removedScratch,
            message = $"Cleared {removedAudio} cached audio analysis file{(removedAudio == 1 ? "" : "s")}, "
                + "the reference subtitles of this run, and "
                + $"{removedScratch} job scratch folder{(removedScratch == 1 ? "" : "s")}.",
            cache = Services.SpeechCache.Describe()
        });
    }

    /// <summary>
    /// Lists subtitle streams for many items at once (optionally expanding series/seasons
    /// into their episodes), so the library browser does not have to make one request per
    /// file before it can queue anything.
    /// </summary>
    /// <param name="request">Item ids and whether to expand series.</param>
    /// <returns>One entry per movie/episode with its syncable tracks.</returns>
    [HttpPost("Subtitles/Batch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<object> GetSubtitlesBatch([FromBody] SubtitleBatchRequest request)
    {
        if (request.ItemIds is null || request.ItemIds.Count == 0)
        {
            return BadRequest("No item ids supplied.");
        }

        if (request.ItemIds.Count > 500)
        {
            return BadRequest("Too many items in one request (max 500).");
        }

        var items = _syncService.ListSubtitlesBulk(request.ItemIds, request.ExpandSeries);
        return Ok(new { items });
    }

    /// <summary>
    /// Kills every sync: queued tasks (any batch) are cancelled and running ffsubsync/ffmpeg
    /// processes are terminated. This is the destructive action behind the UI's Kill button.
    /// </summary>
    /// <returns>What was stopped, plus what is still running afterwards.</returns>
    [HttpPost("Kill")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> KillAll()
    {
        var (queuedCancelled, runningKilled) = _syncService.KillAll();
        var (running, queued) = _syncService.GetActive();
        return Ok(new
        {
            queuedCancelled,
            runningKilled,
            stillRunning = running.Count,
            stillQueued = queued
        });
    }

    /// <summary>
    /// Reports what is executing right now (running jobs and queued count), so the UI can
    /// tell the user whether a cancel has fully taken effect.
    /// </summary>
    /// <returns>The active work summary.</returns>
    [HttpGet("Active")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> GetActive()
    {
        var (running, queued) = _syncService.GetActive();
        return Ok(new
        {
            running = running.Select(j => new
            {
                jobId = j.Id,
                batchId = j.BatchId,
                title = j.Label,
                phase = j.Phase,
                progress = j.Progress
            }).ToList(),
            queued
        });
    }

    /// <summary>
    /// Returns the tail of the plugin's own log file as plain text.
    ///
    /// Exists so the log can be read or downloaded from the interface: handing over a log for
    /// debugging should not require shell access to the server the plugin runs on.
    /// </summary>
    /// <param name="kilobytes">How much of the end of the file to return (4-4096 kB).</param>
    /// <returns>The tail of the log as text.</returns>
    [HttpGet("Log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLog([FromQuery] int kilobytes = 256)
    {
        var take = Math.Clamp(kilobytes, 4, 4096) * 1024;
        return Content(Services.PluginLog.Tail(take), "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Serves the client-side injection script that adds "Sync Subtitles" to video pages.
    /// </summary>
    /// <returns>The JavaScript content.</returns>
    [HttpGet("ClientScript")]
    [Produces("application/javascript")]
    [AllowAnonymous]
    public IActionResult GetClientScript()
    {
        var js = GetEmbeddedResource("Jellyfin.Plugin.SubSync.Web.subsync.js");
        if (js is null)
        {
            return NotFound();
        }

        return Content(js, "application/javascript");
    }

    private string? GetEmbeddedResource(string resourceName)
    {
        var assembly = typeof(SubSyncController).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Request body for listing the subtitles of many items at once.
/// </summary>
public class SubtitleBatchRequest
{
    /// <summary>Gets or sets the items to list subtitles for.</summary>
    public List<Guid>? ItemIds { get; set; }

    /// <summary>Gets or sets a value indicating whether series/seasons expand into episodes.</summary>
    public bool ExpandSeries { get; set; } = true;
}

/// <summary>
/// Request body for starting a subtitle sync.
/// </summary>
public class SyncRequest
{
    /// <summary>
    /// Gets or sets the multi-subtitle mode (normal | parallel | fast). Optional;
    /// defaults to the configured MultiSyncMode.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the subtitle stream index.</summary>
    [Range(0, 999, ErrorMessage = "Subtitle index must be non-negative")]
    public int SubtitleIndex { get; set; }
}

/// <summary>
/// One task inside a batch create request.
/// </summary>
public class BatchTaskRequest
{
    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the subtitle stream index.</summary>
    public int SubtitleIndex { get; set; }

    /// <summary>Gets or sets an optional display title for the task.</summary>
    public string? Title { get; set; }
}

/// <summary>
/// Request body for creating a batch.
/// </summary>
public class BatchCreateRequest
{
    /// <summary>
    /// Gets or sets the mode for the whole batch (normal | parallel | fast).
    /// Optional; defaults to the configured MultiSyncMode.
    /// </summary>
    public string? Mode { get; set; }

    /// <summary>Gets or sets the scope label (shown in history).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the task list.</summary>
    public List<BatchTaskRequest>? Tasks { get; set; }
}

/// <summary>
/// Per-task view inside a batch.
/// </summary>
public class BatchTask
{
    /// <summary>Gets or sets when the task started running (null while queued).</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>Gets or sets the 0-based task position.</summary>
    public int BatchIndex { get; set; }

    /// <summary>Gets or sets the Jellyfin item ID (episode/movie) this task belongs to.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the display title.</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets the status string.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the progress 0..1.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets the phase label.</summary>
    public string? Phase { get; set; }

    /// <summary>Gets or sets the error message on failure.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the written output path on success.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets what the sync changed (e.g. "−1250 ms offset"), set on success.</summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// Gets or sets how the embedded subtitle was obtained and what it cost (e.g. "read through the
    /// container index (matroska-cues), 31 ms" or "demuxed with ffmpeg, 96000 ms"). Shown with the
    /// task result so a slow run can be traced to the reader that did it instead of the server log.
    /// </summary>
    public string? ExtractionNote { get; set; }
}

/// <summary>
/// Full view of one batch (used for live progress and expanded history).
/// </summary>
public class BatchView
{
    /// <summary>Gets or sets the batch identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the multi-subtitle mode this batch runs in.</summary>
    public string Mode { get; set; } = Services.SyncJobMode.Normal;

    /// <summary>
    /// Gets or sets every task currently running. Parallel and ultimate modes run
    /// several media files at once, so the UI shows one row per running task.
    /// </summary>
    public List<BatchTask> RunningTasks { get; set; } = new();

    /// <summary>
    /// Gets or sets the largest worker count the server accepts, so the pages clamp against the
    /// same number the server uses instead of carrying their own copy.
    /// </summary>
    public int WorkerCeiling { get; set; } = Services.SubSyncService.MaxParallelWorkers;

    /// <summary>
    /// Gets or sets the worker count as configured, so the page can show it beside the limit in
    /// force and reveal any disagreement between the two.
    /// </summary>
    public int WorkerSetting { get; set; }

    /// <summary>
    /// Gets or sets the running plugin version, so the page can show which build it is talking
    /// to instead of leaving it to be worked out from the interface.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many tasks this batch may run at once (the configured worker count for
    /// parallel strategies). Reported next to the running count so a lower-than-expected
    /// parallelism can be traced to the setting instead of guessed at.
    /// </summary>
    public int WorkerLimit { get; set; }

    /// <summary>Gets or sets the scope label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the aggregate status (Queued/Running/Completed/Partial/Failed/Cancelled).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the total task count.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the count of finished tasks (any terminal state).</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets the count of succeeded tasks.</summary>
    public int Ok { get; set; }

    /// <summary>Gets or sets the count of failed tasks.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the count of cancelled tasks.</summary>
    public int Cancelled { get; set; }

    /// <summary>Gets or sets the batch creation time.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the time the batch finished (if it has).</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Gets or sets the currently running task, if any.</summary>
    public BatchTask? CurrentTask { get; set; }

    /// <summary>Gets or sets all tasks in batch order.</summary>
    public List<BatchTask> Tasks { get; set; } = new();
}

/// <summary>
/// Lightweight batch entry for the history list.
/// </summary>
public class BatchSummary
{
    /// <summary>Gets or sets the batch identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the scope label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the aggregate status.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets or sets the total task count.</summary>
    public int Total { get; set; }

    /// <summary>Gets or sets the count of finished tasks.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets the count of succeeded tasks.</summary>
    public int Ok { get; set; }

    /// <summary>Gets or sets the count of failed tasks.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets the count of cancelled tasks.</summary>
    public int Cancelled { get; set; }

    /// <summary>Gets or sets the batch creation time.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Gets or sets the time the batch finished (if it has).</summary>
    public DateTime? FinishedAtUtc { get; set; }
}
