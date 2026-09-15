using System.Text.Json;

// Runs the plugin's real QualityOfFit over the S31 fixture's real shift-score curves.
// Input: JSON from tests/backend/s31_quality_curves.py ({variants:{name:{shifts:[],scores:[]}}})
// Output: one line per variant with the three metrics and the quality score, plus the source's
// threshold verdict (0.75), and the same for the curve with its peak removed (a null test).
var path = args.Length > 0 ? args[0] : "/tmp/s31-curves.json";
using var doc = JsonDocument.Parse(File.ReadAllText(path));
var variants = doc.RootElement.GetProperty("variants");

Console.WriteLine($"{"variant",-12} {"peak",6} {"peak@s",8} {"mono",6} {"prom",6} {"nonedge",8} {"quality",8}  verdict");

foreach (var v in variants.EnumerateObject())
{
    var scores = v.Value.GetProperty("scores").EnumerateArray().Select(e => e.GetDouble()).ToArray();
    var shifts = v.Value.GetProperty("shifts").EnumerateArray().Select(e => e.GetDouble()).ToArray();
    var peak = v.Value.GetProperty("peak").GetDouble();
    var peakShift = v.Value.GetProperty("peak_shift").GetDouble();

    var mono = Jellyfin.Plugin.SubSync.Services.QualityOfFit.MetricPeakMonotonicity(scores, 20);
    var prom = Jellyfin.Plugin.SubSync.Services.QualityOfFit.MetricPeakProminence(scores, 20);
    var edge = Jellyfin.Plugin.SubSync.Services.QualityOfFit.MetricNonEdgeness(scores, 5);
    var quality = Jellyfin.Plugin.SubSync.Services.QualityOfFit.ComputeQuality(scores, 20, 5);
    var verdict = quality >= Jellyfin.Plugin.SubSync.Services.QualityOfFit.DefaultThreshold
        ? "TRUSTWORTHY (>= 0.75)" : "REJECTED (< 0.75)";

    Console.WriteLine($"{v.Name,-12} {peak,6:0} {peakShift,8:+0.0;-0.0} {mono,6:0.000} {prom,6:0.000} {edge,8:0.000} {quality,8:0.000}  {verdict}");

    // Null test on the same curve: flatten the peak's own column, so the shape is a ridge
    // rather than a point. A score that does not fall here is not measuring the peak.
    var flattened = (double[])scores.Clone();
    var bestIndex = Array.IndexOf(scores, scores.Max());
    for (var i = Math.Max(0, bestIndex - 2); i <= Math.Min(scores.Length - 1, bestIndex + 2); i++)
    {
        flattened[i] = scores.Average();
    }

    var flatQuality = Jellyfin.Plugin.SubSync.Services.QualityOfFit.ComputeQuality(flattened, 20, 5);
    Console.WriteLine($"{"",-12} {"",6} {"",8} {"",6} {"",6} {"",8} {flatQuality,8:0.000}  (peak flattened: null test)");
}
