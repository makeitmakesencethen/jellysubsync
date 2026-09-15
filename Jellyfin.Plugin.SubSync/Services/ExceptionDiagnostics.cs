using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Turns the exception a caller actually gets into the fault that actually happened.
/// </summary>
/// <remarks>
/// Two places in the plugin run work in parallel and then report what went wrong: the reader's prefetch, where a
/// worker's failure arrives as an <see cref="AggregateException"/>, and the sync pump, which stands behind every
/// queued job (B21, B31). An aggregate's own message is "One or more errors occurred.", which says nothing, and a
/// cancellation wrapped in one is worse than useless: it stops being recognisable as a kill and starts being
/// treated as a failure, which sends a job down the fallback path it should never take.
/// </remarks>
internal static class ExceptionDiagnostics
{
    /// <summary>
    /// Returns the single exception a caller should act on: the cancellation or fault inside an
    /// <see cref="AggregateException"/>, or the exception itself.
    /// </summary>
    /// <param name="exception">The exception as thrown.</param>
    /// <returns>The exception to log or rethrow.</returns>
    internal static Exception RootCause(Exception exception)
    {
        if (exception is not AggregateException aggregate)
        {
            return exception;
        }

        var faults = aggregate.Flatten().InnerExceptions;
        var cancelled = faults.OfType<OperationCanceledException>().FirstOrDefault();
        if (cancelled is not null)
        {
            // A kill stays a kill, however deeply it was wrapped.
            return cancelled;
        }

        if (faults.Count == 1)
        {
            return faults[0];
        }

        if (faults.Count == 0)
        {
            return exception;
        }

        return new IOException(
            $"{faults.Count} failures: "
            + string.Join("; ", faults.Take(3).Select(fault => fault.GetType().Name + ": " + fault.Message))
            + (faults.Count > 3 ? "; …" : string.Empty),
            aggregate);
    }

    /// <summary>
    /// Describes an exception as one line for the plugin's own log.
    /// </summary>
    /// <param name="exception">The exception as thrown.</param>
    /// <returns>Type and message of the fault that actually happened.</returns>
    internal static string Describe(Exception exception)
    {
        var real = RootCause(exception);
        var wrapped = real == exception ? string.Empty : $" (wrapped in {exception.GetType().Name})";
        return $"{real.GetType().Name}: {real.Message}{wrapped}";
    }
}
