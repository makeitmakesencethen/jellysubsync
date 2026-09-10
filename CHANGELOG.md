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
- Added: **one-time config migration** to the automatic strategy. Installs that stored an
  explicit mode (the pre-1.1.0.16 default was `normal`) are moved onto `auto` the first time
  the new build loads, and it is saved immediately — so an upgrade gets the new behaviour
  without opening the settings page. Manual overrides chosen afterwards are respected.
- Fixed: parallel runs on a **single-volume library effectively ran one task at a time**.
  The per-volume heavy-read gate allowed exactly one heavy reader, and with an uncached
  library every first analysis is heavy — so four workers sat idle. The gate now takes a
- Fixed: the per-worker progress rows vanished under the automatic strategy — the panel
  keyed on an explicit parallel mode, and `auto` is resolved only once the run starts. It
  now shows whenever anything is running, and the run line names the strategy and worker
  count (`parallel + reusing audio analysis · 2 workers · task 12/284: …`).
- Changed: extraction now **says what it is doing**. The phase names the method before the
  work starts — `Extracting subtitle with the Matroska cue index` / `with the MP4 sample
  table` / `with ffmpeg — reading 18432 MB, up to 20 min` — and while ffmpeg works the phase
  tracks its position (`Extracting subtitle with ffmpeg — 43% of the file read`), driven by
  ffmpeg's `-progress` stream, with elapsed time per worker in the run box.
- Changed: the library browser now reads subtitle lists in **bulk** (`POST
  SubSync/Subtitles/Batch`, 25 items per request, series expanded server-side) instead of one
  request per file and per episode — a library-wide selection went from hundreds of
  sequential round-trips to a handful.
- Added: **automatic sync strategy** — the mode picker is gone from the Sync tab. `auto`
  (now the default) decides per run: one subtitle on one file stays sequential; several
  subtitles of one file analyse the audio once and reuse it; several files run in parallel
  with each file's analysis reused. The explicit modes remain as a troubleshooting override
  in Settings.
- Added: storage-aware scheduling — later removed in 1.1.0.20 as unnecessary
  (`MediaVolume.Of` maps a path to its mount point/device), so parallel workers sharing a
  disk no longer stall together; tasks whose audio analysis is already cached read nothing
  and still run fully in parallel. Several subtitles of one file may now share a wave once
  that file's speech signal is cached.
- Added: **MP4/MOV index extraction** — embedded `tx3g`/`mov_text` subtitles are read
  through the file's sample table instead of demuxing it (verified against ffmpeg: 200/200
  cues, identical text, 0.000 s timing difference). The extraction chain is now Matroska
  cues → MP4 sample table → ffmpeg, each skipped method logging its reason.
- Added: **extraction timeout** (`ExtractionTimeoutMinutes`, default 20) — a slow or stuck
  ffmpeg fallback now fails with a message naming the file size, the timeout and why the
  indexed readers could not be used, instead of showing a progress bar frozen at 5%.
- Fixed: **embedded subtitles could be aligned against themselves.** ffsubsync's default
  detector (`subs_then_*`) prefers a file's embedded text subtitle as the speech signal —
  but when the subtitle being synced *is* that embedded track, the alignment can only
  return zero, so the sync silently did nothing ("already in sync"). Measured: a track 6 s
  out of sync came back unchanged (offset 0.000); the same file with the reference pointed
  at its other text track was corrected by exactly −6.000 s. Embedded syncs now pass
  `--reference-stream s:N` for another text track when the file has one, and `a:0` (audio)
  when the track is alone — the speech-signal choice is also part of the speech-cache key.
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
  FIFO batch queue with history that survives page reloads.## [1.1.0.26]

### Added
- **Visible batch width.** The batch view now reports the effective worker limit and the run
  line shows `2/4 workers` (running / limit) whenever a parallel batch still has work left, and
  every wave logs its own width: `Wave: starting 2 job(s) (worker limit 4, mode ultimate, 37
  still queued)`. Asked "why did a 20-episode series only run two at a time?" the scheduler had
  no answer to give; now the setting and the wave size are both stated.

  The selector itself was verified against that exact shape: 20 episodes, two subtitles each,
  one disk — it selects four jobs, and eight when eight workers are configured.

## [1.1.0.25]

### Fixed
- **Extraction now uses `CueRelativePosition`, which is why it was still slow.** Real remuxes
  record, for every cue point, the exact byte offset of the referenced block inside its
  cluster — the remux here has it on 1873 of 1873 cue points, written by both ffmpeg and
  mkvmerge. The reader ignored it and walked the cluster's blocks instead, and a remux cluster
  holds ~150 of them (mostly audio frames): an episode with 344 subtitle cue points meant tens
  of thousands of small reads. Now an index entry means *one* small read.

  Measured, same files, same output:

  | case | before | after |
  | --- | --- | --- |
  | real 8.2 GB remux, 4 subtitles | 127 reads, 7.0 MB | **28 reads, 0.1 MB, 27 ms** |
  | 1,400 subtitle cue points | 1,421 reads, 91.8 MB | **1,413 reads, 5.8 MB** |

  Output is byte-identical to ffmpeg's own extraction of the same track. When a file has no
  block offsets (or holds a cluster in a shape that does not match), the cluster walk still
  runs, so nothing regresses — the harness asserts that the indexed path reads less than the
  walk on identical fixtures.

- **Fixed a time offset the indexed path introduced**: CueTime is the seek point's timestamp,
  which for the referenced block is already its own time, so using it as the base and adding
  the block's relative timecode double-counted it (+51 ms and +459 ms against ffmpeg). The
  cluster's Timecode element is used as the base instead — the same base the walking path
  uses — which is muxer independent.

### Changed
- Extraction progress reports its cost while it works: `reading subtitle 80/344 (1.2 MB, 96
  reads)` instead of a bare cluster counter, so a slow read can be told apart from a slow disk.

## [1.1.0.24]

### Fixed
- **Kill now kills.** The sync's ffsubsync run was started with `CancellationToken.None`, so
  the per-job token that Cancel/Kill fires never reached it: the process kept running after
  the button was pressed, and so did the ffmpeg it spawns internally. Every child process
  (ffsubsync, both ffmpeg paths) now gets the job's token, all of them are registered while
  they run, Kill terminates whole process trees directly instead of relying on a token being
  noticed, and the log reports what was killed and whether anything survived. A killed job is
  recorded as *cancelled*, not failed. The in-process Matroska extraction honours the token
  too, so a slow read can be interrupted instead of holding a worker.

- **Reading a cue cluster no longer costs one disk round trip per block.** A real remux holds
  ~150 blocks in a cluster (mostly audio frames), and the reader has to look at each one to
  find the subtitle block — with unbuffered reads that was ~150 round trips per cluster, and
  an episode with 315 subtitle cue clusters meant roughly 46,000 of them. That is the
  "reading cluster 32/315" crawl. Reads are now windowed: a 4 KB window while walking cluster
  headers, and a window sized to the cluster (up to 512 KB) while enumerating the blocks of a
  cluster the cue index pointed at. Measured on a real 8.2 GB Blu-ray remux: **540 → 127 read
  calls**, 29.6 ms, identical 4 cues.

### Changed
- **The audio analysis is always kept.** It was only cached in `fast`/`ultimate` mode, so a
  single-subtitle sync threw the result away and every later run (or the next subtitle of
  that file) analysed the audio again. The cache key is per *media file* (path + VAD method +
  engine build), never per title, so a series gets one entry per episode and nothing is keyed
  by name. Since the analysis happens anyway, keeping it costs one small file and makes
  re-runs and extra subtitles skip the audio pass. The UI no longer narrates caching — the
  phase simply says *analysing the audio* or *reusing the audio analysis*.

- **Run line simplified.** While the batch is assembled it says `Loading…` instead of a
  running commentary of file and track counts. While it runs it shows only what is meaningful:
  a strategy word (`parallel`, `reusing the audio analysis`) when there is one to state, the
  worker count only when more than one worker is active, and a single position counter
  (`episode 6/160`, or `task 4/12` when the batch covers one episode). Sequential runs no
  longer label themselves "single", and episode/task counters are no longer repeated.

### Audited
- The library browser and series sync use the same service code as every other entry point,
  so the settings apply there too: the language filter and image-track exclusion (enforced
  server-side when tracks are listed *and* re-checked when a job runs), the sync strategy and
  worker count, copy vs replace, golden-section search, VAD method, ffmpeg/ffsubsync paths,
  encoding, offset limits, and the indexed-extraction settings. The browser sends no mode of
  its own — it inherits the configured one, including the automatic strategy.

## [1.1.0.23]

### Fixed
- **Several subtitles of one movie now sync in parallel, not one after another.** The
  automatic strategy picked *fast* (reuse only) whenever a batch covered a single media file,
  and *fast* is not a parallel mode — so the first subtitle analysed the audio and every
  other one then queued up behind it, each waiting its turn even though its work was already
  cheap. That case now resolves to *ultimate*: the first subtitle builds the speech analysis,
  and the remaining ones align in parallel against that cached signal, up to the configured
  worker count.

  The file-sharing rule still applies and is what keeps this safe: a second subtitle of the
  same file only joins a wave once that file's speech analysis is cached, so the analysis is
  built exactly once and no two workers race to build it.

  Explicit modes are unchanged — *Fast* still runs sequentially with reuse if you select it
  in Settings for troubleshooting.

## [1.1.0.22]

### Fixed
- **Embedded Matroska extraction is now genuinely index-based.** The previous reader located
  subtitles with hundreds of thousands of tiny reads and then read the *video payload* of
  every block it looked at, so it cost a large share of the file instead of a few
  kilobytes — and when the cue index had no entries for the subtitle track, it gave up and
  let ffmpeg demux the whole file, which is the "extraction takes longer than the sync" case
  on a remux.

  Measured on a 63 GB Blu-ray-shaped remux (12,000 clusters, cue index at the end of the
  file), producing the same 40 subtitle cues:

  | case | before | after |
  | --- | --- | --- |
  | subtitle cue points present | 612 ms, 996 MB read, 12,214 reads | **28 ms, 0.2 MB, 505 reads** |
  | no cue points for the track | failed → ffmpeg reads 63 GB | **95 ms, 1.8 MB, 108,138 reads** |
  | no Cues element at all | failed → ffmpeg reads 63 GB | **113 ms, 1.7 MB, 120,134 reads** |

  What changed in `MkvSubtitleExtractor`:
  - the SeekHead is used to jump to Tracks and Cues instead of walking every cluster;
  - the cue index is read in one bulk pass and parsed from memory;
  - block *headers* are read first and payloads only for the wanted track, so other tracks'
    data is skipped rather than read;
  - when the index has no entries for the track (or no Cues element), the plugin now scans
    cluster metadata only — headers read, payloads skipped by seeking — instead of falling
    back to ffmpeg, which read the entire file;
  - reads are unbuffered, so a seek costs the bytes it asks for instead of a 64 KB refill;
  - truncated or malformed elements stop the scan gracefully instead of failing the
    extraction.

- Extraction now reports what it cost in the log (`method, clusters, blocks, MB, read
  calls, timings`), and the job phase tracks the work (`Extracting subtitle: reading cluster
  12/40 from the cue index`, `scanning clusters (400 read, 0.4 MB, 7 subtitles found)`), so a
  slow extraction is visible instead of frozen at 5%.

## [1.1.0.21]

### Changed
- Parallel waves now **spread across storage volumes as a preference**: the scheduler takes
  one file per volume first, then fills the remaining worker slots with whatever is left,
  same disk or not. A batch that spans several disks runs one file from each instead of
  hammering one; a batch that lives on a single disk still runs at the full worker count,
  because nothing is blocked. Volumes never cap a wave — they only decide which job goes in
  first. The rule that several subtitles of the same media file never start before that
  file's audio analysis is cached is unchanged.

## [1.1.0.20]

### Removed
- The **per-volume heavy-read limit** is gone, along with its `HeavyReadsPerVolume` setting.
  Waves are bounded by `Parallel workers` and nothing else, so parallel work now runs at the
  width you ask for — four workers means four tasks, whether or not they sit on one disk.
  Storage scheduling is left to the OS, which sees the real device queue. The one remaining
  rule is correctness, not throttling: two subtitles of the *same* media file never run
  before that file's speech analysis is cached, so they cannot race to build it.


