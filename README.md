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
- **Fast embedded extraction** — for Matroska files the plugin reads an embedded text subtitle through the file's cue index (kilobytes of I/O) instead of demuxing the whole container, which costs one full-file read per track (minutes for a large file on a NAS). Measured on a 1.7 GB test file: 202 ms indexed vs 2.32 s with ffmpeg, same 600 cues and identical text. Anything the index reader can't handle (image codecs, laced blocks, no cue index) falls back to ffmpeg automatically, and it can be switched off in the config.
- **Speech cache** (fast mode) — one small file per media file (a few KB, measured ~2 KB for a 15-minute episode) holding the detected speech signal. It lives in Jellyfin's plugin data directory (`<data>/subsync/state/speech-cache`), never next to your media. Entries unused for 30 days are pruned automatically, the cache is capped at 250 MB, and the Settings tab shows its size with a **Clear cache** button.
- **Automatic sync strategy** — the mode is chosen from the work, so there is nothing to configure: one subtitle on one file stays sequential; several subtitles of one file analyse the audio once and reuse it; several files run in parallel with each file's analysis reused. Manual overrides exist in Settings for troubleshooting. Every strategy produces identical alignments.
- **Three-deep extraction** — embedded subtitles are read through the container's own index first (Matroska `Cues`, MP4 `stbl` sample table — kilobytes instead of a full-file read), then ffmpeg as a fallback, guarded by a configurable timeout that reports why a slow read happened instead of looking hung.
- **Per-worker progress** — in Parallel and Ultimate modes the run box lists one row per worker with the file it is on, its current phase and its own progress bar, on top of the overall batch bar.
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
