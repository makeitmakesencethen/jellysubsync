# Changelog

All notable changes to this plugin are documented here. Versions follow
`MAJOR.MINOR.PATCH`; the plugin version is also what Jellyfin shows in the plugin list
(release zips are named `Jellyfin.Plugin.SubSync_<version>.0.zip`).

## 1.1.0 (beta)

- Added: multi-select in the library browser without checkboxes — click one movie or
  series, then **shift-click or right-click** any other rows to add or remove them
  individually (picks do not have to be adjacent). Right-click is the primary gesture;
  shift-click works too and no longer drag-selects the page text. The **Select all** link
  under the search box stays scoped to the current library + search view, and once
  anything is picked a full-width action row appears with a **Sync N files** button, a
  **Subtitles** dropdown and a count note. The dropdown lists the languages actually
  present in the picked files (with track counts), gathered by a background scan that
  reads the same cached subtitle lists the queue build uses; **several languages can be
  picked at once** (they show as removable chips next to the dropdown, and *All languages*
  clears them). Language codes are folded onto one canonical entry per language, so
  `sv`, `swe`, `sv-SE` and `Swedish` all become a single "Swedish" option. Series always
  expand to all their episodes.
  While the queue is assembled the button reads "Working…" and the run line shows
  "Reading subtitles… N/M files", then the batch joins the server queue (behind a running
  batch if one is streaming). Movies contribute their subtitle tracks; series contribute
  one track per language per episode (external preferred).
- Changed: image-based subtitle tracks (Blu-ray **PGS**, DVD **VobSub**, **DVB**, **XSUB**)
  are no longer listed anywhere — library rows, detail-page track lists, language
  dropdowns and the scheduled sweep skip them, so they can't be picked and fail. Only
  alignable text subtitles are offered.
- Added: a **global language filter**. In the dashboard **Settings** tab (own section,
  "Only sync subtitles in these languages") or the plugin config page (comma-separated),
  list the languages you want and everything else is hidden from every list and skipped by
  the sweep. Codes and names are interchangeable — `sv`, `swe`, `sv-SE`, `Swedish` all
  match the same language, as do `chi`/`zh`/`zho`/`Chinese`. Empty list = sync every
  language; while a filter is set, text tracks with no language at all are skipped because
  they can't be matched.
- Added: **shift+right-click** a row to select the whole range from the anchor (the last
  plain-clicked row) — the previous range behaviour, now opt-in so single-row right-click
  picking stays exact.
- Added: a small spinner next to the language dropdown while the picked files are being
  read ("reading subtitle lists… N/M files"), and the note now counts **subtitle tracks**
  once a language is picked — e.g. `280 files selected — series sync all their episodes —
  1,240 German subtitle tracks to sync` — with the button reading `Sync 1,240 subtitles`
  in that case. Counts are only shown once the scan is complete, so the number always
  matches the queue that gets built.
- Added: **multi-subtitle modes** — choose in the Settings tab (and per run in the library
  action row): **Normal** (one at a time, audio analysed every run), **Parallel** (several
  subtitles at once, `Parallel workers` 1-8) or **Fast** (the audio is analysed once per
  media file and the speech signal is reused for its other subtitles — identical results,
  ~2-3x less work per extra subtitle). The cached speech signal lives in the plugin's state
  directory (~2 KB per file) and is keyed by file size/mtime, VAD method and ffsubsync
  build, so a replaced file never reuses stale data. Nothing is ever written into media
  folders.
- Changed: **Cancel now reports and escalates.** Pressing Cancel drops queued tasks and
  then tells you what happened: how many tasks were dropped and whether any run is still
  working (a running ffsubsync cannot be interrupted). While processes are still alive the
  button becomes a red **Kill all syncing**, which terminates the running ffsubsync/ffmpeg
  processes and empties the whole queue across every batch (`POST SubSync/Kill`); the log
  and phase line report how many runs were killed and whether any are still shutting down.
  `GET SubSync/Active` backs the reporting.
- Added: **Ultimate mode** — parallel plus audio reuse: several media files at once, each
  file's speech analysis computed once and reused by its remaining subtitles. Same
  alignments as every other mode; the only difference is throughput.
- Added: **per-worker progress** in Parallel and Ultimate modes — the run box now shows one
  row per worker (file, phase, own progress bar) instead of only a single combined line.
- Added: **fast embedded extraction for Matroska** — embedded text subtitles are read via
  the file's cue index instead of demuxing the whole container. On a 1.7 GB test file that
  is 202 ms instead of 2.32 s (the fallback path scales with file size: minutes over a
  NAS), with the same cues and identical text. Falls back to ffmpeg automatically for
  anything it cannot handle (image codecs, laced blocks, compressed blocks, no cue index)
  and can be disabled with `FastMkvExtraction`.
- Changed: parallel waves now cover **different media files only** — several subtitle
  tracks of the same file are never processed simultaneously, since that would have two
  processes reading the same file and repeating the same audio analysis.
- Changed: default parallel workers 2 → **4**.
- Added: speech-cache housekeeping — entries unused for 30 days are pruned automatically
  and the cache is capped at 250 MB; the Settings tab shows its current size with a
  **Clear cache** button (`POST SubSync/SpeechCache/Clear`). Each entry is a few KB and
  lives in Jellyfin's plugin data directory, never in a media folder.
- Changed: the Settings language picker no longer uses a native `<datalist>` (its popup
  opened as an enormous list that could not be sized) — it is now a compact, scrollable
  suggestion box filtered as you type.
- Fixed: selecting a series in the library showed **no languages at all** in the row's
  Subtitle picker unless a specific season was chosen — the language scan started before
  the row existed; it now starts right after the row is rendered. The picker also folds
  language codes together (`sv` / `swe` / `sv-SE` / `Swedish` are one entry, shown with
  friendly names and per-track counts).

## 1.0.10

- Fixed: the sync outcome (offset in ms / framerate ratio) is now shown in the live
  progress log and in expanded history, not just in the history log builder.

## 1.0.9

- Added: every successful sync reports what it actually did — signed offset in
  milliseconds (`+79 ms offset`) and, when a framerate mismatch was corrected, the
  fitted `framerate ratio 1.0004x` with the cumulative drift it fixed.
- Fixed: a subtitle that ffsubsync judged already in sync (shift under its 3 s
  threshold) used to fail with "output file was not created"; it is now a success
  reported as "already in sync — no change needed".

## 1.0.8

- Added: syncs can be queued while another run streams — stack as many movies, seasons
  and series as you like; each joins the server queue and the UI advances to the next
  queued run automatically.

## 1.0.7

- Fixed: embedded subtitle extraction on files that mix embedded and external tracks
  (`Failed to set value '0:4' for option 'map'`). The real container stream is now
  located by probing the file with ffmpeg instead of trusting Jellyfin's stream index.

## 1.0.6

- Fixed: embedded tracks on files with both embedded and external subtitles
  (`Failed to set value '0:s:2' for option 'map'`).

## 1.0.5

- Added: series/season scopes select subtitles by language (friendly name + track
  count); duplicate languages per episode are merged, preferring the external track.

## 1.0.4

- Added: language filter for series/season syncs.
- Removed: the "first track only / all tracks" toggle for series — the language picker
  makes it redundant.

## 1.0.3

- Added: library sweep as a native Jellyfin Scheduled Task ("Sync subtitles (library
  sweep)") with a persistent skip/fail cache, so repeat runs only touch new or changed
  subtitles. Ported from Marnalas/jellyfin-subsync (MIT).

## 1.0.1 – 1.0.2

- Added: episode-accurate batch counters and task titles (episode x/y, task n/m).
- Change: video files are never modified — embedded subtitles are extracted and saved
  as new external sidecar files; the remux path was removed entirely.
- Docs: architecture, API reference and configuration notes synchronised with the code.

## 1.0.0

- First public release: bundled self-contained ffsubsync (linux-x64), zero setup on
  Docker, detail-page "Sync Subtitles" action, dashboard library browser with per-track
  selection, copy-by-default output (`-SYNCED.srt`, original untouched), server-side
  FIFO batch queue with history that survives page reloads.
