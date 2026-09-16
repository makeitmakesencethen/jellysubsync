# Fix plan — every finding from both records, one line each

**How this file is kept** (added 2026-09-14): every finding gets a row the moment it is found - status,
severity, what it is, and the evidence in its own cell, quoting log lines verbatim and naming file and line
when the evidence is code. Rows move to `done` with the commit that fixed them, and a change that reverses an
existing rule records *why* in the same entry. The file is updated as work happens, never batched at the end.

**How a row is ranked** (added 2026-09-14, the triage): severity is blast radius for a user, not effort -
**high** means a wrong result, data loss, or an unauthorised action reaching the library; **medium** a visible
malfunction, wasted work, resource growth or a misleading report with a workaround; **low** cosmetics,
robustness and hygiene with no user-visible behaviour change. Every open row carries one, and its evidence
cell says how far the claim has been checked: a row read against the code says **Verified** and quotes a
`file:line`, a row that has only been ranked says so in as many words, and a row closed as **refuted** keeps
the reasoning, because that is the register remembering rather than forgetting.
`python3 tests/check_fixplan.py` fails when an open row or section has no severity, no evidence, or a verdict
it cannot back - so this file cannot rot back into rows of unknown seriousness.

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
| done | **B7** | high | `Kill` cannot interrupt a Matroska extraction. The lane handed the reader `CancellationToken.None`; measured on the slow profile, a Kill was followed by 805.6 MB of reads in 40 s while `/SubSync/Active` said nothing was running. `Kill` now cancels the pass and the source is re-armed for the next one; the same measurement after the fix: reads stop at +21.7 s (the current read batch finishes first), and the pass logs `stopped (killed by the user)`. A cancelled pass no longer marks tracks as unextractable, and the reason is printed (it used to be relabelled as an ffmpeg whole-file read) | `lane-kill-before.json` / `lane-kill-after.json`, harness `tests/backend/lane_kill.py` |
| done | **S6** | high | bulk ran one whole-file pass per worker | `a46c5cd` — 48/50 tracks in 18 min where 1/50 took 22 min |

## Tier 2 — nothing may write a wrong file, lie on screen, or leave junk behind (14 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| open | **D11** | low | the resolved sync mode is opaque and varies for identical input (`normal`/`auto`/`ultimate`) | ranked by blast radius; claim not yet read against the code |
| open | **D12** | low | two version sources in one response (`PluginVersion` vs the plugin path) | ranked by blast radius; claim not yet read against the code |
| done | **D15** | high | the plugin has **no authorisation checks at all**: an ordinary user can read the plugin log and all job history, trigger | `a776267` — 403 for a non-admin on Install/Kill/SpeechCache-Clear/Log, 200 for the admin |
| done | **D16** | critical | replace mode overwrites the original subtitle and deletes the backup on success — unrecoverable, verified | `fd923ab` — every backup kept; verified on the replace fixture |
| done | **D17** | critical | with a mixed cue index the extractor returns 803, 843 or 401 cues for the same track, silently, and reports Completed | `fd923ab` — cue parity; 27 cues where it used to give 24 |
| done | **D2** | high | "Install ffsubsync" reports success while the configured binary path does not exist; status says `IsInstalled: true`. **Fixed by asking the resolver instead of the plugin's own directory.** `IsInstalled` was `File.Exists(ManagedFfSubSyncPath)`, which answers "is the plugin's managed binary present" while a job executes `ResolveFfSubSyncPath()` - so a user with a configured path read "installed", saw no install prompt, and watched every sync fail at the first engine call. `IsInstalled` is now whether the *resolved* engine can be found (`EngineIsInstalled` → `EngineIsUsable`: a rooted path is asked of the filesystem, a bare `ffsubsync` is looked up on PATH exactly as the shell would, and an empty PATH finds nothing, failing closed), `ResolvedBinaryPath` is finally assigned (it had been declared and never set), and `EngineNote` names the path that was looked for when it is missing, so the page can say which setting is wrong. | Harness: 5 checks on `EngineIsUsable` (rooted existing, rooted missing, bare name found on PATH, bare name absent, empty/missing PATH, no path at all). Integration: with a configured engine that exists the status resolves to it; after that file is removed the status reports `IsInstalled: false`, `ResolvedBinaryPath` = the configured path, and the note names it (`The configured ffsubsync path /tmp/ffsubsync-probe-engine does not exist...`); storing the setting again restores `IsInstalled: true`. One honest note: the settings API refuses a path that does not exist *at save time* (`SettingsValidation` rewrites it to the default with a note), so the case that bites is a path that was valid and whose binary then went away - which is what the probe reproduces.Shipped in 2.0.51 (beta) and checked as an installer sees it: the catalog reports 2.0.51.0, the 42 503 689-byte zip's MD5 is `1864088f5d06ffd3cba488c0428afd4d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.51.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`OwnerId`, `CopyForViewer`, `ForViewer`, `EngineIsUsable`, `EngineNote`, `ClassifySyncTarget`, `SyncQueueConflictException`) and its user-facing sentences as UTF-16 string literals (`not a video: pick the episodes themselves`, `already queued by another account`, `does not exist. Correct it in the settings`) - a plain ASCII search of the DLL finds the names and misses the sentences, which is the wrong test for one half and the right one for the other. |
| open | **D5** | medium | status claims "4 in use (setting 4)" while nothing is running | ranked by blast radius; claim not yet read against the code |
| open | **D6** | medium | History tab shows the "nothing synced yet" empty state while listing a completed run | ranked by blast radius; claim not yet read against the code |
| open | **F24** | low | The legacy settings page's save path has no error handling | A legacy page whose save can fail silently. ranked by blast radius; claim not yet read against the code |
| open | **F27** | medium | `/SubSync/Active` omits the worker fields the UI reads, and the worker count is expressed three ways | The API omits fields the UI reads and expresses the worker count three ways - the same visible confusion as D5. ranked by blast radius; claim not yet read against the code |
| done | **F28** | low | Error shapes are inconsistent, so the UI shows whatever came back. Closed with **D10**: the server now has one body for every failure and the page has one reader for it - `problemText` prefers `title: detail`, passes a bare body through unchanged (a proxy or an older build can still send one) and falls back to the status when there is no body at all. The reader is taken from the shipped file and run in node, so the check is on the code the browser runs. | Evidence: the D10 row, plus 1 node unit test and 3 source checks. Shipped in 2.0.49 (beta) and checked as an installer sees it: the catalog reports 2.0.49.0, the 42 498 188-byte zip's MD5 is `dc38a23f849f4c6ad916150d7c89a99c` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.49.0, and the embedded page script in the shipped DLL carries this fix's own markers (`GET_REUSE_MS`, `inflightGets`, `noteAlreadyQueued`, `problemText`) - the web assets are embedded resources, so they are findable in the assembly where a folded constant would not be. |
| open | **F29** | medium | Kill has no confirmation | Pair it with F2: a global, unowned stop with no confirmation is one misclick away from ending every run on the server. ranked by blast radius; claim not yet read against the code |
| done | **S3** | medium | a reference track aligned far off is written instead of refused | `9c9514e` — job 047b4890 Failed/Refused at -59080 ms, sidecar sha256 unchanged; setting + page field added |
| done | **S4** | medium | a failed job leaves a 0-byte subtitle in the library | `0a2b23d` — job 0f490268 Failed before the copy; fixture folder byte-identical before/after, no 0-byte sidecar |

## Tier 3 — critical and high severity — the user feels these (2 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| decision | **D1** | high | `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it | answered 2026-09-11: keep it global and admin-only, add the confirmation the UI lacks (F29); not implemented yet |
| done | **D3** | high | settings validation is partial: offset, paths, encoding and language tags accept nonsense and are saved silently | **Verified.** `Api/SubSyncController.cs:514-538` deserialises the body and calls `Plugin.Instance!.UpdateConfiguration(wanted)` with no range or format check on anything; the only clamping in the controller is deliberate and elsewhere (`:270` the worker limit into `1..MaxParallelWorkers`, `:470` the log tail into `4..4096` KB), which shows the habit exists where it was thought about. Offset, paths, encoding and language tags save whatever they are given. **Shipped in 2.0.41** (`2ef4982`, "one validation path for the settings, and the engine is given only validated numbers"): one `SettingsValidation.Apply` is called where settings are stored (the controller and the plugin's own save), every numeric/format field is clamped or substituted, and the notes it returns are written to `Plugin.LastSettingsNotes` so the page can say what changed. The rules and their bounds are pinned by the suite (F10's numbers included). |

## Tier 3b — 2.0.24 read-policy follow-ups (4 items)

`R…` items come from the verification work of 2.0.24 (`ReadPolicy`), not from the 2026-09-11 audit.

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| held | **R1** | medium | the cue-indexed route plans one read per located block plus the cluster head, so its read *calls* follow the cue count (2 per cue) at every storage profile. The goal for the read policy stated the opposite: "on per-read-latency storage, reads per pass are bounded by bytes/window + prefetched ranges, never by cue count". Confirmed by measurement, not inference: the plan is identical at every profile (matrix `blocks early/late`, 800 cues, 1 track: 1600 calls, 1,9 MB) and the wall clock follows `cues x latency` - the same two rig passes take 1,54 s and 2,92 s on a 10 ms/read share and 5,53 s and 13,62 s on a 50 ms/read one, reading the same 0,65 MB and 1,08 MB. The walk's merge threshold never fires for a 20-900 KB cluster gap either, because `MergeGapBytes` is derived from the pass's own small-read samples (~1,2 KB at any latency). The prefetch width hid it by ~5x, not 16x, and a share that queues reads would expose the serial 527 x 50 ms = 26 s. The shared 32-track route *is* bounded (451 calls for 800 cues x 32 tracks). | rig `--call-ms 50 --mb-per-second 11`: 2.0.23 325,21 MB/338 reads/24,27 s and 1086,48 MB/275 reads/113,50 s; read policy 0,65 MB/527 reads/5,53 s and 1,08 MB/267 reads/13,62 s. Matrix prints the waiting per cell (80,00 s serial / 5,00 s at 16 wide for 800 cues at 50 ms). **Held on branch `hold/r1-throughput-fit` (tip `3ef1b2a`)**: the fit logic and its unit checks are good, but the change is not earning its risk - no measured improvement on the episode shapes that matter, and a regression risk on the fast-reads/slow-bytes profile that rests on the measured latency being right. **Path forward, with no probe**: the fit needs two read sizes in one pass, and a real batch has them on the same storage - a multi-track file's cue index is several times the single-track one (measured: 4 tracks fits at 50 ms/read, merge gap 256 KB at plan time, where one track at the same latency fell back to 0,1 MB/s), and the walk route reads 16 MB chunks against 2 KB blocks - fitted at 16,6 MB/s on the walk fixture at a modelled 11 MB/s, so that pass does have both sizes to learn from. The blocker is that the profile is per pass, so a cue-indexed pass cannot learn from the walk or the multi-track pass running beside it on the same share. The ffmpeg fallback **cannot** feed it: it demuxes in its own process, invisible to the policy. So the fix is a per-volume profile shared between passes (fed only by reads the plugin already makes, decaying so a busy share is not believed for ever) or a re-plan after the first fetch - not a deliberate larger read, which is the pre-work probe the goal forbids. Noise awareness comes first either way: the same fixture reported 0,1 MB/s in one run and 11,1 MB/s in the next, because a ~0,7 ms difference was being read against 50 ms of latency. | **The decision this row is waiting on** (measured 2026-09-14/15): the NAS collapses past limit 4 (8 episodes at limits 1/2/4/8 gave 22,2/48,9/94,1/421,3 s per file and 122/135/133/66 files/h), so a tiered per-volume cap would buy back per-file latency with no throughput loss on this share - and it reverses three invariant checks plus one reflection check (`no per-volume budget`), which is a user decision, not a code change. The measurement side needs no new scaffolding: two filesystems (`shm` and the shimmed `media-slow` volume) and `tests/backend/s28-concurrency.sh`.
| done | **R2** | medium | a plan that is an upper bound rather than an estimate (`PlanWalk`, and a cue plan with clusters to walk) is marked `coarse: true`, and `Compare` then suppresses the miss flag entirely: the expected-vs-actual line for that route can never fail. Measured on the `walk.mkv`/`nocues.mkv` fixtures: plan 62,92 MB / 4 read(s) against actual 0,24 MB / 60 read(s) (bytes 0,00x, reads 15,00x), and on the rig `remux-walk` 545,26 MB / 130 against 1,08 MB / 267. An invisible verification gap. | fixed 2026-09-13 by (b), not (a): the walk stops once it has found the blocks it came for, so its cost is data-dependent and no tight prediction can be derived from the index - a bound tight enough to warn would warn wrongly. `ReadPolicy.BoundNote` (" [bound, not verified]") is appended to every bounded plan's log line and `MkvExtractionStats.PlanBound` counts them, so the gap is visible in the user's log rather than reading as verification. `7409e02` - four new checks per fixture: the marker is on exactly the bounded plans, a fully located plan never carries it, and the walk's cost is held to something that *can* fail (one window per cluster visited: 123 reads for 60 clusters on `nocues.mkv`, where a per-block-header walk would need thousands). 482 checks green. Ships in 2.0.25. |
| done | **R3** | low | `ReadPolicy.Measured` and `ReadPolicy.Samples` read the *pending* sample window, not the pass's measurement state: `Observe` resets `_sampleCount` after every update, so `Measured` is true only in the instant the 8th sample of a round arrives and false the rest of the time, and `Samples` falls back to 0 every 8 reads. Nothing calls either (verified: no caller in the plugin, the harness or the checks), so nothing is broken by it today. | Half-finished, and wrong for the meaning its name and doc claim. Recommendation: either delete both, or rename to `PendingSamples` and add a real `MeasuredOnce`/total-samples pair, then use it in `DescribeProfile` so the log says whether the numbers are measured or still the class defaults (0,05 ms/read, 1500 B/ms). Ships in 2.0.25. Also found while verifying it: the profile line was logged only inside the metadata-walk branch, so a cue-indexed pass never said whether its numbers were measured or still the defaults - it is emitted once for every route now, with a check pinning that. |
| done | **R4** | medium | the shared multi-track phase was compared against its own raw plan while being served out of the primary track's fetch, so every file with two or more subtitles logged `extract plan: <file> shared-pass expected 0,62 MB/520 read(s), actual 0,00 MB/0 read(s) - this pass missed its own prediction` as a WARN. Netting the plan against what is already in hand under-predicts instead (1 read promised where the phase makes nine window-sized ones), so the line is logged with both numbers plus how much the phase read of its own, and the miss flag is not armed: the fetches it lives on were compared by the phase that made them. | Seen in the rig on `arcane-4t` (`missed=1` on every run, now 0). Check added: the multi-track fixture must not report a miss. Ships in 2.0.25. |

## Tier 3c — the extraction programme's first two items (NEbml evaluation, the cue-index defect)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| done (decision: keep the hand-rolled parser) | **E1** | high | **NEbml evaluated, not adopted.** An isolated prototype of the container-structure layer on NEbml 1.1.0.5 - SeekHead, Tracks, Cues with their per-track positions, cluster and block headers, and NEbml's VINT decoder - reproduces the shipped parser's output *byte for byte* everywhere the shipped parser's own route reaches the data: the D17 file and its mixed-index variant, three tracks of the 2.4 GB episode, the 9.4 GB and 4.9 GB films, a 320-cue episode, a signs track, and the synthetic cue/walk/multi-track/grouped shapes. It is nevertheless **not adopted**, and the blocker is disqualifying on its own: **NEbml cannot parse a Segment with an unknown size** (`EbmlDataFormatException: unknown-size elements are not allowed at root level`), which is how every fixture this project generates and every streamed mkv starts - so "adoption" would mean hand-rolling the top two levels (EBML header, Segment header) to work *around* the library on exactly the structure we produce, which is not a simplification. Second trap found: `ElementId.Value` strips the VINT marker bit (the Segment reads as `0x8538067`, not `0x18538067`), so every comparison must use `EncodedValue`, and the mistake is silent ("found nothing") rather than loud. **The comparison rig stays alive as a standing verification tool, not production code**: `tests/backend/nebmlrig/` (the NEbml-driven extraction) plus `tests/backend/nebml_compare.py` (the per-fixture runner against the shipped parser and ffmpeg) are to be pointed at any future extraction-path change as an extra correctness check - the way they caught E2 below. | Branch `eval/nebml-structure-parser` (rig tip `8075ad6`); prototype + driver in `tests/backend/nebmlrig/` and `tests/backend/nebml_compare.py`. Per-fixture results: 14 of 16 fixtures byte-identical and +0 against ffmpeg; the two that are not (`Wide Multi-Language`, synthetic `nocues.mkv`) have no Cues element at all, so they take the shipped metadata-scan route, which the prototype does not implement. |
| done | **E2** | high | **The shipped parser emitted some subtitle cues twice, and visited clusters it did not need to - one defect, two faces.** Face one: `ParseCueRefs` held a cue point's cluster and block offsets in variables that outlived each CueTrackPositions, so they ended up being whichever track's came last. One cue point carries one CueTrackPositions per track (mkvmerge writes the video's and every subtitle track's into the same cue point), so a cue point that matched the wanted track still handed it a neighbouring track's offsets: on `kopps.mkv` 104 of 829 cue points named another track's block, on `Sune i Grekland` **all 1019** did. Every one of those fell back to walking its cluster - 933 cluster visits for 829 cue points, 2038 for 1019 - which is the D17 `1205 vs 803` shape seen from the other side. Face two: the walk scans a cluster's blocks without knowing which of them a neighbouring cue point already emitted, so a block reached by both routes came out twice - once with the duration it carries, once with the 2 s default this extractor uses when a block has none, which `SrtWriter`'s overlap pass then cut to 1,5 s, so one subtitle read as two cues with mismatched ends. **Result: kopps 832 -> 829 cues (index and ffmpeg both say 829), Sune i Grekland 1251 -> 1019 (both say 1019), D17 mixed-index 843 -> 803 (ffmpeg says 803, which closes the difference the 2026-09-11 audit recorded for that shape)**; each byte-identical to the NEbml reference. No change on files whose cue points hold one position each: Helikopterranet 803/726/898, Mauri 320. The fixtures could not see it because `make_remux.py` wrote one CueTrackPositions per cue point; it can now write mkvmerge's shape (`--grouped-cues`, `MKV_FIX_GROUPED`) and a partly located index (`MKV_FIX_MIXED`), and the suite asserts the cue count, one cluster visit per located cue point (10 for 10, was 20), no repeated (start, text) pair, and that a second pass over the same file returns the same subtitles. | `5a0ad05` (offsets belong to the track they name) and `9bbec18` (a block is one subtitle however many routes reach it), both on `beta`. Rig before/after per fixture: `nebml-compare*.json`. 493 checks green. |
| done | **E3** | low | `MkvExtractionStats.ClustersVisited` counted a walked cluster twice: the cue point counted the cluster it names before walking it, and `ReadCluster` counted the same cluster again. The counter is the number the user's log shows and the denominator two checks reason about, and it read **15 visits for 10 cue points on `MKV_FIX_MIXED` and 1205 for 803 cue points on the D17 mixed index, where the NEbml reference reports 803**. Fixed by removing the walk's own increment - every caller either counted that cluster already (a cue point naming it) or counts the clusters it iterates itself (the metadata walk, the multi-track pass) - which leaves one visit per cue point and matches the reference on every fixture and real file: kopps 829, Sune i Grekland 1019, Helikopterranet 803, D17 mixed index 803 (was 1205), `MKV_FIX_MIXED` 10 (was 15). The mixed-fixture check now pins the exact number instead of `> located`. | `483c11e`, suite 493 checks green. |

| held (premise refuted by measurement) | **E4** | high | **Cluster-head read elimination (Phase 2): no reduction available.** The cluster head must be read to turn the cue index's offset into a block position, and the element the phase wanted to drop with the block's own timestamp (the cluster's `Timecode` child) sits inside that same planned read. Measured with the decision read built in: read count and bytes unchanged to the byte - kopps 1644 reads / 2,099,265 bytes, Sune i Grekland 1928 / 2,500,491, Helikopterranet track 5 1575 / 2,953,395 - while the verification does confirm the index's timestamps are the blocks' own on real files (every located cue point exact) and are not on the project's own fixtures (9 of 10 cue points, -5000 to -45000 ms). No measured improvement, so it is not shipped: held on branch `eval/drop-cluster-head-read` (`145151a`), which carries the measurement, the per-pass verification read and the two log lines saying which branch a file takes. | Branch `eval/drop-cluster-head-read`; the byte-diff matrix and the before/after read/byte table are in the Phase 2 report. |
| declined (considered, not done) | **E4b** | low | **Merging each cue's cluster head and block into one covering read, as a speed route.** The only way from two reads per cue point to one is the covering read the policy already prices against `MergeGapBytes`: one read from the cluster start past the block, paying the gap bytes instead of a second call. Declined on the numbers rather than on principle - at the measured 46 ms/read the trade is a wash (46 ms + ~0.5 MB of gap against two calls at 46 ms each) and at 13 ms/read it loses outright (13 ms + ~45 ms of transfer against 26 ms of calls), and on the `--sub-position late` shape the gap is the video payload, so the bytes paid are real bytes of the library. A change that can slow down part of the library for no measured gain is not worth carrying; the policy already makes this call per file from what it has measured, which is the right place for it. | Rig arithmetic from the measured reads/bytes in the Phase 2 report; E4's row has the before/after table. |
| merged 2026-09-14 (premise still refuted; the numbers now feed the per-volume walk cap) | **E5** | medium | **Per-volume storage profile (Phase 3, R1's original fix): no measurable gain, so not shipped.** Built to spec - one profile per volume, fed only by reads the passes were already making, latency as an age-weighted median and throughput as a byte-weighted mean trimmed of the slowest fifth, half-life 10 minutes, seeded into every read policy at construction - and covered by ten new checks (504 green). It changes nothing: with the rig running several passes in one process on one volume, `arcane` x3 at 46 ms/read is 527/527/527 reads and 5,12/4,94/4,88 s before against 5,09/4,96/4,83 s after with a byte-identical plan, the walk shape 267/267 both sides, a small-index file 47/47/47 both sides, and at 100 ms/read 527/527 both sides. Structurally: a pass reads the SeekHead and Cue index *before* it plans, so its own profile already holds the measured storage, and the one lever a profile feeds (`MergeGapBytes`) is the byte-equivalent of one read call, so acting on it is break-even by construction. Held on branch `eval/per-volume-profile` (`9e53170`), which also carries the rig's ability to run a sequence of passes in one process. | `9e53170`; before side `3f6db92`, rig rows in `.tests-work/p3-*.json`. |
## Phase 4 triage (2026-09-13) — which open items actually touch extraction / the read policy

Reproduced with `p4probe` (a throwaway harness in `.tests-work/p4probe`, same shape as the read rig) on
generated fixtures: a 520-cluster late-block episode, the same with no subtitle cue points, a 1,3 GB remux
whose index locates nothing, a 3-track file with mkvmerge's grouped cue points and a half-patched one, and a
6-track 300-cluster file. Items are only called reproduced where a fixture actually shows it.

| id | sev | state | evidence |
|---|---|---|---|
| **B11** | low | **reproduced** | the metadata-scan route logs `scanning clusters (400 read, 0.0 MB, 200 subtitles found)` on a 520-cluster fixture while it is reading - the byte counter of that route is never fed. It is the slowest route there is (this is the walk that takes minutes on the user's share), so the one number a user watches during the longest wait does not move. |
| **B9** | low | **reproduced** | two passes in parallel in one process both report `kernelBytes=42006 kernelCalls=46` while each made 17 reads of its own: the kernel-IO baseline is process-wide static state, so a pass's log line claims the other lanes' I/O. The server runs 4 workers. |
| **B7 / B20** | medium | **reproduced (extraction side)** | cancelling on the first progress line stops the pass promptly (75 ms) and reports `ok=False reason="cancelled" method="cancelled"`. The extraction side behaves; B20's claim is about the *caller* turning `cancelled` into a failure and taking the ffmpeg fallback, which lives in `SubSyncService` and still needs its own check. |
| **B13** | low | **measured at remux scale and refuted - closed with the numbers** (`tests/backend/b13_probe.py`, 2026-09-15). The row's "4 MB window + up to 384 MB of prefetched ranges ... gigabytes of prefetch on a batch" was a code bound, never an observation; the triage's fixture was a 300-cluster file. Measured against 30 GB sparse remux fixtures (6 000 clusters, 2 tracks, 4 000 cues, subtitle blocks late in their cluster behind 12 frames), sampling `GC.GetTotalMemory` every 10 ms while the real extractor ran: **peak managed heap 10,3-12,4 MB for one lane and 59,4-86,9 MB for eight at once**, against the row's 384 MB *per reader*; working set 55-127 MB and private bytes 116-357 MB for the process. Slow storage (the rig's shim, 10 ms per read round trip) did not change the figure at all - 86,8 MB for 8 lanes on local disk against 79,6 MB on the shim - it changed the wall clock (1,3 s against 21,7 s for the same eight passes), i.e. that profile costs waiting, not memory. The reader does hold its whole fetch (3 999 ranges / 4 735 744 B, exactly the 4 736 000 B its plan priced) and nothing is retained after a pass (settled heap back to the 0,1 MB baseline). The cluster-walk route (no cue index, which is where the 16 MB chunk lives) measured lower still: 10,3 MB for one lane, 59,4 MB for eight. Derived worst case from the code's own ceiling: min(plan fetch, `ReadPolicy.MaxPrefetchBytes` 64 MiB) + 4 MB window + 16 MB walk chunk ~ 84 MB per lane, ~0,7 GB at eight lanes, only if one pass's merged fetch ever reached the whole ceiling. **No cap was added: there is no measured improvement to buy**, and the existing ceiling is the number that bounds it. What this does not cover: a batch wider than eight lanes, and storage slower than the shim's 10 ms/read. |
| **B2 / B4** | high on paper | **not reproduced - likely already fixed** | the shared pass produced exactly the same cue count as per-track extraction (10/10/10) on the grouped, half-patched and 3-track fixtures. B4 is E2's root cause (`ParseCueRefs` not resetting per `CueTrackPositions`), fixed in 2.0.26; B2 looks like the same defect seen from the shared pass. Recommend closing both after one run on the D17 mixed-index shape. |
| **S7** | medium | **stale for extraction** | `MkvExtractionStats.StorageProbeMs` is assigned from `policy.MsPerCall` - the pass's own reads - so the extraction path no longer probes the storage; only the field name survives. Whether the *queueing* layer still probes needs a check in `SubSyncService`/`WavePolicy`. |
| **B10** | low | **verified, fixed** | `FinaliseStats(stats)` was called before `stats.ReadCalls` and `stats.TotalMs` were assigned (`MkvSubtitleExtractor` 345-347 and 359-361), so the summary it builds cannot carry them. **Half of it bit**: the block rate was 0 on every extraction that read a file (`blocks=10 totalMs=0.8 perSecond=0.00` on each of the four fixtures) because `TotalMs` is assigned nowhere else, while `ReadLatencyMs` survived by accident - the cue-indexed loop's live-counter line fills `ReadCalls` as it goes, so ms/read was right while blocks/s was not. Fixed by assigning both counters before `FinaliseStats` at both call sites; the check asserts the field equals `SubtitleBlocks / (TotalMs / 1000)` exactly (15908.37 / 8221.66 / 14120.30 / 10137.88 blocks/s after, 0.00 before) and the redundant fallback in `ToString` is removed with it (same number, no log text moves). **Correction to the read-out that proposed fixing this row**: the field evidence offered there - `actual 0.81 MB/197 read(s) (1888.7 ms)` beside the lane's `209 reads, 4193 ms` on one Thunder S01E03 pass - is **not** this defect. The plan line's ms is `Price() = calls x msPerRead + bytes / bytesPerMs`, a priced estimate rather than a measured time (197 x 9,588 = 1888,8 ms, the profile's own figure), and its read count is the planned phase's delta, not the pass total: two scopes and one estimate, both correct. | `f38ce6e`; before 4 failures, after 513 checks green. |
| **B25** | medium | static | `if (payloadRead < payloadLength) payload = payload.AsSpan(0, payloadRead).ToArray();` - a short read becomes the cue text instead of a refusal. A healthy local file cannot produce one, so this needs either a fault-injecting shim or the user's share to hit it. |
| **B24** | low | static | the per-pass dedup key is `position * 31 + cueRef.RelativePosition` - a hash rather than a pair. A collision needs Δposition = k and Δrelative = -31k, i.e. cluster positions within ~1 KB with relative offsets ~31 kB apart; possible in principle, and it drops a cue silently when it happens. |
| **B12** | medium | **verified and fixed** | the unbounded stores are `ReferenceStore.Entries`, `SubtitleCache.Memory`, `SharedExtractionStore.Consumers` and `SweepState` (the last two have their own rows). Extraction-adjacent: `SharedExtractionStore` is what the shared pass publishes into. All four are now bounded and swept during a run - see the Tier 4 row for what each was and the checks that hold them. |

Everything else open in Tier 4 is GUI, settings, API surface, job/queue/process handling or file writing
(the D and F series, B1/B3/B5/B6/B14-B19/B22/B23/B26-B30, S5/S12-S14) and does not touch the extraction or
read-policy path.


## Tier 4 — the rest of both matrices, layout, hygiene, and the audit's unproven static leads (verify first: refuting one is a real result) (68 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| closed | **B1** | low | Replace mode can destroy the original subtitle with no rollback | **Not real.** The write path backs up before it overwrites and rolls back on failure. `Services/SubSyncService.cs:5211-5213` copies the original to the backup first (`File.Copy(originalPath, backupPath, overwrite: false)` - it fails rather than clobbering an existing backup) and only then `:5215-5217` copies the synced file over the original; `:5140-5156` is an explicit ROLLBACK that restores the original and, if the rollback itself fails, logs "ROLLBACK FAILED ... Backup file preserved ... Do NOT delete the backup - it's the user's last resort"; and `NextBackupPath` (`:5223-5231`) keeps each replace's own copy at `*.bak.subsync`, a name Jellyfin does not offer as a track. Closed as refuted. Residual, worth one clause rather than a row: the backup lives on the same volume as the original, so a volume-level failure takes both. |
| done | **B31** | low | An unguarded scheduler loop: one exception inside `PumpAsync` ends it, and a stall would have no line in the plugin log. **Fixed and proved by driving the real loop with failing passes.** The pump's body is now `PumpOnceAsync(inFlight)` and the loop is `RunPumpLoopAsync(keepGoing, body, onFault)`: a pass that throws is counted, written to the plugin log (`pump: pass failed (IOException: the media share went away), fault #N; the queue keeps running`) and to Jellyfin's log, and the loop carries on after a pause that doubles from 250 ms to a cap of 8 s, so a pass that fails immediately cannot spin. | 7 harness checks: 3 failed passes leave the loop alive with every pass run and every fault delivered as the exception that happened; passes that succeed record nothing; a loop told not to continue runs no pass; the pause is 250/500/4000/8000. Writing them exposed a defect in the fix itself - the fault handler threw `ArgumentNullException` when a service has no logger, i.e. the handler would have ended the very loop it protects - so it now counts first and writes each log target under its own guard, driven by a check that calls it with no logger at all.Shipped in 2.0.50 (beta) and checked as an installer sees it: the catalog reports 2.0.50.0, the 42 501 202-byte zip's MD5 is `1a9af1990606d6db5d31c4e148dc4671` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.50.0 with the bundled ffsubsync inside. Verification note for the assembly: the new log lines are present as **UTF-16** string literals (`teardown: tracked=`, `killedDirect=`, `pump: pass failed (`, `prefetch: `, `no fallback attempted`), while type and member names such as `ExceptionDiagnostics` are UTF-8 metadata - a plain ASCII byte search of the DLL reports the first group as missing and is simply the wrong test. |
| done | **B10** | low | `FinaliseStats` runs before `ReadCalls`/`TotalMs` are assigned, so the summary's derived ms/read and blocks/s cannot see them | `f38ce6e`: reproduced on the four fixtures (block rate 0 on all, `perSecond=0.00`), fixed by assigning the counters first at both call sites, and the redundant blocks/s fallback in `ToString` removed. The ms/read figure was already right - the cue-indexed loop's live-counter line fills `ReadCalls`, so only the total-time half bit. 513 checks green. |
| done | **B11** | low | The metadata-scan route's progress line never left 0,0 MB: it printed the stats field that only the cue-indexed loop fills, while this is the route that reads most of the file and the one a user watches longest. Fixed: the line takes its numbers from the reader the pass is reading through (kernel counters where the platform provides them, the reader's own count otherwise) and brings the pass's own counters up to date with it. | `ba795d0`. Reproduction and verification on the same fixture (`tests/backend/p4_probe.py b11-progress`, 520 clusters): before `scanning clusters (400 read, 0,0 MB, 200 subtitles found)`, after `scanning clusters (400 read, 1,6 MB, 200 subtitles found)` with the pass reporting 2 155 817 bytes over 528 reads; the cue-indexed route's line is unchanged (`reading subtitle 260/260 - 0,6 MB, 588 reads`). 494 checks green. |
| done | **B12** | medium | Four planner/cache dictionaries grow without bound | **Read against the code, and three of the four were real.** `SubtitleCache.Memory` was the worst: a `ConcurrentDictionary` of every extracted subtitle's *text* that `Prune()` never touched (it pruned the disk layer only) and `Clear()` emptied wholesale, so a server left running accumulated the text of every track it had ever extracted - including the new key a re-encode creates - at a few hundred kilobytes each. It is now bounded on both axes (`MaxMemoryEntries` 512 and `MaxMemoryChars` 16 M characters ~ 32 MB of text) with least-recently-used eviction, its size is reported by `Describe()`, and `Prune()` drops the memory entries whose file it removed (an evicted entry is still answered from the disk layer - checked, so eviction costs a file read, not the data). `ReferenceStore.Entries` and `SharedExtractionStore.Consumers` are removed by the job that created them, which is fine until a job ends without reaching that call - a task that never unwound after a kill or a stall stop, or a job context evicted while its reference was reserved - and then both the entry and its per-file directory stayed until the next plugin start: `ReferenceStore.SweepOrphans(isLive)` is new (plus a 512-entry backstop), the existing `SharedExtractionStore.Cleanup(isLiveJob)` is now *called* during a run, and both run from `SweepLongLivedStores()` - on the scheduler's pass and on the 30-minute cleanup timer, throttled to one directory walk every 5 minutes, with liveness read from the job table so a reference is never removed from under a running ffsubsync. `SweepState` was bounded only when the file was *read back* (`MaxEntries` 5 000 on load), so a sweep of a larger library grew the in-memory dictionary past its own bound and only came back inside it after a restart; it now trims the least recently touched records as they arrive. | `tests/backend/b13_probe.py` for the sibling row; 10 new suite checks: the entry bound with the newest entry surviving and the evicted one still answered from disk, the character bound with 4 x 6 MB subtitles, the prune dropping a memory entry whose file is gone, an orphaned reference entry *and its directory* removed while a live one is kept, an orphaned shared directory removed and its consumer count cleared, and the sweep state staying inside its bound while 5 060 records arrive with the newest one surviving.  Shipped in 2.0.46 (beta) and verified as an installer would see it: the beta catalog reports 2.0.46.0, the 42 493 447-byte zip's MD5 is `74a2fdcda2b5aab4d44565765fe2004e` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.46.0 with the bundled ffsubsync inside. |
| closed (refuted by measurement) | **B13** | high | Per-reader memory: 4 MB window + up to 384 MB of prefetched ranges, which with a worker pool would be gigabytes of prefetch on a batch - an out-of-memory kill mid-run. **Measured and refuted at remux scale** (`tests/backend/b13_probe.py`, added by this work, and run in the suite as `--check`): peak managed heap 10,3-12,4 MB per lane and 59,4-86,9 MB for eight concurrent passes over 30 GB sparse remux fixtures (6 000 clusters, 4 000 cues, two routes - cue-indexed and cluster-walk - plus the shared multi-track pass), sampling every 10 ms; the fetch a reader holds is exactly the plan's own 4 735 744 B against a 64 MiB ceiling in `ReadPolicy.MaxPrefetchBytes`, and after the passes the heap is back at its 0,1 MB baseline. Slow storage (10 ms per read) moves the wall clock (1,3 s -> 21,7 s for eight lanes) and not the memory. Worst case implied by the code is ~84 MB per lane. No cap added - there is nothing measured to improve. | `tests/backend/b13_probe.py --all-shapes`; evidence `.tests-work/b13-memory.json`; 7 suite checks that assert the ceilings the measurement justifies.  Shipped in 2.0.46 (beta) and verified as an installer would see it: the beta catalog reports 2.0.46.0, the 42 493 447-byte zip's MD5 is `74a2fdcda2b5aab4d44565765fe2004e` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.46.0 with the bundled ffsubsync inside. |
| done | **B14** | medium | `Dispose` leaves child processes and lanes running against the share. **Fixed, and the fix was measured against a running server with work in flight.** The teardown now reads the tracked set *before* it cancels anything (a count taken afterwards says "nothing was running" about a run that was), cancels every run token, kills the trees it still tracks, waits a bounded 5 s for the lanes and the pump, clears the process-wide registry, and reports what it found: `teardown: tracked=2 stopped=2 killedDirect=0 exitedDirect=0 aliveAfter=0 tasksDone=4/4 trackedLeft=0`. Writing that probe found a second hole: `RunProcessCaptureAsync`, the fifth process runner (`ffsubsync --version`, `python3 -m venv`, `pip install`, `apt-get install`), started children that were never registered, so neither a kill nor a teardown could see them - every runner registers now (a check compares the counts: 5 registered of 5 started). | Harness: a tracked real child is killed and counted as exited, a 300 ms wait against a 60 s task reports 0 finished in 300 ms, an already-finished task counts, and the registry empties. Integration (`tests/backend/b14_teardown_probe.py`, rig with a run in flight): 2 children alive at the signal, `tracked=2 stopped=2 aliveAfter=0`, and nothing of the rig left running on the host. Honest reading of the numbers: those two were stopped by their own runner's cancellation callback (`killedDirect=0`), so what the teardown proved is that nothing survived - the direct kill is exercised by the harness check with a real process.Shipped in 2.0.50 (beta) and checked as an installer sees it: the catalog reports 2.0.50.0, the 42 501 202-byte zip's MD5 is `1a9af1990606d6db5d31c4e148dc4671` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.50.0 with the bundled ffsubsync inside. Verification note for the assembly: the new log lines are present as **UTF-16** string literals (`teardown: tracked=`, `killedDirect=`, `pump: pass failed (`, `prefetch: `, `no fallback attempted`), while type and member names such as `ExceptionDiagnostics` are UTF-8 metadata - a plain ASCII byte search of the DLL reports the first group as missing and is simply the wrong test. |
| done | **B15** | low | Logging while holding the queue lock | **Confirmed, fixed in 2.0.54.** Read against the code, the queue lock is held by sixteen critical sections and exactly one of them wrote a log line: the duplicate-queue path in `EnqueueSyncTimed` logged inside the lock, while every other section had already been reduced to status updates (the S40 work did that for the cancel paths). It matters because Jellyfin's own sink and the plugin log serialise every writer, so a line written under the queue lock makes the enqueue path - the thing the lock exists to keep short - wait for another thread's disk write. The line is now written after the lock is released, and the guard is mechanical rather than a promise: the suite finds every `lock (_queueLock)` block by matching braces and fails if any body calls `PluginLog.` or `_logger.`, so a log line added to a critical section later cannot pass unnoticed (`B15:` in `tests/run_checks.py`). | Rig (`s7-queue-load`): the longest lock hold in a 27-task burst was under 50 ms and the worst enqueue's own `queueLock=` phase was 0 ms, while jobs of another batch were running. Shipped in 2.0.54 (beta) and checked as an installer sees it: the catalog reports 2.0.54.0, the 42 508 185-byte zip's MD5 is `eb0a8e5d8681e2280694ccb384cdc03c` and matches the published checksum, the packaged `meta.json` and the DLL both carry 2.0.54.0 with the bundled ffsubsync inside (177 members), and the assembly carries `queue duplicate: item=` as a UTF-16 literal - a plain ASCII byte search of the DLL misses it, which is the wrong test for the string half. |
| done | **B16** | medium | `KillAll` busy-waits with `Thread.Sleep` and fabricates its return count. **Both halves fixed, and the count is now measured.** The return value was `Math.Max(processesKilled, runningKilled)` - the larger of two unrelated numbers, neither of which was an exit - so the interface said "N stopped" for processes that were still alive; and the wait was a `Thread.Sleep(100)` poll loop. Each tree asked to stop is now awaited on its own handle with a deadline (`CountExited(toKill, KillWaitMs)` / `process.WaitForExit(3000)`), and the number returned, logged and shown is the number of processes that really exited, with survivors still reported separately. | 2 unit checks with real processes: a killed `sleep 60` counts as stopped (1), and one that is still running after the deadline counts as 0 - i.e. the figure cannot be inflated. Source check: the fabricated expression and the sleep-poll loop are both gone.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| done | **B17** | medium | Completed jobs are evicted whenever the store exceeds 50 entries, so a long run's history disappears while the user is watching it. **Fixed by making retention age-bound with a backstop a real batch cannot reach.** The rule lived inline in `CleanupOldJobs` as `(_jobs.Count > 50)`; it is now `JobsToEvict(jobs, now, JobRetention = 1 h, MaxTrackedJobs = 10 000, isHistoryOnly)`, which the checks drive directly: finished jobs are kept for an hour, restored history rows are never judged as jobs, and only past 10 000 rows are the oldest dropped first. The field's own batch was 2 497 tasks, so the old rule discarded its first ~2 450 rows mid-run. | 3 unit checks (60 fresh completed jobs survive a cleanup pass; past the backstop the oldest go and the newest stay; a restored history row is kept) plus the S25 source check updated to the new form in the same commit.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| open | **B18** | low | Five near-identical process runners, two argument styles | Duplication: maintenance cost, no behaviour change. ranked by blast radius; claim not yet read against the code |
| done | **B19** | medium | `out_time_ms` is converted as milliseconds. **Reproduced with the real ffmpeg, fixed, and the fix is checked against the measurement.** ffmpeg prints the same moment three ways and `out_time_ms` is *microseconds* despite its name - measured on this project's fixture with ffmpeg 7.1: `out_time_us=33000000`, `out_time_ms=33000000`, `out_time=00:00:33.000000`. Dividing the middle field by 1000 turned 33 s into 33 000, which the extraction window clamps to "100 % of the file read", so a pass alternated between the true fraction and a full bar once per progress block and the phase text a user reads could be the wrong one. `ParseFfmpegProgressSeconds` divides it by 1e6 now, and is `internal` so the checks feed it the three measured lines. | 3 unit checks: all three fields agree on 33 s (us=33, ms=33, hms=33), a 30 s line at a 60 s duration is a 0,50 fraction rather than a full bar, and a non-progress line is still -1.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| done | **B2** | low | The shared multi-track pass returns success while a requested track is missing from its results, and nothing in the return says which. **Fixed as visibility, which is what the row asked for.** `MkvExtractionStats.MissedTracks` names every requested ordinal the pass did not produce, `TryExtractMany` puts them in its reason (`no subtitle for track(s) 9`), the lane's log line carries `missedTracks=…`, and the job's extraction note says so - so a gap in a pass can no longer be read as a track that was simply not queued for. | 2 unit checks: a pass that serves both requested tracks reports none missed, and a requested track that cannot be produced is named in the stats *and* in the reason while the other track is still served. Source check: the lane line and the warning name the missed ordinals.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| done | **B20** | medium | A cancelled extraction is reported as a failure and takes the fallback path. **Fixed at every boundary of the extraction chain.** A killed pass and a file that genuinely cannot be read both come back as "no text", and the chain treated them the same: it went on to the next engine - minutes of work on a file the user had just stopped - and ended with the job marked failed. `StopChainIfCancelled(reason, token, where)` is now called at each boundary (the shared pass, the Matroska index pass, the MP4 sample-table pass); it logs `extract: <pass> was stopped (killed by the user) - no fallback attempted` and throws `OperationCanceledException`, which the job runner turns into `Cancelled`. | 4 harness checks, including the coupling that matters: a real extraction with a cancelled token reports `reason=cancelled` and the chain's rule recognises the reader's own string; a cancelled token makes even an empty reason a cancellation; and a genuine read failure ("no subtitle blocks in this file") is not mistaken for a kill. Plus 2 source checks on the boundaries and the rule.Shipped in 2.0.50 (beta) and checked as an installer sees it: the catalog reports 2.0.50.0, the 42 501 202-byte zip's MD5 is `1a9af1990606d6db5d31c4e148dc4671` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.50.0 with the bundled ffsubsync inside. Verification note for the assembly: the new log lines are present as **UTF-16** string literals (`teardown: tracked=`, `killedDirect=`, `pump: pass failed (`, `prefetch: `, `no fallback attempted`), while type and member names such as `ExceptionDiagnostics` are UTF-8 metadata - a plain ASCII byte search of the DLL reports the first group as missing and is simply the wrong test. |
| done | **B21** | low | Prefetch faults lose the real error inside an `AggregateException`. **One shared unwrapper, used where the fault is logged and where it is rethrown.** `BlobReader.Prefetch`'s `Parallel.For` now unwraps its aggregate through `ExceptionDiagnostics.RootCause`: the flattened faults, a cancellation winning over any fault (a moved kill must stay a kill - which is also what B20 depends on), a single fault returned as itself, several collapsed into one exception that names them instead of \"One or more errors occurred.\", logs `prefetch: IOException: the share went away (wrapped in AggregateException) - 1 fault(s) fetching N range(s) of <file>`, and rethrows the real fault so the job's own reporting names it. | 5 harness checks (a fault two aggregates deep returns the inner instance; a wrapped cancellation stays a cancellation; three faults are named and do not carry the aggregate's own sentence; an ordinary exception passes through unchanged; the log line's exact shape) and 3 source checks.Shipped in 2.0.50 (beta) and checked as an installer sees it: the catalog reports 2.0.50.0, the 42 501 202-byte zip's MD5 is `1a9af1990606d6db5d31c4e148dc4671` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.50.0 with the bundled ffsubsync inside. Verification note for the assembly: the new log lines are present as **UTF-16** string literals (`teardown: tracked=`, `killedDirect=`, `pump: pass failed (`, `prefetch: `, `no fallback attempted`), while type and member names such as `ExceptionDiagnostics` are UTF-8 metadata - a plain ASCII byte search of the DLL reports the first group as missing and is simply the wrong test. |
| open | **B22** | low | `MeasureSyncChange` is computed twice per job | Recomputed work, no visible effect. ranked by blast radius; claim not yet read against the code |
| done | **B23** | high | `ClearStaleJobDirectories` recursively deletes anything under the scratch root. **Read against the code: the delete was `Directory.Delete(directory, recursive: true)` for every directory in the cache root that was not named `ref` and not a tracked job - which is every other thing that lives there.** With a misconfigured or repointed root that is data loss outside the plugin's own scratch. It now deletes a directory only when its name is *exactly* a job id (32 lowercase hex characters, the shape `Guid.NewGuid().ToString("N")` produces - `IsJobScratchDirectory`), only when the resolved path is inside the resolved root (`IsInsideRoot`, which rejects `/cache/subsync-evil/…` and `/cache/subsync/../media`), and it logs the refusal when it declines. | 2 unit checks: job ids are recognised and `ref`/`shared`/`logs`/uppercase/31 chars/non-hex/`..` are not; a path outside the root is refused while one inside is allowed. Source check: the delete is guarded by both.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| done | **B24** | low | The cue-indexed loop's dedup key was `clusterPosition * 31 + relativePosition` - a hash of two numbers rather than the pair - so two cue points whose cluster positions are d bytes apart with relative offsets differing by exactly -31d shared a key, and the second was skipped without its cluster ever being read: a subtitle lost silently, with the pass reporting success. | **Reproduced and fixed**: `tests/fixtures/make_collision.py` builds the collision (two clusters 67 bytes apart, the first cue point carrying a relative offset of 31 * 67 = 2077 that points outside its own cluster - the kind of value a foreign index carries - the second sitting at relative 0 where its block is, both clusters holding a block so neither cue point is a "missing block" case). Before: 1 cue, the missing text being exactly the second cue point's; the same file with the first offset nudged one byte (keys differ) gives 2. After (`6903f94`): both give 2 cues. Unchanged sentinels: kopps 829, Sune i Grekland 1019, D17 mixed-index track 803; 494 checks green. |
| open (not reproducible by its stated trigger) | **B25** | medium | A short read does not reach the cue text, because the reader retries it. The truncation path exists (`if (payloadRead < payloadLength) payload = payload.AsSpan(0, payloadRead).ToArray()` - truncated text emitted with no refusal and no marker), but the storage cannot trigger it: `BlobReader.ReadAt` loops until the destination is full or EOF and `ReadNear` falls back to it whenever the request does not fit the window, so a share that answers with a short read is retried rather than propagated. **Verified two ways**: `tests/backend/shortread.c` (libc interposition on pread/pread64, proven to shorten a python `os.pread` by half on every second call) left the plugin's output byte-identical while shortening every second read of the fixture, and the read path itself says so. The one trigger left is EOF inside a payload - a file being copied - and on the fixtures built here the index guard refuses first (`InvalidDataException: index element truncated: wanted 530 bytes at 20973448, got 490`), while cutting a subtitle block in a no-index fixture landed in the sparse video hole. **Kept**: the shim stays in the tree for future use (the fused shim is the only way to model a storage that answers short). If the user wants the residue hardened anyway - refuse instead of truncate - it is a two-line change, not a reproduction. | Shim `tests/backend/shortread.c`/`.so`; probe mode `b25-short-read`; both runs recorded in the Phase 4 report. |
| done | **B26** | medium | numeric values parsed under whatever culture the process happened to have, so a comma-decimal locale could change what a number means. **Fixed in 2.0.53.** The two helpers that read the engine's own scores and offsets already named `InvariantCulture` (from the B19 work) and even normalize a comma the engine printed; the remaining sites were 10 `int.TryParse` calls and one `long.TryParse` that read digits with the current culture. Every numeric parse in the plugin now names its culture, and the guard is exhaustive rather than a sample: the suite fails if a new parse is added without one (it found these 11). **Verified with the process set to a comma-decimal locale** (a clone of the invariant culture with a comma decimal separator, since this container has no ICU data for a named culture such as `sv-SE`): `out_time=00:00:33.000000` still parses to 33, `out_time_us=33000000` to 33, `score: 12.5` to 12.5 and `offset seconds: -2.25` to -2.25, while the same runtime writes `12,5` | harness checks in `tests/run_checks.py` (`B26:`), suite now runs with globalization on by default Shipped in 2.0.53 (beta) and checked as an installer sees it: the catalog reports 2.0.53.0, the 42 506 034-byte zip's MD5 is `8710ac2a5861fa6ff3338c43865f41f1` and matches the published checksum, the packaged `meta.json` and the DLL both carry 2.0.53.0. |
| open | **B27** | low | `Diag` builds its message even when diagnostics are off, and writes to stderr | Wasted work and stderr noise. ranked by blast radius; claim not yet read against the code |
| open | **B28** | low | Magic numbers that should be settings, and one that contradicts the documented policy | Magic numbers and one that contradicts the documented policy: maintainability. ranked by blast radius; claim not yet read against the code |
| open | **B29** | low | Non-volatile `_disposing`, unsynchronised `_lastPassFinishedUtc` | Theoretical tearing on flags read across threads. ranked by blast radius; claim not yet read against the code |
| done | **B3** | medium | Scheduler touches the media share while holding the queue lock | **Read against the code and refuted for the current build - the S40 work had already answered it, and what was missing was anything keeping it that way. Verified and guarded in 2.0.54.** Every `lock (_queueLock)` block was audited by matching braces: sixteen critical sections, and none of them calls a filesystem, process or shared-store API. The planner runs outside the lock by construction (`LogLockHold("pump-plan-outside-lock", …)` is the line that shows it), and the per-file predicates it consults - `MediaVolume.Of` (reads `/proc/mounts` once and caches), `SpeechIsCached` (memoised with a TTL, the fix AGENTS.md records for the 105-424 s enqueue) and `ExtractionReady` - are called from the planner, not from a critical section. What stays inside is job status claims, the run-order list and `SiblingOrdinals`, all in-memory. The guard is the same mechanical audit as B15: `tests/run_checks.py` fails if a `lock (_queueLock)` body ever gains a `File.`/`Directory.`/`SettingsSource.Current`/`SubtitleCache.`/`SpeechCache.`/`Process.` call (`B3:`). `Task.Run` is deliberately allowed there - starting a lane or the pump inside the critical section is thread-pool scheduling, not a storage round trip, and both are started while the lock is held so the reservation stays atomic (the rig measured the whole wake at `wakePump=0 ms`). | Rig (`s7-queue-load`): the worst enqueue's `queueLock=` phase was 0 ms of a 194 ms total while 7 jobs were running. Shipped in 2.0.54 (beta) and checked as an installer sees it: the catalog reports 2.0.54.0, the 42 508 185-byte zip's MD5 is `eb0a8e5d8681e2280694ccb384cdc03c` and matches the published checksum, and the packaged `meta.json` and the DLL both carry 2.0.54.0. |
| partial | **B31** | low | **latent - code verified, no evidence in this batch** | The scheduler's loop is unguarded and a batch that stalls says nothing in the plugin log. `PumpAsync` (`SubSyncService` 2398-2595) has **no try/catch around its loop**: one unexpected exception ends the scheduler, and `WakePump()` (2370-2383) only restarts it when something else wakes it, so if nothing can finish - jobs waiting on extraction while the lane is gone or blocked - the batch freezes with no line in the plugin log at all. The lane has the mirror-image gap: its unexpected-exception handler (1746) writes to Jellyfin's server log via `_logger.LogWarning`, not to `PluginLog`, so a lane failure is invisible in the file this project reads runs out of. **The 2.0.28 batch itself is clean and this gap did not fire**: the server shows no stuck or failed jobs and the files have their subtitles, while the slice read here (52 jobs queued, 38 dispatch lines ending `dispatch: starting 1, running 7, limit 8, queued 1` at 15:09:50Z, 23 completed / 0 failed / 0 cancelled, 29 jobs with no terminal line, last line 15:11:29Z) ends mid-run. The likely reading is that the log read here is a snapshot of the server's log that simply stops while the run continued - the alternative, the plugin's writer stopping, is hard to square with a batch that finished: `PluginLog.Append` re-opens the path per write, has no disable-on-error flag and never throws, the volume has 79 GB free and no rotation happened (1.29 MB against a 4 MB limit). No defect, and no evidence of this gap. **What would still prove a real stall** (nobody has looked): Jellyfin's own log around that moment - a plugin `Extraction lane failed for <file>` warning, or an unobserved-task trace - and the batch's state on the plugin page. **Fix (not written yet, low priority)**: guard the pump loop with a catch-all that logs `PluginLog.Error` and continues, mirror the lane's exception warning into the plugin log, and emit a heartbeat line every few minutes while jobs are outstanding, so a stall reads as a stall instead of as silence. | Log `/subsync-logs/subsync.log` lines 4236-4688 (2.0.28 slice), 0 ERROR lines, no `startup:` line after 15:07:56Z; code at `SubSyncService` 2398-2595, 2370-2383, 1746. |
| **B30** | ? | `DescribeExtraction`'s catch-all relabels anything unknown as ffmpeg | `DescribeExtraction`'s catch-all no longer calls every unknown reader "ffmpeg (whole-file read)" and names cancellation: a cancelled pass used to be logged as an ffmpeg demux (see B7) |
| done | **B4** | high | Cue-point parsing did not reset per `CueTrackPositions`: the cluster and block offsets lived in variables that outlived each of them, so the last track's position in a cue point was adopted for whichever track matched - every cue point then named another track's block. Fixed in 2.0.26 (`5a0ad05`, "A cue index's cluster and block offsets belong to the track they name"): kopps 104 of 829 cue points named another track's block and now none do, with the cue count matching the file's own index and ffmpeg (829, from 832; Sune i Grekland 1019 from 1251). | Closed 2026-09-13 on the reproduction that first exposed it: the D17 mixed-index shape (`patch_cues.py patch ... 5 2`) extracts 803 cues for track 5 alone, identical to ffmpeg, and the 40 duplicate cues the mixed index used to produce are gone (`9bbec18`). |
| done | **B5** | medium | `--version` process is spawned synchronously from a property getter, under the queue lock | **Confirmed, fixed in 2.0.54.** `BundledFfSubSyncVersion` ran `ffsubsync --version` and read the pipe on every `get`, and `EngineIdentity()` - which is what the speech-cache key is built from - called it once per queued job on every planning pass, so the scheduler paid a fork plus a PyInstaller bootstrap per job while the queue waited. The answer is now resolved once per binary by `EngineVersionCache`, keyed on the binary's path, size and modification time: a plugin upgrade changes the key and the new engine is asked once, and there is no TTL to guess at. The resolved *path* is cached with it (a stat and a `chmod` per call before, now at most once a second). The one spawn that remains is reported in the log (`engine identity: … answered 'ffsubsync 0.5.1' (resolved once for this binary)`), and the property no longer spawns anything: it hands the cache the binary's identity. | Rig (`s7-queue-load`), counted *outside* the plugin by a wrapper that records every argv before exec-ing the real binary: **3 `--version` spawns for a 27-task burst queued under load before the fix, 0 in the window afterwards** (the whole run costs one, at the first plan). Harness: 500 questions about one binary spawn it once, an engine that cannot be run is asked once, the key changes when the binary does and not when it does not (`B5:` in `tests/run_checks.py`). Shipped in 2.0.54 (beta) and checked as an installer sees it: the catalog reports 2.0.54.0, the 42 508 185-byte zip's MD5 is `eb0a8e5d8681e2280694ccb384cdc03c` and matches the published checksum, the packaged `meta.json` and the DLL both carry 2.0.54.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`EngineVersionCache`, `EngineVersionProbes`, `CacheSaysMissing`, `RememberCacheMiss`, `StatTtlMsValue`) and its new log lines as UTF-16 literals (`engine identity: `, `settingsUnaccounted=`, `duplicateCheck=`) - a plain ASCII byte search of the DLL finds the names and misses the sentences, which is the wrong test for one half and the right one for the other. |
| done | **B6** | high | A job can stay `Running` forever: it holds its worker slot, its batch never reports itself finished, and a restart is the only way out. **Read against the code, reproduced, and closed - two mechanisms, and the code had both.** (1) `RunSyncJob` sets `Status = Running` on its first line and then did its volume probe, its `SharedExtractionStore.Acquire` and its media-file check *before* its own `try`, so the `FileNotFoundException` a video that vanished between queueing and running raises ended the task while the job stayed `Running`; `RunSyncJobWithContext` had no catch for anything but `OperationCanceledException`, and the pump takes a completed (faulted) task out of `inFlight`, so the slot came free while the job, its batch and its reference tree stayed unfinished for good. (2) The audio-analysis gate was awaited without a token (`await speechGate.WaitAsync()`, `SubSyncService` ~5087) - a wait that can last as long as a feature film's analysis (91 min measured), which neither the user's Kill nor any deadline could end. Fixed: everything that can throw is inside the job's `try`; the gate wait carries the job's token; every exit from a job passes `StuckJobPolicy.Settle`, which fails a job still `Running`/`Queued` with the reason and the last phase it reported; and a stall watchdog (`ReapStuckJobs`) stops jobs that stop making progress - called by the scheduler pump on every pass *and* by the 30-minute cleanup timer, so recovery does not depend on the pump being alive (B31's gap). Its two rules are the measured ones: no child process of its own and no activity for `StuckJobTimeoutMinutes` (default 15) is stopped; a live process is judged by its own output over `WedgedProcessTimeoutMinutes` (default 60), because a demux of an episode over a share or a three-hour film's analysis is quiet while it works - measured before choosing the window, the bundled engine printed 47 stderr lines in a 17,7 s run over a 50-minute episode (largest gap 1,4 s) while this server's own log holds 5 minutes of engine silence inside a 6,7-minute run. A job waiting on another subtitle of the same file is spared while that job is working (that wait is legitimate and can be an hour). A stopped job is failed with the numbers, its token is cancelled so whatever it was waiting in unwinds and its slot comes back, and it is no longer relabelled "Cancelled by user" - the cancellation handler now leaves a job another path already settled alone. | `0801761`, beta 2.0.45; suite 744 checks green with 26 new B6 ones: the idle window either side of 15 minutes, a live process quiet for 59 vs 61 minutes, a waiter spared and then recovered when its holder stalls, the sweep's selection out of a mixed batch (idle + wedged stopped, waiting + finished untouched), `Stop`'s error text/phase/timestamp, the batch's own completion test going from false to true so the run can finish, `Settle`'s idempotence, the windows coming from the configuration (`StuckJobTimeoutMinutes` 15 / `WedgedProcessTimeoutMinutes` 60, clamped: `Stuck-job timeout: 0 minutes is outside 2-240; 2 is stored`), and the process registry's ref-counting and silence bookkeeping. Residual, stated rather than implied: a job whose engine child is alive and printing is never stopped, so a legitimate long analysis cannot be broken - a child that wedges *silently* is bounded by the 60-minute window instead, and a job whose task ignores cancellation keeps its slot even though its state is terminal.  Shipped in 2.0.45 (beta) and verified as an installer would see it: `curl https://makeitmakesencethen.github.io/jellysubsync/beta/manifest.json` reports 2.0.45.0, the 42 491 876-byte zip's MD5 is `280497bd8adca3969ed7b95052220b0d` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.45.0 (one version string in the assembly) with the bundled ffsubsync inside. |
| done | **B7** | medium | `Kill` cannot interrupt a Matroska extraction, so the button does nothing until the pass ends by itself. **The walk route was the gap and is closed.** The cue-indexed loop and the shared pass already checked the token per cue point / per cluster, but `ScanClusters` - the metadata walk, the route that reads most of the file - checked it only every 64th cluster, and a fresh 16 MB sequential chunk was read without asking. Both now check at every cluster boundary: nothing half-read is left behind (a cluster boundary is exactly where nothing is), and the largest single read the extractor makes is refused for a pass that has been cancelled. | 2 unit checks on a purpose-built 1 200-cluster fixture with no cue index (progress lines every 400 clusters, so the cancel lands with 800 clusters unread): a pass started cancelled returns `reason=cancelled method=cancelled`, and one cancelled from its first progress line returns the cancellation with no text having **walked 401 of 1 200 clusters in 7 ms**. Source check: the 64-cluster sampling is gone and the chunk read is guarded.  Shipped in 2.0.48 (beta) and verified as an installer would see it: the beta catalog reports 2.0.48.0, the 42 495 520-byte zip's MD5 is `6ad4af83ede66b6b47c89fb4db61c6d8` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.48.0 with the bundled ffsubsync inside. Two of these fixes cannot be found in the shipped DLL by string search and must not be "verified" that way: B19's divisor is a folded constant and B7's comment never survives compilation - what the packaged assembly proves is that it is the same build the suite ran (assemble version + MD5), and the *behaviour* is what the checks above exercise. |
| done | **B8** | high | A failed ffmpeg extraction was accepted if a partial file exists, so the engine could be handed a prefix of a track as if it were all of it. **Reproduced with the real ffmpeg, fixed, and proven both ways.** The rule was `if (exitCode != 0 && !File.Exists(outputPath)) throw` in `ExtractSubtitleWithProgressAsync`: any nonzero exit with a file on disk passed, and the file it left behind was never removed. The shape that matters most does not even fail - measured on a fixture the suite now builds (a real MKV with a subrip track, cut to 57 % of its bytes, the same command line the plugin runs): ffmpeg 7.1 exits **0**, writes a well-formed SRT holding **17 of its 30 cues** that ends at a cue boundary like any complete file, and reports the truncation on stderr only (`[matroska,webm @ …] File ended prematurely`) - exit code, structure and size all look healthy, which is why the plugin's own `LooksLikeSignsTrack` note was the only hint. A killed run leaves the other shape: a byte prefix of a healthy file whose last cue is cut off mid-block. Now `ExtractionOutputGuard` judges every extraction on four things before anything downstream can see it - the exit code, ffmpeg's own statement about the input (five container-level markers: `ended prematurely`, `Truncating packet`, `Packet corrupt`, `Invalid data found when processing input`, `Error opening input`), the file existing, and the file being a structurally complete SRT with at least one cue - refuses with the measured reason, and deletes the partial: "ffmpeg read a partial file - it reported 'ended prematurely' - so the 17 cue(s) it produced are only part of the track - discarded the partial extraction (917 bytes) at …". An extraction that was stopped (kill, cancellation, the extraction timeout) deletes what it wrote before it unwinds, so a partial cannot sit in the file's shared extraction directory for a later job of that file to read. Damaged *video* frames (`error while decoding`, `corrupt decoded frame`) are deliberately not refusals - they cannot lose subtitle cues, and failing a job over one hands the user a problem they cannot act on; the cost of the choice is a marker list that is container-level only. | `0801761`, beta 2.0.45; suite 744 checks green, 17 of them new: the guard accepts a complete SRT (30 cues, file kept) and refuses a killed prefix, a partial that ends inside a cue **at exit 0**, a failed run with a file present (with a check that the rule this replaced did accept it), the structurally-complete truncated shape, an empty file, a missing file and a malformed cue; every marker is recognised and a clean log has none. Integration through the plugin's own `ExtractSubtitleWithProgressAsync` with the real ffmpeg: the truncated container is refused (message names `prematurely`) and nothing is left at the output path, while the same file whole returns all 30 cues and is kept. ffmpeg is not a suite dependency: without it those two checks print SKIP and the rest still run.  Shipped in 2.0.45 (beta) and verified as an installer would see it: `curl https://makeitmakesencethen.github.io/jellysubsync/beta/manifest.json` reports 2.0.45.0, the 42 491 876-byte zip's MD5 is `280497bd8adca3969ed7b95052220b0d` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.45.0 (one version string in the assembly) with the bundled ffsubsync inside. |
| open | **B9** | low | Kernel-IO baseline is shared mutable static state raced by parallel lanes | A shared static baseline raced by lanes misprices a plan; it costs reads, not correctness. ranked by blast radius; claim not yet read against the code |
| done | **D10** | low | error bodies are inconsistent: validation gives ProblemDetails, other failures give "Error processing request." with no detail. **One shape for every failure.** The 13 refusal sites that answered with a bare string now answer `{ status, title, detail }` (`SubSyncController.Fail`), and an exception that escapes an endpoint is caught by `SubSyncExceptionFilter` and turned into the same shape with its message as `detail` (409 for a state conflict, 400 for an argument, 500 otherwise) - so a client no longer has to read three formats to find out what happened. Verified twice: the filter is called directly in the harness with real exceptions and the resulting body inspected (3 checks), and against the rig two different refusals answer the same three keys (empty batch → 400 "Empty batch: A batch must contain at least one task."; an item this account cannot see → 403 "Not permitted: …"). The page reads `detail` (`problemText`), unit-tested in node against the shipped file. F28 is the same defect seen from the page, so it is closed with this. | Evidence: 3 harness checks, 3 rig checks, a node unit test of the shipped reader, and 3 source checks. Shipped in 2.0.49 (beta) and checked as an installer sees it: the catalog reports 2.0.49.0, the 42 498 188-byte zip's MD5 is `dc38a23f849f4c6ad916150d7c89a99c` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.49.0, and the embedded page script in the shipped DLL carries this fix's own markers (`GET_REUSE_MS`, `inflightGets`, `noteAlreadyQueued`, `problemText`) - the web assets are embedded resources, so they are findable in the assembly where a folded constant would not be. |
| done | **D13** | medium | on a series scope, "Sync selected" issued a *preview* call (`/Subtitles/Batch`) rather than creating a batch. **Refuted by measurement on the current build.** The sheet posts the preview when it opens, because that call is what lists the tracks for the scope; pressing Sync then posts `/SubSync/Batch` and polls the batch it created, which is the run the user asked for. Measured on the series page of the rig with the page's own calls captured: opening "Sync all episodes" → `GET /SubSync/ClientScript`, `POST /SubSync/Subtitles/Batch`; pressing Sync → the preview again followed by `POST /SubSync/Batch -> 200` and repeated `GET /SubSync/Batch/1d0b7865397c41398208ecb76628ae59 -> 200` progress polls. The dialog staying open is the sheet showing that run, not a preview that never became work. | Evidence: `tests/gui/d13-f29-d13-current.json` (`series.apiAfterBulkOpen`, `series.apiAfterBulkPress`, `series.dialogAfterPress`). Side note from the same run: the probe's account is not an administrator, so its kill presses now answer 403 - F2's gate working, recorded here because it is the same file's evidence. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| done | **D14** | low | the injected client script is delivered into the SPA shell (verified), but whether its hooks match the React item page is unverified. **Verified working on the SPA's own item page.** On a movie's detail page the script's entry is in the item menu, opened the way a user opens it (the ⋮ menu, not by searching the DOM first): `Sync Subtitles` with `data-id="subsync"`. Pressing it opens the plugin's sheet, which lists the item's tracks (language, `embedded`, codec) each with its own Sync button, and the sheet's contents came from `GET /SubSync/Subtitles/7dffdc11-…` - so the hook matches the markup the SPA renders and the failure mode the row named (a missing button) does not occur. | Evidence: `tests/gui/d14.json` (`menu.scriptLoaded`, `menu.entryAfterOpeningMenu`, `menu.clicked`, `apiAfterClick`) from `tests/gui/d14-probe.js`, run against the rig on the same build. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| done | **D18** | medium | in Jellyfin Web 12 `EnableInMainMenu` produces no entry in the main app menu. **Measured, and the plugin cannot fix that - so what it *can* control is verified instead.** Read in the installed client's own bundles (both copies of the helper): the client filters `pages.filter(p => p.PluginId === id)` and then takes `mine.find(p => !!p.EnableInMainMenu) || mine[0]` - under Jellyfin 12 the flag picks a plugin's *representative page* for the dashboard's plugin list; it does not build sidebar entries, so no server-side flag can add one. The plugin therefore keeps the flag on the settings page itself (exactly one page sets it) and makes the routes that do work lead there: in a real browser the main menu listed only the library and SyncPlay rows (no plugin entry), while `/web/configurationpage?name=subsync-main` served the page (HTTP 200, 31 598 bytes, `subsyncMainPage` present and the page rendered) and the dashboard page both lists the plugin and links to it. | Evidence: `tests/gui/d7-d18.json` (menuEntries and routes), `tests/backend/d_series_probe.py` (both routes against the rig), and 2 suite checks (one page carries the flag, the pointer page leads to it). Shipped in 2.0.49 (beta) and checked as an installer sees it: the catalog reports 2.0.49.0, the 42 498 188-byte zip's MD5 is `dc38a23f849f4c6ad916150d7c89a99c` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.49.0, and the embedded page script in the shipped DLL carries this fix's own markers (`GET_REUSE_MS`, `inflightGets`, `noteAlreadyQueued`, `problemText`) - the web assets are embedded resources, so they are findable in the assembly where a folded constant would not be. |
| closed (not a defect) | **D19** | low | The golden-section-search checkbox measures 1×1 px. **Verified, and the 1×1 box is the hidden native input, not the control.** jellyfin-web's own stylesheet makes it so on purpose: `emby-checkbox{-webkit-appearance:none;appearance:none;border:none;height:1px;margin:0;opacity:0;padding:0;position:absolute;width:1px}` - the element the user sees and clicks is `.emby-checkbox-label` (`display:inline-flex;height:2.35em;padding-left:2.4em;cursor:pointer;width:100%`), which is where the icon box is drawn. Measured in Chromium with the plugin's own markup and that stylesheet (`tests/gui/p4-d19-probe.js`): the label is **620 x 37,6 px**, `cursor: pointer`, and a hit test at its centre returns the label - i.e. the control is a full-width, ~38 px tall row, and the framerate-correction switch above it measures identically. Caveat on the instrument: measured from the plugin's markup plus jellyfin-web's CSS, without the web client's JS conversion, because the page needs a session and the browser tool refuses loopback addresses; the 1x1 px rule was read out of the shipped stylesheet. | `tests/gui/p4-d19-probe.js`, `tests/gui/p4-d19.json`; the CSS rules quoted above.  Shipped in 2.0.47 (beta) and verified as an installer would see it: the beta catalog reports 2.0.47.0, the 42 494 373-byte zip's MD5 is `517db01d785ee1a8cbffa69ab62b2cd5` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.47.0 - with the new settings surface *inside* the DLL (`is="emby-select" id="ss-encoding"`, `.checkboxContainer.ss-inert`, `syncGoldenSectionState`, the worker wording), since the plugin's web resources are embedded resources rather than loose files in the zip. |
| decision | **D4** | medium | the dashboard config page and the main page edit the same settings with different subsets — two sources of truth | answered 2026-09-11: merge the two settings pages (the dashboard page redirects to the main page's settings); not implemented yet |
| done | **D7** | medium | polling runs flat out (≈1.4 req/s) with nothing happening, including three duplicate calls at load. **Reproduced, fixed, and re-measured in a real browser.** An idle page now asks for 4 requests in 20 s - **0.20 req/s**, one tick per 10 s - and the load window went from 7 requests (three of them the same `/SubSync/Batches`, two inside 500 ms) to 5 with **no duplicate**. The poll is self-scheduled (10 s idle, 2 s while a run is on screen), identical GETs in flight share one request, a fresh answer is reused for 500 ms and any write throws that reuse away (so nothing stale can be read back), and the poll still skips a hidden tab and catches up on the next tab switch. | Evidence: `tests/gui/d7-d18.json` (`idle`, `loadWindow`) plus 6 suite checks including the idle rate being at least four times the busy rate. Shipped in 2.0.49 (beta) and checked as an installer sees it: the catalog reports 2.0.49.0, the 42 498 188-byte zip's MD5 is `dc38a23f849f4c6ad916150d7c89a99c` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.49.0, and the embedded page script in the shipped DLL carries this fix's own markers (`GET_REUSE_MS`, `inflightGets`, `noteAlreadyQueued`, `problemText`) - the web assets are embedded resources, so they are findable in the assembly where a folded constant would not be. |
| done | **D8** | medium | a batch accepts a series `ItemId`, which single sync rejects outright. **One rule, asked by both entry points.** The single-sync endpoint refused a series, season or folder while the batch endpoint accepted whatever it was handed and failed one task at a time later - the same request answered two ways, with the worse answer coming after the work had started. Both now ask `InspectSyncTarget` (which delegates to `ClassifySyncTarget`): a missing item is a 404, and anything that is not a video is a 400 `Not a video` whose detail names the item and what it is and says what to do instead ("pick the episodes themselves, or use the library sweep"). A batch carrying one such task is refused whole, before any job is created. | Harness: 5 checks on the classifier (a series refused by name, a season covered by the same rule, a video accepted, a missing item refused as not found, the refusal pointing at the sweep). Integration: the same series id offered to `/SubSync/Sync` and to `/SubSync/Batch` answers with the identical status, title and detail.Shipped in 2.0.51 (beta) and checked as an installer sees it: the catalog reports 2.0.51.0, the 42 503 689-byte zip's MD5 is `1864088f5d06ffd3cba488c0428afd4d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.51.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`OwnerId`, `CopyForViewer`, `ForViewer`, `EngineIsUsable`, `EngineNote`, `ClassifySyncTarget`, `SyncQueueConflictException`) and its user-facing sentences as UTF-16 string literals (`not a video: pick the episodes themselves`, `already queued by another account`, `does not exist. Correct it in the settings`) - a plain ASCII search of the DLL finds the names and misses the sentences, which is the wrong test for one half and the right one for the other. |
| done | **D9** | medium | duplicate jobs/tasks are accepted with no dedupe (same item+track twice → two jobs). **Measured against a running server, before and after.** Asking twice for the same item and track now queues one job: the first request answers `total=1, alreadyQueued=0`, the second answers `total=0, alreadyQueued=1` in a batch of its own, the first batch still holds exactly one task, and the plugin log names the duplicate it refused (`queue duplicate: item=7dffdc11-… stream=0 existing=…`). The rule is `SubSyncService.FindDuplicate`, consulted under the queue lock: a *finished* job is not a duplicate (re-syncing a track later still works), a different track or item is not, and a library sweep whose tasks were all already queued reports "nothing new" rather than counting them as failures. | Evidence: 4 harness unit checks on the predicate, `tests/backend/d_series_probe.py` (6 checks against the rig), and 3 source checks (the lock, the batch reporting, the page's own note). Shipped in 2.0.49 (beta) and checked as an installer sees it: the catalog reports 2.0.49.0, the 42 498 188-byte zip's MD5 is `dc38a23f849f4c6ad916150d7c89a99c` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.49.0, and the embedded page script in the shipped DLL carries this fix's own markers (`GET_REUSE_MS`, `inflightGets`, `noteAlreadyQueued`, `problemText`) - the web assets are embedded resources, so they are findable in the assembly where a folded constant would not be. |
| open | **F1** | low | `POST /SubSync/Install` lets any authenticated user run apt-get/pip as root | **Guarded elsewhere.** `Api/SubSyncController.cs:334` carries `[Authorize(Policy = RequiresElevationPolicy)]` (`:30` defines it as Jellyfin's `"RequiresElevation"`), so the claim's "any authenticated user" is wrong: Install is admin-only. The privileged call is real - `Services/SubSyncService.cs:6535` ("python3 with venv support is missing; attempting automatic installation via apt-get"), `:6545` `apt-get install -y python3 python3-venv`, `:6548` `apt-get update`, and `:669`/`:680` pip inside the plugin's own venv - and runs as whatever user Jellyfin runs as. Downgraded to hygiene: an admin pressing Install can invoke the server's package manager, which is a large action behind a small button, not an unauthenticated one. |
| done | **F10** | high | No range checks on the numeric settings the engine is given as argv | **Shipped in 2.0.41** (`2ef4982`): no number reaches `BuildFfSubSyncArgs` unvalidated - each is read through `SettingsValidation` (clamped to its documented bound, NaN substituted), and a value that had to be changed is named in the validation notes the page shows. The same change closed D3's path, which is why the two shipped together. Verified by the suite's validation checks (range, NaN, out-of-range note). |
| done | **F11** | medium | Output encoding is free text; the server silently substitutes utf-8 and the UI still says "Saved." **Read against the code, and both halves were real.** The server *did* report the substitution - `Apply` has replaced an unusable encoding and named it since F10 - but the field was free text, so the typo was invited, and the report arrived as a footnote after a save that said everything worked. The field is now a dropdown over exactly the five encodings `SubSyncService.AllowedOutputEncodings` accepts (utf-8, utf-8-sig, utf-16, latin-1, ascii), each labelled with what it is for, so the value cannot be typed wrong in the first place; the server-side replacement stays as the backstop for a hand-edited config.xml and is still named on save (`Output encoding: 'utf8' is not one of ascii, latin-1, utf-16, utf-8, utf-8-sig; utf-8 is stored`). | Unit checks: a chosen encoding is stored as chosen with nothing reported, a typo is replaced *and* reported, and `OutputEncodingOf` returns the stored value (the fallback only for what the engine cannot be given). Page check: the dropdown offers exactly the server's set, parsed from both sides, and the field is no longer an `input`. Integration (real Jellyfin, real API, `tests/backend/p4_settings_probe.py`): `latin-1` is stored and comes back on a fresh read - the retention the page's reload depends on - and `utf8` is replaced with a note.  Shipped in 2.0.47 (beta) and verified as an installer would see it: the beta catalog reports 2.0.47.0, the 42 494 373-byte zip's MD5 is `517db01d785ee1a8cbffa69ab62b2cd5` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.47.0 - with the new settings surface *inside* the DLL (`is="emby-select" id="ss-encoding"`, `.checkboxContainer.ss-inert`, `syncGoldenSectionState`, the worker wording), since the plugin's web resources are embedded resources rather than loose files in the zip. |
| done | **F12** | low | "Use golden-section search" is inert unless framerate correction is enabled. An inert checkbox misleads; nothing is written wrongly. **Fixed on both sides.** The engine was never given `--gss` without framerate correction (`FramerateArgs` emits it only when correction is on), so the switch was decoration: the interface now disables it and marks its row inert (`ss-inert`, 55 % opacity, `cursor: not-allowed`) whenever "Correct framerate mismatch" is off - on load and on every change of that control - with the description saying why, and the tick itself is kept so turning correction back on restores what was set. The server reports the combination too (`Golden-section search: it only does anything together with "Correct framerate mismatch", which is off; it is stored but the engine is not given --gss`), which is what a hand-edited config.xml needs. | Unit checks: `--gss` is absent while correction is off (and the two no-fix flags are there), present alone when it is on, the inert tick is reported and kept, and nothing is reported while the pair is usable. Page/JS checks: the gating function exists, runs on load and on the change event, and is wired at startup. Integration: the note appears in `/SubSync/Settings/ValidationNotes` on a real server. Measured in Chromium under jellyfin-web's own stylesheet: the row's opacity becomes 0.55 in the inert state (`tests/gui/p4-d19-probe.js`).  Shipped in 2.0.47 (beta) and verified as an installer would see it: the beta catalog reports 2.0.47.0, the 42 494 373-byte zip's MD5 is `517db01d785ee1a8cbffa69ab62b2cd5` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.47.0 - with the new settings surface *inside* the DLL (`is="emby-select" id="ss-encoding"`, `.checkboxContainer.ss-inert`, `syncGoldenSectionState`, the worker wording), since the plugin's web resources are embedded resources rather than loose files in the zip. |
| done | **F13** | low | The "Parallel workers" change applies immediately to sync but needs a restart for extraction lanes. **The gap was real and is now closed, not just documented.** The lanes' width is `ParallelWorkers / 2` capped at 3, and `WakeExtractor` read it from `Plugin.Instance.Configuration` - the plugin's in-memory copy, which a settings file edited outside the API never updates - while the sync side reads `SettingsSource.Current()`. Both now read the same source, and saving a configuration wakes the scheduler (`_syncService.ApplySettingsNow()` from the settings API, which wakes the pump and therefore the extractor, logging `settings applied: workers=N lanes=M`), so an increase takes effect at once and a decrease as the running extractions finish. The field's description states both that the lanes take half the number (at most 3) and what happens on the way down. | Unit check: `ConfiguredLaneLimit` is half the worker count capped at the documented 3. Source checks: the controller calls `ApplySettingsNow()` on save, the service exposes it, and `Plugin.Instance?.Configuration?.ParallelWorkers` no longer appears anywhere in the service. Integration (real server): setting the workers to 64 puts `settings applied: workers=64 lanes=3` in the plugin log.  Shipped in 2.0.47 (beta) and verified as an installer would see it: the beta catalog reports 2.0.47.0, the 42 494 373-byte zip's MD5 is `517db01d785ee1a8cbffa69ab62b2cd5` and matches the catalog's checksum, and the packaged `meta.json` and the DLL both carry 2.0.47.0 - with the new settings surface *inside* the DLL (`is="emby-select" id="ss-encoding"`, `.checkboxContainer.ss-inert`, `syncGoldenSectionState`, the worker wording), since the plugin's web resources are embedded resources rather than loose files in the zip. |
| open | **F14** | medium | The scheduled sweep ships with no trigger, yet the UI advertises it | A feature the UI advertises does nothing until the user adds a trigger: silent non-function, which is worse than an absent feature. ranked by blast radius; claim not yet read against the code |
| open | **F15** | medium | Two settings pages that disagree, and sweep knobs exposed nowhere | Two settings surfaces that disagree mean an edit is silently ignored on one side. ranked by blast radius; claim not yet read against the code |
| done | **F16** | medium | the extracted-subtitle cache was undescribed in the interface (the audio cache was described, this one - the larger of the two on a library that has been synced for a while - was not), and the claim of unbounded memory growth was already closed by the B12 work. **Fixed in 2.0.53.** The settings page now describes it (120 days, 512 MB on disk, a bounded number of entries in memory) and shows what it holds right now from the status call the rest of the page uses, and the bounds are pinned by checks that fail if any of them is loosened | harness checks in `tests/run_checks.py` (`F16:`), source pins on the status field, the page and the script Shipped in 2.0.53 (beta) and checked as an installer sees it: the catalog reports 2.0.53.0, the 42 506 034-byte zip's MD5 is `8710ac2a5861fa6ff3338c43865f41f1` and matches the published checksum, the packaged `meta.json` and the DLL both carry 2.0.53.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SubtitleCacheSummary`) and the sentence describing the cache as a UTF-16 literal. |
| open | **F17** | low | The audio-analysis cache's 30-day rule ignores use | Costs re-analysis time, not correctness. ranked by blast radius; claim not yet read against the code |
| open | **F18** | low | `SpeechCache.Prune` still prunes a file pattern that moved elsewhere | Pruning a pattern that moved elsewhere: dead code, no user-visible effect. ranked by blast radius; claim not yet read against the code |
| open | **F19** | medium | `SrtWriter.Render` treats a real 2-second cue as a "guessed" duration | A real 2 s cue rendered as a guessed duration changes what is written to the user's subtitle. ranked by blast radius; claim not yet read against the code |
| done | **F2** | medium | `POST /SubSync/Kill` is global, id-less and unowned. **Half refuted, half fixed - and the fixed half was the dangerous one.** The gate was already `RequiresElevationPolicy` (measured: a signed-in non-administrator gets 403 for every shape of the request, so "unowned" was never reachable), but the *global* part was real: an administrator's cancel stopped **every** run on the server, including another administrator's, with no target in the request and nothing in the answer saying so. A cancel now has to say what it means - `{all: true}` for every run (administrators only), a `batchId`, or a `jobId` - and a request that names nothing is refused with 400 `Say what to stop` before a single token is cancelled; a run that is not queued or running answers 404 `Nothing to stop`. The global case keeps its full effect (lanes and child processes too) because that is what an administrator asking for `all` means, while a scoped stop cancels exactly the work it named and leaves the rest of the queue alone. The owner checks in the rule stay as belt and braces: with the endpoint administrator-only they cannot be reached today, and they are what keeps a future policy change from turning a scoped cancel into a server-wide one. | Harness: 8 checks driving the rule (names nothing; own batch, queued and running parts only; `all` without the permission; `all` as an administrator, excluding finished runs; another account's run by id; an own run by id; a run the plugin queued itself, for both callers; a finished run). Integration (`tests/backend/consistency_probe.py`): non-administrator 403 for `{}` and `{all}`, administrator 400 for `{}`, 404 for an unknown id, and a scoped `{jobId}` that returns `runningKilled: 1` with that run `Cancelled` while its batch keeps running. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| open | **F20** | medium | `MediaVolume.Of` matches mount points by raw string prefix | Mount matching by raw string prefix can attribute a file to the wrong volume, which mis-prices its extraction and mis-sets its walk ceiling. ranked by blast radius; claim not yet read against the code |
| open | **F21** | medium | The response-body swap in the middleware has no restore on every path | A response-body swap with no restore on every path can serve a corrupted page or script. ranked by blast radius; claim not yet read against the code |
| open | **F22** | low | The middleware disables compression for every index page response | Bandwidth only; pages still work. ranked by blast radius; claim not yet read against the code |
| closed | **F23** | low | The only anonymous endpoint is the client script | **Not real as stated.** The row says the client script is the only anonymous endpoint; there are two: `Api/SubSyncController.cs:480` `[AllowAnonymous] [HttpGet("ClientScript")]` and `:572` `[AllowAnonymous] [HttpGet("MainScript")]`, both serving `application/javascript`. No action needed - closed as refuted, with the count corrected. |
| open | **F25** | low | The legacy page still tells admins to inject the script by hand | Stale instructions on a legacy page. ranked by blast radius; claim not yet read against the code |
| open | **F26** | low | One closing `</div>` leaves two settings fields outside their section | A stray closing tag: layout, nothing functional. ranked by blast radius; claim not yet read against the code |
| done | **F3** | high | Item-scoped endpoints have no per-item access check (IDOR, read and write) - see `F3_ACCESS_GOAL_PROMPT.md` | **Fixed in `db8950d`.** `Services/ItemAccess.cs` holds the decision as a pure predicate (`Allows`, `FirstDenied`, `UserIdFrom`) with no Jellyfin types in it, so the suite drives it: all-folders account allows, a folder match at any depth allows, either of Jellyfin's id forms matches, and a missing account, a missing library list or an item with no resolved ancestors all **deny** - fail closed, the same shape as the walk ceiling's "not known to be fast is not fast". `SubSyncService.CanUserSeeItem`/`FirstItemNotVisibleTo` (beside `ILibraryManager`/`IUserManager`, injected at the constructor) resolve it from Jellyfin, and all four endpoints consult it: `GetSubtitles` and `SyncSubtitle` through `CallerMayActOn`, `CreateBatch` and `GetSubtitlesBatch` through `RefuseInvisibleItems`, which refuses the whole request so a partial batch is never queued. Refusals are logged with the item id (that log is admin-only) and answered with a reply that does not confirm the item exists. **Not** made admin-only on purpose: that would remove the feature from users entitled to it, which is a worse trade than the bug. 598 checks green, including a source-level check that each of the four endpoints still asks. Field check owed: the admin dashboard and the item-menu action verified unchanged on the server before this ships (same treatment as the walk ceiling's field run). |
| done | **F30** | low | the sweep history grew past its own cap until the next restart. **Verified rather than changed in 2.0.53** - the cap (5,000 entries) is applied on load as well as on every write since the B12 work, and the check drives that path with an oversized state file rather than trusting the constant: a 5,750-entry file loads to exactly 5,000 entries, the most recently touched end is what is kept, and the rest is dropped | harness checks in `tests/run_checks.py` (`F30:`) + source pin on the load-time trim Shipped in 2.0.53 (beta) and checked as an installer sees it: the catalog reports 2.0.53.0, the 42 506 034-byte zip's MD5 is `8710ac2a5861fa6ff3338c43865f41f1` and matches the published checksum, and the packaged `meta.json` and the DLL both carry 2.0.53.0. |
| done | **F4** | medium | Server-wide job history and the plugin log expose other users' runs and server paths, with no admin gate. **Fixed, and the second half of the claim was refuted first.** The log half was already wrong: `HttpGet("Log")` carries `RequiresElevationPolicy`. The history half was real - `Jobs`, `Jobs/{jobId}`, `Batches` and `Batch/{batchId}` sat under the class-level `[Authorize]` alone and returned jobs carrying `ItemId` and `OutputPath`, so any authenticated account read every other account's activity and the server's absolute media paths with it. A job now records `OwnerId` (carried by every queue path: single sync, batch, and each task's failed row), and the run list, a run by id, the batch list, a batch by id and cancelling a batch are all scoped: an administrator sees everything, anyone else sees only the runs their own requests created, a job with no owner - the sweep's work, or a row restored from a history written before jobs recorded one - is the administrator's, and a run that is not the caller's answers exactly like one that does not exist. Paths are removed for non-administrators through a copy (`ForViewer`/`CopyForViewer`), so answering a viewer never edits the tracked job the sweep and the history read, and absolute paths inside error and outcome text are replaced with `<path>`. Writing the probe exposed a second defect this created: D9's duplicate rule returned another account's queued job to the caller, handing over an identifier they cannot read (their list omits it and asking for it answers 404); work another account has queued now answers 409 `Already queued`. | Harness: 14 checks driving the rules (the filter with an owner, an outsider and a null caller; `MaySeeJob`; redaction of a POSIX path with a space, a Windows path, a message with none and a null; the copy property - the tracked job keeps its path while the viewer's copy does not; batch tasks). Integration (`tests/backend/f4_d2_d8_probe.py`): a real non-administrator account on the rig - its run list holds none of the administrator's runs, the administrator's run id and batch id both answer 404, queued work answers 409 with no path or owner in the body, and **the same completed run read by both accounts** gives the non-administrator `OutputPath: null` where the administrator reads `/opt/data/jf12test/media/Helikopterrånet S01E01.swe.SYNCED.srt`. 23 checks, ALL PASS.Shipped in 2.0.51 (beta) and checked as an installer sees it: the catalog reports 2.0.51.0, the 42 503 689-byte zip's MD5 is `1864088f5d06ffd3cba488c0428afd4d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.51.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`OwnerId`, `CopyForViewer`, `ForViewer`, `EngineIsUsable`, `EngineNote`, `ClassifySyncTarget`, `SyncQueueConflictException`) and its user-facing sentences as UTF-16 string literals (`not a video: pick the episodes themselves`, `already queued by another account`, `does not exist. Correct it in the settings`) - a plain ASCII search of the DLL finds the names and misses the sentences, which is the wrong test for one half and the right one for the other. |
| done | **F5** | medium | `POST /SubSync/Batch` accepts tasks that `POST /SubSync/Sync` rejects, and never dedupes them. Closed in two halves: the dedupe half by **D9** (2.0.49 - one job per item and track, reported in the batch view), and the validation half by **D8** (the batch now applies the same target rule as single sync, so a request that would be refused is refused wherever it arrives, with the same status, title and detail). | Evidence: the D9 and D8 rows above, plus the integration check that posts the same series id to both endpoints and compares the answers.Shipped in 2.0.51 (beta) and checked as an installer sees it: the catalog reports 2.0.51.0, the 42 503 689-byte zip's MD5 is `1864088f5d06ffd3cba488c0428afd4d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.51.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`OwnerId`, `CopyForViewer`, `ForViewer`, `EngineIsUsable`, `EngineNote`, `ClassifySyncTarget`, `SyncQueueConflictException`) and its user-facing sentences as UTF-16 string literals (`not a video: pick the episodes themselves`, `already queued by another account`, `does not exist. Correct it in the settings`) - a plain ASCII search of the DLL finds the names and misses the sentences, which is the wrong test for one half and the right one for the other. |
| done | **F6** | medium | "Clear cache" can delete the reference subtitle a running job is using. **Fixed by refusing the clear while anything is reading the cache, because the narrower question cannot be answered.** A running job is handed its reference subtitle as a command-line argument and the audio and feature caches are keyed by a hash of the media path and the VAD settings, so "delete everything except what is in use" is not something the plugin can work out from outside the run; what it can answer is whether anything is using the cache at all. The clear now inspects the live job counts first and answers 409 `Runs are using the cache` naming how many runs are in the way (and how many are queued behind them) instead of deleting a file one of them is reading - a run that loses its reference mid-flight fails for a reason the user did not cause. With nothing running the same request clears and reports what it removed, as before. | Integration (`tests/backend/consistency_probe.py`): with a run in flight the clear answers 409 with the count in the sentence, and once the run has settled the same request answers 200 with the removal counts. Source check: the refusal and its status are wired, with the reason recorded in the code. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| done | **F7** | low | scratch directories left behind by a killed job or a crash were never swept: the only thing that ever removed one was the operator-facing "clear the cache" action, so they accumulated on any server where jobs had been cancelled or the server stopped mid-run. **Fixed in 2.0.53.** The sweep that prunes the caches now also removes orphaned scratch directories, and the plugin does it once while it loads; the safety rule is unchanged (only directories whose name is a job id are candidates, and a directory belonging to a job that is queued or running is never touched). **The first version of this fix was wrong, and the rig showed it:** it asked whether the id was merely *known*, so every interrupted job - whose record the history file carries - kept its directory; a job that finished, failed or was cancelled must not protect its directory, since that directory is the thing being cleaned up. **Measured on the rig, where the defect's own evidence was still lying around:** `/opt/data/jf12test/cache/subsync` held 3 stale job directories from earlier runs; after loading the plugin that root held none, the directories planted for the probe were gone with them (`startup: removed 5 orphaned job scratch director(ies)`), a directory planted while the server ran went on the next maintenance pass with no button pressed (log: `sweep: removed 1 orphaned job scratch director(ies)`), a directory whose name is not a job id survived, and `ref/` beside them was untouched | `tests/backend/f7_probe.py` (`f7_results.json`), harness checks + source pin in `tests/run_checks.py` Shipped in 2.0.53 (beta) and checked as an installer sees it: the catalog reports 2.0.53.0, the 42 506 034-byte zip's MD5 is `8710ac2a5861fa6ff3338c43865f41f1` and matches the published checksum, the packaged `meta.json` and the DLL both carry 2.0.53.0 with the bundled ffsubsync inside, and the assembly carries this batch's sentences as UTF-16 literals (`startup: removed … orphaned job scratch director(ies)`, `sweep: removed … orphaned job scratch director(ies)`). |
| done | **F8** | ? | The GUI can build batches the API rejects (1000-task cap, no chunking) | Done in `7f474d5` ("F8: the page splits a selection the API would refuse"): `Jellyfin.Plugin.SubSync/Web/subsyncMain.js:1107` `var BATCH_CHUNK = 1000;` with `postBatch` splitting at `rows.length <= BATCH_CHUNK` (`:1110`) into sequential parts, and `tests/run_checks.py:2535-2540` pins the page's cap to the controller's own bound (`request.Tasks.Count > 1000`) and requires the two call sites. Verified 2026-09-14. |
| open | **F9** | medium | `POST /SubSync/Subtitles/Batch` bounds items but not the expansion | The same bound F8 fixed on the page, reached through the API: a selection can expand past the task limit and be refused or partially enqueued. ranked by blast radius; claim not yet read against the code |
| done | **S5** | medium | a bitmap track is missing from the track list instead of refused with a reason. **Verified against the code, then fixed: it was a hard filter.** `ListSubtitles` carried `.Where(s => !LanguageSupport.IsImageBased(s.Codec))`, so a file with a bitmap subtitle among its tracks showed one track fewer and the missing one could not be asked about at all - and queueing that stream index by hand (which the API accepts) failed later inside the engine with a message about a subtitle it could not read. The filter is gone: the track is listed like any other with `UnsupportedReason` set to a sentence naming the format (`This track is PGS, an image subtitle format: the engine aligns text, so it cannot be re-timed here. Choose a text track (srt, ass, webvtt) for the same language, or convert this one first.`), and queueing it is refused with the same sentence at enqueue time instead of failing mid-run. | Harness: 4 checks on the refusal sentence (a bitmap format is named, the other bitmap formats are covered, a text track and a null codec are not refused, the sentence says what to do instead). Source checks: the filter is gone, the sentence is produced in one place, and the enqueue path raises the same one. The rig carries no bitmap track to drive end to end, which the checks do not pretend otherwise. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| done | **S7** | medium | queueing under load costs ~212 ms and each job re-probes the storage | **Measured, then fixed in 2.0.54.** The second half named a mechanism and the mechanism was real: `SettingsSource.Current()` stat-ed the settings file on every call, and the enqueue path calls it several times per task (the enqueue itself for the mode, the worker count, the extraction lanes' width, and the planner's own passes). Measured on the rig on *one build* - `SUBSYNC_SETTINGS_STAT_MS=0` restores the pre-fix behaviour, so the comparison is not between two builds - a burst of 27 tasks queued while another batch was running cost **484 storage probes (~18 per queued task)**; with the default 250 ms window the same burst costs **one or two**. A save through the settings API resets the cache outright, so an applied setting never waits for the window. The scheduler's other per-job storage question (`ExtractionReady` → `SubtitleCache.TryGet`, asked for every queued job on every pass) is now memoised on the miss path for two seconds: a remembered miss cannot hide a track that has arrived, because the lane's hand-over marks it ready first and that is checked first. | Rig (`s7-queue-load`): 27 tasks queued while 4-16 jobs of another batch were running - **median 4 ms, p95 7 ms, worst 8 ms per task**, `queueLock=0 ms`, longest critical-section hold 3 ms (`pump-snapshot`), settings probes 484 → 1, engine spawns 0, and the assertion is that a burst costs at most one storage probe per queued task. The instrument needed three corrections before its numbers were trustworthy, all now in the tree: `enqueue slow:` separates `settingsRead=`/`jellyfinLog=`/`duplicateCheck=` (a lock wait inside the `settings` phase had read as a 185 ms storage read), the residue is named `settingsUnaccounted=` (time the thread was not running, not plugin work), and the scenario selects its log lines by timestamp and by the burst's own stream indices rather than by a line offset - the plugin log is appended and rotates, which is what made an earlier reading count 216 lines for 27 tasks. Shipped in 2.0.54 (beta) and checked as an installer sees it: the catalog reports 2.0.54.0, the 42 508 185-byte zip's MD5 is `eb0a8e5d8681e2280694ccb384cdc03c` and matches the published checksum, and the packaged `meta.json` and the DLL both carry 2.0.54.0 with the bundled ffsubsync inside. |
| decision | **S8** | medium | an in-sync subtitle synced against the audio is moved and written as a success | answered 2026-09-11: an audio-only result is reported unverified and no sidecar is written unless the reference was a subtitle track; not implemented yet, and it needs the external-sidecar case settled first (see the report's §S8 note) |

## New findings this session (2026-09-11, evening)

| state | id | sev | what | evidence |
|---|---|---|---|---|
| done | **S12** | medium | `/SubSync/Subtitles/{id}` hides the plugin's own `.SYNCED.` sidecars, but `/Sync` accepts an index that resolves to one and syncs it again - it wrote `Helikopterrånet S01E01.SYNCED.ukr.SYNCED.srt`. **Both halves fixed: listed, and no loop.** The list hid its own outputs (`.Where(s => !IsOwnSidecar(s))`) while the queue accepted the index, so the list and the queue disagreed about what a track is; the sidecar is now listed and flagged (`IsPluginOutput`), so a file the plugin produced is labelled rather than invisible. And the loop is closed at the name: `SrtWriter.StripSyncedMarker` removes a marker the plugin already wrote - any fields after it included - before `SyncedTargetName` adds one, so re-syncing `Film.S01E01.SYNCED.ukr.srt` updates that file instead of writing `Film.S01E01.SYNCED.ukr.SYNCED.srt` into the library, which no player associates with the episode and no sweep removes. The legacy hyphen form is stripped too. | Harness: 5 checks on the naming rule (a marker is stripped, `SYNCED` inside a longer word is not, the hyphen form is stripped, our own output cannot nest a second marker, an untouched subtitle still gets its marker, and a language-named sidecar keeps the field form Jellyfin needs). Integration: a copy-mode run wrote a sidecar whose name carries exactly one marker, and the media directory gained no file with two. Source checks pin that the list no longer filters and that one helper names every copy-mode output. Shipped in 2.0.52 (beta) and checked as an installer sees it: the catalog reports 2.0.52.0, the 42 505 605-byte zip's MD5 is `e40abfd204c196aaf935d963108fda0d` and matches the catalog's checksum, the packaged `meta.json` and the DLL both carry 2.0.52.0 with the bundled ffsubsync inside, and the assembly carries this batch's members in metadata (`SelectKillTargets`, `KillJobs`, `ActiveJobCounts`, `ImageBasedRefusal`, `StripSyncedMarker`, `SyncedTargetName`, `IsPluginOutput`, `UnsupportedReason`) and its user-facing sentences as UTF-16 literals (`Say what to stop`, `Nothing to stop`, `Runs are using the cache`, `an image subtitle format`, `would delete a file a run is using`). | |
| done | **S13** | high | the change that makes a bulk run finish was itself blocked by a harness defect: `tests/backend/slowread.so` did not exist, so the first "slow profile" run of this session silently measured the fast path (the loader warns and continues) | Stale. `tests/backend/slowread.so` exists in the tree (16272 bytes) and `tests/backend/start-server.sh:26-31` builds it when missing, so the harness defect the row describes is gone; the shim was used for every concurrency measurement on 2026-09-14 (`tests/backend/s28-*.sh`, `slowread-fabji.env`). Verified 2026-09-14. |
| fixed (contract half) / open (stability half) | **S14** | medium | `MediaStream.Index` counted every stream in the file, video and audio included, and the enqueue path stored it as the subtitle's *ordinal* - the number the extraction lane and the subtitle cache count subtitle tracks by. The two numbers agree only when a file's subtitles are its first streams. **Fixed at the enqueue boundary** (one definition, `SubSyncService.EmbeddedSubtitleOrdinal`, also used by the run-time resolver), shipped in **2.0.28** (`b0304f6`). **Still open**: a job addresses its track by the `Index` captured at queue time, so numbering that shifts *between* queueing and running (the sidecar case above) still resolves to whatever stream holds that number by then; folding the track's identity - language, codec, position - into the job context is the fix, and it is not in this change | `9b09678`; reproduction and numbers below |
| answered | **S3-conflict** | high | `GOAL_PROMPT`'s hard rule "the plugin never refuses a job" vs `AGENTS.md` + this plan's S3 line ("implement `MaxSubtitleReferenceOffsetSeconds` as a refusal"). **Decided by fabji 2026-09-11: the refusal stands, as `AGENTS.md` and the fix plan specify.** The never-refuse rule applies where it was meant to: a reference that cannot be *built* falls back to the audio (S11), a reference that exists and is provably from another cut is refused (S3) | `9c9514e`; the two checks rewritten in the same commit assert the refusal |

### A8 — the measurements (2026-09-12)

- **Slow profile** (the aggregate number the plan asks for), 50 tasks of the 2.38 GB episode, cold cache each
  time, one setting at a time: **w=1 2277.2 s · w=2 667.6 s · w=4 786.1 s (4 failed — S19) · w=8 409.9 s**.
  Transcript: `tests/backend/a8-slow-sweep-transcript.md`.
- **Fast profile**, same file, same settings, from the second sweep: **w=1 37.0 s · w=2 18.8 s · w=4 18.2 s ·
  w=8 16.5 s**, each 49 Completed + 1 Refused, **0 failed** (`acceptance-A8-fast-w*.json`).
- A5 (whole series, 100 tasks) 34.9 s and A6 (one season) 30.7 s: 98 Completed, 2 Refused, 0 Failed each.
  A7 (hand-picked, 8 non-adjacent tracks from each of two episodes): 16 tasks, 14 Completed, 2 Refused,
  0 Failed, 7.8 s.

## New findings this session (2026-09-12, Matrix B / GUI)

| state | id | sev | what | evidence |
|---|---|---|---|---|
| fixed | **D20** | critical | the plugin's own page ran nothing in Jellyfin 12: an inline `<script>` in an injected plugin page is never executed. The page rendered with every control in place, the status line stayed on "Checking status...", the library list stayed empty and the page made **no request of any kind** | browser probe: `tests/gui/page-script-probe.js` + `.json`; the same script attached by script runs (window.__ssMainScriptRan === true; then requests to `/SubSync/InstallationStatus`, `/SubSync/Configuration`, `/SubSync/Batches`, `/SubSync/Active`) |
| fixed | **D21** | high | with its script running, the page died on `Uncaught ReferenceError: ApiClient is not defined` at its first setting read — Jellyfin 12 never defined `window.ApiClient` in this instance (still `undefined` 22 s in). Fixed in the page: token from the web client's stored credentials, own URLs, own `GET/POST /SubSync/Configuration`, `GET /Users/Me` for the signed-in user, and a status line that reports a failed start instead of a dead surface | pageerror from `page-script-probe`; `/SubSync/Configuration` answers 200; page-interactive probe: status line filled, libraries listed |
| fixed | **D22** | high | the fix in D20's code path (attaching `/SubSync/MainScript` from `subsync.js`, the client script the middleware injects, which is the path that already works for the ⋮ menu) is implemented and did **not** yet make the page start by itself — the tag's script is fetched but not executed, while the same script attached by script runs. Next step: the Network → Initiator column for that request, and whether the web client replaces the injected page element | `tests/gui/page-script-probe.json`, third run; source checks in `tests/run_checks.py` |
| open | **D23** | medium | (now blocked by S19, not by the page) | F29's two-press kill and the mirrored-run-box fix F29's two-press kill and the mirrored-run-box fix are still **not verified in a browser**: both need a run the page can see, and in the harness the page's own session got a 403 on one call and the batch queued from it never appeared to the page (button stayed "Cancel", line "Nothing queued."). The probe drives both and is ready to re-run | `tests/gui/page-interactive{1,2,3}.json`, `tests/gui/page-interactive-probe.js` (Ranked by blast radius in the 2026-09-14 triage; the claim has not yet been read against the code.) |
| done | **D13 / D14 / D18 / D19** | — | answered in a real browser: D14 works, D13's audit claim refuted, D18 confirmed, D19 refuted at 1600×1000 | `knowledge/gui-test-report-2026-09-12.md`, `tests/gui/gui-pass-*.json`, `tests/gui/shots/` |

### How D20–D22 were closed (2026-09-12, measured in Chromium)

- **D20 fixed**: the page's script lives in `Web/subsyncMain.js`, served by `/SubSync/MainScript`. Verified
  end to end: the page opens with **nothing attached by hand**, the script runs (`window.__subsyncPageLoaded`),
  the status line fills ("ffsubsync source: … · ffmpeg: … · Workers: 8 in use"), **5 libraries** are listed,
  and the page polls `/Users/Me`, `/SubSync/InstallationStatus`, `/SubSync/Configuration`,
  `/SubSync/Batches`, `/SubSync/Active`.
- **D21 fixed**: the page never asks for `ApiClient` to read or write its settings; it takes its token from
  the web client's stored credentials, builds its own URLs and uses `GET/POST /SubSync/Configuration`.
- **D22 fixed, and the reason is worth keeping**: execution of the injected page's script **stops part-way
  through the file without any error** — a trace at the top and bottom of the script showed the first line
  ran and nothing after the declarations did, with no `pageerror` anywhere. The web client replaces the
  document while the script is still executing, which cuts the remainder off. Moving the start to the top of
  the file (listeners for `DOMContentLoaded`/`pageshow`/`load` plus a `setTimeout(0)`, all registered before
  anything else can go wrong) is what makes the page work. Any future code that must run in this page has to
  start from the top, not from the last line.
- **D23 half verified in a browser**: with a run queued through the page's own session, the mirrored run box
  **appears** — `#ss-runbox` visible, the Cancel control visible, the line reading "8/1 workers · 3/20 · 3 failed"
  (`tests/gui/page-selfstart3.json`) — which is the fix that used to fill a box nobody had shown. What is still
  open is F29's two-press confirmation *in the browser*: the button reads "Kill all syncing" only when there is
  no run the page can attach to, and the probe did not reach that state (it mirrored a run, so its first press
  was a plain cancel, as designed). The source check pins the two-press behaviour; the browser check needs a
  server busy with runs the page cannot attach to.

| state | id | sev | what | evidence |
|---|---|---|---|---|
| fixed | **S19** | high | a bulk run can lose jobs to a missing extraction file: 4 of 50 tasks in the A8 w=4 sweep run failed with `DirectoryNotFoundException: …/cache/subsync/<jobId>/subtitle_15.srt`, and the same shape appeared for streams 13, 14, 19 — the files are written by the shared extraction pass and read from a *job's own* temporary directory, and a job deletes that directory when it finishes (`SubSyncService` 3104 creates `TempPath/<job.Id>`, 3205 reads `subtitle_<n>.srt` from it, 4027 deletes it). Jobs that started later read a directory their predecessor had already removed | `tests/backend/a8-slow-sweep-transcript.md` (the sweep's own output: w=1 2277.2 s, w=2 667.6 s, **w=4 786.1 s with 4 failed**, w=8 409.9 s) + 4 `DirectoryNotFoundException` lines in the plugin log at 01:05:35–01:07:35 UTC; every other worker setting had 0 failures | 
| closed | **S20** | — | the probe's own batches failed for two harness reasons, not a product defect: `Guid can't be empty (Parameter 'id')` (the probe read an item id key that does not exist in `ids.json`) and, once that was fixed, `Subtitle stream index 7/8/9 not found.` (the probe asked for indices 2..21; a sidecar added by an earlier run renumbered them — S14). The server refused both correctly. The probe now reads the item's tracks from the server before queueing | `tests/gui/page-selfstart{2,4}.json`; batch `b8926df6` = 16 Completed / 3 Failed / 1 Refused, all three failures "stream index not found" |

### S19 fixed and verified (2026-09-12, decided by fabji)

The extracted subtitle no longer lives in a job's own temporary directory: `SharedExtractionStore` keys the
directory by video file, every job that touches the file registers as a consumer, and the directory goes only
when the last consumer has released it (created on every acquire; nothing deletes it inline; "Clear cache"
cleans only directories no live job reads).

Evidence, both shapes:

- **the case that failed**: one slow-profile run of the 2.38 GB episode at `ParallelWorkers = 4`, cold cache,
  50 tasks — **49 Completed, 1 Refused, 0 Failed**, 1434.4 s (`acceptance-S19-fix-slow-w4.json`). The same
  setting produced 38 Completed + **4 Failed** before the fix, and the plugin log still holds exactly those
  four `DirectoryNotFoundException` lines and **no new ones** after the fix.
- **the targeted test** (`tests/backend/s19_shared_dir.py`, 50-track movie, 14 tasks, 4 workers): 13 Completed,
  1 Refused, 0 Failed; the shared directory is seen while the run is in flight, never vanishes mid-run, no
  `DirectoryNotFoundException` in that run's log, and it is gone again after the last consumer
  (`s19-shared-dir.json`).

### F29 — both halves measured, one page-side gap left (2026-09-12)

- **The page asks before it kills** and the wording is what the brief asked for: first press →
  "Confirm: kill all syncing" plus "Press again to stop every sync on this server — that includes runs other
  users started. Nothing has been stopped yet."; second press → one `POST /SubSync/Kill`
  (`tests/gui/f29-confirm3.json`).
- **The server stops what is running**: with 4 jobs running and four `ffmpeg -i …` readers on the episode,
  the kill answered `{"queuedCancelled": 2, "runningKilled": 4, "stillRunning": 0, "stillQueued": 0}`, every
  engine process was gone 5.1 s later and `/SubSync/Active` was empty (`tests/backend/f29-kill.json`).
- **Open, page-side**: in that browser session the page did not update its own state after the kill — the
  label stayed on "Confirm: kill all syncing" and the phase line went empty, i.e. the kill handler's success
  path did not run (its `.catch` would have said "Kill request failed."). Next step: log the kill response in
  the page (`diag`) and check whether the request is refused for the page's own session — the earlier probes
  saw one `Error: HTTP 403` from a page call while the same endpoint answers 200 for the harness token.

### S14 fixed and verified (2026-09-13)

Fresh evidence came from the 2.0.27 test run: five jobs refused, all in the same shape, all in the lane's own
words - `extract lane: Egghead.Republic.2025.1080p.WEB.H264-AFO.mkv -> 0/2 subtitle(s), 0,0 MB, 8 reads, 56 ms,
ok=False, reason=subtitle ordinal 11 out of range (11 tracks)`, and the same for the `lav` track of Thunder in
My Heart S01E04/S01E05/S01E06/S01E07 (`ordinal 10`, `13`, `10`, `12`, against `(10 tracks)`). The queue lines
name the cause: `queued: job=... stream=11 ... language=mkd` - a stream index over all streams, handed to code
that counts subtitle tracks.

Reproduced before touching the code, on a fixture of the same shape (`make_remux.py --sub-tracks 11`: one video
stream, one audio stream, eleven subtitle tracks, so stream index 11 is the tenth subtitle, ordinal 9, and the
ordinals stop at 10):

| | before | after |
|---|---|---|
| the enqueue path's value for a file of that shape | `stream index 11 -> ordinal 11` | `stream index 11 -> ordinal 9` |
| what the lane did with it | `subtitle ordinal 11 out of range (11 tracks)` | extracted that track's own text (`Track 9 line`) |

The ordinal 9/10 distinction is the one that matters: at 11 the lane refuses, and at 10 it would have read a
neighbouring subtitle and cached its text under a key no job reads. Files whose subtitles are their first
streams are unaffected - ordinal and index are the same number there - which is why 24 of the run's 29 lane
passes were fine and these five were not.

Checked in `tests/run_checks.py` (9 C# checks + 2 source checks): the enqueue path's translation, the lane
resolving it, the lane producing the *right* track, the raw stream index still being refused with the run's own
line, and the contract's other shapes - subtitles-first file, ten tracks behind four streams (stream 13 ->
ordinal 9, Thunder's shape), a sidecar (no ordinal), a stream the container's embedded set does not hold (no
ordinal, rather than a wrong one), and a lone embedded track (ordinal 0 whatever Jellyfin calls it). Also
recorded as measured, not assumed - **what the leak did not do**: it did not lose a subtitle. A job resolves
its own container stream with ffmpeg and reads the subtitle it extracted itself, so only the lane's cache was
keyed wrongly. In the captured window two of the five refused tracks completed anyway (Thunder S01E05 and
S01E07, `SYNCED.lav.srt`) and the other three ran `ffsubsync exit=0` with no completion line yet - the log ends
mid-run (`running 7, limit 8, queued 1`), and Egghead Republic's file has no completion line in it at all. The
cost of the leak is the wasted pass and a loud `ok=False` lane line, which is why it stays medium and not
higher.

505 checks green (494 before this change). The real-file sentinels (kopps 829, Sune i Grekland 1019, D17
mixed-index 803) are not runnable from this machine - the media share is on the server - and this change
touches no parsing code: it only changes which of two numbers the queue hands the lane.

## New findings this session (2026-09-13/14 — the 2 497-task run, 4 batches)

Found while auditing one batch of 2 497 tasks (`9374ce59` 36, `a068567a` 907 cancelled by fabji at
23:06:32, `5396a1a3` 647 cancelled at 23:33:24, `ce6a7a65` 907 cancelled at 02:17:24). Read-out only —
**no fix is scoped for any row below**, and nothing here has been coded. Severities are proposed, not
decided. `S21`/`S22` reuse ids that were proposed for the withdrawn Alex subtitle-mismatch items on
2026-09-13 and never logged; the Alex cause was a wrong source subtitle on fabji's side, not a defect.

| state | id | sev | what | evidence |
|---|---|---|---|---|
| done | **S45** | medium | after a wrong-cut ruler is discarded, the audio retry could write the *discarded ruler's* answer and report it as the audio's. **Found while writing the characterization tests for `RunSyncJob` (2026-09-16), reproduced deterministically, fixed and shipped in 2.0.55 the same day.** When the reference ceiling discarded a subtitle ruler, the audio re-alignment was handed `tempOutput` - the file the *discarded* run had just written - and the test afterwards was `audioExit == 0 && File.Exists(tempOutput)`, so an audio run that exited 0 without writing (the engine suppresses its write when the shift is under its threshold, which is exactly what "this subtitle already matches the film" looks like) left that stale file in place: the job wrote the discarded ruler's answer and reported it as the audio's - silent wrongness of the D17/E2 family, in the one branch whose whole purpose is not to write a wrong-cut ruler's answer. **Fix**: the audio retry writes to a path of its own (`audio-fallback.srt`, deleted before the run), so `File.Exists` can only be true of a file that retry wrote; that is the pattern the wide-window ladder and the audio cross-check already use (`wide-window.srt`, `wide-check.srt`, `audio-cross-check.srt`, each deleted before its run), so this branch was the odd one out. | **Before** (pre-fix build, and reproducible today by the `P8-stale` mutation in `tests/backend/mutation_job_checks.py:75`, which puts the gate back on the reference run's path): `status=Completed`, outcome `the file's own subtitle track is not the same cut, so this was aligned against the audio · +45000 ms offset`, `written-cue=00:10:45,000 --> 00:10:47,000` - the ruler's `+45 s` answer in the library, while the audio run wrote nothing. **After** (`Jellyfin.Plugin.SubSync/Services/SubSyncService.cs:6144` the new path, `:6176` the gate, `:6195` `tempOutput = audioOutput`): the same case is `Failed`/`Refused` with `refused: the subtitle was aligned against the file's own subtitle track s:0, which demanded a 45000 ms shift — that track is not the same cut — and aligning against the audio instead produced nothing. Nothing was written.`, `OutputPath` null, `sidecar=False`. The regression test is `tests/job_checks.cs:373` (`S45: the audio retry writes to its own path, so a stale reference output is not taken for its answer`), and the neighbour check `tests/job_checks.cs:349` was strengthened to assert *which* answer reached the library (`00:10:05,000` the audio's, not `00:10:45,000` the ruler's). Suite 963 checks green; `P8-stale` caught by both checks. |
| open | **S46** | medium | the wider-window retry is handed an audio reference the plugin has already deleted, so the retry that exists to rescue a window-pinned answer cannot run, and the job refuses | **Found while fixing S22 (2026-09-16) by reading the code the field log named; not yet reproduced in the harness or on the rig.** `PrepareAudioReferenceAsync` hands the engine `SpeechCache.CreateReferenceLink(videoPath, speechKey)` - a symlink `<state>/speech-cache/<key>.mkv` pointing at the media file - and once a run that *used a cached analysis* finishes, the plugin calls `SpeechCache.DropLink(speechKey)` (`Jellyfin.Plugin.SubSync/Services/SubSyncService.cs:6023-6025`), which deletes every file under the speech-cache root matching `<key>.*` that is not `.npz` (`SpeechCache.cs:106-125`): the symlink included. The wide-window retry and the verification run in the same job are handed `referenceArg`, the value captured *before* that drop (`:5863` the first run, `:6237`/`:6281` the retries), so they ask ffsubsync for `--reference <deleted path>`. That is the field's refusal on 2026-09-15: `unable to read reference /config/data/data/subsync/state/speech-cache/5e16e1f2a0c1c78f085afdfdd3275934.mkv; try ensuring file exists and has correct permissions` - 7 of that run's 8 refusals, every one a job whose answer had reached the search window and whose retry then could not start. Repair (not written): rebuild the link before the wide/verify/cross-check runs (or keep it until the job's `finally` drops it), or hand those runs the analysed `.npz`. What would settle it: a harness case with a cached analysis and a window-pinned first result - `SpeechCache.KeyFor` and `SpeechCache.Root` are both reachable from the test, so the cache can be primed - and then a rig run of the same shape. |
| open | **S44** | medium | **A folder the Jellyfin user cannot write costs a full engine pass per job before it refuses.** Seen in fabji's live 2.0.43 batch (2026-09-15 13:19:18 and 13:19:57, `Black.Mirror.2011.S05` on `/media/Serier`): `ffsubsync exit=0 after 4062 ms` then `System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched.` - the guard itself is right and nothing was damaged, but `RequireWritable` runs at the write step, so every job in an unwritable folder pays its extraction and alignment first. 184 of that batch's ~298 jobs address `/media/Serier`, so the shape repeats. Fixes: `Jellyfin.Plugin.SubSync/Services/SubSyncService.cs:6054` (where the check is called at the write) and `:2628` (`RequireWritable` itself) - probe writability once per directory when a job starts rather than at the write, memoised like `SpeechIsCached` so it never touches storage under `_queueLock`, and refuse with the same wording it already prints. |
| done | **S21** | low | a completed job that writes *nothing* is invisible in the plugin log. Both no-change paths (`SubSyncService` 3988 and 4298–4312) log through Jellyfin's logger only and `return` before the plugin-log line at 4540, so no `job … completed:` line is ever emitted for them — while the sibling *unverified* outcome (`UNVERIFIED:`) logs to *both* logs and appears 151 times. The UI is right (`Status=Completed`, `OutputPath=null`); only the plugin log is short. **Sharpens B31**, which scoped the blind spot to the lane's unexpected failures — routine completions are just as invisible | `9374ce59` (36 tasks) closes only as 16 Completed + **20 silent** = 36, and its last dispatch `22:48:42.998 dispatch: starting 1, running 7, limit 8, queued 1, batch 9374ce59…` shows the queue drained; `a068567a` closes only as 25 + 2 Refused + 865 cancelled + 8 stopped + **7 silent** = 907. In the live batch at 01:35: 238 Completed + 151 UNVERIFIED + 6 Refused + 14 failed = 409 terminal lines against 563 jobs that had left the queue. Closing the count needs Jellyfin's own log: `grep -E 'sync changed nothing|subtitle already in sync' /config/log/jellyfin*.log`. **Fixed in `0a34ca9`**: one definition builds the completion line and all three completed paths call it (the success path and both nothing-written paths); the four refusal paths keep their own REFUSED/UNVERIFIED lines |
| done (not shipped) | **S22** | medium | a refusal blamed the search window when the engine could not read the reference it was handed, and sent the user to a setting that cannot help it. 7 of the 8 refusals in one field run were this shape. **Read against the code and fixed 2026-09-16 as the S22 half of Phase 1 of the `RunSyncJob` extraction.** The refusal's `detail` already carried the engine's own words (`· engine said: …`), so the cause had been sitting in the message with nobody reading it. `EngineCouldNotReadReference(engineTail)` (`SubSyncService.cs:7048`) classifies the tail on the engine's own markers (`unable to read reference`, `No such file or directory`, `Permission denied`), and that refusal now takes its own branch: it names the reference the engine was handed, quotes what the engine said, and states that raising "Maximum offset" will not help. The reference in the field's case was a path the plugin had deleted itself - that root cause is **S46**, not fixed here. | **The field's shape, reproduced in the harness** (`tests/job_checks.cs` case `s22-reference-unreadable`: the first run is pinned to the window, the wider retry exits 1 saying it could not open the reference): the refusal is now `refused: the engine could not read the reference it was aligned against (/tmp/speech-cache/e3eba24e…mkv) · engine said: ffsubsync: unable to read reference /config/data/subsync/state/speech-cache/5e16….mkv; try ensuring file exists and has correct permissions. Nothing was written. Raising "Maximum offset" will not help this - the reference could not be opened, so the alignment had nothing to measure against.` - and it does **not** contain `Raise "Maximum offset" in the plugin settings`. Two checks, both in `tests/run_checks.py`: `S22: the engine's own words decide whether a refusal is a search-window problem` (the classifier, on the field's exact tail plus `No such file or directory` / `Permission denied` / an ordinary empty run) and `S22: a refusal after the engine could not read its reference names that cause instead of the setting` (end to end). The `S22-cause` mutation in `tests/backend/mutation_job_checks.py` is caught by both. |
| done | **S23** | low | a cancelled job gets no line of its own — cancelling records batch-level counts and the phase ids of the running jobs, nothing per job | `23:06:32 cancel batch a068567a…: 865 queued cancelled, 8 running stopped (phases: 091663b3=Analyzing speech, a790cdd3=Analyzing speech, …)`; `23:33:24 cancel batch 5396a1a3…: 636 queued cancelled, 11 running stopped (phases: … Syncing (from cache) ×8, Extracting subtitle …)`; `02:17:24 cancel batch ce6a7a65…: 290 queued cancelled, 15 running stopped`. 1 501 jobs left the log with no terminal record. **Fixed in the commit logged with this row**: `job <id> cancelled: mode=… item=… stream=…` is written per job by both `CancelBatch` loops and both `KillAll` paths. Also worth noting: in-flight results still land after a cancel — lane lines appear at 02:17:47–48, after the 02:17:24 cancel |
| done | **S24** | low | a run has no progress or ETA surface: the only queue depth anywhere is the dispatch line, emitted only when a job *starts* and naming only the batch that job belongs to | `02:13:00 dispatch: starting 1, running 7, limit 8, queued 304, batch ce6a7a65…` — nothing reports jobs left, files left, the lane's current file, or that a run has ended. On this run the backlog (338 jobs over 338 files, one job per file) and the ETA had to be derived by hand from lane lines and dispatch timestamps: 11 jobs in 918 s = 0,7/min ⇒ 4–8 h of uncertainty. The lane also spends long stretches inside a single file with no line in between. **Fixed** (commit logged with this row): one line a minute while work exists - `queue: N queued, M running, F file(s) left, lane currently on: <file>, T min elapsed on it` - emitted from the pump tick, with the lane's start time now stored per pass |
| done | **S25** | low | batch history is in-memory and unpersisted, and progress follows a single batch, so the two read as one complaint ("there is no way to tell how many jobs are left" + "the history resets") | batches live in `SubSyncService` and are read back through `GetBatch` / `GET /SubSync/Batches` (server-side, shared by all viewers per `AGENTS.md`); no retention, eviction or persistence code was found for them, so a restart clears the list. The page watches one batch at a time (`watchOrQueueBatch(batchId, label, count)`), so creating a new batch replaces the progress the user was looking at. Reported by fabji after queueing four batches in one session. **Fixed** (commit logged with this row): `{StatePath}/batch-history.json` via `BatchHistory`, restored at start-up, written from the pump tick (signature + 60 s) and once on shutdown; restored jobs are display-only, exempt from the cleanup timer, and anything interrupted comes back as cancelled |
| fixed | **S26** | medium | on one release the cue-indexed plan and its prefetch disagree with reality by ~20×, and the reads that follow are the single largest cost in the batch. The shared-pass route on the same file predicts correctly, so this is specific to the cue-indexed plan over that release | `WARN extract plan: solsidan.s03e02.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/528 read(s) (118777.6 ms), actual 42.03 MB/10635 read(s) (3641220.3 ms) - bytes 66.…` then `WARN extract: solsidan.s03e02… read 0,09 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 789 range(s)…`; same shape on s03e03, s03e04, s03e05, s03e06 — lane passes of **17,6, 19,3, 23,5, 56,6, 78,5 and 95 min** for ~11 000 reads each, ~20 reads/cue against the family's ~2. Per-read cost on the same release: `extract profile: solsidan.s03e09…: storage 2,14 ms per read and 1,8 MB/s` versus `1613,40 ms/read, 0,0 MB/s` on s03e04. `past the fetch` has no row anywhere in this plan although it is in the tail watcher's filter list (Ranked by blast radius in the 2026-09-14 triage; the claim has not yet been read against the code.) **Live instance, fabji's own library, 2.0.43 (2026-09-15 13:32)**: `Sunes Sommar 1993 WEB-DL 1080p.mkv` (`/Media/Movies`, a fast volume) - `cue-indexed expected 1.55 MB/1308 read(s) (1320.4 ms), actual 134.45 MB/33754 read(s) (52096.8 ms) - bytes 86.50x, reads 25.81x - this pass missed its own prediction`, and 8 s later the same file again at 85.09x/25.41x. The plan itself is *right* (`the index locates 663 of 663 cue point(s); 663 located block(s), 0 cluster(s) to walk, reads are 0.00 ms each, merge gap 2 KB`), and the reference line reports `method=seekhead-cues ms=2097 cues=663 bytesRead=133597851 readCalls=33525` - so ~50 reads and ~200 KB are being read per subtitle block by the read layer, not by the plan. The same pass also breaks two ledger rules the policy says must be zero: `bytesTwice=1,37 MB` on the lane line and `read 0,09 MB past the fetch`. The R2 flag caught it (that is the safety net working), and the second pass on the same file took 192 ms, so the cost is cold bytes. Worth its own reproduction: the file is small (a 1993 WEB-DL), local, and the shape repeats inside 8 seconds. The read layer's own call sites are `Services/MkvSubtitleExtractor.cs:1825` (payload) and `:3083` (`ReadNear`), which is where the per-block cost has to be looked for. **Mechanism found 2026-09-15, with a local cold probe** (`tests/backend/s26_probe.py`, which builds the shape with `tests/fixtures/make_remux.py` and runs the real extractor): a cluster whose size vint is the EBML unknown marker cannot be read by the indexed route - both `TryReadClusterHeader` and `TryReadClusterTimecode` refuse `size == ulong.MaxValue` (`Services/MkvSubtitleExtractor.cs:1296-1310`, `:1334-1362`) - so **every cue point misses the indexed read and falls back to walking its cluster** (`ReadCluster(..., wideWindow: true)`, `:810`), while the plan line still reports `0 cluster(s) to walk` because the *index* located every cue point. Measured on the fixture: as generated `indexedMisses=0`, with every cluster size rewritten to unknown `indexedMisses=40` of 40 cue points, same 40 cues out - the mechanism, reproduced in seconds and cold. What the fixture does **not** reproduce is the magnitude: at ~3-4 blocks per cluster the fallback walk costs the same as the indexed read (`bytesRead=114665 reads=87` either way, ratio 1.21x/1.09x, and 6 tracks instead of 2 changes nothing because a per-track extraction visits only its own cue points), where the field file read 133 MB / 33 525 reads for 663 cues ≈ 50 block headers per cue point. So the field's 86x is block density x a walk that repeats per cue point, and the generator cannot yet write a dense cluster (one video block per cluster) - `--blocks-per-cluster` is the first task. Two fix candidates, in the order the evidence supports them: (a) accept an unknown-size cluster, deriving its end from the next cluster position or the file length, so the indexed read works for the files the field is made of; (b) walk a cluster **once** and reuse the block offsets it discovers for the other cue points in it, which bounds the cost even when the indexed read genuinely cannot be made. Diagnostic gap to close in the same change: `IndexedMisses` is counted (`:110`, `:807`) and never printed, so no field log can say how many cue points were walked - the plan line says zero. **Culprit proven 2026-09-15 with a cold local repro - the plan is right, the reader refuses the cluster.** The dense fixture now reproduces the field's shape in seconds (`tests/backend/s26_probe.py` + `tests/fixtures/make_remux.py --blocks-per-cluster 48 --frame-payload 24`, both new): clusters whose size vint is the EBML unknown marker are refused by `TryReadClusterHeader` (`size == ulong.MaxValue`, `Services/MkvSubtitleExtractor.cs:1338-1343`), so `ReadIndexedBlock` returns false (`:1304-1308`) and the cue-indexed loop falls back to `ReadCluster(..., wideWindow: true)` (`:810`) **once per cue point**. The walk reads every block header, and in a real cluster the headers sit behind frame payloads of tens of KB while the window is ~2.1 KB - one real read per header. Measured on the repro: known-size clusters `bytesRead=114693 reads=87` (1.21x/1.09x, plan respected), the same bytes with every cluster size marked unknown `bytesRead=4169733 reads=2007` -> **44.02x bytes, 25.09x reads, 50.2 reads/cue**, against the field's 85.09x/25.41x and 50.6 reads/cue. Same read count per cue point, so the mechanism is identical; the byte ratio differs only because the fixture's frame gaps are 24 KB where the field's are larger. So `0 cluster(s) to walk` in the plan line is a true statement about the *index* and a false one about the pass. Fix, in this order: (a) accept an unknown-size cluster, bounding it by the next cue point's cluster offset (or the file length) instead of refusing it, so the located read happens and no walk is needed - the bound must stay an upper bound and the block verification (track id, timecode) must not be weakened; (b) walk a cluster once and reuse the offsets it discovers for the other cue points in it, which bounds the cost when an index genuinely cannot be used. Also print `IndexedMisses` (`:110`, `:807`), counted today and never logged, so a field run says how many cue points were walked. Then the goal's own bar: output byte-identical on kopps 829 / Sune i Grekland 1019 / D17 803 / Helikopterrånet 803-726-898 with the NEbml cross-check, the two ledger rules at zero on the repro and every fixture, both storage profiles, the index-present/absent x 1/many-track matrix, and a suite check that fails without the fix. **Fixed and verified in code 2026-09-15, not released.** `TryReadClusterHeader` bounds an unknown-size cluster instead of refusing it (the bound is the next cluster the index mentions, or the file length), `ReadIndexedBlock` takes that bound and - because the bound is not the cluster's own end - keeps the read only when the block it produced is the cue point's own (its computed time against the cue point's), the cue loop makes the located attempt once instead of twice, remembers the clusters it walked so a sibling cue point cannot walk the same one again, and the lane line prints `indexedMisses` and `walked`. Witness, cold, `tests/backend/s26_probe.py`: the dense fixture with every cluster size marked unknown went from `bytesRead=4169733 reads=2007` (44.02x its planned bytes, 25.09x its planned reads, 50.2 reads per cue) to `bytesRead=114765 reads=87` (1.21x/1.09x, 2.2 reads per cue) - the same 40 cues and the same md5 as the identical file with known sizes, both ledger rules 0 B, `walked=0`. Control run: putting only the refusal back makes the new suite check fail again (44.02x/25.09x, `indexedMisses=40 walked=40`) while the known-size file stays at 1.21x, so the check can disagree with the code it guards. What this does NOT prove: that the field file's 86x is gone. Its ~4 KB per read implies the *wide* window `ReadCluster` sets only for clusters whose size IS stated, so the field's pass most plausibly came from a known-size cluster whose located read failed for another reason (a damaged or foreign index, a BlockGroup, an offset past the cluster end) and whose walk then ran once per cue point - same class, different trigger. The two counters added here name it on the next live run of that file; until then the field cause is unclaimed. **Shipped in 2.0.44 (beta) and verified as an installer would see it** (manifest reports 2.0.44.0, the 42 483 690-byte zip's MD5 matches the catalog's checksum `ac166cc1f820953ba94c452bc10770da`, and the packaged `meta.json` and DLL both carry 2.0.44.0 with the bundled ffsubsync inside), with the rig's storage scenarios (`s41-steady` at 10 ms/read, `s39-ratio` across a fast and a slow volume) passing on that build - the goal's own bar, met as far as this host can measure it. The one thing still unproven is the field file itself: its ~4 KB per read implies the wide window set only for clusters whose size IS stated, so if `walked=` comes back non-zero on the next live run there is a second trigger to find, and the counters will name it. |
| explained — no code change | **S28** | medium | the audio-ruler analysis costs **one full walk of the media file**, and that walk *is* the cost; the observed ~6x penalty over the share's own measured speed is concurrency (up to 8 walks at once on one volume). A fix that extracts a compact audio reference first was tested and **refuted**: through the share's profile the rip costs 901 s and the engine's own job costs 903 s, so the copy moves the walk rather than removing it (it would only help re-analysis, parked below). The real levers are (1) how many full-file walks run at once, (2) the storage path itself, (3) jobs that align against a subtitle reference and never walk the media (median 2,9 s) | Measured 2026-09-14 with `tests/backend/slowread.so` at fabji's share profile (10 ms/round trip + 11 MB/s), one 2,4 GB episode: audio rip **901 s** (capped, rip complete), engine with the media as reference **903 s exit 0**, engine against a **local** audio-only reference **9 s**, decode-only 160 s (killed by a cleanup, unusable), engine with the media reference on **local disk 17 s**, engine against the local audio-only file on local disk **8 s**, and both produced the same alignment within 70 ms (`offset seconds: 4.690` vs `4.620`). Field: 232 audio-ruler engine runs median **35,3 s**, films 57-111 min, per-read latency on the share 2,14-1613 ms within minutes, 8 concurrent walks + the extraction lane reading ~10 000 ranges. Concurrency experiment **measured** (`tests/backend/s28-concurrency.sh`) — see the concurrency section below the table for the raw per-level numbers, what they do to the capping question, and the server confirmation recipe |
| done | **S27** | medium | no deadline and no heartbeat around the engine run: the plugin logs a start and an exit and nothing between, so a wedged engine pins a worker indefinitely and a correct 91-minute run is indistinguishable from a hang on every surface the user has. **Fixed in `8d2b548`**: `EngineHeartbeat` writes `[<job>] <file>: engine running, N min elapsed, reference=<ruler>` to the plugin log every 5 minutes for exactly as long as the process runs (started after the process is live, disposed when it is gone, best effort). No deadline and no kill were added - visibility only, as decided. The wording, the interval and the absence of a timeout are pinned by the suite | Hereditary `00:22:09 [d805de52…] ffsubsync start: … reference=a:0 …` with no exit line **111 minutes** later; Midsommar completed at `02:05:45 job c4d7d3ae… completed: … bytes=69518 change=+112836 ms` after **91,3 min**; Oppenheimer 65,5 min; The Lighthouse 57,2 min. During all of it the plugin log carries nothing between start and exit. `ExtractionTimeoutMinutes` (20, `timeoutCts.CancelAfter`) governs the ffmpeg extraction fallback only — the engine is killed solely through the caller's cancellation token. `ps` during the run: parent `ffsubsync` at **0,2–2,9 % CPU (18 s CPU in 106 min)** ⇒ waiting, not computing; the work sits in ffmpeg children, which `ps -C ffsubsync` does not show. Cost by ruler, whole run: `reference=a:0` n=232 median **35,3 s** max 5 479,8 s; subtitle ruler n=429 median **2,9 s** — a feature film with no usable embedded subtitle costs ~1 h |

### Concurrency experiment - measured 2026-09-14 (`tests/backend/s28-concurrency.sh`)

Fixture: a 218 MB / 5 min slice of a real 2,4 GB episode inside the shim's prefix, 59-cue sidecar, each
job being exactly what an audio-ruler analysis costs the engine (`--vad webrtc --reference-stream a:0`).
Levels 1/2/4/8 concurrent walks, run twice: once with the volume's **bytes** shared
(`SLOWREAD_MS_PER_16K x N`, per-call latency unchanged) and once with bytes **and round trips** shared
(both x N). All 30 jobs exited 0 and wrote output. Raw per-job times: `/tmp/s28-conc/job-*.time`
(format `<ms> <exit>`).

| model | N | min s | median s | max s | spread | aggregate |
|---|---|---|---|---|---|---|
| bandwidth | 1 | 83,444 | 83,444 | 83,444 | - | 43,1 files/h |
| bandwidth | 2 | 102,621 | 102,632 | 102,644 | 23 ms | 70,2 |
| bandwidth | 4 | 141,647 | 141,660 | 141,667 | 20 ms | 101,7 |
| bandwidth | 8 | 219,641 | 219,922 | 219,945 | 304 ms | 131,0 |
| bandwidth+queue | 1 | 83,212 | 83,212 | 83,212 | - | 43,3 |
| bandwidth+queue | 2 | 163,413 | 163,421 | 163,428 | 15 ms | 44,1 |
| bandwidth+queue | 4 | 324,035 | 324,047 | 324,059 | 24 ms | 44,4 |
| bandwidth+queue | 8 | 644,884 | 645,041 | 645,074 | 190 ms | 44,7 |

What it says:

- **A walk costs `reads x per-call latency + bytes x per-byte charge`**, and at N=1 the latency term is
  **77 %** of the total: fitting `c + b*N` gives c=63,9 s and b=19,5 s, predicting N=2 at 102,9 s and
  N=4 at 141,9 s against 102,6 s and 141,7 s measured.
- **bandwidth (bytes shared, latency not queued): aggregate rises with N**, 43 -> 131 files/h, because
  the fixed latency term is amortised by overlapping jobs; per-file grows sublinearly.
- **bandwidth+queue: per-file is proportional to N** (1,96x / 3,88x / 7,73x), so **aggregate is flat**,
  43,3 -> 44,7 files/h (+3 %). Flat, *not falling* - a falling aggregate needs *super-linear* queueing
  (per-read latency growing faster than N), which this model does not contain.
- **Both pre-test predictions were wrong**: bandwidth-only was predicted flat (measured: rising) and
  bandwidth+queue was predicted falling (measured: flat). The decomposition above is why.
- Cross-check against the separate full-size run: 218 MB at N=1 = 83,4 s, and scaling the 2,4 GB walk
  (903 s) by 218/2382 gives 82,6 s - two independent runs agree within 1 %.
- Min/median/max are within 0,3 s at every level because the shim's charges are deterministic; it has
  no variance and no cross-job randomness, so the spread is not evidence of anything.
- **The two models give opposite answers about capping**, so the shim cannot settle it: under
  bandwidth-only a cap costs throughput (43 vs 131 files/h); under bandwidth+queue a cap is free in
  throughput and buys up to ~8x lower per-file latency. Which regime the real share is in is a property
  of the share, not of this arithmetic.

**Server confirmation (small, controlled, nothing to push).** `ParallelWorkers` is read live from
`SettingsSource.Current()` (`SubSyncService.cs:2556/2565/2677`), so the limit can be lowered without a
restart, and the installed 2.0.29.0 already logs `queued: job=...` and `job ... completed: mode=...`
with ISO-ms timestamps plus `dispatch: ... limit L`, so per-file wall clock is derivable from the plugin
log alone. Recipe: one fixed set of 3-4 audio-ruler files (their jobs show the `Analyzing speech`
phase), each run once at ParallelWorkers 1 / 2 / 4, same files and same order, recording each level's
wall-clock window, plus one known-file read before each level to time the share's idle throughput. Then
per-file (queued -> completed) and aggregate per level discriminate the regime: flat, proportional, or
worse than proportional.

### Server confirmation - measured 2026-09-14 (8 episodes, `Outsiders` S10, `/media/synology`, one batch per level)

Same 8 files, same order, audio analysis cleared before every level (verified: all 24 engine runs logged
`reference=a:0 cachedSpeech=False`), worker limit changed between levels, read from the plugin log
(`tests/backend/s28-server-levels.py`).

| limit | files | per-file min / median / max | level wall | aggregate | validity |
|---|---|---|---|---|---|
| 1 | 8 | 18,0 / **22,2** / 32,8 s | 235,8 s | 122,15 files/h | OK |
| 2 | 8 | 34,1 / **48,9** / 53,5 s | 213,9 s | 134,63 | OK |
| 4 | 8 | 68,6 / **94,1** / 107,1 s | 216,7 s | 132,89 | OK |
| 8 | 8 | 366,5 / **421,3** / 432,6 s | 438,9 s | **65,62** | OK |

- **Per-file time is proportional to the limit** (1 : 2,20 : 4,24) - the queueing signature, exactly what
  the shim's bandwidth+queue model predicted.
- **The level's wall clock is flat up to 4** (3,9 / 3,6 / 3,6 min) and so is the aggregate
  (122-135 files/h) - but **both collapse at 8**: 7,3 min and **65,6 files/h**, half the throughput of
  every lower level, with 421 s for a file that takes 22 s alone. So parallelism is *free* up to ~4 and
  *harmful* past it: the knee sits between 4 and 8, and per-file time is linear (1 : 2,20 : 4,24) only
  until the volume itself saturates.
- **What a user watching the UI sees**: at limit 1 the first episode is done in 22 s and files land
  every ~25 s; at limit 4 the first result takes 69 s and they then arrive in bursts; at limit 8 nothing
  completes for six minutes. The *last* file lands at ~3,6-3,9 min either way (4,4 min at limit 8), so
  concurrency redistributes the wait rather than shortening it.
- **Verdict: this share is in the queueing regime.** Concurrency buys no throughput at all; it only
  multiplies how long one file waits. All 8 files needing analysis cost ~3,6 min of wall clock whether
  the limit is 1 or 4, but a single file finishes in 22 s at limit 1 against 94 s at limit 4.
- The earlier limit-8 point for the same episodes (median 503,4 s, 20,74 files/h) came from inside a
  2 500-task batch, so the other 2 492 tasks' extraction shared the volume. The pure queueing model
  predicts ~176 s per file at limit 8 (22,2 x 8); the measured 503 s means that mixed load cost a further
  ~3x. **That is the production shape**: at a high limit with other work queued, per-file time collapses
  and aggregate throughput falls with it (20,7 vs 133 files/h).
- Caveat: one show's episodes, one share, one batch of audio-ruler jobs per level. The regime is
  measured; the exact cap that is optimal under mixed load is not.

**Proposed change (not scoped, nothing built).** Cap concurrent *audio-ruler* analyses separately from
subtitle-ruler jobs, since the two cost entirely different things: an audio ruler walks the whole
container (22-500 s per file), a subtitle ruler reads the container index (field median 2,9 s). The
measurement says an audio cap is free - aggregate is flat between limit 1 and 4 - while it cuts the time
for any *individual* file up to 4x, and it protects the extraction lane from competing with eight walks
on the same volume. Measured curve points at **1 or 2**: limit 1 is the fastest per file (22 s) and limit 2 costs 2,2x per
file for the same aggregate, while 4 costs 4,2x and 8 costs 19x *and half the throughput*. Decide between
1 and 2 when this is scoped.

### Producer/consumer (pre-warm the analysis) - viability measured 2026-09-14, scope assessed

**The assumption that decides it**: a *warm* analysis must remove the media read, or a pre-warm lane just
moves the walk (the mistake the audio-copy idea made). Measured on the test server with the purpose-built
`Single Track (2026)` fixture, which can only be an audio ruler (one subtitle track, so no sibling
reference), same item and track twice, log timestamps rather than polling:

| run | engine line | engine start -> terminal |
|---|---|---|
| cold | `cachedSpeech=False reference=a:0 args=.../speech-cache/1367190121e143d85ea6317` | 1,03 s |
| warm | `cachedSpeech=True reference=a:0 args=.../speech-cache/1367190121e143d85ea6317c` | **0,29 s** |

The engine is handed the same speech-cache path either way and `cachedSpeech=True` is what makes it use
the stored analysis instead of walking the media. **The assumption holds**: a warm analysis removes the
I/O, leaving the alignment CPU (~seconds, and ~10-30 s on a full film's features). Scaled to fabji's
share that replaces 22-421 s of walking per file with a local feature read.

**What it does not do**: the walk still happens once per file, exactly as the audio-copy test showed. A
pre-warm lane does not reduce total NAS reads - it takes the walk off the workers' critical path, so
workers stop stalling and stop competing with each other for the volume. The win is stall/latency in
mixed batches, not throughput.

**Refuted**: "might also help on fast local storage". Where the volume is not shared, concurrency *raises*
aggregate throughput (shim bandwidth model: 43 -> 131 files/h from 1 -> 8 concurrent walks), so a 1-2
worker analysis pool would *cut* throughput there. This must stay opt-in for storage-bound setups.

**Can the existing architecture support it cleanly - yes, but not as "one pool hands jobs to another".**
A job is atomic (extract -> build reference -> run engine -> write) and analysis+alignment happen inside a
single ffsubsync invocation, so there is no "finished analysis" object to hand over. The shape that fits
the existing code is a *pre-warm lane*: a small pool that populates the speech cache ahead of the workers.
Precedents already in place: `ExtractLaneAsync` + `_laneTasks` is exactly that pattern and its size is
already derived from config (`ParallelWorkers / 2`, `SubSyncService.cs:1807`); `SpeechIsCached(job)`
(`:2104`) already tells the scheduler which jobs will walk; `_speechGates` already stops two callers
walking the same file.

**Scope, plainly** (estimates, before building):
- cap alone - a predicate in the existing `PlanStart` dispatch (`:2445`, called at `:2680`) + a setting +
  one check: **small, one file, ~30-60 lines**.
- pre-warm lane - clone `ExtractLaneAsync` + a pre-warm policy (which files, under what storage
  condition) + its own setting (1-2, default off) + admission so walkers and workers never double-walk +
  tests: **roughly 2-4x the cap**, ~150-300 lines, and the risk is in policy rather than plumbing.
- delivering the described behaviour needs **both**: the lane keeps walkers busy, the cap bounds what
  workers do when the cache is cold. Cost is the union; the cap is worth doing regardless.

Defaults, given the measured curve (122 / 135 / 133 / 66 files/h at limits 1 / 2 / 4 / 8): the current
default of 4 is already the best aggregate measured, so a cap must be opt-in, not a new default.

### Per-volume walk concurrency, automatic (scoped 2026-09-14, not built)

Goal: reject the global setting; cap concurrent audio-ruler walks *per volume*, decided automatically, no
new settings entry, so one batch can span a fast SSD and a slow NAS without either throttling the other.

**What already exists in beta** (found while scoping - this is why the estimate is small):

- `MediaVolume.Of(path)` already keys a volume by the device behind the longest matching mount point
  (`Services/MediaVolume.cs`), and `PlanStart` already receives it:
  `Func<SyncJob, string> volumeOf` fed by `job => MediaVolume.Of(ctx.Video.Path)` (`:2686`).
- `PlanStart`/`WavePolicy` already carry `InUseVolumes` and an `IsHeavyIo` predicate (`:2445`, `:2472`,
  `:2505`), and `SelectWave` already builds a `usedVolumes` set (`:2208`).
- "Will this job read the media?" is already a predicate: `JobNeedsHeavyIo` (`:1598`) returns true for
  embedded extraction or for an uncached audio pass (`UsesSpeechCache(mode) && !SpeechIsCached(job)`).
- So the walk cap is expressible as a **pure function of data the selector already holds**: the running
  jobs, their volumes, and their heavy-ness. No new accounting, no semaphore, no second pool.

**What R1 adds that is still wanted**: the *measurements*. R1's `VolumeProfile` (single commit `9e53170`,
`Services/VolumeProfile.cs`, 370 lines, held on `eval/per-volume-profile`) keeps per-volume read latency
(age-weighted median) and throughput (byte-weighted mean, slowest fifth trimmed, 10-minute half-life),
fed by reads the passes were already making, exposed as `VolumeProfiles.For(path)`, `KeyFor(path)`,
`MsPerCall()`, `BytesPerMs()`. Its volume *identity* duplicates `MediaVolume`, so only the numbers are
needed from it.

**Fixed 1-2 vs live/adaptive - reasoned, with the measurements we have:**

- Per-volume caps solve the *cross-volume* half by construction: a slow volume's cap cannot throttle jobs
  on a fast volume, and nothing forces the two to share a number. That is goal 4 and it holds for a fixed
  number too.
- A fixed 1-2 does *not* leave a fast volume alone *within itself*: where the volume is not shared,
  concurrency raises throughput (shim bandwidth model: 43 -> 131 files/h from 1 -> 8 walks), so capping an
  SSD at 1-2 costs its own batch throughput even though each file stays fast.
- The tiered version therefore wins on both sides, and it is cheap because the separating signal already
  exists and is huge: class default 0,05 ms/read, the user's share 13-46 ms at rest and 1419-1613 ms under
  load. A threshold on `MsPerCall()` (e.g. >5 ms/read => storage-bound => cap 1-2; else uncapped) gets the
  automatic behaviour without a control loop.
- A live hill-climb on per-file walk time is a bigger build (per-volume walk-duration history, hysteresis,
  cold start, oscillation) for little over the threshold, which already reads the same volume's measured
  latency, i.e. it *is* adaptive to degradation. Not recommended for the first cut.

**Blocker found while scoping: the repo has a deliberate invariant *against* a per-volume cap.**
`tests/run_checks.py` asserts, in three places plus a reflection check, that a volume may express a
*preference* but never a limit:

- "Heavy work is no longer throttled per volume: with four workers, four heavy first-time reads on one
  shared disk all enter the same wave" - `Check("heavy work on one disk is not throttled", heavyWave.Count == 3)`.
- "a wave must still fill up to the worker count, with no storage throttling" -
  `Check("four heavy tasks on one volume fill the wave", full.Count == 4)`, and
  `Check("worker count is the only bound (2 requested -> 2 in flight)", twoWorkers.Count == 2)`.
- "There is no per-volume budget left on the policy type: volume information may only express a preference
  (VolumeOf), never a cap on how many jobs a volume contributes" - enforced by reflection,
  `Check("WavePolicy has no per-volume budget", !typeof(WavePolicy).GetProperties().Any(p => p.Name.Contains("PerVolume") || p.Name.Contains("Budget")))`.

The reason the budget was removed is **not in this repo's history** - `git log -S` resolves only to
`71347e8`, the initial import that already carries the new wording - so the original measurement behind it
cannot be cited. What the checks state is the intent: *the worker count is the only bound, never storage*.
That intent is exactly what this night's measurement contradicts on a storage-bound volume: at limit 8 the
share delivered 65,6 files/h against 122-135 at limits 1-4, with per-file time 19x worse (421 s vs 22 s).
The rule was evidently set when a bound was assumed harmless, and the check "four heavy tasks on one volume
fill the wave" describes the behaviour we have now measured as harmful on the NAS and correct on an SSD.

So this is not only a code cost: **it reverses a deliberate invariant and needs an explicit decision.**
The design happens to answer the original objection (a per-volume cap cannot starve workers the way a
global one does, because other volumes keep working), and the tiered cap keeps the invariant for fast and
unknown volumes, which is what "no volume information still runs at full width" requires. But three checks
have to be rewritten and the reflection check replaced with one asserting the new rule - renaming a
property to slip past it would be gaming the check, not honouring it.

**Revised scope estimate, with that in mind:** the mechanism stays **~100-180 lines** (up from 80-150:
the wave-policy change plus the invariant rewrite) plus the single-commit cherry-pick of `9e53170`, plus
**10-15 checks touched** (3-4 rewritten, 1 replaced). Still roughly 2-3x the global cap, not an order of
magnitude - the decision, not the line count, is the real cost.

**Scope estimate** (before building):
- identity + "will walk" predicate + the running-job data: **already there, no work**.
- `WavePolicy`: per-volume load dict + per-volume cap dict, `SelectWave` skips a heavy candidate whose
  volume is at its cap: **~30-50 lines**.
- cap lookup from `VolumeProfile.MsPerCall()` with a threshold and a small clamp: **~15-25 lines**, plus
  the single-commit cherry-pick of `9e53170` (conflicts expected only where beta moved on: `ReadPolicy.cs`,
  `tests/run_checks.py`).
- checks: mixed volumes (fast uncapped / slow capped) asserting the fast volume is not throttled, and a
  slow volume never exceeding its cap at any limit: **~8-12 checks**, deterministic, no I/O.
- Total: **~80-150 lines + one cherry-pick => roughly 2-3x the global cap (~30-60 lines), not a different
  order of magnitude.** The one design detail to settle during the build: `JobNeedsHeavyIo` is broader than
  "will walk" (it also counts embedded extraction, which is cheap index reading) - the cap wants the
  narrower "will this job invoke the engine with the audio ruler", and over-counting is the safe side.

**Verification plan** (after building, matching the goal): two genuinely different devices on the test
server - a tmpfs volume as the fast one and the disk under the shim's prefix as the slow one - so the
identity really differs; one mixed batch to show the fast volume's jobs are not throttled by the slow
volume's cap and vice versa; the four-level curve re-confirmed for the slow volume alone; suite green.

### Per-volume walk ceiling - BUILT 2026-09-14 (`WalkCapOf`), with the invariant reversal recorded

**The new rule** (this replaces "the worker count is the only bound", which the checks used to enforce):
a volume that nothing has measured, or that measures fast, has **no** ceiling and behaves exactly as it did
before; a volume whose own reads have shown it storage-bound carries a ceiling of its own, and that ceiling
counts **that volume alone** - jobs on other volumes are untouched by it.

**Why the old rule was wrong** (recorded here because the original rule's own rationale was lost when this
repo was imported at `71347e8` - nothing in the history said why the per-volume budget was removed, only
that it must not come back):

- measured on one share, one season, four levels, the same eight episodes, worker limit 1 / 2 / 4 / 8:
  per file **22,2 / 48,9 / 94,1 / 421,3 s**, level wall **3,9 / 3,6 / 3,6 / 7,3 min**, aggregate
  **122 / 135 / 133 / 66 files/h**. The volume delivered the same work per hour at 1, 2 and 4, and **half**
  of it at 8, where a file that takes 22 s alone took 421 s. Eight walks on one volume is not parallelism.
- a *global* cap would have fixed that and broken the case the old rule protected, which is why the ceiling
  is per volume: a slow volume's number never touches another volume's jobs.

**What was built** (`SubSyncService.cs`):

- `WavePolicy.HeavyInUseByVolume` - media reads already running per volume, computed in `PlanStart` from the
  running jobs, their `volumeOf` and `isHeavyIo`. No new accounting and no second pool: the ceiling is a pure
  function of data the wave selector already holds.
- `WavePolicy.WalkCapOf` - the ceiling for a candidate's volume. Null or `int.MaxValue` means none, which is
  what every unmeasured and every fast volume gets.
- `SelectWave` asks `HasRoomOnItsVolume` in **both** passes (spread and fill) and counts a claim with
  `ClaimHeavy`, so raising the worker count cannot walk past a volume's ceiling.
- `WalkCapForProfile(double? msPerCall)`: nothing measured -> no ceiling; `>= 5` ms per read -> 2;
  `>= 100` ms per read -> 1. 5 ms is a wide margin above the class default a read policy starts from
  (0,05 ms) and far below this share (13-46 ms at rest); 100 ms is the bottom of what the share shows while
  thrashing (1419-1613 ms per read observed). The thresholds are data-derived, not tuned by feel, and the
  mapping is unit-checked at each of those numbers.
- `WalkCapOfPath(path)` reads `VolumeProfiles.For(path).MsPerCall()` - R1's per-volume profile, which is what
  the cherry-pick of `9e53170` is here for. Its read-merging premise stays refuted (E5); its measurements now
  have a purpose.

**Invariant rewritten, not gamed.** The three "never throttled" checks and the reflection check
(`WavePolicy has no per-volume budget`) are gone; behavioural checks replace them, including the two that
matter most: "four heavy tasks on an unmeasured volume fill the wave" (unchanged behaviour for everyone not
storage-bound) and "a slow volume's ceiling does not throttle a fast volume's jobs" (the property that makes
the ceiling safe). Renaming a property to slip past the old reflection check was not an option.

**Before/after:** suite 542 PASS / 0 FAIL before (`ab7c296`, cherry-pick only) -> **554 PASS / 0 FAIL** after,
+12 checks. Policy-level before/after on the same queue of five heavy jobs: an unmeasured volume still fills
4 of 4 (as it always did); a volume measuring 20 ms per read now takes **2**, and one measuring over 100 ms
takes **1**.

**Verification status.** Done, in three layers:

1. The ceiling rule itself: unmeasured and fast volumes fill the wave exactly as before; a volume measuring
   20 ms per read takes 2 of 5 queued; one measuring over 100 ms takes 1; light work is never bound.
2. The mapping: 0,05 ms (the class default) -> uncapped, 13 and 46 ms -> 2, 1419 and 1613 ms -> 1, a path with
   no volume -> uncapped, and a `VolumeProfile` fed this share's own latencies -> 2.
3. **The mixed-storage case, on real devices, through the real dispatch entry.** `PlanStart` is called with
   `volumeOf = MediaVolume.Of` and `walkCapOf = WalkCapOfPath` over a queue spanning two genuinely different
   devices - `/opt/data` (`/dev/nvme0n1p2`) and `/dev/shm` (tmpfs) - with the tmpfs volume fed slow reads:
   the wave takes **2 of the 3 slow jobs and all 3 fast ones, 5 of 6 slots**, i.e. the storage-bound volume
   is held and the fast volume is not throttled by it. The run also asserts the two paths resolve to two
   different volumes, so this is the real mount-table identity and not a stand-in.

**What the shim could not do**: the plan was a synthetic slow volume via `slowread.so`, but the shim only
slows processes we launch - it cannot be injected into the running Jellyfin process, and throttling a device
needs privileges this host lacks. Feeding the real profile slow samples and then driving the real dispatch is
the faithful substitute: same code path, same identity, same decision, only the latency source differs.

**Still open**: the four-level curve re-confirmed *with the ceiling in place* on the user's own server. That
needs the new build running there (a dev build, not a release - nothing is pushed), and it is the last piece
of the goal's verification list.

### S30 - a refusal is rendered as a failure (medium, reporting)

Seen by the user in the batch view: `FAIL Extrema livsstilar - Swedish - SUBRIP - unverified: this subtitle was
aligned against the audio (-120 ms offset) and the file holds no other text track to check that against, so
nothing was written...`

The decision is deliberate - an audio-only ruler on a short file can be out by a second or more, and with no
second text track to check it against the plugin declines to write - but the plugin has only three terminal
statuses (`Completed`, `Failed`, `Cancelled`), every refusal is filed as `Failed`, and the page prints
`FAIL <title> - <message>` for anything Failed (`Web/subsyncMain.js:279,1551,1588`). A refusal ("found
something, will not trust it") and a failure ("this broke") are different outcomes rendered identically.

Fix: give refusals their own terminal state or badge ("not written - unverified", "refused") and render it as
its own line, not as FAIL. The plugin log already distinguishes them (`UNVERIFIED:` / `REFUSED:`), so this is
presentation only.

### S31 - a reference subtitle is trusted on plausibility, never on correctness (high, shipped in 2.0.39; the refusal this row proposed was refuted by measurement and replaced by the cross-check)

Question that produced this row: when the plugin checks a sync against a sibling subtitle, how do we know that
sibling is right? **We do not.** There is no ground truth inside the plugin; what exists is four plausibility
checks, and a wrong-but-plausible track passes all of them:

- cue count: a track with too few cues is rejected as a signs track (`The reference subtitle {Track} ... holds
  only {Cues} cue(s) - a signs track, not usable as a reference`);
- span against the file's duration, 3 % (`IsTargetOffTheVideo`: `referenceOnVideo = |referenceSpan/videoSeconds
  - 1| <= 0,03`);
- the shift it may ask for (`MaxSubtitleReferenceOffsetSeconds`, default 30 s - past it the ruler is discarded
  and the audio used);
- framerate plausibility (a span ratio that is not a framerate pair is refused).

What none of those catch is **subtitles for a different film with a similar runtime** - which is not
hypothetical: the Alex S01E01 sidecar investigated on 2026-09-13 held another show's dialogue, and every span
and ratio check passed because the lengths were close. That investigation was withdrawn as a subtitle
acquisition error, but the same shape can poison a sync silently.

**Cheapest signal, currently thrown away**: ffsubsync prints an alignment `score` (seen in a local run:
`score: 126979.776`), and nothing in the plugin captures, logs or judges it. Logging it, and refusing to trust
a *subtitle* ruler whose score is far below what the audio ruler produces for the same file, would catch a
wrong reference at the one point where the engine itself knows something is off.

Also worth having: the reference's provenance in the job result, not only in the log - the log line names the
source track and cue count (`Built this run's reference subtitle for {Video} from track {Reference}: {Cues}
cues`) but the reference file is run-scoped and deleted, so after the fact nothing can be inspected.

**Where this is, in the code** (the triage went looking, 2026-09-14): the plausibility checks are
`Services/SubSyncService.cs:3448-3449` and `:3870` (a forced/signs track recognised by its cue count), `:4241`
(the same test as a log line: "The reference subtitle {Track} for {Video} holds only {Cues} cue(s) - a signs
track, not usable as a reference; syncing against the audio instead"), and `:3541`
`IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds)`, used at `:3638` against the file's duration. The
engine's own `score:` appears in exactly one place in the whole plugin - `:5300`,
`else if (lower.Contains("got score") && lower.Contains("for ratio"))` - inside the parser that turns the
engine's *error* text into a message. Nothing reads the score of a successful alignment, so the one signal that
could tell a right reference from a merely plausible one is thrown away.

**Status, 2026-09-15 - reproduced end to end, and the cheap fix this row proposed does not hold.**

*Reproduced, in the rig, on a real 50-minute episode* (`python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler`):
the fixture carries two embedded tracks - the episode's own track shifted +5 s as the target, and the same track
*stretched 1,02x* as the ruler, i.e. a different cut of the same episode that passes every check the plugin has
(cue count 803, span inside the 3 % rule, demanded shift under the 30 s ceiling). Against the released 2.0.38:

    queued: job=2da83f01... stream=3 ordinal=0 language=eng
    reference: method=subtitle cues=803 track=s:1 file=.../S31 Episode (2026).mkv
    ffsubsync exit=0 after 640 ms
    note: aligned to the reference subtitle s:1 at 24170 ms - check the result; a shift this size usually means
          that track is not the same cut
    job 2da83f01... completed: mode=normal output=.../S31 Episode (2026).SYNCED.eng.srt bytes=56767 change=+24170 ms

The plugin *noticed* (its own note says the shift usually means the track is not the same cut), wrote the file
anyway, and reported the job Completed. The right answer for that fixture is -5 s, so the output is ~29 s wrong
and nothing in the job record says so. That is the Alex class, reproduced with a real episode rather than argued.

**The score, measured (ffsubsync 0.5.1, the plugin's own engine).** The engine prints `score:` and
`offset seconds:` for every run and the plugin discarded both. Now captured and logged
(`[job] ffsubsync alignment: score=66451 offset=24.170 s against reference subtitle s:1`). But as a *refusal*
threshold it does not hold up:

| ruler (same 50-minute episode, same input) | score |
|---|---|
| the file's real sibling subtitle | 198 713 |
| the same track from a 2 % longer cut | **274 721** - higher than the correct one |
| the rig's wrong ruler (target +5 s, ruler stretched) | 66 451 |
| the film's own **audio** (webrtc VAD) for the same file | 53 566 |

So "far below what an audio-reference sync produces" would have called the *wrong* ruler in the rig (66 451)
acceptable against the audio bar (53 566) on that same file, and it would have refused a correct pair (198 713)
on a file whose audio path scores lower. The score separates a ruler of *another film* by 3-70x (2 864 for a
4,9-minute subtitle against a 45,6-minute episode) and it is worth having in the log for exactly that, but it is
not a threshold that can be picked from this evidence without inventing one, which is the failure mode this
register exists to avoid.

**What was implemented anyway, because it is sound and can disagree** (working tree, commit with this entry):
- the score and offset are captured and logged for both reference paths, so a field run can be read (above);
- `ReadPolicy`-style separation of concerns for the reference: `SyncChange` now carries the per-cue
  **dispersion** (IQR and range), and `RulerSpreadTooWide` refuses a subtitle ruler whose cues did not move
  together (a quarter of the configured reference ceiling, so it has no constant of its own). Measured: the real
  sibling scores an IQR of 0,00 s, the same track from a 2 % longer cut 27,76 s. It does **not** fire on the rig
  reproduction above, because there the engine absorbed the wrong ruler as a single +24,17 s shift - dispersion 0 -
  which is exactly why it cannot be the whole fix.
- Nine checks (638 in the suite, green) pin the score parser, the dispersion measurement and the decision.

**What the row still needs, and the shape it should take:** the answer a *subtitle* ruler gives has to be
cross-checked against the film's own audio before it is written, for the cases where the ruler's demand is large
enough to be suspicious (the note above is the plugin already saying "this looks wrong" and doing nothing). The
audio path exists, is cached per file, and is the one ruler that cannot be from a different cut; the cost is one
audio analysis for the suspicious case only. That is a scoped change, and it is what this row should close on -
not a score threshold.

**Part 2, 2026-09-15 - the cross-check, and the two findings that came out of building it.**

What was implemented (working tree, commit with this entry): a subtitle ruler whose demand crosses a third of the
configured ceiling (10 s at the default) is cross-checked against the film's own audio *before anything is
written*, and when the two disagree by more than a tenth of that ceiling (3 s) the ruler is discarded and the
audio's answer is written. Both fractions derive from `MaxSubtitleReferenceOffsetSeconds`, so the default
behaviour is what it was and the numbers follow the setting the user controls.

Proved in both directions on the rig, one command each:

    python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler --s31-ruler other-cut   # 5 of 5
    python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler --s31-ruler correct    # 5 of 5

- the other-cut ruler: `the reference subtitle s:1 and the film's own audio disagree (24170 ms against -5080 ms,
  over the 3 s they are allowed to differ) - that track is not this film's timeline, so it is discarded as a
  ruler and the audio's answer is written`. The audio's -5080 ms is the truth for that fixture, measured
  independently: the film's own audio puts the extracted track at -0,08 s, and the target is that track +5 s.
  With the ruler gone the file has no second track to verify against, so the job reports
  `UNVERIFIED: the audio was the only ruler (-5080 ms offset) ... nothing written` - the wrong answer is not
  written, which is the outcome this row exists for.
- the correct ruler: `the reference subtitle's shift (-20000 ms) is confirmed by the film's own audio
  (-20080 ms, within 3 s) - keeping the reference's answer`, nothing discarded, the sidecar written. The check
  can disagree in both directions, which is what makes it a check.

**Finding 1 (worth its own row): the audio path is not necessarily audio.** The first version of the cross-check
returned the *ruler's own* answer (24 170 ms, the same score to three decimals as the subtitle run), because
`--vad subs_then_webrtc` - the plugin's default - makes ffsubsync read the video's **embedded subtitles** as its
speech signal, and the ruler being checked is one of them. The cross-check now forces `webrtc` for that one run.
The same trap sits under the *audio fallback* generally: on any file that carries subtitle tracks, "the audio
ruler" can be a subtitle ruler wearing an audio name. Filed as S43 below.

**Finding 2: the score still is not a threshold.** Recorded above; the log now carries it, which is what makes
this kind of thing visible in the field.

### The walk ceiling A - shipped and verified in the field (2.0.30/2.0.31, done)

The per-volume ceiling is in the released beta and was measured on fabji's own server, on the season that
first exposed the problem (`Outsiders` S10, eight episodes, same share, `ParallelWorkers` 8):

| | walks | per walk (min) | max concurrent | batch |
|---|---|---|---|---|
| 2.0.30, ceiling never applied | 8 | 5,5 6,6 6,6 6,8 6,8 6,8 6,9 7,0 | **8** | 7,3 min |
| 2.0.31, ceiling applying | 8 | 0,6 0,6 0,7 0,7 0,8 0,8 1,1 1,1 | **2** | 3,4 min |

Per file 8,5x faster, batch 2,1x faster, every job again `UNVERIFIED` (nothing written) so the test repeats.
2.0.30 shipped the ceiling with "no measurement means no ceiling", and the measurement is filled in *during*
the extraction pass while the wave is planned *before* it - so on a cold store the ceiling was never consulted
with anything in it. Two holes: `WalkCapForProfile(null)` and `WalkCapOfPath(null)` (a job whose volume could
not be resolved) both returned "no ceiling". Fixed in 2.0.31 by treating an unmeasured volume, and an
unidentifiable one, as **not known to be fast** - held at 2 until that volume's own reads say otherwise.


**Where this is, in the code** (the triage went looking, 2026-09-14): the four plausibility checks this row
describes are `Services/SubSyncService.cs:3448-3449` and `:3870` (a forced/signs track is recognised by its cue
count), `:4241` (the same test stated as a log line: "The reference subtitle {Track} for {Video} holds only
{Cues} cue(s) - a signs track, not usable as a reference; syncing against the audio instead"), `:3541`
`IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds)` used at `:3638` (the span against the file's
duration), and the offset ceiling the reference may ask for. The engine's own `score:` appears in exactly one
place in the plugin - `:5300`, `else if (lower.Contains("got score") && lower.Contains("for ratio"))`, inside the
parser that turns the engine's *error* text into a message. Nothing reads the score of a successful alignment, so
the one signal that could tell a right reference from a plausible wrong one is thrown away.

### S32 - the ceiling's own log line could not say which of two cases it was in (done)

`walk ceiling: holding <volume> at 2 concurrent media read(s)` chose its wording from the cap *value*, and 2 is
both the unmeasured fallback and what a volume measured between 5 and 100 ms per read gets. On 2026-09-14 it
printed this once a minute, for the whole run:

    walk ceiling: holding /media/synology|192.168.0.110:/volume1/JELLYFIN at 2 concurrent media read(s) -
    this volume has not been read yet, so it is treated as slow until it measures fast

for a volume whose own extraction pass had measured 7,74 ms and 39,25 ms per read minutes earlier
(`extract: ... storage 7,74 ms per read`, `storage 39,25 ms per read`) - so the line could not be told from the
unmeasured case, which is exactly the case S33 is about.

Fixed: the measurement now travels with the cap. `WalkCapOf` is a `Func<SyncJob, (int Cap, string Why)>` and
`WalkCapForProfile` / `WalkCapOfPath` return the reason alongside the number, so the line reads

    walk ceiling: holding /media/synology|...:/JELLYFIN at 2 concurrent media read(s) - this volume measured
    21,0 ms per read, which is storage-bound

or `nothing has measured this volume yet, so it is treated as slow until something does`, never both for the
same situation. Four checks pin it: the two cap-of-2 cases produce different reasons, only the unmeasured case
says nothing has measured the volume, and the measured reasons quote their number and unit. Suite: 563 checks
green. Commit `af66eaf`.

**Verified in the field on 2026-09-14**, on the same share, before and after 2.0.32:

    14:15:34  2.0.31  walk ceiling: holding /media/synology|192.168.0.110:/volume1/JELLYFIN at 2 concurrent
                      media read(s) - this volume has not been read yet, so it is treated as slow until it
                      measures fast
    14:49:09  2.0.32  walk ceiling: holding /media/synology|192.168.0.110:/volume1/JELLYFIN at 2 concurrent
                      media read(s) - nothing has measured this volume yet, so it is treated as slow until
                      something does
    14:50:09  2.0.32  walk ceiling: holding /media/synology|192.168.0.110:/volume1/JELLYFIN at 2 concurrent
                      media read(s) - this volume's last walk moved 17,4 MB/s, which is storage-bound

The 2.0.31 line was printed for a volume the extraction pass had measured at 7,74 ms per read in the same run;
the 2.0.32 lines distinguish the unmeasured case from the measured one, and the third line is the walk's own
measurement, which is the point of S33.

### S33 - a fast volume's ceiling could never lift, because nothing fed the profile (done in code and checks)

`VolumeProfiles` was fed only by reads made through `ReadPolicy`, and reads only happen when the extraction
path reads. On a run whose extractions were all served from the subtitle cache there are no reads at all, so a
volume could keep the conservative cap of 2 for ever - on fast storage as well. The reads that actually cost
the time were invisible too: ffsubsync runs as a child process.

Fixed by giving the profile a second source of truth, **the walk itself**. The plugin knows the file's length
and how long the engine took over it, so a volume can be measured with nothing read to measure it:

- `VolumeProfile.ObserveWalk(bytes, ms)` records the walk, in a list of its own. Kept apart from the read
  samples on purpose: a read sample describes a read call (32 KB in 20 ms is a latency measurement, not a
  statement about transfer rate), and letting a walk's aggregate pose as one read's cost would drive
  `MsPerCall` from milliseconds to minutes. `WalkBytesPerMs()` reports it, byte-weighted and with the slowest
  fifth dropped, exactly like the read-side throughput.
- `WalkCapForProfile(msPerCall, walkBytesPerMs)` takes the **more conservative** of the two signals - either
  one saying "storage-bound" is enough to hold the volume - and names in its reason which measurement decided.
- Thresholds are data-derived: the walk side uses 20 MB/s (local disk walked a 2,4 GB file in 17 s = 137 MB/s)
  and 1 MB/s (nothing measured has come near it, so it is a floor), against the read side's existing 5 and
  100 ms per read. The share's walks measure 2,6-3,5 MB/s, comfortably inside the 2-20 MB/s band that caps at 2.
- The feed happens where the walk is over (`ffsubsync exit=… after … ms`), only when the run used the audio as
  its reference - a subtitle-ruled run never reads the media, so its duration says nothing about the volume.
  It logs what it measured: `this walk moved 1876,4 MB of <path> in 402,0 s = 4,7 MB/s - the ceiling for that
  volume is 2 (this volume's last walk moved 4,7 MB/s, which is storage-bound)`.
- An unmeasured volume keeps the conservative cap of 2: the hole 2.0.30 shipped is not reopened, it is only
  closed on the other side, so a fast volume leaves it as soon as its own walk has finished.

Verified in the suite (577 checks green, commit `3391972`): a walk-only volume measured fast keeps no
ceiling; the same volume at the share's rate is held at 2; one whose walk barely moves is held at 1; reads fast
with a slow walk and reads slow with a fast walk both hold at 2; with neither signal the volume stays at 2; and
end to end through `PlanStart` on a device of its own (`/dev/shm`), eight heavy jobs on an unmeasured volume
start **2**, and after the walk the same eight start **all eight** ("this volume's last walk moved 141,2 MB/s,
which is fast").

**Pitfall found while writing those checks:** `VolumeProfiles` keys a profile by *device*, not by path, so
every temp path on one filesystem shares a single profile. The first version of these checks contaminated each
other through it and failed for the wrong reason. Checks that need a volume of their own must go through the
mapping directly, or use `/dev/shm`.

**Field run on 2026-09-14, on fabji's server with 2.0.32 installed** (thirteen audio-ruler walks, parsed from
`/subsync-logs/subsync.log`; the plugin had to keep the media as its reference, `reference=(default)`):

- **criterion (a), the line names the measurement: passed.** Every one of the thirteen walks logged
  `this walk moved <MB> MB of <file> in <s> s = <MB/s> - the ceiling for that volume is <n> (<why>)`.
- **criterion (c), a local volume goes above two: passed.** Five episodes on `/Media`
  (`/dev/nvme0n1p2`): the first two walks ran under the unmeasured cap, then **3 ran at once** - max concurrent
  **3** - at 84,4 / 85,3 / 85,6 / 91,0 / 91,4 MB/s, 13,8-16,5 s each, `ceiling none (fast)`. That is the case
  that had never been measured, and the ceiling lifted for it without any read ever being taken on that volume.
- **criterion (b), the share stays at two: failed** - max concurrent **5**. Cause found, see S35; the threshold
  it was measured against was mine, not the share's.
- The share itself behaved: eight episodes, 16,8-23,1 MB/s per walk, 33-116 s each, 9,82 GB in 212 s -
  against 5,5-7,0 min per walk at eight concurrent before the ceiling existed.

### S35 - the walk threshold sat inside the share's own measured range (done, verified in the field)

The field run on 2026-09-14 with 2.0.32 failed criterion (b): the share ran **five concurrent walks**. The
threshold is why, and it was set from a bad measurement. `FastWalkMbPerSec` was 20, taken from "the share walks
2,4 GB in 903 s = 2,6 MB/s" - measured while eight walks were already running on that volume, i.e. **while the
volume was being throttled by the very ceiling the number was meant to decide**. Walked at its own pace the
same share measures ~17-34 MB/s. At 20 the ceiling lifted after a 23,1 MB/s walk and dropped after a 17,4 MB/s
one, so a wave planned while it read "fast" put five walks on the share at once (three within 11 s).

Fixed by putting the boundary between the two populations the field has shown: **50 MB/s**, 2,2x above every
walk the share has produced and 1,7x below every local walk (NVMe 84,4-137 MB/s). Commit `804f1f3`, 581 checks
green, shipped in 2.0.33.

**Verified in the field on 2.0.33** (Outsiders S10 on `/media/synology`, `ParallelWorkers` 8, speech cache
cleared first, and eight walks again - the batch repeats because every job ends `UNVERIFIED` and writes
nothing):

- **criterion (b): passed.** 8 walks, **max concurrent 2**, per walk 35,8-54,2 s at 29,4-34,0 MB/s, 9,82 GB in
  209 s = 48,1 MB/s aggregate. The hold line is stable for the whole run instead of flipping:
  `15:04:20  walk ceiling: holding /media/synology|192.168.0.110:/volume1/JELLYFIN at 2 concurrent media
  read(s) - this volume's last walk moved 34,0 MB/s, which is storage-bound`, and the same at 15:05:20 with
  32,1 MB/s. Every walk in the run took 36-54 s against 5,5-7,0 min per walk before the ceiling existed.
- **criterion (c): passed again.** Five episodes on `/Media` (`/dev/nvme0n1p2`) at 92,4-101,0 MB/s,
  12,7-14,9 s each, every one `ceiling none (fast)`, with **3 concurrent** - 5,98 GB in 30 s = 203,9 MB/s.
- The share's walks also got *faster* once the ceiling stopped flipping: 29,4-34,0 MB/s here against
  16,8-23,1 MB/s in the 2.0.32 run, where the cap kept lifting and dropping under the same batch.

**The goal is complete**: the ceiling measures itself from its own reads and its own walks, lifts for local
storage, holds the share at 2, and all three criteria have now been seen in a field log.

### Considered and declined: persisting the volume profiles

Raised while verifying S35: should the plugin build profiles for the different volumes *over time* and remember
them across restarts, rather than measuring each volume live for the life of the process?

**Declined.** What persistence would buy, measured on 2026-09-14: the conservative first wave at 2 costs one
batch's first 13-16 s on local storage (the walks there take 13-16 s), and nothing at all on the share, which
stays at 2 whatever the first wave was. That is the whole prize.

What it would risk is the failure the ceiling exists to prevent, and for a stale entry rather than a fresh one:
a volume remembered as fast because it was fast yesterday, now slow because the share is busy, degraded or
simply has a heavier day, gets no conservative wave at all - eight walks on to one volume, 5,5-7,0 min per walk,
every job in the batch slower. The key does not save the design: it is the volume's own identity
(`/media/synology|192.168.0.110:/volume1/JELLYFIN`), so a share that *moves* gets a new key and no stale hit,
but a share that stays put and gets slower keeps its key and its stale "fast".

And the field has now shown how volatile the number itself is. The same share measured 16,8-23,1 MB/s in one
batch and 29,4-34,0 MB/s in the next, an hour apart, a swing of about 2x. S35 *is* the story of a threshold set
from one measurement taken under different conditions than the run that used it - 20 MB/s, derived from a walk
measured while that volume was already being throttled, which put five concurrent walks on the share. Persisting
throughputs is that same mistake with a longer gap between the measurement and the decision.

The live profile already adapts, and that is the right amount: per volume, a 10-minute half-life so a share that
was busy ten minutes ago stops being believed, the slowest fifth of samples dropped so the tail does not carry
the average, two independent signals with the more conservative winning, and a fresh measurement at every
restart - which is the safety property persistence would have to give up. It costs a volume at most one
conservative wave to re-earn that, and the extraction route that shares the same profile re-derives just as
cheaply.

**If this is ever revisited**, the bar to clear is not "it saves 15 s a restart" but a run in which the first
wave at 2 is *hurting*: a fast volume, many files, every batch, after every restart. Then the cheaper fix is to
re-plan immediately after the first walk finishes (already the behaviour) rather than to trust a remembered
number. Persisting the profile would also need a staleness rule - a maximum age, and a rule for what happens
the first time a remembered-fast volume measures slow - which is a design, not a cache.

### S36 - a batch's first planning pass can see a worker limit of 1 before the configuration has loaded (low)

Three batches in the 2026-09-14 log opened with a dispatch line reading `limit 1` and planned nothing
(`starting 0`), then seconds later the next pass read `limit 8` and started work:

    15:03:37  dispatch: starting 0, running 0, limit 1, queued 8, batch 681a0a2b
    15:03:37  dispatch: starting 1, running 0, limit 1 -> and the next line limit 8

Same shape for `341dc2d4` (14:54:37) and `e90a146c` (15:07:11). The plugin's default `ParallelWorkers` is 1 and
a batch queued before the settings provider has loaded is planned against that default, so its first pass is
throttled and the batch waits for the next pass to start. Harmless in these runs (the delay was seconds) but it
is the plugin's own default deciding how a user's batch starts, and it is why a batch's log opens with a limit
the user never chose. Not fixed: recorded while verifying S35, one item, one commit.

### The first wave on any volume is deliberately conservative, and it is why a small local batch shows three walks

A plugin restart empties the volume profiles (they are in memory on purpose - a profile restored after a reboot
may describe a share that is no longer there), so the first wave on every volume is planned before anything has
measured it and is held at 2. On the 2026-09-14 local run that is the whole of the user-visible "not all workers
were used": five files on local disk, two walked first, the remaining three then ran at once - **3 concurrent
out of 8 workers**, which is the arithmetic of the first wave rather than under-use. A local batch with eight or
more files would reach the full worker count after that first wave; eight local files have not been run yet.
The only way to remove even that one conservative wave is to persist volume profiles across restarts, which is
a separate decision with its own staleness question (see `VolumeProfile`'s own remarks).

### S34 - the only way to force re-analysis is an API call (low, done: refuted)

`POST /SubSync/SpeechCache/Clear` exists, is documented safe, and is what a re-test needs after a run has
cached its audio analyses - but it is not wired to anything in the page (no UI hits for it). Anyone wanting to
re-walk a file has to call the API with an API key. A button in the plugin's settings would make it reachable.

**Refuted, 2026-09-14.** The page does call it: `Jellyfin.Plugin.SubSync/Web/subsyncMain.js:2118` is inside the
click handler for the page's own clear-cache button (`var clearCacheBtn = $('ss-clearcache')`, `:2105-2120`), and
it reads the result back to the user (`Cleared N cached audio analysis files`). The row was written from a grep
that missed the call, and said "no UI hits for it". Correction owed: the advice given on 2026-09-14 that the only
way to clear the audio cache is an API call was wrong - there is a button, and it is the one to use.

### Housekeeping - the open rows that carry no severity (low)

77 rows are open; 53 of them sit in the older tables that have no severity column (GUI, API surface, queue and
file-writing items), so "what is left" cannot be answered by severity for most of the list. Worth one pass to
give them a severity and drop the stale ones - **S13** in particular describes a harness defect
(`tests/backend/slowread.so` missing) that no longer exists: the shim is in the tree and was used for every
measurement on 2026-09-14.

### Parked, low priority — cache the decoded audio for re-analysis (from S28, 2026-09-14)

Not a row to act on; recorded so the numbers are not lost. Keying an audio-only copy next to the speech
analysis would turn *re*-analysis into a local read: today the analysis key includes the VAD method and the
engine version, and the cache expires at 30 days / 250 MB, so any of those changes re-walks every file
(900 s each on fabji's share, versus 8-9 s against a local audio copy). Cost: ~4 % of media size in local
disk, and the walk still has to happen once per file, which is why it is not a speed fix for first-time
analysis (measured: rip 901 s + analysis 9 s vs the engine's own 903 s). Revisit only if VAD-method changes
or cache expiry become a routine annoyance.

### Not a plugin defect — the 14 failed `Portkod 1321` jobs (environment, fabji's side)

The 14 `job … failed: mode=ultimate` lines (00:24:21–00:26:01, all ERROR level) are one file each of
`Portkod 1321` (S01E01–S02E08) and the cause is outside the plugin:

```
job 9ba588e4… failed: mode=ultimate item=54c5c734-… stream=0
…IOException: Cannot write the synced subtitle to '/Media/TV Shows/Portkod 1321/Season 1': the Jellyfin
user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the…
```

That is a permissions/mount problem on the media share, **not a plugin defect** — the plugin detected it,
said exactly what was wrong, wrote nothing and touched no source file. Recorded here so the 14 failures in
this run are not counted against the plugin later. Worth noting the effect was moot anyway: those tracks also
came back `reference: method=index-none … reason=no subtitle blocks found`, so they would have fallen to the
audio ruler and written nothing (see S8).

Related, not new: **F8** — the page-side half of the chunked queue is implemented in `7f474d5` (committed
locally, deliberately unpushed, held for fabji's go-ahead); the F8 row stays `open` until it ships.

## Ask the user before coding these

- **D1** — `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it
  (the button now asks twice — F29, settled — but the endpoint still has no per-job form)
- ~~D4~~ — settled 2026-09-11: merged into one settings surface (`configPage.html` is a pointer)
- ~~S8~~ — settled 2026-09-11: external sidecars fall back to the audio; an embedded track with no sibling
  reference is not written from the audio alone

## Cannot be tested on this machine

- a **full disk** and a **separate filesystem for staging** both need a small filesystem, which
  needs root. Record them as `blocked` with the reason instead of working around them.


- `MaxSubtitleReferenceOffsetSeconds` bounds what a *subtitle* reference is trusted for. A shift past it means that track is not the same cut: the reference is discarded as a ruler and the subtitle is aligned against the audio instead (one audio analysis per file, cached), rather than the file being refused. Changed in 2.0.17; before that the ceiling ended the job.

### S37 - a walk measured the moment as well as the volume, so a mixed batch throttled the fast one (high, fixed)

Reported from the field on 2026-09-14, from a mixed batch (`Outsiders` on `/media/synology` plus `Tusen bröder`
on `/Media`, 86 tasks): the series on the local NVMe ran at two walks at a time while the share's 75 walks ran
alongside it, against 6 concurrent in the same process when it was alone.

The volume's own numbers, same process, an hour apart:

| when | in flight with it | per walk | ceiling |
|---|---|---|---|
| 15:50-15:52 | only its own jobs | 68,2-90,8 MB/s (9-15 s) | **none (fast)**, 6 concurrent |
| 16:02-16:03 | the share's walks, 75 of them | **45,6-58,0 MB/s** (16-39 s) | **2**, and then none again, flipping |

and the line that shows the ceiling being decided by the second row:

    16:02:47.362  walk ceiling: holding /Media|/dev/nvme0n1p2 at 2 concurrent media read(s) - this volume's
                  last walk moved 45,6 MB/s, which is storage-bound

After that the batch settled at `dispatch: starting 1, running 1` for twenty-five minutes. **A walk's throughput
is the storage's speed and everything else that was running**: 45,6 MB/s is a true statement about that moment
and a false one about that disk, and it straddled the 50 MB/s boundary, so the ceiling could not make up its mind.

**Fixed** by refusing to judge a volume from a contended walk: `VolumesOtherThan(thisVolume, otherVolumes)`
(counted over every running job's volume) decides, a walk with anything in flight elsewhere is logged with what
it moved and **not** fed to the profile, and the feed still happens in exactly one place. Checks: nothing in
flight is that volume's own measurement, another volume in flight is contention, a volume's own concurrent jobs
are not, and every other volume counts. 603 checks green. Commit `8373662`.

The share's half of that batch was correct throughout - 2 concurrent, 29-31 MB/s, stable for 75 walks - so this
was one volume being misjudged, not the ceiling misbehaving.

### S38 - a cold mixed batch has no uncontended measurement of its fast volume (high, fixed and verified in the field)

Confirmed in the field on 2026-09-14, on the 2.0.35 run, and it is why that run used two workers on a volume with
six free slots:

    17:33:22.661  [c8…] this walk moved 1235,5 MB of /Media/…/Tusen bröder S01E01.mkv in 17,7 s = 69,9 MB/s, but it
                  is not being used to judge that volume: 2 job(s) on another volume were being read at the same time
    17:33:22.723  … S01E02 68,6 MB/s, same reason
    17:33:54.871  … S01E04 76,6 MB/s, same reason
    17:33:54.952  … S01E03 70,4 MB/s, same reason

Four walks on the fast volume, **none of them usable**, because the share's walks were running throughout. So both
volumes still said `nothing has measured this volume yet` (five hold lines for the share, two for the local disk)
and both stayed at the conservative two, while the dispatches read `starting 2 running 0 limit 8 queued 55` for
minutes. S37 was doing its job exactly - it refused the contaminated samples rather than being misled by them -
but refusing evidence is not the same as having any.

**Fixed** by giving a volume with nothing measured about it one timed read of its own: 16 KB, at a third of the
file rather than at the header (a header is often in the page cache and says little about the disk under it),
once per volume per process (claimed by `VolumeProfile.TryBeginProbe`), taken in the job's own thread just before
the audio-ruler engine starts and **never while the queue lock is held** - the scheduler must not touch storage on
its way to a decision (B3, B5). It records through `Observe`, so the volume can then be classified on its own
latency, and it logs the result: `volume <key> had nothing measured about it, so it was read once: 16 KB took
1,42 ms - the ceiling for that volume is 2 (…)`.

**The recalibration this forced.** With the read reference floored at 0,25 ms, the thrash tier at 100x would mean
25 ms per read - so fabji's share, at 13-46 ms, would have been held to **one** walk at a time where its own
measurements say two is its best. `ThrashingReadRatio` is now 400, which is 100 ms against the floor: the old
absolute threshold, reproduced from a ratio. Ratios have to be chosen so the old behaviour falls out of them, or
"portable" quietly means "different".

**Verified**: 625 checks green, including the probe's one-shot behaviour (on standalone profiles, because
`VolumeProfiles` keys by device and a path under /dev/shm shares its profile with every other check that read
there), that one measured read is enough to hold a share at 2 and let local storage go, and that the thrash tier
still means 100 ms rather than 25. A source-level check pins the call to the job's own thread and to one site.

**Field run owed** on 2.0.36: in a mixed batch the local volume should lift within a wave or two of its first job
starting, while the share stays at 2. The 2.0.35 run could not evaluate S39's ratio criteria at all - no walk was
ever *used*, so no ratio was ever computed - which is the other reason that run has to be repeated.

### S41 - one cold read can decide a volume's class, and a loaded share reads cold (high, done: reproduced and fixed on the rig, 2026-09-15)

The first field run of the probe (2.0.37, 2026-09-14) is a success and a defect at once. The successes first,
because they are what the last four runs were missing:

    18:05:35  volume /dev/nvme0n1p2 had nothing measured about it, so it was read once: 16 KB took 0,83 ms -
              the ceiling for that volume is none (this volume measured 0,83 ms per read, which is fast)
    18:06:42  dispatch: starting 2, running 6, limit 8, queued 44, batch f3f9aefe

Eight concurrent jobs - the fast volume filling the pool the moment it had a measurement of its own, and nine of
its walks used with `ceiling none (this volume measured 0,83 ms per read, which is fast)`, at 92,7 / 90,8 / 69,1 /
61,9 / 61,8 / 58,2 MB/s. S38's fix does what it was written to do.

**The defect is the same probe on the slow volume:**

    18:04:21  volume 192.168.0.110:/volume1/JELLYFIN had nothing measured about it, so it was read once: 16 KB
              took 231,34 ms - the ceiling for that volume is 1 (this volume measured 231,3 ms per read, which is
              thrashing)

and it held there for the whole run - fifteen hold lines from 18:04:10 to 18:18:29, all reading
`this volume measured 231,3 ms per read, which is thrashing`. So the share ran **one** walk at a time where its
own measurements say two is its best.

Why: the probe is a *single* cold read, taken a third of the way into a file, at a moment when that volume may
already be loaded - 231 ms against a steady state of 13-46 ms measured at rest. It lands in the profile as one
sample, and because no *other* volume had eight samples yet (the local disk had only its own probe at that point,
and its eight walks came minutes later) there was no reference to compare against, so the documented absolute
fallback decided: 231 ms is over 100 ms, which is the thrash tier, which is one walk. The ratio machinery S39
added never got to speak.

**Direction of the fix**: take the probe as a small number of reads and record the median rather than one cold
sample; and make the probe line print what it is standing on (how many samples the median came from), so a verdict
that rests on one read is visible as such in the field instead of looking like a measurement of the volume. The
fallback tier probably also wants to require a minimum sample count before it will call a volume thrashing - a
single read is not a steady state.

**Verified the same run**: the fast volume's nine used walks, the eight concurrent jobs, and S37's contention rule
holding (three local walks were refused as contended rather than counted).

**Reproduced, fixed and verified on the rig, 2026-09-15.** `tests/rig/run_scenario.py` starts a local Jellyfin
with the `slowread.so` shim in front of one media directory, queues a batch through the plugin's own API and
asserts on the plugin log; `results.json` keeps every run. The shim was given the field's shape rather than an
average: every read costs 13 ms, and the **first read on a freshly opened handle costs 231 ms** (new knobs
`SLOWREAD_FD_FIRST_MS` / `SLOWREAD_FD_FIRST_READS`), which is exactly what the probe does - it opens its own
handle and reads once. The two volumes are genuinely different filesystems (`media-slow/` on the disk, the fast
library on a tmpfs): the plugin names a volume by the device behind the longest mount point, so `media/` and
`media-slow/` would have been one volume and produced one verdict.

The command, one line:

    python3 tests/rig/run_scenario.py --scenario s41-cold-read --timeout 240

**Before the fix (2.0.37), the defect, verbatim:**

    2026-09-14 22:42:00.970Z INFO  volume /dev/nvme0n1p2 had nothing measured about it, so it was read once: 16 KB
        took 231.13 ms - the ceiling for that volume is 1 (this volume measured 231.1 ms per read, which is thrashing)

Failing assertions, from the same run: *the slow volume's verdict names how many reads it stands on* (the line
says "read once"), *a verdict does not rest on a single read* (`samples=0 (one cold read)`), and *the shimmed
volume is allowed the walks its own state says (2)* (`ceiling=1`). The fast volume came out `none` in that same
run, so the two verdicts were read side by side - production behaviour, reproduced here.

**After the fix (2.0.38), the same command, 6 of 6 assertions:**

    2026-09-14 22:49:44.681Z INFO  volume /dev/nvme0n1p2 had nothing measured about it, so it was read 3 time(s) of
        16 KB: median of 3 reads took 13.11 ms (slowest 81.14 ms) - the ceiling for that volume is 2 (this volume
        measured 13.1 ms per read, over 3 read(s), which is storage-bound)

    PASS  the slow volume's verdict names how many reads it stands on
    PASS  a verdict does not rest on a single read - samples=3 (one cold read decides at 1), median=13.11 ms, verdict=2
    PASS  the fast volume gets no ceiling      (volume shm ... ceiling for that volume is none ... over 3 read(s), which is fast)
    PASS  the shimmed volume is allowed the walks its own state says (2)

The single cold read is still in that median - it is the `slowest 81.14 ms` - which is the point: it no longer
decides. The steady case is pinned separately against fabji's own measured share (10 ms per read, 11 MB/s):

    python3 tests/rig/run_scenario.py --scenario s41-steady

    PASS  the shimmed volume is allowed the walks its own state says (2) - ceiling=2 because this volume measured
          11.6 ms per read, which is storage-bound
    PASS  the fast volume gets no ceiling

**The change** (commit with this row, 2.0.38): `ProbeVolumeIfUnmeasured` takes `ProbeReads = 3` reads of 16 KB at
separated offsets (`ProbeOffset`) and feeds each to the volume's profile, so the figure the ceiling is decided
from is a median of three; the line reports `median of N reads` and the slowest sample; and the read-side tier
gained `ThrashTierMinReads = 2`, so a figure a *single* read produced is held at two walks with the reason
*"but that is one read and one read is not a steady state, so it is held at 2 until the volume has been read
again"*. Four checks pin it (629 in the suite, green before the release commit).

### S40 - the enqueue itself is the slow part of a batch's start (high, shipped in 2.0.42; the field run that names the lock holder is the outstanding step and it is the user's test)

Measured on 2026-09-14 while a 55-task batch was being queued, one line per item that took longer than it should:

    enqueue slow: item=0 ms, sources=2 ms, settings=0 ms, log=21276 ms, total=21278 ms stream=2 video=/media/synology/…
    enqueue slow: item=0 ms, sources=1 ms, settings=0 ms, log=8451 ms,  total=8453 ms  stream=0 …
    enqueue slow: item=0 ms, sources=2 ms, settings=0 ms, log=7954 ms,  total=7957 ms  stream=2 …
    enqueue slow: item=0 ms, sources=8 ms, settings=3 ms, log=7861 ms,  total=7873 ms  stream=3 …
    enqueue slow: item=0 ms, sources=3 ms, settings=0 ms, log=15422 ms, total=15426 ms stream=2 …

Item lookup, source resolution and settings all cost nothing; **the `log` phase costs 8-21 seconds per queued item**,
and the batch is queued one item at a time through that path. That is most of ten minutes spent before a 55-task
batch has even started, and it is what "the batch takes a while to start" is. This is the first run in which the
line was visible, and it is not the ceiling: the dispatcher showed `limit 8` throughout, so nothing was being held
back by a cap.

**Diagnosed 2026-09-15, and the first reading of it was wrong - here is what the instrumentation showed.**
The phase the field reported as `log` is four things (two dictionary writes, one line to the plugin log, the queue
lock, and waking the pump), so the line now reports them separately, and `SUBSYNC_ENQUEUE_TRACE_MS` lowers the
threshold because local storage never reaches the field's 250 ms.

What the rig then measured, and what it did not:

| the worst of 56 enqueues | total | log | state | logWrite | queueLock | wakePump |
|---|---|---|---|---|---|---|
| local storage, before any change | 211 ms | 190 ms | 0 ms | 0 ms | **190 ms** | 0 ms |
| local storage, after the changes below | 211 ms | 190 ms | 0 ms | 0 ms | **190 ms** | 0 ms |

**No improvement, and the instrument that was supposed to explain the phase explained it away**: `queueLock` here
is the time spent around the acquisition - the wait *plus* whatever the enqueueing thread itself was scheduled
out for - and on a box running two 56-task batches, a cancel and an extraction at once it is noise, not
contention. The plugin log costs nothing (`logWrite=0` on every one of 280-392 lines), so the row's guess about
flushing the log is dead as well. What the new `queue lock slow: holder=…` trace does show is the plan itself: it
takes ~55 ms (`holder=pump-plan-outside-lock ms=55`) and it used to run inside that same lock, so every enqueue
landing in that window waited for it.

Two changes are therefore in the tree, structurally pinned rather than shown as a local speedup, because a local
measurement cannot show them:

- the pump plans **outside** the queue lock: it snapshots the queue under a short lock and runs `PlanStart` after it
  (a check brace-matches the lock block out of `PumpAsync` and fails if `PlanStart` is inside it - a timing check
  would pass on a fast disk with the defect still present);
- cancelling no longer writes a log line per job while holding that lock: one to Jellyfin's sink and one to the
  plugin log, per cancelled job, inside the critical section the enqueue waits on (`holder=cancel-batch` /
  `holder=cancel-all` report their hold time now).

**What is still unknown** is who holds the lock for 8-21 s on the field's share. The local figure cannot answer it,
and guessing produced the wrong answer once already. Next step: the field run that queues a big batch, read
`enqueue slow: … breakdown:` and `queue lock slow: holder=…` together - the second names the holder, the first says
how long it had to wait. Everything needed for that is in the tree; it is held unpushed until it is measured, not
shipped on an argument.
### S39 - a volume was judged by constants measured on one machine (high, done: the ratios decided a ceiling on the rig, 2026-09-15)

Implemented in `503f292`: the rule compares each volume against the best this machine has measured
(`FastestReadMsPerCall()`, `FastestWalkBytesPerMs()` in `Jellyfin.Plugin.SubSync/Services/VolumeProfiles.cs:412`),
with hysteresis so a cap cannot oscillate, and names both numbers in its reason. What is still owed is the field
proof. On 2026-09-14 the ratio path never once decided a ceiling; the run fell through to the absolute fallback
instead, which is itself the finding that produced S41:

    walk ceiling: holding 192.168.0.110:/volume1/JELLYFIN at 1 concurrent media read(s) - this volume measured
    231,3 ms per read, which is thrashing

The absolute fallback's own words, not a ratio's. S39 closes when a mixed run shows a ceiling chosen between two
measured numbers.

**Proved on the rig, 2026-09-15.** What the row owed was a ceiling chosen *between two numbers this machine
measured*, and it now does: one volume sets the bar with its own walks, another is judged against that number.

    python3 tests/rig/run_scenario.py --scenario s39-ratio --timeout 600

The two volumes are different devices — the reference on the overlay filesystem (`/tmp/s39-fast`, a library the
scenario registers itself), the judged one on the shimmed share — because the plugin names a volume by the device
behind its longest mount point. The reference needs 8 walks before it can set the bar, so the scenario gives it
12 (one walk is discarded for contention often enough that 8 exactly is a coin toss: the first attempt measured
7 of 8 and had to be repeated).

    12 walk line(s)  e.g. this walk moved 83.6 MB of /tmp/s39-fast/Walk Reference 03 (2026).mkv in 0.5 s = 172.1 MB/s
    judged volume:   this walk moved 83.6 MB of /opt/data/jf12test/media-slow/Judged Clip 01 (2026).mkv in 70.0 s = 1.3 MB/s
    the line:        walk ceiling: holding /opt/data|/dev/nvme0n1p2 at 2 concurrent media read(s) - this volume's last
                     walk moved 1.3 MB/s against the best 162.7 MB/s this machine has measured (0.01x), which is
                     storage-bound

That is the ratio, both numbers, and the cap it chose (2) — not a constant anyone picked for one machine. The
hold line only exists while a volume is *at* its cap, which is why the scenario fills it: three judged jobs are
queued together, two run and the third is held, and that is the moment the scheduler says what the ceiling came
from. Two earlier attempts are worth remembering: judging before the judged volume had a walk of its own fell
back to the read tier (the line then read `this volume measured 12,9 ms per read`, the absolute behaviour), and
R1's note about the rig measuring one axis still applies - the walk volume's figure moves with the page cache
(172-236 MB/s across runs), which the ratio absorbs because it compares two numbers from the *same* run.

Evidence: `tests/rig/results.json` (scenario `s39-ratio`, 7 of 7 assertions), rig scenario
`tests/rig/run_scenario.py:scenario_s39_ratio`.

### S42 - a walk is measured against the file's length even when the engine never read the file (medium, open)

Found while proving S39, 2026-09-15, and measured rather than argued: the same file, on the same shimmed volume,
with the same shim settings, "walked" at **280,1 MB/s** with the audio analysis cached and at **1,3 MB/s** without
it (83,6 MB in 0,3 s against 83,6 MB in 70,0 s). The walk figure is `MediaLengthOf(videoPath) / engineWatch`
(`Services/SubSyncService.cs:4828`, `:4850`), so when ffsubsync runs on a cached speech file it reads no media at
all and the file's length is divided by the engine's own time. That number is then fed to the ceiling
(`ObserveWalk`), which is how a share can read as its fastest volume: the ceiling it produces is
`this volume's last walk moved 280,1 MB/s ... which is fast`.

Not fixed here: correcting it means deciding what a walk *is* when the read did not happen (skip the sample, or
measure the extraction pass's own reads instead), and that is a decision for its own item, not a rider on S39.
Until then, any walk-based ceiling on a run whose speech was already cached is suspect, and the tests/rig S39
scenario clears the caches before the walk it needs for exactly this reason.

### S43 - "the audio ruler" is whatever the VAD picks, and the default VAD reads subtitles (high, shipped in 2.0.40)

Found while proving S31 part 2, 2026-09-15, measured rather than reasoned. The plugin hands ffsubsync the media
file as the audio reference with `--vad subs_then_webrtc` (the default, `AllowedVadMethods`), and that VAD takes
the video's **embedded subtitle tracks** as the speech signal when it has any. So on a file with subtitles, the
"audio" path can be a *subtitle* path - and in the S31 fixture it was the very track being cross-checked:

    cross-check run: reference=.../speech-cache/7580d625....mkv input=.../shared/.../subtitle_4.srt
    ffsubsync alignment: score=66451 offset=24.170 s against the audio (cross-check of a subtitle ruler)   <- the ruler's own answer
    ... -5080 ms against the film's real answer once `webrtc` is forced instead

The same run with `--vad webrtc` gives -5080 ms, which matches the film's own measurement (-0,08 s for the
extracted track, the fixture's target being that track +5 s). The cross-check in S31 now forces `webrtc` for its
one run, but the general path is open: `PrepareAudioReferenceAsync` calls the audio analysis a measurement of the
*audio*, and with this VAD on a file that has subtitles it may be a measurement of a subtitle track - including a
wrong one, which is precisely the case the audio fallback exists to escape. Next step is a rig scenario that
queues an audio-only fallback on a file whose embedded subtitle is the wrong ruler and shows whether the fallback
inherits the ruler's answer; the fix is either forcing `webrtc` in the analysis or documenting the VAD's
behaviour where the fallback is decided.

**Scoped, 2026-09-15 - how often, and what it costs.** The mechanism is `--vad subs_then_webrtc`, the plugin's
default (`Configuration/PluginConfiguration.cs:62`), which makes ffsubsync take the video's **embedded subtitle
tracks** as its speech signal whenever it has any. The precondition is the norm on this machine: of the media in
the rig, every file carries embedded subrip tracks except one (the episode carries **50**, `Wide Multi-Language`
8, each fixture 1-3), and the plugin hands the engine the *video* as its "audio" reference
(`SpeechCache.CreateReferenceLink`), so "the audio path" reads subtitles on essentially every real file.

Whether that helps or hurts depends on which track the engine picks, and both have now been measured:

| the container's track the engine picked | the answer | compared with |
|---|---|---|
| the film's own track (`Walk Reference 01`, 27 cues, target = that track +20 s) | **-20,00 s**, exactly right | `--vad webrtc` gave -18,21 s on the same 2-minute file |
| a wrong-cut track (the S31 fixture, 803 cues) | **+24,170 s**, the wrong track's own answer | the film's own audio says -5,080 s |

So the label is wrong every time and the *outcome* is right only when the engine happens to pick a track that
matches the film. Worse, when it does, the plugin's own reference-selection checks (cue count, signs-track
detection, span against the file) have been bypassed: the engine chose the track, not the plugin.

**The rule to implement** (next step): the plugin decides and the engine does not. Wherever the plugin intends
the *audio* to be the reference - the ruler fallbacks, the cross-check, and the ordinary no-usable-sibling case -
it passes `--vad webrtc` and logs the VAD it used, so "audio" means audio; `subs_then_webrtc` stays only where the
plugin itself supplied a subtitle reference it has already vetted. Cost: one audio analysis per file, which the
speech cache already pays once, and the measured precision trade above (1,79 s on a 2-minute clip) needs a wider
sample before it is claimed as general. A rig scenario follows the fix: a file whose *only* reference is the audio,
with a subtitle in the container that contradicts it, asserting the answer matches the film rather than the track.

**Implemented and shipped in 2.0.40** (suite green; the rig pair below is the proof, and the artifact was verified in the catalogue: 2.0.40.0, md5 matching the manifest, the rule present in the published DLL). The rule is one
function and one constant, so a check can disagree with it:

```csharp
private const string AudioReferenceVad = "webrtc";
internal static string? VadForReference(string? referenceSpec)      // null = the configured method stands
    => referenceSpec is null ? AudioReferenceVad : null;
```

Every run in the job flow builds its arguments through it - the main run, the over-ceiling fallback, both
wide-window retries and the verification run - the cross-check keeps using the same constant, and the speech-cache
key now names the VAD the engine is actually given rather than the configured one it is not given (two analyses
that used to share a key can no longer). A run that overrides the configuration states so, once, with the reason:

```
[job] reference is the audio, so the engine is given --vad webrtc: with the configured 'subs_then_webrtc' it
      would read the video's own subtitle tracks as its speech signal, and which track that is, is the engine's
      choice rather than the plugin's
```

Checks: the configured method stands for a vetted subtitle reference; the audio reference forces the audio VAD;
the override reaches every call site; the speech key follows the VAD; the log line exists.

**Measured where the engine's own choice actually diverged.** The scenario `s43-audio-is-audio` builds a file whose
only subtitle track is 30 s out of sync, so the audio is the only usable reference, and asserts the engine's answer
(through the alignment line part 1 added) plus the VAD statement:

| build | the VAD statement | the engine's answer | scenario |
|---|---|---|---|
| released 2.0.39 | absent - nothing said which signal ran | -30,08 s (the film's answer) | FAILED (1 of 3) |
| working tree | present, naming the override and the reason | -30,08 s | **PASSED** (3 of 3) |

Honest scope: on *that* fixture the released build already answered correctly, so it is not evidence of a wrong
answer - the engine did not follow the subtitle route there. It is evidence that the signal is now fixed by the
plugin and stated in the log instead of left to the engine. The measurement that shows the subtitle route producing
a **wrong** answer is the S31 fixture (two tracks, one a wrong cut), where the same job shape returned the wrong
track's own +24,170 s with the default VAD and the film's -5,080 s with the audio VAD forced - exactly the
divergence this rule removes, shipped in 2.0.39 for the cross-check and now for every audio run. The engine's own
output confirms the route exists: with `subs_then_webrtc` it logs `extracting speech segments from subtitles`
(`ffsubsync.py:192`).


| decision (rejected, evidence kept) | C1 (this session) | low/medium (cost: faster but unreliable) | tympanix/subsync's sampled audio reference (--multi-segment-sync): 16 segments answered correctly at two different shifts on the episode fixture and reduced the pass; but 8 segments answered 63 s and 142 s wrong on the same file in a shape the plugin's guard accepts as PAL correction (ratio ~1,03 over a 17 min fixture, 1,01082 over a 48 min film). No segment count is both reliably right and faster, so the setting was not adopted; the row lives at `tests/backend/engine_sampling_bench.py` with the numbers above, and the plumbing (`AudioSampleSegmentsOf`, `AudioSamplingArgs`) has been removed from the plugin (`PluginConfiguration.cs`, `SettingsValidation.cs`, `SubSyncService.cs` verified: none of those names remain). Verdict: out — faster, but no measured reliability win; leave to the engine's own multi-segment sync, which the plugin keeps (`PiecewiseArgs`). |
| done (shipped in 2.0.43) | C4 (this session) | medium | **The sweep's state file was rewritten once per subtitle.** `SweepState.Record` saved the whole JSON document on every record, so a sweep of N subtitles cost N writes of a document that grows with N. Measured in the suite: 2 000 records = **2 000 writes / 4 466 ms / 1 735 MB written** before, **80 writes / 173 ms / 68 MB** after (25 records or 30 s per write), flushed when the pump drains its queue, at the end of a sweep and at shutdown, so a record cannot be lost by the flush moving. Checks: 100 records cost exactly 4 writes, a reload sees every entry, and the state still writes when a file it tracks has gone. |
| done (shipped in 2.0.43) | C2 (this session) | low | piecewise (split-penalty) alignment with the plugin's own guard: fixture `step-out.srt` (20 s step at 17-min midpoint) reads as two flat segments; the linear ratio is refused by `IsRescaleAcceptable` (ratio > 1,02); `PiecewiseHolds` holds and `far-out.srt` is refused. 6 new checks (`tests/run_checks.py`) + 5 tested via `engine_sampling_bench.py`. Keeps the engine flag (`PiecewiseArgs(config)`). Verdict: in, **shipped in 2.0.43** as the `SplitPenalty` setting (0 = one global offset, the plugin's old behaviour; the settings page exposes it). Measured for the changelog: on a fixture whose second half is 20 s out, the single-offset answer leaves that half 20,0 s wrong and the penalty brings both halves within 60 ms. |
| decision (rejected, with reason) | bazarr (GPL-3.0) | low | subtitle provider/download layer (`subliminal_patch`) — GPL-3.0, so study only; its batch-download and multi-language query patterns are the same family the plugin already solves (batch + queue + settings). Nothing here beats our scheduling / volume profile / sweep state approach; no build. |
| decision (rejected, with reason) | AutoSubSync (GPL-3.0) | low | same-language download/sync layer; same GPL barrier; no feature missing from our architecture that this repo provides (same settings + batch + error-shape family). No build. |
| decision (rejected, with reason) | autosubsync (MIT) | low | `quality_of_fit.py`: shape-based confidence score for shift-curve peaks (monotonicity, prominence, non-edgeness, threshold 0,75). Useful idea but no C# equivalent in our fixtures produces it; no measured win. Study kept, no port. |
| decision (rejected, with reason) | subsync (Apache 2.0) | low | bounded-slice idea already covered by C1 (rejected); no extra bulk/grouping mechanism (the repo is the engine layer, not scheduling). No build. |
| done (this session) | jellyfin-subsync (MIT, Marnalas) | low | the manual reference-picker feature is explicitly excluded (`GOAL_PROMPT.md` / user's instruction); everything else fair game, and the repo is the same-language/same-platform code this plugin's architecture derives from. The only portable addition found (piecewise/split-penalty) is already reflected in our engine arguments (`PiecewiseArgs`) and verified via C2. Verdict: no direct merge beyond what's verified, keep separate. |
| decision (examined, not applicable) | ext-autosubsync (this session) | low | **AutoSubSync's extractor never parses a container.** `subtitle_extractor.py:extract_subtitles()` (186) lists subtitle streams with `ffprobe -select_streams s -show_entries stream=index,codec_name:stream_tags=language,title` (204-215) and then hands **one** ffmpeg process a `-map 0:<stream> -c:s <codec> <out>` output per compatible track (255-267), so its cost is a full demux of the file - the cost this project measured and removed (S11: the engine demuxing the 2.4 GB episode hung; S28: one walk of it at the share profile is 901 s, against the index route's 0.4-3.8 MB / ~1-3 k reads). Its only safeguard is `choose_best_subtitle()` (158): among the files it just extracted, pick the one whose **cue count** is closest to the reference subtitle's (179-182), or the longest file when the reference has no timestamps (161-170, via `parse_timestamps` 123). That check is *relative* - every candidate comes out of the same ffmpeg pass - so a systematic extraction defect is invisible to it, and on D17's mixed-index shape (843 cues for a track whose real count is 803) it would silently prefer the corrupted candidate whenever the reference's own cue count sits nearer to it. It has no equivalent of this project's validation layer (`MkvExtractionStats.ClustersVisited`/`PlanBound`, cue parity against the container index and ffmpeg, read-plan-vs-actual accounting). **Verdict: simpler, not more correct - nothing to adopt**; D17/E2 could not occur in their design, and the design's price (a demux per file) is exactly what the index route exists to avoid. |
| decision (examined, not applicable) | pair-autosubsync (this session) | low | **AutoSubSync matches files on disk because it has no library to ask.** `pairing.py:pair_paths()` (54) does a two-pass match: an exact effective-basename match first (`effective_basename`, 22, which also strips a trailing 2-4 character language tag), then the best **prefix-length similarity** score (`calculate_file_similarity`, 31 = common leading characters x 10, minus 2 per character of length difference capped at half the score, +50 for an exact base) with a threshold of 30 (91); `pair_folder`/`pair_folders` (111/124) scan one or two directories. The GUI's own auto-pair is not similarity-based at all: `gui_auto_pairing.py:AutoPairingDialog.pair_files()` (748) keys both lists by `(season, episode)` parsed from the filename (`extract_season_episode`, 40), pairs within equal keys, and `update_pairing_display()` (776) only recolours and orders the lists - no confidence is shown to the user, and there is no fuzzy library in the tree (the one `levenshtein_distance`, `utils.py:214`, suggests *encoding* names). This plugin resolves from Jellyfin's metadata instead: the subtitle ordinal at the enqueue boundary (`EmbeddedSubtitleOrdinal`, `SubSyncService.cs:2513` and `:1081`), the real container index re-derived by probing and matching by position among embedded subtitle streams (`ResolveContainerSubtitleIndexAsync`, `:4006/:4022`), and a sibling reference read as text (`TryReadReferenceTextAsync`, `:4957`). **S14 is track identity across time, and no name similarity can fix it** - the file is the same file; S14's own recorded fix is folding the track's language, codec and position into the job context, i.e. exactly the metadata Jellyfin already hands us, which is *stronger* than a filename prefix score. **Verdict: not applicable; S14's existing row covers the real gap.** |
| decision (examined, not applicable) | progress-autosubsync (this session) | low | **`gui_batch_mode.py` (3178 lines) is the batch builder, not a progress surface.** Its only progress signals are the pre-flight processed-item scan and the database workers (`ProcessedItemsScanner.scan_progress`/`DatabaseOperationWorker.operation_progress`, `int,int` current/total, `gui_batch_mode.py:129-132`/`:180-184`), and `BatchTreeView._update_header_pair_counts` (1172) counts valid/invalid/skipped pairs; there is **no ETA, elapsed, throughput or percentage anywhere** in the file (zero matches for eta/elapsed/remaining/percent). The sync's own progress is `gui_log_window.update_progress(value, current, total)` (345), which sets a `QProgressBar` labelled `%p% (current/total)` from `sync_auto.update_progress` (78) - and that percentage is **scraped from the sync tool's stdout** (`re.search(r"(\d{1,3}(?:\.\d+)?)\s*%", line)`, `sync_auto.py:487-497`), i.e. their intra-file granularity is the external tool's, which our engine does not print; our own state-derived surface is `queue: N queued, M running, F file(s) left, lane currently on: <file>, T min elapsed on it` (`SubSyncService.cs:1320`, S24) plus the 5-minute `EngineHeartbeat` (S27). **`processed_items_manager.py` is a dedup ledger, not history**: singleton over SQLite `processed_items.db` with one row per processed *video* keyed by a **partial content hash** (SHA-256 of size + first 64 KB + last 64 KB, `_calculate_partial_hash` 95-133) so a moved or renamed file still counts as processed (`is_processed` 142, `mark_as_processed` 170, `remove_from_processed` 209, `clear_all` 243, `import_from_database` 286). The GUI persists nothing about runs (no `json.dump` in any gui module; `cli.py:312-325` writes a per-item JSON *report* to stdout only). S25's gap is closed on our side already (`Services/BatchHistory.cs`: 20 batches, atomic temp+move, restored at start-up with interrupted jobs coming back cancelled), and a video-level fingerprint would be *wrong* for this plugin - one file has many subtitle tracks, so "already processed" would skip unsynced tracks - while our sweep keys per **subtitle** with a content hash and a fail streak (`SweepState`). **Verdict: not applicable; S24 and S25 stand as shipped.** |
| decision (not competing for a scheduling win) | group-2 (this session) | low | bulk batch grouping: `BatchVolumeGroup.cs` (isolated, build verified 0/0) is structural only — pure grouping of existing `BatchHistory` by `MediaVolume`, no scheduling wired, no measured throughput change. Verdict per user's choice (b): stays unwired; marked explicitly. **What (a) would cost, for the record**: the invariant is not advisory — `tests/run_checks.py` asserts "heavy work on one disk is not throttled" (`heavyWave.Count == 3`), "four heavy tasks on one volume fill the wave" (`full.Count == 4`), "worker count is the only bound" (`twoWorkers.Count == 2`) and replaces the budget with a reflection check (`!typeof(WavePolicy).GetProperties().Any(p => p.Name.Contains("PerVolume") \|\| p.Name.Contains("Budget"))`), so wiring a per-volume cap means rewriting 3 behavioural checks and replacing that reflection check with one asserting the new rule (renaming the property would game it). Measurement side is ready and needs no new scaffolding: the rig already has two filesystems — `shm` (`/dev/shm/s39-fast`) and the shimmed volume (`/opt/data/jf12test/media-slow` on `/dev/nvme0n1p2`), which the README states must differ for two verdicts — and `tests/backend/s28-concurrency.sh` is the throughput/per-file measurement already used for the concurrency numbers. Evidence: `docs/CANDIDATE_batch_volume_group.md` (the class itself was **deleted before shipping 2.0.43** - nothing called it, and a dead type in the shipped DLL is worse than a documented idea; the measurement side above is unaffected). |

| done (decision closed: the gate is rejected, the score ships as passive logging) | s31-quality (this session) | low | **Built, measured, rejected as a gate.** `QualityOfFit.cs` (autosubsync's MIT `quality_of_fit.py`: monotonicity x prominence x non-edgeness, 0.75 threshold) + `SubtitleRulerShape.cs` (curve for a ruler against the subtitle being synced: 0.1 s steps, 0.5 s kernel, window = `MaxOffsetSeconds`). Rig, this tree's build, real 2.4 GB fixture, no shim: with the skip wired, the correct ruler (`quality=0.833 peak=803 at -20000 ms covering 100%`) skipped the audio pass and the job fell from **18.05 s to 1.53 s (16.52 s saved, 91.5 %, 11.8x)**, the other-cut ruler (`quality=0.025 peak=231 covering 28.8%`) was cross-checked and discarded as before - but `--s31-ruler offset` (the ruler is the same cut, +25 s along) scored **0.833, identical to a correct ruler**, so the gate skipped and wrote `change=+25000 ms` where 2.0.42 refuses and writes nothing (3 rig assertions failed). **Q1, field frequency** (`tests/backend/ruler_field_frequency.py`, the server's own log 2026-09-12..15, builds 2.0.7-2.0.34): 710 runs used a subtitle ruler, **8 (1.1 %) demanded a shift over 10 s, all in the 10-30 s band, none below**, and zero cross-check outcomes are recorded because that server predates the check - so the gate's 16.52 s/run is worth ~0.17 s per subtitle-ruler run, and the field cannot yet say how many of those 8 are same-cut (skippable) against different-cut (not). **Q2, the narrow band** (`tests/backend/gate_narrow_band_probe.py`): a 0.95 bar is unreachable for any ruler that asks for a real shift - non-edgeness caps a perfectly correlated ruler at 0.833 at a 20 s offset - and a small-offset condition sits below the cross-check's own trigger (> ceiling/3 = 10 s), so it skips nothing; the blind spot is also offset-independent (0.833 at 2/5/10/20/25 s alike). **Shipped**: the score is a diagnostic line before the cross-check (`ruler shape: ... - context only; the audio cross-check runs either way`), no shape condition wraps the cross-check, and the rejected `SkipsAudioCrossCheck` helper is deleted. Suite: C# ALL PASS with 7 checks (4 scoring incl. the blind spot pinned as a property, 3 source); the 2 pre-existing page failures are unchanged. Evidence `docs/EVIDENCE_s31_shape_gate.md`; cue-level head-to-head `docs/EVIDENCE_s31_quality_of_fit.md`. |

