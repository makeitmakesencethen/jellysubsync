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
    python3 tests/backend/s26_probe.py --check    # the suite's PASS/FAIL form of the same measurement
    python3 tests/backend/s26_probe.py --slow     # the same, under the rig's slow-storage shim

Run ONE of these at a time. The probe builds its fixtures when they are missing, so two probes started
together write and read the same files, and a torn fixture reads as "every located block could not be
read" - which looks exactly like the defect being measured, and cost an afternoon chasing it.
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
var digest = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
    System.Text.Encoding.UTF8.GetBytes(srt))).ToLowerInvariant();
Console.WriteLine($"PROBE file={Path.GetFileName(path)}");
Console.WriteLine($"PROBE cues={cues} md5={digest} chars={srt.Length} method={stats.Method} reason=\"{reason}\"");
foreach (var line in planLines.Where(l => l.Contains("expected")))
{
    Console.WriteLine("PROBE " + line);
}
Console.WriteLine($"PROBE actual: bytesRead={stats.BytesRead} reads={stats.ReadCalls} "
    + $"indexedMisses={stats.IndexedMisses} clustersVisited={stats.ClustersVisited}");
Console.WriteLine($"PROBE prefetched={stats.PrefetchedRanges} ranges/{stats.PrefetchedBytes} B, "
    + $"unused={stats.PrefetchedUnusedRanges} ranges/{stats.PrefetchedUnusedBytes} B");
// The read policy's two ledger rules: bytes a read went to the file for after the pass had already
// fetched them, and bytes fetched over ranges the location phase had already read. Both must be zero.
Console.WriteLine($"PROBE ledger: readAfterFetch={stats.BytesReadAfterFetch} B, "
    + $"fetchedOverDisk={stats.BytesFetchedOverDisk} B, bytesTwice={stats.BytesReadTwice} B, "
    + $"walked={stats.WalkedClusters}");
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


def run(dll, path, ordinal=0, slow=False):
    env = dict(os.environ, TZ='UTC', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
               LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu')
    if slow:
        # The rig's slow storage profile: a real per-read latency on the fixture's directory, the same
        # shim and numbers the scenarios use (tests/backend/rig.py, run_scenario.py --ms-per-call).
        env['LD_PRELOAD'] = str(REPO / 'tests' / 'backend' / 'slowread.so')
        env['SLOWREAD_PREFIX'] = str(FIXTURES) + '/'
        env['SLOWREAD_MS_PER_CALL'] = '10'
        env['SLOWREAD_MS_PER_16K'] = '1.46'
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
    if '--check' in sys.argv:
        return check_mode(dll)

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
        slow = '--slow' in sys.argv
        suffix = ' [slow storage: 10 ms/read]' if slow else ''
        print(f'== {label}: {plain.name} (clusters with a known size){suffix}')
        print('  ' + run(dll, plain, slow=slow))
        print(f'== {label}: {unknown.name} (every cluster size marked unknown){suffix}')
        print('  ' + run(dll, unknown, slow=slow))
        print()

    if '--keep' not in sys.argv:
        shutil.rmtree(FIXTURES, ignore_errors=True)
    return 0


def parse(report):
    """Turn the harness's PROBE lines into a dict keyed by the line's own label (top/actual/ratio/...)."""
    out = {}
    for line in report.splitlines():
        line = line.strip()  # run() indents continuation lines for the printed report
        if not line.startswith('PROBE '):
            continue
        body = line[len('PROBE '):]
        label, sep, rest = body.partition(':')
        if not sep:
            label, rest = 'top', body
        for part in rest.split(' '):
            key, _, value = part.partition('=')
            if key and value:
                out[f'{label.strip()}.{key}'] = value
    return out


def check_mode(dll):
    """Assert the pass reads what its own plan priced, on a file that states no cluster size (S26).

    Prints the suite's PASS/FAIL lines and returns the failure count. Two files, the same bytes apart
    from their clusters' size vints: with the fix the unknown-size one has to match the known-size one
    block for block, and neither may read past its own plan or make the ledger's two rules non-zero.
    """
    dense = FIXTURES / 'shape-dense.mkv'
    unknown = FIXTURES / 'shape-dense-unknown-size.mkv'
    if not dense.exists():
        FIXTURES.mkdir(parents=True, exist_ok=True)
        subprocess.run(['python3', str(GENERATOR), str(dense), *DENSE], check=True, capture_output=True)
    if not unknown.exists():
        unknown_size_variant(dense, unknown)

    failures = 0

    def report(name, ok, detail=''):
        nonlocal failures
        if not ok:
            failures += 1
        print(('PASS  ' if ok else 'FAIL  ') + name + (f'   [{detail}]' if detail and not ok else ''))

    try:
        known = parse(run(dll, dense))
        other = parse(run(dll, unknown))
    except Exception as exc:  # a probe that cannot run is a failure, and says why
        report('the S26 cost probe ran at all (s26)', False, f'{type(exc).__name__}: {exc}')
        return failures

    def number(report_dict, key):
        try:
            return float(str(report_dict.get(key, 'nan')).rstrip('x'))
        except ValueError:
            return float('nan')

    caught = number(other, 'actual.indexedMisses') == 0 and number(other, 'ledger.walked') == 0
    report('a file that states no cluster size does not turn every located read into a walk (s26)',
           caught,
           f"indexedMisses={other.get('actual.indexedMisses')} walked={other.get('ledger.walked')}")

    ratio_bytes = number(other, 'ratio.bytes')
    ratio_reads = number(other, 'ratio.reads')
    report('a located pass reads what its own plan priced, unknown-size clusters included (s26)',
           0 < ratio_bytes <= 2.0 and 0 < ratio_reads <= 2.0,
           f'bytes {ratio_bytes}x, reads {ratio_reads}x (known-size file: '
           f"{known.get('ratio.bytes')}x/{known.get('ratio.reads')}x)")

    report('the located path extracts the same subtitle, byte for byte (s26)',
           known.get('top.md5') == other.get('top.md5') and known.get('top.cues') == other.get('top.cues')
           and known.get('top.cues') not in (None, '0'),
           f"known {known.get('top.cues')}/{known.get('top.md5')} vs "
           f"unknown {other.get('top.cues')}/{other.get('top.md5')}")

    clean = (number(known, 'ledger.readAfterFetch') == 0 and number(known, 'ledger.fetchedOverDisk') == 0
             and number(other, 'ledger.readAfterFetch') == 0 and number(other, 'ledger.fetchedOverDisk') == 0)
    report('both read-ledger rules stay at zero on the repro (s26)', clean,
           f"known {known.get('ledger.readAfterFetch')}/{known.get('ledger.fetchedOverDisk')}, "
           f"unknown {other.get('ledger.readAfterFetch')}/{other.get('ledger.fetchedOverDisk')}")

    return failures


if __name__ == '__main__':
    raise SystemExit(main())
