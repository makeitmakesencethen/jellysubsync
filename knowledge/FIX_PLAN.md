# Fix plan — every finding from both records, one line each

Companion to `GOAL_PROMPT.md`. **This file is the loop.** Work top-down, one finding per commit:
reproduce it, fix it, verify the fix the same way you reproduced it, then tick the box here with the
commit hash and the evidence beside it. When your budget runs out, leave the rest unticked with a
one-line note on each. **Never tick something you did not verify.**

`state` is one of: `open`, `done` (commit + evidence), `refuted` (you reproduced it and it is not a real
defect — say what you saw), `decision` (the user must choose — ask them), `blocked` (cannot be done here —
name why). **Refuting a lead is a real result; fixing something that was never broken is not.**

`D…` are the audit's 19 live findings. `B1–B30` are its static audit of the scheduler and extractor.
`F1–F30` are its static audit of the API, GUI and settings. `S…` come from the 2026-09-11 backend test.

## Tier 1 — the run has to finish — a bulk run must neither hang nor fail (3 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| open | **S11** | high | the reference derivation hands ffsubsync the video, so it demuxes the whole file and hangs |  |
| open | **S11b** | high | cancelling a batch leaves the engine ffmpeg child alive |  |
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
| open | **S3** | medium | a reference track aligned far off is written instead of refused |  |
| open | **S4** | medium | a failed job leaves a 0-byte subtitle in the library |  |

## Tier 3 — what the user actually touches, the rest of both test matrices, layout, hygiene (2 items)

| state | id | sev | what | evidence / commit |
|---|---|---|---|---|
| decision | **D1** | high | `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it | yours: keep it (now admin-only), scope it to the caller, or remove it |
| open | **D3** | high | settings validation is partial: offset, paths, encoding and language tags accept nonsense and are saved silently |  |

## Ask the user before coding these

- **D1** — `POST /SubSync/Kill` is kill-everything; there is no per-job kill, and any authenticated user can call it
- **D4** — the dashboard config page and the main page edit the same settings with different subsets — two sources of truth
- **S8** — an in-sync subtitle synced against the audio is moved and written as a success

## Cannot be tested on this machine

- a **full disk** and a **separate filesystem for staging** both need a small filesystem, which
  needs root. Record them as `blocked` with the reason instead of working around them.

