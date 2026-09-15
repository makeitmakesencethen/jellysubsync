#!/usr/bin/env python3
"""S26 probe: does a cluster with an *unknown size* make the cue-indexed route walk instead of read?

The live evidence (2026-09-15, `Sunes Sommar 1993 WEB-DL 1080p.mkv`) is a pass whose own plan is right
(`the index locates 663 of 663 cue point(s); 663 located block(s), 0 cluster(s) to walk`) but which read
133 MB / 33 525 reads where 1.57 MB / 1 319 were priced - ~50 reads and ~200 KB per located block.

The code has one obvious way for a *located* block to cost a cluster: when the indexed read cannot be
made, the cue-indexed loop falls back to `ReadCluster(..., wideWindow: true)` (MkvSubtitleExtractor.cs:810)
for that cue point. And both preconditions of the indexed read refuse a cluster whose size vint is the
EBML unknown marker (`size == ulong.MaxValue`, MkvSubtitleExtractor.cs:1296-1310 and :1334-1362).

So this probe builds the same fixture twice - once as the generator writes it, once with every cluster's
size rewritten in place as unknown - and prints plan vs actual and `IndexedMisses` for both. If the second
file shows the shape the field showed, the cold repro is a fixture and the culprit is known.

Usage:
    python3 tests/backend/s26_probe.py            # both files
    python3 tests/backend/s26_probe.py --keep     # leave the fixtures for inspection
"""
import os
import pathlib
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = pathlib.Path(os.environ.get('TESTS_WORK') or REPO / '.tests-work')
DOTNET = os.environ.get('DOTNET_BIN') or '/opt/data/.dotnet/dotnet'
GENERATOR = REPO / 'tests' / 'fixtures' / 'make_remux.py'
HARNESS = WORK / 's26-probe'
FIXTURES = WORK / 's26-fixtures'

# The fixture shape: many clusters with subtitle blocks sitting late in them (the real-world layout), two
# subtitle tracks so the shared pass is exercised too.
SHAPE = ['--clusters', '120', '--payload', '1', '--sub-every', '3', '--sub-tracks', '2',
         '--sub-position', 'late']
# A real cluster carries a subtitle block behind dozens of frames; the field file read ~50 block headers
# per cue point. Density is the variable that turns the same defect from free into 86x.
DENSE = SHAPE + ['--blocks-per-cluster', '48', '--frame-payload', '24']

PROGRAM = r"""
using Jellyfin.Plugin.SubSync.Services;

// Extract one file and print what the pass cost against what it planned.
var path = args[0];
var ordinal = args.Length > 1 ? int.Parse(args[1]) : 0;
var planLines = new List<string>();
MkvSubtitleExtractor.TryExtract(path, ordinal, out var srt, out var reason, planLines.Add, out var stats);
var cues = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
Console.WriteLine($"PROBE file={Path.GetFileName(path)}");
Console.WriteLine($"PROBE cues={cues} method={stats.Method} reason=\"{reason}\"");
foreach (var line in planLines.Where(l => l.Contains("expected")))
{
    Console.WriteLine("PROBE " + line);
}
Console.WriteLine($"PROBE actual: bytesRead={stats.BytesRead} reads={stats.ReadCalls} "
    + $"indexedMisses={stats.IndexedMisses} clustersVisited={stats.ClustersVisited}");
Console.WriteLine($"PROBE prefetched={stats.PrefetchedRanges} ranges/{stats.PrefetchedBytes} B, "
    + $"unused={stats.PrefetchedUnusedRanges} ranges/{stats.PrefetchedUnusedBytes} B");
if (stats.PlanExpectedBytes > 0)
{
    Console.WriteLine($"PROBE ratio: bytes={(double)stats.BytesRead / stats.PlanExpectedBytes:0.00}x "
        + $"reads={(double)stats.ReadCalls / Math.Max(1, stats.PlanExpectedCalls):0.00}x");
}
if (cues > 0)
{
    // The field file: 663 cues, 33 525 reads, 133.6 MB -> ~50 reads and ~200 KB per cue point.
    Console.WriteLine($"PROBE per cue: reads={(double)stats.ReadCalls / cues:0.0} "
        + $"bytes={stats.BytesRead / cues} B");
}
"""


def harness():
    HARNESS.mkdir(parents=True, exist_ok=True)
    (HARNESS / 's26probe.csproj').write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>s26probe</AssemblyName>
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
        sys.stderr.write(build.stdout[-2000:] + build.stderr[-2000:])
        raise SystemExit('probe harness did not build')
    return HARNESS / 'bin' / 'Release' / 'net10.0' / 's26probe.dll'


def run(dll, path, ordinal=0):
    env = dict(os.environ, TZ='UTC', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
               LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu')
    result = subprocess.run([DOTNET, str(dll), str(path), str(ordinal)],
                            capture_output=True, text=True, env=env, timeout=1800)
    return (result.stdout.strip() or result.stderr[-800:]).replace('\n', '\n  ')


CLUSTER_ID = b'\x1f\x43\xb6\x75'


def unknown_size_variant(source: pathlib.Path, target: pathlib.Path) -> int:
    """Rewrite every cluster's size vint in place as the EBML unknown marker (same width, all value bits 1)."""
    data = bytearray(source.read_bytes())
    rewritten = 0
    cursor = 0
    while True:
        at = data.find(CLUSTER_ID, cursor)
        if at < 0:
            break
        size_at = at + len(CLUSTER_ID)
        first = data[size_at]
        if first == 0:
            cursor = size_at + 1
            continue
        width = 8 - first.bit_length() + 1            # vint length from the marker bit's position
        if 1 <= width <= 8 and size_at + width <= len(data):
            data[size_at] = 0xFF >> (width - 1)       # marker + all value bits set = unknown size
            for i in range(1, width):
                data[size_at + i] = 0xFF
            rewritten += 1
            cursor = size_at + width
        else:
            cursor = size_at + 1
    target.write_bytes(bytes(data))
    return rewritten


def main():
    dll = harness()
    FIXTURES.mkdir(parents=True, exist_ok=True)

    for label, shape in (('sparse (as the generator wrote it until now)', SHAPE),
                         ('dense (48 frames of 24 KB payload per cluster)', DENSE)):
        plain = FIXTURES / ('shape.mkv' if shape is SHAPE else 'shape-dense.mkv')
        if not plain.exists():
            subprocess.run(['python3', str(GENERATOR), str(plain), *shape], check=True, capture_output=True)
        unknown = FIXTURES / (plain.stem + '-unknown-size.mkv')
        if not unknown.exists():
            count = unknown_size_variant(plain, unknown)
            print(f'== {label}: {count} cluster size(s) rewritten as unknown')
        print(f'== {label}: {plain.name} (clusters with a known size)')
        print('  ' + run(dll, plain))
        print(f'== {label}: {unknown.name} (every cluster size marked unknown)')
        print('  ' + run(dll, unknown))
        print()

    if '--keep' not in sys.argv:
        shutil.rmtree(FIXTURES, ignore_errors=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
