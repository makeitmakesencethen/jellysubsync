using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What an alignment changed: the displacement, the framerate ratio, and the shape of the change.
/// </summary>
/// <remarks>
/// Split out of <c>SubSyncService</c> (the C6 cluster of <c>knowledge/SUBSYNCSERVICE_MAP.md</c>). Every member
/// here is a measurement or a predicate over two subtitle files - the sync's own maths, with no queue, no job
/// and no storage in it. The one exception is <see cref="RescaleOntoReferenceSpan"/>, which reports a failure
/// through the service's logger and now takes that logger as its last parameter; it is the only member whose
/// text changed when it moved, and the change is one line in the catch.
/// </remarks>
public static class AlignmentMetrics
{
    /// <summary>
    /// Whether a subtitle ruler's answer and the film's own audio disagree about how far the subtitles must move.
    /// </summary>
    /// <remarks>
    /// A pure function on purpose: the numbers are the decision, and a check has to be able to disagree with it.
    /// Measured 2026-09-15 on the rig - the same ruler back-to-back agrees with itself to a hundredth of a second,
    /// where the other-cut ruler's 24 170 ms against the audio's own answer is a different film's timeline.
    /// </remarks>
    /// <param name="rulerShiftMs">The shift the subtitle ruler asked for.</param>
    /// <param name="audioShiftMs">The shift the film's own audio asked for, on the same subtitle.</param>
    /// <param name="referenceCeilingMs">The configured limit on a subtitle ruler's demanded shift.</param>
    /// <returns>True when the ruler loses to the audio.</returns>
    internal static bool RulersDisagree(long rulerShiftMs, long audioShiftMs, double referenceCeilingMs)
        => Math.Abs(audioShiftMs - rulerShiftMs)
           > Math.Max(1.0, referenceCeilingMs) * SubtitleReferenceAudioAgreementFraction;

    /// <summary>
    /// Whether a subtitle ruler's cues moved too unevenly for it to be this film's own timeline.
    /// </summary>
    /// <remarks>
    /// Kept as a pure function because the numbers are the whole decision and a check has to be able to disagree
    /// with it: on 2026-09-15 the real sibling track of a file measured a spread of 0 ms and the same track from
    /// a 2 % longer cut measured 27 760 ms, at a 30 s reference ceiling (so a 7 500 ms bar).
    /// </remarks>
    /// <param name="change">What the sync changed, cue by cue.</param>
    /// <param name="referenceCeilingMs">The configured limit on a subtitle ruler's demanded shift, in milliseconds.</param>
    /// <returns>True when the ruler should be discarded and the audio used instead.</returns>
    internal static bool RulerSpreadTooWide(SyncChange change, double referenceCeilingMs)
        => change.SpreadMs > Math.Max(1.0, referenceCeilingMs) * SubtitleReferenceSpreadFraction;

    /// <summary>
    /// How much of the subtitle-reference offset ceiling the per-cue spread may reach before that ruler is
    /// refused as "not the same cut".
    /// </summary>
    /// <remarks>
    /// A quarter of <c>MaxSubtitleReferenceOffsetSeconds</c>, so both numbers move together and the check has no
    /// constant of its own: at the 30 s default a spread over 7,5 s is refused. Measured on 2026-09-15, the real
    /// sibling track of a file measured a spread of 0,00 s and the same track from a 2 % longer cut measured
    /// 27,76 s, so the line sits between two observations rather than in the middle of one. A language's own
    /// timing differences are fractions of a second, and a genuinely rescaled framerate mismatch is rescaled by
    /// the plugin *before* the engine sees it - which is why a wide spread here means the ruler, not the timing.
    /// </remarks>
    public const double SubtitleReferenceSpreadFraction = 0.25;

    /// <summary>
    /// How far apart a subtitle ruler's answer and the film's own audio may be before the ruler loses.
    /// </summary>
    /// <remarks>
    /// A tenth of the reference ceiling - 3 s at the default. Measured on 2026-09-15: two alignments of the same
    /// file against the *same* ruler differ by hundredths of a second, and the rig's other-cut ruler measured
    /// 24 170 ms against the audio's own answer, so the line sits between a measured agreement and a measured
    /// disagreement rather than in the middle of one.
    /// </remarks>
    public const double SubtitleReferenceAudioAgreementFraction = 0.1;

    /// <summary>
    /// Whether an alignment holds up against the film's own audio.
    /// </summary>
    /// <remarks>
    /// The audio is the film: a subtitle that has been put in the right place needs almost nothing further to line up
    /// with the speech, while a stretch applied to a differently cut subtitle - or a wide allowance that locked onto
    /// the wrong part of the audio - still asks for a large shift. Ten seconds, or half a percent of the runtime, is
    /// the room left for a genuine difference in intro or outro length.
    /// </remarks>
    /// <param name="ratio">Time ratio the audio alignment asked for on the result (1.0 = none).</param>
    /// <param name="shiftMs">Shift the audio alignment asked for on the result.</param>
    /// <param name="videoSeconds">The file's duration.</param>
    /// <returns>True when the result holds.</returns>
    internal static bool AlignmentHoldsAgainstAudio(double ratio, long shiftMs, double videoSeconds)
    {
        if (Math.Abs(ratio - 1.0) > 0.005)
        {
            return false;
        }

        var ceiling = Math.Max(10.0, videoSeconds * 0.005);
        return Math.Abs(shiftMs) <= ceiling * 1000.0;
    }

    /// <summary>
    /// Whether the subtitle being synced is the one that sits off the file's own timeline.
    /// </summary>
    /// <remarks>
    /// Both a subtitle timed for another playback speed and one from a longer cut show up as a span that is a few
    /// percent away from the reference's, so the references alone cannot say which of the two is the odd one out.
    /// The file's duration can: a subtitle spans the film it was timed for, so the side that disagrees with the
    /// duration is the one to correct. Getting this backwards would rescale a correct subtitle onto a mis-timed
    /// ruler, and every subtitle of the file shares that ruler in a bulk run.
    /// </remarks>
    /// <param name="targetSpan">Span of the subtitle being synced, in seconds.</param>
    /// <param name="referenceSpan">Span of the reference, in seconds.</param>
    /// <param name="videoSeconds">The file's duration, in seconds.</param>
    /// <returns>True when the subtitle is the side to rescale; false when the reference is, or when both or neither are.</returns>
    internal static bool IsTargetOffTheVideo(double targetSpan, double referenceSpan, double videoSeconds)
    {
        if (videoSeconds <= 60)
        {
            return true;
        }

        var targetOnVideo = Math.Abs((targetSpan / videoSeconds) - 1.0) <= 0.03;
        var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
        if (!referenceOnVideo && !targetOnVideo)
        {
            // Neither span matches the file: not a case this rule can settle, so the pair rule and the alignment
            // guards below decide, as before.
            return true;
        }

        return !targetOnVideo;
    }

    /// <summary>
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
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frac))
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
    /// <param name="videoDuration">The file's duration, which settles which side is off the video's timeline.</param>
    /// <param name="tempDir">Directory to write the copy into.</param>
    /// <param name="job">The job, for the log.</param>
    /// <param name="logger">The logger a failure to rescale is reported to.</param>
    /// <returns>The path of the rescaled copy, or null when nothing should change.</returns>
    internal static string? RescaleOntoReferenceSpan(
        string targetPath,
        string referencePath,
        TimeSpan videoDuration,
        string tempDir,
        SyncJob job,
        ILogger logger)
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

        // Which side is off the video's timeline? A subtitle spans the film it was timed for, so a span a few
        // percent away from the file's duration is a subtitle timed for a different playback speed. If the
        // *reference* is that one, rescaling the target onto it would drag a correct subtitle onto a mis-timed
        // ruler - and in bulk that would happen to every subtitle of the file, because they share the reference.
        if (videoDuration > TimeSpan.FromSeconds(60))
        {
            var videoSeconds = videoDuration.TotalSeconds;
            var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
            if (IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds))
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (!referenceOnVideo)
            {
                PluginLog.Info(
                    $"[{job.Id}] framerate: the reference spans {referenceSpan:0.0} s of the file's {videoSeconds:0.0} s "
                    + $"(the subtitle's own span, {targetSpan:0.0} s, matches the file), so the reference is the odd one "
                    + "out \u2014 no rescale; a shift from a reference this far off the video is refused as before");
                return null;
            }
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
                + "framerate pair \u2014 a different cut, left for the alignment to report");
            return null;
        }

        try
        {
            var rescaled = Path.Combine(tempDir, "rescaled-input.srt");
            using (var writer = new StreamWriter(rescaled, false, new System.Text.UTF8Encoding(false)))
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
                + "\u2014 rescaling it onto the reference's time base before aligning, because a subtitle "
                + "reference cannot fix a framerate mismatch itself");
            return rescaled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not rescale {Path} onto the reference's time base", targetPath);
            return null;
        }
    }

    /// <summary>
    /// Parses SRT cue start times (seconds).
    /// </summary>
    internal static List<double>? ParseSrtCueStarts(string path)
    {
        try
        {
            var starts = new List<double>();
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                var arrow = trimmed.IndexOf("-->", StringComparison.Ordinal);
                if (arrow <= 0)
                {
                    continue;
                }

                var ts = trimmed[..arrow].Trim();
                var parts = ts.Split(':', ',', '.');
                if (parts.Length < 4)
                {
                    continue;
                }

                if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var s))
                {
                    // The fraction matters: SRT writes milliseconds (",500"), and dropping them
                    // truncated every cue to a whole second. A real 400 ms shift then measured as
                    // "0 ms offset" - which would have skipped saving a genuine correction. Some
                    // tools write one or two digits, so scale by the digit count.
                    var fraction = 0.0;
                    if (parts.Length > 3 && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var frac))
                    {
                        fraction = frac / Math.Pow(10, parts[3].Length);
                    }

                    starts.Add((h * 3600.0) + (m * 60.0) + s + fraction);
                }
            }

            return starts.Count >= 3 ? starts : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What a successful sync actually changed, measured by comparing the cue timings of the input
    /// and the synced file.
    /// </summary>
    /// <param name="ShiftMs">Median displacement of the cues, in milliseconds (signed; + = moved later).</param>
    /// <param name="Ratio">Fitted time ratio; 1.0 when no framerate correction was needed.</param>
    /// <param name="DriftMs">Cumulative drift the ratio fixes over the subtitle's runtime.</param>
    /// <param name="SpreadMs">
    /// Interquartile range of the per-cue displacement, in milliseconds. A median alone cannot tell a ruler that
    /// matches from a ruler that is not this cut: measured on 2026-09-15, a subtitle aligned against the same
    /// track from a 2 % longer cut showed a median of -26,52 s - under the 30 s reference ceiling, so nothing
    /// refused it - with an IQR of 27,76 s, where the correct sibling scored an IQR of 0,00 s.
    /// </param>
    /// <param name="RangeMs">Largest minus smallest displacement, in milliseconds.</param>
    internal readonly record struct SyncChange(long ShiftMs, double Ratio, long DriftMs, long SpreadMs = 0, long RangeMs = 0)
    {
        /// <summary>
        /// Gets a value indicating whether the sync changed nothing: no offset and no framerate
        /// correction. ffsubsync still writes an output file in that case, and a synced sidecar whose
        /// timings are identical to its source is only noise in the library.
        /// </summary>
        public bool IsNoChange => ShiftMs == 0 && Math.Abs(Ratio - 1.0) < 1e-4;

        /// <summary>
        /// Describes the change for the log and the History list.
        /// </summary>
        /// <returns>A short human-readable description.</returns>
        public string Describe() => Math.Abs(Ratio - 1.0) < 1e-4
            ? $"{ShiftMs:+0;-0} ms offset"
            : $"{ShiftMs:+0;-0} ms offset at start \u00b7 framerate ratio {Ratio:0.0000}\u00d7 (\u2248{DriftMs:+0;-0} ms cumulative drift)";
    }

    /// <summary>
    /// How the engine's answer moves the cues: the pieces it moved them in, how flat each piece is, and the
    /// largest displacement any cue got.
    /// </summary>
    /// <param name="Segments">How many runs of consecutive cues share one displacement.</param>
    /// <param name="MaxWithinSpreadMs">The widest displacement span inside any one run, in milliseconds.</param>
    /// <param name="LargestShiftMs">The largest displacement any cue got, in absolute milliseconds.</param>
    /// <param name="SmallestStepMs">
    /// The smallest jump between two neighbouring pieces. This is what tells a staircase from a ramp: a piece
    /// boundary is a real step, while a rescale creeps by one engine sample at a time and produces a piece per
    /// cue.
    /// </param>
    internal readonly record struct SegmentStructure(
        int Segments, long MaxWithinSpreadMs, long LargestShiftMs, long SmallestStepMs);

    /// <summary>
    /// How different two neighbouring cues' displacements must be before they count as different pieces.
    /// </summary>
    /// <remarks>
    /// One engine sample: its speech signal is a 100 Hz series, so offsets are quantized to 10 ms and a
    /// difference below that is the same offset written twice. Not a policy value - a resolution.
    /// </remarks>
    internal const long EngineSampleMs = 10;

    /// <summary>
    /// Reads a sync as a piecewise-constant displacement rather than a line through it.
    /// </summary>
    /// <remarks>
    /// Why this exists (C2, measured 2026-09-15): with a split penalty the engine may move different parts of
    /// the subtitle by different amounts, and the plugin's own guard then sees a *linear* ratio away from 1.0
    /// and refuses a result that is right in both halves. Measured on the 48-minute episode with a 20 s
    /// discontinuity inserted at its midpoint: the piecewise answer is +7,54 s then +27,54 s-20 s (both halves
    /// within 60 ms of the truth), and the least-squares line through it reads 1,01082x - which
    /// <see cref="IsRescaleAcceptable"/> refuses, because 1,01082 is not a framerate pair. The line is the
    /// wrong reading of a step; this is the right one.
    /// </remarks>
    /// <param name="inputPath">The subtitle the engine was given.</param>
    /// <param name="outputPath">The subtitle the engine wrote.</param>
    /// <param name="stepToleranceMs">How far apart two neighbouring displacements must be to be a new piece.</param>
    /// <returns>The structure, or null when the two files cannot be compared cue for cue.</returns>
    internal static SegmentStructure? MeasureSegmentStructure(string inputPath, string outputPath, long stepToleranceMs)
    {
        var before = ParseSrtCueStarts(inputPath);
        var after = ParseSrtCueStarts(outputPath);
        if (before is null || after is null || before.Count < 3 || after.Count != before.Count)
        {
            return null;
        }

        var segments = 1;
        var largestShift = 0L;
        var runMinMs = (long)Math.Round((after[0] - before[0]) * 1000.0);
        var runMaxMs = runMinMs;
        var previousPieceMs = runMinMs;
        var maxWithin = 0L;
        long? smallestStepMs = null;

        for (var i = 1; i < before.Count; i++)
        {
            var shiftMs = (long)Math.Round((after[i] - before[i]) * 1000.0);
            largestShift = Math.Max(largestShift, Math.Abs(shiftMs));

            if (Math.Abs(shiftMs - runMaxMs) > stepToleranceMs || Math.Abs(shiftMs - runMinMs) > stepToleranceMs)
            {
                // A new piece: close the one that ran so far, and start this one. The jump between the two
                // pieces is recorded, because a ramp is a long series of jumps the size of one sample.
                maxWithin = Math.Max(maxWithin, runMaxMs - runMinMs);
                var stepMs = Math.Abs(shiftMs - previousPieceMs);
                smallestStepMs = smallestStepMs is null ? stepMs : Math.Min(smallestStepMs.Value, stepMs);
                previousPieceMs = shiftMs;
                segments++;
                runMinMs = shiftMs;
                runMaxMs = shiftMs;
                continue;
            }

            runMinMs = Math.Min(runMinMs, shiftMs);
            runMaxMs = Math.Max(runMaxMs, shiftMs);
        }

        maxWithin = Math.Max(maxWithin, runMaxMs - runMinMs);
        return new SegmentStructure(segments, maxWithin, largestShift, smallestStepMs ?? 0);
    }

    /// <summary>
    /// Decides whether a piecewise reading of the engine's answer is trustworthy enough to write.
    /// </summary>
    /// <remarks>
    /// The bar is the one the plugin already uses for "these cues moved together" - the spread ceiling that
    /// <see cref="RulerSpreadTooWide"/> refuses a subtitle ruler past (a quarter of the configured reference
    /// ceiling, so it carries no constant of its own) - plus the search window every displacement must stay
    /// inside, and every step between two pieces at least that large. A *rescale* cannot pass it: a ramp is a
    /// long series of jumps one engine sample (10 ms) wide, so either it reads as one piece whose spread is the
    /// whole drift, or as a thousand pieces whose steps are 10 ms - both are refused, by the same tolerance. A
    /// staircase the engine paid a split penalty for is what can pass: few pieces, each flat, each step real.
    /// <para>
    /// The residual risk, stated rather than hidden: a wrong answer that happens to be stepwise and flat would
    /// be accepted. The engine charges `split-penalty` seconds of overlap per step, so a staircase is expensive
    /// to fake, and the subtitle-ruler cross-check (S31) is unaffected - it reads the median.
    /// </para>
    /// </remarks>
    /// <param name="structure">The measured structure.</param>
    /// <param name="spreadCeilingMs">How far the displacement may vary inside one piece.</param>
    /// <param name="windowMs">The search window a displacement must stay inside.</param>
    /// <returns>True when the piecewise reading holds.</returns>
    internal static bool PiecewiseHolds(SegmentStructure structure, double spreadCeilingMs, double windowMs)
        => structure.Segments >= 2
           && structure.MaxWithinSpreadMs <= Math.Max(1.0, spreadCeilingMs)
           && structure.SmallestStepMs >= Math.Max(1.0, spreadCeilingMs)
           && structure.LargestShiftMs <= windowMs;

    /// <summary>
    /// Framerate pairs ffsubsync can legitimately be correcting: 25/23.976 (PAL film speedup),
    /// 25/24, 24/23.976, their inverses, and the half/double cases.
    /// </summary>
    internal static readonly double[] KnownFramerateRatios =
    {
        1.04271, 1.04167, 1.00100, 0.99900, 0.96000, 0.95904, 1.25, 0.8, 2.0, 0.5
    };

    /// <summary>
    /// Decides whether a measured sync is safe to write.
    /// </summary>
    /// <remarks>
    /// A rescale is not a correction that can be partly right: every cue after the first moves by a
    /// growing amount, so a wrong ratio ruins a whole file rather than leaving it slightly off. With
    /// framerate correction switched off, any ratio away from 1.0 means the engine rescaled timings
    /// anyway - the failure this guard exists for - so the result is refused. With it switched on, a
    /// ratio is expected but has to be a real framerate pair. The shift bound catches the rest: a
    /// single offset is what <c>--max-offset-seconds</c> asked for, so a shift beyond double that
    /// bound means the timings moved for some other reason.
    /// </remarks>
    /// <param name="ratio">Measured time ratio between input and synced output (1.0 = no rescale).</param>
    /// <param name="shiftMs">Measured offset in milliseconds.</param>
    /// <param name="maxOffsetSeconds">The configured offset bound handed to ffsubsync.</param>
    /// <param name="framerateCorrectionEnabled">Whether rescaling was requested.</param>
    /// <returns>True when the output may be written.</returns>
    internal static bool IsRescaleAcceptable(double ratio, long shiftMs, int maxOffsetSeconds, bool framerateCorrectionEnabled)
    {
        if (framerateCorrectionEnabled)
        {
            foreach (var known in KnownFramerateRatios)
            {
                if (Math.Abs(ratio - known) <= 0.003)
                {
                    return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L * 20;
                }
            }

            return Math.Abs(ratio - 1.0) <= 0.005;
        }

        if (Math.Abs(ratio - 1.0) > 0.005)
        {
            return false;
        }

        return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L * 2;
    }

    /// <summary>
    /// Measures what a sync changed, by comparing cue timings of the original and the synced file.
    /// </summary>
    /// <param name="inputPath">Subtitle handed to ffsubsync.</param>
    /// <param name="outputPath">Subtitle ffsubsync wrote.</param>
    /// <returns>The measurement, or null when the files cannot be compared (too few cues, non-SRT).</returns>
    internal static SyncChange? MeasureSyncChange(string inputPath, string outputPath)
    {
        var before = ParseSrtCueStarts(inputPath);
        var after = ParseSrtCueStarts(outputPath);
        if (before is null || after is null || before.Count < 3 || after.Count != before.Count)
        {
            return null;
        }

        var n = before.Count;
        var diffs = new List<double>(n);
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (var i = 0; i < n; i++)
        {
            var x = before[i];
            var y = after[i];
            diffs.Add(y - x);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        diffs.Sort();
        var shiftMs = (long)Math.Round(diffs[n / 2] * 1000.0);

        var denom = (n * sumXX) - (sumX * sumX);
        double ratio = 1.0;
        if (Math.Abs(denom) > 1e-9)
        {
            ratio = ((n * sumXY) - (sumX * sumY)) / denom;
        }

        var driftMs = (long)Math.Round((ratio - 1.0) * before[^1] * 1000.0);

        // The spread of the displacement, not just its middle: cues that all moved by the same amount are a
        // sync (or a cut that matches), cues that moved by wildly different amounts are a ruler that does not
        // belong to this film wherever the median happens to land.
        var spreadMs = (long)Math.Round((diffs[(3 * n) / 4] - diffs[n / 4]) * 1000.0);
        var rangeMs = (long)Math.Round((diffs[^1] - diffs[0]) * 1000.0);
        return new SyncChange(shiftMs, ratio, driftMs, spreadMs, rangeMs);
    }

    /// <summary>
    /// Whether a subtitle looks like a forced/signs track rather than the full one: very few cues for
    /// a long video.
    ///
    /// A full episode subtitle carries hundreds of cues (roughly one every few seconds), so a handful
    /// over more than ten minutes is a track that only translates on-screen text. Nothing else about
    /// the output shows it - the synced sidecar is perfectly valid, it just contains two lines - which
    /// is why this is stated in the log and in the job's outcome.
    /// </summary>
    /// <param name="cueCount">Number of cues in the subtitle that was synced.</param>
    /// <param name="duration">Runtime of the video.</param>
    /// <returns>True when the track is suspiciously sparse.</returns>
    public static bool LooksLikeSignsTrack(int cueCount, TimeSpan duration)
        => cueCount > 0 && cueCount < 12 && duration > TimeSpan.FromMinutes(10);

    /// <summary>
    /// Counts the cues in a subtitle file. Returns -1 when it cannot be read, which is treated as
    /// "unknown" rather than as zero cues.
    /// </summary>
    /// <param name="path">Subtitle path.</param>
    /// <returns>Cue count, or -1.</returns>
    internal static int CountSubtitleCues(string path)
    {
        try
        {
            return SrtWriter.CountCues(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
        }
    }

    internal static string? DescribeSyncChange(string inputPath, string outputPath)
        => MeasureSyncChange(inputPath, outputPath)?.Describe();
}
