#!/usr/bin/env python3
"""Checks that knowledge/FIX_PLAN.md can be read as an ordered list.

An issue register is only worth keeping if "what is left" can be answered from it in priority order.
That needs, for every open row and every open section: a severity, and something in the row that a
reader can check - a file and line, a commit, or a logged observation. This script fails when any of
that is missing, and prints what it counted, so the register cannot quietly rot back into rows of
unknown seriousness.

Run it from the repository root:

    python3 tests/check_fixplan.py

Exit code 0 when the register is intact, 1 when it is not.
"""

from __future__ import annotations

import pathlib
import re
import sys

PLAN = pathlib.Path(__file__).resolve().parent.parent / "knowledge" / "FIX_PLAN.md"

SEVERITIES = {"high", "medium", "low"}
STATUSES_OPEN = {"open", "todo", "pending", "backlog", "new"}

# What counts as something a reader can check for themselves.
EVIDENCE_PATTERNS = (
    re.compile(r"\b[\w./-]+\.(?:cs|js|py|md|sh|json|xml|html|yml|yaml):\d+"),   # file:line
    re.compile(r"`[0-9a-f]{7,}`"),                                             # commit hash
    re.compile(r"\bcommit\b", re.I),
    re.compile(r"^\s{4,}\S", re.M),                                            # an indented quote
)
# Markers that say how far a row has been checked.
VERIFIED = re.compile(r"\*\*?(verified|guarded elsewhere|real|not real|refuted)\b", re.I)
RANKED_ONLY = re.compile(r"ranked by blast radius", re.I)


def cells(line: str) -> list[str]:
    return [c.strip() for c in line.strip().strip("|").split("|")]


def main() -> int:
    text = PLAN.read_text()
    lines = text.splitlines()

    open_rows: list[tuple[int, list[str], list[str]]] = []
    header: list[str] | None = None
    for i, line in enumerate(lines):
        if not line.startswith("|"):
            continue
        row = cells(line)
        if all(set(c) <= set("-: ") for c in row if c):
            header = [c.lower() for c in cells(lines[i - 1])]
            continue
        if header is None:
            header = [c.lower() for c in row]
            continue
        record = dict(zip(header, row))
        status = (record.get("status") or row[0]).lower()
        if status in STATUSES_OPEN:
            open_rows.append((i + 1, row, header))

    failures: list[str] = []
    verified = ranked_only = carried = 0
    for lineno, row, hdr in open_rows:
        record = dict(zip(hdr, row))
        rid = record.get("id") or (row[1] if len(row) > 1 else "")
        rid = re.sub(r"\*+", "", rid) or f"line {lineno}"
        sev = (record.get("severity") or record.get("sev") or (row[2] if len(row) > 2 else "")).lower()
        # the evidence is the last cell that is not the severity column
        body = " ".join(row) if len(row) < 5 else row[-1]
        if sev not in SEVERITIES:
            failures.append(f"{rid} (line {lineno}): open with no severity ({sev or 'empty'!r})")
        if not body.strip():
            failures.append(f"{rid} (line {lineno}): open with an empty evidence cell")
        elif RANKED_ONLY.search(body):
            # Explicitly marked as ranked, not yet read against the code: honest, and allowed.
            ranked_only += 1
        elif VERIFIED.search(body):
            # Claims a verdict, so it has to carry something a reader can check themselves.
            verified += 1
            if not any(p.search(body) for p in EVIDENCE_PATTERNS):
                failures.append(f"{rid} (line {lineno}): claims a verdict with nothing checkable in it")
        elif any(p.search(body) for p in EVIDENCE_PATTERNS):
            carried += 1
        else:
            failures.append(f"{rid} (line {lineno}): open with nothing checkable in its evidence cell")

    # sections: ### S31 - ... (low) / (medium, ...) / (done ...)
    sections = re.findall(r"^### ((?:S|D|B|E|F|R)\d+ - .*?)(?:\s*\((.+?)\))?\s*$", text, re.M)
    open_sections = [(h, tag or "") for h, tag in sections if "done" not in (tag or "").lower()]
    for heading, tag in open_sections:
        rid = heading.split(" - ")[0]
        if not any(s in tag.lower() for s in SEVERITIES):
            failures.append(f"{rid}: open section without a severity in its heading ({tag or 'none'})")
        i = text.index(f"### {heading}")
        j = text.find("\n### ", i + 1)
        body = text[i:j if j > 0 else len(text)]
        if not any(p.search(body) for p in EVIDENCE_PATTERNS):
            failures.append(f"{rid}: open section with nothing checkable in it")

    total = len(open_rows)
    print(f"FIX_PLAN: {total} open row(s), {len(open_sections)} open section(s)")
    print(f"  rows carrying a verdict from reading the code: {verified}")
    print(f"  rows ranked by blast radius only (claim not yet read): {ranked_only}")
    print(f"  rows carrying evidence from an earlier audit: {carried}")
    print(f"  open sections: {', '.join(h.split(' - ')[0] for h, _ in open_sections) or '(none)'}")
    if failures:
        print(f"\nFAIL - {len(failures)} open item(s) cannot be read in priority order:")
        for f in failures:
            print(f"  {f}")
        return 1
    print("\nPASS - every open row and section carries a severity and something checkable.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
