# API Reference

All endpoints are under the `SubSync` route prefix, require `[Authorize]`, and live in
`SubSyncController.cs` (`Jellyfin.Plugin.SubSync/Api/SubSyncController.cs`).

Two exceptions to note:

- `GET /SubSync/ClientScript` is `[AllowAnonymous]` — the injected script has to load on the
  sign-in page too. It is the only endpoint that answers without a token.
- Clients must authenticate with `Authorization: MediaBrowser Token="…"` or `?ApiKey=…`.
  Jellyfin 12 disables the legacy `X-Emby-Token` header and `?api_key=` by default, so those
  two return 401 even with a valid token (see `jellyfin-12-migration.md`).

## Endpoints

| Method | Path | Handler | Request | Response | Notes |
|--------|------|---------|---------|----------|-------|
| GET | `/SubSync/Subtitles/{itemId}` | `GetSubtitles` | `itemId: Guid` (route) | `List<SubtitleInfo>` | 404 if item not found or not video |
| POST | `/SubSync/Sync` | `SyncSubtitle` | `SyncRequest` (JSON body) | `SyncJob` | 404 for FileNotFoundException, 400 for InvalidOperationException |
| GET | `/SubSync/Jobs/{jobId}` | `GetJobStatus` | `jobId: string` (route) | `SyncJob` | 404 if job not found |
| GET | `/SubSync/Jobs` | `GetAllJobs` | — | `IEnumerable<SyncJob>` | Returns all tracked jobs |
| POST | `/SubSync/Batch` | `CreateBatch` | `BatchCreateRequest` (JSON body) | `BatchView` | Enqueues a batch; tasks run FIFO on the server pump; invalid tasks become pre-failed entries |
| GET | `/SubSync/Batch/{batchId}` | `GetBatch` | `batchId` (route) | `BatchView` | Live per-task view used for progress + expanded history |
| GET | `/SubSync/Batches` | `GetBatches` | — | `IEnumerable<BatchSummary>` | History list (newest first, shared by all viewers) |
| POST | `/SubSync/Batch/{batchId}/Cancel` | `CancelBatch` | `batchId` (route) | `BatchView` | Cancels not-yet-started tasks of the batch |
| GET | `/SubSync/InstallationStatus` | `GetInstallationStatus` | — | `FfSubSyncInstallationStatus` | Reports bundled/custom/legacy-managed engine availability |
| GET | `/SubSync/Install` | `InstallFfSubSync` | — | `{ message: string }` | Legacy: installs a managed venv (no-op path when a bundled binary exists) |
| GET | `/SubSync/ClientScript` | `GetClientScript` | — | `application/javascript` | Serves embedded `subsync.js` |

## Request/Response Types

### SyncRequest

| Field | Type | Constraints |
|-------|------|-------------|
| `ItemId` | `Guid` | Required |
| `SubtitleIndex` | `int` | `[Range(0, 999)]` |

Source: `SubSyncController.cs:175-183`

### SubtitleInfo

| Field | Type | Notes |
|-------|------|-------|
| `Index` | `int` | Stream index within media source |
| `Title` | `string` | Display title (language + title) |
| `Language` | `string` | Three-letter code |
| `IsExternal` | `bool` | Whether sidecar file |
| `ExternalPath` | `string?` | `[JsonIgnore]` — not in API responses |
| `HasSyncedVersion` | `bool` | Whether a completed sync exists for this stream |

Source: `SubSyncService.cs:32-52`

### SyncJob

| Field | Type | Default | Notes |
|-------|------|---------|-------|
| `Id` | `string` | `Guid.NewGuid().ToString("N")` | 32-char hex, no hyphens |
| `ItemId` | `Guid` | — | Jellyfin item ID |
| `SubtitleIndex` | `int` | — | Stream index |
| `Status` | `SyncJobStatus` | `Queued` | See enum below |
| `Progress` | `double` | `0.0` | 0.0 to 1.0 |
| `Phase` | `string` | `"Preparing"` | Human-readable phase label |
| `Error` | `string?` | `null` | Error message if Failed |
| `OutputPath` | `string?` | `null` | Output file path after success |
| `CreatedAtUtc` | `DateTime` | `UtcNow` | Queue order |
| `FinishedAtUtc` | `DateTime?` | `null` | Set on terminal state |
| `BatchId` | `string?` | `null` | Owning batch, null for standalone jobs |
| `BatchIndex` | `int` | `-1` | 0-based position inside the batch |
| `BatchLabel` | `string?` | `null` | Batch scope label (e.g. "Series · Season 2") |
| `Label` | `string?` | `null` | Human display label (subtitle/track title) |

Source: `SubSyncService.cs:57-103`

### FfSubSyncInstallationStatus

| Field | Type | Notes |
|-------|------|-------|
| `IsInstalled` | `bool` | A usable engine is available (bundled, custom, or managed) |
| `ManagedBinaryPath` | `string?` | Path to legacy managed venv ffsubsync |
| `VenvPath` | `string?` | Path to legacy managed virtualenv |
| `ResolvedBinaryPath` | `string?` | Actual binary that will be used |
| `PythonAvailable` | `bool` | System python3 found |
| `PythonVersion` | `string?` | Python version string |
| `FfSubSyncVersion` | `string?` | Engine version string |
| `BundledFfSubSyncVersion` | `string?` | Version of the plugin-shipped binary, if present |
| `BundledRid` | `string?` | Runtime identifier the bundled binary targets (e.g. `linux-x64`) |

Source: `SubSyncService.cs:108-136`

### SyncJobStatus Enum

| Value | Meaning |
|-------|---------|
| `Queued` | Waiting to start |
| `Running` | Currently executing |
| `Completed` | Finished successfully |
| `Failed` | Errored out |
| `Cancelled` | Cancelled before it ran (batch cancel) |

Source: `SubSyncService.cs:15-27`

## Client-Side Integration

The client JS (`subsync.js`) is an ES5 IIFE that:
- Hooks `document` click events looking for `button.btnMoreCommands` (the "More" button)
- Polls for `.actionSheetContent` with up to 20 retries at 100ms intervals
- Injects a "Sync Subtitles" menu item with `data-id="subsync"`
- Shows a modal dialog for subtitle selection and sync progress
- Polls `GET /SubSync/Jobs/{jobId}` every 2000ms for progress updates
- Authenticates all requests via `X-Emby-Token` header from `ApiClient.accessToken()`


## Cancel vs Kill

- `POST SubSync/Batch/{batchId}/Cancel` — marks only that batch's *queued* jobs as
  `Cancelled`. A running job keeps running: ffsubsync has no way to resume a partial
  analysis, so interrupting one only wastes the work already done.
- `POST SubSync/Kill` — the destructive one: cancels every queued job in every batch and
  cancels each running job's `CancellationTokenSource`, which terminates the ffsubsync and
  ffmpeg processes (the process helper registers `process.Kill(entireProcessTree: true)` on
  cancellation). Returns `{ queuedCancelled, runningKilled, stillRunning, stillQueued }`.
- `GET SubSync/Active` — `{ running: [ { jobId, batchId, title, phase, progress } ], queued }`,
  used by the UI to tell the user whether a cancel has fully taken effect.

`RunSyncJob` receives its job's token and passes it to the ffsubsync run, the ffmpeg
subtitle extraction and the library refresh, so a kill stops the work rather than letting
it finish in the background. Cancelled jobs land in `SyncJobStatus.Cancelled` with the
phase `Cancelled`.
