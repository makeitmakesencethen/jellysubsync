# SubSync — the combined brief: fix the backend **and** the GUI

You are working on the Jellyfin plugin `Jellyfin.Plugin.SubSync` at `/opt/data/jellysubsync` (C#,
`net10.0`, Jellyfin 12) — backend and web GUI in one job, one session, one branch.

Two earlier briefs are scope-limited and still valid as documents: `BACKEND_GOAL_PROMPT.md` (the
original 46-row backend matrix) and `GUI_GOAL_PROMPT.md` (the GUI-only matrix). **This file is the one
to work from.** It carries both surfaces, the state of play after the 2026-09-11 sessions, and a single
patching order. Where this file and an older one disagree, this file wins.

**Mission.** Two things, in this order, and never the other way round:

1. **Finish measuring.** The backend matrices were run for about a third of their rows and the GUI was
   never tested in a real browser at all. Everything that is not measured is not known.
2. **Fix what is proved** — backend and GUI together — highest user impact first, one finding per
   commit, each with a before/after measurement taken the same way on the same profile.

Do not fix anything you have not reproduced. Do not reproduce anything the two records below already
prove, unless you are re-running it as the "before" of a fix.

## What one session will and will not finish (read this before anything else)

The two records together hold about **75 items**: 19 live findings (D1–D19) and 30 static ones (F1–F30)
from the audit, the junk list's line-numbered items, and 7 new ones from the backend test (S3, S4, S5, S7,
S8, S11, S11b). **One session will not fix all of them.** That is expected, not a failure — what matters is
that each session leaves commits that are verified, a report that says what was and was not attempted, and
a list a later session can continue from. The 2026-09-11 session finished roughly a third of its own
matrix; that is the honest scale of this work.

So work in tiers, in this order, and stop cleanly at the end of your budget rather than half-finishing a
tier:

- **Tier 1 — the run has to finish.** S11 (the reference path that parks a job in a whole-file ffmpeg
  demux), S11b (a cancelled batch leaving that demux alive), then the acceptance run: a full 50-track
  episode and a whole series with **zero failures** (A4, A5, A8). If you do nothing else, do this.
- **Tier 2 — it must not write a wrong file or leave junk.** S3 (a −59 s reference alignment is written
  with a note where the docs promise a refusal), S4 (a failed job leaves a 0-byte sidecar in the library),
  and "anything that lies": D5, D12, F27, F28, D2, D6, D11.
- **Tier 3 — the things the user actually touches.** D13 and D14 (does the item-page action work at all?),
  F29 (an armed, unconfirmed global Kill), F24 (a spinner that never clears), D18 (how a page is reached),
  D7 (polling load), D3/D4/F12/F15 (the settings surface).
- **Tier 4 — the rest of the matrices, layout, hygiene, and the low-severity findings.**

Three things are **yours to decide, not the agent's to code**, and it must ask rather than guess:

1. **S8** — a subtitle synced against the *audio* came back `+1780 ms` and was written as a success. Should
   an audio-only result be written at all, or reported as unverified?
2. **D1 / F2** — `Kill` stops every run on the server, with no id and no confirmation. Keep it (now
   admin-only), scope it to the caller's own batch, or remove it?
3. **D4 / F15** — there are two settings pages that edit different subsets of the same configuration.
   Merge them, or make both render the same fields?

Two things **cannot be tested on this machine** and must be named as blocked, not worked around: a
**full disk** and a **separate filesystem for staging** both need a small filesystem this container cannot
create without root.

Whatever is left when the session ends is written into the report under "not attempted", with the reason
and the smallest step that would finish it. **The report is the deliverable that makes the next session
possible** — a session that fixes nothing but documents precisely where it stopped has done its job.

## 0. The record (read these; do not rediscover them)

| Document | What it is |
|---|---|
| `knowledge/audit-2026-09-11.md` | the original audit: 19 live findings (D1–D19) and 60 static ones (F1–F30 + junk). §9 is the original patching order |
| `knowledge/backend-test-report-2026-09-11.md` | the backend test: what was run, what each finding measured, the speed profile, the failure inventory, the settings truth table, and §8b's open questions |
| `knowledge/side-issues-2026-09-11.md` | rough edges found while fixing extraction |
| `AGENTS.md` | the product's own rules — "UI conventions" and "Key Patterns & Gotchas" are both binding |
| `knowledge/architecture-and-gotchas.md`, `config-and-validation.md`, `api-reference.md` | how it is built, what the settings mean, what the endpoints do |

## 1. Where you start (not optional)

- **Branch: `fix/extraction-parity-and-backup`.** Five fixes are there and **not on `beta`** (beta is at
  `8f82ffb`). Starting from `beta` means testing an old backend and editing stale code.
- The server is already running the **phase-2 build** of that branch. Restore the released `2.0.9.0` DLL
  only if you deliberately need pre-fix behaviour, and record which DLL every measurement was taken on.
- Verify the start state before anything else: `python3 tests/run_checks.py` (**334 checks, 0 failures**),
  `GET /SubSync/InstallationStatus`, and the first lines of the plugin log (`startup: version=… assembly=…
  workers=…`).

### The environment, exactly as it is

- Jellyfin 12 at `127.0.0.1:8096`, data in `/opt/data/jf12test`, admin `admin` (empty password), token in
  `/opt/data/tmp/jf_token.txt`.
- **It only starts with `LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu`.** Without it
  Jellyfin dies on missing libicu — or, worse, serves the setup wizard on the same port so every API call
  answers 503/HTML, which looks exactly like "the plugin is broken".
- Media: the real 2.38 GB 50-track episode (as a film *and* as a 2-episode series), a fixture library at
  `/opt/data/jf12test/media-fixtures` (external sidecars, MP4, truncated, no cue index, mixed cue index,
  bitmap/VobSub, ASS, empty track, single track), and a throttled "Slow Storage" library of hardlinks.
- **Slow-storage profile:** no FUSE and no root here, so it is an `LD_PRELOAD` shim
  (`tests/backend/slowread.c` → `slowread.so`) that sleeps proportionally to bytes read, only under
  `SLOWREAD_PREFIX`. Start Jellyfin with
  `LD_PRELOAD=…/slowread.so SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_16K=12.8`;
  the plugin's own log confirms it (`extract: storage 12.94 ms per 16 KB read`). Say which profile every
  timing claim was taken on.
- Harnesses: `tests/backend/` (API driver `ss.py`/`drive.py`, matrix rows `rows_a/c/d.py`, aggregate
  `slow_baseline.py`, fixture builders, `slowread.c`, `browser-item.js`, `evidence-2026-09-11.jsonl`,
  `README.md`). The audit's browser/jsdom harness is in `/opt/data/tmp/audit/` with `shots/`; Playwright is
  at `/tmp/audit/pw/node_modules/playwright`. **Put anything you write into `tests/backend/` or
  `tests/gui/`** — the audit's harness only survived by being copied out of a tmp directory.

## 2. State of play

### Already fixed — verified, do not redo

| Finding | Fix | Verified by |
|---|---|---|
| D17 — a mixed cue index produced 24 cues for a 27-cue track, silently, cached, reported Completed | cue-parity gate; the pass refuses and ffmpeg serves the track | `cues=27` on the fixture that gave `cues=24` |
| D16 — replace mode overwrote the original and deleted its backup | every backup kept (`*.bak.subsync`), path known before the destructive copy, named in the result | `.bak.subsync` present after a real replace |
| S6 — four workers each read the whole 2.38 GB file after the lane had already read it | `ExtractionReady` answers no while a lane pass is in flight for that file | 48/50 tracks in 18 min vs 1/50 in 22 min; the 4-way concurrent pass is gone |
| D15 — any user could install packages, kill every run, wipe caches, read the log | `[Authorize(Policy = "RequiresElevation")]` on `Install`, `Kill`, `SpeechCache/Clear`, `Log` only | 403 for a real non-admin, 200 for the admin, item-scoped endpoints still 200 |

### Open — backend

| Finding | What it is | Severity |
|---|---|---|
| **S11** | the reference derivation falls back to handing ffsubsync the **video file** (`reference=s:1`), so its own ffmpeg demuxes 2.38 GB per job; two jobs sat there 7 and 17 minutes and never finished. The sibling track's text was already cached at the time | high — this is what stops a bulk run finishing |
| **S11b** | cancelling the batch left one of those ffmpeg demuxes alive | high |
| **S3** | a reference track aligned at −59 080 ms is **written** with a warning note, where `AGENTS.md` documents `MaxSubtitleReferenceOffsetSeconds` as a refusal — that setting exists nowhere in the code | high (a written wrong file) |
| **S4** | a failed job left a 0-byte `.SYNCED.eng.srt` in the library; on the next scan Jellyfin logs `FfmpegException: ffprobe failed - streams and format are both null` | medium |
| **S8** | an in-sync subtitle aligned against the **audio** came back `+1780 ms` and was written as a plain success (a sibling-subtitle reference is exact) — needs attribution against a standalone ffsubsync run, then a product decision | medium |
| **S5** | a bitmap track is absent from `GET /SubSync/Subtitles/{id}` (`200 []`) although `/Sync` refuses it with a correct message — the user cannot see it to learn why | low/confusing |
| **F4** | `Jobs`, `Batches`, `InstallationStatus` are still readable by any user (cross-user history, server paths). Closing it needs those filtered to the caller's own jobs **and** the item-page dialog driven as a non-admin to prove it | medium |
| D1/F2 | `Kill` is still global and id-less; the UI arms it behind one click with no confirm (F29) | medium |
| D8/D9/D10 | batch validation looser than single sync, no dedupe anywhere, inconsistent error bodies | medium |
| D3/F10/F11/F12/F13 | the settings surface: no range checks, free-text encoding, golden-section inert without framerate, worker count half-applied, two pages that disagree (F15) | medium |
| F6/F7/F14/F16/F17/F18/F21/F23/F30 | clear-cache vs running jobs, orphaned scratch, sweep has no trigger, cache accounting, middleware body/compression, anonymous client script, `SweepState` cap | low–medium |

### Open — GUI

The GUI was never tested in a real browser beyond one screenshot pass. Every claim below is to be
**confirmed or refuted there**, and jsdom may never be the evidence for a visual claim (it has no layout
engine). The two that have never been verified at all are the ones that matter most:

- **D14** — does the injected client script actually hook the Jellyfin 12 item page? (It is delivered into
  the SPA shell; nobody has seen its button.)
- **D13** — does one press of "Sync selected" start a batch, or only preview one?

Then: D4/F15 (two settings surfaces with different subsets), D5 ("4 in use" while idle), D6 (history's
empty state beside a real run), D7 (~1.4 req/s idle polling, three duplicate calls on load), D18
(`EnableInMainMenu` yields no entry in Jellyfin 12), D19 (the golden-section checkbox measured 1×1 px),
F12, F22–F29, and the junk list's line-numbered items (stale `LANG_NAMES`, unreachable handlers, dead ids,
mis-indented `</div>`, duplicated helpers between the three files).

## 3. Hard rules (breaking these fails the job)

- **Real everything.** Real Jellyfin, real media, real ffsubsync runs, a real browser. No mocks, no
  "should work", no claim without a log line, an API record, a file hash, or a screenshot.
- **Never fabricate a number or an outcome.** Report bytes, reads, ms, cue counts, request counts and
  timings as the plugin's own log and the captured traffic print them. If it is not measured, write
  "not measured". A blocked row is a fine result; an invented one is not.
- **Phase 1 changes no production code.** Evidence first. Fixes second, one finding per commit.
- **Product rules that must not regress:**
  - the plugin never refuses a job — a bad reference means falling back to the audio;
  - "Clear cache" clears everything (speech cache, extracted subtitles, references, scratch);
  - progress is honest — real MB / reads / ms / counts, never a fake percentage, never a bar that moves
    for no reason;
  - the original subtitle is never destroyed without a deliberate setting and a way back;
  - **a number on screen equals the API value behind it**; a state that is no longer true must stop being
    shown.
- **Do not trade correctness for speed.** Cue parity, verify-by-cue-count, and the honest-status rules stay.
- **Test on the test server.** The user's server may be read for corroboration (their plugin log is mounted
  read-only at `/subsync-logs/`) and only for shows they have allowed; ask before anything else.
- Keep the check suite green (**334 checks today**) and add a check for every fixed defect that can be
  pinned in source. A check that starts failing is a finding, not something to delete.
- Never leave the server, its library, its settings or the branch in a broken state: restore the config,
  delete throwaway users and fixtures, say where every file went.

## 4. Matrix A — backend rows still to run

The rows already done are in the report's coverage table with their evidence; re-run one only as the
"before" of a fix. These are the open ones:

| Row | What to run |
|---|---|
| A3 | all tracks of one film (the language picker's "all"), fast and slow profile |
| A4 | all tracks of one episode — **finish** it: 48 of 50 completed last time, zero failures, and the last two were parked in S11 |
| A5 | whole series from the series detail page (100 tasks over 2 episodes) |
| A6 | one season |
| A7 | a hand-picked multi-episode selection from the menu page |
| A8 | bulk with `ParallelWorkers` = 1, 2, 4, 8 on the slow profile — the aggregate number that matters |
| C23a | the file with **no cue index at all** (the cluster walk) — the fixture was corrupt last time and was rebuilt but never run |
| D29–D37 | the settings rows: `SyncLanguages` (match / no match / mixed case / unknown / empty), `ParallelWorkers` limits, `MultiSyncMode` auto vs normal vs ultimate on the same batch, `MaxOffsetSeconds`/`MaxSubtitleSeconds` (normal, zero, negative, absurd — and what argv actually reaches ffsubsync), `VadMethod`, golden-section, framerate, `ExtractionTimeoutMinutes`, broken `FfSubSyncPath`/`FfmpegPath`, sweep limits. `tests/backend/rows_d.py` runs most of them |
| E39–E45 | read-only library folder; staging on another filesystem; missing/broken ffsubsync; cancel mid-extraction and mid-ffsubsync then `Kill`; restart with jobs queued; an unprobeable file and a file that disappears between queue and run; two users acting at once. (`Kill` is now admin-only, so E45 also tests what a non-admin sees.) |

## 5. Matrix B — GUI rows (all of them)

Run each in a **real browser** against the live server, at desktop and narrow widths, dark and light.
Evidence per row: screenshot file name, the captured `/SubSync/*` calls, the console log, and the DOM text
you actually read.

**Entry points and paths.** 1) menu page (`#/configurationpage?name=subsync-main`): library → series →
episodes → track picker → sync. 2) back out of every level into a different item; is state kept? 3) the
language filter: one language, several, one the file does not have. 4) the 50-track episode's track list:
does it render, scroll, stay usable? 5) empty selection, no library, one-item library, a search with no
result. 6) **History**: nothing, one run, failures, a cancelled batch (D6). 7) **Settings**: every field,
saved one at a time, read back from `GET /Plugins/{id}/Configuration` and from the other page. 8) cancel a
running batch from the UI, then the armed "Kill all syncing" (F29, D1). 9) the menu page **while a batch
runs**: count the requests per tab per minute, idle and busy (D7). 10) film detail page → injected "Sync
Subtitles" → one track, then all tracks (**D14**). 11) episode detail page → the same two paths. 12) series
detail page → "Sync all episodes" → whole series; then one season; then a single episode (**D13**).
13) a **non-admin** on the item page: the dialog must still work, and `Install`/`Kill`/`Clear`/`Log` must
403 without breaking it. 14) how a user is meant to reach the plugin's pages at all (D18).

**States and honesty.** 15) empty / loading / error states for every panel, including a request that fails
mid-load. 16) **truthfulness**: for every number shown (workers, totals, ok/failed, progress, cue counts,
cache sizes, versions) assert it equals the API value at that moment (D5, D12). 17) list correctness at the
edges: 0, 1, a batch with failures, a cancelled batch, a batch whose jobs were evicted from history.
18) progress during a real bulk run on the slow profile: does anything move, is it real, does it match the
plugin log? 19) error shapes for a malformed request, an unknown item, a 403, a 500 (F28). 20) console and
network hygiene: no uncaught error, no plugin-caused 4xx/5xx, no duplicate script evaluation (F25).

**Settings surface.** 21) round-trip every field: typed → saved → stored → read back by both pages →
honoured by a run. 22) hostile values exactly as the audit typed them (D3). 23) golden-section vs framerate
(F12, D19). 24) install button and status line with a nonexistent binary path (D2). 25) the sweep knobs and
the sweep's missing trigger (F14).

**Layout, input, robustness.** 26) 1920 / 1440 / 1024 / 768 / 420 px and a resize mid-interaction.
27) keyboard only: tab order, Enter/Space on every control, visible focus, Esc, no focus trap. 28) the page
embedded in the Jellyfin shell **and** standalone (it loses its `<head>` when embedded). 29) both themes.
30) throttled/blocked responses: no lying, no double submit, no lost selection. 31) reload mid-batch, two
tabs at once, a second browser session.

**Failure and lifecycle.** 32) server restart mid-batch with the page open — does it recover? 33) token or
session revoked while a page is open. 34) make a settings save fail (F24: no `.catch`) — does the spinner
clear and do the typed values survive? 35) F23 (`ClientScript` is anonymous) and F22 (the middleware forces
`Accept-Encoding: identity` on every web-client bootstrap — measure the cost if you can). 36) a restricted
user: what do the plugin's pages show them?

## 6. Measurement rules

- **Backend:** bytes read, read calls, ms per read, per-stage and per-file timings from the plugin's own
  `extract lane:` / `extract: method=…` / `ffsubsync start|exit` / `job … completed` lines; cue parity
  against the file's own count (`ffprobe` / the cue index) as a correctness gate.
- **GUI:** requests per minute per tab (idle and busy), time to first meaningful content, time from "press
  sync" to the first honest progress, time to a terminal state, and how long a stale state persisted. Any
  pixel claim needs a screenshot **and** a DOM measurement at a stated viewport.
- Every speed or behaviour claim is a **before/after pair on the same file, same track, same profile, same
  browser steps**. A change without a measurement is not a fix. If jsdom and the real browser disagree, the
  browser is the truth and the disagreement is itself a finding about the harness.
- The aggregate numbers the report must carry: a full 50-track episode single vs bulk, a whole series, the
  effect of `ParallelWorkers` on the slow profile, and the request/traffic profile per GUI tab.

## 7. Deliverable, phase 1 (document, no code changes)

Two report files, or one with two halves — `knowledge/backend-test-report-<date>.md` (append a new dated
section; the 2026-09-11 one stays as the record) and `knowledge/gui-test-report-<date>.md`. Both in the
shape the user already approved (the audit's):

1. **Coverage table** — every row of Matrix A and B: result, evidence id (job id / screenshot / request
   count), pass / fail / blocked with the precise reason.
2. **Findings** — symptom → exact repro (command, API call, or route + viewport + clicks) → evidence →
   impact → severity → fix direction. Separate **broken**, **slow**, **confusing**, **cosmetic**.
   Every audit finding (D1–D19, F1–F30) marked **confirmed**, **refuted** or **not attempted**.
3. **Speed / traffic profile** — backend per stage on both storage profiles; GUI requests per tab.
4. **Failure inventory** — what each failure left behind: clean library? other jobs alive? actionable
   message? stuck control?
5. **Truthfulness table** — every number on screen against the API value behind it.
6. **Settings truth table** — what each setting does in practice vs what the UI/README claims.
7. **Junk** — dead code, dead ids, unreachable handlers, duplicated helpers, unused settings, leftovers.
8. **What was not tested** and why, with the smallest step that would test it.
9. **Patching order**, highest user impact first.

Commit locally; do not push to a release branch.

## 8. Deliverable, phase 2 — the patching order

One finding per commit, before/after evidence taken the same way, one-line reason in the message.

1. **S11** — a bulk run must not end in a whole-file ffmpeg demux per job. When the sibling track's text is
   already cached, build the reference from it; never hand the engine the container.
2. **S11b** — cancellation must reach the engine's children; a cancelled batch leaves nothing running.
3. **A4/A5/A8 finished** — the full 50-track episode and a whole series with **zero failures**, no orphaned
   scratch, no half-written subtitles, and a measured speed-up over the 2026-09-11 baseline on the slow
   profile. This is the acceptance test for 1 and 2.
4. **S3** — implement `MaxSubtitleReferenceOffsetSeconds` as a refusal, as `AGENTS.md` already claims.
5. **S4** — never leave a sidecar behind for a failed job.
6. **D13 / D14** — the item-page action: fix it if it does not work, and say in the report what a real
   browser showed.
7. **Anything that lies** — D5, D12, F27, F28, D2, D6, D11, and any truthfulness finding of your own.
8. **Stuck or destructive controls** — F29 (armed Kill with no confirm), F24 (spinner that never clears),
   D1/F2 (a global, id-less Kill).
9. **Unusable paths** — D18 (how a user reaches the pages), D7 (polling load), F4 (per-user scoping of
   `Jobs`/`Batches`/`InstallationStatus`, verified by driving the dialog as a non-admin), S5.
10. **Settings surface** — D4/F15 one source of truth, D3/F10/F11/F12/F13 honest validation and honest
    effects, F14 the sweep trigger.
11. **S8** — the audio-reference product decision, once attributed.
12. **Layout, robustness, hygiene** — the rest of Matrix B, then the junk list, then the low-severity rows.

Never trade correctness for speed, and never close a finding by deleting the check that caught it.
Anything released goes through the GitHub Actions workflow on `beta` and must be verified in the public
manifest (zip HTTP 200, checksum, DLL contents) before it is called done.

## 9. Definition of done

- **Matrix A complete**: every row attempted, with evidence; blocked rows explained precisely.
- **Matrix B complete**: the GUI exercised in a real browser on every entry point, state and width, with
  screenshots and captured calls; D13 and D14 answered in writing.
- Every audit finding marked confirmed or refuted.
- The patching order worked as far as each finding's evidence justified; each fix verified by re-running
  the step that exposed it.
- A full 50-track episode **and** a whole series finish with **zero failures**, no orphaned scratch, no
  half-written or empty subtitles, and a measured speed-up over the 2026-09-11 baseline on the slow profile.
- Every number on every page equals the API value behind it; no stuck spinner, no armed destructive
  control, no uncaught console error in any exercised path; the item page and series page work for an
  ordinary non-admin user while the four administrator-only endpoints return 403.
- `tests/run_checks.py` green with new checks for the fixed defects.
- A one-page summary: what was broken, what was slow, what was confusing, what changed, what is still open,
  and how long each test run took.

## 10. Practical notes

- Start every session with `python3 tests/run_checks.py`, `GET /SubSync/InstallationStatus`, and the plugin
  log's first lines — they state the version, the binary in use and the worker setting.
- Keep jobs' scratch directories until a finding is written; they are the only forensic record.
- In the browser: log in through the real login form so the pages see a real session; record browser +
  viewport + which DLL + whether the slow profile was active before making any claim.
- Watch the documented traps while working: the menu page loses its `<head>` when embedded (all its
  `<style>` lives in the body), `emby-select`/`emby-input` inject sibling `<label>` elements (never lay
  those out in a flex row), every call must use `Authorization: MediaBrowser Token="…"`
  (`X-Emby-Token` and `?api_key=` are dead on Jellyfin 12), and helpers used by a page must be defined in
  that page — the injected script is an IIFE and shares nothing.
- Where a GUI symptom has its cause in the backend, fix the backend in its own commit and say so. The two
  surfaces share exactly `Api/SubSyncController.cs` and `Services/SubSyncService.cs`.
- Ask the user before anything that touches their own server, and before any release.
