#!/usr/bin/env python3
"""Reproduce priority 4 exactly: a batch is created, and History is read while its tasks are still pending.

The user's report is that newly queued jobs appear in History marked **Failed**. The page's chip comes from
`historyState()` over the summary `GET /SubSync/Batches` returns, so the question is what that summary says
during the window when the batch's tasks have not all finished.

Two things make the window observable on this rig, and both are required:

  * the **slow-storage shim** (`SLOW=1 tests/backend/start-server.sh`), so a read of a fixture takes tens of
    milliseconds instead of none and a batch takes seconds rather than being over before the first poll;
  * a batch big enough that its *early* samples have failures but not-yet-finished tasks - which is the
    shape the page meets on the user's library, where a run of hundreds of tasks fails a few along the way.

The probe records, at 100 ms resolution, the exact triple the chip is computed from (`Status`, `Ok`,
`Failed`) plus the counts line's inputs, and then applies the page's own `historyState()` to each sample so
the chip the user would have seen is on the record next to the server's words.

    python3 tests/backend/priority4_queued_failed.py [--label NAME]
"""
import argparse
import json
import pathlib
import subprocess
import sys
import time

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import priority_scratch as ps  # noqa: E402  (the token + api client live there)

FIXTURES = '6e12d888b24506af0978b97da66979b1'
OUT_DIR = pathlib.Path(__file__).resolve().parents[1] / 'gui'

# historyState() from Jellyfin.Plugin.SubSync/Web/subsyncMain.js:328-339, copied so the chip in this
# report is the page's own answer and not a second opinion about it.
HISTORY_STATE = r'''
function historyState(b) {
    var status = b.Status || b.status || '';
    var ok = (b.Ok != null) ? b.Ok : (b.ok || 0);
    var failed = (b.Failed != null) ? b.Failed : (b.failed || 0);
    var total = (b.Total != null) ? b.Total : (b.total || 0);
    if (status === 'Queued') return { chip: 'Queued', cls: 'partial' };
    if (status === 'Running') return { chip: 'Running', cls: 'partial' };
    if (status === 'Cancelled') return { chip: 'Cancelled', cls: 'partial' };
    if (failed > 0 && ok > 0) return { chip: 'Partly failed', cls: 'partial' };
    if (failed > 0 || status === 'Failed') return { chip: 'Failed', cls: 'fail' };
    return { chip: total > 0 ? 'Succeeded' : (status || 'Finished'), cls: 'ok' };
}
const rows = JSON.parse(require('fs').readFileSync(process.argv[2], 'utf8'));
for (const r of rows) {
    const s = historyState(r);
    console.log(JSON.stringify({ at: r.at, Status: r.Status, Total: r.Total, Completed: r.Completed,
                                 Ok: r.Ok, Failed: r.Failed, Cancelled: r.Cancelled,
                                 chip: s.chip, cls: s.cls,
                                 counts: r.Completed + '/' + r.Total + ' done (' +
                                         Math.round((r.Completed / (r.Total || 1)) * 100) + '%) · ' +
                                         r.Failed + ' failed' }));
}
'''


def chip_sequence(samples, label):
    """Apply the page's historyState() to every sample, using the rig's node."""
    script = OUT_DIR / ('%s-chip.js' % label)
    script.write_text(HISTORY_STATE)
    rows = OUT_DIR / ('%s-rows.json' % label)
    rows.write_text(json.dumps(samples))
    out = subprocess.run(['node', str(script), str(rows)], capture_output=True, text=True)
    if out.returncode != 0:
        raise SystemExit('node failed: %s' % out.stderr[-800:])
    return [json.loads(line) for line in out.stdout.splitlines() if line.strip()]


def queue_probe_batch(count):
    """A batch of real tracks, big enough to be sampled while it runs."""
    uid = ps.api('Users/Me')['Id']
    items = ps.api('Users/%s/Items?ParentId=%s&Recursive=true&IncludeItemTypes=Movie' % (uid, FIXTURES))
    tasks = []
    for item in items.get('Items', []):
        for track in (ps.api('SubSync/Subtitles/%s' % item['Id']) or []):
            if track.get('UnsupportedReason'):
                continue
            tasks.append({'itemId': item['Id'], 'subtitleIndex': track['Index'], 'title': item['Name']})
        if len(tasks) >= count:
            break
    return tasks[:count]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--label', default='priority4')
    parser.add_argument('--tasks', type=int, default=12)
    parser.add_argument('--budget', type=float, default=60.0)
    args = parser.parse_args()

    tasks = queue_probe_batch(args.tasks)
    if not tasks:
        raise SystemExit('no queueable fixture tracks')
    view = ps.api('SubSync/Batch', {'label': args.label, 'tasks': tasks})
    batch = view['Id']
    print('batch %s: %d task(s), created as Status=%s Completed=%s/%s Failed=%s'
          % (batch, len(tasks), view.get('Status'), view.get('Completed'), view.get('Total'), view.get('Failed')))

    samples = []
    deadline = time.time() + args.budget
    while time.time() < deadline:
        row = [b for b in ps.api('SubSync/Batches') if b['Id'] == batch]
        if row:
            row = row[0]
            samples.append({
                'at': round(time.time() - (deadline - args.budget), 2),
                'Status': row['Status'], 'Total': row['Total'], 'Completed': row['Completed'],
                'Ok': row['Ok'], 'Failed': row['Failed'], 'Cancelled': row['Cancelled'],
                'FinishedAtUtc': row.get('FinishedAtUtc'),
            })
        if row and row['Completed'] >= row['Total'] and row['Total'] > 0:
            break
        time.sleep(0.1)

    chips = chip_sequence(samples, args.label)
    # Collapse to the points where the chip changed: that is the sequence the user would have seen.
    seen = []
    for c in chips:
        if not seen or seen[-1]['chip'] != c['chip'] or seen[-1]['Status'] != c['Status']:
            seen.append(c)

    report = {
        'label': args.label,
        'batch': batch,
        'tasks': len(tasks),
        'createdAs': {k: view.get(k) for k in ('Status', 'Total', 'Completed', 'Ok', 'Failed', 'Cancelled')},
        'samples': chips,
        'chipTransitions': seen,
    }
    out = OUT_DIR / ('%s-report.json' % args.label)
    out.write_text(json.dumps(report, indent=1))

    print('\nthe chip the page would have drawn, at every change:')
    for c in seen:
        flag = '  <-- FAILED while unfinished' if (c['cls'] == 'fail' and c['Completed'] < c['Total']) else ''
        print('  %5.2fs  %-8s %-9s  %s%s' % (c['at'], c['Status'], c['chip'], c['counts'], flag))
    bad = [c for c in chips if c['cls'] == 'fail' and c['Completed'] < c['Total']]
    print('\nsamples where a still-unfinished run was drawn as a failure: %d' % len(bad))
    if bad:
        print('FIRST: %s' % json.dumps(bad[0]))
    print('report: %s' % out)


if __name__ == '__main__':
    main()
