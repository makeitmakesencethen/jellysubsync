#!/usr/bin/env python3
"""The read rig: measure what an extraction costs, on a fixture shaped like a real file, under a
storage profile that behaves like the share it came from.

Why it exists
-------------
`tests/run_checks.py` asserts *what* a pass reads (bytes, reads, routes, invariants) on this box's
local disk. It cannot assert what a pass costs on a share that charges 10 ms per read and delivers
11 MB/s, because this box is neither. This rig can: it runs the same extraction with the
`slowread.so` LD_PRELOAD shim in front of the fixture directory, which adds the user's measured
per-read latency and per-byte cost, so a wall-clock number here means the same thing as one in his
plugin log.

It also measures a *revision*: `--rev e35cb2c` builds the same harness against that git revision's
plugin project (in a worktree under `.tests-work/`), so "before" and "after" are the real code of
both, not arithmetic.

Fixture shapes
--------------
The generator (`tests/fixtures/make_remux.py`) writes clusters of `--payload CR MB` with a sparse
video payload and a subtitle block every `--sub-every` clusters. Two layouts matter:

* `--sub-position early` - the block is one of the cluster's first blocks. Every fixture used before
  2026-09-13 was this shape.
* `--sub-position late`  - the block sits after the video payload, most of a cluster in. This is the
  shape fabji's server reported: the prefetch ranges it made were 393,7 MB over 384 clusters (~1 MB
  each, `extract lane: ... prefetched=384 ranges/393,7 MB`), which only happens when the block is
  about a megabyte past its cluster's start.

Usage
-----
    python3 tests/backend/read_rig.py --rev e35cb2c --label before
    python3 tests/backend/read_rig.py --label after
    python3 tests/backend/read_rig.py --quick          # small fixtures, both revisions

Numbers it prints, per run: method, route, bytes read, read calls, wall clock, the pass's own
predicted cost, bytes read twice and fetched-but-unused bytes (both from the ledger inside the pass).
"""
import argparse
import json
import os
import pathlib
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = pathlib.Path(os.environ.get('TESTS_WORK') or REPO / '.tests-work')
DOTNET = os.environ.get('DOTNET_BIN') or '/opt/data/.dotnet/dotnet'
SLOWREAD = REPO / 'tests' / 'backend' / 'slowread.so'

# fabji's Synology share, as measured from his own plugin log (2026-09-12): ~10 ms per read AND
# ~11 MB/s in aggregate. Modelling only the second half is what made earlier benchmarks say
# "thousands of serial reads are cheap" while his server needed two minutes.
SLOW = {'SLOWREAD_MS_PER_CALL': '10', 'SLOWREAD_MS_PER_16K': '1.46'}


def slow_profile(call_ms, mb_per_second):
    """The share as the shim models it: a round trip plus the bytes it carried.

    Both halves are charged (see slowread.c). A profile with only the latency would call a 325 MB pass
    cheap, and one with only the bytes would call thousands of small reads free, so a rig that wants to
    mean what a real server's log means has to carry both.
    """
    return {'SLOWREAD_MS_PER_CALL': f'{call_ms:g}',
            'SLOWREAD_MS_PER_16K': f'{16384.0 / (mb_per_second * 1000.0):g}'}

PROGRAM = r"""
using System.Diagnostics;
using Jellyfin.Plugin.SubSync.Services;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: readrig <file.mkv> [ordinal ...]   (RIG_SEQUENCE=1: every argument is a file)");
    return 2;
}

// A sequence: one extraction per file, in this one process, in order. This is what the per-volume storage
// profile is for - the first pass on a volume has nothing to go on, and every pass after it starts from what
// the volume has already shown - so the rig has to be able to run several passes without a restart.
var sequence = Environment.GetEnvironmentVariable("RIG_SEQUENCE") == "1";
if (sequence)
{
    var sequenceWatch = Stopwatch.StartNew();
    double totalBytes = 0, totalReads = 0;
    foreach (var file in args)
    {
        var passWatch = Stopwatch.StartNew();
        var passOk = MkvSubtitleExtractor.TryExtract(file, 0, out var passText, out var passReason, null, out var passStats);
        passWatch.Stop();
        var passFields = new Dictionary<string, object?>
        {
            ["sequence"] = true,
            ["file"] = Path.GetFileName(file),
            ["ok"] = passOk,
            ["reason"] = passReason,
            ["cues"] = passText.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length,
            ["wallMs"] = passWatch.Elapsed.TotalMilliseconds,
            ["method"] = passStats.Method,
            ["route"] = typeof(MkvExtractionStats).GetProperty("Route")?.GetValue(passStats) ?? "n/a",
            ["bytes"] = passStats.BytesRead,
            ["reads"] = passStats.ReadCalls,
            ["clusters"] = passStats.ClustersVisited,
            ["msPerRead"] = passStats.MeasuredMsPerRead,
            ["mbPerSecond"] = passStats.MeasuredMbPerSecond,
            ["planLines"] = string.Join(" | ", passStats.PlanLines),
        };
        totalBytes += passStats.BytesRead;
        totalReads += passStats.ReadCalls;
        Console.WriteLine("RIG " + System.Text.Json.JsonSerializer.Serialize(passFields));
        Console.Out.Flush();
    }

    sequenceWatch.Stop();
    Console.WriteLine("RIG " + System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["sequence"] = true,
        ["total"] = true,
        ["files"] = args.Length,
        ["wallMs"] = sequenceWatch.Elapsed.TotalMilliseconds,
        ["bytes"] = totalBytes,
        ["reads"] = totalReads,
    }));
    return 0;
}

var path = args[0];
var ordinals = args.Skip(1).Select(int.Parse).ToList();
if (ordinals.Count == 0)
{
    ordinals.Add(0);
}

var watch = Stopwatch.StartNew();
bool ok;
string reason;
MkvExtractionStats stats;
int cues;
if (ordinals.Count == 1)
{
    ok = MkvSubtitleExtractor.TryExtract(path, ordinals[0], out var text, out reason, null, out stats);
    cues = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
}
else
{
    ok = MkvSubtitleExtractor.TryExtractMany(path, ordinals, out var many, out reason, out stats);
    cues = many.Count == 0 ? 0 : SrtWriter.CountCues(many[ordinals[0]]);
}

watch.Stop();

// The numbers are read through reflection: this harness measures more than one revision, and a revision
// that predates a metric reports zero for it instead of failing to build. (2.0.23, for instance, has no
// plan, route or ledger fields - it is the "before" side of the comparison.)
var fields = new Dictionary<string, object?>
{
    ["ok"] = ok,
    ["reason"] = reason,
    ["cues"] = cues,
    ["wallMs"] = watch.Elapsed.TotalMilliseconds,
    ["method"] = stats.Method,
};

void Add(string key, string property)
{
    var value = typeof(MkvExtractionStats).GetProperty(property)?.GetValue(stats);
    fields[key] = value ?? 0;
}

fields["route"] = typeof(MkvExtractionStats).GetProperty("Route")?.GetValue(stats) ?? "n/a";
Add("bytes", "BytesRead");
Add("reads", "ReadCalls");
Add("clusters", "ClustersVisited");
Add("blocks", "SubtitleBlocks");
Add("alsoBlocks", "AlsoBlocks");
Add("expectedBytes", "PlanExpectedBytes");
Add("expectedCalls", "PlanExpectedCalls");
Add("missed", "PlanMissed");
Add("bytesTwice", "BytesReadTwice");
Add("prefetchedRanges", "PrefetchedRanges");
Add("prefetchedBytes", "PrefetchedBytes");
Add("unusedRanges", "PrefetchedUnusedRanges");
Add("unusedBytes", "PrefetchedUnusedBytes");
Add("memoryReads", "MemoryServedReads");
Add("msPerRead", "MeasuredMsPerRead");
Add("mbPerSecond", "MeasuredMbPerSecond");
Add("probeMs", "StorageProbeMs");

Console.WriteLine("RIG " + System.Text.Json.JsonSerializer.Serialize(fields));
return ok ? 0 : 1;
"""


def revision_dir(revision):
    """A worktree of `revision`, built once, so 'before' is the real code of that revision."""
    if revision in ('HEAD', 'worktree'):
        return REPO, None
    target = WORK / f'rev-{revision}'
    if not (target / 'Jellyfin.Plugin.SubSync').is_dir():
        # run_checks.py clears .tests-work between runs, which deletes a worktree the repository still has
        # registered: without the prune the add dies with "missing but already registered worktree".
        subprocess.run(['git', 'worktree', 'prune'], cwd=REPO, capture_output=True, text=True)
        subprocess.run(['git', 'worktree', 'add', '-f', '--detach', str(target), revision],
                       cwd=REPO, check=True, capture_output=True, text=True)
    return target, target


def harness(repo, tag):
    """Builds the rig harness against one revision's plugin project."""
    directory = WORK / f'readrig-{tag}'
    directory.mkdir(parents=True, exist_ok=True)
    (directory / 'readrig.csproj').write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>readrig</AssemblyName>
    <InvariantGlobalization>true</InvariantGlobalization>
    <NoWarn>CS1591</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="{repo}/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj" />
    <PackageReference Include="Jellyfin.Controller" Version="12.0.0" />
    <PackageReference Include="Jellyfin.Model" Version="12.0.0" />
  </ItemGroup>
</Project>
""")
    (directory / 'Program.cs').write_text(PROGRAM)
    env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1', TZ='UTC')
    build = subprocess.run([DOTNET, 'build', '-c', 'Release', '--nologo', '-v', 'q'],
                           cwd=directory, capture_output=True, text=True, env=env)
    if build.returncode != 0:
        sys.stderr.write(build.stdout[-3000:] + build.stderr[-3000:])
        raise SystemExit('rig harness did not build')
    return directory / 'bin' / 'Release' / 'net10.0' / 'readrig.dll'


def fixture(directory, name, clusters, payload_mb, sub_every, extra=()):
    path = directory / name
    if not path.exists():
        subprocess.run(['python3', str(REPO / 'tests' / 'fixtures' / 'make_remux.py'), str(path),
                        '--clusters', str(clusters), '--payload', str(payload_mb),
                        '--sub-every', str(sub_every), *extra], check=True, capture_output=True)
    return path


def run(dll, path, ordinals, profile):
    files = path if isinstance(path, (list, tuple)) else [path]
    first = pathlib.Path(files[0])
    env = dict(os.environ)
    env['DOTNET_SYSTEM_GLOBALIZATION_INVARIANT'] = '1'
    latencies = env.get('LD_LIBRARY_PATH')
    env['LD_LIBRARY_PATH'] = '/opt/data/local/icu/usr/lib/x86_64-linux-gnu' + ((':' + latencies) if latencies else '')
    env['TZ'] = 'UTC'
    if profile == 'slow':
        if not SLOWREAD.exists():
            raise SystemExit(f'build the shim first: gcc -shared -fPIC -O2 -o {SLOWREAD} '
                             f'{REPO / "tests" / "backend" / "slowread.c"} -ldl')
        env['LD_PRELOAD'] = str(SLOWREAD)
        env['SLOWREAD_PREFIX'] = str(first.parent) + '/'
        env.update(SLOW)
    if len(files) > 1:
        env['RIG_SEQUENCE'] = '1'
    prefixes = {str(pathlib.Path(f).parent) + '/' for f in files}
    if len(prefixes) > 1:
        raise SystemExit('a sequence has to run on one volume: ' + ', '.join(sorted(prefixes)))
    command = [DOTNET, str(dll), *[str(f) for f in files]]
    if len(files) == 1:
        command += [str(o) for o in ordinals]
    result = subprocess.run(command, capture_output=True, text=True, env=env, timeout=3600)
    rows = [json.loads(line[4:]) for line in result.stdout.splitlines() if line.startswith('RIG ')]
    if not rows:
        raise SystemExit(f'no RIG line from {path} (exit {result.returncode})\n{result.stdout[-2000:]}\n{result.stderr[-2000:]}')
    return rows if len(files) > 1 else rows[0]


def describe(label, row, profile):
    return (
        f"{label:<28} {profile:<5} {row['method']:<13} route={row['route']:<12} "
        f"{row['bytes'] / 1e6:8.2f} MB  {row['reads']:6d} reads  {row['wallMs'] / 1000:8.2f} s  "
        f"cues={row['cues']:<5} ms/read={row['msPerRead']:6.2f}  "
        f"{row['mbPerSecond']:5.1f} MB/s  plan={row['expectedBytes'] / 1e6:6.2f} MB/{row['expectedCalls']} "
        f"missed={row['missed']} twice={row['bytesTwice'] / 1e6:5.2f} MB "
        f"unusedFetch={row['unusedRanges']} memReads={row['memoryReads']}"
    )


SHAPES = {
    # fabji's Arcane episode: 0,5 GB and 50-70 s per episode on his share, subtitles late in their
    # cluster (the 393,7 MB of prefetch over 384 ranges in his log).
    'arcane': dict(name='arcane-0.5gb.mkv', clusters=520, payload_mb=1, sub_every=2,
                   extra=('--sub-position', 'late')),
    # A remux whose cue index does not locate the blocks, so the clusters have to be walked:
    # the shape 2.0.23's changelog measured at ~2 min for 1,3 GB on this share.
    'remux-walk': dict(name='remux-1.3gb-walk.mkv', clusters=260, payload_mb=5, sub_every=2,
                        extra=('--no-rel-pos', '--sub-position', 'late')),
    'quick': dict(name='quick.mkv', clusters=40, payload_mb=1, sub_every=2,
                  extra=('--sub-position', 'late')),
    'quick-walk': dict(name='quick-walk.mkv', clusters=40, payload_mb=1, sub_every=2,
                       extra=('--no-rel-pos', '--sub-position', 'late')),
    'quick-many': dict(name='quick-many.mkv', clusters=40, payload_mb=1, sub_every=2,
                       extra=('--sub-position', 'late', '--sub-tracks', '4')),
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--rev', default='worktree',
                        help="git revision to measure: 'worktree' = the code here, or a commit "
                             "(beta releases are commits, not tags - e.g. e35cb2c is 2.0.23)")
    parser.add_argument('--label', default=None, help='name for this run')
    parser.add_argument('--shapes', default='arcane,remux-walk', help='comma-separated fixture shapes')
    parser.add_argument('--profiles', default='slow', help='slow (fabji share), fast (local disk), both')
    parser.add_argument('--call-ms', type=float, default=10.0,
                        help='per-read latency of the modelled share (default 10, the user\'s Synology)')
    parser.add_argument('--mb-per-second', type=float, default=11.0,
                        help='aggregate bandwidth of the modelled share (default 11)')
    parser.add_argument('--quick', action='store_true', help='small fixtures instead of the 0,5/1,3 GB ones')
    parser.add_argument('--json', default=None, help='append the rows to this file as JSON lines')
    parser.add_argument('--sequence', default=None,
                        help='comma-separated shapes extracted in sequence in ONE process, which is the only '
                             'way to measure what one pass teaches the next about a volume')
    args = parser.parse_args()

    shapes = [s.strip() for s in args.shapes.split(',') if s.strip()]
    if args.quick:
        shapes = [s if s not in SHAPES else ('quick' if s == 'arcane' else 'quick-walk' if s == 'remux-walk' else s)
                  for s in shapes]
    profiles = ['slow', 'fast'] if args.profiles == 'both' else [args.profiles]

    SLOW.clear()
    SLOW.update(slow_profile(args.call_ms, args.mb_per_second))

    WORK.mkdir(parents=True, exist_ok=True)
    fixtures_dir = WORK / 'rig-fixtures'
    fixtures_dir.mkdir(exist_ok=True)

    repo, _ = revision_dir(args.rev)
    tag = (args.label or args.rev).replace('/', '_')
    dll = harness(repo, tag)
    print(f"# rig: rev={args.rev} ({repo}) tag={tag} "
          f"share={args.call_ms:g} ms/read, {args.mb_per_second:g} MB/s")

    rows = []
    if args.sequence:
        names = [s.strip() for s in args.sequence.split(',') if s.strip()]
        paths = []
        for name in names:
            spec = dict(SHAPES[name])
            extra = spec.pop('extra')
            paths.append(fixture(fixtures_dir, spec.pop('name'), spec.pop('clusters'), spec.pop('payload_mb'),
                                 spec.pop('sub_every'), extra))
        for profile in profiles:
            measured = run(dll, paths, [0], profile)
            total = measured[-1]
            passes = measured[:-1]
            print(f"# rig sequence: {len(passes)} pass(es) in one process, {args.call_ms:g} ms/read, "
                  f"{args.mb_per_second:g} MB/s", flush=True)
            for index, row in enumerate(passes):
                print(f"  pass {index + 1}  {row['file']:<22} {row['route']:<12} "
                      f"{row['bytes'] / 1e6:8.2f} MB {int(row['reads']):>6} reads "
                      f"{row['wallMs'] / 1000:8.2f} s  cues={row['cues']:<5} "
                      f"{(row['reads'] / max(1, row['cues'])):5.2f} reads/cue", flush=True)
            print(f"  total   {total['files']} pass(es)              "
                  f"{total['bytes'] / 1e6:8.2f} MB {int(total['reads']):>6} reads "
                  f"{total['wallMs'] / 1000:8.2f} s", flush=True)
            for row in passes:
                row.update({'shape': 'sequence', 'label': tag, 'rev': args.rev, 'profile': profile,
                            'profileCallMs': args.call_ms, 'profileMbPerSecond': args.mb_per_second})
                rows.append(row)
            rows.append({'shape': 'sequence-total', 'label': tag, 'rev': args.rev, 'profile': profile,
                         'profileCallMs': args.call_ms, 'profileMbPerSecond': args.mb_per_second, **total})
        if args.json:
            with open(args.json, 'a') as handle:
                for row in rows:
                    handle.write(json.dumps(row) + '\n')
            print(f"# appended {len(rows)} row(s) to {args.json}")
        return 0

    for shape in shapes:
        spec = dict(SHAPES[shape])
        extra = spec.pop('extra')
        path = fixture(fixtures_dir, spec.pop('name'), spec.pop('clusters'), spec.pop('payload_mb'),
                       spec.pop('sub_every'), extra)
        for profile in profiles:
            row = run(dll, path, [0], profile)
            row.update({'shape': shape, 'label': tag, 'rev': args.rev, 'profile': profile,
                        'fileMb': path.stat().st_size / 1e6,
                        'profileCallMs': args.call_ms, 'profileMbPerSecond': args.mb_per_second})
            rows.append(row)
            print(describe(shape, row, profile), flush=True)

    if args.json:
        with open(args.json, 'a') as handle:
            for row in rows:
                handle.write(json.dumps(row) + '\n')
        print(f"# appended {len(rows)} row(s) to {args.json}")
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
