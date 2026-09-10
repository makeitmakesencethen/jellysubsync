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
                Outcome = current.Outcome
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
                Outcome = j.Outcome
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
    /// Empties the cached speech analysis ("fast" mode). Safe at any time: entries are
    /// rebuilt on the next fast-mode run.
    /// </summary>
    /// <returns>The new cache summary.</returns>
    [HttpPost("SpeechCache/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ClearSpeechCache()
    {
        var removed = Services.SpeechCache.Clear();
        return Ok(new
        {
            removed,
            cache = Services.SpeechCache.Describe()
        });
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
}

/// <summary>
/// Full view of one batch (used for live progress and expanded history).
/// </summary>
public class BatchView
{
    /// <summary>Gets or sets the batch identifier.</summary>
    public string Id { get; set; } = string.Empty;

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
