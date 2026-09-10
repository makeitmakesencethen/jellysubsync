using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Maps a media path to the storage volume it lives on.
///
/// Parallel workers that read from the same device compete for one spindle or one network
/// link, which is how four simultaneous extractions end up crawling together (each reading
/// a whole file). Knowing the volume lets the scheduler allow at most one heavy read per
/// device per wave, while cheap (already cached) work still runs in parallel.
/// </summary>
public static class MediaVolume
{
    private static readonly object Lock = new();
    private static List<(string MountPoint, string Device)>? _mounts;

    /// <summary>Identifies the volume holding a path.</summary>
    /// <param name="path">File path.</param>
    /// <returns>A stable volume key (mount point, or the device name when known).</returns>
    public static string Of(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "unknown";
        }

        var full = Path.GetFullPath(path);
        foreach (var (mountPoint, device) in Mounts())
        {
            if (full.StartsWith(mountPoint, StringComparison.Ordinal))
            {
                return string.IsNullOrEmpty(device) ? mountPoint : mountPoint + "|" + device;
            }
        }

        // No mount table (non-Linux): fall back to the path root.
        try
        {
            return Path.GetPathRoot(full) ?? "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private static List<(string MountPoint, string Device)> Mounts()
    {
        lock (Lock)
        {
            if (_mounts is not null)
            {
                return _mounts;
            }

            var result = new List<(string, string)>();
            try
            {
                if (File.Exists("/proc/mounts"))
                {
                    foreach (var line in File.ReadLines("/proc/mounts"))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length < 2)
                        {
                            continue;
                        }

                        var mountPoint = parts[1].Replace("\\040", " ", StringComparison.Ordinal);
                        result.Add((mountPoint, parts[0]));
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the path-root fallback.
            }

            // Longest mount point first, so /mnt/media wins over /mnt.
            result.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            _mounts = result;
            return _mounts;
        }
    }
}
