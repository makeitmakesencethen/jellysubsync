using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What a finished extraction process amounts to: usable, or partial and to be thrown away.
/// </summary>
/// <remarks>
/// The reason is written for the job's error message, so it names the evidence rather than a code.
/// </remarks>
/// <param name="Accept">Whether the produced subtitle may be used.</param>
/// <param name="Reason">Why it may not, empty when it may.</param>
/// <param name="Cues">Cues the output holds, 0 when it was not read.</param>
/// <param name="DiscardPartial">Whether a file was left behind that has to be deleted.</param>
internal sealed record ExtractionVerdict(bool Accept, string Reason, int Cues, bool DiscardPartial)
{
    /// <summary>Builds the verdict for a usable output.</summary>
    /// <param name="cues">Cues the output holds.</param>
    /// <returns>The verdict.</returns>
    internal static ExtractionVerdict Usable(int cues) => new(true, string.Empty, cues, false);
}

/// <summary>
/// Decides whether an extraction's output may be used, and takes the partial away when it may not (B8).
/// </summary>
/// <remarks>
/// A failed ffmpeg run used to be accepted whenever a file existed at the output path
/// (<c>if (exitCode != 0 &amp;&amp; !File.Exists(outputPath))</c>), so a kill, a decode error or a disk that ran
/// out of space during a demux left a part of the subtitle behind and the engine was handed it as if it were
/// the whole track. The measured shape of a truncated read makes the file itself look healthy: ffmpeg reading
/// a cut-short container exits <b>0</b>, writes a well-formed SRT that ends at a cue boundary, and reports the
/// truncation only on stderr - 17 of the fixture's 30 cues, "File ended prematurely", exit code 0.
/// <para>
/// So the guard looks at four things and accepts only when all of them hold: the exit code, ffmpeg's own
/// statement about the input (its truncation warnings), the file existing, and the file being a
/// structurally complete SRT with at least one cue. The last two are the kill-mid-write case, where a byte
/// prefix of a healthy file ends inside a cue block.
/// </para>
/// </remarks>
internal static class ExtractionOutputGuard
{
    /// <summary>
    /// The lines ffmpeg prints when the input it read was not the whole file.
    /// </summary>
    /// <remarks>
    /// Deliberately demuxer and input level only: "Error while decoding" and "corrupt decoded frame" describe
    /// the video stream, which cannot lose subtitle cues, and refusing a whole extraction over a damaged frame
    /// of a file whose subtitles are perfect is a failure the user cannot act on. These five are the container
    /// reader saying the file ended early or could not be read at all.
    /// </remarks>
    internal static readonly string[] TruncationMarkers =
    {
        "ended prematurely",
        "Truncating packet",
        "Packet corrupt",
        "Invalid data found when processing input",
        "Error opening input"
    };

    /// <summary>
    /// Matches an SRT timing line, the only line a cue must have to be a cue.
    /// </summary>
    private static readonly Regex TimingLine = new(
        @"^\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}\s*-->\s*\d{1,2}:\d{2}:\d{2}[,.]\d{1,3}",
        RegexOptions.Compiled);

    /// <summary>
    /// Finds the first line in which ffmpeg says the input was not read to its end.
    /// </summary>
    /// <param name="lines">The process's stderr lines, in arrival order.</param>
    /// <returns>The matching marker, or null when ffmpeg made no such statement.</returns>
    internal static string? TruncationMarkerIn(IEnumerable<string>? lines)
    {
        if (lines is null)
        {
            return null;
        }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (var marker in TruncationMarkers)
            {
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    return marker;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Judges a finished extraction and, when it may not be used, deletes what it left behind.
    /// </summary>
    /// <param name="exitCode">The process's exit code.</param>
    /// <param name="outputPath">Where the subtitle was to be written.</param>
    /// <param name="stderrLines">The process's stderr lines, in arrival order.</param>
    /// <returns>The verdict, with <see cref="ExtractionVerdict.DiscardPartial"/> set when a file was removed.</returns>
    internal static ExtractionVerdict Judge(int exitCode, string? outputPath, IReadOnlyList<string>? stderrLines)
    {
        var exists = !string.IsNullOrEmpty(outputPath) && File.Exists(outputPath);
        var marker = TruncationMarkerIn(stderrLines);

        if (exitCode != 0)
        {
            var why = marker is null
                ? $"ffmpeg stopped with exit code {exitCode} before it had written the subtitle"
                : $"ffmpeg stopped with exit code {exitCode} after reporting '{marker}'";
            return Reject(why, outputPath, exists);
        }

        if (!exists)
        {
            return new ExtractionVerdict(false, "ffmpeg reported success but wrote no subtitle file", 0, false);
        }

        string text;
        try
        {
            text = File.ReadAllText(outputPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Reject($"the extracted subtitle could not be read back ({ex.Message})", outputPath, true);
        }

        if (marker is not null)
        {
            var cues = CountCues(text);
            return Reject(
                $"ffmpeg read a partial file - it reported '{marker}' - so the {cues} cue(s) it produced are "
                + "only part of the track",
                outputPath,
                true);
        }

        if (SrtIsComplete(text, out var found, out var whyNot))
        {
            return ExtractionVerdict.Usable(found);
        }

        return Reject("the extracted subtitle is incomplete: " + whyNot, outputPath, true);
    }

    /// <summary>
    /// Checks that a text is a complete SRT: every block is a cue, and the file ends at a cue boundary.
    /// </summary>
    /// <remarks>
    /// The trailing blank line is not a nicety of this parser but what ffmpeg's SRT muxer always writes: every
    /// cue ends with a newline and a blank line, verified on this project's fixtures, on a real episode's
    /// signs track and on a single-cue file. A file that stops in the middle of a cue therefore fails this,
    /// which is exactly the shape a killed process leaves.
    /// </remarks>
    /// <param name="text">The extracted subtitle.</param>
    /// <param name="cues">Cues found.</param>
    /// <param name="whyNot">Why it is not complete, empty when it is.</param>
    /// <returns>True when the text is a complete, parseable SRT.</returns>
    internal static bool SrtIsComplete(string text, out int cues, out string whyNot)
    {
        cues = 0;
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            whyNot = "the file is empty";
            return false;
        }

        if (!normalized.EndsWith("\n\n", StringComparison.Ordinal))
        {
            whyNot = "the file ends inside a cue, so the last line of the track was never written out";
            return false;
        }

        foreach (var block in normalized.Split("\n\n", StringSplitOptions.None))
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                continue;
            }

            var lines = block.Split('\n');
            if (lines.Length < 3)
            {
                whyNot = $"cue {cues + 1} is cut off (only {lines.Length} line(s) of it are there)";
                return false;
            }

            if (!lines[0].Trim().All(char.IsDigit) || lines[0].Trim().Length == 0)
            {
                whyNot = $"cue {cues + 1} does not start with a cue number";
                return false;
            }

            if (!TimingLine.IsMatch(lines[1]))
            {
                whyNot = $"cue {cues + 1} has no usable timing line";
                return false;
            }

            if (lines.Skip(2).All(string.IsNullOrWhiteSpace))
            {
                whyNot = $"cue {cues + 1} has no text";
                return false;
            }

            cues++;
        }

        if (cues == 0)
        {
            whyNot = "the file holds no cues";
            return false;
        }

        whyNot = string.Empty;
        return true;
    }

    /// <summary>
    /// Counts cues in an SRT without judging it, for the numbers in a rejection message.
    /// </summary>
    /// <param name="text">The subtitle text.</param>
    /// <returns>How many timing lines it holds.</returns>
    internal static int CountCues(string text)
    {
        var count = 0;
        foreach (var line in text.Split('\n'))
        {
            if (TimingLine.IsMatch(line))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Deletes a partial extraction so nothing downstream can be handed it.
    /// </summary>
    /// <param name="outputPath">The file to remove.</param>
    /// <returns>One line for the log, saying what was removed.</returns>
    internal static string Discard(string? outputPath)
    {
        if (string.IsNullOrEmpty(outputPath) || !File.Exists(outputPath))
        {
            return "no partial file was left behind";
        }

        long size;
        try
        {
            size = new FileInfo(outputPath).Length;
        }
        catch (Exception)
        {
            size = -1;
        }

        try
        {
            File.Delete(outputPath);
            return $"discarded the partial extraction ({size} bytes) at {outputPath}";
        }
        catch (Exception ex)
        {
            return $"could not delete the partial extraction at {outputPath} ({ex.Message}) - it is not used for anything";
        }
    }

    private static ExtractionVerdict Reject(string reason, string? outputPath, bool discardPartial)
    {
        if (!discardPartial)
        {
            return new ExtractionVerdict(false, reason, 0, false);
        }

        // The file goes first: whatever this verdict causes, a partial RST must not be readable afterwards,
        // and the reason text carries what was removed.
        return new ExtractionVerdict(false, reason + " - " + Discard(outputPath), 0, true);
    }
}
