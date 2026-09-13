#!/usr/bin/env python3
"""The Phase 4 probe: reproduce one extraction/read-policy finding from FIX_PLAN's Tier 4 on a fixture.

Why it exists
-------------
Every finding in the Tier 4 list is only worth a fix if a fixture shows the problem, and the fixes that came
out of this project's own audits (D17's cross-track cue offsets, E2's duplicate cues) were the ones where
somebody had a reproduction. This harness is that reproduction, kept so the next reader can re-run it rather
than take the report's word: it builds a small program against the working tree's plugin project, generates
the fixtures each mode needs with the same generator the rest of the tests use, and prints the numbers.

Usage
-----
    python3 tests/backend/p4_probe.py b11-progress      # progress line during a metadata scan
    python3 tests/backend/p4_probe.py b2-mixed-shared   # shared pass vs per-track extraction
    python3 tests/backend/p4_probe.py b24-collision     # the cue dedup key's collision case
    python3 tests/backend/p4_probe.py b25-short-read    # a short read reaching the cue text (needs the shim)
    python3 tests/backend/p4_probe.py all

Modes that need fixtures generate them under `$TESTS_WORK/p4-fixtures` (default `.tests-work`), which is the
same directory the suite clears, so a run regenerates what it needs.
"""
import json
import os
import pathlib
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = pathlib.Path(os.environ.get('TESTS_WORK') or REPO / '.tests-work')
DOTNET = os.environ.get('DOTNET_BIN') or '/opt/data/.dotnet/dotnet'
GENERATOR = REPO / 'tests' / 'fixtures' / 'make_remux.py'
PATCH_CUES = REPO / 'tests' / 'fixtures' / 'patch_cues.py'
SHORTREAD = REPO / 'tests' / 'backend' / 'shortread.so'
HARNESS = WORK / 'p4-probe'
FIXTURES = WORK / 'p4-fixtures'

PROGRAM = r"""
using Jellyfin.Plugin.SubSync.Services;

// One mode per finding. Each prints "PROBE ..." lines: what it measured, on which fixture.
var mode = args[0];
var path = args.Length > 1 ? args[1] : string.Empty;

if (mode == "b11-progress")
{
    // B11: the metadata-scan route's progress line never leaves 0.0 MB, because it printed the stats field
    // that only the cue-indexed loop fills.
    var lines = new List<string>();
    MkvSubtitleExtractor.TryExtract(path, 0, out _, out var reason, lines.Add, out var stats);
    var printed = lines.Where(l => l.Contains("MB")).ToList();
    Console.WriteLine($"PROBE method={stats.Method} reason={reason} progress line(s)={printed.Count}");
    foreach (var line in printed.Take(2)) Console.WriteLine("PROBE   " + line);
    foreach (var line in printed.TakeLast(1)) Console.WriteLine("PROBE   " + line);
    Console.WriteLine($"PROBE stats after the pass: bytesRead={stats.BytesRead} reads={stats.ReadCalls}");
}
else if (mode == "b2-mixed-shared")
{
    // B2/B4: does the shared multi-track pass produce the same subtitles as extracting each track alone?
    var ordinals = args.Skip(2).Select(int.Parse).ToArray();
    MkvSubtitleExtractor.TryExtractMany(path, ordinals, out var many, out var reason, out var manyStats);
    foreach (var single in ordinals)
    {
        MkvSubtitleExtractor.TryExtract(path, single, out var alone, out _, null, out var aloneStats);
        var shared = many.TryGetValue(single, out var text) ? Count(text) : -1;
        var sameText = shared >= 0 && text == alone;
        Console.WriteLine($"PROBE ordinal {single}: shared={shared} alone={Count(alone)} "
            + $"sameCues={(shared == Count(alone) ? "yes" : "NO")} sameText={(sameText ? "yes" : "NO")} "
            + $"method={aloneStats.Method}/{manyStats.Method}");
    }

    Console.WriteLine($"PROBE shared pass: reason=\"{reason}\"");
}
else if (mode == "b24-collision")
{
    // B24: the per-pass dedup key is `position * 31 + relative`, so two different cue points can share a
    // key. This mode feeds the extractor a fixture built to contain such a pair and reports whether the
    // second cue survives.
    MkvSubtitleExtractor.TryExtract(path, 0, out var srt, out var reason, null, out var stats);
    var cues = Count(srt);
    var texts = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(b => string.Join("\n", b.Split('\n').Skip(2)))
        .ToList();
    Console.WriteLine($"PROBE cues={cues} expectedPairs={(args.Length > 2 ? args[2] : "?")} "
        + $"method={stats.Method} reason=\"{reason}\"");
    Console.WriteLine($"PROBE cue texts: {string.Join(" | ", texts.Select(t => t.Replace("\n", "/")))}");
}
else if (mode == "b25-short-read")
{
    // B25: a short read is accepted as the cue's text (`if (payloadRead < payloadLength)` truncates and
    // continues), so a share that answers with a partial read produces truncated subtitles instead of a
    // refusal. Run under the shortread shim, which makes every Nth read return half of what was asked.
    MkvSubtitleExtractor.TryExtract(path, 0, out var srt, out var reason, null, out var stats);
    Console.WriteLine($"PROBE shim={Environment.GetEnvironmentVariable("SHORTREAD") ?? "off"} ok={(srt.Length > 0)} "
        + $"cues={Count(srt)} method={stats.Method} reason=\"{reason}\"");
    var texts = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
        .Select(b => b.Split('\n').Skip(2).FirstOrDefault() ?? string.Empty).ToList();
    Console.WriteLine($"PROBE first texts: {string.Join(" | ", texts.Take(4))}");
    var truncated = texts.Count(t => t.Length is > 0 and < 12);
    Console.WriteLine($"PROBE texts under 12 characters (a truncation signature): {truncated} of {texts.Count}");
}
else
{
    Console.WriteLine("unknown mode: " + mode);
}

static int Count(string srt) => srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;
"""


def harness():
    """Builds the probe against the working tree's plugin project."""
    HARNESS.mkdir(parents=True, exist_ok=True)
    (HARNESS / 'p4probe.csproj').write_text(f"""<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>p4probe</AssemblyName>
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
    return HARNESS / 'bin' / 'Release' / 'net10.0' / 'p4probe.dll'


def fixture(name, extra):
    FIXTURES.mkdir(parents=True, exist_ok=True)
    path = FIXTURES / name
    if not path.exists():
        subprocess.run(['python3', str(GENERATOR), str(path), *extra], check=True, capture_output=True)
    return path


def run(dll, mode, path=None, extra=(), shim=False):
    env = dict(os.environ, TZ='UTC', DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
               LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu')
    if shim:
        if not SHORTREAD.exists():
            raise SystemExit(f'build the shim first: gcc -shared -fPIC -O2 -o {SHORTREAD} '
                             f'{REPO / "tests" / "backend" / "shortread.c"} -ldl')
        env['LD_PRELOAD'] = str(SHORTREAD)
        env['SHORTREAD_PREFIX'] = str(FIXTURES) + '/'
        env['SHORTREAD'] = 'every 4th read returns half'
    command = [DOTNET, str(dll), mode] + ([str(path)] if path else []) + [str(x) for x in extra]
    result = subprocess.run(command, capture_output=True, text=True, env=env, timeout=1800)
    return result.stdout.strip() or result.stderr[-800:]


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else 'all'
    dll = harness()

    if mode in ('b11-progress', 'all'):
        print('== B11: progress line during a metadata scan')
        print('   fixture: 520 clusters, 1 MB payload, no subtitle cue points (metadata-scan route)')
        print(run(dll, 'b11-progress', fixture('nosub.mkv', ['--clusters', '520', '--payload', '1',
                                                            '--sub-every', '2', '--no-sub-cues',
                                                            '--sub-position', 'late'])))
        print('   control: same size with cue points (cue-indexed route, must stay correct)')
        print(run(dll, 'b11-progress', fixture('episode.mkv', ['--clusters', '520', '--payload', '1',
                                                              '--sub-every', '2', '--sub-position', 'late'])))

    if mode in ('b2-mixed-shared', 'all'):
        print('== B2/B4: shared pass versus per-track extraction, on a half-located index')
        grouped = fixture('grouped.mkv', ['--clusters', '60', '--payload', '1', '--sub-every', '6',
                                          '--sub-tracks', '3', '--grouped-cues'])
        mixed = FIXTURES / 'grouped-mixed.mkv'
        if not mixed.exists():
            subprocess.run(['python3', str(PATCH_CUES), 'patch', str(grouped), str(mixed), '3', '2'],
                           check=True, capture_output=True)
        print(run(dll, 'b2-mixed-shared', mixed, extra=('0', '1', '2')))

    if mode in ('b24-collision', 'all'):
        print('== B24: the dedup key collision case')
        collision = FIXTURES / 'collision.mkv'
        if not collision.exists():
            built = subprocess.run(['python3', str(REPO / 'tests' / 'fixtures' / 'make_collision.py'),
                                    str(collision)], capture_output=True, text=True)
            if built.returncode != 0:
                print('   (fixture generator make_collision.py not available: '
                      + built.stderr.strip().splitlines()[-1][:160] + ')')
                collision = None
        if collision:
            print(run(dll, 'b24-collision', collision, extra=('2',)))

    if mode in ('b25-short-read', 'all'):
        print('== B25: a short read reaching the cue text (under the shortread shim)')
        plain = fixture('grouped.mkv', ['--clusters', '60', '--payload', '1', '--sub-every', '6',
                                        '--sub-tracks', '3', '--grouped-cues'])
        print('   without the shim:')
        print(run(dll, 'b25-short-read', plain))
        print('   with the shim:')
        print(run(dll, 'b25-short-read', plain, shim=True))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
