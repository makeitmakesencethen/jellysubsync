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
    raise SystemExit('dotnet not found: install the .NET 9 SDK, put it on PATH, or set DOTNET=/path/to/dotnet')


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
Check("uncached file cannot run two workers",
    heavyFirstWave.Count(j => j.ItemId == v1) == 1, "v1 count " + heavyFirstWave.Count(j => j.ItemId == v1));

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
Check("same file is never split across workers when uncached",
    sameFileWave.Count(j => j.ItemId == volA1) == 1, "volA1 count " + sameFileWave.Count(j => j.ItemId == volA1));
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
    }
}

// The indexed path must read less than walking the clusters to find the same subtitles:
// if CueRelativePosition handling ever regresses, these two numbers collapse together.
var indexedStats = new MkvExtractionStats();
var walkStats = new MkvExtractionStats();
var indexedPath = Environment.GetEnvironmentVariable("MKV_FIX_CUES");
var walkPath = Environment.GetEnvironmentVariable("MKV_FIX_WALK");
if (!string.IsNullOrEmpty(indexedPath) && !string.IsNullOrEmpty(walkPath))
{
    MkvSubtitleExtractor.TryExtract(indexedPath, 0, out _, out _, null, out indexedStats);
    MkvSubtitleExtractor.TryExtract(walkPath, 0, out _, out _, null, out walkStats);
    Check("indexed extraction reads less than the block walk",
        indexedStats.BytesRead < walkStats.BytesRead,
        $"indexed {indexedStats.BytesRead / 1e6:0.00} MB vs walk {walkStats.BytesRead / 1e6:0.00} MB");
    Check("indexed extraction needs fewer reads than the block walk",
        indexedStats.ReadCalls <= walkStats.ReadCalls,
        $"indexed {indexedStats.ReadCalls} vs walk {walkStats.ReadCalls}");
    Check("block-offsets are actually used", indexedStats.IndexedMisses == 0,
        $"{indexedStats.IndexedMisses} misses");
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


def main():
    shutil.rmtree(WORK, ignore_errors=True)
    os.makedirs(WORK)
    with open(f'{WORK}/logictest.csproj', 'w') as f:
        f.write(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>logictest</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{REPO}/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj" />
    <PackageReference Include="Jellyfin.Controller" Version="10.11.10" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.10" />
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
    ENV.clear()
    ENV.update(env)

    os.makedirs(WORK, exist_ok=True)

    build = subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo', '-v', 'q'], cwd=WORK, capture_output=True, text=True, env=ENV)
    if build.returncode != 0:
        print(build.stdout[-1500:], build.stderr[-1500:])
        return 1

    run = subprocess.run([DOTNET, f'{WORK}/bin/Release/net9.0/logictest.dll'], cwd=WORK, capture_output=True, text=True, env=ENV)
    print(run.stdout or run.stderr)
    return run.returncode


if __name__ == '__main__':
    raise SystemExit(main())
