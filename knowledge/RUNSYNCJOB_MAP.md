# RunSyncJob map — investigation only, no code changes

Read-only static map of `SubSyncService.RunSyncJob` (Jellyfin.Plugin.SubSync), produced 2026-09-15 against the
working tree at `a9b907a` (2.0.54). Line numbers are from that revision.

## 0. Shape facts

- Method: `SubSyncService.cs:5400–6930` — **1562 lines** including signature and closing brace; body **5408–6929**.
- One `try` (5444–6858), two `catch` (6859 `OperationCanceledException` with a token filter, 6867 generic),
  one `finally` (6895–6929). Exactly one loop in the whole method — a 7-line `foreach` over `FramerateArgs` in P9
  (6274) — and no `while`/`switch`/`break`/`continue`/`goto`.
- **17 exit points**: 10 `return`s (5666, 5689, 5695 inside the local function; 6080, 6207, 6330, 6362, 6417, 6579,
  6609) and 7 `throw`s (5472, 5479, 5491, 5530, 6011, 6620, 6721).
- 3 `lock` sites, all on a local `List<string>` (`engineErrors` 5894/5976/5986/6006, `wideErrors` 6247/6336) — these
  are **closure-shared buffers written from the process's stderr pump thread**, not service state.
- 1 local function (`PrepareAudioReferenceAsync`, 5643–5696) and 5 stderr callback lambdas (5881, 5983, 6152, 6245,
  6476), every one of them mutating captured locals.
- The method is a linear pipeline over one mutable `job` object with 5 refusal terminals and 3 success terminals.

## 1. Phases (as the code actually reads)

| # | phase | lines | what it does | reads | writes | exits |
|---|---|---|---|---|---|---|
| P0 | claim + pre-flight | 5408–5428 | marks the job Running, derives the media paths, declares every outcome flag | `video.Path/RunTimeTicks`, `Plugin.Instance.TempPath`, `job.Id/Mode/SubtitleIndex` | `job.Status/StartedAtUtc/Progress`; locals `videoPath/videoDir/videoNameNoExt/videoExt/tempDir`, 4 flags, `referenceSpec`, `videoDurationForReference`, `backupPath/tempOutput/changedDir/cuesNote` | none |
| P1 | availability + per-file setup | 5451–5480 | volume probe, temp dir, shared extraction dir, media file exists, engine exists, language filter | `_jobContexts[job.Id]` (5459) | `sharedExtractDir`, `ffsubsyncExe`; `job.Phase/Progress` | throws 5472, 5479, 5491 |
| P2a | external input + sibling pick | 5495–5522 | input is the sidecar; picks a sibling embedded track as ruler if any | `video.GetMediaSources(true)` | `subtitleInputPath`, `referenceStream` | none |
| P2b | embedded input + extraction | 5523–5593 | image-codec refusal, real container index, reference pick, extraction, note | `subtitleStream.Codec/Language` | `subtitleInputPath`, `referenceStream`, `job.ExtractionNote/Phase` | throws 5530 |
| P3 | engine reference resolution | 5595–5846 | speech-cache lookup + per-file gate, or sibling reference built from text with reserve/reuse/discard | `SpeechCache`, `_speechGates`, `_referenceGates`, `ReferenceStore` | `referencePath`, `referenceSpec`, `referenceStream`, `usedSubtitleReference`, `speechKey`, `serializeSpeech`, `usingCachedSpeech`, `job.Phase/HoldsSpeechGate`, `cuesNote` | returns 5666, 5689, 5695 (local function) |
| P4 | framerate rescale of the input | 5848–5856 | rescaled copy of the subtitle becomes the engine's input | `config.FixFramerate`, `usedSubtitleReference` | `engineInput`, `referenceArg` | none |
| P5 | the engine run | 5858–6021 | args built, run, stderr parsed, walk measured, cached-speech retry, harvest/prune | `config`, `referenceStream`, `serializeSpeech` | `args`, `exitCode`, `engineScore/Offset`, `engineErrors`, volume-profile observations, `tempOutput` | throws 6011 |
| P6 | stretch verification | 6025–6066 | only when the input was rescaled: ask the audio whether the stretch holds | `engineInput != subtitleInputPath` | `tempOutput`, `engineInput`, `stretchDropped`, `job.Outcome` | none |
| P7 | terminal A: already in sync | 6068–6081 | engine wrote nothing → success, no sidecar | `cuesNote` | `job.Outcome/Phase/Status/Progress` | **return 6080** |
| P8 | reference ceiling + spread | 6083–6209 | measures the change; refuses a subtitle ruler that is a different cut and re-aligns against the audio | `measured`, `referenceSpec` | `measured`, `referenceSpec/Stream/Arg`, `usedSubtitleReference`, `audioFallback` | **return 6207** (refusal B) |
| P9 | search-window ladder | 6211–6364 | on the window, re-align wider, verify against the audio, accept or refuse | `ceilingMs` | `tempOutput`, `measured`, `wideAllowanceApplied` | **return 6330/6362** (refusals C, D) |
| P10 | rescale acceptance + piecewise | 6366–6418 | `PiecewiseHolds` / `IsRescaleAcceptable` decide whether a rescaled answer may be written | `measured`, `config` | none | **return 6417** (refusal E) |
| P11 | suspicious-reference cross-check | 6420–6554 | for a subtitle-aligned shift over 1/3 ceiling: shape score (diagnostic), audio cross-check, keep or discard the ruler | `measured`, `referenceArg` | `cuesNote`, `tempOutput`, `measured`, `referenceSpec/Stream/Arg`, `usedSubtitleReference`, `audioFallback` | none |
| P12 | terminal F: changed nothing | 6556–6580 | re-measures against the user's file when rescaled; no sidecar | `engineInput == subtitleInputPath` | `job.Outcome/Phase/Status/Progress/FinishedAtUtc/OutputPath` | **return 6579** |
| P13 | terminal G: audio-only unverified | 6582–6610 | embedded track with only the audio as ruler → nothing written | `usedSubtitleReference`, `subtitleStream.IsExternal` | same job fields | **return 6609** |
| P14 | engine-output guard | 6612–6623 | refuses a missing/empty engine output before anything is copied | `tempOutput` | none | throws 6620 |
| P15 | the write | 6625–6701 | copy-mode sidecar / replace-mode overwrite / embedded sidecar | `config.SyncModeCopy`, `subtitleStream` | `job.OutputPath`, `changedDir`, `backupPath` | none |
| P16 | verify the written file | 6703–6722 | existence + size of `job.OutputPath`, deletes an empty one | `job.OutputPath` | `job.Phase/Progress` | throws 6721 |
| P17 | outcome text | 6724–6779 | describes what changed (offset, rescale factor, backup, signs note) | `measured`, `cuesNote`, `backupPath` | `job.Outcome/Phase/Status/Progress` | none |
| P18 | announce + library | 6781–6857 | size probe, completion log, folder report, item refresh | `_refreshGate`, `_libraryMonitor`, `_libraryManager` | `outputSize`, `changedDir` | none |
| P19 | catch / finally | 6859–6929 | cancel → Cancelled; error → log, **rollback from `backupPath`**, Failed; finally: temp dir, speech gate, shared dir | `backupPath` | `job.Status/Phase/Error/FinishedAtUtc` | — |

## 2. Fragility map (against the fixes this project has shipped)

**High risk — the phase is the code the listed row changed**

- **P2b** 5523–5593 — `ResolveContainerSubtitleIndexAsync` (5543) is the container-index/ordinal translator (**S14**);
  `ExtractEmbeddedAsync` (5565) is where **D17** (mixed cue index), **E2** (cross-track cue offsets), **S26**
  (read-plan mismatch) and **B8** (`ExtractionOutputGuard`) live. The phase itself only routes; the history is one
  call deep.
- **P3** 5595–5846 — reference selection: **S31** (a ruler trusted on plausibility), **S43** (audio-as-audio),
  **S11** (never hand the container to the engine), **S14** (ordinal → the sibling's identity). The signs-track
  reuse-discard at 5741 is **B8**-adjacent (`LooksLikeSignsTrack` is B8's own helper).
- **P4** 5848–5856 — one assignment, and `engineInput != subtitleInputPath` afterwards means "the plugin rescaled":
  the guard chain at 6028, 6052, 6560, 6743 all key off it. The framerate family lives here.
- **P5** 5858–6021 — `BuildFfSubSyncArgs` + `VadForReference` (**S43**), the cached-speech retry (**B6**-adjacent,
  the retry exists because a cached analysis can be stale), and the walk/volume observation (`WalkCapForProfile`
  at 5946, which is where the **R1/E4** read-policy family is decided).
- **P6** 6025–6066 — `VerifyStretchAgainstAudioAsync` + `AlignmentHoldsAgainstAudio`: the stretch/framerate
  acceptance family.
- **P8** 6083–6209 — `MeasureSyncChange` + `RulerSpreadTooWide`: **S31**'s spread rule.
- **P9** 6211–6364 — the wide-window ladder (2.0.20's 112 s→56 s fix) and the refusal texts **S22** is about.
- **P10** 6366–6418 — `IsRescaleAcceptable` and `PiecewiseHolds` (**C2**, and the gate S31 rejected as a decision).
- **P11** 6420–6554 — the audio cross-check (**S31**), including the forced `webrtc` VAD (**S43**) and
  `RulersDisagree`.

**Medium risk — adjacent to / sharing state with that history**

- **P0/P1** — P1 holds **B6**'s fix (everything that can throw inside the `try`) and **S38**'s volume probe.
- **P7/P12/P13** — terminal decisions that encode **S8** (audio-only writes only for external sidecars, 6588) and
  the "changed nothing is about the user's file" rule (6560).
- **P15** — **S12**'s `SyncedTargetName`, and the only place the user's original file is overwritten.
- **P16/P17** — the output guard and the outcome text; **B22** (open) is the double `MeasureSyncChange` at 6093/6562.
- **P19** — the rollback and the reference-store/finally release (**B6**, S11's shared extraction store).

**Low risk — isolated, no fragile history found**

- **P14** (a plain existence/size guard), **P18** (library notify, `F`-series refresh gate) and the logging lines
  throughout (B15's rule about logging under a lock does not apply here: no lock in this method except the local
  list locks at 5894/5976/5986/6006/6247/6336).

## 3. Shared/mutable state that crosses phase boundaries

**Crossing locals (write sites → read sites), the reason an extraction must thread them explicitly:**

- `referenceStream` writes 5420/5511/5553/5762/5801/5842/6180/6521 → reads 5519…6286 (16 sites)
- `referenceSpec` writes 5431/5715/5841/6179/6520 → reads 22 sites incl. every refusal text
- `referenceArg` writes 5852/6181/6522 → reads 5855, 5859, 5973, 6225, 6269, 6451
- `referencePath` writes 5622/5700/5761/5800/5843/5970 → reads 5852, 6018
- `engineInput` writes 5851/5855/6044 → reads 15 sites (P4→P17)
- `tempOutput` writes 5440/5616/6043/6297 → reads 22 sites (P5→P19); the terminal paths *delete* it
- `speechKey` 5624/5655 → 15 reads; `serializeSpeech` 5623/5692/5971 → 6 reads; `usingCachedSpeech` 5625/5662/5685 → 5863, 5957
- the 4 outcome flags: `usedSubtitleReference` (5425/5763/5802/6178/6519 → 5853, 5912, 6588), `stretchDropped`
  (5426/6040 → 6738), `audioFallback` (5427/6182/6523 → 6733), `wideAllowanceApplied` (5428/6296 → 6218)
- `measured` writes 6093/6183/6298/6524 → 11 reads (the whole refusal chain re-decides on it)
- `cuesNote` 5442/5610/6430/6525 → merged into the outcome at 6760
- `backupPath` 5439/6667 → the catch's rollback at 6874

**Closures that mutate captured locals** — these are what make naive extraction unsafe:

- `PrepareAudioReferenceAsync` (5643–5696) captures and writes `speechKey` (5655), `usingCachedSpeech`
  (5662/5685), `serializeSpeech` (5692), and writes `job.Phase`/`job.HoldsSpeechGate`. It is **called from four
  sites** (5700, 5843, 6142, 6460), so a later call *re-decides* what the earlier phases read. It also returns a
  value (`cached`, `harvestedWhileWaiting`, the reference link) and has 3 internal returns.
- The 5 stderr lambdas (5881, 5983, 6152, 6245, 6476) write `engineErrors`/`wideErrors` (under a local lock) and
  the score/offset nullables read after the await.

**The `job` object is shared with the HTTP layer.** 48 field mutations inside this method (`Status`, `Phase`,
`Progress`, `Outcome`, `Error`, `OutputPath`, `ExtractionNote`, `HoldsSpeechGate`, `StartedAtUtc`,
`FinishedAtUtc`) are read live by the API/UI, so any extraction must keep the same write points, not batch them.

**Reads of other jobs' state**: `_jobContexts[job.Id]` (5459) and `_jobs.Values.Select(...)` (5929) — the walk
judgement looks at *other running jobs* to decide whether its own measurement is about the storage or the moment.

## 4. Test coverage per phase

Facts first: **no check anywhere names `RunSyncJob`** (0 matches in `tests/run_checks.py` and
`tests/rig/run_scenario.py`); the harness *can* build the service (`new SubSyncService(null!, null!, null!, null!)`
at run_checks.py 2980, 3377, 3641, via `InternalsVisibleTo("logictest")`) but the method is `private`, so it is not
invocable from the suite, with or without reflection.

| phase | behavioural coverage | named checks / scenarios |
|---|---|---|
| P1 setup | none | — |
| P2b extraction | rig, indirect | `s39-ratio`, `s41-steady`, `s43-audio-is-audio`, `s31-wrong-ruler` run real jobs through it; extractor itself: the D17/E2/S26 checks ("no subtitle is emitted twice (mixed cue points)", "a located cue point costs one cluster visit", …) |
| P3 reference | unit + rig | "the audio reference forces the audio VAD"; "a reference that cannot be built falls back to the audio"; "the engine is never handed the container as a reference"; "a forced/signs track is never chosen as the reference"; rig `s31-wrong-ruler`, `s43-audio-is-audio` |
| P4 rescale decision | unit only | "a PAL-timed subtitle is the side to rescale"; "two subtitles that both match the file are not rescaled"; `RescaleOntoReferenceSpan` itself is a **source pin** (run_checks.py 4601–4603) |
| P5 engine run | rig + unit on the VAD/args | `s43-audio-is-audio` (the VAD statement + the engine's answer), `s31-wrong-ruler`; args: "opt-in framerate: …", "the speech cache is keyed by the VAD the engine is actually given" |
| P6 stretch | **unit on the predicate + source pin only** | `AlignmentHoldsAgainstAudio` (1936–1939); `VerifyStretchAgainstAudioAsync` appears only as a source pin (4624–4626). **No scenario** |
| P7 terminal A | rig | `s43-audio-is-audio` asserts "already in sync" |
| P8 ceiling/spread | unit | "a sync whose cues moved unevenly measures the spread", "the spread decides, at a quarter of the configured reference ceiling", `RulerSpreadTooWide` (3 checks) |
| P9 wide ladder | **source pin only** | run_checks.py 5041–5050 pins `wide-window.srt` and `AlignmentHoldsAgainstAudio(residualRatio, …)`. **No behavioural test** |
| P10 rescale/piecewise | unit | `IsRescaleAcceptable` (3), piecewise (5 C2 checks) |
| P11 cross-check | unit + rig | `RulersDisagree` ("a ruler and the audio that disagree decide against the ruler"), the band check; rig `s31-wrong-ruler` (both ruler shapes) |
| P12 terminal F | unit on the measure | "a framerate correction is a change even with a zero median offset" |
| P13 terminal G (unverified) | **no coverage** | zero matches for unverified/Unverified in the suite |
| P14 output guard | **no coverage** | — |
| P15 write (copy) | rig | sidecar assertions in `s39-ratio`, `s31-wrong-ruler`; naming: "S12: a language-named sidecar keeps the field form Jellyfin needs" |
| P15 write (replace + backup + rollback) | **no coverage** | `ReplaceExternalSubtitle`, `NextBackupPath`, `bak.subsync`: 0 matches in the suite and 0 in the rig |
| P16 verify written file | **no coverage** | — |
| P17 outcome text | **no coverage** | `DescribeSyncChange`: 0 matches |
| P18 library notify | **no coverage** | `ReportFileSystemChanged`: 0 matches in the suite, 0 in the rig |
| P19 catch/finally | partial | the release paths are indirectly exercised by any scenario that runs two subtitles of one file (`s39-ratio`, `s31-wrong-ruler`); the rollback branch is not |

## 5. Conflicts with open/held rows

- **B22 (open, low)** — "`MeasureSyncChange` is computed twice per job": literally P8's `measured` (6093) and
  P12's `changedForUser` (6562). Any extraction that "cleans up" `measured` touches this row's subject; the row was
  never read against the code, so a refactor here could close or invalidate it.
- **S22 (open, medium)** — the refusal blames the search window when the engine could not read the reference it was
  handed. The offending *text* is P9's refusal bodies (6322–6325 and 6356) and P8's refusal (6200–6202). A
  refactor moving those into one refusal helper would meet S22 head-on: sequence S22 first, or extract the refusal
  helper *as* the S22 fix.
- **E4 (held, premise refuted)** and **R1 (held, per-volume cap)** — both concern the read policy: E4 inside the
  extractor (P2b's callee), R1 at P5's walk judgement (5946) and the planner. R1's row records that wiring a
  per-volume cap means rewriting three wave checks plus a reflection check, so it is not a refactor of this method.
- **S40 (closed tonight as B3/B5/S7/B15)** — the enqueue/lock path; **outside** `RunSyncJob` entirely. No conflict.
- **S8 (decision)** — P13's rule. **S14 (fixed contract half / open stability half)** — P2b's ordinal translation.
  **S43, S31, C2, S26, D17, E2, B6, B8** are all shipped: their code is *inside* the phases listed high-risk above.

## 6. MkvSubtitleExtractor.cs (secondary)

Yes, it has one oversized method — `Extract`, **474–1284 = 811 lines** — but its size is spread far more evenly
than `RunSyncJob`'s: 3246 lines across ~40 methods, of which exactly one is over 200 lines, three are 100–132
(`ScanClusters` 1749–1880 = 132, `ParseCueRefs` 2129–2259 = 131, `ReadClusterChildren` 1632–1748 = 117), and the
remaining ~35 run 9–97 lines. So 811/3246 = 25 % of the file sits in one method, against `RunSyncJob` at
1562/9253 = 17 % of *its* file — and the extractor's neighbours are flatter and far more testable (pure or nearly
pure, and the D17/E2/S26 checks drive them directly), whereas `RunSyncJob` owns the whole run-time decision chain
of a job and has no behavioural test of its own.

## 7. Tooling recommendation

**A scoped, manual extract-method pass is the right approach, but not yet** — the mapping changes the sequencing in
two ways.

1. **Characterization tests first, for the eight phases that have no behavioural coverage at all.** P6 (stretch),
   P9 (wide-window ladder), P13 (unverified refusal), P14 (output guard), P15-replace (backup + rollback), P16,
   P17 (outcome text) and P18 (library notify) are currently pinned only by *source shape* (P6/P9) or by nothing
   (the rest). Extracting those phases today would be a change no test can see: the suite would stay green through
   a behaviour regression. Note the characteristic of this method: the harness cannot call it, so coverage has to
   come from the rig (a scenario per terminal) or from extracting the *pure* decision into a testable helper as
   part of each step.
2. **Order by data flow, not by line order.** The method is a pipeline over one mutable `job` plus a set of
   crossing locals and one state-mutating closure. So:
   - **First**, the terminal blocks that only read `measured`/`cuesNote`/`referenceSpec` and write `job` fields
     (P7, P12, P13, and the four refusal bodies in P8/P9/P10) — these become small methods returning an outcome or
     a refusal, with no shared mutable input beyond their arguments. Low risk, immediate shrink (~250 lines), and
     it is also where S22's fix belongs.
   - **Second**, the engine-run cluster (P5 + the retry + the closures) as one method that returns
     `(exitCode, errors, score, offset)` — the closures stay inside it, which is exactly what makes it safe.
   - **Third**, P15–P18 (write, verify, describe, announce) — mechanistically independent of the alignment chain,
     but with the replace/rollback path needing the characterization test from (1) first.
   - **Last**, P3 reference resolution, which is the one part that cannot be extracted without either threading
     `speechKey`/`serializeSpeech`/`usingCachedSpeech`/`referencePath` in and out or introducing a small context
     object — and it is the part with the most fix history (S31/S43/S11/S14).
3. **Do not hand the low-risk phases to a broad automated refactoring tool blind.** The low-risk list is short
   (P14, P18, logging) and the tool would have to respect the five refusal terminals, the `job` write points read
   live by the API, and the closure's captured-state mutation; the mechanical win is smaller than the review cost.

**What static reading cannot determine** (stated rather than guessed): (a) the true runtime interleaving when
`PrepareAudioReferenceAsync` is called a second or third time (6142/6460) while another job of the same file holds
the speech gate — the values `speechKey`, `serializeSpeech` and `usingCachedSpeech` have at those points depend on
which job won the gate, which only a rig run shows; (b) whether every refusal terminal is reachable in practice
(the refusal bodies at 6200, 6322 and 6356 need specific engine behaviour); (c) the real cost split of P5's
sub-steps on a slow share, which the `enqueue slow:`-style instrumentation does not exist for inside a job.

## 8. Phase 1 characterization coverage (written 2026-09-16, before any extraction)

The map above found eight phases with no behavioural coverage at all. They now have it, without touching
`RunSyncJob`: 21 checks (the suite went from 942 to 963) plus a mutation driver that proves each check fails
against a broken version of the phase it covers.

### Where the tests live

- `tests/job_checks.cs` — the cases. Spliced into the harness by `program_with_job_checks()` in
  `tests/run_checks.py` (the file is split at `// @@TYPES@@` because the class it declares has to follow every
  top-level statement of the generated `Program.cs`).
- `tests/fixtures/fake_ffsubsync.sh` — the stand-in engine. `behaviour`, `payload.N.srt` and `stderr.txt` in its
  own directory decide what each invocation writes, prints and exits with; every invocation's argv is appended to
  `argv.log`, which is how the checks assert *which* reference the engine was handed.
- `tests/fixtures/fake_ffmpeg.sh` — the stand-in ffmpeg, reachable only through `JELLYFIN_FFMPEG`, which the
  single embedded case points at for its own run (this suite's own ffmpeg checks must keep the real binary).
- `tests/backend/mutation_job_checks.py` — breaks one production line per check, rebuilds, and reports which
  checks failed. `python3 tests/backend/mutation_job_checks.py [name ...]`.

### How a private method is driven

`RunSyncJob` has no internal wrapper, so the checks reach it by reflection
(`GetMethod("RunSyncJob", NonPublic | Instance)`), the way this suite already reaches `LogPluginCompletion`. Three
substitutions make that possible, and each is stated in the code:

1. **The engine** comes from `PATH`: with no bundled binary and no settings file, `ResolveFfSubSyncPath()` returns
   the literal `"ffsubsync"` and the spawn resolves against the harness's stand-in.
2. **The media item** is `JobCheckVideo : Video`, overriding `GetMediaSources` — that method is virtual in Jellyfin
   12 and a hand-built `Video` has no media source manager, which is what the plugin's two call sites use.
3. **A sibling reference** is supplied by priming `SubtitleCache.Store(videoPath, ordinal, text)`, the first place
   `TryReadReferenceTextAsync` looks.

### What each check covers

| check | phase | asserts (today's behaviour) |
|---|---|---|
| `RunSyncJob is reachable for characterization` | — | the engine is on PATH and the method was found; everything below depends on it |
| `P7: an engine that writes no output completes as already in sync, with no output path (terminal A)` | P7 6068–6081 | Completed / "Complete" / outcome starts `already in sync (shift under 3 s)` / `OutputPath` null / progress 1.0 / no sidecar |
| `P12: an output identical to the engine's input completes as 'changed nothing' (terminal F)` | P12 6556–6580 | outcome starts `already in sync (+0 ms offset) — nothing written` / `OutputPath` null / no sidecar |
| `P13: an embedded track aligned against the audio alone is refused as unverified (terminal G)` | P13 6582–6610 | Failed / phase `Unverified — audio-only alignment` / error starts `unverified:` and carries `+5000 ms offset` / no sidecar |
| `P14: an engine output with no subtitles in it fails before anything is written next to the media` | P14 6612–6623 | Failed / error contains `synced output is missing or empty` / `OutputPath` null / no sidecar |
| `P5: a non-zero engine exit fails the job and quotes the engine's last output` | P5 6003–6012 | the error is exactly `ffsubsync exited with code 3. Last output: ffsubsync: could not read reference` |
| `P15: copy mode writes a .SYNCED sidecar, leaves the user's file untouched and reports the offset (P17)` | P15 6625–6655, P17 | `OutputPath` is `<stem>.SYNCED.srt`, non-empty, the user's file is byte-identical, outcome `+5000 ms offset` |
| `P17: a signs-sized track adds its note to the outcome and the sidecar is still written` | P17 6760–6765 | outcome carries both the offset and `looks like a forced/signs track`, and the sidecar exists |
| `P15: replace mode replaces the user's file and keeps the original as <name>.bak.subsync` | P15 6656–6673 | `OutputPath` is the original path, the backup exists with the original's bytes, the original now differs, outcome ends `original replaced, kept at <name>` |
| `P19: a replace that fails after the backup restores the original and removes the backup` | P19 6872–6890 | Failed, original restored byte-for-byte, **no backup left**, error names the access failure |
| `P10: a rescaled result that is not a framerate pair is refused, quoting the ratio` | P10 6366–6418 | Failed / phase `Refused` / error contains `refused: the engine rescaled the timings` and `1.0500` and ends with the framerate-pair sentence |
| `P10: a PAL-like rescale with correction on is written, and the outcome names the factor` | P10 | Completed, outcome contains `ratio 1.0417`, the sidecar exists |
| `P9: a window-pinned answer whose wider retry writes nothing refuses and names the setting` | P9 6333–6363 | Failed / phase `Refused` / error starts `refused: the 360 s window produced nothing (exit 0).` and contains `Raise "Maximum offset"` |
| `P9: a wider retry that is itself pinned refuses with both measured shifts` | P9 | error contains `180000 ms reached it`, `the 360 s window measured 300000 ms`, `did not hold up against the film's audio` |
| `P9: a wide-window answer the film's audio still disagrees with is refused with the residual` | P9 6293–6331 | error contains `still asked for 90000 ms more (ratio 1.0000)`; exactly 3 engine runs |
| `P9: a wide-window answer that holds against the audio is written` | P9 6293–6306 | Completed, outcome `+250000 ms offset`, sidecar exists |
| `P8: a subtitle ruler demanding a shift past the ceiling is discarded and the audio's answer written` | P8 6111–6187 | outcome starts `the file's own subtitle track is not the same cut, so this was aligned against the audio`, 2 engine runs, the second not handed the reference file |
| `P8: a wrong-cut ruler whose audio retry produces nothing refuses and says nothing was written` | P8 6188–6208 | Failed / phase `Refused` / error contains `demanded a 45000 ms shift` and `Nothing was written.` / `OutputPath` null |
| `P8: with the reference run's output left in place, that stale file is accepted as the audio's answer` | P8 (see §9) | Completed, outcome as above, and the **written cue is the reference-aligned one** (`00:10:45,000`) although the audio run wrote nothing |
| `S43: a vetted subtitle ruler is handed to the engine as a file and the audio VAD is not forced` | P3/P5 5709–5860 | one engine run, argv contains `/subsync/ref/` and not `--vad webrtc` |
| `P18: a job that wrote a subtitle completes even with no library monitor and no library manager` | P18 6817–6857 | every case that wrote a subtitle is Completed (the monitor and the manager were null throughout) |

### Mutation verification

`tests/backend/mutation_job_checks.py` breaks one line per check, rebuilds the harness and runs it. Caught means
the named check failed with the phase broken; missed means the check would not have noticed.

| mutation | what it breaks | result |
|---|---|---|
| `P7` | the terminal-A sentence (`shift under 3 s` → `4 s`) | CAUGHT — P7 |
| `P12` | the terminal-F sentence (`nothing written` → `nothing to write`) | CAUGHT — P12 |
| `P13` | the terminal-G phase label (em dash → hyphen) | CAUGHT — P13 |
| `P14` | drops the `Length == 0` half of the engine-output guard | CAUGHT — P14 |
| `P5` | the engine-exit message (`code` → `status`) | CAUGHT — P5 |
| `P15-copy` | the sidecar name (`SyncedTargetName` → a fixed name) | CAUGHT — P15-copy and the signs-note check |
| `P17` | `LooksLikeSignsTrack` never fires | CAUGHT — the signs-note check |
| `P15-replace` | the backup name (`NextBackupPath` → a fixed suffix) | CAUGHT — P15-replace |
| `P19` | the rollback guard (`if (backupPath is not null && File.Exists(backupPath))` → `if (false && …)`) | CAUGHT — P19 |
| `P10-refused` | `IsRescaleAcceptable` never refuses | CAUGHT — P10-refused |
| `P10-accepted` | the accepted-rescale shift allowance is tightened (`* 20` → `/ 100`), so the PAL pair is refused | CAUGHT — P10-accepted |
| `P9-nothing` | the job's refusal no longer names the setting (`Raise "Maximum offset"` → `Change the search window`) | CAUGHT — P9-nothing and P9-clamped |
| `P9-clamped` | the clamped-wide-window sentence is reworded | CAUGHT — P9-clamped |
| `P9-verify` | the audio check after the wide window always holds | CAUGHT — P9-clamped and P9-verify |
| `P9-accepted` | the accepted wide answer writes the *first* run's file | CAUGHT — P9-accepted |
| `P8-discard` | the reference ceiling never trips (×100 the limit) | CAUGHT — P8-refusal and P8-stale |
| `P8-refusal` | the refusal no longer says "the file's own subtitle track" | CAUGHT — P8-refusal |
| `P8-stale` | the audio-retry gate never accepts a file | CAUGHT — P8-discard and P8-stale |
| `S43-vad` | the reference handed to the engine becomes the media file | CAUGHT — S43 |
| `P18-catch` | the item refresh catches only `InvalidOperationException`, so the null-manager failure escapes | CAUGHT — eight checks (every case that writes) |

**Every check failed against a broken phase.** Six of the twenty mutations had to be corrected before they proved
anything, and that is the useful part of the record:

- `P19`, first attempt: redirecting the restore's *target* is invisible, because in that fault shape the user's file
  was never damaged in the first place. The one that proves the check is disabling the rollback guard, which leaves
  the backup behind. **The check verifies that the rollback ran** (original intact, backup removed) - it does not
  prove a *damaged* original is restored, which would need fault injection inside `ReplaceExternalSubtitle` itself.
- `P9-nothing`, first attempt: the mutation edited the *plugin log* line, and the check reads the job's `Error`.
- `P9-clamped`, first attempt: the case never reached the branch. The wider window is `max(2 × 180, 300) = 360 s`, so
  a 300 s answer is *inside* it and goes to the verify path instead; the case now uses a 360 s payload and the check
  asserts the refusal the clamped branch actually produces. (The old 300 s case was in fact a second refusal-C
  shape, which is why `P9-verify` also caught this mutation.)
- `P9-accepted` and `P10-accepted`: the first levers were invisible (dropping `measured = wider` changes nothing the
  outcome text shows, and comparing the *original* subtitle instead of the engine's input gives the same ratio), so
  the levers were changed to ones the check can see (`P10-accepted`'s shift allowance, `P9-accepted`'s output path).
- `P8-refusal`: the check did not assert the sentence it was supposed to protect, so it was strengthened to require
  `against the file's own subtitle track s:0` before the mutation could catch it.

One check was also wrong on first writing and was corrected: `P10-accepted` originally asserted a
`stretched to …x onto the reference's timeline` prefix that this case never produces - the plugin did not rescale
the input here, the *engine's* answer merely looks like a rescale, which is the branch the case really covers.

### What is still not covered

- **P4/P6, the framerate stretch** (`RescaleOntoReferenceSpan` and `VerifyStretchAgainstAudioAsync`). The pure
  predicate has unit checks and the call sites have source pins, but no case drives the plugin's *own* rescale: that
  needs a sibling reference whose span differs from the subtitle's by a framerate factor, and the case was not
  written in this pass.
- **P16's post-write guard** (6707–6722): the copy either succeeds or throws, so an empty file can only appear at
  that point through a fault inside the write itself - out of reach without fault injection.
- **The real extraction** (P2b with real ffmpeg and a real container): the harness case uses a stand-in ffmpeg for
  the probe and the fallback. The pre-existing rig scenarios (`s39-ratio`, `s41-steady`, `s31-wrong-ruler`,
  `s43-audio-is-audio`) drive the real path end to end and are unchanged by this work.
- **The real library** (`ILibraryMonitor`/`ILibraryManager`): every case runs with both null, which is what makes the
  P18 check meaningful, but the *effect* of a real folder report is only covered by the rig scenarios that read the
  sidecar back through Jellyfin.

### An observation the characterization tests found (not fixed here)

Writing the P8 case turned up a behaviour that looks like a defect. It is **recorded, not corrected** (this pass
is tests only), and it is pinned by the check that fails above under the `P8-stale` mutation.

**What happens.** When a subtitle ruler is discarded for demanding a shift past the ceiling, the code re-aligns
against the audio (`RunSyncJob` 6142–6187) and hands that run `tempOutput` — **the same path the reference run
just wrote to**. The gate afterwards is `if (audioExit == 0 && File.Exists(tempOutput))`, so when the audio run
exits 0 *without writing* (ffsubsync suppresses its write when the shift is under its threshold, which is exactly
what "this subtitle is already in sync with the film's audio" means) the file left behind by the *discarded ruler's*
run is still there. The job then takes the success branch, measures that stale file, and reports it as the
audio's answer.

**Reproduced deterministically** in `tests/job_checks.cs` (`p8-stale-output`), which asserts today's behaviour:

```
status=Completed
outcome='the file's own subtitle track is not the same cut, so this was aligned against the audio · +45000 ms offset'
runs=2   written-cue=00:10:45,000 --> 00:10:47,000
```

The engine's second run wrote nothing (`payload.2.srt` = `__NONE__`), and the cue written to the library is the
one the **reference** run produced (`+45 s`, the ruler's timeline). With the same case but the stale file removed
(`payload.2.srt` = `__DELETE__`) the job refuses instead, with the sentence the refusal row describes —
`...which demanded a 45000 ms shift — that track is not the same cut — and aligning against the audio instead
produced nothing. Nothing was written.` So the difference is entirely the leftover file, not anything the audio
produced.

**Why it matters.** The row this code exists for (`S31`) is about not writing a wrong-cut ruler's answer. Here the
plugin writes that exact answer and labels it as the checked one, which is the same family as `S22` (a message
that does not describe what happened) with a written sidecar attached.

**Suggested repair for the later pass** (not done here): delete or rename `tempOutput` before the audio retry, or
give the audio retry its own output path, so `File.Exists(tempOutput)` can only be true of a file the audio run
wrote. That is a behaviour change and needs its own before/after evidence, which is why it is a plan row rather
than part of a characterization pass.
