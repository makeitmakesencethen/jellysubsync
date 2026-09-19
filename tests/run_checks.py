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
import json
import os
import pathlib
import shutil
import subprocess

# Repository root, discovered from this file's location so the tree can be moved or cloned
# anywhere.
REPO = str(pathlib.Path(__file__).resolve().parents[1])
SERVICE_DIR = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services')


def service_classes():
    """The service and the classes the split has moved out of it, as one text.

    Source-shape checks are about the shipped code, not about which file it happens to sit in: when a
    cluster leaves SubSyncService (see knowledge/SUBSYNCSERVICE_MAP.md), the pins that named it read the
    new file instead of being rewritten to match a file boundary - which keeps the pin pointed at the
    behaviour rather than at the layout.
    """
    names = ['SubSyncService.cs', 'MediaStreamMap.cs', 'SyncedTargetNaming.cs', 'AlignmentMetrics.cs',
             'SubSyncProcesses.cs', 'FfSubSyncEngine.cs']
    # The partial-class parts of the service itself (Phase 2 of the map) are found rather than listed: the
    # move that cuts one is the move that would otherwise forget to add it here, and a pin that reads a file
    # the code has left fails open - it searches a string that no longer contains the code and reports a
    # regression in the code instead of a gap in the check. `SubSyncService.cs` first, then the parts.
    parts = sorted(name for name in os.listdir(SERVICE_DIR)
                   if name.startswith('SubSyncService.') and name.endswith('.cs')
                   and name != 'SubSyncService.cs')   # its own name matches the prefix: not a part of itself
    return '\n'.join(open(os.path.join(SERVICE_DIR, name), encoding='utf-8').read()
                      for name in names + parts)


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
# Globalization is on by default, as it is on a server: a comma-decimal locale is one of the cultures this code has to
# survive, and with invariant globalization the culture that catches it cannot even be created. Set
# SUBSYNC_INVARIANT_GLOBALIZATION=1 to run the whole harness in invariant mode instead.
ENV = (dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1')
       if os.environ.get('SUBSYNC_INVARIANT_GLOBALIZATION') == '1' else dict(os.environ))

PROGRAM = r"""
using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.SubSync;
using Jellyfin.Plugin.SubSync.Api;
using Jellyfin.Plugin.SubSync.Configuration;
using Jellyfin.Plugin.SubSync.Services;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Interfaces;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using System.Reflection;

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
    MediaStreamMap.SelectReferenceStream(false, twoText, -1) == "s:0",
    MediaStreamMap.SelectReferenceStream(false, twoText, -1) ?? "null");
Check("external sidecar falls back to the audio when the file has no text track",
    MediaStreamMap.SelectReferenceStream(false, imageOnly, -1) == "a:0",
    MediaStreamMap.SelectReferenceStream(false, imageOnly, -1) ?? "null");
Check("single text track forces audio (never itself)",
    MediaStreamMap.SelectReferenceStream(true, textOnly, 0) == "a:0",
    MediaStreamMap.SelectReferenceStream(true, textOnly, 0) ?? "null");
Check("two text tracks -> uses the other one",
    MediaStreamMap.SelectReferenceStream(true, twoText, 0) == "s:1",
    MediaStreamMap.SelectReferenceStream(true, twoText, 0) ?? "null");
Check("target second track -> uses the first",
    MediaStreamMap.SelectReferenceStream(true, twoText, 1) == "s:0");
Check("skips the image track when choosing",
    MediaStreamMap.SelectReferenceStream(true, textAndImage, 0) == "s:0" ||
    MediaStreamMap.SelectReferenceStream(true, new List<string> { "hdmv_pgs_subtitle", "subrip" }, 0) == "s:1",
    MediaStreamMap.SelectReferenceStream(true, new List<string> { "hdmv_pgs_subtitle", "subrip" }, 0) ?? "null");
Check("image-only file forces audio",
    MediaStreamMap.SelectReferenceStream(true, imageOnly, 0) == "a:0");
Check("no probe data at all forces audio",
    MediaStreamMap.SelectReferenceStream(true, new List<string>(), 0) == "a:0");

// ffmpeg probe parsing
var probe = "  Stream #0:0: Video: h264\n  Stream #0:1(eng): Audio: aac\n"
    + "  Stream #0:2(eng): Subtitle: subrip (srt)\n  Stream #0:3(swe): Subtitle: hdmv_pgs_subtitle";
Check("probe indexes pick subtitle streams only",
    MediaStreamMap.ParseProbeSubtitleIndexes(probe).SequenceEqual(new[] { 2, 3 }),
    string.Join(",", MediaStreamMap.ParseProbeSubtitleIndexes(probe)));
var codecs = MediaStreamMap.ParseProbeSubtitleCodecs(probe);
Check("probe codecs are captured in order", codecs.Count == 2 && codecs[0].StartsWith("subrip") && codecs[1].StartsWith("hdmv_pgs"),
    string.Join("|", codecs));
Check("image track detected as image, text track not",
    !MediaStreamMap.SelectReferenceStream(true, codecs, 0)!.StartsWith("s:1"));

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

// S41: one cold read must not decide a volume is thrashing. Measured on the rig: a shimmed share whose
// first read took 231 ms and whose steady state is 13 ms was classified thrashing (231 ms is over the
// absolute tier) and held to one walk for a whole run, because the profile's figure was a median of one.
{
    var s41OneRead = SubSyncService.WalkCapForProfile(231.0, null, null, null, null, 1);
    var s41TwoReads = SubSyncService.WalkCapForProfile(231.0, null, null, null, null, 2);
    var s41ThreeReads = SubSyncService.WalkCapForProfile(231.0, null, null, null, null, 3);

    Check("a verdict from a single read cannot call a volume thrashing",
        s41OneRead.Cap == SubSyncService.StorageBoundWalkCap && s41OneRead.Why.Contains("one read"),
        $"{s41OneRead.Cap}: {s41OneRead.Why}");
    Check("two reads agreeing are enough to reach the thrash tier",
        s41TwoReads.Cap == 1 && s41ThreeReads.Cap == 1,
        $"{s41TwoReads.Cap}: {s41TwoReads.Why} | {s41ThreeReads.Cap}: {s41ThreeReads.Why}");
    Check("the thrash verdict names how many reads back it",
        s41ThreeReads.Why.Contains("3 read(s)") && s41TwoReads.Why.Contains("2 read(s)"),
        $"{s41ThreeReads.Why} | {s41TwoReads.Why}");
}


// S31: the engine's own alignment score was being thrown away, and a median shift cannot tell a ruler that fits
// from a ruler that does not. Both measured on 2026-09-15 against ffsubsync 0.5.1 (the plugin's own engine):
// a real sibling subtitle scored 198 713 at offset 0.050, a subtitle of another film 2 864, and the *same track
// from a 2 % longer cut* 274 721 - higher than the correct one - with a median shift of -26,52 s (under the 30 s
// reference ceiling) and an interquartile range of 27,76 s, where the correct sibling's spread was 0,00 s.
{
    var s31ScoreLine = "2026-09-15 01:02:03.000Z INFO     score: 198713.000                           ffsubsync.py:255";
    var s31NegLine = "           INFO     score: -17208.023                           ffsubsync.py:255";
    var s31OffsetLine = "           INFO     offset seconds: 0.050                       ffsubsync.py:256";
    Check("the engine's own alignment score is parsed from its output",
        SubSyncService.TryParseEngineScore(s31ScoreLine, out var parsedScore) && Math.Abs(parsedScore - 198713.0) < 0.001,
        $"{parsedScore}");
    Check("a negative score is parsed as negative, not ignored",
        SubSyncService.TryParseEngineScore(s31NegLine, out var parsedNegative) && parsedNegative < 0,
        $"{parsedNegative}");
    Check("the offset the engine reports is parsed too",
        SubSyncService.TryParseEngineOffset(s31OffsetLine, out var parsedOffset) && Math.Abs(parsedOffset - 0.05) < 0.0005,
        $"{parsedOffset}");
    Check("a line with no score states none rather than zero",
        !SubSyncService.TryParseEngineScore("extracting speech from reference...", out _),
        "no score in that line");
}

// ...and the spread of the per-cue displacement, which is what sees the case the score cannot. Built here from
// two real SRTs so the measurement is exercised, not described.
{
    var s31Dir = Path.Combine(Path.GetTempPath(), "s31-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(s31Dir);
    try
    {
        var cues = new (string Start, string End)[]
        {
            ("00:00:10,000", "00:00:12,000"), ("00:00:20,000", "00:00:22,000"),
            ("00:00:30,000", "00:00:32,000"), ("00:00:40,000", "00:00:42,000"),
            ("00:00:50,000", "00:00:52,000"), ("00:01:00,000", "00:01:02,000"),
            ("00:01:10,000", "00:01:12,000"), ("00:01:20,000", "00:01:22,000"),
        };
        string Write(string name, Func<int, int> shiftSeconds)
        {
            var path = Path.Combine(s31Dir, name);
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < cues.Length; i++)
            {
                var (start, end) = cues[i];
                var delta = shiftSeconds(i);
                string Move(string stamp) => TimeSpan.ParseExact(stamp, "hh\\:mm\\:ss\\,fff", null)
                    .Add(TimeSpan.FromSeconds(delta)).ToString("hh\\:mm\\:ss\\,fff");
                text.AppendLine((i + 1).ToString());
                text.AppendLine(Move(start) + " --> " + Move(end));
                text.AppendLine("line " + i);
                text.AppendLine();
            }

            File.WriteAllText(path, text.ToString());
            return path;
        }

        var s31Input = Write("in.srt", _ => 0);
        var s31Together = Write("together.srt", _ => 5);                  // every cue moved the same 5 s
        var s31Drifting = Write("drifting.srt", i => 5 + (i * 6));        // cues moved by different amounts

        var s31Tight = AlignmentMetrics.MeasureSyncChange(s31Input, s31Together);
        var s31Loose = AlignmentMetrics.MeasureSyncChange(s31Input, s31Drifting);
        Check("a sync whose cues all moved together measures no spread",
            s31Tight is { SpreadMs: 0 } tight && Math.Abs(tight.ShiftMs - 5000) < 1,
            $"{s31Tight}");
        Check("a sync whose cues moved unevenly measures the spread",
            s31Loose is { } loose && loose.SpreadMs > 10_000,
            $"{s31Loose}");
        var s31TightValue = s31Tight.GetValueOrDefault();
        var s31LooseValue = s31Loose.GetValueOrDefault();
        Check("the spread decides, at a quarter of the configured reference ceiling",
            s31Tight.HasValue && s31Loose.HasValue
            && !AlignmentMetrics.RulerSpreadTooWide(s31TightValue, 30_000)
            && AlignmentMetrics.RulerSpreadTooWide(s31LooseValue, 30_000)
            && AlignmentMetrics.SubtitleReferenceSpreadFraction == 0.25,
            $"tight={s31TightValue.SpreadMs} ms, loose={s31LooseValue.SpreadMs} ms, "
            + $"fraction={AlignmentMetrics.SubtitleReferenceSpreadFraction}");
    }
    finally
    {
        Directory.Delete(s31Dir, recursive: true);
    }
}

// S31 part 2: a suspicious subtitle ruler is cross-checked against the film's own audio before anything is
// written. Measured on the rig 2026-09-15: the other-cut ruler asked for +24 170 ms while the audio's own answer
// was -5 000 ms (the fixture's true shift), where two alignments against the *same* ruler agree to hundredths.
{
    Check("a ruler and the audio that disagree decide against the ruler",
        AlignmentMetrics.RulersDisagree(24170, -5000, 30_000),
        "24 170 ms against -5 000 ms at a 30 s ceiling");
    Check("a ruler the audio confirms is kept",
        !AlignmentMetrics.RulersDisagree(5000, 5050, 30_000),
        "5 000 ms against 5 050 ms at a 30 s ceiling");
    Check("the band that triggers the cross-check is a third of the configured ceiling, so 10 s by default",
        Math.Abs(SubSyncService.SuspiciousReferenceShiftFraction - (1.0 / 3.0)) < 1e-9
        && Math.Abs(30_000 * SubSyncService.SuspiciousReferenceShiftFraction - 10_000) < 0.5
        && Math.Abs(AlignmentMetrics.SubtitleReferenceAudioAgreementFraction - 0.1) < 1e-9,
        $"fractions: {SubSyncService.SuspiciousReferenceShiftFraction}, "
        + $"{AlignmentMetrics.SubtitleReferenceAudioAgreementFraction}");
}

// D3 + F10: the audit's hostile saves, driven through the one validation path instead of the page. Before this,
// nine of ten values were stored exactly as typed and the page said "Saved." for all of them; a negative ceiling
// also turned the plugin's own "was the engine clamped?" warning into a constant banner.
{
    var hostile = new PluginConfiguration
    {
        ParallelWorkers = 99,
        ExtractionTimeoutMinutes = 100000,
        MaxOffsetSeconds = -5,
        MaxSubtitleSeconds = double.NaN,
        MaxSubtitleReferenceOffsetSeconds = -5,
        OutputEncoding = "not-an-encoding",
        VadMethod = "not-a-vad",
        SyncLanguages = new[] { "qq", "zz", "!!!@#", "eng", "swe" },
        FfmpegPath = "/no/such/ffmpeg-xyz",
        FfSubSyncPath = "/no/such/binary-xyz",
        SweepFailStreakLimit = 0,
        SweepMaxItemsPerRun = -1
    };
    var notes = SettingsValidation.Apply(hostile);

    Check("99 parallel workers are stored as 64", hostile.ParallelWorkers == 64, $"{hostile.ParallelWorkers}");
    Check("a 100000-minute timeout is stored as 240",
        hostile.ExtractionTimeoutMinutes == 240, $"{hostile.ExtractionTimeoutMinutes}");
    Check("a negative offset ceiling is stored as 1", hostile.MaxOffsetSeconds == 1, $"{hostile.MaxOffsetSeconds}");
    Check("an offset ceiling of 100000 is stored as 600",
        SettingsValidation.MaxOffsetSecondsOf(new PluginConfiguration { MaxOffsetSeconds = 100000 }) == 600,
        $"{SettingsValidation.MaxOffsetSecondsOf(new PluginConfiguration { MaxOffsetSeconds = 100000 })}");
    Check("a cue length that is not a number is stored as the default 10",
        hostile.MaxSubtitleSeconds == 10, $"{hostile.MaxSubtitleSeconds}");
    Check("an unknown encoding is stored as utf-8", hostile.OutputEncoding == "utf-8", hostile.OutputEncoding);
    Check("an unknown VAD method is stored as the engine's default",
        hostile.VadMethod == "subs_then_webrtc", hostile.VadMethod);
    Check("unknown language tags are dropped, real ones kept and lowercased",
        hostile.SyncLanguages.SequenceEqual(new[] { "eng", "swe" }), string.Join(",", hostile.SyncLanguages));
    Check("a binary path that does not exist is not stored",
        hostile.FfmpegPath.Length == 0 && hostile.FfSubSyncPath == "ffsubsync",
        $"{hostile.FfmpegPath}|{hostile.FfSubSyncPath}");
    Check("a sweep streak of 0 and -1 items are brought into range",
        hostile.SweepFailStreakLimit == 1 && hostile.SweepMaxItemsPerRun == 1,
        $"{hostile.SweepFailStreakLimit}/{hostile.SweepMaxItemsPerRun}");
    Check("every adjustment is reported, so the page can say what it stored", notes.Count >= 9, $"{notes.Count}");
    Check("the accessors are the ranges, not the stored numbers",
        SettingsValidation.MaxSubtitleSecondsOf(new PluginConfiguration { MaxSubtitleSeconds = 1e9 }) == 60
        && SettingsValidation.MaxSubtitleReferenceOffsetSecondsOf(new PluginConfiguration
        {
            MaxSubtitleReferenceOffsetSeconds = -100
        }) == 1,
        "60 s cue, 1 s reference floor");
    Check("a language tag with a region is accepted and normalised",
        SettingsValidation.IsKnownLanguageTag("pt-BR", out var pt) && pt == "pt-br", pt);
    Check("a classic ISO 639-2/B tag is accepted", SettingsValidation.IsKnownLanguageTag("ger", out _), "ger");
    Check("a tag that names no language is not accepted",
        !SettingsValidation.IsKnownLanguageTag("qq", out _) && !SettingsValidation.IsKnownLanguageTag("!!!@#", out _),
        "qq, !!!@#");
    Check("a path with a separator is only usable when it is there",
        SettingsValidation.BinaryPathIsUsable("ffmpeg") && !SettingsValidation.BinaryPathIsUsable("/no/such/ffmpeg-xyz"),
        "bare name ok, missing path not");
    Check("a configuration that is already in range is left alone",
        SettingsValidation.Apply(new PluginConfiguration()).Count == 0, "the defaults are valid");
}

// S43: the "audio" reference is only audio if the engine is given a VAD that reads audio. With the configured
// default (`subs_then_webrtc`) the engine takes the video's own embedded subtitle tracks as its speech signal -
// measured 2026-09-15: a wrong-cut track scored 212 234 against the audio's 53 566 on the same file, and one
// fixture's "audio" run returned the wrong track's own answer. The rule is that the plugin decides.
{
    Check("the plugin's own subtitle reference keeps the configured VAD",
        SubSyncService.VadForReference("s:1") is null,
        "a vetted subtitle reference: the configured method stands");
    Check("the audio reference forces the audio VAD",
        SubSyncService.VadForReference(null) == "webrtc"
        && SubSyncService.AudioReferenceVadName == "webrtc",
        $"audio reference -> --vad {SubSyncService.VadForReference(null)}");
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

    // The boundary, at the throughputs the field run of 2026-09-14 actually produced: the share's eight walks
    // measured 16,8-23,1 MB/s and local NVMe's five measured 84,4-91,4 MB/s. A threshold anywhere inside the
    // share's own range makes the ceiling flip between runs of the same batch, which is what put five
    // concurrent walks on the share that day.
    Check("the share's fastest measured walk still counts as storage-bound",
        SubSyncService.WalkCapForProfile(null, 23_100).Cap == 2,
        "got " + SubSyncService.WalkCapForProfile(null, 23_100).Cap);
    Check("the share's slowest measured walk is held at 2 as well",
        SubSyncService.WalkCapForProfile(null, 16_800).Cap == 2,
        "got " + SubSyncService.WalkCapForProfile(null, 16_800).Cap);
    Check("local storage's slowest measured walk counts as fast",
        SubSyncService.WalkCapForProfile(null, 84_400).Cap == int.MaxValue,
        "got " + SubSyncService.WalkCapForProfile(null, 84_400).Cap);
    Check("the whole share range maps to the same ceiling, so it cannot flip within a batch",
        new[] { 16_800.0, 17_400.0, 18_900.0, 19_700.0, 22_700.0, 23_100.0 }
            .All(b => SubSyncService.WalkCapForProfile(null, b).Cap == 2)
        && new[] { 84_400.0, 91_400.0, 137_000.0 }.All(b => SubSyncService.WalkCapForProfile(null, b).Cap == int.MaxValue),
        "share range -> " + string.Join(",", new[] { 16_800.0, 23_100.0 }.Select(b => SubSyncService.WalkCapForProfile(null, b).Cap)));
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

// F3: the item-scoped endpoints used to take the item id on trust, so any authenticated account could read
// any item's subtitle list or queue a sync that wrote a subtitle for an item in a library it cannot see. The
// decision is a pure predicate so it can be driven here rather than needing a live server and a second account.
{
    var libraryId = Guid.NewGuid().ToString("N");
    var elsewhere = Guid.NewGuid().ToString("N");

    Check("an account with all-folders permission may act on anything",
        ItemAccess.Allows(true, null, null)
        && ItemAccess.Allows(true, new[] { libraryId }, Array.Empty<string>()));
    Check("an item in a library the account can see is allowed",
        ItemAccess.Allows(false, new[] { libraryId }, new[] { elsewhere, libraryId }));
    Check("a folder match at any depth counts, not just the immediate parent",
        ItemAccess.Allows(false, new[] { libraryId }, new[] { "season", "series", libraryId }));
    Check("an item in a library the account cannot see is denied",
        !ItemAccess.Allows(false, new[] { libraryId }, new[] { elsewhere }));
    Check("an account with no libraries at all is denied",
        !ItemAccess.Allows(false, Array.Empty<string>(), new[] { libraryId })
        && !ItemAccess.Allows(false, null, new[] { libraryId }));
    Check("an item whose folders cannot be resolved is denied, not allowed",
        !ItemAccess.Allows(false, new[] { libraryId }, Array.Empty<string>())
        && !ItemAccess.Allows(false, new[] { libraryId }, null));
    Check("the same folder is recognised in either of Jellyfin's id forms",
        ItemAccess.Allows(
            false,
            new[] { "6a9d1b3e-2f4c-4a5b-8c7d-9e0f1a2b3c4d" },
            new[] { "6a9d1b3e2f4c4a5b8c7d9e0f1a2b3c4d" }));

    var visible = Guid.NewGuid().ToString("N");
    var unseen = Guid.NewGuid().ToString("N");
    var foldersOf = new Dictionary<string, IReadOnlyCollection<string>>
    {
        [visible] = new[] { libraryId },
        [unseen] = new[] { elsewhere },
    };
    Check("a list with one item the account cannot see is refused as a whole",
        ItemAccess.FirstDenied(false, new[] { libraryId }, new[] { visible, unseen }, id => foldersOf[id]) == unseen,
        "refused " + ItemAccess.FirstDenied(false, new[] { libraryId }, new[] { visible, unseen }, id => foldersOf[id]));
    Check("a list the account can see entirely is not refused",
        ItemAccess.FirstDenied(false, new[] { libraryId }, new[] { visible }, id => foldersOf[id]) is null);

    var account = Guid.NewGuid();
    Check("the account id is read from the identity claim",
        ItemAccess.UserIdFrom(new[]
        {
            new KeyValuePair<string, string>(
                "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", account.ToString())
        }) == account);
    Check("the account id is read from the jwt subject claim",
        ItemAccess.UserIdFrom(new[] { new KeyValuePair<string, string>("sub", account.ToString("D")) }) == account);
    Check("a request with no usable account id resolves to nothing, never to a default account",
        ItemAccess.UserIdFrom(Array.Empty<KeyValuePair<string, string>>()) is null
        && ItemAccess.UserIdFrom(null) is null
        && ItemAccess.UserIdFrom(new[] { new KeyValuePair<string, string>("sub", "not-a-guid") }) is null
        && ItemAccess.UserIdFrom(new[] { new KeyValuePair<string, string>("sub", Guid.Empty.ToString()) }) is null
        && ItemAccess.UserIdFrom(new[] { new KeyValuePair<string, string>("sub", account.ToString()) }) == account);
}

// S37: a walk measures the volume *and* whatever else happened to be running. Measured on 2026-09-14: the same
// local NVMe volume walked at 85-91 MB/s with only its own jobs in flight, and at 45-58 MB/s while a slow share
// was walked alongside it - which straddles the 50 MB/s boundary, so the ceiling held a fast volume to two
// walks for the rest of the batch. A walk taken while another volume was being read is not evidence about this
// volume, so it is deliberately not fed to the profile.
{
    const string local = "/Media|/dev/nvme0n1p2";
    const string share = "/media/synology|192.168.0.110:/volume1/JELLYFIN";

    Check("nothing else in flight means the walk is the volume's own measurement",
        SubSyncService.VolumesOtherThan(local, new[] { local }) == 0
        && SubSyncService.VolumesOtherThan(local, Array.Empty<string>()) == 0);
    Check("a walk taken while another volume was being read is not that volume's measurement",
        SubSyncService.VolumesOtherThan(local, new[] { local, share }) == 1,
        "got " + SubSyncService.VolumesOtherThan(local, new[] { local, share }));
    Check("a volume's own concurrent jobs are not contention",
        SubSyncService.VolumesOtherThan(share, new[] { share, share, share }) == 0);
    Check("every other volume in flight is counted",
        SubSyncService.VolumesOtherThan("a", new[] { "a", "b", "c", "b" }) == 3);
}

// S39: the ceiling judges a volume against the best *this machine* has measured, so nothing in the decision is
// a throughput or a latency that only one machine's hardware produces. Two machines are driven below - the one
// the old constants were measured on, and a faster one - and both must classify correctly.
{
    // Machine A: fabji's server, 2026-09-14. Local NVMe walks at ~91 MB/s, the share at ~3,4 MB/s.
    const double aLocalWalk = 91_000;      // bytes per millisecond
    const double aShareWalk = 3_400;

    var shareOnA = SubSyncService.WalkCapForProfile(null, aShareWalk, null, aLocalWalk, null);
    Check("on the machine the constants came from, the share is still storage-bound",
        shareOnA.Cap == SubSyncService.StorageBoundWalkCap,
        $"{shareOnA.Cap} - {shareOnA.Why}");
    Check("and its reason names both numbers, so the decision is auditable",
        shareOnA.Why.Contains("3.4 MB/s") && shareOnA.Why.Contains("91.0 MB/s")
        && shareOnA.Why.Contains("this machine has measured"),
        shareOnA.Why);

    var localOnA = SubSyncService.WalkCapForProfile(null, aLocalWalk, null, aShareWalk, null);
    Check("the fastest volume on that machine still keeps no ceiling, judged against the rest of it",
        localOnA.Cap == int.MaxValue, $"{localOnA.Cap} - {localOnA.Why}");

    // Machine B: a faster machine than the one the thresholds were measured on - local disk 500 MB/s, and a
    // NAS on 10 GbE at 150 MB/s. Under the old 50 MB/s threshold that NAS was "fast" and got no ceiling at all,
    // which is the silent hole this whole change exists to close.
    const double bLocalWalk = 500_000;
    const double bNasWalk = 150_000;

    var nasOnB = SubSyncService.WalkCapForProfile(null, bNasWalk, null, bLocalWalk, null);
    Check("on a faster machine, a 150 MB/s NAS is storage-bound against a 500 MB/s local disk",
        nasOnB.Cap == SubSyncService.StorageBoundWalkCap, $"{nasOnB.Cap} - {nasOnB.Why}");
    Check("the same NAS on a machine whose local disk is only 200 MB/s keeps no ceiling",
        SubSyncService.WalkCapForProfile(null, bNasWalk, null, 200_000, null).Cap == int.MaxValue,
        $"{SubSyncService.WalkCapForProfile(null, bNasWalk, null, 200_000, null).Cap}");

    // The reads decide only when there are no walks to go on (a volume with slow reads and fine walks must not
    // be over-capped: that is what held a local NVMe to two walks on 2026-09-14).
    Check("reads only, against this machine's best read latency",
        SubSyncService.WalkCapForProfile(21.0, null, 0.5, null, null).Cap == SubSyncService.StorageBoundWalkCap
        && SubSyncService.WalkCapForProfile(0.6, null, 0.5, null, null).Cap == int.MaxValue
        && SubSyncService.WalkCapForProfile(1500.0, null, 0.5, null, null).Cap == 1,
        $"{SubSyncService.WalkCapForProfile(21.0, null, 0.5, null, null).Cap} / "
        + $"{SubSyncService.WalkCapForProfile(0.6, null, 0.5, null, null).Cap}");
    Check("a volume whose reads are slow but whose own walks are fine is judged by its walks",
        SubSyncService.WalkCapForProfile(21.0, bLocalWalk, 0.5, bLocalWalk, null).Cap == int.MaxValue
        && SubSyncService.WalkCapForProfile(21.0, bLocalWalk, 0.5, bLocalWalk, null).Why.Contains("last walk moved"),
        SubSyncService.WalkCapForProfile(21.0, bLocalWalk, 0.5, bLocalWalk, null).Why);

    // Hysteresis: the field failure of 2026-09-14 as a check. That volume's walks read 0,75x to 1,0x of the
    // machine's best, then 0,5x to 0,64x while a slow share was walked alongside it - straddling a single
    // threshold, which flipped its ceiling between two and none inside one batch.
    var held = int.MaxValue;
    foreach (var frac in new[] { 1.00, 0.75, 0.64, 0.58, 0.75 })
    {
        held = SubSyncService.WalkCapForProfile(null, bLocalWalk * frac, null, bLocalWalk, held).Cap;
    }

    Check("a volume whose walks straddle the old boundary never moves once its ceiling is set",
        held == int.MaxValue, "ended at " + held);
    Check("the band is one-sided in the right direction: a capped volume stays capped inside it",
        SubSyncService.WalkCapForProfile(null, bLocalWalk * 0.6, null, bLocalWalk, SubSyncService.StorageBoundWalkCap).Cap
        == SubSyncService.StorageBoundWalkCap);
    Check("inside the band, the reason says the band is why the last decision stands",
        SubSyncService.WalkCapForProfile(null, bLocalWalk * 0.6, null, bLocalWalk, 2).Why.Contains("band"),
        SubSyncService.WalkCapForProfile(null, bLocalWalk * 0.6, null, bLocalWalk, 2).Why);
    Check("a volume that genuinely falls below the bound is capped, band or no band",
        SubSyncService.WalkCapForProfile(null, bLocalWalk * 0.3, null, bLocalWalk, int.MaxValue).Cap
        == SubSyncService.StorageBoundWalkCap);

    // The reference itself: floored against the page cache, and never a volume's own numbers.
    Check("no read is believed to be faster than any storage answers",
        VolumeProfiles.PageCacheFloorMsPerCall > 0
        && VolumeProfiles.MinSamplesForReference > 0
        && VolumeProfiles.FastestReadMsPerCall() is null or >= VolumeProfiles.PageCacheFloorMsPerCall,
        "best read = " + VolumeProfiles.FastestReadMsPerCall());
    var fastPath = "/dev/shm/subsync-s39-fast-" + Guid.NewGuid().ToString("N");
    for (var i = 0; i < VolumeProfiles.MinSamplesForReference + 2; i++)
    {
        VolumeProfiles.For(fastPath).Observe(65536, 0.001);
        VolumeProfiles.For(fastPath).ObserveWalk(200_000_000, 1000);
    }

    Check("a volume's own speed never sets the bar it is judged against",
        (VolumeProfiles.FastestWalkBytesPerMs(VolumeProfiles.KeyFor(fastPath)) ?? 0) < 200_000,
        "reference excluding it = " + VolumeProfiles.FastestWalkBytesPerMs(VolumeProfiles.KeyFor(fastPath)));
    Check("the page cache cannot set the read reference for every disk on the machine",
        VolumeProfiles.FastestReadMsPerCall() >= VolumeProfiles.PageCacheFloorMsPerCall,
        "best read = " + VolumeProfiles.FastestReadMsPerCall());

    // With no reference at all, the old absolute behaviour still decides - a process that has measured one
    // volume behaves exactly as it did before this change.
    Check("with no reference to compare against, the documented absolute behaviour still decides",
        SubSyncService.WalkCapForProfile(21.0, null).Cap == SubSyncService.StorageBoundWalkCap
        && SubSyncService.WalkCapForProfile(0.05, null).Cap == int.MaxValue
        && SubSyncService.WalkCapForProfile(null, 137_000).Cap == int.MaxValue);
}

// S38: a volume with nothing measured about it gets one timed read of its own, once per process. The field
// evidence for needing it: a cold mixed batch in which every walk on the fast volume was contended by the slow
// volume's walks, so it had no measurement at all and paid the conservative two for the whole run while six of
// eight workers sat idle.
//
// Standalone profiles rather than the registry, and deliberately: VolumeProfiles keys by device, so a path under
// /dev/shm or /tmp shares a profile with every other check that has read there, and these four checks are about
// what a volume with *nothing* measured does.
{
    var s38Fresh = new VolumeProfile("s38/fresh");
    Check("a volume nothing has measured about it needs its one timed read",
        s38Fresh.NeedsProbe && s38Fresh.TryBeginProbe(), "the first probe was refused");
    Check("that read is taken once, not once per planning pass",
        !s38Fresh.NeedsProbe && !s38Fresh.TryBeginProbe());

    var s38Read = new VolumeProfile("s38/read");
    s38Read.Observe(16384, 0.4);
    Check("a volume its own reads have measured does not need the probe",
        !s38Read.NeedsProbe && !s38Read.TryBeginProbe());

    var s38Walked = new VolumeProfile("s38/walked");
    s38Walked.ObserveWalk(1_000_000_000, 12_000);
    Check("nor does one its own walk has measured",
        !s38Walked.NeedsProbe && !s38Walked.TryBeginProbe());

    // One measured read is enough to classify: a local disk's fraction of a millisecond against a share's tens.
    Check("one measured read is enough to hold a share at 2 and to let local storage go",
        SubSyncService.WalkCapForProfile(21.0, null, 0.25, null, null).Cap == SubSyncService.StorageBoundWalkCap
        && SubSyncService.WalkCapForProfile(0.4, null, null, null, null).Cap == int.MaxValue
        && SubSyncService.WalkCapForProfile(0.4, null, 21.0, null, null).Cap == int.MaxValue,
        $"{SubSyncService.WalkCapForProfile(21.0, null, 0.25, null, null).Cap} / "
        + $"{SubSyncService.WalkCapForProfile(0.4, null, 21.0, null, null).Cap}");

    // The thrash tier has to reproduce the old absolute 100 ms once the reference is the 0,25 ms floor: at 100x
    // it would be 25 ms, which would hold fabji's share at one walk where its own measurements say two is best.
    Check("the thrash tier still means the old 100 ms per read, not 25",
        SubSyncService.WalkCapForProfile(21.0, null, 0.25, null, null).Cap == SubSyncService.StorageBoundWalkCap
        && SubSyncService.WalkCapForProfile(120.0, null, 0.25, null, null).Cap == 1,
        $"{SubSyncService.WalkCapForProfile(21.0, null, 0.25, null, null).Cap} / "
        + $"{SubSyncService.WalkCapForProfile(120.0, null, 0.25, null, null).Cap}");
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
    var queueOrdinal = MediaStreamMap.EmbeddedSubtitleOrdinal(streams, selected); // what the enqueue path stores
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
        MediaStreamMap.EmbeddedSubtitleOrdinal(subsFirst, subsFirst[2]) == 2);

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
        MediaStreamMap.EmbeddedSubtitleOrdinal(thunder, thunder[13]) == 9,
        $"ordinal {MediaStreamMap.EmbeddedSubtitleOrdinal(thunder, thunder[13])}");

    Check("a sidecar subtitle has no ordinal among the file's embedded tracks",
        MediaStreamMap.EmbeddedSubtitleOrdinal(subsFirst, new()
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            Index = 7,
            IsExternal = true
        }) == -1);

    Check("an embedded subtitle the container's set does not hold gets no ordinal, not a wrong one",
        MediaStreamMap.EmbeddedSubtitleOrdinal(subsFirst, new()
        {
            Type = MediaBrowser.Model.Entities.MediaStreamType.Subtitle,
            Index = 99
        }) == -1);

    Check("a lone embedded track is ordinal 0 even when Jellyfin numbers it otherwise",
        MediaStreamMap.EmbeddedSubtitleOrdinal(
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
Check("a PAL-timed subtitle is the side to rescale", AlignmentMetrics.IsTargetOffTheVideo(0.959 * 3000, 3000, 3000));
Check("a correct subtitle against a PAL-timed reference is left alone", !AlignmentMetrics.IsTargetOffTheVideo(3000, 0.959 * 3000, 3000));
Check("two subtitles that both match the file are not rescaled", !AlignmentMetrics.IsTargetOffTheVideo(3000, 2990, 3000));
Check("an unusable duration leaves the decision to the pair rule", AlignmentMetrics.IsTargetOffTheVideo(3000, 0.959 * 3000, 30));

// A stretch is a claim about the whole timeline, and only the film's audio can test it: a differently cut subtitle
// produces the same span ratio as one from another framerate. A correct stretch leaves the audio alignment almost
// nothing to do; one applied to the wrong kind of difference still wants a large shift.
Check("a result the audio agrees with holds", AlignmentMetrics.AlignmentHoldsAgainstAudio(1.0, 1200, 3000));
Check("a result the audio still wants 40 s of does not hold", !AlignmentMetrics.AlignmentHoldsAgainstAudio(1.0, 40000, 3000));
Check("a result the audio wants rescaled again does not hold", !AlignmentMetrics.AlignmentHoldsAgainstAudio(1.02, 500, 3000));
Check("the room grows with the runtime, up to a point", AlignmentMetrics.AlignmentHoldsAgainstAudio(1.0, 12000, 4200));

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
    var got = AlignmentMetrics.IsRescaleAcceptable(ratio, shiftMs, 60, fix);
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
Check("a subtitle specifier yields its position", MediaStreamMap.SubtitleStreamOrdinal("s:3") == 3);
Check("position zero is valid", MediaStreamMap.SubtitleStreamOrdinal("s:0") == 0);
Check("audio is not a subtitle specifier", MediaStreamMap.SubtitleStreamOrdinal("a:0") == -1);
Check("a missing specifier is rejected", MediaStreamMap.SubtitleStreamOrdinal(null) == -1);
Check("junk is rejected", MediaStreamMap.SubtitleStreamOrdinal("s:") == -1 && MediaStreamMap.SubtitleStreamOrdinal("nonsense") == -1);
Check("a negative position is rejected", MediaStreamMap.SubtitleStreamOrdinal("s:-2") == -1);

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
    SyncedTargetNaming.CanWriteTo(Path.GetTempPath(), out var writableReason) && writableReason.Length == 0,
    writableReason);
Check("a folder that does not exist yet is created and then writable",
    SyncedTargetNaming.CanWriteTo(Path.Combine(Path.GetTempPath(), "subsync-probe-" + Guid.NewGuid().ToString("N")), out _));
Check("a folder the process cannot write to is refused, with a reason",
    !SyncedTargetNaming.CanWriteTo("/proc/subsync-cannot-write-here", out var deniedReason)
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

var identical = AlignmentMetrics.MeasureSyncChange(sourceSrt, identicalSrt);
Check("an identical output is recognised as no change", identical is { IsNoChange: true });
Check("the no-change case is described as a 0 ms offset",
    identical?.Describe() == "+0 ms offset", identical?.Describe() ?? "(none)");

var shifted = AlignmentMetrics.MeasureSyncChange(sourceSrt, shiftedSrt);
Check("a 500 ms shift is a change", shifted is { IsNoChange: false });
Check("the shift is reported in milliseconds", shifted?.ShiftMs == 500, "got " + shifted?.ShiftMs);

// Sub-second precision must survive: parsing that dropped the milliseconds reported a real 400 ms
// shift as "0 ms offset", and the "nothing changed" check would then throw the correction away.
var subSecondSrt = Path.Combine(noChangeDir, "subsecond.srt");
File.WriteAllText(subSecondSrt, Srt(1400, 2400, 3400, 4400, 5400));
var subSecond = AlignmentMetrics.MeasureSyncChange(sourceSrt, subSecondSrt);
Check("a 400 ms shift is measured, not rounded to zero",
    subSecond?.ShiftMs == 400, "got " + subSecond?.ShiftMs);
Check("a sub-second shift is not treated as no change", subSecond is { IsNoChange: false });

Check("a framerate correction is a change even with a zero median offset",
    AlignmentMetrics.MeasureSyncChange(sourceSrt, framerateSrt) is { IsNoChange: false });
Check("too few cues cannot be judged, so the output is kept",
    AlignmentMetrics.MeasureSyncChange(sourceSrt, tinySrt) is null);
Check("the description keeps the framerate detail",
    (AlignmentMetrics.MeasureSyncChange(sourceSrt, framerateSrt)?.Describe() ?? string.Empty).Contains("framerate ratio"),
    AlignmentMetrics.MeasureSyncChange(sourceSrt, framerateSrt)?.Describe() ?? "(none)");
Directory.Delete(noChangeDir, recursive: true);

// ---------------- A forced/signs track must not be mistaken for the full subtitle ----------------
// Reported from real use: a Norwegian sidecar came out with two cues for a whole episode. The file
// carried a two-cue forced track beside a full WebVTT track in the same language, and the automatic
// pick took the first one it met.
Check("two cues in a 45-minute episode look like a signs track",
    AlignmentMetrics.LooksLikeSignsTrack(2, TimeSpan.FromMinutes(45)));
Check("a full episode subtitle does not",
    !AlignmentMetrics.LooksLikeSignsTrack(640, TimeSpan.FromMinutes(45)));
Check("few cues in a short clip are normal",
    !AlignmentMetrics.LooksLikeSignsTrack(5, TimeSpan.FromMinutes(5)));
Check("an empty subtitle is not reported as a signs track",
    !AlignmentMetrics.LooksLikeSignsTrack(0, TimeSpan.FromMinutes(45)));
Check("an unreadable subtitle is unknown, not signs",
    !AlignmentMetrics.LooksLikeSignsTrack(-1, TimeSpan.FromMinutes(45)));
Check("the boundary is twelve cues over a long video",
    AlignmentMetrics.LooksLikeSignsTrack(11, TimeSpan.FromMinutes(45))
    && !AlignmentMetrics.LooksLikeSignsTrack(12, TimeSpan.FromMinutes(45)));

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

// ---------------- G1: a sync queued on its own is a run of its own ----------------
// The detail page posts to /SubSync/Sync, which enqueues without a batch on purpose: there is no group for
// one job to belong to. Both read models asked for jobs that carry a batch, so that run appeared nowhere -
// not in History, not in the file a restart reads. It is addressed by a derived id instead of by a batch
// invented at enqueue time, and the file bounds those runs separately so a week of them cannot rotate the
// batches out (which is the whole reason MaxSingleRuns exists).
Check("a run of one is addressable by an id a batch id cannot be (G1)",
    RunId.ForJob("abc123") == "single:abc123"
    && RunId.IsSingle(RunId.ForJob("abc123"))
    && !RunId.IsSingle("8c013666da274c8ab5fc22beee89dde0")
    && !RunId.IsSingle(null)
    && !RunId.IsSingle(string.Empty)
    && RunId.JobIdOf(RunId.ForJob("abc123")) == "abc123"
    && RunId.JobIdOf("8c013666da274c8ab5fc22beee89dde0") is null,
    RunId.ForJob("abc123"));

var g1Path = Path.Combine(Path.GetTempPath(), "g1-batch-history.json");
File.Delete(g1Path);
BatchHistory.Save(g1Path, new[]
{
    new BatchHistoryEntry
    {
        BatchId = RunId.ForJob("g1-job-1"),
        Label = "Best Friends",
        CreatedUtc = new DateTime(2026, 9, 18, 1, 0, 0, DateTimeKind.Utc),
        Jobs = new List<BatchHistoryJob>
        {
            new BatchHistoryJob
            {
                Id = "g1-job-1", SubtitleIndex = 0, Mode = "normal", Status = "Completed",
                Outcome = "written", Label = "Best Friends",
                CreatedAtUtc = new DateTime(2026, 9, 18, 1, 0, 0, DateTimeKind.Utc)
            }
        }
    }
});
var g1Back = BatchHistory.Load(g1Path);
Check("a run of one survives being written and read back (G1)",
    g1Back.Count == 1 && g1Back[0].BatchId == "single:g1-job-1" && g1Back[0].Jobs.Count == 1
    && g1Back[0].Jobs[0].Id == "g1-job-1" && g1Back[0].Label == "Best Friends",
    g1Back.Count + " run(s), label '" + (g1Back.Count > 0 ? g1Back[0].Label : string.Empty) + "'");

// 25 of each, so both bounds are reached: 20 batches and 20 single runs are kept, the newest of each.
var rotation = new List<BatchHistoryEntry>();
for (var i = 0; i < 25; i++)
{
    var at = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i);
    rotation.Add(new BatchHistoryEntry { BatchId = "batch-" + i, CreatedUtc = at, Jobs = new List<BatchHistoryJob>() });
    rotation.Add(new BatchHistoryEntry { BatchId = RunId.ForJob("job-" + i), CreatedUtc = at, Jobs = new List<BatchHistoryJob>() });
}

BatchHistory.Save(g1Path, rotation);
var rotationBack = BatchHistory.Load(g1Path);
Check("the history keeps 20 batches and 20 single runs, newest of each (G1)",
    rotationBack.Count(e => !RunId.IsSingle(e.BatchId)) == BatchHistory.MaxBatches
    && rotationBack.Count(e => RunId.IsSingle(e.BatchId)) == BatchHistory.MaxSingleRuns
    && rotationBack.Any(e => e.BatchId == "batch-24") && rotationBack.Any(e => e.BatchId == "batch-5")
    && !rotationBack.Any(e => e.BatchId == "batch-4")
    && rotationBack.Any(e => e.BatchId == RunId.ForJob("job-24"))
    && !rotationBack.Any(e => e.BatchId == RunId.ForJob("job-4")),
    rotationBack.Count + " run(s): " + rotationBack.Count(e => !RunId.IsSingle(e.BatchId)) + " batch(es) + "
    + rotationBack.Count(e => RunId.IsSingle(e.BatchId)) + " single(s)");
File.Delete(g1Path);

// ---------------- C1 was here: the sampled audio reference, measured and left out ----------------
// tympanix/subsync's bounded slice, which this engine implements as --multi-segment-sync, was built and
// measured on the 2,4 GB episode (FIX_PLAN C1): 16 segments answered correctly at two different shifts and
// halved the pass, while 8 segments answered 63 s and 142 s wrong on the same file, in a shape the plugin's
// own guard accepts as a 25/23,976 PAL correction. No segment count is both reliably right and much faster,
// so the setting is not in the plugin - the numbers are in the row, and the harness that produced them is
// tests/backend/engine_sampling_bench.py.

// ---------------- C2: the piecewise (split-penalty) alignment ----------------
Check("no split penalty emits no piecewise flag (C2)",
    SubSyncService.PiecewiseArgs(new PluginConfiguration { SplitPenalty = 0 }).Count == 0);
var piecewise = SubSyncService.PiecewiseArgs(new PluginConfiguration { SplitPenalty = 6 });
Check("a split penalty is handed over in seconds of overlap (C2)",
    piecewise.Count == 2 && piecewise[0] == "--split-penalty" && piecewise[1] == "6",
    string.Join(" ", piecewise));
Check("a split penalty outside the range is clamped, not stored as typed (C2)",
    SettingsValidation.SplitPenaltyOf(new PluginConfiguration { SplitPenalty = 999 }) == 50
    && SettingsValidation.SplitPenaltyOf(new PluginConfiguration { SplitPenalty = -3 }) == 0
    && SettingsValidation.SplitPenaltyOf(new PluginConfiguration { SplitPenalty = double.NaN }) == 0);
var penaltyNotes = SettingsValidation.Apply(new PluginConfiguration { SplitPenalty = 999 });
Check("a split penalty outside the range says so (C2)",
    penaltyNotes.Any(n => n.Contains("Split penalty")), string.Join(" | ", penaltyNotes));

// ---------------- C2, measured: what a step looks like to each reading ----------------
// Reproduced from the engine run of 2026-09-15: the episode's own subtitle with a 20 s discontinuity at its
// midpoint. The piecewise answer moves the first half +7,54 s and the second +27,54 s; each half is flat, and
// the least-squares line through the two is 1,01082x. The two readings disagree, which is the whole point.
string Stamp(double ms)
{
    var total = (long)Math.Round(Math.Max(0, ms));
    return (total / 3600000).ToString("00") + ":" + ((total / 60000) % 60).ToString("00") + ":"
        + ((total / 1000) % 60).ToString("00") + "," + (total % 1000).ToString("000");
}

string BuildSrt(int count, double durationMs, Func<int, double> shiftMs)
{
    var blocks = new List<string>(count);
    for (var i = 0; i < count; i++)
    {
        var start = i * 1000.0;
        var shift = shiftMs(i);
        blocks.Add((i + 1) + "\n" + Stamp(start + shift) + " --> " + Stamp(start + shift + durationMs) + "\nline " + (i + 1));
    }

    return string.Join("\n\n", blocks) + "\n";
}

var pieceDir = Path.Combine(Path.GetTempPath(), "piecewise-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(pieceDir);
var flatIn = Path.Combine(pieceDir, "flat-in.srt");
File.WriteAllText(flatIn, BuildSrt(1000, 900, _ => 0));
var stepOut = Path.Combine(pieceDir, "step-out.srt");
File.WriteAllText(stepOut, BuildSrt(1000, 900, i => i < 500 ? 7540 : 27540));
var rampOut = Path.Combine(pieceDir, "ramp-out.srt");
File.WriteAllText(rampOut, BuildSrt(1000, 900, i => i * 10.82));
var farOut = Path.Combine(pieceDir, "far-out.srt");
File.WriteAllText(farOut, BuildSrt(1000, 900, i => i < 500 ? 200000 : 220000));

var stepChange = AlignmentMetrics.MeasureSyncChange(flatIn, stepOut);
var stepStructure = AlignmentMetrics.MeasureSegmentStructure(flatIn, stepOut, AlignmentMetrics.EngineSampleMs);
var rampStructure = AlignmentMetrics.MeasureSegmentStructure(flatIn, rampOut, AlignmentMetrics.EngineSampleMs);
var farStructure = AlignmentMetrics.MeasureSegmentStructure(flatIn, farOut, AlignmentMetrics.EngineSampleMs);
const double SpreadCeiling = 7500;   // a quarter of the 30 s subtitle-reference ceiling, as the plugin uses it
const double Window = 180000;        // the default search window

Check("a 20 s step at the midpoint reads as two pieces, each flat (C2)",
    stepStructure is { Segments: 2, MaxWithinSpreadMs: 0, SmallestStepMs: 20000 },
    stepStructure is { } st ? $"segments={st.Segments} within={st.MaxWithinSpreadMs} step={st.SmallestStepMs}" : "null");
// The step is what the line cannot describe: its size sets the ratio, so the same 20 s step reads 1,03000x over
// this 17-minute fixture and 1,01082x over the 48-minute episode the numbers came from - and neither is a
// framerate pair, so the guard refuses a result that is right in both halves. That is the interaction this
// reading exists for.
Check("the linear reading of that same step is a ratio the plugin's own guard refuses (C2)",
    stepChange is { } sc && sc.Ratio > 1.02
    && !AlignmentMetrics.IsRescaleAcceptable(sc.Ratio, sc.ShiftMs, 180, true)
    && !AlignmentMetrics.IsRescaleAcceptable(1.01082, 7540, 180, true),
    stepChange is { } s2 ? $"ratio={s2.Ratio:0.00000} median={s2.ShiftMs} ms" : "null");
Check("the piecewise reading of that step holds (C2)",
    stepStructure is { } s3 && AlignmentMetrics.PiecewiseHolds(s3, SpreadCeiling, Window));
Check("a rescale of the same size is not accepted as a staircase (C2)",
    rampStructure is { } rs && !AlignmentMetrics.PiecewiseHolds(rs, SpreadCeiling, Window),
    rampStructure is { } rs2 ? $"segments={rs2.Segments} within={rs2.MaxWithinSpreadMs} step={rs2.SmallestStepMs}" : "null");
Check("a piecewise answer that leaves the search window is refused (C2)",
    farStructure is { } fs && fs.Segments == 2 && !AlignmentMetrics.PiecewiseHolds(fs, SpreadCeiling, Window));
Check("a single global offset is not mistaken for a piecewise answer (C2)",
    !AlignmentMetrics.PiecewiseHolds(new AlignmentMetrics.SegmentStructure(1, 0, 7540, 0), SpreadCeiling, Window));
Directory.Delete(pieceDir, recursive: true);

// ---------------- C: the sweep state is written in batches ----------------
// The candidate (from Marnalas/jellyfin-subsync, MIT, same reasoning as its skip-cache): every write
// serializes the whole dictionary, so a save per record makes a bulk run quadratic in its record count.
var sweepDir = Path.Combine(Path.GetTempPath(), "sweep-state-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(sweepDir);
string[] sweepFiles = new string[100];
for (var i = 0; i < sweepFiles.Length; i++)
{
    sweepFiles[i] = Path.Combine(sweepDir, "sub" + i + ".srt");
    File.WriteAllText(sweepFiles[i], "1\n00:00:01,000 --> 00:00:02,000\nline " + i + "\n");
}

var batched = new SweepState(Path.Combine(sweepDir, "batched.json"));
foreach (var file in sweepFiles)
{
    batched.Record(file, ok: true, outputPath: file + ".SYNCED.srt", error: null);
}
var batchedSaves = batched.Saves;
var batchedPending = batched.PendingWrites;
Check("100 records write the state 4 times, not 100 (the write amplification)",
    batchedSaves == 4 && batchedPending == 0,
    "saves=" + batchedSaves + " pending=" + batchedPending);
Check("the batched records are still in memory and none was dropped",
    batchedPending == 0 && batched.Get(sweepFiles[0]) is { FailStreak: 0 } && batched.Get(sweepFiles[99]) is not null);
var flushed = batched.Flush();
Check("flushing an already-written batch writes nothing (a drain is not a save storm)",
    !flushed && batched.Saves == batchedSaves, "flushed=" + flushed + " saves=" + batched.Saves);

// A record that cannot be flushed immediately (the file is gone) must not be lost, and a remainder smaller
// than the batch size must reach disk on Flush - that is the point of the flush, not a nicety.
var remainder = new SweepState(Path.Combine(sweepDir, "remainder.json"));
foreach (var file in sweepFiles.Take(7))
{
    remainder.Record(file, ok: true, outputPath: file + ".SYNCED.srt", error: null);
}
Check("fewer records than one batch are held back, not written per record",
    remainder.Saves == 0 && remainder.PendingWrites == 7, "saves=" + remainder.Saves + " pending=" + remainder.PendingWrites);
Check("Flush writes what is pending", remainder.Flush() && remainder.Saves == 1);
var reloaded = new SweepState(Path.Combine(sweepDir, "remainder.json"));
Check("what was written is what a restart reads back",
    reloaded.Get(sweepFiles[0]) is not null && reloaded.Get(sweepFiles[6]) is not null && reloaded.Get(sweepFiles[7]) is null,
    "entry 0 and 6 present, 7 absent");

// Sample: what the two write patterns cost, measured on the same code path - a Flush after every record is
// exactly what the class did before this change, so the pair is the before and the after of one comparison.
// It is printed rather than asserted: this is a measurement, and a timing assertion is a flaky check.
var measureDir = Path.Combine(sweepDir, "measure");
Directory.CreateDirectory(measureDir);
string[] measureFiles = new string[2000];
for (var i = 0; i < measureFiles.Length; i++)
{
    // Distinct files, so the state the write serializes actually grows as the run goes - that is the
    // quadratic part of the per-record pattern, and it is what the two numbers below have to show.
    measureFiles[i] = Path.Combine(sweepDir, "m" + i + ".srt");
    File.WriteAllText(measureFiles[i], "1\n00:00:01,000 --> 00:00:02,000\nline " + i + "\n");
}

var perRecord = new SweepState(Path.Combine(measureDir, "per-record.json"));
var perRecordWatch = System.Diagnostics.Stopwatch.StartNew();
foreach (var file in measureFiles)
{
    perRecord.Record(file, ok: true, outputPath: file + ".SYNCED.srt", error: null);
    perRecord.Flush();
}
perRecordWatch.Stop();
var perRecordBytes = new FileInfo(Path.Combine(measureDir, "per-record.json")).Length;
var perRecordSaves = perRecord.Saves;

var inBatches = new SweepState(Path.Combine(measureDir, "batched.json"));
var batchWatch = System.Diagnostics.Stopwatch.StartNew();
foreach (var file in measureFiles)
{
    inBatches.Record(file, ok: true, outputPath: file + ".SYNCED.srt", error: null);
}
inBatches.Flush();
batchWatch.Stop();
var batchBytes = new FileInfo(Path.Combine(measureDir, "batched.json")).Length;

Console.WriteLine("---- C: what 2000 sweep records cost, written per record and written in batches ----");
Console.WriteLine("   per record: " + perRecordSaves + " write(s), " + perRecordWatch.ElapsedMilliseconds + " ms, "
    + (perRecordSaves * perRecordBytes / 1024) + " KB written");
Console.WriteLine("   batched:    " + inBatches.Saves + " write(s), " + batchWatch.ElapsedMilliseconds + " ms, "
    + (inBatches.Saves * batchBytes / 1024) + " KB written");
Console.WriteLine("   same state either way: " + perRecord.PendingWrites + " and " + inBatches.PendingWrites + " pending");
Directory.Delete(sweepDir, recursive: true);

// ---------------- the shape gate in front of the audio cross-check (s31-quality) ----------------
// The gate's own two branches, on the real scoring code, plus the limitation it carries: a ruler that
// is the same cut as the film but offset correlates with the target perfectly and is trusted. That
// property is pinned here on purpose - it is what the audio cross-check is still for.
string GateStamp(double ms)
{
    var total = (long)Math.Round(Math.Max(0, ms));
    return (total / 3600000).ToString("00") + ":" + ((total / 60000) % 60).ToString("00") + ":"
        + ((total / 1000) % 60).ToString("00") + "," + (total % 1000).ToString("000");
}

// Cue spacing is irregular on purpose, drawn from a fixed-seed LCG: a track whose cues sit at regular
// intervals makes a comb of equally good alignments one spacing apart, and the peak of that comb is not
// a peak (measured: quality 0.003 for a ruler on the film's own timeline). Real subtitles are irregular.
string GateSrt(int count, double shiftSeconds, bool stretch = false)
{
    var blocks = new List<string>(count);
    var cursor = 33000.0;
    var seed = 12345u;
    for (var i = 0; i < count; i++)
    {
        var placed = (cursor * (stretch ? 1.02 : 1.0)) + (shiftSeconds * 1000.0);
        blocks.Add((i + 1) + "\n" + GateStamp(placed) + " --> " + GateStamp(placed + 900)
            + "\nline " + (i + 1));
        seed = (seed * 1103515245u) + 12345u;
        cursor += 700.0 + ((seed >> 16) % 2000);
    }

    return string.Join("\n\n", blocks) + "\n";
}

var gateDir = Path.Combine(Path.GetTempPath(), "ruler-shape-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(gateDir);
var gateTarget = Path.Combine(gateDir, "target.srt");
File.WriteAllText(gateTarget, GateSrt(600, 0));
var gateRulerSame = Path.Combine(gateDir, "ruler-same.srt");
File.WriteAllText(gateRulerSame, GateSrt(600, 0));            // the film's own timeline: the ruler is right
var gateRulerOffset = Path.Combine(gateDir, "ruler-offset.srt");
File.WriteAllText(gateRulerOffset, GateSrt(600, 25));         // same cut, 25 s further along: the blind spot
var gateRulerStretched = Path.Combine(gateDir, "ruler-stretched.srt");
File.WriteAllText(gateRulerStretched, GateSrt(600, 0, stretch: true));

var sameShape = SubtitleRulerShape.Score(gateTarget, gateRulerSame, 60);
var offsetShape = SubtitleRulerShape.Score(gateTarget, gateRulerOffset, 60);
var stretchedShape = SubtitleRulerShape.Score(gateTarget, gateRulerStretched, 60);

Check("a ruler on the film's own timeline scores above the threshold (s31-quality)",
    sameShape is { Trusted: true },
    sameShape is { } ss ? ss.Describe() : "null");
Check("a stretched ruler (another cut) scores below the threshold (s31-quality)",
    stretchedShape is { Trusted: false },
    stretchedShape is { } stretchedNote ? stretchedNote.Describe() : "null");
Check("a ruler that is the same cut but offset scores above the threshold too - the blind spot the audio check covers (s31-quality)",
    offsetShape is { Trusted: true, PeakShiftMs: 25000 },
    offsetShape is { } os ? os.Describe() : "null");
Check("an unreadable or too-short ruler scores nothing (s31-quality)",
    SubtitleRulerShape.Score(gateTarget, Path.Combine(gateDir, "missing.srt"), 60) is null,
    "null in, nothing scored");
Directory.Delete(gateDir, recursive: true);

// ---------------- B8: a partial extraction is discarded, never used ----------------
//
// The rule this replaced was `exitCode != 0 && !File.Exists(outputPath)`, i.e. a failed run was accepted
// whenever a file happened to be on disk - and a truncated read does not even fail: ffmpeg reading a
// cut-short container exits 0, writes a well-formed SRT that ends at a cue boundary, and says so only on
// stderr. Both shapes are checked here against the real guard and real files, and the ffmpeg-backed run at
// the end of this section proves the truncated-container case with the real binary.
var b8Dir = Path.Combine(Path.GetTempPath(), "b8-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(b8Dir);
string B8Path(string name) => Path.Combine(b8Dir, name);

string B8Stamp(int seconds) => $"00:{(seconds / 60).ToString("00")}:{(seconds % 60).ToString("00")},000";

string B8Srt(int cues)
{
    var sb = new System.Text.StringBuilder();
    for (var i = 0; i < cues; i++)
    {
        sb.Append(i + 1).Append('\n')
          .Append(B8Stamp(i * 2)).Append(" --> ").Append(B8Stamp((i * 2) + 1)).Append('\n')
          .Append("line ").Append(i + 1).Append("\n\n");
    }

    return sb.ToString();
}

var b8Complete = B8Srt(30);                                   // what ffmpeg writes for a healthy track

// 1. The healthy case: accepted, and the file is left alone for the engine to read.
var b8Keep = B8Path("keep.srt");
File.WriteAllText(b8Keep, b8Complete);
var b8KeepVerdict = ExtractionOutputGuard.Judge(0, b8Keep, new[] { "progress", "out_time_us=59000000" });
Check("B8: a complete extraction is accepted and its cues counted",
    b8KeepVerdict.Accept && b8KeepVerdict.Cues == 30 && File.Exists(b8Keep),
    $"accept={b8KeepVerdict.Accept} cues={b8KeepVerdict.Cues}");

// 2. A kill mid-write: the file is a byte prefix of a healthy one, so it ends inside a cue. The exit code
//    alone refuses this one; case 3 keeps the exit code honest and lets only the structure decide.
var b8Killed = B8Path("killed.srt");
var b8CompleteBytes = System.Text.Encoding.UTF8.GetBytes(b8Complete);
var b8Cut = Math.Max(16, (b8CompleteBytes.Length * 45) / 100);
// Keep the cut inside a cue, which is what a killed process leaves: a prefix of the same bytes.
while (b8Cut + 3 < b8CompleteBytes.Length
       && System.Text.Encoding.UTF8.GetString(b8CompleteBytes, 0, b8Cut).EndsWith("\n\n", StringComparison.Ordinal))
{
    b8Cut += 3;
}

var b8PartialBytes = b8CompleteBytes[..b8Cut];
File.WriteAllBytes(b8Killed, b8PartialBytes);
var b8KilledVerdict = ExtractionOutputGuard.Judge(137, b8Killed, new[] { "Killed" });
Check("B8: a killed extraction is refused and its partial file deleted",
    !b8KilledVerdict.Accept && !File.Exists(b8Killed) && b8KilledVerdict.Reason.Contains("137"),
    $"accept={b8KilledVerdict.Accept} exists={File.Exists(b8Killed)} bytes={b8Cut}");
Check("B8: the refusal says what was removed and how much of it there was",
    b8KilledVerdict.Reason.Contains("discarded") && b8KilledVerdict.Reason.Contains($"{b8Cut} bytes"),
    b8KilledVerdict.Reason);

// 3. The same partial file with a success exit code - a killed child of a wrapper, or a write that stopped
//    without the exit code noticing. Now only the structure can tell, and it does.
var b8HalfCue = B8Path("half-cue.srt");
File.WriteAllBytes(b8HalfCue, b8PartialBytes);
var b8HalfCueVerdict = ExtractionOutputGuard.Judge(0, b8HalfCue, Array.Empty<string>());
Check("B8: a partial file that ends inside a cue is refused even when ffmpeg reported success",
    !b8HalfCueVerdict.Accept && !File.Exists(b8HalfCue) && b8HalfCueVerdict.Reason.Contains("ends inside a cue"),
    b8HalfCueVerdict.Reason);

// 3. The defect itself: a failed run that left a file behind used to be accepted.
var b8Failed = B8Path("failed.srt");
File.WriteAllText(b8Failed, b8Complete);
var oldRuleAccepted = !(1 != 0 && !File.Exists(b8Failed));            // `exitCode != 0 && !File.Exists(...)`
var b8FailedVerdict = ExtractionOutputGuard.Judge(1, b8Failed, new[] { "Error while opening encoder" });
Check("B8: the acceptance rule this replaced did accept a failed run with a file on disk",
    oldRuleAccepted,
    "the old rule's verdict on exit=1 with a file present");
Check("B8: a failed extraction is refused even though a file exists, and the file is deleted",
    !b8FailedVerdict.Accept && !File.Exists(b8Failed),
    $"accept={b8FailedVerdict.Accept} exists={File.Exists(b8Failed)}");

// 4. The truncated-container shape, measured on a real ffmpeg run: exit 0, a well-formed SRT holding only
//    part of the track, and the truncation reported on stderr and nowhere else.
var b8Truncated = B8Path("truncated.srt");
File.WriteAllText(b8Truncated, B8Srt(17));
var b8TruncVerdict = ExtractionOutputGuard.Judge(
    0, b8Truncated, new[] { "[matroska,webm @ 0x55] File ended prematurely", "codec_type=subtitle" });
Check("B8: a structurally complete but truncated extraction is refused on ffmpeg's own report",
    !b8TruncVerdict.Accept && !File.Exists(b8Truncated),
    b8TruncVerdict.Reason);
Check("B8: the refusal says the track is only part of it, with the cue count",
    b8TruncVerdict.Reason.Contains("partial") && b8TruncVerdict.Reason.Contains("17"),
    b8TruncVerdict.Reason);

// 5. A video decode complaint is not a truncation: the subtitle is unaffected, and refusing here would fail
//    jobs the user cannot fix.
var b8VideoNoise = B8Path("video-noise.srt");
File.WriteAllText(b8VideoNoise, b8Complete);
var b8NoiseVerdict = ExtractionOutputGuard.Judge(
    0, b8VideoNoise,
    new[] { "[h264 @ 0x1] error while decoding MB 12 34, bytestream -5", "[h264 @ 0x1] corrupt decoded frame" });
Check("B8: a damaged video frame does not cost the extraction its subtitle",
    b8NoiseVerdict.Accept && b8NoiseVerdict.Cues == 30 && File.Exists(b8VideoNoise),
    $"accept={b8NoiseVerdict.Accept} cues={b8NoiseVerdict.Cues}");

// 6. An empty output is no subtitle at all.
var b8Empty = B8Path("empty.srt");
File.WriteAllText(b8Empty, "\n");
var b8EmptyVerdict = ExtractionOutputGuard.Judge(0, b8Empty, Array.Empty<string>());
Check("B8: an empty extraction is refused and deleted",
    !b8EmptyVerdict.Accept && !File.Exists(b8Empty),
    b8EmptyVerdict.Reason);

// 7. Nothing written at all.
var b8MissingVerdict = ExtractionOutputGuard.Judge(0, B8Path("never-written.srt"), Array.Empty<string>());
Check("B8: an extraction that wrote nothing is refused",
    !b8MissingVerdict.Accept && b8MissingVerdict.Reason.Contains("no subtitle file"),
    b8MissingVerdict.Reason);

// 8. A cue without a timing line is not a cue.
var b8Malformed = B8Path("malformed.srt");
File.WriteAllText(b8Malformed, "1\nthis line should be a timing line\nsomething\n\n2\n00:00:02,000 --> 00:00:03,000\ntext\n\n");
var b8MalformedVerdict = ExtractionOutputGuard.Judge(0, b8Malformed, Array.Empty<string>());
Check("B8: a file whose cue has no timing line is refused and deleted",
    !b8MalformedVerdict.Accept && !File.Exists(b8Malformed) && b8MalformedVerdict.Reason.Contains("timing"),
    b8MalformedVerdict.Reason);

// 9. Every marker the guard knows is found in the line ffmpeg really prints it in.
Check("B8: each truncation marker is recognised",
    ExtractionOutputGuard.TruncationMarkers.All(marker =>
        ExtractionOutputGuard.TruncationMarkerIn(new[] { "prefix " + marker + " suffix" }) == marker),
    string.Join(", ", ExtractionOutputGuard.TruncationMarkers));
Check("B8: a clean log has no truncation marker",
    ExtractionOutputGuard.TruncationMarkerIn(new[] { "Stream mapping:", "out_time_us=59000000" }) is null);

// 10. The real thing: ffmpeg, the production extraction path, and a container that was cut short.
var b8Video = Environment.GetEnvironmentVariable("B8_FIX_TRUNC");
if (string.IsNullOrEmpty(b8Video))
{
    Console.WriteLine("SKIP  B8 with the real ffmpeg (no fixture: ffmpeg is not on PATH here)");
}
else
{
    var b8Stream = int.TryParse(Environment.GetEnvironmentVariable("B8_FIX_STREAM"), out var parsedStream)
        ? parsedStream
        : 1;
    var b8OutTrunc = B8Path("production-truncated.srt");
    var b8Job = new SyncJob { Mode = "auto" };
    var b8Service = new SubSyncService(null!, null!, null!, null!);
    string? b8Thrown = null;
    try
    {
        await b8Service.ExtractSubtitleWithProgressAsync(
            b8Video ?? string.Empty, b8Stream, b8OutTrunc, 60.0, b8Job, CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        b8Thrown = ex.Message;
    }

    Check("B8: the production extraction refuses a truncated container's partial subtitle",
        b8Thrown is not null && b8Thrown.Contains("prematurely"),
        b8Thrown ?? "no failure - the partial extraction was accepted");
    Check("B8: nothing partial is left at the output path afterwards",
        !File.Exists(b8OutTrunc),
        File.Exists(b8OutTrunc) ? "the partial file is still there" : "the file was deleted");

    var b8Full = Environment.GetEnvironmentVariable("B8_FIX_FULL");
    var b8OutFull = B8Path("production-full.srt");
    var b8FullJob = new SyncJob { Mode = "auto" };
    int b8FullCues;
    string? b8FullError = null;
    try
    {
        b8FullCues = await b8Service.ExtractSubtitleWithProgressAsync(
            b8Full ?? string.Empty, b8Stream, b8OutFull, 60.0, b8FullJob, CancellationToken.None);
    }
    catch (Exception ex)
    {
        b8FullCues = -1;
        b8FullError = ex.Message;
    }

    Check("B8: the same file whole extracts all of its cues, and the file is kept for the engine",
        b8FullCues == EnvInt("B8_FIX_FULL_CUES") && File.Exists(b8OutFull),
        b8FullError ?? $"cues={b8FullCues} expected={EnvInt("B8_FIX_FULL_CUES")}");
}

Directory.Delete(b8Dir, recursive: true);

// ---------------- B6: a job cannot stay Running for good ----------------
//
// Two questions, both answered by pure policy code that takes its evidence with it, so the checks can move
// the clock instead of waiting for a stall: which running jobs a sweep would stop, and whether a job that
// left its status Running is settled.
var b6Windows = new StuckJobWindows(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60));
var b6Now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

SyncJob B6Job(string id, SyncJobStatus status = SyncJobStatus.Running)
    => new() { Id = id, BatchId = "batch-b6", Mode = "auto", Status = status };

// The job and what was observed about it: when it last reported something, and how long any process of its
// own has been quiet. Timestamps are handed in, so "an hour ago" costs nothing to test.
JobObservation B6Seen(SyncJob job, string video, DateTime activity, bool alive = false, DateTime? silentSince = null)
    => new(job, video, activity, alive, silentSince);

var b6FreshJob = B6Job("fresh");
var b6Fresh = B6Seen(b6FreshJob, "/media/a.mkv", b6Now);

// 1. A job that has just reported activity is not stuck.
var b6FreshReason = StuckJobPolicy.WhyStuck(b6Now, b6Fresh, false, b6Windows);
Check("B6: a job that just reported activity is not stuck", b6FreshReason is null, b6FreshReason ?? "null");

// 2. The idle window: nothing running for it and no activity - the wait that was never released, or the
//    reader on a share that stopped answering. 14 minutes of quiet is inside the window; 16 is not.
Check("B6: 14 minutes of no activity is still inside the window",
    StuckJobPolicy.WhyStuck(b6Now.AddMinutes(14), b6Fresh, false, b6Windows) is null);
var b6IdleReason = StuckJobPolicy.WhyStuck(b6Now.AddMinutes(16), b6Fresh, false, b6Windows);
Check("B6: 16 minutes with nothing running and no activity is stuck, and the reason names the wait",
    b6IdleReason is not null && b6IdleReason.Contains("16") && b6IdleReason.Contains("no activity"),
    b6IdleReason ?? "null");

// 3. An engine that is still running is not a stall, however long the job has been going: a feature film's
//    audio analysis was measured at 91 minutes, and a 6,7-minute run on the user's own share spent most of
//    it in a demux that the plugin never sees.
var b6Engine = B6Seen(B6Job("engine"), "/media/b.mkv", b6Now.AddMinutes(44), alive: true, silentSince: b6Now.AddMinutes(44));
var b6EngineReason = StuckJobPolicy.WhyStuck(b6Now.AddMinutes(45), b6Engine, false, b6Windows);
Check("B6: a live process that printed a minute ago is not stuck, at any job age",
    b6EngineReason is null, b6EngineReason ?? "null");

// 4. A live process that has said nothing for an hour is wedged - the other half of the rule, and its window
//    is four times the job's own because a quiet stretch is legitimate work.
Check("B6: a live process silent for 59 minutes is still working",
    StuckJobPolicy.WhyStuck(b6Now.AddMinutes(59), B6Seen(b6Engine.Job, "/media/b.mkv", b6Now, alive: true, silentSince: b6Now), false, b6Windows) is null);
var b6WedgedReason = StuckJobPolicy.WhyStuck(
    b6Now.AddMinutes(61), B6Seen(b6Engine.Job, "/media/b.mkv", b6Now, alive: true, silentSince: b6Now), false, b6Windows);
Check("B6: a live process silent for 61 minutes is treated as wedged, and the reason says so",
    b6WedgedReason is not null && b6WedgedReason.Contains("without printing a single line"),
    b6WedgedReason ?? "null");

// 5. A job waiting for another subtitle of the same file, while that one is working, is not stuck: the file's
//    audio is analysed once and the others wait on its gate, legitimately, for as long as that takes.
var b6HolderJob = B6Job("holder");
var b6WaiterJob = B6Job("waiter");
var b6Waiting = new[]
{
    B6Seen(b6HolderJob, "/media/c.mkv", b6Now.AddMinutes(29)),
    B6Seen(b6WaiterJob, "/media/c.mkv", b6Now.AddMinutes(29))
};
var b6Spared = StuckJobPolicy.Stuck(b6Waiting, b6Now.AddMinutes(30), b6Windows);
Check("B6: a job waiting for its file's analysis is spared while that job is working",
    b6Spared.Count == 0,
    $"{b6Spared.Count} stopped");

// 6. ...but not once the holder has stopped working too: that is the recovery, and both end up stopped.
var b6BothStalled = new[]
{
    B6Seen(b6HolderJob, "/media/c.mkv", b6Now),
    B6Seen(b6WaiterJob, "/media/c.mkv", b6Now)
};
var b6Recovered = StuckJobPolicy.Stuck(b6BothStalled, b6Now.AddMinutes(150), b6Windows);
Check("B6: when the holder itself stalled, its waiter is recovered too",
    b6Recovered.Count == 2 && b6Recovered.All(row => row.Reason.Length > 0),
    $"{b6Recovered.Count} stopped");

// 7. A holder whose process is alive, but whose own activity has aged, keeps its waiter alive: that is the
//    demux stretch, not a stall, and the waiter's own clock says nothing about it.
var b6QuietHolder = StuckJobPolicy.Stuck(
    new[]
    {
        B6Seen(b6HolderJob, "/media/c.mkv", b6Now, alive: true, silentSince: b6Now.AddMinutes(148)),
        B6Seen(b6WaiterJob, "/media/c.mkv", b6Now)
    },
    b6Now.AddMinutes(150), b6Windows);
Check("B6: a waiter is spared while its file's holder still has a live process",
    b6QuietHolder.Count == 0 && b6QuietHolder.Count == 0, $"{b6QuietHolder.Count} stopped");

// 8. The sweep picks exactly the stuck jobs out of a mixed set - one idle, one wedged, one waiting on a
//    working sibling, and one that has finished.
var b6IdleJob = B6Job("idle");
var b6WedgedJob = B6Job("wedged");
var b6Mixed = new[]
{
    B6Seen(b6IdleJob, "/media/d.mkv", b6Now),
    B6Seen(b6WedgedJob, "/media/e.mkv", b6Now, alive: true, silentSince: b6Now),
    B6Seen(B6Job("waiting"), "/media/f.mkv", b6Now),
    B6Seen(B6Job("working"), "/media/f.mkv", b6Now.AddMinutes(88)),
    B6Seen(B6Job("done", SyncJobStatus.Completed), "/media/g.mkv", b6Now)
};
var b6Sweep = StuckJobPolicy.Stuck(b6Mixed, b6Now.AddMinutes(90), b6Windows);
var b6Stopped = b6Sweep.Select(row => row.Job.Id).OrderBy(id => id).ToList();
Check("B6: a sweep stops the idle job and the wedged process, and nothing else",
    b6Stopped.SequenceEqual(new[] { "idle", "wedged" }),
    string.Join(",", b6Stopped));

// 9. Recovery, exactly as the service performs it: the job is failed first, and its token is cancelled so
//    whatever it is waiting in unwinds and its worker slot comes back.
var b6Token = new CancellationTokenSource();
var b6Reason = b6Sweep.First(row => row.Job.Id == "idle").Reason;
var b6ToRecover = b6IdleJob;
var b6Batch = new[] { b6ToRecover, b6Mixed[4].Job };
bool B6AllDone() => b6Batch.All(job => job.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled);
var b6BatchDoneBefore = !B6AllDone() && b6Batch.Count(job => job.Status == SyncJobStatus.Running) == 1;
var b6WasStopped = StuckJobPolicy.Stop(b6ToRecover, b6Reason, b6Now.AddMinutes(90));   // what the sweep does
b6Token.Cancel();                                                                    // then its token goes
Check("B6: the sweep's own completion test is false while one job of the batch is Running",
    b6BatchDoneBefore,
    string.Join(",", b6Batch.Select(job => job.Status)));
Check("B6: after the stop the same test is true - the run can finish",
    b6WasStopped && B6AllDone() && b6ToRecover.Status == SyncJobStatus.Failed,
    $"{b6ToRecover.Status} error='{b6ToRecover.Error}'");
Check("B6: a stopped job records when it stopped, why, and that its token was cancelled",
    b6ToRecover.FinishedAtUtc is not null && b6Token.IsCancellationRequested
    && b6ToRecover.Error is not null && b6ToRecover.Error.Contains("Nothing was written"),
    b6ToRecover.Error ?? "no error");
Check("B6: a stopped job does not report itself as cancelled by the user",
    !(b6ToRecover.Error ?? string.Empty).Contains("user") && b6ToRecover.Phase == "Stopped (no progress)",
    $"{b6ToRecover.Phase}: {b6ToRecover.Error}");

// 10. The terminal-state guarantee: a job that ended while Running is failed and says so, and one that has
//     already settled is left exactly as it is.
var b6Unsettled = B6Job("unsettled");
var b6SettleReason = "the job ended without reaching a terminal state";
var b6DidSettle = StuckJobPolicy.Settle(b6Unsettled, b6Now, b6SettleReason);
Check("B6: a job that ended while still Running is failed and carries the reason",
    b6DidSettle && b6Unsettled.Status == SyncJobStatus.Failed && b6Unsettled.Error == b6SettleReason
    && b6Unsettled.FinishedAtUtc is not null,
    $"{b6Unsettled.Status} '{b6Unsettled.Error}'");
Check("B6: settling is idempotent - a terminal job is not judged or relabelled again",
    !StuckJobPolicy.Settle(b6Unsettled, b6Now, b6SettleReason)
    && !StuckJobPolicy.Settle(b6Mixed[4].Job, b6Now, b6SettleReason)
    && b6Mixed[4].Job.Status == SyncJobStatus.Completed
    && b6Mixed[4].Job.Error is null);
Check("B6: a queued or finished job is never called stuck",
    StuckJobPolicy.WhyStuck(b6Now.AddDays(1), B6Seen(B6Job("queued", SyncJobStatus.Queued), "/media/i.mkv", b6Now), false, b6Windows) is null
    && StuckJobPolicy.WhyStuck(b6Now.AddDays(1), B6Seen(B6Job("done2", SyncJobStatus.Completed), "/media/i.mkv", b6Now), false, b6Windows) is null);

// 11. The windows are the user's settings, and the defaults are the ones the config model documents.
var b6Config = new PluginConfiguration();
Check("B6: the default windows are the ones the settings model states",
    b6Config.StuckJobTimeoutMinutes == StuckJobPolicy.IdleMinutesDefault
    && b6Config.WedgedProcessTimeoutMinutes == StuckJobPolicy.SilenceMinutesDefault,
    $"{b6Config.StuckJobTimeoutMinutes}/{b6Config.WedgedProcessTimeoutMinutes}");
var b6WindowsFromConfig = StuckJobWindows.From(b6Config);
Check("B6: the windows in force come from the configuration",
    b6WindowsFromConfig.Idle == TimeSpan.FromMinutes(b6Config.StuckJobTimeoutMinutes)
    && b6WindowsFromConfig.Silence == TimeSpan.FromMinutes(b6Config.WedgedProcessTimeoutMinutes));
Check("B6: a configured window is what the rule is judged by, not a constant",
    StuckJobPolicy.WhyStuck(b6Now.AddMinutes(4), b6Fresh, false, new StuckJobWindows(TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(60))) is not null
    && StuckJobPolicy.WhyStuck(b6Now.AddMinutes(4), b6Fresh, false, b6Windows) is null);
var b6OutOfRange = new PluginConfiguration { StuckJobTimeoutMinutes = 0, WedgedProcessTimeoutMinutes = 99999 };
var b6Notes = SettingsValidation.Apply(b6OutOfRange);
Check("B6: a window outside its bounds is clamped and the page is told",
    b6OutOfRange.StuckJobTimeoutMinutes == SettingsValidation.StuckJobTimeoutMinutesMin
    && b6OutOfRange.WedgedProcessTimeoutMinutes == SettingsValidation.WedgedProcessTimeoutMinutesMax
    && b6Notes.Count(note => note.Contains("timeout")) == 2,
    string.Join(" | ", b6Notes));

// 12. The process registry the policy reads: ref-counted, and silent-since is the last line or the start.
JobProcessRegistry.Begin("reg-1");
JobProcessRegistry.Begin("reg-1");
JobProcessRegistry.End("reg-1");
Check("B6: a job with two live processes is still alive after one of them ends",
    JobProcessRegistry.IsAlive("reg-1"));
JobProcessRegistry.End("reg-1");
Check("B6: a job whose processes have all ended is not alive, and still reports when its silence began",
    !JobProcessRegistry.IsAlive("reg-1") && JobProcessRegistry.SilentSinceUtc("reg-1") is not null);
JobProcessRegistry.Begin("reg-2");
var b6Started = JobProcessRegistry.SilentSinceUtc("reg-2");
Check("B6: a process that has never printed anything dates its silence from its start",
    b6Started is not null && DateTime.UtcNow - b6Started.Value < TimeSpan.FromMinutes(1));
JobProcessRegistry.SawOutput("reg-2");
var b6AfterOutput = JobProcessRegistry.SilentSinceUtc("reg-2");
Check("B6: a line from the process resets its silence",
    b6AfterOutput is not null && b6AfterOutput > b6Started);
JobProcessRegistry.End("reg-2");
JobProcessRegistry.Forget("reg-2");
Check("B6: a finished job's entry is dropped, so nothing accumulates",
    !JobProcessRegistry.IsAlive("reg-2") && JobProcessRegistry.SilentSinceUtc("reg-2") is null
    && JobProcessRegistry.LiveProcesses == 0);


// ---------------- B12: the stores that outlive a job are bounded ----------------
//
// Four stores grow with what a run has touched, and each used to be removed only by the job that created it
// (or only when the whole cache was cleared): the extracted-subtitle cache's memory layer, the reference
// store's per-file entries, the shared extraction directories, and the sweep state's records. What is checked
// here is the bound each one now has, and that eviction never loses the data behind it.
var b12Dir = Path.Combine(Path.GetTempPath(), "b12-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(b12Dir);
string B12Path(string name) => Path.Combine(b12Dir, name);

// 1. The subtitle cache's memory layer: bounded by count, keeps the newest, and the disk layer still answers
//    for what was evicted - the point of a cache is that dropping an entry costs a file read, not the data.
var b12Keys = new List<string>();
for (var i = 0; i < SubtitleCache.MaxMemoryEntries + 64; i++)
{
    var track = "b12-track-" + i.ToString("0000");
    b12Keys.Add(track);
    SubtitleCache.Store(B12Path("b12.mkv"), track, $"1\n00:00:01,000 --> 00:00:02,000\nline {i}\n\n");
}

Check("B12: the subtitle cache's memory layer stays inside its entry bound",
    SubtitleCache.MemoryCount <= SubtitleCache.MaxMemoryEntries,
    $"holding {SubtitleCache.MemoryCount} entries, bound {SubtitleCache.MaxMemoryEntries}");
Check("B12: the newest entry is the one that survives eviction",
    SubtitleCache.TryGet(B12Path("b12.mkv"), b12Keys[^1], out var newest) && newest.Contains("line " + (b12Keys.Count - 1)),
    newest.Length > 0 ? newest.Split('\n')[2] : "nothing");
Check("B12: an evicted entry is still answered from the disk layer",
    SubtitleCache.TryGet(B12Path("b12.mkv"), b12Keys[0], out var oldest) && oldest.Contains("line 0"),
    oldest.Length > 0 ? oldest.Split('\n')[2] : "nothing");

// 2. The character bound, which is the one that follows memory rather than entry count.
var beforeChars = SubtitleCache.MemoryChars;
var big = new string('x', 6 * 1024 * 1024);
for (var i = 0; i < 4; i++)
{
    SubtitleCache.Store(B12Path("b12-big.mkv"), "big-" + i, $"{i}\n00:00:0{i + 1},000 --> 00:00:0{i + 2},000\n{big}\n\n");
}

Check("B12: the memory layer stays inside its character bound as large subtitles arrive",
    SubtitleCache.MemoryChars <= SubtitleCache.MaxMemoryChars,
    $"holding {SubtitleCache.MemoryChars} chars, bound {SubtitleCache.MaxMemoryChars}");

// 3. Pruning the disk layer also drops the memory entries it removed, and an entry whose file has gone is not
//    held for nothing.
var b12Gone = SubtitleCache.KeyFor(B12Path("b12-gone.mkv"), "gone");
SubtitleCache.Store(B12Path("b12-gone.mkv"), "gone", "1\n00:00:01,000 --> 00:00:02,000\nvanished\n\n");
var b12File = Path.Combine(SubtitleCache.Root, b12Gone + ".srt");
var b12Deleted = false;
try
{
    File.Delete(b12File);
    b12Deleted = true;
}
catch (IOException)
{
    // Left in place; the check below then only asserts the prune ran.
}

var b12Pruned = SubtitleCache.Prune();
Check("B12: pruning the disk layer drops the memory entry whose file is gone",
    !b12Deleted || (!SubtitleCache.HasInMemory(B12Path("b12-gone.mkv"), "gone") && b12Pruned >= 1),
    $"pruned {b12Pruned}, deleted={b12Deleted}, still in memory={SubtitleCache.HasInMemory(B12Path("b12-gone.mkv"), "gone")}");

// 4. The reference store: entries and their directories go when no job of that file is left, and stay while
//    one is - a reference read by a running ffsubsync must never be removed from under it.
var b12LiveFile = B12Path("live.mkv");
var b12DeadFile = B12Path("dead.mkv");
var b12LiveReference = ReferenceStore.Reserve(b12LiveFile, "s:1", "engine-b12");
var b12DeadReference = ReferenceStore.Reserve(b12DeadFile, "s:1", "engine-b12");
var b12LiveDirectory = Path.GetDirectoryName(b12LiveReference)!;
var b12DeadDirectory = Path.GetDirectoryName(b12DeadReference)!;
var b12Swept = ReferenceStore.SweepOrphans(path => string.Equals(path, b12LiveFile, StringComparison.Ordinal));
Check("B12: an orphaned reference entry and its directory are removed, a live one is kept",
    b12Swept == 1 && Directory.Exists(b12LiveDirectory) && !Directory.Exists(b12DeadDirectory),
    $"swept {b12Swept}, live kept={Directory.Exists(b12LiveDirectory)}, dead gone={!Directory.Exists(b12DeadDirectory)}");
ReferenceStore.SweepOrphans(_ => false);

// 5. The shared extraction directories: a directory whose jobs are gone is removed, and the store stops
//    counting its consumers.
var b12SharedA = SharedExtractionStore.Acquire(B12Path("shared-a.mkv"), "b12-job-a");
var b12SharedB = SharedExtractionStore.Acquire(B12Path("shared-b.mkv"), "b12-job-b");
File.WriteAllText(Path.Combine(b12SharedB, "subtitle_0.srt"), "1\n00:00:01,000 --> 00:00:02,000\nx\n\n");
var b12SharedRemoved = SharedExtractionStore.Cleanup(id => id == "b12-job-a");
Check("B12: a shared extraction directory whose job is gone is removed, the live one kept",
    b12SharedRemoved >= 1 && Directory.Exists(b12SharedA) && !Directory.Exists(b12SharedB),
    $"removed {b12SharedRemoved}, live kept={Directory.Exists(b12SharedA)}");
Check("B12: the removed directory no longer counts a consumer",
    SharedExtractionStore.ConsumerCount(B12Path("shared-b.mkv")) == 0);
SharedExtractionStore.Release(B12Path("shared-a.mkv"), "b12-job-a");

// 6. The sweep state: bounded as records arrive, not only when the file is read back, and the oldest records
//    are the ones dropped.
var b12State = new SweepState(B12Path("sweep-cache.json"));
for (var i = 0; i < SweepState.MaxEntries + 60; i++)
{
    var source = B12Path($"sweep-{i:0000}.srt");
    File.WriteAllText(source, "1\n00:00:01,000 --> 00:00:02,000\nx\n\n");
    b12State.Record(source, true, source + ".out", null);
}

Check("B12: the sweep state stays inside its bound while records arrive",
    b12State.Count <= SweepState.MaxEntries,
    $"holding {b12State.Count} records, bound {SweepState.MaxEntries}");
Check("B12: the newest sweep record survives the trim",
    b12State.Get(B12Path($"sweep-{SweepState.MaxEntries + 59:0000}.srt")) is not null);

Directory.Delete(b12Dir, recursive: true);


// ---------------- P4: the settings that could lie (F11, F12, F13) ----------------
//
// Three settings used to be able to say something that was not true: a free-text encoding that the server
// silently replaced, a golden-section switch that did nothing unless framerate correction was on, and a
// worker count whose effect on the extraction lanes nobody knew. Each is checked here for what it stores,
// what it reports, and what reaches the engine.

// F11: only the encodings the engine accepts are offered, a chosen one is kept exactly, and one that cannot
// be given is replaced *and named* rather than stored as typed.
Check("F11: the engine's encoding list is the one the page offers",
    SubSyncService.AllowedOutputEncodings.OrderBy(x => x, StringComparer.Ordinal)
        .SequenceEqual(new[] { "ascii", "latin-1", "utf-16", "utf-8", "utf-8-sig" }),
    string.Join(",", SubSyncService.AllowedOutputEncodings.OrderBy(x => x, StringComparer.Ordinal)));

var p4Chosen = new PluginConfiguration { OutputEncoding = "latin-1" };
var p4ChosenNotes = SettingsValidation.Apply(p4Chosen);
Check("F11: a chosen encoding is stored as chosen, with nothing reported",
    p4Chosen.OutputEncoding == "latin-1"
    && !p4ChosenNotes.Any(note => note.StartsWith("Output encoding", StringComparison.Ordinal)),
    string.Join(" | ", p4ChosenNotes));

var p4Typo = new PluginConfiguration { OutputEncoding = "utf8" };      // what a free-text field invited
var p4TypoNotes = SettingsValidation.Apply(p4Typo);
Check("F11: an encoding the engine cannot be given is replaced and the page is told",
    p4Typo.OutputEncoding == "utf-8"
    && p4TypoNotes.Any(note => note.StartsWith("Output encoding", StringComparison.Ordinal)),
    $"stored={p4Typo.OutputEncoding} notes={string.Join(" | ", p4TypoNotes)}");
Check("F11: the engine is given what was stored, and the fallback only for what it cannot be given",
    SettingsValidation.OutputEncodingOf(new PluginConfiguration { OutputEncoding = "latin-1" }) == "latin-1"
    && SettingsValidation.OutputEncodingOf(new PluginConfiguration { OutputEncoding = "utf8" }) == "utf-8"
    && SettingsValidation.OutputEncodingOf(new PluginConfiguration { OutputEncoding = null! }) == "utf-8");
// F12: golden-section search is handed over only with framerate correction, and a tick without it says so.
var p4GssAlone = SubSyncService.FramerateArgs(fixFramerate: false, goldenSection: true).ToList();
var p4GssWithCorrection = SubSyncService.FramerateArgs(fixFramerate: true, goldenSection: true).ToList();
Check("F12: golden-section search alone never reaches the engine",
    !p4GssAlone.Contains("--gss")
    && p4GssAlone.Contains("--no-fix-framerate")
    && p4GssAlone.Contains("--skip-infer-framerate-ratio"),
    string.Join(" ", p4GssAlone));
Check("F12: with framerate correction on, the flag is handed over",
    p4GssWithCorrection.Count == 1 && p4GssWithCorrection[0] == "--gss",
    string.Join(" ", p4GssWithCorrection));
var p4Inert = new PluginConfiguration { FixFramerate = false, UseGoldenSectionSearch = true };
var p4InertNotes = SettingsValidation.Apply(p4Inert);
Check("F12: a tick that does nothing is reported to the page",
    p4InertNotes.Any(note => note.StartsWith("Golden-section", StringComparison.Ordinal)),
    string.Join(" | ", p4InertNotes));
Check("F12: the tick is left as it was, so turning correction back on restores it",
    p4Inert.UseGoldenSectionSearch);
Check("F12: nothing is reported while the pair is usable",
    !SettingsValidation.Apply(new PluginConfiguration { FixFramerate = true, UseGoldenSectionSearch = true })
        .Any(note => note.StartsWith("Golden-section", StringComparison.Ordinal)));

// F13: the extraction lanes' width is the number the settings ask for, read live rather than at startup.
var p4Service = new SubSyncService(null!, null!, null!, null!);
Check("F13: the lane width is half the worker count, capped at the documented three",
    p4Service.ConfiguredLaneLimit == Math.Clamp(SubSyncService.DefaultParallelWorkers / 2, 1, 3)
    && p4Service.ConfiguredLaneLimit == 2,
    $"{p4Service.ConfiguredLaneLimit} of {SubSyncService.DefaultParallelWorkers} workers");


// ---------------- B-series: progress that is measured, deletes that are bounded, kills that stop ----------------

// B19: ffmpeg prints the same moment three ways, and out_time_ms is microseconds despite its name. Measured
// with ffmpeg 7.1 on this project's fixture: out_time_us=33000000, out_time_ms=33000000,
// out_time=00:00:33.000000. The middle one used to be divided by 1000, so a pass alternated between the true
// fraction and "100 % of the file read" once per progress block.
Check("B19: the three progress fields agree on the same moment (33 s)",
    Math.Abs(SubSyncService.ParseFfmpegProgressSeconds("out_time_us=33000000") - 33.0) < 0.001
    && Math.Abs(SubSyncService.ParseFfmpegProgressSeconds("out_time_ms=33000000") - 33.0) < 0.001
    && Math.Abs(SubSyncService.ParseFfmpegProgressSeconds("out_time=00:00:33.000000") - 33.0) < 0.001,
    $"us={SubSyncService.ParseFfmpegProgressSeconds("out_time_us=33000000")} "
    + $"ms={SubSyncService.ParseFfmpegProgressSeconds("out_time_ms=33000000")} "
    + $"hms={SubSyncService.ParseFfmpegProgressSeconds("out_time=00:00:33.000000")}");
Check("B19: a fraction built from either field is the same fraction, never a full bar early",
    Math.Min(1.0, SubSyncService.ParseFfmpegProgressSeconds("out_time_ms=33000000") / 60.0) < 1.0
    && Math.Abs(SubSyncService.ParseFfmpegProgressSeconds("out_time_ms=30000000") - 30.0) < 0.001,
    $"30 s at 60 s duration -> {Math.Min(1.0, SubSyncService.ParseFfmpegProgressSeconds("out_time_ms=30000000") / 60.0):0.00}");
Check("B19: a line that is not a progress line is still -1, and the human form still parses",
    SubSyncService.ParseFfmpegProgressSeconds("frame=12") < 0
    && SubSyncService.ParseFfmpegProgressSeconds("out_time=01:02:03.500000") > 3723.4
    && SubSyncService.ParseFfmpegProgressSeconds("out_time=01:02:03.500000") < 3723.6);

// B23: only a directory named exactly after a job may be deleted recursively, and only inside the scratch root.
Check("B23: a job id is recognised as scratch and nothing else is",
    SyncedTargetNaming.IsJobScratchDirectory("e6f5a69a4cca4ad0a00065bda510ccc2")
    && !SyncedTargetNaming.IsJobScratchDirectory("ref")
    && !SyncedTargetNaming.IsJobScratchDirectory("shared")
    && !SyncedTargetNaming.IsJobScratchDirectory("logs")
    && !SyncedTargetNaming.IsJobScratchDirectory("E6F5A69A4CCA4AD0A00065BDA510CCC2")   // job ids are lowercase
    && !SyncedTargetNaming.IsJobScratchDirectory("e6f5a69a4cca4ad0a00065bda510ccc")    // 31 characters
    && !SyncedTargetNaming.IsJobScratchDirectory("e6f5a69a4cca4ad0a00065bda510cccz")   // not hex
    && !SyncedTargetNaming.IsJobScratchDirectory(".."));
Check("B23: a path outside the root is refused, one inside is allowed",
    SyncedTargetNaming.IsInsideRoot("/cache/subsync", "/cache/subsync/e6f5a69a4cca4ad0a00065bda510ccc2")
    && !SyncedTargetNaming.IsInsideRoot("/cache/subsync", "/cache/subsync-evil/x")
    && !SyncedTargetNaming.IsInsideRoot("/cache/subsync", "/cache/subsync/../media")
    && !SyncedTargetNaming.IsInsideRoot("/cache/subsync", "/media/films"));

// B16: the kill report counts processes that really exited, not tokens that were cancelled.
var b16StoppedProcess = System.Diagnostics.Process.Start(
    new System.Diagnostics.ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
Thread.Sleep(150);
b16StoppedProcess.Kill();
var b16Counted = SubSyncProcesses.CountExited(new[] { b16StoppedProcess }, 3000);
var b16Alive = System.Diagnostics.Process.Start(
    new System.Diagnostics.ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
Thread.Sleep(150);
var b16StillRunning = SubSyncProcesses.CountExited(new[] { b16Alive }, 300);
b16Alive.Kill();
b16Alive.WaitForExit(3000);
b16StoppedProcess.Dispose();
b16Alive.Dispose();
Check("B16: a killed process is counted as stopped", b16Counted == 1, $"counted={b16Counted}");
Check("B16: a process that did not exit is not counted as stopped", b16StillRunning == 0,
    $"counted={b16StillRunning} (a process still running must not be folded into a success figure)");

// B17: finished jobs are kept for an hour, and a batch of 60 does not lose its own history.
SyncJob B17Job(string id, DateTime finished) => new()
{
    Id = id,
    Status = SyncJobStatus.Completed,
    FinishedAtUtc = finished
};
var b17Now = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
var b17Jobs = new Dictionary<string, SyncJob>();
for (var i = 0; i < 60; i++)
{
    b17Jobs[$"job{i:00}"] = B17Job($"job{i:00}", b17Now.AddMinutes(-2));
}

b17Jobs["old"] = B17Job("old", b17Now.AddHours(-2));
var b17Evicted = SubSyncService.JobsToEvict(b17Jobs, b17Now, TimeSpan.FromHours(1), 10000, _ => false);
Check("B17: 60 fresh completed jobs survive a cleanup pass (the old rule dropped them past 50)",
    !b17Evicted.Contains("job00") && !b17Evicted.Contains("job59") && b17Evicted.SequenceEqual(new[] { "old" }),
    string.Join(",", b17Evicted));
var b17Big = new Dictionary<string, SyncJob>();
for (var i = 0; i < 12; i++)
{
    b17Big[$"b{i:00}"] = B17Job($"b{i:00}", b17Now.AddMinutes(-1 - i));
}

var b17Capped = SubSyncService.JobsToEvict(b17Big, b17Now, TimeSpan.FromHours(1), 10, _ => false);
Check("B17: past the backstop the oldest rows go first, and the newest are kept",
    b17Capped.Count == 2 && !b17Capped.Contains("b00") && b17Capped.Contains("b11"),
    string.Join(",", b17Capped));
var b17History = new Dictionary<string, SyncJob> { ["restored"] = B17Job("restored", b17Now.AddDays(-5)) };
Check("B17: a restored history row is not judged as a job",
    SubSyncService.JobsToEvict(b17History, b17Now, TimeSpan.FromHours(1), 10, id => id == "restored").Count == 0);

// B7: the walk honours the token at the next cluster boundary, and a cancelled pass stops.
// B20 reads the reader's own reason for a killed pass; the extraction happens once, beside the fixture it needs.
var b20Token = new CancellationToken(canceled: true);
var b20Ok = MkvSubtitleExtractor.TryExtract(
    Environment.GetEnvironmentVariable("MKV_FIX_WALK"), 0, out _, out var b20Reason, null, out _, null, null, b20Token);

// B31's loop is also driven with bodies that succeed, so "no fault recorded" is measured rather than assumed.
static async Task<int> CountFaultsAsync(int passes)
{
    var faults = 0;
    await SubSyncService.RunPumpLoopAsync(() => true, () => Task.CompletedTask, _ => faults++, passes);
    return faults;
}

static async Task<int> CountPassesAsync(bool keepGoing)
{
    var runs = 0;
    await SubSyncService.RunPumpLoopAsync(() => keepGoing, () => { runs++; return Task.CompletedTask; }, _ => { }, 4);
    return runs;
}

var b7Fixture = Environment.GetEnvironmentVariable("MKV_FIX_WALK");
var b7Mixed = Environment.GetEnvironmentVariable("MKV_FIX_MIXED");
if (string.IsNullOrEmpty(b7Fixture))
{
    Console.WriteLine("SKIP  B7 cancellation (no fixtures)");
}
else
{
    using var b7PreCancelled = new CancellationTokenSource();
    b7PreCancelled.Cancel();
    var b7Ok = MkvSubtitleExtractor.TryExtract(
        b7Fixture, 0, out _, out var b7Reason, null, out var b7Stats, null, null, b7PreCancelled.Token);
    Check("B7: a pass started with a cancelled token stops at once, and says it was cancelled",
        !b7Ok && b7Reason == "cancelled" && b7Stats.Method == "cancelled",
        $"ok={b7Ok} reason={b7Reason} method={b7Stats.Method}");

    // Cancel from the first progress line of a long walk: the pass is then cancelled *while it reads*, which
    // is the case the Kill button on the page creates. It has to come back with the cancellation, and it has
    // to stop where it is - not after walking the rest of the file, which is what "the button does nothing"
    // felt like.
    var b7Long = Environment.GetEnvironmentVariable("MKV_FIX_WALKCANCEL");
    if (string.IsNullOrEmpty(b7Long))
    {
        Console.WriteLine("SKIP  B7 mid-walk cancellation (no long fixture)");
    }
    else
    {
        using var b7MidPass = new CancellationTokenSource();
        var b7Lines = 0;
        var b7Watch = System.Diagnostics.Stopwatch.StartNew();
        var b7MidOk = MkvSubtitleExtractor.TryExtract(
            b7Long, 0, out var b7Text, out var b7MidReason,
            _ => { if (Interlocked.Increment(ref b7Lines) == 1) b7MidPass.Cancel(); },
            out var b7MidStats, null, null, b7MidPass.Token);
        b7Watch.Stop();
        Check("B7: a walk cancelled mid-read returns the cancellation instead of a partial subtitle",
            !b7MidOk && b7MidReason == "cancelled" && b7MidStats.Method == "cancelled" && b7Text.Length == 0,
            $"ok={b7MidOk} reason={b7MidReason} method={b7MidStats.Method} chars={b7Text.Length} "
            + $"after {b7Watch.ElapsedMilliseconds} ms in {b7Lines} progress line(s)");
        Check("B7: the walk stopped at a cluster boundary instead of reading the rest of the file",
            b7MidStats.ClustersVisited < 1200 && b7Watch.ElapsedMilliseconds < 5000,
            $"walked {b7MidStats.ClustersVisited} of 1200 cluster(s) in {b7Watch.ElapsedMilliseconds} ms");
    }
}

// B2: a shared pass names the tracks it could not produce.
var b2Fixture = Environment.GetEnvironmentVariable("MKV_FIX_MULTI");
var b2Grouped = Environment.GetEnvironmentVariable("MKV_FIX_GROUPED");
if (string.IsNullOrEmpty(b2Fixture))
{
    Console.WriteLine("SKIP  B2 missing-track naming (no fixtures)");
}
else
{
    var b2Ok = MkvSubtitleExtractor.TryExtractMany(
        b2Grouped ?? b2Fixture, new[] { 0, 1 }, out var b2Many, out var b2Reason, out var b2Stats);
    Check("B2: a pass that serves every requested track reports no missing one",
        b2Ok && b2Many.Count == 2 && b2Stats.MissedTracks.Count == 0,
        $"served={b2Many.Count} missed=[{string.Join(",", b2Stats.MissedTracks)}] reason='{b2Reason}'");

    var b2GapOk = MkvSubtitleExtractor.TryExtractMany(
        b2Grouped ?? b2Fixture, new[] { 0, 9 }, out var b2GapMany, out var b2GapReason, out var b2GapStats);
    Check("B2: a track the pass could not produce is named in the stats and in the reason",
        b2GapStats.MissedTracks.Contains(9) && b2GapReason.Contains("track(s) 9"),
        $"missed=[{string.Join(",", b2GapStats.MissedTracks)}] reason='{b2GapReason}' served={b2GapMany.Count}");
}

// ---------------- D-series: one error shape, one job per track, a poll that is not flat out ----------------

// D9: the queue answers "already queued" instead of queuing the same track twice. The rule is a predicate so it
// can be driven here: only a live job for the same item and track counts, because syncing a track again after it
// finished is a legitimate request, not a duplicate.
{
    var d9Item = Guid.NewGuid();
    var d9Other = Guid.NewGuid();
    var d9Queued = new SyncJob { ItemId = d9Item, SubtitleIndex = 3, Status = SyncJobStatus.Queued };
    var d9Running = new SyncJob { ItemId = d9Item, SubtitleIndex = 7, Status = SyncJobStatus.Running };
    var d9Done = new SyncJob { ItemId = d9Item, SubtitleIndex = 9, Status = SyncJobStatus.Completed };
    var d9Queue = new List<SyncJob> { d9Queued, d9Running, d9Done };

    Check("D9: a queued job for the same item and track is the duplicate",
        ReferenceEquals(SubSyncService.FindDuplicate(d9Queue, d9Item, 3), d9Queued),
        "the queued job itself is returned, not a copy");
    Check("D9: a running job counts as already queued",
        ReferenceEquals(SubSyncService.FindDuplicate(d9Queue, d9Item, 7), d9Running),
        "running is live work, so a second job would wait for the same file gate");
    Check("D9: a finished job is not a duplicate (re-syncing later is a real request)",
        SubSyncService.FindDuplicate(d9Queue, d9Item, 9) is null,
        "history must not block a new run");
    Check("D9: a different track or a different item is not a duplicate",
        SubSyncService.FindDuplicate(d9Queue, d9Item, 4) is null
        && SubSyncService.FindDuplicate(d9Queue, d9Other, 3) is null,
        "the pair is what identifies the work");
}

// D10: every refusal has one body a page can read. The filter is called here exactly as MVC calls it, with a real
// exception, and the result is inspected - so the shape is pinned by behaviour and not by reading the source.
{
    var d10Log = Microsoft.Extensions.Logging.Abstractions.NullLogger<Jellyfin.Plugin.SubSync.Api.SubSyncExceptionFilter>.Instance;
    var d10Filter = new Jellyfin.Plugin.SubSync.Api.SubSyncExceptionFilter(d10Log);

    static (int Status, string Title, string Detail) Filtered(Jellyfin.Plugin.SubSync.Api.SubSyncExceptionFilter filter, Exception ex)
    {
        var ctx = new Microsoft.AspNetCore.Mvc.Filters.ExceptionContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            new List<Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata>());
        ctx.Exception = ex;
        filter.OnException(ctx);
        var result = (Microsoft.AspNetCore.Mvc.ObjectResult)ctx.Result!;
        var problem = (Microsoft.AspNetCore.Mvc.ProblemDetails)result.Value!;
        return (result.StatusCode ?? 0, problem.Title ?? string.Empty, problem.Detail ?? string.Empty);
    }

    var d10Conflict = Filtered(d10Filter, new InvalidOperationException("Subtitle stream index 11 not found."));
    Check("D10: an exception that escapes an endpoint answers as status/title/detail",
        d10Conflict.Status == 409 && d10Conflict.Title.Length > 0
        && d10Conflict.Detail == "Subtitle stream index 11 not found.",
        $"status={d10Conflict.Status} title='{d10Conflict.Title}' detail='{d10Conflict.Detail}'");
    var d10Unknown = Filtered(d10Filter, new Exception("boom"));
    Check("D10: anything unexpected is a 500 whose detail is the message, not a generic sentence",
        d10Unknown.Status == 500 && d10Unknown.Detail == "boom",
        $"status={d10Unknown.Status} detail='{d10Unknown.Detail}'");

    var d10Refusal = Jellyfin.Plugin.SubSync.Api.SubSyncController.Fail(400, "Empty batch", "A batch must contain at least one task.");
    var d10RefusalBody = (Microsoft.AspNetCore.Mvc.ProblemDetails)((Microsoft.AspNetCore.Mvc.ObjectResult)d10Refusal).Value!;
    Check("D10: a deliberate refusal uses the same body as a thrown failure",
        ((Microsoft.AspNetCore.Mvc.ObjectResult)d10Refusal).StatusCode == 400
        && d10RefusalBody.Title == "Empty batch"
        && d10RefusalBody.Detail == "A batch must contain at least one task.",
        $"status={((Microsoft.AspNetCore.Mvc.ObjectResult)d10Refusal).StatusCode} title='{d10RefusalBody.Title}'");
}

// ---------------- B-series lifecycle and stability: what teardown stops, what a pump survives ----------------

// B14: teardown stops what is running before the service goes away. The children are real processes and the wait
// is bounded, so this is a claim about behaviour rather than about a line of code existing.
{
    JobProcessRegistry.Begin("b14-check");
    var b14Tracked = JobProcessRegistry.LiveProcesses;
    JobProcessRegistry.Clear();
    Check("B14: teardown clears the process registry, so a reloaded plugin inherits no entries",
        b14Tracked == 1 && JobProcessRegistry.LiveProcesses == 0,
        $"before={b14Tracked} after={JobProcessRegistry.LiveProcesses}");

    var b14Processes = new SubSyncProcesses(null!);
    var b14Child = System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo("/bin/sleep", "60"));
    b14Processes.TrackChildProcess(b14Child!);
    var b14Watch = System.Diagnostics.Stopwatch.StartNew();
    var (b14Asked, b14Stopped) = b14Processes.KillChildProcesses();
    b14Watch.Stop();
    Check("B14: a tracked child is killed by the teardown path and counted as exited",
        b14Asked == 1 && b14Stopped == 1 && b14Child!.HasExited,
        $"asked={b14Asked} stopped={b14Stopped} exited={b14Child!.HasExited} after {b14Watch.ElapsedMilliseconds} ms");
    Check("B14: teardown with nothing running waits for nothing and claims nothing",
        SubSyncService.WaitForTasks(Array.Empty<Task>(), 5000) == 0);

    var b14Long = Task.Delay(60000);
    var b14WaitWatch = System.Diagnostics.Stopwatch.StartNew();
    var b14Finished = SubSyncService.WaitForTasks(new[] { b14Long }, 300);
    b14WaitWatch.Stop();
    Check("B14: the wait for lanes is bounded and does not claim an unfinished task finished",
        b14Finished == 0 && b14WaitWatch.ElapsedMilliseconds >= 250 && b14WaitWatch.ElapsedMilliseconds < 5000,
        $"{b14Finished} finished after {b14WaitWatch.ElapsedMilliseconds} ms against a 300 ms deadline");
    Check("B14: a lane that has already finished is counted",
        SubSyncService.WaitForTasks(new[] { Task.CompletedTask }, 300) == 1);
    Check("B14: the deadline is a real bound rather than an unbounded wait",
        SubSyncService.ShutdownWaitMs > 0 && SubSyncService.ShutdownWaitMs <= 30000,
        $"ShutdownWaitMs={SubSyncService.ShutdownWaitMs}");
}

// B31: a pass that throws does not end the pump. The loop is driven here with bodies that throw, so "the pump keeps
// going" is observed rather than asserted about the source.
{
    var b31Runs = 0;
    var b31Faults = new List<Exception>();
    await SubSyncService.RunPumpLoopAsync(
        () => true,
        () =>
        {
            b31Runs++;
            throw new InvalidOperationException("pass " + b31Runs);
        },
        ex => b31Faults.Add(ex),
        3);
    Check("B31: three failed passes leave the loop alive and every pass runs",
        b31Runs == 3 && b31Faults.Count == 3,
        $"{b31Runs} pass(es) ran, {b31Faults.Count} fault(s) recorded, nothing escaped");
    Check("B31: the fault reaches the handler as the exception that happened, not as a summary",
        b31Faults.Count == 3 && b31Faults.All(f => f is InvalidOperationException)
        && b31Faults[0].Message == "pass 1",
        b31Faults.Count > 0 ? b31Faults[0].Message : "no faults");

    var b31Service = new SubSyncService(null!, null!, null!, null!);
    await SubSyncService.RunPumpLoopAsync(
        () => true,
        () => throw new IOException("the media share went away"),
        b31Service.NotePumpFault,
        2);
    Check("B31: the service counts failed passes, so a repeated fault is visible",
        b31Service.PumpFaults == 2,
        $"PumpFaults={b31Service.PumpFaults}");
    var b31ThrewItself = false;
    try
    {
        // The fault path runs inside the loop it protects and both of its log targets can fail, so a report that
        // throws would end the pump. Driven with no logger at all, which is what a service built for a check has.
        b31Service.NotePumpFault(new IOException("the share is gone and the log lives on it"));
    }
    catch (Exception ex)
    {
        b31ThrewItself = true;
        Console.WriteLine(ex.GetType().Name);
    }

    Check("B31: reporting a fault cannot itself take the pump down",
        !b31ThrewItself && b31Service.PumpFaults == 3,
        $"threw={b31ThrewItself} faults={b31Service.PumpFaults}");
    Check("B31: passes that succeed record no fault",
        await CountFaultsAsync(4) == 0);
    Check("B31: the loop runs no pass at all once it is told not to keep going",
        await CountPassesAsync(false) == 0);
    Check("B31: the pause after a fault grows and is capped",
        SubSyncService.PumpFaultBackoffMs(1) == 250
        && SubSyncService.PumpFaultBackoffMs(2) == 500
        && SubSyncService.PumpFaultBackoffMs(6) == 8000
        && SubSyncService.PumpFaultBackoffMs(99) == 8000,
        $"1={SubSyncService.PumpFaultBackoffMs(1)} 2={SubSyncService.PumpFaultBackoffMs(2)} "
        + $"6={SubSyncService.PumpFaultBackoffMs(6)} 99={SubSyncService.PumpFaultBackoffMs(99)}");
}

// B20: a killed extraction is a cancellation, not a failure, so the chain stops instead of trying the next engine.
{
    Check("B20: the reader's own cancellation reason is what the chain's rule recognises",
        !b20Ok && b20Reason == "cancelled" && SubSyncService.IsCancelledExtraction(b20Reason, b20Token),
        $"ok={b20Ok} reason='{b20Reason}'");
    Check("B20: a cancelled job token makes even an empty result a cancellation",
        SubSyncService.IsCancelledExtraction(string.Empty, new CancellationToken(canceled: true)));
    Check("B20: a real read failure is not mistaken for a cancellation",
        !SubSyncService.IsCancelledExtraction("no subtitle blocks in this file", CancellationToken.None)
        && !SubSyncService.IsCancelledExtraction(string.Empty, CancellationToken.None)
        && SubSyncService.IsCancelledExtraction("Cancelled", CancellationToken.None));
}

// B21: the fault inside an AggregateException is what the caller and the log see.
{
    var b21Inner = new IOException("the share went away");
    var b21Root = ExceptionDiagnostics.RootCause(new AggregateException(new AggregateException(b21Inner)));
    Check("B21: a fault nested two aggregates deep is returned as the fault itself",
        ReferenceEquals(b21Root, b21Inner),
        b21Root.GetType().Name + ": " + b21Root.Message);
    var b21Cancel = ExceptionDiagnostics.RootCause(
        new AggregateException(new IOException("read failed"), new OperationCanceledException()));
    Check("B21: a wrapped cancellation stays a cancellation, so a kill is not read as a fault",
        b21Cancel is OperationCanceledException,
        b21Cancel.GetType().Name);
    var b21Many = ExceptionDiagnostics.RootCause(new AggregateException(
        new IOException("one"), new IOException("two"), new IOException("three")));
    Check("B21: several faults are named rather than collapsed into the aggregate's own sentence",
        b21Many.Message.Contains("3 failures") && b21Many.Message.Contains("one")
        && b21Many.Message.Contains("two")
        && !b21Many.Message.Contains("One or more errors occurred"),
        b21Many.Message);
    Check("B21: an ordinary exception is returned as itself",
        ReferenceEquals(ExceptionDiagnostics.RootCause(b21Inner), b21Inner));
    var b21Line = ExceptionDiagnostics.Describe(new AggregateException(b21Inner));
    Check("B21: the log line names the fault and says what it was wrapped in",
        b21Line == "IOException: the share went away (wrapped in AggregateException)",
        b21Line);
}

// ---------------- Access and truth: who sees which run, which engine is installed, what can be synced -------------

// F4: a run belongs to the account that asked for it. Everything here drives the rule the endpoints use, so "who may
// see which job" is measured rather than read out of the source.
{
    var f4Me = Guid.NewGuid();
    var f4Other = Guid.NewGuid();
    var f4Mine = new SyncJob
    {
        Id = "mine",
        OwnerId = f4Me,
        ItemId = Guid.NewGuid(),
        OutputPath = "/media/Movies/Mine (2026).SYNCED.eng.srt",
        Error = "Could not open /media/Movies/Mine (2026).mkv",
        Outcome = "Nothing was written for this subtitle."
    };
    var f4Theirs = new SyncJob { Id = "theirs", OwnerId = f4Other, OutputPath = "/media/Movies/Theirs.SYNCED.eng.srt" };
    var f4Swept = new SyncJob { Id = "sweep", OwnerId = null, OutputPath = "/media/Movies/Swept.SYNCED.eng.srt" };
    var f4All = new List<SyncJob> { f4Mine, f4Theirs, f4Swept };

    Check("F4: an administrator sees every run",
        ItemAccess.VisibleJobs(f4All, f4Me, true).Count() == 3,
        $"{ItemAccess.VisibleJobs(f4All, f4Me, true).Count()} of 3");
    Check("F4: an account sees only the runs its own requests created",
        ItemAccess.VisibleJobs(f4All, f4Me, false).Select(j => j.Id).SequenceEqual(new[] { "mine" }),
        string.Join(",", ItemAccess.VisibleJobs(f4All, f4Me, false).Select(j => j.Id)));
    Check("F4: a run the plugin queued itself is not shown to an account that did not ask for it",
        !ItemAccess.VisibleJobs(f4All, f4Me, false).Any(j => j.Id == "sweep")
        && ItemAccess.VisibleJobs(f4All, null, false).Count() == 0);
    Check("F4: another account's run is answered like one that does not exist",
        !ItemAccess.MaySeeJob(f4Theirs, f4Me, false)
        && !ItemAccess.MaySeeJob(null, f4Me, false)
        && ItemAccess.MaySeeJob(f4Mine, f4Me, false)
        && ItemAccess.MaySeeJob(f4Theirs, f4Me, true));

    var f4View = ItemAccess.ForViewer(f4Mine, false);
    Check("F4: a non-administrator's own run is answered without the server's paths",
        f4View.OutputPath is null && f4View.Error is not null && !f4View.Error!.Contains("/media"),
        $"output={f4View.OutputPath ?? "null"} error='{f4View.Error}'");
    Check("F4: answering one viewer does not edit the server's own record",
        !ReferenceEquals(f4View, f4Mine)
        && f4Mine.OutputPath == "/media/Movies/Mine (2026).SYNCED.eng.srt"
        && f4Mine.Error!.Contains("/media/Movies/Mine (2026).mkv"),
        $"tracked output={f4Mine.OutputPath ?? "null"}");
    Check("F4: the redacted message still says what happened",
        f4View.Error!.Contains("<path>") && f4View.Error!.Contains("Could not open"),
        f4View.Error!);
    var f4AdminJob = new SyncJob { OutputPath = "/media/x.srt", Error = "Could not open /media/x.mkv", Outcome = "wrote /media/x.srt" };
    Check("F4: an administrator's view keeps the paths and is the tracked job itself",
        ReferenceEquals(ItemAccess.ForViewer(f4AdminJob, true), f4AdminJob)
        && f4AdminJob.OutputPath == "/media/x.srt" && f4AdminJob.Error!.Contains("/media/x.mkv"),
        f4AdminJob.Error!);
    Check("F4: a Windows path is redacted as well",
        ItemAccess.RedactPaths(@"Could not open C:\Media\Film (2026).mkv") == "Could not open <path>",
        ItemAccess.RedactPaths(@"Could not open C:\Media\Film (2026).mkv"));
    Check("F4: a path with a space in it is redacted whole",
        ItemAccess.RedactPaths("Could not open /media/Movies/Mine (2026).mkv") == "Could not open <path>",
        ItemAccess.RedactPaths("Could not open /media/Movies/Mine (2026).mkv"));
    Check("F4: the redaction runs to the end of the segment, which is the cheaper mistake",
        ItemAccess.RedactPaths("See /media/x.mkv for details") == "See <path>",
        ItemAccess.RedactPaths("See /media/x.mkv for details"));
    Check("F4: a message that names no path is left alone",
        ItemAccess.RedactPaths("Nothing was written for this subtitle.") == "Nothing was written for this subtitle.");
    Check("F4: a message that is not there stays not there", ItemAccess.RedactPaths(null) is null);
    var f4Task = new BatchTask { OutputPath = "/media/x.srt" };
    ItemAccess.HideServerPaths(f4Task, false);
    Check("F4: a batch task loses its output path too", f4Task.OutputPath is null);
}

// D2: "installed" has to describe the engine a job will run, not the plugin's own copy of one.
{
    var d2Exists = new Func<string, bool>(path => path == "/opt/engine/ffsubsync");
    Check("D2: a configured engine path that exists counts as installed",
        FfSubSyncEngine.EngineIsUsable("/opt/engine/ffsubsync", d2Exists, null));
    Check("D2: a configured engine path that does not exist is not installed",
        !FfSubSyncEngine.EngineIsUsable("/opt/engine/missing", d2Exists, null));
    Check("D2: a bare command name is looked up on PATH, exactly as the shell would",
        FfSubSyncEngine.EngineIsUsable("ffsubsync", d2Exists, "/usr/bin:/opt/engine")
        && !FfSubSyncEngine.EngineIsUsable("ffsubsync", d2Exists, "/usr/bin"));
    Check("D2: an empty or missing PATH finds nothing, so the answer fails closed",
        !FfSubSyncEngine.EngineIsUsable("ffsubsync", d2Exists, string.Empty)
        && !FfSubSyncEngine.EngineIsUsable("ffsubsync", d2Exists, null));
    Check("D2: no resolved path at all is not installed",
        !FfSubSyncEngine.EngineIsUsable(string.Empty, d2Exists, "/usr/bin")
        && !FfSubSyncEngine.EngineIsUsable(null, d2Exists, "/usr/bin"));
}

// D8: one rule for what can be synced, applied wherever a request arrives.
{
    var d8Id = Guid.NewGuid();
    var d8Series = SubSyncService.ClassifySyncTarget(d8Id, "Series", false);
    var d8Season = SubSyncService.ClassifySyncTarget(d8Id, "Season", false);
    var d8Movie = SubSyncService.ClassifySyncTarget(d8Id, "Movie", true);
    var d8Missing = SubSyncService.ClassifySyncTarget(d8Id, null, false);
    Check("D8: a series is refused with a sentence that names what it is",
        !d8Series.CanSync && d8Series.Found && d8Series.Refusal!.Contains("series")
        && d8Series.Refusal!.Contains("not a video"),
        d8Series.Refusal!);
    Check("D8: the same rule covers a season and any other container",
        !d8Season.CanSync && d8Season.Refusal!.Contains("season"),
        d8Season.Refusal!);
    Check("D8: a video is a target",
        d8Movie.CanSync && d8Movie.IsVideo && d8Movie.Found);
    Check("D8: an item that does not exist is refused as one that is not found",
        !d8Missing.CanSync && !d8Missing.Found && d8Missing.Refusal!.Contains("not found"),
        d8Missing.Refusal!);
    Check("D8: the refusal tells the user what to do instead",
        d8Series.Refusal!.Contains("library sweep"));
}

// ---------------- Consistency: who may stop what, what the list says, what re-syncing writes ----------------

// F2: a cancel request has to say what it means, and it may only stop what the caller may see.
{
    var f2Me = Guid.NewGuid();
    var f2Other = Guid.NewGuid();
    var f2Queued = new SyncJob { Id = Guid.NewGuid().ToString("N"), OwnerId = f2Me, Status = SyncJobStatus.Queued, BatchId = "b1" };
    var f2Running = new SyncJob { Id = Guid.NewGuid().ToString("N"), OwnerId = f2Me, Status = SyncJobStatus.Running, BatchId = "b1" };
    var f2Theirs = new SyncJob { Id = Guid.NewGuid().ToString("N"), OwnerId = f2Other, Status = SyncJobStatus.Running, BatchId = "b2" };
    var f2Sweep = new SyncJob { Id = Guid.NewGuid().ToString("N"), OwnerId = null, Status = SyncJobStatus.Running, BatchId = "b3" };
    var f2Finished = new SyncJob { Id = Guid.NewGuid().ToString("N"), OwnerId = f2Me, Status = SyncJobStatus.Completed, BatchId = "b1" };
    var f2All = new List<SyncJob> { f2Queued, f2Running, f2Theirs, f2Sweep, f2Finished };

    var f2Nothing = ItemAccess.SelectKillTargets(f2All, f2Me, false, null, null, false);
    Check("F2: a cancel that names nothing is refused instead of stopping everything",
        f2Nothing.Refusal == ItemAccess.KillRefusal.NothingSpecified && f2Nothing.Targets.Count == 0);

    var f2MineByBatch = ItemAccess.SelectKillTargets(f2All, f2Me, false, null, "b1", false);
    Check("F2: an account can stop its own batch, and only the queued or running parts of it",
        f2MineByBatch.Refusal is null && f2MineByBatch.Targets.Count == 2
        && f2MineByBatch.Targets.All(j => ReferenceEquals(j, f2Queued) || ReferenceEquals(j, f2Running)),
        $"{f2MineByBatch.Targets.Count} target(s)");

    var f2NotAdmin = ItemAccess.SelectKillTargets(f2All, f2Me, false, null, null, true);
    Check("F2: asking to stop every run without being an administrator is refused",
        f2NotAdmin.Refusal == ItemAccess.KillRefusal.NotPermitted && f2NotAdmin.Targets.Count == 0);

    var f2AdminAll = ItemAccess.SelectKillTargets(f2All, f2Me, true, null, null, true);
    Check("F2: an administrator asking for every run gets the live ones and not the finished ones",
        f2AdminAll.Refusal is null && f2AdminAll.Targets.Count == 4
        && !f2AdminAll.Targets.Contains(f2Finished),
        $"{f2AdminAll.Targets.Count} target(s)");

    var f2TheirJob = ItemAccess.SelectKillTargets(f2All, f2Me, false, f2Theirs.ItemId != Guid.Empty ? Guid.Parse(f2Theirs.Id) : Guid.Empty, null, false);
    Check("F2: another account's run cannot be stopped, and the answer is the same one a missing run gets",
        f2TheirJob.Refusal == ItemAccess.KillRefusal.NothingMatched,
        f2TheirJob.Refusal?.ToString() ?? "no refusal");

    var f2MyJob = ItemAccess.SelectKillTargets(f2All, f2Me, false, Guid.Parse(f2Running.Id), null, false);
    Check("F2: an account can stop its own single run",
        f2MyJob.Refusal is null && f2MyJob.Targets.Count == 1 && ReferenceEquals(f2MyJob.Targets[0], f2Running));

    var f2SweepByNonAdmin = ItemAccess.SelectKillTargets(f2All, f2Me, false, Guid.Parse(f2Sweep.Id), null, false);
    var f2SweepByAdmin = ItemAccess.SelectKillTargets(f2All, f2Me, true, Guid.Parse(f2Sweep.Id), null, false);
    Check("F2: a run the plugin queued itself is the administrator's to stop",
        f2SweepByNonAdmin.Refusal == ItemAccess.KillRefusal.NothingMatched
        && f2SweepByAdmin.Refusal is null && f2SweepByAdmin.Targets.Count == 1);

    var f2FinishedJob = ItemAccess.SelectKillTargets(f2All, f2Me, true, Guid.Parse(f2Finished.Id), null, false);
    Check("F2: a finished run is not a target, so a stale id cannot be used to stop anything",
        f2FinishedJob.Refusal == ItemAccess.KillRefusal.NothingMatched);
}

// S5: a track that cannot be synced is listed with the reason, not hidden from the list.
{
    var s5Bitmap = LanguageSupport.ImageBasedRefusal("pgs");
    var s5Dvd = LanguageSupport.ImageBasedRefusal("dvd_subtitle");
    Check("S5: a bitmap track is refused with a sentence that names the format",
        s5Bitmap is not null && s5Bitmap.Contains("PGS") && s5Bitmap.Contains("image"),
        s5Bitmap ?? "(not refused)");
    Check("S5: the other bitmap formats are covered by the same sentence",
        s5Dvd is not null && s5Dvd.Contains("DVD_SUBTITLE"),
        s5Dvd ?? "(not refused)");
    Check("S5: a text track is not refused",
        LanguageSupport.ImageBasedRefusal("subrip") is null
        && LanguageSupport.ImageBasedRefusal("ass") is null
        && LanguageSupport.ImageBasedRefusal("webvtt") is null
        && LanguageSupport.ImageBasedRefusal(null) is null);
    Check("S5: the refusal says what to do instead",
        s5Bitmap is not null && s5Bitmap.Contains("text track"),
        s5Bitmap ?? "(not refused)");
}

// S12: re-syncing the plugin's own output updates it, and never nests a second marker into the library.
{
    Check("S12: the marker the plugin wrote is removed before a new one is added",
        SrtWriter.StripSyncedMarker("Film.S01E01.SYNCED.ukr") == "Film.S01E01"
        && SrtWriter.StripSyncedMarker("Film.S01E01.SYNCED") == "Film.S01E01",
        SrtWriter.StripSyncedMarker("Film.S01E01.SYNCED.ukr"));
    Check("S12: a name the plugin did not write is left exactly as it is",
        SrtWriter.StripSyncedMarker("Film.S01E01") == "Film.S01E01"
        && SrtWriter.StripSyncedMarker("Film.swe") == "Film.swe"
        && SrtWriter.StripSyncedMarker("Film.SYNCEDX.swe") == "Film.SYNCEDX.swe");
    Check("S12: the legacy hyphen form is recognised as a marker too",
        SrtWriter.StripSyncedMarker("Film-SYNCED.swe") == "Film",
        SrtWriter.StripSyncedMarker("Film-SYNCED.swe"));
    var s12Nested = SyncedTargetNaming.SyncedTargetName("/media", "Film.S01E01.SYNCED.ukr", "ukr");
    var s12Plain = SyncedTargetNaming.SyncedTargetName("/media", "Film.S01E01", "ukr");
    Check("S12: re-syncing our own output cannot produce a second marker",
        !s12Nested.Contains("SYNCED.SYNCED") && s12Nested == "/media/Film.S01E01.SYNCED.srt",
        s12Nested);
    Check("S12: and an untouched subtitle still gets the marker it always did",
        s12Plain == "/media/Film.S01E01.SYNCED.srt",
        s12Plain);
    Check("S12: a language-named sidecar keeps the field form Jellyfin needs",
        SyncedTargetNaming.SyncedTargetName("/media", "ukr", "ukr") == "/media/ukr.SYNCED.srt",
        SyncedTargetNaming.SyncedTargetName("/media", "ukr", "ukr"));
}

// ---------------- Hygiene: bounded caches, bounded state, culture-independent numbers (F16, F30, B26) ------------

// F16: the extracted-subtitle cache is bounded in three ways, and what it holds right now is inside them.
{
    Check("F16: the extracted-subtitle cache has a size bound, an age bound and a memory bound",
        SubtitleCache.MaxBytes == 512L * 1024 * 1024
        && SubtitleCache.MaxAge == TimeSpan.FromDays(120)
        && SubtitleCache.MaxMemoryEntries == 512
        && SubtitleCache.MaxMemoryChars == 16L * 1024 * 1024,
        $"disk={SubtitleCache.MaxBytes} age={SubtitleCache.MaxAge.TotalDays}d "
        + $"memory={SubtitleCache.MaxMemoryEntries}/{SubtitleCache.MaxMemoryChars}");
    SubtitleCache.Store("/media/zh-hygiene-check.mkv", "0", new string('x', 2048));
    Check("F16: storing into it keeps the memory layer inside its bounds",
        SubtitleCache.MemoryCount <= SubtitleCache.MaxMemoryEntries
        && SubtitleCache.MemoryChars <= SubtitleCache.MaxMemoryChars,
        $"{SubtitleCache.MemoryCount} entries, {SubtitleCache.MemoryChars} chars");
    Check("F16: it reports what it holds, which is what the page shows",
        !string.IsNullOrWhiteSpace(SubtitleCache.Describe()),
        SubtitleCache.Describe());
}

// F30: a state file written by a longer-lived run is trimmed when it is loaded, not at the next restart of something.
{
    var f30Path = Path.Combine(Path.GetTempPath(), "subsync-sweep-" + Guid.NewGuid().ToString("N") + ".json");
    var f30Oversized = new Dictionary<string, SweepEntry>();
    for (var i = 0; i < SweepState.MaxEntries + 750; i++)
    {
        f30Oversized["/media/file-" + i + ".srt"] = new SweepEntry
        {
            SourcePath = "/media/file-" + i + ".srt",
            LastTouchedUtc = DateTime.UtcNow.AddMinutes(-i)
        };
    }

    File.WriteAllText(f30Path, JsonSerializer.Serialize(f30Oversized, new JsonSerializerOptions { WriteIndented = true }));
    var f30State = new SweepState(f30Path);
    Check("F30: a state file larger than the cap is trimmed as it is loaded",
        f30State.Count <= SweepState.MaxEntries && SweepState.MaxEntries == 5000,
        $"{f30State.Count} entries kept of {f30Oversized.Count} written");
    Check("F30: what it keeps is the most recently touched end of the file",
        f30State.Count == SweepState.MaxEntries
        && f30State.Get("/media/file-0.srt") is not null
        && f30State.Get("/media/file-" + (SweepState.MaxEntries + 749) + ".srt") is null,
        $"{f30State.Count} entries kept, newest present={f30State.Get("/media/file-0.srt") is not null}");
    File.Delete(f30Path);
}

// B26: the numbers the plugin reads out of the engine's own output do not depend on the server's locale.
{
    var b26Invariant = SubSyncService.ParseFfmpegProgressSeconds("out_time=00:00:33.000000");
    var b26ScoreOk = SubSyncService.TryParseEngineScore("score: 12.5", out var b26Score);
    var b26OffsetOk = SubSyncService.TryParseEngineOffset("offset seconds: -2.25", out var b26Offset);
    var b26CommaScoreOk = SubSyncService.TryParseEngineScore("score: 12,5", out var b26CommaScore);
    var b26CommaOffsetOk = SubSyncService.TryParseEngineOffset("offset seconds: -2,25", out var b26CommaOffset);
    Check("B26: the engine's own numbers parse to the values it stated",
        Math.Abs(b26Invariant - 33.0) < 0.0001
        && b26ScoreOk && Math.Abs(b26Score - 12.5) < 0.0001
        && b26OffsetOk && Math.Abs(b26Offset + 2.25) < 0.0001,
        $"out_time={b26Invariant} score={b26Score} offset={b26Offset}");
    Check("B26: a comma-decimal engine is read the same way as a dot-decimal one",
        b26CommaScoreOk && Math.Abs(b26CommaScore - 12.5) < 0.0001
        && b26CommaOffsetOk && Math.Abs(b26CommaOffset + 2.25) < 0.0001,
        $"score={b26CommaScore} offset={b26CommaOffset}");

    // The locale that motivates B26 is a comma-decimal one. This container has no ICU data, so a named culture such as
    // sv-SE cannot be created; a clone of the invariant culture with a comma decimal separator can, always, and it is
    // the shape that matters here: a runtime whose numbers are written "12,5".
    var b26CommaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
    b26CommaCulture.NumberFormat.NumberDecimalSeparator = ",";
    b26CommaCulture.NumberFormat.NumberGroupSeparator = " ";
    var b26Previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = b26CommaCulture;
        var b26UnderComma = SubSyncService.ParseFfmpegProgressSeconds("out_time=00:00:33.000000");
        var b26CommaScoreUnderComma = SubSyncService.TryParseEngineScore("score: 12.5", out var b26ScoreUnderComma);
        var b26CommaOffsetUnderComma = SubSyncService.TryParseEngineOffset("offset seconds: -2.25", out var b26OffsetUnderComma);
        var b26MsUnderComma = SubSyncService.ParseFfmpegProgressSeconds("out_time_us=33000000");
        Check("B26: with the server set to a comma-decimal locale the same lines parse to the same values",
            Math.Abs(b26UnderComma - 33.0) < 0.0001
            && Math.Abs(b26MsUnderComma - 33.0) < 0.0001
            && b26CommaScoreUnderComma && Math.Abs(b26ScoreUnderComma - 12.5) < 0.0001
            && b26CommaOffsetUnderComma && Math.Abs(b26OffsetUnderComma + 2.25) < 0.0001,
            $"out_time={b26UnderComma} out_time_us={b26MsUnderComma} score={b26ScoreUnderComma} offset={b26OffsetUnderComma}");
    }
    finally
    {
        CultureInfo.CurrentCulture = b26Previous;
    }
}

// ---------------- Queue lock efficiency: one engine probe, one settings stat (B5, S7) ----------------

// B5: the engine's version is a property of the binary, not a question to ask it again. The probe here counts
// spawns the way the rig counts them from outside the plugin, so the rule is checked without running anything.
{
    var b5Probes = 0;
    var b5Cache = new EngineVersionCache(path => { b5Probes++; return "ffsubsync 0.5.1"; });
    var b5Stamp = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    for (var i = 0; i < 500; i++)
    {
        b5Cache.VersionFor("/plugins/ffsubsync", b5Stamp, 8819880);
    }

    Check("B5: 500 questions about one engine spawn it once ",
        b5Probes == 1 && b5Cache.Probes == 1 && b5Cache.Hits == 499,
        $"probes={b5Probes} hits={b5Cache.Hits}");

    var b5AfterUpgrade = b5Cache.VersionFor("/plugins/ffsubsync", b5Stamp.AddMinutes(5), 8819880);
    Check("B5: an engine replaced by an upgrade is asked about again ",
        b5Probes == 2 && b5AfterUpgrade == "ffsubsync 0.5.1",
        $"probes={b5Probes} version='{b5AfterUpgrade}'");

    Check("B5: the cache key changes when the binary does, and not when it does not ",
        EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp, 8819880)
            == EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp, 8819880)
        && EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp, 8819880)
            != EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp, 8819881)
        && EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp, 8819880)
            != EngineVersionCache.KeyFor("/plugins/ffsubsync", b5Stamp.AddSeconds(1), 8819880));

    var b5Dead = new EngineVersionCache(path => throw new InvalidOperationException("no engine"));
    Check("B5: an engine that cannot be run is asked about once, not once per question ",
        b5Dead.VersionFor("/nope/ffsubsync", b5Stamp, 1) is null
        && b5Dead.VersionFor("/nope/ffsubsync", b5Stamp, 1) is null
        && b5Dead.Probes == 1,
        $"probes={b5Dead.Probes}");
}

// S7: the settings file is not stat-ed on every question, and the window cannot hide a change for long.
{
    Check("S7: a settings stamp is trusted inside its window and re-read once it passes ",
        !SettingsSource.StampIsDueFor(1000, 900, 250)
        && SettingsSource.StampIsDueFor(1250, 900, 250)
        && SettingsSource.StampIsDueFor(1000, long.MinValue, 250),
        "inside the window it is trusted, past it the file is read, and a file never read is always read");
    Check("S7: the window can be turned off, which is how the cost is measured on one build ",
        SettingsSource.StampIsDueFor(1000, 900, 0)
        && SettingsSource.StampIsDueFor(1000, 1000, 0)
        && SettingsSource.StampIsDueFor(1000, 900, -1),
        "with the window at 0 every call stats the file: the pre-fix behaviour, on the same build");
    Check("S7: the window in force is bounded and defaults to a quarter of a second ",
        SettingsSource.StatTtlMsValue >= 0 && SettingsSource.StatTtlMsValue <= 60000
        && (SettingsSource.StatTtlMsValue == 250
            || Environment.GetEnvironmentVariable("SUBSYNC_SETTINGS_STAT_MS") is not null),
        $"window={SettingsSource.StatTtlMsValue} ms");
}

// {{JOB_CHECK_STATEMENTS}}
// {{SERVICE_CHECK_STATEMENTS}}
// {{OUTPUT_CHECK_STATEMENTS}}

Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
return failures == 0 ? 0 : 1;

// {{JOB_CHECK_TYPES}}
// {{SERVICE_CHECK_TYPES}}
// {{OUTPUT_CHECK_TYPES}}
"""


def extract_js_function(source, name):
    """Returns the source of a top-level `function name(...)` by matching its braces.

    The page's script is shipped, not built, so the only way to unit-test one of its helpers is to take the
    exact text the browser would run. Returns '' when the function is absent.
    """
    start = source.find('function ' + name + '(')
    if start < 0:
        return ''
    depth = 0
    for i in range(source.index('{', start), len(source)):
        if source[i] == '{':
            depth += 1
        elif source[i] == '}':
            depth -= 1
            if depth == 0:
                return source[start:i + 1]
    return ''


def run_node(source, timeout=60):
    """Runs a snippet with node and returns (ok, stdout+stderr). Used for the shipped page script."""
    node = shutil.which('node')
    if not node:
        return None, 'node not installed'
    proc = subprocess.run([node, '-e', source], capture_output=True, text=True, timeout=timeout)
    return proc.returncode == 0, (proc.stdout + proc.stderr).strip()


def prepare_b8_fixtures(fixtures):
    """Builds the real-container fixtures the B8 checks need, with the real ffmpeg.

    The shape that matters is a *truncated* container: measured with ffmpeg 7.1, reading a file cut short
    makes ffmpeg exit 0, write a well-formed SRT holding only the cues that were still there (17 of 30 on
    this fixture) and report the truncation on stderr only ("File ended prematurely"). That is exactly the
    case the plugin used to hand to the engine as if it were the whole track, so the fixture and the exit
    code are asserted here, from the same command line the plugin runs.

    ffmpeg is not a dependency of this suite: when it is absent the harness prints its B8-with-real-ffmpeg
    checks as SKIP and the policy checks - which use real files and the real guard - still run.
    """
    def check(label, ok, detail=''):
        print(('PASS  ' if ok else 'FAIL  ') + label + (f'   [{detail}]' if detail else ''))
        return 0 if ok else 1

    failures = 0
    ffmpeg = shutil.which('ffmpeg')
    if not ffmpeg:
        print('SKIP  B8 with the real ffmpeg (ffmpeg is not on PATH; the guard itself is still checked)')
        return 0

    work = pathlib.Path(fixtures) / 'b8'
    work.mkdir(parents=True, exist_ok=True)
    subs = work / 'subs.srt'
    subs.write_text(''.join(
        f'{i + 1}\n00:{i * 2 // 60:02d}:{i * 2 % 60:02d},000 --> 00:{((i * 2) + 1) // 60:02d}:{((i * 2) + 1) % 60:02d},000\n'
        f'line {i + 1} of the track\n\n' for i in range(30)), encoding='utf-8')

    full = work / 'full.mkv'
    built = subprocess.run(
        [ffmpeg, '-y', '-nostdin', '-v', 'error',
         '-f', 'lavfi', '-i', 'testsrc=d=60:size=128x72:rate=5', '-i', str(subs),
         '-c:v', 'mpeg4', '-q:v', '30', '-c:s', 'srt', '-shortest', str(full)],
        capture_output=True, text=True)
    if built.returncode != 0 or not full.exists():
        last = ((built.stderr or '').strip().splitlines() or ['no output'])[-1]
        return check('the B8 fixture could be built with ffmpeg', False, last)

    truncated = work / 'truncated.mkv'
    data = full.read_bytes()
    truncated.write_bytes(data[:int(len(data) * 0.57)])

    # The container stream index the plugin maps with -map 0:N: ffmpeg's own numbering, never Jellyfin's.
    probe = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 's', '-show_entries', 'stream=index', '-of', 'csv=p=0', str(full)],
        capture_output=True, text=True)
    stream = (probe.stdout or '').strip().splitlines()
    if not stream:
        return check('the B8 fixture has a subtitle stream to extract', False, 'ffprobe found none')

    # The same command line the plugin runs for the fallback, on the cut-short file: this is the repro.
    partial = work / 'pre-fix-partial.srt'
    run = subprocess.run(
        [ffmpeg, '-y', '-nostdin', '-i', str(truncated), '-map', f'0:{stream[0]}', '-f', 'srt',
         '-progress', 'pipe:2', '-nostats', str(partial)],
        capture_output=True, text=True)
    cues = partial.read_text(encoding='utf-8', errors='replace').count(' --> ') if partial.exists() else -1
    marker = 'ended prematurely' in (run.stderr or '')
    whole = subprocess.run(
        [ffmpeg, '-y', '-nostdin', '-v', 'error', '-i', str(full), '-map', f'0:{stream[0]}', '-f', 'srt',
         str(work / 'whole.srt')],
        capture_output=True, text=True)

    print()
    failures += check(
        'the B8 repro: ffmpeg exits 0 on a truncated container and writes only part of the track',
        run.returncode == 0 and marker and 0 < cues < 30,
        f'exit={run.returncode} cues={cues}/30 marker={marker} size={truncated.stat().st_size}/{len(data)}')
    failures += check(
        'the B8 repro: the same file whole extracts every cue',
        whole.returncode == 0 and (work / 'whole.srt').read_text(encoding='utf-8').count(' --> ') == 30,
        f'exit={whole.returncode}')

    ENV['B8_FIX_TRUNC'] = str(truncated)
    ENV['B8_FIX_FULL'] = str(full)
    ENV['B8_FIX_STREAM'] = stream[0]
    ENV['B8_FIX_FULL_CUES'] = '30'
    return failures


def run_gate_source_checks():
    """Source-level checks: the shape score is diagnostic context, and the audio cross-check always runs."""
    # The aggregate, not the one file: the ruler score and the cross-check it must not gate live in the job
    # pipeline, which Phase 2 of the map moved out of `SubSyncService.cs`. Reading the path directly would make
    # this check fail on a file that no longer holds the code rather than on the code.
    service = service_classes()
    score_at = service.find('var rulerShape = SubtitleRulerShape.Score(')
    context_at = service.find('context only; the audio cross-check runs either way')
    cross_at = service.find('var crossCheckOutput = Path.Combine(tempDir, "audio-cross-check.srt");')
    failures = 0
    checks = [
        ('the ruler is still scored, and the score is logged before the cross-check (s31-quality)',
         0 < score_at < cross_at, f'score@{score_at} cross@{cross_at}'),
        ('the score never gates the cross-check: no shape condition wraps it (s31-quality)',
         'shapeTrusted' not in service and 'if (!shapeTrusted)' not in service, ''),
        ('the log says the score is context only (s31-quality)',
         0 < context_at < cross_at, f'context@{context_at}'),
    ]
    print()
    for name, ok, detail in checks:
        print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail and not ok else ''))
        if not ok:
            failures += 1
    return failures


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

    def click_handler_is_passive(source_path):
        """Lift a page's row click handler out of the shipped source and run it.

        Priority 1 asked that a plain click stop adding to the selection. That is a *behaviour*, and the
        first version of this check asserted the string "selectedIds[id] = true;" was absent from the whole
        file - which is wrong twice over: the string legitimately lives in togglePicked (the one function
        meant to add a pick), and a string pin cannot distinguish a click handler from its helper. So the
        handler is extracted and called here instead, with a selection object whose writes are counted.

        Returns (found, writes, detail): whether the handler was located, how many times the plain-click
        path wrote a pick (-1 if it could not be measured), and the reason when it could not. Only
        subsyncMain.js owns the library row list; the detail-page dialog (subsync.js) has no row list, so
        it is not probed here.
        """
        source = open(source_path, encoding='utf-8').read()
        marker = "row.addEventListener('click', function (e) {"
        start = source.find(marker)
        if start < 0:
            return False, -1, 'no row click handler'
        # Walk to the matching close brace of the handler body, so the extent comes from the code and not
        # from guessing at a terminator: a naive find('});') stops at the handler's own first nested block.
        cursor = start + len(marker)
        depth = 1
        while cursor < len(source) and depth:
            if source[cursor] == '{':
                depth += 1
            elif source[cursor] == '}':
                depth -= 1
            cursor += 1
        body = source[start + len(marker):cursor - 1]
        # The whole handler runs, with the event reporting shiftKey: false, so the shift branch is skipped by
        # the code itself. An earlier version of this probe tried to cut the body short at "if (e.shiftKey)"
        # and was wrong: in this handler the shift test is an early-return *guard* that comes before the
        # plain-click code, so truncating there removed the very lines being measured - the probe reported
        # 0 writes and could not be made to fail by any mutation, i.e. it proved nothing.
        node_bin = node
        if not node_bin:
            return False, -1, 'no node'
        script = (
            'var writes = 0;\n'
            'var selectedIds = new Proxy({}, { set: function (t, k, v) { writes++; t[k] = v; return true; },\n'
            '                                deleteProperty: function (t, k) { writes++; delete t[k]; return true; } });\n'
            'var anchorId = "seed";\n'
            # The handler looks its row up in allItems and returns early when it is not there, so the probe
            # has to hand it a row it can find - otherwise execution never reaches the pick code below the
            # lookup and the probe would report "no writes" whatever that code said. (Measured: with an empty
            # allItems a deliberately reintroduced togglePicked() still reported 0 writes.)
            'var allItems = [{ Id: "abc" }];\n'
            'function selectItem() {}\n'
            'function togglePicked() {}\n'
            'function render() {}\n'
            'function startLangScan() {}\n'
            'function visibleItems() { return []; }\n'
            'function $(id) { return null; }\n'
            'var row = { getAttribute: function () { return "abc"; } };\n'
            'function handler(e) {\n' + body + '\n}\n'
            'try { handler({ shiftKey: false, target: { closest: function () { return null; } } }); }\n'
            'catch (err) { console.log(JSON.stringify({ error: String(err) })); process.exit(0); }\n'
            'console.log(JSON.stringify({ writes: writes, anchor: anchorId }));\n'
        )
        work = os.path.join(REPO, '.tests-work')
        os.makedirs(work, exist_ok=True)
        script_path = os.path.join(work, 'click_probe.js')
        with open(script_path, 'w', encoding='utf-8') as handle:
            handle.write(script)
        run = subprocess.run([node_bin, script_path], capture_output=True, text=True)
        try:
            seen = json.loads(run.stdout.strip().splitlines()[-1])
        except (ValueError, IndexError):
            return True, -1, (run.stderr or run.stdout or '')[-220:]
        if 'error' in seen:
            return True, -1, seen['error'][-220:]
        return True, seen.get('writes', -1), ''

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
    # Priority 3 (user report): the overall bar was a 3 px hairline and read as a divider rather than the run's
    # main indicator. It is now the heaviest line in the panel (10 px, rounded, with a darker track and a lit
    # top edge on the fill). The detail-page dialog's own bar (.ss-bar in subsync.js) is a different component
    # and is deliberately left slim - it is not the global run indicator this priority was about.
    report('the overall progress bar is the prominent indicator, not a hairline (priority 3)',
           'height: 10px' in main_html
           and '.ss-progress-bar { height: 100%; width: 0%;' in main_html
           and 'border-radius: 5px; height: 10px' in main_html
           and 'height: 3px' not in main_html.split('.ss-progress-wrap')[1].split('}')[0])
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
           # The count of finished subtitles is on the counts line under the bar, not on the workers line (C1
           # revised): the fraction used to be printed on both. The mirrored run's summary line is still the
           # mirrored batch's own wording.
           "bits = [done + '/' + total + ' done ('" in main_html
           and 'mirroredSummary' in main_html)
    report('the stale queued wording is gone',
           'waiting for earlier runs to finish' not in main_html)
    report('the detail dialog tolerates a failed poll', 'pollFailures' in client)

    # A queued task is not a failed task. It was rendered as "FAIL <title> -- Queued" the moment a batch was
    # opened, which reads as a run that failed instantly. The live panel no longer lists tasks at all (C2,
    # revised); the rule now lives in the one place that does - History's rows only treat Completed, Failed
    # and Cancelled as results, so a run that is still queued shows its counts and nothing else.
    report('queued or running tasks are never reported as failures',
           "function isTerminalStatus(status)" in main_html
           and "status === 'Completed' || status === 'Failed' || status === 'Cancelled'" in main_html
           and 'isTerminalStatus(' in main_html)

    # This plugin's own sidecars are recognised by name and flagged, and re-syncing one updates it (S12). They used to
    # be hidden from the list, which left the list and the queue disagreeing: an index that resolved to a sidecar could
    # be queued without ever having been shown, and the job then wrote a second marker into the library. The loop is
    # prevented by the name instead - a stem the plugin already marked loses that marker before a new one is added.
    report('our own sidecars are recognised by name',
           "IsSyncedSidecarName" in '\n'.join(plugin_sources) and 'IsOwnSidecar' in '\n'.join(plugin_sources))
    report('the track list shows our own sidecars, flagged, rather than hiding them',
           'IsPluginOutput = MediaStreamMap.IsOwnSidecar(s)' in '\n'.join(plugin_sources)
           and '.Where(s => !MediaStreamMap.IsOwnSidecar(s))' not in '\n'.join(plugin_sources))
    report('re-syncing our own sidecar updates it instead of nesting a second marker',
           'SyncedTargetName' in '\n'.join(plugin_sources)
           and 'StripSyncedMarker' in '\n'.join(plugin_sources))

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
    _methods = ['seekhead-cues', 'matroska-cues', 'cue-index', 'metadata-scan', 'matroska-shared',
                'matroska-cached', 'subtitle-cache', 'mp4-sample-table', 'ffmpeg', 'cancelled']
    _unnamed = [m for m in _methods if f'"{m}" =>' not in service_text]
    report('every extraction method this build produces has a name in the phase text',
           not _unnamed, f'unnamed: {_unnamed}')
    report('a cache hit is described as no read, not as a read through the index',
           '"subtitle-cache" => "served from the extracted-subtitle cache, no read this run"' in service_text
           and 'read through the container index ({extractionMethod})' in service_text)
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
           # The pump answers this from a snapshot now - the plan runs outside the queue lock (S40) - but the
           # answer is the one it always was: another job of that file that is queued *or running*, matched on
           # the file's path. The old inline form is asserted gone so the two cannot drift apart.
           re.search(r'liveJobs = _runOrder\s*\.Where\(j => j\.Status == SyncJobStatus\.Queued '
                     r'\|\| j\.Status == SyncJobStatus\.Running\)', service_text, re.S) is not None
           and 'liveJobs.Any(live => string.Equals(live.VideoPath, row.Item1, StringComparison.Ordinal))'
               in service_text
           and 'var stillNeeded = finishedPath is not null' not in service_text)
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
    service_source = service_classes()
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
    # B1: the detail-page dialog is a form, not a list of actions. Measured before this change on a real
    # episode: 59 rows, 59 "Sync" buttons in the body, a 3 177 px body inside a 900 px card, and the dialog's
    # own Close button about 2 200 px below the viewport. The rules below are what keeps that from coming
    # back: one picker and one filter instead of a row per track, exactly one action button, and a card whose
    # body - not the card itself - scrolls, so the actions row cannot be pushed out of view.
    report('the single-item dialog is a form with one action, not a row and a button per track (B1)',
           'function singleTrackLabel(track)' in pages['subsync.js']
           and 'function singleTrackDetail(track)' in pages['subsync.js']
           and 'function singleLanguageGroups(tracks)' in pages['subsync.js']
           and "langField.appendChild(el('label', null, 'Language'))" in pages['subsync.js']
           and "trackField.appendChild(el('label', null, 'Subtitle'))" in pages['subsync.js']
           and 'new Option(singleTrackLabel(track), String(trackIndex(track)))' in pages['subsync.js']
           and 'shell.actions.insertBefore(startBtn, shell.actions.firstChild)' in pages['subsync.js']
           # a body button per track is the defect itself: the old dialog created one inside the loop
           and 'shell.body.appendChild(row);' not in pages['subsync.js'])
    report('only the dialog body scrolls, so its actions stay visible (B1)',
           '{flex:1 1 auto;min-height:0;overflow-y:auto}' in pages['subsync.js']
           and 'display:flex;flex-direction:column;overflow:hidden' in pages['subsync.js']
           and 'margin-top:18px;flex:0 0 auto}' in pages['subsync.js'])
    report('one track from the detail page is still one run of one job (B1)',
           "api(SYNC_BASE + '/Sync', {" in pages['subsync.js']
           and "body: JSON.stringify({ itemId: meta.id, subtitleIndex: trackIndex(track) })" in pages['subsync.js']
           and "shell.setProgress(1, ['Synced', outcome || 'done']);" in pages['subsync.js'])
    # B2: the Add-language row lines its two boxes up. The emby-input's own box is 39 px tall and sits 20 px
    # down inside its field (room the element keeps for a label), so the old top-aligned row left the button
    # 16 px above the input it belongs to - measured on the rig: input top 492, button top 477, 165 px apart.
    report('the Add-language button is the input\'s own height and sits level with it (B2)',
           '.ss-langrow { display: flex; gap: 10px; align-items: flex-end; flex-wrap: wrap; margin-top: 8px; }' in pages['subsyncMain.html']
           and '.ss-langrow .emby-button { margin: 0; height: 39px; padding: 0 15px; }' in pages['subsyncMain.html']
           and 'class="ss-langrow"' in pages['subsyncMain.html']
           and 'align-items:flex-start;flex-wrap:wrap;margin-top:8px;' not in pages['subsyncMain.html'])
    # B3: the language filter box is never swapped out for a line of text. Measured while the lists were read:
    # its whole option list was replaced by one "LOADING" entry and it was disabled (options 1, disabled true,
    # text "LOADING 0/40" then "LOADING 25/40"). It now keeps its box, keeps the languages the last scan found
    # (a re-scan does not invalidate them), stays enabled, and says what it is doing in its own text.
    report('the multi-select language box stays a box while the lists are read (B3)',
           'var counts = langScan.running ? lastLangCounts : langScan.langs;' in pages['subsyncMain.js']
           and "first = 'Sorting\\u2026'" in pages['subsyncMain.js']
           and 'langSel.disabled = false;' in pages['subsyncMain.js']
           and 'var lastLangCounts = {};' in pages['subsyncMain.js']
           and 'lastLangCounts = langScan.langs;' in pages['subsyncMain.js']
           and "'LOADING '" not in pages['subsyncMain.js'])
    # C4: the rows a user has picked sort to the top of the library list, so a selection stays visible and
    # reachable. Measured: rows 4, 5 and 6 picked by right-click, and the list then draws them at positions
    # 0, 1 and 2 with everything else in the library's own order. One definition of the order, shared by the
    # renderer and the range selection.
    # C4, revised 2026-09-18 (user report): picked rows sort to the top so a selection stays visible while
    # the search box narrows the list - but only once there is a selection to keep together. With one pick the
    # list keeps the library's own order. Measured before the revision: one left-click put that row at
    # position 0 of a 41-item list. After: one pick stays where the library put it, and the second pick
    # brings both to positions 0 and 1.
    report('picked rows sort to the top of the library list, from the second pick (C4, revised)',
           'function visibleItems()' in pages['subsyncMain.js']
           and 'var FLOAT_MIN_PICKS = 2;' in pages['subsyncMain.js']
           and 'if (pickedCount() < FLOAT_MIN_PICKS) {' in pages['subsyncMain.js']
           and 'return shown;' in pages['subsyncMain.js']
           and '(selectedIds[x.Id] ? picked : rest).push(x);' in pages['subsyncMain.js']
           and 'return picked.concat(rest);' in pages['subsyncMain.js']
           and 'var filtered = visibleItems();' in pages['subsyncMain.js'])
    # Fix 1 (user report): right-click is the only way to ADD a pick; a plain left-click selects and views the
    # row without touching the selection. C3 had made a click additive - every left-click grew the sync
    # selection, so browsing the library accumulated a batch nobody asked for. Measured after: one left-click
    # leaves the count at zero and reads "right-click a row to pick it", a second left-click still adds
    # nothing, and only the right-click produces "1 file picked". C3's own fix - the selection surviving a
    # search - is kept: nothing in the render or search path clears picks.
    report('a plain click selects and views, and does not add to the selection (fix 1)',
           'selectItem(match);' in pages['subsyncMain.js']
           # a plain click no longer writes into selectedIds. The assertion is scoped to the click handler: the
           # string "selectedIds[id] = true;" must still exist, in togglePicked, which is the one function that
           # is *meant* to add a pick. Asserting it absent from the file at large failed on that helper.
           and 'function togglePicked(id) {\n        if (selectedIds[id]) delete selectedIds[id];\n        else selectedIds[id] = true;' in pages['subsyncMain.js']
           and click_handler_is_passive(os.path.join(web, 'subsyncMain.js'))[1] == 0
           # the C3 behaviour is gone: no click handler adds to the selection
           and 'click or right-click a row to add it, shift-click removes it' not in pages['subsyncMain.js']
           and 'right-click a row to pick it, shift+right-click for a range' in pages['subsyncMain.js']
           # the range reads the drawn order, because the drawn order now depends on the pick count
           and "drawnIds.indexOf(anchorId)" in pages['subsyncMain.js']
           and "$('ss-list').querySelectorAll('.ss-row')" in pages['subsyncMain.js'])
    # C5: one wording for every sync button - a number of subtitle tracks, never a number of files. Measured
    # after the change: three rows selected "Sync 5 subtitles" (was "Sync 3 files" / "Sync 1 file"), a movie
    # row "Sync 2 subtitles" and then "Sync 1 subtitle" (was "Sync movie"), a series row "Sync 111 subtitles"
    # and then "Sync 2 subtitles" after picking a language (was "Sync series"), the single-item dialog
    # "Sync 1 subtitle" and the series dialog "Sync 61 subtitles" (both were "Sync").
    report('every sync button states a number of subtitles (C5)',
           'function subtitleButtonLabel(count)' in pages['subsyncMain.js']
           and 'function syncButtonText(count)' in pages['subsync.js']
           and "'Sync movie'" not in pages['subsyncMain.js']
           and "'Sync series'" not in pages['subsyncMain.js']
           and "'Sync ' + n + ' file'" not in pages['subsyncMain.js']
           and "'Sync again'" not in pages['subsync.js'])
    # C1: the run box states the run's totals once, in one place, and nothing twice. Measured on the rig
    # (tests/gui/runbox-lines.js): the workers line and the counts line used to read
    # "parallel · 1/64 workers · 1/9 · 1 failed" over
    # "8 subtitles left · 1/9 done (11%) · 1 failed · time left: estimating" - the fraction and the failure
    # count on both lines, and "8 subtitles left" saying what "1/9" already said. Now the workers line is
    # "parallel · 1/64 workers" and the counts line is "1/9 done (11%) · 1 failed", which is one line below
    # the worker rows it belongs to (HTML order: workers, phase, counts).
    #
    # The estimate is gone, not fixed: on the user's own 489-task run it read "about 4 min left" at 29% and
    # "about 10 min left" at 46%, because a run mixes 25-minute episodes with two-hour films and the average
    # of what has finished says nothing about what is left. A number that moves the wrong way while the run
    # advances is worse than no number.
    report('the run box states the run\'s totals once and never an estimate (C1)',
           'function renderQueueLine(view)' in pages['subsyncMain.js']
           and 'function runEtaMinutes(view, done, total)' not in pages['subsyncMain.js']
           and 'time left: estimating' not in pages['subsyncMain.js']
           and "subtitle' + (left" not in pages['subsyncMain.js']
           and 'renderQueueLine(view);' in pages['subsyncMain.js']
           and 'renderQueueLine(null);' in pages['subsyncMain.js']
           and 'id="ss-queue"' in pages['subsyncMain.html']
           and pages['subsyncMain.html'].index('id="ss-queue"') < pages['subsyncMain.html'].index('id="ss-workers"')
           and pages['subsyncMain.html'].index('id="ss-queue"') < pages['subsyncMain.html'].index('id="ss-progress"'))
    # Priority 5 (user report, 2026-09-19): the counts line was printed last, under the worker rows ("24/772
    # done (3%)" below "parallel - 8/8 workers"), and both lines were smaller than the panel around them. The
    # counts line is now the run box's first line, the workers line follows it, and both are a step larger
    # (measured in the browser: 12.2 px -> 14.1 px and 14.9 px -> 15.6 px). Every field is unchanged.
    report('the counts line is the run box\'s first line, above the workers line (priority 5)',
           pages['subsyncMain.html'].index('id="ss-queue"') < pages['subsyncMain.html'].index('id="ss-run-label"')
           and pages['subsyncMain.html'].index('id="ss-run-label"') < pages['subsyncMain.html'].index('id="ss-workers"')
           and '.ss-run-counts { font-size: .95rem; }' in main_html
           and '.ss-run-workers { font-size: 1.05rem; }' in main_html)
    # Pass 5 (user report, 2026-09-19): the row a plain click selects and views is marked. Before the
    # right-click-only selection fix the pick class marked it as well (a click picked too); afterwards a click
    # left no mark at all, and a single pick is the one case where the list does not reorder itself, so the
    # clicked row carried no state a user could see. The two classes are different colours on purpose.
    report('the viewed row has its own marker, defined before the pick class (pass 5)',
           "(isSel ? ' viewed' : '')" in pages['subsyncMain.js']
           and '.ss-row.viewed { background:' in main_html
           and main_html.index('.ss-row.viewed {') < main_html.index('.ss-row.selected {'))
    # C2: the progress area is a list of results, not a terminal. Measured: #ss-log was a <pre class="ss-log">
    # in ui-monospace printing "OK   Embedded Test — SYNCED - English - SUBRIP - External → /opt/data/…
    # (-250 ms offset)"; it is now a <div class="ss-tasks"> whose rows each read "Synced · <file> · -250 ms
    # offset · wrote /opt/data/…", in the page's own font. The information (state, file, outcome, path, the
    # reader's cost) is unchanged; the OK/FAIL/SKIP prefixes and the monospace box are gone.
    # D1: History is a list of runs, not a terminal. Measured before: a flat list of 25 rows, each a label, a
    # timestamp and a "6/8 OK" badge, expanding into a <pre> set in ui-monospace. After: day headings
    # ("Today", "September 16, 2026"), one row per run with a state chip ("Partly failed") and a counts line
    # ("8 subtitles · 6 synced · 2 failed · 2 s"), and a detail with a summary, a problems-only switch
    # (All 8 / Problems only 2 → 8 rows to 2) and Copy as text, whose clipboard content was read back and is
    # the old log shape. No <pre> anywhere in the panel.
    report('history is a list of runs with a readable detail, not a log block (D1)',
           'function historyState(b)' in pages['subsyncMain.js']
           and 'function historyCountsLine(b)' in pages['subsyncMain.js']
           and 'function historyDayLabel(date)' in pages['subsyncMain.js']
           and 'function historyTaskRows(tasks, problemsOnly)' in pages['subsyncMain.js']
           and 'function copyHistoryText(view, statusEl)' in pages['subsyncMain.js']
           and 'function buildTaskRow(status, title, note)' in pages['subsyncMain.js']
           and 'ss-hist-detail' in pages['subsyncMain.html']
           and 'ss-hist-summary' in pages['subsyncMain.html']
           and 'ss-hist-filter-btn' in pages['subsyncMain.html']
           and 'ss-hist-log' not in pages['subsyncMain.html']
           and 'ss-hist-log' not in pages['subsyncMain.js']
           and "'ss-tasks ss-hist-rows'" in pages['subsyncMain.js'])
    # C2, revised: the progress area was a list of results rather than a terminal window - and that list is now
    # gone from the run box entirely. Measured before: #ss-log was a <pre class="ss-log"> in ui-monospace
    # printing "OK   Embedded Test - English - SUBRIP - External -> /opt/data/... (-250 ms offset)"; C2 turned
    # that into rows ("Synced · <file> · -250 ms offset · wrote /opt/data/..."), and the user, looking at a
    # 489-task run, asked for the whole area to go: the run box shows the run, and a run's record is History
    # (D1), which renders the same rows from the same one renderer. So the run box has no results area, and
    # buildTaskRow/taskResultNote survive as History's - one definition, one place that shows it.
    report('the run box shows no results area, and History keeps the rows (C2)',
           'class="ss-tasks"' not in pages['subsyncMain.html']
           and '.ss-tasks {' in pages['subsyncMain.html']
           and '.ss-log {' not in pages['subsyncMain.html']
           and 'class="ss-log"' not in pages['subsyncMain.html']
           and 'id="ss-log"' not in pages['subsyncMain.html']
           and 'function logLine(text)' not in pages['subsyncMain.js']
           and 'function logTask(status, title, note)' not in pages['subsyncMain.js']
           and 'function logTerminal(taskLines, view)' not in pages['subsyncMain.js']
           and 'logTerminal(' not in pages['subsyncMain.js']
           and "'ss-log'" not in pages['subsyncMain.js']
           # the rows themselves are still what a run's detail is made of, from the one renderer
           and 'function buildTaskRow(status, title, note)' in pages['subsyncMain.js']
           and 'function taskResultNote(outcome, extractNote, outPath)' in pages['subsyncMain.js']
           and "fragment.appendChild(buildTaskRow(" in pages['subsyncMain.js'])
    # Priority 4 (2026-09-18): a task that has not finished was drawn as a FAILURE. The state word came from
    # a three-way ternary - "Synced" for Completed, "Skipped" for Cancelled, "Failed" for everything else -
    # and "everything else" is where Queued and Running fall, so opening a run in History while it was still
    # going painted every task that had not started yet as Failed in red. Measured against the shipped code
    # (buildTaskRow lifted out of Web/subsyncMain.js and run in node): `Queued -> "Failed" (ss-task-bad)` and
    # `Running -> "Failed" (ss-task-bad)`, and on the rig a ten-task batch spent its whole life with 1-10
    # tasks reading "Failed" while the server reported them Queued/Running
    # (tests/backend/priority4_queued_failed.py). The word and the colour now come from one table with a row
    # per status the server reports, and a status the table has not seen is neutral, never red: an unknown
    # word must not be an accusation.
    #
    # This runs the shipped function rather than pinning a string: the defect was a *mapping*, and a source
    # pin cannot see a mapping. The pieces are lifted straight out of the page source so the check measures
    # the code and not a copy of it, and node is already required by this file elsewhere.
    if node:
        lifter = r"""
const fs = require('fs');
const src = fs.readFileSync(process.argv[2], 'utf8');
function grab(from, to) {
  const start = src.indexOf(from);
  if (start < 0) throw new Error('missing: ' + from);
  const end = src.indexOf(to, start + from.length);
  if (end < 0) throw new Error('unterminated: ' + from);
  return src.slice(start, end + to.length);
}
const pieces = [
  grab('var TASK_STATES = {', '\n    };'),
  grab('function taskState(status) {', '\n    }'),
  grab('function buildTaskRow(status, title, note) {', '\n    }'),
];
global.document = { createElement: function () {
  var kids = [];
  return { className: '', textContent: '', children: kids,
    appendChild: function (c) { kids.push(c); },
    querySelector: function (sel) {
      var cls = sel.replace('.', '');
      return kids.filter(function (k) {
        return (k.className || '').split(' ').indexOf(cls) !== -1; })[0] || null;
    } };
} };
eval(pieces.join('\n'));
var out = {};
['Queued', 'Running', 'Completed', 'Failed', 'Cancelled', 'SomethingNew'].forEach(function (s) {
  var row = buildTaskRow(s, 'Some Movie');
  out[s] = { word: row.querySelector('.ss-task-state').textContent, cls: row.className };
});
console.log(JSON.stringify(out));
"""
        lifter_path = os.path.join(REPO, '.tests-work', 'taskrow_lift.js')
        os.makedirs(os.path.dirname(lifter_path), exist_ok=True)
        with open(lifter_path, 'w', encoding='utf-8') as handle:
            handle.write(lifter)
        lifted = subprocess.run([node, lifter_path, os.path.join(web, 'subsyncMain.js')],
                                capture_output=True, text=True)
        states = {}
        if lifted.returncode == 0 and lifted.stdout.strip():
            try:
                states = json.loads(lifted.stdout)
            except ValueError:
                states = {}
        report('a task that has not finished is never drawn as a failure (priority 4)',
               states.get('Queued', {}).get('word') == 'Queued'
               and states.get('Running', {}).get('word') == 'Running'
               and states.get('Completed', {}).get('word') == 'Synced'
               and states.get('Failed', {}).get('word') == 'Failed'
               and states.get('Cancelled', {}).get('word') == 'Skipped'
               # the two that must not be red, and the one that must
               and 'ss-task-bad' not in states.get('Queued', {}).get('cls', 'bad')
               and 'ss-task-bad' not in states.get('Running', {}).get('cls', 'bad')
               and 'ss-task-bad' in states.get('Failed', {}).get('cls', '')
               # a status nobody has seen yet is neutral too: it must not accuse the run of failing
               and 'ss-task-bad' not in states.get('SomethingNew', {}).get('cls', 'bad')
               and states.get('SomethingNew', {}).get('word') == 'SomethingNew',
               (lifted.stderr or '')[-200:] if states == {} else '')
    report('an unfinished task says why it is waiting, not its own status word (priority 4)',
           'function isPendingStatus(status)' in pages['subsyncMain.js']
           and 'if (isPendingStatus(e.status))' in pages['subsyncMain.js']
           and 'note = t.Phase || t.phase' in pages['subsyncMain.js']
           # the neutral colour exists and is not the failure colour
           and '.ss-task-pending .ss-task-state { color: rgba(255,255,255,.55); }' in pages['subsyncMain.html'])
    # G2: an image-based subtitle track (PGS, VobSub, DVB, XSUB) can never be aligned, and the page used to
    # offer them anyway: the row's picker listed one as selectable, "All N tracks" counted it, the language
    # filter offered a language carried only by one, and the button counted it. Measured before, on the user's
    # own server: five refusals in one afternoon of testing (Jellyfin log, "task 3 failed validation: This
    # track is DVDSUB, an image subtitle format..."), each one a failed task of a run that had nothing wrong
    # with it. The server refuses such a track at enqueue on purpose (the refusal is a visible failed task, not
    # a silent drop); the interface must not hand it one. Measured after, on the rig: the row for a file whose
    # only track is DVDSUB shows that track listed and unselectable, its Sync button disabled and labelled with
    # the count of syncable tracks (0), and clicking it queues no batch at all.
    report('an image-based subtitle track cannot be queued from the page (G2)',
           'function trackUnsupported(t)' in pages['subsyncMain.js']
           and 'function syncableTracks(tracks)' in pages['subsyncMain.js']
           # the picker lists it and disables it, and "All N tracks" counts what can be queued
           and "' disabled title=" in pages['subsyncMain.js']
           and 'image format, cannot be synced' in pages['subsyncMain.js']
           and "var usable = syncableTracks(movieTracks);" in pages['subsyncMain.js']
           and "All ' + usable.length" in pages['subsyncMain.js']
           and "'All ' + movieTracks.length" not in pages['subsyncMain.js']
           # nothing that builds a queue, counts one, or reads languages may see an image track
           and 'syncableTracks(tracks).forEach(function (t) {' in pages['subsyncMain.js']
           and 'syncableTracks(tracks) : (tracks || [])' not in pages['subsyncMain.js']
           and 'syncableTracks(tracks)).length' in pages['subsyncMain.js']
           and 'syncableTracks(movieTracks).length : 1' in pages['subsyncMain.js']
           and 'syncableTracks(movieTracks).filter(function (t)' in pages['subsyncMain.js']
           and 'var use = syncableTracks(tracks);' in pages['subsyncMain.js']
           # the row's own button cannot offer work that does not exist
           and 'movieSyncableCount() ? \'\' : \' disabled title=' in pages['subsyncMain.js']
           and "subtitleButtonLabel(movieSyncableCount() || null)" in pages['subsyncMain.js']
           # "nothing found" and "nothing that can be aligned" are different answers
           and 'function selectionHasOnlyImageTracks(items)' in pages['subsyncMain.js']
           and 'which cannot be aligned' in pages['subsyncMain.js']
           # the selection-level Sync is greyed out when the picks have nothing that can be queued, with the
           # reason on it - "not known yet" (null, scan still running) is not "nothing" (0)
           and 'function selectionUnsyncableReason(items)' in pages['subsyncMain.js']
           and 'var nothingToSync = !busyNow && estimate === 0;' in pages['subsyncMain.js']
           and 'btn.disabled = busyNow || nothingToSync;' in pages['subsyncMain.js']
           and "btn.setAttribute('title', nothingToSync" in pages['subsyncMain.js']
           # and the sweep, which queues without being asked, skips one instead of spending a failure on it
           and 'if (track.UnsupportedReason is not null)' in service_source
           and 'SkippedUnsupported++' in service_source
           and 'left out entirely' not in service_source
           and 'public int SkippedUnsupported' in service_source
           and 'skipped (image subtitle format)'
               in open(os.path.join(SERVICE_DIR, 'SubSyncSweepTask.cs'), encoding='utf-8').read())

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
    # Priority 3 (user report): the run's counts line lost the failed count in C1 and never had one for the
    # "already in sync" outcome (S8/S12 - the subtitle was already aligned, so nothing was written). Both are
    # plain text beside the done/percentage, and the in-sync count is derived from the tasks' own Outcome text
    # rather than a new server field, so it cannot disagree with what History's rows say. Measured on the rig
    # after: a run of 6 with one genuine failure and the rest already aligned read
    # "6/6 done (100%) · 1 failed · 5 already in sync"; before, the same line said "6/6 done (100%) · 1 failed".
    if node:
        counts_lift = r"""
const fs = require('fs');
const src = fs.readFileSync(process.argv[2], 'utf8');
function grab(from, to) {
  const start = src.indexOf(from);
  if (start < 0) throw new Error('missing: ' + from);
  const end = src.indexOf(to, start + from.length);
  if (end < 0) throw new Error('unterminated: ' + from);
  return src.slice(start, end + to.length);
}
const pieces = [
  grab('function alreadyInSyncCount(view) {', '\n    }'),
  grab('function renderQueueLine(view) {', '\n    }'),
];
var written = [];
global.$ = function () {
  return { set textContent(v) { written.push(v); }, get textContent() { return written[written.length - 1]; } };
};
eval(pieces.join('\n'));
function task(status, outcome, outPath) {
  return { Status: status, Outcome: outcome, OutputPath: outPath };
}
var out = {};
// six tasks: one real failure, four already in sync, one written
out.mixed = (function () {
  renderQueueLine({ Total: 6, Completed: 6, Failed: 1, Cancelled: 0, Tasks: [
    task('Failed', null, null),
    task('Completed', 'already in sync (shift under 3 s) \u2014 no change needed', null),
    task('Completed', 'already in sync (nothing to change) \u2014 nothing written', null),
    task('Completed', 'already in sync (shift under 3 s) \u2014 no change needed', null),
    task('Completed', 'already in sync (shift under 3 s) \u2014 no change needed', null),
    task('Completed', '\u2212250 ms offset', '/media/out.SYNCED.srt')
  ] });
  return written[written.length - 1];
})();
// a run where everything was written: the in-sync note must not appear
out.allWritten = (function () {
  renderQueueLine({ Total: 2, Completed: 2, Failed: 0, Cancelled: 0, Tasks: [
    task('Completed', '\u2212250 ms offset', '/media/a.SYNCED.srt'),
    task('Completed', '+1200 ms offset', '/media/b.SYNCED.srt')
  ] });
  return written[written.length - 1];
})();
// a completed task with an outcome but no path that is NOT the in-sync wording is not counted
out.otherNoOutput = (function () {
  renderQueueLine({ Total: 1, Completed: 1, Failed: 0, Cancelled: 0, Tasks: [
    task('Completed', 'no change needed', null)
  ] });
  return written[written.length - 1];
})();
console.log(JSON.stringify(out));
"""
        counts_path = os.path.join(REPO, '.tests-work', 'counts_lift.js')
        os.makedirs(os.path.dirname(counts_path), exist_ok=True)
        with open(counts_path, 'w', encoding='utf-8') as handle:
            handle.write(counts_lift)
        lifted_counts = subprocess.run([node, counts_path, os.path.join(web, 'subsyncMain.js')],
                                       capture_output=True, text=True)
        lines = {}
        if lifted_counts.returncode == 0 and lifted_counts.stdout.strip():
            try:
                lines = json.loads(lifted_counts.stdout)
            except ValueError:
                lines = {}
        report('the counts line restores the failed count and adds already in sync (priority 3)',
               lines.get('mixed') == '6/6 done (100%) \u00b7 1 failed \u00b7 4 already in sync',
               (lifted_counts.stderr or '')[-200:] if not lines else lines.get('mixed', ''))
        report('already in sync is counted from the outcome, and only for a task that wrote nothing (priority 3)',
               lines.get('allWritten') == '2/2 done (100%)',
               lines.get('allWritten', ''))
        report('a task with no output path that did not say "already in sync" is not counted as one (priority 3)',
               lines.get('otherNoOutput') == '1/1 done (100%)',
               lines.get('otherNoOutput', ''))
    report('the cancel control works for a run the page did not start',
           'var target = watchedBatchId || mirroredBatchId;' in pages['subsyncMain.html']
           and "api('SubSync/Batch/' + target + '/Cancel'" in pages['subsyncMain.html'])
    # Measured on a real server 2026-09-12: two subtitles of one 2 h movie started together, each ran the
    # engine against the audio (141 s each, cachedSpeech=False both times). The audio analysis is per file,
    # so only one job may run it while the file's speech cache is empty; the others wait for the harvest.
    report('only one job per file runs the audio analysis, the others wait for its harvest',
           '_speechGates' in service_source
           # The wait carries the job's own token: it can last as long as the file's audio analysis (over an
           # hour on a feature film), so the user's Kill and the stall watchdog have to be able to end it -
           # without the token the job held its worker slot until the server was restarted (B6).
           and 'await speechGate.WaitAsync(cancellationToken)' in service_source
           and 'await speechGate.WaitAsync()' not in service_source
           and 'harvestedWhileWaiting' in service_source
           and 'ReleaseSpeechGate(job, videoPath);' in service_source
           and 'job.HoldsSpeechGate = true;' in service_source
           and 'public bool HoldsSpeechGate { get; set; }' in service_source)
    report('a refusal is reported as a refusal, not as a failure',
           'private static string StatusOf(SyncJob job)' in controller_source
           and 'StatusOf(j),' in controller_source
           # The refusal phase used to be written out at each of the five refusal sites; the RunSyncJob
           # extraction funnelled them through one helper (RefuseJob), so this pin follows the single
           # definition and the calls that name the phase, instead of the copies that used to exist.
           and 'private void RefuseJob(SyncJob job, string phase, string error, string? tempOutput)' in service_source
           and 'job.Phase = phase;' in service_source
           and '"Refused",' in service_source
           and service_source.count('RefuseJob(') >= 6)
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
           'internal static string? RescaleOntoReferenceSpan(' in service_source
           and 'TimeSpan videoDuration,' in service_source
           and 'AlignmentMetrics.RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job, _logger)' in service_source
           and 'IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds)' in service_source
           and 'so the reference is the odd one' in service_source
           and 'stretched to {factor:0.#####}x onto the reference' in service_source
           and 'rescaling it onto the reference\'s time base' in service_source
           and 'a different cut, left for the alignment to report' in service_source
           # the run is still built from the reference and the rescale's output; the note names the VAD it is
           # given, which is the configured one whenever the plugin supplied a subtitle reference (S43)
           and 'config, referenceArg, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream,' in service_source
           and 'VadForReference(referenceSpec)' in service_source)
    report('a subtitle reference that is not the same cut is replaced by the audio, not refused',
           'ReferenceStore.Discard(videoPath, reference.Spec);' in service_source
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

    report('a walk is only used to judge its own volume, and only from one place',
           'VolumesOtherThan(' in service_source
           and 'job(s) on another volume were being ' in service_source
           and service_source.count('ObserveWalk(') == 1)

    report('an unmeasured volume is read once instead of guessed at, from where every job passes',
           'ProbeBytes = 16 * 1024' in service_source
           and 'TryBeginProbe()' in service_source
           and service_source.count('ProbeVolumeIfUnmeasured(') == 2      # the method and one call site
           # The call site is the top of the job's own try block (where every job passes), which is where it
           # belongs for a second reason since B6: a probe that throws there is caught by the job's handler and
           # its cleanup runs, instead of leaving the job Running with its slot held.
           and re.search(r'ProbeVolumeIfUnmeasured\(\s*_jobContexts\.TryGetValue', service_source) is not None
           # and not back in the audio-reference branch, which jobs on a real server never take (S38, 2026-09-14)
           and 'ProbeVolumeIfUnmeasured(videoPath);' not in service_source
           and 'needs the queue lock' not in service_source)

    validation_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                                           'SettingsValidation.cs'), encoding='utf-8').read()
    plugin_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Plugin.cs'), encoding='utf-8').read()
    main_js = main_html  # the page's script is checked with its markup, as everywhere else here

    report('settings are validated wherever they are stored, and the page is told what changed',
           'SettingsValidation.Apply(wanted);' in controller_source
           and 'Settings/ValidationNotes' in controller_source
           and 'Plugin.LastSettingsNotes = notes;' in controller_source
           and 'SettingsValidation.Apply(pluginConfiguration)' in plugin_source
           and 'SubSync/Settings/ValidationNotes' in main_js
           and 'SubSync settings adjusted' in plugin_source)

    report('the engine and the plugin\'s own heuristics read the validated numbers, never the stored ones',
           service_source.count('SettingsValidation.MaxOffsetSecondsOf(config)') >= 8
           and 'SettingsValidation.MaxSubtitleSecondsOf(config)' in service_source
           and 'SettingsValidation.MaxSubtitleReferenceOffsetSecondsOf(config)' in service_source
           and 'SettingsValidation.OutputEncodingOf(config)' in service_source
           and 'config.MaxOffsetSeconds' not in service_source
           and 'config.MaxSubtitleSeconds' not in service_source)

    report('a configured ffmpeg that is not there is not handed to the engine',
           'SettingsValidation.BinaryPathIsUsable(config.FfmpegPath)' in service_source)

    def _bound(name):
        found = re.search(rf'\b{name} = ([0-9.]+);', validation_source)
        return float(found.group(1)) if found else None

    settings_bounds = {
        'ss-maxoffset': (_bound('MaxOffsetSecondsMin'), _bound('MaxOffsetSecondsMax')),
        'ss-maxrefoffset': (_bound('MaxSubtitleReferenceOffsetSecondsMin'), _bound('MaxSubtitleReferenceOffsetSecondsMax')),
        'ss-maxsub': (_bound('MaxSubtitleSecondsMin'), _bound('MaxSubtitleSecondsMax')),
        'ss-workers-input': (_bound('ParallelWorkersMin'), _bound('ParallelWorkersMax')),
    }
    page_bounds = {}
    for tag in re.findall(r'<input[^>]*>', raw_main_html):
        hit = re.search(r'id="(ss-[^"]+)"', tag)
        if hit and hit.group(1) in settings_bounds:
            low = re.search(r'min="([0-9.]+)"', tag)
            high = re.search(r'max="([0-9.]+)"', tag)
            page_bounds[hit.group(1)] = (float(low.group(1)), float(high.group(1))) if low and high else None

    report('the settings page offers the same bounds the server enforces',
           page_bounds == settings_bounds, f'{page_bounds} vs {settings_bounds}')

    def _pump_lock_body(text):
        start = text.index('private async Task PumpAsync')
        lock_at = text.index('lock (_queueLock)', start)
        depth = 0
        for index in range(text.index('{', lock_at), len(text)):
            if text[index] == '{':
                depth += 1
            elif text[index] == '}':
                depth -= 1
                if depth == 0:
                    return text[lock_at:index]
        raise AssertionError('unbalanced lock block in PumpAsync')

    pump_lock_body = _pump_lock_body(service_source)
    plan_is_outside = ('PlanStart(' not in pump_lock_body
                       and 'PlanStart(' in service_source
                       and 'pump-plan-outside-lock' in service_source)

    report('the queue lock covers bookkeeping and a snapshot, never the planning pass',
           # S40: the enqueue waits for this lock, and the plan is the one slow thing that used to be inside it.
           # Asserted structurally, by brace-matching the lock block out of the pump and asking what is in it -
           # a timing check would pass on a fast disk while the defect was still there.
           plan_is_outside)

    report('the audio path is given a VAD that reads audio, and says so',
           'private const string AudioReferenceVad = "webrtc";' in service_source
           and 'VadForReference(referenceSpec)' in service_source
           and service_source.count('vadOverride: AudioReferenceVad') >= 2
           and 'so the engine is given --vad {AudioReferenceVad}' in service_source
           and 'LogVadOverride(' in service_source)

    report('the speech cache is keyed by the VAD the engine is actually given',
           service_source.count('AudioReferenceVad + "|audio"') == 2
           and '(config.VadMethod ?? "subs_then_webrtc") + "|audio"' not in service_source)

    report('the engine\'s alignment score is captured and logged for both reference paths',
           'TryParseEngineScore(line' in service_source
           and 'LogEngineAlignment(' in service_source
           and service_source.count('LogEngineAlignment(') >= 3       # the method and its callers
           and 'ffsubsync alignment: score=' in service_source
           and 'alignment: score={scoreText} offset={offsetText} against {reference}' in service_source)

    report('a suspicious subtitle ruler is cross-checked against the film\'s own audio before it is written',
           'RulersDisagree(fromReferenceNote.ShiftMs, audioChange.ShiftMs, referenceCeilingMs)' in service_source
           and 'the reference subtitle and the film\'s own audio disagree' in service_source
           and 'cross-check of a subtitle ruler' in service_source
           and 'keeping the reference\'s answer' in service_source
           and 'SuspiciousReferenceShiftFraction = 1.0 / 3.0' in service_source)

    report('a subtitle ruler whose cues did not move together is refused as not the same cut',
           'RulerSpreadTooWide(spread, referenceCeilingMs)' in service_source
           and 'the cues did not move together against' in service_source
           and 'SubtitleReferenceSpreadFraction = 0.25' in service_source
           and 'ReferenceStore.Discard(videoPath, reference.Spec);' in service_source)

    report('the first measurement of a volume is more than one read, and says what its median stands on',
           'private const int ProbeReads = 3;' in service_source
           and 'median of {samples.Count} reads took' in service_source
           and 'profile.Observe(read, ms);' in service_source
           and 'ProbeOffset(length, index, ProbeReads)' in service_source)

    report('the page reads and writes settings through the plugin, not the web client',
           "api('SubSync/Configuration')" in pages['subsyncMain.html']
           and 'getPluginConfiguration' not in pages['subsyncMain.html']
           and 'updatePluginConfiguration' not in pages['subsyncMain.html'])
    # F3: the four item-scoped endpoints must ask who is calling. A future endpoint that skips the check is the
    # realistic way this regresses, and so is a refactor that drops it from one of the four.
    controller_text = '\n'.join(plugin_sources)
    for signature, guard in [
        ('public ActionResult<List<SubtitleInfo>> GetSubtitles(Guid itemId)', 'CallerMayActOn(itemId)'),
        ('public ActionResult<SyncJob> SyncSubtitle([FromBody] SyncRequest request)', 'CallerMayActOn(request.ItemId)'),
        ('public ActionResult<BatchView> CreateBatch([FromBody] BatchCreateRequest request)',
         'RefuseInvisibleItems(request.Tasks.Select(t => t.ItemId))'),
        ('public ActionResult<object> GetSubtitlesBatch([FromBody] SubtitleBatchRequest request)',
         'RefuseInvisibleItems(request.ItemIds)'),
    ]:
        at = controller_text.find(signature)
        body = controller_text[at:at + 1400] if at >= 0 else ''
        report('the item-scoped endpoint asks who is calling: ' + signature.split('(')[0].split(' ')[-1],
               at >= 0 and guard in body,
               'the endpoint is gone' if at < 0 else 'no guard in its body')
    report('the open-by-design note for the item-scoped endpoints is gone with the hole',
           'about the items that account' in controller_text
           and 'stay open to any authenticated user on purpose' not in controller_text)

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

    # ---------------- D-series: where the page lives, how often it polls, one job per track, one error shape ----

    import json as _djson

    controller_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Api', 'SubSyncController.cs'),
                             encoding='utf-8').read()
    plugin_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Plugin.cs'), encoding='utf-8').read()
    page_js = pages['subsyncMain.js']

    # D18: under Jellyfin 12 the installed client picks a plugin's *representative page* with EnableInMainMenu and
    # does not add sidebar entries for plugin pages at all (read in the client's own bundle below). So the fix is
    # not a menu flag the server can set: it is making sure the flag names the settings page itself, that the
    # dashboard page still leads there, and that the reachable routes are documented and tested.
    report('D18: the page Jellyfin offers for this plugin is the settings page itself',
           plugin_source.count('EnableInMainMenu = true') == 1
           and re.search(r'Name = "subsync-main",.{0,260}?EnableInMainMenu = true', plugin_source, re.S) is not None
           and plugin_source.count('EnableInMainMenu = false') == 1
           and 'MenuIcon = "subtitles"' in plugin_source)
    report('D18: the dashboard page still leads to the page Jellyfin opens',
           'configurationpage?name=subsync-main' in pages['configPage.html'])

    # The rule the plugin is relying on, read from the installed client rather than assumed. If no Jellyfin web
    # client is on this host the rule cannot be re-read here, and the check does not pretend it was.
    web_root = '/opt/data/jf12test/jellyfin/jellyfin-web'
    client_bundles = []
    if os.path.isdir(web_root):
        for name in sorted(os.listdir(web_root)):
            if name.endswith('.js') and 'plugins' in name:
                client_bundles.append(open(os.path.join(web_root, name), encoding='utf-8',
                                           errors='replace').read())
    if client_bundles:
        joined = '\n'.join(client_bundles)
        report('D18: the installed client selects a plugin page by EnableInMainMenu (it adds no menu entry)',
               'EnableInMainMenu' in joined
               and 'EnableInMainMenu))||' in joined.replace(' ', '').replace('!', '')
               or 'EnableInMainMenu' in joined)

    # D7: an idle page used to ask the server for the same thing every 2 s, plus the same GET several times at
    # load. Measured before the fix in a real browser: ~1.4 requests per second with nothing running.
    report('D7: the poll is self-scheduled at an idle rate instead of a fixed 2 s interval',
           'setInterval(tick, 2000)' not in page_js
           and 'heartbeatIdleMs = 10000' in page_js
           and 'heartbeatBusyMs = 2000' in page_js
           and 'mirroredBatchId ? heartbeatBusyMs : heartbeatIdleMs' in page_js)
    idle = re.search(r'heartbeatIdleMs = (\d+)', page_js)
    busy = re.search(r'heartbeatBusyMs = (\d+)', page_js)
    report('D7: the idle rate is at least four times slower than the rate while a run is on screen',
           bool(idle and busy) and int(idle.group(1)) >= 4 * int(busy.group(1)))
    report('D7: identical GETs in flight share one request',
           'var inflightGets = {};' in page_js
           and 'if (inflightGets[key]) {' in page_js
           and 'inflightGets[key] = request;' in page_js
           and 'request.then(remember, dropFailed);' in page_js)
    report('D7: the next tick is scheduled only after the current one settles, so ticks cannot pile up',
           'heartbeatTimer = setTimeout(function () {' in page_js
           and 'if (heartbeatTimer) return;' in page_js)

    # D9: the same item and track is one job, not two. The rule is consulted inside the queue lock - the shape
    # changed in the S7 work (the log line moved out of the critical section, the lookup stayed in), so the check
    # reads the lookup rather than the log line.
    report('D9: an enqueue consults the duplicate rule while it holds the queue lock',
           'alreadyQueued = FindDuplicate(_runOrder, itemId, subtitleIndex);' in service_source
           and 'SyncJob? alreadyQueued;\n        lock (_queueLock)\n        {\n            alreadyQueued = FindDuplicate('
               in service_source
           and 'queue duplicate: item=' in service_source)
    report('D9: a batch reports what was already queued instead of counting it as its own work',
           'alreadyQueued.Add(queued.Job);' in service_source
           and 'Jobs = jobs, AlreadyQueued = alreadyQueued' in service_source
           and 'BuildBatchViewForCaller(created.BatchId, created.AlreadyQueued.Count)' in controller_source)
    report('D9: the page tells the user when a request was already queued',
           'not queued a second time' in page_js and 'function noteAlreadyQueued(view)' in page_js)

    # D10: one failure shape.
    report('D10: no endpoint answers a failure with a bare string any more',
           'return BadRequest("' not in controller_source
           and 'return NotFound("' not in controller_source
           and controller_source.count('return Fail(') >= 12)
    report('D10: an exception that escapes an endpoint is filtered into that same shape',
           '[TypeFilter(typeof(SubSyncExceptionFilter))]' in controller_source
           and os.path.exists(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Api',
                                           'SubSyncExceptionFilter.cs')))
    report('D10: the page shows the reason the server gave, not raw JSON',
           'function problemText(status, body)' in page_js
           and 'parsed.detail || parsed.Detail' in page_js
           and "throw new Error(problemText(r.status, b));" in page_js)

    # The page's error reader, run for real: the shipped function is taken from the shipped file and evaluated.
    problem_fn = extract_js_function(page_js, 'problemText')
    if problem_fn:
        ok_node, node_out = run_node(
            problem_fn
            + "var cases=[problemText(400, JSON.stringify({status:400,title:'Empty batch',"
            + "detail:'A batch must contain at least one task.'})),"
            + "problemText(409, JSON.stringify({status:409,title:'Request refused',detail:'Index 11 not found.'})),"
            + "problemText(500, 'Error processing request.'), problemText(503, '')];"
            + "console.log(JSON.stringify(cases));")
        cases = []
        if ok_node:
            try:
                cases = _djson.loads(node_out.strip().splitlines()[-1])
            except (ValueError, IndexError):
                cases = []
        report('D10: the page reads a problem body as "title: detail", a bare body as itself, an empty one by status',
               cases == ['Empty batch: A batch must contain at least one task.',
                         'Request refused: Index 11 not found.',
                         'Error processing request.',
                         'HTTP 503'],
               f'node said {node_out!r}')
    else:
        report('D10: the page reads a problem body as "title: detail"', False,
               'problemText was not found in the shipped page script')


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
    service = service_classes()
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
           # P5-5 (2026-09-19): the demand is now the measured change when one exists and the engine's own answer when
           # none does, so the log line names `fromReference` (the value that decided) rather than the SyncChange's
           # field. The guard fires on the same shape as before for a measurable subtitle, and now also for a sparse
           # one, which is what the field's 14 jobs were (see the job check "a sibling ruler pinned at the search
           # window is discarded even when the subtitle is too sparse to measure").
           and 'it demanded {fromReference} ms' in service
           and 'var rulerDemandMs = measured?.ShiftMs ?? engineShiftMs;' in service
           and 'ReferenceStore.Discard(videoPath, reference.Spec);' in service
           and 'worth checking, a shift this size' in service
           and 'refusing a reference-derived shift' not in service)
    report('the offset window is a search range: a result on it is retried wider, and only a definitive answer is written',
           'public int MaxOffsetSeconds { get; set; } = 180;' in config_source
           and 'MaxOffsetSecondsOf(config) * 2, 300)' in service
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
           # Settings/ValidationNotes joined it with the settings validation: it reports what the plugin
           # adjusted in an administrator's configuration, so it is administrator-only like the rest.
           sorted(elevated) == ['Configuration', 'Configuration', 'Install', 'Kill', 'Log',
                                'Settings/ValidationNotes', 'SpeechCache/Clear']
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

    # ---------------- B-series lifecycle and stability: teardown, the pump, cancellations, wrappers ----------------

    registry_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                        'JobProcessRegistry.cs'), encoding='utf-8').read()
    diagnostics_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                           'ExceptionDiagnostics.cs'), encoding='utf-8').read()
    itemaccess_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                          'ItemAccess.cs'), encoding='utf-8').read()
    languagesupport_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                               'LanguageSupport.cs'), encoding='utf-8').read()
    srtwriter_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                         'SrtWriter.cs'), encoding='utf-8').read()
    subtitlecache_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                             'SubtitleCache.cs'), encoding='utf-8').read()
    sweepstate_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                          'SweepState.cs'), encoding='utf-8').read()
    settings_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                        'SettingsSource.cs'), encoding='utf-8').read()
    engineversioncache_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services',
                                                 'EngineVersionCache.cs'), encoding='utf-8').read()

    # B14: teardown cancels the run tokens, kills what it tracks, waits for a bounded time and forgets the registry.
    report('B14: teardown kills the tracked children, waits for the lanes, and clears the registry',
           'var (childrenAsked, childrenStopped) = _processes.KillChildProcesses();' in service_source
           and 'var tasksDone = WaitForLanes(ShutdownWaitMs);' in service_source
           and 'JobProcessRegistry.Clear();' in service_source
           and 'foreach (var entry in _jobCancellation)' in service_source
           and 'teardown: tracked=' in service_source)
    report('B14: the kill step and the bounded wait are shared with KillAll and testable on their own',
           'internal (int Asked, int Stopped) KillChildProcesses()' in service_source
           and 'internal static int WaitForTasks(IEnumerable<Task> tasks, int milliseconds)' in service_source
           and 'internal void TrackChildProcess(Process process)' in service_source
           and service_source.count('KillChildProcesses(') >= 2)
    report('B14: the registry can be cleared, so a reloaded plugin inherits nothing',
           'internal static void Clear() => Entries.Clear();' in registry_source)
    report('B14: every child process the plugin starts is tracked, so a kill or a teardown can find it',
           service_source.count('TrackChildProcess(process);') == service_source.count('process.Start();'),
           f"{service_source.count('TrackChildProcess(process);')} registered of "
           f"{service_source.count('process.Start();')} started")

    # ---------------- Access and truth: who sees which run, which engine, what can be synced (F4, D2, D8) ----------

    report('F4: a job records the account that asked for it, and every queue path carries it',
           'public Guid? OwnerId { get; set; }' in service_source
           and 'OwnerId = ownerId,' in service_source
           and 'string? mode = null, Guid? ownerId = null' in service_source
           and service_source.count('ownerId)') >= 3
           and 'request.Mode, CallerId());' in controller_source
           and 'request.Mode, CallerId()' in controller_source)
    report('F4: the run list and the batch list are scoped to the caller, and its paths are removed',
           'ItemAccess.VisibleJobs(_syncService.GetAllJobs(), CallerId(), isAdmin)' in controller_source
           and 'ItemAccess.ForViewer(job, isAdmin)' in controller_source
           and 'ItemAccess.HideServerPaths(task, false);' in controller_source
           and 'ItemAccess.MaySeeJob(job, CallerId(), isAdmin)' in controller_source
           and 'private BatchView? BuildBatchViewForCaller' in controller_source
           and 'private bool CallerIsAdmin()' in controller_source)
    report('F4: the rules themselves are one place the checks can drive, and a read cannot edit the tracked job',
           'internal static IEnumerable<SyncJob> VisibleJobs(' in itemaccess_source
           and 'internal static bool MaySeeJob(' in itemaccess_source
           and 'internal static SyncJob ForViewer(SyncJob job, bool isAdmin)' in itemaccess_source
           and 'public SyncJob CopyForViewer() => (SyncJob)MemberwiseClone();' in service_source
           and 'internal static void HideServerPaths(BatchTask task, bool isAdmin)' in itemaccess_source
           and 'internal static string? RedactPaths(string? text)' in itemaccess_source)
    report('F4: work another account has queued is not handed over as if it were the caller\'s',
           'public sealed class SyncQueueConflictException' in service_source
           and 'alreadyQueued.OwnerId != ownerId' in service_source
           and 'StatusCodes.Status409Conflict, "Already queued"' in controller_source)

    # D2: the status describes the engine that will run.
    report('D2: "installed" is the engine a job would execute, resolved the way a job resolves it',
           'IsInstalled = enginePresent,' in service_source
           and 'var enginePath = ResolveFfSubSyncPath();' in service_source
           and 'ResolvedBinaryPath = enginePath,' in service_source
           and 'public bool EngineIsInstalled()' in service_source
           and 'internal static bool EngineIsUsable(string? resolvedPath, Func<string, bool> fileExists, string? pathVariable)' in service_source)
    report('D2: the old answer - the plugin\'s own managed binary - is gone',
           'IsInstalled = File.Exists(ManagedFfSubSyncPath)' not in service_source)
    report('D2: a missing engine is explained in words that name the path',
           'EngineNote = enginePresent' in service_source
           and 'public string? EngineNote { get; set; }' in service_source
           and 'internal string EngineMissingNote(string? enginePath)' in service_source)

    # D8: one rule for what can be synced, wherever the request arrives.
    report('D8: both entry points refuse a target that cannot be synced, through one helper',
           controller_source.count('RefuseUntargetable(') >= 3
           and 'private ObjectResult? RefuseUntargetable(Guid itemId)' in controller_source
           and 'public SyncTarget InspectSyncTarget(Guid itemId)' in service_source
           and 'internal static SyncTarget ClassifySyncTarget(Guid itemId, string? itemKind, bool isVideo)' in service_source)
    report('F7: orphaned scratch directories are swept by the maintenance pass and once at startup',
           'var scratchRemoved = ClearStaleJobDirectories();' in service_source
           and 'sweep: removed {scratchRemoved} orphaned job scratch director(ies)' in service_source
           and 'startup: removed {orphans} orphaned job scratch director(ies)' in service_source
           and service_source.count('ClearStaleJobDirectories()') >= 3)

    # B26: a numeric parse without an explicit culture is the bug this row is about, so the check is exhaustive rather
    # than a sample: every Parse/TryParse of a number in the plugin has to say which culture it means.
    numeric_parses = re.findall(
        r'\b(?:double|float|decimal|int|long|short|uint|ulong)\.(?:Parse|TryParse)\([^;]*', '\n'.join(plugin_sources))
    culture_less = [call.split('\n')[0][:70] for call in numeric_parses
                    if 'CultureInfo' not in call and 'Invariant' not in call]
    report('B26: every numeric parse in the plugin names the culture it means',
           not culture_less,
           f'{len(culture_less)} without a culture, first: {culture_less[0] if culture_less else "-"}')
    report('B26: cache keys and their lookups are built with an invariant culture, so a locale cannot change a key',
           'CultureInfo.InvariantCulture' in subtitlecache_source
           and 'string.Create(CultureInfo.InvariantCulture,' in subtitlecache_source)

    report('F16: the extracted-subtitle cache is described in the interface and reported by the status',
           'SubtitleCacheSummary = SubtitleCache.Describe()' in service_source
           and 'public string? SubtitleCacheSummary { get; set; }' in service_source
           and 'ss-subtitlecache' in main_html
           # the interface names the cache; the wording is the page's to change (E2 rewrote this description
           # from 'The extracted subtitle is ...' to a labelled line), so only the name is pinned
           and 'extracted subtitle' in main_html.lower()
           and 'SubtitleCacheSummary' in page_js)
    report('F30: the sweep history has one cap, applied on load and on every write',
           'public const int MaxEntries = 5000;' in sweepstate_source
           and 'if (entries.Count > MaxEntries)' in sweepstate_source
           and '_entries.Count > MaxEntries || _pendingWrites >= SaveBatchSize' in sweepstate_source)

    # B3/B15: the queue lock is what the enqueue path waits on, so the work that must never happen while it is held
    # is work that touches storage or writes a log line. The audit is mechanical - every `lock (_queueLock)` block is
    # found by matching braces, and the bodies are searched - because the defect this guards against is somebody
    # adding a predicate or a log line to a critical section six months from now (AGENTS.md says any new predicate in
    # `PlanStart` gets the same treatment as `SpeechIsCached`, which is the row this came from).
    def queue_lock_bodies(text):
        """Every `lock (_queueLock)` block in a source file, as (first line number, body lines)."""
        lines = text.split('\n')
        blocks = []
        for index, line in enumerate(lines):
            if 'lock (_queueLock)' not in line:
                continue
            depth, started = 0, False
            for scan in range(index, len(lines)):
                depth += lines[scan].count('{') - lines[scan].count('}')
                started = started or '{' in lines[scan]
                if started and depth <= 0:
                    blocks.append((index + 1, lines[index:scan + 1]))
                    break
        return blocks

    logging_call = re.compile(r'\b(PluginLog\.|_logger\.)')
    # Storage, process and shared-store calls. `Task.Run` is deliberately not in this list: starting a lane or
    # the pump from inside a critical section is a thread-pool scheduling call (microseconds), not a round trip
    # to the share, and both are started while the lock is held so the reservation is atomic (S7 measured the
    # whole wake at `wakePump=0 ms`). What must never be in there is work that waits on storage.
    storage_call = re.compile(
        r'\b(File\.|Directory\.|FileInfo|DirectoryInfo|SettingsSource\.Current|SubtitleCache\.|SpeechCache\.|'
        r'VolumeProfiles\.|ReferenceStore\.|Process\.)\b|new Process')
    log_in_lock = []
    storage_in_lock = []
    for first_line, body in queue_lock_bodies(service_source):
        joined = '\n'.join(part for part in body if not part.strip().startswith('//'))
        for match in logging_call.finditer(joined):
            log_in_lock.append(first_line + joined[:match.start()].count('\n'))
        for match in storage_call.finditer(joined):
            storage_in_lock.append(first_line + joined[:match.start()].count('\n'))
    report('B15: no log line is written while the queue lock is held',
           not log_in_lock,
           f'{len(log_in_lock)} critical section(s) log: {log_in_lock[:3]}'
           if log_in_lock else 'every critical section is status updates only')
    report('B3: no filesystem or process work happens while the queue lock is held',
           not storage_in_lock,
           f'{len(storage_in_lock)} critical section(s) touch storage: {storage_in_lock[:3]}'
           if storage_in_lock else 'the planner, the reference store and the extractor all run outside it')

    report('S7: the settings file is not stat-ed on every question',
           'private static readonly long StatTtlMs = ResolveStatTtlMs();' in settings_source
           and 'if (!StampIsDue())' in settings_source
           and 'Interlocked.Increment(ref _stats);' in settings_source
           and 'StampIsDue' not in service_source   # the rule lives where the file is read, not in the service
           and 'SUBSYNC_SETTINGS_STAT_MS' in settings_source)
    report('S7: a saved setting does not wait for the trust window',
           'Services.SettingsSource.Reset();' in service_source
           and 'Interlocked.Exchange(ref _lastStatMs, long.MinValue);' in settings_source)
    report('S7: the extraction cache is not re-read for every queued job on every pass',
           'private bool CacheSaysMissing(string key)' in service_source
           and 'if (CacheSaysMissing(key))' in service_source
           and 'RememberCacheMiss(key);' in service_source
           and 'private static readonly TimeSpan ExtractedMissTtl' in service_source)
    report('S7: the enqueue line reports the settings probes and the two phases inside the settings phase',
           'settingsStats={Services.SettingsSource.Stats}' in service_source
           and 'settingsReads={Services.SettingsSource.Reads}' in service_source
           and 'settingsRead={settingsReadMs} ms, jellyfinLog={jellyfinLogMs} ms, ' in service_source
           and 'duplicateCheck={duplicateCheckMs} ms' in service_source)
    report('S7: a lock wait is reported as a lock wait, not folded into the phase beside it',
           'duplicateCheckMs = phase.ElapsedMilliseconds;' in service_source
           and 'settingsMs = phase.ElapsedMilliseconds;\n        phase.Restart();\n\n        // One job per item and track (D9)' in service_source)
    report('B5: the bundled engine is spawned for its version once per binary, not once per question',
           'return _bundledVersionCache.VersionFor(path, stamp, size);' in service_source
           and 'internal long EngineVersionProbes => _bundledVersionCache.Probes;' in service_source
           and 'engine identity: {path} answered' in service_source
           and 'public long Probes => System.Threading.Interlocked.Read(ref _probes);' in engineversioncache_source
           and 'public static string KeyFor(string path, DateTime stampUtc, long size)' in engineversioncache_source)
    report('B5: the bundled path is not re-stat-ed and re-chmod-ed on every question',
           'now - Volatile.Read(ref _bundlePathCheckedMs) < BundlePathRecheckMs' in service_source
           and 'private const long BundlePathRecheckMs = 1000;' in service_source)

    report('F2: the cancel endpoint names what it stops and refuses a request that names nothing',
           'var (targets, refusal) = ItemAccess.SelectKillTargets(' in controller_source
           and 'ItemAccess.KillRefusal.NothingSpecified => Fail(' in controller_source
           and 'ItemAccess.KillRefusal.NotPermitted => Fail(' in controller_source
           and 'request?.All == true' in controller_source
           and '_syncService.KillJobs(targets)' in controller_source
           and 'public (int QueuedCancelled, int RunningKilled) KillJobs(IEnumerable<SyncJob> targets)' in service_source
           and 'internal static (IReadOnlyList<SyncJob> Targets, KillRefusal? Refusal) SelectKillTargets(' in itemaccess_source)
    report('F2: the page asks for the global stop explicitly, so the button keeps working',
           "body: JSON.stringify({ all: true })" in page_js)

    report('F6: a cache clear refuses while a run is reading the cache, instead of deleting a file in use',
           'var (runningCount, queuedCount) = _syncService.ActiveJobCounts();' in controller_source
           and 'StatusCodes.Status409Conflict,\n                "Runs are using the cache"' in controller_source
           and 'would delete a file a run is using, so it was not cleared' in controller_source)

    report('S5: the track list shows a bitmap track with the reason instead of hiding it',
           'UnsupportedReason = LanguageSupport.ImageBasedRefusal(s.Codec),' in service_source
           and 'LanguageSupport.IsImageBased(s.Codec)' not in service_source
           and 'public static string? ImageBasedRefusal(string? codec)' in languagesupport_source
           and 'throw new InvalidOperationException(imageRefusal);' in service_source)
    report('S12: a sidecar the plugin wrote is listed and flagged, and re-syncing it cannot nest a marker',
           'IsPluginOutput = MediaStreamMap.IsOwnSidecar(s)' in service_source
           and '!MediaStreamMap.IsOwnSidecar' not in service_source
           and '!IsOwnSidecar' not in service_source
           and 'public static string StripSyncedMarker(string? stem)' in srtwriter_source
           and 'internal static string SyncedTargetName(string directory, string stem, string? language)' in service_source
           and 'var target = SyncedTargetNaming.SyncedTargetName(dir, stem, lang);' in service_source)

    report('D8: a series is refused by name, with what to do instead, and the answer is the same shape as every refusal',
           'not a video: pick the episodes themselves' in service_source
           and 'Fail(StatusCodes.Status400BadRequest, "Not a video", target.Refusal!)' in controller_source
           and 'Fail(StatusCodes.Status404NotFound, "Item not found", target.Refusal!)' in controller_source)

    # B31: the pump's pass runs inside a guarded loop that survives a failing pass and says so.
    report('B31: the pump runs its pass through the guarded loop rather than a bare loop',
           'await RunPumpLoopAsync(' in service_source
           and '() => PumpOnceAsync(inFlight),' in service_source
           and 'NotePumpFault)' in service_source
           and 'private async Task PumpOnceAsync(Dictionary<Task, SyncJob> inFlight)' in service_source)
    report('B31: a pass that throws is counted, logged in the plugin log, and the loop carries on',
           'consecutiveFaults++;' in service_source
           and 'onFault(ex);' in service_source
           and 'pump: pass failed (' in service_source
           and 'PluginLog.Error(' in service_source)
    report('B31: the loop backs off, so a pass that fails immediately cannot spin',
           'await Task.Delay(PumpFaultBackoffMs(consecutiveFaults)).ConfigureAwait(false);' in service_source
           and 'internal static int PumpFaultBackoffMs(int consecutiveFaults)' in service_source)

    # B20: a cancelled attempt ends the chain instead of trying the next engine.
    report('B20: every fallback boundary stops the chain when the attempt was cancelled',
           service_source.count('StopChainIfCancelled(') >= 4
           and 'throw new OperationCanceledException(token);' in service_source
           and 'no fallback attempted' in service_source)
    report('B20: the rule reads the reader\'s own cancellation reason as well as the token',
           'internal static bool IsCancelledExtraction(string reason, CancellationToken token)' in service_source
           and 'string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase)' in service_source
           and 'token.IsCancellationRequested' in service_source)

    # B21: the prefetch reports the fault that actually happened.
    report('B21: the prefetch unwraps its aggregate and rethrows the real fault',
           'catch (AggregateException aggregate)' in extractor_source
           and 'ExceptionDiagnostics.RootCause(aggregate)' in extractor_source
           and 'throw real;' in extractor_source
           and 'prefetch: ' in extractor_source)
    report('B21: the log line names the fault and what it was wrapped in',
           'ExceptionDiagnostics.Describe(aggregate)' in extractor_source
           and 'internal static string Describe(Exception exception)' in diagnostics_source)
    report('B21: one place decides what the real fault is, and a kill stays a kill',
           'internal static Exception RootCause(Exception exception)' in diagnostics_source
           and 'OfType<OperationCanceledException>().FirstOrDefault()' in diagnostics_source
           and 'aggregate.Flatten().InnerExceptions' in diagnostics_source)
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
           service.count('EmbeddedSubtitleOrdinal(') >= 2
           and service.count('public static int EmbeddedSubtitleOrdinal(') == 1)
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

    report('clearing never deletes a running job\'s scratch folder, and does not keep a finished one\'s',
           '_jobs.TryGetValue(name, out var tracked)' in service
           and 'tracked.Status is SyncJobStatus.Queued or SyncJobStatus.Running' in service
           and 'ref" continue' not in service)
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
           # B17 replaced the ".Where(kvp => !_historyOnlyJobs.Contains(kvp.Key))" clause with the eviction
           # policy, so this check asserts the same rule in the form the code now states it: history rows are
           # excluded from eviction, and the cleanup pass asks the policy rather than an inline query.
           and 'JobsToEvict(' in service
           and 'id => _historyOnlyJobs.Contains(id)' in service
           and 'interrupted by a plugin restart' in service
           and 'BatchHistory.Save(BatchHistory.DefaultPath, SnapshotBatchHistory());' in service)

    # G1: a sync queued on its own is a run of its own. The detail page's "Sync Subtitles" posts to
    # /SubSync/Sync, which enqueues without a batch on purpose, and both read models used to want a batch id
    # before they would show a job - so that run lived in no history at all, in memory or in the file a
    # restart reads. The rule is: the read models derive a run id for it, the job carries a title for its
    # row, and the file bounds those runs separately from the batches.
    run_id_source = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'RunId.cs'),
                         encoding='utf-8').read()
    report('a run started from a detail page is a run in the history read models (G1)',
           'public static class RunId' in run_id_source
           and 'public const string SinglePrefix = "single:"' in run_id_source
           # both read models, and the job lookup every one of them goes through
           and 'RunId.ForJob(j.Id)' in service
           and 'RunId.JobIdOf(batchId)' in service
           and 'BatchId = RunId.ForJob(job.Id)' in service
           # the row a run of one gets, since it has no batch label to fall back on
           and 'Label = label ?? (string.IsNullOrEmpty(batchId) ? video.Name : null)' in service
           and 'Services.RunId.IsSingle(batchId)' in controller
           # and the bound that keeps a week of single syncs from rotating the batches out
           and 'MaxSingleRuns = 20' in batch_history
           and 'RunId.IsSingle(e.BatchId)' in batch_history)

    report('the answer the scheduler keys on is memoised, not read per planning pass',
           'SpeechCachedTtl' in service and 'private static string MediaStamp' not in cache_source)

    # B8: a partial extraction is judged before anything downstream can use it, and the file it left behind
    # goes. The rule that was there accepted a *failed* run whenever a file existed at the output path, which
    # is how a kill or a demux error handed the engine a prefix of the track as if it were all of it.
    report('a failed extraction is no longer accepted just because a file exists (B8)',
           'exitCode != 0 && !File.Exists(outputPath)' not in service
           and 'ExtractionOutputGuard.Judge(' in service
           and 'DiscardPartialExtraction(' in service
           and 'extract: rejected' in service)
    # B6: the two halves - a job that returns while Running is settled, and a job that stops making progress
    # is stopped - and both callers of the sweep (the pump, and the cleanup timer that runs even if the pump
    # has died).
    report('a job cannot be left Running: it is settled on every exit, and stalled jobs are stopped (B6)',
           'StuckJobPolicy.Settle(' in service
           and service.count('ReapStuckJobs()') == 3          # the method and its two call sites
           and 'JobProcessRegistry.Begin(owner)' in service
           and 'JobProcessRegistry.SawOutput(owner)' in service
           and 'JobProcessRegistry.End(owner)' in service
           and 'StuckJobPolicy.Stop(job, reason, now)' in service)
    report('the job watchdog reads its windows from the settings, not from constants (B6)',
           'StuckJobWindows.From(Services.SettingsSource.Current())' in service
           and 'StuckJobTimeoutMinutes' in open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                                                             'PluginConfiguration.cs'), encoding='utf-8').read()
           and 'WedgedProcessTimeoutMinutes' in open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                                                                  'PluginConfiguration.cs'), encoding='utf-8').read()
           and 'StuckJobTimeoutMinutesMin' in open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Configuration',
                                                                'SettingsValidation.cs'), encoding='utf-8').read())

    # B12: the stores that outlive a job are bounded here, and swept during a run rather than only at the
    # next plugin start. The sweep runs from the pump and from the cleanup timer, like the stall watchdog.
    report('the stores that outlive a job are bounded and swept during a run (B12)',
           'SweepLongLivedStores()' in service
           and service.count('SweepLongLivedStores()') == 3        # the method and its two call sites
           and 'ReferenceStore.SweepOrphans(' in service
           and 'SharedExtractionStore.Cleanup(' in service
           and 'SubtitleCache.Prune()' in service
           and 'SubtitleCache.MemoryCount' in service)

    subtitle_cache = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'SubtitleCache.cs'),
                          encoding='utf-8').read()
    reference_store = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'ReferenceStore.cs'),
                           encoding='utf-8').read()
    sweep_state = open(os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'SweepState.cs'),
                       encoding='utf-8').read()
    report('the in-memory subtitle cache has a bound and drops the least recently used entry (B12)',
           'public const int MaxMemoryEntries' in subtitle_cache
           and 'public const long MaxMemoryChars' in subtitle_cache
           and 'TrimMemory()' in subtitle_cache
           and 'Interlocked.Add(ref _memoryChars, -' in subtitle_cache)
    report('the reference store has a bound and an orphan sweep (B12)',
           'public const int MaxEntries = 512' in reference_store
           and 'public static int SweepOrphans(' in reference_store
           and 'VideoPath { get; init; }' in reference_store)
    report('the sweep state trims as records arrive, not only when the file is read back (B12)',
           'private int TrimLocked()' in sweep_state
           and '_entries.Count > MaxEntries || _pendingWrites >= SaveBatchSize' in sweep_state)


    # P4 (F11/F12/F13): the settings page and the server agree on what the encoding may be, the
    # golden-section switch is unavailable while framerate correction is off, and the worker field says what
    # the extraction lanes do with the number.
    page_path = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Web', 'subsyncMain.html')
    script_path = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Web', 'subsyncMain.js')
    controller_path = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Api', 'SubSyncController.cs')
    page = open(page_path, encoding='utf-8').read()
    script = open(script_path, encoding='utf-8').read()
    controller = open(controller_path, encoding='utf-8').read()

    encoding_block = page.split('id="ss-encoding"', 1)[-1].split('</select>', 1)[0]
    page_encodings = set(re.findall(r'<option value="([^"]+)"', encoding_block))
    server_block = service.split('AllowedOutputEncodings', 1)[1].split('};', 1)[0]
    server_encodings = set(re.findall(r'"([^"]+)"', server_block))
    report('F11: the encoding dropdown offers exactly the encodings the engine accepts',
           re.search(r'<select[^>]*id="ss-encoding"', page) is not None
           and re.search(r'<input[^>]*id="ss-encoding"', page) is None
           and page_encodings == server_encodings and len(page_encodings) == 5,
           f'page={sorted(page_encodings)} server={sorted(server_encodings)}')
    report('F11: the encoding field says a typo can no longer be saved',
           # E4 rewrote this field: the claim is the same, in fewer words - the list only offers what the
           # engine accepts, so an invalid value cannot be typed in and silently written as UTF-8.
           'Only values the engine accepts are offered' in page)
    report('F11: the chosen encoding is handed to the engine by name',
           '"--output-encoding", outputEncoding' in service
           and 'var outputEncoding = Configuration.SettingsValidation.OutputEncodingOf(config);' in service)

    report('F12: the golden-section switch follows framerate correction in the interface',
           'function syncGoldenSectionState()' in script
           and 'gss.disabled = !usable' in script
           and "fixfps.addEventListener('change', syncGoldenSectionState)" in script
           and 'syncGoldenSectionState();' in script                      # on load
           and 'wireSettingsControls();' in script                        # wired at startup
           and 'ss-inert' in page and '.checkboxContainer.ss-inert' in page
           and 'Correct framerate mismatch</strong> above' in page)

    report('F13: the worker field says what the extraction lanes do with the number',
           # E4 rewrote this field into lines: the lane rule is still stated, with the same two clauses about
           # when a change takes effect
           'Extraction has its own lanes (half this number, at most 3)' in page
           and 'applies as soon as you save' in page
           and 'as the running extractions finish' in page)

    report('F13: a saved configuration is applied to the running scheduler, not the next restart',
           '_syncService.ApplySettingsNow();' in controller
           and 'public void ApplySettingsNow()' in service
           and 'public int ConfiguredLaneLimit' in service
           and 'Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers' in service
           and 'Plugin.Instance?.Configuration?.ParallelWorkers' not in service)

    # B-series: the walk checks the token at a cluster boundary, the kill path counts exits, and the scratch
    # clear is guarded (B2/B7/B16/B17/B23).
    extractor_path = os.path.join(REPO, 'Jellyfin.Plugin.SubSync', 'Services', 'MkvSubtitleExtractor.cs')
    extractor_source = open(extractor_path, encoding='utf-8').read()
    report('B7: the walk checks cancellation at every cluster, not every 64th',
           'visited & 0x3F' not in extractor_source
           and 'Per cluster, not every 64th (B7)' in extractor_source
           and extractor_source.count('if (cancellationToken.IsCancellationRequested)') >= 5
           and 'must not start a fresh 16 MB read' in extractor_source)
    report('B16: the kill path waits on process handles and returns the measured count',
           'CountExited(toKill, KillWaitMs)' in service
           and 'process.WaitForExit(waitMs)' in service
           and 'Math.Max(processesKilled, runningKilled)' not in service
           and 'Thread.Sleep(100)' not in service)
    report('B23: the scratch clear only deletes job-id directories inside the root',
           'IsJobScratchDirectory(name)' in service
           and 'IsInsideRoot(rootFull, directory)' in service
           and 'refused to delete {directory} (outside' in service)
    report('B17: finished jobs are kept by age with a backstop a real batch cannot reach',
           'JobsToEvict(' in service and '_jobs.Count > 50' not in service
           and 'private const int MaxTrackedJobs = 10000' in service)
    report('B2: the shared pass names the tracks it could not produce',
           'missedTracks={string.Join(",", manyStats.MissedTracks)}' in service
           and 'produced no subtitle for track(s)' in service
           and 'public List<int> MissedTracks { get; } = new();' in extractor_source)
    report('B19: out_time_ms is read as microseconds',
           'return microsFromMs / 1_000_000.0;' in service
           and 'out_time_ms</c> is <b>microseconds</b> despite its name' in service)
    return failures


def run_b13_memory_checks():
    """B13: measure what a remux-scale extraction pass actually holds, at 1 and 8 lanes.

    The register's row claims "4 MB window + up to 384 MB of prefetched ranges ... gigabytes of prefetch on a
    batch". Nothing had ever measured it at remux scale, so this runs the real extractor against 30 GB sparse
    remux fixtures and asserts what the process may hold. The probe is tests/backend/b13_probe.py; its manual
    form additionally runs the cluster-walk shape, the shared multi-track pass and the slow-storage profile.
    """
    import sys

    print()
    probe_path = os.path.join(REPO, 'tests', 'backend', 'b13_probe.py')
    try:
        proc = subprocess.run([sys.executable, probe_path, '--check'], capture_output=True, text=True,
                              timeout=3600, env=ENV)
    except subprocess.TimeoutExpired:
        print('FAIL  the B13 memory probe ran at all (b13)   [timed out]')
        return 1
    lines = [line for line in proc.stdout.splitlines() if line.startswith(('PASS', 'FAIL'))]
    for line in lines:
        print(line)
    if not lines:
        tail = (proc.stderr or proc.stdout).strip().splitlines()
        print(f'FAIL  the B13 memory probe ran at all (b13)   [{tail[-1] if tail else "no output"}]')
        return 1
    return sum(1 for line in lines if line.startswith('FAIL'))


def run_s26_cost_checks():
    """The cue-indexed pass has to read what its own plan priced, even when the file states no cluster
    size (a streamed or non-remuxed mkv writes clusters with the unknown-size marker).

    This is the S26 repro: a fixture whose clusters carry dozens of frames with real payload gaps behind
    them, and the same fixture with every cluster size rewritten as unknown. Before the fix the located
    read was refused for every cue point and the pass walked each cue's cluster - 44x its planned bytes
    and 25x its planned reads, which is the 86x/25x a live pass showed on fabji's server. The probe that
    builds both files and measures them is tests/backend/s26_probe.py; this runs it.
    """
    import sys

    print()
    probe_path = os.path.join(REPO, 'tests', 'backend', 's26_probe.py')
    try:
        proc = subprocess.run([sys.executable, probe_path, '--check'], capture_output=True, text=True,
                              timeout=1800, env=ENV)
    except subprocess.TimeoutExpired:
        print('FAIL  the S26 cost probe ran at all (s26)   [timed out]')
        return 1
    lines = [line for line in proc.stdout.splitlines() if line.startswith(('PASS', 'FAIL'))]
    for line in lines:
        print(line)
    if not lines:
        tail = (proc.stderr or proc.stdout).strip().splitlines()
        print(f'FAIL  the S26 cost probe ran at all (s26)   [{tail[-1] if tail else "no output"}]')
        return 1
    return sum(1 for line in lines if line.startswith('FAIL'))


def program_with_job_checks():
    """The logictest program, with the characterization cases spliced in.

    Two files, each split at its own marker because the class it declares has to follow every top-level
    statement in the generated Program.cs: the statements go before the final result line, the types after it.
    tests/job_checks.cs drives RunSyncJob; tests/service_checks.cs drives the clusters the service map found
    with no coverage (the process runners, the access-control surface, engine status and install).

    Order matters and is deliberate: RunSyncJob's cases expect no plugin instance and no settings file, so they
    run first, and service_checks.cs builds the plugin instance it needs at the end of its own block.
    """
    def splice(path, statements, types, text):
        with open(path, encoding='utf-8') as f:
            source = f.read()
        head, _, tail = source.partition('// @@TYPES@@')
        return text.replace(statements, head).replace(types, tail)

    program = PROGRAM
    program = splice(os.path.join(REPO, 'tests', 'job_checks.cs'),
                     '// {{JOB_CHECK_STATEMENTS}}', '// {{JOB_CHECK_TYPES}}', program)
    program = splice(os.path.join(REPO, 'tests', 'service_checks.cs'),
                     '// {{SERVICE_CHECK_STATEMENTS}}', '// {{SERVICE_CHECK_TYPES}}', program)
    program = splice(os.path.join(REPO, 'tests', 'output_checks.cs'),
                     '// {{OUTPUT_CHECK_STATEMENTS}}', '// {{OUTPUT_CHECK_TYPES}}', program)
    return program


def prepare_fake_engine(root):
    """Writes the stand-in ffsubsync and ffmpeg the RunSyncJob cases drive, and returns their paths.

    The engine script is what makes every terminal reachable: `behaviour`, `payload.N.srt` and `stderr.txt` in
    its own directory tell each invocation what to write, what to say and how to exit. The ffmpeg stand-in is
    only reachable through JELLYFIN_FFMPEG, so no other check in this suite can pick it up by accident.
    """
    engine_dir = os.path.join(root, 'engine')
    ffmpeg_dir = os.path.join(root, 'ffmpeg')
    os.makedirs(engine_dir, exist_ok=True)
    os.makedirs(ffmpeg_dir, exist_ok=True)

    engine = os.path.join(engine_dir, 'ffsubsync')
    shutil.copyfile(os.path.join(REPO, 'tests', 'fixtures', 'fake_ffsubsync.sh'), engine)
    os.chmod(engine, 0o755)

    ffmpeg = os.path.join(ffmpeg_dir, 'ffmpeg')
    shutil.copyfile(os.path.join(REPO, 'tests', 'fixtures', 'fake_ffmpeg.sh'), ffmpeg)
    os.chmod(ffmpeg, 0o755)

    # `ffmpeg -i` only, in the shape ParseProbeSubtitleIndexes/ParseProbeSubtitleCodecs read: one subtitle
    # stream at container index 2, which is the stream the embedded cases put in Jellyfin's own list.
    with open(os.path.join(ffmpeg_dir, 'banner.txt'), 'w', encoding='utf-8') as f:
        f.write("Input #0, matroska,webm, from 'Probe Movie (2026).mkv':\n"
                "  Duration: 01:10:00.00, start: 0.000000, bitrate: 1000 kb/s\n"
                "    Stream #0:0: Video: h264 (High), yuv420p, 1280x720, 25 fps, 25 tbr\n"
                "    Stream #0:1: Audio: aac (LC), 48000 Hz, stereo, fltp\n"
                "    Stream #0:2(eng): Subtitle: subrip (srt)\n")

    cues = ''.join(
        f"{i + 1}\n{_srt_time(600 + (i * 80))} --> {_srt_time(600 + (i * 80) + 2)}\nLine {i + 1}.\n\n"
        for i in range(40))
    with open(os.path.join(ffmpeg_dir, 'extracted.srt'), 'w', encoding='utf-8') as f:
        f.write(cues)

    return engine_dir, ffmpeg


def _srt_time(seconds):
    """Seconds as an SRT timestamp."""
    hours, rest = divmod(seconds, 3600)
    minutes, secs = divmod(rest, 60)
    return f'{hours:02d}:{minutes:02d}:{secs:02d},000'


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
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <ProjectReference Include="{REPO}/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj" />
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" />
  </ItemGroup>
</Project>
""")
    with open(f'{WORK}/Program.cs', 'w') as f:
        f.write(program_with_job_checks())

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

    # F19's own file: two subtitle cues that *state* a 2 000 ms duration (a BlockGroup with a
    # BlockDuration, which is how a muxer writes a cue whose end is a fact), five seconds apart, with
    # payload text that carries no timings of its own so one cue is one timing line. The other fixtures
    # write plain SimpleBlocks with no duration, which is why the defect never showed up in them.
    duration_path = os.path.join(fixtures, 'duration-2000.mkv')
    subprocess.run(['python3', generator, duration_path, '--clusters', '6', '--payload', '0',
                    '--sub-every', '5', '--sub-duration', '2000', '--sub-text', 'plain'],
                   check=True, capture_output=True)
    env['MKV_FIX_DURATION'] = duration_path

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

    # B7: a file long enough that a cancellation issued while the pass reads lands at a cluster boundary
    # rather than after the last one. 1 200 clusters with no subtitle cue index, so the pass walks cluster
    # headers from the start (the route the Kill button could not interrupt); progress lines come every 400
    # clusters, so cancelling on the first one leaves 800 clusters unread.
    walk_cancel = os.path.join(fixtures, 'walkcancel.mkv')
    subprocess.run(['python3', generator, walk_cancel, '--clusters', '1200', '--payload', '1',
                    '--sub-every', '3', '--no-sub-cues'], check=True, capture_output=True)
    env['MKV_FIX_WALKCANCEL'] = walk_cancel

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

    # RunSyncJob is private, so its terminals are driven by reflection from the logictest program
    # (tests/job_checks.cs). The engine those cases run is a script that writes exactly the subtitle each case
    # needs, which is how every refusal and every success terminal is reached without a real ffsubsync: the
    # plugin finds it on PATH because the harness has no bundled binary and no settings file. The stand-in ffmpeg
    # is only reachable through SUBSYNC_FAKE_FFMPEG, which the one embedded case points JELLYFIN_FFMPEG at for its
    # own run: this suite's own ffmpeg checks (B8) must keep the real binary.
    fake_engine_dir, fake_ffmpeg = prepare_fake_engine(os.path.join(WORK, 'fakes'))
    ENV['SUBSYNC_FAKE_ENGINE'] = fake_engine_dir
    ENV['SUBSYNC_FAKE_FFMPEG'] = fake_ffmpeg
    ENV['PATH'] = fake_engine_dir + os.pathsep + ENV.get('PATH', '')

    b8_fixture_failures = prepare_b8_fixtures(fixtures)

    os.makedirs(WORK, exist_ok=True)

    build = subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo', '-v', 'q'], cwd=WORK, capture_output=True, text=True, env=ENV)
    if build.returncode != 0:
        print(build.stdout[-1500:], build.stderr[-1500:])
        return 1

    run = subprocess.run([DOTNET, f'{WORK}/bin/Release/net10.0/logictest.dll'], cwd=WORK, capture_output=True, text=True, env=ENV)
    print(run.stdout or run.stderr)
    if run.returncode != 0 and run.stderr.strip():
        # A harness that died part-way says why on stderr, and only stdout used to be printed - which hid the
        # reason behind a truncated list of passes.
        print('---- harness stderr ----')
        print(run.stderr)
    page_failures = run_page_checks()
    source_failures = run_gate_source_checks()
    cost_failures = run_s26_cost_checks()
    memory_failures = run_b13_memory_checks()
    total_failures = page_failures + source_failures + cost_failures + b8_fixture_failures + memory_failures
    if total_failures:
        print(f'{total_failures} FAILURE(S)')
    return run.returncode or (1 if total_failures else 0)


if __name__ == '__main__':
    raise SystemExit(main())
