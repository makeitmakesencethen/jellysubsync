# GUI test report — 2026-09-11 (session 2): not attempted in a browser

**This session did not exercise the GUI in a real browser at all.** The GUI half of the brief
(`GUI_GOAL_PROMPT.md`, `GOAL_PROMPT.md` §5 and §7) is therefore still unstarted, and this file exists
so the next session knows exactly where it stands and what the smallest first step is — rather than
assuming a report that does not exist means a GUI that was tested.

What did change on the GUI side in this session, with the evidence there is:

| Change | Verified by |
|---|---|
| `subsyncMain.html` gained the **"Max shift from a subtitle reference"** field (`ss-maxrefoffset`), loaded from `MaxSubtitleReferenceOffsetSeconds` and written back on save, and the Max offset description now says a clamped result is refused | source-level checks in `tests/run_checks.py` (`the documented reference limit exists in the model` runs today and passes), **plus** the settings round-trip is not proven in a browser — the field was never rendered |

Everything else in Matrix B is untouched:

* **D14** (does the injected client script hook the Jellyfin 12 item page?) — still unanswered. One
  press of the item-page action has never been observed.
* **D13** ("Sync selected" on a series scope issued a preview call rather than creating a batch) —
  still unanswered.
* D4/F15 (two settings surfaces) — the user answered the product question today (**merge**: the
  dashboard page redirects to the main page's settings), but neither page was opened.
* D5, D6, D7, D18, D19, F12, F22–F29, and the junk list's line-numbered items — unchanged from
  session 1's record; none of them confirmed or refuted in a browser this session.

The one GUI-adjacent defect this session produced came from the API side, not the browser: while
chasing S3 I queued a track index that had shifted (S14), the sync accepted an index resolving to one
of the plugin's own `.SYNCED.` sidecars, and the run wrote `…SYNCED.ukr.SYNCED.srt` next to the
episode (S12). The junk file was deleted. It matters to the GUI because the item page lists tracks by
the same index, so a stale page can queue a track that has moved.

## The smallest step that starts this properly

```sh
node /opt/data/jellysubsync/tests/backend/browser-item.js      # needs /tmp/audit/pw/node_modules/playwright
```

`browser-item.js` already exists from session 1 and drives a real Chromium against the live server.
The first two rows to run, in this order, because they are the two that have never been verified at
all and they decide whether the item-page half of the plugin works:

1. **D14** — log in through the real login form, open the film's detail page, and answer in writing:
   is the injected "Sync Subtitles" button there, what does one press do (which `/SubSync/*` calls,
   in what order), and does the dialog survive a 403 from the four admin-only endpoints?
2. **D13** — from the series detail page, "Sync all episodes": does one press create a batch
   (`POST /SubSync/Batch` + a batch id) or does it issue `/SubSync/Subtitles/Batch` (a preview)?

Evidence per row, as the brief requires: screenshot file name, the captured `/SubSync/*` calls, the
console log, the DOM text actually read, and browser + viewport + DLL + slow-profile state recorded
before the claim. jsdom may not be the evidence for any visual or layout claim, and the settings
round-trip (typed → saved → read back from `GET /Plugins/{id}/Configuration` → honoured by a run) has
to be done on the field this session added as well.
