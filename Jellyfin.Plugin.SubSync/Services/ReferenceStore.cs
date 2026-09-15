using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Holds the reference subtitle a sync is aligned against for as long as the run needs it, and
/// nowhere longer.
///
/// Why not a permanent cache: a reference extracted from a sibling subtitle track carries that
/// track's own error, and every other track of the file inherits it. A cached reference that is
/// wrong therefore poisons every future run of that file, and nothing in the file's state says so.
/// It is also the cheapest artefact in the pipeline to rebuild (tens of milliseconds from the
/// container index), so persisting it trades correctness for almost nothing.
///
/// The file still has to exist on disk while ffsubsync runs - it is passed as the reference
/// argument - so it lives under the plugin's cache directory, is shared by every job of that media
/// file within the run, and is deleted as soon as the last of those jobs has finished. A hard kill
/// cannot leave it behind either: <see cref="SweepLeftovers"/> clears the whole tree at startup.
/// </summary>
public static class ReferenceStore
{
    /// <summary>
    /// Largest number of per-file entries this run may hold (B12).
    /// </summary>
    /// <remarks>
    /// An entry is created when a file's reference is reserved and removed when that file's last job ends, so
    /// the count follows the run's width - except for the two ways a job can end without reaching that call: a
    /// task that never unwinds (killed, or stopped by the stall watchdog) and a job context evicted while its
    /// reference was still reserved. Each of those left an entry, and its directory on disk, until the next
    /// startup. The bound is a backstop; <see cref="SweepOrphans"/> is what removes them during a run.
    /// </remarks>
    public const int MaxEntries = 512;

    private sealed class Entry
    {
        public required string Directory { get; init; }

        /// <summary>The media file this reference was reserved for, so orphan-hood can be decided.</summary>
        public required string VideoPath { get; init; }

        /// <summary>When the entry was reserved; the oldest is dropped first by the backstop.</summary>
        public long ReservedTicks;

        public bool Ready { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>Gets the directory holding this run's reference subtitles.</summary>
    public static string Root =>
        Path.Combine(Plugin.Instance?.TempPath ?? Path.Combine(Path.GetTempPath(), "subsync"), "ref");

    /// <summary>
    /// Gets whether a reference for this media file is ready to be used, without touching the disk.
    /// The scheduler asks this for every queued job, so it must stay a dictionary lookup: it used to
    /// be a marker file checked while the queue lock was held.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <returns>True when a reference has been extracted for that file in this run.</returns>
    public static bool IsReady(string? videoPath) =>
        videoPath is not null
        && Entries.TryGetValue(VideoKey(videoPath), out var entry)
        && entry.Ready;

    /// <summary>
    /// Returns the path this run's reference for the given track will live at, creating the
    /// per-file directory. Call this before extracting; mark it ready once the file is written.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="streamSpec">Stream specifier of the reference track, e.g. <c>s:1</c>.</param>
    /// <param name="engineIdentity">Bundled/used ffsubsync version.</param>
    /// <returns>Path of the reference subtitle for this run.</returns>
    public static string Reserve(string videoPath, string streamSpec, string engineIdentity)
    {
        var key = VideoKey(videoPath);
        var directory = Path.Combine(Root, key);
        Directory.CreateDirectory(directory);

        var file = Path.Combine(
            directory,
            SpeechCache.KeyFor(videoPath, "reference-subtitle|" + streamSpec, engineIdentity) + ".ref.srt");

        var entry = new Entry { Directory = directory, VideoPath = videoPath };
        Interlocked.Exchange(ref entry.ReservedTicks, DateTime.UtcNow.Ticks);
        Entries.AddOrUpdate(
            key,
            _ => entry,
            (_, existing) => existing);

        // A backstop, not the normal path: SweepOrphans removes what a run leaves behind.
        while (Entries.Count > MaxEntries)
        {
            var oldestKey = Entries
                .OrderBy(pair => Interlocked.Read(ref pair.Value.ReservedTicks))
                .Select(pair => pair.Key)
                .FirstOrDefault();
            if (oldestKey is null || !Entries.TryRemove(oldestKey, out var dropped))
            {
                break;
            }

            DeleteDirectory(dropped.Directory);
        }

        return file;
    }

    /// <summary>Records that this run's reference file is complete and safe to hand to the engine.</summary>
    /// <param name="videoPath">Path of the media file.</param>
    public static void MarkReady(string videoPath)
    {
        if (Entries.TryGetValue(VideoKey(videoPath), out var entry))
        {
            entry.Ready = true;
        }
    }

    /// <summary>
    /// Ends one job's use of this file's reference. The files are removed only when nothing else is
    /// using them: not this job, and no other job of the same media file that is still queued *or
    /// running*.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="stillInUse">
    /// True when another job of this file — queued or already running — still needs this reference.
    /// A running job counts: it is handed the reference file as an argument, so removing it mid-run
    /// makes ffsubsync fail with "unable to read reference".
    /// </param>
    public static void EndJob(string? videoPath, bool stillInUse)
    {
        if (videoPath is null)
        {
            return;
        }

        var key = VideoKey(videoPath);

        lock (Gate)
        {
            if (!Entries.TryGetValue(key, out var entry))
            {
                return;
            }

            if (stillInUse)
            {
                return;
            }

            Entries.TryRemove(key, out _);
            DeleteDirectory(entry.Directory);
        }
    }

    /// <summary>
    /// Cue count of a subtitle file, for the log. A reference with only a handful of cues is a
    /// signs/forced track, and aligning full subtitles against one produces nonsense - which is
    /// impossible to see afterwards without this number.
    /// </summary>
    /// <param name="path">Path of the subtitle file.</param>
    /// <returns>The cue count, or -1 when the file cannot be read.</returns>
    public static int CueCount(string path)
    {
        try
        {
            return SrtWriter.CountCues(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return -1;
        }
    }

    /// <summary>
    /// Stops offering a reference for this media file: the track just proved unusable (a signs track
    /// holds too few cues to align anything), so the jobs that follow fall back to the audio instead of
    /// reusing it. The file itself is left alone - a sibling job may be reading it right now - and the
    /// run's cleanup removes the directory.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="streamSpec">Stream specifier of the reference that was rejected.</param>
    public static void Discard(string videoPath, string? streamSpec)
    {
        if (Entries.TryGetValue(VideoKey(videoPath), out var entry))
        {
            entry.Ready = false;
        }
    }

    /// <summary>Removes every reference of this run (plugin shutdown, or an operator asking for a clean slate).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
            DeleteDirectory(Root);
        }
    }

    /// <summary>
    /// Removes the entries - and the directories - of files no run is working on any more (B12).
    /// </summary>
    /// <remarks>
    /// A reference is removed when the last job of its file ends, which is the only thing that normally
    /// removes it. A job whose task never unwound, or whose context was evicted, ends without that call, and
    /// both the entry and the per-file directory then stayed until the next plugin start. Called from the same
    /// periodic pass as the stall watchdog, so a long run cleans up after itself instead of at shutdown.
    /// </remarks>
    /// <param name="isLive">Answers whether a media file still has a queued or running job.</param>
    /// <returns>How many entries were removed.</returns>
    public static int SweepOrphans(Func<string, bool> isLive)
    {
        lock (Gate)
        {
            var dropped = 0;
            foreach (var pair in Entries.ToList())
            {
                if (isLive(pair.Value.VideoPath) && Directory.Exists(pair.Value.Directory))
                {
                    continue;
                }

                if (Entries.TryRemove(pair.Key, out var entry))
                {
                    DeleteDirectory(entry.Directory);
                    dropped++;
                }
            }

            return dropped;
        }
    }

    /// <summary>
    /// Deletes anything left in the reference tree by an earlier run. Called at startup: a reference
    /// must never be able to outlive the run that made it, which is the whole point of not caching it.
    /// </summary>
    public static void SweepLeftovers()
    {
        lock (Gate)
        {
            Entries.Clear();
            DeleteDirectory(Root);
        }
    }

    /// <summary>Describes what this run is holding, for the interface and the log.</summary>
    /// <returns>Entry count and total size on disk, in a one-line form.</returns>
    public static string Describe()
    {
        var count = 0;
        long bytes = 0;

        foreach (var entry in Entries)
        {
            count++;
            bytes += DirectorySize(entry.Value.Directory);
        }

        return count == 0
            ? "none (references are rebuilt as each file runs)"
            : $"{count} file(s) in this run, {bytes} bytes";
    }

    private static string VideoKey(string videoPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(videoPath));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static long DirectorySize(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return 0;
            }

            long total = 0;
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                total += new FileInfo(file).Length;
            }

            return total;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort: a leftover is removed by the next startup sweep.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: the sweep is the backstop.
        }
    }
}
