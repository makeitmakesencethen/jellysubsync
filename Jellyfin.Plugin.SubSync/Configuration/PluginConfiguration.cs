using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubSync.Configuration;

/// <summary>
/// Plugin configuration for SubSync.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{

    /// <summary>
    /// Gets or sets the configuration revision this file was last migrated to. Used to move
    /// existing installs onto newer defaults exactly once.
    /// </summary>
    public int ConfigVersion { get; set; }

    /// <summary>
    /// Gets or sets how long a single embedded-subtitle extraction may take before it is
    /// aborted with a clear error (minutes). Guards against a stuck or pathologically slow
    /// read looking like a hang in the UI.
    /// </summary>
    public int ExtractionTimeoutMinutes { get; set; } = 20;

    /// <summary>
    /// Gets or sets how multiple subtitles of the same media file are synced.
    /// <c>normal</c> = one at a time, audio analysed every run;
    /// <c>parallel</c> = up to <see cref="ParallelWorkers"/> subtitles at once;
    /// <c>fast</c> = reuse the speech analysis of the first run for the remaining
    /// subtitles of that file (identical results, ~3x less work per extra subtitle).
    /// </summary>
    public string MultiSyncMode { get; set; } = "normal";

    /// <summary>
    /// Gets or sets the worker count used by the parallel strategies (1-64).
    /// Each worker is CPU-bound and holds a few hundred MB while analysing audio,
    /// so keep this low on slow storage or a small server.
    /// </summary>
    public int ParallelWorkers { get; set; } = 4;

    /// <summary>
    /// Gets or sets the subtitle languages that may be synced. Empty = every language.
    /// Tracks in other languages are not listed and not synced, and tracks with no
    /// language at all are skipped while a filter is set (they cannot be matched).
    /// </summary>
    public string[] SyncLanguages { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets the path to the ffsubsync executable.
    /// </summary>
    public string FfSubSyncPath { get; set; } = "ffsubsync";

    /// <summary>
    /// Gets or sets the path to the ffmpeg executable (passed via --ffmpeg-path).
    /// Leave empty to use system PATH.
    /// </summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the voice activity detector to use.
    /// </summary>
    public string VadMethod { get; set; } = "subs_then_webrtc";

    /// <summary>
    /// Gets or sets the maximum allowed offset in seconds for any subtitle segment.
    /// </summary>
    public int MaxOffsetSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets how far (in seconds) a subtitle may be moved by an alignment that was taken from
    /// another subtitle track of the same file.
    /// </summary>
    /// <remarks>
    /// A subtitle reference is exact when it is the same cut, and catastrophically wrong when it is
    /// not: a reference from a different release drags every subtitle of the file onto it, and the
    /// result is a plausible-looking sidecar, not an error. Measured on this project's own fixture:
    /// the forced track of a 49-minute episode was "aligned" to a sibling at -59 080 ms and written as
    /// a success. A shift beyond this limit is therefore refused with the measured numbers, and the
    /// audio is the way to sync a file whose tracks are not from the same cut.
    /// </remarks>
    public double MaxSubtitleReferenceOffsetSeconds { get; set; } = 30.0;

    /// <summary>
    /// Gets or sets the output encoding for synced subtitles.
    /// </summary>
    public string OutputEncoding { get; set; } = "utf-8";

    /// <summary>
    /// Gets or sets the maximum duration in seconds for a subtitle to appear on-screen.
    /// </summary>
    public double MaxSubtitleSeconds { get; set; } = 10.0;

    /// <summary>
    /// Gets or sets a value indicating whether ffsubsync may rescale subtitle timings to correct a
    /// framerate mismatch between the video and the subtitles.
    /// </summary>
    /// <remarks>
    /// Off by default, deliberately. ffsubsync 0.5.1 infers a framerate ratio from the ratio between
    /// the reference duration and the subtitle's own span, and rescales the whole file unless it is
    /// told not to. A subtitle whose last cue sits a few percent outside the video - very common, from
    /// a release with a longer credits roll, or an extra scene - therefore reads as a framerate
    /// mismatch. Measured with the bundled engine on a 45-minute file: a span 4.17% too long became a
    /// 0.960x scale, a -51.9 s shift and -104 s of drift, and the log called it "framerate ratio
    /// 1.0427x" rather than saying the subtitle had been time-scaled. Off means offsets only, bounded
    /// by <see cref="MaxOffsetSeconds"/>. Turn it on only for subtitles known to come from a different
    /// framerate; results that are not a real framerate pair are then refused rather than written.
    /// </remarks>
    public bool FixFramerate { get; set; } = false;

    /// <summary>
    /// Gets or sets whether to use golden-section search for optimal framerate ratio.
    /// </summary>
    public bool UseGoldenSectionSearch { get; set; } = false;

    /// <summary>
    /// Gets or sets whether sync output is written as a NEW file ("Name -SYNCED.srt",
    /// original subtitle untouched) instead of replacing the original in place.
    /// Copy mode is the default so users never lose their original subtitles.
    /// </summary>
    public bool SyncModeCopy { get; set; } = true;

    /// <summary>
    /// Gets or sets how many consecutive execution failures a subtitle may have
    /// during library sweeps before the sweep stops retrying it (its file content
    /// changing resets the streak). A failed manual sync does not count.
    /// </summary>
    public int SweepFailStreakLimit { get; set; } = 3;

    /// <summary>
    /// Gets or sets the maximum number of subtitle tracks one library sweep run will queue.
    /// </summary>
    public int SweepMaxItemsPerRun { get; set; } = 500;
}
