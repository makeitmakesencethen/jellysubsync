# Evidence — S31: autosubsync's shape-based quality score vs the plugin's current checks

Measured 2026-09-15 on the S31 fixture's own cue data (803 cues per track, extracted from the real
2.4 GB `S31 Episode (2026).mkv`). No synthetic arrays: the numbers below come from the fixture's
real timings and from the plugin's real C# (`Services/QualityOfFit.cs`, run by
`tests/backend/qfit_runner/`, which compiles that file as-is).

## What was measured

| variant | shape score | threshold | shape says | parity | span stretch | ruler demand | the current checks say |
|---|---|---|---|---|---|---|---|
| `other-cut` (wrong ruler, 1.02x stretch) | **0.021** | 0.75 | REJECTED | pass | 2.00 % | 24.2 s | **ACCEPTED** |
| `correct` (film's own timeline, +20 s) | **0.833** | 0.75 | TRUSTWORTHY | pass | 0.00 % | 20.2 s | **ACCEPTED** |

Reproduce: `python3 tests/backend/s31_quality_compare.py --sigma 0.5`
(curves: `tests/backend/s31_quality_curves.py`, runner: `tests/backend/qfit_runner/`).

The three metrics behind the score, per variant (σ = 0.5 s kernel):

| variant | peak | peak at | monotonicity | prominence | non-edgeness | quality |
|---|---|---|---|---|---|---|
| other-cut | 230 / 803 | +19.9 s (wrong place) | 0.800 | 0.032 | 0.833 | 0.021 |
| correct | 803 / 803 | −20.0 s (the right place) | 1.000 | 1.000 | 0.833 | 0.833 |

## What it says

1. **The shape score separates the two variants; the current read-time checks do not.** Both rulers
   pass cue-count parity, the 3 % span rule and the 30 s `MaxSubtitleReferenceOffsetSeconds` ceiling
   (2.00 % and 24.2 s; 0.00 % and 20.2 s) — so the plugin's own plausibility checks accept the wrong
   ruler, which is the S31 defect. The score rejects it (0.021 < 0.75) and accepts the right one
   (0.833 ≥ 0.75): **0/2 separation against 2/2 separation.**
2. **It is not sensitive to the kernel width.** Re-run at σ = 0.25 / 0.5 / 1.0 / 2.0 s:
   other-cut 0.017 / 0.021 / 0.140 / 0.241 (always rejected), correct 0.833 at every width (always
   trusted). The source's own 0.75 threshold sits in the gap at every width, so the verdict here is
   not an artefact of a tuned constant.
3. **It costs no media read.** Parsing the two tracks and building the 801-sample curve takes
   3.2 ms + 372 ms in Python (`tests/backend/s31_quality_curves.py`) and a few ms in C#; the audio
   cross-check it would stand in front of costs one audio alignment — 22–421 s per file on this
   share (measured in S28, `FIX_PLAN` S28/S28-server-levels).
4. **Where it would sit.** The plugin runs the audio cross-check whenever a subtitle ruler demands a
   shift past `referenceCeilingMs * SuspiciousReferenceShiftFraction`
   (`Services/SubSyncService.cs:5825-5829`, cross-check at `:5846+`). Both S31 variants cross that
   gate, so today the correct ruler pays an audio pass only to be confirmed, and the wrong ruler pays
   one to be discarded. A shape score at that point decides both cases from the two subtitle files
   alone: discard the other-cut ruler (0.021) without an audio read, and confirm the correct one
   (0.833) without an audio read.

## The end-to-end half — run on the rig, both variants (2026-09-15 11:59 / 12:15)

`python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler --s31-ruler other-cut|correct`,
this tree's build (2.0.42.0), the real 2.4 GB fixture, no shim (local disk). Both runs exited 0 with
every assertion holding (`tests/rig/results.json`, 955.9 s / 68.3 s of scenario wall clock — the first
number includes the rig's own 900 s evidence wait, so the job numbers below come from the plugin log).

| variant | shape score (cue-only) | shape verdict | the plugin's ruler answer | the cross-check's answer | the plugin's verdict | cross-check cost | job wall clock |
|---|---|---|---|---|---|---|---|
| other-cut | 0.021 | REJECTED | +24.170 s, score 66 451 (wrong) | −5.080 s, score 46 805 | disagree by 29.250 s → ruler discarded, audio's answer, `UNVERIFIED`, nothing written | 16.64 s | ~19 s |
| correct | 0.833 | TRUSTWORTHY | −20.000 s, score 287 443 (right) | −20.080 s, score 46 805 | confirmed within 0.08 s → ruler kept, sidecar written (56 767 B) | 16.52 s | 18.05 s |

**Both mechanisms reached the right verdict on both variants — 2/2 each.** The difference is what they
cost: the shape score needs the two subtitle tracks (3.2 ms to parse + 372 ms to build the curve in
Python, a few ms in C#), the cross-check needs an audio pass (16.5–16.6 s per job on local disk here;
22–421 s per file on the user's share per S28).

The correct variant is where that matters most: of its 18.05 s job, **16.52 s (91.5 %) was the audio
pass spent confirming a ruler the score had already trusted** (0.833 ≥ 0.75). The wrong variant's
16.64 s bought a decision the score also reaches, without reading the media at all.


## What this does not prove

- **It is not wired into the plugin.** `QualityOfFit.cs` exists and builds (0 warnings / 0 errors);
  nothing calls it. The gate it would replace is identified (`Services/SubSyncService.cs:5825-5829`),
  and the saving above is what that gate spends today — proving the end-to-end saving needs the gate
  wired and the two rig variants re-run.
- **One fixture, one failure shape.** The score is a self-consistency measure of the ruler against
  the target, so it sees a stretched or mismatched ruler; it cannot see a case where *both* tracks are
  from the same wrong cut. The audio cross-check remains the authority there — the two agree on both
  S31 variants, they are not interchangeable.

