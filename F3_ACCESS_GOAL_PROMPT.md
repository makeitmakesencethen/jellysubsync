# GOAL: make the item-scoped endpoints act only on items the caller can see (F3)

## Why this exists

The register's highest genuinely-real item. `Api/SubSyncController.cs` has exactly one gate - the class-level
`[Authorize]` at line 16 - and **no per-item check anywhere**: a grep of the file for `User`, `HasPermission`,
`GetItemById`, `IUserManager`, `Claims` or `Identity` returns nothing. Four endpoints take an item id on trust:

| endpoint | line | what it does with the id |
|---|---|---|
| `GET Subtitles/{itemId}` | `:48` | `_syncService.ListSubtitles(itemId)` (`:51`) - reads any item's subtitle list |
| `POST Sync` | `:67` | `_syncService.StartSync(request.ItemId, …)` (`:74`) - **queues a job that writes a subtitle** |
| `POST Batch` | `:125` | `request.Tasks` expanded into jobs, no ownership or visibility check |
| `POST Subtitles/Batch` | `:393` | the same, for a list of items |

So any authenticated account on the server can name any item id and cause a write into the library. What makes
it reachable in practice rather than theoretical is F4: the job history (`Jobs`, `Jobs/{jobId}`, `Batches`,
`Batch/{batchId}`) is server-wide for the same account and the job records carry `ItemId` and `OutputPath` - so
the ids are discoverable through the plugin itself. **F4 is the follow-up, not this goal**: fixing the reads
without fixing the writes leaves the write half open.

This was verified during the 2026-09-14 triage by reading the shipped code; the row's own wording was not taken
as evidence, and the quotes above are the evidence.

## GOAL

Every item-scoped endpoint acts only on items the calling account can see, with the decision derived from
Jellyfin's own view of that account rather than from anything new to configure, and with no change for a caller
who legitimately can see what they asked for.

## Work

1. **Extract the decision as a pure predicate, testable without a server.** Given what Jellyfin knows about the
   caller (their identity and their enabled library folders) and what it knows about the item (the folders it
   lives in), return allow or deny. No Jellyfin interface types in the predicate itself - the wiring passes plain
   values in - because the suite drives this directly and a check that needs a live server is a check that will
   not run. Follow the plugin's house pattern: an `internal static` helper with the decision in it, called by the
   endpoint.
2. **Define "visible" and write the definition down**: an account with all-folders permission sees everything;
   otherwise the item must live in a folder that account has enabled. An item whose folders cannot be resolved is
   **denied** - fail closed, the same shape as the walk ceiling's "not known to be fast is not fast".
3. **Wire it into all four endpoints**, and for the two bulk ones check **every** item: a request that mixes an
   allowed item with a disallowed one is refused as a whole with nothing queued. Partial enqueue is worse than a
   refusal, because the user sees a batch that started and cannot tell what was left out.
4. **Refuse without confirming existence.** A caller who cannot see an item should not learn from the reply
   whether it exists - the message names the account and the reason, not the item. Log the refusal with the id
   for the server operator; the plugin log is admin-only (`Api/SubSyncController.cs:465-466`), so that is where
   the id belongs.
5. **Leave the item menu working.** A user who can see an item, and the admin dashboard's own flows, must behave
   exactly as before: the fix narrows who may act, not what acting does. If the item-menu action turns out to be
   reachable by a user who cannot see the item, that is a finding to report rather than a reason to loosen the
   check.

## Non-negotiables

- **No new setting.** Visibility is derived, never configured.
- **Fail closed.** "Cannot tell" means deny, in every branch.
- **Do not reach for elevation.** Making `Sync` admin-only would "fix" the IDOR by removing the feature from
  users who are entitled to it: a legitimate account syncing an item it can see is the behaviour being
  protected, not an exception to it.
- **No change to sync behaviour**: the same engine, the same reference handling, the same writes. This goal adds
  a gate in front of the endpoints, nothing else.
- One item, one commit; suite green after every step; the FIX_PLAN row for F3 gets the verdict, the commit and
  the reasoning; hold before pushing or bumping a version, as always.

## Verification - what "done" means

- **The predicate, driven by the suite**: all-folders account on any item → allow; an account whose enabled
  folders contain the item's folder → allow; an account missing that folder → deny; an account with no folders →
  deny; an item whose folders cannot be resolved → deny.
- **Bulk refusal**: a request with one visible and one invisible item is refused and **queues nothing** - checked,
  not assumed.
- **Each endpoint actually calls it**: a source-level check over `Api/SubSyncController.cs` (the way F8 is pinned)
  that all four named endpoints consult the predicate, so a future endpoint cannot quietly skip it and a future
  refactor cannot drop it from one of them.
- **The legitimate paths still work**: an admin flow and a visible-item flow behave as they did - in the suite
  where possible, and on fabji's server for the page itself, where the admin dashboard and the item-menu action
  are verified unchanged.
- Suite green, with the new checks counted in.

## Failure modes this goal must not repeat

- **Fixing `Sync` and forgetting the batches.** The bulk endpoints are the easier way to hit the same hole.
- **A check that falls back to "allow"** when the item or the account cannot be resolved - that is the bug
  wearing the fix's clothes.
- **Confirming existence in the refusal**, turning an access check into an inventory oracle.
- **Calling it done from unit checks alone**: the page's own flows are the thing most likely to break, so they
  get looked at on a real server before the row is closed.
