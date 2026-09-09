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
