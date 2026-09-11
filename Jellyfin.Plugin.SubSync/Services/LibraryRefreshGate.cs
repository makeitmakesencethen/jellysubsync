using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Decides when a Jellyfin item needs refreshing again after a synced subtitle was written.
///
/// Two different jobs need two different treatments after a sync:
///
/// <list type="bullet">
/// <item><description>The folder report (<c>ILibraryMonitor.ReportFileSystemChanged</c>) is what makes
/// Jellyfin notice the new file. It is cheap, Jellyfin coalesces repeats internally, and skipping one
/// risks leaving a written file invisible until the next scan — so it is never suppressed here.</description></item>
/// <item><description>The item refresh (<c>ILibraryManager.UpdateItemAsync</c>) re-probes the media file
/// to rebuild its stream list. That is the expensive part and it is identical for every track of the same
/// episode, so this gate lets it run once per item per window instead of once per subtitle.</description></item>
/// </list>
///
/// A window rather than a "once per run" flag: a single episode can be synced again an hour later, and
/// then the refresh must happen again.
/// </summary>
public sealed class LibraryRefreshGate
{
    /// <summary>
    /// How long a refresh of the same item is considered recent enough to skip.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;

    /// <summary>Item id to the time it was last refreshed.</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _lastRefresh = new();

    private long _suppressed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryRefreshGate"/> class.
    /// </summary>
    /// <param name="window">Window in which repeat refreshes are skipped; defaults to one minute.</param>
    /// <param name="utcNow">Clock, injectable so the window can be tested without waiting.</param>
    public LibraryRefreshGate(TimeSpan? window = null, Func<DateTime>? utcNow = null)
    {
        _window = window ?? DefaultWindow;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Gets the number of item refreshes skipped as redundant, for diagnostics.
    /// </summary>
    public long SuppressedCount => Interlocked.Read(ref _suppressed);

    /// <summary>
    /// Gets the number of items currently tracked by the gate.
    /// </summary>
    public int TrackedItems => _lastRefresh.Count;

    /// <summary>
    /// Reports whether the item should be refreshed now, and records the decision.
    ///
    /// Safe to call from several workers at once: the first caller inside the window wins, the rest are
    /// told to skip, and no later-than-now timestamp can be written by a loser of the race.
    /// </summary>
    /// <param name="itemId">Jellyfin item (video) id.</param>
    /// <returns>True when the caller should perform the refresh.</returns>
    public bool ShouldRefresh(Guid itemId)
    {
        var now = _utcNow();

        while (true)
        {
            if (_lastRefresh.TryGetValue(itemId, out var last))
            {
                if (now - last < _window)
                {
                    Interlocked.Increment(ref _suppressed);
                    return false;
                }

                if (_lastRefresh.TryUpdate(itemId, now, last))
                {
                    return true;
                }

                // Another worker refreshed it while we compared; look again.
                continue;
            }

            if (_lastRefresh.TryAdd(itemId, now))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Drops items whose window has passed, so the map cannot grow with the whole library.
    /// </summary>
    /// <returns>The number of entries removed.</returns>
    public int Prune()
    {
        var now = _utcNow();
        var removed = 0;
        foreach (var pair in _lastRefresh)
        {
            if (now - pair.Value >= _window && _lastRefresh.TryRemove(pair.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}
