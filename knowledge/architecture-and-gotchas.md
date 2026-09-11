# Architecture & Gotchas

## Component Architecture

```
Plugin.cs                        — Entry point: BasePlugin<PluginConfiguration>, IHasWebPages.
                                   Sets static Instance; provides config page resources.
                                   Does NOT inject scripts (see SubSyncMiddleware).

SubSyncServiceRegistrator.cs     — DI: IServerServiceRegistrator.
                                   Registers SubSyncService (singleton) and
                                   app.UseMiddleware<SubSyncMiddleware>() (response pipe).

Api/SubSyncMiddleware.cs         — Response middleware: injects
                                   <script src="/SubSync/ClientScript"> into index.html
                                   responses (path ends with "/" , "/index.html" or
                                   "/web/index.html"), idempotently.

Api/SubSyncController.cs         — REST API: [Route("SubSync")], [Authorize].
                                   Serves client JS from an embedded resource.

Services/SubSyncService.cs       — FIFO queue pump + sync engine (ffsubsync/ffmpeg),
                                   job/batch tracking, copy/replace output, folder rescan.

Web/subsyncMain.html             — Main-menu plugin page: Sync browser, History, Settings.
Web/subsync.js                   — Client script for detail/library ⋮ menus (single sync).
Web/configPage.html              — Dashboard plugin-settings page (legacy UI parity).
```

## Script Injection

- **Where**: `SubSyncMiddleware.cs` (response middleware registered in
  `SubSyncServiceRegistrator`), NOT `Plugin.cs`.
- Injects the `<script src="/SubSync/ClientScript"></script>` tag into HTML responses for
  the web root, `/index.html`, and `/web/index.html` (checks path + content-type).
- Injection is per-request; the middleware re-injects every time the page is served and
  guards against double-injection when handling both `/` and `/index.html`.
- `Plugin.cs` only owns plugin identity/pages — do not add injection logic there.

## Sync Job Execution Pipeline (`RunSyncJob`)

1. **Prepare** (0.0) — resolve the ffsubsync binary, validate it exists
2. **Extract subtitle** (0.05) — embedded only: `ffmpeg -map 0:s:{ordinal}` where
   `{ordinal}` is the stream's position among subtitle streams (not the container index)
3. **Run ffsubsync** (0.1–0.75) — real-time stderr parsing for progress
4. **Write output**:
   - External original → copy mode writes a NEW sidecar (`{lang}.SYNCED.srt` for
     pure-language originals, `{stem}-SYNCED.srt` otherwise) or replace mode overwrites
     the original with `.bak.subsync` backup + rollback
   - **Embedded → ALWAYS a new external sidecar** next to the video
     (`{videoNameNoExt}-SYNCED.{lang}.srt`, or `-SYNCED.srt` when the track has no
     language). There is **no remux path**: video files are only ever READ (for
     ffsubsync reference audio), never written. If a video-writing code path reappears,
     that is a regression.
5. **Verify** (0.95) — output exists and non-empty
6. **Cleanup** — delete backup on success; rollback on failure
7. **Discovery** — `ILibraryMonitor.ReportFileSystemChanged(dir)` (one-folder rescan) +
   `UpdateItemAsync` so new sidecars appear without a library scan

### Atomic Replacement Pattern (replace mode, external only)

- Backup first: `File.Copy(original, original + ".bak.subsync", overwrite: false)`
- Copy synced into place; rollback on failure; rollback failure preserves the `.bak`
- Temp-dir cleanup always in `finally`

### Progress Parsing from ffsubsync stderr

ffsubsync stderr is read line-by-line in real time:

| Pattern | Progress Range | Phase Label |
|---------|---------------|-------------|
| tqdm `NN%|...` regex | 0.10–0.55 (linear map) | "Analyzing speech" |
| "extracting speech segments from subtitle" | 0.55 | "Extracting subtitle speech" |
| "computing alignments" | 0.60 | "Computing alignment" |
| "got score" … "for ratio" | +0.01 per line, max 0.74 | "Computing alignment" |
| "writing output" | 0.75 | "Writing output" |

## Concurrency Model

- Single global FIFO: `_runOrder` list + `_wakePump` semaphore + `_pumpTask`.
- Every sync (detail-page or batch) is a `SyncJob` in this queue; the pump runs jobs
  strictly one at a time so overlapping batches can never race on the same files.
- Batch tasks carry `BatchId`/`BatchIndex`; `BatchLabel` is the scope title
  (e.g. "Series · Season 2"), `Label` is the per-task subtitle title.
- There is intentionally NO `SemaphoreSlim(2,2)`-style parallel limiter anymore; do not
  reintroduce parallel execution without per-file locking.
- `_jobContexts` holds resolved (video, stream, ordinal, config) per queued job so a
  refresh mid-queue cannot change what a job does.

## Job Storage

- Jobs: `ConcurrentDictionary<string, SyncJob>` — in-memory only, NOT persisted across
  restarts (history is server-side but ephemeral).
- `_cleanupTimer` fires every 30 minutes → `CleanupOldJobs()`; see
  [config-and-validation.md](config-and-validation.md) for exact eviction rules.

## Library Sweep (Scheduled Task)

- `SubSyncSweepTask` (IScheduledTask, DI-registered as such; appears in Dashboard →
  Scheduled Tasks with "Run Now" and normal scheduling triggers; no default
  trigger).
- `SweepLibraryAsync()` enumerates the library (RootFolder recursive children,
  OfType<Video>), then queues ONE job per **external** subtitle track lacking a
  synced output, capped by `SweepMaxItemsPerRun`. Embedded tracks are out of
  scope for sweeps (manual UI only).
- Queued work is a normal batch on the shared FIFO pump — never parallel to user
  syncs — and the task waits for it, reporting real progress.
- Persistent skip/fail cache lives in `SweepState` at
  `{DataPath}/subsync/state/sweep-cache.json` (SHA-256 per source file; cap 5000
  entries, oldest evicted). Rules: skip if the same content already produced an
  output that still exists on disk; skip if the same content failed
  `SweepFailStreakLimit` consecutive runs; a content change resets both. Hooks
  fire in the pump/`RunSyncJobWithContext` for external tracks only. Corrupt or
  unwritable state files are non-fatal (fresh start / run anyway).
- Sweep feature ported from Marnalas/jellyfin-subsync (MIT) — attribution kept in
  `SubSyncSweepTask.cs` and `SweepState.cs`.

## Process Execution

Three process runner methods:

| Method | Purpose | Stderr Handling |
|--------|---------|----------------|
| `RunProcessArgumentListAsync` | ffmpeg + general (argv-based, no shell escaping) | Read all at end, log on non-zero exit |
| `RunProcessWithStderrCallbackAsync` | ffsubsync | Line-by-line real-time via callback |
| `RunProcessCaptureAsync` | Status checks (python3 --version, etc.) | Returns `(exitCode, combined output)` |

Rules:
- All use `UseShellExecute=false`, `CreateNoWindow=true`, cancellation kills the whole
  process tree.
- Prefer `ArgumentList.Add(...)` for every invocation (paths with spaces/unicode) —
  hand-escaped strings (`EscapeArg`) survive only on the legacy managed-venv python/pip
  path and must not return to the engine.

## Installation Security (legacy managed-venv install path)

- Install concurrency guarded by `Interlocked.CompareExchange` (`_installing`)
- `venvPath` is plugin-derived (`{DataPath}/subsync/venv`), never raw user input, so no
  traversal containment check exists on it — a former
  `GetFullPath(venvPath).Contains("..")` check was dead code and was removed

## Disposal

`SubSyncService` implements `IDisposable` and is registered as a DI singleton, so
Jellyfin's container disposal at shutdown calls `Dispose()`: it disposes `_cleanupTimer`
and `_wakePump` and sets `_disposing`, which stops the background pump cleanly (the pump
is unblocked with a release before the semaphore is disposed). No extra plugin lifecycle
hook is required.

## Test Project Gap

No test project exists yet. `AssemblyInfo.cs` used to declare
`InternalsVisibleTo("Jellyfin.Plugin.SubSync.Tests")` for a project that was never
scaffolded; the attribute was removed. A real xunit project is planned together with
the library-sweep + skip/fail-cache port (Marnalas/jellyfin-subsync, MIT) — prime
candidates are `EscapeArg`-free arg building, the VAD allow-list fallback, cleanup
eviction rules and skip-cache logic.

## Build Constraints

- `TreatWarningsAsErrors=true` — all compiler warnings are build errors
- `GenerateDocumentationFile=true` — every public member needs `///` XML docs
- `Nullable=enable`, `ImplicitUsings=enable` via `Directory.Build.props`

See also:
- [api-reference.md](api-reference.md) for REST endpoint details
- [config-and-validation.md](config-and-validation.md) for configuration fields and validation rules


## Embedded subtitle extraction: index reads vs demuxing

`SubSyncService.RunSyncJob` extracts an embedded track in one of two ways:

1. **Indexed (always on; the setting was removed)** —
   `MkvSubtitleExtractor.TryExtract` walks EBML/segment → `Tracks` → `Cues`, then reads
   only the clusters that hold the target track's blocks and writes SRT itself. Touches
   kilobytes.
2. **ffmpeg fallback** — `ExtractSubtitle` maps the resolved container stream index and
   converts to SRT. Correct for every container, but it demuxes the whole file: measured
   cold 2.32 s for a 1.7 GB file, and it tracks the full-file read rather than the
   1 MB read (0.01 s).

The extractor deliberately returns `false` (→ ffmpeg) for anything it cannot prove:
non-Matroska, no cue index, unknown codec, images (`S_HDMV/PGS`, `S_VOBSUB`), laced blocks
(`lacing != 0` — decoding them wrongly would be silent corruption), `ContentEncodings`
compression, or a malformed EBML structure. It never partially succeeds.

Ordinals: Jellyfin's `MediaStream.Index` is not a container index, so
`ResolveContainerSubtitleIndexAsync` probes with ffmpeg and matches by position. That same
position (0-based among subtitle streams, including image tracks) is what the extractor
takes, and it keeps non-text tracks in its own list so `0:s:N` numbering lines up.

Verified (`MkvSubtitleExtractor` vs ffmpeg on real files): subrip and ASS tracks, 600/600
cues, identical text, ≤0.023 s timing difference (the extractor's timestamps equal the
muxed source; ffmpeg re-times slightly); and a 3-track file where ordinals 0/1/2 returned
English/ASS-Swedish/German exactly as `ffmpeg -map 0:s:N` did.


## Never let an embedded subtitle be its own sync reference

ffsubsync's default detector family (`subs_then_webrtc` and friends) prefers an *embedded
text subtitle stream of the reference file* as the speech signal — documented upstream as
cheaper and often more accurate than a VAD over the audio. That is exactly wrong when the
subtitle being synced is itself an embedded track of that file: reference and input are the
same timings, so the best alignment is zero and the run reports success without changing
anything.

`SubSyncService.SelectReferenceStream` handles it: for embedded inputs it returns the first
*text* subtitle stream that is not the one being synced (`--reference-stream s:N`), and
`a:0` when the file has no other text track. External sidecars keep the default, because
then the file's embedded track is a legitimate (and cheap) reference.

Measured with ffsubsync 0.5.1 on a file holding a 6-second-out-of-sync text track plus one
correct track:

| reference choice | result |
| --- | --- |
| default (no `--reference-stream`) | offset 0.000 — not corrected |
| `--reference-stream s:1` (other text track) | offset −6.000 — corrected exactly |
| `--reference-stream a:0` (audio) | audio path used (synthetic tone audio → not a valid accuracy check) |

The choice changes the derived speech signal, so `(vad method, reference stream)` is part of
the `SpeechCache` key.


## Extraction method chain and the stall watchdog

`SubSyncService.ExtractEmbeddedAsync` tries methods cheapest-first and returns which one
worked:

1. `MkvSubtitleExtractor` — the container's own structures (text codecs only):
   - **SeekHead** → Tracks and Cues positions, so no walk past the clusters is needed.
   - **Cues** read in one bulk pass and parsed in memory (an index can hold thousands of
     cue points; parsing them one syscall at a time is what made this slow).
   - **CueRelativePosition first**: real muxers (ffmpeg, mkvmerge) record the referenced
     block's byte offset inside its cluster for every cue point. One cue point must therefore
     cost one small read. Walking a cluster's ~150 blocks (mostly audio frames) instead is what
     made extraction take tens of seconds per episode; measured on a real 8.2 GB remux, 127
     reads / 7.0 MB became 28 reads / 0.1 MB, and a 1,400-subtitle file went from 91.8 MB to
     5.8 MB read. The walk stays as a fallback, and the harness asserts the indexed path reads
     less than the walk on identical fixtures.
   - **time base is the cluster Timecode**, never CueTime: CueTime is the seek point's
     timestamp, which for the referenced block already is its own time, so adding the block's
     relative timecode double-counts it (+51 ms / +459 ms measured against ffmpeg).
   - **block headers first**: a block's track number is read from its header and the payload
     is only read for the wanted track — reading payloads for every block pulls the video
     into the extraction.
   - **metadata-only cluster scan** when the index has no entries for the track (or no Cues
     element at all): headers are read, payloads are skipped by seeking. This replaced a
     fall back to ffmpeg, which read the whole file.
   - reads are unbuffered (`bufferSize: 0`), so a seek costs the bytes it asks for instead
     of a 64 KB buffered refill.
   - reads are **windowed**: a 4 KB window while walking cluster headers, and a window
     sized to the cluster (up to 512 KB) while enumerating the blocks of a cluster the
     cue index named. A real remux cluster holds ~150 blocks (mostly audio frames), so
     one window per block would be ~150 device round trips per cluster — for an episode
     with 315 subtitle cue clusters that is ~46,000 round trips (the "reading cluster
     32/315" crawl). Windowed: 540 → 127 reads on a real 8.2 GB remux.
   - the audio analysis is cached **per media file** (path + VAD method + engine
     build), never per title, and always — the analysis happens regardless, so keeping
     it costs one small file and saves the whole audio pass on any later run.
   - cancellable: Kill's token is checked between clusters, so a slow read cannot pin a
     worker. Every child process (ffsubsync, ffmpeg) is started with the job token and
     registered, and Kill terminates whole trees rather than hoping a token is noticed.
   - reports `MkvExtractionStats` (method, clusters, blocks, MB, read calls, timings), which
     the service logs: `Extracted embedded subtitle in 28 ms (40 cues, seekhead-cues: 40
     clusters, 40 blocks, 0.2 MB in 505 reads, 28 ms ...)`. Assert on **bytes read**, not
     wall time — a fast local disk hides a reader that walks the whole file. The harness
     (`/opt/data/tmp/logictest.py`) builds synthetic remuxes and checks exactly that.
2. `Mp4SubtitleExtractor` — MP4/MOV `stbl` sample table (`tx3g`/`mov_text`, `text`).
3. `ExtractSubtitle` — ffmpeg demux, which reads the whole file.

Each skipped method logs its reason (`matroska-cues: no cue index`, `mp4-sample-table:
codec c608 needs ffmpeg`, …), so a slow extraction can be traced to a cause. Step 3 runs
under `ExtractionTimeoutMinutes` (default 20) and fails with a message naming the file
size, the timeout and the skipped reasons — a pathologically slow read then surfaces as an
error instead of a progress bar stuck at 5% (the phase extraction sets).

Why this matters: four parallel workers each falling back to ffmpeg on the same volume is
exactly how "all workers stuck at 5%" happens. Index reads remove the read entirely; the
volume gate below stops the rest from competing.

## Scheduling: auto mode, volume gating, media sharing

`SyncJobMode.ResolveAuto(totalTasks, distinctMediaFiles)` decides the mode when the setting
is `auto` (the default): ≤1 task → `normal`; one file → `fast`; several files →
`ultimate`. `ResolveModeForBatch` applies it to the batch's jobs before wave selection and
logs the decision.

`SubSyncService.SelectWave` takes a `WavePolicy`:

- `Limit` — worker count (parallel modes only).
- `IsHeavyIo` — true when the job must read a lot (embedded extraction, or an audio
  analysis with no cached speech signal). It is used for one rule only: a second job for a
  media file may join a wave solely when that file's speech analysis is already cached, so
  two workers never race to build the same cache entry.
- **Never let ffsubsync pull a subtitle stream out of the video.** `--reference-stream s:N` makes
  ffsubsync demux the whole file to extract that stream: measured on an 8.2 GB episode, 12.5 s and
  8218 MB read — per subtitle, so ten tracks meant ten full reads. The reference subtitle is
  extracted once by our own container-index reader (29.5 ms, 0.1 MB), cached as `<key>.ref.srt`
  beside the speech cache, and passed as a small file (0.6 s, 6.5 MB). Output is identical
  (verified byte-for-byte against the stream reference). If the extraction fails, the old
  stream-reference behaviour is kept rather than failing the sync.
- **The speech cache covers audio references only.** A sibling-subtitle reference has no speech
  signal to store (ffsubsync compares subtitles, which costs 0.6 s on a small file) — do not "fix"
  its absence by caching it, and do not read "it analyses every time" as a bug without checking
  `Embedded sync of <file>: deriving the speech signal from '<reference>'` in the log first.
- **A sibling-subtitle reference cannot see a shift shared by every subtitle of a file.** It
  compares two tracks on the same timeline, so a whole-file offset returns `+0 ms` and nothing
  changes. Only an audio reference can see that case. Keep this in mind before "fixing" a run that
  reports 0 for all tracks: it may simply be correct, or it may be blind, and the difference is
  whether the subtitles are actually mistimed on playback.
- **Embedded extraction has a floor, and it is latency, not volume.** A file with subtitle cue
  points costs ~1 read of ~4 KB per cue (measured: 326 cues = 342-371 kernel reads, 1.3 MB,
  0.08% of the file). Do not try to "optimise" it further by reading bigger windows — the next cue
  is a cluster away, so a bigger read buys nothing and costs bandwidth. Extraction time is
  `cues × storage read latency`, and several workers on one disk multiply that latency for every
  worker. Report `ms/read` rather than claiming a speed-up.
- Never report progress from counters that are filled in at the end of an operation: it produced
  a literal "0.0 MB, 0 reads" through an entire extraction. Fill counters live, and where possible
  cross-check them against the kernel (`/proc/self/io`).
- The queue is a **worker pool, not a wave barrier**: `PlanStart` computes the free slots
  (`limit − running`) and starts that many jobs; each finishing job wakes the pump to refill its
  own slot. Group semantics (awaiting a whole wave) wasted every finished worker's time and made
  all workers step together, which also hits the disk in one burst. Starts within a single
  dispatch are staggered by 400 ms.
- **The scheduler's candidate scan must be wide enough to find a wave, not just to hold one.**
  A cap of `limit x 4` candidates meant a ten-track episode covered only two media files, so every
  batch ran two wide no matter the setting. It now scales with the worker count, with an absolute
  ceiling so a huge queue cannot stall a pass.
- **Sharing a media file is decided by the policy alone** (`CanShareMediaFile`). An extra "heavy
  job" veto used to override it and blocked all overlap, which is why a file's later subtitles
  queued behind the first even after their reference was cached.
- A job whose media file is already being read waits for that file (to avoid racing the stored
  audio analysis) but never for anyone else; in-use volumes and in-use media files are passed
  into the selector as `InUseVolumes` / `InUseItemIds`.
- Waves are bounded by `ParallelWorkers` and nothing else — there is no per-volume budget.
  Volume information (`VolumeOf` → `MediaVolume.Of`) only expresses a *preference*: the first
  selection pass takes the first job of each distinct volume so a wave spreads over the
  storage you have, and a second pass then fills the remaining slots with whatever is left,
  same volume or not. A queue that lives on one disk therefore still runs at full width.

`MediaVolume.Of` maps a path to its mount point + device via `/proc/mounts` (longest match
wins), falling back to the path root where that file does not exist.
