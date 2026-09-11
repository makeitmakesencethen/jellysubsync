using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The plugin's own log file, written next to its other state under the plugin data folder
/// (<c>&lt;jellyfin-data&gt;/subsync/logs/subsync.log</c>).
///
/// Why a second log exists at all: Jellyfin's server log is shared with everything else the server
/// does, rotates on the server's schedule, and needs shell access to read. A sync that behaves badly
/// - a subtitle that took two minutes to extract, a batch that never used the configured parallelism,
/// a track that was silently swapped - needs the numbers from *that run*, in one file, readable from
/// the interface. This is deliberately not a duplicate of the server log: it records the queue, the
/// dispatch decision, the extraction cost and the final result, and nothing else.
///
/// Rules: never throw (a log write must not break a sync), one entry per event with a UTC timestamp,
/// size-based rotation with a fixed number of kept files, and no secrets (paths and counts only).
/// </summary>
public static class PluginLog
{
    /// <summary>Size at which the current file is rotated.</summary>
    public const long DefaultMaxBytes = 4L * 1024 * 1024;

    /// <summary>
    /// Number of rotated files kept beside the current one (subsync.log.1 … subsync.log.N).
    /// </summary>
    public const int KeptFiles = 3;

    private static readonly object Gate = new();

    /// <summary>Gets the directory holding the log files.</summary>
    public static string Directory
        => Plugin.Instance?.LogPath ?? Path.Combine(Path.GetTempPath(), "subsync-logs");

    /// <summary>Gets the path of the current log file.</summary>
    public static string FilePath => Path.Combine(Directory, "subsync.log");

    /// <summary>Writes an informational entry.</summary>
    /// <param name="message">Message text; newlines are kept for stack traces.</param>
    public static void Info(string message) => Append(Directory, "INFO", message, DefaultMaxBytes);

    /// <summary>Writes a warning entry.</summary>
    /// <param name="message">Message text.</param>
    public static void Warn(string message) => Append(Directory, "WARN", message, DefaultMaxBytes);

    /// <summary>Writes an error entry, with the exception detail when there is one.</summary>
    /// <param name="message">Message text.</param>
    /// <param name="exception">Optional exception.</param>
    public static void Error(string message, Exception? exception = null)
        => Append(Directory, "ERROR", exception is null ? message : message + Environment.NewLine + exception, DefaultMaxBytes);

    /// <summary>
    /// Appends one entry to <c>&lt;directory&gt;/subsync.log</c>, rotating it away first if the file has
    /// reached <paramref name="maxBytes"/>.
    ///
    /// The directory and the size limit are parameters so the rotation can be tested without a live
    /// plugin; callers use <see cref="Info"/> and friends.
    /// </summary>
    /// <param name="directory">Directory to write in; created when missing.</param>
    /// <param name="level">Short level tag (INFO/WARN/ERROR).</param>
    /// <param name="message">Message text.</param>
    /// <param name="maxBytes">Rotate once the file is at least this large.</param>
    public static void Append(string directory, string level, string message, long maxBytes = DefaultMaxBytes)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "subsync.log");

            lock (Gate)
            {
                var size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (size >= maxBytes && maxBytes > 0)
                {
                    Rotate(directory);
                }

                var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var builder = new StringBuilder();
                builder.Append(stamp).Append("Z ").Append(level.PadRight(5)).Append(' ').Append(message ?? string.Empty)
                    .Append(Environment.NewLine);
                File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // A log write must never be the reason a sync fails. Console is the last resort so a
            // failure to write is still visible in the container log.
            try
            {
                Console.WriteLine("SubSync log write failed: " + message);
            }
            catch
            {
                // Nothing left to do.
            }
        }
    }

    /// <summary>
    /// Describes the log for the interface: absolute path, current size and how many rotated files
    /// sit beside it.
    /// </summary>
    /// <returns>A one-line description.</returns>
    public static string Describe()
    {
        try
        {
            var path = FilePath;
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            var rotated = 0;
            for (var i = 1; i <= KeptFiles; i++)
            {
                if (File.Exists(path + "." + i.ToString(CultureInfo.InvariantCulture)))
                {
                    rotated++;
                }
            }

            return path + " (" + HumanSize(size) + (rotated > 0 ? ", " + rotated + " rotated" : string.Empty) + ")";
        }
        catch
        {
            return "(log unavailable)";
        }
    }

    /// <summary>
    /// Reads the last <paramref name="maxBytes"/> of the current log, for the download endpoint.
    /// </summary>
    /// <param name="maxBytes">How much of the tail to return.</param>
    /// <returns>The tail of the log, or a short note saying why it is empty.</returns>
    public static string Tail(int maxBytes)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return "(no log yet at " + path + ")";
            }

            var length = new FileInfo(path).Length;
            if (length <= maxBytes)
            {
                return File.ReadAllText(path);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(-maxBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            return "(showing the last " + HumanSize(maxBytes) + " of " + HumanSize(length) + ")"
                + Environment.NewLine + reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return "(could not read the log: " + ex.Message + ")";
        }
    }

    private static void Rotate(string directory)
    {
        var path = Path.Combine(directory, "subsync.log");
        for (var i = KeptFiles; i >= 1; i--)
        {
            var target = path + "." + i.ToString(CultureInfo.InvariantCulture);
            var source = i == 1 ? path : path + "." + (i - 1).ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                }

                File.Move(source, target);
            }
            catch (IOException)
            {
                // Another writer has the file; leave it and try again next time.
            }
        }
    }

    private static string HumanSize(long bytes)
        => bytes >= 1024 * 1024
            ? (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
            : bytes >= 1024
                ? (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " kB"
                : bytes + " bytes";
}
