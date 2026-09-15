using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What a subtitle ruler's shape score says about it: how clean the peak is, where it sits, and how
/// much of the target track it accounts for.
/// </summary>
/// <param name="Quality">The <see cref="QualityOfFit"/> score, 0..1.</param>
/// <param name="Peak">The curve's value at its maximum (a sum of kernel terms, so at most the cue count).</param>
/// <param name="PeakShiftMs">The shift the peak sits at, in milliseconds.</param>
/// <param name="MatchFraction">Peak divided by the target's cue count: how many cues the peak explains.</param>
/// <param name="Trusted">Whether the score reached <see cref="QualityOfFit.DefaultThreshold"/>.</param>
internal readonly record struct RulerShape(
    double Quality,
    double Peak,
    long PeakShiftMs,
    double MatchFraction,
    bool Trusted)
{
    /// <summary>One line, for the plugin log.</summary>
    /// <returns>The description.</returns>
    internal string Describe()
        => $"quality={Quality:0.000} peak={Peak:0} at {PeakShiftMs:+#;-#;0} ms "
           + $"covering {MatchFraction * 100:0.#}% of the cues (threshold {QualityOfFit.DefaultThreshold:0.00})";
}

/// <summary>
/// Scores how well a subtitle ruler and the subtitle being synced agree — the shape of their
/// shift-score curve — using the <see cref="QualityOfFit"/> metrics (a fresh C# reimplementation of
/// oseiskar/autosubsync's <c>quality_of_fit.py</c>, MIT).
/// </summary>
/// <remarks>
/// This is the gate in front of the audio cross-check: a ruler the two subtitle tracks agree on
/// cleanly is trusted without reading the film, and anything else is cross-checked against the audio
/// exactly as before.
///
/// **What it cannot see, and why the cross-check stays the authority**: the score is built from the
/// two subtitle tracks only, so it is blind to a ruler that is a plain *offset* from the film while
/// matching the target's shape — a same-cut track from a version with a longer intro correlates
/// perfectly and scores as high as a correct ruler. The screenshots and the probe for this are in
/// <c>docs/EVIDENCE_s31_quality_of_fit.md</c>; the audio cross-check is what catches that case, which
/// is why the gate must never be the only check on a ruler that asks for a large shift.
/// </remarks>
internal static class SubtitleRulerShape
{
    /// <summary>Shift resolution of the curve, in seconds (the engine's own answers are 10 ms steps).</summary>
    internal const double StepSeconds = 0.1;

    /// <summary>Width of the kernel that turns cue hits into a smooth curve, in seconds.</summary>
    internal const double KernelSigmaSeconds = 0.5;

    /// <summary>How wide the peak-search window is, in seconds (as in the source's <c>find_max_window</c>).</summary>
    internal const double PeakWindowSeconds = 2.0;

    /// <summary>How wide the non-edgeness window is, in seconds (the source uses 0.5).</summary>
    internal const double NonEdgeWindowSeconds = 0.5;

    /// <summary>
    /// Scores a ruler against the subtitle being synced.
    /// </summary>
    /// <param name="targetPath">The subtitle the engine aligned (its input).</param>
    /// <param name="rulerPath">The reference subtitle handed to the engine.</param>
    /// <param name="maxShiftSeconds">Half-width of the shift range to search, in seconds.</param>
    /// <param name="threshold">The score at or above which the ruler is trusted.</param>
    /// <returns>The score, or null when the files cannot be read as subtitle tracks.</returns>
    internal static RulerShape? Score(
        string targetPath,
        string rulerPath,
        double maxShiftSeconds,
        double threshold = QualityOfFit.DefaultThreshold)
    {
        var target = SubSyncService.ParseSrtCueStarts(targetPath);
        var ruler = SubSyncService.ParseSrtCueStarts(rulerPath);
        if (target is null || ruler is null || target.Count < 3 || ruler.Count < 3)
        {
            return null;
        }

        var range = Math.Max(StepSeconds * 2, maxShiftSeconds);
        var samples = (int)Math.Round((range * 2) / StepSeconds) + 1;
        var curve = new double[samples];
        var rulerSorted = ruler.OrderBy(x => x).ToArray();

        for (var i = 0; i < samples; i++)
        {
            // ParseSrtCueStarts returns seconds, so the shift is seconds too.
            var shift = -range + (i * StepSeconds);
            var sum = 0.0;
            foreach (var cueSeconds in target)
            {
                var want = cueSeconds + shift;
                var d = NearestDistance(rulerSorted, want);
                sum += Math.Exp(-((d / KernelSigmaSeconds) * (d / KernelSigmaSeconds)));
            }

            curve[i] = sum;
        }

        var peak = curve.Max();
        var peakIndex = Array.IndexOf(curve, peak);
        var peakShiftMs = (long)Math.Round((-range + (peakIndex * StepSeconds)) * 1000);

        var quality = QualityOfFit.ComputeQuality(
            curve,
            windowHalfSizeSamples: Math.Max(1, (int)Math.Round(PeakWindowSeconds / StepSeconds)),
            nonEdgeHalfSizeSamples: Math.Max(1, (int)Math.Round(NonEdgeWindowSeconds / StepSeconds)));

        return new RulerShape(
            quality,
            peak,
            peakShiftMs,
            target.Count == 0 ? 0 : peak / target.Count,
            quality >= threshold);
    }

    private static double NearestDistance(double[] sorted, double value)
    {
        var index = Array.BinarySearch(sorted, value);
        if (index >= 0)
        {
            return 0;
        }

        index = ~index;
        var best = double.MaxValue;
        if (index < sorted.Length)
        {
            best = Math.Abs(sorted[index] - value);
        }

        if (index > 0)
        {
            var previous = Math.Abs(sorted[index - 1] - value);
            best = Math.Min(best, previous);
        }

        return best == double.MaxValue ? double.MaxValue : best;
    }
}
