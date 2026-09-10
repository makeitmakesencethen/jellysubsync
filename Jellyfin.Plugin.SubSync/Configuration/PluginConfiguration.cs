using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SubSync.Configuration;

/// <summary>
/// Plugin configuration for SubSync.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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
    /// Gets or sets the output encoding for synced subtitles.
    /// </summary>
    public string OutputEncoding { get; set; } = "utf-8";

    /// <summary>
    /// Gets or sets the maximum duration in seconds for a subtitle to appear on-screen.
    /// </summary>
    public double MaxSubtitleSeconds { get; set; } = 10.0;

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
