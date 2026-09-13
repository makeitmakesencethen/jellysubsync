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

PROGRAM = r"""
using System.Diagnostics;
using Jellyfin.Plugin.SubSync.Services;

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: readrig <file.mkv> [ordinal ...]");
    return 2;
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
        subprocess.run(['git', 'worktree', 'add', '--detach', str(target), revision],
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
        env['SLOWREAD_PREFIX'] = str(path.parent) + '/'
        env.update(SLOW)
    result = subprocess.run([DOTNET, str(dll), str(path), *[str(o) for o in ordinals]],
                            capture_output=True, text=True, env=env, timeout=3600)
    for line in result.stdout.splitlines():
        if line.startswith('RIG '):
            return json.loads(line[4:])
    raise SystemExit(f'no RIG line from {path} (exit {result.returncode})\n{result.stdout[-2000:]}\n{result.stderr[-2000:]}')


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
    parser.add_argument('--quick', action='store_true', help='small fixtures instead of the 0,5/1,3 GB ones')
    parser.add_argument('--json', default=None, help='append the rows to this file as JSON lines')
    args = parser.parse_args()

    shapes = [s.strip() for s in args.shapes.split(',') if s.strip()]
    if args.quick:
        shapes = [s if s not in SHAPES else ('quick' if s == 'arcane' else 'quick-walk' if s == 'remux-walk' else s)
                  for s in shapes]
    profiles = ['slow', 'fast'] if args.profiles == 'both' else [args.profiles]

    WORK.mkdir(parents=True, exist_ok=True)
    fixtures_dir = WORK / 'rig-fixtures'
    fixtures_dir.mkdir(exist_ok=True)

    repo, _ = revision_dir(args.rev)
    tag = (args.label or args.rev).replace('/', '_')
    dll = harness(repo, tag)
    print(f"# rig: rev={args.rev} ({repo}) tag={tag}")

    rows = []
    for shape in shapes:
        spec = dict(SHAPES[shape])
        extra = spec.pop('extra')
        path = fixture(fixtures_dir, spec.pop('name'), spec.pop('clusters'), spec.pop('payload_mb'),
                       spec.pop('sub_every'), extra)
        for profile in profiles:
            row = run(dll, path, [0], profile)
            row.update({'shape': shape, 'label': tag, 'rev': args.rev, 'profile': profile,
                        'fileMb': path.stat().st_size / 1e6})
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
