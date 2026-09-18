using System;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Names a run that is not a batch: one job the user queued on its own.
/// </summary>
/// <remarks>
/// Every run the plugin shows is addressed by a batch id, and a sync started from a Jellyfin detail page
/// has none: the page posts to <c>/SubSync/Sync</c> and the job is enqueued without a batch on purpose,
/// because there is no group for it to belong to. The read models asked only for jobs that carry a batch,
/// so a detail-page sync ran, wrote its subtitle and appeared in no history at all - not in the tab, not
/// in the file that survives a restart (FIX_PLAN G1).
///
/// A run of one is addressed by a derived id rather than by a batch invented at enqueue time: the job
/// itself is untouched, and no fake group is written anywhere. The derived id cannot collide with a real
/// batch id, which the pages mint as 32 hex characters, so <see cref="IsSingle"/> tells the two apart with
/// no extra bookkeeping and an existing history file stays readable.
/// </remarks>
public static class RunId
{
    /// <summary>The prefix a single-job run id carries.</summary>
    public const string SinglePrefix = "single:";

    /// <summary>
    /// Builds the run id of one job queued on its own.
    /// </summary>
    /// <param name="jobId">The job's id.</param>
    /// <returns>The run id the job is shown and read back under.</returns>
    public static string ForJob(string jobId) => SinglePrefix + jobId;

    /// <summary>
    /// Reports whether a run id addresses a single job rather than a batch.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <returns>True for a single job's own run id.</returns>
    public static bool IsSingle(string? runId)
        => runId is not null && runId.StartsWith(SinglePrefix, StringComparison.Ordinal);

    /// <summary>
    /// Extracts the job id from a single job's run id.
    /// </summary>
    /// <param name="runId">The run id.</param>
    /// <returns>The job id, or null when the run id does not address a single job.</returns>
    public static string? JobIdOf(string? runId)
        => IsSingle(runId) ? runId![SinglePrefix.Length..] : null;
}
