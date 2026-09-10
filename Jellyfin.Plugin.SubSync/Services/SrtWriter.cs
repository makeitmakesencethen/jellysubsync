using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Shared SRT writing for the container-aware extractors (Matroska and MP4), so both
/// produce identical output formatting.
/// </summary>
public static class SrtWriter
{
    /// <summary>One subtitle entry with millisecond timings.</summary>
    /// <param name="StartMs">Start time.</param>
    /// <param name="EndMs">End time.</param>
    /// <param name="Text">Plain text (line breaks allowed).</param>
    public readonly record struct Entry(long StartMs, long EndMs, string Text);

    /// <summary>Renders entries as SRT text, in the order given.</summary>
    /// <param name="entries">Entries, already sorted by start time.</param>
    /// <returns>SRT document text.</returns>
    public static string Render(IReadOnlyList<Entry> entries)
    {
        var builder = new StringBuilder();
        var index = 1;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var end = entry.EndMs;

            // Without an explicit duration the end is a guess: never let a guess overrun
            // the next entry.
            var guessed = entry.EndMs - entry.StartMs == 2000;
            if (i + 1 < entries.Count && (end > entries[i + 1].StartMs || guessed))
            {
                var next = entries[i + 1].StartMs;
                end = next > entry.StartMs ? Math.Min(next - 1, entry.StartMs + 7000) : entry.StartMs + 1500;
            }

            if (end <= entry.StartMs)
            {
                continue;
            }

            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTime(entry.StartMs)).Append(" --> ").Append(FormatTime(end)).Append('\n');
            builder.Append(entry.Text).Append("\n\n");
            index++;
        }

        return builder.ToString();
    }

    /// <summary>Formats milliseconds as an SRT timestamp.</summary>
    /// <param name="totalMs">Milliseconds.</param>
    /// <returns>"HH:MM:SS,mmm".</returns>
    public static string FormatTime(long totalMs)
    {
        if (totalMs < 0)
        {
            totalMs = 0;
        }

        var ms = totalMs % 1000;
        var totalSeconds = totalMs / 1000;
        var seconds = totalSeconds % 60;
        var minutes = (totalSeconds / 60) % 60;
        var hours = totalSeconds / 3600;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:00}:{1:00}:{2:00},{3:000}",
            hours,
            minutes,
            seconds,
            ms);
    }

    /// <summary>Counts cues in SRT text (used for logs and sanity checks).</summary>
    /// <param name="srt">SRT content.</param>
    /// <returns>Number of timing lines.</returns>
    public static int CountCues(string srt)
    {
        var count = 0;
        var searchFrom = 0;
        while (true)
        {
            var at = srt.IndexOf("-->", searchFrom, StringComparison.Ordinal);
            if (at < 0)
            {
                return count;
            }

            count++;
            searchFrom = at + 3;
        }
    }
}
