# GOAL: make FIX_PLAN answer "what's left, in what order" (register triage)

## Why this exists

`knowledge/FIX_PLAN.md` holds **78 open table rows and 4 open sections**, and it cannot currently be read in
priority order:

- **three rows carry a severity of high** (D2 "Install ffsubsync" reports success while the configured binary
  path does not exist; D3 settings validation is partial; S13, which is stale), **14 medium, 6 low**;
- **about 55 open rows carry no severity at all** - the whole F series (API, settings, security) and most of
  the B series from the GUI/API audit - and their evidence cells are **empty**. Four of them read like the top
  of the list:

  | row | what it claims |
  |---|---|
  | F1 | `POST /SubSync/Install` lets any authenticated user run apt-get/pip as root |
  | F3 | item-scoped endpoints have no per-item access check (IDOR, read and write) |
  | F4 | server-wide job history and the plugin log expose other users' runs and server paths, with no admin gate |
  | B1 | Replace mode can destroy the original subtitle with no rollback |

- **two rows are already settled** and were never closed: F8 (the page-side 1000-task chunking is implemented
  in `7f474d5` and pinned by a check on `var BATCH_CHUNK = 1000` in `Jellyfin.Plugin.SubSync/Web/subsyncMain.js`) and S13 (the
  missing `tests/backend/slowread.so` it describes exists and was used for every measurement on 2026-09-14).

A register whose ordering cannot be trusted is a register nobody works from: the next goal has to be able to
say "the most serious open item is X" and be right. That is the defect this goal fixes - not any of the rows.

## GOAL

Make the register readable as an ordered list: every open row or section carries a severity and the evidence
for its claim, the settled ones are closed with their evidence, and the four unranked rows above are verified
against the shipped code and ranked truthfully - including being refuted if that is what the code says.

## Work, in this order

1. **Close the two that are already settled** - F8 and S13 - with the evidence in the row's own cell: the
   commit and the check for F8, the shim's presence and the measurements that used it for S13. Verify both
   before writing (the row's claim is not evidence; neither is this prompt's).
2. **Verify F1, F3, F4 and B1, one at a time, against the shipped code.** For each, read the endpoint or
   handler and quote the line that decides the behaviour, then record exactly one verdict:
   - **real** - with the request, the role, or the sequence that reaches it;
   - **guarded elsewhere** - naming the guard and where it lives;
   - **not real** - saying what refuted it.
   A refutation is a result, not a failure. For F1 in particular, find whether the route reaches a privileged
   call in the build that ships, and what it requires before it will. For F4, name the endpoint, what it
   returns, and who can call it. For B1, follow the write path (replace versus sidecar): what is written, in
   what order, and what the code does if the write fails part way.
3. **Severitise every remaining open row and section** that has no severity: high / medium / low, each with one
   clause of reasoning, ranked by blast radius for a user - what breaks if it happens, and how likely it is.
   A row that cannot be ranked without reading code should say so, and say what would settle it.
4. **Add a checker so the register cannot rot again**: a small script that fails when an open row or section
   lacks a severity or an evidence cell, and prints the counts it found. It runs against
   `knowledge/FIX_PLAN.md` only, and the goal is done when it passes.

Keep the register's own upkeep rule (already at the top of the file): a finding gets a row the moment it is
found, `done` carries the commit, a reversed rule records why, one item, one commit.

## Non-negotiables

- **No plugin code changes in this goal.** It may change `knowledge/FIX_PLAN.md` and add the checker script.
  Fixing anything found here is the *next* goal's work, chosen from a ranked list.
- **Escalate instead of filing**: if verifying F1, F3, F4 or B1 shows something genuinely exploitable or
  destructive in the shipped build, stop the triage and report it immediately - that is not a row, that is
  news.
- **Every claim is verified by reading the code**, with the file and the line quoted. Nothing is carried over
  from the row's original wording, and no severity is changed without the reason being written into the row.
- **Refuted rows are closed, never deleted.** The register remembers why something was thought to be a defect.
- Run `python3 tests/run_checks.py` at the end to show the register work touched no behaviour, and hold before
  pushing or bumping anything, as always.

## Verification - what "done" means

- The checker passes: **zero open rows or sections without a severity, zero without an evidence cell.**
- Its before/after counts are in the FIX_PLAN entry for this work (rows ranked, rows closed, rows refuted).
- F1, F3, F4 and B1 each carry a verdict with a quoted line, or a named guard, or a statement of refutation.
- F8 and S13 are `done` with their evidence.
- The ordered list is demonstrable: print the top five open items by severity and they are a short, real list
  that a person can start on - not "55 rows of unknown seriousness".
- `tests/run_checks.py` green (582 or more checks), proving the goal changed no behaviour.

## Failure modes this goal must not repeat

- **Ranking from the row's wording.** The rows were written by an earlier audit whose evidence cells are
  empty; treat every one of them as a claim to check, not a fact to sort.
- **Deleting an inconvenient row**, or quietly dropping a severity to make the list look better.
- **Turning the triage into a fix.** The value here is an ordered list; fixing F1 or B1 without knowing whether
  it is real is how the register got its unverified rows in the first place.
