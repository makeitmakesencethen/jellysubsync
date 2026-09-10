# Changelog

All notable changes to this plugin are documented here. Versions follow
`MAJOR.MINOR.PATCH`; the plugin version is also what Jellyfin shows in the plugin list
(release zips are named `Jellyfin.Plugin.SubSync_<version>.0.zip`).

## 1.1.0 (beta)

- Added: multi-select in the library browser — a checkbox per row, **Select all shown**
  (scoped to the current library + search view) and **shift-click** to extend over a
  range. **Sync selected (N)** queues every picked item into one batch: movies
  contribute their subtitle tracks, series contribute one track per language per
  episode (external preferred). Works while another run is streaming — the new batch
  joins the queue behind it.

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
