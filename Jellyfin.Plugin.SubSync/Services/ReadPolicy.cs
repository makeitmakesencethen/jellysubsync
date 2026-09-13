using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Which route a pass reads its file by. The route is chosen per pass from what the index provides
/// and from what the storage is measured to cost while the pass runs.
/// </summary>
public enum ReadRoute
{
    /// <summary>One read per subtitle block, at a position the cue index named.</summary>
    CueIndexed,

    /// <summary>Walk the clusters' block headers, because the index does not locate the blocks.</summary>
    ClusterWalk,

    /// <summary>One cue-indexed pass serving several tracks of the same file at once.</summary>
    SharedPass,
}

/// <summary>
/// One read a pass knows it will make. Window and fetch are one decision: a read served from memory is
/// only possible when the range that was fetched actually covers it, which is why the plan states the
/// reads rather than a window size.
/// </summary>
/// <param name="Start">Where the read begins.</param>
/// <param name="Length">How many bytes it asks for.</param>
public readonly record struct PlannedRead(long Start, int Length)
{
    /// <summary>Gets one past the last byte of the read.</summary>
    public long End => Start + Length;
}

/// <summary>
/// What a pass decided: the route, the window to use for reads the plan did not foresee, the reads it
/// expects to make, and what it therefore expects to cost.
/// </summary>
public sealed class ReadPlan
{
    /// <summary>Initializes a new instance of the <see cref="ReadPlan"/> class.</summary>
    /// <param name="route">Route chosen for this pass.</param>
    /// <param name="windowBytes">Window for reads the plan did not foresee.</param>
    /// <param name="reads">Reads the pass expects to make, in file order.</param>
    /// <param name="reason">Why this route and these reads, in the words the log uses.</param>
    /// <param name="coarse">True when the cost is an upper bound rather than an estimate.</param>
    public ReadPlan(ReadRoute route, int windowBytes, IEnumerable<PlannedRead> reads, string reason, bool coarse = false)
    {
        Route = route;
        WindowBytes = windowBytes;
        Reads = reads as IReadOnlyList<PlannedRead> ?? reads.ToList();
        Reason = reason;
        Coarse = coarse;
    }

    /// <summary>Gets the route this pass reads by.</summary>
    public ReadRoute Route { get; }

    /// <summary>Gets the window for reads the plan did not foresee, in bytes.</summary>
    public int WindowBytes { get; }

    /// <summary>Gets the reads the pass expects to make, in file order.</summary>
    public IReadOnlyList<PlannedRead> Reads { get; }

    /// <summary>
    /// Gets the subset of <see cref="Reads"/> to fetch into memory first. A walk reads in file order and
    /// brings its clusters in itself, so those reads are predicted but not fetched: fetching them would
    /// read the same bytes twice, which is the one thing the ledger refuses to allow.
    /// </summary>
    public IReadOnlyList<PlannedRead> Fetches { get; init; } = Array.Empty<PlannedRead>();

    /// <summary>Gets why this route was chosen.</summary>
    public string Reason { get; }

    /// <summary>Gets a value indicating whether the cost is an upper bound rather than an estimate.</summary>
    public bool Coarse { get; }

    /// <summary>Gets the bytes the pass expects to read.</summary>
    public long ExpectedBytes => Reads.Sum(r => (long)r.Length);

    /// <summary>Gets the read calls the pass expects to make.</summary>
    public int ExpectedCalls => Reads.Count;

    /// <summary>Gets the route as the log names it.</summary>
    public string RouteLabel => Route switch
    {
        ReadRoute.CueIndexed => "cue-indexed",
        ReadRoute.ClusterWalk => "cluster-walk",
        _ => "shared-pass",
    };

    /// <summary>Human-readable summary of the decision, for the log.</summary>
    /// <returns>One line naming the route, the window and the expected cost.</returns>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "{0}, window {1:0.#} KB, {2} planned read(s), expects {3:0.00} MB{4} ({5})",
        RouteLabel,
        WindowBytes / 1024.0,
        ExpectedCalls,
        ExpectedBytes / 1e6,
        Coarse ? " or less" : string.Empty,
        Reason);
}

/// <summary>
/// What one pass read and fetched, so the two can be compared afterwards. Bytes read twice are the
/// failure this exists to catch: a fetched range that a later read goes back to disk for, or two reads
/// over the same bytes, is work the pass paid for and did not need.
/// </summary>
public sealed class ReadLedger
{
    private readonly List<(long Start, long End)> _disk = new();
    private readonly List<(long Start, long End, long Touched)> _fetched = new();

    /// <summary>
    /// Gets the bytes the pass asked the file for more than once: the sum of
    /// <see cref="BytesReadAfterFetch"/> and <see cref="BytesFetchedOverDisk"/>.
    /// </summary>
    public long BytesReadTwice => BytesReadAfterFetch + BytesFetchedOverDisk;

    /// <summary>
    /// Gets the bytes a read went to the file for even though the pass had already fetched that range:
    /// the fetched range did not cover the read it was made for. This is the waste the ledger exists to
    /// catch (118,3 MB fetched and then read past, because the ranges missed the cluster head), so it is
    /// kept apart from the bytes the location phase read before the pass fetched over them.
    /// </summary>
    public long BytesReadAfterFetch { get; private set; }

    /// <summary>
    /// Gets the bytes a fetch covered that the location phase had already read from the file (the element
    /// headers a SeekHead walk touched). Bounded by one window and not a fault: the location work has to
    /// read those bytes before the pass knows what to fetch.
    /// </summary>
    public long BytesFetchedOverDisk { get; private set; }

    /// <summary>Gets how many ranges were fetched up front.</summary>
    public int FetchedRanges => _fetched.Count;

    /// <summary>Gets the bytes those ranges hold.</summary>
    public long FetchedBytes => _fetched.Sum(f => f.End - f.Start);

    /// <summary>Gets the bytes of the fetched ranges that no read ever used.</summary>
    public long UnusedFetchedBytes => _fetched.Sum(f => f.End - f.Start - f.Touched);

    /// <summary>Gets the fetched ranges no read ever used.</summary>
    public int UnusedFetchedRanges => _fetched.Count(f => f.Touched == 0);

    /// <summary>
    /// Records a read that went to the file.
    /// </summary>
    /// <param name="start">Where the read began.</param>
    /// <param name="length">How many bytes came back.</param>
    public void RecordDiskRead(long start, int length)
    {
        if (length <= 0 || start < 0)
        {
            return;
        }

        var end = start + length;
        BytesReadAfterFetch += OverlapBytes(_fetched.Select(f => (f.Start, f.End)).ToList(), start, end);
        MarkFetchedUsed(start, end);
        Insert(_disk, start, end);
    }

    /// <summary>
    /// Records a range that was fetched into memory before the pass needed it.
    /// </summary>
    /// <param name="start">Where the range begins.</param>
    /// <param name="length">How many bytes it holds.</param>
    public void RecordFetchedRange(long start, int length)
    {
        if (length <= 0 || start < 0)
        {
            return;
        }

        var end = start + length;
        BytesFetchedOverDisk += OverlapBytes(_disk, start, end);
        _fetched.Add((start, end, 0));
    }

    /// <summary>Records that a read was answered without going to the file.</summary>
    /// <param name="start">Where the read began.</param>
    /// <param name="length">How many bytes it asked for.</param>
    public void RecordServedFromMemory(long start, int length)
    {
        if (length <= 0 || start < 0)
        {
            return;
        }

        MarkFetchedUsed(start, start + length);
    }

    /// <summary>
    /// Gets a value indicating whether the range is already in hand, either read from the file or
    /// fetched into memory.
    /// </summary>
    /// <param name="start">Where the range begins.</param>
    /// <param name="length">How many bytes it holds.</param>
    /// <returns>True when the bytes have already been paid for.</returns>
    public bool AlreadyInHand(long start, int length) =>
        length > 0 && start >= 0 && IsCovered(start, start + length);

    /// <summary>Human-readable summary of the ledger, for the log.</summary>
    /// <returns>One line with the fetch, the reuse and any repeat reads.</returns>
    public string Describe() => string.Format(
        CultureInfo.InvariantCulture,
        "fetched {0} range(s)/{1:0.00} MB, {2} unused ({3:0.00} MB), read past the fetch {4:0.00} MB, "
        + "fetched over the location reads {5:0.00} MB",
        FetchedRanges,
        FetchedBytes / 1e6,
        UnusedFetchedRanges,
        UnusedFetchedBytes / 1e6,
        BytesReadAfterFetch / 1e6,
        BytesFetchedOverDisk / 1e6);

    private static long OverlapBytes(List<(long Start, long End)> intervals, long start, long end)
    {
        var total = 0L;
        foreach (var (s, e) in intervals)
        {
            if (e <= start)
            {
                continue;
            }

            if (s >= end)
            {
                break;
            }

            total += Math.Min(e, end) - Math.Max(s, start);
        }

        return total;
    }

    private void MarkFetchedUsed(long start, long end)
    {
        for (var i = 0; i < _fetched.Count; i++)
        {
            var (s, e, touched) = _fetched[i];
            var overlap = Math.Min(e, end) - Math.Max(s, start);
            if (overlap > 0)
            {
                _fetched[i] = (s, e, touched + overlap);
            }
        }
    }

    private bool IsCovered(long start, long end)
    {
        if (OverlapBytes(_disk, start, end) >= end - start)
        {
            return true;
        }

        foreach (var (s, e, _) in _fetched)
        {
            if (s <= start && end <= e)
            {
                return true;
            }
        }

        return false;
    }

    private static void Insert(List<(long Start, long End)> intervals, long start, long end)
    {
        // Kept sorted and merged: a pass issues thousands of reads and every one of them asks this list
        // what it has already paid for.
        var index = 0;
        while (index < intervals.Count && intervals[index].End < start)
        {
            index++;
        }

        while (index < intervals.Count && intervals[index].Start <= end)
        {
            start = Math.Min(start, intervals[index].Start);
            end = Math.Max(end, intervals[index].End);
            intervals.RemoveAt(index);
        }

        intervals.Insert(index, (start, end));
    }
}

/// <summary>
/// The one place a read route, a read window and a fetch decision are made.
///
/// Three callers ask it: the cue-indexed pass, the cluster walk and the shared multi-track pass. It is
/// given what the file's index actually provides (which blocks are located, how far into their cluster
/// they sit, what a walk would have to cover) and what the storage has been measured to cost during the
/// pass, and it prices the candidate routes and windows with one model:
///
///     cost = reads x (milliseconds per read) + bytes / (bytes per millisecond)
///
/// Nothing here is decided from a probe taken before the work: the profile starts as "reads are cheap,
/// bytes are cheap" and is replaced by what the pass's own reads measured, so a window that was sized
/// wrong corrects itself after a handful of reads instead of for the whole file. Every decision is
/// logged with the numbers it was made from, and each pass prints what it expected beside what it cost.
/// </summary>
public sealed class ReadPolicy
{
    /// <summary>
    /// What the log appends to a plan whose expected cost is an upper bound rather than an estimate. It is
    /// public and constant because the checks assert it is present exactly where a bound is reported: a
    /// prediction that silently passes whatever the pass does is worse than no prediction at all.
    /// </summary>
    public const string BoundNote = " [bound, not verified]";

    /// <summary>Smallest window the reader may use, in bytes.</summary>
    public const int MinWindow = 512;

    /// <summary>Largest window the reader may use, in bytes.</summary>
    public const int MaxWindow = 4 * 1024 * 1024;

    /// <summary>How much a forward walk brings in at a time, in bytes.</summary>
    public const int WalkChunkBytes = 16 * 1024 * 1024;

    /// <summary>Bytes read to see a cluster's element header and its timecode.</summary>
    // 256 rather than 16: the timecode is the cluster's first child, but a muxer may put a CRC or a Void
    // before it, and a head read that misses costs a round trip on the storage this exists for.
    public const int ClusterHeadRead = 256;

    /// <summary>Bytes read for one subtitle block: its header plus its text.</summary>
    // A cue's text is a few hundred bytes; 2 KB covers the longest line a subtitle carries whole, and a
    // block longer than that simply falls back to a read of its own. Sized to what a block *is* rather
    // than to what a read costs: fetching 8 KB per cue would carry 3 MB of neighbouring video to collect
    // 70 KB of text on a 364-cue episode, which is the waste this whole policy exists to remove.
    public const int BlockRead = 2 * 1024;

    /// <summary>How far before a block a fetch starts, to cover the cluster header in between.</summary>
    public const int BlockSlack = 64;

    /// <summary>Reads the walk keeps in flight when it fetches its route up front.</summary>
    public const int PrefetchParallelism = 16;

    private const int SamplesBeforeTrusting = 8;
    private const double DefaultMsPerCall = 0.05;
    private const double DefaultBytesPerMs = 1500;

    // How far apart two observed reads have to be before their two timings can be read as a line
    // (time = latency + bytes x slope): a factor of two and at least 4 KB, or the slope is noise.
    private const double FitSizeFactor = 2.0;
    private const long FitMinSpreadBytes = 4 * 1024;
    private const long MaxPrefetchBytes = 64L * 1024 * 1024;

    private double _msPerCall = DefaultMsPerCall;
    private double _bytesPerMs = DefaultBytesPerMs;
    private long _sampleBytes;
    private double _sampleMs;
    private int _sampleCount;

    // The pass's own reads, kept as two extremes: the smallest read it has made and the largest. One read
    // size can only give one number, and that number moves with the size - a 2 KB read that takes 50 ms
    // says nothing about how fast the storage is, only how long the round trip is. Two sizes separate them:
    // the latency is what does not grow with the read, and the throughput is what does.
    private long _smallBytes;
    private double _smallMs;
    private long _bigBytes;
    private double _bigMs;
    private int _samples;
    private int _updates;
    private bool _fitted;

    /// <summary>Initializes a new instance of the <see cref="ReadPolicy"/> class.</summary>
    /// <param name="fileLength">Length of the file being read, in bytes.</param>
    /// <param name="route">Route the pass starts by.</param>
    /// <param name="label">Name of the pass, for the log.</param>
    public ReadPolicy(long fileLength, ReadRoute route, string label)
    {
        FileLength = fileLength;
        Route = route;
        Label = label;
        CurrentWindow = InitialWindow;
    }

    /// <summary>Gets the length of the file being read.</summary>
    public long FileLength { get; }

    /// <summary>Gets the route the pass is reading by.</summary>
    public ReadRoute Route { get; }

    /// <summary>Gets the pass's name, for the log.</summary>
    public string Label { get; }

    /// <summary>
    /// Gets what one read costs in waiting, in milliseconds - the round trip, separated from the bytes
    /// when the pass has measured two read sizes, and the whole average of one read when it has not.
    /// </summary>
    public double MsPerCall => _msPerCall;

    /// <summary>
    /// Gets the bytes the storage delivers per millisecond: fitted from two read sizes where the pass has
    /// made them, and a lower bound (the window average, which folds the round trip into the throughput)
    /// where it has not.
    /// </summary>
    public double BytesPerMs => _bytesPerMs;

    /// <summary>
    /// Gets how many reads have been observed since the profile was last updated. It is the pending half
    /// of a window, not the pass's measurement state: it goes back to zero every
    /// <see cref="SamplesBeforeTrusting"/> reads, so it must not be read as "this pass has measured the
    /// storage" - <see cref="MeasuredOnce"/> and <see cref="SampleCount"/> answer that.
    /// </summary>
    public int PendingSamples => _sampleCount;

    /// <summary>Gets how many reads this pass has observed in total.</summary>
    public int SampleCount => _samples;

    /// <summary>
    /// Gets a value indicating whether the profile has been measured at all, as opposed to still being
    /// the class defaults. True from the first published update onwards.
    /// </summary>
    public bool MeasuredOnce => _updates > 0;

    /// <summary>Gets how many times the profile has been updated from the pass's own reads.</summary>
    public int ProfileUpdates => _updates;

    /// <summary>
    /// Gets a value indicating whether latency and throughput were separated by fitting the two read sizes
    /// the pass has measured, rather than being read off one contaminated average.
    /// </summary>
    public bool ProfileIsFitted => _fitted;

    /// <summary>Gets the latency the profile rests on, in milliseconds - the part of a read that does not grow with it.</summary>
    public double LatencyMs => _msPerCall;

    /// <summary>Gets the throughput the profile rests on, in bytes per millisecond.</summary>
    public double ThroughputBytesPerMs => _bytesPerMs;

    /// <summary>Gets or sets the window the pass is reading with, so a re-pricing can be described.</summary>
    public int CurrentWindow { get; set; }

    /// <summary>
    /// Gets the number of bytes that may be fetched without the gap between two reads costing more than
    /// the read it saves: one merged read costs one call and carries the gap, two reads cost two calls, so
    /// the gap is worth carrying while its transfer costs less than the round trip - i.e. one round trip's
    /// worth of bytes, which is what a fitted profile can actually state.
    /// </summary>
    public long MergeGapBytes => Math.Clamp((long)(_msPerCall * _bytesPerMs), 0, 256L * 1024);

    /// <summary>Gets the window a pass starts with, before it has measured anything.</summary>
    public int InitialWindow => MinWindow * 8;

    /// <summary>
    /// Records what one read cost and carried. The profile is the average of the reads the pass has
    /// actually made, so it describes the storage as it behaves *during* the work rather than as a probe
    /// found it before the work started.
    /// </summary>
    /// <param name="bytes">Bytes the read returned.</param>
    /// <param name="ms">Milliseconds the read took.</param>
    public void Observe(long bytes, double ms)
    {
        if (bytes <= 0 || ms < 0)
        {
            return;
        }

        // The two extremes of the pass's own reads are kept because one read size gives one number, and
        // that number moves with the size: a 2 KB read that took 50 ms says how long a round trip is, not
        // how fast the storage is. Two sizes separate the two.
        if (_samples == 0 || bytes < _smallBytes)
        {
            _smallBytes = bytes;
            _smallMs = ms;
        }

        if (bytes > _bigBytes)
        {
            _bigBytes = bytes;
            _bigMs = ms;
        }

        _samples++;
        _sampleBytes += bytes;
        _sampleMs += ms;
        _sampleCount++;
        if (_sampleCount < SamplesBeforeTrusting)
        {
            return;
        }

        // The window average, as before: it is what the profile rests on when only one read size has been
        // seen. Averaged over a window of reads rather than over the whole pass, because a share that was
        // busy while the first jobs ran is not the share that answers the last one.
        var windowMsPerRead = _sampleMs / SamplesBeforeTrusting;
        var windowBytesPerMs = Math.Max(1.0, _sampleBytes / Math.Max(0.001, _sampleMs));

        // Two sizes, one line: time = round trip + bytes / throughput. The slope is what a byte costs and
        // the intercept is what the round trip costs, so the merge decision can ask what a round trip is
        // worth in bytes rather than multiplying two numbers that came from the same small reads - which is
        // how a share delivering 11 MB/s was measured as delivering 0,1 MB/s and never merged anything.
        var spread = _bigBytes - _smallBytes;
        var fitted = spread >= FitMinSpreadBytes
                     && _bigBytes >= _smallBytes * FitSizeFactor
                     && _bigMs > _smallMs;
        if (fitted)
        {
            var msPerByte = (_bigMs - _smallMs) / spread;
            var throughput = msPerByte > 0 ? 1.0 / msPerByte : windowBytesPerMs;
            var latency = _smallMs - (msPerByte * _smallBytes);
            if (throughput > 0 && latency >= 0)
            {
                _bytesPerMs = throughput;
                _msPerCall = latency;
            }
            else
            {
                fitted = false;
            }
        }

        if (!fitted)
        {
            _msPerCall = windowMsPerRead;
            _bytesPerMs = windowBytesPerMs;
        }

        _fitted = fitted;
        _updates++;
        _sampleBytes = 0;
        _sampleMs = 0;
        _sampleCount = 0;
    }

    /// <summary>
    /// Prices a candidate read pattern: what it costs in waiting plus what it costs in transfer.
    /// </summary>
    /// <param name="bytes">Bytes the pattern reads.</param>
    /// <param name="calls">Read calls the pattern makes.</param>
    /// <returns>Estimated cost in milliseconds.</returns>
    public double Price(long bytes, long calls) =>
        (calls * _msPerCall) + (bytes / Math.Max(0.001, _bytesPerMs));

    /// <summary>
    /// Gets the window to walk the clusters with, priced against the alternatives from what the walk has
    /// measured so far: a bigger window cuts the reads per cluster and raises the bytes per cluster.
    /// </summary>
    /// <param name="regionBytes">Bytes the walk still has to cover.</param>
    /// <param name="bytesPerCluster">Bytes the walk has pulled per cluster so far, or 0 before it has read any.</param>
    /// <param name="readsPerCluster">Reads the walk has issued per cluster so far, or 0 before it has read any.</param>
    /// <param name="clustersRemaining">Clusters the walk still has to visit.</param>
    /// <returns>Window size in bytes.</returns>
    public int WalkWindow(long regionBytes, double bytesPerCluster, double readsPerCluster, long clustersRemaining)
    {
        if (regionBytes <= 0 || clustersRemaining <= 0 || bytesPerCluster <= 0 || readsPerCluster <= 0)
        {
            return CurrentWindow;
        }

        var best = CurrentWindow;
        var bestPrice = double.MaxValue;
        foreach (var window in new[] { MinWindow * 8, 64 * 1024, 1024 * 1024, MaxWindow })
        {
            // A window w reads about w more bytes per read than a window of 4 KB does, and needs about
            // 4 KB/w as many reads. Both ends are bounded by the region itself and by one read per
            // cluster, so the model cannot promise more than the walk could ever deliver.
            var scale = Math.Min(64.0, (double)window / (MinWindow * 8));
            var bytes = (long)Math.Min(regionBytes, Math.Max(bytesPerCluster, MinWindow * 8) * clustersRemaining * Math.Min(scale, 16.0));
            var calls = (long)Math.Max(clustersRemaining, readsPerCluster * clustersRemaining / Math.Min(scale, 16.0));
            var price = Price(bytes, calls);
            if (price < bestPrice)
            {
                bestPrice = price;
                best = window;
            }
        }

        return best;
    }

    /// <summary>
    /// Plans the cue-indexed route: one read per located block, plus the cluster head that locates it.
    /// </summary>
    /// <param name="blocks">Cluster start and block position of every cue the index located.</param>
    /// <param name="clustersToWalk">
    /// Cue clusters the index named but did not locate, which therefore have to be walked. Their reads are
    /// predicted as an upper bound rather than fetched: a walk brings its own bytes in.
    /// </param>
    /// <param name="reason">Why this route applies, for the log.</param>
    /// <returns>The plan for the pass.</returns>
    public ReadPlan PlanIndexedReads(
        IReadOnlyList<(long ClusterStart, long BlockStart)> blocks,
        IReadOnlyList<long> clustersToWalk,
        string reason)
    {
        var reads = new List<PlannedRead>(blocks.Count * 2);
        foreach (var (clusterStart, blockStart) in blocks)
        {
            if (clusterStart < 0 || clusterStart >= FileLength)
            {
                continue;
            }

            var located = blockStart >= clusterStart && blockStart < FileLength;

            // One covering read per cluster, from its start to past the block, is the alternative to a
            // head read plus a block read. It costs one call instead of two and carries the bytes in
            // between, so it wins exactly when that gap is cheaper than the call it saves - which is what
            // MergeGapBytes measures on this storage. This is the decision that used to be a hard-coded
            // window: on a share charging per read it became a megabyte at a time, and every one of those
            // megabytes was read for a block a few hundred bytes long.
            if (!located || blockStart - clusterStart > MergeGapBytes + BlockRead)
            {
                Add(reads, clusterStart, ClusterHeadRead);
            }
            else
            {
                Add(reads, clusterStart, (int)Math.Min(MaxWindow, blockStart - clusterStart + BlockRead + BlockSlack));
            }

            if (located && blockStart - clusterStart > MergeGapBytes + BlockRead)
            {
                Add(reads, blockStart - BlockSlack, BlockRead + BlockSlack);
            }
        }

        reads.Sort((a, b) => a.Start.CompareTo(b.Start));
        var fetches = Merge(reads, MergeGapBytes, MaxPrefetchBytes);

        // The clusters that have to be walked are predicted, not fetched: each of them costs at most one
        // window's worth of the reads that follow, so the estimate is an upper bound and says so.
        var boundReads = new List<PlannedRead>(fetches);
        foreach (var clusterStart in clustersToWalk)
        {
            if (clusterStart >= 0 && clusterStart < FileLength)
            {
                boundReads.Add(new PlannedRead(clusterStart, (int)Math.Min(MaxWindow, FileLength - clusterStart)));
            }
        }

        boundReads.Sort((a, b) => a.Start.CompareTo(b.Start));
        var plan = new ReadPlan(
            ReadRoute.CueIndexed,
            WindowFor(fetches),
            boundReads,
            reason + string.Format(
                CultureInfo.InvariantCulture,
                "; {0} located block(s), {1} cluster(s) to walk, reads are {2:0.00} ms each, merge gap {3} KB",
                blocks.Count,
                clustersToWalk.Count,
                _msPerCall,
                MergeGapBytes / 1024),
            coarse: clustersToWalk.Count > 0)
        {
            Fetches = fetches,
        };
        return plan;
    }

    // A metadata walk reads the block headers and skips the payloads, so a small window reads a fraction
    // of the cluster and costs one read per block; a window that covers the cluster reads all of it in one
    // read. Which is cheaper is the storage's business - 11% of a cluster at one read per block is free on
    // an SSD and ruinous on a share that charges per round trip - so both are priced and the cheapest wins.

    /// <summary>
    /// Gets the window to read one cluster with when its blocks have to be found by walking it.
    /// </summary>
    /// <param name="clusterBytes">The cluster's own size in bytes.</param>
    /// <param name="averageBlockBytes">Typical block size in the file, used to estimate how many headers a walk meets.</param>
    /// <returns>Window size in bytes.</returns>
    public int ClusterWindow(long clusterBytes, long averageBlockBytes = 16 * 1024)
    {
        if (clusterBytes <= 0)
        {
            return MinWindow;
        }

        var headers = Math.Max(1, clusterBytes / Math.Max(1024, averageBlockBytes));
        var small = MinWindow * 8;
        var smallBytes = (long)Math.Min(clusterBytes, headers * small);
        var smallPrice = Price(smallBytes, headers);
        var coverBytes = Math.Min(clusterBytes, MaxWindow);
        var coverPrice = Price(coverBytes, Math.Max(1, coverBytes / small));
        return coverPrice < smallPrice ? (int)coverBytes : small;
    }

    /// <summary>
    /// Plans the cluster walk: the blocks the index did not locate have to be found by reading their
    /// clusters, so the walk's window is what decides its cost.
    /// </summary>
    /// <param name="start">Where the walk begins.</param>
    /// <param name="end">Where it must stop.</param>
    /// <param name="clusters">Clusters the region is expected to hold, or 0 when unknown.</param>
    /// <returns>The plan for the pass.</returns>
    public ReadPlan PlanWalk(long start, long end, long clusters)
    {
        var region = Math.Max(0, Math.Min(end, FileLength) - Math.Max(0, start));
        var window = InitialWindow;
        var chunks = new List<PlannedRead>();
        var cursor = Math.Max(0, start);
        while (cursor < Math.Min(end, FileLength))
        {
            var length = (int)Math.Min(WalkChunkBytes, Math.Min(end, FileLength) - cursor);
            chunks.Add(new PlannedRead(cursor, length));
            cursor += length;
        }

        var reason = string.Format(
            CultureInfo.InvariantCulture,
            "the index does not locate the blocks, so the clusters are walked: {0:0.0} MB, about {1} cluster(s); "
            + "window starts at {2} KB and is re-priced from the walk's own reads",
            region / 1e6,
            clusters,
            window / 1024);
        return new ReadPlan(ReadRoute.ClusterWalk, window, chunks, reason, coarse: true);
    }

    /// <summary>
    /// Plans the shared pass: every requested track's blocks in one pass, plus the clusters a track has
    /// to be walked in.
    /// </summary>
    /// <param name="indexed">Cluster start and block position of every located block of every wanted track.</param>
    /// <param name="walkClusters">Clusters that must be read whole, as start and end.</param>
    /// <param name="reason">Why this route applies, for the log.</param>
    /// <returns>The plan for the pass.</returns>
    public ReadPlan PlanSharedReads(
        IReadOnlyList<(long ClusterStart, long BlockStart)> indexed,
        IReadOnlyList<(long Start, long End)> walkClusters,
        string reason)
    {
        var inner = PlanIndexedReads(indexed, Array.Empty<long>(), reason);
        var reads = inner.Reads.ToList();
        var walked = 0L;

        foreach (var (start, end) in walkClusters)
        {
            if (end <= start || start < 0 || start >= FileLength)
            {
                continue;
            }

            // A track whose cue points name the cluster but not the block has to be walked there, and a
            // walk of a cluster needs the cluster: this is the one read that is genuinely cluster-sized.
            var length = (int)Math.Min(MaxWindow, Math.Min(end, FileLength) - start);
            Add(reads, start, length);
            walked += length;
        }

        reads.Sort((a, b) => a.Start.CompareTo(b.Start));
        reads = Merge(reads, MergeGapBytes, MaxPrefetchBytes);
        return new ReadPlan(
            ReadRoute.SharedPass,
            Math.Max(inner.WindowBytes, WindowFor(reads)),
            reads,
            reason + string.Format(
                CultureInfo.InvariantCulture,
                "; {0} located block(s), {1} cluster(s) walked ({2:0.0} MB)",
                indexed.Count,
                walkClusters.Count,
                walked / 1e6))
        {
            Fetches = inner.Fetches,
        };
    }

    /// <summary>
    /// States what a finished pass expected beside what it cost, and whether it missed that estimate.
    /// </summary>
    /// <param name="plan">The plan the pass ran by.</param>
    /// <param name="actualBytes">Bytes the pass read.</param>
    /// <param name="actualCalls">Read calls the pass made.</param>
    /// <returns>
    /// One line for the log, and a flag saying whether it missed by more than a factor of two. A plan that
    /// is an upper bound returns false here and says so in the line: the walk stops once it has found what
    /// it came for, so what it will cost is not knowable from the index and a bound tight enough to warn on
    /// would warn wrongly. The line carries "bound, not verified" instead of reading as a verified check.
    /// </returns>
    public (string Line, bool Missed) Compare(ReadPlan plan, long actualBytes, int actualCalls)
    {
        var expectedBytes = Math.Max(1, plan.ExpectedBytes);
        var expectedCalls = Math.Max(1, plan.ExpectedCalls);
        var byteRatio = (double)actualBytes / expectedBytes;
        var callRatio = (double)actualCalls / expectedCalls;
        var missed = !plan.Coarse && (byteRatio > 2.0 || byteRatio < 0.5 || callRatio > 2.0 || callRatio < 0.5);
        var line = string.Format(
            CultureInfo.InvariantCulture,
            "extract plan: {0} {1}{10} expected {2:0.00} MB/{3} read(s) ({4:0.0} ms), actual {5:0.00} MB/{6} read(s) ({7:0.0} ms) - bytes {8:0.00}x, reads {9:0.00}x",
            Label,
            plan.RouteLabel,
            expectedBytes / 1e6,
            expectedCalls,
            Price(plan.ExpectedBytes, plan.ExpectedCalls),
            actualBytes / 1e6,
            actualCalls,
            Price(actualBytes, actualCalls),
            byteRatio,
            callRatio,
            plan.Coarse ? BoundNote : string.Empty);
        return (line, missed);
    }

    /// <summary>Human-readable description of the measured profile, for the log.</summary>
    /// <returns>
    /// One line naming the numbers the decisions were made from, whether they are measurements or still
    /// the defaults, how many reads they rest on, and whether latency and throughput were separated.
    /// </returns>
    public string DescribeProfile() => MeasuredOnce
        ? string.Format(
            CultureInfo.InvariantCulture,
            "{0}: storage {1:0.00} ms per read and {2:0.0} MB/s ({3}, {4} read(s) over {5} update(s)); merge gap {6} KB",
            Label,
            _msPerCall,
            _bytesPerMs / 1000.0,
            _fitted
                ? string.Format(CultureInfo.InvariantCulture, "fitted to {0} B and {1} B reads", _smallBytes, _bigBytes)
                : "one read size only, so throughput is a lower bound",
            _samples,
            _updates,
            MergeGapBytes / 1024)
        : string.Format(
            CultureInfo.InvariantCulture,
            "{0}: storage not measured yet - deciding from the defaults ({1:0.00} ms per read, {2:0.0} MB/s); merge gap {3} KB, no read observed",
            Label,
            _msPerCall,
            _bytesPerMs / 1000.0,
            MergeGapBytes / 1024);

    private static int WindowFor(IReadOnlyList<PlannedRead> reads)
    {
        if (reads.Count == 0)
        {
            return MinWindow;
        }

        return Math.Clamp(reads.Max(r => r.Length), 1024, 64 * 1024);
    }

    private void Add(List<PlannedRead> reads, long start, int length)
    {
        var clampedStart = Math.Max(0, start);
        var available = FileLength - clampedStart;
        if (available <= 0)
        {
            return;
        }

        var count = (int)Math.Min(length, available);
        if (count > 0)
        {
            reads.Add(new PlannedRead(clampedStart, count));
        }
    }

    private static List<PlannedRead> Merge(List<PlannedRead> reads, long mergeGap, long maxBytes)
    {
        var merged = new List<PlannedRead>(reads.Count);
        var total = 0L;
        foreach (var read in reads)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                var end = Math.Max(last.End, read.End);
                var grown = end - last.Start;

                // A merge has to pass the same ceiling as a read added on its own: checked only on entry,
                // 32 blocks four kilobytes apart merged into one range per cluster and the fetch grew to
                // 119,2 MB for a pass that planned to read 67,1 MB - the ceiling held for the ranges that
                // did not merge and was ignored by every one that did.
                if (read.Start - last.End <= mergeGap && grown <= MaxWindow && total - last.Length + grown <= maxBytes)
                {
                    total = total - last.Length + grown;
                    merged[^1] = new PlannedRead(last.Start, (int)grown);
                    continue;
                }
            }

            if (total + read.Length > maxBytes)
            {
                break;
            }

            total += read.Length;
            merged.Add(read);
        }

        return merged;
    }
}
