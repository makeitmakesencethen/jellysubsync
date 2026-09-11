using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Keeps the subtitle text taken out of a media file, so it is taken out once and never again.
///
/// Reading a subtitle track out of an MKV means walking the file: the blocks of any track are
/// interleaved with the video's, and no index lists them (Jellyfin's own extraction reads the whole
/// file, which is why it can hold a NAS link at line rate for minutes). That cost is worth paying
/// once per file and never again - which is the pattern the rest of the ecosystem uses (the
/// Subtitle Extract plugin, pre-extracting sidecar SRTs) and the reason "extraction takes 20-30
/// seconds" threads end in "extract them in advance and cache them".
///
/// What is stored is the subtitle as it came out of the container, before syncing: the sync depends
/// on settings and the engine and must run again, but re-reading the file does not. Entries are
/// keyed by the file's path, size and modification time, so a re-encoded or replaced file cannot
/// serve a stale subtitle.
/// </summary>
public static class SubtitleCache
{
    /// <summary>How long an extracted subtitle is kept without being used.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(120);

    /// <summary>Largest total size the cache may reach before the oldest entries are dropped.</summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, string> Memory = new(StringComparer.Ordinal);

    /// <summary>Gets the directory holding the cached subtitles.</summary>
    public static string Root
    {
        get
        {
            var basePath = Plugin.Instance?.StatePath ?? Path.GetTempPath();
            return Path.Combine(basePath, "subtitle-cache");
        }
    }

    /// <summary>
    /// Builds the key for one track of a media file. The file's size and modification time are part
    /// of it: a re-encode, a remux or a replaced file must not be served the old subtitle.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="track">Subtitle track ordinal, or another identifier for the track.</param>
    /// <returns>A stable file-name-safe key.</returns>
    public static string KeyFor(string videoPath, string track)
    {
        string stamp;
        try
        {
            var info = new FileInfo(videoPath);
            stamp = info.Exists
                ? string.Create(CultureInfo.InvariantCulture, $"{info.Length}:{info.LastWriteTimeUtc.Ticks}")
                : "missing";
        }
        catch (IOException)
        {
            stamp = "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            stamp = "unreadable";
        }

        var material = string.Join('\n', Path.GetFullPath(videoPath), stamp, track);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Takes the cached subtitle for a track, if it is there.</summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="track">Subtitle track ordinal.</param>
    /// <param name="text">The subtitle text.</param>
    /// <returns>True when a usable entry was found.</returns>
    public static bool TryGet(string videoPath, string track, out string text)
    {
        var key = KeyFor(videoPath, track);
        if (Memory.TryGetValue(key, out text!))
        {
            return text.Length > 0;
        }

        var path = Path.Combine(Root, key + ".srt");
        try
        {
            if (!File.Exists(path))
            {
                text = string.Empty;
                return false;
            }

            text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Length == 0)
            {
                return false;
            }

            // Touch it so Prune keeps what is actually being used.
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            Memory[key] = text;
            return true;
        }
        catch (IOException)
        {
            text = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>Stores an extracted subtitle.</summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="track">Subtitle track ordinal.</param>
    /// <param name="text">The subtitle text.</param>
    public static void Store(string videoPath, string track, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var key = KeyFor(videoPath, track);
        Memory[key] = text;

        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, key + ".srt");
            if (!File.Exists(path) || !string.Equals(File.ReadAllText(path, Encoding.UTF8), text, StringComparison.Ordinal))
            {
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
        }
        catch (IOException)
        {
            // The cache is an optimisation: failing to write it must never fail a job.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>How many tracks are cached, in memory or on disk.</summary>
    /// <returns>Entry count.</returns>
    public static int Count()
    {
        try
        {
            return Directory.Exists(Root) ? Directory.GetFiles(Root, "*.srt").Length : 0;
        }
        catch (IOException)
        {
            return Memory.Count;
        }
    }

    /// <summary>Drops entries that have aged out, then the oldest until the cache fits.</summary>
    /// <returns>Number of entries removed.</returns>
    public static int Prune()
    {
        var removed = 0;
        try
        {
            lock (Gate)
            {
                if (!Directory.Exists(Root))
                {
                    return 0;
                }

                var files = new DirectoryInfo(Root).GetFiles("*.srt").ToList();
                var cutoff = DateTime.UtcNow - MaxAge;
                foreach (var file in files.ToList())
                {
                    var used = file.LastAccessTimeUtc > file.LastWriteTimeUtc ? file.LastAccessTimeUtc : file.LastWriteTimeUtc;
                    if (used < cutoff)
                    {
                        try
                        {
                            file.Delete();
                            removed++;
                        }
                        catch (IOException)
                        {
                            // Another job may be reading it; it goes on the next pass.
                        }

                        files.Remove(file);
                    }
                }

                var total = files.Sum(f => f.Length);
                foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (total <= MaxBytes)
                    {
                        break;
                    }

                    try
                    {
                        total -= file.Length;
                        file.Delete();
                        removed++;
                    }
                    catch (IOException)
                    {
                        // Leave it for next time.
                    }
                }
            }
        }
        catch (IOException)
        {
            // Nothing to do; the cache stays as it is.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return removed;
    }

    /// <summary>Empties the cache.</summary>
    /// <returns>Number of entries removed.</returns>
    public static int Clear()
    {
        var removed = 0;
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.GetFiles(Root, "*.srt"))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        // A job is reading it; it will go on the next clear.
                    }
                }
            }
        }
        catch (IOException)
        {
            // Report what was removed.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        Memory.Clear();
        return removed;
    }

    /// <summary>One line for the settings tab.</summary>
    /// <returns>Summary text.</returns>
    public static string Describe()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return "empty";
            }

            var files = new DirectoryInfo(Root).GetFiles("*.srt");
            var mb = files.Sum(f => f.Length) / 1e6;
            return string.Create(CultureInfo.InvariantCulture, $"{files.Length} subtitles, {mb:0.0} MB");
        }
        catch (IOException)
        {
            return "unavailable";
        }
    }
}
