# AGENTS.md

## Overview

Jellyfin plugin (C# / .NET 9) that synchronizes subtitles with video audio using a
**bundled** self-contained ffsubsync binary (linux-x64, PyInstaller). Runs on
Jellyfin 10.11+. No Python/venv provisioning, no root, no container rebuilds.

## Build & Run

```bash
dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release
# Output: Jellyfin.Plugin.SubSync/bin/Release/net9.0/Jellyfin.Plugin.SubSync.dll
```

No test runner exists. Deploy via the release zip (DLL + meta.json + bundled
`ffsubsync/linux-x64/`), which is served by the GitHub Pages plugin catalog.

## Architecture

```
Plugin.cs                        — Entry point (BasePlugin<PluginConfiguration>, IHasWebPages).
                                   Sets static Instance. Does NOT inject scripts.
SubSyncServiceRegistrator.cs     — DI registration (IServerServiceRegistrator): registers
                                   SubSyncService (singleton) and app.UseMiddleware<SubSyncMiddleware>().
Api/SubSyncMiddleware.cs         — Response middleware: injects <script src="/SubSync/ClientScript">
                                   into index.html responses (idempotent, per-request).
Configuration/PluginConfiguration.cs — Settings model (XML-serialized by Jellyfin).
Api/SubSyncController.cs         — REST API at /SubSync/* ([ApiController], [Authorize]).
                                   Serves the client JS via GET /SubSync/ClientScript.
Services/SubSyncService.cs       — Queue pump + sync engine (ffsubsync/ffmpeg), job tracking,
                                   batch model, copy/replace output, folder rescan.
Web/subsyncMain.html             — Main-menu page: Sync browser, History, Settings.
Web/subsync.js                   — Client script for detail/library ⋮ menus (single-item sync dialog).
Web/configPage.html              — Legacy Dashboard plugin-settings page.
```

## Key Patterns & Gotchas

- **Plugin GUID** `c7d8e9f0-a1b2-4c3d-e5f6-a7b8c9d0e1f2` stays consistent across meta.json,
  build.yaml, Plugin.cs and the JS pages.
- **Server-side queue**: every sync (dashboard batch or detail-page single) is a `SyncJob`
  registered in `_jobs`; one background pump (`PumpAsync`) runs queued jobs strictly one at
  a time in FIFO order. Batches group jobs via `BatchId`/`BatchIndex`/`BatchLabel`/`Label`.
  `_jobContexts` holds the resolved (video, stream, ordinal, config) per queued job.
- **Validation is eager**: bad item/stream requests fail immediately in `EnqueueSync`;
  execution is deferred to the pump. Batch tasks that fail validation become pre-failed
  jobs inside the batch instead of aborting it.
- **Web resources are embedded** via `<EmbeddedResource Include="Web\**" />`.
- **Main-menu pages lose their `<head>`** when jellyfin-web embeds them — ALL `<style>`
  for subsyncMain.html lives inside the body. Never move it back to head.
- **`emby-select`/`emby-input` inject sibling `<label>` elements** — never lay them out
  side-by-side in a flex row; stack fields instead.
- **ffmpeg mapping**: Jellyfin's `MediaStream.Index` IS the CONTAINER-wide stream
  index (video/audio/subtitle all counted). Embedded extraction maps with
  `-map 0:{Index}` — never `-map 0:s:N` (subtitle-scoped): deriving a subtitle
  ordinal by counting subtitle streams from MediaStreams is unreliable because
  that list also contains external sidecar tracks, skewing the count.
  Image-based embedded subs (PGS/DVD/VobSub) are rejected up front with a clear message.
- **Video files are NEVER written**: only read (as ffsubsync reference audio) or analysed.
  Embedded tracks are extracted and saved as new external sidecars
  (`{videoNameNoExt}-SYNCED.{lang}.srt`) — the remux path was deleted; do not restore it.
- **All process invocations use `ProcessStartInfo.ArgumentList`** (argv direct, no
  string escaping) — ffmpeg extraction and the ffsubsync engine included. Do not
  regress to hand-escaped argument strings.
- **Copy mode (default)**: output goes to a NEW sidecar — `{lang}.SYNCED.srt` for
  pure-language external originals (keeps Jellyfin's parser resolving the language),
  `{stem}-SYNCED.srt` otherwise, `{videoNameNoExt}-SYNCED.{lang}.srt` for embedded
  tracks. Replace mode overwrites the original external file with backup+rollback.
- **New sidecar discovery**: after a copy, the engine calls
  `ILibraryMonitor.ReportFileSystemChanged(dir)` (one-folder rescan) and refreshes the
  video item — no full library scan required.
- **SyncJob statuses**: Queued → Running → Completed/Failed, plus Cancelled (pre-run only).
  Terminal jobs are evicted after ~1h (cleanup timer also trims `_runOrder`/`_jobContexts`).
- **Library sweep** (`SubSyncSweepTask`, IScheduledTask): queues every EXTERNAL subtitle
  without a synced output onto the normal FIFO pump; embedded tracks are manual-UI only.
  Persistent skip/fail cache (`SweepState`, `{DataPath}/subsync/state/sweep-cache.json`)
  skips unchanged content whose output still exists and stops retrying tracks that fail
  `SweepFailStreakLimit` times; content change resets both. Ported from
  Marnalas/jellyfin-subsync (MIT).
- **Config allow-lists**: VAD method and output encoding are validated server-side
  (`AllowedVadMethods`/`AllowedOutputEncodings`) to prevent argument injection.
- **`TreatWarningsAsErrors` + `GenerateDocumentationFile`**: every public member needs an
  XML doc comment; all warnings are build errors.
- **Jellyfin NuGet packages use `<ExcludeAssets>runtime</ExcludeAssets>`** — compile-time
  only; at runtime the plugin resolves against Jellyfin's own assemblies.

## UI conventions

- Settings fields use the emby `label` attribute only (manual labels duplicate).
- Per-track results log `OK/FAIL <title> → <output path>`; paths come from the job's
  OutputPath so the user can verify what the engine wrote.
- Dashboard History is server-side (`/SubSync/Batches`), shared by all viewers.
