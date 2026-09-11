# SubSync GUI — exhaustive test, truthfulness and polish brief

You are working on the **web GUI of the Jellyfin plugin `Jellyfin.Plugin.SubSync`** at
`/opt/data/jellysubsync`. The GUI is three things, all of them served by the plugin:

- `Web/subsyncMain.html` (2398 lines) — the plugin's own **menu page**: *Sync* browser (library →
  series/film rows → episode rows → track picker → sync buttons), *History*, *Settings*.
- `Web/configPage.html` (331 lines) — the legacy **dashboard plugin-settings** page.
- `Web/subsync.js` (977 lines) — the **injected client script** that adds a "Sync Subtitles" action to
  the real Jellyfin item detail page.

The backend behind them was tested exhaustively on 2026-09-11 (`knowledge/backend-test-report-2026-09-11.md`,
plus four fixes). **The GUI was not.** The audit's GUI pass was jsdom plus one real-browser screenshot run,
and it left its two most important questions — *does the injected script actually hook the Jellyfin 12 item
page* (D14), and *does one press of "Sync selected" start anything* (D13) — explicitly **unverified**.

**Mission:** drive every GUI path in a **real browser** on a **real Jellyfin**, in every state it can be in
(empty, loading, running, failed, cancelled, second user, narrow window, restart), and document what
actually happens in a report meant for later patching. Then fix what the report proves, starting with
anything that lies to the user. Document first, patch second.

## 0. Read these first — the backlog you are verifying, not rediscovering

| What | Why |
|---|---|
| `knowledge/backend-test-report-2026-09-11.md` | the backend's current behaviour, the settings truth table, §8b's open questions, `tests/backend/` harness |
| `knowledge/audit-2026-09-11.md` | §2 D1–D19 (the live findings; the GUI ones are D4, D5, D6, D7, D13, D14, D18, D19), §7.2 F4, F15, F22–F29, and its junk list with line numbers |
| `AGENTS.md` → "UI conventions" and "Key Patterns & Gotchas" | the rules the pages already broke once and must not break again |
| `knowledge/architecture-and-gotchas.md` | how the pages are served, embedded resources, the middleware |
| `knowledge/side-issues-2026-09-11.md` | known rough edges, some of them UI-facing |

**Treat these as the backlog.** Every GUI finding in the audit is a claim to confirm or refute in a real
browser — several of the audit's GUI conclusions were reached with jsdom, which has **no layout engine**
(`getClientRects()` returns empty), so any claim about *visibility*, *overlap*, *clipping* or *size* from
that pass must be re-derived visually or measurement-by-measurement. Do not repeat an audit claim as fact
without re-proving it.

**Already verified, do not re-audit:** both HTML files are tag-balanced, have no duplicate live ids and no
missing `$('id')` targets; every `innerHTML` sink escapes via `esc()` or `textContent` (no XSS found).
The stray `</div>` (F26) is already fixed.

## 0b. Where to start, and what not to redo (not optional)

Five backend fixes from the 2026-09-11 session are **in the working tree on the branch
`fix/extraction-parity-and-backup` and are NOT on `beta`** (beta is still at `8f82ffb`):

| Already fixed | Where |
|---|---|
| D17 — a half-read subtitle track is refused instead of cached and reported Completed | `MkvSubtitleExtractor.cs` |
| D16 — replace mode keeps every backup and says so in the result | `SubSyncService.cs` |
| S6 — a job no longer reads a file the extraction lane is already reading | `SubSyncService.cs` |
| D15 — `Install`, `Kill`, `SpeechCache/Clear`, `Log` are administrator-only | `SubSyncController.cs` |
| 334 checks in `tests/run_checks.py` (was 333) | `tests/run_checks.py` |

**Do not redo any of those, and do not test the GUI against a backend that lacks them.** Work on the
branch that carries them. If you start from `beta` you will test an old backend, your edits to the two
shared files will be made against stale code, and the check count will be wrong.

**The two sessions share exactly two production files** — `Api/SubSyncController.cs` and
`Services/SubSyncService.cs` — so these GUI findings are *backend* edits, not frontend ones:

| Finding | Where the fix lands |
|---|---|
| D5 "4 in use (setting 4)" while nothing runs | `SubSyncService.cs` (`WorkerSummary`) |
| D11 / F27 the resolved mode is opaque; `/SubSync/Active` omits the worker triple | `SubSyncController.cs` |
| D12 two versions reported at once | `SubSyncController.cs` (`InstallationStatus`) |
| D2 "installed successfully", and a status line that names a binary that is not there | `SubSyncController.cs` + `SubSyncService.cs` |
| F28 inconsistent error bodies | `SubSyncController.cs` |
| F4 the residual cross-user history leak (`Jobs`/`Batches`/`InstallationStatus` are still open to any user) | `SubSyncController.cs`, and it needs the dialog driven as a non-admin to prove it |

Everything else in the matrix is `Web/subsyncMain.html`, `Web/configPage.html`, `Web/subsync.js`, or
documentation. `MkvSubtitleExtractor.cs` is not yours to touch.

Phase 2 for this brief is **the GUI**; where a GUI-visible lie has its cause in the backend, fix the
backend in one commit and say so in the report. Do not fold an unrelated backend rewrite into this work.

## 1. Hard rules (breaking these fails the job)

- **A real browser against a real server, or nothing.** Chromium via Playwright is installed at
  `/tmp/audit/pw/node_modules/playwright`; `playwright-core` is in `/opt/data/tmp/audit/node_modules`.
  jsdom may be used for driving logic, but **never** as the evidence for a visual claim.
- **Never fabricate a number or an outcome.** Evidence is: a screenshot (the audit's are in
  `/opt/data/tmp/audit/shots/` — put yours somewhere durable, e.g. `tests/gui/shots/`, and reference the
  file name from the report), the captured request/response list, the console log, and the DOM text you
  actually read. If you did not measure it, write "not measured".
- **Phase 1 changes no production code.** It produces the report. Fixes happen in phase 2, one finding at a
  time, each verified in the same browser under the same steps as the "before".
- **Test on your own Jellyfin**, never the user's server. Their log is mounted read-only at
  `/subsync-logs/` if you need corroboration.
- **Product rules that must not regress:**
  - progress is honest — real MB / reads / ms / counts, **never a fake percentage and never a bar that
    moves for no reason**;
  - the plugin never refuses a job — a bad reference means falling back to the audio, not an error dialog;
  - "Clear cache" clears everything (speech cache, extracted subtitles, reference files, scratch);
  - the original subtitle is never destroyed without a deliberate setting and a way back;
  - **no fake intermediate state**: if the UI shows a number, that number must equal what `/SubSync/*`
    returned when it was drawn.
- **The settings a page writes must be the settings the backend reads.** The backend now reads some of them
  through `SettingsSource.Current()`; a save that appears to work but changes nothing is a finding, not a
  success.
- Keep the check suite green: `python3 tests/run_checks.py` (334 checks today — it pins source-level
  invariants, including the new administrator-only endpoints). If a change makes a check fail, that is a
  finding, not something to delete. **Add a check for every GUI defect you fix** that can be pinned in
  source.
- Never leave the test server, its library or its settings in a broken state: restore the config, delete
  throwaway users and test items, and say where every file went.

## 2. The environment, exactly as it is (this bit cost a whole session to work out)

- Test server: Jellyfin 12 at `127.0.0.1:8096`, data in `/opt/data/jf12test`, admin user `admin` (empty
  password), token in `/opt/data/tmp/jf_token.txt`.
- **It only starts with `LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu`** — this host has no
  system libicu, and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is *not* a substitute (Jellyfin then dies
  with `CultureNotFoundException: en-US`). Without it the server serves the setup wizard on the same port
  and every API call answers 503/HTML, which looks exactly like "the plugin is broken".
- Start command (background, tracked): `cd /opt/data/jf12test/jellyfin && env LD_LIBRARY_PATH=… JELLYFIN_DATA_DIR=/opt/data/jf12test/data JELLYFIN_CONFIG_DIR=/opt/data/jf12test/config JELLYFIN_CACHE_DIR=/opt/data/jf12test/cache JELLYFIN_LOG_DIR=/opt/data/jf12test/log JELLYFIN_WEB_DIR=/opt/data/jf12test/jellyfin/jellyfin-web DOTNET_ROOT=/opt/data/.dotnet /opt/data/.dotnet/dotnet /opt/data/jf12test/jellyfin/jellyfin.dll`
- **Which build is installed matters.** The server currently carries the **phase-2 backend build** (D17/D16/S6
  fixes + the administrator-only gate on `Install`, `Kill`, `SpeechCache/Clear`, `Log`), copied over
  `data/plugins/SubSync_2.0.9.0/Jellyfin.Plugin.SubSync.dll`. Record which DLL you tested against in the
  report's first table; if you need the released behaviour, put the `2.0.9.0` zip's DLL back first.
- Media: the real 2.38 GB 50-track episode (both as a film and as a 2-episode series), plus this
  session's fixture library in `/opt/data/jf12test/media-fixtures` (register it as "SubSync Fixtures"):
  external sidecars, MP4, truncated tail, no cue index, mixed cue index, bitmap (VobSub), ASS, an empty
  track, a single-track file. There is also a throttled
  "Slow Storage" library — see §4.
- Existing harnesses, all still on disk: `/opt/data/tmp/audit/{gui.js,main-flow.js,main-sync.js,main-investigate.js,settings-and-inject.js,route-check.js,menu-path.js,browser-visual.js,browser-pass.js}` and
  `shots/`; `tests/backend/{ss.py,drive.py,rows_*.py,slow_baseline.py,browser-item.js}` with `README.md`.
  Move anything you write or fix into `tests/backend/` (or a `tests/gui/` of your own) — the audit's harness
  only survived because someone copied it out of a tmp directory.

## 3. Test matrix — attempt every row; name the ones you could not

For each row record: surface, route/URL, starting state, clicks/keys performed, screenshots, the
`/SubSync/*` calls observed, what the UI displayed, wall-clock of the interaction, and pass / fail /
blocked with the finding id.

**A. The three surfaces, every path**

1. Menu page (`#/configurationpage?name=subsync-main`): library picker → series → episodes → track picker → sync.
2. Menu page: back out of every level and into a different item; is state kept or lost?
3. Menu page with the language filter set: one language, several, and a language the file does not have.
4. Menu page on the 50-track episode: does the track list render, scroll and stay usable?
5. Menu page: empty selection, no library, a library with one item, a search with no result.
6. Menu page: **History** with nothing, with one completed batch, with failures, with a cancelled batch —
   audit D6 says the empty state renders unconditionally.
7. Menu page: **Settings** — every field, saved one at a time, then read back from `GET /Plugins/{id}/Configuration`
   and from the other page (audit D4/F15: the two surfaces edit different subsets).
8. Menu page: cancel a running batch from the UI; then the armed "Kill all syncing" (audit F29, D1).
9. Menu page **while a batch is running** — audit D7 measured ~1.4 req/s idle, three duplicate calls on load.
   Count the calls yourself, per tab, per minute, idle and busy.
10. Movie detail page → injected "Sync Subtitles" → single track; then all tracks (audit D14 — never verified
    in a browser).
11. Episode detail page → the same two paths.
12. Series detail page → "Sync all episodes" → whole series; then one season; then a single episode
    (audit D13 — one press may only preview).
13. A **non-admin** user on the item detail page: the buttons, the dialog, the API calls. The four
    server-wide endpoints now return 403 for them (see §0); the dialog must still work.
14. The plugin's own pages in the **dashboard** navigation and, in the main app, how a user is supposed to
    reach them at all (audit D18: `EnableInMainMenu` produces no entry in Jellyfin 12).

**B. States and honesty (this is the heart of the ask)**

15. Empty / loading / error states: for each panel, capture what is shown before data arrives, when the
    request fails (stop the server or revoke the token mid-load), and when the answer is empty.
16. **Truthfulness:** for every number the UI displays (worker count, totals, ok/failed, progress, cue
    counts, cache sizes, versions), assert it equals what the API returned for the same moment. Audit D5
    ("4 in use" while nothing runs) and D12 (two versions at once) are prior art.
17. History/list correctness at the edges: 0 tasks, 1 task, a batch with failures, a batch that was
    cancelled, a job evicted from history while its batch is still listed.
18. Progress during a real bulk run: does anything move, is it real, and does the text match the plugin
    log's own numbers? Use the slow-storage profile (§4) so the run lasts long enough to watch.
19. Error shapes: a malformed request, an unknown item, a 403, a 500 — audit F28 says the pages render
    whatever body came back, so one class of error shows raw JSON in a small status line. Verify all of them.
20. Console/network hygiene: zero uncaught JS errors, zero 4xx/5xx caused by the plugin's own polling,
    no duplicate script evaluation (audit F25: the config page still tells admins to inject the script by
    hand, which would double it).

**C. Settings and configuration surface**

21. Each settings field round-trips (typed → saved → stored → read back by both pages → honoured by a run).
22. Hostile values, exactly as the audit typed them (D3): `MaxOffsetSeconds` negative and absurd, a
    `FfSubSyncPath` that does not exist, `OutputEncoding` nonsense, `SyncLanguages` with junk tags,
    `ParallelWorkers` out of range. What does the user see, and what is stored?
23. Audit F12: "Use golden-section search" does nothing unless framerate correction is on. Audit D19: the
    checkbox measured 1×1 px. Verify visually and behaviourally.
24. Audit D2: the install button and the status line when the configured binary path does not exist.
25. The sweep knobs (`SweepFailStreakLimit`, `SweepMaxItemsPerRun`) appear in neither page, and the sweep
    task ships with no trigger (F14). Confirm, and say what a user would have to do.

**D. Layout, input and robustness**

26. Real widths: 1920, 1440, 1024, 768, 420, and a resize mid-interaction. Horizontal scroll, clipped text,
    off-screen controls, tap-target sizes.
27. Keyboard only: tab order, Enter/Space on every control, focus visible, Esc behaviour, no focus trap.
28. Both the plugin page **inside the Jellyfin shell** (it loses its `<head>`) and standalone — the same
    page must render correctly in both (this is a documented trap in `AGENTS.md`).
29. Light and dark theme, if the client offers both.
30. Slow/blocked responses: throttle the API (Playwright route interception or the throttled library) and
    check that the page does not lie, double-submit, or lose the user's selection.
31. Reload mid-batch, close and reopen the tab, two tabs at once, and a second browser session.

**E. Failure and lifecycle paths**

32. Server restart mid-batch with the page open — what does the page show, and does it recover?
33. Token/session expiry or revocation while a page is open.
34. Audit F24: the config page's save has no `.catch` — make the save fail and see whether the spinner
    clears and the typed values survive.
35. Audit F23/F22: `/SubSync/ClientScript` is anonymous; the middleware forces `Accept-Encoding: identity`
    for every web-client bootstrap. Measure the cost of that last one if you can.
36. A user who may not see the library (restricted account) — what do the plugin's pages show them?

## 4. Measurement rules

- Count **requests per minute per tab** from the captured network log (idle and during a run) and report
  the numbers next to audit D7's ~1.4 req/s.
- Time the interactions that matter: time to first meaningful content, time from "press sync" to the first
  honest progress, time to a terminal state, and how long a state persists after it stopped being true.
- Any claim about pixels comes from a screenshot **plus** a DOM measurement (`getBoundingClientRect`) at a
  stated viewport.
- Where behaviour differs between the jsdom pass and the real browser, say so and treat the real browser as
  the truth — that difference is itself a finding about the harness.
- **Slow-storage profile:** to watch progress and to reproduce the user's reality, the plugin can be run
  against the throttled library. There is no FUSE and no root here; the profile is an `LD_PRELOAD` shim
  (`tests/backend/slowread.c`, built to `slowread.so`) that sleeps proportionally to bytes read, only under
  `SLOWREAD_PREFIX`. Start Jellyfin with `LD_PRELOAD=…/slowread.so SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_16K=12.8`
  and the plugin's own log will confirm it (`extract: storage 12.94 ms per 16 KB read`). State which profile
  every timing claim was taken on.

## 5. Deliverable, phase 1 (document, no code changes)

`knowledge/gui-test-report-<date>.md`, in the same shape as the audit and the backend report (the user likes
that shape):

1. **Coverage table** — every matrix row: surface, route, result, evidence (screenshot filename, request
   count, DOM excerpt).
2. **Findings** — symptom → exact repro (route, viewport, clicks/keys) → evidence → impact → severity →
   fix direction. Separate **broken** from **confusing** from **cosmetic**. Every GUI finding in the audit
   appears here as confirmed or refuted, with the browser evidence.
3. **Truthfulness table** — every number the UI shows, against the API value at the same moment.
4. **Request/traffic profile** — per tab, idle and busy.
5. **Layout findings** — per viewport, per theme.
6. **Junk** — dead ids, unreachable handlers, duplicated helpers between the three files, stale language
   lists (the audit's junk list already names several with line numbers), log/console noise.
7. **What was not tested** and why, with the smallest step that would test it.
8. **Suggested patching order**, highest user impact first (anything that lies comes before anything that
   merely looks wrong).

Commit it locally; do not push to a release branch.

## 6. Deliverable, phase 2 (fix what phase 1 proved)

- One finding per change, before/after evidence from the **same browser steps**, one-line reason in the
  commit. Show the "after" screenshot next to the "before".
- Priority: (a) anything that states something untrue, (b) anything that loses the user's work or leaves a
  control stuck (spinners, armed destructive buttons, unrecoverable states), (c) anything that makes a
  feature unusable (D13/D14 class), (d) layout/polish, (e) traffic and noise.
- Never trade truth for smoothness: an honest "no progress reported" beats a bar that moves on its own.
- `python3 tests/run_checks.py` stays green, and each fix gets a check where the defect can be pinned in
  source (a required attribute, a guard, an escaping rule, a missing `.catch`).
- Anything released goes through the GitHub Actions workflow on `beta` and must be verified in the public
  manifest (zip HTTP 200, checksum, DLL contents) before you call it done.

## 7. Definition of done

- Every matrix row attempted in a real browser, with screenshots and captured calls; blocked rows explained
  with the precise blocker.
- The report committed and complete. **Every audit GUI finding (D4–D7, D13, D14, D18, D19, F4, F15,
  F22–F29) marked confirmed or refuted** — the two that have never been verified in a browser (D13, D14)
  are the ones that matter most.
- Every number on every page equals the API value behind it; no stuck spinners; no armed destructive
  control; no uncaught console error in any exercised path.
- The item page and the series page work for an ordinary, non-admin user, with the administrator-only
  endpoints returning 403 without breaking the dialog.
- `tests/run_checks.py` green with new checks for the fixed defects.
- A one-page summary: what was broken, what was confusing, what changed, what is still open, and how long
  each browser pass took.

## 8. Practical notes

- Log in through the real login form (admin, empty password), not by injecting a token, so the session the
  pages see is a real one. If you must inject, `POST /Users/AuthenticateByName` gives a token for
  `Authorization: MediaBrowser Token="…"`.
- Before any GUI claim, record: server version, plugin DLL path + version (`GET /SubSync/InstallationStatus`),
  browser + viewport, and whether the slow profile was active.
- Keep the screenshots. A GUI finding without a picture is a guess.
- If a page has to be opened from the dashboard rather than the main app menu, say so in the report — that
  is how a user actually finds it (audit D18).
- Watch out for the known traps while working: the menu page loses its `<head>` when embedded, so all its
  `<style>` lives in the body; `emby-select`/`emby-input` inject sibling `<label>` elements, so never lay
  those fields out in a flex row; every page call must use `Authorization: MediaBrowser Token="…"`
  (`X-Emby-Token` and `?api_key=` are dead on Jellyfin 12); helpers used by a page must be defined in that
  page, because the injected script is an IIFE and nothing it defines is visible to the others.
