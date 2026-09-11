# GUI test report — 2026-09-12 (Matrix B, second pass)

Chromium (playwright, `/opt/data/.playwright`) against the live test server on `http://127.0.0.1:8096`.
Every browser session is obtained by creating a throwaway admin account through the REST API, logging in
through the real web form, and deleting the account afterwards; no password is typed anywhere (the test
server's admin has one, so the brief's "admin (empty password)" is wrong).

## D13 and D14 — answered

- **D14 (client script hooks the Jellyfin 12 item page): works.** The ⋮ menu on an episode carries
  "Sync Subtitles"; pressing it issues `GET /SubSync/Subtitles/{id}` and opens the dialog ("Pick the
  subtitle to synchronize. The original is never modified.") with a Sync button per track; pressing one
  issues `POST /SubSync/Sync` and then polls `GET /SubSync/Jobs/{id}`.
- **D13 (series/season bulk sync): the audit's claim is refuted.** The series page's ⋮ menu carries
  "Sync all episodes"; opening it issues `POST /SubSync/Subtitles/Batch` (the expansion the audit saw)
  *and* shows the bulk dialog (scope and counts: "The Helicopter Heist — 2 episodes, 1 season",
  "All languages — 100 tracks"); pressing Sync issues `POST /SubSync/Batch` and polls the batch. The
  preview call precedes the run, it does not replace it.
- **D18:** no plugin entry appears in the main menu; the plugin page is reachable as a page URL only
  (`/web/configurationpage?name=subsync-main`), not as a hash route (`#/configurationpage?name=…` is
  bounced to `#/home` — nine route shapes tried).
- **D19:** refuted at 1600×1000 — the golden-section checkbox measures 13×13 px with a 620 px label and
  toggles.
- Screenshots and the captured `/SubSync/*` traffic per step: `tests/gui/shots/`, `tests/gui/gui-pass-*.json`.

## The finding that took the rest of the session: the plugin's own page ran nothing

Measured in a browser, not read from the source (`tests/gui/page-script-probe.js` + `.json`):

1. **An inline `<script>` in the plugin page never executes.** Jellyfin 12 injects the page as markup;
   the page's ~2100 lines were inline, and the result was a rendered shell: every control in place, the
   status line stuck on "Checking status...", an empty library list, and **no request of any kind**.
   The same script served from `/SubSync/MainScript` is **fetched** — and the same script attached by
   script (blob/appendChild) **runs and fills the page in** — but the tag that arrives with the injected
   markup does not run. Attaching it from `subsync.js` (the client script the middleware injects into the
   web client, the path that already works for the ⋮ menu) is implemented and did **not** yet produce a
   running page; the next session should look at the browser's Network → Initiator column for the
   `MainScript` request and at whether the client replaces the page element it injects.
2. **With the script running it died on `ApiClient`** — `Uncaught ReferenceError: ApiClient is not
   defined` at the first setting it read; `typeof window.ApiClient` stayed `undefined` for 22 s in this
   instance. The page now takes its token from the web client's stored credentials when that global is
   absent, builds its own URLs (`apiUrl`), reads and writes settings through its own
   `GET/POST /SubSync/Configuration` (verified: 200 with the configuration body), asks `GET /Users/Me`
   for the signed-in user, and reports its own failure on the status line instead of showing a dead page.

3. **With the script attached by hand, the page works** (`tests/gui/page-interactive1..3.json`):
   status line filled ("ffsubsync source: …/venv/bin/ffsubsync", ffmpeg line), libraries listed
   (Movies …), settings and history read through the plugin's own endpoints, and the heartbeat polls
   `/SubSync/Batches` + `/SubSync/Active` continuously. One call in that session answered **403** and the
   batch queued from the page's session did not appear to it, so the run it should have mirrored never
   existed in its view — which is why the two things below are still not verified in a browser.

## Still not verified in a browser (and why)

- **F29's two-press kill.** Implemented (first press arms, "Confirm: kill all syncing", second press
  within 8 s kills, any other label drops it) and pinned by a source check. The browser check needs a
  running run that the page can see: with no watched run the button stays "Cancel" (batch cancel), which
  is what the probe measured ("Nothing queued."). The probe script
  (`tests/gui/page-interactive-probe.js`) already drives the presses and is ready to re-run.
- **The mirrored run box** (the fix from earlier today: `renderMirroredBatch` wrote rows into a box that
  was never shown). Same blocker as above: it needs a run in the page's own view.
