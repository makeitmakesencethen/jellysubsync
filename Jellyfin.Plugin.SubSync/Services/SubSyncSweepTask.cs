using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Native Jellyfin Scheduled Task that sweeps the whole library and syncs every
/// external subtitle track that does not yet have a synced output. Runs through
/// the same server-side FIFO queue as manual syncs; repeat runs are cheap thanks
/// to the persistent skip/fail cache (unchanged content whose output still exists
/// is skipped, and repeatedly failing tracks stop being retried until their file
/// content changes).
/// </summary>
/// <remarks>
/// Feature ported from Marnalas/jellyfin-subsync (MIT) — sweep + skip/fail cache —
/// reimplemented on this plugin's in-process engine and FIFO queue.
/// </remarks>
public class SubSyncSweepTask : IScheduledTask
{
    private readonly SubSyncService _service;
    private readonly ILogger<SubSyncSweepTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncSweepTask"/> class.
    /// </summary>
    /// <param name="service">The SubSync service.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public SubSyncSweepTask(SubSyncService service, ILoggerFactory loggerFactory)
    {
        _service = service;
        _logger = loggerFactory.CreateLogger<SubSyncSweepTask>();
    }

    /// <inheritdoc />
    public string Name => "Sync subtitles (library sweep)";

    /// <inheritdoc />
    public string Key => "SubSyncLibrarySweep";

    /// <inheritdoc />
    public string Description => "Scans the library and synchronizes every external subtitle that has no synced copy yet. Already-synced subtitles are skipped unless their content changes; subtitles that keep failing are left alone after a configurable number of attempts.";

    /// <inheritdoc />
    public string Category => "SubSync";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Library sweep started");

        var result = await _service.SweepLibraryAsync(progress, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Library sweep completed: {Scanned} items scanned, {Enqueued} queued, {Cached} skipped (already synced), {Failed} skipped (fail streak), {Ok} ok, {Bad} failed/cancelled",
            result.ScannedItems, result.CandidatesEnqueued, result.SkippedCached, result.SkippedFailed,
            result.Completed, result.FailedOrCancelled);

        progress.Report(1.0);
    }
}
