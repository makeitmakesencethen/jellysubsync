using System.Security.Claims;
using Jellyfin.Plugin.SubSync.Api;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Decides who may act on which item.
/// </summary>
/// <remarks>
/// Deliberately free of Jellyfin's own types: every value goes in as a plain string or bool, so the
/// decision can be driven directly by the suite. A check that needs a live server is a check that will
/// not run, and this one guards writes.
///
/// Everything here fails closed. An account with no resolved identity, no enabled folders, or an item
/// with no resolved ancestors is denied - the same shape as the walk ceiling's "not known to be fast is
/// not fast". The one exception is an account with all-folders permission, which is Jellyfin saying
/// "this one sees everything".
/// </remarks>
internal static class ItemAccess
{
    /// <summary>
    /// Claim types that may carry the caller's account id, most specific first. Jellyfin's own
    /// authentication puts the id in the nameidentifier claim; the JWT forms are here because the same
    /// principal reaches a plugin through more than one door.
    /// </summary>
    internal static readonly string[] UserIdClaimTypes =
    {
        ClaimTypes.NameIdentifier,
        "nameid",
        "sub",
        "Jellyfin-UserId",
    };

    /// <summary>
    /// Decides which jobs an account may see, and what has to be hidden from it (F4).
    /// </summary>
    /// <remarks>
    /// Every authenticated account used to see every job: `/SubSync/Jobs` and `/SubSync/Batches` are only protected by
    /// the class-level `[Authorize]`, and a job carries the item id and the output path it wrote. So one account read
    /// every other account's activity, and the server's absolute media paths with it. An administrator sees everything;
    /// anyone else sees the jobs their own requests created and nothing more. A job with no owner - the scheduled
    /// sweep's work, or a row restored from a history file written before jobs recorded one - is the administrator's,
    /// because an unknown owner must not mean everybody.
    /// </remarks>
    /// <param name="jobs">Every tracked job.</param>
    /// <param name="callerId">The calling account, when it could be resolved.</param>
    /// <param name="isAdmin">Whether that account is an administrator.</param>
    /// <returns>The jobs the caller may see, in the order they arrived.</returns>
    internal static IEnumerable<SyncJob> VisibleJobs(IEnumerable<SyncJob> jobs, Guid? callerId, bool isAdmin)
        => isAdmin
            ? jobs
            : jobs.Where(job => callerId is not null && job.OwnerId == callerId);

    /// <summary>
    /// Says whether an account may see one job (F4).
    /// </summary>
    /// <param name="job">The job, or null when no such job exists.</param>
    /// <param name="callerId">The calling account, when it could be resolved.</param>
    /// <param name="isAdmin">Whether that account is an administrator.</param>
    /// <returns>True when the caller may see it.</returns>
    internal static bool MaySeeJob(SyncJob? job, Guid? callerId, bool isAdmin)
        => job is not null && (isAdmin || (callerId is not null && job.OwnerId == callerId));

    /// <summary>
    /// Removes the server's own paths from a job an administrator is not the one reading (F4).
    /// </summary>
    /// <remarks>
    /// An account may see that its own run finished without being told where the server keeps its media: the folder
    /// layout is the operator's, not the viewer's. The output path goes, and the free-text fields - a failure names the
    /// file it could not open, a phase names the reference it read - have any absolute path inside them replaced.
    /// </remarks>
    /// <param name="job">The job about to be returned.</param>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    /// <returns>
    /// The job itself for an administrator, and a sanitised copy for anyone else: the tracked job is the server's own
    /// record, and answering one viewer must not edit it (F4).
    /// </returns>
    internal static SyncJob ForViewer(SyncJob job, bool isAdmin)
    {
        if (isAdmin)
        {
            return job;
        }

        var viewer = job.CopyForViewer();
        viewer.OutputPath = null;
        viewer.Error = RedactPaths(viewer.Error);
        viewer.Outcome = RedactPaths(viewer.Outcome);
        viewer.ExtractionNote = RedactPaths(viewer.ExtractionNote);
        return viewer;
    }

    /// <summary>
    /// Replaces absolute paths inside a message with a placeholder (F4).
    /// </summary>
    /// <remarks>
    /// Messages are written for whoever reads the log next, and they name files: "/media/Movies/X (2026).mkv" and the
    /// reference audio beside it. The words are kept so the message still explains itself; only the locations go, and
    /// the match runs to the end of the segment (a quote, a semicolon or the line's end) rather than stopping at the
    /// first space: a path with a space in it is ordinary, and half a path still gives away the folder it lives in.
    /// Over-redacting the tail of a sentence is the price, and it is the cheaper mistake.
    /// </remarks>
    /// <param name="text">The message, or null.</param>
    /// <returns>The message with paths replaced, or null.</returns>
    internal static string? RedactPaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var redacted = System.Text.RegularExpressions.Regex.Replace(text, @"[A-Za-z]:\\[^"";\r\n]*", Placeholder);
        return System.Text.RegularExpressions.Regex.Replace(redacted, @"(/[^"";\r\n]*)", Placeholder);
    }

    /// <summary>The placeholder a redacted path is replaced with (F4).</summary>
    internal const string Placeholder = "<path>";

    /// <summary>
    /// Removes the server's own paths from a batch task an administrator is not the one reading (F4).
    /// </summary>
    /// <param name="task">The batch task about to be returned.</param>
    /// <param name="isAdmin">Whether the caller is an administrator.</param>
    internal static void HideServerPaths(BatchTask task, bool isAdmin)
    {
        if (isAdmin || task is null)
        {
            return;
        }

        task.OutputPath = null;
    }

    /// <summary>Why a cancel request was refused (F2).</summary>
    internal enum KillRefusal
    {
        /// <summary>The request named nothing to stop.</summary>
        NothingSpecified,

        /// <summary>The caller may not stop that work.</summary>
        NotPermitted,

        /// <summary>Nothing queued or running matched, or it belongs to another account.</summary>
        NothingMatched,
    }

    /// <summary>
    /// Decides what a cancel request may stop (F2).
    /// </summary>
    /// <remarks>
    /// The cancel endpoint used to stop every run on the server with no target and no owner: an administrator
    /// pressing "Kill all syncing" stopped another administrator's run, and nothing in the request said so. A request
    /// now has to say what it wants - <c>all</c> (administrators only), a batch, or one run - and anything else is
    /// refused before a single token is cancelled. Work that belongs to another account is answered the way a request
    /// for a run that does not exist is, so a cancel cannot be used to probe for other people's runs.
    /// </remarks>
    /// <param name="jobs">Every tracked job.</param>
    /// <param name="callerId">The calling account, when it could be resolved.</param>
    /// <param name="isAdmin">Whether that account is an administrator.</param>
    /// <param name="jobId">One run to stop, when the request names one.</param>
    /// <param name="batchId">A batch to stop, when the request names one.</param>
    /// <param name="all">Whether the request asks for every run on the server.</param>
    /// <returns>The jobs to stop, or the reason the request was refused.</returns>
    internal static (IReadOnlyList<SyncJob> Targets, KillRefusal? Refusal) SelectKillTargets(
        IEnumerable<SyncJob> jobs,
        Guid? callerId,
        bool isAdmin,
        Guid? jobId,
        string? batchId,
        bool all)
    {
        var live = jobs.Where(job => job.Status is SyncJobStatus.Queued or SyncJobStatus.Running).ToList();
        if (all)
        {
            return isAdmin
                ? (live, null)
                : (Array.Empty<SyncJob>(), KillRefusal.NotPermitted);
        }

        if (jobId is not null)
        {
            var wanted = jobId.Value.ToString("N");
            var job = live.FirstOrDefault(candidate => string.Equals(candidate.Id, wanted, StringComparison.OrdinalIgnoreCase));
            return job is not null && MaySeeJob(job, callerId, isAdmin)
                ? (new[] { job }, null)
                : (Array.Empty<SyncJob>(), KillRefusal.NothingMatched);
        }

        if (!string.IsNullOrWhiteSpace(batchId))
        {
            var mine = live
                .Where(job => string.Equals(job.BatchId, batchId, StringComparison.OrdinalIgnoreCase)
                    && MaySeeJob(job, callerId, isAdmin))
                .ToList();
            return mine.Count > 0
                ? (mine, null)
                : (Array.Empty<SyncJob>(), KillRefusal.NothingMatched);
        }

        return (Array.Empty<SyncJob>(), KillRefusal.NothingSpecified);
    }

    /// <summary>
    /// Gets the account id from a request's claims, or null when there is none to trust.
    /// </summary>
    /// <param name="claims">Claim type and value pairs, in any order.</param>
    /// <returns>The account id, or null.</returns>
    internal static Guid? UserIdFrom(IEnumerable<KeyValuePair<string, string>> claims)
    {
        if (claims is null)
        {
            return null;
        }

        var byType = claims.ToLookup(c => c.Key, c => c.Value);
        foreach (var type in UserIdClaimTypes)
        {
            foreach (var value in byType[type])
            {
                if (Guid.TryParse(value, out var id) && id != Guid.Empty)
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an account may act on an item, from the account's folder permission and the folders the
    /// item lives in.
    /// </summary>
    /// <param name="allFolders">Whether the account has all-folders permission.</param>
    /// <param name="allowedFolderIds">Ids of the libraries the account may see.</param>
    /// <param name="itemAncestorIds">Ids of every folder the item sits under, itself excluded.</param>
    /// <returns>True when the account may act on the item.</returns>
    internal static bool Allows(
        bool allFolders,
        IReadOnlyCollection<string>? allowedFolderIds,
        IReadOnlyCollection<string>? itemAncestorIds)
    {
        if (allFolders)
        {
            return true;
        }

        if (allowedFolderIds is null || allowedFolderIds.Count == 0)
        {
            return false;
        }

        if (itemAncestorIds is null || itemAncestorIds.Count == 0)
        {
            // An item with no ancestors cannot be placed in anyone's library, so it is not allowed to
            // anyone. The alternative - allowing it - is the bug this guards against.
            return false;
        }

        foreach (var ancestor in itemAncestorIds)
        {
            foreach (var allowed in allowedFolderIds)
            {
                if (string.Equals(Normalise(ancestor), Normalise(allowed), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first candidate an account may not act on, for the requests that carry a whole list.
    /// </summary>
    /// <typeparam name="T">The candidate type, e.g. a task or an item id.</typeparam>
    /// <param name="allFolders">Whether the account has all-folders permission.</param>
    /// <param name="allowedFolderIds">Ids of the libraries the account may see.</param>
    /// <param name="candidates">What the request asked for.</param>
    /// <param name="ancestorsOf">How to get a candidate's folder ids.</param>
    /// <returns>The first denied candidate, or null when the whole list is allowed.</returns>
    internal static T? FirstDenied<T>(
        bool allFolders,
        IReadOnlyCollection<string>? allowedFolderIds,
        IEnumerable<T> candidates,
        Func<T, IReadOnlyCollection<string>> ancestorsOf)
        where T : class
    {
        if (allFolders)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (!Allows(false, allowedFolderIds, ancestorsOf(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalises an id for comparison: Jellyfin hands the same folder out as both the dashed and the
    /// undashed form depending on where it came from, and a mismatch here denies a user their own library.
    /// </summary>
    /// <param name="id">A folder id in either form.</param>
    /// <returns>The id without dashes.</returns>
    private static string Normalise(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal);
}
