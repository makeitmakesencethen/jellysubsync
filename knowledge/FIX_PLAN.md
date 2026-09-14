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

### S31 - a reference subtitle is trusted on plausibility, never on correctness (high, correctness)

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

### S32 - the ceiling's own log line cannot say which of two cases it is in (low, small) - see `WALK_CEILING_GOAL_PROMPT.md`

`walk ceiling: holding <volume> at 2 concurrent media read(s)` picks its wording from the *cap value*, and 2
is both the unmeasured fallback and what a volume measured between 5 and 100 ms per read gets. On 2026-09-14
it printed "this volume has not been read yet" for a volume whose extraction pass had measured 7,7-39 ms per
read minutes earlier, so the line could not distinguish "nothing measured yet" from "measured slow". Fix: pass
the measured value into the message. This is the one output S33 is diagnosed from, so it is worth having.

### S33 - a fast volume's ceiling may never lift, because nothing feeds the profile (medium, correctness) - see `WALK_CEILING_GOAL_PROMPT.md`

`VolumeProfiles` is fed only by reads made through `ReadPolicy` (`ReadPolicy.cs:466 _volume?.Observe`), and
those come from the extraction path. Two consequences:

- when a run's extraction is served from the **subtitle cache** (`extract: method=cache ... no read: this file's
  subtitle was extracted on an earlier run`), no reads happen, so the volume keeps no samples and the
  conservative cap of 2 sticks - on a *fast* volume too, which is a throughput regression the ceiling was never
  meant to introduce;
- the **walk's own reads are invisible**: ffsubsync runs as a child process, so the tens of thousands of reads
  that actually cost the time are never measured by the plugin's read path.

The 2.0.31 run's hold line said "has not been read yet" once a minute for the whole run, which is consistent
with either case (see S32). Needs the measurement surfaced first (S32), then a decision: feed the profile from
the walk (even approximately, e.g. duration and bytes read) or treat "no samples after N walks" as fast rather
than as unmeasured.

### S34 - the only way to force re-analysis is an API call (low)

`POST /SubSync/SpeechCache/Clear` exists, is documented safe, and is what a re-test needs after a run has
cached its audio analyses - but it is not wired to anything in the page (no UI hits for it). Anyone wanting to
re-walk a file has to call the API with an API key. A button in the plugin's settings would make it reachable.

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
