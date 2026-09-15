using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SubSync.Configuration;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The two windows in which a running job is judged to be stuck.
/// </summary>
/// <param name="Idle">How long a job may show no activity at all while no child process is running for it.</param>
/// <param name="Silence">How long a child process may run without printing a single line.</param>
internal sealed record StuckJobWindows(TimeSpan Idle, TimeSpan Silence)
{
    /// <summary>Reads the windows from the stored settings, falling back to the defaults.</summary>
    /// <param name="config">The stored configuration, possibly null.</param>
    /// <returns>The windows in force.</returns>
    internal static StuckJobWindows From(PluginConfiguration? config) => config is null
        ? new StuckJobWindows(
            TimeSpan.FromMinutes(StuckJobPolicy.IdleMinutesDefault),
            TimeSpan.FromMinutes(StuckJobPolicy.SilenceMinutesDefault))
        : new StuckJobWindows(
            TimeSpan.FromMinutes(SettingsValidation.StuckJobTimeoutOf(config)),
            TimeSpan.FromMinutes(SettingsValidation.WedgedProcessTimeoutOf(config)));
}

/// <summary>
/// One running job, as the watchdog sees it: the job itself plus what was observed about it.
/// </summary>
/// <remarks>
/// Everything the decision turns on is carried in the observation rather than read from a static when the
/// decision is made, so the checks can describe a job - how long it has been quiet, whether a process of its
/// own is alive - instead of waiting for that state to occur.
/// </remarks>
/// <param name="Job">The job.</param>
/// <param name="VideoPath">The media file it works on, so jobs of one file can be compared.</param>
/// <param name="LastActivityUtc">When the job last showed activity (its own phase or progress change).</param>
/// <param name="ProcessAlive">Whether a child process of this job is running.</param>
/// <param name="ProcessSilentSinceUtc">When that process last printed something, or started when it never did.</param>
internal sealed record JobObservation(
    SyncJob Job,
    string? VideoPath,
    DateTime LastActivityUtc,
    bool ProcessAlive,
    DateTime? ProcessSilentSinceUtc)
{
    /// <summary>Gets the status of the job.</summary>
    internal SyncJobStatus Status => Job.Status;
}

/// <summary>
/// Decides whether a job that is still <c>Running</c> has stopped making progress, and gives it a terminal
/// state so its worker slot and its run can move on (B6).
/// </summary>
/// <remarks>
/// A job stuck in <c>Running</c> is stuck for the whole batch: the run reports itself as unfinished, the slot
/// it claimed is never handed to the next task, and the only way out was a restart. Two shapes of it, and they
/// need different evidence:
/// <list type="bullet">
/// <item><description>nothing is running for the job any more - a wait that was never released, a reader on a
/// share that stopped answering, a task that returned without settling its status. Nothing will ever signal
/// progress again, so the window that matters is short.</description></item>
/// <item><description>a child process is alive but has gone silent. That is not automatically a stall: a
/// demux of a 2,4 GB episode over SMB, or a three-hour film's speech analysis, is legitimate work with quiet
/// stretches - measured 5 minutes of engine silence in a 6,7-minute run on the user's own share, and a
/// reported 91-minute audio analysis. So a live process is judged by its own output over a much longer
/// window, and never by the job's own silence.</description></item>
/// </list>
/// A job waiting for another subtitle of the same file - the file's audio is analysed once and the others
/// wait on its gate, which can legitimately take an hour - is not stuck either, which is what the sibling
/// exemption is for. Every decision here is a pure function of what was observed, so the checks can put a
/// clock in front of it instead of waiting.
/// </remarks>
internal static class StuckJobPolicy
{
    /// <summary>
    /// Default idle window, in minutes. Kept equal to <see cref="PluginConfiguration.StuckJobTimeoutMinutes"/>'s
    /// own default, which a check pins: a job must not be judged by a window different from the one the user's
    /// settings file shows.
    /// </summary>
    internal const int IdleMinutesDefault = 15;

    /// <summary>Default window in which a live child process may stay silent, in minutes.</summary>
    internal const int SilenceMinutesDefault = 60;

    /// <summary>
    /// Judges one running job against the two windows.
    /// </summary>
    /// <param name="now">The current time, so a check can put a clock in front of this.</param>
    /// <param name="job">The job and what was observed about it.</param>
    /// <param name="siblingActive">Whether another job of the same media file is itself showing activity.</param>
    /// <param name="windows">The windows in force.</param>
    /// <returns>Why the job is stuck, or null when it is not.</returns>
    internal static string? WhyStuck(
        DateTime now,
        JobObservation job,
        bool siblingActive,
        StuckJobWindows windows)
    {
        if (job.Status != SyncJobStatus.Running)
        {
            return null;
        }

        if (job.ProcessAlive)
        {
            if (job.ProcessSilentSinceUtc is not { } silentSince)
            {
                return null;
            }

            var silentFor = now - silentSince;
            if (silentFor < windows.Silence)
            {
                return null;
            }

            return $"its process has been running for {Describe(silentFor)} without printing a single line "
                + $"({job.Job.Phase}) - there is no progress to wait for";
        }

        if (siblingActive)
        {
            // Waiting for the file's audio analysis or its reference subtitle, which another job is doing.
            return null;
        }

        var idleFor = now - job.LastActivityUtc;
        if (idleFor < windows.Idle)
        {
            return null;
        }

        return $"nothing has been running for it and it has shown no activity for {Describe(idleFor)} "
            + $"({job.Job.Phase})";
    }

    /// <summary>
    /// Asks whether another job of the same media file is itself working, so a job waiting on it is not
    /// mistaken for a stalled one.
    /// </summary>
    /// <remarks>
    /// The audio analysis of a file is done once and the file's other subtitles wait on its gate, which is a
    /// legitimate wait of up to an hour; its own activity is what says the wait is worth having. A live child
    /// process counts too, because that is precisely the stretch - a demux over a share - in which the working
    /// job has nothing new to report.
    /// </remarks>
    /// <param name="sibling">The other job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="windows">The windows in force.</param>
    /// <returns>True when that job is working on the file right now.</returns>
    internal static bool IsWorking(JobObservation sibling, DateTime now, StuckJobWindows windows)
        => sibling.Status == SyncJobStatus.Running
           && ((now - sibling.LastActivityUtc) < windows.Idle || sibling.ProcessAlive);

    /// <summary>
    /// Picks the running jobs that are stuck, with the reason for each.
    /// </summary>
    /// <remarks>
    /// The whole selection is here, away from the service, so the checks can hand it a clock, a set of observed
    /// jobs and assert exactly which of them a sweep would stop.
    /// </remarks>
    /// <param name="jobs">Every job the plugin is tracking, as observed.</param>
    /// <param name="now">The current time.</param>
    /// <param name="windows">The windows in force.</param>
    /// <returns>The jobs to stop, in the order they were found.</returns>
    internal static IReadOnlyList<(SyncJob Job, string Reason)> Stuck(
        IEnumerable<JobObservation> jobs,
        DateTime now,
        StuckJobWindows windows)
    {
        var all = jobs.ToList();
        var running = all.Where(job => job.Status == SyncJobStatus.Running).ToList();
        var stuck = new List<(SyncJob, string)>();

        foreach (var job in running)
        {
            var siblingActive = job.VideoPath is not null
                && running.Any(other => !ReferenceEquals(other.Job, job.Job)
                    && string.Equals(other.VideoPath, job.VideoPath, StringComparison.Ordinal)
                    && IsWorking(other, now, windows));

            var reason = WhyStuck(now, job, siblingActive, windows);
            if (reason is not null)
            {
                stuck.Add((job.Job, reason));
            }
        }

        return stuck;
    }

    /// <summary>
    /// Builds the error text a stopped job carries, so the wording is stated once.
    /// </summary>
    /// <param name="reason">Why it was stopped.</param>
    /// <returns>The text the job's error field and the interface show.</returns>
    internal static string StoppedErrorText(string reason)
        => $"Stopped because {reason}. Nothing was written for this subtitle; the rest of the run continues.";

    /// <summary>
    /// Applies a stop to a running job: finds it, fails it with the reason, and dates it.
    /// </summary>
    /// <remarks>
    /// The service calls this instead of writing the four assignments itself, so what the checks exercise is
    /// what a run does - including the phase the interface shows for a job that was stopped rather than
    /// cancelled by the user.
    /// </remarks>
    /// <param name="job">The job to stop.</param>
    /// <param name="reason">Why it is being stopped.</param>
    /// <param name="now">The current time.</param>
    /// <returns>True when the job was stopped, false when it was no longer running.</returns>
    internal static bool Stop(SyncJob job, string reason, DateTime now)
    {
        if (job.Status != SyncJobStatus.Running)
        {
            return false;
        }

        job.Status = SyncJobStatus.Failed;
        job.Phase = "Stopped (no progress)";
        job.Error = StoppedErrorText(reason);
        job.FinishedAtUtc ??= now;
        return true;
    }

    /// <summary>
    /// Gives a job a terminal state when it is still running, so nothing can be left looking unfinished.
    /// </summary>
    /// <remarks>
    /// This is the second half of B6 and the one that does not depend on any timing: every path out of a job
    /// - including the one that used to throw before the job's own error handling was reached, leaving a job
    /// <c>Running</c> with a faulted task behind it - passes through here.
    /// </remarks>
    /// <param name="job">The job to settle.</param>
    /// <param name="now">The current time.</param>
    /// <param name="reason">Why it is being settled, for its error message.</param>
    /// <returns>True when the job had to be settled, false when it was already in a terminal state.</returns>
    internal static bool Settle(SyncJob job, DateTime now, string reason)
    {
        if (job.Status is not (SyncJobStatus.Queued or SyncJobStatus.Running))
        {
            return false;
        }

        var wasRunning = job.Status == SyncJobStatus.Running;
        job.Status = SyncJobStatus.Failed;
        job.Phase = wasRunning ? "Stopped" : "Failed";
        job.Error = reason;
        job.FinishedAtUtc ??= now;
        return true;
    }

    /// <summary>
    /// Formats a duration the way the user's log reads it.
    /// </summary>
    /// <param name="span">The duration.</param>
    /// <returns>Minutes, or seconds below a minute.</returns>
    internal static string Describe(TimeSpan span)
        => span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0.#} min"
            : $"{span.TotalSeconds:0.#} s";
}
