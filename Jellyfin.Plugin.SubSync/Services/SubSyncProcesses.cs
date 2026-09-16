using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Every child process this plugin runs, and the table of the ones still alive.
/// </summary>
/// <remarks>
/// Split out of <c>SubSyncService</c> (the C4 cluster of <c>knowledge/SUBSYNCSERVICE_MAP.md</c>, and B18's
/// subject: four runners, two argument styles). The runners share one body shape - start with redirected
/// streams, register the process so Kill and the teardown can reach it, kill the tree when the token is
/// cancelled, unregister in a finally - and they differ in what they do with the child's output:
/// <list type="bullet">
/// <item><description>the two <c>ArgumentList</c> runners pass each argument through untouched;</description></item>
/// <item><description>the two string runners let the runtime parse a whole argument string, which is why a
/// caller quotes a path with a space through <c>EscapeArg</c>;</description></item>
/// <item><description>the stderr-callback runner also feeds <c>JobProcessRegistry</c>, which is how the stall
/// watchdog tells a slow read from a wedged process (B6).</description></item>
/// </list>
/// The table is what makes a Kill kill: a cancelled token only helps a process that polls it, so the processes
/// are tracked here and killed as trees by both Kill (B16) and the teardown (B14).
/// </remarks>
internal sealed class SubSyncProcesses
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, Process> _liveProcesses = new();

    /// <summary>Initializes a new instance of the <see cref="SubSyncProcesses"/> class.</summary>
    /// <param name="logger">Logger the runners report through.</param>
    internal SubSyncProcesses(ILogger logger) => _logger = logger;

    /// <summary>Gets how many child processes are tracked right now, alive or not yet reaped.</summary>
    internal int TrackedCount => _liveProcesses.Count;

    /// <summary>Gets how many tracked processes are still running.</summary>
    /// <returns>The number that have not exited.</returns>
    internal int LiveCount() => _liveProcesses.Values.Count(process => !SafeHasExited(process));

    /// <summary>
    /// Kills every child process this service started and reports how many really exited.
    /// </summary>
    /// <remarks>
    /// Cancelling a token only stops what polls it; ffsubsync spawns ffmpeg, and both hold the media file, so the
    /// trees are killed directly. Used by <c>SubSyncService.KillAll</c> and by <c>Dispose</c> (B14).
    /// </remarks>
    /// <returns>How many trees were asked to stop, and how many had exited by the deadline.</returns>
    internal (int Asked, int Stopped) KillChildProcesses()
    {
        var asked = 0;
        var toKill = new List<Process>();
        foreach (var process in _liveProcesses.Values.ToList())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    asked++;
                    toKill.Add(process);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not kill process {Pid}", process.Id);
            }
        }

        return (asked, CountExited(toKill, KillWaitMs));
    }

    /// <summary>
    /// Tracks a child process so a kill or a teardown can find it (B14).
    /// </summary>
    /// <param name="process">The process just started.</param>
    internal void TrackChildProcess(Process process)
    {
        _liveProcesses[process.Id] = process;
    }

    /// <summary>How long a killed process is given to actually exit before it is reported as a survivor.</summary>
    private const int KillWaitMs = 3000;

    /// <summary>
    /// Waits for processes to exit and reports how many really did (B16).
    /// </summary>
    /// <remarks>
    /// `WaitForExit` on each handle, not a sleep-and-sample loop: the caller wants a count that was measured,
    /// and a process that ignored SIGKILL for longer than the deadline has to be reported as still alive
    /// rather than folded into a success figure.
    /// </remarks>
    /// <param name="processes">Processes that were asked to stop.</param>
    /// <param name="waitMs">How long each one may take.</param>
    /// <returns>How many of them exited.</returns>
    internal static int CountExited(IEnumerable<Process> processes, int waitMs)
    {
        var exited = 0;
        foreach (var process in processes)
        {
            try
            {
                if (process.WaitForExit(waitMs) && process.HasExited)
                {
                    exited++;
                }
            }
            catch (Exception)
            {
                // A process that cannot be waited on is not counted as stopped.
            }
        }

        return exited;
    }

    /// <summary>True when a process has exited, without throwing when it is gone.</summary>
    /// <param name="process">Process to check.</param>
    /// <returns>True when it is no longer running.</returns>
    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal async Task<(int ExitCode, string Stderr)> RunProcessArgumentListAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDir, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        return (process.ExitCode, stderr);
    }

    internal async Task<int> RunProcessAsync(string executable, string arguments, string? workingDir, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Process {Exe} exited with code {Code}. stderr: {Stderr}", executable, process.ExitCode, stderr);
        }
        else
        {
            _logger.LogDebug("Process {Exe} completed. stdout: {Stdout}", executable, stdout);
        }

        return process.ExitCode;
    }

    internal async Task<int> RunProcessWithStderrCallbackAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDir,
        Action<string>? onStderrLine, CancellationToken cancellationToken, EngineWatch? watch = null,
        string? jobId = null)
    {
        var owner = jobId ?? watch?.JobId;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);
        if (owner is not null)
        {
            JobProcessRegistry.Begin(owner);
        }

        // S27: while this process runs, say so in the plugin's own log. Started once the process is
        // live and disposed when it exits, so no line can ever describe a process that is already gone.
        using var heartbeat = watch is null ? null : new EngineHeartbeat(watch);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        // Read stdout in background
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        // Read stderr line-by-line in real-time
        var stderrTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    // End of the stream is the loop's own exit; EndOfStream is sync-over-async on
                    // .NET 10 (CA2024) and blocks a thread of the pool while we await a line.
                    break;
                }

                if (owner is not null)
                {
                    // A line from the process is the process saying it is working: this is what dates its
                    // silence for the wedged-process rule (B6).
                    JobProcessRegistry.SawOutput(owner);
                }

                onStderrLine?.Invoke(line);
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
            if (owner is not null)
            {
                JobProcessRegistry.End(owner);
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a process and returns (exitCode, combined stdout+stderr output).
    /// </summary>
    internal async Task<(int ExitCode, string Output)> RunProcessCaptureAsync(string executable, string arguments, string? workingDir)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        process.Start();
        TrackChildProcess(process);

        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = string.Concat(stdout, stderr).Trim();
            return (process.ExitCode, output);
        }
        finally
        {
            // Registered like the other four runners (B14). This one was the exception, and it covers the provisioning
            // and probe steps - `ffsubsync --version`, `python3 -m venv`, `pip install`, `apt-get install` - any of
            // which can run for minutes, so a kill or a teardown had no handle on them.
            _liveProcesses.TryRemove(process.Id, out _);
        }
    }
}
