# SubSyncService.cs map — investigation only, no code changes

Read-only static map of `Jellyfin.Plugin.SubSync/Services/SubSyncService.cs`, produced 2026-09-16 against the
working tree at `c85046c` (2.0.60, beta). Same discipline as `RUNSYNCJOB_MAP.md`: every claim below comes from
reading the file (or a scripted parse of it, §9), never from a FIX_PLAN row's wording. No code was changed; this
document is the only file written.

> **Revision note (added with the Phase 0 tests, commit after `4fcd12d`).** `RunCapturedAsync` - the fifth
> runner this map counted in C4 - was confirmed dead (no call site in the plugin, the suite, the pages or the
> scripts) and **removed in `4fcd12d`**, 52 lines. The line numbers in this document are from the revision it
> was written against, so **every reference after line 9181 is now 52 lines lower** (the file is 9 594 lines
> instead of 9 646, and `RunProcessAsync`, which follows the removed method, sits at 9181 rather than 9233).
> C4 is therefore **four** runners, and the four are now covered by checks: `tests/service_checks.cs`, with
> its mutations in `tests/backend/mutation_phase0_checks.py`.

> **Phase 1 done (2026-09-16): the four separable clusters left the class.** The §6 recommendation was
> executed in the order §7 proposed, one commit each, and the §1-§2 line numbers are now **historical** -
> the code they point at lives in its own file:
>
> | cluster | became | commit | lines | what the move cost |
> |---|---|---|---|---|
> | C7 (224) | `MediaStreamMap` + `SyncedTargetNaming` (two classes: streams vs. the files written beside them) | `304d14b` | 203 + 133 | 10 of 12 blocks byte-identical, 2 one keyword (`private` -> `internal`) |
> | C6 (326) | `AlignmentMetrics` (14 methods, the two `record struct`s, 4 consts) | `55c066f` | 604 | 19 of 20 byte-identical; `RescaleOntoReferenceSpan` changed 4 lines (it reported through the service's logger, now a parameter) |
> | C4 (299) | `SubSyncProcesses` (4 runners + the tracked-process table) | `86e745e` | 386 | 4 byte-identical, 4 one keyword each, 1 one doc line (a `<see cref>` cannot cross classes), the C7 probe 6 lines |
> | C9 (410) | `FfSubSyncEngine` (resolver, bundled-version cache, status, installer) | `b4f525e` | 577 | 18 of 21 byte-identical incl. the constructor block; 2 one keyword, `GetInstallationStatusAsync` 2 lines (the worker summary is the service's own setting) |
>
> **What did not leave, and why:** `SyncPhaseLabel` (a phase label the job pipeline reads, not a stream or path
> question - it goes with C5), `SuspiciousReferenceShiftFraction` (the cross-check's threshold) and the
> walk/read-policy constants (C2's), `WaitForLanes`/`WaitForTasks` (they wait on the service's *lane tasks*,
> not on processes), and the seven one-line members the service keeps as a facade over `FfSubSyncEngine`
> because the controller, the settings page and the suite call them by name. The clusters that share the
> run-state (C1, C2, C3, C5, C8, C10) are untouched: they are §7's Phase 2-4 work, and it is why this file is
> 7 811 lines rather than 3 000.
>
> **Re-verified after the split, not assumed:** the suite is green at **1008 checks**, and both mutation
> drivers were re-pointed at the files the code moved to and re-run - **43 of 43** Phase 0 mutations still
> caught (9 re-pointed to `SubSyncProcesses.cs`, 13 to `FfSubSyncEngine.cs`, 3 anchors re-quoted), and
> **25 of 25** RunSyncJob mutations still caught (4 anchors followed `SyncedTargetNaming`,
> `AlignmentMetrics.LooksLikeSignsTrack`/`IsRescaleAcceptable`/`AlignmentHoldsAgainstAudio`, and the driver
> gained per-mutation file support so a mutation names the file its line lives in).


## 0. Shape facts

- **9 647 lines.** `SubSyncService` is 353–9646 = **9 293 lines (96 % of the file)**; six DTOs (20–351) and eight
  nested types sit above/beside it: `WavePolicy` 1980, `SyncChange` 4992, `SegmentStructure` 5022,
  `BatchCreation` 6308, `SyncWriteOutcome` 6667, `ReferenceResolution` 6857, `SyncQueueConflictException` 7368,
  `SyncTarget` 7388.
- This one file is **44 % of the plugin's 21 560 C# lines**; the next largest is `MkvSubtitleExtractor.cs`
  (3 245, 15 %), then `Api/SubSyncController.cs` (1 181).
- **192 declaration sites → 181 methods** across ~172 distinct names (overloads: `SelectWave` 2819/3273,
  `WalkCapForProfile` 3640/3664/3691, `ResolveEnqueueTraceMs` 2419/2442), plus 7 expression-bodied
  properties/consts (`ManagedFfSubSyncPath` 670, `ManagedPipPath` 675, `ManagedPythonPath` 680,
  `ConfiguredLaneLimit` 2385, `ConfiguredWorkerLimit` 3866, `EffectiveWorkerLimit` 3856,
  `AudioReferenceVadName` 5302).
- **45 fields** (43 instance + `_ceilingLogged` 3199 and `_lastLoggedWorkerLimit` 3868 static).
- Method bodies total **~6 800 lines — 74 % of the class**. Everything else is fields, doc comments, blanks.
- Size profile: one giant (`RunSyncJob` 5400, **904**, already extracted by `RUNSYNCJOB_MAP.md` §10–§15), then
  `ExtractEmbeddedAsync` 8624 (254), `PumpOnceAsync` 4026 (248), `EnqueueSyncTimed` 1242 (203),
  `RunEngineAttemptAsync` 6331 (188), `ResolveReferenceAsync` 6968 (174), `SweepLibraryAsync` 4390 (162),
  `ExtractLaneAsync` 2478 (142), `WriteSyncedSubtitleAsync` 6535 (122), `ExtractSubtitleWithProgressAsync` 8897
  (99), `SelectWave` 2819 (97), `TryReadReferenceTextAsync` 8489 (97). **Nothing else reaches 100 lines.** So the
  file is not one long method any more: it is 181 short-to-medium methods sharing 45 fields.
- The single largest structural fact: `_jobs` 464, `_jobContexts` 574, `_runOrder` 488 (a plain `List<>`) and
  `_jobCancellation` 579 are read or written by **34 methods covering 2 680 lines**, under 15 `lock (_queueLock)`
  sites (1339, 1401, 1556, 1802, 1835, 2397, 2464, 2630, 3902, 4058, 4241, 4254, 4279, 8267, 8425).

## 1. Responsibility inventory

Every member of the class, grouped by what it does. Sizes include the signature and the closing brace. The last
two lines of each block are the fields that group touches (from §2). The clusters are the ten the code actually
falls into — three of the prompt's expected clusters turned out to be two things each (§1.1).

### C1 queue/dispatch/job lifecycle — 23 methods, 634 lines

- reads: `_jobCancellation`, `_jobs`, `_laneTasks`, `_libraryManager`, `_liveProcesses`, `_logger`, `_pumpTask`, `_queueLock`, `_runOrder`
- writes: `_jobContexts`, `_jobs`, `_laneStop`, `_liveProcesses`, `_runOrder`

| method | first line | lines |
|---|---|---|
| `StartSync` | 1187 | 12 |
| `EnqueueSync` | 1216 | 4 |
| `EnqueueSyncTimed` | 1242 | 203 |
| `CreateBatch` | 1455 | 90 |
| `CancelBatch` | 1551 | 61 |
| `KillAll` | 1830 | 89 |
| `SafeHasExited` | 1923 | 11 |
| `LiveProcessCount` | 1937 | 1 |
| `GetActive` | 1944 | 6 |
| `GetBatchJobs` | 1956 | 6 |
| `GetBatchIds` | 1967 | 8 |
| `GetJob` | 4368 | 4 |
| `GetAllJobs` | 4377 | 4 |
| `FindDuplicate` | 7356 | 4 |
| `KillJobs` | 7538 | 36 |
| `ActiveJobCounts` | 7585 | 3 |
| `KillChildProcesses` | 7622 | 23 |
| `TrackChildProcess` | 7650 | 4 |
| `WaitForLanes` | 7660 | 4 |
| `JobsToEvict` | 7770 | 31 |
| `CountExited` | 7816 | 20 |
| `HasCompletedSync` | 7991 | 7 |
| `LogPluginCancellation` | 9409 | 3 |

### C2 scheduler/pump + wave & walk policy — 32 methods, 900 lines

- reads: `_disposing`, `_jobContexts`, `_jobs`, `_lastPassFinishedUtc`, `_logger`, `_passInFlight`, `_queueLock`, `_runOrder`, `_sweepState`, `_wakePump`
- writes: `_ceilingLogged`, `_lastLoggedWorkerLimit`, `_pumpFaults`, `_pumpTask`, `_referenceGates`, `_wakePump`

| method | first line | lines |
|---|---|---|
| `ResolveModeForBatch` | 2055 | 29 |
| `JobNeedsHeavyIo` | 2089 | 14 |
| `QueuedReason` | 2185 | 19 |
| `SelectWave` | 2819 | 97 |
| `PlanStart` | 3104 | 50 |
| `HasRoomOnItsVolume` | 3165 | 33 |
| `LogCeilingHeld` | 3207 | 11 |
| `ClaimHeavy` | 3220 | 10 |
| `TryTake` | 3240 | 24 |
| `SelectWave` | 3273 | 6 |
| `MediaLengthOf` | 3314 | 11 |
| `WalkCapOfPath` | 3335 | 19 |
| `ProbeOffset` | 3399 | 9 |
| `ProbeVolumeIfUnmeasured` | 3421 | 72 |
| `VolumesOtherThan` | 3507 | 2 |
| `WalkCapForProfile` | 3640 | 2 |
| `WalkCapForProfile` | 3664 | 2 |
| `WalkCapForProfile` | 3691 | 85 |
| `CapName` | 3777 | 1 |
| `CapFromReadCost` | 3779 | 24 |
| `Backed` | 3807 | 1 |
| `Backing` | 3812 | 7 |
| `CapFromWalkThroughput` | 3820 | 16 |
| `NormalizeWorkers` | 3849 | 2 |
| `LogWorkerLimit` | 3874 | 15 |
| `WakePump` | 3893 | 19 |
| `RunPumpLoopAsync` | 3933 | 28 |
| `PumpFaultBackoffMs` | 3967 | 2 |
| `PumpAsync` | 3970 | 8 |
| `NotePumpFault` | 3989 | 27 |
| `PumpOnceAsync` | 4026 | 248 |
| `CountQueued` | 4277 | 7 |

### C3 extraction (lanes + extraction + caches) — 26 methods, 1031 lines

- reads: `_disposing`, `_extractTried`, `_extractWake`, `_extractedMissGate`, `_extractedMissMemo`, `_extractedReady`, `_extractedText`, `_jobContexts`, `_laneStop`, `_laneTasks`, `_logger`, `_passInFlight`, `_queueLock`, `_runOrder`, `_speechCachedGate`
- writes: `_extractTried`, `_extractWake`, `_extractedMissMemo`, `_extractedOrder`, `_extractedReady`, `_extractedText`, `_laneTasks`, `_lastPassFinishedUtc`, `_passInFlight`, `_referenceOrdinals`, `_speechCachedMemo`

| method | first line | lines |
|---|---|---|
| `CacheSaysMissing` | 2258 | 8 |
| `RememberCacheMiss` | 2269 | 15 |
| `ExtractionReady` | 2291 | 64 |
| `ExtractedKeyOf` | 2360 | 1 |
| `WakeExtractor` | 2389 | 20 |
| `LaneAlive` | 2460 | 10 |
| `ExtractLaneAsync` | 2478 | 142 |
| `NextFileToExtract` | 2626 | 60 |
| `ReferenceOrdinalsFor` | 2700 | 43 |
| `SpeechIsCached` | 2744 | 44 |
| `ExtractionFraction` | 3064 | 18 |
| `IsCancelledExtraction` | 7703 | 3 |
| `StopChainIfCancelled` | 7714 | 10 |
| `SiblingOrdinals` | 8421 | 30 |
| `TryTakeExtracted` | 8457 | 2 |
| `TryPeekExtracted` | 8470 | 2 |
| `TryReadReferenceTextAsync` | 8489 | 97 |
| `CacheExtracted` | 8591 | 10 |
| `ExtractedKey` | 8602 | 1 |
| `ExtractEmbeddedAsync` | 8624 | 254 |
| `ExtractSubtitleWithProgressAsync` | 8897 | 99 |
| `DiscardPartialExtraction` | 9007 | 5 |
| `ParseFfmpegProgressSeconds` | 9027 | 31 |
| `DescribeExtraction` | 9060 | 14 |
| `ExtractSubtitle` | 9075 | 32 |
| `FfmpegError` | 9108 | 16 |

### C4 process execution — 5 methods, 299 lines

- reads: `_logger`
- writes: `_liveProcesses`

| method | first line | lines |
|---|---|---|
| `RunProcessArgumentListAsync` | 9125 | 55 |
| `RunCapturedAsync` | 9181 | 51 |
| `RunProcessAsync` | 9233 | 59 |
| `RunProcessWithStderrCallbackAsync` | 9429 | 95 |
| `RunProcessCaptureAsync` | 9528 | 39 |

### C5 job pipeline (RunSyncJob + helpers) — 32 methods, 2159 lines

- reads: `_jobContexts`, `_jobs`, `_libraryManager`, `_libraryMonitor`, `_logger`, `_refreshGate`, `_speechGates`
- writes: `_jobCancellation`, `_referenceGates`, `_speechGates`

| method | first line | lines |
|---|---|---|
| `ReleaseSpeechGate` | 2210 | 15 |
| `Join` | 2230 | 2 |
| `Join` | 2799 | 4 |
| `RunSyncJobWithContext` | 4298 | 64 |
| `LogEngineAlignment` | 5276 | 15 |
| `VadForReference` | 5298 | 2 |
| `AudioReferenceVadName` | 5302 | 1 |
| `LogVadOverride` | 5314 | 12 |
| `TryParseEngineScore` | 5341 | 24 |
| `TryParseEngineOffset` | 5372 | 24 |
| `RunSyncJob` | 5400 | 904 |
| `RunEngineAttemptAsync` | 6331 | 188 |
| `WriteSyncedSubtitleAsync` | 6535 | 122 |
| `DescribeCompletedSync` | 6688 | 67 |
| `AnnounceCompletedAsync` | 6765 | 80 |
| `PrepareAudioReferenceAsync` | 6896 | 61 |
| `ResolveReferenceAsync` | 6968 | 174 |
| `MarkCancelled` | 7148 | 8 |
| `FailJobAndRollBack` | 7165 | 28 |
| `CleanUpAfterJob` | 7205 | 55 |
| `RefuseJob` | 7275 | 10 |
| `CompleteAlreadyInSync` | 7292 | 10 |
| `CompleteAsNoChange` | 7311 | 16 |
| `EngineCouldNotReadReference` | 7339 | 4 |
| `ReplaceExternalSubtitle` | 7878 | 15 |
| `NextBackupPath` | 7903 | 19 |
| `SafeDelete` | 7926 | 5 |
| `ParseFfSubSyncStderr` | 7944 | 43 |
| `FramerateArgs` | 8296 | 15 |
| `BuildFfSubSyncArgs` | 8312 | 78 |
| `PiecewiseArgs` | 8401 | 7 |
| `VerifyStretchAgainstAudioAsync` | 9317 | 87 |

### C6 alignment measurement (pure) — 14 methods, 326 lines

- reads: `_logger`
- writes: —

| method | first line | lines |
|---|---|---|
| `RulersDisagree` | 3522 | 3 |
| `RulerSpreadTooWide` | 3537 | 2 |
| `AlignmentHoldsAgainstAudio` | 4742 | 10 |
| `IsTargetOffTheVideo` | 4767 | 18 |
| `TryParseSrtTime` | 4792 | 24 |
| `RescaleOntoReferenceSpan` | 4835 | 92 |
| `ParseSrtCueStarts` | 4931 | 46 |
| `MeasureSegmentStructure` | 5050 | 43 |
| `PiecewiseHolds` | 5115 | 5 |
| `IsRescaleAcceptable` | 5147 | 22 |
| `MeasureSyncChange` | 5176 | 42 |
| `LooksLikeSignsTrack` | 5231 | 2 |
| `CountSubtitleCues` | 5240 | 15 |
| `DescribeSyncChange` | 5397 | 2 |

### C7 media probing, stream mapping & naming — 14 methods, 224 lines

- reads: —
- writes: —

| method | first line | lines |
|---|---|---|
| `IsOwnSidecar` | 1036 | 2 |
| `EmbeddedSubtitleOrdinal` | 2930 | 22 |
| `SubtitleStreamOrdinal` | 2958 | 16 |
| `SyncPhaseLabel` | 2986 | 11 |
| `CanWriteTo` | 3010 | 29 |
| `RequireWritable` | 3045 | 10 |
| `ResolveContainerSubtitleIndexAsync` | 4587 | 28 |
| `ParseProbeSubtitleCodecs` | 4622 | 14 |
| `SelectReferenceStream` | 4657 | 34 |
| `IsTextSubtitleCodec` | 4692 | 17 |
| `ParseProbeSubtitleIndexes` | 4714 | 14 |
| `SyncedTargetName` | 7602 | 8 |
| `IsJobScratchDirectory` | 7847 | 4 |
| `IsInsideRoot` | 7858 | 15 |

### C8 API read models, sweep & history — 21 methods, 615 lines

- reads: `_jobContexts`, `_jobs`, `_libraryManager`, `_logger`, `_passInFlight`, `_queueLock`, `_runOrder`, `_sweepState`, `_userManager`
- writes: `_historyOnlyJobs`, `_historySavedUtc`, `_historySignature`, `_jobs`, `_lastProgressLineUtc`

| method | first line | lines |
|---|---|---|
| `CanUserSeeItem` | 367 | 45 |
| `FirstItemNotVisibleTo` | 420 | 20 |
| `AncestorIds` | 446 | 10 |
| `ListSubtitles` | 1044 | 46 |
| `ListSubtitlesBulk` | 1104 | 71 |
| `DescribeProgress` | 1636 | 2 |
| `LogPluginProgress` | 1642 | 2 |
| `RestoreBatchHistory` | 1648 | 72 |
| `SnapshotBatchHistory` | 1721 | 35 |
| `MaybePersistBatchHistory` | 1760 | 22 |
| `MaybeLogProgress` | 1785 | 38 |
| `ApplySettingsNow` | 2372 | 8 |
| `ConfiguredLaneLimit` | 2385 | 2 |
| `EffectiveWorkerLimit` | 3856 | 2 |
| `ConfiguredWorkerLimit` | 3866 | 1 |
| `SweepLibraryAsync` | 4390 | 162 |
| `RecordSweepOutcome` | 4553 | 21 |
| `InspectSyncTarget` | 7411 | 8 |
| `ClassifySyncTarget` | 7431 | 20 |
| `IsAdministrator` | 7457 | 13 |
| `EngineMissingNote` | 7730 | 15 |

### C9 engine resolution & install — 16 methods, 410 lines

- reads: `_bundlePath`, `_bundlePathCheckedMs`, `_bundledVersionCache`, `_logger`
- writes: `_installing`

| method | first line | lines |
|---|---|---|
| `ManagedFfSubSyncPath` | 670 | 1 |
| `ManagedPipPath` | 675 | 1 |
| `ManagedPythonPath` | 680 | 1 |
| `ResolveFfSubSyncPath` | 689 | 30 |
| `BundledFfSubSyncPath` | 724 | 70 |
| `BundledFfSubSyncVersion` | 798 | 31 |
| `ResolveFfmpegPath` | 835 | 26 |
| `GetInstallationStatusAsync` | 866 | 87 |
| `InstallFfSubSyncAsync` | 960 | 63 |
| `EngineIdentity` | 2799 | 4 |
| `EngineIsInstalled` | 7481 | 5 |
| `EngineIsUsable` | 7499 | 27 |
| `EnsurePythonAvailableAsync` | 9576 | 40 |
| `IsPythonVenvReadyAsync` | 9620 | 13 |
| `Truncate` | 9634 | 2 |
| `EscapeArg` | 9637 | 9 |

### C10 lifecycle & maintenance — 8 methods, 319 lines

- reads: `_historyOnlyJobs`, `_jobCancellation`, `_jobContexts`, `_jobs`, `_laneTasks`, `_liveProcesses`, `_logger`, `_pumpTask`, `_queueLock`, `_refreshGate`, `_sweepState`
- writes: `_cleanupTimer`, `_disposing`, `_extractWake`, `_jobContexts`, `_jobs`, `_laneStop`, `_lastReapTicks`, `_lastStoreSweepTicks`, `_runOrder`, `_wakePump`

| method | first line | lines |
|---|---|---|
| `ClearStaleJobDirectories` | 2114 | 63 |
| `ResolveEnqueueTraceMs` | 2419 | 1 |
| `LogLockHold` | 2432 | 7 |
| `ResolveEnqueueTraceMs` | 2442 | 7 |
| `Dispose` | 8000 | 77 |
| `SweepLongLivedStores` | 8095 | 59 |
| `ReapStuckJobs` | 8171 | 61 |
| `CleanupOldJobs` | 8236 | 44 |

**Total: 191 declaration sites, 6917 lines of method bodies** (the remaining ~2 400 lines of the class are fields, doc comments, blank lines and the nested types).

### 1.1 What the expected clusters got right, and where the code disagrees

Confirmed as expected: **process execution** (C4 — exactly five `RunProcess*` methods, 299 lines),
**extraction orchestration** (C3 — but bigger than "orchestration": it also owns the extracted-text caches, the
memoised plan predicates and the whole ffmpeg progress/parse chain, 26 methods / 1 031 lines), and the
**RunSyncJob pipeline** (C5 — 32 methods / 2 159 lines, the largest cluster).

Corrected:

- **"Queue/dispatch/scheduling" is two clusters, not one.** The pump and the policy it consults (C2: 32 methods,
  900 lines, all on the pump/lane threads or pure) are separate from the HTTP-facing queue surface (C1: 23
  methods, 634 lines — enqueue, batch, cancel, kill, job lookup). They share the run-state, but one is a threaded
  control loop and the other is an API surface; merging them in one file is what makes the lock discipline hard
  to see.
- **"Batch/sweep" is three places, not one.** `CreateBatch` 1455 and `CancelBatch` 1551 are the enqueue API (C1);
  `SelectWave` 2819/3273 and `PlanStart` 3104 are scheduler policy (C2); `SweepLibraryAsync` 4390 and
  `RecordSweepOutcome` 4553 sit with the history and the API read models (C8) because that is the state they
  touch (`_sweepState` 591, `_jobs`, plus the callbacks into C1). There is no "batch/sweep" cluster in the code.
- **"Settings/config reading" is not a cluster at all.** The class never holds a config object: every read goes
  through the static `Services.SettingsSource.Current()` seam at the point of use (691, 1321, 1462, 2394, 4109,
  8184, 2386, 3866), and `SettingsSource` lives in another file. What remains here is the settings *surface* —
  `ApplySettingsNow` 2372, `ConfiguredLaneLimit` 2385, `ConfiguredWorkerLimit` 3866, `EffectiveWorkerLimit` 3856
  (C8) — plus `LogWorkerLimit` 3874 (C2). Anyone hoping to extract a "configuration" class will find nothing to
  extract.
- **Nothing is left over.** Every one of the 181 methods fits one of the ten clusters; the only true glue are
  `Join` 2230 (a note-joiner the job pipeline uses) and the two lock-reporting helpers `LogLockHold` 2432 /
  `ResolveEnqueueTraceMs` 2442, which C1, C2, C3 and C8 all call (classified C10).

## 2. Coupling audit — what each cluster touches, and who else touches it

Every field of the class against the ten clusters. `R` = read, `W` = written (a `lock (_queueLock)` counts as a
read, §9). The **clusters** column is the coupling cost: 1 means that cluster owns the field, 5 means five
clusters would have to be given the same object before any of them can become a class.

| field (line) | C1 | C2 | C3 | C4 | C5 | C6 | C7 | C8 | C9 | C10 | clusters |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `_logger` (355) | R | R | R | R | R | R | · | R | R | R | **9** |
| `_jobContexts` (574) | W | R | R | · | R | · | · | R | · | RW | **6** |
| `_jobs` (464) | RW | R | · | · | R | · | · | RW | · | RW | **5** |
| `_queueLock` (475) | R | R | R | · | · | · | · | R | · | R | **5** |
| `_runOrder` (488) | RW | R | R | · | · | · | · | R | · | W | **5** |
| `_libraryManager` (457) | R | · | · | · | R | · | · | R | · | · | **3** |
| `_disposing` (470) | · | R | R | · | · | · | · | · | · | W | **3** |
| `_pumpTask` (489) | R | W | · | · | · | · | · | · | · | R | **3** |
| `_passInFlight` (511) | · | R | RW | · | · | · | · | R | · | · | **3** |
| `_laneTasks` (520) | R | · | RW | · | · | · | · | · | · | R | **3** |
| `_laneStop` (573) | W | · | R | · | · | · | · | · | · | W | **3** |
| `_jobCancellation` (579) | R | · | · | · | W | · | · | · | · | R | **3** |
| `_liveProcesses` (585) | RW | · | · | W | · | · | · | · | · | R | **3** |
| `_sweepState` (591) | · | R | · | · | · | · | · | R | · | R | **3** |
| `_refreshGate` (463) | · | · | · | · | R | · | · | · | · | R | **2** |
| `_wakePump` (499) | · | RW | · | · | · | · | · | · | · | W | **2** |
| `_extractWake` (508) | · | · | RW | · | · | · | · | · | · | W | **2** |
| `_lastPassFinishedUtc` (530) | · | R | W | · | · | · | · | · | · | · | **2** |
| `_referenceGates` (560) | · | W | · | · | W | · | · | · | · | · | **2** |
| `_historyOnlyJobs` (1616) | · | · | · | · | · | · | · | W | · | R | **2** |
| `_userManager` (458) | · | · | · | · | · | · | · | R | · | · | **1** |
| `_libraryMonitor` (459) | · | · | · | · | R | · | · | · | · | · | **1** |
| `_installing` (467) | · | · | · | · | · | · | · | · | W | · | **1** |
| `_bundledVersionCache` (481) | · | · | · | · | · | · | · | · | R | · | **1** |
| `_bundlePath` (483) | · | · | · | · | · | · | · | · | R | · | **1** |
| `_bundlePathCheckedMs` (484) | · | · | · | · | · | · | · | · | R | · | **1** |
| `_pumpFaults` (492) | · | W | · | · | · | · | · | · | · | · | **1** |
| `_extractedReady` (512) | · | · | RW | · | · | · | · | · | · | · | **1** |
| `_referenceOrdinals` (521) | · | · | W | · | · | · | · | · | · | · | **1** |
| `_extractTried` (527) | · | · | RW | · | · | · | · | · | · | · | **1** |
| `_lastReapTicks` (533) | · | · | · | · | · | · | · | · | · | W | **1** |
| `_lastStoreSweepTicks` (542) | · | · | · | · | · | · | · | · | · | W | **1** |
| `_extractedText` (552) | · | · | RW | · | · | · | · | · | · | · | **1** |
| `_extractedOrder` (553) | · | · | W | · | · | · | · | · | · | · | **1** |
| `_speechGates` (567) | · | · | · | · | RW | · | · | · | · | · | **1** |
| `_cleanupTimer` (588) | · | · | · | · | · | · | · | · | · | W | **1** |
| `_historySignature` (1617) | · | · | · | · | · | · | · | W | · | · | **1** |
| `_historySavedUtc` (1618) | · | · | · | · | · | · | · | W | · | · | **1** |
| `_lastProgressLineUtc` (1625) | · | · | · | · | · | · | · | W | · | · | **1** |
| `_speechCachedMemo` (2236) | · | · | W | · | · | · | · | · | · | · | **1** |
| `_speechCachedGate` (2238) | · | · | R | · | · | · | · | · | · | · | **1** |
| `_extractedMissMemo` (2249) | · | · | RW | · | · | · | · | · | · | · | **1** |
| `_extractedMissGate` (2251) | · | · | R | · | · | · | · | · | · | · | **1** |
| `_ceilingLogged` (3199) | · | W | · | · | · | · | · | · | · | · | **1** |
| `_lastLoggedWorkerLimit` (3868) | · | W | · | · | · | · | · | · | · | · | **1** |

### The shared fields, worst first

- **9 cluster(s)** — `_logger` (355): C1, C2, C3, C4, C5, C6, C8, C9, C10
- **6 cluster(s)** — `_jobContexts` (574): C1, C2, C3, C5, C8, C10
- **5 cluster(s)** — `_jobs` (464): C1, C2, C5, C8, C10
- **5 cluster(s)** — `_queueLock` (475): C1, C2, C3, C8, C10
- **5 cluster(s)** — `_runOrder` (488): C1, C2, C3, C8, C10
- **3 cluster(s)** — `_libraryManager` (457): C1, C5, C8
- **3 cluster(s)** — `_disposing` (470): C2, C3, C10
- **3 cluster(s)** — `_pumpTask` (489): C1, C2, C10
- **3 cluster(s)** — `_passInFlight` (511): C2, C3, C8
- **3 cluster(s)** — `_laneTasks` (520): C1, C3, C10
- **3 cluster(s)** — `_laneStop` (573): C1, C3, C10
- **3 cluster(s)** — `_jobCancellation` (579): C1, C5, C10
- **3 cluster(s)** — `_liveProcesses` (585): C1, C4, C10
- **3 cluster(s)** — `_sweepState` (591): C2, C8, C10
- **2 cluster(s)** — `_refreshGate` (463): C5, C10
- **2 cluster(s)** — `_wakePump` (499): C2, C10
- **2 cluster(s)** — `_extractWake` (508): C3, C10
- **2 cluster(s)** — `_lastPassFinishedUtc` (530): C2, C3
- **2 cluster(s)** — `_referenceGates` (560): C2, C5
- **2 cluster(s)** — `_historyOnlyJobs` (1616): C8, C10
- **1 cluster(s)** — `_userManager` (458): C8
- **1 cluster(s)** — `_libraryMonitor` (459): C5
- **1 cluster(s)** — `_installing` (467): C9
- **1 cluster(s)** — `_bundledVersionCache` (481): C9
- **1 cluster(s)** — `_bundlePath` (483): C9
- **1 cluster(s)** — `_bundlePathCheckedMs` (484): C9
- **1 cluster(s)** — `_pumpFaults` (492): C2
- **1 cluster(s)** — `_extractedReady` (512): C3
- **1 cluster(s)** — `_referenceOrdinals` (521): C3
- **1 cluster(s)** — `_extractTried` (527): C3
- **1 cluster(s)** — `_lastReapTicks` (533): C10
- **1 cluster(s)** — `_lastStoreSweepTicks` (542): C10
- **1 cluster(s)** — `_extractedText` (552): C3
- **1 cluster(s)** — `_extractedOrder` (553): C3
- **1 cluster(s)** — `_speechGates` (567): C5
- **1 cluster(s)** — `_cleanupTimer` (588): C10
- **1 cluster(s)** — `_historySignature` (1617): C8
- **1 cluster(s)** — `_historySavedUtc` (1618): C8
- **1 cluster(s)** — `_lastProgressLineUtc` (1625): C8
- **1 cluster(s)** — `_speechCachedMemo` (2236): C3
- **1 cluster(s)** — `_speechCachedGate` (2238): C3
- **1 cluster(s)** — `_extractedMissMemo` (2249): C3
- **1 cluster(s)** — `_extractedMissGate` (2251): C3
- **1 cluster(s)** — `_ceilingLogged` (3199): C2
- **1 cluster(s)** — `_lastLoggedWorkerLimit` (3868): C2

### 2.1 Verdict per cluster

- **C7 media probing / stream mapping / naming — own state: none.** 14 methods, 224 lines, and the script finds
  **no field reference at all**: every method is `static` or pure (`EmbeddedSubtitleOrdinal` 2930,
  `SubtitleStreamOrdinal` 2958, `SyncedTargetName` 7602, `IsInsideRoot` 7858, `CanWriteTo` 3010,
  `RequireWritable` 3045 …). **A real class today, with no plumbing.**
- **C6 alignment measurement — `_logger` only.** 14 methods, 326 lines; the single field use is a diagnostic
  log line inside `MeasureSegmentStructure` 5050. **A real class today** (pass an `ILogger` in, or drop the log
  line).
- **C4 process execution — `_liveProcesses` (written) + `_logger`.** Five methods, 299 lines. `_liveProcesses`
  585 is only touched by: the runners (`TrackChildProcess` 7650, called at 9154/9206/9257/9461/9547), `KillAll`
  1830 and `Dispose` 8000 (`KillChildProcesses` 7622, `LiveProcessCount` 1937, `SafeHasExited` 1923). **A real
  class today if the registry moves with it** (`SubSyncProcesses`: track, kill, count) — three call sites from
  outside, no other state.
- **C9 engine resolution & install — its own four fields and nothing else.** 16 methods, 410 lines; touches
  `_bundlePath` 483, `_bundlePathCheckedMs` 484, `_bundledVersionCache` 481, `_installing` 467, `_logger`. No
  other cluster names any of them. **A real class today** (the constructor callback at 637–643 moves with it).
- **C5 job pipeline — reads 8 fields, writes 3, and all 3 writes are registrations.** It reads `_jobContexts`,
  `_jobs` (one walk judgement at 5929), `_speechGates`, `_refreshGate`, `_libraryManager`, `_libraryMonitor`,
  `_logger`, and writes `_jobCancellation` (RunSyncJobWithContext 4308), `_speechGates` (PrepareAudioReferenceAsync
  6930), `_referenceGates` (ResolveReferenceAsync 7023). **`RunSyncJob` itself writes no field of the class** —
  its state is the `job` object it is handed plus four static stores (`ReferenceStore`, `SubtitleCache`,
  `SpeechCache`, `SharedExtractionStore`). So the job pipeline is the most *separable* large cluster, and its only
  genuine coupling is the job registry it is dispatched from (`RunSyncJobWithContext` 4298 is the seam) and the
  two per-file gates. **Real class after step 3 of §7** (the gates and the run-state must be objects first).
- **C1 + C2 — one entangled core.** They share `_jobs`, `_jobContexts`, `_runOrder`, `_queueLock`, `_wakePump`,
  `_pumpTask`, `_laneTasks`, `_jobCancellation`, `_laneStop`, `_liveProcesses`, `_disposing`. Every one of C1's
  writes is state the pump reads on its next pass and the API reads on every request. **Partial-class file first;
  a real class only after the run-state is an object.**
- **C3 extraction — entangled with the core and with the job path.** It reads `_jobContexts` (2744, 2291),
  `_runOrder` (2632), `_queueLock` (2630), and writes `_passInFlight`, `_extractedReady`, `_extractTried`,
  `_lastPassFinishedUtc`, which C2 reads in its plan predicates (4134, 4138) and C8 reads for its "why is this
  waiting" text (2195). **Partial-class file first.**
- **C8 API read models / sweep / history — writes four history fields, reads the run-state.** It is the only
  writer of `_historyOnlyJobs` 1616, `_historySignature` 1617, `_historySavedUtc` 1618, `_lastProgressLineUtc`
  1625, and it reads `_jobs`/`_jobContexts`/`_runOrder`/`_queueLock`/`_userManager`/`_libraryManager`. The
  access-control half (`CanUserSeeItem` 367, `FirstItemNotVisibleTo` 420, `AncestorIds` 446, `IsAdministrator`
  7457) is a self-contained island: it reads `_userManager` only. **Split it in two: the access island is a real
  class today; the rest is a partial until the run-state exists.**
- **C10 lifecycle & maintenance — the janitor, and it must stay in the shell.** It reads 11 fields and writes 10;
  `Dispose` 8000 touches nearly everything (lanes, pump, cancellation, processes, timer, sweep state, history).
  A "Maintenance" class would need the whole class. **Keep in the shell file** (that is the file that owns the
  fields anyway).

## 3. Fragility map — clusters against the shipped fixes

**High risk (the fix's code is inside the cluster)**

- **C5 — 2 159 lines, the densest history in the file.** `RunSyncJob`'s phases and the extracted methods carry
  **S22** (the refusal bodies), **S31** (the audio cross-check and `RulersDisagree` 3522), **S43**
  (`VadForReference` 5298, `AudioReferenceVad` 608), **S45** (the audio retry's own output path), **S46** (the
  analysis link's lifetime — the drop now lives in `CleanUpAfterJob` 7205), **B6** (`StuckJobPolicy.Settle`
  4345 + the watchdog), **B22** (the double `MeasureSyncChange` 5726/5828), and the framerate/rescale family
  (`RescaleOntoReferenceSpan` 4835, `FramerateArgs` 8296, `BuildFfSubSyncArgs` 8312) plus the split-penalty pair
  (`PiecewiseArgs` 8401, `PiecewiseHolds` 5115, `MeasureSegmentStructure` 5050).
- **C3 — 1 031 lines.** **B8** (`DiscardPartialExtraction` 9007 and the guard in the extraction chain),
  **B19** (`ParseFfmpegProgressSeconds` 9027), **B20** (`IsCancelledExtraction` 7703, `StopChainIfCancelled`
  7714), **S26 / D17 / E2** one call deep (`MkvSubtitleExtractor.TryExtractMany` at 2520), and **S7**'s memos
  (`SpeechIsCached` 2744, `ExtractionReady` 2291, `CacheSaysMissing` 2258, `RememberCacheMiss` 2269).
- **C2 — 900 lines.** **S40** (the snapshot-then-plan split at 4058–4147 and the `LogLockHold` labels), **B3**
  (the scheduler no longer touches the share under the lock — that is what the snapshot is), **B31**
  (`RunPumpLoopAsync` 3933, `PumpFaultBackoffMs` 3967, `NotePumpFault` 3989), **B5** (the `--version` spawn is
  no longer synchronous under the lock — the cache it moved into is C9's), **B29** (`_disposing` 470 read at
  3974, `_lastPassFinishedUtc` 530 read at 4138), the walk-cap policy family (**R1/E4**, held) and **D11**
  (`ResolveModeForBatch` 2055).
- **C10 — 319 lines.** **B14** (`Dispose`'s teardown, 8028–8060), **B23** (`ClearStaleJobDirectories` 2114),
  **B12** (`SweepLongLivedStores` 8095), **B17** (`CleanupOldJobs` 8236 + `JobsToEvict` 7770), **B6**'s watchdog
  (`ReapStuckJobs` 8171).
- **C1 — 634 lines.** **B15/S40** (every critical section here is status-writes only, with the logging moved
  out: 1339 → 1352, 1556 → 1575, 1835 → 1852), **B16** (`KillAll`'s measured count, 1897), **D9**
  (`FindDuplicate` 7356, the duplicate branch 1338–1368, `SyncQueueConflictException` 7368 for F4's ownership
  rule), and **S14**'s enqueue half (the index→ordinal translation at 1315).

**Medium risk**

- **C6** — its predicates are the *decision rules* the fixes installed (`RulerSpreadTooWide` 3537,
  `RulersDisagree` 3522, `IsRescaleAcceptable` 5147, `PiecewiseHolds` 5115, `IsTargetOffTheVideo` 4767). The
  risk is not fragility but pinning: the suite asserts these by name (113 references), so any move has to keep
  the names.
- **C8** — **D5/F27** (`GetActive` 1944, `ActiveJobCounts` 7585), **D6** (the history read models),
  **S24/S25** (progress + history persistence), **F14/F15** (the sweep's trigger and knobs).
- **C9** — **D2** (`EngineIsUsable` 7499, `EngineIsInstalled` 7481, the status at 866), **D12**, **F1**
  (`InstallFfSubSyncAsync` 960 → `apt-get` at 9596/9602).

**Low risk**

- **C7** — it holds three shipped fixes (**S14**'s ordinal helpers 2930/2958, **S12**'s `SyncedTargetName` 7602,
  **B23**'s `IsJobScratchDirectory`/`IsInsideRoot` 7847/7858) but every one is a pure function with its own
  checks (85 references). `CanWriteTo`/`RequireWritable` 3010/3045 are **S44**'s guard, and that row is about
  where they are *called* (C5), not about them.
- **C4** — **B18** (open) is this cluster's own row, and nothing else exists in it.

## 4. Test coverage per cluster

**How the suite can reach anything at all** (this constrains every split decision):

- The service is constructed **five times**: `tests/job_checks.cs:83` (a real `NullLogger`, three null
  collaborators) and `tests/run_checks.py:2980, 3377, 3641, 3690` (four nulls), made possible by
  `InternalsVisibleTo("logictest")`. Any instance method that touches `_libraryManager`/`_userManager`/
  `_libraryMonitor` throws under that instance, so **instance coverage is effectively limited to pure-ish
  members**.
- **Reflection is used at four sites**: `LogPluginCompletion` (`run_checks.py:2495`), `LogPluginCancellation`
  (`:2522`), `LogPluginProgress` (`:2542`) and **`RunSyncJob`** (`job_checks.cs:84`, the entry point of the 40
  characterization checks + 25 mutations from `RUNSYNCJOB_MAP.md` §8–§15).
- **Source-shape pins read this file as text at three sites** — `run_checks.py:4246` (the ruler-shape score is
  logged before the cross-check and never gates it), `:4511` (the page-side kill wording), `:5007`
  (`ReferenceStore.Reserve` present, `SpeechCache.ReferencePath` absent). They open
  `Services/SubSyncService.cs` **by path**: moving that code to another *file* makes them fail loudly, which is
  correct behaviour, but any file split has to re-point them in the same commit (the Phase-3 lesson in
  `RUNSYNCJOB_MAP.md`).
- **The rig** runs the real thing end to end: 11 scenarios — `smoke`, `s7-queue-load`, `s40-enqueue`,
  `s41-cold-read`, `s41-steady`, `s41-thrash-tier`, `s31-wrong-ruler`, `s39-ratio`, `s43-audio-is-audio`,
  `p3-speech-gate`, `d3-settings` (`tests/rig/run_scenario.py:1286–1310`).

| cluster | names hit in `tests/` | mechanism | gaps |
|---|---|---|---|
| C1 (23) | 13, 37 refs | rig (every scenario enqueues through it); `EnqueueSyncTimed`'s timing line in the s40/s7 logs | `EnqueueSync`, `StartSync`, `CancelBatch`, `GetActive`, `GetJob`, `GetBatchJobs` never named |
| C2 (32) | 16, **204 refs** | unit checks on the static policy (`WalkCapForProfile` 83, `SelectWave` 30, `PlanStart` 17, `PumpFaultBackoffMs` 10, `RunPumpLoopAsync` 5); rig `s40-enqueue`, `s7-queue-load`, `s41-*` | `WakePump`, `CountQueued`, `ResolveModeForBatch`, `JobNeedsHeavyIo`, the three mode helpers, `MediaLengthOf` |
| C3 (26) | 21, 55 refs | unit checks on the extractor and the parsers; rig `s39-ratio`, `p3-speech-gate` | `ExtractEmbeddedAsync` never named (exercised through the rig), `ExtractedKey`/`ExtractedKeyOf`, `TryPeekExtracted`, `FfmpegError` |
| C4 (5) | 1, 14 refs | B14's checks count spawned vs registered processes through a shim | **all five runners unnamed; nothing asserts which runner is used or what it passes** |
| C5 (32) | 18, 163 refs | the 40 reflection-driven `RunSyncJob` checks + 25 mutations; rig `s31-wrong-ruler`, `s43-audio-is-audio`, `s39-ratio` | the nine extracted helpers are never named *directly* — they are covered *through* `RunSyncJob`, which is the point of the extraction |
| C6 (14) | 13, 113 refs | direct unit checks on the static predicates | `CountSubtitleCues` |
| C7 (14) | 13, 85 refs | direct unit checks (S14/S12/B23 checks) | `RequireWritable` (behaviour covered through the write path) |
| C8 (21) | 12, 34 refs | the page/API source checks; rig status polls | **`SweepLibraryAsync` and `RecordSweepOutcome` entirely; `ListSubtitles`, `ListSubtitlesBulk`, `CanUserSeeItem`, `FirstItemNotVisibleTo`, `AncestorIds` — the whole access-control surface — and `ConfiguredWorkerLimit`/`EffectiveWorkerLimit`** |
| C9 (17) | 7, 20 refs | 5 checks on `EngineIsUsable`; rig `d3-settings` | **`GetInstallationStatusAsync`, `InstallFfSubSyncAsync`, `EnsurePythonAvailableAsync`, `IsPythonVenvReadyAsync`, the bundled-path properties — the install/status surface has no check at all** |
| C10 (8) | 4, 8 refs | B12/B17/B23 checks touch the policies | `CleanupOldJobs`, `LogLockHold`, `ResolveEnqueueTraceMs` |

**Clusters a split would be invisible to**: **C8's sweep + access surface**, **C9's install/status**, and
**C4's five runners** — the same P13/P15-shaped gap the RunSyncJob work had to close first. C10's `Dispose` is
partly covered (B14) but its *ordering* is not.

## 5. Conflicts with open and held work

| row | where it lands | what it means for a split |
|---|---|---|
| **B3** (done, medium — scheduler touched the share under the lock) | C2's snapshot design 4058–4147 | the fix *is* the structure: preserve the snapshot/plan split and the `LogLockHold` labels (they are S40's evidence) |
| **B5** (done, medium — `--version` spawned from a getter under the lock) | C2 (the lock) + C9 (the cache) | a C9 extraction must keep the one-spawn-per-binary property (it is checked) |
| **B15** (done, low — logging under the queue lock) | C1's three cancel/enqueue sections, C2's pump | the "status writes only, log after release" shape is pinned by the suite; a moved block must keep the log *outside* the lock |
| **B18** (open, low — five runners, two argument styles) | **C4 entirely** | the row is C4's specification: consolidate *as* the extraction, not after it. Two use `ArgumentList` (9125, 9429), three build escaped strings (9181, 9233, 9528); `RunCapturedAsync` 9181 has no call site anywhere in the repo; `RunProcessAsync` 9233 serves only the venv install (997/1005) |
| **S44** (open, medium — unwritable folder costs a full engine pass) | C5's write step (6583/6594/6627), the guard in C7 (3010/3045) | the fix changes *when* the check runs, i.e. C5's behaviour; keep it out of a structural commit |
| **E4** (held) and **R1** (held) | C2's walk-cap family (`WalkCapOfPath` 3335, `WalkCapForProfile` 3640/3664/3691, `ProbeVolumeIfUnmeasured` 3421, `VolumesOtherThan` 3507) + C3's handover to the extractor | both are read-policy decisions; R1's row records that a per-volume cap means rewriting three wave checks plus a reflection check, i.e. it will touch C2's *tests*. Move C2's policy code before or after that decision, not during |
| **B22, B29** (open, low) | C5 (double measure); C2/C3 (`_disposing`, `_lastPassFinishedUtc`) | small and behaviour-adjacent; keep them in their own commits so a split diff stays verifiable |
| **D11, D5, F27, D6** (open) | C2 (`ResolveModeForBatch` 2055), C8 (`GetActive` 1944, `ActiveJobCounts` 7585, history) | behaviour fixes inside clusters a split would move — sequence first or after |
| **D1 / F29** (decision / open) | C1 (`KillAll` 1830, `KillJobs` 7538) | the row's "no per-job kill" wording is stale: `KillJobs` exists and the controller calls it (`Api/SubSyncController.cs:729`) |
| **F1, D12** (open) | C9 | the authorisation is the controller's; the privileged calls are here |
| **F14, F15** (open) | C8's sweep + the scheduled task (another file) | |

Not this file, so not sequencing constraints: **D17, E2, S26** (`MkvSubtitleExtractor`), **F17/F18**
(`SpeechCache`), **F19** (`SrtWriter`), **F20** (`MediaVolume`), **F21/F22** (`SubSyncMiddleware`), **B9**
(`MkvSubtitleExtractor.KernelIo` 1285).

## 6. Partial class or real class — the explicit recommendation

**What a partial split costs: nothing.** C# partial classes are one class across files: accessibility unchanged,
DI registration unchanged, the fields stay where they are declared, no call site moves. The only real cost is
that the three source-shape pins (`run_checks.py:4246/4511/5007`) read `Services/SubSyncService.cs` **by path**
and have to be re-pointed in the same commit as the text they search for. Two lines per pin, no risk.

**What a real class costs:** a new type with an explicit constructor — which is exactly what the tangled
clusters cannot do today. Five clusters share `_jobs`/`_jobContexts`/`_runOrder`/`_queueLock`, so a
"QueueService" would need the very objects the pump, the lanes, the history and the sweeper hold. Either those
become one injected `SyncRunState`, or the classes are not independent and the split has bought nothing but
parameter lists (the 16-parameter `RunEngineAttemptAsync` lesson from `RUNSYNCJOB_MAP.md` §11).

| cluster | recommendation | why |
|---|---|---|
| C7 (14 methods, 224 lines, **0 fields**) | **real class now** — static (`SubSyncTargetNaming` / `MediaStreamMap`) | already pure; the file checks take their paths as arguments |
| C6 (14, 326, `_logger` only) | **real class now** — static (`AlignmentMetrics`, logger passed or dropped) | pure predicates with 113 check references; moving them keeps every name |
| C4 (5, 299, `_liveProcesses` + `_logger`) | **real class now** — `SubSyncProcesses`, taking the registry with it | `_liveProcesses` + `JobProcessRegistry` are a process-wide concern already; three external call sites (`TrackChildProcess` 7650, `KillChildProcesses` 7622, `LiveProcessCount` 1937) |
| C9 (16, 410, its own 4 fields) | **real class now** — `EngineLocator` / `FfSubSyncInstaller` | no other cluster names `_bundlePath`/`_bundlePathCheckedMs`/`_bundledVersionCache`/`_installing`; D2/D12/F1/B5 history comes along |
| C8's access island (`CanUserSeeItem` 367, `FirstItemNotVisibleTo` 420, `AncestorIds` 446, `IsAdministrator` 7457) | **real class now** — it belongs with the existing static `ItemAccess` class | the island reads `_userManager` only |
| C1, C2, C3, C5, C8 (rest), C10 | **partial-class files first** | they share the run-state; the file split is the readability win with zero behaviour risk and no new public surface |
| the DTOs (`SyncJob` 91, `SubtitleInfo` 37, `BulkSubtitleItem` 227, `FfSubSyncInstallationStatus` 245, `SweepResult` 326, `SyncJobStatus` 20) | **do not touch** | `Api/SubSyncController.cs` and `Web/subsyncMain.js` serialise them; `SyncJob.CopyForViewer` 122 is F4's per-viewer seam |

## 7. Sequencing recommendation

Least-risky/most-isolated first, the same principle as the RunSyncJob phases.

1. **Phase 0 — the characterization net for what has none** (C4's runners, C9's install/status, C8's sweep and
   access surface, C10's `Dispose` ordering). These are the P13/P15-shaped gaps: without them a split is a change
   no test can see. Cheap, because those members are public/internal and mostly side-effect-free to call with a
   null-collaborator instance, and the rig already has `d3-settings` for the install path.
2. **Phase 1 — the four separable clusters become real classes**, one commit each: C7 (224 lines) → C6 (326) →
   C4 (299, registry moves with it) → C9 (410) → C8's access island (~120). Each is a pure move by the
   `RUNSYNCJOB_MAP.md` discipline (compare the moved block before/after, keep every name, since the checks
   reference them by name). **≈1 380 lines and 53 methods leave the class**, which also stops owning 9 of its
   45 fields.
3. **Phase 2 — partial-class file split of the tangled core, zero behaviour change.** `SubSyncService.cs` keeps
   the fields, the constructor, `Dispose` and the public API surface; behaviour moves to
   `SubSyncService.Queue.cs` (C1, 634), `.Scheduler.cs` (C2, 900), `.Extraction.cs` (C3, 1 031),
   `.JobPipeline.cs` (C5, 2 159), `.SweepHistory.cs` (C8, 615). Six files of 300–2 200 lines instead of one of
   9 300: this is the deliverable the goal is actually about, and it carries no behaviour risk at all.
4. **Phase 3 — the run-state object** (the prerequisite for anything more): `_jobs`, `_jobContexts`, `_runOrder`,
   `_jobCancellation`, `_historyOnlyJobs` and `_queueLock` become one injected `SyncRunState` with the operations
   the five clusters need (add, snapshot, claim, evict, find duplicate, cancel). This pays down the 2 680-line
   coupling. The pump is the least affected: `PlanStart` 3104 already takes the queue, the running set and five
   delegates as arguments.
5. **Phase 4 — promote the partials to real classes one at a time**, least coupled first: C8 (it owns its own
   history fields) → C5 (the job pipeline: reads the run-state, writes only two gates) → C3 (lanes) → C1/C2 (the
   core, last, and only if it still looks worth it).

**How many files this reasonably becomes.** The coupling data does not force one number, but it gives a floor and
a ceiling. Floor (Phase 1 + 2, no redesign): **1 shell + 4 real classes + 5-6 partial files ≈ 11 files** of
200–2 200 lines, replacing one of 9 300. Ceiling (after Phase 3/4, the partials becoming real classes): the same
shape with **~10 classes plus the shell** — and not much beyond that without redesigning `SyncJob` itself,
because the job object is mutated across threads and read live by the HTTP layer by design.

## 8. What static reading cannot determine

- **The lane/planner race through the miss memo.** The code order (`_extractedReady` checked at 2311 before
  `CacheSaysMissing` at 2330) is what makes the 2-second `_extractedMissMemo` safe, and nothing asserts it.
  Whether a stale miss can hide a track that arrived depends on the interleaving of a lane's hand-over
  (2539/2566) with a plan pass — a rig run with a lane in flight, not reading.
- **Whether `_lastPassFinishedUtc`'s unlocked read at 4138 ever matters** (it decides whether jobs may extract
  for themselves for a 20-second window after a lane dies).
- **The memo hit rates** behind "the predicates are memoised so re-planning every 750 ms costs nothing" (4199) on
  a 2 500-job queue; the memos are 5 s (`_speechCachedMemo` 2236) and 2 s (`_extractedMissMemo` 2249).
- **Whether `SweepLibraryAsync`'s 750 ms poll (4532) and its per-call `GetBatchJobs` scan of `_jobs` disturb the
  queue** — it takes no lock, but it runs on the scheduled task's thread and nothing bounds its cost.
- **The four static stores** (`ReferenceStore`, `SubtitleCache`, `SpeechCache`, `SharedExtractionStore`) are
  process-wide state shared by every job. A real-class extraction of C5 would inherit them; whether that is safe
  for parallel jobs is a runtime property, not a readable one.
- **D5/F27 and D11** need a live response and the page's readers (`Web/subsyncMain.js`); this file is only the
  server half.
- **Whether the harness can be given real collaborators** (it passes nulls today): I cannot tell by reading which
  public methods would then be drivable, and that decides how much of Phase 0 can be unit checks rather than rig
  scenarios.

## 9. How these numbers were produced, and where the parse is approximate

- **Member spans** come from a brace-depth walk over the file (string/verbatim/interpolated/raw-string and
  comment aware) plus a declaration filter; **192 declaration sites**, cross-checked against a hand-verified list
  of 186 names from an earlier pass. Four "sites" are false matches — call sites inside another member's
  expression body: `Join` at 670/675/680 (inside `ManagedFfSubSyncPath`/`PipPath`/`PythonPath`) and
  `SafeHasExited` at 1937 (inside `LiveProcessCount`) — and they are excluded everywhere. Three members whose
  declaration line breaks after the name (`BundledFfSubSyncPath` 724–793, `BundledFfSubSyncVersion` 798–828,
  `LaneAlive` 2460–2469) were added by hand. Method counts are ±2.
- **Field usage** is a token search for each of the 45 field names inside each member's body with string literals
  and comments stripped; a use counts as **written** when it matches an assignment, `++`/`--`,
  `Interlocked.*(ref …)`, or one of a fixed mutator list (`Add`/`TryAdd`/`TryRemove`/`Remove`/`RemoveAll`/
  `Enqueue`/`Clear`/`Release`/`Cancel`/`Flush`/`Store`/`Insert`/`Forget`/`GetOrAdd`/`AddOrUpdate`/`TryUpdate`/
  `Dispose`/…), and as a read otherwise. So the matrix is a **floor**: passing a field's contents into something
  else is a read even when the object is mutated (`gate.Release()` counts the `_speechGates` lookup as a read;
  only the `GetOrAdd` at 6930/7023 as a write), and `lock (_queueLock)` counts as a read. Two cells were
  corrected by hand after spot-checking the sources: `_speechGates` and `_referenceGates` are **written** by C5
  (6930, 7023) — the first pass missed them because `GetOrAdd` was not in the mutator list.
- **"Named in tests"** counts are substring matches over the whole `tests/` tree, which is why a method exercised
  only through the rig (`ExtractEmbeddedAsync`) reads as unnamed, and why `SelectWave` shows 30 hits that are
  mostly check *names*. The mechanism column is what distinguishes them.
- Where this document and an earlier revision of it disagree (C5's write set, the two gate fields), this one is
  the corrected version.
