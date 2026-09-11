# SubSync backend — exhaustive test, speed and reliability brief

You are working on the **backend of the Jellyfin plugin `Jellyfin.Plugin.SubSync`** at
`/opt/data/jellysubsync` (C#, `net10.0`, Jellyfin 12). The plugin takes subtitles that are out of sync and
fixes them against a reference (another subtitle track of the same file, or the audio). "Downloading" in
this brief means everything the backend does to *acquire* text and *use* it: extracting embedded subtitle
tracks from MKV/MP4, reading external sidecar subtitles, building the reference track, the ffsubsync run,
and the caches that make repeats cheap.

**Mission:** drive every path the product offers — one subtitle and bulk, movies and series, from the menu
page and from every detail page, with every setting, mode and filter it exposes, and every failure it can
meet. Document exactly what happens in a report meant for later patching. Then make single and bulk sync
**as fast as possible with zero failures**, and prove each change with numbers. Document first, optimise
second.

Read `knowledge/audit-2026-09-11.md` first: it is the audit of this same plugin (19 live findings, 60
static ones). Do not rediscover it — start from it, and treat its findings as the backlog you are testing
and then fixing.

## 1. Hard rules (breaking these fails the job)

- **Real work only.** Real Jellyfin, real media files, real ffsubsync runs. No mocks, no "should work",
  no claim that is not backed by a log line, a job record or a command you actually ran.
- **Never fabricate a number or an outcome.** Report bytes read, read calls, ms per read, stage times and
  cue counts as the plugin's own log prints them. If something is not measured, write "not measured".
- **Phase 1 changes no production code.** It produces the report. Fixes happen in phase 2, one finding at a
  time, each with a before/after measurement.
- **Product rules that must not regress:** the plugin never refuses a job (a bad reference means falling
  back to the audio, not declining); "Clear cache" clears everything (speech cache, extracted subtitles,
  reference files, scratch); progress is honest (real MB/reads/ms, never a fake percentage); the original
  subtitle is never destroyed without a deliberate setting and a way back.
- **Test on your own Jellyfin**, not the user's. The user's server may be used read-only for evidence
  (their plugin log is mounted at `/subsync-logs/`), and only for the shows they have allowed. Ask before
  anything else on their server.
- Keep the check suite green: `python3 tests/run_checks.py` currently passes 333 checks. If a change makes
  a check fail, that is a finding, not something to delete.
- Never leave the test server or the test library in a broken state: restore settings, delete fixtures,
  say where every file went.

## 2. What exists (verify it yourself; this list may be stale)

- **HTTP API** (`/SubSync/...`): `Subtitles/{itemId}`, `Subtitles/Batch`, `Sync`, `Batch`,
  `Batch/{id}`, `Batches`, `Batch/{id}/Cancel`, `Jobs`, `Jobs/{id}`, `Active`, `Kill`, `Log`,
  `InstallationStatus`, `Install`, `SpeechCache/Clear`, `ClientScript`.
- **Three entry points in the UI:** the plugin's **menu page** (`configurationpage?name=subsync-main`:
  library picker → series/movie rows → sync buttons), the **item detail page** of a movie/episode
  (injected "Sync Subtitles" action, one track or all tracks), and the **series/season detail page**
  (injected "Sync all episodes": whole series or one season).
- **Modes:** `MultiSyncMode` = auto / normal / ultimate (resolved per batch and reported in the batch
  view); single jobs vs batches; the extraction lane runs alongside the sync workers.
- **Settings** (`PluginConfiguration`): `ParallelWorkers`, `ExtractionTimeoutMinutes`, `MultiSyncMode`,
  `SyncLanguages` (filter), `FfSubSyncPath`, `FfmpegPath`, `VadMethod`, `MaxOffsetSeconds`,
  `MaxSubtitleSeconds`, `OutputEncoding`, `FixFramerate`, `UseGoldenSectionSearch`, `SyncModeCopy`,
  `SweepFailStreakLimit`, `SweepMaxItemsPerRun`.
- **Caches:** extracted-subtitle cache (per file+track), speech/audio cache, reference store, job scratch.
- **Job states:** Queued → Running (with a Phase string) → Completed / Failed / Cancelled; the batch view
  adds Ok / Failed / Cancelled / Partial counters.
- **Sweep:** `SubSyncSweepTask` (scheduled) with two limits that no GUI exposes.
- **Reference material:** `knowledge/audit-2026-09-11.md` (findings), `knowledge/side-issues-2026-09-11.md`,
  `tests/fixtures/patch_cues.py` (fixture that produces a mixed cue index), the plugin log at
  `<data>/subsync/logs/subsync.log`.

## 3. Test matrix — attempt every row, and name the ones you could not

For each row record: entry point, mode, item kind, settings in force, wall-clock, per-stage time, bytes
read, read calls, cue counts, ffsubsync time, resulting file (path + action), and pass / fail / blocked
with the job id.

**A. Modes × kinds (the core of the ask)**
1. Single subtitle: one movie, one track, "keep original" (copy) mode.
2. Single subtitle: same, replace mode — and confirm what happens to the original (audit D16).
3. All tracks of one movie (language picker "all").
4. All tracks of one episode.
5. Whole series from the series detail page (every episode, every queued track).
6. One season from the season detail page.
7. A hand-picked multi-episode selection from the menu page.
8. Bulk across several shows at once (two or three files in flight, workers = 1, 2, 4, 8).
9. Re-sync of something already synced (cache hits: what is read, what is skipped, what is written).
10. Same item + same track queued twice; the same batch queued twice.

**B. Entry points × menu mode (every variation, not one happy path)**
11. Menu page: library → series → episodes → tracks → sync; then back and a different item.
12. Menu page with the language filter set: one language, several, a language the file does not have.
13. Menu page: search, empty result, very long list, 50-track file.
14. Movie detail page → "Sync Subtitles" → single track; then all tracks.
15. Episode detail page → same two paths.
16. Series detail page → "Sync all episodes"; then "one season"; then a single episode from that page.
17. Menu page while a batch is running (does the UI stay truthful? does the queue accept more?).
18. History tab and Settings tab during and after runs; cancel a running batch from the UI.
19. The injected client script on a real item page (audit D14 was never verified in a browser).
20. Restart Jellyfin mid-batch: what happens to running/queued jobs on restart.

**C. Sources of subtitle text — every variation the extractor claims to handle**
21. MKV with many tracks (the 50-track episode is the reference case).
22. MKV whose cue index has block offsets for only part of a track (fixture:
    `tests/fixtures/patch_cues.py`) — audit D17 says this is lossy and unstable; prove or disprove.
23. MKV with no cue index at all (walk path) and with a truncated/garbage tail.
24. MP4/MOV embedded subtitles (`Mp4SubtitleExtractor`).
25. External sidecar `.srt` (copy mode and replace mode).
26. Forced/signs track, empty track, ASS/SSA track, bitmap track (PGS/VobSub) — must fail cleanly with a
    clear message, never silently produce an empty subtitle.
27. A file that has only one subtitle track (no usable second reference) → audio alignment path.
28. A file whose "reference" track is itself badly out of sync (the fallback-to-audio rule).

**D. Settings and filters — each alone, then in combination**
29. `SyncLanguages`: matching, non-matching, mixed case, unknown tag, empty list.
30. `ParallelWorkers`: 1, 2, 4, 8, 64; confirm the effective limit and that nothing is starved.
31. `MultiSyncMode`: auto vs normal vs ultimate on the same batch (time, reads, quality of result).
32. `MaxOffsetSeconds` / `MaxSubtitleSeconds`: normal, zero, negative, absurd — and what ffsubsync is
    actually invoked with (audit D3 says there is no range check).
33. `VadMethod` options, `UseGoldenSectionSearch`, `FixFramerate`: on/off on the same file, with numbers.
34. `ExtractionTimeoutMinutes`: a timeout that is too short for a slow file — clean failure?
35. `FfSubSyncPath` / `FfmpegPath`: valid, missing, non-executable; what the user is told.
36. `SyncModeCopy` copy vs replace: file outcome, Jellyfin visibility, recoverability.
37. Sweep limits (`SweepFailStreakLimit`, `SweepMaxItemsPerRun`): run the sweep task and watch it.
38. Cache behaviour: clear cache then re-run; restart Jellyfin then re-run; corrupt one cache entry.

**E. Failure paths — each must end in an honest, actionable state**
39. Read-only library folder / library not writable.
40. Disk full or staging on another filesystem.
41. ffsubsync missing / broken / wrong version; install path unavailable.
42. Job cancelled from the UI mid-extraction and mid-ffsubsync; then `Kill` (audit D1: it kills all).
43. Server restart, plugin update, or config change while jobs are queued.
44. A file Jellyfin cannot probe; a file that disappears between queue and run.
45. Two Jellyfin users acting at once (one queues, one cancels) — audit D15 says there is no authz at all.
46. **Slow storage profile:** measure on a share with real latency (the user's NAS is ~12,8 ms per 16 KB
    read). Every speed claim must be repeated under that profile, because that is where the product
    actually lives. Simulate if necessary (throttled loopback/FUSE) and say which you used.

## 4. Measurement rules

- Baseline first: before changing anything, run the matrix's core rows and record the numbers as they are.
- Per-run evidence is the plugin's own log lines (`extract lane:`, `extract: method=...`, `ffsubsync
  start/exit`, `job ... completed`), the `/SubSync/Jobs` records and the produced files.
- Speed claims are always **same file, same track, before vs after**, with the commands and numbers in the
  report. State where the time goes: locate, extract, reference, ffsubsync, write.
- Report the aggregate too: total wall-clock for a full 30-track episode and for a whole series, single
  mode vs bulk, and the effect of `ParallelWorkers`.
- Cue-count parity is a correctness gate: the extracted cue count must match the file's own count (check
  with `ffprobe`/the cue index), and the synced output must keep the same number of cues.

## 5. Deliverable, phase 1 (document, no code changes)

`knowledge/backend-test-report-<date>.md`, structured like the audit document (the user liked that shape):

1. **Coverage table** — every matrix row, result, evidence id.
2. **Findings** — symptom → exact repro (command/API call/clicks) → evidence → impact → severity →
   direction for a fix. Separate *broken* from *slow* from *confusing*.
3. **Speed profile** — per stage, single vs bulk, on fast local storage and on the slow-storage profile,
   with the bottleneck named and measured.
4. **Failure inventory** — every failure, and the state it left behind (clean library? other jobs alive?
   actionable message?).
5. **Settings/filters truth table** — what each setting actually does vs what the UI/README claims.
6. **Junk** — dead code, duplicated logic, unused settings, leftover files, log noise.
7. **What was not tested** and why, with the smallest step that would test it.
8. **Suggested patching order**, highest user impact first (the audit's section 9 is the starting point).

Commit it locally; do not push to a release branch.

## 6. Deliverable, phase 2 (optimise — only what phase 1 proved)

- Work the patching order. One finding per change, before/after numbers, one-line reason in the commit.
- Priority: (a) eliminate data loss and silent wrongness first (audit D16/D17 are the confirmed ones),
  (b) remove repeated work — re-extraction, re-reading a file for a second track, redundant reference
  extraction, work done twice in bulk, (c) shorten the slowest stage on slow storage, (d) make every
  status message true.
- Never trade correctness for speed: cue parity, verify-by-cue-count and the "never refuse a job" rule all
  stay. `python3 tests/run_checks.py` must stay green, and new checks must be added for each fixed defect.
- Anything released goes through the GitHub Actions workflow on `beta` and must be verified in the public
  manifest (zip HTTP 200, checksum and DLL contents) before you call it done.

## 7. Definition of done

- Every matrix row attempted, with evidence; blocked rows explained precisely.
- Report committed and complete; no claim without backing.
- The confirmed critical findings (D16 replace mode, D17 mixed cue index) are fixed and *verified* on the
  fixture that proves them today, plus the authorisation gaps (D15) if the user agrees on who may do what.
- A full-episode bulk run (30+ tracks) and a whole-series batch finish with **zero failures**, no orphaned
  scratch, no half-written subtitles, and a measured speed-up over the baseline on the slow-storage
  profile.
- A one-page summary: what was broken, what was slow, what changed, what is still open, and how long each
  test run took.

## 8. Practical notes

- The test server used for the audit is still available: Jellyfin 12 at `127.0.0.1:8096`, plugin installed
  from the released zip, the real 2.38 GB episode (50 subtitle tracks) plus small fixtures, and a Showa
  library with a series and two episodes.
- Fixture tools and harnesses from the audit live in `/opt/data/tmp/audit/` (`probe.py` for API calls,
  `gui.js`/`main-*.js` for driving the GUI, `browser-visual.js` for a real browser) and
  `tests/fixtures/patch_cues.py` in the repo.
- Start every session with `python3 tests/run_checks.py`, `GET /SubSync/InstallationStatus` and a look at
  the plugin log's first lines — they state the version, the binary in use and the worker setting.
- Keep the jobs' scratch directories until a finding is written; they are the only forensic record.
