using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Cache of ffsubsync's speech analysis ("fast" mode).
///
/// ffsubsync derives a speech signal from the media's audio and aligns each subtitle
/// against it. That signal depends only on the file, the VAD method and the ffsubsync
/// version — never on which subtitle is being synced — so it can be produced once
/// (<c>--serialize-speech</c>) and reused for every other subtitle of that file by
/// passing the resulting <c>.npz</c> as the reference.
///
/// ffsubsync writes the <c>.npz</c> next to whatever reference path it was given, so we
/// hand it a symlink inside our own cache directory. That keeps media folders untouched
/// (nothing is ever written next to the video) and works even if a media mount is
/// read-only.
/// </summary>
public static class SpeechCache
{
    /// <summary>Entries older than this are pruned when a new entry is written.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    /// <summary>Total cache size cap; oldest entries are dropped beyond this.</summary>
    public const long MaxBytes = 250L * 1024 * 1024;

    /// <summary>Gets the directory holding cached speech signals.</summary>
    public static string Root
    {
        get
        {
            var basePath = Plugin.Instance?.StatePath ?? Path.GetTempPath();
            return Path.Combine(basePath, "speech-cache");
        }
    }

    /// <summary>
    /// Builds the cache key for a media file. File size and modification time are part
    /// of the key, so a replaced or re-encoded file never reuses stale speech; the VAD
    /// method and ffsubsync version are included because both change the analysis.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="vadMethod">Configured VAD method.</param>
    /// <param name="engineVersion">Bundled/used ffsubsync version or path.</param>
    /// <returns>A stable, filesystem-safe cache key.</returns>
    public static string KeyFor(string videoPath, string vadMethod, string engineVersion)
    {
        string stamp;
        try
        {
            var info = new FileInfo(videoPath);
            stamp = info.Exists
                ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}"
                : "missing";
        }
        catch (Exception)
        {
            stamp = "unreadable";
        }

        var raw = $"{videoPath}|{stamp}|{vadMethod}|{engineVersion}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }

    /// <summary>Gets the cached speech file path for a key, or null when absent.</summary>
    /// <param name="key">Cache key from <see cref="KeyFor"/>.</param>
    /// <returns>Path of the cached <c>.npz</c>, or null.</returns>
    public static string? TryGet(string key)
    {
        var path = Path.Combine(Root, key + ".npz");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Creates (or refreshes) the symlink that makes ffsubsync write its speech cache
    /// into <see cref="Root"/> instead of the media folder.
    /// </summary>
    /// <param name="videoPath">Path of the media file.</param>
    /// <param name="key">Cache key from <see cref="KeyFor"/>.</param>
    /// <returns>The symlink path to pass to ffsubsync as the reference.</returns>
    public static string CreateReferenceLink(string videoPath, string key)
    {
        Directory.CreateDirectory(Root);
        var extension = Path.GetExtension(videoPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ".mkv";
        }

        var linkPath = Path.Combine(Root, key + extension);
        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            File.CreateSymbolicLink(linkPath, videoPath);
            return linkPath;
        }
        catch (Exception)
        {
            // No symlink support (or a filesystem that refuses them): fall back to the
            // real path. ffsubsync then writes the .npz next to the media file, which is
            // still correct — we copy it into the cache afterwards.
            return videoPath;
        }
    }

    /// <summary>
    /// Moves a speech file that ffsubsync wrote next to the media file into the cache.
    /// Used by the fallback path of <see cref="CreateReferenceLink"/>.
    /// </summary>
    /// <param name="referencePath">The reference path that was passed to ffsubsync.</param>
    /// <param name="key">Cache key from <see cref="KeyFor"/>.</param>
    public static void Harvest(string referencePath, string key)
    {
        try
        {
            var produced = Path.Combine(
                Path.GetDirectoryName(referencePath) ?? ".",
                Path.GetFileNameWithoutExtension(referencePath) + ".npz");
            var target = Path.Combine(Root, key + ".npz");
            if (File.Exists(produced) && !string.Equals(produced, target, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Root);
                File.Move(produced, target, overwrite: true);
            }
        }
        catch (Exception)
        {
            // Best effort: a missing cache only costs time on the next run.
        }
    }

    /// <summary>Removes the temporary symlink for a key, if any.</summary>
    /// <param name="key">Cache key from <see cref="KeyFor"/>.</param>
    public static void DropLink(string key)
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(Root, key + ".*"))
            {
                if (!path.EndsWith(".npz", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception)
        {
            // Nothing to clean up.
        }
    }

    /// <summary>
    /// Drops entries older than <see cref="MaxAge"/>, then the oldest ones until the cache
    /// fits in <see cref="MaxBytes"/>. Called after every freshly written entry, so cache
    /// files orphaned by replaced or re-encoded media cannot pile up forever.
    /// </summary>
    /// <returns>Number of entries removed.</returns>
    public static int Prune()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            var cutoff = DateTime.UtcNow - MaxAge;
            var entries = new List<FileInfo>();
            foreach (var file in Directory.EnumerateFiles(Root, "*.npz"))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < cutoff || info.LastAccessTimeUtc < cutoff)
                {
                    try
                    {
                        info.Delete();
                        removed++;
                        continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                }

                entries.Add(info);
            }

            long total = 0;
            foreach (var entry in entries)
            {
                total += entry.Length;
            }

            if (total <= MaxBytes)
            {
                return removed;
            }

            foreach (var entry in entries.OrderBy(e => e.LastWriteTimeUtc))
            {
                if (total <= MaxBytes)
                {
                    break;
                }

                try
                {
                    total -= entry.Length;
                    entry.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // Pruning is best effort; it must never break a sync.
        }

        return removed;
    }

    /// <summary>Deletes every cached entry (Settings → "Clear speech cache").</summary>
    /// <returns>Number of entries removed.</returns>
    public static int Clear()
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            foreach (var file in Directory.GetFiles(Root))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException)
                {
                    continue;
                }
            }
        }
        catch (Exception)
        {
            // Best effort.
        }

        return removed;
    }

    /// <summary>Human-readable cache size, for logs and the status line.</summary>
    /// <returns>Entries and total megabytes, e.g. "12 entries, 0.3 MB".</returns>
    public static string Describe()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return "empty";
            }

            var files = Directory.GetFiles(Root, "*.npz");
            long bytes = 0;
            foreach (var file in files)
            {
                bytes += new FileInfo(file).Length;
            }

            var mb = bytes / (1024.0 * 1024.0);
            return $"{files.Length} entr{(files.Length == 1 ? "y" : "ies")}, "
                + mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }
}
