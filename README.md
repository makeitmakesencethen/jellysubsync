# Jellyfin SubSync Plugin

Automatically synchronizes subtitle timing with video audio using [ffsubsync](https://github.com/smacke/ffsubsync).

Originally forked from [camalolo/jellysubsync](https://github.com/camalolo/jellysubsync) (GPL-2.0) and since rebuilt into a standalone project — see [NOTICE.md](NOTICE.md) for full credits and bundled components, and [CHANGELOG.md](CHANGELOG.md) for what changed when.

## What you get

- **Bundled ffsubsync** — the plugin ships a self-contained linux-x64 ffsubsync binary inside the release zip, extracted automatically on first start. No Python, no pip, no root, no container rebuilds.
- **Detail-page sync** — a **"Sync Subtitles"** action in the ⋮ More menu of any movie or episode (also on library/home rows). Choose a subtitle track and sync it.
- **Dashboard menu** — a **SubSync** page in the main menu with a library browser:
  - click any movie or series to select it — sync options appear on that row
  - movies: pick one subtitle track or sync all
  - series: sync a whole series or a single season (all tracks, or just the first)
- **Server-side queue** — every run is one FIFO queue on the server, executed one task at a time. Start several runs: later ones queue and start automatically. Refreshing, closing the page, or opening the page in several tabs/devices never kills or duplicates a run — every viewer sees the same live state, and progress resumes with full detail after a reload.
- **Safe by default** — syncs write a new file like `uzb.SYNCED.srt` next to the original and never touch your original subtitle (pure-language originals get a Jellyfin-friendly name so the copy displays cleanly). An optional **Replace the original** mode exists. Video files are never modified: embedded tracks are extracted and saved as new external subtitle files.
- **History** — every run is recorded on the server with per-task results and written paths, shared across tabs/devices. History is in-memory: it does not survive a Jellyfin restart, and old entries are evicted (failed/cancelled runs on the next cleanup pass, completed runs after ~1 hour or in bulk once the total exceeds 50 jobs).
- **Library sweep** — a native Dashboard → Scheduled Tasks task ("Run Now" or on your own schedule) that syncs every external subtitle without a synced copy yet. A persistent skip/fail cache makes repeat runs cheap: unchanged subtitles whose synced file still exists are skipped, and tracks that fail 3 times in a row (configurable) stop being retried until their file changes. Runs through the same server queue as manual syncs.
- **Language filter** (Settings tab or the plugin config page) — list the subtitle languages you want and everything else is hidden from the library lists and skipped by the scheduled sweep. Codes and names are interchangeable (`sv`, `swe`, `sv-SE` and `Swedish` all match). Empty = sync every language.
- **Only syncable tracks are shown** — image-based subtitles (Blu-ray **PGS**, DVD **VobSub**, **DVB**, **XSUB**) can't be aligned, so they are never listed or queued instead of failing mid-run. Text subtitles are supported both embedded (**SRT**, **ASS/SSA**, **WebVTT**, **MOV_TEXT**) and as external files (**SRT**, **ASS/SSA**, **VTT**, **TTML**, **MicroDVD/SubViewer .sub**); synced output is always an SRT sidecar.
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
