# Fix plan — every finding from both records, one line each

Companion to `GOAL_PROMPT.md`. **This file is the loop.** Work top-down, one finding per commit:
reproduce it, fix it, verify the fix the same way you reproduced it, then tick the box here with the
commit hash and the evidence beside it. When your budget runs out, leave the rest unticked with a
one-line note on each. **Never tick something you did not verify.**

`state` is one of: `open`, `done` (commit + evidence), `decision` (the user must choose — ask them),
`blocked` (cannot be done here — name why).

`D…` are the audit's 19 live findings. `B1–B30` are its static audit of the scheduler and extractor.
`F1–F30` are its static audit of the API, GUI and settings. `S…` come from the 2026-09-11 backend test.

## Tier 1 — the run has to finish — a bulk run must neither hang nor fail (3 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| done | **S11** | high | the reference derivation hands ffsubsync the video, so it demuxes the whole file and hangs | `ac66249` — before 7 Completed + 1 Failed (`unable to read reference`), after 8/8 Completed, 0 engine demuxes; A4 50/50, 16.5 s, no leftovers |
| refuted | **S11b** | high | cancelling a batch left the engine's ffmpeg child alive. Measured on `SLOW=1` with the shim active (engine 70 s per run): the batch was cancelled while ffsubsync pid 101537 and its child pid 101541 were both alive, and **both were gone within 1 s and stayed gone for 25 s** (0 survivors). The session-1 symptom was seen while the engine was demuxing the whole container — the S11 fallback that no longer exists. Residual risk, still open as **B7**: the extraction lane passes `CancellationToken.None`, so a `Kill` cannot stop a lane pass | `s11b-after.json`, batch `93f9471c…`; harness `tests/backend/s11b_cancel.py` |
| done | **S6** | high | bulk ran one whole-file pass per worker | `a46c5cd` — 48/50 tracks in 18 min where 1/50 took 22 min |

## Tier 2 — nothing may write a wrong file, lie on screen, or leave junk behind (14 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| open | **D11** | low | the resolved sync mode is opaque and varies for identical input (`normal`/`auto`/`ultimate`) |  |
| open | **D12** | low | two version sources in one response (`PluginVersion` vs the plugin path) |  |
| done | **D15** | high | the plugin has **no authorisation checks at all**: an ordinary user can read the plugin log and all job history, trigger | `a776267` — 403 for a non-admin on Install/Kill/SpeechCache-Clear/Log, 200 for the admin |
| done | **D16** | critical | replace mode overwrites the original subtitle and deletes the backup on success — unrecoverable, verified | `fd923ab` — every backup kept; verified on the replace fixture |
| done | **D17** | critical | with a mixed cue index the extractor returns 803, 843 or 401 cues for the same track, silently, and reports Completed | `fd923ab` — cue parity; 27 cues where it used to give 24 |
| open | **D2** | high | "Install ffsubsync" reports success while the configured binary path does not exist; status says `IsInstalled: true` |  |
| open | **D5** | medium | status claims "4 in use (setting 4)" while nothing is running |  |
| open | **D6** | medium | History tab shows the "nothing synced yet" empty state while listing a completed run |  |
| open | **F24** | ? | The legacy settings page's save path has no error handling |  |
| open | **F27** | ? | `/SubSync/Active` omits the worker fields the UI reads, and the worker count is expressed three ways |  |
| open | **F28** | ? | Error shapes are inconsistent, so the UI shows whatever came back |  |
| open | **F29** | ? | Kill has no confirmation |  |
| done | **S3** | medium | a reference track aligned far off is written instead of refused | `9c9514e` — job 047b4890 Failed/Refused at -59080 ms, sidecar sha256 unchanged; setting + page field added |
| done | **S4** | medium | a failed job leaves a 0-byte subtitle in the library | `0a2b23d` — job 0f490268 Failed before the copy; fixture folder byte-identical before/after, no 0-byte sidecar |

## Tier 3 — critical and high severity — the user feels these (2 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| decision | **D1** | high | `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it | answered 2026-09-11: keep it global and admin-only, add the confirmation the UI lacks (F29); not implemented yet |
| open | **D3** | high | settings validation is partial: offset, paths, encoding and language tags accept nonsense and are saved silently |  |

## Tier 4 — the rest of both matrices, layout, hygiene, and the audit's unproven static leads (verify first: refuting one is a real result) (68 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| open | **B1** | ? | Replace mode can destroy the original subtitle with no rollback |  |
| open | **B10** | ? | `FinaliseStats` runs before `ReadCalls`/`TotalMs` are assigned |  |
| open | **B11** | ? | Progress line during a metadata scan always reports 0.0 MB |  |
| open | **B12** | ? | Four planner/cache dictionaries grow without bound |  |
| open | **B13** | ? | Per-reader memory: 4 MB window + up to 384 MB of prefetched ranges |  |
| open | **B14** | ? | `Dispose` leaves child processes and lanes running |  |
| open | **B15** | ? | Logging while holding the queue lock |  |
| open | **B16** | ? | `KillAll` busy-waits with `Thread.Sleep` and fabricates its return count |  |
| open | **B17** | ? | Completed jobs are evicted whenever the store exceeds 50 entries |  |
| open | **B18** | ? | Five near-identical process runners, two argument styles |  |
| open | **B19** | ? | `out_time_ms` is converted as milliseconds |  |
| open | **B2** | ? | Shared extraction pass silently drops cues for tracks with a mixed cue index |  |
| open | **B20** | ? | A cancelled extraction is reported as a failure and takes the fallback path |  |
| open | **B21** | ? | Prefetch faults lose the real error inside an `AggregateException` |  |
| open | **B22** | ? | `MeasureSyncChange` is computed twice per job |  |
| open | **B23** | ? | `ClearStaleJobDirectories` recursively deletes anything under the scratch root |  |
| open | **B24** | ? | Cue deduplication uses a hash as the set key |  |
| open | **B25** | ? | Short reads are accepted as truncated cue text |  |
| open | **B26** | ? | Culture-sensitive parsing/formatting in two places |  |
| open | **B27** | ? | `Diag` builds its message even when diagnostics are off, and writes to stderr |  |
| open | **B28** | ? | Magic numbers that should be settings, and one that contradicts the documented policy |  |
| open | **B29** | ? | Non-volatile `_disposing`, unsynchronised `_lastPassFinishedUtc` |  |
| open | **B3** | ? | Scheduler touches the media share while holding the queue lock |  |
| open | **B30** | ? | `DescribeExtraction`'s catch-all relabels anything unknown as ffmpeg |  |
| open | **B4** | ? | Cue-point parsing does not reset per `CueTrackPositions` |  |
| open | **B5** | ? | `--version` process is spawned synchronously from a property getter, under the queue lock |  |
| open | **B6** | ? | A job can stay `Running` forever |  |
| open | **B7** | ? | `Kill` cannot interrupt a Matroska extraction |  |
| open | **B8** | ? | A failed ffmpeg extraction is accepted if a partial file exists |  |
| open | **B9** | ? | Kernel-IO baseline is shared mutable static state raced by parallel lanes |  |
| open | **D10** | low | error bodies are inconsistent: validation gives ProblemDetails, other failures give "Error processing request." with no  |  |
| open | **D13** | unverified | on a series scope, "Sync selected" issued a *preview* call (`/Subtitles/Batch`) rather than creating a batch |  |
| open | **D14** | unverified | the injected client script is delivered into the SPA shell (verified), but whether its hooks match the React item page i |  |
| open | **D18** | medium | in Jellyfin Web 12 `EnableInMainMenu` produces no entry in the main app menu; the page is reachable from the dashboard's |  |
| open | **D19** | low | the golden-section-search checkbox measures 1×1 px (styled input — verify visually before calling it broken) |  |
| decision | **D4** | medium | the dashboard config page and the main page edit the same settings with different subsets — two sources of truth | answered 2026-09-11: merge the two settings pages (the dashboard page redirects to the main page's settings); not implemented yet |
| open | **D7** | medium | polling runs flat out (≈1.4 req/s) with nothing happening, including three duplicate calls at load |  |
| open | **D8** | medium | a batch accepts a series `ItemId`, which single sync rejects outright |  |
| open | **D9** | medium | duplicate jobs/tasks are accepted with no dedupe (same item+track twice → two jobs) |  |
| open | **F1** | ? | `POST /SubSync/Install` lets any authenticated user run apt-get/pip as root |  |
| open | **F10** | ? | No range checks on the numeric settings the engine is given as argv |  |
| open | **F11** | ? | Output encoding is free text; the server silently substitutes utf-8 and the UI still says "Saved." |  |
| open | **F12** | ? | "Use golden-section search" is inert unless framerate correction is enabled |  |
| open | **F13** | ? | The "Parallel workers" change applies immediately to sync but needs a restart for extraction lanes |  |
| open | **F14** | ? | The scheduled sweep ships with no trigger, yet the UI advertises it |  |
| open | **F15** | ? | Two settings pages that disagree, and sweep knobs exposed nowhere |  |
| open | **F16** | ? | The extracted-subtitle cache is undocumented in the UI and unbounded in memory |  |
| open | **F17** | ? | The audio-analysis cache's 30-day rule ignores use |  |
| open | **F18** | ? | `SpeechCache.Prune` still prunes a file pattern that moved elsewhere |  |
| open | **F19** | ? | `SrtWriter.Render` treats a real 2-second cue as a "guessed" duration |  |
| open | **F2** | ? | `POST /SubSync/Kill` is global, id-less and unowned |  |
| open | **F20** | ? | `MediaVolume.Of` matches mount points by raw string prefix |  |
| open | **F21** | ? | The response-body swap in the middleware has no restore on every path |  |
| open | **F22** | ? | The middleware disables compression for every index page response |  |
| open | **F23** | ? | The only anonymous endpoint is the client script |  |
| open | **F25** | ? | The legacy page still tells admins to inject the script by hand |  |
| open | **F26** | ? | One closing `</div>` leaves two settings fields outside their section |  |
| open | **F3** | ? | Item-scoped endpoints have no per-item access check (IDOR, read and write) |  |
| open | **F30** | ? | `SweepState` grows past its own cap until the next restart |  |
| open | **F4** | ? | Server-wide job history and the plugin log expose other users' runs and server paths, with no admin gate |  |
| open | **F5** | ? | `POST /SubSync/Batch` accepts tasks that `POST /SubSync/Sync` rejects, and never dedupes them |  |
| open | **F6** | ? | "Clear cache" can delete the reference subtitle a running job is using |  |
| open | **F7** | ? | Job scratch directories orphaned by a kill or crash are never swept |  |
| open | **F8** | ? | The GUI can build batches the API rejects (1000-task cap, no chunking) |  |
| open | **F9** | ? | `POST /SubSync/Subtitles/Batch` bounds items but not the expansion |  |
| open | **S5** | medium | a bitmap track is missing from the track list instead of refused with a reason |  |
| open | **S7** | medium | queueing under load costs ~212 ms and each job re-probes the storage |  |
| decision | **S8** | medium | an in-sync subtitle synced against the audio is moved and written as a success | answered 2026-09-11: an audio-only result is reported unverified and no sidecar is written unless the reference was a subtitle track; not implemented yet, and it needs the external-sidecar case settled first (see the report's §S8 note) |

## New findings this session (2026-09-11, evening)

| state | id | sev | what | evidence |
|---|---|---|---|---|
| open | **S12** | medium | `/SubSync/Subtitles/{id}` hides the plugin's own `.SYNCED.` sidecars, but `/Sync` accepts an index that resolves to one and syncs it again — it wrote `Helikopterrånet S01E01.SYNCED.ukr.SYNCED.srt` (69 602 B) from `…SYNCED.ukr.srt`. Listing and queueing disagree about what a track is | plugin log 21:34:36 `job 4e2a2674 … output=…SYNCED.ukr.SYNCED.srt … extraction=n/a`; the junk file was deleted |
| open | **S13** | high | the change that makes a bulk run finish was itself blocked by a harness defect: `tests/backend/slowread.so` did not exist, so the first "slow profile" run of this session silently measured the fast path (the loader warns and continues) | `ld.so: object …/slowread.so cannot be preloaded`; plugin log `extract: storage 0.01 ms per 16 KB read`. Fixed in `start-server.sh`, which now builds it |
| open | **S14** | medium | `MediaStream.Index` is not stable: adding a sidecar renumbered the episode's subtitles from 4..54 to 8..57, so any hard-coded track index measures or syncs a different track (my first S3 attempt hit an external sidecar and produced S12's junk file) | `/SubSync/Subtitles` before/after the A4 run |
| answered | **S3-conflict** | high | `GOAL_PROMPT`'s hard rule "the plugin never refuses a job" vs `AGENTS.md` + this plan's S3 line ("implement `MaxSubtitleReferenceOffsetSeconds` as a refusal"). **Decided by fabji 2026-09-11: the refusal stands, as `AGENTS.md` and the fix plan specify.** The never-refuse rule applies where it was meant to: a reference that cannot be *built* falls back to the audio (S11), a reference that exists and is provably from another cut is refused (S3) | `9c9514e`; the two checks rewritten in the same commit assert the refusal |

## Ask the user before coding these

- **D1** — `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it
- **D4** — the dashboard config page and the main page edit the same settings with different subsets — two sources of truth
- **S8** — an in-sync subtitle synced against the audio is moved and written as a success

## Cannot be tested on this machine

- a **full disk** and a **separate filesystem for staging** both need a small filesystem, which
  needs root. Record them as `blocked` with the reason instead of working around them.

