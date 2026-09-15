# C# candidate — bulk batch grouping across volumes (built and tested)

Candidate: `BatchVolumeGroup.cs` (new file, isolated, no existing behavior replaced).

What it does: reads the persisted `BatchHistory` (same `batch-history.json` file the plugin uses) and groups a batch's jobs by the storage volume of each file (`MediaVolume.Of` on the output path's directory). Each group reports: volume key, task count, distinct status values, ordered by batch position. The result is a pure computation — nothing writes to it; the group description is available to any caller (e.g., the history endpoint or a future page line).

Why it competes: the user's original ask: "anything in these repos about making bulk/batch syncs more manageable when files span multiple volumes/drives at once — queueing, grouping, or scheduling approaches." None of the 5 repos carries a per-volume batch grouping above their scheduling layer (bazarr/AutoSubSync use download pools, autosubsync is a single-file model, subsync has no batch, Marnalas uses the same flat batch as ours). So the adaptation is fresh C# on our architecture (`MediaVolume`, `BatchHistory`), not a literal port.

Evidence vs current:
- Build: `dotnet build -c Release` → 0 warnings, 0 errors. File at `Services/BatchVolumeGroup.cs`.
- Test against fixtures: `tests/run_checks.py` → 680 PASS, 2 FAIL (same pre-existing settings/offset checks as before this change; no regression from the new file). The file is pure (no I/O at call time except the read from history, which is already covered by `BatchHistory.Load`).
- Numbers: `GroupByVolume` on the current `BatchHistoryEntry` (20 max batches) costs the grouping of at most the newest batch (bounded by the file); the grouping is O(tasks) with a single dictionary — no new storage probe, no new lock, no change to the pump (`PumpAsync`). The `DescribeGroups` output mirrors the plugin's existing `batch history: ...` log line (`BatchHistory.DescribeRestore`), so any future check can pin it.

Verdict: built and verified, no regression, no measured behavior change on fixtures (the grouping is a read-side view, not a scheduling change). It does not claim to be faster or more correct — it is the structural piece the user asked for (per-volume grouping), available to compete when the user decides whether the scheduling layer should use it (e.g., cap per volume, or show groups in the UI). Not promoted to replace anything; kept as isolated file.

Row added to FIX_PLAN (this session, C2 already there; this is the separate group lead the user selected as #2).

> **Not shipped.** The class was removed from the tree when 2.0.43 was cut: nothing called it, and this
> document plus the FIX_PLAN row keep the idea and its cost estimate. Re-adding it is a decision about the
> scheduler invariant described in the row, not a code change.
