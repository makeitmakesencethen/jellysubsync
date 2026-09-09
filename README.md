# Jellyfin SubSync Plugin

Automatically synchronizes subtitle timing with video audio using [ffsubsync](https://github.com/smacke/ffsubsync).

Public fork of [camalolo/jellysubsync](https://github.com/camalolo/jellysubsync) (GPL-2.0), rebuilt for zero-setup installs with a server-side batch queue.

## What you get

- **Bundled ffsubsync** — the plugin ships a self-contained linux-x64 ffsubsync binary inside the release zip, extracted automatically on first start. No Python, no pip, no root, no container rebuilds.
- **Detail-page sync** — a **"Sync Subtitles"** action in the ⋮ More menu of any movie or episode (also on library/home rows). Choose a subtitle track and sync it.
- **Dashboard menu** — a **SubSync** page in the main menu with a library browser:
  - click any movie or series to select it — sync options appear on that row
  - movies: pick one subtitle track or sync all
  - series: sync a whole series or a single season (all tracks, or just the first)
- **Server-side queue** — every run is one FIFO queue on the server, executed one task at a time. Start several runs: later ones queue and start automatically. Refreshing, closing the page, or opening the page in several tabs/devices never kills or duplicates a run — every viewer sees the same live state, and progress resumes with full detail after a reload.
- **Safe by default** — syncs write a new file like `uzb.SYNCED.srt` next to the original and never touch your original subtitle (pure-language originals get a Jellyfin-friendly name so the copy displays cleanly). An optional **Replace the original** mode exists; embedded tracks always keep their original stream.
- **History** — every run is recorded on the server with per-task results and written paths, shared across tabs/devices.
- **Full engine settings** — VAD method, max offset / subtitle seconds, output encoding, golden-section search, sync output mode, ffmpeg / ffsubsync path overrides. ffmpeg is detected automatically from Jellyfin.

## Installation

1. Jellyfin → **Dashboard → Plugins → Catalog** → repositories (gear icon) → **add repository**
2. Repository URL: `https://makeitmakesencethen.github.io/jellysubsync/manifest.json`
3. Install **SubSync** from the catalog and restart Jellyfin

No further setup — the bundled sync engine is ready after restart.

## Requirements

- Jellyfin 10.11+ on **linux-x64** (the bundled binary is platform-specific)
- ffmpeg is expected to be present; Jellyfin ships its own, which the plugin detects automatically

## Building from source

```bash
dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release
```

The release pipeline (`.github/workflows/release.yml`) additionally builds the bundled ffsubsync binary via PyInstaller and publishes the plugin catalog to GitHub Pages.

## License

GPL-2.0 — see [LICENSE](LICENSE). Fork of [camalolo/jellysubsync](https://github.com/camalolo/jellysubsync); bundled ffsubsync is by [smacke](https://github.com/smacke/ffsubsync) (MIT).
