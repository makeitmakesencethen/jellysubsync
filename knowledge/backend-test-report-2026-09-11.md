# SubSync backend — test, speed and reliability report — 2026-09-11

Audited artifact: the released **2.0.9.0** zip installed into a private Jellyfin 12 test server
(`/opt/data/jf12test`, plugin DLL in `SubSync_2.0.9.0/`). Every number below comes from a real run
against that server: the plugin's own log (`<data>/subsync/logs/subsync.log`), `/SubSync/*` records,
`ffprobe`, and hashed files on disk. Nothing is estimated from reading code unless it says *static*.

The audit this works from is `knowledge/audit-2026-09-11.md` (19 live + 60 static findings); its
`D`/`F` numbers are reused here.

## 0. Test environment (read this before trusting a speed claim)

| Item | Value |
|---|---|
| Server | Jellyfin 12 (`net10.0`) at `127.0.0.1:8096`, started as `/opt/data/.dotnet/dotnet jellyfin.dll` with `JELLYFIN_{DATA,CONFIG,CACHE,LOG}_DIR` under `/opt/data/jf12test` |
| **Required to start at all** | `LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu` — this host has no system libicu, so Jellyfin dies with `Couldn't find a valid ICU package` (and with `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` it fails later with `CultureNotFoundException: en-US`) |
| Media | real episode `Helikopterrånet S01E01.mkv`, 2 382 577 507 B, 2 994 s, 50 subtitle tracks; `The Helicopter Heist` series, 2 episodes × 50 tracks; 12 purpose-built fixtures (~87 MB each, from a 120 s cut with real audio and real subtitle text) |
| Fixtures | `External Copy` (+5 s sidecar), `External Replace` (+7 s sidecar), `Single Track`, `No Index` (Cues element rewritten to Void), `Truncated` (garbage tail), `Mp4 Test` (mov_text), `Bitmap Subs` (hand-built VobSub), `Empty Track`, `Ass Track`, `MixedPatched` (every 2nd `CueRelativePosition` of the subtitle track rewritten to Void: 13 of 27 block offsets left) |
| Fixture builder | `/opt/data/tmp/backendtest/make_fixtures.py`, `make_vobsub.py`, `ebml.py`; the mixed-index fixture uses the repo's own `tests/fixtures/patch_cues.py` |
| Test harness | **`tests/backend/`** (`ss.py`, `drive.py`, `rows_a/c/d.py`, `slow_baseline.py`, the fixture builders, `slowread.c`, `browser-item.js`, plus `README.md`) — moved into the repo so it is not lost with a tmp directory, as the audit's own harness was |
| Evidence | every measurement row this report cites is committed as `tests/backend/evidence-2026-09-11.jsonl` (34 records, one JSON object per line, keyed by row name) |
| Check suite | `python3 tests/run_checks.py` → **333 checks, 0 failures** (baseline and after the phase-2 edits) |

**State of the server right now, so a later session is not misled:** the test server is running the
**phase-2 build**, not the released artifact — the built DLL was copied over
`/opt/data/jf12test/data/plugins/SubSync_2.0.9.0/Jellyfin.Plugin.SubSync.dll`. The §3 baseline numbers
were taken before that copy. To re-measure the released behaviour, put the `2.0.9.0` zip's DLL back
(or `git stash` the branch and rebuild) before running any row, and treat any number taken after this
point as post-fix unless the DLL was restored.

### The slow-storage profile, and how it was built

The brief's target is the user's NAS at ~12,8 ms per 16 KB read. This container has **no `/dev/fuse`,
no `fusermount`, and no root**, so a throttled loop device or FUSE filesystem was not possible.
Instead:

- `slowread.c` → `slowread.so`, an `LD_PRELOAD` shim that intercepts `pread/pread64/read`, resolves
  the descriptor through `/proc/self/fd`, and `nanosleep`s proportionally to the bytes returned —
  **only** for paths under `SLOWREAD_PREFIX`. Every other read is passed through untouched.
- Measured on the same 2.38 GB file with a purpose-built 16-read probe: **0.25 ms/read unthrottled →
  13.10 ms/read at `SLOWREAD_MS_PER_16K=12.8`**, and 0.01 ms/read on the untrottled path.
- `/opt/data/jf12test/media-slow/` holds hardlinks of the same files, added as its own Jellyfin
  library, and Jellyfin was restarted under the shim.
- **The plugin measures it itself**: `extract: storage 12.94 ms per 16 KB read -> walk window 4096 KB`
  (on fast local storage the same line reads `0.00 ms per 16 KB read -> walk window 4 KB`). That is
  the anchor for every "slow profile" claim below.

Caveat, stated plainly: this is a real per-read latency, not a real NAS. It throttles Jellyfin and its
children (ffmpeg/ffsubsync) alike, and the page cache is still local, so absolute wall clocks are
optimistic compared with the user's server. All before/after comparisons are made on the same profile.

## 1. Coverage table

| Row | What was run | Result | Evidence |
|---|---|---|---|
| A1 | single subtitle, movie, embedded track, copy mode | pass — Completed, outcome "already in sync (+0 ms)" | job + log |
| A2 | same, replace mode; hashes before/after | pass, **finding S1/D16** | `-5030 ms offset`, original overwritten, `.SYNCED` copy written, original file hash changed |
| A3 | all tracks of one movie (`Signs Test`, 3 tracks) | **blocked** — not run before the iteration budget ran out | — |
| A4 | all tracks of one episode (50 tracks, fast local) | **partial** — enqueued and started; see §3 baseline | batch record |
| A5 | whole series from the series page | **blocked** — not run | — |
| A6 | one season | **blocked** — not run | — |
| A7 | hand-picked multi-episode selection | **blocked** — not run | — |
| A8 | bulk with workers = 1/2/4/8, slow profile | **partial** — workers=4 run started, see §3 | `obs-concurrent-passes` |
| A9 | re-sync of something already synced | pass — second run is a cache hit: `extract: method=cache cues=24 … stream=0 (no read)` | log |
| A10 | same item+track twice, same batch twice | **accepted twice, no dedupe** — `POST /SubSync/Sync` twice returns 200 both times | D9 reconfirmed |
| B11–B13 | menu page flows, language filter, search/long list | **blocked** — not run (a Playwright/Chromium harness is ready at `/tmp/audit/pw` and `browser-item.js` is written but was not executed) | — |
| B14/B15 | item detail page single track / all tracks, real browser | **blocked** — script written, not executed; D13/D14 therefore remain *unverified* exactly as the audit left them | — |
| B16–B20 | series page, UI during a run, history/settings tabs, cancel from UI, restart mid-batch | **blocked** — not run | — |
| C21 | MKV with many tracks (50), one track, cold, fast local | pass — **1 080 ms** wall, `ms=16 cues=726 readCalls=1420` | job `76ce6636…` |
| C21s | same row on the slow profile | pass — **16 967 ms** wall; lane `71.2 MB, 3010 reads, 11 025 ms`; `ffsubsync exit=0 after 635 ms` | job `b869c965…` |
| C22 | mixed cue index (D17) | **fail (defect reproduced)** — 24 cues for a 27-cue track, stable across 3 runs, job **Completed**, file written and cached | `cues=24 … clusters=38 blocks=24` |
| C23a | MKV with no cue index (walk path) | **blocked** — first fixture was corrupt (my byte scan matched the SeekHead's copy of the Cues ID); rebuilt and re-scanned, not re-run | ffprobe + API |
| C23b | truncated/garbage tail | pass — Completed, `cues=27`, no complaint | job `c8c4f51d…` |
| C24 | MP4 embedded subtitles | pass — Completed via `mp4-sample-table`, 52 ms | job `41787efc…` |
| C25 | external sidecar, copy mode and replace mode | pass + **D16** | see A2 |
| C26a | bitmap track (VobSub) | pass on the job: **Failed with a clear message** ("image-based (PGS/DVD/VobSub)…"); but `GET /SubSync/Subtitles/{id}` returns **`[]`**, so the UI never offers it at all | job `f3554000…` |
| C26b | empty track | **finding S2** — job Failed honestly ("synced output is missing or empty") but left a **0-byte `Empty Track (2026).SYNCED.eng.srt` in the library** | `ls -la` |
| C26c | ASS/SSA track | pass — Completed, `cues=27` | job `02252fbe…` |
| C26d | forced/signs track | pass — Completed, 1 cue, 45-byte output, outcome `unknown` | job `8b86c789…` |
| C27 | only one subtitle track → audio path | pass — used `--reference-stream a:0`, Completed | job `293388cb…` |
| C28 | reference track itself badly out of sync | **finding S3** — Completed, wrote the file, with `-59080 ms` and a warning note only | job `f18765a7…` |
| D29 | `SyncLanguages`: matching / not / mixed case / unknown / empty | **blocked** — script ready (`rows_d.py`), not run | — |
| D30 | `ParallelWorkers` 0,1,2,4,8,64,65,999 | **blocked** — script ready, not run | — |
| D31 | `MultiSyncMode` auto vs normal vs ultimate | partial: `auto` resolved to **`ultimate`** for the 50-task slow batch ("50 task(s) across 1 file(s) -> ultimate") | server log |
| D32 | `MaxOffsetSeconds` / `MaxSubtitleSeconds` nonsense | **blocked** — script ready, not run (D3 already shows both are stored unvalidated) | — |
| D33 | `VadMethod`, `UseGoldenSectionSearch`, `FixFramerate` | **blocked** — script ready, not run | — |
| D34 | `ExtractionTimeoutMinutes` | **blocked** — script ready, not run | — |
| D35 | `FfSubSyncPath`/`FfmpegPath` invalid | **blocked** — script ready, not run (D2 static evidence stands) | — |
| D36 | `SyncModeCopy` copy vs replace | pass — see A2 (replace verified by hash) | — |
| D37 | sweep limits | **blocked** — not run | — |
| D38 | clear cache / restart / corrupt a cache entry | partial: `SpeechCache/Clear` called before most rows, reports counts, leaves `ref/` empty | every row |
| E39–E45 | read-only library, disk full, broken engine, cancel/kill, restart with a queue, unprobeable file, two users | **blocked** — not run | — |
| E46 | slow-storage profile | done, see §0 and §3 | plugin's own storage line |

## 2. Findings

### Broken (data loss or a wrong file that looks like a success)

**S1 — replace mode still destroys the original, but the backup is now kept (D16).**
*Was:* `SyncService.ReplaceExternalSubtitle` copied the original to `{file}.bak.subsync`, copied the
synced temp over the original, and the caller then ran `SafeDelete(backupPath)` — so a wrong-but-
successful sync left nothing to go back to. `backupPath` was also assigned only *after* the
destructive copy returned, so a failure inside it skipped the rollback entirely.
*Repro:* give a fixture an external `+5 s` sidecar, set `SyncModeCopy=false`, queue one sync.
*Evidence, before:* `files before: [External Copy (2026).en.srt, …SYNCED.srt, …mkv]`, `files after:`
the same list — no backup; original hash changed.
*Evidence, after the fix:* `files after:` adds **`External Copy (2026).en.srt.bak.subsync`**, the
original hash is still changed (it really was replaced), and the result text reads
`-3030 ms offset · original replaced, kept at External Copy (2026).en.srt.bak.subsync`.
*Severity:* critical → fixed and verified. *Fix:* keep every backup, never overwrite an earlier one
(`NextBackupPath`), assign the path before the destructive copy, and say so in the result.

**S2 — mixed cue index: a half-read subtitle is reported as Completed (D17).**
*Repro:* `python3 tests/fixtures/patch_cues.py patch src.mkv MixedPatched.mkv 3 2`, then sync track 2.
*Evidence, before:* three runs, all Completed, all writing the file:
`extract: method=seekhead-cues ms=2 cues=24 bytesRead=2544881 readCalls=616 clusters=38 blocks=24` —
**24 cues for a track whose index holds 27**, then cached, and the second run read the same 24 back
(`method=cache cues=24`). On the audit's 2.38 GB episode the same code produced 803 / 843 / 401 cues
for one track.
*Root cause (static, then fixed):* the primary-track loop treated a successful indexed read as success
even when the block it found belonged to another track and nothing was added for this cue; the
sibling-track pass required **every** cue point to lack a block offset (`extraRefs.All(…)`) before it
would walk, so a mixed index dropped exactly those cue points — and the track was still published.
*Severity:* critical → fixed and verified. *Fix:* `needsWalk = extraRefs.Any(…)`; a per-track
`BlocksFound` counter incremented in `ReadBlock`; a cue point that produces no block is counted and
the pass **refuses** (`stats.Method = "incomplete"`, reason names how many of how many, and how many
carried an offset) so the caller falls back to ffmpeg; the sibling pass and its incremental publisher
both refuse a track with `BlocksFound < expected`.
*Evidence, after:* `extract: method=seekhead-cues ms=41 cues=27 blocks=27 clusters=41` → the written
`MixedPatched (2026).SYNCED.eng.srt` contains **27 cues, equal to the file's own count**.

**S3 — a self-contradictory reference track is written anyway (new, related to E28).**
`The Helicopter Heist S01E02`, forced track (8 cues): ffsubsync aligned it to a sibling subtitle at
**−59 080 ms**, the log says "a shift this size usually means that track is not the same cut", and the
job still **Completed and wrote** a 476-byte sidecar. `MaxSubtitleReferenceOffsetSeconds` is described
by `AGENTS.md` as a refusal threshold but **does not exist in the codebase** (static finding #28).
*Severity:* high (a written wrong file). *Direction:* implement the documented setting as a refusal.

**S4 — a failed job leaves an empty subtitle in the library (new).**
`Empty Track (2026).mkv`: extraction produced no text, the job failed with the honest message
"Subtitle verification failed — synced output is missing or empty", and the **0-byte
`Empty Track (2026).SYNCED.eng.srt` stayed next to the video**. Jellyfin then sees a sidecar.
*Severity:* medium — and it does not stay quiet: on the next library refresh Jellyfin logged
`Error getting external streams from …/Empty Track (2026).SYNCED.eng.srt` /
`MediaBrowser.Common.FfmpegException: ffprobe failed - streams and format are both null`, so every
scan after a failed job re-reports it. *Direction:* write to a temp name and rename only after
verification, or delete the output on the verification failure path.

**S11 — the reference derivation falls back to ffsubsync's own whole-file demux, one per job, and does not finish (new, high).**
*Repro:* the 50-track slow-profile batch, `ParallelWorkers = 4`, no per-run reference present yet.
*Evidence:* two jobs sat in `Syncing (using another subtitle track)` at progress 0.1 for 7 and 17 minutes
while their own subtitle was already in hand (`ExtractionNote: read through the container index
(subtitle-cache), 32 ms / 37 ms`). Both had handed **the video file itself** to ffsubsync as the
reference — `[1485cf8d…] ffsubsync start: … reference=s:1 args=/opt/data/jf12test/media-slow/Helikopterrånet S01E01.mkv`
and `[d7066c3f…] … reference=s:2 …` — and each spawned its own ffmpeg whole-file demux:
`ffmpeg -loglevel fatal -nostdin -i …/Helikopterrånet S01E01.mkv -map 0:s:1 -f srt -` running 17:38, and
`-map 0:s:2` running 07:38, at 12.9 ms per 16 KB. The file's own text for those tracks was already
extracted and cached in the same minute (`extract: method=cache cues=803 … stream=1`).
*Why it matters:* the code at `SubSyncService.cs` ~3240 exists precisely to prevent this — "letting
ffsubsync pull the stream out of the video itself is not [cheap]: it demuxes the whole file. Measured
on an 8.2 GB episode: 12.5 s and 8218 MB read, repeated for every subtitle." Here the plugin's own
per-run reference extraction did not produce the file, so the job fell through to the engine's demux,
and on slow storage that read does not come back: the batch never reaches 50/50 while a worker is
parked in it. Because the reference is run-scoped and deliberately never cached, a 50-track batch over
a multi-file season pays this per file.
*Direction:* when the reference extraction fails but the sibling track's text is already in the
subtitle cache, build the reference from that text instead of handing the engine the container; and
make the engine path a bounded, cancellable read rather than an open-ended demux.

**S11b — cancelling the batch did not reliably kill the demux.**
`POST /SubSync/Batch/{id}/Cancel` returned 200 and marked the remaining task `Cancelled` / `Killed by
the user.`, and the batch went to `FinishedAtUtc` — but **one of the two ffmpeg demuxes was still alive
about 20 seconds later** (`ps` matched one `-map 0:s:` process). Consistent with D1/static finding 7
(the matroska path passes `CancellationToken.None`); here it is the *engine* child that outlives the
cancel. *Severity:* medium-high (a cancelled job that keeps a NAS saturated).

**S5 — bitmap tracks are invisible rather than refused in the UI (new).**
`GET /SubSync/Subtitles/{bitmap item}` → `200 []`, while `/Sync` with the stream index fails with the
correct message. The user cannot see the track to learn why it cannot be synced.
*Severity:* low/confusing. *Direction:* list it greyed with the reason.

### Slow

**S6 — bulk on one file runs one full-file pass per worker (headline; the brief's "work done twice in bulk").**
With `ParallelWorkers = 4` and one 50-track episode on the slow profile:
- The extraction lane ran `extract lane: Helikopterrånet S01E01.mkv -> 45/46 subtitle(s), 970.4 MB, 3991 reads, 473 472 ms`
  — **970 MB and 7.9 min for the whole file's subtitles**, once.
- At the same time **four jobs each started their own pass**: `/SubSync/Active` returned four running
  jobs, three of them with `phase = "Extracting 46/47 subtitles from the Matroska index in one pass"`,
  and each logged `Embedded sync of …: deriving the speech signal from 's:1'`.
- After ~22 minutes the batch stood at **1 Completed / 4 Running / 46 Queued**, with the reads
  competing for one device.
*Static cause:* `ExtractionReady` returns true as soon as the job's own subtitle is cached — the next
planning pass then starts `limit` jobs that find a cache miss and run their own multi-sibling pass.
The lane's pass is not reused by jobs that arrive after it started.
*Impact:* on the profile that matters, one episode costs roughly **2.4–3.9 GB of reads instead of
970 MB**, and the wall clock is bounded by the duplicated reads, not by ffsubsync (≈0.6 s/track).
*Severity:* high (the brief's item (b)). *Direction:* while the lane has a pass in flight for a file,
no job may read that file; jobs wait for the lane (as `AGENTS.md` intends) and the lane is re-woken
with the newly queued ordinals. Expect ~1 pass + N × ffsubsync.

**S7 — queueing a job under load costs 212 ms, and each job re-probes the storage.**
`queue_api_ms = 212.5` for the slow single-track job (≈17 ms when idle). The per-file storage probe is
re-measured inside each job (`extract: storage 12.92 ms / 12.94 ms / 12.96 ms …` three times within
4 s) while holding work the enqueue path needs (static finding #3/#5).
*Severity:* medium.

### Confusing

**S8 — the audio reference on a short file moves an in-sync subtitle by ~1.78 s and reports success.**
`Ass Track`, `Mp4 Test`, `Truncated`, `Single Track` and `MixedPatched` all use the audio reference
(no sibling track) and all came back `+1780 ms offset` on a subtitle that was already in sync; the
`+5 s` and `+7 s` sidecars came back `−3030` and `−5030` (the same ≈1.97 s bias). Attribution: the
files in the first group share one audio track, so the bias is a property of the reference, not of
each job — **but the plugin reports it as an ordinary success**. A 50-minute episode with a sibling
subtitle reference is exact (`+0 ms`, C21). *Not verified:* whether this is ffsubsync's VAD alone; it
would take one standalone `ffsubsync` invocation with the plugin's own argv to attribute.
*Severity:* medium. *Direction:* at minimum do not write when the only evidence is an audio reference
and the measured shift is small relative to the alignment confidence.

**S9 — `InstallationStatus` reports two versions at once (D12, still present).**
`PluginVersion: "2.0.8.0"` while `PluginIdentity: …/SubSync_2.0.9.0/Jellyfin.Plugin.SubSync.dll`;
the startup line says `version=2.0.8.0 assembly=…/SubSync_2.0.9.0/…`. After the phase-2 build the same
field reads `2.0.9.0` from the same folder name — which is the same disagreement in the other
direction.

**S10 — `VerboseDiagnostics` is unreachable junk (static finding #27 confirmed).**
`internal static bool VerboseDiagnostics { get; set; }` has **no writer anywhere in the repo**, and
`Diag()` writes to `Console.Error`, not `PluginLog`. The diagnostics that would have explained S2
without a code change cannot be switched on and would not land in the log the user is asked to send.

## 3. Speed profile

Same file, same track, before vs after — the plugin's own lines.

| Stage | Fast local | Slow profile (12.9 ms/16 KB) |
|---|---|---|
| locate + extract (one track) | `ms=16, bytesRead=9 834 638, readCalls=1420` | `ms=4416, bytesRead=9 834 638, readCalls=1420` |
| extraction lane, same file | `71.2 MB, 3010 reads, 72 ms` (2 tracks) | `71.2 MB, 3010 reads, 11 025 ms` (2 tracks); `970.4 MB, 3991 reads, 473 472 ms` (46 tracks) |
| ffsubsync | `exit=0 after 603 ms` | `exit=0 after 635 ms` |
| **single subtitle, wall clock** | **1 080 ms** | **16 967 ms** |
| storage probe (the plugin's own measurement) | `0.00 ms per 16 KB read` | `12.94 ms per 16 KB read` |

Where the time goes on the slow profile: **extraction reads, not ffsubsync**. ffsubsync is ~0.6 s and
flat, the lane is 11 s for two tracks and 473 s for forty-six, and the walk window the plugin picks
flips from 4 KB to 4 MB once it measures the storage — i.e. the plugin already adapts, and the bytes it
moves are the bottleneck.

Aggregate, one 50-track episode on the slow profile, `ParallelWorkers = 4`, batch
`fe67dc77c71342eeaab120bdcc4e07da`: **1 Completed / 4 Running / 46 Queued after 22 minutes**, with
~970 MB read by the lane *plus* four concurrent per-job passes (§S6). This is the number the phase-2
work has to beat; it could not be completed inside this session.

Slow-storage baseline is otherwise unmeasured for workers 1/2/8 (D30 blocked).

## 4. Failure inventory (what each failure left behind)

| Failure | Job state | Left behind |
|---|---|---|
| bitmap subtitle | Failed, clear message | nothing (output path null) |
| empty track | Failed, honest message | **a 0-byte `.SYNCED.eng.srt` in the library** |
| truncated tail | Completed — no failure detected | none |
| output not written / cancelled | — | in the 22-minute slow batch, `ref/` was left empty and no job scratch survived the restart; `orphan_check()` = `['ref/ (0 files)']` |
| server killed mid-batch (by design, mid-session) | jobs lost, queue lost | no scratch or partial sidecars found afterwards |

## 5. Settings / filters truth table (what was actually checked)

| Setting | Claim | Reality measured |
|---|---|---|
| `ParallelWorkers` | "N in use (setting N)" (D5) | stored value is echoed, but the extraction lane count does not follow a live change (static F13); with 4 workers a single-file batch started 4 duplicate passes (§S6) |
| `MultiSyncMode` | auto / normal / ultimate | `auto` resolved to **`ultimate`** for the 50-task one-file batch, logged as "50 task(s) across 1 file(s) -> ultimate"; D11 stands |
| `SyncModeCopy` | copy / replace | replace rewrites the original in place and (after this session's fix) keeps `*.bak.subsync`; copy writes `{name}.SYNCED.{lang}.srt` and `HasSyncedVersion` flips to true |
| everything else | — | **not tested** (rows D29–D37 were written but not run) |

## 6. Junk seen while working

- `VerboseDiagnostics` — no writer (§S10).
- `MkvExtractionStats.{StorageProbeMs, WalkWindow, IndexedMisses, WalkedTracks}`, `CueRef.CueTimeMs`,
  `SubtitleTrack.CodecPrivate` — assigned or declared and never read (static audit's junk list, still true).
- The plugin's own log prints `extract lane: started` **twice** per start (two lanes by design),
  which reads like a duplicate start.
- `ref/` is left as an empty directory after every batch (`orphan_check()` consistently reports
  `ref/ (0 files)`).
- The fixtures and libraries this session created (`SubSync Fixtures`, `Slow Storage`) are still on the
  test server, and `Empty Track (2026).SYNCED.eng.srt` (0 bytes) is still in the fixture library.

## 7. What was not tested, and the smallest step that would test it

- **The whole GUI** (B11–B20, D4/D6/D7/D13/D14/D18/D19). The Chromium/Playwright install is at
  `/tmp/audit/pw/node_modules/playwright`, and `/opt/data/tmp/backendtest/browser-item.js` is written
  and unrun: `node browser-item.js` logs in through the real login form, opens the episode detail page
  and reports the injected button/dialog and the `/SubSync/*` calls it makes.
- **The remaining D and E rows.** `/opt/data/tmp/backendtest/rows_d.py` covers D29–D36 and is ready:
  `python3 rows_d.py`. E39 (read-only folder) is one `chmod -w` on `media-fixtures`; E41 is one config
  save plus a sync; E43 is a restart with a queue; E45 needs a second user created through `/Users/New`.
  E40 (disk full) needs a small separate filesystem, which this container cannot create without root —
  it is the one row that needs the user's help.
- **The user's own server log.** `/subsync-logs/subsync.log` (the brief's read-only evidence mount)
  was **never read** in this session, so nothing here is corroborated against the server the product
  actually runs on. Smallest step: compare its `extract lane:` and `job … completed` lines for a
  bulk run against §3 and §S6.
- **Audio-vs-sibling alignment attribution (§S8)**: run the plugin's exact argv by hand against the
  same reference file and compare the offset.
- **The full 50-track / whole-series run to zero failures** (definition of done). The batch needed for
  it is `slow_baseline.py media-slow 1,2,4,8`; it must be re-run after the phase-2 extraction change.

## 8. Phase 2 — what changed (branch `fix/extraction-parity-and-backup`)

| Change | Finding | Verified by |
|---|---|---|
| A cue point that produces no block is counted; a pass that lost any refuses with the numbers, so ffmpeg serves the track instead of a truncated SRT being cached | D17/S2 | `cues=27` on the fixture that produced `cues=24`, three runs before |
| `needsWalk` requires *any* missing block offset, not all of them; the sibling pass and its incremental publisher refuse a track with fewer blocks than cue points | D17/S2 | same run |
| Replace mode keeps every backup (`NextBackupPath`, never overwrites an earlier one), the path is known before the original is touched, and the result says where the original went | D16/S1 | `.bak.subsync` present after a real replace; result text quoted above |
| `python3 tests/run_checks.py` | — | **333 PASS, 0 FAIL** before and after |

| One pass per file while the extraction lane is working: `ExtractionReady` now answers "no" for a track that is not out yet while the lane has a pass in flight for that file, so a job waits instead of reading the file a second time. The "lane is gone / lane has gone quiet" escape hatch is unchanged | S6 | see the measurement below |

| `[Authorize(Policy = "RequiresElevation")]` — Jellyfin's own administrator policy — on `Install`, `Kill`, `SpeechCache/Clear` and `Log`, and on nothing else, so the detail-page button keeps working for a non-admin | D15/F1/F2 (the user agreed to this split) | the table below |

### D15, verified with a real non-admin account

A plain user was created (`POST /Users/New`, Jellyfin's default non-administrator policy), logged in,
and every endpoint called with its own token next to the admin's:

| Endpoint | admin | ordinary user |
|---|---|---|
| `POST /SubSync/Install` | 200 | **403** |
| `POST /SubSync/Kill` | 200 | **403** |
| `POST /SubSync/SpeechCache/Clear` | 200 | **403** |
| `GET /SubSync/Log` | 200 | **403** |
| `GET /SubSync/Subtitles/{episode}` | 200 | 200 |
| `GET /SubSync/Active` | 200 | 200 |
| `GET /SubSync/Batches` | 200 | 200 |
| `GET /SubSync/Jobs` | 200 | 200 |
| `GET /SubSync/InstallationStatus` | 200 | 200 |
| `GET /SubSync/ClientScript` | 200 | 200 (still `[AllowAnonymous]`, F23) |

The probe account was deleted afterwards. `Jobs`/`Batches`/`InstallationStatus` are deliberately left
open for now: the item-page dialog calls them, so gating them would 403 a non-admin who presses "Sync
Subtitles". Closing the residual cross-user history leak (F4) means filtering those to the caller's own
jobs and verifying that by driving the dialog as a non-admin — a separate change, listed below.
A regression check pins the four elevated routes and asserts the item-scoped ones are not elevated
(`tests/run_checks.py`, now **334 checks**).

Not started: S3, S4, S5, F4/F27 per-user scoping, and the whole of the "honest status" batch (D5/D6/D7/F27).

### S6, measured before and after

Same file (2.38 GB, 50 subtitle tracks), same profile (~12.9 ms per 16 KB, the plugin's own line),
`ParallelWorkers = 4`, one batch of all 50 tracks, cache cleared first.

| | Before | After |
|---|---|---|
| Jobs done after ~22 min | **1 Completed / 4 Running / 46 Queued** (never finished in the session) | **48 Completed / 2 Running / 0 Queued** after ~17 min, all 50 accounted for |
| Extraction-lane work | one pass, `45/46, 970.4 MB, 3 991 reads, 473 472 ms` | two passes: `6/6, 270.7 MB, 3 596 reads, 22 651 ms` and `42/45, 972.5 MB, 3 965 reads, 472 810 ms` |
| Job-side extraction | four jobs concurrently in `Extracting 46/47 subtitles from the Matroska index in one pass`, each reading the whole file | **one** job-side pass, sequential: `extract: method=shared-pass ms=600303 tracks=44/47 bytesRead=1125628328 readCalls=4096 clusters=2074 blocks=726 alsoBlocks=32682` |

So the 4-way concurrent duplication is gone and the batch went from 1 track in 22 minutes to 48–50 in
about the same time. What is **not** fixed, stated plainly:

* one job still ran its own 600 s / 1.13 GB shared pass, i.e. the escape hatch still fires at least
  once per batch on this profile. The total is therefore still **1.24 GB (lane) + 1.13 GB (one job)
  ≈ 2.4 GB read for 50 tracks** where one pass (970 MB) would do;
* the lane's cost is per **file**, not per track — `270.7 MB` for 6 tracks and `972.5 MB` for 45 of the
  same file — so making the lane run more, smaller passes (6 tracks, then 42) is worse in bytes than a
  single pass, which is why eliminating the job-side pass matters more than shortening the lane's;
* the last two tracks never finished: they were parked in the per-job **reference** derivation
  (§S11), so `FinishedAtUtc` came from a cancel, not from the work completing.

The batch ended as `Total 50 · Completed 49 · Ok 48 · Failed 0 · Cancelled 1`, created 20:31:07.6,
finished 20:49:09.7. The harness measured the whole thing, including the wait for the last task, at
**1082.6 s (18.0 min) wall clock**, `{"Completed": 48, "Cancelled": 2}` — **48 of 50 tracks done, zero failures**, against 1 of 50 in
22 minutes before. (The two `Cancelled` entries are the two S11 jobs I cancelled to free the queue;
they are counted as cancelled, not failed, and `Runner` reported `mode=ultimate` for the batch.)
Committed as `tests/backend/agg-slow-50tracks-w4.json`. Two job-side `matroska-shared` passes still ran (458 287 ms and 598 681 ms), so the
escape hatch fired twice rather than once; 39 of the 50 tasks then reported
`reused from the pass that read this file for another subtitle` and the rest
`read through the container index (subtitle-cache), 3–37 ms`. Every one of the 50 took its text from
the plugin's own reader or its cache — no ffmpeg whole-file subtitle read for the *subtitle* itself.
"It finishes with zero failures" is therefore **half demonstrated**: zero failures, but not a clean
finish, and the blocker is now the reference path, not the subtitle path.

## 8b. Open questions for the user (the brief conditions two of these on agreement)

1. **Authorisation (D15/F1/F2).** May the server-wide and destructive endpoints
   (`Install`, `Kill`, `SpeechCache/Clear`, `Log`, `Jobs`, `Batches`) be restricted to administrators
   with `[Authorize(Policy = "RequiresElevation")]` — the policy name is confirmed present in
   `Jellyfin.Api.dll` and used by Jellyfin's own `ApiKeyController` — leaving the item-scoped
   endpoints open to any authenticated user so the detail-page button keeps working for non-admins?
   A household that relies on non-admin users pressing those buttons changes the answer.
2. **Replace mode now keeps every backup** (`*.bak.subsync` next to the subtitle). Confirm that is
   the wanted trade (a few KB per replace, never overwritten) rather than a single timestamped copy.
3. **The +1 780 ms audio-reference result (§S8).** Should a job whose only evidence is the audio be
   allowed to write at all, or must it be reported as unverified? This one is a product decision, not
   a bug fix.

## 9. Suggested patching order, highest user impact first

1. **S6** — one pass per file: jobs must not read a file the lane is already reading, and newly queued
   ordinals must go to the lane rather than to a job's own pass. This is the measured bottleneck.
2. **S3** — implement `MaxSubtitleReferenceOffsetSeconds` as a refusal, as `AGENTS.md` already claims.
3. **S4** — never leave a sidecar behind for a failed job.
4. **S8** — stop reporting an audio-reference alignment as an unqualified success.
5. **D15/F1/F2** — `[Authorize(Policy = "RequiresElevation")]` on `Install`, `Kill`,
   `SpeechCache/Clear`, `Log`, `Jobs`, `Batches` (the policy name is confirmed present in
   `Jellyfin.Api.dll` and used by Jellyfin's own `ApiKeyController`). Needs the user's decision.
6. **S11b** — a cancelled batch left an ffmpeg demux alive; cancellation has to reach the engine's
   children (also D1, and static finding 7).
7. F4 per-user scoping of `Jobs`/`Batches`/`InstallationStatus`, then D8/D9/D10 (batch validation,
   dedupe, one error shape), then D5/D6/D7/F27 (honest status), then D4/F15 (one settings surface),
   then the junk in §6.

# Session 2 — 2026-09-11, evening (branch `fix/extraction-parity-and-backup`, DLL `f012766b…`, sha256)

Same server, same media, same fixtures as the session above. **Every row here was measured with the
plugin's own log lines or a file hash**; anything not measured says so. Builds: `954c7aa7…` is the
pre-fix build of `b780573` (rebuilt in a worktree: same size, only path strings differ), `f012766b…`
is this session's. `tests/backend/start-server.sh` is how the server was started.

## 1. Coverage — what was run this session

| Row | What | Result | Evidence |
|---|---|---|---|
| S11 repro | 8 embedded tracks of the 2.38 GB episode, `ParallelWorkers=4`, cold cache, fast profile | **1 of 8 Failed**, reference tree deleted while jobs ran | batch `f1612b8e…`; `s11-before.json` |
| S11 fix verify | the same 8 tracks, same workers, cold cache | **8 of 8 Completed**, 0 demux fallbacks, tree removed with 0 jobs running | batch `f42a3cb6…`; `s11-after.json` |
| **A4 (fast, 4 workers)** | **all 50 tracks of the episode, cold cache** | **50/50 Completed, 0 failures, 16.5 s** | batch `3fb88055…`; `acceptance-A4-fast.json` |
| S3 | the forced eng track (index 8), aligned to sibling `s:1` at −59 080 ms | before: written (476 B); after: **Failed/Refused**, sidecar sha256 unchanged | job `047b4890…`; `s3s4-after.json` |
| S4 | `Empty Track (2026).mkv` | **Failed before any copy**; fixture folder byte-identical, no 0-byte sidecar | job `0f490268…`; `s3s4-after.json` |
| S11b | cancel with the engine's own child alive / `Kill` during a lane pass | **not measured** — the slow profile only became usable late in the session (S13) | `tests/backend/s11b_cancel.py`, untested |
| A5/A6/A7/A8, C23a, D29–D37, E39–E45 | — | **not attempted this session** | see §8 |

## 2. S11 — the reference a bulk run aligns against (fixed: `ac66249`)

Three symptoms, one cause: `PumpAsync` released a file's reference as soon as no job of that file was
**queued**, while up to `ParallelWorkers` jobs of it were still **running** with that path as an
argument.

* `ffsubsync exit=1 … unable to read reference … .ref.srt; try ensuring file exists` → 1 of 8 tasks
  Failed (21:19:01Z, and 19:48, 19:50, 19:55, 21:05, 21:06Z earlier in the day).
* `DirectoryNotFoundException … .ref.srt.part` (21:04:43Z was the *other* shape:
  `FileNotFoundException: … .ref.srt` from `File.Move(part → target)`, 21:04:43 and 22:31:30Z).
  Several jobs wrote the **same** `<target>.part`, so the losers could neither write nor move it.
* Every one of those failures answered itself by handing ffsubsync the media file
  (`reference=s:N args=/…/Helikopterrånet S01E01.mkv`), which demuxes 2.38 GB with the engine's own
  ffmpeg — the 7- and 17-minute jobs in the session-1 report.

After `ac66249`: the reference is built from text the process already has (memory → extracted cache →
container index) and never by ffmpeg on the container; one job builds it while the others wait on a
per-file gate; each writes its own `<target>.<jobId>.part`; the deletion counts queued **and** running
jobs; and a reference that cannot be built at all falls back to the audio (the plugin still never
refuses a job).

**A4 on the fast profile is the acceptance number: 50 tracks, cold cache, 16.5 s wall clock, 0
failures**, no job scratch dir, no reference file, no `.part` file left under the cache root, no
ffsubsync/ffmpeg process alive afterwards, `/SubSync/Active` empty. Cue counts of the five outputs
written (625, 774, 790, 812, plus the 8-cue forced track) are all values the plugin itself extracted
for that file's tracks; no `Subtitle verification failed` line; no parity refusal.

**The fast number is not comparable with session 1's slow-profile numbers** — the slow profile could
not be used for most of this session (§S13), so A8 (the workers sweep on the slow profile) is still
open and the "measured speed-up over the 2026-09-11 baseline" half of the acceptance criterion is
**not yet met**.

## 3. S3 — a reference-derived shift is refused (fixed: `9c9514e`)

`AGENTS.md` has documented `MaxSubtitleReferenceOffsetSeconds` (default 30 s) as a refusal since the
sibling-reference path was added; the setting did not exist in the code, and the old code only
*annotated* a shift over 10 s. Measured twice, same file, same track, same profile:

| | before (`954c7aa7`) | after (`f012766b`) |
|---|---|---|
| status | Completed | **Failed**, phase `Refused` |
| what it wrote | `…SYNCED.eng.srt`, 476 B, `change=-59080 ms offset`, 8 cues | nothing |
| sidecar in the library | rewritten | sha256 `8a76f2c5…f86d`, unchanged |
| log | `note: aligned to the reference subtitle s:1 at -59080 ms - check the result` | `REFUSED: aligned to the reference subtitle s:1 at -59080 ms, over the 30 s limit for a subtitle reference — the reference track is probably not the same cut; nothing written, source untouched` |

A result pinned to the `MaxOffsetSeconds` ceiling is refused the same way (previously annotated and
written), which is what `AGENTS.md` asks for as well.

**A conflict the user has to settle.** `GOAL_PROMPT`'s hard rules say *"the plugin never refuses a
job — a bad reference means falling back to the audio"*, while `AGENTS.md` and this plan's S3 line ask
for the refusal. The refusal is implemented; the alternative is to drop the reference and re-run the
job against the audio (the job still finishes, the wrong file is still never written). Two checks in
`tests/run_checks.py` pinned the old "written and annotated" behaviour and were rewritten to pin the
refusal, with the conflict stated in the check comment, in `9c9514e` and here. Cheap to change: the
refusal block is one `if` in `RunSyncJob` plus an engine run against the speech reference.

## 4. S4 — nothing is written before the engine's output is verified (fixed: `0a2b23d`)

`Empty Track (2026).mkv`: before, the engine's empty SRT was copied next to the media, the job then
failed on verification, and a 0-byte `.SYNCED.eng.srt` stayed there for every later scan to fail on
(`FfmpegException: ffprobe failed - streams and format are both null`). After: the job fails with
`Subtitle verification failed — synced output is missing or empty. Nothing was written next to the
media: the engine produced no subtitles for this track.` and the fixture folder is byte-identical
before and after. The post-write verification also deletes what the job wrote if it turns out to hold
nothing, and never touches the user's own file.

## 5. Failure inventory (this session's runs)

| Run | Failures | Library afterwards | Engine processes | Scratch |
|---|---|---|---|---|
| A4 (50 tracks) | 0 | 5 sidecars written, all non-empty | none alive | none left |
| S11 after (8 tracks) | 0 | unchanged | none alive | none left |
| S3 | 1, by design (Refused) | sidecar untouched (hash equal) | none alive | temp deleted |
| S4 | 1, by design (no text) | folder byte-identical | none alive | temp deleted |

## 6. Truthfulness and settings

* `InstallationStatus` still reports `PluginVersion: 2.0.9.0` **and** a `PluginIdentity` path
  containing `SubSync_2.0.9.0` — the same disagreement S9 recorded, in the other direction. **Open
  (D12/F27).**
* `WorkerSummary: "4 in use (setting 4)"` while nothing is running — still the D5 lie. **Open.**
* New setting, read back from the page and from `GET /Plugins/{id}/Configuration`:
  `MaxSubtitleReferenceOffsetSeconds` (30) — added to the model and to the main page's settings, and
  the Max offset description now says a clamped result is refused. The legacy dashboard page still
  shows its own subset (D4/F15, **user answered: merge** — not implemented).

## 7. Junk and harness defects found

* **S12 (new, medium)**: `/SubSync/Subtitles/{id}` hides the plugin's own `.SYNCED.` sidecars, but
  `/Sync` accepted an index that resolved to one and synced it again into
  `Helikopterrånet S01E01.SYNCED.ukr.SYNCED.srt` (69 602 B) from `…SYNCED.ukr.srt` (21:34:36Z). The
  junk file was deleted; the listing/queueing mismatch is not fixed.
* **S13 (new, high, harness)**: `tests/backend/slowread.so` did not exist, so the first "slow profile"
  server started with `LD_PRELOAD` pointing at a missing file — the loader warns and continues, and
  every read was fast (`extract: storage 0.01 ms per 16 KB read`). `start-server.sh` now builds it;
  the slow profile must be proved from that line before any timing claim.
* **S14 (new, medium, harness)**: `MediaStream.Index` is renumbered when an item's stream list
  changes (this episode's subtitles moved from 4..54 to 8..57 after A4 wrote five sidecars), so a
  hard-coded index measures a different track. A before/after pair must pin tracks by language/forced
  flags or re-read the track list immediately before each run.
* The two checks that failed after S3 were updated, not deleted, and they carry the reason.

## 8. Not attempted, with the smallest next step

| Item | Why not | Smallest step that finishes it |
|---|---|---|
| **S11b** | the slow profile only became usable late (S13), and the harness that measures it is untested | `SLOW=1 ./tests/backend/start-server.sh`, hardlink `Single Track (2026).mkv` into `media-slow` (done already), then `python3 tests/backend/s11b_cancel.py --mode engine-cancel` before and after a fix to the cancel path |
| **A8 / the slow-profile speed-up** | ~35–40 min per run at 12.8 ms/16 KB, and the baseline it must be compared with was taken on the slow profile | one `acceptance.py --scope episode --workers 4 --no-clear` under `SLOW=1`, compared with session 1's 1 082.6 s |
| **A5/A6/A7, C23a, D29–D37, E39–E45** | budget; the queue/failure surface was where the fixes were | `acceptance.py --scope series`, then `rows_d.py` for the settings rows |
| **Matrix B (all of it)** | needs a real browser session; nothing this session touched the GUI beyond one settings field | see `knowledge/gui-test-report-2026-09-11.md` |
| **S8, D1/F2+F29, D4/F15** | the user answered all three today; each is a separate change | S8: audio-only results report unverified and write no sidecar; F29: a confirmation before the global Kill; D4/F15: the dashboard page redirects to the main page's settings |
| full disk / separate staging filesystem | needs root; both live on device `66306` | unchanged from session 1: **blocked** |

## 9. Patching order, updated

1. ~~S11~~ (`ac66249`), 2. S11b (harness ready), 3. A4 done on the fast profile / **A5 + A8 still
open**, 4. ~~S3~~ (`9c9514e`), 5. ~~S4~~ (`0a2b23d`), 6. D13/D14 (browser), 7. the lies (D5, D12,
F27, F28, D2, D6, D11), 8. stuck/destructive controls (F29, F24, D1/F2), 9. unusable paths (D18, D7,
F4, S5, **S12 new**), 10. the settings surface (D4/F15 answered, D3/F10–F13, F14), 11. S8 + S12,
12. layout, robustness, hygiene.
