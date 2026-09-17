#!/usr/bin/env python3
"""B13 memory probe: what does one extraction pass (and eight of them) actually hold?

The register's claim (B13) is "per-reader memory: 4 MB window + up to 384 MB of prefetched ranges; with a
worker pool that is gigabytes of prefetch on a batch, which is an out-of-memory kill in the middle of a run".
The Phase 4 triage measured 0,04-0,08 MB retained after an extraction and 0,35 MB across 299 prefetched
ranges on a 300-cluster 6-track pass - i.e. the row's figure has never been observed, but nothing has ever
measured it at *remux scale* either, which is the scale the row is about.

So this probe puts the real extractor in front of remux-shaped sparse fixtures (30 GB apparent, 6 000
clusters, 2 000 subtitle blocks per track - generated in under two seconds and costing ~300 MB of disk
because the video payload is a hole) and measures, per lane and for the process as a whole:

  * peak managed heap while the passes run (sampled every 10 ms),
  * peak working set and private bytes,
  * what the pass itself reports: planned bytes, fetched bytes/ranges (what the reader is holding),
    bytes read, read calls, and the window it chose.

Runs at 1 and 8 lanes and under two storage profiles: local disk, and the rig's slow-storage shim at the
numbers measured on the user's share (10 ms per read round trip). Slow storage is the interesting profile:
the window and the merge gap are sized from what reads cost, so that is where the fetch is largest.

Usage:
    python3 tests/backend/b13_probe.py                     # 1 and 8 lanes, local disk
    python3 tests/backend/b13_probe.py --slow               # the same under the shim
    python3 tests/backend/b13_probe.py --lanes 1 3 8        # a sweep
    python3 tests/backend/b13_probe.py --check              # the suite's PASS/FAIL form (asserts a ceiling)
    python3 tests/backend/b13_probe.py --keep               # leave the fixtures for inspection

Run ONE of these at a time: the probe builds its fixtures when they are missing, so two probes started
together write and read the same files.
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys

from _dotnet import find_dotnet

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = pathlib.Path(os.environ.get('TESTS_WORK') or REPO / '.tests-work')
DOTNET = find_dotnet()
GENERATOR = REPO / 'tests' / 'fixtures' / 'make_remux.py'
HARNESS = WORK / 'b13-probe'
FIXTURES = WORK / 'b13-fixtures'
EVIDENCE = WORK / 'b13-memory.json'

# A remux shape: 6 000 clusters, ~30 GB apparent (5 MB of video payload per cluster, left as a hole), a
# subtitle block every third cluster with two tracks, and blocks sitting late in their cluster behind a
# dozen frames - the layout a real remux has and the one that makes a block header cost a read.
SHAPE = ['--clusters', '6000', '--payload', '5', '--sub-every', '3', '--sub-tracks', '2',
         '--sub-position', 'late', '--blocks-per-cluster', '12', '--frame-payload', '24']
MAX_LANES = 8

PROGRAM = r"""
using System.Diagnostics;
using Jellyfin.Plugin.SubSync.Services;

var files = (Environment.GetEnvironmentVariable("B13_FILES") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries);
var ordinals = (Environment.GetEnvironmentVariable("B13_ORDINALS") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(int.Parse)
    .ToArray();
if (files.Length == 0)
{
    Console.WriteLine("PROBE error: no fixtures given");
    return 2;
}

var also = (Environment.GetEnvironmentVariable("B13_ALSO") ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(int.Parse)
    .ToArray();
var lanes = files.Length;
using var stop = new CancellationTokenSource();
var before = GC.GetTotalMemory(forceFullCollection: true);
var baselineAllocated = GC.GetTotalAllocatedBytes(precise: false);
var process = Process.GetCurrentProcess();

long peakManaged = before;
long peakWorkingSet = 0;
long peakPrivate = 0;
long peakGcHeap = 0;
var samples = 0;

// The measurement runs on its own thread, so the lanes are never sampled from inside the work they do.
var monitor = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        var managed = GC.GetTotalMemory(forceFullCollection: false);
        var heap = GC.GetGCMemoryInfo().HeapSizeBytes;
        process.Refresh();
        peakManaged = Math.Max(peakManaged, managed);
        peakGcHeap = Math.Max(peakGcHeap, heap);
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);
        samples++;
        try
        {
            await Task.Delay(10, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            break;
        }
    }
});

// Every lane starts from the same barrier, so the peak the monitor sees is all of them together - which is
// the number the row is about.
using var barrier = new Barrier(lanes);
var results = new MkvExtractionStats?[lanes];
var srtLengths = new int[lanes];
var cueCounts = new int[lanes];
var elapsed = new long[lanes];
var digests = new string[lanes];
var reasons = new string[lanes];
var allocated = new long[lanes];

var work = new Task[lanes];
for (var i = 0; i < lanes; i++)
{
    var index = i;
    work[index] = Task.Factory.StartNew(
        () =>
        {
            barrier.SignalAndWait();
            var startAllocated = GC.GetTotalAllocatedBytes(precise: false);
            var watch = Stopwatch.StartNew();
            var ordinal = ordinals.Length > index ? ordinals[index] : 0;
            string srt = string.Empty;
            string reason = string.Empty;
            MkvExtractionStats stats;
            var ok = also.Length == 0
                ? MkvSubtitleExtractor.TryExtract(files[index], ordinal, out srt, out reason, null, out stats)
                : MkvSubtitleExtractor.TryExtract(
                    files[index], ordinal, out srt, out reason, null, out stats, alsoExtract: also);
            watch.Stop();
            elapsed[index] = watch.ElapsedMilliseconds;
            results[index] = stats;
            reasons[index] = ok ? reason : "FAILED: " + reason;
            srtLengths[index] = srt.Length;
            cueCounts[index] = SrtWriter.CountCues(srt);
            digests[index] = srt.Length == 0
                ? "-"
                : Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
                    System.Text.Encoding.UTF8.GetBytes(srt))).ToLowerInvariant()[..8];
            allocated[index] = GC.GetTotalAllocatedBytes(precise: false) - startAllocated;
        },
        TaskCreationOptions.LongRunning);
}

await Task.WhenAll(work).ConfigureAwait(false);
stop.Cancel();
await monitor.ConfigureAwait(false);

var after = GC.GetTotalMemory(forceFullCollection: true);
var totalAllocated = GC.GetTotalAllocatedBytes(precise: false) - baselineAllocated;

Console.WriteLine($"PROBE mem lanes={lanes} samples={samples} "
    + $"baselineManagedMB={before / 1048576.0:0.0} peakManagedMB={peakManaged / 1048576.0:0.0} "
    + $"settledManagedMB={after / 1048576.0:0.0} peakGcHeapMB={peakGcHeap / 1048576.0:0.0} "
    + $"peakWorkingSetMB={peakWorkingSet / 1048576.0:0.0} peakPrivateMB={peakPrivate / 1048576.0:0.0} "
    + $"allocatedTotalMB={totalAllocated / 1048576.0:0.0} "
    + $"perLanePeakManagedMB={(peakManaged - before) / 1048576.0 / lanes:0.0}");

for (var i = 0; i < lanes; i++)
{
    var stats = results[i];
    if (stats is null)
    {
        Console.WriteLine($"PROBE lane{i} no stats");
        continue;
    }

    Console.WriteLine($"PROBE lane{i} ms={elapsed[i]} cues={cueCounts[i]} md5={digests[i]} chars={srtLengths[i]} "
        + $"method={stats.Method} walkWindow={stats.WalkWindow} msPerRead={stats.MeasuredMsPerRead:0.00} "
        + $"expected={stats.PlanExpectedBytes} "
        + $"expectedCalls={stats.PlanExpectedCalls} bytesRead={stats.BytesRead} readCalls={stats.ReadCalls} "
        + $"prefetchedRanges={stats.PrefetchedRanges} prefetchedBytes={stats.PrefetchedBytes} "
        + $"unusedFetched={stats.PrefetchedUnusedBytes} memoryServedReads={stats.MemoryServedReads} "
        + $"clusters={stats.ClustersVisited} blocks={stats.SubtitleBlocks} "
        + $"allocatedMB={allocated[i] / 1048576.0:0.0} reason=\"{reasons[i]}\"");
}

return 0;
"""


def harness():
    HARNESS.mkdir(parents=True, exist_ok=True)
    (HARNESS / 'b13probe.csproj').write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>b13probe</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{REPO}/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj" />
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" />
  </ItemGroup>
</Project>
""")
    (HARNESS / 'Program.cs').write_text(PROGRAM)
    env = dict(os.environ, TZ='UTC', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
               LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu')
    build = subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo', '-v', 'q'],
                           cwd=HARNESS, capture_output=True, text=True, env=env)
    if build.returncode != 0:
        sys.stderr.write(build.stdout[-3000:] + build.stderr[-2000:])
        raise SystemExit('probe harness did not build')
    return HARNESS / 'bin' / 'Release' / 'net10.0' / 'b13probe.dll'


# The walk route: no cue index at all, so the pass walks cluster headers - this is where the 16 MB chunk
# and one header read per cluster live, and it is the shape the register's "per-reader memory" is about.
WALK_SHAPE = ['--clusters', '6000', '--payload', '5', '--sub-every', '3', '--sub-tracks', '2',
              '--sub-position', 'late', '--blocks-per-cluster', '12', '--frame-payload', '24', '--no-cues']


def fixtures(count, shape='cues'):
    directory = FIXTURES if shape == 'cues' else FIXTURES.parent / f'b13-fixtures-{shape}'
    directory.mkdir(parents=True, exist_ok=True)
    options = WALK_SHAPE if shape == 'walk' else SHAPE
    paths = []
    for i in range(count):
        path = directory / f'remux-{i}.mkv'
        if not path.exists():
            subprocess.run(['python3', str(GENERATOR), str(path), *options], check=True, capture_output=True)
        paths.append(path)
    return paths


def run(dll, count, slow, shape='cues', shared=False):
    paths = fixtures(count, shape)
    env = dict(os.environ, TZ='UTC', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
               LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu',
               B13_FILES=','.join(str(p) for p in paths),
               B13_ORDINALS=','.join('0' for _ in paths))
    if slow:
        # The rig's slow profile: a per-read latency on the fixture directory, at the numbers measured on
        # the user's share (tests/backend/slowread.c).
        env['LD_PRELOAD'] = str(REPO / 'tests' / 'backend' / 'slowread.so')
        env['SLOWREAD_PREFIX'] = str(FIXTURES) + '/'
        env['SLOWREAD_MS_PER_CALL'] = '10'
        env['SLOWREAD_MS_PER_16K'] = '1.46'
    result = subprocess.run([DOTNET, str(dll)], capture_output=True, text=True, env=env, timeout=3600)
    out = (result.stdout or '').strip()
    if not out:
        raise SystemExit('the memory probe produced nothing:\n' + result.stderr[-2000:])
    return out


def parse(report):
    """Turns the harness's PROBE lines into {label.key: value} plus a list of lane dicts."""
    summary = {}
    lanes = []
    for line in report.splitlines():
        line = line.strip()
        if not line.startswith('PROBE '):
            continue
        body = line[len('PROBE '):]
        label, _, rest = body.partition(' ')
        target = summary if label == 'mem' else {}
        for part in rest.split(' '):
            key, sep, value = part.partition('=')
            if sep and key:
                target[key] = value.strip('"')
        if label.startswith('lane'):
            lanes.append(target)
    return summary, lanes


def mb(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return float('nan')


def report_run(label, summary, lanes, slow):
    print(f'== {label}' + ('   [slow storage: 10 ms per read]' if slow else '   [local disk]'))
    print(f'   lanes={summary.get("lanes")} samples={summary.get("samples")} '
          f'peakManaged={summary.get("peakManagedMB")} MB '
          f'perLanePeak={(summary.get("perLanePeakManagedMB"))} MB '
          f'baseline={summary.get("baselineManagedMB")} MB settledAfter={summary.get("settledManagedMB")} MB '
          f'peakGcHeap={summary.get("peakGcHeapMB")} MB')
    print(f'   peakWorkingSet={summary.get("peakWorkingSetMB")} MB '
          f'peakPrivate={summary.get("peakPrivateMB")} MB '
          f'allocatedTotal={summary.get("allocatedTotalMB")} MB')
    for lane in lanes:
        print(f'   {lane.get("method")} cues={lane.get("cues")} ms={lane.get("ms")} '
              f'window={lane.get("walkWindow")} msPerRead={lane.get("msPerRead")} '
              f'expected={lane.get("expected")} read={lane.get("bytesRead")} calls={lane.get("readCalls")} '
              f'prefetched={lane.get("prefetchedRanges")} ranges/{lane.get("prefetchedBytes")} B '
              f'blocks={lane.get("blocks")} clusters={lane.get("clusters")} '
              f'allocated={lane.get("allocatedMB")} MB')
    print()


def check_mode(dll):
    """The suite's form: a remux-scale pass has to stay under a memory ceiling, at 1 and at 8 lanes."""
    failures = 0

    def report(name, ok, detail=''):
        nonlocal failures
        if not ok:
            failures += 1
        print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail else ''))

    # Ceilings with room in them, stated as what the measurement is allowed to show rather than as a target:
    # 8 lanes of remux extraction on a server that must also run ffsubsync workers.
    try:
        one = parse(run(dll, 1, slow=False))
        eight = parse(run(dll, MAX_LANES, slow=False))
        eight_slow = parse(run(dll, MAX_LANES, slow=True))
    except SystemExit as exc:
        report('the B13 memory probe ran at all (b13)', False, str(exc)[:200])
        return failures

    def peak(pair):
        return mb(pair[0].get('peakManagedMB'))

    per_lane = mb(eight[0].get('perLanePeakManagedMB'))
    report('one remux-scale pass holds well under the 4 MB window + 384 MB the register claims (b13)',
           peak(one) < 200,
           f'peak managed {peak(one)} MB, {one[1][0].get("prefetchedBytes")} B fetched, '
           f'window {one[1][0].get("walkWindow")}, method {one[1][0].get("method")}')
    report('eight remux-scale passes at once stay inside a batch-sized ceiling (b13)',
           peak(eight) < 1200 and per_lane < 150,
           f'peak managed {peak(eight)} MB for 8 lanes, {per_lane} MB per lane')
    report('the slow-storage profile does not multiply the per-lane figure (b13)',
           peak(eight_slow) < 1500 and per_lane < 200,
           f'peak managed {peak(eight_slow)} MB for 8 lanes on 10 ms/read storage, '
           f'{eight_slow[0].get("perLanePeakManagedMB")} MB per lane')
    report('a pass releases what it fetched: settled heap is back near the baseline (b13)',
           mb(eight[0].get('settledManagedMB')) < mb(eight[0].get('baselineManagedMB')) + 64,
           f'baseline {eight[0].get("baselineManagedMB")} MB, settled {eight[0].get("settledManagedMB")} MB')

    for pair, label in ((one, '1 lane'), (eight, '8 lanes'), (eight_slow, '8 lanes, slow storage')):
        lanes = pair[1]
        if not lanes:
            continue
        fetched = max(mb(lane.get('prefetchedBytes')) for lane in lanes)
        report(f'no lane fetches more than its plan prices ({label}) (b13)',
               all(mb(lane.get('prefetchedBytes')) <= mb(lane.get('expected')) + (4 * 1048576)
                   for lane in lanes),
               f'largest fetch {fetched / 1048576:.1f} MB against '
               f'largest plan {max(mb(l.get("expected")) for l in lanes) / 1048576:.1f} MB')

    with open(EVIDENCE, 'w', encoding='utf-8') as handle:
        json.dump({'one': one, 'eight': eight, 'eight_slow': eight_slow}, handle, indent=2)
    print(f'   evidence: {EVIDENCE}')
    return failures


def main():
    dll = harness()
    if '--check' in sys.argv:
        return check_mode(dll)

    lanes = [1, MAX_LANES]
    if '--lanes' in sys.argv:
        at = sys.argv.index('--lanes')
        lanes = [int(sys.argv[at + 1 + i]) for i in range(len(sys.argv) - at - 1) if sys.argv[at + 1 + i].isdigit()]
    slow_only = '--slow' in sys.argv
    shapes = ['cues']
    if '--all-shapes' in sys.argv:
        shapes = ['cues', 'walk', 'shared']

    for shape in shapes:
        for count in lanes:
            for slow in ([True] if slow_only else [False]):
                label = f'{count} lane(s)'
                if shape == 'walk':
                    label += ', no cue index (cluster walk)'
                elif shape == 'shared':
                    label += ', shared multi-track pass'
                report_run(label, *parse(run(dll, count, slow, shape=shape, shared=(shape == 'shared'))), slow=slow)

    if '--keep' not in sys.argv:
        shutil.rmtree(FIXTURES, ignore_errors=True)
        shutil.rmtree(FIXTURES.parent / 'b13-fixtures-walk', ignore_errors=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
