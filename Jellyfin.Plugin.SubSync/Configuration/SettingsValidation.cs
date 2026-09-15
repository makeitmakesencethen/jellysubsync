using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.SubSync.Configuration;

/// <summary>
/// The one place a setting is checked: before it is stored, and again before it reaches the engine's argv.
/// </summary>
/// <remarks>
/// Two findings, one cause (D3 and F10 in the audit of 2026-09-11). A value that cannot mean anything was stored
/// silently - a negative or absurd offset ceiling, a typo'd encoding, a binary path that does not exist, a
/// language tag that matches nothing - and the page then said "Saved." for it, because a clamp (99 workers became
/// 64) was applied and never mentioned. Separately, the numbers written straight into argv were never re-checked
/// at the point of use, so a hand-edited config.xml could hand the engine <c>--max-offset-seconds -5</c> and turn
/// the plugin's own "was the engine clamped?" warning into a constant banner, hiding real clamping.
/// <para>
/// So: <see cref="Apply"/> is called wherever a configuration is stored and returns what it adjusted, which the
/// settings page shows; the <c>*Of</c> accessors are what argv and the plugin's own heuristics read, so the engine
/// can never be given a value outside these ranges whatever the stored file says.
/// </para>
/// </remarks>
public static class SettingsValidation
{
    /// <summary>Smallest offset ceiling that leaves the plugin's own ceiling warning room to mean anything.</summary>
    public const int MaxOffsetSecondsMin = 1;

    /// <summary>Largest offset ceiling; the settings page offers the same bounds.</summary>
    public const int MaxOffsetSecondsMax = 600;

    /// <summary>Smallest shift allowed from a subtitle reference.</summary>
    public const double MaxSubtitleReferenceOffsetSecondsMin = 1;

    /// <summary>Largest shift allowed from a subtitle reference.</summary>
    public const double MaxSubtitleReferenceOffsetSecondsMax = 600;

    /// <summary>Smallest cue length ffsubsync will be told to accept.</summary>
    public const double MaxSubtitleSecondsMin = 1;

    /// <summary>Largest cue length ffsubsync will be told to accept.</summary>
    public const double MaxSubtitleSecondsMax = 60;

    /// <summary>Smallest number of extraction lanes.</summary>
    public const int ParallelWorkersMin = 1;

    /// <summary>Largest number of extraction lanes.</summary>
    public const int ParallelWorkersMax = 64;

    /// <summary>Shortest extraction timeout, in minutes.</summary>
    public const int ExtractionTimeoutMinutesMin = 1;

    /// <summary>Longest extraction timeout, in minutes.</summary>
    public const int ExtractionTimeoutMinutesMax = 240;

    /// <summary>Shortest window in which a running job may show no activity at all.</summary>
    public const int StuckJobTimeoutMinutesMin = 2;

    /// <summary>Longest window in which a running job may show no activity at all.</summary>
    public const int StuckJobTimeoutMinutesMax = 240;

    /// <summary>Shortest window a live process may stay silent in, for the wedged-process rule.</summary>
    public const int WedgedProcessTimeoutMinutesMin = 5;

    /// <summary>Longest window a live process may stay silent in, for the wedged-process rule.</summary>
    public const int WedgedProcessTimeoutMinutesMax = 1440;

    /// <summary>Smallest sweep failure streak the sweep will honour.</summary>
    public const int SweepFailStreakLimitMin = 1;

    /// <summary>Largest sweep failure streak the sweep will honour.</summary>
    public const int SweepFailStreakLimitMax = 50;

    /// <summary>Smallest number of items one sweep will take on.</summary>
    public const int SweepMaxItemsPerRunMin = 1;

    /// <summary>Largest number of items one sweep will take on.</summary>
    public const int SweepMaxItemsPerRunMax = 100000;

    /// <summary>Lowest split penalty the engine accepts (0 = off, a single global offset).</summary>
    public const double SplitPenaltyMin = 0;

    /// <summary>Highest split penalty worth handing over; the engine's own typical range is 4-20.</summary>
    public const double SplitPenaltyMax = 50;

    private static readonly Lazy<HashSet<string>> KnownLanguageTags = new(BuildKnownLanguageTags);

    /// <summary>Reads the offset ceiling the engine and the plugin's own heuristics are allowed to see.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The stored value, brought into range.</returns>
    public static int MaxOffsetSecondsOf(PluginConfiguration config)
        => Clamp(config.MaxOffsetSeconds, MaxOffsetSecondsMin, MaxOffsetSecondsMax);

    /// <summary>Reads the cue-length limit the engine is allowed to see.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The stored value, brought into range.</returns>
    public static double MaxSubtitleSecondsOf(PluginConfiguration config)
        => Clamp(config.MaxSubtitleSeconds, MaxSubtitleSecondsMin, MaxSubtitleSecondsMax);

    /// <summary>Reads the reference-shift ceiling the plugin's own checks are allowed to see.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The stored value, brought into range.</returns>
    public static double MaxSubtitleReferenceOffsetSecondsOf(PluginConfiguration config)
        => Clamp(config.MaxSubtitleReferenceOffsetSeconds, MaxSubtitleReferenceOffsetSecondsMin,
                 MaxSubtitleReferenceOffsetSecondsMax);

    /// <summary>Reads the split penalty the engine is allowed to be given.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The penalty in seconds of overlap, or 0 for a single global offset.</returns>
    public static double SplitPenaltyOf(PluginConfiguration config)
        => double.IsNaN(config.SplitPenalty)
            ? 0
            : Clamp(config.SplitPenalty, SplitPenaltyMin, SplitPenaltyMax);

    /// <summary>Reads the window in which a running job may show no activity at all.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The stored value, brought into range.</returns>
    public static int StuckJobTimeoutOf(PluginConfiguration config)
        => Clamp(config.StuckJobTimeoutMinutes, StuckJobTimeoutMinutesMin, StuckJobTimeoutMinutesMax);

    /// <summary>Reads the window in which a live process may stay silent before its job is treated as wedged.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The stored value, brought into range.</returns>
    public static int WedgedProcessTimeoutOf(PluginConfiguration config)
        => Clamp(config.WedgedProcessTimeoutMinutes, WedgedProcessTimeoutMinutesMin, WedgedProcessTimeoutMinutesMax);

    /// <summary>Reads the output encoding, falling back to utf-8 for anything the engine cannot be given.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>One of the encodings the engine accepts.</returns>
    public static string OutputEncodingOf(PluginConfiguration config)
        => Services.SubSyncService.AllowedOutputEncodings.Contains(config.OutputEncoding ?? string.Empty)
            ? config.OutputEncoding!
            : "utf-8";

    /// <summary>Reads the VAD method, falling back to the engine's own default.</summary>
    /// <param name="config">The stored configuration.</param>
    /// <returns>One of the VAD methods the engine accepts.</returns>
    public static string VadMethodOf(PluginConfiguration config)
        => Services.SubSyncService.AllowedVadMethods.Contains(config.VadMethod ?? string.Empty)
            ? config.VadMethod!
            : "subs_then_webrtc";

    /// <summary>Checks a configured binary path, treating a bare name (resolved from PATH or the bundle) as fine.</summary>
    /// <param name="path">The configured path.</param>
    /// <returns>True when the value is empty, a bare name, or a file that exists on this server.</returns>
    public static bool BinaryPathIsUsable(string? path)
        => string.IsNullOrWhiteSpace(path)
           || path.IndexOf(Path.DirectorySeparatorChar) < 0
           || File.Exists(path);

    /// <summary>Checks a language tag against the languages this plugin is asked to sync.</summary>
    /// <param name="tag">The tag as typed.</param>
    /// <param name="normalized">The tag as it is stored, when it is one.</param>
    /// <returns>True when the tag names a language or a classic ISO 639-2 alternate.</returns>
    public static bool IsKnownLanguageTag(string? tag, out string normalized)
    {
        normalized = (tag ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
        if (normalized.Length == 0)
        {
            return false;
        }

        var parts = normalized.Split('-');
        if (parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsLetter))
        {
            return false;
        }

        return KnownLanguageTags.Value.Contains(parts[0]);
    }

    /// <summary>
    /// Brings a configuration into range, in place, and says what it changed.
    /// </summary>
    /// <remarks>
    /// Called wherever a configuration is stored, so a value that cannot mean anything is never the value the
    /// plugin runs on, and the page can report every adjustment instead of a bare "Saved.".
    /// </remarks>
    /// <param name="config">The configuration to bring into range.</param>
    /// <returns>One short line per adjustment, empty when nothing was adjusted.</returns>
    public static IReadOnlyList<string> Apply(PluginConfiguration config)
    {
        var notes = new List<string>();

        var workers = Clamp(config.ParallelWorkers, ParallelWorkersMin, ParallelWorkersMax);
        if (workers != config.ParallelWorkers)
        {
            notes.Add($"Parallel workers: {config.ParallelWorkers} is outside "
                      + $"{ParallelWorkersMin}-{ParallelWorkersMax}; {workers} is stored");
            config.ParallelWorkers = workers;
        }

        var timeout = Clamp(config.ExtractionTimeoutMinutes, ExtractionTimeoutMinutesMin,
                            ExtractionTimeoutMinutesMax);
        if (timeout != config.ExtractionTimeoutMinutes)
        {
            notes.Add($"Extraction timeout: {config.ExtractionTimeoutMinutes} minutes is outside "
                      + $"{ExtractionTimeoutMinutesMin}-{ExtractionTimeoutMinutesMax}; {timeout} is stored");
            config.ExtractionTimeoutMinutes = timeout;
        }

        // Golden-section search is only ever handed to the engine together with framerate correction
        // (`FramerateArgs` emits `--gss` only when correction is on), so a tick without it is a control that
        // does nothing - which is what it looked like: "an inert checkbox misleads" (F12). The value is kept
        // (the page shows it, and turning the correction back on restores what it was set to) and the page is
        // told it is doing nothing meanwhile.
        if (config.UseGoldenSectionSearch && !config.FixFramerate)
        {
            notes.Add("Golden-section search: it only does anything together with \"Correct framerate mismatch\", "
                      + "which is off; it is stored but the engine is not given --gss");
        }

        var stuck = StuckJobTimeoutOf(config);
        if (stuck != config.StuckJobTimeoutMinutes)
        {
            notes.Add($"Stuck-job timeout: {config.StuckJobTimeoutMinutes} minutes is outside "
                      + $"{StuckJobTimeoutMinutesMin}-{StuckJobTimeoutMinutesMax}; {stuck} is stored");
            config.StuckJobTimeoutMinutes = stuck;
        }

        var wedged = WedgedProcessTimeoutOf(config);
        if (wedged != config.WedgedProcessTimeoutMinutes)
        {
            notes.Add($"Wedged-process timeout: {config.WedgedProcessTimeoutMinutes} minutes is outside "
                      + $"{WedgedProcessTimeoutMinutesMin}-{WedgedProcessTimeoutMinutesMax}; {wedged} is stored");
            config.WedgedProcessTimeoutMinutes = wedged;
        }

        if (double.IsNaN(config.MaxSubtitleSeconds))
        {
            notes.Add($"Max subtitle seconds: '{Fmt(config.MaxSubtitleSeconds)}' is not a number; 10 is stored");
            config.MaxSubtitleSeconds = 10;
        }

        if (double.IsNaN(config.MaxSubtitleReferenceOffsetSeconds))
        {
            notes.Add("Max shift from a subtitle reference: not a number; 30 is stored");
            config.MaxSubtitleReferenceOffsetSeconds = 30;
        }

        var ceiling = MaxOffsetSecondsOf(config);
        if (ceiling != config.MaxOffsetSeconds)
        {
            notes.Add($"Max offset seconds: {config.MaxOffsetSeconds} is outside "
                      + $"{MaxOffsetSecondsMin}-{MaxOffsetSecondsMax}; {ceiling} is stored");
            config.MaxOffsetSeconds = ceiling;
        }

        var reference = MaxSubtitleReferenceOffsetSecondsOf(config);
        if (!reference.Equals(config.MaxSubtitleReferenceOffsetSeconds))
        {
            notes.Add($"Max shift from a subtitle reference: {Fmt(config.MaxSubtitleReferenceOffsetSeconds)} is "
                      + $"outside {MaxSubtitleReferenceOffsetSecondsMin:0.#}-{MaxSubtitleReferenceOffsetSecondsMax:0.#}; "
                      + $"{Fmt(reference)} is stored");
            config.MaxSubtitleReferenceOffsetSeconds = reference;
        }

        var cue = MaxSubtitleSecondsOf(config);
        if (!cue.Equals(config.MaxSubtitleSeconds))
        {
            notes.Add($"Max subtitle seconds: {Fmt(config.MaxSubtitleSeconds)} is outside "
                      + $"{MaxSubtitleSecondsMin:0.#}-{MaxSubtitleSecondsMax:0.#}; {Fmt(cue)} is stored");
            config.MaxSubtitleSeconds = cue;
        }

        var streak = Clamp(config.SweepFailStreakLimit, SweepFailStreakLimitMin, SweepFailStreakLimitMax);
        if (streak != config.SweepFailStreakLimit)
        {
            notes.Add($"Sweep fail streak limit: {config.SweepFailStreakLimit} is outside "
                      + $"{SweepFailStreakLimitMin}-{SweepFailStreakLimitMax}; {streak} is stored");
            config.SweepFailStreakLimit = streak;
        }

        var sweep = Clamp(config.SweepMaxItemsPerRun, SweepMaxItemsPerRunMin, SweepMaxItemsPerRunMax);
        if (sweep != config.SweepMaxItemsPerRun)
        {
            notes.Add($"Sweep items per run: {config.SweepMaxItemsPerRun} is outside "
                      + $"{SweepMaxItemsPerRunMin}-{SweepMaxItemsPerRunMax}; {sweep} is stored");
            config.SweepMaxItemsPerRun = sweep;
        }

        var penalty = SplitPenaltyOf(config);
        if (!penalty.Equals(config.SplitPenalty))
        {
            var why = double.IsNaN(config.SplitPenalty) ? "is not a number" : "is outside";
            notes.Add($"Split penalty: {Fmt(config.SplitPenalty)} {why} "
                      + $"{Fmt(SplitPenaltyMin)}-{Fmt(SplitPenaltyMax)}; {Fmt(penalty)} is stored (0 is a single "
                      + "global offset)");
            config.SplitPenalty = penalty;
        }

        var encoding = OutputEncodingOf(config);
        if (!string.Equals(encoding, config.OutputEncoding, StringComparison.Ordinal))
        {
            notes.Add($"Output encoding: '{config.OutputEncoding}' is not one of "
                      + $"{string.Join(", ", Services.SubSyncService.AllowedOutputEncodings.OrderBy(x => x))}; "
                      + $"{encoding} is stored");
            config.OutputEncoding = encoding;
        }

        var vad = VadMethodOf(config);
        if (!string.Equals(vad, config.VadMethod, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"VAD method: '{config.VadMethod}' is not one of "
                      + $"{string.Join(", ", Services.SubSyncService.AllowedVadMethods.OrderBy(x => x))}; "
                      + $"{vad} is stored");
            config.VadMethod = vad;
        }

        if (!BinaryPathIsUsable(config.FfSubSyncPath))
        {
            notes.Add($"ffsubsync executable: '{config.FfSubSyncPath}' does not exist on this server; the bundled "
                      + "build is used instead");
            config.FfSubSyncPath = "ffsubsync";
        }

        if (!BinaryPathIsUsable(config.FfmpegPath))
        {
            notes.Add($"ffmpeg path: '{config.FfmpegPath}' does not exist on this server; the server's own ffmpeg "
                      + "is used instead");
            config.FfmpegPath = string.Empty;
        }

        var languages = config.SyncLanguages ?? Array.Empty<string>();
        var kept = new List<string>();
        var dropped = new List<string>();
        foreach (var tag in languages)
        {
            if (IsKnownLanguageTag(tag, out var normalized))
            {
                if (!kept.Contains(normalized, StringComparer.Ordinal))
                {
                    kept.Add(normalized);
                }
            }
            else if (!string.IsNullOrWhiteSpace(tag))
            {
                dropped.Add(tag.Trim());
            }
        }

        if (dropped.Count > 0)
        {
            notes.Add($"Only sync these languages: dropped {string.Join(", ", dropped.Select(d => $"'{d}'"))}, "
                      + $"which {(dropped.Count == 1 ? "is not a language tag" : "are not language tags")}"
                      + (kept.Count == 0 && languages.Length > 0
                          ? " - with none left, every language is synced"
                          : string.Empty));
        }

        if (dropped.Count > 0 || !kept.SequenceEqual(languages, StringComparer.Ordinal))
        {
            config.SyncLanguages = kept.ToArray();
        }

        return notes;
    }

    /// <summary>Formats a number for a note the way the settings page shows it.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The value, without a trailing zero tail where it has none.</returns>
    private static string Fmt(double value)
        => double.IsNaN(value) ? "not a number" : value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Brings a value into range.</summary>
    /// <param name="value">The value.</param>
    /// <param name="min">Inclusive minimum.</param>
    /// <param name="max">Inclusive maximum.</param>
    /// <returns>The value, brought into range.</returns>
    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>Brings a value into range, treating a non-number as the minimum.</summary>
    /// <param name="value">The value.</param>
    /// <param name="min">Inclusive minimum.</param>
    /// <param name="max">Inclusive maximum.</param>
    /// <returns>The value, brought into range.</returns>
    private static double Clamp(double value, double min, double max)
        => double.IsNaN(value) ? min : value < min ? min : value > max ? max : value;

    /// <summary>
    /// Builds the set of language tags this plugin accepts, from the cultures the server knows.
    /// </summary>
    /// <remarks>
    /// A typo'd tag used to be stored and then silently match nothing, so nothing was synced and nothing said so.
    /// The classic ISO 639-2/B codes are kept as well, because a container's metadata may carry those while the
    /// culture tables name the 639-2/T form (a German track is `ger` in one and `deu` in the other).
    /// </remarks>
    /// <returns>The accepted tags.</returns>
    private static HashSet<string> BuildKnownLanguageTags()
    {
        // The tags media containers carry, spelled out rather than looked up: this list has to be the same on
        // every server, and a host without globalization data must not quietly reject every language it is given.
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Two-letter ISO 639-1, and the same languages as three-letter ISO 639-2/T; both spellings of a
            // language are accepted because the tags come from the container and the plugin may be asked for
            // either.
            "en", "eng", "sv", "swe", "no", "nor", "nb", "nob", "nn", "nno", "da", "dan", "fi", "fin", "is", "ice",
            "de", "deu", "ger", "fr", "fra", "fre", "es", "spa", "it", "ita", "pt", "por", "nl", "nld", "dut",
            "pl", "pol", "cs", "ces", "cze", "sk", "slk", "slo", "hu", "hun", "ro", "ron", "rum", "bg", "bul",
            "el", "ell", "gre", "tr", "tur", "ru", "rus", "uk", "ukr", "be", "bel", "sr", "srp", "hr", "hrv",
            "bs", "bos", "sl", "slv", "mk", "mkd", "mac", "sq", "sqi", "alb", "et", "est", "lv", "lav", "lt", "lit",
            "ar", "ara", "he", "heb", "fa", "fas", "per", "ur", "urd", "hi", "hin", "bn", "ben", "ta", "tam",
            "te", "tel", "ml", "mal", "kn", "kan", "mr", "mar", "gu", "guj", "pa", "pan", "si", "sin", "ne", "nep",
            "th", "tha", "vi", "vie", "id", "ind", "ms", "msa", "may", "tl", "tgl", "fil", "ja", "jpn", "ko", "kor",
            "zh", "zho", "chi", "yue", "ca", "cat", "gl", "glg", "eu", "eus", "baq", "cy", "cym", "wel", "ga", "gle",
            "gd", "gla", "mt", "mlt", "af", "afr", "sw", "swa", "am", "amh", "ha", "hau", "yo", "yor", "zu", "zul",
            "xh", "xho", "hy", "hye", "arm", "ka", "kat", "geo", "az", "aze", "kk", "kaz", "ky", "kir", "uz", "uzb",
            "tg", "tgk", "mn", "mon", "my", "mya", "bur", "km", "khm", "lo", "lao", "bo", "bod", "tib", "ps", "pus",
            "ku", "kur", "so", "som", "ti", "tir", "rw", "kin", "sn", "sna", "ny", "nya", "st", "sot", "tn", "tsn",
            "ss", "ssw", "ve", "ven", "ts", "tso", "eo", "epo", "la", "lat", "haw", "mi", "mri", "mao", "sm", "smo",
            "fj", "fij", "to", "ton", "ty", "tah", "qu", "que", "gn", "grn", "ay", "aym", "nv", "nav", "iu", "iku",
            "se", "sme", "sma", "smj", "kl", "kal", "fo", "fao", "fy", "fry", "lb", "ltz", "rm", "roh", "oc", "oci",
            "br", "bre", "co", "cos", "sc", "srd", "an", "arg", "ast", "ceb", "jv", "jav", "su", "sun", "mg", "mlg",
            "yi", "yid", "sah", "ce", "che", "tt", "tat", "ba", "bak", "cv", "chv", "os", "oss", "ab", "abk",
            "av", "ava", "kv", "kom", "udm", "mhr", "myv", "ceb", "war", "hil", "pam", "ilo", "bik", "ch", "cha"
        };

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (!string.IsNullOrEmpty(culture.TwoLetterISOLanguageName)
                && !string.Equals(culture.TwoLetterISOLanguageName, "iv", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(culture.TwoLetterISOLanguageName);
            }

            if (!string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName))
            {
                tags.Add(culture.ThreeLetterISOLanguageName);
            }
        }

        return tags;
    }
}
