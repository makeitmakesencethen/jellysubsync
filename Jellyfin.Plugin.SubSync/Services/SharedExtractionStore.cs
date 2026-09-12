using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The directory a file's extracted subtitles live in, shared by every job that works on that file.
/// </summary>
/// <remarks>
/// The extracted text used to be written into the job's own temporary directory. A job deletes that
/// directory when it finishes, so anything else still reading from it failed with
/// <c>DirectoryNotFoundException: Could not find a part of the path …/cache/subsync/&lt;jobId&gt;/subtitle_15.srt</c>
/// — measured on the slow profile: 4 of 50 tasks in one bulk run died that way, and the failing job's mode
/// had changed from <c>auto</c> to <c>ultimate</c> between queueing and the failure, i.e. it was working in
/// a directory an earlier step had already removed.
///
/// The directory is therefore not owned by any job: it is keyed by the video file, every job that touches
/// that file is a consumer of it, and it is removed only when the last consumer has finished. Nothing
/// deletes it inline, so a job that starts late (or re-enters) always finds the file it expects, and the
/// file is created on every acquire so it cannot be missing when a job writes it.
/// </remarks>
public static class SharedExtractionStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, HashSet<string>> Consumers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the root the shared directories live under.</summary>
    public static string Root => Path.Combine(Plugin.Instance?.TempPath ?? Path.GetTempPath(), "shared");

    /// <summary>
    /// Registers a job as a consumer of its file's extracted subtitles and returns the directory to use.
    /// </summary>
    /// <param name="videoPath">Path of the video file.</param>
    /// <param name="jobId">Id of the job that will read (and write) the extracted tracks.</param>
    /// <returns>The directory, which exists when this returns.</returns>
    public static string Acquire(string videoPath, string jobId)
    {
        var directory = DirectoryFor(videoPath);
        lock (Gate)
        {
            if (!Consumers.TryGetValue(videoPath, out var jobs))
            {
                jobs = new HashSet<string>(StringComparer.Ordinal);
                Consumers[videoPath] = jobs;
            }

            jobs.Add(jobId);
        }

        // Created on every acquire, not just the first: the whole point is that whoever is about to write
        // into it must find it there.
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Drops a job as a consumer and removes the directory once nothing is left that reads it.
    /// </summary>
    /// <param name="videoPath">Path of the video file.</param>
    /// <param name="jobId">Id of the job that is done with it.</param>
    /// <returns>True when the directory was removed.</returns>
    public static bool Release(string videoPath, string jobId)
    {
        var last = false;
        lock (Gate)
        {
            if (Consumers.TryGetValue(videoPath, out var jobs))
            {
                jobs.Remove(jobId);
                if (jobs.Count == 0)
                {
                    Consumers.Remove(videoPath);
                    last = true;
                }
            }
            else
            {
                // Not registered (a job that never reached Acquire, or a duplicate release): treating it as
                // the last consumer is the safe reading, the cleanup below re-checks against the running jobs.
                last = true;
            }
        }

        if (!last)
        {
            return false;
        }

        var gone = TryDelete(DirectoryFor(videoPath));
        TryRemoveEmptyRoot();
        return gone;
    }

    /// <summary>
    /// Removes shared directories that nobody is reading any more.
    /// </summary>
    /// <param name="isLiveJob">Answers whether a job id still exists (queued or running).</param>
    /// <returns>Number of directories removed.</returns>
    public static int Cleanup(Func<string, bool> isLiveJob)
    {
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var removed = 0;
        lock (Gate)
        {
            // A directory whose consumers all disappeared without a Release (a restart, a killed process)
            // is left behind on purpose until nothing that could read it is still alive.
            var orphaned = Consumers
                .Where(pair => pair.Value.All(id => !isLiveJob(id)))
                .Select(pair => pair.Key)
                .ToList();
            foreach (var videoPath in orphaned)
            {
                Consumers.Remove(videoPath);
                if (TryDelete(DirectoryFor(videoPath)))
                {
                    removed++;
                }
            }

            var known = new HashSet<string>(Consumers.Keys.Select(DirectoryFor), StringComparer.OrdinalIgnoreCase);

            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                if (known.Contains(directory))
                {
                    continue;
                }

                if (TryDelete(directory))
                {
                    removed++;
                }
            }
        }

        TryRemoveEmptyRoot();
        return removed;
    }

    /// <summary>Gets the shared directory for a file (whether or not it exists).</summary>
    /// <param name="videoPath">Path of the video file.</param>
    /// <returns>The directory path.</returns>
    public static string DirectoryFor(string videoPath) => Path.Combine(Root, KeyOf(videoPath));

    /// <summary>Gets the number of jobs currently registered for a file.</summary>
    /// <param name="videoPath">Path of the video file.</param>
    /// <returns>The consumer count.</returns>
    public static int ConsumerCount(string videoPath)
    {
        lock (Gate)
        {
            return Consumers.TryGetValue(videoPath, out var jobs) ? jobs.Count : 0;
        }
    }

    private static string KeyOf(string videoPath)
    {
        var bytes = Encoding.UTF8.GetBytes(videoPath);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Removes the root when nothing is in it, so "Clear cache" leaves no empty directories behind.
    /// </summary>
    /// <returns>True when the root is gone.</returns>
    private static bool TryRemoveEmptyRoot()
    {
        try
        {
            if (Directory.Exists(Root) && !Directory.EnumerateFileSystemEntries(Root).Any())
            {
                Directory.Delete(Root);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            // A reader is still in it; the next cleanup takes it.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
