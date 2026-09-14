using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What identifies a running process for the heartbeat line.
/// </summary>
/// <param name="JobId">The job the process belongs to.</param>
/// <param name="FileLabel">A short label for the media file (its file name).</param>
/// <param name="Reference">The ruler the engine was given, as the start line reports it.</param>
public sealed record EngineWatch(string JobId, string FileLabel, string Reference);

/// <summary>
/// Writes a periodic "engine running" line to the plugin's own log while a process runs.
///
/// Visibility only (S27): this changes nothing about what the engine does, how it is invoked, or how
/// long it is allowed to run - the plugin deliberately has no deadline on it, because a feature film
/// with an audio reference legitimately takes an hour. Without this line the plugin log shows a start
/// and an exit and nothing in between, which makes a slow run and a wedged one read identically.
/// </summary>
public sealed class EngineHeartbeat : IDisposable
{
    /// <summary>How often the plugin writes a heartbeat while a process runs.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _stop = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a heartbeat and starts logging, once per interval, until it is disposed.
    /// </summary>
    /// <param name="watch">What to name in the line.</param>
    /// <param name="interval">How often to write; the plugin passes its own default.</param>
    public EngineHeartbeat(EngineWatch watch, TimeSpan? interval = null)
    {
        var period = interval ?? DefaultInterval;

        // The clock starts here, so the first line reads as the time this process has been running.
        var clock = Stopwatch.StartNew();

        // Deliberately not stored: the loop owns nothing that has to be awaited, and the token source
        // below is what ends it. A heartbeat must never be able to outlive the process it describes.
        _ = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(period);
                while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        PluginLog.Info(Describe(watch.JobId, watch.FileLabel, clock.Elapsed, watch.Reference));
                    }
                    catch
                    {
                        // A log write must never affect a sync.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The process ended; the loop ends with it.
            }
        });
    }

    /// <summary>
    /// Formats one heartbeat line. Pure, so a check can pin the wording.
    /// </summary>
    /// <param name="jobId">The job the process belongs to.</param>
    /// <param name="fileLabel">A short label for the media file.</param>
    /// <param name="elapsed">How long the process has been running.</param>
    /// <param name="reference">The ruler the engine was given.</param>
    /// <returns>The log line.</returns>
    public static string Describe(string jobId, string fileLabel, TimeSpan elapsed, string reference)
        => $"[{jobId}] {fileLabel}: engine running, {elapsed.TotalMinutes:0.#} min elapsed, reference={reference}";

    /// <summary>Stops the heartbeat.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stop.Cancel();
        }
        catch
        {
            // Already gone.
        }

        _stop.Dispose();
    }
}
