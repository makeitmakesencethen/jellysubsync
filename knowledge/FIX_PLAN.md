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
| done | **B7** | high | `Kill` cannot interrupt a Matroska extraction. The lane handed the reader `CancellationToken.None`; measured on the slow profile, a Kill was followed by 805.6 MB of reads in 40 s while `/SubSync/Active` said nothing was running. `Kill` now cancels the pass and the source is re-armed for the next one; the same measurement after the fix: reads stop at +21.7 s (the current read batch finishes first), and the pass logs `stopped (killed by the user)`. A cancelled pass no longer marks tracks as unextractable, and the reason is printed (it used to be relabelled as an ffmpeg whole-file read) | `lane-kill-before.json` / `lane-kill-after.json`, harness `tests/backend/lane_kill.py` |
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

## Tier 3b — 2.0.24 read-policy follow-ups (4 items)

`R…` items come from the verification work of 2.0.24 (`ReadPolicy`), not from the 2026-09-11 audit.

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| held | **R1** | medium | the cue-indexed route plans one read per located block plus the cluster head, so its read *calls* follow the cue count (2 per cue) at every storage profile. The goal for the read policy stated the opposite: "on per-read-latency storage, reads per pass are bounded by bytes/window + prefetched ranges, never by cue count". Confirmed by measurement, not inference: the plan is identical at every profile (matrix `blocks early/late`, 800 cues, 1 track: 1600 calls, 1,9 MB) and the wall clock follows `cues x latency` - the same two rig passes take 1,54 s and 2,92 s on a 10 ms/read share and 5,53 s and 13,62 s on a 50 ms/read one, reading the same 0,65 MB and 1,08 MB. The walk's merge threshold never fires for a 20-900 KB cluster gap either, because `MergeGapBytes` is derived from the pass's own small-read samples (~1,2 KB at any latency). The prefetch width hid it by ~5x, not 16x, and a share that queues reads would expose the serial 527 x 50 ms = 26 s. The shared 32-track route *is* bounded (451 calls for 800 cues x 32 tracks). | rig `--call-ms 50 --mb-per-second 11`: 2.0.23 325,21 MB/338 reads/24,27 s and 1086,48 MB/275 reads/113,50 s; read policy 0,65 MB/527 reads/5,53 s and 1,08 MB/267 reads/13,62 s. Matrix prints the waiting per cell (80,00 s serial / 5,00 s at 16 wide for 800 cues at 50 ms). **Held on branch `hold/r1-throughput-fit` (tip `3ef1b2a`)**: the fit logic and its unit checks are good, but the change is not earning its risk - no measured improvement on the episode shapes that matter, and a regression risk on the fast-reads/slow-bytes profile that rests on the measured latency being right. **Path forward, with no probe**: the fit needs two read sizes in one pass, and a real batch has them on the same storage - a multi-track file's cue index is several times the single-track one (measured: 4 tracks fits at 50 ms/read, merge gap 256 KB at plan time, where one track at the same latency fell back to 0,1 MB/s), and the walk route reads 16 MB chunks against 2 KB blocks - fitted at 16,6 MB/s on the walk fixture at a modelled 11 MB/s, so that pass does have both sizes to learn from. The blocker is that the profile is per pass, so a cue-indexed pass cannot learn from the walk or the multi-track pass running beside it on the same share. The ffmpeg fallback **cannot** feed it: it demuxes in its own process, invisible to the policy. So the fix is a per-volume profile shared between passes (fed only by reads the plugin already makes, decaying so a busy share is not believed for ever) or a re-plan after the first fetch - not a deliberate larger read, which is the pre-work probe the goal forbids. Noise awareness comes first either way: the same fixture reported 0,1 MB/s in one run and 11,1 MB/s in the next, because a ~0,7 ms difference was being read against 50 ms of latency. |
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
| held (premise refuted by measurement) | **E5** | medium | **Per-volume storage profile (Phase 3, R1's original fix): no measurable gain, so not shipped.** Built to spec - one profile per volume, fed only by reads the passes were already making, latency as an age-weighted median and throughput as a byte-weighted mean trimmed of the slowest fifth, half-life 10 minutes, seeded into every read policy at construction - and covered by ten new checks (504 green). It changes nothing: with the rig running several passes in one process on one volume, `arcane` x3 at 46 ms/read is 527/527/527 reads and 5,12/4,94/4,88 s before against 5,09/4,96/4,83 s after with a byte-identical plan, the walk shape 267/267 both sides, a small-index file 47/47/47 both sides, and at 100 ms/read 527/527 both sides. Structurally: a pass reads the SeekHead and Cue index *before* it plans, so its own profile already holds the measured storage, and the one lever a profile feeds (`MergeGapBytes`) is the byte-equivalent of one read call, so acting on it is break-even by construction. Held on branch `eval/per-volume-profile` (`9e53170`), which also carries the rig's ability to run a sequence of passes in one process. | `9e53170`; before side `3f6db92`, rig rows in `.tests-work/p3-*.json`. |
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
| **B13** | low | **reproduced, and smaller than the row claims** | retained memory after extraction measured 0,04-0,08 MB, with 0,35 MB in 299 prefetched ranges on a 300-cluster 6-track pass. The row's "4 MB window + up to 384 MB" is a code bound, not an observed figure; unverified at the 60 GB remux scale where it would matter. |
| **B2 / B4** | high on paper | **not reproduced - likely already fixed** | the shared pass produced exactly the same cue count as per-track extraction (10/10/10) on the grouped, half-patched and 3-track fixtures. B4 is E2's root cause (`ParseCueRefs` not resetting per `CueTrackPositions`), fixed in 2.0.26; B2 looks like the same defect seen from the shared pass. Recommend closing both after one run on the D17 mixed-index shape. |
| **S7** | medium | **stale for extraction** | `MkvExtractionStats.StorageProbeMs` is assigned from `policy.MsPerCall` - the pass's own reads - so the extraction path no longer probes the storage; only the field name survives. Whether the *queueing* layer still probes needs a check in `SubSyncService`/`WavePolicy`. |
| **B10** | low | **verified, fixed** | `FinaliseStats(stats)` was called before `stats.ReadCalls` and `stats.TotalMs` were assigned (`MkvSubtitleExtractor` 345-347 and 359-361), so the summary it builds cannot carry them. **Half of it bit**: the block rate was 0 on every extraction that read a file (`blocks=10 totalMs=0.8 perSecond=0.00` on each of the four fixtures) because `TotalMs` is assigned nowhere else, while `ReadLatencyMs` survived by accident - the cue-indexed loop's live-counter line fills `ReadCalls` as it goes, so ms/read was right while blocks/s was not. Fixed by assigning both counters before `FinaliseStats` at both call sites; the check asserts the field equals `SubtitleBlocks / (TotalMs / 1000)` exactly (15908.37 / 8221.66 / 14120.30 / 10137.88 blocks/s after, 0.00 before) and the redundant fallback in `ToString` is removed with it (same number, no log text moves). **Correction to the read-out that proposed fixing this row**: the field evidence offered there - `actual 0.81 MB/197 read(s) (1888.7 ms)` beside the lane's `209 reads, 4193 ms` on one Thunder S01E03 pass - is **not** this defect. The plan line's ms is `Price() = calls x msPerRead + bytes / bytesPerMs`, a priced estimate rather than a measured time (197 x 9,588 = 1888,8 ms, the profile's own figure), and its read count is the planned phase's delta, not the pass total: two scopes and one estimate, both correct. | `f38ce6e`; before 4 failures, after 513 checks green. |
| **B25** | medium | static | `if (payloadRead < payloadLength) payload = payload.AsSpan(0, payloadRead).ToArray();` - a short read becomes the cue text instead of a refusal. A healthy local file cannot produce one, so this needs either a fault-injecting shim or the user's share to hit it. |
| **B24** | low | static | the per-pass dedup key is `position * 31 + cueRef.RelativePosition` - a hash rather than a pair. A collision needs Δposition = k and Δrelative = -31k, i.e. cluster positions within ~1 KB with relative offsets ~31 kB apart; possible in principle, and it drops a cue silently when it happens. |
| **B12** | medium | static | the unbounded stores are `ReferenceStore.Entries`, `SubtitleCache.Memory`, `SharedExtractionStore.Consumers` and `SweepState` (the last two have their own rows). Extraction-adjacent: `SharedExtractionStore` is what the shared pass publishes into. |

Everything else open in Tier 4 is GUI, settings, API surface, job/queue/process handling or file writing
(the D and F series, B1/B3/B5/B6/B14-B19/B22/B23/B26-B30, S5/S12-S14) and does not touch the extraction or
read-policy path.


## Tier 4 — the rest of both matrices, layout, hygiene, and the audit's unproven static leads (verify first: refuting one is a real result) (68 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| open | **B1** | ? | Replace mode can destroy the original subtitle with no rollback |  |
| open | **B31** | low | An unguarded scheduler loop: one exception inside `PumpAsync` ends it silently, and the lane's unexpected failures go to Jellyfin's log, so a stalled batch would have no line in the plugin log. **Latent** - the 2.0.28 batch finished cleanly on the server (no stuck or failed jobs, files have their subtitles) and the frozen slice read here is a snapshot that ends mid-run, so this gap is not what was seen. Fix sketched in the Matrix B row | `/subsync-logs/subsync.log` lines 4236-4688; `SubSyncService` 2398-2595, 2370-2383, 1746 |
| done | **B10** | low | `FinaliseStats` runs before `ReadCalls`/`TotalMs` are assigned, so the summary's derived ms/read and blocks/s cannot see them | `f38ce6e`: reproduced on the four fixtures (block rate 0 on all, `perSecond=0.00`), fixed by assigning the counters first at both call sites, and the redundant blocks/s fallback in `ToString` removed. The ms/read figure was already right - the cue-indexed loop's live-counter line fills `ReadCalls`, so only the total-time half bit. 513 checks green. |
| done | **B11** | low | The metadata-scan route's progress line never left 0,0 MB: it printed the stats field that only the cue-indexed loop fills, while this is the route that reads most of the file and the one a user watches longest. Fixed: the line takes its numbers from the reader the pass is reading through (kernel counters where the platform provides them, the reader's own count otherwise) and brings the pass's own counters up to date with it. | `ba795d0`. Reproduction and verification on the same fixture (`tests/backend/p4_probe.py b11-progress`, 520 clusters): before `scanning clusters (400 read, 0,0 MB, 200 subtitles found)`, after `scanning clusters (400 read, 1,6 MB, 200 subtitles found)` with the pass reporting 2 155 817 bytes over 528 reads; the cue-indexed route's line is unchanged (`reading subtitle 260/260 - 0,6 MB, 588 reads`). 494 checks green. |
| open | **B12** | ? | Four planner/cache dictionaries grow without bound |  |
| open | **B13** | ? | Per-reader memory: 4 MB window + up to 384 MB of prefetched ranges |  |
| open | **B14** | ? | `Dispose` leaves child processes and lanes running |  |
| open | **B15** | ? | Logging while holding the queue lock |  |
| open | **B16** | ? | `KillAll` busy-waits with `Thread.Sleep` and fabricates its return count |  |
| open | **B17** | ? | Completed jobs are evicted whenever the store exceeds 50 entries |  |
| open | **B18** | ? | Five near-identical process runners, two argument styles |  |
| open | **B19** | ? | `out_time_ms` is converted as milliseconds |  |
| open | **B2** | low | The shared multi-track pass returns success while a requested track is missing from its results, and nothing in the return says which. **Reproduced** on the D17 mixed-index shape (`tests/backend/p4_probe.py b2-mixed-shared /tmp/d17fx/d17-mixed.mkv 0 1 2`): the half-located track 5 is absent while `TryExtractMany` returns true with an empty reason, and 803 cues when extracted alone - tracks 4 and 6 match the shared pass exactly (8 and 726). Not cue loss: `SubSyncService` extracts a track alone when its subtitle never reaches the cache, which is why this is low and not high. What is missing is visibility, and it is thinner than it looks: the pass's log line already reports `tracks=N/M` (how many of the requested tracks it served, out of how many), so the *count* of misses is visible - but not **which** track was missed, so reading the log cannot tell you whether the missing one was the language someone is waiting for. A fix would name them (a missed-tracks field on the stats, or the reason naming the ordinals); no behaviour change to the pass. | `tests/backend/p4_probe.py` mode `b2-mixed-shared`; counts above. |
| open | **B20** | ? | A cancelled extraction is reported as a failure and takes the fallback path |  |
| open | **B21** | ? | Prefetch faults lose the real error inside an `AggregateException` |  |
| open | **B22** | ? | `MeasureSyncChange` is computed twice per job |  |
| open | **B23** | ? | `ClearStaleJobDirectories` recursively deletes anything under the scratch root |  |
| done | **B24** | low | The cue-indexed loop's dedup key was `clusterPosition * 31 + relativePosition` - a hash of two numbers rather than the pair - so two cue points whose cluster positions are d bytes apart with relative offsets differing by exactly -31d shared a key, and the second was skipped without its cluster ever being read: a subtitle lost silently, with the pass reporting success. | **Reproduced and fixed**: `tests/fixtures/make_collision.py` builds the collision (two clusters 67 bytes apart, the first cue point carrying a relative offset of 31 * 67 = 2077 that points outside its own cluster - the kind of value a foreign index carries - the second sitting at relative 0 where its block is, both clusters holding a block so neither cue point is a "missing block" case). Before: 1 cue, the missing text being exactly the second cue point's; the same file with the first offset nudged one byte (keys differ) gives 2. After (`6903f94`): both give 2 cues. Unchanged sentinels: kopps 829, Sune i Grekland 1019, D17 mixed-index track 803; 494 checks green. |
| open (not reproducible by its stated trigger) | **B25** | medium | A short read does not reach the cue text, because the reader retries it. The truncation path exists (`if (payloadRead < payloadLength) payload = payload.AsSpan(0, payloadRead).ToArray()` - truncated text emitted with no refusal and no marker), but the storage cannot trigger it: `BlobReader.ReadAt` loops until the destination is full or EOF and `ReadNear` falls back to it whenever the request does not fit the window, so a share that answers with a short read is retried rather than propagated. **Verified two ways**: `tests/backend/shortread.c` (libc interposition on pread/pread64, proven to shorten a python `os.pread` by half on every second call) left the plugin's output byte-identical while shortening every second read of the fixture, and the read path itself says so. The one trigger left is EOF inside a payload - a file being copied - and on the fixtures built here the index guard refuses first (`InvalidDataException: index element truncated: wanted 530 bytes at 20973448, got 490`), while cutting a subtitle block in a no-index fixture landed in the sparse video hole. **Kept**: the shim stays in the tree for future use (the fused shim is the only way to model a storage that answers short). If the user wants the residue hardened anyway - refuse instead of truncate - it is a two-line change, not a reproduction. | Shim `tests/backend/shortread.c`/`.so`; probe mode `b25-short-read`; both runs recorded in the Phase 4 report. |
| open | **B26** | ? | Culture-sensitive parsing/formatting in two places |  |
| open | **B27** | ? | `Diag` builds its message even when diagnostics are off, and writes to stderr |  |
| open | **B28** | ? | Magic numbers that should be settings, and one that contradicts the documented policy |  |
| open | **B29** | ? | Non-volatile `_disposing`, unsynchronised `_lastPassFinishedUtc` |  |
| open | **B3** | ? | Scheduler touches the media share while holding the queue lock |  |
| partial | **B31** | low | **latent - code verified, no evidence in this batch** | The scheduler's loop is unguarded and a batch that stalls says nothing in the plugin log. `PumpAsync` (`SubSyncService` 2398-2595) has **no try/catch around its loop**: one unexpected exception ends the scheduler, and `WakePump()` (2370-2383) only restarts it when something else wakes it, so if nothing can finish - jobs waiting on extraction while the lane is gone or blocked - the batch freezes with no line in the plugin log at all. The lane has the mirror-image gap: its unexpected-exception handler (1746) writes to Jellyfin's server log via `_logger.LogWarning`, not to `PluginLog`, so a lane failure is invisible in the file this project reads runs out of. **The 2.0.28 batch itself is clean and this gap did not fire**: the server shows no stuck or failed jobs and the files have their subtitles, while the slice read here (52 jobs queued, 38 dispatch lines ending `dispatch: starting 1, running 7, limit 8, queued 1` at 15:09:50Z, 23 completed / 0 failed / 0 cancelled, 29 jobs with no terminal line, last line 15:11:29Z) ends mid-run. The likely reading is that the log read here is a snapshot of the server's log that simply stops while the run continued - the alternative, the plugin's writer stopping, is hard to square with a batch that finished: `PluginLog.Append` re-opens the path per write, has no disable-on-error flag and never throws, the volume has 79 GB free and no rotation happened (1.29 MB against a 4 MB limit). No defect, and no evidence of this gap. **What would still prove a real stall** (nobody has looked): Jellyfin's own log around that moment - a plugin `Extraction lane failed for <file>` warning, or an unobserved-task trace - and the batch's state on the plugin page. **Fix (not written yet, low priority)**: guard the pump loop with a catch-all that logs `PluginLog.Error` and continues, mirror the lane's exception warning into the plugin log, and emit a heartbeat line every few minutes while jobs are outstanding, so a stall reads as a stall instead of as silence. | Log `/subsync-logs/subsync.log` lines 4236-4688 (2.0.28 slice), 0 ERROR lines, no `startup:` line after 15:07:56Z; code at `SubSyncService` 2398-2595, 2370-2383, 1746. |
| **B30** | ? | `DescribeExtraction`'s catch-all relabels anything unknown as ffmpeg | `DescribeExtraction`'s catch-all no longer calls every unknown reader "ffmpeg (whole-file read)" and names cancellation: a cancelled pass used to be logged as an ffmpeg demux (see B7) |
| done | **B4** | high | Cue-point parsing did not reset per `CueTrackPositions`: the cluster and block offsets lived in variables that outlived each of them, so the last track's position in a cue point was adopted for whichever track matched - every cue point then named another track's block. Fixed in 2.0.26 (`5a0ad05`, "A cue index's cluster and block offsets belong to the track they name"): kopps 104 of 829 cue points named another track's block and now none do, with the cue count matching the file's own index and ffmpeg (829, from 832; Sune i Grekland 1019 from 1251). | Closed 2026-09-13 on the reproduction that first exposed it: the D17 mixed-index shape (`patch_cues.py patch ... 5 2`) extracts 803 cues for track 5 alone, identical to ffmpeg, and the 40 duplicate cues the mixed index used to produce are gone (`9bbec18`). |
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
| open | **D23** | medium | (now blocked by S19, not by the page) | F29's two-press kill and the mirrored-run-box fix are still **not verified in a browser**: both need a run the page can see, and in the harness the page's own session got a 403 on one call and the batch queued from it never appeared to the page (button stayed "Cancel", line "Nothing queued."). The probe drives both and is ready to re-run | `tests/gui/page-interactive{1,2,3}.json`, `tests/gui/page-interactive-probe.js` |
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
| done | **S21** | low | a completed job that writes *nothing* is invisible in the plugin log. Both no-change paths (`SubSyncService` 3988 and 4298–4312) log through Jellyfin's logger only and `return` before the plugin-log line at 4540, so no `job … completed:` line is ever emitted for them — while the sibling *unverified* outcome (`UNVERIFIED:`) logs to *both* logs and appears 151 times. The UI is right (`Status=Completed`, `OutputPath=null`); only the plugin log is short. **Sharpens B31**, which scoped the blind spot to the lane's unexpected failures — routine completions are just as invisible | `9374ce59` (36 tasks) closes only as 16 Completed + **20 silent** = 36, and its last dispatch `22:48:42.998 dispatch: starting 1, running 7, limit 8, queued 1, batch 9374ce59…` shows the queue drained; `a068567a` closes only as 25 + 2 Refused + 865 cancelled + 8 stopped + **7 silent** = 907. In the live batch at 01:35: 238 Completed + 151 UNVERIFIED + 6 Refused + 14 failed = 409 terminal lines against 563 jobs that had left the queue. Closing the count needs Jellyfin's own log: `grep -E 'sync changed nothing|subtitle already in sync' /config/log/jellyfin*.log`. **Fixed in `0a34ca9`**: one definition builds the completion line and all three completed paths call it (the success path and both nothing-written paths); the four refusal paths keep their own REFUSED/UNVERIFIED lines |
| open | **S22** | medium | a refusal blames the search window when the engine could not read the reference it was handed, and sends the user to a setting that cannot help it. 7 of the 8 refusals in this run are this shape | `job 205fbc99… REFUSED: this subtitle is further out than the plugin is searching (the 300 s window produced nothing (exit 1) · engine said: [00:59:01] ERROR unable to read reference /config/data/data/subsync/state/speech-cache/5e16e1f2a0c1c78f085afdfdd3275934.mkv; try ensuring file exists and has correct permissions); raise "Maximum offset" and run it again, or sync it by hand. Nothing written, source untouched` — the other six: 100 årstider (2023), 12 Years a Slave, Bröderna Lejonhjärta, Pulp Fiction, The Emigrants, Tusen bröder S02E02/S02E03. The eighth is a genuine window case (`the 300 s window also reached its limit (416183 ms)`). Lead worth checking first: the unreadable path is the audio analysis under `state/speech-cache/`, where `AGENTS.md` says the engine's reference must *not* live |
| done | **S23** | low | a cancelled job gets no line of its own — cancelling records batch-level counts and the phase ids of the running jobs, nothing per job | `23:06:32 cancel batch a068567a…: 865 queued cancelled, 8 running stopped (phases: 091663b3=Analyzing speech, a790cdd3=Analyzing speech, …)`; `23:33:24 cancel batch 5396a1a3…: 636 queued cancelled, 11 running stopped (phases: … Syncing (from cache) ×8, Extracting subtitle …)`; `02:17:24 cancel batch ce6a7a65…: 290 queued cancelled, 15 running stopped`. 1 501 jobs left the log with no terminal record. **Fixed in the commit logged with this row**: `job <id> cancelled: mode=… item=… stream=…` is written per job by both `CancelBatch` loops and both `KillAll` paths. Also worth noting: in-flight results still land after a cancel — lane lines appear at 02:17:47–48, after the 02:17:24 cancel |
| done | **S24** | low | a run has no progress or ETA surface: the only queue depth anywhere is the dispatch line, emitted only when a job *starts* and naming only the batch that job belongs to | `02:13:00 dispatch: starting 1, running 7, limit 8, queued 304, batch ce6a7a65…` — nothing reports jobs left, files left, the lane's current file, or that a run has ended. On this run the backlog (338 jobs over 338 files, one job per file) and the ETA had to be derived by hand from lane lines and dispatch timestamps: 11 jobs in 918 s = 0,7/min ⇒ 4–8 h of uncertainty. The lane also spends long stretches inside a single file with no line in between. **Fixed** (commit logged with this row): one line a minute while work exists - `queue: N queued, M running, F file(s) left, lane currently on: <file>, T min elapsed on it` - emitted from the pump tick, with the lane's start time now stored per pass |
| done | **S25** | low | batch history is in-memory and unpersisted, and progress follows a single batch, so the two read as one complaint ("there is no way to tell how many jobs are left" + "the history resets") | batches live in `SubSyncService` and are read back through `GetBatch` / `GET /SubSync/Batches` (server-side, shared by all viewers per `AGENTS.md`); no retention, eviction or persistence code was found for them, so a restart clears the list. The page watches one batch at a time (`watchOrQueueBatch(batchId, label, count)`), so creating a new batch replaces the progress the user was looking at. Reported by fabji after queueing four batches in one session. **Fixed** (commit logged with this row): `{StatePath}/batch-history.json` via `BatchHistory`, restored at start-up, written from the pump tick (signature + 60 s) and once on shutdown; restored jobs are display-only, exempt from the cleanup timer, and anything interrupted comes back as cancelled |
| open | **S26** | medium | on one release the cue-indexed plan and its prefetch disagree with reality by ~20×, and the reads that follow are the single largest cost in the batch. The shared-pass route on the same file predicts correctly, so this is specific to the cue-indexed plan over that release | `WARN extract plan: solsidan.s03e02.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/528 read(s) (118777.6 ms), actual 42.03 MB/10635 read(s) (3641220.3 ms) - bytes 66.…` then `WARN extract: solsidan.s03e02… read 0,09 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 789 range(s)…`; same shape on s03e03, s03e04, s03e05, s03e06 — lane passes of **17,6, 19,3, 23,5, 56,6, 78,5 and 95 min** for ~11 000 reads each, ~20 reads/cue against the family's ~2. Per-read cost on the same release: `extract profile: solsidan.s03e09…: storage 2,14 ms per read and 1,8 MB/s` versus `1613,40 ms/read, 0,0 MB/s` on s03e04. `past the fetch` has no row anywhere in this plan although it is in the tail watcher's filter list |
| explained — no code change | **S28** | medium | the audio-ruler analysis costs **one full walk of the media file**, and that walk *is* the cost; the observed ~6x penalty over the share's own measured speed is concurrency (up to 8 walks at once on one volume). A fix that extracts a compact audio reference first was tested and **refuted**: through the share's profile the rip costs 901 s and the engine's own job costs 903 s, so the copy moves the walk rather than removing it (it would only help re-analysis, parked below). The real levers are (1) how many full-file walks run at once, (2) the storage path itself, (3) jobs that align against a subtitle reference and never walk the media (median 2,9 s) | Measured 2026-09-14 with `tests/backend/slowread.so` at fabji's share profile (10 ms/round trip + 11 MB/s), one 2,4 GB episode: audio rip **901 s** (capped, rip complete), engine with the media as reference **903 s exit 0**, engine against a **local** audio-only reference **9 s**, decode-only 160 s (killed by a cleanup, unusable), engine with the media reference on **local disk 17 s**, engine against the local audio-only file on local disk **8 s**, and both produced the same alignment within 70 ms (`offset seconds: 4.690` vs `4.620`). Field: 232 audio-ruler engine runs median **35,3 s**, films 57-111 min, per-read latency on the share 2,14-1613 ms within minutes, 8 concurrent walks + the extraction lane reading ~10 000 ranges. Concurrency experiment: `tests/backend/s28-concurrency.sh` (per-file wall clock at 1/2/4/8 concurrent walks, bandwidth-shared model) |
| done | **S27** | medium | no deadline and no heartbeat around the engine run: the plugin logs a start and an exit and nothing between, so a wedged engine pins a worker indefinitely and a correct 91-minute run is indistinguishable from a hang on every surface the user has. **Fixed in `8d2b548`**: `EngineHeartbeat` writes `[<job>] <file>: engine running, N min elapsed, reference=<ruler>` to the plugin log every 5 minutes for exactly as long as the process runs (started after the process is live, disposed when it is gone, best effort). No deadline and no kill were added - visibility only, as decided. The wording, the interval and the absence of a timeout are pinned by the suite | Hereditary `00:22:09 [d805de52…] ffsubsync start: … reference=a:0 …` with no exit line **111 minutes** later; Midsommar completed at `02:05:45 job c4d7d3ae… completed: … bytes=69518 change=+112836 ms` after **91,3 min**; Oppenheimer 65,5 min; The Lighthouse 57,2 min. During all of it the plugin log carries nothing between start and exit. `ExtractionTimeoutMinutes` (20, `timeoutCts.CancelAfter`) governs the ffmpeg extraction fallback only — the engine is killed solely through the caller's cancellation token. `ps` during the run: parent `ffsubsync` at **0,2–2,9 % CPU (18 s CPU in 106 min)** ⇒ waiting, not computing; the work sits in ffmpeg children, which `ps -C ffsubsync` does not show. Cost by ruler, whole run: `reference=a:0` n=232 median **35,3 s** max 5 479,8 s; subtitle ruler n=429 median **2,9 s** — a feature film with no usable embedded subtitle costs ~1 h |

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
