#!/usr/bin/env python3
"""Generate knowledge/FIX_PLAN.md — one line per finding from the audit and the backend report."""
import re
import pathlib

audit = pathlib.Path('/opt/data/jellysubsync/knowledge/audit-2026-09-11.md').read_text()
rep = pathlib.Path('/opt/data/jellysubsync/knowledge/backend-test-report-2026-09-11.md').read_text()

D = re.findall(r'^\| (D\d+) \| ([^|]+) \| ([^|]+) \| ([^|]+) \|', audit, re.M)
S1 = re.findall(r'^### (\d+)\. (.+)$', audit, re.M)
S2 = re.findall(r'^### (F\d+)\. (.+)$', audit, re.M)
SX = re.findall(r'^\*\*(S\d+[a-z]?) — (.+?)\.\*\*', rep, re.M)


def sev_after(heading_regex):
    m = re.search(heading_regex, audit)
    if not m:
        return '?'
    nxt = audit.find('\n### ', m.end())
    body = audit[m.end():nxt if nxt > 0 else len(audit)]
    s = re.search(r'[-*] Severity:?\s*\**\s*([a-z]+)', body, re.I)
    return s.group(1).lower() if s else '?'


rows = []
for id_, sev, area, line in D:
    rows.append(dict(id=id_, sev=sev.strip().lower(), area=area.strip(),
                     what=line.strip(), src='audit live'))
for num, title in S1:
    rows.append(dict(id='B' + num, sev=sev_after(r'^### %s\. %s' % (num, re.escape(title))),
                     area='backend', what=title.strip(), src='audit static, scheduler/extractor'))
for id_, title in S2:
    rows.append(dict(id=id_, sev=sev_after(r'^### %s\. %s' % (id_, re.escape(title))),
                     area='backend/gui', what=title.strip(), src='audit static, API/GUI/settings'))

newmap = {
    'S3': 'a reference track aligned far off is written instead of refused',
    'S4': 'a failed job leaves a 0-byte subtitle in the library',
    'S5': 'a bitmap track is missing from the track list instead of refused with a reason',
    'S7': 'queueing under load costs ~212 ms and each job re-probes the storage',
    'S8': 'an in-sync subtitle synced against the audio is moved and written as a success',
    'S11': 'the reference derivation hands ffsubsync the video, so it demuxes the whole file and hangs',
    'S11b': 'cancelling a batch leaves the engine ffmpeg child alive',
}
for id_, _ in SX:
    if id_ in newmap:
        rows.append(dict(id=id_, sev='high' if id_ in ('S11', 'S11b') else 'medium',
                         area='backend', what=newmap[id_], src='backend test 2026-09-11'))
rows.append(dict(id='S6', sev='high', area='backend', src='backend test 2026-09-11',
                 what='bulk ran one whole-file pass per worker'))

state = {
    'D15': ('done', '`a776267` — 403 for a non-admin on Install/Kill/SpeechCache-Clear/Log, 200 for the admin'),
    'D16': ('done', '`fd923ab` — every backup kept; verified on the replace fixture'),
    'D17': ('done', '`fd923ab` — cue parity; 27 cues where it used to give 24'),
    'S6':  ('done', '`a46c5cd` — 48/50 tracks in 18 min where 1/50 took 22 min'),
    'D1':  ('decision', 'yours: keep it (now admin-only), scope it to the caller, or remove it'),
    'S8':  ('decision', 'yours: may a result based only on the audio be written at all?'),
    'D4':  ('decision', 'yours: merge the two settings pages, or make both show the same fields'),
}
tier1 = {'S11', 'S11b', 'S6'}
tier2 = {'S3', 'S4', 'D16', 'D17', 'D15', 'D5', 'D6', 'D11', 'D12', 'D2', 'F27', 'F28', 'F24', 'F29'}
for r in rows:
    r['state'], r['evidence'] = state.get(r['id'], ('open', ''))
    r['tier'] = 1 if r['id'] in tier1 else 2 if r['id'] in tier2 else (
        3 if r['sev'] in ('critical', 'high') else 4)
rows.sort(key=lambda r: (r['tier'], r['id']))

out = [
    "# Fix plan — every finding from both records, one line each",
    "",
    "Companion to `GOAL_PROMPT.md`. **This file is the loop.** Work top-down, one finding per commit:",
    "reproduce it, fix it, verify the fix the same way you reproduced it, then tick the box here with the",
    "commit hash and the evidence beside it. When your budget runs out, leave the rest unticked with a",
    "one-line note on each. **Never tick something you did not verify.**",
    "",
    "`state` is one of: `open`, `done` (commit + evidence), `decision` (the user must choose — ask them),",
    "`blocked` (cannot be done here — name why).",
    "",
    "`D…` are the audit's 19 live findings. `B1–B30` are its static audit of the scheduler and extractor.",
    "`F1–F30` are its static audit of the API, GUI and settings. `S…` come from the 2026-09-11 backend test.",
    "",
]
for t, desc in ((1, "the run has to finish — a bulk run must neither hang nor fail"),
                (2, "nothing may write a wrong file, lie on screen, or leave junk behind"),
                (3, "critical and high severity — the user feels these"),
                (4, "the rest of both matrices, layout, hygiene, and the audit's unproven static leads "
                    "(verify first: refuting one is a real result)")):
    sel = [r for r in rows if r['tier'] == t]
    out.append("## Tier %d — %s (%d items)\n" % (t, desc, len(sel)))
    out.append("| state | id | sev | what | evidence / commit |")
    out.append("|---|---|---|---|---|")
    for r in sel:
        out.append("| %s | **%s** | %s | %s | %s |" % (
            r['state'], r['id'], r['sev'], r['what'][:120].replace('|', '/'), r['evidence']))
    out.append("")
out.append("## Ask the user before coding these\n")
for r in rows:
    if r['state'] == 'decision':
        out.append("- **%s** — %s" % (r['id'], r['what'][:140]))
out += ["",
        "## Cannot be tested on this machine",
        "",
        "- a **full disk** and a **separate filesystem for staging** both need a small filesystem, which",
        "  needs root. Record them as `blocked` with the reason instead of working around them.",
        ""]
pathlib.Path('/opt/data/jellysubsync/knowledge/FIX_PLAN.md').write_text("\n".join(out) + "\n")
print("rows:", len(rows))
print("tiers:", {t: sum(1 for r in rows if r['tier'] == t) for t in (1, 2, 3, 4)})
