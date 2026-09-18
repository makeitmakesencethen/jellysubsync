using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Jellyfin.Plugin.SubSync.Configuration;
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
[TypeFilter(typeof(SubSyncExceptionFilter))]
public class SubSyncController : ControllerBase
{
    /// <summary>
    /// Jellyfin's own policy for administrator-only endpoints.
    ///
    /// The name is the one Jellyfin registers and uses itself (Jellyfin.Api's ApiKeyController), so
    /// the plugin inherits the server's idea of "administrator" rather than inventing a check. It
    /// gates the four endpoints that have no per-user meaning: installing packages, wiping the
    /// caches, stopping every run on the server, and reading the plugin log with the server's own
    /// paths in it.
    ///
    /// The item-scoped endpoints are open to any authenticated account -*about the items that account
    /// can see*, which is not the same thing and is what `ItemAccess` decides. They used to take the
    /// item id on trust, so any account could read any item's subtitle list or queue a sync that wrote
    /// a subtitle for an item in a library it had no access to (F3, verified 2026-09-14). The check is
    /// per item rather than per role on purpose: the "Sync Subtitles" button on a detail page must keep
    /// working for a non-admin, and making it admin-only would remove the feature instead of guarding
    /// it.
    /// </summary>
    private const string RequiresElevationPolicy = "RequiresElevation";

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
    /// Whether the calling account may act on an item.
    /// </summary>
    /// <remarks>
    /// The refusal is logged with the item id, because the plugin log is admin-only and belongs to the
    /// server operator, while the caller gets a reply that does not say whether the item exists.
    /// </remarks>
    /// <param name="itemId">The item the request names.</param>
    /// <returns>True when the caller may act on it.</returns>
    private bool CallerMayActOn(Guid itemId)
    {
        var userId = CallerId();
        if (userId is null)
        {
            PluginLog.Info($"refused item {itemId}: the request carries no account id");
            return false;
        }

        if (_syncService.CanUserSeeItem(userId.Value, itemId))
        {
            return true;
        }

        PluginLog.Info($"refused item {itemId} for account {userId.Value}: not in a library that account can see");
        return false;
    }

    /// <summary>
    /// Refuses a request that names several items when any one of them is not available to the caller.
    /// </summary>
    /// <remarks>
    /// The whole request is refused rather than the offending item dropped: a partially queued batch looks
    /// to the user like a batch that started, with no way to tell what was left out.
    /// </remarks>
    /// <param name="itemIds">Every item the request names.</param>
    /// <returns>A refusal to return, or null when the caller may act on all of them.</returns>
    private ActionResult? RefuseInvisibleItems(IEnumerable<Guid> itemIds)
    {
        var userId = CallerId();
        if (userId is null)
        {
            PluginLog.Info("refused a bulk request: the request carries no account id");
            return Fail(StatusCodes.Status403Forbidden, "Not permitted", "This request could not be attributed to an account.");
        }

        var denied = _syncService.FirstItemNotVisibleTo(userId.Value, itemIds);
        if (denied is not null)
        {
            PluginLog.Info(
                $"refused a bulk request for account {userId.Value}: item {denied.Value} is not in a library that account can see");
            return Fail(StatusCodes.Status403Forbidden, "Not permitted", "One or more items in this request are not available to this account.");
        }

        return null;
    }

    /// <summary>
    /// Gets the calling account's id from the request's claims, or null when there is none to trust.
    /// </summary>
    /// <returns>The account id, or null.</returns>
    private Guid? CallerId() => ItemAccess.UserIdFrom(
        User.Claims.Select(c => new KeyValuePair<string, string>(c.Type, c.Value)));

    /// <summary>
    /// Says whether the calling account is an administrator, which decides how much of the run list it sees (F4).
    /// </summary>
    /// <returns>True only when Jellyfin says so.</returns>
    private bool CallerIsAdmin() => CallerId() is Guid id && _syncService.IsAdministrator(id);

    /// <summary>
    /// Refuses a request naming an item that cannot be synced, in the same shape from every entry point (D8).
    /// </summary>
    /// <remarks>
    /// The single-sync endpoint refused a series, season or folder; the batch endpoint accepted them and failed one
    /// task at a time later. Both now ask the service the same question, so the same request gets the same answer
    /// wherever it arrives.
    /// </remarks>
    /// <param name="itemId">The item the request named.</param>
    /// <returns>The refusal, or null when the item can be synced.</returns>
    private ObjectResult? RefuseUntargetable(Guid itemId)
    {
        var target = _syncService.InspectSyncTarget(itemId);
        if (target.CanSync)
        {
            return null;
        }

        return target.Found
            ? Fail(StatusCodes.Status400BadRequest, "Not a video", target.Refusal!)
            : Fail(StatusCodes.Status404NotFound, "Item not found", target.Refusal!);
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
        if (!CallerMayActOn(itemId))
        {
            return Fail(404, "Item not found", "The item was not found or is not available to this account.");
        }

        var subtitles = _syncService.ListSubtitles(itemId);
        if (subtitles is null)
        {
            return Fail(404, "Not a video", "The item was not found or is not a video.");
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
        if (!CallerMayActOn(request.ItemId))
        {
            return Fail(404, "Item not found", "The item was not found or is not available to this account.");
        }

        var untargetable = RefuseUntargetable(request.ItemId);
        if (untargetable is not null)
        {
            return untargetable;
        }

        var isAdmin = CallerIsAdmin();
        try
        {
            var job = _syncService.StartSync(request.ItemId, request.SubtitleIndex, request.Mode, CallerId());

            // A job the caller did not create can still come back from the queue (D9's duplicate rule returns the job
            // already queued for this track); the paths in it are the server's, so they are removed for anyone but an
            // administrator.
            return Ok(ItemAccess.ForViewer(job, isAdmin));
        }
        catch (FileNotFoundException ex)
        {
            return Fail(404, "Job not found", ex.Message);
        }
        catch (Services.SubSyncService.SyncQueueConflictException ex)
        {
            // Nothing about the request is wrong: the queue holds that work for another account (F4).
            return Fail(StatusCodes.Status409Conflict, "Already queued", ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Fail(400, "Request refused", ex.Message);
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
            return Fail(404, "Job not found", "No job with that identifier.");
        }

        // A job that is not the caller's is answered exactly like one that does not exist (F4): another account's run
        // is not something to confirm the existence of.
        var isAdmin = CallerIsAdmin();
        if (!ItemAccess.MaySeeJob(job, CallerId(), isAdmin))
        {
            return Fail(404, "Job not found", "No job with that identifier.");
        }

        return Ok(ItemAccess.ForViewer(job, isAdmin));
    }

    /// <summary>
    /// Lists all sync jobs.
    /// </summary>
    /// <returns>All sync jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<SyncJob>> GetAllJobs()
    {
        var isAdmin = CallerIsAdmin();
        var visible = ItemAccess.VisibleJobs(_syncService.GetAllJobs(), CallerId(), isAdmin)
            .Select(job => ItemAccess.ForViewer(job, isAdmin))
            .ToList();

        return Ok(visible);
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
            return Fail(400, "Empty batch", "A batch must contain at least one task.");
        }

        if (request.Tasks.Count > 1000)
        {
            return Fail(400, "Batch too large", "A batch carries at most 1000 tasks.");
        }

        var refused = RefuseInvisibleItems(request.Tasks.Select(t => t.ItemId));
        if (refused is not null)
        {
            return refused;
        }

        // The same rule single sync applies (D8): a batch used to accept a series, season or folder and fail one task
        // at a time later, which is the same refusal with a worse report and wasted engine work.
        foreach (var task in request.Tasks)
        {
            var untargetable = RefuseUntargetable(task.ItemId);
            if (untargetable is not null)
            {
                return untargetable;
            }
        }

        var tasks = request.Tasks
            .Select(t => (t.ItemId, t.SubtitleIndex, Title: t.Title))
            .ToList();

        var created = _syncService.CreateBatch(request.Label ?? string.Empty, tasks, request.Mode, CallerId());
        if (created.Jobs.Count == 0 && created.AlreadyQueued.Count == 0)
        {
            // Every task failed validation: the batch exists as failed rows only.
            return Ok(BuildCreatedBatchView(created.BatchId, 0));
        }

        var createdView = BuildBatchViewForCaller(created.BatchId, created.AlreadyQueued.Count)
            ?? BuildCreatedBatchView(created.BatchId, created.AlreadyQueued.Count);
        return Ok(createdView);
    }

    /// <summary>
    /// Builds the body every failed SubSync request has: the status, a short title, and a detail the page can
    /// show (D10). Deliberate refusals used to answer with a bare string while a thrown failure answered with
    /// Jellyfin's "Error processing request." - two shapes, one of which said nothing.
    /// </summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="title">Short summary of what was refused.</param>
    /// <param name="detail">The specific reason, suitable to show to the user.</param>
    /// <returns>The response body as an <see cref="ObjectResult"/>.</returns>
    internal static ObjectResult Fail(int status, string title, string detail)
        => new(new ProblemDetails { Status = status, Title = title, Detail = detail }) { StatusCode = status };

    /// <summary>
    /// Builds the view of a batch for the calling account, hiding what it may not see (F4).
    /// </summary>
    /// <remarks>
    /// A batch belongs to the accounts whose tasks are in it. A viewer with none of them gets the same answer as for a
    /// batch that does not exist, and a viewer who does own tasks in it does not get the server's paths for the tasks
    /// that are someone else's.
    /// </remarks>
    /// <param name="batchId">Batch identifier.</param>
    /// <param name="alreadyQueuedCount">How many tasks the queue already held, reported once on creation (D9).</param>
    /// <returns>The view, or null when the caller may see none of it.</returns>
    private BatchView? BuildBatchViewForCaller(string batchId, int alreadyQueuedCount = 0)
    {
        var isAdmin = CallerIsAdmin();
        var callerId = CallerId();
        if (!isAdmin && !_syncService.GetBatchJobs(batchId).Any(job => ItemAccess.MaySeeJob(job, callerId, false)))
        {
            return null;
        }

        var view = BuildBatchView(batchId, alreadyQueuedCount);
        if (view is null || isAdmin)
        {
            return view;
        }

        foreach (var task in view.Tasks)
        {
            ItemAccess.HideServerPaths(task, false);
        }

        return view;
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
        var view = BuildBatchViewForCaller(batchId);
        return view is null ? Fail(404, "Batch not found", "No batch with that identifier.") : Ok(view);
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
            var view = BuildBatchViewForCaller(batchId);
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
        // Refused before anything is cancelled: an account may not stop a run it cannot see (F4).
        var view = BuildBatchViewForCaller(batchId);
        if (view is null)
        {
            return Fail(404, "Batch not found", "No batch with that identifier.");
        }

        _syncService.CancelBatch(batchId);
        return Ok(BuildBatchViewForCaller(batchId)!);
    }

    /// <summary>
    /// Builds the view of a batch.
    /// </summary>
    /// <param name="batchId">Batch identifier.</param>
    /// <param name="alreadyQueuedCount">How many requested tasks the queue already held (D9), reported once on creation.</param>
    /// <returns>The view, or null when the batch has no jobs.</returns>
    private BatchView? BuildBatchView(string batchId, int alreadyQueuedCount = 0)
    {
        var view = BuildBatchViewCore(batchId);
        if (view is not null && alreadyQueuedCount > 0)
        {
            view.AlreadyQueuedCount = alreadyQueuedCount;
        }

        return view;
    }

    /// <summary>
    /// Builds the view returned when a batch is created, including a batch whose every task was already queued (D9).
    /// </summary>
    /// <remarks>
    /// The batch view is null when a batch has no jobs, which is how a request for an unknown batch is answered with
    /// 404. Creating such a batch is not an error - it means the work was already queued - so it answers with an empty
    /// view that carries the count, and the page can say "nothing new, N already queued" instead of showing nothing.
    /// </remarks>
    /// <param name="batchId">Batch identifier.</param>
    /// <param name="alreadyQueuedCount">How many tasks the queue already held.</param>
    /// <returns>The view of the batch that was just created.</returns>
    private BatchView BuildCreatedBatchView(string batchId, int alreadyQueuedCount)
    {
        var view = BuildBatchViewCore(batchId) ?? new BatchView { Id = batchId, Status = "Completed" };
        view.AlreadyQueuedCount = alreadyQueuedCount;
        return view;
    }

    /// <summary>
    /// Builds the view of a batch from its jobs.
    /// </summary>
    /// <param name="batchId">Batch identifier.</param>
    /// <returns>The view, or null when the batch has no jobs.</returns>
    private BatchView? BuildBatchViewCore(string batchId)
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
            // A run of one has no batch label to show, so its row is titled by the item the job carries -
            // what the user picked on the detail page (G1). Batches keep the label their caller sent.
            Label = Services.RunId.IsSingle(batchId)
                ? jobs[0].Label ?? string.Empty
                : jobs.FirstOrDefault(j => j.BatchLabel is not null)?.BatchLabel ?? string.Empty,
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
                    Status = StatusOf(j),
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
                Status = StatusOf(current),
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
                Status = StatusOf(j),
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
        [Authorize(Policy = RequiresElevationPolicy)]
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
            return Fail(StatusCodes.Status503ServiceUnavailable, "Not available", ex.Message);
        }
        catch (Exception ex)
        {
            return Fail(400, "Installation failed", ex.Message);
        }
    }

    /// <summary>
    /// Empties every cache this plugin keeps: the audio analysis ("fast" mode), the reference
    /// subtitles, and the scratch directories of jobs that are no longer running. Safe at any time -
    /// each entry is rebuilt the next time it is needed, so clearing only costs time.
    /// </summary>
    /// <returns>What was removed, and what is left.</returns>
        [Authorize(Policy = RequiresElevationPolicy)]
    [HttpPost("SpeechCache/Clear")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> ClearSpeechCache()
    {
        // Nothing referenced by a running job may be deleted (F6). Which file a run is reading cannot be worked out
        // from here - a reference is handed to the engine as an argument and the audio and feature caches are keyed by
        // a hash - so the clear refuses while anything is running instead of guessing and deleting a file in use.
        var (runningCount, queuedCount) = _syncService.ActiveJobCounts();
        if (runningCount > 0)
        {
            return Fail(
                StatusCodes.Status409Conflict,
                "Runs are using the cache",
                $"{runningCount} run(s) are reading cached files right now (and {queuedCount} more are queued). Clearing "
                + "the cache would delete a file a run is using, so it was not cleared: let them finish, or stop them "
                + "first.");
        }

        var removedAudio = Services.SpeechCache.Clear();
        var removedSubtitles = Services.SubtitleCache.Clear();
        Services.ReferenceStore.Clear();
        var removedScratch = _syncService.ClearStaleJobDirectories();

        return Ok(new
        {
            removed = removedAudio,
            removedAudio,
            removedScratch,
            removedSubtitles,
            message = $"Cleared {removedAudio} cached audio analysis file{(removedAudio == 1 ? "" : "s")}, "
                + $"{removedSubtitles} extracted subtitle{(removedSubtitles == 1 ? "" : "s")}, "
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
            return Fail(400, "No items", "No item ids were supplied.");
        }

        if (request.ItemIds.Count > 500)
        {
            return Fail(400, "Too many items", "At most 500 items can be scanned in one request.");
        }

        var refused = RefuseInvisibleItems(request.ItemIds);
        if (refused is not null)
        {
            return refused;
        }

        var items = _syncService.ListSubtitlesBulk(request.ItemIds, request.ExpandSeries);
        return Ok(new { items });
    }

    /// <summary>
    /// Kills every sync: queued tasks (any batch) are cancelled and running ffsubsync/ffmpeg
    /// processes are terminated. This is the destructive action behind the UI's Kill button.
    /// </summary>
    /// <returns>What was stopped, plus what is still running afterwards.</returns>
        [Authorize(Policy = RequiresElevationPolicy)]
    [HttpPost("Kill")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<object> KillAll([FromBody] KillRequest? request)
    {
        var isAdmin = CallerIsAdmin();
        var (targets, refusal) = ItemAccess.SelectKillTargets(
            _syncService.GetAllJobs(),
            CallerId(),
            isAdmin,
            request?.JobId,
            request?.BatchId,
            request?.All ?? false);

        if (refusal is not null)
        {
            return refusal switch
            {
                ItemAccess.KillRefusal.NothingSpecified => Fail(
                    StatusCodes.Status400BadRequest,
                    "Say what to stop",
                    "Name what to stop: {\"all\": true} for every run (administrators only), or a batchId, or a jobId."),
                ItemAccess.KillRefusal.NotPermitted => Fail(
                    StatusCodes.Status403Forbidden,
                    "Not permitted",
                    "Stopping every run on the server needs an administrator. Stop your own work with a batchId or a jobId."),
                _ => Fail(
                    StatusCodes.Status404NotFound,
                    "Nothing to stop",
                    "No queued or running work matched that request."),
            };
        }

        // The global case keeps its full effect - the extraction lanes and every child process go too - because that
        // is what an administrator asking for "all" means. A scoped stop touches only what it was told to (F2).
        var (queuedCancelled, runningKilled) = request?.All == true
            ? _syncService.KillAll()
            : _syncService.KillJobs(targets);
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
        [Authorize(Policy = RequiresElevationPolicy)]
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
            return Fail(404, "Not found", "No such SubSync resource.");
        }

        return Content(js, "application/javascript");
    }

    /// <summary>
    /// Reads the plugin configuration.
    /// </summary>
    /// <remarks>
    /// The plugin page used to read and write its settings through the web client's
    /// <c>ApiClient.getPluginConfiguration</c>. That global is not defined when the page runs in
    /// Jellyfin 12 (measured: <c>typeof window.ApiClient</c> stayed "undefined" for 22 seconds), and a
    /// page that depends on it shows an empty settings form and makes no call at all. These two
    /// endpoints are the page's own way in, the same shape as the rest of the plugin's API.
    /// </remarks>
    /// <returns>The stored configuration.</returns>
    [Authorize(Policy = RequiresElevationPolicy)]
    [HttpGet("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration?> GetConfiguration()
        => Ok(Plugin.Instance?.Configuration);

    /// <summary>
    /// Stores the plugin configuration.
    /// </summary>
    /// <param name="body">The configuration as the page read it, with its changes on top.</param>
    /// <returns>The configuration as the server stored it.</returns>
    [Authorize(Policy = RequiresElevationPolicy)]
    [HttpPost("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<PluginConfiguration?> SaveConfiguration([FromBody] JsonElement body)
    {
        // What the page sends back is what this API serialized (camelCase), so names are matched
        // without regard to case; a configuration the model cannot carry is refused rather than
        // half-stored.
        PluginConfiguration? wanted;
        try
        {
            wanted = body.Deserialize<PluginConfiguration>(CaseInsensitiveJson);
        }
        catch (JsonException ex)
        {
            return Fail(400, "Configuration unreadable", ex.Message);
        }

        if (wanted is null)
        {
            return Fail(400, "Empty body", "The request body carried no configuration.");
        }

        // One validation path for the settings: what cannot mean anything is brought into range here rather
        // than stored as typed, and the response says what was adjusted so the page can report it instead of a
        // bare "Saved." (D3/F10).
        var notes = Configuration.SettingsValidation.Apply(wanted);
        Plugin.Instance!.UpdateConfiguration(wanted);

        // The body stays exactly what it always was - the page reads these fields back and compares them - so
        // what was adjusted is reported by SettingsValidationNotes instead (D3).
        Plugin.LastSettingsNotes = notes;

        // What was just saved is in force from here, not from the next restart (F13): the scheduler re-reads
        // the worker limit on its next pass and the extraction lanes' width is recomputed now.
        _syncService.ApplySettingsNow();
        return Ok(Plugin.Instance.Configuration);
    }

    /// <summary>
    /// Reports what the last stored configuration had to be adjusted by, if anything.
    /// </summary>
    /// <remarks>
    /// A setting that cannot mean anything is brought into range instead of stored as typed, and a setting that
    /// was silently dropped used to look exactly like a stored one ("Saved." either way). The settings page reads
    /// this right after a save and shows it (D3/F10).
    /// </remarks>
    /// <returns>One short line per adjustment, empty when the configuration was stored as sent.</returns>
    [Authorize(Policy = RequiresElevationPolicy)]
    [HttpGet("Settings/ValidationNotes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> SettingsValidationNotes()
        => Ok(Plugin.LastSettingsNotes);

    /// <summary>
    /// The outcome of a job as the API reports it.
    /// </summary>
    /// <remarks>
    /// A refusal is not a failure: the plugin decided, on purpose, that it will not write what it
    /// measured (a reference taken from another cut, or a rescaled result). Both used to be reported as
    /// <c>Failed</c>, so a bulk run of 100 tracks read as "2 failed" when nothing had gone wrong — and
    /// the plan's acceptance criterion is "zero failures". The job's phase already says "Refused", so the
    /// status says it too, and a caller can tell the two apart.
    /// </remarks>
    /// <param name="job">The job to describe.</param>
    /// <returns>The status name.</returns>
    private static string StatusOf(SyncJob job)
        => (job.Status == SyncJobStatus.Failed && string.Equals(job.Phase, "Refused", StringComparison.Ordinal))
            ? "Refused"
            : job.Status.ToString();

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private static readonly JsonSerializerOptions CaseInsensitiveJson =
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Serves the plugin page's client script.
    /// </summary>
    /// <remarks>
    /// The page used to carry this script inline and a real browser showed the page rendered but inert
    /// (no request of any kind, status line unfilled), so the script is served the same way the
    /// item-page client is — one file the plugin owns, referenced by the page it belongs to.
    /// </remarks>
    /// <returns>The script, or 404 when the build did not embed it.</returns>
    [HttpGet("MainScript")]
    [Produces("application/javascript")]
    [AllowAnonymous]
    public IActionResult GetMainScript()
    {
        var js = GetEmbeddedResource("Jellyfin.Plugin.SubSync.Web.subsyncMain.js");
        if (js is null)
        {
            return Fail(404, "Not found", "No such SubSync resource.");
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
/// Request body for stopping work (F2).
/// </summary>
/// <remarks>
/// The cancel endpoint used to take no body at all and stop everything, including another administrator's run. A
/// request now says what it means: <see cref="All"/> for every run on the server (administrators only),
/// <see cref="BatchId"/> for one batch, or <see cref="JobId"/> for one run.
/// </remarks>
public class KillRequest
{
    /// <summary>Gets or sets the run to stop.</summary>
    public Guid? JobId { get; set; }

    /// <summary>Gets or sets the batch to stop.</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets a value indicating whether every run on the server should be stopped (administrators only).</summary>
    public bool All { get; set; }
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

    /// <summary>
    /// Gets or sets how many of the requested tasks were already queued or running when the batch was created and
    /// were therefore not queued twice (D9). Shown by the page so a smaller-than-expected run is explained.
    /// </summary>
    public int AlreadyQueuedCount { get; set; }

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
