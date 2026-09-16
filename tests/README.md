# Checks

Regression checks for the plugin. They exist because most of this project's behaviour is
invisible: how much of a file the reader touches, whether a worker waits for another worker,
whether the configured worker count is honoured, whether a progress bar tells the truth. None of
that shows up by looking at the interface, and each of those was a real bug that reached a beta
release before a check existed for it.

## Running them

```bash
python3 tests/run_checks.py
```

Requires the **.NET 9 SDK** and **python3**. Nothing else: no ffsubsync binary, no media library,
no Jellyfin instance. Fixtures are synthetic Matroska files built on the fly, so the checks never
touch real media.

`dotnet` is found on `PATH`, or from `DOTNET_ROOT`, `~/.dotnet`, `/usr/share/dotnet`,
`/opt/dotnet`, `/opt/data/.dotnet`; set `DOTNET=/path/to/dotnet` to be explicit. Scratch files
(the generated harness project and fixtures) go to `.tests-work/`, which is git-ignored;
`TESTS_WORK` overrides the location.

## What is covered

**Extraction cost.** How many bytes and read calls the Matroska reader needs for a file with
subtitle cue points, without them, with a cue index but no block offsets, and with no cue index at
all. The indexed path must stay a small fraction of the file, and must stay cheaper than walking
clusters.

**Scheduling.** A job occupies a slot rather than a group: an empty pool fills every slot, one
free slot starts exactly one job, a full pool starts nothing, and a media file already being read
is not given a second worker. Worker counts 1, 3, 16, 32 and 64 are honoured as written — a silent
cap at 8 was once the reason a 32-worker setting ran eight.

**Language matching and configuration.** ISO 639-1/639-2 normalisation, the language filter,
mode selection (auto/normal/parallel/fast/ultimate), and speech-cache keying.

**Progress accounting.** A progress line's `done/total` counters must map to a fraction, so the
bar follows real work; a line without counters, or with a zero total, must yield nothing rather
than a division error.

**The clusters with no coverage (Phase 0, `tests/service_checks.cs`).** The map of `SubSyncService`
(`knowledge/SUBSYNCSERVICE_MAP.md`) found three groups of code nothing exercised, and they are covered
before any class extraction touches them:

- the **process runners** (`RunProcessArgumentListAsync`, `RunProcessAsync`,
  `RunProcessWithStderrCallbackAsync`, `RunProcessCaptureAsync`): what each style does with an argument
  (the `ArgumentList` runners pass it through untouched; the string runners let the runtime split and
  group it, which is why `EscapeArg`'s quoting has to survive), exit codes, the stderr callback, the
  capture runner's concatenated output, cancellation killing the child rather than only the wait, and
  the tracked-process table being left empty. `RunCapturedAsync` was dead code and is gone;
- the **access-control surface** (`CanUserSeeItem`, `FirstItemNotVisibleTo`, `AncestorIds`,
  `IsAdministrator`) and the read models (`ListSubtitles`, `ClassifySyncTarget`): a user with all-folders
  permission may act anywhere, a user holding another library is denied the item, an item under no
  library at all is denied to everyone, an unreadable folder list denies, an unknown item or account
  denies, and the bulk check names the first item the caller may not act on. Jellyfin's own interfaces are
  faked with `DispatchProxy`, so no check needs a server;
- **engine status and install** (`GetInstallationStatusAsync`, `InstallFfSubSyncAsync`,
  `EnsurePythonAvailableAsync`): a configured engine path that does not exist reports not-installed and
  names the path (D2's own case, with a managed binary present as the trap), a managed binary that is
  there reports installed with the version it answers, the install gate refuses a second attempt and is
  released by a failed one, a non-zero pip is reported with its exit code, an install whose binary never
  appears is refused, and a missing python3 on a non-root process is refused with the cause and the fix.
  The install runs against stand-in python3/pip scripts: no network, no root, no venv.

Every one of these is mutation-verified: `tests/backend/mutation_phase0_checks.py` breaks one production
line per check and reports which check caught it.

**Where the service's code lives, and why the checks do not care.** The map's Phase 1 and 2 split
`SubSyncService` into real classes (`SubSyncProcesses`, `FfSubSyncEngine`, `AlignmentMetrics`,
`MediaStreamMap`, `SyncedTargetNaming`) and, for the clusters that share the run-state, into
partial-class parts of the same class (`SubSyncService.Queue.cs`, `.Scheduler.cs`, `.Extraction.cs`,
`.JobPipeline.cs`, `.SweepHistory.cs`). Source-shape checks - the pins that read the C# as text - read
`service_classes()`, the aggregate of the main file and every part, so a pin stays pointed at the
behaviour and not at a file boundary. That is also why a *move* is the one change that can make a pin
fail for a reason that has nothing to do with the code: if a pin reads one path directly (or the
aggregate misses a part), it searches a string the code has left and reports a regression instead of a
gap in the check. Nothing in this suite does that now; keep it that way.

The moves themselves are reproducible and self-reporting:
`python3 tests/backend/partial_split.py <cluster> --dry-run` prints the regions it would cut, the purity
count (each region byte-identical in the new file) and the subtraction check (the main file is exactly
the original minus the moved lines). `tests/backend/retarget_mutations.py` moves each mutation anchor to
the file its line now lives in and reports anything it cannot resolve rather than guessing.

## Adding a check

Add the assertion to `tests/run_checks.py` (C# checks live in the `PROGRAM` string, Python checks
drive fixtures). Write the check for the *bug*, not for the fix: state what must not regress, so a
later change that reintroduces it fails loudly.

Keep fixtures synthetic. A check that needs a 40 GB remux or a running Jellyfin will be skipped
the moment it is inconvenient, and a skipped check protects nothing.
