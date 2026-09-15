# The shape gate — built, measured, rejected; shipped as passive logging

Final state, 2026-09-15. Green-lit as a gate, measured on the rig and against the field's own logs, and
**rejected** — the score is now logged as context only and the audio cross-check always runs, exactly as
in 2.0.42. `docs/EVIDENCE_s31_quality_of_fit.md` holds the cue-level head-to-head.

## What shipped

- `Services/QualityOfFit.cs` — the metrics (monotonicity × prominence × non-edgeness) and the source's
  0.75 threshold, fresh C# from autosubsync's MIT `quality_of_fit.py`.
- `Services/SubtitleRulerShape.cs` — builds the shift-score curve for a ruler against the subtitle being
  synced (0.1 s steps, 0.5 s kernel, window = the configured `MaxOffsetSeconds`). The rejected design's
  `SkipsAudioCrossCheck` decision helper is **gone**: nothing in the tree can skip the cross-check.
- `Services/SubSyncService.cs` — before the cross-check, one diagnostic line:
  `[job] ruler shape: quality=… peak=… covering … % of the cues — context only; the audio cross-check
  runs either way`. The cross-check block is not wrapped in any shape condition.
- `tests/run_checks.py` — 7 checks: the score's four properties (trust / doubt / **the blind spot** /
  unreadable input) and three source checks that pin the score *before* the cross-check, no shape
  condition wrapping it, and the "context only" wording. C# suite: **ALL PASS**.
- `tests/rig/run_scenario.py` — the S31 scenario asserts the context line *and* that the cross-check
  still runs and decides; it gained the third ruler kind `offset` for the blind-spot probe.

## Why it was rejected — the gate's own measurement

Rig, this tree's build, real 2.4 GB fixture, local disk, no shim:

| variant | shape score | with the gate | required behaviour |
|---|---|---|---|
| correct (ruler = the film's timeline) | 0.833, peak 803 at −20 000 ms, 100 % | skipped the pass, job **1.53 s** vs 18.05 s (**16.52 s saved, 91.5 %**) | correct answer — the speed win is real |
| other-cut (ruler stretched 1.02×) | 0.025, peak 231, 28.8 % | cross-check ran, ruler discarded, nothing written | unchanged ✓ |
| **blind spot: same cut, +25 s offset** | **0.833 — identical to the correct ruler** | **skipped, wrote `change=+25000 ms`** | **2.0.42 refuses and writes nothing → 3 rig assertions fail** |

## The shipped build, verified on all three scenarios

Same rig, same fixture, this tree's build (log-only):

| variant | job wall clock | score logged | cross-check | verdict |
|---|---|---|---|---|
| correct | **17.56 s** | 0.833 (context) | ran, confirmed (−20 000 vs −20 080 ms) | sidecar written, `change=-20000 ms` ✓ |
| other-cut | **17.49 s** | 0.025 (context) | ran, disagreed (24 170 vs −5 080 ms) | ruler discarded, `UNVERIFIED`, nothing written ✓ |
| **offset (blind spot)** | **17.45 s** | 0.833 (context) | ran, disagreed (25 000 vs −80 ms) | ruler discarded, `UNVERIFIED`, nothing written ✓ — **the probe passes again** |

All three scenarios exit 0 (`tests/rig/results.json`). The gated build's 1.53 s on the correct case is
paid back as 17.56 s: that 16.03 s is the price of not writing a wrong file in the blind-spot case. The
score itself adds nothing measurable — its log line lands 1.48 s after the queue line, against 1.52 s in
the gated build, of which the engine's own ruler pass is 0.87–1.0 s (`ffsubsync exit=0 after …`).

## Q1 — how often is "same cut, just offset" in the field?

From the server's own plugin log (`/subsync-logs/subsync.log*`, 2026-09-12 → 2026-09-15, builds 2.0.7
to 2.0.34), mined by `tests/backend/ruler_field_frequency.py`:

- **710 runs used a subtitle track as the ruler**; 1 135 used the audio.
- **8 runs (1.1 %)** demanded a shift large enough to be worth checking — every one in the **10–30 s**
  band, none below 10 s (they are the `check the result` notes: −29 310, −19 745, −16 541, +22 540,
  +19 880, +26 360, +10 310 ms, some repeated across runs of the same file).
- **No field record of either outcome**: zero `confirmed by the film's own audio` and zero
  `disagreed` lines anywhere in the three logs, because the newest build on that server (2.0.34)
  predates the S31 audio cross-check. So the field cannot yet say how many of those 8 are same-cut
  mis-shifted (safe to skip) against genuinely different cuts (must not skip).

Consequence for the speed argument: the gate can only ever save the pass on the ~1 % of runs that
demand more than 10 s, so its measured 16.52 s/run is worth **≈0.17 s per subtitle-ruler run** on
average — and 0 s on the server today, where the cross-check is not yet running at all.

## Q2 — does a higher bar (0.95) plus a small ruler offset make it safe?

`tests/backend/gate_narrow_band_probe.py`, S31 cues, window ±60 s, cross-check trigger `|demand| > 10 s`:

| ruler offset | score | ≥ 0.95? | cross-check today | narrow gate |
|---|---|---|---|---|
| 2 s | 0.833 | fails | does not run | skips nothing |
| 5 s | 0.833 | fails | does not run | skips nothing |
| 10 s | 0.833 | fails | does not run | skips nothing |
| 20 s | 0.833 | fails | RUNS | does not fire |
| 25 s | 0.833 | fails | RUNS | does not fire |
| 60 s | 0.000 | fails | RUNS | does not fire |

Two independent reasons the narrow band saves nothing:

1. **A 0.95 bar is unreachable for any ruler that asks for a real shift.** The score's non-edgeness
   factor is a function of where the peak sits in the search window, so a perfectly correlated ruler at
   a 20 s offset tops out at **0.833** (and at 60 s it collapses to 0.000). Any threshold above 0.833
   rejects everything that would need skipping.
2. **A "small offset" condition sits below the cross-check's own trigger.** The cross-check runs only
   when `|demand| > ceiling/3` (10 s by default), so a band under a few seconds skips work that was
   never going to happen — and the field's 8 suspicious events were all 10–30 s, so it would have met
   none of them either.

And the blind spot is not offset-dependent: at 2 s, 5 s, 10 s, 20 s and 25 s the same-cut ruler scores
0.833 with the peak exactly at the offset, so there is no small-offset region where the score is safe.

## Decision (closed)

Per fabji's rule — if neither question narrows the risk meaningfully, go log-only — the gate is
**rejected as a skipping mechanism and shipped only as passive logging**, which is what the tree now
does. The score never changes a decision; it explains one.

Reproduce: `python3 tests/run_checks.py`; `python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler
--s31-ruler correct|other-cut|offset`; `python3 tests/backend/{s31_quality_compare,gate_spacing_probe,
gate_narrow_band_probe,ruler_field_frequency}.py`.
