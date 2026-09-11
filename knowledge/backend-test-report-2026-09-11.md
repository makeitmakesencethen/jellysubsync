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

Not started: S3, S4, S5, D15/F1/F2 authorisation (the brief makes that conditional on the user agreeing
who may do what), and the whole of the "honest status" batch (D5/D6/D7/F27).

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
finished 20:49:09.7 — **48 of 50 tracks done in ~18 minutes with zero failures**, against 1 of 50 in
22 minutes before. Two job-side `matroska-shared` passes still ran (458 287 ms and 598 681 ms), so the
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
6. D8/D9/D10 (batch validation, dedupe, one error shape), then D5/D6/D7/F27 (honest status), then
   D4/F15 (one settings surface), then the junk in §6.
