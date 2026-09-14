// <copyright file="VolumeProfile.cs" company="Jellyfin.Plugin.SubSync">
//     SubSync: fix or create subtitles for a video, entirely inside Jellyfin.
// </copyright>

using System.Globalization;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What one volume's storage has cost the reads this plugin has already made on it.
///
/// Why the profile is per volume rather than per pass
/// --------------------------------------------------
/// The read policy prices every decision - how big a window to read with, whether two reads are worth
/// merging into one - from what it has *measured*, and a pass measures only what it reads itself. That
/// makes the first plan of every pass a guess from the class defaults (0,05 ms per read, 1500 bytes per
/// millisecond), and it means the knowledge a pass collects is thrown away when it ends: a cue-indexed pass
/// cannot learn from the walk beside it, from a multi-track pass, or from the file synced five minutes ago
/// on the same share. Measured on the user's share, one read costs 13-46 ms whatever it carries (outliers
/// to 1290 ms), so a plan priced from 0,05 ms is wrong by two orders of magnitude.
///
/// How the numbers are kept
/// -------------------------
/// Every read any pass makes is fed in here, and nothing else is: the profile is never measured by a read
/// taken to measure it, because the goal this serves forbids the pre-work probe - a larger read taken
/// before the work to find out how fast the storage is, paid for by every extraction including the ones
/// that did not need it.
///
/// * Latency is the *median* of the per-read times, weighted by age. The median is what protects against
///   the outlier tail: one 1290 ms read in eight does not move it, where it moves a mean by 160 ms. A
///   plain mean over the same samples is the reading this project has already watched lie (0,1 MB/s in one
///   run and 11,1 MB/s in the next, from a ~0,7 ms difference read against 50 ms of latency).
/// * Throughput is the byte-weighted mean of the per-read throughputs after discarding the slowest fifth,
///   also weighted by age: bytes are what a read costs in transfer, so a 16 MB chunk says more about
///   throughput than a 256 byte header, and the discarded fifth is the tail that would otherwise carry it.
/// * Age is a half-life, not a cliff: a sample counts half as much after <see cref="HalfLife"/>, so a share
///   that was busy ten minutes ago stops being believed without the profile ever being empty.
///
/// What it is not
/// --------------
/// Not persisted. It lives as long as the plugin process, which spans a sync run and usually much more, and
/// it is deliberately not written to disk: a profile restored after a reboot or a plugin update may describe
/// a share that is no longer there, and the cost of being wrong is paid in a bad first plan either way. If
/// it ever earns its keep, persisting it is a separate decision with its own staleness question.
/// </summary>
public sealed class VolumeProfile
{
    /// <summary>How long a sample counts for before it is worth half as much.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromMinutes(10);

    /// <summary>Samples kept per volume; older ones are dropped rather than weighted into nothing.</summary>
    public const int MaxSamples = 256;

    private const double SlowestFifth = 0.2;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private readonly List<Sample> _walks = new();
    private int? _lastCeiling;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Initializes a new instance of the <see cref="VolumeProfile"/> class.</summary>
    /// <param name="key">What identifies the volume, for the log.</param>
    /// <param name="clock">Source of the current time; the checks drive it.</param>
    public VolumeProfile(string key, Func<DateTimeOffset>? clock = null)
    {
        Key = key;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Gets what identifies the volume, for the log.</summary>
    public string Key { get; }

    /// <summary>Gets how many reads this volume's profile has been fed.</summary>
    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>
    /// Records what one read on this volume cost and carried.
    /// </summary>
    /// <param name="bytes">Bytes the read returned.</param>
    /// <param name="ms">Milliseconds the read took.</param>
    public void Observe(long bytes, double ms)
    {
        if (bytes <= 0 || ms <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _samples.Add(new Sample(bytes, ms, _clock()));
            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }
        }
    }

    /// <summary>
    /// Gets the storage's per-read cost as this volume has shown it, or null while nothing has been read on
    /// it. Zero is a real answer (a read answered from the page cache), which is why this is nullable.
    /// </summary>
    /// <returns>Milliseconds per read, or null.</returns>
    public double? MsPerCall()
    {
        List<(double Value, double Weight)> weighted;
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            var now = _clock();
            weighted = _samples.Select(s => (Value: s.Ms, Weight: Weight(s, now))).ToList();
        }

        return WeightedMedian(weighted);
    }

    /// <summary>
    /// Records what one *walk* of a file on this volume cost and carried: the plugin knows the file's length
    /// and how long the engine took over it, so a volume can be measured without taking any read to measure it.
    /// </summary>
    /// <remarks>
    /// Kept apart from the read samples on purpose. A read sample describes a read call - 32 KB in 20 ms is a
    /// latency measurement, not a statement about the storage's transfer rate - while a walk describes the
    /// whole file moving through the engine. Mixing them would let a walk's aggregate pose as one read's cost
    /// and drive <see cref="MsPerCall"/> to minutes.
    /// </remarks>
    /// <param name="bytes">Bytes the walk had to move, normally the media file's length.</param>
    /// <param name="ms">Milliseconds the walk took.</param>
    public void ObserveWalk(long bytes, double ms)
    {
        if (bytes <= 0 || ms <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _walks.Add(new Sample(bytes, ms, _clock()));
            if (_walks.Count > MaxSamples)
            {
                _walks.RemoveRange(0, _walks.Count - MaxSamples);
            }
        }
    }

    /// <summary>
    /// Gets the ceiling this volume was last held to, or null when none has been decided.
    /// </summary>
    /// <remarks>
    /// Sticky state on purpose. A ceiling that is re-decided from scratch every planning pass is a ceiling that
    /// oscillates: measured on 2026-09-14, a volume whose walks straddled an absolute threshold flipped between
    /// two and no ceiling twice inside one batch. The band in <c>WalkCapForProfile</c> needs somewhere to hold
    /// the last answer, and this is it.
    /// </remarks>
    public int? LastCeiling
    {
        get
        {
            lock (_gate)
            {
                return _lastCeiling;
            }
        }
    }

    /// <summary>Records the ceiling this volume has just been held to.</summary>
    /// <param name="cap">The ceiling, or <see cref="int.MaxValue"/> for none.</param>
    public void RememberCeiling(int cap)
    {
        lock (_gate)
        {
            _lastCeiling = cap;
        }
    }

    /// <summary>Gets how many walks this volume's profile has been fed.</summary>
    public int WalkCount
    {
        get
        {
            lock (_gate)
            {
                return _walks.Count;
            }
        }
    }

    /// <summary>
    /// Gets the throughput this volume showed *while walking*, or null while no walk has finished on it. This
    /// is the signal that still exists when every extraction in a run was served from the subtitle cache and
    /// nothing was read through <see cref="ReadPolicy"/>.
    /// </summary>
    /// <returns>Bytes per millisecond, or null.</returns>
    public double? WalkBytesPerMs()
    {
        List<Sample> samples;
        DateTimeOffset now;
        lock (_gate)
        {
            if (_walks.Count == 0)
            {
                return null;
            }

            samples = _walks.ToList();
            now = _clock();
        }

        var ordered = samples.OrderBy(s => s.Bytes / s.Ms).ToList();
        var keep = Math.Max(1, (int)Math.Ceiling(ordered.Count * (1 - SlowestFifth)));
        var kept = ordered.Skip(ordered.Count - keep).ToList();
        var bytes = kept.Sum(s => (double)s.Bytes * Weight(s, now));
        var ms = kept.Sum(s => s.Ms * Weight(s, now));
        return ms <= 0 ? null : bytes / ms;
    }

    /// <summary>
    /// Gets the storage's throughput as this volume has shown it, or null while nothing has been read on it.
    /// </summary>
    /// <returns>Bytes per millisecond, or null.</returns>
    public double? BytesPerMs()
    {
        List<Sample> samples;
        DateTimeOffset now;
        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            samples = _samples.ToList();
            now = _clock();
        }

        // Drop the slowest fifth by per-read throughput, then let the bytes decide: a 16 MB chunk that took
        // 1,4 s describes transfer better than a 256 byte header that took 13 ms.
        var ordered = samples.OrderBy(s => s.Bytes / s.Ms).ToList();
        var keep = Math.Max(1, (int)Math.Ceiling(ordered.Count * (1 - SlowestFifth)));
        var kept = ordered.Skip(ordered.Count - keep).ToList();

        var bytes = kept.Sum(s => (double)s.Bytes * Weight(s, now));
        var ms = kept.Sum(s => s.Ms * Weight(s, now));
        return ms <= 0 ? null : bytes / ms;
    }

    /// <summary>
    /// Describes the profile for the log: what the volume has cost, over how many reads and how recent.
    /// </summary>
    /// <returns>A one-line description.</returns>
    public string Describe()
    {
        List<Sample> samples;
        DateTimeOffset now;
        lock (_gate)
        {
            samples = _samples.ToList();
            now = _clock();
        }

        var ms = MsPerCall();
        var bytesPerMs = BytesPerMs();
        if (ms is null || bytesPerMs is null)
        {
            return $"{Key}: nothing read on this volume yet";
        }

        var newest = samples.Max(s => s.At);
        var age = now - newest;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1:0.00} ms per read and {2:0.0} MB/s, from {3} read(s) it has already served, last {4:0.0} min ago",
            Key,
            ms,
            bytesPerMs / 1000.0,
            samples.Count,
            age.TotalMinutes);
    }

    private static double Weight(Sample sample, DateTimeOffset now)
    {
        var age = now - sample.At;
        if (age <= TimeSpan.Zero)
        {
            return 1.0;
        }

        return Math.Pow(0.5, age.TotalMilliseconds / HalfLife.TotalMilliseconds);
    }

    private static double WeightedMedian(List<(double Value, double Weight)> weighted)
    {
        var ordered = weighted.OrderBy(w => w.Value).ToList();
        var total = ordered.Sum(w => w.Weight);
        var half = total / 2;
        var running = 0.0;
        foreach (var (value, weight) in ordered)
        {
            running += weight;
            if (running >= half)
            {
                return value;
            }
        }

        return ordered[^1].Value;
    }

    private readonly record struct Sample(long Bytes, double Ms, DateTimeOffset At);
}

/// <summary>
/// The volume profiles this process has, one per volume, shared by every pass that reads from it.
/// </summary>
public static class VolumeProfiles
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, VolumeProfile> Profiles = new(StringComparer.Ordinal);

    /// <summary>
    /// How many samples a volume needs before it can set the reference the others are judged against, so one
    /// lucky read or one fast walk cannot raise the bar for every other volume on the machine.
    /// </summary>
    public const int MinSamplesForReference = 8;

    /// <summary>
    /// The fastest read latency that counts as storage rather than as the page cache, in milliseconds.
    /// </summary>
    /// <remarks>
    /// A volume answering out of the page cache answers in microseconds. If such a volume set the reference,
    /// every disk on the machine would be judged against memory: with the figure tried first (0,05 ms) a volume
    /// reading at 20 ms per call came out at 400x the reference and was classified as thrashing rather than
    /// storage-bound. A quarter of a millisecond is roughly a queue-depth-1 NVMe read - no storage answers
    /// faster than that, and anything quicker is the cache rather than evidence about any disk.
    /// </remarks>
    public const double PageCacheFloorMsPerCall = 0.25;

    /// <summary>
    /// Gets the fastest read latency any volume on this machine has shown, in milliseconds per read, or null
    /// while no volume has enough samples to set the reference.
    /// </summary>
    /// <param name="exceptKey">A volume to leave out of the reference: a volume cannot set its own bar.</param>
    /// <returns>Milliseconds per read, floored at <see cref="PageCacheFloorMsPerCall"/>.</returns>
    public static double? FastestReadMsPerCall(string? exceptKey = null)
    {
        var best = double.MaxValue;
        var found = false;
        foreach (var profile in Snapshot())
        {
            if (profile.Key == exceptKey)
            {
                continue;
            }

            if (profile.SampleCount < MinSamplesForReference || profile.MsPerCall() is not { } ms)
            {
                continue;
            }

            found = true;
            best = Math.Min(best, ms);
        }

        return found ? Math.Max(best, PageCacheFloorMsPerCall) : null;
    }

    /// <summary>
    /// Gets the fastest throughput any volume on this machine has shown while walking, in bytes per
    /// millisecond, or null while no volume has enough walks to set the reference.
    /// </summary>
    /// <param name="exceptKey">A volume to leave out of the reference: a volume cannot set its own bar.</param>
    /// <returns>Bytes per millisecond.</returns>
    public static double? FastestWalkBytesPerMs(string? exceptKey = null)
    {
        var best = 0.0;
        var found = false;
        foreach (var profile in Snapshot())
        {
            if (profile.Key == exceptKey)
            {
                continue;
            }

            if (profile.WalkCount < MinSamplesForReference || profile.WalkBytesPerMs() is not { } bytesPerMs)
            {
                continue;
            }

            found = true;
            best = Math.Max(best, bytesPerMs);
        }

        return found ? best : null;
    }

    /// <summary>Gets the profiles this process holds, copied so the caller can query them without the lock.</summary>
    /// <returns>The profiles, one per volume.</returns>
    private static List<VolumeProfile> Snapshot()
    {
        lock (Gate)
        {
            return Profiles.Values.ToList();
        }
    }

    /// <summary>Gets how many volumes this process has profiles for, for the checks.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Profiles.Count;
            }
        }
    }

    /// <summary>
    /// Gets the profile for the volume a file lives on, creating one the first time that volume is seen.
    /// </summary>
    /// <param name="path">Any path on the volume (the media file being read).</param>
    /// <returns>The volume's profile.</returns>
    public static VolumeProfile For(string path)
    {
        var key = KeyFor(path);
        lock (Gate)
        {
            if (!Profiles.TryGetValue(key, out var profile))
            {
                profile = new VolumeProfile(key);
                Profiles.Add(key, profile);
            }

            return profile;
        }
    }

    /// <summary>
    /// Clears every profile. For the checks: state that outlives a test is not state a test can reason about.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Profiles.Clear();
        }
    }

    /// <summary>
    /// Names the volume a path sits on: the device behind the longest mount point that contains it, so two
    /// mount points of the same filesystem (a bind mount, a share mounted twice) share one profile - which
    /// is the storage's own answer to "is this the same volume" rather than the path's.
    /// </summary>
    /// <param name="path">Any path on the volume.</param>
    /// <returns>The volume's key.</returns>
    public static string KeyFor(string path)
    {
        var full = Path.GetFullPath(path);
        var device = DeviceFor(full);
        if (device is not null)
        {
            return device;
        }

        // No mount table to read (not Linux, or not readable): the drive the platform reports for the path.
        try
        {
            var drive = new DriveInfo(full);
            return string.IsNullOrEmpty(drive.Name) ? "unknown volume" : drive.Name;
        }
        catch (ArgumentException)
        {
            return "unknown volume";
        }
    }

    private static string? DeviceFor(string fullPath)
    {
        var mounts = ReadMounts();
        if (mounts.Count == 0)
        {
            return null;
        }

        string? bestDevice = null;
        var bestLength = -1;
        foreach (var (mountPoint, device) in mounts)
        {
            if (!fullPath.StartsWith(mountPoint, StringComparison.Ordinal))
            {
                continue;
            }

            if (mountPoint != "/" && fullPath.Length > mountPoint.Length
                && fullPath[mountPoint.Length] != Path.DirectorySeparatorChar)
            {
                continue;
            }

            if (mountPoint.Length > bestLength)
            {
                bestDevice = device;
                bestLength = mountPoint.Length;
            }
        }

        return bestDevice;
    }

    private static List<(string MountPoint, string Device)> ReadMounts()
    {
        var mounts = new List<(string, string)>();
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return mounts;
        }

        try
        {
            foreach (var line in File.ReadLines("/proc/self/mounts"))
            {
                var fields = line.Split(' ');
                if (fields.Length < 2)
                {
                    continue;
                }

                // /proc/mounts escapes spaces in paths as \040.
                var mountPoint = fields[1].Replace("\\040", " ", StringComparison.Ordinal);
                mounts.Add((mountPoint, fields[0]));
            }
        }
        catch (IOException)
        {
            return new List<(string, string)>();
        }
        catch (UnauthorizedAccessException)
        {
            return new List<(string, string)>();
        }

        return mounts;
    }
}
