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
    /// <param name="DurationGuessed">
    /// True when the block carried no duration and <paramref name="EndMs"/> is this reader's own
    /// guess, so the renderer may pull it back to the next entry. False is a duration the file
    /// stated, which must be written as it is - a stated duration that happens to equal the guess
    /// (2000 ms) used to be indistinguishable from one, and was rewritten (F19).
    /// </param>
    public readonly record struct Entry(long StartMs, long EndMs, string Text, bool DurationGuessed = false);

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

            // Only a guessed end may be pulled back to the next entry. A duration the file stated is
            // written as it is, whatever its length: inferring the guess from the number (2000 ms)
            // rewrote real two-second cues (F19).
            var guessed = entry.DurationGuessed;
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

    /// <summary>
    /// True when a subtitle file name is one of this plugin's own synced outputs.
    /// </summary>
    /// <remarks>
    /// They are written as <c>&lt;video&gt;.SYNCED.&lt;lang&gt;.srt</c>, and Jellyfin reads the marker
    /// as the language name, so these used to be offered in every track list as a language called
    /// "SYNCED" and were queued alongside the tracks they were produced from - which meant a season
    /// sync did the same work twice and the second pass wrote over what the first had just written.
    /// The legacy hyphen form is recognised too, so a library carrying both is still cleaned up.
    /// </remarks>
    /// <param name="fileName">File name, with or without a directory.</param>
    /// <returns>True when the name carries the synced marker.</returns>
    public static bool IsSyncedSidecarName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var name = Path.GetFileName(fileName);
        return name.Contains(".SYNCED.", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-SYNCED.", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("SYNCED.srt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes a marker the plugin itself wrote from a file name stem (S12).
    /// </summary>
    /// <remarks>
    /// Re-syncing a subtitle the plugin produced used to append a second marker: a job taking
    /// <c>Film.S01E01.SYNCED.ukr.srt</c> wrote <c>Film.S01E01.SYNCED.ukr.SYNCED.srt</c> into the library, which no player
    /// associates with the episode and no sweep ever removes. Stripping the marker the plugin wrote means re-syncing
    /// its own output updates that output.
    /// </remarks>
    /// <param name="stem">The file name without its extension.</param>
    /// <returns>The stem with a trailing marker - and any fields after it - removed.</returns>
    public static string StripSyncedMarker(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return string.Empty;
        }

        var marker = stem.IndexOf(".SYNCED.", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            return stem[..marker];
        }

        if (stem.EndsWith(".SYNCED", StringComparison.OrdinalIgnoreCase))
        {
            return stem[..^".SYNCED".Length];
        }

        var dashed = stem.IndexOf("-SYNCED.", StringComparison.OrdinalIgnoreCase);
        return dashed >= 0 ? stem[..dashed] : stem;
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
