#!/usr/bin/env python3
"""Give the framerate option a job on the reference-subtitle path.

A subtitle reference cannot fix a framerate mismatch: the reference is itself a subtitle, so there is no frame
rate for the engine to read and it can only fit a shift. A PAL-timed subtitle then comes out as a pure shift of
about half the film's drift (measured on the user's file: 111.9 s on a 96-minute film), which the 30 s reference
ceiling refuses - so the option appeared to do nothing on exactly the file it was turned on for.
"""
import pathlib

svc = pathlib.Path('Jellyfin.Plugin.SubSync/Services/SubSyncService.cs')
t = svc.read_text()

# 1. the helper, next to the other SRT measurement code
anchor = """    /// <summary>
    /// Parses SRT cue start times (seconds).
    /// </summary>
    internal static List<double>? ParseSrtCueStarts(string path)"""
helper = '''    /// <summary>
    /// Reads one SRT timestamp ("00:01:02,345") as milliseconds.
    /// </summary>
    /// <param name="text">The timestamp, optionally followed by cue coordinates.</param>
    /// <param name="ms">The parsed value.</param>
    /// <returns>True when it parsed.</returns>
    internal static bool TryParseSrtTime(string text, out double ms)
    {
        ms = 0;
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
        {
            return false;
        }

        var parts = token.Split(':', ',', '.');
        if (parts.Length < 4
            || !int.TryParse(parts[0], out var h)
            || !int.TryParse(parts[1], out var m)
            || !int.TryParse(parts[2], out var s)
            || !int.TryParse(parts[3], out var frac))
        {
            return false;
        }

        // One, two or three fraction digits all appear in the wild (see ParseSrtCueStarts).
        var fraction = frac / Math.Pow(10, parts[3].Length);
        ms = ((h * 3600.0) + (m * 60.0) + s + fraction) * 1000.0;
        return true;
    }

    /// <summary>
    /// Writes a copy of a subtitle scaled onto the reference subtitle's time base, when the two spans are a
    /// framerate pair apart.
    /// </summary>
    /// <remarks>
    /// A subtitle reference cannot fix a framerate mismatch by itself: it is another subtitle, so the engine has
    /// no frame rate to read and can only fit a shift. A PAL-timed target then comes out as a pure shift of
    /// roughly half the film's drift - measured on a user's file at 111.9 s over 96 minutes - which the reference
    /// ceiling refuses, so the sync failed on exactly the file the framerate option was turned on for. Both spans
    /// are known here, so the plugin does the rescale the reference cannot do and leaves the aligner the small
    /// shift it is good at. A span difference that is not a pair is a different cut: nothing is rescaled.
    /// </remarks>
    /// <param name="targetPath">The subtitle being synced.</param>
    /// <param name="referencePath">The reference subtitle.</param>
    /// <param name="tempDir">Directory to write the copy into.</param>
    /// <param name="job">The job, for the log.</param>
    /// <returns>The path of the rescaled copy, or null when nothing should change.</returns>
    private string? RescaleOntoReferenceSpan(string targetPath, string referencePath, string tempDir, SyncJob job)
    {
        var target = ParseSrtCueStarts(targetPath);
        var reference = ParseSrtCueStarts(referencePath);
        if (target is null || reference is null || target.Count < 3 || reference.Count < 3)
        {
            return null;
        }

        var targetSpan = target[^1] - target[0];
        var referenceSpan = reference[^1] - reference[0];
        if (targetSpan <= 60 || referenceSpan <= 60)
        {
            return null;
        }

        var scale = referenceSpan / targetSpan;
        if (Math.Abs(scale - 1.0) <= 0.005)
        {
            return null;
        }

        if (!KnownFramerateRatios.Any(known => Math.Abs(scale - known) <= 0.003))
        {
            PluginLog.Info(
                $"[{job.Id}] framerate: the subtitle's span is {scale:0.0000}x the reference's, which is not a "
                + "framerate pair \\u2014 a different cut, left for the alignment to report");
            return null;
        }

        try
        {
            var rescaled = Path.Combine(tempDir, "rescaled-input.srt");
            using (var writer = new StreamWriter(rescaled, false, new UTF8Encoding(false)))
            {
                foreach (var line in File.ReadLines(targetPath))
                {
                    var arrow = line.IndexOf("-->", StringComparison.Ordinal);
                    if (arrow > 0
                        && TryParseSrtTime(line[..arrow].Trim(), out var startMs)
                        && TryParseSrtTime(line[(arrow + 3)..].Trim(), out var endMs))
                    {
                        writer.WriteLine(
                            $"{SrtWriter.FormatTime((long)Math.Round(startMs * scale))} --> "
                            + SrtWriter.FormatTime((long)Math.Round(endMs * scale)));
                    }
                    else
                    {
                        writer.WriteLine(line);
                    }
                }
            }

            PluginLog.Info(
                $"[{job.Id}] framerate: the subtitle's span is {scale:0.0000}x the reference's (a framerate pair) "
                + "\\u2014 rescaling it onto the reference's time base before aligning, because a subtitle "
                + "reference cannot fix a framerate mismatch itself");
            return rescaled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not rescale {Path} onto the reference's time base", targetPath);
            return null;
        }
    }

''' + anchor
assert anchor in t
t = t.replace(anchor, helper, 1)

# 2. use it: the engine gets the rescaled copy, the measurement keeps the original so the guard still sees the
#    whole transform (ratio 1.0427 is a pair; an unrequested rescale is still refused).
old = """            var args = BuildFfSubSyncArgs(config, referencePath, subtitleInputPath, tempOutput, tempDir, serializeSpeech, referenceStream);

            _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);"""
new = """            // The engine is given a rescaled copy when a frame rate difference stands between the two subtitles;
            // the measurement below still compares the original subtitle with the output, so the rescale is
            // visible to the guard rather than hidden from it.
            var engineInput = subtitleInputPath;
            if (usedSubtitleReference && config.FixFramerate && referencePath is not null)
            {
                engineInput = RescaleOntoReferenceSpan(subtitleInputPath, referencePath, tempDir, job) ?? engineInput;
            }

            var args = BuildFfSubSyncArgs(config, referencePath, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream);

            _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);"""
assert old in t
t = t.replace(old, new, 1)

# the retry path builds the same command line
old = """                args = BuildFfSubSyncArgs(config, referencePath, subtitleInputPath, tempOutput, tempDir, serializeSpeech, referenceStream);"""
new = """                args = BuildFfSubSyncArgs(config, referencePath, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream);"""
assert old in t
t = t.replace(old, new, 1)

svc.write_text(t)
print('reference path rescales a framerate-mismatched subtitle before aligning')
