# Goal: an extraction pass must read what its own plan says it will — S26, the 86× over-read

## Objective

Establish why a cue-indexed extraction pass whose plan is *correct* (`the index locates 663 of 663 cue
point(s); 663 located block(s), 0 cluster(s) to walk`) still issues ~25× the planned read calls and
reads ~86× the planned bytes, fix the layer that does it, and hold the read policy's own two ledger
rules at zero while doing so — **without changing one byte of what is extracted.**

This is S26, and it now has a live instance in fabji's library that reproduces in seconds, which is why
it is worth a session of its own: the row's own note already says these reads are the single largest cost
in a batch, and the safety net that caught it (R2's miss flag) only reports the symptom.

## The measurement that justifies the goal (live, 2.0.43, 2026-09-15 13:32)

File `Sunes Sommar 1993 WEB-DL 1080p.mkv` on `/Media/Movies` — a **fast local volume** (`/dev/nvme0n1p2`
measured 0.40 ms/read that minute). The pass's own lines, verbatim:

```
extract plan: cue-indexed, window 2.1 KB, 1319 planned read(s), expects 1.57 MB
  (the index locates 663 of 663 cue point(s); 663 located block(s), 0 cluster(s) to walk,
   reads are 0.00 ms each, merge gap 2 KB)
WARN extract plan: Sunes Sommar 1993 WEB-DL 1080p.mkv cue-indexed expected 1.57 MB/1319 read(s) (5.0 ms),
  actual 133.45 MB/33516 read(s) (192.5 ms) - bytes 85.09x, reads 25.41x - this pass missed its own prediction
extract profile: Sunes Sommar 1993 WEB-DL 1080p.mkv: storage 0.00 ms per read and 1406.4 MB/s
  (32206 read(s) of this pass over 4025 update(s)); merge gap 4 KB
WARN extract: Sunes Sommar 1993 WEB-DL 1080p.mkv read 0,00 MB past the fetch
  (a fetched range that did not cover the read it was made for) - fetched 1319 range(s)/1.57 MB
reference: method=seekhead-cues ms=2097 cues=663 bytesRead=133597851 readCalls=33525
extract lane: … -> 1/2 subtitle(s), 136,0 MB, 34430 reads, ok=True, … bytesTwice=1,37 MB …
```

The arithmetic that frames it: **33 525 read calls for 663 cues ≈ 50 reads per subtitle block**, and
133.6 MB over those calls ≈ **4 KB per read** — i.e. ~200 KB of the container is touched per block the
index had already located exactly. A second pass on the same file, 8 seconds later, shows the same ratios
(85.09x / 25.41x) and takes **192 ms** because the file is now in page cache: **the cost is cold bytes,
so a repro must be cold.**

## The question to answer first (do not assume a culprit)

Why does a *fully located* plan with **zero clusters to walk** produce ~50 read calls per block? Test the
candidates in this order, and write down which one it is before changing anything:

1. **The located-block decision** — `Services/ReadPolicy.cs:561-604`
   (`if (!located || blockStart - clusterStart > MergeGapBytes + BlockRead)`): does a located block get
   served by reading from its cluster head across the gap, per block, instead of around it?
2. **The merge that builds the plan's ranges** — `Merge(reads, MergeGapBytes, MaxPrefetchBytes)`
   (`ReadPolicy.cs:580`, `:702`) — ranges that do not cover the reads issued for them are exactly what the
   `read past the fetch` counter counts, and that counter is non-zero on this pass.
3. **The payload path itself** — `MkvSubtitleExtractor.cs:1763-1830` (`ReadBlock` → `ReadNear`, `:3083`)
   and the offsets built at `:699` (`cueCluster + cueRef.RelativePosition + 32`): an offset that lands
   before the payload rather than on it makes the reader hunt for the block.

If the answer turns out to be **the plan** rather than the reader (e.g. a located block that is not really
located on this file's layout), fix the plan and say so. The row's value is the measurement; the culprit is
whatever the evidence names.

## Done means (verified, not asserted)

1. **A repro that runs in seconds and does not need fabji's library.** The server's media is **not**
   mounted on the development host (`/media` is an empty directory there), so the first step is to obtain
   the shape: copy `Sunes Sommar 1993 WEB-DL 1080p.mkv` into the rig's media directory if it can be
   fetched, or rebuild the layout with the fixture makers (`tests/fixtures/make_remux.py` and its
   `--grouped-cues` / `MKV_FIX_*` switches — the same instruments E2 used). It must be **cold** (page
   cache dropped, or a fresh copy of the file), with plan vs actual printed side by side.
2. **The over-read is gone on that repro**: plan vs actual within a small factor (state the factor and
   why), not "improved". The same numbers, before and after, in the release notes.
3. **Both ledger rules hold at zero on the repro and on the fixtures**: `bytesTwice` and
   `read past the fetch` are the read policy's own acceptance criteria (`ReadPolicy.cs:112` `ReadLedger`,
   its `Describe` at `:212`) — a pass that breaks them is not fixed, whatever its bytes say.
4. **The extraction result is bit-identical**: `kopps 829`, `Sune i Grekland 1019`, D17 mixed-index `803`,
   Helikopterrånet 803/726/898, Mauri 320 cues — unchanged, and the standing correctness tool
   (`tests/backend/nebml_compare.py` + `tests/backend/nebmlrig/`, the NEbml-driven cross-check kept for
   exactly this — note its source lives on the branch `eval/nebml-structure-parser`, not in this tree;
   `tests/backend/nebmlrig/` here holds only build output) agrees.
5. **The suite is green** (`python3 tests/run_checks.py` exits 0), with **new checks that can fail** for
   the defect: the cost invariant this file breaks, expressed on a fixture (e.g. a located plan must not
   read more than N× its planned bytes, with the ledger counters asserted at zero on every fixture).
6. **Measured on both storage profiles in the rig** — local disk and the shimmed share
   (`--ms-per-call 10 --ms-per-16k 1.46`) — because a fix that only helps one of them is not a fix.
7. **The matrix is unregressed**: index present / absent × 1 track / many tracks, including the walk and
   shared-pass shapes. A route that improves this file while degrading the walk route is a regression.
8. **FIX_PLAN's S26 row is updated as the work happens** (not at the end) with the cause once it is known,
   the numbers, and the verdict; the release is cut only when 1-6 hold, then verified the usual way
   (catalog manifest, zip MD5, packaged `meta.json` and DLL version).

## Constraints

- **Output first, cost second.** If a change alters what is extracted, it is wrong regardless of its
  numbers. The subtitles that come out must be byte-identical to today's for every fixture and real file.
- **No window tuning to hide it.** Raising the window, widening the merge gap, or capping reads per pass
  makes the symptom cheaper without answering the question — and the policy's whole point is that the
  route follows what the file provides and what the storage measures.
- **No filename or file special-casing.** This is a layout shape, not a movie.
- **Nothing here touches scheduling, the walk ceiling, the engine invocation or the cache semantics.**
- **Do not accept a warm measurement.** The same file reads 192 ms the second time; a check that re-runs
  the same file cannot disagree with itself, so it proves nothing. Cold, or a copy.

## Failure modes to avoid (each of these cost a session on this project)

- Validating with a check that cannot disagree with the change (the re-run above; a threshold chosen by
  hand after seeing the result).
- Reading the plan's *priced* millisecond figure (`reads × ms/read + bytes / bytesPerMs`) as a measured
  time — B10's correction, already recorded in this plan.
- Fixing one route while another keeps the old behaviour, then reading the mixed outcome as the fix.
- Trusting a single fixture family: the defect appears on a layout the fixtures did not have, which is why
  the first step is to build a fixture that shows it.
- Shipping a cost claim derived from arithmetic instead of a measured pass.

## Process

One change per release, with the rig measurement that justifies it; the plugin log is the acceptance test,
not the changelog. Work top-down in `knowledge/FIX_PLAN.md`, one finding per commit, and leave the row
honest if the budget runs out.
