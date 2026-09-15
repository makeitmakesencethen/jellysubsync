using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// C# reimplementation of oseiskar/autosubsync's <c>quality_of_fit.py</c> (MIT):
/// the shape-based confidence score for a shift-score curve.
///
/// Faithful to the source's metric definitions:
/// <list type="bullet">
/// <item><c>find_peak</c> — around the maximum, walk outward while the curve stays above
///   <c>low + (high-low) * 0.2</c>, giving a rising side, a falling side and the two tails.</item>
/// <item><c>metric_peak_monotonicity</c> — the mean "is this step rising?" of the rising side
///   and of the reversed falling side; the weaker of the two.</item>
/// <item><c>metric_peak_prominence</c> — how far the peak stands above the tallest point of
///   each tail, as a fraction of the peak's own height; the weaker of the two sides.</item>
/// <item><c>metric_non_edgeness</c> — how far the maximum sits inside the search window,
///   <c>min(distance)/max(distance)</c>; 1 when the peak is not at an edge.</item>
/// </list>
/// <c>quality = monotonicity * prominence * non_edgeness</c>, threshold 0.75 (source lines 65, 70).
/// </summary>
/// <remarks>
/// Fresh C# on our conventions: pure arrays, no NumPy, no frame-rate constant from the source's
/// <c>features.py</c> (the caller states its window in samples). MIT source, attribution kept in
/// the class name and this comment.
/// </remarks>
public static class QualityOfFit
{
    /// <summary>The source's cross-validated threshold for calling a fit trustworthy.</summary>
    public const double DefaultThreshold = 0.75;

    /// <summary>How far above the low point a sample must sit to count as part of the peak.</summary>
    public const double PeakThresholdFraction = 0.2;

    /// <summary>Computes <c>monotonicity * prominence * non_edgeness</c> for one shift-score curve.</summary>
    /// <param name="shiftScores">Score per candidate shift, ascending in shift.</param>
    /// <param name="windowHalfSizeSamples">Half-width, in samples, of the window the peak lives in.</param>
    /// <param name="nonEdgeHalfSizeSamples">Half-width used by the non-edgeness metric (0.5 s in the source).</param>
    /// <returns>The quality score in 0..1.</returns>
    public static double ComputeQuality(
        double[] shiftScores,
        int windowHalfSizeSamples = 2,
        int nonEdgeHalfSizeSamples = 1)
    {
        return MetricPeakMonotonicity(shiftScores, windowHalfSizeSamples)
            * MetricPeakProminence(shiftScores, windowHalfSizeSamples)
            * MetricNonEdgeness(shiftScores, nonEdgeHalfSizeSamples);
    }

    /// <summary>Monotonicity of both flanks of the peak: the weaker side's mean rising fraction.</summary>
    /// <param name="shiftScores">Score per candidate shift.</param>
    /// <param name="windowHalfSizeSamples">Half-width of the window the peak lives in.</param>
    /// <returns>0..1.</returns>
    public static double MetricPeakMonotonicity(double[] shiftScores, int windowHalfSizeSamples = 2)
    {
        var (rising, falling, _, _) = FindPeak(shiftScores, windowHalfSizeSamples);
        var fallingReversed = falling.Reverse().ToArray();
        return Math.Min(MeanRaising(rising), MeanRaising(fallingReversed));
    }

    /// <summary>Prominence: the weaker flank's peak height over its tail's tallest point.</summary>
    /// <param name="shiftScores">Score per candidate shift.</param>
    /// <param name="windowHalfSizeSamples">Half-width of the window the peak lives in.</param>
    /// <returns>0..1.</returns>
    public static double MetricPeakProminence(double[] shiftScores, int windowHalfSizeSamples = 2)
    {
        var (rising, falling, beforePeak, afterPeak) = FindPeak(shiftScores, windowHalfSizeSamples);
        return Math.Min(OneSided(rising, beforePeak), OneSided(falling, afterPeak));
    }

    /// <summary>Non-edgeness: how far the maximum sits inside the search window.</summary>
    /// <param name="shiftScores">Score per candidate shift.</param>
    /// <param name="windowHalfSizeSamples">Half-width of the window the peak lives in.</param>
    /// <returns>0..1.</returns>
    public static double MetricNonEdgeness(double[] shiftScores, int windowHalfSizeSamples = 1)
    {
        if (shiftScores.Length == 0)
        {
            return 0;
        }

        var best = IndexOfMax(shiftScores);
        var wnd = Math.Max(1, windowHalfSizeSamples);
        var lowBound = Math.Max(0, best - wnd);
        var highBound = Math.Min(best + wnd + 1, shiftScores.Length);
        var left = best - lowBound;
        var right = highBound - best;
        var larger = Math.Max(left, right);
        return larger == 0 ? 0 : (double)Math.Min(left, right) / larger;
    }

    /// <summary>
    /// The source's <c>find_peak</c>: the rising flank, the falling flank, and the two tails
    /// outside the window the peak is searched in.
    /// </summary>
    /// <param name="x">Score per candidate shift.</param>
    /// <param name="windowHalfSizeSamples">Half-width of the peak-search window.</param>
    /// <returns>Rising flank, falling flank, tail before, tail after.</returns>
    public static (double[] Rising, double[] Falling, double[] BeforePeak, double[] AfterPeak) FindPeak(
        double[] x, int windowHalfSizeSamples = 2)
    {
        if (x.Length == 0)
        {
            return (Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>());
        }

        var best = IndexOfMax(x);
        var wnd = Math.Max(1, windowHalfSizeSamples);
        var lowBound = Math.Max(0, best - wnd);
        var highBound = Math.Min(best + wnd + 1, x.Length);

        var before = x[lowBound..(best + 1)];
        var after = x[best..highBound];

        // The source's cut: the lowest sample either side of the peak, plus 20 % of the peak's rise.
        var low = Math.Min(before.Length == 0 ? x[best] : before.Min(),
                           after.Length == 0 ? x[best] : after.Min());
        var high = before.Length == 0 ? x[best] : before[^1];
        var limit = low + ((high - low) * PeakThresholdFraction);

        var end = 0;
        while (end < after.Length && !(after[end] < limit))
        {
            end++;
        }

        if (end == 0)
        {
            end = after.Length;
        }

        var beginSteps = 0;
        while (beginSteps < before.Length && !(before[before.Length - 1 - beginSteps] < limit))
        {
            beginSteps++;
        }

        var begin = before.Length - beginSteps;

        var beforePeak = begin >= 0 && lowBound + begin <= x.Length
            ? x[..(lowBound + begin)]
            : Array.Empty<double>();
        var rising = before[begin..];
        var falling = after[..Math.Min(end, after.Length)];
        var afterPeak = begin + end <= x.Length
            ? x[(best + end)..]
            : Array.Empty<double>();

        return (rising, falling, beforePeak, afterPeak);
    }

    private static double MeanRaising(double[] vec)
    {
        if (vec.Length < 2)
        {
            return 0;
        }

        var rising = 0;
        for (var i = 0; i + 1 < vec.Length; i++)
        {
            if (vec[i + 1] > vec[i])
            {
                rising++;
            }
        }

        return (double)rising / (vec.Length - 1);
    }

    private static double OneSided(double[] peak, double[] others)
    {
        if (peak.Length == 0)
        {
            return 0;
        }

        if (others.Length == 0)
        {
            return 1.0;
        }

        var peakLow = peak.Min();
        var height = peak.Max() - peakLow;
        if (height <= 0)
        {
            return 0;
        }

        return Math.Min(1.0 - ((others.Max() - peakLow) / height), 1.0);
    }

    private static int IndexOfMax(double[] x)
    {
        var best = 0;
        for (var i = 1; i < x.Length; i++)
        {
            if (x[i] > x[best])
            {
                best = i;
            }
        }

        return best;
    }
}
