#!/usr/bin/env python3
"""Mutation verification for the Phase 0 characterization checks (tests/service_checks.cs).

A characterization check that still passes against a deliberately broken version of the code it covers is not
coverage. Each entry below breaks one production line the named check exists to protect, rebuilds the logictest
harness and records which of the new checks failed. The sources are always put back, and the exit status is
non-zero if any mutation left every check green.

The three clusters the map found uncovered are C4 (the process runners), C8 (the access-control surface, the
read models and the sweep) and C9 (engine status and install). C8's decisions are split across two files - the
service asks, ItemAccess answers - so an entry is (relative path, text to break, what it becomes), and the sweep
cache's persistence lives in SweepState.cs.

Run it after `python3 tests/run_checks.py` has built the harness (it reuses .tests-work):

    python3 tests/backend/mutation_phase0_checks.py             # every mutation
    python3 tests/backend/mutation_phase0_checks.py C9-d2 C4-untracked
"""

import json
import os
import pathlib
import re
import shutil
import signal
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
WORK = REPO / '.tests-work'
SERVICE = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
PROCESSES = 'Jellyfin.Plugin.SubSync/Services/SubSyncProcesses.cs'
ENGINE = 'Jellyfin.Plugin.SubSync/Services/FfSubSyncEngine.cs'
ITEM_ACCESS = 'Jellyfin.Plugin.SubSync/Services/ItemAccess.cs'
SWEEP_STATE = 'Jellyfin.Plugin.SubSync/Services/SweepState.cs'
SWEEP_HISTORY = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.SweepHistory.cs'

# Any Phase 0 characterization check counts: C4, C8 or C9, whatever the individual label.
LABELS = re.compile(r'FAIL  (C[0-9])')

# name -> (file, text to break, what it becomes)
MUTATIONS = {
    # ---------------- C4: the process runners ----------------
    'C4-argument-quoting': (PROCESSES,
                            'process.StartInfo.ArgumentList.Add(argument);',
                            'process.StartInfo.ArgumentList.Add("\\"" + argument + "\\"");'),
    'C4-string-quotes': (PROCESSES,
                         '            Arguments = arguments,',
                         '            Arguments = arguments.Replace("\\"", string.Empty),'),
    'C4-callback-exit': (PROCESSES,
                         '        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);\n\n        return process.ExitCode;',
                         '        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);\n\n        return 0;'),
    'C4-callback-lines': (PROCESSES,
                          '                onStderrLine?.Invoke(line);',
                          '                onStderrLine?.Invoke(line + "!");'),
    'C4-capture-concat': (PROCESSES,
                          '            var output = string.Concat(stdout, stderr).Trim();',
                          '            var output = stdout.Trim();'),
    'C4-untracked': (PROCESSES,
                     '            _liveProcesses.TryRemove(process.Id, out _);',
                     '            if (process.Id == int.MinValue) { _liveProcesses.TryRemove(process.Id, out _); }'),
    'C4-kill-on-cancel': (PROCESSES,
                          '        using var registration = cancellationToken.Register(() =>\n'
                          '        {\n'
                          '            try { process.Kill(entireProcessTree: true); }\n'
                          '            catch { /* process may have already exited */ }\n'
                          '        });',
                          '        using var registration = cancellationToken.Register(() => { /* mutant: the child is left running */ });'),
    'C4-precancel': (PROCESSES,
                     '        if (cancellationToken.IsCancellationRequested)\n'
                     '        {\n'
                     '            throw new OperationCanceledException(cancellationToken);\n'
                     '        }',
                     '        if (cancellationToken.IsCancellationRequested && Environment.TickCount == int.MinValue)\n'
                     '        {\n'
                     '            throw new OperationCanceledException(cancellationToken);\n'
                     '        }'),
    'C4-missing-binary': (PROCESSES,
                          '            process.StartInfo.WorkingDirectory = workingDir;\n'
                          '        }\n'
                          '\n'
                          '        process.Start();\n'
                          '        TrackChildProcess(process);',
                          '            process.StartInfo.WorkingDirectory = workingDir;\n'
                          '        }\n'
                          '\n'
                          '        try\n'
                          '        {\n'
                          '            process.Start();\n'
                          '        }\n'
                          '        catch (System.ComponentModel.Win32Exception)\n'
                          '        {\n'
                          '            return (127, string.Empty);\n'
                          '        }\n'
                          '\n'
                          '        TrackChildProcess(process);'),

    # ---------------- C9: engine status and install ----------------
    'C9-identity': (ENGINE,
                    ': "plugin instance unavailable",',
                    ': "no plugin instance",'),
    'C9-d2': (ENGINE,
              '    public bool EngineIsInstalled()\n'
              '        => EngineIsUsable(\n'
              '            ResolveFfSubSyncPath(),\n'
              '            File.Exists,\n'
              '            Environment.GetEnvironmentVariable("PATH"));',
              '    public bool EngineIsInstalled()\n'
              '        => File.Exists(ManagedFfSubSyncPath);'),
    'C9-note': (ENGINE,
                '            EngineNote = enginePresent\n'
                '                ? null\n'
                '                : EngineMissingNote(enginePath),',
                '            EngineNote = null,'),
    'C9-version': (ENGINE,
                   '                    status.FfSubSyncVersion = stdout.Trim();',
                   '                    status.FfSubSyncVersion = string.Empty;'),
    'C9-no-plugin': (ENGINE,
                     '?? throw new InvalidOperationException("Plugin not initialized.");',
                     '?? throw new InvalidOperationException("The plugin is not initialized.");'),
    'C9-gate-release': (ENGINE,
                        '            Interlocked.Exchange(ref _installing, 0);',
                        '            if (Environment.TickCount == int.MinValue) { Interlocked.Exchange(ref _installing, 0); }'),
    'C9-busy-guard': (ENGINE,
                      '        if (Interlocked.CompareExchange(ref _installing, 1, 0) == 1)',
                      '        if (Interlocked.CompareExchange(ref _installing, 1, 0) == 0)'),
    'C9-pip-failure': (ENGINE,
                       '            if (installExit != 0)\n'
                       '            {\n'
                       '                throw new InvalidOperationException($"pip install ffsubsync failed with exit code {installExit}.");\n'
                       '            }',
                       '            if (installExit != 0 && Environment.TickCount == int.MinValue)\n'
                       '            {\n'
                       '                throw new InvalidOperationException($"pip install ffsubsync failed with exit code {installExit}.");\n'
                       '            }'),
    'C9-no-binary': (ENGINE,
                     '            if (!File.Exists(ManagedFfSubSyncPath))\n'
                     '            {\n'
                     '                throw new InvalidOperationException("ffsubsync was installed but the binary was not found at the expected path.");\n'
                     '            }',
                     '            if (!File.Exists(ManagedFfSubSyncPath) && Environment.TickCount == int.MinValue)\n'
                     '            {\n'
                     '                throw new InvalidOperationException("ffsubsync was installed but the binary was not found at the expected path.");\n'
                     '            }'),
    'C9-non-root': (ENGINE,
                    '"python3 with venv support is missing and the Jellyfin process is not running as root, " +',
                    '"python3 with venv support is missing and this process has no root, " +'),
    'C9-python-catch': (ENGINE,
                        '        catch (System.ComponentModel.Win32Exception)\n'
                        '        {\n'
                        '            // python3 itself is not installed.\n'
                        '            return false;\n'
                        '        }',
                        '        catch (System.ComponentModel.Win32Exception)\n'
                        '        {\n'
                        '            throw;\n'
                        '        }'),
    'C9-version-source': (ENGINE,
                          '                var (exitCode, stdout) = await _processes.RunProcessCaptureAsync(ManagedFfSubSyncPath, "--version", null).ConfigureAwait(false);',
                          '                var (exitCode, stdout) = await _processes.RunProcessCaptureAsync(enginePath, "--version", null).ConfigureAwait(false);'),
    'C9-install-wrong-binary': (ENGINE,
                                '            var installExit = await _processes.RunProcessAsync(ManagedPipPath, "install ffsubsync \\"setuptools<81\\"", null, cancellationToken).ConfigureAwait(false);',
                                '            var installExit = await _processes.RunProcessAsync(ManagedPythonPath, "install ffsubsync \\"setuptools<81\\"", null, cancellationToken).ConfigureAwait(false);'),

    # ---------------- C8: the access-control surface ----------------
    'C8-allfolders': (SWEEP_HISTORY,
                      '        var allFolders = user.HasPermission(PermissionKind.EnableAllFolders);',
                      '        var allFolders = false;'),
    'C8-allows': (SWEEP_HISTORY,
                  '        var allowed = ItemAccess.Allows(allFolders, folders, ancestors);',
                  '        var allowed = ItemAccess.Allows(true, folders, ancestors);'),
    'C8-item-null': (SWEEP_HISTORY,
                     '        var item = _libraryManager.GetItemById(itemId);\n'
                     '        if (item is null)\n'
                     '        {\n'
                     '            return false;\n'
                     '        }',
                     '        var item = _libraryManager.GetItemById(itemId);\n'
                     '        if (item is null)\n'
                     '        {\n'
                     '            return true;\n'
                     '        }'),
    'C8-user-null': (SWEEP_HISTORY,
                     '        var user = _userManager.GetUserById(userId);\n'
                     '        if (user is null)\n'
                     '        {\n'
                     '            return false;\n'
                     '        }',
                     '        var user = _userManager.GetUserById(userId);\n'
                     '        if (user is null)\n'
                     '        {\n'
                     '            return true;\n'
                     '        }'),
    'C8-bulk-unresolved': (SWEEP_HISTORY,
                           '            return itemIds.FirstOrDefault();',
                           '            return null;'),
    'C8-bulk-first': (SWEEP_HISTORY,
                      '            if (!CanUserSeeItem(userId, itemId))\n'
                      '            {\n'
                      '                return itemId;\n'
                      '            }',
                      '            if (false && !CanUserSeeItem(userId, itemId))\n'
                      '            {\n'
                      '                return itemId;\n'
                      '            }'),
    'C8-ancestors-dashed': (SWEEP_HISTORY,
                            '            ids.Add(parent.Id.ToString("N"));',
                            '            ids.Add(parent.Id.ToString());'),
    'C8-admin-default': (SWEEP_HISTORY,
                         '            return _userManager?.GetUserById(userId)?.HasPermission(PermissionKind.IsAdministrator) ?? false;',
                         '            return _userManager?.GetUserById(userId)?.HasPermission(PermissionKind.IsAdministrator) ?? true;'),
    'C8-admin-catch': (SWEEP_HISTORY,
                       '            // Fails closed: an account that cannot be resolved is not an administrator.\n'
                       '            _logger.LogDebug(ex, "Could not resolve whether {UserId} is an administrator", userId);\n'
                       '            return false;',
                       '            _logger.LogDebug(ex, "Could not resolve whether {UserId} is an administrator", userId);\n'
                       '            return true;'),
    'C8-allows-empty-folders': (ITEM_ACCESS,
                                '        if (allowedFolderIds is null || allowedFolderIds.Count == 0)\n'
                                '        {\n'
                                '            return false;\n'
                                '        }',
                                '        if (allowedFolderIds is null || allowedFolderIds.Count == 0)\n'
                                '        {\n'
                                '            return true;\n'
                                '        }'),
    # The condition cannot be broken here: `itemKind is null` is what narrows the type for the sentence below,
    # so a mutant that drops the branch does not compile. The refused sentence is the observable that changes.
    'C8-classify-missing': (SWEEP_HISTORY,
                            'return new SyncTarget { ItemId = itemId, Refusal = "The item was not found or is not available to this account." };',
                            'return new SyncTarget { ItemId = itemId, Refusal = "The item was not found." };'),
    'C8-classify-notvideo': (SWEEP_HISTORY,
                             '$"Item {itemId} is a {itemKind.ToLowerInvariant()}, not a video: pick the episodes themselves, "',
                             '$"Item {itemId} is a {itemKind.ToLowerInvariant()}, which cannot be synced: pick the episodes themselves, "'),
    'C8-listing-plugin-output': (SWEEP_HISTORY,
                                 '                    IsPluginOutput = MediaStreamMap.IsOwnSidecar(s)',
                                 '                    IsPluginOutput = false'),
    'C8-listing-image': (SWEEP_HISTORY,
                         '                    UnsupportedReason = LanguageSupport.ImageBasedRefusal(s.Codec),',
                         '                    UnsupportedReason = null,'),
    'C8-listing-nonvideo': (SWEEP_HISTORY,
                            '        var item = _libraryManager.GetItemById(itemId);\n'
                            '        if (item is not Video video)\n'
                            '        {\n'
                            '            return null;\n'
                            '        }',
                            '        var item = _libraryManager.GetItemById(itemId);\n'
                            '        if (item is not Video video)\n'
                            '        {\n'
                            '            return new List<SubtitleInfo>();\n'
                            '        }'),

    # ---------------- C8: the sweep ----------------
    'C8-sweep-scanned': (SWEEP_HISTORY,
                         '        result.ScannedItems = videos.Count;',
                         '        result.ScannedItems = 0;'),
    'C8-sweep-cached': (SWEEP_HISTORY,
                        '                    // Same source content, synced output still on disk.\n'
                        '                    result.SkippedCached++;',
                        '                    // Same source content, synced output still on disk.\n'
                        '                    result.SkippedOther++;'),
    'C8-sweep-failstreak': (SWEEP_HISTORY,
                            '                    && entry.FailStreak >= failStreakLimit)',
                            '                    && entry.FailStreak >= failStreakLimit + 5)'),
    'C8-sweep-state-load': (SWEEP_STATE,
                            '            var entries = JsonSerializer.Deserialize<Dictionary<string, SweepEntry>>(File.ReadAllText(filePath))\n'
                            '                ?? new Dictionary<string, SweepEntry>(StringComparer.Ordinal);',
                            '            var entries = new Dictionary<string, SweepEntry>(StringComparer.Ordinal);'),
    'C8-record-flag': (SWEEP_HISTORY,
                       '            _sweepState.Value.Record(ctx.Stream.Path, ok, outputPath, error);',
                       '            _sweepState.Value.Record(ctx.Stream.Path, true, outputPath, error);'),
    'C8-record-external-only': (SWEEP_HISTORY,
                                '            if (!ctx.Stream.IsExternal || string.IsNullOrWhiteSpace(ctx.Stream.Path))\n'
                                '            {\n'
                                '                return; // skip/fail cache tracks external subtitle files only\n'
                                '            }',
                                '            if (ctx.Stream.IsExternal || string.IsNullOrWhiteSpace(ctx.Stream.Path))\n'
                                '            {\n'
                                '                return; // skip/fail cache tracks external subtitle files only\n'
                                '            }'),
}


def environment():
    """The environment the suite runs its harness in, with the stand-in engine on PATH."""
    env = dict(
        os.environ,
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT='1',
        LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu',
        TZ='UTC',
    )
    env['PATH'] = '/opt/data/.dotnet' + os.pathsep + env.get('PATH', '')
    sys.path.insert(0, str(REPO / 'tests'))
    import run_checks
    engine_dir, fake_ffmpeg = run_checks.prepare_fake_engine(str(REPO / '.tests-work' / 'fakes'))
    env['SUBSYNC_FAKE_ENGINE'] = engine_dir
    env['SUBSYNC_FAKE_FFMPEG'] = fake_ffmpeg
    env['PATH'] = engine_dir + os.pathsep + env['PATH']
    return env


def run_harness(env):
    """Rebuilds and runs the harness; returns (stdout, error)."""
    for child in WORK.iterdir():
        if child.is_dir() and any(child.glob('*.csproj')):
            shutil.rmtree(child, ignore_errors=True)
    build = subprocess.run(
        ['dotnet', 'build', '-c', 'Release', '--nologo', '-v', 'q'], cwd=WORK, capture_output=True, text=True, env=env)
    if build.returncode != 0:
        return None, (build.stdout + build.stderr)[-800:]
    run = subprocess.run(
        ['dotnet', str(WORK / 'bin' / 'Release' / 'net10.0' / 'logictest.dll')],
        cwd=WORK, capture_output=True, text=True, env=env, timeout=1800)
    return run.stdout + run.stderr, None


def failures(output):
    return [line.split('  ', 1)[1].split('  [')[0].strip() for line in output.splitlines() if LABELS.match(line)]


def main():
    if not (WORK / 'Program.cs').exists():
        print('run `python3 tests/run_checks.py` first: the harness is built from .tests-work/Program.cs')
        return 2

    # Refuse to run against a tree that already carries a broken line: a driver killed mid-mutation
    # leaves exactly that, and this run's baseline would then report a false red (FIX_PLAN T1).
    sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
    from mutation_residue_guard import check as sources_are_clean
    if not sources_are_clean():
        print('refusing to run: the production source differs from HEAD')
        return 2

    env = environment()
    wanted = sys.argv[1:] or list(MUTATIONS)
    originals = {}

    def source_of(relative):
        path = REPO / relative
        if relative not in originals:
            originals[relative] = path.read_text(encoding='utf-8')
        return path, originals[relative]

    # The finally below restores the source on a clean exit and on an exception, but not on a signal:
    # a SIGKILL cannot be caught (hence the check above), and SIGINT/SIGTERM would otherwise leave the
    # mutation in the tree.
    def put_sources_back(signum, _frame):
        for relative, text in originals.items():
            try:
                (REPO / relative).write_text(text, encoding='utf-8')
            except OSError as exc:
                print(f'signal {signum}: could not restore {relative}: {exc}')
        put_back = all((REPO / relative).read_text(encoding='utf-8') == text for relative, text in originals.items())
        print(f'\nsignal {signum}: source put back: {put_back}')
        raise SystemExit(3)

    for handled in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
        signal.signal(handled, put_sources_back)

    baselines = {}
    for relative, _, _ in MUTATIONS.values():
        path, text = source_of(relative)
        baselines[relative] = text

    baseline, error = run_harness(env)
    if baseline is None:
        print('baseline build failed:', error)
        return 2
    if failures(baseline):
        print('baseline is not green:')
        for line in failures(baseline):
            print(' ', line)
        return 2
    passing = len([l for l in baseline.splitlines() if l.startswith('PASS') and LABELS.match('FAIL' + l[4:])])
    print(f'baseline: {passing} Phase 0 characterization check(s) pass, 0 fail')

    results = {}
    try:
        for name in wanted:
            if name not in MUTATIONS:
                print(f'{name}: unknown mutation')
                continue
            relative, old, new = MUTATIONS[name]
            path, original = source_of(relative)
            if old not in original:
                results[name] = 'ANCHOR NOT FOUND'
                print(f'{name}: ANCHOR NOT FOUND (the source moved)')
                continue
            path.write_text(original.replace(old, new), encoding='utf-8')
            out, error = run_harness(env)
            path.write_text(original, encoding='utf-8')
            if out is None:
                results[name] = 'BUILD FAILED'
                print(f'{name}: the mutation does not compile')
            elif 'ALL PASS' not in out and not out.rstrip().endswith(('FAILURE(S)', 'FAILURE(S) ')):
                # A check that throws takes the harness down before it can print FAIL, which would read as a
                # harmless mutation. The run is only complete when it reached its own summary line.
                results[name] = 'HARNESS ABORTED'
                print(f'{name}: HARNESS ABORTED (no summary line - a check threw instead of failing)')
            else:
                caught = failures(out)
                results[name] = caught
                state = 'CAUGHT' if caught else 'MISSED'
                print(f'{name}: {state} -> ' + '; '.join(f[:70] for f in caught))
    finally:
        for relative, original in originals.items():
            (REPO / relative).write_text(original, encoding='utf-8')

    restored = all((REPO / relative).read_text(encoding='utf-8') == text for relative, text in originals.items())
    print(f'\nsources put back: {restored}')
    print(json.dumps(results, indent=1))
    missed = [name for name, value in results.items()
              if value == [] or value in ('ANCHOR NOT FOUND', 'BUILD FAILED', 'HARNESS ABORTED')]
    if missed:
        print('NOT VERIFIED BY ANY CHECK: ' + ', '.join(missed))
    return 1 if (missed or not restored) else 0


if __name__ == '__main__':
    sys.exit(main())
