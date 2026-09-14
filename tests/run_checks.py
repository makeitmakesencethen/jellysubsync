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

// Settled with the user on 2026-09-11 (S8): an external sidecar is aligned against a sibling text
// track when the file has one, because that alignment can be checked, and against the audio when it
// does not — which is the one case where an unverifiable result is still written.
Check("external sidecar uses a sibling text track when the file has one",
    SubSyncService.SelectReferenceStream(false, twoText, -1) == "s:0",
    SubSyncService.SelectReferenceStream(false, twoText, -1) ?? "null");
Check("external sidecar falls back to the audio when the file has no text track",
    SubSyncService.SelectReferenceStream(false, imageOnly, -1) == "a:0",
    SubSyncService.SelectReferenceStream(false, imageOnly, -1) ?? "null");
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

// ---------------- Walk ceilings: bounded by the worker count, unless a volume measures slow ----------------
//
// This block used to assert the opposite - "volume information may only express a preference (VolumeOf),
// never a cap on how many jobs a volume contributes", enforced by a reflection check that no such property
// existed. That invariant was reversed deliberately on 2026-09-14, because the rule it encoded was measured
// wrong on a storage-bound volume, and the cap it forbade is the one thing that protects such a volume:
//
//   one share, one season, four levels, the same eight episodes, worker limit 1 / 2 / 4 / 8:
//     per file  22,2 / 48,9 / 94,1 / 421,3 s     level wall  3,9 / 3,6 / 3,6 / 7,3 min
//     aggregate  122 / 135 / 133 / 66 files/h
//   The volume delivered the same work per hour at 1, 2 and 4, and HALF of it at 8, where a file that
//   takes 22 s alone took 421 s. Eight walks on one volume is not parallelism; it is a queue whose first
//   file takes six minutes to answer.
//
// The rule that replaces it, and that these checks assert:
//
//   a volume that nothing has measured, or that measures fast, has NO ceiling and behaves exactly as it
//   did before this existed; a volume whose own reads have shown it storage-bound carries a ceiling of its
//   own, and that ceiling counts that volume alone - jobs on other volumes are untouched by it.
//
// The second half is what keeps the old rule's concern answered: a slow volume's ceiling cannot slow a
// batch down, because it never touches another volume's jobs.
var wideQueue = new List<SyncJob>
{
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 0, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 1, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 2, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 3, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued },
    new SyncJob { Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = 4, ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued }
};

// Unmeasured: held conservatively. This is the case that failed in the field on 2026-09-14 - a whole
// season's eight walks were admitted in one wave before the first extraction pass had read anything, so
// "no measurement means no ceiling" left all eight walking one share at once (max concurrent 8).
var unmeasured = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false, VolumeOf = _ => "//nas/share",
    WalkCapOf = _ => SubSyncService.WalkCapForProfile(null),
});
Check("a volume nothing has read yet is held at the conservative ceiling", unmeasured.Count == 2,
    "got " + unmeasured.Count + " (expected " + SubSyncService.UnmeasuredWalkCap + ")");
Check("the wave takes the first queued tasks in order",
    unmeasured.Select(j => j.BatchIndex).SequenceEqual(new[] { 0, 1 }),
    "got " + string.Join(",", unmeasured.Select(j => j.BatchIndex)));
// A volume the profile measures as fast says so explicitly, and no ceiling applies.
var fastVolume = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false, VolumeOf = _ => "//nvme/data",
    WalkCapOf = _ => SubSyncService.WalkCapForProfile(0.05),
});
Check("a volume that measures fast is not capped", fastVolume.Count == 4, "got " + fastVolume.Count);

var twoWorkers = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 2, IsHeavyIo = _ => true, CanShareMediaFile = _ => false, VolumeOf = _ => "//nas/share",
});
Check("worker count is the only bound for uncapped work (2 requested -> 2 in flight)",
    twoWorkers.Count == 2, "got " + twoWorkers.Count);

// Measured storage-bound: it carries a ceiling of 2, or 1 while it is thrashing.
var cappedTwo = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false, VolumeOf = _ => "//nas/share",
    WalkCapOf = _ => (2, "check"),
});
Check("a storage-bound volume is held to its own ceiling (2 of 5 queued)",
    cappedTwo.Count == 2, "got " + cappedTwo.Count);

var cappedOne = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false, VolumeOf = _ => "//nas/share",
    WalkCapOf = _ => (1, "check"),
});
Check("a thrashing volume is held to one walk at a time", cappedOne.Count == 1, "got " + cappedOne.Count);

// The ceiling counts ONE volume. This is the property the old invariant protected, and the one that makes
// the ceiling safe: a slow volume's number never touches a fast volume's jobs, and vice versa.
var mixedVolumes = new Dictionary<Guid, string>();
var mixedQueue = new List<SyncJob>();
foreach (var (vol, count) in new[] { ("//nas/share", 3), ("//nvme/data", 3) })
{
    for (var k = 0; k < count; k++)
    {
        var job = new SyncJob
        {
            Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = mixedQueue.Count,
            ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued
        };
        mixedVolumes[job.ItemId] = vol;
        mixedQueue.Add(job);
    }
}

var mixedWave = SubSyncService.SelectWave(mixedQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false,
    VolumeOf = j => mixedVolumes[j.ItemId],
    WalkCapOf = j => mixedVolumes[j.ItemId] == "//nas/share" ? (2, "check") : (int.MaxValue, "check"),
});
var slowInWave = mixedWave.Count(j => mixedVolumes[j.ItemId] == "//nas/share");
var fastInWave = mixedWave.Count(j => mixedVolumes[j.ItemId] == "//nvme/data");
Check("a slow volume's ceiling does not throttle a fast volume's jobs",
    slowInWave == 2 && fastInWave == 2,
    $"slow {slowInWave} (ceiling 2), fast {fastInWave} of 4 slots");

var mixedTight = SubSyncService.SelectWave(mixedQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false,
    VolumeOf = j => mixedVolumes[j.ItemId],
    WalkCapOf = j => mixedVolumes[j.ItemId] == "//nas/share" ? (1, "check") : (int.MaxValue, "check"),
});
Check("a ceiling of 1 costs the slow volume one slot, not the fast volume's three",
    mixedTight.Count(j => mixedVolumes[j.ItemId] == "//nas/share") == 1
    && mixedTight.Count(j => mixedVolumes[j.ItemId] == "//nvme/data") == 3,
    string.Join(",", mixedTight.Select(j => mixedVolumes[j.ItemId])));

// Walks already running count against the ceiling, so raising the worker count cannot walk past it.
var busySlow = new Dictionary<string, int> { ["//nas/share"] = 2 };
var busyWave = SubSyncService.SelectWave(mixedQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => true, CanShareMediaFile = _ => false,
    VolumeOf = j => mixedVolumes[j.ItemId],
    WalkCapOf = j => mixedVolumes[j.ItemId] == "//nas/share" ? (2, "check") : (int.MaxValue, "check"),
    HeavyInUseByVolume = busySlow,
});
Check("a volume already at its ceiling starts no more walks on it",
    busyWave.Count(j => mixedVolumes[j.ItemId] == "//nas/share") == 0
    && busyWave.Count(j => mixedVolumes[j.ItemId] == "//nvme/data") == 3,
    string.Join(",", busyWave.Select(j => mixedVolumes[j.ItemId])));

// Only media reads are bounded: extraction-only work keeps the full worker count.
var lightWave = SubSyncService.SelectWave(wideQueue, "ultimate", "b", new SubSyncService.WavePolicy
{
    Limit = 4, IsHeavyIo = _ => false, CanShareMediaFile = _ => false, VolumeOf = _ => "//nas/share",
    WalkCapOf = _ => (1, "check"),
});
Check("the ceiling bounds media reads only", lightWave.Count == 4, "got " + lightWave.Count);

// S32: a cap of 2 comes from two different situations and the log line has to say which. It used to choose
// its wording from the cap value, so a volume measured at 21 ms per read was reported as "has not been read
// yet" - which is how a working ceiling looked broken in the field on 2026-09-14.
{
    var s32Unmeasured = SubSyncService.WalkCapForProfile(null);
    var s32Measured = SubSyncService.WalkCapForProfile(21.26);
    var s32Thrashing = SubSyncService.WalkCapForProfile(1419.0);
    var s32Fast = SubSyncService.WalkCapForProfile(0.05);

    Check("both cases that cap at 2 report their own reason",
        s32Unmeasured.Cap == 2 && s32Measured.Cap == 2 && s32Unmeasured.Why != s32Measured.Why,
        $"unmeasured: {s32Unmeasured.Why} | measured: {s32Measured.Why}");
    Check("the measured reason quotes the measurement and its unit",
        s32Measured.Why.Contains("21") && s32Measured.Why.Contains("ms per read"), s32Measured.Why);
    Check("only the unmeasured case says nothing has measured the volume",
        s32Unmeasured.Why.Contains("nothing has measured") && !s32Measured.Why.Contains("nothing has measured"),
        $"unmeasured: {s32Unmeasured.Why} | measured: {s32Measured.Why}");
    Check("the thrashing and fast reasons carry their numbers too",
        s32Thrashing.Why.Contains("1419") && s32Fast.Why.Contains("0") && s32Fast.Cap == int.MaxValue,
        $"{s32Thrashing.Cap}: {s32Thrashing.Why} | {s32Fast.Cap}: {s32Fast.Why}");
}

// S33: the walk is the second signal, and the only one that exists when every extraction in a run was served
// from the subtitle cache and nothing was read through the policy at all. A volume measured this way must not
// sit at the conservative ceiling for ever. The throughputs are the ones measured on 2026-09-14: the share
// walks a 2,4 GB file in 903 s (2,6 MB/s), local disk the same file in 17 s (137 MB/s).
//
// Note for anyone adding to this block: VolumeProfiles keys a profile by *device*, not by path, so every
// temp path under the same filesystem shares one profile. Checks that need a volume of their own must be
// written against the mapping directly, or against /dev/shm, which nothing else in this suite reads.
{
    Check("a volume only a walk has measured, fast, keeps no ceiling",
        SubSyncService.WalkCapForProfile(null, 137_000).Cap == int.MaxValue,
        "got " + SubSyncService.WalkCapForProfile(null, 137_000).Cap);
    Check("a volume only a walk has measured at the share's rate is held at 2",
        SubSyncService.WalkCapForProfile(null, 2_600).Cap == 2,
        "got " + SubSyncService.WalkCapForProfile(null, 2_600).Cap);
    Check("a volume whose walk barely moves is held at 1",
        SubSyncService.WalkCapForProfile(null, 250).Cap == 1,
        "got " + SubSyncService.WalkCapForProfile(null, 250).Cap);
    Check("the ceiling's reason names the walk as what measured it",
        SubSyncService.WalkCapForProfile(null, 2_600).Why.Contains("MB/s")
        && SubSyncService.WalkCapForProfile(null, 2_600).Why.Contains("2")
        && !SubSyncService.WalkCapForProfile(null, 2_600).Why.Contains("nothing has measured"),
        SubSyncService.WalkCapForProfile(null, 2_600).Why);

    // Both signals present: the more conservative of them decides, whichever side it comes from.
    Check("reads fast and a walk slow hold the volume at 2",
        SubSyncService.WalkCapForProfile(0.05, 2_600).Cap == 2,
        $"{SubSyncService.WalkCapForProfile(0.05, 2_600).Cap} - {SubSyncService.WalkCapForProfile(0.05, 2_600).Why}");
    Check("reads slow and a walk fast hold the volume at 2",
        SubSyncService.WalkCapForProfile(25.0, 137_000).Cap == 2,
        $"{SubSyncService.WalkCapForProfile(25.0, 137_000).Cap} - {SubSyncService.WalkCapForProfile(25.0, 137_000).Why}");
    Check("both signals fast leave the volume with no ceiling",
        SubSyncService.WalkCapForProfile(0.05, 137_000).Cap == int.MaxValue,
        "got " + SubSyncService.WalkCapForProfile(0.05, 137_000).Cap);
    Check("with neither signal a volume stays conservative",
        SubSyncService.WalkCapForProfile(null, null).Cap == SubSyncService.UnmeasuredWalkCap,
        "got " + SubSyncService.WalkCapForProfile(null, null).Cap);
}

// ... and end to end through the real path-to-volume wiring and the real planner, on a filesystem nothing
// else in this suite has read: a volume nothing has measured starts 2 of eight, and a walk that measures it
// fast lets all eight start. `subsync-walk-` is on /dev/shm, a device of its own.
{
    var s33Path = "/dev/shm/subsync-walk-" + Guid.NewGuid().ToString("N");
    var s33Volume = VolumeProfiles.KeyFor(s33Path);
    var s33Queue = new List<SyncJob>();
    for (var k = 0; k < 8; k++)
    {
        s33Queue.Add(new SyncJob
        {
            Id = Guid.NewGuid().ToString("N"), BatchId = "walkplan", BatchIndex = k,
            ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued
        });
    }

    Check("nothing has measured this volume yet",
        VolumeProfiles.For(s33Path).MsPerCall() is null && VolumeProfiles.For(s33Path).WalkCount == 0,
        $"reads {VolumeProfiles.For(s33Path).MsPerCall()}, walks {VolumeProfiles.For(s33Path).WalkCount}");
    Check("its ceiling is the conservative one",
        SubSyncService.WalkCapOfPath(s33Path).Cap == SubSyncService.UnmeasuredWalkCap,
        "got " + SubSyncService.WalkCapOfPath(s33Path).Cap);

    var s33Cold = SubSyncService.PlanStart(
        s33Queue, new List<SyncJob>(), "ultimate", "walkplan", 8,
        _ => s33Volume, _ => true, _ => false,
        walkCapOf: _ => SubSyncService.WalkCapOfPath(s33Path));
    Check("eight heavy jobs on a volume nothing has measured do not all start",
        s33Cold.Count == SubSyncService.UnmeasuredWalkCap, "planned " + s33Cold.Count);

    // The walk the plugin would record: 2,4 GB of media, 17 s of engine time, on local disk.
    VolumeProfiles.For(s33Path).ObserveWalk(2_400_000_000, 17_000);
    Check("the walk measures the volume without a single read",
        VolumeProfiles.For(s33Path).WalkCount == 1
        && VolumeProfiles.For(s33Path).WalkBytesPerMs() is not null
        && VolumeProfiles.For(s33Path).MsPerCall() is null,
        $"walks {VolumeProfiles.For(s33Path).WalkCount}, {VolumeProfiles.For(s33Path).WalkBytesPerMs()} bytes/ms, "
        + $"reads {VolumeProfiles.For(s33Path).MsPerCall()}");
    Check("and its ceiling is gone",
        SubSyncService.WalkCapOfPath(s33Path).Cap == int.MaxValue,
        $"{SubSyncService.WalkCapOfPath(s33Path).Cap} - {SubSyncService.WalkCapOfPath(s33Path).Why}");

    var s33Warm = SubSyncService.PlanStart(
        s33Queue, new List<SyncJob>(), "ultimate", "walkplan", 8,
        _ => s33Volume, _ => true, _ => false,
        walkCapOf: _ => SubSyncService.WalkCapOfPath(s33Path));
    Check("the same eight start once a walk has measured that volume fast",
        s33Warm.Count == 8, "planned " + s33Warm.Count + " - " + SubSyncService.WalkCapOfPath(s33Path).Why);
}

// The mapping from a measured cost per read onto a ceiling, at the numbers this plugin actually sees.
Check("nothing measured means the conservative ceiling, never none",
    SubSyncService.WalkCapForProfile(null).Cap == SubSyncService.UnmeasuredWalkCap,
    "got " + SubSyncService.WalkCapForProfile(null).Cap);
Check("a volume that measures fast is uncapped from that first measured pass onwards",
    SubSyncService.WalkCapForProfile(0.05).Cap == int.MaxValue && SubSyncService.WalkCapForProfile(1.0).Cap == int.MaxValue,
    $"0,05 ms -> {SubSyncService.WalkCapForProfile(0.05).Cap}, 1 ms -> {SubSyncService.WalkCapForProfile(1.0).Cap}");
Check("fabji's share at rest (13-46 ms per read) is capped at 2",
    SubSyncService.WalkCapForProfile(13).Cap == 2 && SubSyncService.WalkCapForProfile(46).Cap == 2,
    $"13 ms -> {SubSyncService.WalkCapForProfile(13).Cap}, 46 ms -> {SubSyncService.WalkCapForProfile(46).Cap}");
Check("the same share thrashing (1419-1613 ms per read) is held to 1",
    SubSyncService.WalkCapForProfile(1419).Cap == 1 && SubSyncService.WalkCapForProfile(1613).Cap == 1,
    $"1419 ms -> {SubSyncService.WalkCapForProfile(1419).Cap}");
Check("a path whose volume cannot be worked out is held at the conservative ceiling",
    SubSyncService.WalkCapOfPath(null).Cap == SubSyncService.UnmeasuredWalkCap
    && SubSyncService.WalkCapOfPath("").Cap == SubSyncService.UnmeasuredWalkCap,
    "got " + SubSyncService.WalkCapOfPath(null));

// End to end: the profile's own arithmetic, fed the share's latencies, produces the ceiling.
{
    var capClock = new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);
    var nasProfile = new VolumeProfile("//nas/share", () => capClock);
    for (var i = 0; i < 20; i++)
    {
        nasProfile.Observe(2048, 20 + (i % 3));
    }

    Check("a profile fed this share's latencies maps to a ceiling of 2",
        SubSyncService.WalkCapForProfile(nasProfile.MsPerCall()).Cap == 2,
        $"{nasProfile.MsPerCall():0.00} ms per read");
}


// End to end through the real dispatch entry: two volumes that really exist on different devices, one of
// them measured slow. This is the mixed-storage case the ceiling exists for - a batch spanning a fast
// volume and a storage-bound one - driven through PlanStart with the same predicates the plugin passes.
{
    var fastPath = "/opt/data/s28-fast-probe.mkv";     // whatever device this checkout lives on
    var slowPath = "/dev/shm/s28-slow-probe.mkv";      // tmpfs: a genuinely different device
    var probeFastVolume = MediaVolume.Of(fastPath);
    var probeSlowVolume = MediaVolume.Of(slowPath);

    Check("two paths on different devices are two volumes",
        !string.Equals(probeFastVolume, probeSlowVolume, StringComparison.Ordinal),
        $"fast {probeFastVolume} vs slow {probeSlowVolume}");

    // Feed the slow volume the latencies this user's share shows at rest; leave the other one alone.
    for (var i = 0; i < 20; i++)
    {
        VolumeProfiles.For(slowPath).Observe(2048, 20 + (i % 3));
    }

    Check("a volume measured at 20 ms per read carries a ceiling of 2",
        SubSyncService.WalkCapOfPath(slowPath).Cap == 2, "got " + SubSyncService.WalkCapOfPath(slowPath).Cap);
    Check("a real volume that nothing has read yet is held conservatively",
        SubSyncService.WalkCapOfPath(fastPath).Cap == SubSyncService.UnmeasuredWalkCap,
        "got " + SubSyncService.WalkCapOfPath(fastPath).Cap);

    // ... and the ceiling lifts as soon as that volume's own reads say it is fast.
    for (var i = 0; i < 20; i++)
    {
        VolumeProfiles.For(fastPath).Observe(1 << 20, 0.05);
    }

    Check("a volume whose own reads measured it fast keeps no ceiling",
        SubSyncService.WalkCapOfPath(fastPath).Cap == int.MaxValue,
        "got " + SubSyncService.WalkCapOfPath(fastPath).Cap);

    var pathOf = new Dictionary<Guid, string>();
    var probeQueue = new List<SyncJob>();
    foreach (var (path, count) in new[] { (slowPath, 3), (fastPath, 3) })
    {
        for (var k = 0; k < count; k++)
        {
            var job = new SyncJob
            {
                Id = Guid.NewGuid().ToString("N"), BatchId = "b", BatchIndex = probeQueue.Count,
                ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued
            };
            pathOf[job.ItemId] = path;
            probeQueue.Add(job);
        }
    }

    // The ordering that failed in the field: nothing measured yet, eight heavy jobs, one wave.
    var coldQueue = new List<SyncJob>();
    for (var k = 0; k < 8; k++)
    {
        coldQueue.Add(new SyncJob
        {
            Id = Guid.NewGuid().ToString("N"), BatchId = "cold", BatchIndex = k,
            ItemId = Guid.NewGuid(), Mode = "ultimate", Status = SyncJobStatus.Queued
        });
    }

    // Standalone on purpose: this is the ordering, before any device or profile in this block exists.
    const string coldVolume = "//nas/cold";
    var coldCap = SubSyncService.WalkCapForProfile(null);
    var coldPlanned = SubSyncService.PlanStart(
        coldQueue, new List<SyncJob>(), "ultimate", "cold", 8,
        _ => coldVolume, _ => true, _ => false,
        walkCapOf: _ => coldCap);
    Check("eight heavy jobs on a volume nothing has read yet do not all start",
        coldPlanned.Count == SubSyncService.UnmeasuredWalkCap,
        "planned " + coldPlanned.Count + " of 8 (ceiling " + SubSyncService.UnmeasuredWalkCap + ")");

    var planned = SubSyncService.PlanStart(
        probeQueue,
        new List<SyncJob>(),
        "ultimate",
        "b",
        6,
        job => MediaVolume.Of(pathOf.TryGetValue(job.ItemId, out var p) ? p : null),
        _ => true,
        _ => false,
        walkCapOf: job => SubSyncService.WalkCapOfPath(pathOf.TryGetValue(job.ItemId, out var p) ? p : null));

    var slowPlanned = planned.Count(j => pathOf[j.ItemId] == slowPath);
    var fastPlanned = planned.Count(j => pathOf[j.ItemId] == fastPath);
    Check("the storage-bound volume is held to 2 while the fast volume takes all 3",
        slowPlanned == 2 && fastPlanned == 3,
        $"slow {slowPlanned} of 3, fast {fastPlanned} of 3, {planned.Count} of 6 slots used");
}

Console.WriteLine();
// ---------------- The per-volume storage profile ----------------
var mkvScenarioPath = Environment.GetEnvironmentVariable("MKV_FIX_CUES") ?? string.Empty;
// It exists so a pass starts from what its volume has already shown rather than from the class defaults
// (0,05 ms per read, 1500 bytes per millisecond), fed only by reads the extraction was making anyway. What
// it must not be is a plain mean: this share answers in 13-46 ms with outliers to 1290 ms, and a mean over
// eight reads that contains one of those reports a storage 160 ms slow - the reading that has already lied
// here once (0,1 MB/s in one run, 11,1 MB/s in the next).
{
    var clock = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    var volume = new VolumeProfile("//nas/share", () => clock);

    Check("a volume says nothing before anything has been read on it",
        volume.MsPerCall() is null && volume.BytesPerMs() is null && volume.SampleCount == 0,
        $"{volume.SampleCount} sample(s)");

    var latencies = new List<double>();
    for (var i = 0; i < 20; i++)
    {
        var ms = 20 + (i % 3);
        latencies.Add(ms);
        volume.Observe(2048, ms);
    }

    var median = volume.MsPerCall()!.Value;
    var mean = latencies.Average();

    // Three reads of the tail this share produces: the median must not move, and the contrast with the
    // mean is the point of choosing it.
    for (var i = 0; i < 3; i++)
    {
        latencies.Add(1290);
        volume.Observe(2048, 1290);
    }

    var afterTail = volume.MsPerCall()!.Value;
    var meanAfterTail = latencies.Average();
    Check("the outlier tail this share produces does not move the profile's latency",
        Math.Abs(afterTail - median) < 1.5,
        $"median {median:0.00} -> {afterTail:0.00} ms, while the mean moved {mean:0.00} -> {meanAfterTail:0.00} ms");

    // Throughput: bytes decide, and the slowest fifth is dropped rather than averaged in.
    var throughput = new VolumeProfile("//nas/share", () => clock);
    for (var i = 0; i < 8; i++)
    {
        throughput.Observe(100_000, 10);
    }

    for (var i = 0; i < 2; i++)
    {
        throughput.Observe(100_000, 1000);
    }

    var measuredMbPerSecond = throughput.BytesPerMs()!.Value / 1000.0;
    Check("a byte-weighted, trimmed throughput ignores the slowest reads",
        Math.Abs(measuredMbPerSecond - 10.0) < 0.5,
        $"{measuredMbPerSecond:0.00} MB/s (a plain mean over the same reads is "
        + $"{1000.0 * 100_000 / (8 * 10 + 2 * 1000):0.00} MB/s)");

    // Decay: a sample counts half as much after a half-life, so a share that was busy ten minutes ago stops
    // deciding what the shares answer now.
    var decaying = new VolumeProfile("//nas/share", () => clock);
    for (var i = 0; i < 3; i++)
    {
        decaying.Observe(2048, 5);
    }

    var stale = decaying.MsPerCall()!.Value;
    clock = clock.Add(VolumeProfile.HalfLife).Add(VolumeProfile.HalfLife).Add(VolumeProfile.HalfLife);
    decaying.Observe(2048, 100);
    var refreshed = decaying.MsPerCall()!.Value;
    Check("an old sample stops deciding the median once it has aged",
        Math.Abs(stale - 5) < 0.01 && Math.Abs(refreshed - 100) < 0.01,
        $"3 x 5 ms -> {stale:0.00} ms, then one 100 ms read three half-lives later -> {refreshed:0.00} ms");

    // A pass starts from the volume and says so, without claiming it measured anything itself.
    var seeded = new ReadPolicy(1024, ReadRoute.CueIndexed, "seeded pass", decaying);
    Check("a pass starts from what its volume has already shown",
        seeded.SeededFromVolume == "//nas/share" && Math.Abs(seeded.MsPerCall - 100) < 0.01,
        $"seeded from {seeded.SeededFromVolume}, {seeded.MsPerCall:0.00} ms per read");
    Check("the log says the numbers came from the volume, not from this pass",
        !seeded.MeasuredOnce && seeded.DescribeProfile().Contains("already shown", StringComparison.Ordinal),
        seeded.DescribeProfile());
    seeded.Observe(4096, 12);
    Check("a pass feeds its own reads back to the volume",
        decaying.SampleCount == 5,
        $"{decaying.SampleCount} sample(s) on the volume after one read by the pass");

    var unseeded = new ReadPolicy(1024, ReadRoute.CueIndexed, "unseeded pass");
    Check("a pass with no volume to ask still says it is deciding from the defaults",
        unseeded.SeededFromVolume is null && !unseeded.MeasuredOnce
        && unseeded.DescribeProfile().Contains("defaults", StringComparison.Ordinal),
        unseeded.DescribeProfile());

    // Two paths on one volume are one profile; the volume is what the storage says it is, not the path.
    var fixtureDir = Path.GetDirectoryName(mkvScenarioPath) ?? ".";
    var here = VolumeProfiles.KeyFor(Path.Combine(fixtureDir, "a.mkv"));
    var alsoHere = VolumeProfiles.KeyFor(Path.Combine(fixtureDir, "sub", "b.mkv"));
    Check("two paths on one volume resolve to one profile",
        here == alsoHere
        && VolumeProfiles.For(Path.Combine(fixtureDir, "a.mkv")) == VolumeProfiles.For(Path.Combine(fixtureDir, "b.mkv")),
        $"{here} vs {alsoHere}");
    var otherVolume = VolumeProfiles.KeyFor("/dev/shm/x.mkv");
    if (Directory.Exists("/dev/shm"))
    {
        Check("a different volume is a different profile",
            otherVolume != here,
            $"{otherVolume} vs {here}");
    }
}

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
    // Where reads are cheap the cue index must still keep a pass far below the file's size - that is the
    // 2.0.5 lesson. Where they are not, reading about the file's bytes is the intended trade: it replaces
    // a round trip per cue. Which of the two applies is the storage's property, and the probe that ran
    // before the extraction is what this check can see of it.
    Check($"reads a fraction of the file ({scenario.Name})",
        readMb < fileMb * (mkvStats.StorageProbeMs < 1.0 ? 0.2 : 1.1),
        $"{readMb:0.00} MB of {fileMb:0.0} MB, probe {mkvStats.StorageProbeMs:0.00} ms per 16 KB");

    if (scenario.Method == "seekhead-cues")
    {
        // Video payloads are holes in the fixture: a reader that touched block payloads for a
        // track it does not want would read the whole file here. The 2.0.5 leak moved 3.2 GB to collect
        // 50 KB of text, and what guards against that is the fraction assertion above. A flat 1 MB
        // ceiling would forbid what the cue window now does on purpose: where a read is a round trip
        // (12,9-29,8 ms per 16 KB measured on the storages this was tuned on) the window is sized up so
        // that 800 cues cost tens of reads instead of 800 - spending bytes to save round trips. The
        // window is bounded at 1 MB, so a pass cannot read more than the file plus one window.
        Check("a cue-indexed pass reads no more than the file plus one window",
            mkvStats.BytesRead < fileMb * 1e6 + 1_100_000,
            $"{mkvStats.BytesRead / 1e6:0.000} MB in {mkvStats.ReadCalls} reads of a {fileMb:0.0} MB file");

        // A cue costs at most two reads: the cluster header that locates the block (the index
        // stores the block's offset relative to the cluster's data) and the block itself. Anything
        // beyond that means the walk re-reads, and on a high-latency share that is the whole cost of
        // an extraction - a real run showed 456 reads for 224 cues at ~100 ms each, 50 s per file.
        var overhead = 12;
        Check($"at most two reads per cue ({scenario.Name})",
            mkvStats.ReadCalls <= (found * 2) + overhead,
            $"{mkvStats.ReadCalls} reads for {found} cues ({(double)mkvStats.ReadCalls / Math.Max(1, found):0.00} per cue)");
    }

    // The on-disk form of "no byte is read twice": every range the pass fetched was used by a read, and
    // no read went to the file for bytes the pass already held. The 2.0.23 bug - 118,3 MB fetched and
    // then ignored, because the ranges missed the cluster head every block read starts with - shows up
    // on the first of these.
    Check($"every fetched range was used by a read ({scenario.Name})",
        mkvStats.PrefetchedUnusedRanges == 0,
        $"{mkvStats.PrefetchedUnusedRanges} of {mkvStats.PrefetchedRanges} ranges unused, "
        + $"{mkvStats.PrefetchedUnusedBytes / 1e6:0.00} MB of {mkvStats.PrefetchedBytes / 1e6:0.00} MB fetched");
    // A read must never go to the file for bytes the pass had already fetched - that is the fetched range
    // failing to cover the read it was made for. The bytes a fetch covers that the location phase read
    // before it are unavoidable (the SeekHead walk reads those headers before the pass knows its route)
    // and are bounded by one window, so they are reported apart from it.
    Check($"no read goes to the file past the fetch ({scenario.Name})",
        mkvStats.BytesReadAfterFetch == 0,
        $"{mkvStats.BytesReadAfterFetch / 1e6:0.00} MB read past the fetch "
        + $"(location overlap {mkvStats.BytesFetchedOverDisk / 1e6:0.00} MB, total twice {mkvStats.BytesReadTwice / 1e6:0.00} MB)");
    Check($"a fetch overlaps the location reads by no more than one window ({scenario.Name})",
        mkvStats.BytesFetchedOverDisk <= ReadPolicy.MaxWindow,
        $"{mkvStats.BytesFetchedOverDisk / 1e6:0.00} MB");
    Check($"the fetch serves the reads it was made for ({scenario.Name})",
        scenario.Method != "seekhead-cues" || mkvStats.MemoryServedReads > 0,
        $"{mkvStats.MemoryServedReads} of {mkvStats.ReadCalls} reads answered from memory");
    // "The policy predicts, the log compares": every phase states its expected cost, the pass prints
    // expected beside actual, and a pass that misses its own prediction by more than 2x is a warning in
    // the log and a failure here.
    Check($"the plan matched what the pass cost ({scenario.Name})",
        mkvStats.PlanMissed == 0 && mkvStats.PlanLines.Count > 0,
        mkvStats.PlanMissed + " miss(es) of " + mkvStats.PlanLines.Count + " plan(s): "
        + string.Join(" | ", mkvStats.PlanLines));
    Check($"the pass names its route and the storage it measured ({scenario.Name})",
        mkvStats.Route == (scenario.Method == "metadata-scan" ? "cluster-walk" : "cue-indexed")
        && mkvStats.MeasuredMsPerRead > 0,
        $"route={mkvStats.Route}, {mkvStats.MeasuredMsPerRead:0.000} ms/read, "
        + $"{mkvStats.MeasuredMbPerSecond:0.0} MB/s");
    Check($"the pass never reads more than the file plus one window ({scenario.Name})",
        mkvStats.BytesRead <= new FileInfo(scenario.Path).Length + ReadPolicy.MaxWindow,
        $"{mkvStats.BytesRead / 1e6:0.00} MB read of {fileMb:0.0} MB");

    // A plan that is a bound rather than an estimate must not read as a verified check. The walk stops once
    // it has found the blocks it came for, so what it will cost is not knowable from the index and a bound
    // tight enough to warn on would warn wrongly - so the line says so, and these two checks keep the gap
    // visible: exactly the bounded plans carry the marker, a plan the index fully locates never does, and
    // the walk's cost is held to something that *can* fail (one window per cluster it visits).
    var markedAsBound = mkvStats.PlanLines.Count(line => line.Contains(ReadPolicy.BoundNote));
    Check($"the log marks exactly the plans that are bounds ({scenario.Name})",
        markedAsBound == mkvStats.PlanBound,
        $"{mkvStats.PlanBound} bounded plan(s), {markedAsBound} marked line(s) of {mkvStats.PlanLines.Count}");
    Check($"a plan the index fully locates is not called a bound ({scenario.Name})",
        scenario.Name != "with subtitle cue points" || mkvStats.PlanBound == 0,
        mkvStats.PlanBound + " bounded plan(s)");
    Check($"the walk reads about one window per cluster ({scenario.Name})",
        scenario.Method != "metadata-scan" || mkvStats.ReadCalls <= (mkvStats.ClustersVisited * 3) + 64,
        $"{mkvStats.ReadCalls} read(s) for {mkvStats.ClustersVisited} cluster(s)");
}

// A cue point carries one CueTrackPositions *per track*: mkvmerge writes the video's position and every
// subtitle track's position into the same cue point, highest track number last. Reading the offsets of
// the last position in the cue point rather than the wanted track's own (which is what this did) hands
// the pass another track's block, so every cue point misses and its whole cluster is walked to find a
// block the index had already located - and a block an earlier cue point already emitted is emitted
// again, which is how kopps came out with 832 cues where its index and ffmpeg both say 829, and Sune i
// Grekland with 1251 where both say 1019. Measured on this fixture: 20 cluster visits for 10 cues
// before, 10 after; on kopps 933 before and 829 after; on Sune i Grekland 2038 before and 1019 after.
var groupedFixture = Environment.GetEnvironmentVariable("MKV_FIX_GROUPED");
if (!string.IsNullOrEmpty(groupedFixture) && File.Exists(groupedFixture))
{
    var groupedExpected = EnvInt("MKV_FIX_GROUPED_EXPECT");
    var groupedOk = MkvSubtitleExtractor.TryExtract(
        groupedFixture, 0, out var groupedSrt, out var groupedReason, null, out var groupedStats);
    var groupedFound = groupedSrt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
    Check("every subtitle block found (grouped cue points)",
        groupedOk && groupedFound == groupedExpected,
        $"{groupedFound} of {groupedExpected}, {groupedReason}");
    Check("a located cue point costs one cluster visit (grouped cue points)",
        groupedStats.ClustersVisited == groupedExpected,
        $"{groupedStats.ClustersVisited} cluster visit(s) for {groupedExpected} cue point(s)");
    Check("the wanted track's own block offsets are the ones used (grouped cue points)",
        groupedStats.BlockOffsets == groupedExpected && groupedStats.IndexedMisses == 0,
        $"{groupedStats.BlockOffsets} block offset(s) of {groupedExpected}, "
        + $"{groupedStats.IndexedMisses} indexed miss(es)");

    // A second pass over the same file must produce the same subtitles: what a pass has already emitted
    // is per-pass state, and state that outlives its pass removes cues instead of duplicating them.
    var againOk = MkvSubtitleExtractor.TryExtract(
        groupedFixture, 0, out var againSrt, out _, null, out _);
    Check("a second pass over the same file extracts the same subtitles",
        againOk && againSrt == groupedSrt,
        $"{againSrt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length} cue(s) on the second pass");
}

// Where the index locates only some of a track's cue points, the pass walks the clusters it has to, and a
// walk cannot know which of a cluster's blocks a neighbouring cue point already emitted. Measured on this
// fixture before the pass remembered its own blocks: 10 cues for 5 located cue points plus 5 walked
// clusters came out as cues the file does not hold, and on the D17 shape 843 cues came out of a file with
// 803 blocks. Both faces of one defect: what reached the count had to be the file's blocks, once each.
var mixedFixture = Environment.GetEnvironmentVariable("MKV_FIX_MIXED");
if (!string.IsNullOrEmpty(mixedFixture) && File.Exists(mixedFixture))
{
    var mixedExpected = EnvInt("MKV_FIX_MIXED_EXPECT");
    var mixedOk = MkvSubtitleExtractor.TryExtract(
        mixedFixture, 0, out var mixedSrt, out var mixedReason, null, out var mixedStats);
    var mixedFound = mixedSrt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
    Check("a partly located index still yields every block once (mixed cue points)",
        mixedOk && mixedFound == mixedExpected,
        $"{mixedFound} of {mixedExpected}, {mixedReason}");
    Check("no subtitle is emitted twice (mixed cue points)",
        DuplicateCuePairs(mixedSrt) == 0,
        DuplicateCuePairs(mixedSrt) + " repeated (start, text) pair(s)");
    // Half the cue points still carry a block offset, so the index is used for those and the walk for
    // the rest: the pass visits more clusters than the index located, and the count of cues stays the
    // number of blocks the file holds.
    // Every cue point is one visit, whether the index located its block or the walk found it, and the walk
    // over a cluster the cue point already counted is not a second visit: counting it there as well is what
    // reported 15 visits for 10 cue points here and 1205 for 803 on the D17 mixed index.
    Check("the index served what it located and the walk the rest (mixed cue points)",
        mixedStats.BlockOffsets == mixedExpected / 2 && mixedStats.ClustersVisited == mixedExpected,
        $"{mixedStats.BlockOffsets} of {mixedExpected} located, {mixedStats.ClustersVisited} cluster visit(s)");
}

static int DuplicateCuePairs(string srt)
{
    var seen = new HashSet<string>();
    var repeats = 0;
    foreach (var block in srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
    {
        var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !lines[1].Contains("-->"))
        {
            continue;
        }

        var key = lines[1].Split("-->")[0].Trim() + "|" + string.Join("\n", lines.Skip(2));
        if (!seen.Add(key))
        {
            repeats++;
        }
    }

    return repeats;
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
    // The shared pass is served out of the primary track's fetch, so its plan overstates what it will read.
    // Compared raw, every file with more than one subtitle logged "expected 0,62 MB/520 read(s), actual
    // 0,00 MB/0 read(s) ... this pass missed its own prediction" - a warning that means nothing, in the log
    // the user reads as the acceptance test, and the phase is reported with both numbers instead.
    Check("a shared pass served from the primary fetch is not reported as a miss",
        manyStats.PlanMissed == 0,
        string.Join(" | ", manyStats.PlanLines));
}

// ---------------- B10: the summary's derived figures come from the pass's final counters ----------------
// `FinaliseStats` computes ms/read and blocks/s from ReadCalls and TotalMs, and both call sites assign
// those *after* calling it, so the derived figures were 0 on every extraction that read the file. The
// live progress line computes its own copy from live values, which is why the zero never showed up in
// the log, but the summary object - documented as the human-readable one-liner for the log - carried it.
foreach (var (b10Label, b10Path) in new[]
    {
        ("cue index", Environment.GetEnvironmentVariable("MKV_FIX_CUES")),
        ("block walk", Environment.GetEnvironmentVariable("MKV_FIX_WALK")),
        ("grouped cues", Environment.GetEnvironmentVariable("MKV_FIX_GROUPED")),
        ("many tracks", Environment.GetEnvironmentVariable("MKV_FIX_MULTI"))
    })
{
    if (string.IsNullOrEmpty(b10Path) || !File.Exists(b10Path))
    {
        continue;
    }

    MkvSubtitleExtractor.TryExtract(b10Path, 0, out _, out _, null, out var b10);
    Check($"the summary's ms/read is the pass's own average ({b10Label})",
        b10.ReadLatencyMs > 0
        && Math.Abs(b10.ReadLatencyMs - (b10.ReadMs / Math.Max(1, b10.ReadCalls))) <= 0.05,
        $"reads={b10.ReadCalls} readMs={b10.ReadMs:0.0} latency={b10.ReadLatencyMs:0.00} | {b10}");
    Check($"the summary's blocks/s comes from the pass's total time ({b10Label})",
        b10.BlocksPerSecond > 0
        // No floor on the denominator: a cached fixture finishes a whole pass in well under a millisecond,
        // and clamping that to 1 ms made the figure look wrong on exactly the fastest runs.
        && Math.Abs(b10.BlocksPerSecond - (b10.SubtitleBlocks / (b10.TotalMs / 1000.0))) <= 0.05,
        $"blocks={b10.SubtitleBlocks} totalMs={b10.TotalMs:R} perSecond={b10.BlocksPerSecond:R}");
}

// ---------------- S14: the queue's ordinal is a subtitle ordinal, not a stream index ----------------
// The extraction lane counts subtitle tracks (0-based, as ffmpeg's 0:s:N). Jellyfin's MediaStream.Index
// counts every stream in the file, video and audio included, so the two numbers agree only when a file's
// subtitles happen to be its first streams. On the 2.0.27 run they did not agree on five jobs, which were
// refused with "subtitle ordinal 11 out of range (11 tracks)" - a file with eleven subtitle tracks behind
// a video and an audio stream, whose eleventh subtitle is stream index 13.
var ordinalPath = Environment.GetEnvironmentVariable("MKV_FIX_ORDINALS");
if (!string.IsNullOrEmpty(ordinalPath) && File.Exists(ordinalPath))
{
    // The failed file's shape: one video stream, one audio stream, eleven subtitle tracks. Stream index
    // 11 is the tenth subtitle - ordinal 9 - and the ordinals stop at 10.
    var streams = new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 0 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 1 }
    };
    for (var i = 0; i < 11; i++)
    {
        streams.Add(new()
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            Index = 2 + i,
            IsExternal = false
        });
    }

    var selected = streams.First(s => s.Index == 11 && s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle);
    // The text the fixture writes for ordinal 9 (track number 12), so the lane's result can be identified.
    const string SelectedText = "Track 9 line";
    var queueOrdinal = SubSyncService.EmbeddedSubtitleOrdinal(streams, selected); // what the enqueue path stores
    Check("the enqueue path names the subtitle it queued as an ordinal, not a stream index",
        queueOrdinal == 9, $"stream index {selected.Index} -> ordinal {queueOrdinal}");

    var laneTexts = new Dictionary<int, string>();
    var laneOk = MkvSubtitleExtractor.TryExtractMany(
        ordinalPath, new[] { queueOrdinal }, out laneTexts, out var laneReason, out _, default, null);
    Check("the extraction lane resolves the ordinal the enqueue path stored", laneOk, laneReason);
    if (laneOk && laneTexts.TryGetValue(queueOrdinal, out var laneText))
    {
        Check("the lane produced the track the queue meant, not a neighbour",
            laneText.Contains(SelectedText, StringComparison.Ordinal),
            $"{laneText.Length} chars, expected the track carrying \"{SelectedText}\"");
    }

    // The trap itself, kept as a check so the reason this translation exists cannot be quietly undone:
    // a stream index handed to the lane is refused, in the same words the real run logged.
    var rawTexts = new Dictionary<int, string>();
    var rawOk = MkvSubtitleExtractor.TryExtractMany(
        ordinalPath, new[] { selected.Index }, out rawTexts, out var rawReason, out _, default, null);
    Check("a stream index is refused where the lane wants a subtitle ordinal",
        !rawOk && rawReason == "subtitle ordinal 11 out of range (11 tracks)", rawReason);

    // The other shapes the translation has to survive. A sidecar track has no ordinal among the file's
    // tracks, and a stream Jellyfin lists but the container's embedded set does not hold gets no ordinal
    // rather than a wrong one.
    var subsFirst = new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 0 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 1 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 2 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 3 }
    };
    Check("a file whose subtitles are its first streams keeps its ordinal",
        SubSyncService.EmbeddedSubtitleOrdinal(subsFirst, subsFirst[2]) == 2);

    // Thunder in My Heart: four streams in front of ten subtitle tracks, so the track at stream 13 is
    // the tenth subtitle - the queued stream=13 that was refused as "out of range (10 tracks)".
    var thunder = new List<MediaBrowser.Model.Entities.MediaStream>
    {
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 0 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 1 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 2 },
        new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 3 }
    };
    for (var i = 0; i < 10; i++)
    {
        thunder.Add(new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 4 + i });
    }

    Check("ten subtitle tracks behind four other streams: stream 13 is ordinal 9",
        SubSyncService.EmbeddedSubtitleOrdinal(thunder, thunder[13]) == 9,
        $"ordinal {SubSyncService.EmbeddedSubtitleOrdinal(thunder, thunder[13])}");

    Check("a sidecar subtitle has no ordinal among the file's embedded tracks",
        SubSyncService.EmbeddedSubtitleOrdinal(subsFirst, new()
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            Index = 7,
            IsExternal = true
        }) == -1);

    Check("an embedded subtitle the container's set does not hold gets no ordinal, not a wrong one",
        SubSyncService.EmbeddedSubtitleOrdinal(subsFirst, new()
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            Index = 99
        }) == -1);

    Check("a lone embedded track is ordinal 0 even when Jellyfin numbers it otherwise",
        SubSyncService.EmbeddedSubtitleOrdinal(
            new List<MediaBrowser.Model.Entities.MediaStream>
            {
                new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Video, Index = 0 },
                new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Audio, Index = 1 },
                new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 2 }
            },
            new() { Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle, Index = 5 }) == 0);
}

// ---------------- The read policy: one decision, asserted across the matrix ----------------
// One component decides the route and the cost of a pass. These cells are synthetic on purpose: a cue
// index that locates its blocks and one that does not, one track and 32, against four storage profiles
// - no single fixture can be all of those at once, and the shape that matters (a subtitle block most of
// a megabyte into its cluster) is the shape a real server reported.
//
// A profile is (ms per read, MB/s), fed to the policy the way a real pass feeds it: by timing the reads
// the plan itself would make (Observe), never by a probe taken before the work. The pass is then
// re-planned from what it measured, which is the loop the extractor runs.
var policyFile = 8L * 1000 * 1000 * 1000;
const int policyClusters = 800;
const long policyClusterBytes = 1_000_000;
const long policyFirstCluster = 12_000_000;

List<(long ClusterStart, long BlockStart)> MatrixLayout(long blockOffset, int tracks)
{
    var blocks = new List<(long, long)>(policyClusters * tracks);
    for (var cluster = 0; cluster < policyClusters; cluster++)
    {
        var start = policyFirstCluster + (cluster * policyClusterBytes);
        for (var track = 0; track < tracks; track++)
        {
            blocks.Add((start, Math.Min(start + blockOffset + (track * 4096), start + policyClusterBytes - 4096)));
        }
    }

    return blocks;
}

double StorageTimeMs(long bytes, double latencyMs, double mbPerSecond) =>
    latencyMs + (bytes / (mbPerSecond * 1000.0));

bool AnyOverlap(IEnumerable<PlannedRead> reads)
{
    var sorted = reads.OrderBy(r => r.Start).ToList();
    for (var i = 1; i < sorted.Count; i++)
    {
        if (sorted[i].Start < sorted[i - 1].End)
        {
            return true;
        }
    }

    return false;
}

// The profiles are the storage classes the read pattern has to be right for: a local disk, a share that
// charges per round trip (fabji's, ~10 ms), one that is merely slow at bytes (11 MB/s), one that is both,
// and a share slow enough per read that no amount of parallel prefetch can hide a read per cue (50 ms is
// where the cue-indexed route's 2 calls per cue stop being free - see knowledge/FIX_PLAN.md R1).
var matrixProfiles = new (string Name, double LatencyMs, double MbPerSecond)[]
{
    ("fast", 0.05, 1500),
    ("10 ms/read", 10.0, 1500),
    ("11 MB/s", 0.05, 11),
    ("slow at both", 10.0, 11),
    ("50 ms/read", 50.0, 1500),
};

var matrixShapes = new[] { "blocks early", "blocks late", "no block offsets", "no cue index" };
var matrixTracks = new[] { 1, 32 };
var matrixRows = new List<(string Shape, int Tracks, string Profile, string Route, int Calls, long Bytes, bool Coarse)>();

Console.WriteLine();
Console.WriteLine("read policy matrix (route, planned calls, planned MB, window KB):");
foreach (var shape in matrixShapes)
{
    foreach (var tracks in matrixTracks)
    {
        foreach (var profile in matrixProfiles)
        {
            var policy = new ReadPolicy(policyFile, ReadRoute.CueIndexed, "matrix");
            ReadPlan plan = null;

            // Plan, then pay for that plan at this storage's prices, then re-plan: three rounds is what a
            // pass does when it prefetches, walks, and re-prices its window from its own reads.
            for (var round = 0; round < 3; round++)
            {
                var clusters = new List<long>();
                for (var cluster = 0; cluster < policyClusters; cluster++)
                {
                    clusters.Add(policyFirstCluster + (cluster * policyClusterBytes));
                }

                plan = shape switch
                {
                    "blocks early" => tracks == 1
                        ? policy.PlanIndexedReads(MatrixLayout(20_000, 1), Array.Empty<long>(), shape)
                        : policy.PlanSharedReads(MatrixLayout(20_000, tracks), Array.Empty<(long, long)>(), shape),
                    "blocks late" => tracks == 1
                        ? policy.PlanIndexedReads(MatrixLayout(900_000, 1), Array.Empty<long>(), shape)
                        : policy.PlanSharedReads(MatrixLayout(900_000, tracks), Array.Empty<(long, long)>(), shape),
                    "no block offsets" => tracks == 1
                        ? policy.PlanIndexedReads(Array.Empty<(long, long)>(), clusters, shape)
                        : policy.PlanSharedReads(Array.Empty<(long, long)>(), clusters.Select(c => (c, c + Math.Min(policyClusterBytes, ReadPolicy.MaxWindow))).ToList(), shape),
                    _ => policy.PlanWalk(policyFirstCluster, policyFile, policyClusters),
                };

                var reads = plan.Fetches.Count > 0 ? plan.Fetches : plan.Reads;
                foreach (var read in reads)
                {
                    policy.Observe(read.Length, StorageTimeMs(read.Length, profile.LatencyMs, profile.MbPerSecond));
                }
            }

            var expectedRoute = shape == "no cue index" ? ReadRoute.ClusterWalk
                : (tracks == 1 ? ReadRoute.CueIndexed : ReadRoute.SharedPass);
            var inside = plan.Reads.All(r => r.Start >= 0 && r.End <= policyFile)
                && plan.Fetches.All(f => f.Start >= 0 && f.End <= policyFile);
            var fetchOverlap = AnyOverlap(plan.Fetches);
            // A fetched range must be made for reads the pass will really make: bytes fetched for nothing
            // are the 2.0.23 bug in its general form (118,3 MB fetched, then read past).
            var fetchWasted = plan.Fetches.Any(f => !plan.Reads.Any(r => r.Start <= f.Start && r.End >= f.End));
            var windowOk = plan.WindowBytes >= ReadPolicy.MinWindow && plan.WindowBytes <= ReadPolicy.MaxWindow;
            var measured = policy.MsPerCall >= profile.LatencyMs * 0.5;
            var costMb = plan.ExpectedBytes / 1e6;
            var planKind = plan.Coarse ? "bounded, not exact" : "exact";

            // What the plan would cost on this storage if its reads went out one at a time, and what the
            // prefetch width actually leaves: calls / 16 rounds of latency. Both matter - the second is why a
            // read-per-cue route survives a slow share, and the first is what it costs when it does not.
            var serialMs = plan.ExpectedCalls * policy.MsPerCall;
            var overlappedMs = (double)plan.ExpectedCalls / ReadPolicy.PrefetchParallelism * profile.LatencyMs;
            Console.WriteLine(
                $"  {shape,-18} {tracks,2} track(s)  {profile.Name,-13} {plan.RouteLabel,-12} "
                + $"{plan.ExpectedCalls,7} calls  {costMb,8:0.0} MB  window {plan.WindowBytes / 1024,4} KB  "
                + $"({policy.MsPerCall:0.00} ms/read measured, {planKind})  "
                + $"waiting: {serialMs / 1000,7:0.00} s serial, {overlappedMs / 1000,6:0.00} s at 16 wide");

            matrixRows.Add((shape, tracks, profile.Name, plan.RouteLabel, plan.ExpectedCalls, plan.ExpectedBytes, plan.Coarse));

            Check($"matrix {shape} / {tracks} track(s) / {profile.Name}: {plan.RouteLabel}, {plan.ExpectedCalls} reads, {costMb:0.0} MB planned",
                plan.Route == expectedRoute && inside && !fetchOverlap && !fetchWasted && windowOk && measured,
                $"route {plan.RouteLabel} (want {expectedRoute}), window {plan.WindowBytes / 1024} KB, measured {policy.MsPerCall:0.00} ms/read, "
                + $"inside the file {inside}, overlapping fetch {fetchOverlap}, fetch no read used {fetchWasted}");
        }
    }
}

// The 2.0.5 leak guard, kept: where the index locates the blocks, a cue-indexed pass on cheap storage
// reads a small fraction of the file. Where it does not, reading about the file is the trade the walk
// makes on purpose, so the guard names the cells it applies to.
foreach (var row in matrixRows.Where(r => (r.Shape == "blocks early" || r.Shape == "blocks late") && r.Profile == "fast"))
{
    Check($"matrix {row.Shape} / {row.Tracks} track(s) on cheap storage reads a fraction of the file",
        row.Bytes < policyFile * 0.2,
        $"{row.Bytes / 1e6:0.0} MB of {policyFile / 1e6:0.0} MB ({100.0 * row.Bytes / policyFile:0.00}%)");
}

// ---------------- The profile the decisions rest on (2.0.25 R3) ----------------
// `Measured` used to read the pending sample window, which goes back to zero every 8 reads, so it was true
// only in the instant the 8th sample of a round arrived - and a check that used it failed while the profile
// was demonstrably measured. The names are exact now: PendingSamples is the window that has not been
// published yet, SampleCount is every read the pass has observed, MeasuredOnce is whether the profile is a
// measurement or still the class defaults, and the log says which of the two it is deciding from.
ReadPolicy ProfilePolicy(string label) => new(1_000_000_000, ReadRoute.CueIndexed, label);

var fresh = ProfilePolicy("fresh");
Check("a pass that has read nothing says it is deciding from the defaults",
    !fresh.MeasuredOnce && fresh.SampleCount == 0 && fresh.PendingSamples == 0
    && fresh.DescribeProfile().Contains("not measured"),
    fresh.DescribeProfile());
for (var i = 0; i < 7; i++)
{
    fresh.Observe(2048, 12.0);
}

Check("seven reads are pending, not an update",
    fresh.PendingSamples == 7 && fresh.SampleCount == 7 && !fresh.MeasuredOnce,
    $"{fresh.PendingSamples} pending, {fresh.SampleCount} total, measured {fresh.MeasuredOnce}");
fresh.Observe(2048, 12.0);
Check("the eighth read publishes the profile and clears the pending window",
    fresh.MeasuredOnce && fresh.ProfileUpdates == 1 && fresh.PendingSamples == 0 && fresh.SampleCount == 8
    && fresh.DescribeProfile().Contains("12.00 ms per read"),
    fresh.DescribeProfile());

// Sharing is the only reason the multi-track pass exists: 32 tracks in one pass must not cost 32 passes.
foreach (var shape in new[] { "blocks early", "blocks late" })
{
    foreach (var profile in matrixProfiles)
    {
        var oneTrack = matrixRows.First(r => r.Shape == shape && r.Tracks == 1 && r.Profile == profile.Name);
        var thirtyTwo = matrixRows.First(r => r.Shape == shape && r.Tracks == 32 && r.Profile == profile.Name);
        Check($"matrix {shape} / {profile.Name}: 32 tracks in one pass cost far less than 32 passes",
            thirtyTwo.Calls * 4 < oneTrack.Calls * 32,
            $"{thirtyTwo.Calls} reads shared vs {oneTrack.Calls * 32} separate ({oneTrack.Calls} per track)");
    }
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

// Which side is off the file's timeline decides whether a framerate rescale happens at all. A subtitle timed for
// PAL on a 23.976 fps file spans ~0.959 of the film; a reference taken from such a release does too. Rescaling the
// subtitle onto a mis-timed reference would move a correct subtitle off the video, and in a bulk run every
// subtitle of that file uses the same reference.
Check("a PAL-timed subtitle is the side to rescale", SubSyncService.IsTargetOffTheVideo(0.959 * 3000, 3000, 3000));
Check("a correct subtitle against a PAL-timed reference is left alone", !SubSyncService.IsTargetOffTheVideo(3000, 0.959 * 3000, 3000));
Check("two subtitles that both match the file are not rescaled", !SubSyncService.IsTargetOffTheVideo(3000, 2990, 3000));
Check("an unusable duration leaves the decision to the pair rule", SubSyncService.IsTargetOffTheVideo(3000, 0.959 * 3000, 30));

// A stretch is a claim about the whole timeline, and only the film's audio can test it: a differently cut subtitle
// produces the same span ratio as one from another framerate. A correct stretch leaves the audio alignment almost
// nothing to do; one applied to the wrong kind of difference still wants a large shift.
Check("a result the audio agrees with holds", SubSyncService.AlignmentHoldsAgainstAudio(1.0, 1200, 3000));
Check("a result the audio still wants 40 s of does not hold", !SubSyncService.AlignmentHoldsAgainstAudio(1.0, 40000, 3000));
Check("a result the audio wants rescaled again does not hold", !SubSyncService.AlignmentHoldsAgainstAudio(1.02, 500, 3000));
Check("the room grows with the runtime, up to a point", SubSyncService.AlignmentHoldsAgainstAudio(1.0, 12000, 4200));

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

// ---------------- S27: the engine says it is still running ----------------
// Driven against the real heartbeat and the real plugin log, not a mock: S27 is about the line landing
// in the file the plugin writes while an engine runs, and stopping with it. Before this change the log
// carried a start line and an exit line and nothing in between, so a slow run and a wedged one read
// identically - see the 91-minute run of 2026-09-14.
var heartbeatProbe = "s27-heartbeat-probe";
var heartbeatLog = Path.Combine(Path.GetTempPath(), "subsync-logs", "subsync.log");
string[] HeartbeatLines() => File.Exists(heartbeatLog)
    ? File.ReadAllLines(heartbeatLog).Where(l => l.Contains(heartbeatProbe)).ToArray()
    : Array.Empty<string>();

using (var heartbeat = new EngineHeartbeat(
    new EngineWatch(heartbeatProbe, "Example Film (2019).mkv", "a:0"), TimeSpan.FromMilliseconds(250)))
{
    await Task.Delay(800);
}

var heartbeatLines = HeartbeatLines();
Check("a running engine says so in the plugin's own log (S27)", heartbeatLines.Length >= 2,
    heartbeatLines.Length + " line(s)");
Check("the heartbeat line names the file, the elapsed time and the reference",
    heartbeatLines.Length > 0
    && heartbeatLines[^1].Contains("Example Film (2019).mkv")
    && heartbeatLines[^1].Contains("engine running")
    && heartbeatLines[^1].Contains("min elapsed")
    && heartbeatLines[^1].Contains("reference=a:0"),
    heartbeatLines.Length > 0 ? heartbeatLines[^1] : "none");
var heartbeatCount = heartbeatLines.Length;
await Task.Delay(500);
Check("the heartbeat stops when the process it describes is gone",
    HeartbeatLines().Length == heartbeatCount,
    HeartbeatLines().Length + " vs " + heartbeatCount);
Console.WriteLine("---- S27 sample: what the plugin log carries while an engine runs ----");
foreach (var line in heartbeatLines.TakeLast(3))
{
    Console.WriteLine("   " + line);
}

// ---------------- S21: a completion that wrote nothing is traceable ----------------
// Before this change the two "completed, nothing written" paths logged only to Jellyfin's log, so a
// batch could not be reconciled from the plugin log alone (27 jobs in one night's run). This invokes
// the shared helper the real paths call, then reads the file the plugin writes.
var nothingWritten = new SyncJob { Id = "s21-nothing-written-probe", Mode = "ultimate" };
nothingWritten.Outcome = "already in sync (median cue delta 0 ms, ratio 1.0000x) - nothing written";
nothingWritten.OutputPath = null;
nothingWritten.ExtractionNote = "seekhead-cues, 412 cues";
var logCompletion = typeof(SubSyncService).GetMethod(
    "LogPluginCompletion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check("the completion line has one shared definition (S21)", logCompletion is not null);
logCompletion!.Invoke(null, new object?[] { nothingWritten, null });
var nothingWrittenLines = File.Exists(heartbeatLog)
    ? File.ReadAllLines(heartbeatLog).Where(l => l.Contains("s21-nothing-written-probe")).ToArray()
    : Array.Empty<string>();
Check("a job that completed without writing still gets its plugin-log line (S21)",
    nothingWrittenLines.Any(l => l.Contains("completed:") && l.Contains("output=(none)") && l.Contains("nothing written")),
    nothingWrittenLines.LastOrDefault() ?? "no line");
Console.WriteLine("---- S21 sample: a completion that wrote nothing ----");
foreach (var line in nothingWrittenLines.TakeLast(2))
{
    Console.WriteLine("   " + line);
}

// ---------------- S23: a cancelled job has a line of its own ----------------
// A cancel used to leave only the batch-level count (`cancel batch <id>: N queued cancelled, M running
// stopped`), so 1 501 jobs in one night's run had no per-job record at all. This invokes the shared
// helper the real cancel paths call.
var cancelledJob = new SyncJob
{
    Id = "s23-cancelled-probe",
    Mode = "parallel",
    ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
    SubtitleIndex = 7
};
var logCancellation = typeof(SubSyncService).GetMethod(
    "LogPluginCancellation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check("the cancellation line has one shared definition (S23)", logCancellation is not null);
logCancellation!.Invoke(null, new object?[] { cancelledJob });
var cancelledLines = File.Exists(heartbeatLog)
    ? File.ReadAllLines(heartbeatLog).Where(l => l.Contains("s23-cancelled-probe")).ToArray()
    : Array.Empty<string>();
Check("a cancelled job gets its own plugin-log line (S23)",
    cancelledLines.Any(l => l.Contains("cancelled:") && l.Contains("mode=parallel")
                            && l.Contains("stream=7") && l.Contains("item=11111111-2222-3333-4444-555555555555")),
    cancelledLines.LastOrDefault() ?? "no line");
Console.WriteLine("---- S23 sample: a cancelled job ----");
foreach (var line in cancelledLines.TakeLast(2))
{
    Console.WriteLine("   " + line);
}

// ---------------- S24: one line that says how much is left ----------------
// The only depth figure the plugin had was the dispatch line, emitted when a job starts and naming only
// that job's batch: sizing the 2026-09-13/14 run meant hand-parsing lane lines and dispatch timestamps.
var logProgress = typeof(SubSyncService).GetMethod(
    "LogPluginProgress", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
Check("the progress line has one shared definition (S24)", logProgress is not null);
logProgress!.Invoke(null, new object?[] { 338, 7, 338, "Solsidan - S03E04 - Del 4.mkv", TimeSpan.FromMinutes(56.6) });
var progressLines = File.Exists(heartbeatLog)
    ? File.ReadAllLines(heartbeatLog).Where(l => l.Contains("queue: 338 queued")).ToArray()
    : Array.Empty<string>();
Check("the progress line answers how much is left (S24)",
    progressLines.Any(l => l.Contains("7 running") && l.Contains("338 file(s) left")
                           && l.Contains("lane currently on: Solsidan") && l.Contains("min elapsed on it")),
    progressLines.LastOrDefault() ?? "no line");
Console.WriteLine("---- S24 sample: how much is left ----");
foreach (var line in progressLines.TakeLast(2))
{
    Console.WriteLine("   " + line);
}
logProgress.Invoke(null, new object?[] { 4, 0, 2, null, null });
Check("the progress line does not claim a lane when the lane is idle",
    File.ReadAllLines(heartbeatLog).Any(l => l.Contains("queue: 4 queued") && l.Contains("lane idle")));

// ---------------- S25: the history survives a restart ----------------
// The real Save/Load pair against a real file: a batch is written, read back, and the line the plugin
// logs on restore is produced. Batches used to live only in memory, so a restart emptied History.
var historyPath = Path.Combine(Path.GetTempPath(), "s25-batch-history.json");
File.Delete(historyPath);
var historyEntry = new BatchHistoryEntry
{
    BatchId = "s25-batch-probe",
    Label = "Series - Season 1",
    CreatedUtc = new DateTime(2026, 9, 13, 22, 47, 0, DateTimeKind.Utc),
    Jobs = new List<BatchHistoryJob>
    {
        new BatchHistoryJob
        {
            Id = "s25-job-1", SubtitleIndex = 3, Mode = "parallel", BatchId = "s25-batch-probe",
            Status = "Completed", Outcome = "no change needed",
            CreatedAtUtc = new DateTime(2026, 9, 13, 22, 47, 0, DateTimeKind.Utc)
        },
        new BatchHistoryJob
        {
            Id = "s25-job-2", SubtitleIndex = 4, Mode = "parallel", BatchId = "s25-batch-probe",
            Status = "Running",
            CreatedAtUtc = new DateTime(2026, 9, 13, 22, 47, 1, DateTimeKind.Utc)
        }
    }
};
BatchHistory.Save(historyPath, new[] { historyEntry });
var historyBack = BatchHistory.Load(historyPath);
Check("a batch survives being written and read back (S25)",
    historyBack.Count == 1 && historyBack[0].BatchId == "s25-batch-probe" && historyBack[0].Jobs.Count == 2
    && historyBack[0].Jobs[0].SubtitleIndex == 3 && historyBack[0].Jobs[0].Outcome == "no change needed"
    && historyBack[0].Jobs[1].Status == "Running",
    historyBack.Count + " batch(es), " + (historyBack.Count > 0 ? historyBack[0].Jobs.Count : 0) + " job(s)");
Check("a missing history file is an empty history, not an error (S25)",
    BatchHistory.Load(Path.Combine(Path.GetTempPath(), "s25-does-not-exist.json")).Count == 0);
Console.WriteLine("---- S25 sample: what a restart now finds ----");
Console.WriteLine("   " + BatchHistory.DescribeRestore(historyBack.Count, historyBack.Sum(b => b.Jobs.Count), historyPath));

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

    raw_html = open(os.path.join(web, 'subsyncMain.html'), encoding='utf-8').read()
    markup = re.sub(r'<script\b.*?</script>', '', raw_html, flags=re.S)
    static_ids = collections.Counter(re.findall(r'\bid="([^"]*)"', markup))
    # The page's behaviour lives in the script file it loads (see the note where `pages` is built), so
    # every check that asks what the page does reads both; the checks that are about the markup read
    # `raw_html`/`markup`, which the script cannot pollute.
    main_script = open(os.path.join(web, 'subsyncMain.js'), encoding='utf-8').read()
    main_html = raw_html + '\n' + main_script
    settings_js = main_script[main_script.find('function loadConfig'):][:4000]
    for element_id in sorted(set(re.findall(r"\$\('([^']+)'\)", settings_js))):
        count = static_ids.get(element_id, 0)
        report(f"settings field '{element_id}' exists exactly once", count == 1, f'found {count}')

    report('the scope dropdown carries no fake item id', 'value="series"' not in main_html)
    report('no page behaviour hides in the markup again',
           not re.search(r'<script(?![^>]*\bsrc=)',
                         open(os.path.join(web, 'subsyncMain.html'), encoding='utf-8').read()))

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
    # A real browser showed what three source checks could not: the heartbeat ran, `/SubSync/Batches`
    # reported `Running`, `document.hidden` was false — and `#ss-runbox` stayed `display: none` for a
    # 50-task run the page had not started, because `renderMirroredBatch` filled rows into a box nobody
    # had shown. The mirror is only real if the box that carries it, and the Cancel/Kill control inside
    # it, actually appear.
    report('the panel mirrors a run it did not start',
           'startHeartbeat' in main_html and 'renderMirroredBatch' in main_html and 'showIdlePanel' in main_html)
    report('the mirrored run shows the box that carries it',
           re.search(r'function renderMirroredBatch\(view\) \{\s*\n(?:\s*//.*\n)*\s*showRunBox\(true\);',
                     main_html) is not None)
    report('a finished mirrored run hides that box again',
           "if (!watchedBatchTimer && !busy && mirroredBatchId)" in main_html
           and 'showRunBox(false);' in main_html)
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

    # B7: the extraction lane reads a whole file in one pass and is not a job, so a Kill needs its own
    # way in. Measured before this: 805.6 MB read in the 40 s after a Kill while /SubSync/Active
    # reported nothing running, because the lane handed the reader `CancellationToken.None`.
    report('a kill reaches the extraction lane\'s read',
           '_laneStop' in '\n'.join(plugin_sources) and 'passStop.Token' in '\n'.join(plugin_sources)
           and 'extract lane: pass on' in '\n'.join(plugin_sources))

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

    # S11: the reference a job is aligned against may only ever be the file's own audio or a sibling
    # track's *text*. Handing ffsubsync the media file makes the engine demux the whole container with
    # its own ffmpeg, once per job: measured on the 2.38 GB fixture, two jobs sat in that demux for 7
    # and 17 minutes and never finished, so a bulk run could not reach its end.
    report('the engine is never handed the container as a reference',
           'TryReadReferenceTextAsync' in service_text
           and 'No usable reference subtitle for' in service_text
           and 'allowFfmpegFallback' not in service_text)
    report('a reference that cannot be built falls back to the audio',
           'aligning against the audio instead' in service_text
           and 'PrepareAudioReferenceAsync' in service_text)
    # The other builders of the same file's reference wait for the one that builds it, and each writes
    # a name of its own: one shared "<target>.part" meant the loser failed to write it or failed to
    # move it into place, and that failure used to be answered by letting ffsubsync demux the file.
    report('only one job builds a file\'s reference at a time',
           '_referenceGates' in service_text and 'referenceGate.WaitAsync' in service_text
           and 'referenceGate.Release()' in service_text)
    report('the reference is written under this job\'s own temporary name',
           'referenceTarget + "." + job.Id + ".part"' in service_text)
    # Reference lifetime: the deletion counts running jobs as well as queued ones, or the last dispatch
    # of a batch removes the file the other workers are reading.
    report('a running job keeps its file\'s reference alive',
           re.search(r'var stillNeeded = finishedPath is not null.{0,500}SyncJobStatus\.Running',
                     service_text, re.S) is not None)
    report('ReferenceStore only releases a reference nothing is using',
           'stillInUse' in '\n'.join(plugin_sources))

    # Jellyfin 12 disables the legacy authorization mechanisms by default, so the header this
    # plugin used for years (X-Emby-Token) and the ?api_key= query parameter are ignored there:
    # every call answers 401, the settings tab and the history come up empty, and nothing in the
    # build says so. `Authorization: MediaBrowser Token="..."` and ?ApiKey= are the modern forms
    # and are accepted by 10.11 as well, so the legacy ones must not come back.
    pages = {name: open(os.path.join(web, name), encoding='utf-8').read()
             for name in ('subsync.js', 'subsyncMain.html', 'subsyncMain.js', 'configPage.html')}
    # The plugin page's client script lives in its own file from 2026-09-12: Jellyfin 12 injects the
    # plugin page as markup, and an inline <script> never executes there (measured in a real browser:
    # the page rendered and made no request of any kind, the status line stuck on "Checking status...",
    # while the same script loaded with a src ran and set window.__ssMainScriptRan). Every check below
    # is about what the page *does*, so it reads the page together with the script the page loads —
    # the raw markup is kept for the checks that are about the markup itself.
    raw_main_html = pages['subsyncMain.html']
    pages['subsyncMain.html'] = raw_main_html + '\n' + open(
        os.path.join(web, 'subsyncMain.js'), encoding='utf-8').read()
    report('the page loads its script instead of carrying it inline',
           '/SubSync/MainScript' in raw_main_html
           and '<script type="text/javascript">' not in raw_main_html)
    controller_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Api',
                                          'SubSyncController.cs'), encoding='utf-8').read()
    shared_store = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                     'SharedExtractionStore.cs'), encoding='utf-8').read()
    service_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                       'SubSyncService.cs'), encoding='utf-8').read()
    # Shipped 2.0.10 carried a rename that made `currentUserId()` call itself: with window.ApiClient present
    # (every real web client) the stack blew and the page stopped at "Loading libraries…", while every browser
    # test here ran where ApiClient is undefined and therefore never took that branch. The self-call is what
    # the check below exists for.
    report('a refused kill says so and does not stay armed',
           "setCancelButton('Kill all syncing', true);" in pages['subsyncMain.js']
           and 'The kill was refused: ' in pages['subsyncMain.js']
           and 'The cancel was refused: ' in pages['subsyncMain.js'])
    report('currentUserId() does not call itself',
           'if (mine) { return mine; }' in pages['subsyncMain.js']
           and 'return currentUserId();' not in pages['subsyncMain.js'])
    report('the page can use the session the client script hands it',
           'function bridge()' in pages['subsyncMain.js']
           and 'handed.token' in pages['subsyncMain.js']
           and 'handed.userId' in pages['subsyncMain.js']
           and 'window.__subsyncBridge = {' in pages['subsync.js'])
    report('one failing step cannot leave the page on "Loading libraries…"',
           "step('libraries', loadLibraries)" in pages['subsyncMain.js']
           and 'function explain(text)' in pages['subsyncMain.js']
           and "explain('Some parts of this page could not load" in pages['subsyncMain.js'])
    report('the client script attaches the page script itself',
           "'/SubSync/MainScript'" in pages['subsync.js']
           and 'data-ss-script' in pages['subsync.js']
           and 'function attachPluginPageScript()' in pages['subsync.js']
           # Fetched and run as a blob, because that is the shape measured to execute in Jellyfin 12;
           # the plain src tag stays as the fallback.
           and 'URL.createObjectURL(new Blob([src]' in pages['subsync.js'])
    report('the page script does nothing when it is loaded a second time',
           'if (window.__subsyncPageLoaded)' in pages['subsyncMain.js'])
    report('shared extraction output outlives every job that reads it',
           'SharedExtractionStore.Acquire(video.Path, job.Id)' in service_source
           and 'SharedExtractionStore.Release(video.Path, job.Id)' in service_source
           and 'subtitleInputPath = Path.Combine(sharedExtractDir, ' in service_source
           # the extracted input must not be written into the job's own directory again: that is the
           # directory the job deletes when it finishes, which failed the jobs that came after it.
           and 'Path.Combine(tempDir, $"subtitle_' not in service_source)
    report('a shared extraction directory is ref-counted and only removed when nothing reads it',
           'public static string Acquire(string videoPath, string jobId)' in shared_store
           and 'public static bool Release(string videoPath, string jobId)' in shared_store
           and 'Consumers.Remove(videoPath);' in shared_store
           and 'TryRemoveEmptyRoot();' in shared_store
           and 'Directory.CreateDirectory(directory);' in shared_store)
    report('clearing caches also clears shared extraction directories nothing reads',
           'SharedExtractionStore.Cleanup(id => _jobs.ContainsKey(id))' in service_source)
    report('the cancel control works for a run the page did not start',
           'var target = watchedBatchId || mirroredBatchId;' in pages['subsyncMain.html']
           and "api('SubSync/Batch/' + target + '/Cancel'" in pages['subsyncMain.html'])
    # Measured on a real server 2026-09-12: two subtitles of one 2 h movie started together, each ran the
    # engine against the audio (141 s each, cachedSpeech=False both times). The audio analysis is per file,
    # so only one job may run it while the file's speech cache is empty; the others wait for the harvest.
    report('only one job per file runs the audio analysis, the others wait for its harvest',
           '_speechGates' in service_source
           and 'await speechGate.WaitAsync()' in service_source
           and 'harvestedWhileWaiting' in service_source
           and 'ReleaseSpeechGate(job, videoPath);' in service_source
           and 'job.HoldsSpeechGate = true;' in service_source
           and 'public bool HoldsSpeechGate { get; set; }' in service_source)
    report('a refusal is reported as a refusal, not as a failure',
           'private static string StatusOf(SyncJob job)' in controller_source
           and 'StatusOf(j),' in controller_source
           and 'job.Phase = "Refused";' in open(
               os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'SubSyncService.cs'),
               encoding='utf-8').read())
    report('a page that fails to start says so instead of showing a dead surface',
           'function initFailed(ex)' in pages['subsyncMain.html']
           and 'could not start: ' in pages['subsyncMain.html']
           and "'catch'](initFailed)" in pages['subsyncMain.html'] or
           ".then(init)['catch'](initFailed)" in pages['subsyncMain.html'])
    # A queued run used to say "waiting to start", which is not an answer: on network storage the wait is the
    # file's extraction pass, and the job knows that (it is the planner's own predicate).
    report('a queued run says why it is waiting, and shows the plugin\'s own spinner',
           'function queuedReason(view, elsewhereBusy)' in pages['subsyncMain.js']
           and 'waiting to start' not in pages['subsyncMain.js']
           and 'runSpinner(true);' in pages['subsyncMain.js']
           and 'runSpinner(false);' in pages['subsyncMain.js']
           and 'starting as soon as a worker is free' in pages['subsyncMain.js']
           and '.ss-spinner { width: 12px; height: 12px' in pages['subsyncMain.html']
           and 'id="ss-spinner"' in pages['subsyncMain.html'])
    # Framerate correction works out of the box and stays switchable: a PAL-timed subtitle is stretched onto the
    # video's timeline on the audio path (the engine) and on the subtitle-reference path (the plugin does it,
    # because a reference subtitle has no frame rate for the engine to read).
    config_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                                      'PluginConfiguration.cs'), encoding='utf-8').read()
    report('framerate correction is on by default and the option to disable it remains',
           'public bool FixFramerate { get; set; } = true;' in config_source
           and 'Correct framerate mismatch (advanced)' not in pages['subsyncMain.html']
           and "On by default" in pages['subsyncMain.html']
           and 'id="ss-fixfps"' in pages['subsyncMain.html'])
    report('a subtitle reference rescales a framerate-mismatched subtitle before aligning',
           'private string? RescaleOntoReferenceSpan(' in service_source
           and 'TimeSpan videoDuration,' in service_source
           and 'RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job)' in service_source
           and 'IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds)' in service_source
           and 'so the reference is the odd one' in service_source
           and 'stretched to {factor:0.#####}x onto the reference' in service_source
           and 'rescaling it onto the reference\'s time base' in service_source
           and 'a different cut, left for the alignment to report' in service_source
           and 'BuildFfSubSyncArgs(config, referenceArg, engineInput' in service_source)
    report('a subtitle reference that is not the same cut is replaced by the audio, not refused',
           'ReferenceStore.Discard(videoPath, referenceSpec);' in service_source
           and 'discarding that ' in service_source
           and 'the reference subtitle is not the same cut as the video' in service_source
           and 'audioFallback = true;' in service_source
           and 'method=audio why=the reference subtitle was not the same cut' in service_source
           and 'the file\'s own subtitle track is not the same cut' in service_source
           and 'aligning against the audio instead' in service_source
           and 'audioArgs' in service_source
           and 'ReleaseSpeechGate(job, videoPath);' in service_source)
    report('a stretch is tested against the film\'s audio, and only when something was stretched',
           'private async Task<(string? Path, bool Dropped, string Input)> VerifyStretchAgainstAudioAsync(' in service_source
           and 'internal static bool AlignmentHoldsAgainstAudio(double ratio, long shiftMs, double videoSeconds)' in service_source
           and 'if (engineInput != subtitleInputPath)\n            {\n                var (verifiedPath, dropped, verifiedInput) = await VerifyStretchAgainstAudioAsync(' in service_source
           and 'the stretch does NOT hold against the audio' in service_source
           and 'aligned with offsets only' in service_source
           and 'the audio confirms the stretch' in service_source
           and '--no-fix-framerate' in service_source
           and 'a differently cut subtitle looks the same as a framerate mismatch in the spans' in service_source)
    report('the queued job carries the reason the interface shows',
           'private string QueuedReason(SyncJob job, int running, int limit)' in service_source
           and 'Reading subtitles from the video' in service_source
           and 'Waiting for a free worker ({running} of {limit} busy)' in service_source
           and 'waiting.Phase = QueuedReason(waiting, running, limit);' in service_source)
    # F8: the page assembled the whole selection and posted it in one request, so a long series or a
    # multi-library pick could pass the API's task bound (episodes x languages) and be refused with
    # "Could not start sync: 400: Batch is too large" - after minutes of reading subtitle lists, with
    # nothing in the page saying what the limit was. The split is client-side on purpose: the API keeps
    # its bound and the page never sends more than the bound in one request.
    cap_marker = 'request.Tasks.Count > '
    controller_text = '\n'.join(plugin_sources)
    api_cap = controller_text.split(cap_marker, 1)[1].split(')')[0].strip() if cap_marker in controller_text else ''
    page_cap = main_html.split('var BATCH_CHUNK = ', 1)[1].split(';')[0].strip() if 'var BATCH_CHUNK = ' in main_html else ''
    report('a selection larger than the API bound is split into batches the API accepts',
           api_cap.isdigit() and page_cap.isdigit() and api_cap == page_cap
           and main_html.count("api('SubSync/Batch'") == 2
           and main_html.count('return postBatch(label, ') == 2
           and 'rows.length <= BATCH_CHUNK' in main_html)

    report('the page reads and writes settings through the plugin, not the web client',
           "api('SubSync/Configuration')" in pages['subsyncMain.html']
           and 'getPluginConfiguration' not in pages['subsyncMain.html']
           and 'updatePluginConfiguration' not in pages['subsyncMain.html'])
    report('the plugin serves the configuration endpoints the page uses',
           '[HttpGet("Configuration")]' in controller_source
           and '[HttpPost("Configuration")]' in controller_source)
    report('the page can find its own session when the web client has not loaded',
           "localStorage.getItem('jellyfin_credentials')" in pages['subsyncMain.html']
           and 'function primeUserId()' in pages['subsyncMain.html'])
    report('the page starts only once it can authenticate',
           'whenApiReady' in pages['subsyncMain.html']
           and 'document.readyState === \'complete\' || document.readyState === \'interactive\') whenApiReady();'
           in pages['subsyncMain.html']
           and 'did not finish loading and no session was found' in pages['subsyncMain.html'])
    report('the page builds its own urls instead of needing the web client for them',
           'fetch(apiUrl(path)' in pages['subsyncMain.html'])
    report('the plugin serves the page script it advertises',
           '[HttpGet("MainScript")]' in controller_source
           and 'Web.subsyncMain.js' in controller_source)
    legacy_header = [name for name, text in pages.items()
                     if re.search(r'''\[\s*['"]X-Emby-Token['"]\s*\]\s*=''', text)]
    report('no page sends the legacy X-Emby-Token header', not legacy_header,
           ', '.join(legacy_header))
    legacy_param = [name for name, text in pages.items() if "'?api_key='" in text]
    report('no page builds a ?api_key= url', not legacy_param, ', '.join(legacy_param))
    for name in ('subsync.js', 'subsyncMain.html'):
        report(f'{name} authenticates with the Authorization header',
               'MediaBrowser Token=' in pages[name])
    # Merged 2026-09-11 (D4/F15, settled with the user): the dashboard page is a pointer to the plugin's
    # own settings page and calls nothing, so the requirement that applies to it is that it stays that
    # way — a second page that read or wrote the configuration is exactly what was removed.
    report('the dashboard page is a pointer, not a second settings surface',
           'configurationpage?name=subsync-main' in pages['configPage.html']
           and 'SubSyncConfigForm' not in pages['configPage.html']
           and '/SubSync/' not in pages['configPage.html'])

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
           'ReferenceStore.EndJob' in service and 'stillInUse' in store)
    report('an interrupted run cannot leave a reference behind',
           'ReferenceStore.SweepLeftovers' in plugin_source and 'SweepLeftovers' in store)
    report('a run releases its reference when it closes',
           'ReferenceStore.EndJob(finishedVideo' in service)
    report('the start decision is "your subtitle is extracted", not "may I share the file"',
           'job => ExtractionReady(job)' in service
           and 'SpeechIsCached(job)\n                            || (_jobContexts' not in service)
    report('releasing the reference happens outside the queue lock',
           service.index('ReferenceStore.EndJob(finishedVideo')
           < service.index('if (toStart.Count == 0)'))
    # Settled by fabji on 2026-09-11: the refusal stands, as AGENTS.md and the fix plan specify. The
    # never-refuse rule applies to a reference that cannot be *built* (S11 falls back to the audio);
    # a reference that exists and is provably from another cut is refused rather than used to write a
    # wrong file. See AGENTS.md, "Key Patterns & Gotchas".
    report('a reference-derived shift past the limit drops the reference and uses the audio',
           'MaxSubtitleReferenceOffsetSeconds' in service
           and 'it demanded {fromReference.ShiftMs} ms' in service
           and 'ReferenceStore.Discard(videoPath, referenceSpec);' in service
           and 'worth checking, a shift this size' in service
           and 'refusing a reference-derived shift' not in service)
    report('the offset window is a search range: a result on it is retried wider, and only a definitive answer is written',
           'public int MaxOffsetSeconds { get; set; } = 180;' in config_source
           and 'Math.Max(config.MaxOffsetSeconds * 2, 300)' in service
           and 'wide-window.srt' in service
           and 'wide-check.srt' in service
           and 'which a window that size cannot be trusted to have found' in service
           and 'the {wideSeconds} s window also reached its limit' in service
           and 'AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds)' in service
           # the rigid-shift attempt is gone: it applied a rescaled timeline's median displacement as a shift
           and 'ShiftSrtBy' not in service
           and 'shifted-verify.srt' not in service
           and 'wide-allowance.srt' not in service
           and 'wideAllowanceApplied' in service)
    report('the documented reference limit exists in the model',
           'MaxSubtitleReferenceOffsetSeconds' in service
           and 'MaxSubtitleReferenceOffsetSeconds' in open(
               os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                            'PluginConfiguration.cs'), encoding='utf-8').read()
           and 'ss-maxrefoffset' in pages['subsyncMain.html']
           and "c.MaxSubtitleReferenceOffsetSeconds = parseFloat" in pages['subsyncMain.html'])
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
    # Any authenticated user used to be able to install packages, wipe the caches, stop every run on
    # the server and read the plugin log with the server's own paths in it. Those four carry
    # Jellyfin's own administrator policy now; the item-scoped endpoints deliberately do not, so the
    # "Sync Subtitles" button on a detail page keeps working for a non-admin.
    elevated = re.findall(
        r'\[Authorize\(Policy = RequiresElevationPolicy\)\]\s*\n\s*\[Http(?:Post|Get)\("([^"]+)"\)\]',
        controller)
    item_scoped = ['Sync', 'Batch', 'Subtitles/Batch', 'Active', 'ClientScript', 'MainScript']
    report('the destructive and server-wide endpoints are administrator-only, the item ones are not',
           # The two configuration endpoints joined the list on 2026-09-12: the plugin's settings are
           # administrator-only in Jellyfin, and the page now reads and writes them here instead of
           # through the web client's API object (which is what it used to depend on).
           sorted(elevated) == ['Configuration', 'Configuration', 'Install', 'Kill', 'Log',
                                'SpeechCache/Clear']
           and 'RequiresElevation' in controller
           and all('\n    [HttpPost("%s")]' % r in controller or '\n    [HttpGet("%s")]' % r in controller
                   for r in item_scoped[:3])
           and not any(r in elevated for r in item_scoped))
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

    # The window fix that made 2.0.5 slower than 2.0.4 on a real server: a walk-sized window leaked into
    # reads at positions the cue index had named exactly, so 779 cues each pulled a 4 MB window (3.2 GB
    # moved to collect ~50 KB of text). 2.0.24 makes that one decision instead of three: a pass is handed a
    # plan, follows it, and every window it sets afterwards comes from the policy. So what is checked here
    # is that the decision has one home and nowhere else decides - the reads themselves are asserted on
    # real fixtures (see the Matroska section) and across the shape x storage matrix.
    extractor_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                         'MkvSubtitleExtractor.cs'), encoding='utf-8').read()
    policy_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                      'ReadPolicy.cs'), encoding='utf-8').read()
    report('a pass reads by the plan the policy made',
           'reader.Apply(cuePlan' in extractor_source
           and 'reader.Apply(walkPlan' in extractor_source
           and 'reader.Apply(sharedPlan' in extractor_source
           and 'policy.PlanIndexedReads(' in extractor_source
           and 'policy.PlanWalk(' in extractor_source
           and 'policy.PlanSharedReads(' in extractor_source)
    window_lines = [line.strip() for line in extractor_source.splitlines()
                    if 'WindowSize =' in line or ('WindowSize {' in line and 'ReadPolicy' in line)]
    report('no window is chosen outside the policy',
           window_lines
           and all('ReadPolicy.' in line and ('plan.WindowBytes' in line or 'window' in line
                                              or 'ReadPolicy.MinWindow * 8' in line
                                              or 'ReadPolicy.MaxWindow' in line)
                   for line in window_lines)
           and 'IndexWindow' not in extractor_source
           and 'GetWalkWindow' not in extractor_source,
           window_lines)
    # 2.0.22 sized the cue window from a probe taken before the work; on one box that probe reported 0,4 ms
    # and 255 ms per 16 KB within minutes, because other jobs and lanes were loading the same disk. The
    # storage is now measured from the reads the pass itself makes, and the number it derives is logged.
    report('every route says whether its numbers are measured or still the defaults',
           extractor_source.count('extract profile: ') == 1
           and 'PluginLog.Info("extract profile: " + policy.DescribeProfile())' in extractor_source
           and 'not measured yet - deciding from the defaults' in policy_source
           and 'MeasuredOnce' in policy_source,
           extractor_source.count('extract profile: '))
    report('the storage is measured from the pass\'s own reads, never from a probe',
           'public void Observe(long bytes, double ms)' in policy_source
           and extractor_source.count('Observe(read, watch.Elapsed.TotalMilliseconds)') == 2
           and 'samples.Sort();' not in extractor_source
           and 'foreach (var fraction in new[] { 0.10, 0.35, 0.60, 0.85 })' not in extractor_source)
    # Both axes of the storage are priced - what a call costs and what a byte costs - instead of one ratio
    # between two probe points, which read as throughput-bound and refused to grow where it mattered.
    report('both axes of the storage are priced, not one ratio',
           'public double Price(long bytes, long calls)' in policy_source
           and 'calls * _msPerCall' in policy_source
           and '(bytes / Math.Max(0.001, _bytesPerMs))' in policy_source
           and '_msPerCall * _bytesPerMs' in policy_source)
    report('a read served from memory is never billed as a read',
           'NoteMemoryServed(position, destination.Length)' in extractor_source
           and 'Ledger.RecordServedFromMemory' in extractor_source
           and 'MemoryServedReads' in extractor_source)
    # The walk visits clusters in file order, so its region is brought in as chunks: a window per cluster
    # was one round trip per cluster (1 533 reads of 4,7 KB, ~19 s of waiting on the user's share).
    report('the walk brings its region in as chunks, not a round trip per cluster',
           'SetSequentialChunk(clusterPosition, ReadPolicy.WalkChunkBytes)' in extractor_source
           and 'reader.Apply(walkPlan' in extractor_source
           and 'RandomAccess.Read' in extractor_source)
    report('the shared pass reads a cluster-sized region, never the whole file',
           'reader.SetWindow(policy.ClusterWindow(' in extractor_source
           and 'public int ClusterWindow(long clusterBytes' in policy_source
           and 'Math.Min(end, FileLength) - start' in policy_source)
    report('each pass states its expected cost and prints it beside the actual',
           'policy.Compare(cuePlan' in extractor_source
           and 'policy.Compare(walkPlan' in extractor_source
           and 'policy.Compare(sharedPlan' in extractor_source
           and 'expected {2:0.00} MB/{3} read(s)' in policy_source)
    report('a fetch that no read used, or a read past the fetch, is named in the log',
           'Ledger.UnusedFetchedRanges' in extractor_source
           and 'MB past the fetch' in extractor_source
           and 'UnusedFetchedRanges' in policy_source
           and 'BytesReadAfterFetch' in policy_source)

    # The run that made all this visible: 240 jobs queued, four workers limit, and the plugin silent
    # for 46 s and then 83 s because the pump waited on a signal that a job becoming startable never
    # sends. And extraction sat inside the job, holding a worker slot for the whole read.
    report('the scheduler pump re-plans on a timer instead of sleeping until signalled',
           '_wakePump.WaitAsync(TimeSpan.FromMilliseconds(750))' in service
           and 'await _wakePump.WaitAsync().ConfigureAwait(false)' not in service)
    report('extraction runs in its own lane, not inside a sync worker',
           'ExtractLaneAsync' in service and 'NextFileToExtract' in service
           and 'WakeExtractor();' in service)
    report('a job is only started once its subtitle is already extracted',
           'job => ExtractionReady(job)' in service)
    report('the lane is a safety valve, not a single point of failure',
           '!LaneAlive' in service and 'private bool LaneAlive' in service)
    extractor_text = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                       'MkvSubtitleExtractor.cs'), encoding='utf-8').read()
    # The walk's route is known before it starts, so its reads go out together instead of one round trip
    # per cluster: the plan names them, BlobReader.Apply fetches them (16 wide, through RandomAccess), and
    # the walk then finds them in hand. Measured on the user's share: one read costs 12,8 ms whatever its
    # size, and a pass needs ~2 000 of them, so ~26 s of a 61 s pass was waiting between reads.
    report('the plan is fetched up front instead of a round trip per cluster',
           'Prefetch(fetches, ReadPolicy.PrefetchParallelism' in extractor_text
           and 'TryReadAhead' in extractor_text
           and 'RandomAccess.Read' in extractor_text
           and 'AlreadyInHand' in extractor_text)
    report('several files are extracted at once, not one after another',
           'MaxExtractionLanes' in service and '_laneTasks.Add(Task.Run(ExtractLaneAsync))' in service)
    report('a track with no text does not leave its job queued for ever',
           '_extractTried.ContainsKey(key)' in service)
    report('the planner asks whether a job may start, not only whether it may share',
           'policy.MayStart is not null && !policy.MayStart(candidate)' in service)
    report('the lane produces the reference track as well as the languages',
           'ReferenceOrdinalsFor' in service and 'foreach (var referenceOrdinal in ReferenceOrdinalsFor' in service)
    report('a track with no text is asked for once, not every pass',
           '_extractTried' in service)

    # S14: the enqueue path stored the selected stream's own Index as the subtitle's ordinal. Those are
    # different numbers on any file whose subtitles are not its first streams: the lane counts subtitle
    # tracks, Jellyfin counts every stream, video and audio included. Measured on the 2.0.27 run, that
    # refused five jobs with "subtitle ordinal 11 out of range (11 tracks)".
    report('the queue translates a stream index into a subtitle ordinal before dispatching',
           'EmbeddedSubtitleOrdinal(source.MediaStreams, subtitleStream)' in service
           and 'var subtitleOrdinal = subtitleStream.Index' not in service)
    report('the enqueue path and the run-time resolver share one definition of the ordinal',
           service.count('EmbeddedSubtitleOrdinal(') >= 3)
    report('a failed ffsubsync says why, not just the exit code',
           'throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.{why}")' in service)

    report('the lane reports what it produced for each file',
           'extract lane:' in service and 'DescribeExtraction(stats.Method)' in service)

    report('clear cache empties the extracted-subtitle cache too',
           'SubtitleCache.Clear()' in controller and 'removedSubtitles' in controller)
    report('extraction serves a previously extracted subtitle without reading the file',
           'SubtitleCache.TryGet(videoPath, subtitleOrdinal' in service
           and 'method=cache' in service)
    report('a pass stores what it extracted for later runs',
           'SubtitleCache.Store(videoPath, pair.Key' in service
           and 'SubtitleCache.Store(videoPath, subtitleOrdinal' in service)

    report('clearing never deletes a running job\'s scratch folder',
           '_jobs.ContainsKey(name)' in service and 'ref" continue' not in service)
    report('the button says what it clears',
           'Cached data' in pages['subsyncMain.html']
           and 'audio analysis' in pages['subsyncMain.html']
           and 'reference subtitle' in pages['subsyncMain.html'].lower())

    report('a reference with too few cues is dropped and the audio used instead',
           'LooksLikeSignsTrack(reuseCues' in service
           and 'falling back to the audio for this job' in service
           and 'ReferenceStore.Discard' in service and 'public static void Discard' in store)
    # S27: visibility for a long engine run - and nothing else. The heartbeat must write to the
    # plugin's own log (not only Jellyfin's), be driven by the process's own lifetime, and add no
    # deadline or kill: a feature film with an audio reference legitimately takes an hour.
    heartbeat_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                         'EngineHeartbeat.cs'), encoding='utf-8').read()
    report('a long engine run says it is still running, in the plugin log (S27)',
           'public static string Describe(' in heartbeat_source
           and 'PluginLog.Info(Describe(' in heartbeat_source
           and 'TimeSpan.FromMinutes(5)' in heartbeat_source
           and 'new EngineHeartbeat(watch)' in service
           and 'Path.GetFileName(videoPath), referenceStream' in service
           and 'CancelAfter' not in heartbeat_source
           and 'Kill' not in heartbeat_source)

    # S21: every completed job is traceable in the plugin log, whatever it wrote. One shared helper
    # builds the line and both "completed, nothing written" paths call it - they used to log only to
    # Jellyfin's log, which is what made a batch impossible to reconcile from the plugin log.
    report('a completion that wrote nothing is traceable in the plugin log (S21)',
           'private static void LogPluginCompletion(SyncJob job, long? outputSize)' in service
           and service.count('LogPluginCompletion(job,') >= 3
           and 'job {job.Id} completed: mode={job.Mode} output={job.OutputPath ?? "(none)"}' in service)

    # S23: every cancelled job has a terminal line of its own, whatever cancelled it - the batch
    # cancel and the UI's kill both go through the one definition.
    report('a cancelled job is traceable, not just counted (S23)',
           'private static void LogPluginCancellation(SyncJob job)' in service
           and service.count('LogPluginCancellation(') >= 5
           and 'job {job.Id} cancelled: mode={job.Mode} item={job.ItemId} stream={job.SubtitleIndex}' in service)

    # S24: one line that answers "how much is left", emitted from the pump's own tick so it appears even
    # while a run is silent inside one long engine call.
    report('the log answers how much is left, without hand-parsing (S24)',
           'public static string DescribeProgress(' in service
           and 'queue: {queued} queued, {running} running, {filesLeft} file(s) left' in service
           and 'lane currently on: {laneFile}' in service
           and 'MaybeLogProgress();' in service
           and 'ProgressLineSeconds = 60' in service
           and 'ConcurrentDictionary<string, DateTime> _passInFlight' in service)

    # S25: the batch history is persisted in the same shape of state file the sweep already keeps, so a
    # restart no longer empties the History tab. Restored jobs are display rows (no job context, so the
    # pump can never start one), the cleanup timer leaves them alone, and a job interrupted by the
    # restart comes back as cancelled rather than as a phantom running job.
    batch_history = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                      'BatchHistory.cs'), encoding='utf-8').read()
    report('the batch history survives a restart (S25)',
           'public static class BatchHistory' in batch_history
           and 'public const int MaxBatches = 20' in batch_history
           and 'private void RestoreBatchHistory()' in service
           and 'RestoreBatchHistory();' in service
           and 'MaybePersistBatchHistory();' in service
           and '_historyOnlyJobs.Contains(kvp.Key)' in service
           and 'interrupted by a plugin restart' in service
           and 'BatchHistory.Save(BatchHistory.DefaultPath, SnapshotBatchHistory());' in service)

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

    # One file written the way mkvmerge writes them: a cue point per timestamp, holding a
    # CueTrackPositions for the video and for every subtitle track, so the highest track number is last.
    grouped_path = os.path.join(fixtures, 'grouped.mkv')
    subprocess.run(['python3', generator, grouped_path, '--clusters', str(clusters),
                    '--payload', '1', '--sub-every', str(sub_every), '--sub-tracks', '3',
                    '--grouped-cues'], check=True, capture_output=True)
    env['MKV_FIX_GROUPED'] = grouped_path
    env['MKV_FIX_GROUPED_EXPECT'] = str(expected)

    # The same file with half of the wanted track's block offsets removed, so the pass has to walk the
    # clusters the index no longer locates. This is the D17 patch shape, and it is what made the walk
    # hand back a block an adjacent cue point had already emitted.
    mixed_path = os.path.join(fixtures, 'mixed.mkv')
    patch = subprocess.run(['python3', os.path.join(REPO, 'tests', 'fixtures', 'patch_cues.py'),
                            'patch', grouped_path, mixed_path, '3', '2'],
                           capture_output=True, text=True)
    if patch.returncode != 0 or not os.path.exists(mixed_path):
        print(patch.stdout[-800:], patch.stderr[-800:])
        return 1
    env['MKV_FIX_MIXED'] = mixed_path
    env['MKV_FIX_MIXED_EXPECT'] = str(expected)

    # One file with three subtitle tracks, for the multi-track pass.
    multi_path = os.path.join(fixtures, 'multi.mkv')
    subprocess.run(['python3', generator, multi_path, '--clusters', str(clusters),
                    '--payload', '1', '--sub-every', str(sub_every), '--sub-tracks', '3'],
                   check=True, capture_output=True)
    env['MKV_FIX_MULTI'] = multi_path
    env['MKV_FIX_MULTI_EXPECT'] = str(expected)

    # S14: eleven subtitle tracks behind one video and one audio stream, so a container stream index and a
    # subtitle ordinal are different numbers in the same file. This is the shape of the file that produced
    # "subtitle ordinal 11 out of range (11 tracks)" on the 2.0.27 run.
    ordinals_path = os.path.join(fixtures, 'ordinals.mkv')
    subprocess.run(['python3', generator, ordinals_path, '--clusters', str(clusters),
                    '--payload', '1', '--sub-every', str(sub_every), '--sub-tracks', '11'],
                   check=True, capture_output=True)
    env['MKV_FIX_ORDINALS'] = ordinals_path
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
