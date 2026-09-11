#!/usr/bin/env python3
"""Regression checks for the SubSync plugin, run against the real plugin assembly.

Two halves: C# checks compiled against Jellyfin.Plugin.SubSync (scheduling, extraction, language
matching, configuration, progress accounting) and Python checks that build Matroska fixtures and
assert how much the reader actually touches.

Run from the repository root or anywhere else:

    python3 tests/run_checks.py

Requires the .NET 9 SDK and python3. No ffsubsync binary and no media library are needed: the
fixtures are synthetic and the tests never call ffsubsync.
"""
import os
import pathlib
import shutil
import subprocess

# Repository root, discovered from this file's location so the tree can be moved or cloned
# anywhere.
REPO = str(pathlib.Path(__file__).resolve().parents[1])

# dotnet: explicit override, then PATH, then the usual install locations.
def _find_dotnet():
    candidate = os.environ.get('DOTNET')
    if candidate:
        return candidate
    on_path = shutil.which('dotnet')
    if on_path:
        return on_path
    roots = [os.environ.get('DOTNET_ROOT'), str(pathlib.Path.home() / '.dotnet'),
             '/usr/share/dotnet', '/usr/local/share/dotnet', '/opt/dotnet', '/opt/data/.dotnet']
    for root in roots:
        if root and os.path.isfile(os.path.join(root, 'dotnet')):
            return os.path.join(root, 'dotnet')
    raise SystemExit('dotnet not found: install the .NET 10 SDK, put it on PATH, or set DOTNET=/path/to/dotnet')


DOTNET = _find_dotnet()

# Scratch space for the generated harness project and fixtures. Kept inside the repository (and
# git-ignored) so a failed run can be inspected, but never committed.
WORK = os.environ.get('TESTS_WORK') or os.path.join(REPO, '.tests-work')
ENV = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1')

PROGRAM = r"""
using Jellyfin.Plugin.SubSync.Services;

int failures = 0;

void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + label + (detail.Length > 0 ? "   [" + detail + "]" : ""));
    if (!ok) failures++;
}

// ---------------- SyncJobMode ----------------
Check("normalize(null) == auto (default)", SyncJobMode.Normalize(null) == "auto");
Check("normalize('ULTIMATE') == ultimate", SyncJobMode.Normalize("ULTIMATE") == "ultimate");
Check("normalize('nonsense') == normal", SyncJobMode.Normalize("nonsense") == "normal");
Check("parallel is parallel", SyncJobMode.IsParallel("parallel"));
Check("ultimate is parallel", SyncJobMode.IsParallel("ultimate"));
Check("fast is not parallel", !SyncJobMode.IsParallel("fast"));
Check("fast uses speech cache", SyncJobMode.UsesSpeechCache("fast"));
Check("ultimate uses speech cache", SyncJobMode.UsesSpeechCache("ultimate"));
Check("parallel does not use speech cache", !SyncJobMode.UsesSpeechCache("parallel"));
Check("normal uses neither", !SyncJobMode.IsParallel("normal") && !SyncJobMode.UsesSpeechCache("normal"));

// ---------------- SelectWave ----------------
SyncJob Job(Guid item, string batch, string mode, int index) => new()
{
    ItemId = item,
    BatchId = batch,
    Mode = mode,
    BatchIndex = index
};

var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid(); var d = Guid.NewGuid();

// 4 tracks of file A followed by one track each of B, C, D — parallel, limit 4
var queue = new List<SyncJob>
{
    Job(a, "batch1", "parallel", 0),
    Job(a, "batch1", "parallel", 1),
    Job(a, "batch1", "parallel", 2),
    Job(b, "batch1", "parallel", 3),
    Job(c, "batch1", "parallel", 4),
    Job(d, "batch1", "parallel", 5),
};
var wave = SubSyncService.SelectWave(queue, "parallel", "batch1", 4);
Check("wave keeps one slot per media file", wave.Count == 4, "got " + wave.Count);
Check("wave has no duplicate media", wave.Select(j => j.ItemId).Distinct().Count() == wave.Count);
Check("wave favours different files (a,b,c,d)", wave.Select(j => j.ItemId).SequenceEqual(new[] { a, b, c, d }));
Check("remaining tracks of the same file stay queued",
    queue.Except(wave).Count() == 2 && queue.Except(wave).All(j => j.ItemId == a));

// limit 1 for normal mode
var normalQueue = new List<SyncJob>
{
    Job(a, "batch1", "normal", 0),
    Job(b, "batch1", "normal", 1),
    Job(c, "batch1", "normal", 2),
};
var waveNormal = SubSyncService.SelectWave(normalQueue, "normal", "batch1", 1);
Check("normal mode takes a single job", waveNormal.Count == 1);
Check("normal mode takes the head of the queue", waveNormal[0].BatchIndex == 0);

// mode grouping: ultimate jobs are not pulled into a parallel wave
var mixed = new List<SyncJob>
{
    Job(a, "b", "ultimate", 0),
    Job(b, "b", "parallel", 1),
    Job(c, "b", "ultimate", 2),
};
var waveUlt = SubSyncService.SelectWave(mixed, "ultimate", "b", 4);
Check("wave only contains its own mode", waveUlt.All(j => j.Mode == "ultimate"), string.Join(",", waveUlt.Select(j => j.Mode)));
Check("wave skipped the parallel job", waveUlt.Count == 2, "got " + waveUlt.Count);

// batch isolation
var twoBatches = new List<SyncJob>
{
    Job(a, "b1", "ultimate", 0),
    Job(b, "b2", "ultimate", 1),
    Job(c, "b1", "ultimate", 2),
};
var waveBatch = SubSyncService.SelectWave(twoBatches, "ultimate", "b1", 4);
Check("batches never interleave", waveBatch.All(j => j.BatchId == "b1") && waveBatch.Count == 2, "got " + waveBatch.Count);

// standalone jobs (no batch) group together
var standalone = new List<SyncJob> { Job(a, null!, "parallel", 0), Job(b, null!, "parallel", 1), Job(c, "b1", "parallel", 2) };
var waveStandalone = SubSyncService.SelectWave(standalone, "parallel", null, 4);
Check("standalone jobs form their own wave", waveStandalone.Count == 2 && waveStandalone.All(j => j.BatchId is null));

// limit larger than the queue
var waveAll = SubSyncService.SelectWave(queue, "parallel", "batch1", 8);
Check("limit above queue size returns distinct files only", waveAll.Count == 4);

// ---------------- SpeechCache ----------------
var tmp = Path.Combine(Path.GetTempPath(), "speechtest_" + Guid.NewGuid().ToString("N") + ".mkv");
File.WriteAllBytes(tmp, new byte[2048]);
var k1 = SpeechCache.KeyFor(tmp, "subs_then_webrtc", "ffsubsync 0.5.1");
var k2 = SpeechCache.KeyFor(tmp, "subs_then_webrtc", "ffsubsync 0.5.1");
var k3 = SpeechCache.KeyFor(tmp, "webrtc", "ffsubsync 0.5.1");
var k4 = SpeechCache.KeyFor(tmp, "subs_then_webrtc", "ffsubsync 0.6.0");
File.SetLastWriteTimeUtc(tmp, DateTime.UtcNow.AddMinutes(5));
var k5 = SpeechCache.KeyFor(tmp, "subs_then_webrtc", "ffsubsync 0.5.1");
Check("key is stable for the same file/settings", k1 == k2);
Check("key changes with the VAD method", k1 != k3);
Check("key changes with the engine build", k1 != k4);
Check("key changes when the file changes", k1 != k5);
Check("cache miss reports nothing cached", SpeechCache.TryGet(k1) is null);
Check("describe works on an empty cache", SpeechCache.Describe().Length > 0, SpeechCache.Describe());
File.Delete(tmp);

// ---------------- SelectReferenceStream ----------------
var textOnly = new List<string> { "subrip" };
var twoText = new List<string> { "subrip", "ass" };
var textAndImage = new List<string> { "subrip", "hdmv_pgs_subtitle" };
var imageOnly = new List<string> { "hdmv_pgs_subtitle", "dvd_subtitle" };

Check("external sidecar keeps the default reference",
    SubSyncService.SelectReferenceStream(false, twoText, 0) is null);
Check("single text track forces audio (never itself)",
    SubSyncService.SelectReferenceStream(true, textOnly, 0) == "a:0",
    SubSyncService.SelectReferenceStream(true, textOnly, 0) ?? "null");
Check("two text tracks -> uses the other one",
    SubSyncService.SelectReferenceStream(true, twoText, 0) == "s:1",
    SubSyncService.SelectReferenceStream(true, twoText, 0) ?? "null");
Check("target second track -> uses the first",
    SubSyncService.SelectReferenceStream(true, twoText, 1) == "s:0");
Check("skips the image track when choosing",
    SubSyncService.SelectReferenceStream(true, textAndImage, 0) == "s:0" ||
    SubSyncService.SelectReferenceStream(true, new List<string> { "hdmv_pgs_subtitle", "subrip" }, 0) == "s:1",
    SubSyncService.SelectReferenceStream(true, new List<string> { "hdmv_pgs_subtitle", "subrip" }, 0) ?? "null");
Check("image-only file forces audio",
    SubSyncService.SelectReferenceStream(true, imageOnly, 0) == "a:0");
Check("no probe data at all forces audio",
    SubSyncService.SelectReferenceStream(true, new List<string>(), 0) == "a:0");

// ffmpeg probe parsing
var probe = "  Stream #0:0: Video: h264\n  Stream #0:1(eng): Audio: aac\n"
    + "  Stream #0:2(eng): Subtitle: subrip (srt)\n  Stream #0:3(swe): Subtitle: hdmv_pgs_subtitle";
Check("probe indexes pick subtitle streams only",
    SubSyncService.ParseProbeSubtitleIndexes(probe).SequenceEqual(new[] { 2, 3 }),
    string.Join(",", SubSyncService.ParseProbeSubtitleIndexes(probe)));
var codecs = SubSyncService.ParseProbeSubtitleCodecs(probe);
Check("probe codecs are captured in order", codecs.Count == 2 && codecs[0].StartsWith("subrip") && codecs[1].StartsWith("hdmv_pgs"),
    string.Join("|", codecs));
Check("image track detected as image, text track not",
    !SubSyncService.SelectReferenceStream(true, codecs, 0)!.StartsWith("s:1"));

// ---------------- ResolveAuto ----------------
Check("auto: single subtitle stays sequential", SyncJobMode.ResolveAuto(1, 1) == "normal");
Check("auto: one file, several subs -> ultimate (reuse AND parallel)", SyncJobMode.ResolveAuto(4, 1) == "ultimate");
Check("auto: several subs of one file run in parallel", SyncJobMode.IsParallel(SyncJobMode.ResolveAuto(4, 1)));
Check("auto: reuse still applies for one file", SyncJobMode.UsesSpeechCache(SyncJobMode.ResolveAuto(4, 1)));
Check("auto: several files -> ultimate", SyncJobMode.ResolveAuto(9, 6) == "ultimate");
Check("auto: two files, two subs -> ultimate", SyncJobMode.ResolveAuto(2, 2) == "ultimate");
Check("auto: zero tasks is harmless", SyncJobMode.ResolveAuto(0, 0) == "normal");
Check("auto is the default", SyncJobMode.Normalize(null) == "auto");
Check("auto is an accepted value", SyncJobMode.Normalize("auto") == "auto");

// ---------------- WavePolicy: volume gating and media sharing ----------------
SyncJob PJob(Guid item, string batch, string mode, int index) => new()
{
    ItemId = item, BatchId = batch, Mode = mode, BatchIndex = index
};

var v1 = Guid.NewGuid(); var v2 = Guid.NewGuid(); var v3 = Guid.NewGuid();

// Heavy work is no longer throttled per volume: with four workers, four heavy first-time
// reads on one shared disk all enter the same wave.
var heavyQueue = new List<SyncJob>
{
    PJob(v1, "b", "ultimate", 0),
    PJob(v2, "b", "ultimate", 1),
    PJob(v3, "b", "ultimate", 2),
};
var heavyWave = SubSyncService.SelectWave(heavyQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    IsHeavyIo = _ => true,
});
Check("heavy work on one disk is not throttled", heavyWave.Count == 3, "got " + heavyWave.Count);
Check("heavy wave keeps queue order",
    heavyWave.Select(j => j.BatchIndex).SequenceEqual(new[] { 0, 1, 2 }),
    "got " + string.Join(",", heavyWave.Select(j => j.BatchIndex)));

var heavyCapped = SubSyncService.SelectWave(heavyQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 2,
    IsHeavyIo = _ => true,
});
Check("worker count is the only limit for heavy work", heavyCapped.Count == 2, "got " + heavyCapped.Count);

// several subtitles of one file: only when its speech analysis is cached
var sameFileQueue = new List<SyncJob>
{
    PJob(v1, "b", "ultimate", 0),
    PJob(v1, "b", "ultimate", 1),
    PJob(v2, "b", "ultimate", 2),
};
var shareWave = SubSyncService.SelectWave(sameFileQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    IsHeavyIo = _ => false,
    CanShareMediaFile = _ => true,
});
Check("cached file may share a wave", shareWave.Count == 3, "got " + shareWave.Count);

var noShareWave = SubSyncService.SelectWave(sameFileQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    IsHeavyIo = _ => false,
    CanShareMediaFile = _ => false,
});
Check("uncached file never shares", noShareWave.Count == 2 && noShareWave.Count(j => j.ItemId == v1) == 1,
    "got " + noShareWave.Count);

var heavyFirstWave = SubSyncService.SelectWave(sameFileQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    IsHeavyIo = _ => true,
    CanShareMediaFile = _ => true,
});
// The policy decides sharing, not the job's weight. This is what lets a file's later subtitles
// run in parallel once its reference subtitle has been extracted - the case that used to report
// "using 2/4 workers" on a ten-track episode.
Check("a heavy job shares a file when the policy allows it",
    heavyFirstWave.Count(j => j.ItemId == v1) == 2, "v1 count " + heavyFirstWave.Count(j => j.ItemId == v1));
Check("and a heavy job still never shares when the policy refuses",
    SubSyncService.SelectWave(sameFileQueue, "ultimate", "b", new SubSyncService.WavePolicy
    {
        Limit = 4,
        IsHeavyIo = _ => true,
        CanShareMediaFile = _ => false,
    }).Count(j => j.ItemId == v1) == 1);

// ---------------- Volume spreading is a preference, not a limit ----------------
// Two files on volA, one on volB. With two workers the wave should take one from each
// disk rather than both files of the first disk.
var volA1 = Guid.NewGuid(); var volA2 = Guid.NewGuid(); var volB1 = Guid.NewGuid();
var spreadQueue = new List<SyncJob>
{
    PJob(volA1, "b", "ultimate", 0),
    PJob(volA2, "b", "ultimate", 1),
    PJob(volB1, "b", "ultimate", 2),
};
var spreadVolumes = new Dictionary<Guid, string> { [volA1] = "/volA", [volA2] = "/volA", [volB1] = "/volB" };
var spreadWave = SubSyncService.SelectWave(spreadQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 2,
    VolumeOf = j => spreadVolumes[j.ItemId],
    IsHeavyIo = _ => true,
});
Check("wave spreads over volumes", spreadWave.Count == 2
    && spreadWave.Any(j => j.ItemId == volA1) && spreadWave.Any(j => j.ItemId == volB1),
    string.Join(",", spreadWave.Select(j => spreadVolumes[j.ItemId])));
Check("spread wave keeps queue order first", spreadWave[0].BatchIndex == 0, "got " + spreadWave[0].BatchIndex);

// Same queue, four workers: still one from each disk first, then the spare /volA file.
var spreadWide = SubSyncService.SelectWave(spreadQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    VolumeOf = j => spreadVolumes[j.ItemId],
    IsHeavyIo = _ => true,
});
Check("wide wave takes everything, spread included", spreadWide.Count == 3, "got " + spreadWide.Count);
Check("wide wave keeps queue order",
    spreadWide.Select(j => j.BatchIndex).SequenceEqual(new[] { 0, 1, 2 }),
    "got " + string.Join(",", spreadWide.Select(j => j.BatchIndex)));

// One volume only: spreading has nothing to work with, so the wave still fills up.
var singleVolume = new List<SyncJob>
{
    PJob(volA1, "b", "ultimate", 0),
    PJob(volA2, "b", "ultimate", 1),
    PJob(Guid.NewGuid(), "b", "ultimate", 2),
    PJob(Guid.NewGuid(), "b", "ultimate", 3),
};
var singleWave = SubSyncService.SelectWave(singleVolume, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    VolumeOf = _ => "/volA",
    IsHeavyIo = _ => true,
});
Check("one volume still runs at full width", singleWave.Count == 4, "got " + singleWave.Count);

// Unknown volumes must not block either (no mount information available).
var unknownWave = SubSyncService.SelectWave(singleVolume, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    IsHeavyIo = _ => true,
});
Check("no volume information still runs at full width", unknownWave.Count == 4, "got " + unknownWave.Count);

// Two subtitles of the SAME file must not be pulled apart by the spreading pass: the file
// rule still decides, and an uncached file still runs one worker only.
var sameFileSpread = new List<SyncJob>
{
    PJob(volA1, "b", "ultimate", 0),
    PJob(volA1, "b", "ultimate", 1),
    PJob(volB1, "b", "ultimate", 2),
};
var sameFileWave = SubSyncService.SelectWave(sameFileSpread, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4,
    VolumeOf = j => spreadVolumes[j.ItemId],
    IsHeavyIo = _ => true,
    CanShareMediaFile = _ => true,
});
Check("a sharing policy also beats the spreading pass",
    sameFileWave.Count(j => j.ItemId == volA1) == 2, "volA1 count " + sameFileWave.Count(j => j.ItemId == volA1));
Check("the other disk still joins the wave", sameFileWave.Any(j => j.ItemId == volB1));

// ---------------- Waves are bounded by the worker count alone ----------------
var wideQueue = new List<SyncJob>
{
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 0, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 1, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 2, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 3, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 4, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued }
};

// Every one of these is a heavy first-time read and they all live on the same volume:
// a wave must still fill up to the worker count, with no storage throttling.
var full = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false,
});
Check("four heavy tasks on one volume fill the wave", full.Count == 4, "got " + full.Count);
Check("the wave takes the first queued tasks in order",
    full.Select(j => j.BatchIndex).SequenceEqual(new[] { 0, 1, 2, 3 }),
    "got " + string.Join(",", full.Select(j => j.BatchIndex)));

var twoWorkers = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 2, IsHeavyIo = _ => true, CanShareMediaFile = _ => false,
});
Check("worker count is the only bound (2 requested -> 2 in flight)", twoWorkers.Count == 2, "got " + twoWorkers.Count);

// There is no per-volume budget left on the policy type: volume information may only
// express a preference (VolumeOf), never a cap on how many jobs a volume contributes.
Check("WavePolicy has no per-volume budget",
    !typeof(SubSyncService.WavePolicy).GetProperties().Any(p =>
        p.Name.Contains("PerVolume", StringComparison.OrdinalIgnoreCase)
        || p.Name.Contains("Budget", StringComparison.OrdinalIgnoreCase)),
    string.Join(",", typeof(SubSyncService.WavePolicy).GetProperties().Select(p => p.Name)));

Console.WriteLine();
// ---------------- Matroska extraction: bytes read, not wall time ----------------
// Fixtures are built by make_remux.py (the same generator used for the 60 GB benchmark) and
// handed in through the environment. The assertions are about *bytes read*: on a fast local
// disk, wall time hides a reader that walks the whole file, which is exactly how extraction
// stayed slow on a real remux while looking fine in a smaller test.
var mkvScenarios = new[]
{
    (Path: Environment.GetEnvironmentVariable("MKV_FIX_CUES"), Name: "with subtitle cue points", Method: "seekhead-cues", Expected: EnvInt("MKV_FIX_CUES_EXPECT")),
    (Path: Environment.GetEnvironmentVariable("MKV_FIX_NOSUB"), Name: "no subtitle cue points", Method: "metadata-scan", Expected: EnvInt("MKV_FIX_NOSUB_EXPECT")),
    (Path: Environment.GetEnvironmentVariable("MKV_FIX_NOCUES"), Name: "no cue index at all", Method: "metadata-scan", Expected: EnvInt("MKV_FIX_NOCUES_EXPECT")),
    (Path: Environment.GetEnvironmentVariable("MKV_FIX_WALK"), Name: "cue index without block offsets", Method: "seekhead-cues", Expected: EnvInt("MKV_FIX_WALK_EXPECT")),
};

foreach (var scenario in mkvScenarios)
{
    if (string.IsNullOrEmpty(scenario.Path) || !File.Exists(scenario.Path))
    {
        Check($"fixture present ({scenario.Name})", false, "missing " + scenario.Path);
        continue;
    }

    var extracted = MkvSubtitleExtractor.TryExtract(scenario.Path, 0, out var mkvSrt, out var mkvReason, null, out var mkvStats);
    var readMb = mkvStats.BytesRead / 1e6;
    var fileMb = new FileInfo(scenario.Path).Length / 1e6;
    var found = mkvSrt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;

    Check($"extraction succeeds ({scenario.Name})", extracted, mkvReason);
    Check($"every subtitle block found ({scenario.Name})", found == scenario.Expected, $"{found} of {scenario.Expected}");
    Check($"method is {scenario.Method} ({scenario.Name})", mkvStats.Method == scenario.Method, mkvStats.Method);
    Check($"reads a fraction of the file ({scenario.Name})", readMb < fileMb * 0.2, $"{readMb:0.00} MB of {fileMb:0.0} MB");

    if (scenario.Method == "seekhead-cues")
    {
        // Video payloads are holes in the fixture: a reader that touched block payloads for a
        // track it does not want would read the whole file here.
        Check("cue-index extraction stays under 1 MB", mkvStats.BytesRead < 1_000_000,
            $"{mkvStats.BytesRead / 1e6:0.000} MB in {mkvStats.ReadCalls} reads");

        // A cue costs at most two reads: the cluster header that locates the block (the index
        // stores the block's offset relative to the cluster's data) and the block itself. Anything
        // beyond that means the walk re-reads, and on a high-latency share that is the whole cost of
        // an extraction - a real run showed 456 reads for 224 cues at ~100 ms each, 50 s per file.
        var overhead = 12;
        Check($"at most two reads per cue ({scenario.Name})",
            mkvStats.ReadCalls <= (found * 2) + overhead,
            $"{mkvStats.ReadCalls} reads for {found} cues ({(double)mkvStats.ReadCalls / Math.Max(1, found):0.00} per cue)");
    }
}

// The indexed path must never read more than walking the clusters to find the same subtitles:
// if CueRelativePosition handling ever regresses, the indexed path starts paying for clusters it
// should have skipped. The two used to differ sharply (0.05 MB against 0.61 MB) because the walk
// read a 64 KB window per probe; now the walk sizes its window to the storage, and on a local disk
// that is a few kilobytes, so on this fixture the two are equal. On storage where a read costs a
// millisecond or more the walk is deliberately given whole-file-sized windows instead, and the gap
// is large again (few reads, the file's bytes) - which is why this is "no worse", not "less".
var indexedStats = new MkvExtractionStats();
var walkStats = new MkvExtractionStats();
var indexedPath = Environment.GetEnvironmentVariable("MKV_FIX_CUES");
var walkPath = Environment.GetEnvironmentVariable("MKV_FIX_WALK");
if (!string.IsNullOrEmpty(indexedPath) && !string.IsNullOrEmpty(walkPath))
{
    MkvSubtitleExtractor.TryExtract(indexedPath, 0, out _, out _, null, out indexedStats);
    MkvSubtitleExtractor.TryExtract(walkPath, 0, out _, out _, null, out walkStats);
    Check("indexed extraction never reads more than the block walk",
        indexedStats.BytesRead <= walkStats.BytesRead,
        $"indexed {indexedStats.BytesRead / 1e6:0.00} MB vs walk {walkStats.BytesRead / 1e6:0.00} MB");
    Check("indexed extraction needs fewer reads than the block walk",
        indexedStats.ReadCalls <= walkStats.ReadCalls,
        $"indexed {indexedStats.ReadCalls} vs walk {walkStats.ReadCalls}");
    Check("block-offsets are actually used", indexedStats.IndexedMisses == 0,
        $"{indexedStats.IndexedMisses} misses");
}

// Several tracks of one file in a single pass. Every requested track must come out identical to what
// a pass of its own would produce, and the whole set must cost fewer reads than doing them one after
// another: that sharing is the only reason the multi-track pass exists.
var multiPath = Environment.GetEnvironmentVariable("MKV_FIX_MULTI");
if (!string.IsNullOrEmpty(multiPath) && File.Exists(multiPath))
{
    var ordinals = new[] { 0, 1, 2 };
    var many = new Dictionary<int, string>();
    var manyOk = MkvSubtitleExtractor.TryExtractMany(multiPath, ordinals, out many, out var manyReason, out var manyStats);
    Check("one pass extracts every requested track", manyOk && many.Count == ordinals.Length,
        manyOk ? $"{many.Count} of {ordinals.Length} tracks" : manyReason);

    var separateReads = 0;
    var separateCues = 0;
    foreach (var ordinal in ordinals)
    {
        var single = new MkvExtractionStats();
        var singleOk = MkvSubtitleExtractor.TryExtract(
            multiPath, ordinal, out var singleText, out var singleReason, null, out single, null, null, default);
        Check($"track {ordinal} extracted on its own", singleOk, singleReason);
        Check($"track {ordinal} is identical with and without sharing",
            many.TryGetValue(ordinal, out var shared) && shared == singleText,
            $"shared {(many.ContainsKey(ordinal) ? many[ordinal].Length : -1)} chars vs single {singleText.Length} chars");
        Check($"track {ordinal} carries only its own text",
            !singleText.Contains("Track " + ordinal + " line", StringComparison.Ordinal) == (ordinal == 0),
            "track text looks like another track's");
        separateReads += single.ReadCalls;
        separateCues += single.SubtitleBlocks;
    }

    Check("sharing one pass reads less than separate passes",
        manyStats.ReadCalls < separateReads,
        $"shared {manyStats.ReadCalls} reads vs {separateReads} separate "
        + $"({Math.Round(100.0 * manyStats.ReadCalls / Math.Max(1, separateReads))}%)");
    Check("the shared pass returns every track's cues",
        manyStats.SubtitleBlocks + manyStats.AlsoBlocks == separateCues,
        $"{manyStats.SubtitleBlocks} + {manyStats.AlsoBlocks} vs {separateCues}");
}

// Our own synced sidecars carry a marker that Jellyfin reads as a language name, so they must never
// be offered as tracks. Every name shape we write, plus names that must not match.
foreach (var (name, expected) in new (string Name, bool Expected)[]
{
    ("Quicksand - S01E01 - Maja WEBDL-1080p.SYNCED.dan.srt", true),
    ("Black.Mirror.2011.S05E02.1080p.NF.WEB-DL.H265.HONE-SYNCED.heb.srt", true),
    ("Sommaren med slakten S01E01 H.265 EAC3.SYNCED.srt", true),
    ("/media/serier/Show S01E01.SYNCED.swe.srt", true),
    ("Quicksand - S01E01 - Maja WEBDL-1080p.dan.srt", false),
    ("Sommaren med slakten S01E01 H.265 EAC3.srt", false),
    ("Show.S01E01.synced-by-hand.srt", false),
    ("", false),
})
{
    var got = SrtWriter.IsSyncedSidecarName(name);
    Check($"sidecar name '{name}' is {(expected ? "ours" : "not ours")}", got == expected, got.ToString());
}

// Framerate correction is opt-in, and opting out needs BOTH flags: measured against the bundled
// engine, the default and either flag alone still rescaled a 4.17% longer span to 0.960x.
Check("opt-in framerate: the engine is told to leave timings alone",
    string.Join(" ", SubSyncService.FramerateArgs(false, false)) == "--no-fix-framerate --skip-infer-framerate-ratio",
    string.Join(" ", SubSyncService.FramerateArgs(false, false)));
Check("opt-in framerate: asking for correction passes no opt-out flags",
    !SubSyncService.FramerateArgs(true, false).Any(),
    string.Join(" ", SubSyncService.FramerateArgs(true, false)));
Check("opt-in framerate: golden section is only passed with correction on",
    string.Join(" ", SubSyncService.FramerateArgs(true, true)) == "--gss"
    && !SubSyncService.FramerateArgs(false, true).Contains("--gss"),
    string.Join(" ", SubSyncService.FramerateArgs(false, true)));

// The guard that decides whether a measured result may be written. Without correction asked for, any
// rescale is the failure this exists for: a 4% scale ruins a whole file rather than a few seconds.
foreach (var (ratio, shiftMs, fix, expected, why) in new (double, long, bool, bool, string)[]
{
    (1.0000, 120, false, true, "a normal offset passes"),
    (0.9600, -51900, false, false, "a 4% rescale nobody asked for is refused"),
    (1.0427, 65579, false, false, "a PAL-sized rescale nobody asked for is refused"),
    (0.6163, -365926, false, false, "a 38% compression is refused"),
    (1.0000, 216000, false, false, "an offset beyond double the bound is refused"),
    (1.0427, 65579, true, true, "a real framerate pair is allowed when asked for"),
    (0.8056, -220187, true, false, "an invented ratio is refused even when asked for"),
    (1.0004, 400, true, true, "a sub-1% fit is tolerated when correction is on"),
})
{
    var got = SubSyncService.IsRescaleAcceptable(ratio, shiftMs, 60, fix);
    Check($"rescale guard: {why}", got == expected, $"ratio={ratio} shift={shiftMs} fix={fix} -> {got}");
}

// ---------------- Worker pool: slots, not groups ----------------
// The failure this guards against: three jobs finished, the fourth kept running, and the three
// idle workers waited for it instead of taking the next jobs from the queue.
var poolQueue = new List<SyncJob>();
for (var i = 0; i < 12; i++)
{
    poolQueue.Add(PJob(Guid.NewGuid(), "pool", "ultimate", i));
}

List<SyncJob> Plan(int poolLimit, params SyncJob[] busy) =>
    SubSyncService.PlanStart(
        poolQueue, busy, "ultimate", "pool", poolLimit,
        _ => "/library", _ => true, _ => false);

var freshPool = Plan(4);
Check("empty pool fills every slot", freshPool.Count == 4, "got " + freshPool.Count);

var threeBusy = Plan(4, poolQueue[0], poolQueue[1], poolQueue[2]);
Check("one free slot starts exactly one job", threeBusy.Count == 1, "got " + threeBusy.Count);
Check("the job started is the next in the queue",
    threeBusy[0].BatchIndex == 3, "got " + threeBusy[0].BatchIndex);

Check("a full pool starts nothing", Plan(4, poolQueue[0], poolQueue[1], poolQueue[2], poolQueue[3]).Count == 0);
Check("single-worker mode runs one job at a time", Plan(1, poolQueue[0]).Count == 0);

// A media file already being read must not get a second worker before its analysis is cached.
var poolFileX = Guid.NewGuid();
var poolFileY = Guid.NewGuid();
var poolSameFileQueue = new List<SyncJob>
{
    PJob(poolFileX, "pool", "ultimate", 0),
    PJob(poolFileX, "pool", "ultimate", 1),
    PJob(poolFileY, "pool", "ultimate", 2),
};
var poolRunningX = PJob(poolFileX, "pool", "ultimate", -1);
var sameFilePlan = SubSyncService.PlanStart(
    poolSameFileQueue, new[] { poolRunningX }, "ultimate", "pool", 4,
    _ => "/library", _ => true, _ => false);
Check("a file being read is not started twice",
    sameFilePlan.All(j => j.ItemId != poolFileX), "planned " + sameFilePlan.Count(j => j.ItemId == poolFileX) + " for the running file");
Check("a different file still starts", sameFilePlan.Any(j => j.ItemId == poolFileY));

// Idle disks are preferred, but never required: two volumes, one already being read.
var poolVolA = Guid.NewGuid();
var poolVolB = Guid.NewGuid();
var poolSpreadQueue = new List<SyncJob>
{
    PJob(poolVolA, "pool", "ultimate", 0),
    PJob(poolVolA, "pool", "ultimate", 1),
    PJob(poolVolB, "pool", "ultimate", 2),
};
var poolBusyA = PJob(poolVolA, "pool", "ultimate", -1);
var spreadPlan = SubSyncService.PlanStart(
    poolSpreadQueue, new[] { poolBusyA }, "ultimate", "pool", 2,
    j => j.ItemId == poolVolB ? "/volB" : "/volA", _ => true, _ => false);
Check("the idle disk is taken first",
    spreadPlan.Count > 0 && spreadPlan[0].ItemId == poolVolB, string.Join(",", spreadPlan.Select(j => j.ItemId)));

// ---------------- Any worker count is honoured ----------------
// 1, 3, 32 must all mean exactly what they say. A silent rewrite of the setting (it used to be
// capped at 8 in five places) is worse than any performance opinion the code might have.
Check("1 worker stays 1", SubSyncService.NormalizeWorkers(1) == 1, "got " + SubSyncService.NormalizeWorkers(1));
Check("3 workers stay 3", SubSyncService.NormalizeWorkers(3) == 3, "got " + SubSyncService.NormalizeWorkers(3));
Check("8 workers stay 8", SubSyncService.NormalizeWorkers(8) == 8, "got " + SubSyncService.NormalizeWorkers(8));
Check("32 workers stay 32", SubSyncService.NormalizeWorkers(32) == 32, "got " + SubSyncService.NormalizeWorkers(32));
Check("the ceiling is 64", SubSyncService.MaxParallelWorkers == 64, "got " + SubSyncService.MaxParallelWorkers);
Check("64 workers stay 64", SubSyncService.NormalizeWorkers(64) == 64, "got " + SubSyncService.NormalizeWorkers(64));
Check("a mistyped value falls back to the ceiling, not to 8",
    SubSyncService.NormalizeWorkers(10000) == 64, "got " + SubSyncService.NormalizeWorkers(10000));
Check("zero or negative means one worker", SubSyncService.NormalizeWorkers(0) == 1 && SubSyncService.NormalizeWorkers(-3) == 1);

// The pool must fill to whatever width is configured, for any width.
var widthQueue = new List<SyncJob>();
for (var i = 0; i < 200; i++)
{
    widthQueue.Add(PJob(Guid.NewGuid(), "wide", "parallel", i));
}

var widthShortQueue = new List<SyncJob>();
for (var i = 0; i < 40; i++)
{
    widthShortQueue.Add(PJob(Guid.NewGuid(), "wide", "parallel", i));
}

int PlanWidth(int limit, List<SyncJob>? queue = null) => SubSyncService.PlanStart(
    queue ?? widthQueue, Array.Empty<SyncJob>(), "parallel", "wide", limit,
    _ => "/library", _ => true, _ => false).Count;

Check("width 3 starts three", PlanWidth(3) == 3, "got " + PlanWidth(3));
Check("width 16 starts sixteen", PlanWidth(16) == 16, "got " + PlanWidth(16));
Check("width 32 starts thirty-two", PlanWidth(32) == 32, "got " + PlanWidth(32));
Check("width 64 starts sixty-four", PlanWidth(64) == 64, "got " + PlanWidth(64));
Check("a width wider than the queue starts only what exists",
    PlanWidth(200, widthShortQueue) == 40, "got " + PlanWidth(200, widthShortQueue));

// ---------------- Progress must follow real work ----------------
Check("fraction from a Matroska progress line",
    Math.Abs((SubSyncService.ExtractionFraction("reading subtitle 128/326 \u00b7 1.3 MB, 341 reads") ?? -1) - 0.3926) < 0.001,
    "got " + SubSyncService.ExtractionFraction("reading subtitle 128/326 \u00b7 1.3 MB, 341 reads"));
Check("half done is half", (SubSyncService.ExtractionFraction("reading subtitle 50/100") ?? -1) == 0.5);
Check("complete is one", (SubSyncService.ExtractionFraction("reading subtitle 326/326") ?? -1) == 1.0);
Check("a line without counters has no fraction", SubSyncService.ExtractionFraction("scanning clusters") is null);
Check("a zero total cannot divide by zero", SubSyncService.ExtractionFraction("reading subtitle 5/0") is null);
Check("null is tolerated", SubSyncService.ExtractionFraction(null) is null);

// ---------------- Sync phases name what actually happens ----------------
// A sibling-subtitle reference keeps the cheap path (0.6 s/track); conflating its label with
// "Analysing the audio" made every subtitle of a file look like a repeated audio analysis.
Check("a reused analysis says so",
    SubSyncService.SyncPhaseLabel(true, true) == "Syncing (from cache)",
    SubSyncService.SyncPhaseLabel(true, true));
Check("the audio path says it analyses the audio",
    SubSyncService.SyncPhaseLabel(false, true) == "Syncing (analysing the audio)",
    SubSyncService.SyncPhaseLabel(false, true));
Check("a sibling subtitle reference says so, not 'analysing speech'",
    SubSyncService.SyncPhaseLabel(false, false) == "Syncing (using another subtitle track)",
    SubSyncService.SyncPhaseLabel(false, false));
Check("no label claims speech analysis when none happens",
    !SubSyncService.SyncPhaseLabel(false, false).Contains("analysing", StringComparison.OrdinalIgnoreCase)
    && !SubSyncService.SyncPhaseLabel(false, false).Contains("analyzing", StringComparison.OrdinalIgnoreCase));

// ---------------- Reading the reference track out of the video once ----------------
// Letting ffsubsync pull a subtitle stream out of the video makes it demux the whole file
// (measured: 12.5 s, 8218 MB for an 8.2 GB episode, per subtitle). The reference is therefore
// extracted once by our own reader and passed as a small file; the position has to be read out of
// the stream specifier to do that.
Check("a subtitle specifier yields its position", SubSyncService.SubtitleStreamOrdinal("s:3") == 3);
Check("position zero is valid", SubSyncService.SubtitleStreamOrdinal("s:0") == 0);
Check("audio is not a subtitle specifier", SubSyncService.SubtitleStreamOrdinal("a:0") == -1);
Check("a missing specifier is rejected", SubSyncService.SubtitleStreamOrdinal(null) == -1);
Check("junk is rejected", SubSyncService.SubtitleStreamOrdinal("s:") == -1 && SubSyncService.SubtitleStreamOrdinal("nonsense") == -1);
Check("a negative position is rejected", SubSyncService.SubtitleStreamOrdinal("s:-2") == -1);

// ---------------- A ten-track episode must not cap the batch width ----------------
// The candidate scan stopped after limit*4 jobs. With ten subtitles per episode those candidates
// covered two media files, so a four-worker batch ran two wide ("using 2/4 workers") no matter
// what was configured. The scan must be wide enough to find a wave, not merely to hold one.
var wideFiles = new List<SyncJob>();
for (var file = 0; file < 12; file++)
{
    var fileId = Guid.NewGuid();
    for (var track = 0; track < 10; track++)
    {
        wideFiles.Add(PJob(fileId, "wide", "ultimate", (file * 10) + track));
    }
}

// No sharing: one subtitle per file, so four workers need four distinct files.
var distinctPlan = SubSyncService.PlanStart(
    wideFiles, Array.Empty<SyncJob>(), "ultimate", "wide", 4,
    _ => "/library", _ => true, _ => false);
Check("10-track episodes still fill four workers",
    distinctPlan.Count == 4, "got " + distinctPlan.Count);
Check("the four jobs are four different episodes",
    distinctPlan.Select(j => j.ItemId).Distinct().Count() == 4,
    "got " + distinctPlan.Select(j => j.ItemId).Distinct().Count() + " distinct");

// With the file's reference extracted, several subtitles of one episode may run together.
var oneEpisodeQueue = new List<SyncJob>();
var oneFileId = Guid.NewGuid();
for (var track = 0; track < 10; track++)
{
    oneEpisodeQueue.Add(PJob(oneFileId, "wide", "ultimate", track));
}

var oneEpisodePlan = SubSyncService.PlanStart(
    oneEpisodeQueue, Array.Empty<SyncJob>(), "ultimate", "wide", 4,
    _ => "/library", _ => true, _ => true);
Check("one episode's subtitles run four wide once its reference is cached",
    oneEpisodePlan.Count == 4, "got " + oneEpisodePlan.Count);

Check("a huge queue cannot stall the scan", SubSyncService.PlanStart(
    Enumerable.Range(0, 30000).Select(i => PJob(Guid.NewGuid(), "wide", "ultimate", i)),
    Array.Empty<SyncJob>(), "ultimate", "wide", 4,
    _ => "/library", _ => true, _ => false).Count == 4);

// A wide setting must fill on many-track episodes too: the scan window scales with the worker
// count, so this is the combination worth pinning down.
var manyTracks = new List<SyncJob>();
for (var file = 0; file < 100; file++)
{
    var fileId = Guid.NewGuid();
    for (var track = 0; track < 10; track++)
    {
        manyTracks.Add(PJob(fileId, "wide", "ultimate", (file * 10) + track));
    }
}

int PlanMany(int workers) => SubSyncService.PlanStart(
    manyTracks, Array.Empty<SyncJob>(), "ultimate", "wide", workers,
    _ => "/library", _ => true, _ => false).Count;

Check("2 workers fill on 10-track episodes", PlanMany(2) == 2, "got " + PlanMany(2));
Check("3 workers fill on 10-track episodes", PlanMany(3) == 3, "got " + PlanMany(3));
Check("8 workers fill on 10-track episodes", PlanMany(8) == 8, "got " + PlanMany(8));
Check("16 workers fill on 10-track episodes", PlanMany(16) == 16, "got " + PlanMany(16));
Check("32 workers fill on 10-track episodes", PlanMany(32) == 32, "got " + PlanMany(32));
Check("64 workers fill on 10-track episodes", PlanMany(64) == 64, "got " + PlanMany(64));
Check("the jobs planned are all different episodes",
    SubSyncService.PlanStart(manyTracks, Array.Empty<SyncJob>(), "ultimate", "wide", 32,
        _ => "/library", _ => true, _ => false).Select(j => j.ItemId).Distinct().Count() == 32);

// ---------------- Settings come from the file Jellyfin writes ----------------
// A saved setting that never reaches the running plugin looked like a setting being ignored
// (worker count, sync mode). The file is now the source of truth, re-read when it changes.
var settingsPath = Path.Combine(Path.GetTempPath(), "subsync-settings-" + Guid.NewGuid().ToString("N") + ".xml");
var settingsXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
    + "<PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\">"
    + "<ParallelWorkers>8</ParallelWorkers><SyncModeCopy>false</SyncModeCopy><VadMethod>webrtc</VadMethod></PluginConfiguration>";
File.WriteAllText(settingsPath, settingsXml);
var loadedSettings = SettingsSource.Read(settingsPath);
Check("settings are read from the settings file",
    loadedSettings is not null && loadedSettings.ParallelWorkers == 8,
    "workers " + (loadedSettings?.ParallelWorkers));
Check("the sync mode comes from the file as well",
    loadedSettings is not null && !loadedSettings.SyncModeCopy);
Check("the VAD method comes from the file as well",
    loadedSettings is not null && loadedSettings.VadMethod == "webrtc",
    loadedSettings?.VadMethod ?? "(null)");
Check("a missing settings file yields nothing rather than throwing",
    SettingsSource.Read(settingsPath + ".missing") is null);
File.WriteAllText(settingsPath, "this is not xml");
Check("a malformed settings file does not throw", SettingsSource.Read(settingsPath) is null);
File.Delete(settingsPath);

// ---------------- A library the plugin cannot write to ----------------
// Reported as "Access to the path ... is denied" once per subtitle. The folder is the problem, so
// it is detected in advance and stated once, naming the folder and confirming nothing changed.
Check("a writable folder is recognised",
    SubSyncService.CanWriteTo(Path.GetTempPath(), out var writableReason) && writableReason.Length == 0,
    writableReason);
Check("a folder that does not exist yet is created and then writable",
    SubSyncService.CanWriteTo(Path.Combine(Path.GetTempPath(), "subsync-probe-" + Guid.NewGuid().ToString("N")), out _));
Check("a folder the process cannot write to is refused, with a reason",
    !SubSyncService.CanWriteTo("/proc/subsync-cannot-write-here", out var deniedReason)
    && deniedReason.Length > 0, deniedReason);
Check("the refusal keeps a probe file from being left behind",
    !File.Exists(Path.Combine("/proc", ".subsync-write-probe-x")));

// ---------------- Jellyfin is only asked to work for what changed ----------------
// After a sync the plugin reports the folder (that is what makes Jellyfin discover the new file) and
// refreshes the item (which re-probes the media file). Ten subtitle tracks of one episode meant ten
// identical re-probes of the same file; the gate lets that happen once per item.
var fakeNow = new DateTime(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);
var gate = new LibraryRefreshGate(TimeSpan.FromSeconds(60), () => fakeNow);
var episodeA = Guid.NewGuid();
var episodeB = Guid.NewGuid();

Check("the first subtitle of an item refreshes it", gate.ShouldRefresh(episodeA));
Check("the second subtitle of the same item is skipped", !gate.ShouldRefresh(episodeA));
Check("another item still gets its own refresh", gate.ShouldRefresh(episodeB));

fakeNow = fakeNow.AddSeconds(59);
Check("inside the window the refresh is still skipped", !gate.ShouldRefresh(episodeA));
fakeNow = fakeNow.AddSeconds(1);
Check("at the window boundary the item is refreshed again", gate.ShouldRefresh(episodeA));
Check("the gate counts what it skipped", gate.SuppressedCount == 2, "got " + gate.SuppressedCount);
Check("only items inside the window are tracked", gate.TrackedItems == 2, "got " + gate.TrackedItems);

fakeNow = fakeNow.AddSeconds(120);
var prunedEntries = gate.Prune();
Check("pruning drops entries whose window has passed", prunedEntries == 2, "got " + prunedEntries);
Check("an item refreshes again after its entry was pruned", gate.ShouldRefresh(episodeA));

// Eight workers finishing tracks of the same episode at the same moment must produce one refresh.
var parallelGate = new LibraryRefreshGate(TimeSpan.FromSeconds(60), () => fakeNow);
var winners = 0;
System.Threading.Tasks.Parallel.For(0, 8, _ =>
{
    if (parallelGate.ShouldRefresh(episodeA))
    {
        Interlocked.Increment(ref winners);
    }
});
Check("parallel finishes of one item produce exactly one refresh", winners == 1, "got " + winners);

// ---------------- A sync that changes nothing writes nothing ----------------
// ffsubsync writes an output file even when the timings come out identical. Saving that as a
// ".SYNCED" sidecar puts a second subtitle with the same timing next to the original: no benefit,
// and it was reported from real use after syncing embedded tracks.
var noChangeDir = Path.Combine(Path.GetTempPath(), "subsync-nochange-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(noChangeDir);

string Srt(params int[] startsMs)
{
    var sb = new System.Text.StringBuilder();
    for (var i = 0; i < startsMs.Length; i++)
    {
        sb.AppendLine((i + 1).ToString());
        sb.AppendLine(TimeSpan.FromMilliseconds(startsMs[i]).ToString(@"hh\:mm\:ss\,fff")
            + " --> "
            + TimeSpan.FromMilliseconds(startsMs[i] + 1200).ToString(@"hh\:mm\:ss\,fff"));
        sb.AppendLine("line " + (i + 1));
        sb.AppendLine();
    }

    return sb.ToString();
}

var sourceSrt = Path.Combine(noChangeDir, "source.srt");
var identicalSrt = Path.Combine(noChangeDir, "identical.srt");
var shiftedSrt = Path.Combine(noChangeDir, "shifted.srt");
var framerateSrt = Path.Combine(noChangeDir, "framerate.srt");
var tinySrt = Path.Combine(noChangeDir, "tiny.srt");

File.WriteAllText(sourceSrt, Srt(1000, 2000, 3000, 4000, 5000));
File.WriteAllText(identicalSrt, Srt(1000, 2000, 3000, 4000, 5000));
File.WriteAllText(shiftedSrt, Srt(1500, 2500, 3500, 4500, 5500));
// Zero median offset but a real framerate correction: 990, 1995, 3000, 4005, 5010 against 1000,
// 2000, 3000, 4000, 5000. The median difference is 0 ms, the fitted ratio is not 1 - this must not
// be mistaken for "nothing changed".
File.WriteAllText(framerateSrt, Srt(990, 1995, 3000, 4005, 5010));
File.WriteAllText(tinySrt, Srt(1000, 2000));

var identical = SubSyncService.MeasureSyncChange(sourceSrt, identicalSrt);
Check("an identical output is recognised as no change", identical is { IsNoChange: true });
Check("the no-change case is described as a 0 ms offset",
    identical?.Describe() == "+0 ms offset", identical?.Describe() ?? "(none)");

var shifted = SubSyncService.MeasureSyncChange(sourceSrt, shiftedSrt);
Check("a 500 ms shift is a change", shifted is { IsNoChange: false });
Check("the shift is reported in milliseconds", shifted?.ShiftMs == 500, "got " + shifted?.ShiftMs);

// Sub-second precision must survive: parsing that dropped the milliseconds reported a real 400 ms
// shift as "0 ms offset", and the "nothing changed" check would then throw the correction away.
var subSecondSrt = Path.Combine(noChangeDir, "subsecond.srt");
File.WriteAllText(subSecondSrt, Srt(1400, 2400, 3400, 4400, 5400));
var subSecond = SubSyncService.MeasureSyncChange(sourceSrt, subSecondSrt);
Check("a 400 ms shift is measured, not rounded to zero",
    subSecond?.ShiftMs == 400, "got " + subSecond?.ShiftMs);
Check("a sub-second shift is not treated as no change", subSecond is { IsNoChange: false });

Check("a framerate correction is a change even with a zero median offset",
    SubSyncService.MeasureSyncChange(sourceSrt, framerateSrt) is { IsNoChange: false });
Check("too few cues cannot be judged, so the output is kept",
    SubSyncService.MeasureSyncChange(sourceSrt, tinySrt) is null);
Check("the description keeps the framerate detail",
    (SubSyncService.MeasureSyncChange(sourceSrt, framerateSrt)?.Describe() ?? string.Empty).Contains("framerate ratio"),
    SubSyncService.MeasureSyncChange(sourceSrt, framerateSrt)?.Describe() ?? "(none)");
Directory.Delete(noChangeDir, recursive: true);

// ---------------- A forced/signs track must not be mistaken for the full subtitle ----------------
// Reported from real use: a Norwegian sidecar came out with two cues for a whole episode. The file
// carried a two-cue forced track beside a full WebVTT track in the same language, and the automatic
// pick took the first one it met.
Check("two cues in a 45-minute episode look like a signs track",
    SubSyncService.LooksLikeSignsTrack(2, TimeSpan.FromMinutes(45)));
Check("a full episode subtitle does not",
    !SubSyncService.LooksLikeSignsTrack(640, TimeSpan.FromMinutes(45)));
Check("few cues in a short clip are normal",
    !SubSyncService.LooksLikeSignsTrack(5, TimeSpan.FromMinutes(5)));
Check("an empty subtitle is not reported as a signs track",
    !SubSyncService.LooksLikeSignsTrack(0, TimeSpan.FromMinutes(45)));
Check("an unreadable subtitle is unknown, not signs",
    !SubSyncService.LooksLikeSignsTrack(-1, TimeSpan.FromMinutes(45)));
Check("the boundary is twelve cues over a long video",
    SubSyncService.LooksLikeSignsTrack(11, TimeSpan.FromMinutes(45))
    && !SubSyncService.LooksLikeSignsTrack(12, TimeSpan.FromMinutes(45)));

// ---------------- WebVTT tracks are readable without ffmpeg ----------------
// A WebVTT track was rejected by the index reader because its codec ID was missing from the text
// list, so every one of them was demuxed by ffmpeg: a whole-file read per subtitle. ffmpeg writes
// D_WEBVTT/SUBTITLES, mkvmerge writes S_TEXT/WEBVTT, and both hold the cue text with the timing in
// the block header, exactly like S_TEXT/UTF8.
Check("the codec ID ffmpeg writes for WebVTT is text",
    MkvSubtitleExtractor.IsTextSubtitleCodecId("D_WEBVTT/SUBTITLES"));
Check("the codec ID mkvmerge writes for WebVTT is text",
    MkvSubtitleExtractor.IsTextSubtitleCodecId("S_TEXT/WEBVTT"));
Check("WebVTT is recognised as WebVTT, not as ASS or UTF8",
    MkvSubtitleExtractor.IsWebVttCodecId("D_WEBVTT/SUBTITLES")
    && !MkvSubtitleExtractor.IsWebVttCodecId("S_TEXT/UTF8")
    && !MkvSubtitleExtractor.IsWebVttCodecId("S_TEXT/ASS"));
Check("subrip and ASS stay text", MkvSubtitleExtractor.IsTextSubtitleCodecId("S_TEXT/UTF8")
    && MkvSubtitleExtractor.IsTextSubtitleCodecId("S_TEXT/ASS"));
Check("image tracks are still refused", !MkvSubtitleExtractor.IsTextSubtitleCodecId("S_HDMV/PGS"));

Check("WebVTT voice and class spans are stripped",
    MkvSubtitleExtractor.CleanWebVttText("<v Speaker>Hei</v> <c.yellow>der</c>") == "Hei der",
    MkvSubtitleExtractor.CleanWebVttText("<v Speaker>Hei</v> <c.yellow>der</c>"));
Check("WebVTT entities are decoded",
    MkvSubtitleExtractor.CleanWebVttText("A &amp; B &lt;x&gt;") == "A & B <x>",
    MkvSubtitleExtractor.CleanWebVttText("A &amp; B &lt;x&gt;"));
Check("an inline cue timestamp is dropped",
    MkvSubtitleExtractor.CleanWebVttText("first<00:00:02.000>second") == "firstsecond",
    MkvSubtitleExtractor.CleanWebVttText("first<00:00:02.000>second"));
Check("a payload carrying its own timing line keeps only the text",
    MkvSubtitleExtractor.CleanWebVttText("00:00:01.000 --> 00:00:02.000\nHei") == "Hei",
    MkvSubtitleExtractor.CleanWebVttText("00:00:01.000 --> 00:00:02.000\nHei"));
Check("a cue that is only a timing line becomes empty",
    MkvSubtitleExtractor.CleanWebVttText("00:00:01.000 --> 00:00:02.000") == string.Empty);
Check("line breaks inside a VTT cue are kept",
    MkvSubtitleExtractor.CleanWebVttText("linje en\nlinje to") == "linje en\nlinje to");

// ---------------- The plugin's own log file ----------------
// Jellyfin's server log is shared, rotates on the server's schedule and needs shell access; a run
// that behaves badly needs its own numbers in one file that can be opened from the interface.
var logDir = Path.Combine(Path.GetTempPath(), "subsync-log-" + Guid.NewGuid().ToString("N")[..8]);
PluginLog.Append(logDir, "INFO", "first line", 4096);
var logFile = Path.Combine(logDir, "subsync.log");
Check("the plugin log is written", File.Exists(logFile) && File.ReadAllText(logFile).Contains("first line"));
Check("a log line carries a UTC timestamp and a level",
    System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(logFile),
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}Z INFO  first line"));

// Error() writes through the resolved path (the temp fallback in this harness, since no plugin
// instance exists), which also proves the exception detail reaches the file.
PluginLog.Error("boom", new InvalidOperationException("the detail"));
var errorLog = PluginLog.FilePath;
Check("an error entry includes the exception detail",
    File.Exists(errorLog) && File.ReadAllText(errorLog).Contains("the detail"), errorLog);
Check("the log path is described for the interface", PluginLog.Describe().Length > 0);
if (File.Exists(errorLog))
{
    File.Delete(errorLog);
}

File.WriteAllText(logFile, new string('x', 5000));
PluginLog.Append(logDir, "WARN", "after rotation", 4096);
Check("reaching the size limit rotates the file",
    File.Exists(logFile + ".1") && new FileInfo(logFile + ".1").Length == 5000);
Check("the new entry lands in the fresh file", File.ReadAllText(logFile).Contains("after rotation"));

for (var roll = 0; roll < PluginLog.KeptFiles + 3; roll++)
{
    File.WriteAllText(logFile, new string('y', 5000));
    PluginLog.Append(logDir, "INFO", "roll " + roll, 4096);
}
Check("rotation keeps a bounded number of files",
    !File.Exists(logFile + "." + (PluginLog.KeptFiles + 1))
    && File.Exists(logFile + "." + PluginLog.KeptFiles));

var described = PluginLog.Tail(64);
Check("the log can be read back for the interface", described.Length > 0 && !described.Contains("could not read"));

// A log write must never be the reason a sync fails.
PluginLog.Append("/proc/subsync-cannot-write-here", "INFO", "unwritable", 4096);
Check("a log write into an unwritable folder is swallowed", true);
Directory.Delete(logDir, recursive: true);

static int EnvInt(string name) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var parsed) ? parsed : -1;

// ---------------- A 20-episode batch must fill every worker ----------------
// Two subtitle languages per episode, interleaved the way the browser queues them, all on
// one volume. The first wave can only use each episode's first subtitle (the second waits for
// that file's cached audio analysis), so it must skip those and still reach the worker count.
var twentyEpisodes = new List<SyncJob>();
var episodeIds = new List<Guid>();
for (var ep = 0; ep < 20; ep++)
{
    var episode = Guid.NewGuid();
    episodeIds.Add(episode);
    twentyEpisodes.Add(PJob(episode, "series", "ultimate", ep * 2));
    twentyEpisodes.Add(PJob(episode, "series", "ultimate", (ep * 2) + 1));
}

var seriesWave = SubSyncService.SelectWave(twentyEpisodes, "ultimate", "series", new SubSyncService.WavePolicy
{
    Limit = 4,
    VolumeOf = _ => "/library",     // every episode on the same disk
    IsHeavyIo = _ => true,          // nothing cached yet
    CanShareMediaFile = _ => false, // so each episode contributes one job
});
Check("twenty-episode wave fills four workers", seriesWave.Count == 4, "got " + seriesWave.Count);
Check("the wave takes the first episode's first subtitle",
    seriesWave[0].BatchIndex == 0, "got " + seriesWave[0].BatchIndex);
Check("the wave never takes two subtitles of one episode before it is cached",
    seriesWave.Select(j => j.ItemId).Distinct().Count() == seriesWave.Count,
    string.Join(",", seriesWave.Select(j => j.ItemId)));

// Once the analysis is cached, both subtitles of the same episode may share a wave.
var cachedWave = SubSyncService.SelectWave(twentyEpisodes, "ultimate", "series", new SubSyncService.WavePolicy
{
    Limit = 4,
    VolumeOf = _ => "/library",
    IsHeavyIo = _ => false,
    CanShareMediaFile = _ => true,
});
Check("cached episodes fill four workers", cachedWave.Count == 4, "got " + cachedWave.Count);

// Worker count is the only bound: ask for eight and eight are selected.
var eightWave = SubSyncService.SelectWave(twentyEpisodes, "ultimate", "series", new SubSyncService.WavePolicy
{
    Limit = 8,
    VolumeOf = _ => "/library",
    IsHeavyIo = _ => true,
    CanShareMediaFile = _ => false,
});
Check("eight workers means eight jobs in flight", eightWave.Count == 8, "got " + eightWave.Count);

Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
return failures == 0 ? 0 : 1;
"""


def run_page_checks():
    """Checks on the dashboard markup, where a mistake is invisible and silently wrong.

    Two elements once shared id="ss-workers" (the worker-rows container and the settings input), so
    getElementById returned the container: the settings field was never loaded and every save
    stored the fallback of 4, whatever the user typed. A duplicate id or a settings field that
    does not resolve is silent, so it is asserted here rather than discovered in use.
    """
    import collections
    import re

    failures = 0
    web = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Web')

    def report(name, ok, detail=''):
        nonlocal failures
        if not ok:
            failures += 1
        print(f'{"PASS" if ok else "FAIL"}  {name}' + (f'   [{detail}]' if detail and not ok else ''))

    for entry in sorted(os.listdir(web)):
        if not entry.endswith('.html'):
            continue
        markup = re.sub(r'<script\b.*?</script>', '',
                        open(os.path.join(web, entry), encoding='utf-8').read(), flags=re.S)
        ids = re.findall(r'\bid="([^"]*)"', markup)
        dupes = {k: v for k, v in collections.Counter(ids).items() if v > 1}
        report(f'{entry} has no duplicate element ids', not dupes, str(dupes))

    main_html = open(os.path.join(web, 'subsyncMain.html'), encoding='utf-8').read()
    markup = re.sub(r'<script\b.*?</script>', '', main_html, flags=re.S)
    static_ids = collections.Counter(re.findall(r'\bid="([^"]*)"', markup))
    settings_js = main_html[main_html.find('function loadConfig'):][:4000]
    for element_id in sorted(set(re.findall(r"\$\('([^']+)'\)", settings_js))):
        count = static_ids.get(element_id, 0)
        report(f"settings field '{element_id}' exists exactly once", count == 1, f'found {count}')

    report('the scope dropdown carries no fake item id', 'value="series"' not in main_html)

    # A checkbox in the new settings page was wired to "FastMkvExtraction", which the config model
    # never had: it rendered, remembered nothing, and controlled nothing (the engine always used the
    # indexed reader). The real switch was the legacy dashboard page's "FastIndexedExtraction", which
    # has now been removed as well because the indexed reader is what the plugin should always do.
    pages = {name: open(os.path.join(web, name), encoding='utf-8').read()
             for name in os.listdir(web) if name.endswith(('.html', '.js'))}
    report('no page offers a switch for the always-on extraction',
           not any('ss-fastmkv' in text or 'FastIndexedExtraction' in text for text in pages.values()))

    plugin_sources = []
    for root, _dirs, files in os.walk(os.path.join(REPO, 'Jellyfin.Plugin.SubSync')):
        if 'bin' in root.split(os.sep) or 'obj' in root.split(os.sep):
            continue
        plugin_sources.extend(open(os.path.join(root, name), encoding='utf-8').read()
                              for name in files if name.endswith(('.cs', '.html', '.js')))
    report('no dead extraction setting is left in the plugin sources',
           not any('FastIndexedExtraction' in text for text in plugin_sources))

    # The detail-view client (jellyfin-web injection) must offer the same bulk flow as the main page
    # for a series or a season, and route it through the same server batch queue so the configured
    # parallel mode applies there too.
    client_path = os.path.join(web, 'subsync.js')
    client = open(client_path, encoding='utf-8').read()
    report('the item menu offers a bulk sync for series and seasons', 'Sync all episodes' in client)
    report('series and seasons are the bulk scopes', 'Series: 1' in client and 'Season: 1' in client)
    report('the bulk flow uses the server batch queue', '/Batch' in client)
    report('the language list comes from the bulk subtitle endpoint', 'Subtitles/Batch' in client)
    report('the bulk dialog shows the configured parallel width', 'WorkerLimit' in client or 'workerLimit' in client)
    node = shutil.which('node')
    if node:
        parsed = subprocess.run([node, '--check', client_path], capture_output=True, text=True)
        report('subsync.js is valid JavaScript', parsed.returncode == 0, (parsed.stderr or '')[-200:])
    report('the detail-view dialog uses the slim progress bar', 'height:3px' in client or 'height: 3px' in client)

    # The run box was reported as messy: a 7px bar, an elapsed time on every worker row, and a phase
    # line that repeated what the rows already said.
    report('the overall progress bar stays slim', 'height: 3px' in main_html)
    report('the worker bars stay slim', 'grid-column: 2 / 4' in main_html)
    report('the phase is not printed twice while worker rows carry it',
           "workersNow > 0 ? '' : phase" in main_html)
    report('the build number lives in the badge, not the progress line',
           's.PluginVersion' in main_html and "bits.push('v' + runningVersion)" not in main_html)

    # A two-cue forced track was picked over the full track in the same language, because the collapse
    # kept "the first one" (external preferred). Both surfaces must rank a forced track last.
    report('the main page ranks forced tracks last', 'function trackRank' in main_html
           and 'trackRank(t) < trackRank(cur)' in main_html)
    report('the detail dialog ranks forced tracks last', 'function trackRank' in client
           and 'trackRank(t) < trackRank(current)' in client)
    report('forced tracks are counted in the language list',
           'forcedCounts' in main_html and 'forcedCounts' in client)
    report('the server exposes the forced flag', any('IsForced' in text for text in plugin_sources))

    # One row per worker that is actually working. Drawing every slot padded the panel with idle
    # rows ("idle —", empty bars) that buried the two or three tasks doing the work.
    report('the worker panel draws only working workers', 'ss-worker-idle' not in main_html)
    report('the worker panel shows each worker phase and percentage',
           'ss-worker-phase' in main_html and "ss-worker-pct" in main_html and "pct + '%" in main_html)

    # The panel used to update only while this page streamed a batch it started itself, so a run
    # started from the detail view left it on "Queued…" until the page was reloaded. The heartbeat
    # mirrors the server's run, and the line carries how many subtitles are done out of how many.
    report('the panel mirrors a run it did not start',
           'startHeartbeat' in main_html and 'renderMirroredBatch' in main_html and 'showIdlePanel' in main_html)
    report('the run line counts finished subtitles',
           "bits.push((pos || 0) + '/' + total)" in main_html and "mirroredSummary" in main_html)
    report('the stale queued wording is gone',
           'waiting for earlier runs to finish' not in main_html)
    report('the detail dialog tolerates a failed poll', 'pollFailures' in client)

    # A queued task is not a failed task. It was rendered as "FAIL <title> -- Queued" the moment a
    # batch was opened, which reads as a run that failed instantly.
    report('queued or running tasks are never reported as failures',
           "status !== 'Completed' && status !== 'Failed' && status !== 'Cancelled'" in main_html
           and 'still queued or running' in main_html)

    # This plugin's own sidecars must not be offered as tracks: Jellyfin reads the marker as the
    # language, so they appeared as a language called "SYNCED" and doubled every batch.
    report('our own sidecars are recognised by name',
           "IsSyncedSidecarName" in '\n'.join(plugin_sources) and 'IsOwnSidecar' in '\n'.join(plugin_sources))
    report('the track list leaves our own sidecars out',
           '.Where(s => !IsOwnSidecar(s))' in '\n'.join(plugin_sources))

    # The enqueue path is timed part by part, so a slow one can be named instead of guessed at.
    report('the enqueue path reports where its time goes',
           'enqueue slow:' in '\n'.join(plugin_sources) and 'gap={gapMs} ms' in '\n'.join(plugin_sources))

    # A synchronous extraction on a pool thread starves everything else the server does, including
    # the request still queueing the batch: that is what made a 50-task batch trickle in a few tasks
    # every few seconds. It gets its own thread.
    report('the blocking extraction runs off the thread pool',
           'TaskCreationOptions.LongRunning' in '\n'.join(plugin_sources))
    report('the pump wake signal cannot be swallowed', '_wakePump = new(0)' in '\n'.join(plugin_sources))

    # One pass over a file serves every queued language of it, and the tracks it produced are reused
    # by the jobs that follow.
    service_text = '\n'.join(plugin_sources)
    report('one extraction pass serves the file\'s queued subtitles',
           'SiblingOrdinals' in service_text and 'TryExtractMany' in service_text)
    report('extracted tracks are reused by the following jobs',
           'CacheExtracted' in service_text and 'TryTakeExtracted' in service_text
           and 'matroska-cached' in service_text)
    report('the worker panel falls back to what the server is running',
           'SubSync/Active' in main_html and 'lastActive' in main_html)
    report('the extraction note reaches the log line',
           'ExtractionNote' in main_html and 'ExtractionNote' in '\n'.join(plugin_sources))

    # Jellyfin 12 disables the legacy authorization mechanisms by default, so the header this
    # plugin used for years (X-Emby-Token) and the ?api_key= query parameter are ignored there:
    # every call answers 401, the settings tab and the history come up empty, and nothing in the
    # build says so. `Authorization: MediaBrowser Token="..."` and ?ApiKey= are the modern forms
    # and are accepted by 10.11 as well, so the legacy ones must not come back.
    pages = {name: open(os.path.join(web, name), encoding='utf-8').read()
             for name in ('subsync.js', 'subsyncMain.html', 'configPage.html')}
    legacy_header = [name for name, text in pages.items()
                     if re.search(r'''\[\s*['"]X-Emby-Token['"]\s*\]\s*=''', text)]
    report('no page sends the legacy X-Emby-Token header', not legacy_header,
           ', '.join(legacy_header))
    legacy_param = [name for name, text in pages.items() if "'?api_key='" in text]
    report('no page builds a ?api_key= url', not legacy_param, ', '.join(legacy_param))
    for name in ('subsync.js', 'subsyncMain.html'):
        report(f'{name} authenticates with the Authorization header',
               'MediaBrowser Token=' in pages[name])
    report('the settings page authenticates with the Authorization header',
           "options.headers['Authorization']" in pages['configPage.html'])

    # The injected client script is an IIFE, so nothing it defines reaches the pages: a page that
    # calls one of its helpers throws a ReferenceError and the rest of that render never runs
    # (the debug-log link took the speech-cache line and the engine badge down with it).
    main_page = pages['subsyncMain.html']
    for helper in ('function token()', 'function authHeader()', 'function apiUrl(path)'):
        report(f'the main page defines {helper[len("function "):].removesuffix("()")} itself',
               helper in main_page)
    report('the main page no longer calls an undefined token()/apiUrl()',
           'authHeader()' in main_page and "'?ApiKey='" in main_page)

    # One ABI per build, and every file that states it has to agree: Jellyfin reads targetAbi
    # back out of the shipped meta.json, filters catalog entries by it, and refuses to load a
    # plugin built against a different server generation.
    import json as _json
    csproj = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync',
                               'Jellyfin.Plugin.SubSync.csproj'), encoding='utf-8').read()
    meta = _json.load(open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'meta.json'),
                           encoding='utf-8'))
    build_yaml = open(os.path.join(REPO, 'build.yaml'), encoding='utf-8').read()
    manifest_script = open(os.path.join(REPO, 'scripts', 'write_manifest.py'),
                           encoding='utf-8').read()

    def _yaml_value(key):
        match = re.search(rf'^{key}:\s*"?([^"\n]+)"?$', build_yaml, flags=re.M)
        return match.group(1).strip() if match else None

    abi = meta.get('targetAbi')
    report('meta.json declares the Jellyfin 12 ABI', abi == '12.0.0.0', str(abi))
    report('build.yaml agrees with meta.json on the ABI', _yaml_value('targetAbi') == abi,
           f'{_yaml_value("targetAbi")} vs {abi}')
    report('the catalog manifest is published for the same ABI',
           f'TARGET_ABI = "{abi}"' in manifest_script)
    report('the plugin targets .NET 10', '<TargetFramework>net10.0</TargetFramework>' in csproj)
    report('build.yaml agrees with the project on the framework',
           _yaml_value('framework') == 'net10.0', str(_yaml_value('framework')))
    report('the plugin builds against the Jellyfin 12 assemblies',
           'Include="Jellyfin.Controller" Version="12.0.0"' in csproj
           and 'Include="Jellyfin.Model" Version="12.0.0"' in csproj)
    csproj_version = re.search(r'<Version>([^<]+)</Version>', csproj)
    report('the shipped meta.json carries the built version',
           csproj_version is not None and meta.get('version') == csproj_version.group(1),
           f'{meta.get("version")} vs {csproj_version and csproj_version.group(1)}')

    # A workflow that still copies from net9.0 or installs the .NET 9 SDK fails at release time,
    # after the tag is pushed.
    for workflow in ('beta.yml', 'release.yml', 'tests.yml'):
        text = open(os.path.join(REPO, '.github', 'workflows', workflow), encoding='utf-8').read()
        report(f'{workflow} builds on .NET 10', "dotnet-version: '10.0.x'" in text)
        report(f'{workflow} does not reference a net9.0 output path', 'net9.0' not in text)

    # A reference subtitle taken from a sibling track inherits that track's error, and every other
    # track of the file then inherits it in turn: one mis-synced reference on a real server moved
    # five language tracks by the same +57.5 s and all five were written as successes. It is
    # therefore kept for the run that made it and never cached across runs.
    service = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'SubSyncService.cs'),
                   encoding='utf-8').read()
    store = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'ReferenceStore.cs'),
                 encoding='utf-8').read()
    plugin_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Plugin.cs'),
                         encoding='utf-8').read()
    cache_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'SpeechCache.cs'),
                        encoding='utf-8').read()

    report('the reference is reserved inside the run, not in the cache directory',
           'ReferenceStore.Reserve' in service
           and 'SpeechCache.ReferencePath' not in service)
    report('no reference marker file is written any more',
           'MarkReferenceReady' not in service and 'ReferencePath' not in cache_source)
    report('extracting a reference logs which track it is and how many cues it has',
           'track {Reference}, {Cues} cues' in service
           and 'public static int CueCount' in store)
    report('a closed run drops the file rather than deleting it while others still need it',
           'ReferenceStore.EndJob' in service and 'moreQueuedForThisFile' in store)
    report('an interrupted run cannot leave a reference behind',
           'ReferenceStore.SweepLeftovers' in plugin_source and 'SweepLeftovers' in store)
    report('the scheduler asks the in-memory store whether a reference is ready',
           'ReferenceStore.IsReady' in service
           and 'ReferenceStore.EndJob(finishedVideo' in service)
    report('releasing the reference happens outside the queue lock',
           service.index('ReferenceStore.EndJob(finishedVideo')
           < service.index('if (toStart.Count == 0)'))
    # Fix the cause, never refuse the job: a plugin whose answer to a bad reference is "I will not
    # sync your file" is worse than one that syncs it against the audio.
    report('a job is never refused over a big shift - it is written and annotated',
           'so the engine was clamped - the file is still written' in service
           and 'worth checking, a shift this size' in service
           and 'sanity limit' not in service
           and 'is mis-synced' not in service
           and 'falling back to the audio for this job' in service)
    report('no leftover sanity-limit setting in the model or either page',
           'MaxSubtitleReferenceOffsetSeconds' not in service
           and 'MaxSubtitleReferenceOffsetSeconds' not in open(
               os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                            'PluginConfiguration.cs'), encoding='utf-8').read()
           and 'maxrefoffset' not in pages['subsyncMain.html']
           and 'MaxSubtitleReferenceOffsetSeconds' not in pages['configPage.html'])
    report('a forced/signs track is never chosen as the reference',
           'forcedTracks' in service and 'Never pick one' in service
           and 'IsForced' in service)
    # "Clear cache" used to empty the audio analysis only, which left the reference subtitles in place:
    # clearing the cache then changed nothing about a wrong result, which cost a whole debugging session.
    controller = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Api', 'SubSyncController.cs'),
                      encoding='utf-8').read()
    report('clear cache empties every cache, not just the audio analysis',
           'SpeechCache.Clear()' in controller
           and 'ReferenceStore.Clear()' in controller
           and 'ClearStaleJobDirectories()' in controller)
    # A stray </div> left behind by a removed field closes the page's container early: the rest of the
    # form lands outside it, the layout comes apart, controls stop responding, and Jellyfin renders a
    # Save button on a page that is not a configuration page. Nothing in this suite noticed, so it
    # shipped - the check below exists so that it cannot happen again.
    def structure_problems(path):
        from html.parser import HTMLParser

        class Diag(HTMLParser):
            def __init__(self):
                super().__init__(convert_charrefs=True)
                self.stack = []
                self.problems = []

            def handle_starttag(self, tag, attrs):
                if tag not in ('input', 'br', 'img', 'meta', 'link', 'hr'):
                    self.stack.append(tag)

            def handle_endtag(self, tag):
                if tag in ('input', 'br', 'img', 'meta', 'link', 'hr'):
                    return
                if self.stack and self.stack[-1] == tag:
                    self.stack.pop()
                else:
                    self.problems.append((self.getpos()[0], tag))

        parser = Diag()
        parser.feed(open(path, encoding='utf-8').read())
        return parser.problems

    for page in ('subsyncMain.html', 'configPage.html'):
        path = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Web', page)
        problems = structure_problems(path)
        report('the ' + page + ' markup closes every tag it opens',
               not problems,
               problems[:3] if problems else '')

    report('clearing never deletes a running job\'s scratch folder',
           '_jobs.ContainsKey(name)' in service and 'ref" continue' not in service)
    report('the button says what it clears',
           'Cached data' in pages['subsyncMain.html']
           and 'audio analysis' in pages['subsyncMain.html']
           and 'reference subtitle' in pages['subsyncMain.html'].lower())

    report('a reference with too few cues is dropped and the audio used instead',
           'LooksLikeSignsTrack(referenceCues' in service
           and 'falling back to the audio for this job' in service
           and 'ReferenceStore.Discard' in service and 'public static void Discard' in store)
    report('the answer the scheduler keys on is memoised, not read per planning pass',
           'SpeechCachedTtl' in service and 'private static string MediaStamp' not in cache_source)

    return failures


def main():
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK)
    with open(f'{WORK}/logictest.csproj', 'w') as f:
        f.write(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>logictest</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{REPO}/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj" />
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" />
  </ItemGroup>
</Project>
""")
    with open(f'{WORK}/Program.cs', 'w') as f:
        f.write(PROGRAM)

    fixtures = os.path.join(WORK, 'fixtures')
    shutil.rmtree(fixtures, ignore_errors=True)
    os.makedirs(fixtures)
    generator = os.path.join(REPO, 'tests', 'fixtures', 'make_remux.py')
    fixture_specs = [
        ('MKV_FIX_CUES', 'cues.mkv', []),
        ('MKV_FIX_NOSUB', 'nosub.mkv', ['--no-sub-cues']),
        ('MKV_FIX_NOCUES', 'nocues.mkv', ['--no-cues']),
        ('MKV_FIX_WALK', 'walk.mkv', ['--no-rel-pos']),
    ]
    clusters, sub_every = 60, 6
    expected = (clusters - 1) // sub_every + 1
    env = dict(ENV)
    for variable, name, extra in fixture_specs:
        path = os.path.join(fixtures, name)
        subprocess.run(['python3', generator, path, '--clusters', str(clusters),
                        '--payload', '1', '--sub-every', str(sub_every)] + extra,
                       check=True, capture_output=True)
        env[variable] = path
        env[variable + '_EXPECT'] = str(expected)

    # One file with three subtitle tracks, for the multi-track pass.
    multi_path = os.path.join(fixtures, 'multi.mkv')
    subprocess.run(['python3', generator, multi_path, '--clusters', str(clusters),
                    '--payload', '1', '--sub-every', str(sub_every), '--sub-tracks', '3'],
                   check=True, capture_output=True)
    env['MKV_FIX_MULTI'] = multi_path
    env['MKV_FIX_MULTI_EXPECT'] = str(expected)
    ENV.clear()
    ENV.update(env)

    os.makedirs(WORK, exist_ok=True)

    build = subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo', '-v', 'q'], cwd=WORK, capture_output=True, text=True, env=ENV)
    if build.returncode != 0:
        print(build.stdout[-1500:], build.stderr[-1500:])
        return 1

    run = subprocess.run([DOTNET, f'{WORK}/bin/Release/net10.0/logictest.dll'], cwd=WORK, capture_output=True, text=True, env=ENV)
    print(run.stdout or run.stderr)
    page_failures = run_page_checks()
    if page_failures:
        print(f'{page_failures} FAILURE(S)')
    return run.returncode or (1 if page_failures else 0)


if __name__ == '__main__':
    raise SystemExit(main())
