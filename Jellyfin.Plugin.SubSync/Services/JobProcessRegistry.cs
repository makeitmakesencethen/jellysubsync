using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Which job owns a running child process, and when that process last said anything (B6).
/// </summary>
/// <remarks>
/// The plugin runs three kinds of child process for a job - the engine, the extraction fallback and the
/// volume probe - and a job blocked on one of them is not stuck, it is waiting for work that is happening
/// outside the plugin's own threads. Whether a child exists is the one fact that separates "waiting for a
/// slow read on the share" from "wedged", so it is recorded here rather than inferred from timing alone.
/// <para>
/// Output timestamps are the second half: a child that is alive but has printed nothing for an hour is a
/// child that is not doing anything. Measured on the bundled engine against a 50-minute episode, its stderr
/// carried a line every ~0,5 s while the speech was extracted (47 lines in a 17,7 s run, largest gap 1,4 s),
/// so silence is a usable signal and it is a *separate*, much longer window than the job's own.
/// </para>
/// </remarks>
internal static class JobProcessRegistry
{
    private sealed class Entry
    {
        internal int Alive;

        /// <summary>When the first process of this job started; ticks, so a check can read it without a lock.</summary>
        internal long StartedTicks;

        /// <summary>When the most recent line arrived from any process of this job; ticks.</summary>
        internal long LastOutputTicks;
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>Gets how many processes are alive across every job, for the checks and for diagnostics.</summary>
    internal static int LiveProcesses => Entries.Values.Sum(entry => Volatile.Read(ref entry.Alive));

    /// <summary>
    /// Records that a process for this job has started.
    /// </summary>
    /// <param name="jobId">The job the process belongs to.</param>
    internal static void Begin(string jobId)
    {
        if (string.IsNullOrEmpty(jobId))
        {
            return;
        }

        var entry = Entries.GetOrAdd(jobId, _ => new Entry());
        Interlocked.Increment(ref entry.Alive);
        if (Volatile.Read(ref entry.StartedTicks) == 0)
        {
            Volatile.Write(ref entry.StartedTicks, DateTime.UtcNow.Ticks);
        }
    }

    /// <summary>
    /// Records that a process for this job has ended.
    /// </summary>
    /// <param name="jobId">The job the process belonged to.</param>
    internal static void End(string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !Entries.TryGetValue(jobId, out var entry))
        {
            return;
        }

        Interlocked.Decrement(ref entry.Alive);
    }

    /// <summary>
    /// Records that a process for this job produced a line.
    /// </summary>
    /// <param name="jobId">The job the process belongs to.</param>
    internal static void SawOutput(string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !Entries.TryGetValue(jobId, out var entry))
        {
            return;
        }

        Volatile.Write(ref entry.LastOutputTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Asks whether any process for this job is still running.
    /// </summary>
    /// <param name="jobId">The job to ask about.</param>
    /// <returns>True while a child of that job is alive.</returns>
    internal static bool IsAlive(string jobId)
        => !string.IsNullOrEmpty(jobId)
           && Entries.TryGetValue(jobId, out var entry)
           && Volatile.Read(ref entry.Alive) > 0;

    /// <summary>
    /// The moment this job's silence began: its last output line, or the start of its first process when it
    /// has never printed one.
    /// </summary>
    /// <param name="jobId">The job to ask about.</param>
    /// <returns>The moment, or null when this job has no process on record.</returns>
    internal static DateTime? SilentSinceUtc(string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !Entries.TryGetValue(jobId, out var entry))
        {
            return null;
        }

        var output = Volatile.Read(ref entry.LastOutputTicks);
        if (output > 0)
        {
            return new DateTime(output, DateTimeKind.Utc);
        }

        var started = Volatile.Read(ref entry.StartedTicks);
        return started > 0 ? new DateTime(started, DateTimeKind.Utc) : null;
    }

    /// <summary>
    /// Forgets every tracked job (B14).
    /// </summary>
    /// <remarks>
    /// The registry is static because the processes it tracks are process-wide, but the jobs belong to a service
    /// instance. A plugin that is torn down and loaded again in the same process would otherwise start with entries
    /// for jobs that ended with the previous one, and report process activity for work nobody is doing.
    /// </remarks>
    internal static void Clear() => Entries.Clear();

    /// <summary>
    /// Forgets one job.
    /// </summary>
    /// <param name="jobId">Job identifier.</param>
    internal static void Forget(string jobId)
    {
        if (!string.IsNullOrEmpty(jobId))
        {
            Entries.TryRemove(jobId, out _);
        }
    }
}
